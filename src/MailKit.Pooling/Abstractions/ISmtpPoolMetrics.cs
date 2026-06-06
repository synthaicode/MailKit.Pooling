using MailKit.Pooling.Metrics;

namespace MailKit.Pooling.Abstractions;

internal interface ISmtpPoolMetrics
{
    void Record(SmtpPoolMetricEvent metricEvent);
}
