namespace MailKit.Pooling.Metrics;

public sealed record SmtpPoolMetricEvent(
    string Name,
    double Value,
    string? EndpointKey = null,
    string? Reason = null);
