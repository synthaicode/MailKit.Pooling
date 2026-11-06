using System.Diagnostics.Metrics;
using MimeKit;
using PooledMailKit.Errors;
using PooledMailKit.Metrics;
using PooledMailKit.Options;
using PooledMailKit.Pooling;
using PooledMailKit.Sending;
using PooledMailKit.Tests.TestDoubles;

namespace PooledMailKit.Tests.Metrics;

public sealed class MetricsFailureIsolationTests
{
    [Fact]
    public async Task Accepted_Send_Remains_Successful_When_Duration_Listener_Throws()
    {
        var options = CreateOptions();
        using var metrics = new SystemDiagnosticsSmtpPoolMetrics();
        using var observer = new ThrowOnceObserver(SmtpMetricNames.SendDuration, options.Host.ToEndpointKey());
        var client = CreateClient(options);
        var accepted = 0;
        client.OnSendAsync = (_, _) => { accepted++; return Task.CompletedTask; };
        var factory = new FakeSmtpConnectionFactory();
        factory.Enqueue(client);
        await using var pool = new SmtpPool(options, factory, metrics: metrics);
        var sender = new SmtpSender(pool, new DefaultSmtpErrorClassifier(), options, metrics: metrics);
        using var message = CreateMessage();

        var result = await sender.SendAsync(message);

        Assert.Equal(1, observer.Throws);
        Assert.Equal(1, accepted);
        Assert.Equal(1, client.SendCalls);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, pool.GetSnapshot().IdleConnections);
        Assert.Equal(0, pool.GetSnapshot().LeasedConnections);
    }

    [Fact]
    public async Task Failure_Classification_Listener_Does_Not_Mask_Error_Or_Leak_Lease()
    {
        var options = CreateOptions();
        using var metrics = new SystemDiagnosticsSmtpPoolMetrics();
        using var observer = new ThrowOnceObserver(SmtpMetricNames.SendClassificationCount, options.Host.ToEndpointKey());
        var original = new IOException("SMTP transport failed");
        var client = CreateClient(options);
        client.OnSendAsync = (_, _) => Task.FromException(original);
        var factory = new FakeSmtpConnectionFactory();
        factory.Enqueue(client);
        await using var pool = new SmtpPool(options, factory, metrics: metrics);
        var sender = new SmtpSender(pool, new DefaultSmtpErrorClassifier(), options, metrics: metrics);
        using var message = CreateMessage();

        var error = await Assert.ThrowsAsync<SmtpSendFailedException>(() => sender.SendAsync(message));

        Assert.Equal(1, observer.Throws);
        Assert.Same(original, error.InnerException);
        Assert.Equal(SmtpFailureKind.UnknownAfterData, error.Classification.Kind);
        Assert.Equal(1, client.SendCalls);
        Assert.Equal(1, client.DisposeCalls);
        Assert.Equal(0, pool.GetSnapshot().TotalConnections);
        Assert.Equal(0, pool.GetSnapshot().LeasedConnections);
    }

    [Fact]
    public async Task Creation_Listener_Does_Not_Corrupt_Capacity_Or_Lose_The_Lease()
    {
        var options = CreateOptions();
        using var metrics = new SystemDiagnosticsSmtpPoolMetrics();
        using var observer = new ThrowOnceObserver(SmtpMetricNames.PoolConnectionsCreated, options.Host.ToEndpointKey());
        var factory = new FakeSmtpConnectionFactory();
        factory.Enqueue(CreateClient(options));
        await using var pool = new SmtpPool(options, factory, metrics: metrics);

        var first = await pool.AcquireLeaseAsync();
        Assert.Equal(1, observer.Throws);
        Assert.Equal(1, pool.GetSnapshot().TotalConnections);
        Assert.Equal(1, pool.GetSnapshot().LeasedConnections);
        await first.ReturnAsync();
        var second = await pool.AcquireLeaseAsync();

        Assert.Equal(first.ConnectionId, second.ConnectionId);
        Assert.Equal(1, factory.CreateCalls);
        Assert.Equal(1, pool.GetSnapshot().TotalConnections);
        Assert.Equal(1, pool.GetSnapshot().LeasedConnections);
        await second.ReturnAsync();
    }

    [Fact]
    public async Task Shutdown_Disposes_All_Connections_When_First_Drop_Listener_Throws()
    {
        var options = CreateOptions(maxPoolSize: 2);
        using var metrics = new SystemDiagnosticsSmtpPoolMetrics();
        using var observer = new ThrowOnceObserver(SmtpMetricNames.PoolConnectionsDropped, options.Host.ToEndpointKey());
        var clients = new[] { CreateClient(options), CreateClient(options) };
        var factory = new FakeSmtpConnectionFactory();
        foreach (var client in clients) factory.Enqueue(client);
        await using var pool = new SmtpPool(options, factory, metrics: metrics);
        var first = await pool.AcquireLeaseAsync();
        var second = await pool.AcquireLeaseAsync();
        await first.ReturnAsync();
        await second.ReturnAsync();

        await pool.DisposeAsync();
        await pool.DisposeAsync();

        Assert.Equal(1, observer.Throws);
        Assert.All(clients, client => Assert.Equal(1, client.DisposeCalls));
        Assert.Equal(0, pool.GetSnapshot().TotalConnections);
    }

    [Fact]
    public async Task Idle_Expiry_Disposes_All_Removed_Connections_When_Drop_Listener_Throws()
    {
        var options = CreateOptions(maxPoolSize: 2);
        options.IdleTimeout = TimeSpan.FromMilliseconds(100);
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero));
        using var metrics = new SystemDiagnosticsSmtpPoolMetrics();
        using var observer = new ThrowOnceObserver(SmtpMetricNames.PoolConnectionsDropped, options.Host.ToEndpointKey());
        var expired = new[] { CreateClient(options), CreateClient(options) };
        var replacement = CreateClient(options);
        var factory = new FakeSmtpConnectionFactory();
        foreach (var client in expired) factory.Enqueue(client);
        factory.Enqueue(replacement);
        await using var pool = new SmtpPool(options, factory, clock, metrics);
        var first = await pool.AcquireLeaseAsync();
        var second = await pool.AcquireLeaseAsync();
        await first.ReturnAsync();
        await second.ReturnAsync();
        clock.Advance(TimeSpan.FromMilliseconds(200));

        var lease = await pool.AcquireLeaseAsync();

        Assert.Equal(1, observer.Throws);
        Assert.All(expired, client => Assert.Equal(1, client.DisposeCalls));
        Assert.Same(replacement, lease.Client);
        Assert.Equal(1, pool.GetSnapshot().TotalConnections);
        Assert.Equal(1, pool.GetSnapshot().LeasedConnections);
        await lease.ReturnAsync();
    }

    [Fact]
    public async Task Keepalive_Failure_Releases_Broken_Lease_When_Drop_Listener_Throws()
    {
        var options = CreateOptions();
        options.EnableKeepAlive = true;
        options.KeepAliveInterval = TimeSpan.FromMilliseconds(100);
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero));
        using var metrics = new SystemDiagnosticsSmtpPoolMetrics();
        using var observer = new ThrowOnceObserver(SmtpMetricNames.PoolConnectionsDropped, options.Host.ToEndpointKey());
        var broken = CreateClient(options);
        broken.OnNoOpAsync = _ => Task.FromException(new IOException("NOOP failed"));
        var replacement = CreateClient(options);
        var factory = new FakeSmtpConnectionFactory();
        factory.Enqueue(broken);
        factory.Enqueue(replacement);
        await using var pool = new SmtpPool(options, factory, clock, metrics);
        var initial = await pool.AcquireLeaseAsync();
        await initial.ReturnAsync();
        clock.Advance(TimeSpan.FromMilliseconds(200));

        var lease = await pool.AcquireLeaseAsync();

        Assert.Equal(1, observer.Throws);
        Assert.Equal(1, broken.NoOpCalls);
        Assert.Equal(1, broken.DisposeCalls);
        Assert.Same(replacement, lease.Client);
        Assert.Equal(1, pool.GetSnapshot().TotalConnections);
        Assert.Equal(1, pool.GetSnapshot().LeasedConnections);
        await lease.ReturnAsync();
    }

    private static SmtpPoolOptions CreateOptions(int maxPoolSize = 1)
    {
        return new SmtpPoolOptions
        {
            Host = new SmtpHostOptions { Host = $"metrics-{Guid.NewGuid():N}.test", Port = 2525 },
            MinPoolSize = 0,
            MaxPoolSize = maxPoolSize,
            AcquireTimeout = TimeSpan.FromSeconds(5),
            ReconnectCooldown = TimeSpan.Zero,
            MaxReconnectCooldown = TimeSpan.Zero,
            RetryBaseDelay = TimeSpan.Zero,
            MaxRetryAttempts = 0,
            JitterRatio = 0,
            EnableKeepAlive = false,
        };
    }

    private static FakeSmtpClientAdapter CreateClient(SmtpPoolOptions options)
        => new() { EndpointKey = options.Host.ToEndpointKey() };

    private static MimeMessage CreateMessage()
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("from@example.test"));
        message.To.Add(MailboxAddress.Parse("to@example.test"));
        message.Subject = "metrics isolation";
        message.Body = new TextPart("plain") { Text = "one message" };
        return message;
    }

    private sealed class ThrowOnceObserver : IDisposable
    {
        private readonly MeterListener listener = new();
        private int throws;

        public ThrowOnceObserver(string metricName, string endpointKey)
        {
            listener.InstrumentPublished = (instrument, activeListener) =>
            {
                if (instrument.Meter.Name == "PooledMailKit" && instrument.Name == metricName)
                {
                    activeListener.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
            {
                // Unique host tags prevent this listener from affecting other
                // tests that create their own meter concurrently.
                foreach (var tag in tags)
                {
                    if (tag.Key == "smtp.host" && Equals(tag.Value, endpointKey))
                    {
                        if (Interlocked.CompareExchange(ref throws, 1, 0) == 0)
                        {
                            throw new IOException("planned measurement subscriber failure");
                        }
                        break;
                    }
                }
            });
            listener.Start();
        }

        public int Throws => Volatile.Read(ref throws);

        public void Dispose() => listener.Dispose();
    }
}
