using PooledMailKit.Metrics;

namespace PooledMailKit.Abstractions;

internal interface ISmtpPoolMetrics
{
    void Record(SmtpPoolMetricEvent metricEvent);
}
