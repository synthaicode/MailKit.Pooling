namespace MailKit.Pooling.Metrics;

internal static class SmtpMetricNames
{
    public const string PoolConnectionsActive = "mailkit.pool.connections.active";
    public const string PoolConnectionsIdle = "mailkit.pool.connections.idle";
    public const string PoolHostCooldownActive = "mailkit.pool.host.cooldown.active";
    public const string PoolHostAvailable = "mailkit.pool.host.available";
    public const string PoolAcquireWaitTime = "mailkit.pool.acquire.wait_time";
    public const string PoolAcquireExhaustedCount = "mailkit.pool.acquire.exhausted.count";
    public const string PoolLeaseDuration = "mailkit.pool.lease.duration";
    public const string PoolConnectionsCreated = "mailkit.pool.connections.created";
    public const string PoolConnectionsDropped = "mailkit.pool.connections.dropped";
    public const string PoolConnectionCreateFailures = "mailkit.pool.connection.create.failures";
    public const string PoolKeepAliveFailureCount = "mailkit.pool.keepalive.failure.count";
    public const string PoolReconnectSuppressed = "mailkit.pool.reconnect.suppressed";
    public const string SendDuration = "mailkit.send.duration";
    public const string SendSuccessCount = "mailkit.send.success.count";
    public const string SendFailedCount = "mailkit.send.failed.count";
    public const string SendDefinitelyNotAcceptedCount = "mailkit.send.definitely_not_accepted.count";
    public const string SendAmbiguousCount = "mailkit.send.ambiguous.count";
    public const string SendRetryCount = "mailkit.send.retry.count";
    public const string SendClassificationCount = "mailkit.send.classification.count";
}
