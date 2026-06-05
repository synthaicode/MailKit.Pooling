namespace MailKit.Pooling.Metrics;

public sealed record SmtpPoolMetricEvent(
    string Name,
    SmtpMetricInstrumentKind InstrumentKind,
    double Value,
    string? SmtpHost = null,
    string? Reason = null,
    string? FailureKind = null,
    string? Stage = null);
