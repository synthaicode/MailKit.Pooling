using PooledMailKit.Abstractions;

namespace PooledMailKit.Metrics;

// Metrics are optional observers. Their failures must not replace an SMTP
// outcome or interrupt the pool's ownership and cleanup transitions.
internal sealed class SafeSmtpPoolMetrics : ISmtpPoolMetrics
{
    private readonly ISmtpPoolMetrics inner;

    private SafeSmtpPoolMetrics(ISmtpPoolMetrics inner)
    {
        this.inner = inner;
    }

    public static ISmtpPoolMetrics Wrap(ISmtpPoolMetrics? metrics)
    {
        return metrics switch
        {
            null => NoOpSmtpPoolMetrics.Instance,
            SafeSmtpPoolMetrics => metrics,
            NoOpSmtpPoolMetrics => metrics,
            _ => new SafeSmtpPoolMetrics(metrics),
        };
    }

    public void Record(SmtpPoolMetricEvent metricEvent)
    {
        ArgumentNullException.ThrowIfNull(metricEvent);

        try
        {
            inner.Record(metricEvent);
        }
#pragma warning disable CA1031 // Optional observer failures must not change SMTP outcomes, pool accounting, or mandatory cleanup.
        catch
        {
            // Do not report this failure through metrics again: a failing
            // subscriber would recurse into the same observer boundary.
        }
#pragma warning restore CA1031
    }
}
