using MailKit.Pooling.Errors;
using MailKit.Pooling.Options;
using MailKit.Pooling.Pooling;
using MailKit.Pooling.Tests.TestDoubles;

namespace MailKit.Pooling.Tests.Pool;

public sealed class PoolStateTransitionTests
{
    [Fact]
    public async Task Acquire_And_Return_Reuses_The_Same_Connection()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-05T00:00:00Z"));
        var factory = new FakeSmtpConnectionFactory();
        var client = new FakeSmtpClientAdapter();
        factory.Enqueue(client);

        await using var pool = new SmtpPool(CreateOptions(), factory, clock);

        var firstLease = await pool.AcquireLeaseAsync();
        var firstConnectionId = firstLease.ConnectionId;
        await firstLease.ReturnAsync();

        var secondLease = await pool.AcquireLeaseAsync();

        Assert.Equal(firstConnectionId, secondLease.ConnectionId);
        Assert.Same(client, secondLease.Client);

        await secondLease.ReturnAsync();
        Assert.Equal(1, factory.CreateCalls);
    }

    [Fact]
    public async Task MaxPoolSize_Is_Not_Exceeded_When_Connections_Are_Leased()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-05T00:00:00Z"));
        var factory = new FakeSmtpConnectionFactory();
        factory.Enqueue(new FakeSmtpClientAdapter { EndpointKey = "smtp://1" });
        factory.Enqueue(new FakeSmtpClientAdapter { EndpointKey = "smtp://2" });

        await using var pool = new SmtpPool(CreateOptions(maxPoolSize: 2), factory, clock);

        var lease1 = await pool.AcquireLeaseAsync();
        var lease2 = await pool.AcquireLeaseAsync();
        var snapshot = pool.GetSnapshot();

        Assert.Equal(2, factory.CreateCalls);
        Assert.Equal(2, snapshot.TotalConnections);
        Assert.Equal(2, snapshot.LeasedConnections);

        await lease1.ReturnAsync();
        await lease2.ReturnAsync();
    }

    [Fact]
    public async Task Warmup_Creates_MinPoolSize_Idle_Connections()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-05T00:00:00Z"));
        var factory = new FakeSmtpConnectionFactory();
        factory.Enqueue(new FakeSmtpClientAdapter { EndpointKey = "smtp://1" });
        factory.Enqueue(new FakeSmtpClientAdapter { EndpointKey = "smtp://2" });

        await using var pool = new SmtpPool(CreateOptions(maxPoolSize: 3, minPoolSize: 2), factory, clock);

        await pool.WarmupAsync();
        var snapshot = pool.GetSnapshot();

        Assert.Equal(2, factory.CreateCalls);
        Assert.Equal(2, snapshot.TotalConnections);
        Assert.Equal(2, snapshot.IdleConnections);
        Assert.Equal(0, snapshot.LeasedConnections);
    }

    [Fact]
    public async Task Invalidated_Connection_Is_Replaced_To_Maintain_MinPoolSize()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-05T00:00:00Z"));
        var factory = new FakeSmtpConnectionFactory();
        var firstClient = new FakeSmtpClientAdapter();
        var replacementClient = new FakeSmtpClientAdapter();
        factory.Enqueue(firstClient);
        factory.Enqueue(replacementClient);

        await using var pool = new SmtpPool(
            CreateOptions(maxPoolSize: 2, minPoolSize: 1, reconnectCooldown: TimeSpan.Zero),
            factory,
            clock);

        await pool.WarmupAsync();
        var lease = await pool.AcquireLeaseAsync();
        await lease.InvalidateAsync();

        var snapshot = pool.GetSnapshot();

        Assert.Equal(2, factory.CreateCalls);
        Assert.Equal(1, snapshot.TotalConnections);
        Assert.Equal(1, snapshot.IdleConnections);
        Assert.Equal(1, firstClient.DisposeCalls);

        var replacementLease = await pool.AcquireLeaseAsync();
        Assert.Same(replacementClient, replacementLease.Client);
        await replacementLease.ReturnAsync();
    }

    [Fact]
    public async Task AcquireTimeout_Throws_Explicit_PoolExhausted_Error()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-05T00:00:00Z"));
        var factory = new FakeSmtpConnectionFactory();
        factory.Enqueue(new FakeSmtpClientAdapter());

        await using var pool = new SmtpPool(
            CreateOptions(maxPoolSize: 1, acquireTimeout: TimeSpan.FromSeconds(10)),
            factory,
            clock);

        var firstLease = await pool.AcquireLeaseAsync();
        var blockedAcquire = pool.AcquireLeaseAsync();

        await Task.Yield();
        clock.Advance(TimeSpan.FromSeconds(10));

        var exception = await Assert.ThrowsAsync<SmtpPoolExhaustedException>(async () => await blockedAcquire);
        Assert.Contains("Timed out", exception.Message);

        await firstLease.ReturnAsync();
    }

    [Fact]
    public async Task Broken_Connection_Is_Not_Reused()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-05T00:00:00Z"));
        var factory = new FakeSmtpConnectionFactory();
        var firstClient = new FakeSmtpClientAdapter();
        var secondClient = new FakeSmtpClientAdapter();
        factory.Enqueue(firstClient);
        factory.Enqueue(secondClient);

        await using var pool = new SmtpPool(CreateOptions(), factory, clock);

        var firstLease = await pool.AcquireLeaseAsync();
        await firstLease.InvalidateAsync();

        clock.Advance(TimeSpan.FromSeconds(31));
        var secondLease = await pool.AcquireLeaseAsync();

        Assert.NotEqual(firstLease.ConnectionId, secondLease.ConnectionId);
        Assert.Same(secondClient, secondLease.Client);
        Assert.Equal(1, firstClient.DisconnectCalls);
        Assert.Equal(1, firstClient.DisposeCalls);

        await secondLease.ReturnAsync();
    }

    [Fact]
    public async Task Cooldown_Blocks_Immediate_Recreation_After_Discard()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-05T00:00:00Z"));
        var factory = new FakeSmtpConnectionFactory();
        factory.Enqueue(new FakeSmtpClientAdapter());
        factory.Enqueue(new FakeSmtpClientAdapter());

        await using var pool = new SmtpPool(
            CreateOptions(acquireTimeout: TimeSpan.FromSeconds(40), reconnectCooldown: TimeSpan.FromSeconds(30)),
            factory,
            clock);

        var lease = await pool.AcquireLeaseAsync();
        await lease.InvalidateAsync();

        var pendingAcquire = pool.AcquireLeaseAsync();
        await Task.Yield();

        Assert.False(pendingAcquire.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(29));
        await Task.Yield();
        Assert.False(pendingAcquire.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(1));
        var nextLease = await pendingAcquire;

        Assert.Equal(2, factory.CreateCalls);
        await nextLease.ReturnAsync();
    }

    [Fact]
    public async Task KeepAlive_Is_Executed_For_Stale_Idle_Connections()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-05T00:00:00Z"));
        var factory = new FakeSmtpConnectionFactory();
        var client = new FakeSmtpClientAdapter();
        factory.Enqueue(client);

        await using var pool = new SmtpPool(
            CreateOptions(
                keepAliveInterval: TimeSpan.FromSeconds(5),
                reconnectCooldown: TimeSpan.Zero),
            factory,
            clock);

        var lease = await pool.AcquireLeaseAsync();
        await lease.ReturnAsync();

        clock.Advance(TimeSpan.FromSeconds(6));
        var reusedLease = await pool.AcquireLeaseAsync();

        Assert.Equal(1, client.NoOpCalls);
        Assert.Equal(lease.ConnectionId, reusedLease.ConnectionId);

        await reusedLease.ReturnAsync();
    }

    [Fact]
    public async Task KeepAlive_Failure_Discards_Connection_Before_Reuse()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-05T00:00:00Z"));
        var factory = new FakeSmtpConnectionFactory();
        var staleClient = new FakeSmtpClientAdapter
        {
            OnNoOpAsync = static _ => Task.FromException(new TimeoutException("noop timed out")),
        };
        var replacementClient = new FakeSmtpClientAdapter();
        factory.Enqueue(staleClient);
        factory.Enqueue(replacementClient);

        await using var pool = new SmtpPool(
            CreateOptions(
                keepAliveInterval: TimeSpan.FromSeconds(5),
                reconnectCooldown: TimeSpan.Zero),
            factory,
            clock);

        var lease = await pool.AcquireLeaseAsync();
        await lease.ReturnAsync();

        clock.Advance(TimeSpan.FromSeconds(6));
        var replacementLease = await pool.AcquireLeaseAsync();

        Assert.Equal(1, staleClient.NoOpCalls);
        Assert.Equal(1, staleClient.DisposeCalls);
        Assert.Same(replacementClient, replacementLease.Client);

        await replacementLease.ReturnAsync();
    }

    [Fact]
    public async Task IdleTimeout_Discards_Expired_Idle_Connection_Before_Reuse()
    {
        var factory = new FakeSmtpConnectionFactory();
        var expiredClient = new FakeSmtpClientAdapter();
        var replacementClient = new FakeSmtpClientAdapter();
        factory.Enqueue(expiredClient);
        factory.Enqueue(replacementClient);

        await using var pool = new SmtpPool(
            CreateOptions(
                idleTimeout: TimeSpan.FromMilliseconds(50),
                reconnectCooldown: TimeSpan.Zero,
                keepAliveInterval: TimeSpan.FromMinutes(1)),
            factory);

        var lease = await pool.AcquireLeaseAsync();
        await lease.ReturnAsync();

        await Task.Delay(100);
        var replacementLease = await pool.AcquireLeaseAsync();

        Assert.Equal(0, expiredClient.NoOpCalls);
        Assert.Equal(1, expiredClient.DisposeCalls);
        Assert.Same(replacementClient, replacementLease.Client);

        await replacementLease.ReturnAsync();
    }

    [Fact]
    public async Task Unhealthy_Idle_Connection_Is_Discarded_Before_Reuse()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-05T00:00:00Z"));
        var factory = new FakeSmtpConnectionFactory();
        var unhealthyClient = new FakeSmtpClientAdapter();
        var replacementClient = new FakeSmtpClientAdapter();
        factory.Enqueue(unhealthyClient);
        factory.Enqueue(replacementClient);

        await using var pool = new SmtpPool(CreateOptions(), factory, clock);

        var lease = await pool.AcquireLeaseAsync();
        await lease.ReturnAsync();
        unhealthyClient.IsConnected = false;

        clock.Advance(TimeSpan.FromSeconds(31));
        var replacementLease = await pool.AcquireLeaseAsync();

        Assert.Same(replacementClient, replacementLease.Client);
        Assert.Equal(1, unhealthyClient.DisposeCalls);

        await replacementLease.ReturnAsync();
    }

    private static SmtpPoolOptions CreateOptions(
        int maxPoolSize = 1,
        int minPoolSize = 0,
        TimeSpan? acquireTimeout = null,
        TimeSpan? reconnectCooldown = null,
        TimeSpan? keepAliveInterval = null,
        TimeSpan? idleTimeout = null)
    {
        return new SmtpPoolOptions
        {
            Host = new SmtpHostOptions
            {
                Host = "localhost",
            },
            MaxPoolSize = maxPoolSize,
            MinPoolSize = minPoolSize,
            AcquireTimeout = acquireTimeout ?? TimeSpan.FromSeconds(15),
            ReconnectCooldown = reconnectCooldown ?? TimeSpan.FromSeconds(30),
            KeepAliveInterval = keepAliveInterval ?? TimeSpan.FromMinutes(1),
            IdleTimeout = idleTimeout ?? TimeSpan.FromMinutes(2),
        };
    }
}
