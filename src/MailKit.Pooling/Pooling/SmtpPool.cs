using MailKit.Pooling.Abstractions;
using MailKit.Pooling.Errors;
using MailKit.Pooling.Metrics;
using MailKit.Pooling.Options;

namespace MailKit.Pooling.Pooling;

public sealed class SmtpPool : IAsyncDisposable
{
    private readonly ISmtpConnectionFactory connectionFactory;
    private readonly IClock clock;
    private readonly ISmtpPoolMetrics metrics;
    private readonly SmtpPoolOptions options;
    private readonly HostRuntimeState[] hostStates;
    private readonly Dictionary<string, HostRuntimeState> hostStatesByEndpointKey;
    private readonly object sync = new();
    private readonly Dictionary<Guid, PooledConnection> connections = new();
    private readonly Queue<Guid> idleConnectionIds = new();
    private bool disposed;
    private int nextHostIndex;
    private int pendingConnectionCreations;
    private int waitingCallers;

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
        hostStates = options.GetConfiguredHosts()
            .Select(host => new HostRuntimeState(host))
            .ToArray();
        hostStatesByEndpointKey = hostStates.ToDictionary(
            state => state.EndpointKey,
            state => state,
            StringComparer.Ordinal);

        if (options.MaxPoolSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxPoolSize), "MaxPoolSize must be greater than zero.");
        }

        if (options.MinPoolSize < 0 || options.MinPoolSize > options.MaxPoolSize)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MinPoolSize), "MinPoolSize must be between zero and MaxPoolSize.");
        }

        if (options.AcquireTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.AcquireTimeout), "AcquireTimeout must be greater than zero.");
        }

        if (hostStates.Length == 0)
        {
            throw new ArgumentException("At least one SMTP host must be configured.", nameof(options));
        }
    }

    public async Task<SmtpConnectionLease> AcquireLeaseAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await EnsureMinimumPoolSizeAsync(cancellationToken).ConfigureAwait(false);

        var deadline = clock.UtcNow + options.AcquireTimeout;
        waitingCallers++;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await DisposeExpiredIdleConnectionsAsync(cancellationToken).ConfigureAwait(false);

                var now = clock.UtcNow;
                if (now >= deadline)
                {
                    metrics.Record(new SmtpPoolMetricEvent(SmtpMetricNames.AcquireTimeouts, 1));
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
                            continue;
                        }

                        candidate.IsLeased = true;
                        reusableConnectionId = candidateId;
                        break;
                    }

                    if (reusableConnectionId is null)
                    {
                        var liveConnections = connections.Count + pendingConnectionCreations;
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
                    metrics.Record(new SmtpPoolMetricEvent(SmtpMetricNames.ReconnectSuppressed, 1));
                }

                if (brokenConnections is not null)
                {
                    foreach (var broken in brokenConnections)
                    {
                        await DisposeConnectionAsync(broken, cancellationToken).ConfigureAwait(false);
                    }

                    await EnsureMinimumPoolSizeAsync(cancellationToken).ConfigureAwait(false);
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
                        RecordCurrentState();
                        return new SmtpConnectionLease(this, leasedConnection.Id, leasedConnection.Client);
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
                            connections.Add(pooledConnection.Id, pooledConnection);
                        }

                        metrics.Record(new SmtpPoolMetricEvent(
                            SmtpMetricNames.ConnectionsCreated,
                            1,
                            pooledConnection.HostState.EndpointKey));
                        RecordCurrentState();
                        return new SmtpConnectionLease(this, pooledConnection.Id, pooledConnection.Client);
                    }
                    catch
                    {
                        lock (sync)
                        {
                            pendingConnectionCreations--;
                            ApplyCooldown(selectedHost!, now + options.ReconnectCooldown);
                        }

                        metrics.Record(new SmtpPoolMetricEvent(
                            SmtpMetricNames.ConnectionCreateFailures,
                            1,
                            selectedHost?.EndpointKey));

                        throw;
                    }
                }

                var nextDelay = ComputeWaitDuration(clock.UtcNow, deadline);
                await clock.Delay(nextDelay, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            waitingCallers--;
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
            return new SmtpPoolSnapshot(
                connections.Count + pendingConnectionCreations,
                idleConnectionIds.Count,
                connections.Values.Count(static connection => connection.IsLeased),
                waitingCallers,
                GetNextCreationAllowedAtUnsafe());
        }
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
            await DisposeConnectionAsync(connection, CancellationToken.None).ConfigureAwait(false);
        }

        RecordCurrentState();
    }

    internal async ValueTask ReturnLeaseAsync(
        Guid connectionId,
        bool isReusable,
        CancellationToken cancellationToken)
    {
        PooledConnection? connectionToDispose = null;

        lock (sync)
        {
            if (!connections.TryGetValue(connectionId, out var connection))
            {
                return;
            }

            connection.IsLeased = false;

            if (disposed || !isReusable || !connection.Client.IsConnected || !connection.Client.IsAuthenticated)
            {
                connections.Remove(connectionId);
                ApplyCooldown(connection.HostState, clock.UtcNow + options.ReconnectCooldown);
                connectionToDispose = connection;
            }
            else
            {
                connection.LastReturnedAt = clock.UtcNow;
                idleConnectionIds.Enqueue(connectionId);
            }
        }

        if (connectionToDispose is not null)
        {
            await DisposeConnectionAsync(connectionToDispose, cancellationToken).ConfigureAwait(false);
            await EnsureMinimumPoolSizeAsync(cancellationToken).ConfigureAwait(false);
        }

        RecordCurrentState();
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

                var targetConnectionCount = connections.Count + pendingConnectionCreations;
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
                    connections.Add(pooledConnection.Id, pooledConnection);
                    idleConnectionIds.Enqueue(pooledConnection.Id);
                }

                metrics.Record(new SmtpPoolMetricEvent(
                    SmtpMetricNames.ConnectionsCreated,
                    1,
                    pooledConnection.HostState.EndpointKey));
                RecordCurrentState();
            }
            catch
            {
                lock (sync)
                {
                    pendingConnectionCreations--;
                    ApplyCooldown(selectedHost!, now + options.ReconnectCooldown);
                }

                metrics.Record(new SmtpPoolMetricEvent(
                    SmtpMetricNames.ConnectionCreateFailures,
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
        catch
        {
            metrics.Record(new SmtpPoolMetricEvent(
                SmtpMetricNames.KeepAliveFailures,
                1,
                connection.HostState.EndpointKey));
            await ReturnLeaseAsync(connection.Id, isReusable: false, cancellationToken).ConfigureAwait(false);
            return false;
        }
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
            await DisposeConnectionAsync(expiredConnection, cancellationToken).ConfigureAwait(false);
        }
    }

    private TimeSpan ComputeWaitDuration(DateTimeOffset now, DateTimeOffset deadline)
    {
        var remaining = deadline - now;
        if (remaining <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var cooldownUntil = GetNextCreationAllowedAt();
        var cooldownWait = cooldownUntil is { } nextAllowedAt && nextAllowedAt > now
            ? nextAllowedAt - now
            : TimeSpan.FromMilliseconds(25);

        return cooldownWait < remaining ? cooldownWait : remaining;
    }

    private async Task<PooledConnection> CreateLeasedConnectionAsync(HostRuntimeState hostState, CancellationToken cancellationToken)
    {
        metrics.Record(new SmtpPoolMetricEvent(SmtpMetricNames.ConnectionCreateAttempts, 1));
        var client = await connectionFactory.CreateAuthenticatedClientAsync(hostState.Host, cancellationToken).ConfigureAwait(false);
        return new PooledConnection(Guid.NewGuid(), client, hostState)
        {
            IsLeased = true,
        };
    }

    private async Task<PooledConnection> CreateIdleConnectionAsync(HostRuntimeState hostState, CancellationToken cancellationToken)
    {
        metrics.Record(new SmtpPoolMetricEvent(SmtpMetricNames.ConnectionCreateAttempts, 1));
        var client = await connectionFactory.CreateAuthenticatedClientAsync(hostState.Host, cancellationToken).ConfigureAwait(false);
        return new PooledConnection(Guid.NewGuid(), client, hostState)
        {
            IsLeased = false,
            LastReturnedAt = clock.UtcNow,
        };
    }

    private async Task DisposeConnectionAsync(PooledConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            if (connection.Client.IsConnected)
            {
                await connection.Client.DisconnectAsync(quit: false, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
        }

        await connection.Client.DisposeAsync().ConfigureAwait(false);
        metrics.Record(new SmtpPoolMetricEvent(
            SmtpMetricNames.ConnectionsDisposed,
            1,
            connection.Client.EndpointKey));
    }

    private void RecordCurrentState()
    {
        var snapshot = GetSnapshot();
        metrics.Record(new SmtpPoolMetricEvent(SmtpMetricNames.ActiveConnections, snapshot.LeasedConnections));
        metrics.Record(new SmtpPoolMetricEvent(SmtpMetricNames.IdleConnections, snapshot.IdleConnections));
    }

    private bool TrySelectHostForCreation(DateTimeOffset now, out HostRuntimeState? selectedHost)
    {
        for (var offset = 0; offset < hostStates.Length; offset++)
        {
            var index = (nextHostIndex + offset) % hostStates.Length;
            var candidate = hostStates[index];
            if (candidate.NextCreationAllowedAt is { } nextAllowedAt && now < nextAllowedAt)
            {
                continue;
            }

            nextHostIndex = (index + 1) % hostStates.Length;
            selectedHost = candidate;
            return true;
        }

        selectedHost = null;
        return false;
    }

    private void ApplyCooldown(HostRuntimeState hostState, DateTimeOffset nextAllowedAt)
    {
        hostState.NextCreationAllowedAt = nextAllowedAt;
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
        DateTimeOffset? nextCreationAllowedAt = null;
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
    }
}
