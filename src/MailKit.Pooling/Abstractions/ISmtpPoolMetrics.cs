using MailKit.Pooling.Metrics;

namespace MailKit.Pooling.Abstractions;

public interface ISmtpPoolMetrics
{
    void Record(SmtpPoolMetricEvent metricEvent);
}
