using PooledMailKit.Abstractions;

namespace PooledMailKit.Metrics;

internal sealed class NoOpSmtpPoolMetrics : ISmtpPoolMetrics
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
