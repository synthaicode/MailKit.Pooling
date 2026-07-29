using PooledMailKit.Abstractions;
using PooledMailKit.Errors;
using PooledMailKit.Metrics;
using PooledMailKit.Options;
using PooledMailKit.Retry;
using System.Threading;

namespace PooledMailKit.Pooling;

internal sealed class SmtpPool : IAsyncDisposable
{
    private readonly ISmtpConnectionFactory connectionFactory;
    private readonly IClock clock;
    private readonly ISmtpPoolMetrics metrics;
    private readonly SmtpPoolOptions options;
    private readonly HostRuntimeState[] hostStates;
    private readonly object sync = new();
    private readonly Dictionary<Guid, PooledConnection> connections = new();
    private readonly Queue<Guid> idleConnectionIds = new();
    private TaskCompletionSource connectionAvailableSignal = CreateAvailabilitySignal();
    // Written only under `sync`; volatile so the lock-free read in
    // ThrowIfDisposed observes the latest value.
    private volatile bool disposed;
    private DateTimeOffset? nextMinPoolRefillAllowedAt;
    private int pendingConnectionCreations;
    private int pendingConnectionDisposals;
    private int waitingCallers;
    private static readonly TimeSpan ConnectionDisposalTimeout = TimeSpan.FromSeconds(1);

    public SmtpPool(
        SmtpPoolOptions options,
        ISmtpConnectionFactory connectionFactory,
        IClock? clock = null,
        ISmtpPoolMetrics? metrics = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        this.clock = clock ?? SystemClock.Instance;
        this.metrics = metrics ?? NoOpSmtpPoolMetrics.Instance;

        // The pool is constructible without the DI package, so the shared
        // rules are enforced here as well as at registration time.
        SmtpPoolOptionsValidator.Validate(options);
        hostStates = options.GetConfiguredHosts()
            .Select(host => new HostRuntimeState(host))
            .ToArray();
    }

    public async Task<SmtpConnectionLease> AcquireLeaseAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await TryEnsureMinimumPoolSizeAsync(cancellationToken).ConfigureAwait(false);

        var acquireStartedAt = clock.UtcNow;
        var deadline = clock.UtcNow + options.AcquireTimeout;
        var reconnectSuppressedRecorded = false;
        Interlocked.Increment(ref waitingCallers);

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Observe the availability signal before inspecting pool state
                // so a return that lands between the check and the wait still
                // wakes this caller (no lost wake-up, no stale signal).
                var availabilitySignal = ObserveConnectionAvailability();
                await DisposeExpiredIdleConnectionsAsync(cancellationToken).ConfigureAwait(false);

                var now = clock.UtcNow;
                if (now >= deadline)
                {
                    RecordAcquireWaitTime(acquireStartedAt, now);
                    metrics.Record(new SmtpPoolMetricEvent(
                        SmtpMetricNames.PoolAcquireExhaustedCount,
                        SmtpMetricInstrumentKind.Counter,
                        1));
                    throw new SmtpPoolExhaustedException("Timed out while waiting for an SMTP connection lease.");
                }

                List<PooledConnection>? brokenConnections = null;
                Guid? reusableConnectionId = null;
                HostRuntimeState? selectedHost = null;
                bool shouldCreateConnection = false;
                bool reconnectSuppressed = false;

                lock (sync)
                {
                    ThrowIfDisposed();

                    while (idleConnectionIds.Count > 0)
                    {
                        var candidateId = idleConnectionIds.Dequeue();
                        if (!connections.TryGetValue(candidateId, out var candidate))
                        {
                            continue;
                        }

                        if (IsConnectionUnusable(candidate))
                        {
                            brokenConnections ??= [];
                            brokenConnections.Add(candidate);
                            connections.Remove(candidateId);
                            pendingConnectionDisposals++;
                            continue;
                        }

                        candidate.IsLeased = true;
                        reusableConnectionId = candidateId;
                        break;
                    }

                    if (reusableConnectionId is null)
                    {
                        var liveConnections = GetLiveConnectionCountUnsafe();
                        if (liveConnections < options.MaxPoolSize && TrySelectHostForCreation(now, out selectedHost))
                        {
                            pendingConnectionCreations++;
                            shouldCreateConnection = true;
                        }
                        else if (liveConnections < options.MaxPoolSize)
                        {
                            reconnectSuppressed = true;
                        }
                    }
                }

