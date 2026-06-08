using System.Collections.Concurrent;
using PooledMailKit.Abstractions;
using PooledMailKit.Metrics;

namespace PooledMailKit.Tests.TestDoubles;

internal sealed class RecordingSmtpPoolMetrics : ISmtpPoolMetrics
{
    private readonly ConcurrentQueue<SmtpPoolMetricEvent> events = new();

    public IReadOnlyCollection<SmtpPoolMetricEvent> Events => events.ToArray();

    public void Record(SmtpPoolMetricEvent metricEvent)
    {
        ArgumentNullException.ThrowIfNull(metricEvent);
        events.Enqueue(metricEvent);
    }

    public SmtpPoolMetricEvent? Latest(string metricName, string? smtpHost = null)
    {
        return Events
            .Where(metricEvent =>
                string.Equals(metricEvent.Name, metricName, StringComparison.Ordinal)
                && (smtpHost is null || string.Equals(metricEvent.SmtpHost, smtpHost, StringComparison.Ordinal)))
            .LastOrDefault();
    }

    public int Count(string metricName, string? smtpHost = null)
    {
        return Events.Count(metricEvent =>
            string.Equals(metricEvent.Name, metricName, StringComparison.Ordinal)
            && (smtpHost is null || string.Equals(metricEvent.SmtpHost, smtpHost, StringComparison.Ordinal)));
    }

    public string Dump()
    {
        return string.Join(
            Environment.NewLine,
            Events.Select(metricEvent =>
                $"{metricEvent.Name} host={metricEvent.SmtpHost ?? "<null>"} value={metricEvent.Value} reason={metricEvent.Reason ?? "<null>"}"));
    }
}
