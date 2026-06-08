using System.Collections.Concurrent;
using PooledMailKit.Abstractions;
using PooledMailKit.Metrics;

namespace PooledMailKit.StressTests.Helpers;

internal sealed class InMemorySmtpPoolMetrics : ISmtpPoolMetrics
{
    private readonly ConcurrentQueue<SmtpPoolMetricEvent> events = new();

    public IReadOnlyCollection<SmtpPoolMetricEvent> Events => events.ToArray();

    public void Record(SmtpPoolMetricEvent metricEvent)
    {
        ArgumentNullException.ThrowIfNull(metricEvent);
        events.Enqueue(metricEvent);
    }

    public int Count(string metricName)
    {
        return Events.Count(metricEvent => string.Equals(metricEvent.Name, metricName, StringComparison.Ordinal));
    }

    public double Sum(string metricName)
    {
        return Events
            .Where(metricEvent => string.Equals(metricEvent.Name, metricName, StringComparison.Ordinal))
            .Sum(metricEvent => metricEvent.Value);
    }

    public IReadOnlyDictionary<string, int> ClassificationCounts()
    {
        return Events
            .Where(metricEvent => string.Equals(metricEvent.Name, SmtpMetricNames.SendClassificationCount, StringComparison.Ordinal))
            .GroupBy(
                metricEvent => $"{metricEvent.FailureKind ?? "unknown"}:{metricEvent.Stage ?? "unknown"}",
                StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
    }
}