                if (reconnectSuppressed)
                {
                    if (!reconnectSuppressedRecorded)
                    {
                        metrics.Record(new SmtpPoolMetricEvent(
                            SmtpMetricNames.PoolReconnectSuppressed,
                            SmtpMetricInstrumentKind.Counter,
                            1));
                        reconnectSuppressedRecorded = true;
                    }
                }
                else
                {
                    reconnectSuppressedRecorded = false;
                }

                if (brokenConnections is not null)
                {
                    foreach (var broken in brokenConnections)
                    {
                        await DisposeConnectionAndReleaseCapacityAsync(broken, cancellationToken).ConfigureAwait(false);
                    }

                    await TryEnsureMinimumPoolSizeAsync(cancellationToken).ConfigureAwait(false);
                }

                if (reusableConnectionId is not null)
                {
                    PooledConnection? leasedConnection;

                    lock (sync)
                    {
                        connections.TryGetValue(reusableConnectionId.Value, out leasedConnection);
                    }

                    if (leasedConnection is null)
                    {
                        continue;
                    }

                    if (await ValidateLeasedConnectionAsync(leasedConnection, cancellationToken).ConfigureAwait(false))
                    {
                        RecordAcquireWaitTime(acquireStartedAt, clock.UtcNow);
                        RecordCurrentState();
                        return new SmtpConnectionLease(this, leasedConnection.Id, leasedConnection.Client, clock.UtcNow);
                    }

                    continue;
                }

                if (shouldCreateConnection)
                {
                    try
                    {
                        var pooledConnection = await CreateLeasedConnectionAsync(selectedHost!, cancellationToken).ConfigureAwait(false);
                        lock (sync)
                        {
                            pendingConnectionCreations--;
                            RegisterSuccessfulConnectionCreation(pooledConnection.HostState);
                            connections.Add(pooledConnection.Id, pooledConnection);
                        }

                        metrics.Record(new SmtpPoolMetricEvent(
                            SmtpMetricNames.PoolConnectionsCreated,
                            SmtpMetricInstrumentKind.Counter,
                            1,
                            pooledConnection.HostState.EndpointKey));
                        RecordAcquireWaitTime(acquireStartedAt, clock.UtcNow);
                        RecordCurrentState();
                        return new SmtpConnectionLease(this, pooledConnection.Id, pooledConnection.Client, clock.UtcNow);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        lock (sync)
                        {
                            pendingConnectionCreations--;
                        }

                        throw;
                    }
                    catch
                    {
                        bool anotherHostEligible;
                        bool hasLiveConnections;

                        lock (sync)
                        {
                            pendingConnectionCreations--;
                            ApplyReconnectCooldown(selectedHost!, clock.UtcNow);
                            anotherHostEligible = AnyOtherHostEligibleForCreationUnsafe(selectedHost!, clock.UtcNow);
                            hasLiveConnections = GetLiveConnectionCountUnsafe() > 0;
                        }

                        metrics.Record(new SmtpPoolMetricEvent(
                            SmtpMetricNames.PoolConnectionCreateFailures,
                            SmtpMetricInstrumentKind.Counter,
                            1,
                            selectedHost?.EndpointKey));

                        if (anotherHostEligible)
                        {
                            continue;
                        }

                        if (!hasLiveConnections)
                        {
                            throw;
                        }

                        // A leased connection may still be returned before the deadline, so keep waiting.
                    }
                }

