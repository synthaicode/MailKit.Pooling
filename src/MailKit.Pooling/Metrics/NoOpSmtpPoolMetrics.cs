using MailKit.Pooling.Abstractions;

namespace MailKit.Pooling.Metrics;

public sealed class NoOpSmtpPoolMetrics : ISmtpPoolMetrics
{
    public static NoOpSmtpPoolMetrics Instance { get; } = new();

    private NoOpSmtpPoolMetrics()
    {
    }

    public void Record(SmtpPoolMetricEvent metricEvent)
    {
        ArgumentNullException.ThrowIfNull(metricEvent);
    }
}
