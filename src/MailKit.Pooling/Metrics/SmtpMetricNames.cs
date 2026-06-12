namespace PooledMailKit.Metrics;

internal static class SmtpMetricNames
{
    public const string PoolConnectionsActive = "pooledmailkit.pool.connections.active";
    public const string PoolConnectionsIdle = "pooledmailkit.pool.connections.idle";
    public const string PoolHostCooldownActive = "pooledmailkit.pool.host.cooldown.active";
    public const string PoolHostAvailable = "pooledmailkit.pool.host.available";
    public const string PoolAcquireWaitTime = "pooledmailkit.pool.acquire.wait_time";
    public const string PoolAcquireExhaustedCount = "pooledmailkit.pool.acquire.exhausted.count";
    public const string PoolLeaseDuration = "pooledmailkit.pool.lease.duration";
    public const string PoolLeaseReturnIgnoredCount = "pooledmailkit.pool.lease.return_ignored.count";
    public const string PoolConnectionsCreated = "pooledmailkit.pool.connections.created";
    public const string PoolConnectionsDropped = "pooledmailkit.pool.connections.dropped";
    public const string PoolConnectionCreateFailures = "pooledmailkit.pool.connection.create.failures";
    public const string PoolKeepAliveFailureCount = "pooledmailkit.pool.keepalive.failure.count";
    public const string PoolReconnectSuppressed = "pooledmailkit.pool.reconnect.suppressed";
    public const string SendDuration = "pooledmailkit.send.duration";
    public const string SendSuccessCount = "pooledmailkit.send.success.count";
    public const string SendFailedCount = "pooledmailkit.send.failed.count";
    public const string SendDefinitelyNotAcceptedCount = "pooledmailkit.send.definitely_not_accepted.count";
    public const string SendAmbiguousCount = "pooledmailkit.send.ambiguous.count";
    public const string SendRetryCount = "pooledmailkit.send.retry.count";
    public const string SendClassificationCount = "pooledmailkit.send.classification.count";
}