                var nextDelay = ComputeWaitDuration(clock.UtcNow, deadline);
                await WaitForConnectionAvailabilityAsync(nextDelay, availabilitySignal, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Decrement(ref waitingCallers);
        }
    }

    public async Task WarmupAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await EnsureMinimumPoolSizeAsync(cancellationToken).ConfigureAwait(false);
    }

    public SmtpPoolSnapshot GetSnapshot()
    {
        lock (sync)
        {
            return GetSnapshotUnsafe();
        }
    }

    private SmtpPoolSnapshot GetSnapshotUnsafe()
    {
        return new SmtpPoolSnapshot(
            GetLiveConnectionCountUnsafe(),
            CountIdleConnectionsUnsafe(),
            connections.Values.Count(static connection => connection.IsLeased),
            Volatile.Read(ref waitingCallers),
            GetNextCreationAllowedAtUnsafe());
    }

    public async ValueTask DisposeAsync()
    {
        List<PooledConnection> snapshot;

        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            snapshot = [.. connections.Values];
            connections.Clear();
            idleConnectionIds.Clear();
        }

        foreach (var connection in snapshot)
        {
            await DisposeConnectionAsync(connection, CancellationToken.None, "shutdown").ConfigureAwait(false);
        }

        // Wake every blocked acquirer; the next loop iteration surfaces
        // ObjectDisposedException(SmtpPool).
        SignalConnectionAvailable();
        RecordCurrentState();
    }

    private static TaskCompletionSource CreateAvailabilitySignal()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private int GetLiveConnectionCountUnsafe()
    {
        return connections.Count + pendingConnectionCreations + pendingConnectionDisposals;
    }

    // Edge-triggered pulse: wakes exactly the callers that observed the
    // current signal before this call. A pulse with no observers leaves no
    // stale state behind, because the next observer waits on a fresh signal —
    // unlike a counting semaphore, where a release without a waiter leaves a
    // stale permit that wakes a later acquirer without a real return event.
    private void SignalConnectionAvailable()
    {
        var completedSignal = Interlocked.Exchange(ref connectionAvailableSignal, CreateAvailabilitySignal());
        completedSignal.TrySetResult();
    }

    private Task ObserveConnectionAvailability()
    {
        return Volatile.Read(ref connectionAvailableSignal).Task;
    }

    internal async ValueTask ReturnLeaseAsync(
        Guid connectionId,
        bool isReusable,
        DateTimeOffset leasedAtUtc,
        CancellationToken cancellationToken)
    {
        PooledConnection? connectionToDispose = null;
        string? endpointKey = null;
        var unknownReturn = false;

        lock (sync)
        {
            if (!connections.TryGetValue(connectionId, out var connection))
            {
                // Unknown or already-removed ids are ignored by design: lease
                // completion is idempotent (double return, return after the
                // pool dropped the connection). The metric below keeps
                // unexpected foreign returns observable.
                unknownReturn = true;
            }
            else
            {
                endpointKey = connection.HostState.EndpointKey;
                connection.IsLeased = false;

                if (disposed || !isReusable || !connection.Client.IsConnected || !connection.Client.IsAuthenticated)
                {
                    // A discarded lease reflects a message-level or connection-level failure.
                    // Host reconnect cooldown is reserved for connection creation failures.
                    connections.Remove(connectionId);
                    pendingConnectionDisposals++;
                    if (!disposed && connections.Count + pendingConnectionCreations < options.MinPoolSize)
                    {
                        ScheduleMinPoolRefill();
                    }
                    connectionToDispose = connection;
                }
                else
                {
                    connection.LastReturnedAt = clock.UtcNow;
                    idleConnectionIds.Enqueue(connectionId);
                    SignalConnectionAvailable();
                }
            }
        }

        if (unknownReturn)
        {
            metrics.Record(new SmtpPoolMetricEvent(
                SmtpMetricNames.PoolLeaseReturnIgnoredCount,
                SmtpMetricInstrumentKind.Counter,
                1));
            return;
        }

        if (connectionToDispose is not null)
        {
            await DisposeConnectionAndReleaseCapacityAsync(
                connectionToDispose,
                cancellationToken,
                isReusable ? "shutdown" : "broken").ConfigureAwait(false);
            await TryEnsureMinimumPoolSizeAsync(cancellationToken).ConfigureAwait(false);
        }

        RecordLeaseDuration(leasedAtUtc, clock.UtcNow, endpointKey);
        RecordCurrentState();
    }

    private async Task TryEnsureMinimumPoolSizeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EnsureMinimumPoolSizeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Intentional swallow: warm refill is best-effort on the acquire and return paths.
        catch
        {
            // Creation failures already recorded metrics and applied the host cooldown.
        }
