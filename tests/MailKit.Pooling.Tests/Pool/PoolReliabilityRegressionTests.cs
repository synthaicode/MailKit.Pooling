using PooledMailKit.Abstractions;
using PooledMailKit.Errors;
using PooledMailKit.Options;
using PooledMailKit.Metrics;
using PooledMailKit.Pooling;
using PooledMailKit.Tests.TestDoubles;
using MimeKit;

namespace PooledMailKit.Tests.Pool;

public sealed class PoolReliabilityRegressionTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Creation_Completed_After_Shutdown_Is_Disposed_Without_Publishing(bool warmup)
    {
        var client = new TrackingClient();
        var factory = new GatedFactory();
        var options = Options();
        options.MinPoolSize = warmup ? 1 : 0;
        var pool = new SmtpPool(options, factory);
        Task operation = warmup ? pool.WarmupAsync() : pool.AcquireLeaseAsync();
        await factory.Entered.Task.WaitAsync(Watchdog);

        await pool.DisposeAsync();
        factory.Complete(client);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => operation.WaitAsync(Watchdog));
        await pool.DisposeAsync();

        Assert.Equal(1, client.DisposeCalls);
        Assert.Equal(0, pool.GetSnapshot().TotalConnections);
        Assert.Equal(0, pool.GetSnapshot().IdleConnections);
        Assert.Equal(0, pool.GetSnapshot().LeasedConnections);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Acquisition_Timeout_Does_Not_Wait_For_Cancellation_Ignoring_Creation(int minimum)
    {
        var options = Options();
        options.MinPoolSize = minimum;
        options.AcquireTimeout = TimeSpan.FromMilliseconds(200);
        var first = new TrackingClient();
        var replacement = new TrackingClient();
        var factory = new GatedFactory(replacement);
        await using var pool = new SmtpPool(options, factory);
        var acquisition = pool.AcquireLeaseAsync();
        await factory.Entered.Task.WaitAsync(Watchdog);

        await Assert.ThrowsAsync<SmtpPoolExhaustedException>(() => acquisition.WaitAsync(Watchdog));
        Assert.False(factory.Completed);
        Assert.Equal(1, pool.GetSnapshot().TotalConnections);
        Assert.Equal(0, pool.GetSnapshot().WaitingCallers);

        factory.Complete(first);
        await first.Disposed.Task.WaitAsync(Watchdog);
        var next = await pool.AcquireLeaseAsync().WaitAsync(Watchdog);
        Assert.Equal(1, first.DisposeCalls);
        Assert.Same(replacement, next.Client);
        Assert.Equal(1, pool.GetSnapshot().TotalConnections);
        await next.ReturnAsync();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Synchronously_Late_Creation_Cannot_Return_A_Successful_Lease(int minimum)
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-11-06T00:00:00Z"));
        var client = new TrackingClient();
        var factory = new DelegateFactory((_, _) =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(350));
            return Task.FromResult<ISmtpClientAdapter>(client);
        });
        var options = Options();
        options.MinPoolSize = minimum;
        options.AcquireTimeout = TimeSpan.FromMilliseconds(100);
        await using var pool = new SmtpPool(options, factory, clock);

        await Assert.ThrowsAsync<SmtpPoolExhaustedException>(() => pool.AcquireLeaseAsync());

        Assert.Equal(1, factory.Calls);
        Assert.Equal(1, client.DisposeCalls);
        Assert.Equal(0, pool.GetSnapshot().TotalConnections);
    }

    [Fact]
    public async Task Caller_Cancellation_Is_Preserved_And_Late_Client_Is_Owned()
    {
        var first = new TrackingClient();
        var replacement = new TrackingClient();
        var factory = new GatedFactory(replacement);
        await using var pool = new SmtpPool(Options(), factory);
        using var cancellation = new CancellationTokenSource();
        var acquisition = pool.AcquireLeaseAsync(cancellation.Token);
        await factory.Entered.Task.WaitAsync(Watchdog);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquisition.WaitAsync(Watchdog));
        factory.Complete(first);
        await first.Disposed.Task.WaitAsync(Watchdog);
        var next = await pool.AcquireLeaseAsync().WaitAsync(Watchdog);
        Assert.Equal(1, first.DisposeCalls);
        Assert.Same(replacement, next.Client);
        await next.ReturnAsync();
    }

    [Fact]
    public async Task Acquisition_Timeout_Covers_Cancellation_Ignoring_Keepalive()
    {
        var noOpCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TrackingClient { OnNoOp = _ => noOpCompleted.Task };
        var replacement = new TrackingClient();
        var factory = new DelegateFactory((call, _) => Task.FromResult<ISmtpClientAdapter>(call == 1 ? first : replacement));
        var options = Options();
        options.AcquireTimeout = TimeSpan.FromMilliseconds(200);
        options.KeepAliveInterval = TimeSpan.FromTicks(1);
        options.EnableKeepAlive = true;
        await using var pool = new SmtpPool(options, factory);
        var initial = await pool.AcquireLeaseAsync();
        await initial.ReturnAsync();
        var acquisition = pool.AcquireLeaseAsync();
        await first.NoOpStarted.Task.WaitAsync(Watchdog);

        await Assert.ThrowsAsync<SmtpPoolExhaustedException>(() => acquisition.WaitAsync(Watchdog));
        Assert.False(noOpCompleted.Task.IsCompleted);
        noOpCompleted.TrySetResult();
        await first.Disposed.Task.WaitAsync(Watchdog);
        var next = await pool.AcquireLeaseAsync().WaitAsync(Watchdog);
        Assert.Same(replacement, next.Client);
        Assert.Equal(1, first.DisposeCalls);
        await next.ReturnAsync();
    }

    [Fact]
    public async Task Successful_Warmup_Wakes_A_Waiter_Without_Clock_Advancement()
    {
        var clock = new SignalingClock(TimeSpan.FromMilliseconds(25));
        var factory = new GatedFactory();
        var options = Options();
        options.MinPoolSize = 1;
        await using var pool = new SmtpPool(options, factory, clock);
        var warmup = pool.WarmupAsync();
        await factory.Entered.Task.WaitAsync(Watchdog);
        var acquisition = pool.AcquireLeaseAsync();
        await clock.WaitRegistered.Task.WaitAsync(Watchdog);
        var initialTime = clock.UtcNow;

        factory.Complete(new TrackingClient());
        await warmup.WaitAsync(Watchdog);
        var lease = await acquisition.WaitAsync(Watchdog);

        Assert.Equal(initialTime, clock.UtcNow);
        Assert.Equal(1, factory.Calls);
        await lease.ReturnAsync();
    }

    [Fact]
    public async Task Warmup_Failure_Cooldown_Starts_At_Observed_Failure_Time()
    {
        var clock = new SignalingClock(TimeSpan.FromMilliseconds(100));
        var factory = new DelegateFactory((call, _) =>
        {
            if (call == 1)
            {
                clock.Advance(TimeSpan.FromMilliseconds(350));
                throw new IOException("slow connection failure");
            }
            return Task.FromResult<ISmtpClientAdapter>(new TrackingClient());
        });
        var options = Options();
        options.MinPoolSize = 1;
        options.ReconnectCooldown = TimeSpan.FromMilliseconds(100);
        await using var pool = new SmtpPool(options, factory, clock);
        await Assert.ThrowsAsync<IOException>(() => pool.WarmupAsync());
        Assert.Equal(clock.UtcNow + TimeSpan.FromMilliseconds(100), pool.GetSnapshot().NextCreationAllowedAt);

        var acquisition = pool.AcquireLeaseAsync();
        await clock.WaitRegistered.Task.WaitAsync(Watchdog);
        Assert.Equal(1, factory.Calls);
        clock.Advance(TimeSpan.FromMilliseconds(99));
        Assert.Equal(1, factory.Calls);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var lease = await acquisition.WaitAsync(Watchdog);
        Assert.Equal(2, factory.Calls);
        await lease.ReturnAsync();
    }

    [Fact]
    public async Task Known_Expired_Result_Does_Not_Wait_For_A_Delayed_Timer_Callback()
    {
        var clock = new DelayedTimerClock();
        var client = new TrackingClient();
        var factory = new DelegateFactory((_, _) => Task.FromResult<ISmtpClientAdapter>(client));
        var options = Options();
        options.AcquireTimeout = TimeSpan.FromMilliseconds(100);
        var metrics = new AdvanceOnCreationMetrics(clock);
        await using var pool = new SmtpPool(options, factory, clock, metrics);

        await Assert.ThrowsAsync<SmtpPoolExhaustedException>(() => pool.AcquireLeaseAsync().WaitAsync(Watchdog));

        Assert.Equal(1, client.DisposeCalls);
        Assert.Equal(0, pool.GetSnapshot().TotalConnections);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Creation_Budget_Expiry_Suppresses_Primary_Before_The_Next_Acquire(int minimum)
    {
        var clock = new SignalingClock(TimeSpan.FromMilliseconds(100));
        var metrics = new RecordingSmtpPoolMetrics();
        var factory = new CancelablePrimaryFactory();
        var options = MultiHostOptions(minimum);
        await using var pool = new SmtpPool(options, factory, clock, metrics);
        var first = pool.AcquireLeaseAsync();
        await factory.Entered.Task.WaitAsync(Watchdog);

        clock.Advance(options.AcquireTimeout);
        await Assert.ThrowsAsync<SmtpPoolExhaustedException>(() => first.WaitAsync(Watchdog));
        Assert.Equal(clock.UtcNow + options.ReconnectCooldown, pool.GetSnapshot().NextCreationAllowedAt);
        Assert.Equal(1, metrics.Count(SmtpMetricNames.PoolConnectionCreateFailures, "primary:25"));

        var next = await pool.AcquireLeaseAsync().WaitAsync(Watchdog);
        Assert.Equal("secondary:25", next.EndpointKey);
        Assert.Equal(new[] { "primary", "secondary" }, factory.RequestedHosts);
        // Cancellation completion must not apply the same failure twice.
        Assert.Equal(1, metrics.Count(SmtpMetricNames.PoolConnectionCreateFailures, "primary:25"));
        await next.ReturnAsync();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Explicit_Caller_Cancellation_Does_Not_Penalize_The_Primary(int minimum)
    {
        var clock = new SignalingClock(TimeSpan.FromMilliseconds(100));
        var metrics = new RecordingSmtpPoolMetrics();
        var factory = new CancelablePrimaryFactory();
        var options = MultiHostOptions(minimum);
        await using var pool = new SmtpPool(options, factory, clock, metrics);
        using var cancellation = new CancellationTokenSource();
        var first = pool.AcquireLeaseAsync(cancellation.Token);
        await factory.Entered.Task.WaitAsync(Watchdog);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(Watchdog));
        var next = await pool.AcquireLeaseAsync().WaitAsync(Watchdog);

        Assert.Equal("primary:25", next.EndpointKey);
        Assert.Equal(new[] { "primary", "primary" }, factory.RequestedHosts);
        Assert.Equal(0, metrics.Count(SmtpMetricNames.PoolConnectionCreateFailures));
        Assert.Null(pool.GetSnapshot().NextCreationAllowedAt);
        await next.ReturnAsync();
    }

    [Fact]
    public async Task Capacity_Wait_Budget_Expiry_Is_Not_A_Host_Creation_Failure()
    {
        var clock = new SignalingClock(TimeSpan.FromMilliseconds(100));
        var metrics = new RecordingSmtpPoolMetrics();
        var factory = new FakeSmtpConnectionFactory();
        factory.Enqueue(new TrackingClient());
        var options = MultiHostOptions(0);
        await using var pool = new SmtpPool(options, factory, clock, metrics);
        var held = await pool.AcquireLeaseAsync();
        var blocked = pool.AcquireLeaseAsync();

        clock.Advance(options.AcquireTimeout);
        await Assert.ThrowsAsync<SmtpPoolExhaustedException>(() => blocked.WaitAsync(Watchdog));
        Assert.Equal(0, metrics.Count(SmtpMetricNames.PoolConnectionCreateFailures));
        Assert.Null(pool.GetSnapshot().NextCreationAllowedAt);
        await held.ReturnAsync();
        var next = await pool.AcquireLeaseAsync().WaitAsync(Watchdog);
        Assert.Equal(held.ConnectionId, next.ConnectionId);
        Assert.Equal(1, factory.CreateCalls);
        await next.ReturnAsync();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Each_Failed_Host_Attempt_In_One_Acquisition_Is_Recorded_Once(int minimum)
    {
        var metrics = new RecordingSmtpPoolMetrics();
        var factory = new FakeSmtpConnectionFactory();
        factory.EnqueueFailure(new IOException("primary unavailable"));
        factory.EnqueueFailure(new IOException("secondary unavailable"));
        factory.Enqueue(new TrackingClient { EndpointKey = "tertiary:25" });
        var options = MultiHostOptions(minimum);
        options.AcquireTimeout = TimeSpan.FromSeconds(2);
        options.Hosts.Add(new SmtpHostOptions { Host = "tertiary", Port = 25, Priority = 20, SecureSocketOptions = "None" });
        await using var pool = new SmtpPool(options, factory, metrics: metrics);

        var lease = await pool.AcquireLeaseAsync().WaitAsync(Watchdog);

        Assert.Equal("tertiary:25", lease.EndpointKey);
        Assert.Equal(3, factory.CreateCalls);
        Assert.Equal(1, metrics.Count(SmtpMetricNames.PoolConnectionCreateFailures, "primary:25"));
        Assert.Equal(1, metrics.Count(SmtpMetricNames.PoolConnectionCreateFailures, "secondary:25"));
        await lease.ReturnAsync();
    }

    private static SmtpPoolOptions MultiHostOptions(int minimum)
    {
        var options = Options();
        options.MinPoolSize = minimum;
        options.AcquireTimeout = TimeSpan.FromMilliseconds(100);
        options.ReconnectCooldown = TimeSpan.FromSeconds(2);
        options.Hosts =
        [
            new SmtpHostOptions { Host = "primary", Port = 25, Priority = 0, SecureSocketOptions = "None" },
            new SmtpHostOptions { Host = "secondary", Port = 25, Priority = 10, SecureSocketOptions = "None" },
        ];
        return options;
    }

    private static SmtpPoolOptions Options() => new()
    {
        Host = new SmtpHostOptions { Host = "localhost", Port = 25, SecureSocketOptions = "None" },
        MinPoolSize = 0,
        MaxPoolSize = 1,
        AcquireTimeout = TimeSpan.FromSeconds(2),
        EnableKeepAlive = false,
        JitterRatio = 0,
        ReconnectCooldown = TimeSpan.Zero,
    };

    private sealed class GatedFactory : ISmtpConnectionFactory
    {
        private readonly TaskCompletionSource<ISmtpClientAdapter> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ISmtpClientAdapter? replacement;
        private int calls;
        public GatedFactory(ISmtpClientAdapter? replacement = null) => this.replacement = replacement;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls => Volatile.Read(ref calls);
        public bool Completed => completion.Task.IsCompleted;
        public void Complete(ISmtpClientAdapter client) => completion.TrySetResult(client);
        public Task<ISmtpClientAdapter> CreateAuthenticatedClientAsync(SmtpHostOptions host, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref calls) != 1)
            {
                return Task.FromResult(replacement ?? throw new InvalidOperationException("Unexpected creation"));
            }
            Entered.TrySetResult();
            return completion.Task;
        }
    }

    private sealed class CancelablePrimaryFactory : ISmtpConnectionFactory
    {
        private int primaryCalls;
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> requestedHosts = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string[] RequestedHosts => requestedHosts.ToArray();
        public Task<ISmtpClientAdapter> CreateAuthenticatedClientAsync(SmtpHostOptions host, CancellationToken cancellationToken)
        {
            requestedHosts.Enqueue(host.Host);
            if (host.Host == "primary" && Interlocked.Increment(ref primaryCalls) == 1)
            {
                Entered.TrySetResult();
                return WaitForCancellationAsync(cancellationToken);
            }
            return Task.FromResult<ISmtpClientAdapter>(new TrackingClient { EndpointKey = host.ToEndpointKey() });
        }
        private static async Task<ISmtpClientAdapter> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("Infinite cancellation wait unexpectedly completed");
        }
    }

    private sealed class DelegateFactory(Func<int, CancellationToken, Task<ISmtpClientAdapter>> create) : ISmtpConnectionFactory
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public Task<ISmtpClientAdapter> CreateAuthenticatedClientAsync(SmtpHostOptions host, CancellationToken cancellationToken)
            => create(Interlocked.Increment(ref calls), cancellationToken);
    }

    private sealed class TrackingClient : ISmtpClientAdapter
    {
        private int disposals;
        public Func<CancellationToken, Task>? OnNoOp { get; init; }
        public TaskCompletionSource NoOpStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsConnected { get; private set; } = true;
        public bool IsAuthenticated => true;
        public string EndpointKey { get; init; } = "smtp://local";
        public int DisposeCalls => Volatile.Read(ref disposals);
        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AuthenticateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task NoOpAsync(CancellationToken cancellationToken)
        {
            NoOpStarted.TrySetResult();
            return OnNoOp?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }
        public Task SendAsync(MimeMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DisconnectAsync(bool quit, CancellationToken cancellationToken)
        {
            IsConnected = false;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref disposals);
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AdvanceOnCreationMetrics(DelayedTimerClock clock) : ISmtpPoolMetrics
    {
        public void Record(SmtpPoolMetricEvent metricEvent)
        {
            if (metricEvent.Name == SmtpMetricNames.PoolConnectionsCreated)
            {
                clock.Advance(TimeSpan.FromMilliseconds(350));
            }
        }
    }

    private sealed class DelayedTimerClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.Parse("2026-11-06T00:00:00Z");
        public void Advance(TimeSpan amount) => UtcNow += amount;
        public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
        {
            // Model an elapsed wall clock whose timer callback has not run yet.
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return completion.Task;
        }
    }

    private sealed class SignalingClock(TimeSpan observedDelay) : IClock
    {
        private readonly FakeClock inner = new(DateTimeOffset.Parse("2026-11-06T00:00:00Z"));
        public TaskCompletionSource WaitRegistered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTimeOffset UtcNow => inner.UtcNow;
        public void Advance(TimeSpan amount) => inner.Advance(amount);
        public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
        {
            var task = inner.Delay(delay, cancellationToken);
            if (delay == observedDelay)
            {
                WaitRegistered.TrySetResult();
            }
            return task;
        }
    }
}
