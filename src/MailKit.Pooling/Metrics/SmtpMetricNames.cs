namespace MailKit.Pooling.Metrics;

public static class SmtpMetricNames
{
    public const string ConnectionsCreated = "smtp.connections.created";
    public const string ConnectionsDisposed = "smtp.connections.disposed";
    public const string ConnectionCreateAttempts = "smtp.connections.create_attempts";
    public const string ConnectionCreateFailures = "smtp.connections.create_failures";
    public const string ActiveConnections = "smtp.connections.active";
    public const string IdleConnections = "smtp.connections.idle";
    public const string AcquireTimeouts = "smtp.acquire.timeouts";
    public const string SendSuccesses = "smtp.send.successes";
    public const string SendFailures = "smtp.send.failures";
    public const string Retries = "smtp.send.retries";
    public const string ReconnectSuppressed = "smtp.reconnect.suppressed";
    public const string ErrorClassifications = "smtp.errors.classified";
    public const string KeepAliveFailures = "smtp.keepalive.failures";
}