#pragma warning restore CA1031
    }

    private async Task EnsureMinimumPoolSizeAsync(CancellationToken cancellationToken)
    {
        if (options.MinPoolSize <= 0)
        {
            return;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DisposeExpiredIdleConnectionsAsync(cancellationToken).ConfigureAwait(false);

            var now = clock.UtcNow;
            HostRuntimeState? selectedHost = null;
            bool shouldCreateConnection;

            lock (sync)
            {
                ThrowIfDisposed();

                if (nextMinPoolRefillAllowedAt is { } nextAllowedAt && now < nextAllowedAt)
                {
                    return;
                }

                var targetConnectionCount = GetLiveConnectionCountUnsafe();
                shouldCreateConnection = targetConnectionCount < options.MinPoolSize
                    && TrySelectHostForCreation(now, out selectedHost);

                if (shouldCreateConnection)
                {
                    pendingConnectionCreations++;
                }
            }

            if (!shouldCreateConnection)
            {
                return;
            }

            try
            {
                var pooledConnection = await CreateIdleConnectionAsync(selectedHost!, cancellationToken).ConfigureAwait(false);

                lock (sync)
                {
                    pendingConnectionCreations--;
                    RegisterSuccessfulConnectionCreation(pooledConnection.HostState);
                    connections.Add(pooledConnection.Id, pooledConnection);
                    idleConnectionIds.Enqueue(pooledConnection.Id);
                    nextMinPoolRefillAllowedAt = null;
                }

                metrics.Record(new SmtpPoolMetricEvent(
                    SmtpMetricNames.PoolConnectionsCreated,
                    SmtpMetricInstrumentKind.Counter,
                    1,
                    pooledConnection.HostState.EndpointKey));
                RecordCurrentState();
            }
            catch
            {
                lock (sync)
                {
                    pendingConnectionCreations--;
                    ApplyReconnectCooldown(selectedHost!, now);
                }

                metrics.Record(new SmtpPoolMetricEvent(
                    SmtpMetricNames.PoolConnectionCreateFailures,
                    SmtpMetricInstrumentKind.Counter,
                    1,
                    selectedHost?.EndpointKey));

                throw;
            }
        }
    }

    private async Task<bool> ValidateLeasedConnectionAsync(PooledConnection connection, CancellationToken cancellationToken)
    {
        if (!RequiresKeepAlive(connection))
        {
            return true;
        }

        try
        {
            await connection.Client.NoOpAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
#pragma warning disable CA1031 // Intentional absorb: any keepalive failure means the idle connection is dropped and the acquire loop continues.
        catch
        {
            metrics.Record(new SmtpPoolMetricEvent(
                SmtpMetricNames.PoolConnectionsDropped,
                SmtpMetricInstrumentKind.Counter,
                1,
                connection.HostState.EndpointKey,
                Reason: "keepalive_failure"));
            metrics.Record(new SmtpPoolMetricEvent(
                SmtpMetricNames.PoolKeepAliveFailureCount,
                SmtpMetricInstrumentKind.Counter,
                1,
                connection.HostState.EndpointKey));
            // The drop and the min-pool refill are pool-internal cleanup; they
            // must complete even when the caller's token is already cancelled,
            // matching the lease's own DisposeAsync behavior.
            await ReturnLeaseAsync(connection.Id, isReusable: false, clock.UtcNow, CancellationToken.None).ConfigureAwait(false);
            return false;
        }
#pragma warning restore CA1031
    }

    private bool IsConnectionUnusable(PooledConnection connection)
    {
        return !connection.Client.IsConnected
            || !connection.Client.IsAuthenticated
            || IsIdleExpired(connection);
    }

    private bool IsIdleExpired(PooledConnection connection)
    {
        if (options.IdleTimeout <= TimeSpan.Zero)
        {
            return false;
        }

        if (connection.LastReturnedAt == default)
        {
            return false;
        }

        return clock.UtcNow - connection.LastReturnedAt >= options.IdleTimeout;
    }

    private bool RequiresKeepAlive(PooledConnection connection)
    {
        if (!options.EnableKeepAlive)
        {
            return false;
        }

        if (connection.LastReturnedAt == default)
        {
            return false;
        }

        return clock.UtcNow - connection.LastReturnedAt >= options.KeepAliveInterval;
    }

    private async Task DisposeExpiredIdleConnectionsAsync(CancellationToken cancellationToken)
    {
        List<PooledConnection>? expiredConnections = null;
        var now = clock.UtcNow;

        lock (sync)
        {
            if (options.IdleTimeout <= TimeSpan.Zero || idleConnectionIds.Count == 0)
            {
                return;
            }

            var retained = new Queue<Guid>(idleConnectionIds.Count);
            while (idleConnectionIds.Count > 0)
            {
                var connectionId = idleConnectionIds.Dequeue();
                if (!connections.TryGetValue(connectionId, out var connection))
                {
                    continue;
                }

                if (!connection.IsLeased
                    && connection.LastReturnedAt != default
                    && now - connection.LastReturnedAt >= options.IdleTimeout)
                {
                    expiredConnections ??= [];
                    expiredConnections.Add(connection);
                    connections.Remove(connectionId);
                    pendingConnectionDisposals++;
                    if (connections.Count + pendingConnectionCreations < options.MinPoolSize)
                    {
                        ScheduleMinPoolRefill();
                    }
                    continue;
                }

                retained.Enqueue(connectionId);
            }

            while (retained.Count > 0)
            {
                idleConnectionIds.Enqueue(retained.Dequeue());
            }
        }

        if (expiredConnections is null)
        {
            return;
        }

        foreach (var expiredConnection in expiredConnections)
        {
            await DisposeConnectionAndReleaseCapacityAsync(expiredConnection, cancellationToken, "idle_timeout").ConfigureAwait(false);
        }
    }

    private TimeSpan ComputeWaitDuration(DateTimeOffset now, DateTimeOffset deadline)
    {
        var remaining = deadline - now;
        if (remaining <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var nextAllowedAt = GetNextCreationAllowedAt();
        var waitUntil = nextAllowedAt is { } candidate && candidate > now
            ? candidate
            : now + TimeSpan.FromMilliseconds(25);
        var cooldownWait = waitUntil - now;

        return cooldownWait < remaining ? cooldownWait : remaining;
    }

    private async Task WaitForConnectionAvailabilityAsync(
        TimeSpan nextDelay,
        Task availabilitySignal,
        CancellationToken cancellationToken)
    {
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timerTask = clock.Delay(nextDelay, waitCts.Token);

        await Task.WhenAny(timerTask, availabilitySignal).ConfigureAwait(false);
        await waitCts.CancelAsync().ConfigureAwait(false);

        try
        {
            await timerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Only the internal wake-up cancellation of the timer is absorbed;
            // caller cancellation propagates.
        }
    }

    private async Task<PooledConnection> CreateLeasedConnectionAsync(HostRuntimeState hostState, CancellationToken cancellationToken)
    {
        var client = await connectionFactory.CreateAuthenticatedClientAsync(hostState.Host, cancellationToken).ConfigureAwait(false);
        return new PooledConnection(Guid.NewGuid(), client, hostState)
        {
            IsLeased = true,
        };
    }

    private async Task<PooledConnection> CreateIdleConnectionAsync(HostRuntimeState hostState, CancellationToken cancellationToken)
    {
        var client = await connectionFactory.CreateAuthenticatedClientAsync(hostState.Host, cancellationToken).ConfigureAwait(false);
        return new PooledConnection(Guid.NewGuid(), client, hostState)
        {
            IsLeased = false,
            LastReturnedAt = clock.UtcNow,
        };
    }

    private async Task DisposeConnectionAsync(PooledConnection connection, CancellationToken cancellationToken, string reason = "broken")
    {
        using var disposalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        disposalCts.CancelAfter(ConnectionDisposalTimeout);

        try
        {
            if (connection.Client.IsConnected)
            {
                var disconnectTask = connection.Client.DisconnectAsync(quit: false, disposalCts.Token);
                try
                {
                    await disconnectTask.WaitAsync(ConnectionDisposalTimeout).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _ = disconnectTask.ContinueWith(
                        static task => _ = task.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }
        }
#pragma warning disable CA1031 // Intentional swallow: disconnect is a best-effort courtesy close; the connection is dropped either way.
        catch
        {
        }
#pragma warning restore CA1031

        try
        {
            await connection.Client.DisposeAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Intentional swallow: one faulting adapter dispose must not abort disposal of the remaining pooled connections.
        catch
        {
        }
#pragma warning restore CA1031

        metrics.Record(new SmtpPoolMetricEvent(
            SmtpMetricNames.PoolConnectionsDropped,
            SmtpMetricInstrumentKind.Counter,
            1,
            connection.Client.EndpointKey,
            Reason: reason));
    }

    private async Task DisposeConnectionAndReleaseCapacityAsync(
        PooledConnection connection,
        CancellationToken cancellationToken,
        string reason = "broken")
    {
        try
        {
            await DisposeConnectionAsync(connection, cancellationToken, reason).ConfigureAwait(false);
        }
        finally
        {
            lock (sync)
            {
                pendingConnectionDisposals--;
            }

            SignalConnectionAvailable();
        }
    }

    private void RecordCurrentState()
    {
        var now = clock.UtcNow;
        SmtpPoolSnapshot snapshot;
        var hostCooldownStates = new (string EndpointKey, bool IsInCooldown)[hostStates.Length];

        lock (sync)
        {
            snapshot = GetSnapshotUnsafe();
            for (var index = 0; index < hostStates.Length; index++)
            {
                var hostState = hostStates[index];
                hostCooldownStates[index] = (
                    hostState.EndpointKey,
                    hostState.NextCreationAllowedAt is { } nextAllowedAt && nextAllowedAt > now);
            }
        }

        metrics.Record(new SmtpPoolMetricEvent(
            SmtpMetricNames.PoolConnectionsActive,
            SmtpMetricInstrumentKind.Gauge,
            snapshot.LeasedConnections));
        metrics.Record(new SmtpPoolMetricEvent(
            SmtpMetricNames.PoolConnectionsIdle,
            SmtpMetricInstrumentKind.Gauge,
            snapshot.IdleConnections));

        foreach (var (endpointKey, isInCooldown) in hostCooldownStates)
        {
            metrics.Record(new SmtpPoolMetricEvent(
                SmtpMetricNames.PoolHostCooldownActive,
                SmtpMetricInstrumentKind.Gauge,
                isInCooldown ? 1 : 0,
                endpointKey));
            metrics.Record(new SmtpPoolMetricEvent(
                SmtpMetricNames.PoolHostAvailable,
                SmtpMetricInstrumentKind.Gauge,
                isInCooldown ? 0 : 1,
                endpointKey));
        }
    }

    private void RecordAcquireWaitTime(DateTimeOffset startedAt, DateTimeOffset completedAt)
    {
        var waitDuration = completedAt - startedAt;
        if (waitDuration < TimeSpan.Zero)
        {
            waitDuration = TimeSpan.Zero;
        }

        metrics.Record(new SmtpPoolMetricEvent(
            SmtpMetricNames.PoolAcquireWaitTime,
            SmtpMetricInstrumentKind.Histogram,
            waitDuration.TotalMilliseconds));
    }

    private void RecordLeaseDuration(DateTimeOffset leasedAtUtc, DateTimeOffset completedAtUtc, string? smtpHost)
    {
        var leaseDuration = completedAtUtc - leasedAtUtc;
        if (leaseDuration < TimeSpan.Zero)
        {
            leaseDuration = TimeSpan.Zero;
        }

        metrics.Record(new SmtpPoolMetricEvent(
            SmtpMetricNames.PoolLeaseDuration,
            SmtpMetricInstrumentKind.Histogram,
            leaseDuration.TotalMilliseconds,
            smtpHost));
    }

    private bool TrySelectHostForCreation(DateTimeOffset now, out HostRuntimeState? selectedHost)
    {
        List<HostRuntimeState>? eligibleHosts = null;
        var bestPriority = int.MaxValue;

        foreach (var candidate in hostStates)
        {
            if (candidate.NextCreationAllowedAt is { } nextAllowedAt && now < nextAllowedAt)
            {
                continue;
            }

            if (candidate.Host.Priority > bestPriority)
            {
                continue;
            }

            if (candidate.Host.Priority < bestPriority)
            {
                eligibleHosts = [];
                bestPriority = candidate.Host.Priority;
            }

            eligibleHosts ??= [];
            eligibleHosts.Add(candidate);
        }

        if (eligibleHosts is null || eligibleHosts.Count == 0)
        {
            selectedHost = null;
            return false;
        }

        var totalWeight = 0;
        HostRuntimeState? bestHost = null;
        foreach (var candidate in eligibleHosts)
        {
            candidate.CurrentWeight += candidate.Host.Weight;
            totalWeight += candidate.Host.Weight;

            if (bestHost is null || candidate.CurrentWeight > bestHost.CurrentWeight)
            {
                bestHost = candidate;
            }
        }

        bestHost!.CurrentWeight -= totalWeight;
        selectedHost = bestHost;
        return true;
    }

    private bool AnyOtherHostEligibleForCreationUnsafe(HostRuntimeState failedHost, DateTimeOffset now)
    {
        foreach (var hostState in hostStates)
        {
            if (ReferenceEquals(hostState, failedHost))
            {
                continue;
            }

            if (hostState.NextCreationAllowedAt is not { } nextAllowedAt || now >= nextAllowedAt)
            {
                return true;
            }
        }

        return false;
    }

    private int CountIdleConnectionsUnsafe()
    {
        var idleCount = 0;
        foreach (var connectionId in idleConnectionIds)
        {
            if (connections.TryGetValue(connectionId, out var connection) && !connection.IsLeased)
            {
                idleCount++;
            }
        }

        return idleCount;
    }

    private void ApplyReconnectCooldown(HostRuntimeState hostState, DateTimeOffset failedAt)
    {
        hostState.ConsecutiveConnectionFailures++;
        var cooldown = CalculateReconnectCooldown(hostState.ConsecutiveConnectionFailures);
        hostState.NextCreationAllowedAt = failedAt + cooldown;
        hostState.CurrentWeight = 0;
    }

    private TimeSpan CalculateReconnectCooldown(int consecutiveFailures)
    {
        var cooldown = RetryDelayCalculator.CalculateNextDelay(
            consecutiveFailures,
            options.ReconnectCooldown,
            options.UseExponentialBackoff,
            options.JitterRatio);

        if (options.MaxReconnectCooldown > TimeSpan.Zero && cooldown > options.MaxReconnectCooldown)
        {
            return options.MaxReconnectCooldown;
        }

        return cooldown;
    }

    private static void RegisterSuccessfulConnectionCreation(HostRuntimeState hostState)
    {
        hostState.ConsecutiveConnectionFailures = 0;
        hostState.NextCreationAllowedAt = null;
    }

    private DateTimeOffset? GetNextCreationAllowedAt()
    {
        lock (sync)
        {
            return GetNextCreationAllowedAtUnsafe();
        }
    }

    private DateTimeOffset? GetNextCreationAllowedAtUnsafe()
    {
        DateTimeOffset? nextCreationAllowedAt = nextMinPoolRefillAllowedAt;
        foreach (var hostState in hostStates)
        {
            if (hostState.NextCreationAllowedAt is null)
            {
                continue;
            }

            if (nextCreationAllowedAt is null || hostState.NextCreationAllowedAt < nextCreationAllowedAt)
            {
                nextCreationAllowedAt = hostState.NextCreationAllowedAt;
            }
        }

        return nextCreationAllowedAt;
    }

    private void ScheduleMinPoolRefill()
    {
        if (options.MinPoolRefillDelay <= TimeSpan.Zero)
        {
            nextMinPoolRefillAllowedAt = null;
            return;
        }

        var candidate = clock.UtcNow + options.MinPoolRefillDelay;
        if (nextMinPoolRefillAllowedAt is null || candidate > nextMinPoolRefillAllowedAt)
        {
            nextMinPoolRefillAllowedAt = candidate;
        }
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(SmtpPool));
        }
    }

    private sealed class PooledConnection
    {
        public PooledConnection(Guid id, ISmtpClientAdapter client, HostRuntimeState hostState)
        {
            Id = id;
            Client = client;
            HostState = hostState;
        }

        public Guid Id { get; }

        public ISmtpClientAdapter Client { get; }

        public HostRuntimeState HostState { get; }

        public bool IsLeased { get; set; }

        public DateTimeOffset LastReturnedAt { get; set; }
    }

    private sealed class HostRuntimeState
    {
        public HostRuntimeState(SmtpHostOptions host)
        {
            Host = host;
            EndpointKey = host.ToEndpointKey();
        }

        public SmtpHostOptions Host { get; }

        public string EndpointKey { get; }

        public DateTimeOffset? NextCreationAllowedAt { get; set; }

        public int CurrentWeight { get; set; }

        public int ConsecutiveConnectionFailures { get; set; }
    }
}
