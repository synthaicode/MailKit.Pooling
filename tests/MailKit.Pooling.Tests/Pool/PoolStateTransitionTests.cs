using PooledMailKit.Errors;
using PooledMailKit.Metrics;
using PooledMailKit.Options;
using PooledMailKit.Pooling;
using PooledMailKit.Tests.TestDoubles;

namespace PooledMailKit.Tests.Pool;

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
    public async Task Invalidated_Connection_Refill_Can_Be_Delayed_Before_MinPoolSize_Is_Restored()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-05T00:00:00Z"));
        var factory = new FakeSmtpConnectionFactory();
        var firstClient = new FakeSmtpClientAdapter();
        var replacementClient = new FakeSmtpClientAdapter();
        factory.Enqueue(firstClient);
        factory.Enqueue(replacementClient);

        await using var pool = new SmtpPool(
            CreateOptions(
                maxPoolSize: 2,
                minPoolSize: 1,
                reconnectCooldown: TimeSpan.Zero,
                minPoolRefillDelay: TimeSpan.FromSeconds(10)),
            factory,
            clock);

        await pool.WarmupAsync();
        var lease = await pool.AcquireLeaseAsync();
        await lease.InvalidateAsync();

        var snapshotBeforeDelay = pool.GetSnapshot();
        Assert.Equal(1, factory.CreateCalls);
        Assert.Equal(0, snapshotBeforeDelay.TotalConnections);

        await pool.WarmupAsync();
        Assert.Equal(1, factory.CreateCalls);

        clock.Advance(TimeSpan.FromSeconds(10));
        await pool.WarmupAsync();

        var snapshotAfterDelay = pool.GetSnapshot();
        Assert.Equal(2, factory.CreateCalls);
        Assert.Equal(1, snapshotAfterDelay.TotalConnections);
        Assert.Equal(1, snapshotAfterDelay.IdleConnections);
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
    public async Task Snapshot_Tracks_Waiting_Callers_While_Acquire_Is_Blocked()
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
        var waitingSnapshot = pool.GetSnapshot();

        Assert.Equal(1, waitingSnapshot.WaitingCallers);

        await firstLease.ReturnAsync();
        var secondLease = await blockedAcquire;
        await secondLease.ReturnAsync();

        var settledSnapshot = pool.GetSnapshot();
        Assert.Equal(0, settledSnapshot.WaitingCallers);
    }

    [Fact]
    public async Task Blocked_Acquire_Busy_Spins_When_Stale_Return_Signal_Remains()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-05T00:00:00Z"));
        var factory = new FakeSmtpConnectionFactory();
        factory.Enqueue(new FakeSmtpClientAdapter());

        await using var pool = new SmtpPool(
            CreateOptions(maxPoolSize: 1, acquireTimeout: TimeSpan.FromMinutes(1)),
            factory,
            clock);

        var lease = await pool.AcquireLeaseAsync();
        const int stalePermitCount = 5;
        for (var i = 0; i < stalePermitCount; i++)
        {
            await lease.ReturnAsync();
            lease = await pool.AcquireLeaseAsync();
        }

        var delayCallsBeforeBlockedAcquire = clock.DelayCallCount;
        var blockedAcquire = pool.AcquireLeaseAsync();

        Assert.True(
            SpinWait.SpinUntil(
                () => clock.DelayCallCount >= delayCallsBeforeBlockedAcquire + stalePermitCount,
                TimeSpan.FromSeconds(1)),
            "Expected blocked acquire to repeatedly recreate delay waits after consuming accumulated stale return signals.");
        Assert.False(blockedAcquire.IsCompleted);

        await lease.ReturnAsync();
        var resumedLease = await blockedAcquire;
        await resumedLease.ReturnAsync();
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
    public async Task Disposed_Lease_Is_Not_Returned_As_Reusable()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-05T00:00:00Z"));
        var factory = new FakeSmtpConnectionFactory();
        var firstClient = new FakeSmtpClientAdapter();
        var replacementClient = new FakeSmtpClientAdapter();
        factory.Enqueue(firstClient);
        factory.Enqueue(replacementClient);

        await using var pool = new SmtpPool(
            CreateOptions(reconnectCooldown: TimeSpan.Zero),
            factory,
            clock);

        var lease = await pool.AcquireLeaseAsync();
        var firstConnectionId = lease.ConnectionId;
        await lease.DisposeAsync();

        var replacementLease = await pool.AcquireLeaseAsync();

        Assert.NotEqual(firstConnectionId, replacementLease.ConnectionId);
        Assert.Same(replacementClient, replacementLease.Client);
        Assert.Equal(1, firstClient.DisposeCalls);

        await replacementLease.ReturnAsync();
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
    public async Task ReconnectCooldown_Uses_Exponential_Backoff_Up_To_MaxReconnectCooldown()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-05T00:00:00Z"));
        var factory = new FakeSmtpConnectionFactory();
        factory.EnqueueFailure(new TimeoutException("first failure"));
        factory.EnqueueFailure(new TimeoutException("second failure"));
        factory.Enqueue(new FakeSmtpClientAdapter());

        await using var pool = new SmtpPool(
            CreateOptions(
                acquireTimeout: TimeSpan.FromMinutes(1),
                reconnectCooldown: TimeSpan.FromSeconds(5),
                maxReconnectCooldown: TimeSpan.FromSeconds(8),
                useExponentialBackoff: true,
                jitterRatio: 0d),
            factory,
            clock);

        await Assert.ThrowsAsync<TimeoutException>(() => pool.AcquireLeaseAsync());
        Assert.Equal(clock.UtcNow + TimeSpan.FromSeconds(5), pool.GetSnapshot().NextCreationAllowedAt);

        clock.Advance(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<TimeoutException>(() => pool.AcquireLeaseAsync());
        Assert.Equal(clock.UtcNow + TimeSpan.FromSeconds(8), pool.GetSnapshot().NextCreationAllowedAt);

        clock.Advance(TimeSpan.FromSeconds(8));
        var lease = await pool.AcquireLeaseAsync();
        await lease.ReturnAsync();
    }

    [Fact]
    public async Task ReconnectSuppressed_Is_Recorded_Once_Per_Blocked_Acquire()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-05T00:00:00Z"));
        var factory = new FakeSmtpConnectionFactory();
        var metrics = new RecordingSmtpPoolMetrics();
        factory.Enqueue(new FakeSmtpClientAdapter());
        factory.Enqueue(new FakeSmtpClientAdapter());

        await using var pool = new SmtpPool(
            CreateOptions(
                acquireTimeout: TimeSpan.FromSeconds(40),
                reconnectCooldown: TimeSpan.FromSeconds(30)),
            factory,
            clock,
            metrics);

        var lease = await pool.AcquireLeaseAsync();
        await lease.InvalidateAsync();

        var pendingAcquire = pool.AcquireLeaseAsync();
        await Task.Yield();
        Assert.Equal(1, metrics.Count(SmtpMetricNames.PoolReconnectSuppressed));

        for (var i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            await Task.Yield();
            Assert.False(pendingAcquire.IsCompleted);
            Assert.Equal(1, metrics.Count(SmtpMetricNames.PoolReconnectSuppressed));
        }

        clock.Advance(TimeSpan.FromSeconds(5));
        var nextLease = await pendingAcquire;
        await nextLease.ReturnAsync();
        Assert.Equal(1, metrics.Count(SmtpMetricNames.PoolReconnectSuppressed));
    }

    [Fact]
    public async Task Cooldown_Is_Applied_Per_Host_And_Allows_Failover_To_Another_Host()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-05T00:00:00Z"));
        var factory = new FakeSmtpConnectionFactory();
        var firstClient = new FakeSmtpClientAdapter { EndpointKey = "smtp-a.local:2525" };
        var secondClient = new FakeSmtpClientAdapter { EndpointKey = "smtp-b.local:2526" };
        factory.Enqueue(firstClient);
        factory.Enqueue(secondClient);

        await using var pool = new SmtpPool(
            CreateMultiHostOptions(reconnectCooldown: TimeSpan.FromSeconds(30)),
            factory,
            clock);

        var firstLease = await pool.AcquireLeaseAsync();
        await firstLease.InvalidateAsync();

        var secondLease = await pool.AcquireLeaseAsync();

        Assert.Equal(
            ["smtp-a.local:2525", "smtp-b.local:2526"],
            factory.RequestedHosts);
        Assert.Equal("smtp-b.local:2526", secondLease.EndpointKey);

        await secondLease.ReturnAsync();
    }

    [Fact]
    public async Task MultiHost_Selection_Rotates_Between_Hosts()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-05T00:00:00Z"));
        var factory = new FakeSmtpConnectionFactory();
        factory.Enqueue(new FakeSmtpClientAdapter { EndpointKey = "smtp-a.local:2525" });
        factory.Enqueue(new FakeSmtpClientAdapter { EndpointKey = "smtp-b.local:2526" });

        await using var pool = new SmtpPool(
            CreateMultiHostOptions(maxPoolSize: 2, reconnectCooldown: TimeSpan.Zero),
            factory,
            clock);

        var lease1 = await pool.AcquireLeaseAsync();
        var lease2 = await pool.AcquireLeaseAsync();

        Assert.Equal(
            ["smtp-a.local:2525", "smtp-b.local:2526"],
            factory.RequestedHosts);

        await lease1.ReturnAsync();
        await lease2.ReturnAsync();
    }

    [Fact]
    public async Task Lower_Priority_Hosts_Are_Not_Selected_While_Higher_Priority_Hosts_Are_Healthy()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-05T00:00:00Z"));
        var factory = new FakeSmtpConnectionFactory();
        factory.Enqueue(new FakeSmtpClientAdapter { EndpointKey = "smtp-primary.local:2525" });
        factory.Enqueue(new FakeSmtpClientAdapter { EndpointKey = "smtp-primary.local:2525" });

        await using var pool = new SmtpPool(
            CreateMultiHostOptions(
                maxPoolSize: 2,
                reconnectCooldown: TimeSpan.Zero,
                hosts:
                [
                    new SmtpHostOptions { Host = "smtp-primary.local", Port = 2525, Priority = 0, Weight = 1 },
                    new SmtpHostOptions { Host = "smtp-secondary.local", Port = 2526, Priority = 10, Weight = 1 },
                ]),
            factory,
            clock);

        var lease1 = await pool.AcquireLeaseAsync();
        var lease2 = await pool.AcquireLeaseAsync();

        Assert.Equal(
            ["smtp-primary.local:2525", "smtp-primary.local:2525"],
            factory.RequestedHosts);

        await lease1.ReturnAsync();
        await lease2.ReturnAsync();
    }

    [Fact]
    public async Task Equal_Priority_Hosts_Use_Weighted_Distribution()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-05T00:00:00Z"));
        var factory = new FakeSmtpConnectionFactory();
        factory.Enqueue(new FakeSmtpClientAdapter { EndpointKey = "smtp-a.local:2525" });
        factory.Enqueue(new FakeSmtpClientAdapter { EndpointKey = "smtp-a.local:2525" });
        factory.Enqueue(new FakeSmtpClientAdapter { EndpointKey = "smtp-b.local:2526" });
        factory.Enqueue(new FakeSmtpClientAdapter { EndpointKey = "smtp-a.local:2525" });

        await using var pool = new SmtpPool(
            CreateMultiHostOptions(
                maxPoolSize: 4,
                reconnectCooldown: TimeSpan.Zero,
                hosts:
                [
                    new SmtpHostOptions { Host = "smtp-a.local", Port = 2525, Priority = 0, Weight = 3 },
                    new SmtpHostOptions { Host = "smtp-b.local", Port = 2526, Priority = 0, Weight = 1 },
                ]),
            factory,
            clock);

        var lease1 = await pool.AcquireLeaseAsync();
        var lease2 = await pool.AcquireLeaseAsync();
        var lease3 = await pool.AcquireLeaseAsync();
        var lease4 = await pool.AcquireLeaseAsync();

        Assert.Equal(
            ["smtp-a.local:2525", "smtp-a.local:2525", "smtp-b.local:2526", "smtp-a.local:2525"],
            factory.RequestedHosts);

        await lease1.ReturnAsync();
        await lease2.ReturnAsync();
        await lease3.ReturnAsync();
        await lease4.ReturnAsync();
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
        var metrics = new RecordingSmtpPoolMetrics();
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
            clock,
            metrics);

        var lease = await pool.AcquireLeaseAsync();
        await lease.ReturnAsync();

        clock.Advance(TimeSpan.FromSeconds(6));
        var replacementLease = await pool.AcquireLeaseAsync();

        Assert.Equal(1, staleClient.NoOpCalls);
        Assert.Equal(1, staleClient.DisposeCalls);
        Assert.Same(replacementClient, replacementLease.Client);
        Assert.Equal(1, metrics.Count(SmtpMetricNames.PoolKeepAliveFailureCount));
        var keepAliveMetric = metrics.Latest(SmtpMetricNames.PoolKeepAliveFailureCount);
        Assert.True(keepAliveMetric is not null, metrics.Dump());
        Assert.Equal("localhost:587", keepAliveMetric!.SmtpHost);

        await replacementLease.ReturnAsync();
    }

    [Fact]
    public async Task Lease_Duration_Is_Recorded_When_A_Lease_Is_Returned()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-05T00:00:00Z"));
        var factory = new FakeSmtpConnectionFactory();
        var metrics = new RecordingSmtpPoolMetrics();
        var client = new FakeSmtpClientAdapter();
        factory.Enqueue(client);

        await using var pool = new SmtpPool(CreateOptions(), factory, clock, metrics);

        var lease = await pool.AcquireLeaseAsync();
        clock.Advance(TimeSpan.FromSeconds(3));
        await lease.ReturnAsync();

        var metric = metrics.Latest(SmtpMetricNames.PoolLeaseDuration);
        Assert.True(metric is not null, metrics.Dump());
        Assert.Equal(SmtpMetricInstrumentKind.Histogram, metric!.InstrumentKind);
        Assert.Equal("localhost:587", metric.SmtpHost);
        Assert.Equal(3000, metric.Value);
    }

    [Fact]
    public async Task Host_Cooldown_And_Availability_Gauges_Reflect_Cooldown_State()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-05T00:00:00Z"));
        var factory = new FakeSmtpConnectionFactory();
        var metrics = new RecordingSmtpPoolMetrics();
        factory.Enqueue(new FakeSmtpClientAdapter { EndpointKey = "smtp-a.local:2525" });
        factory.Enqueue(new FakeSmtpClientAdapter { EndpointKey = "smtp-b.local:2526" });

        await using var pool = new SmtpPool(
            CreateMultiHostOptions(reconnectCooldown: TimeSpan.FromSeconds(30)),
            factory,
            clock,
            metrics);

        var lease = await pool.AcquireLeaseAsync();
        await lease.InvalidateAsync();

        Assert.Equal(1, metrics.Latest(SmtpMetricNames.PoolHostCooldownActive, "smtp-a.local:2525")!.Value);
        Assert.Equal(0, metrics.Latest(SmtpMetricNames.PoolHostAvailable, "smtp-a.local:2525")!.Value);
        Assert.Equal(0, metrics.Latest(SmtpMetricNames.PoolHostCooldownActive, "smtp-b.local:2526")!.Value);
        Assert.Equal(1, metrics.Latest(SmtpMetricNames.PoolHostAvailable, "smtp-b.local:2526")!.Value);

        clock.Advance(TimeSpan.FromSeconds(31));
        var recoveredLease = await pool.AcquireLeaseAsync();

        Assert.Equal(0, metrics.Latest(SmtpMetricNames.PoolHostCooldownActive, "smtp-a.local:2525")!.Value);
        Assert.Equal(1, metrics.Latest(SmtpMetricNames.PoolHostAvailable, "smtp-a.local:2525")!.Value);

        await recoveredLease.ReturnAsync();
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
        TimeSpan? maxReconnectCooldown = null,
        TimeSpan? keepAliveInterval = null,
        TimeSpan? idleTimeout = null,
        TimeSpan? minPoolRefillDelay = null,
        bool useExponentialBackoff = true,
        double jitterRatio = 0d)
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
            MaxReconnectCooldown = maxReconnectCooldown ?? TimeSpan.FromMinutes(5),
            KeepAliveInterval = keepAliveInterval ?? TimeSpan.FromMinutes(1),
            IdleTimeout = idleTimeout ?? TimeSpan.FromMinutes(2),
            MinPoolRefillDelay = minPoolRefillDelay ?? TimeSpan.Zero,
            UseExponentialBackoff = useExponentialBackoff,
            JitterRatio = jitterRatio,
        };
    }

    private static SmtpPoolOptions CreateMultiHostOptions(
        int maxPoolSize = 1,
        TimeSpan? reconnectCooldown = null,
        IReadOnlyList<SmtpHostOptions>? hosts = null)
    {
        return new SmtpPoolOptions
        {
            Hosts =
            [
                .. (hosts ??
                [
                    new SmtpHostOptions { Host = "smtp-a.local", Port = 2525 },
                    new SmtpHostOptions { Host = "smtp-b.local", Port = 2526 },
                ]),
            ],
            MaxPoolSize = maxPoolSize,
            AcquireTimeout = TimeSpan.FromSeconds(15),
            ReconnectCooldown = reconnectCooldown ?? TimeSpan.FromSeconds(30),
            MaxReconnectCooldown = TimeSpan.FromMinutes(5),
            KeepAliveInterval = TimeSpan.FromMinutes(1),
            IdleTimeout = TimeSpan.FromMinutes(2),
            UseExponentialBackoff = true,
            JitterRatio = 0d,
        };
    }
}
