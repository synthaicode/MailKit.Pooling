using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using PooledMailKit.Abstractions;

namespace PooledMailKit.Metrics;

internal sealed class SystemDiagnosticsSmtpPoolMetrics : ISmtpPoolMetrics, IDisposable
{
    private readonly Meter meter = new("PooledMailKit");
    private readonly object sync = new();
    private readonly Dictionary<string, Counter<double>> counters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Histogram<double>> histograms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ObservableGauge<double>> gauges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> gaugeValues = new(StringComparer.Ordinal);

    public void Record(SmtpPoolMetricEvent metricEvent)
    {
        ArgumentNullException.ThrowIfNull(metricEvent);

        var tags = BuildTags(metricEvent);
        switch (metricEvent.InstrumentKind)
        {
            case SmtpMetricInstrumentKind.Gauge:
                RecordGauge(metricEvent);
                break;
            case SmtpMetricInstrumentKind.Counter:
                GetCounter(metricEvent.Name).Add(metricEvent.Value, tags);
                break;
            case SmtpMetricInstrumentKind.Histogram:
                GetHistogram(metricEvent.Name).Record(metricEvent.Value, tags);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(metricEvent), metricEvent.InstrumentKind, "Unknown metric instrument kind.");
        }
    }

    public void Dispose()
    {
        meter.Dispose();
    }

    private void RecordGauge(SmtpPoolMetricEvent metricEvent)
    {
        lock (sync)
        {
            if (!gauges.ContainsKey(metricEvent.Name))
            {
                gauges.Add(
                    metricEvent.Name,
                    meter.CreateObservableGauge(
                        metricEvent.Name,
                        () => ObserveGauge(metricEvent.Name),
                        description: string.Create(
                            CultureInfo.InvariantCulture,
                            $"PooledMailKit metric '{metricEvent.Name}'.")));
            }

            gaugeValues[BuildGaugeKey(metricEvent.Name, metricEvent.SmtpHost)] = metricEvent.Value;
        }
    }

    private Counter<double> GetCounter(string metricName)
    {
        lock (sync)
        {
            if (!counters.TryGetValue(metricName, out var counter))
            {
                counter = meter.CreateCounter<double>(
                    metricName,
                    description: string.Create(CultureInfo.InvariantCulture, $"PooledMailKit metric '{metricName}'."));
                counters.Add(metricName, counter);
            }

            return counter;
        }
    }

    private Histogram<double> GetHistogram(string metricName)
    {
        lock (sync)
        {
            if (!histograms.TryGetValue(metricName, out var histogram))
            {
                histogram = meter.CreateHistogram<double>(
                    metricName,
                    description: string.Create(CultureInfo.InvariantCulture, $"PooledMailKit metric '{metricName}'."));
                histograms.Add(metricName, histogram);
            }

            return histogram;
        }
    }

    private IEnumerable<Measurement<double>> ObserveGauge(string metricName)
    {
        List<Measurement<double>> measurements = [];
        var prefix = $"{metricName}|";

        lock (sync)
        {
            foreach (var pair in gaugeValues)
            {
                if (!pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var tags = new TagList();
                var smtpHost = ExtractGaugeHost(pair.Key);
                if (!string.IsNullOrWhiteSpace(smtpHost))
                {
                    tags.Add("smtp.host", smtpHost);
                }

                measurements.Add(new Measurement<double>(pair.Value, tags));
            }
        }

        return measurements;
    }

    private static TagList BuildTags(SmtpPoolMetricEvent metricEvent)
    {
        var tags = new TagList();
        if (!string.IsNullOrWhiteSpace(metricEvent.SmtpHost))
        {
            tags.Add("smtp.host", metricEvent.SmtpHost);
        }

        if (!string.IsNullOrWhiteSpace(metricEvent.Reason))
        {
            tags.Add("reason", metricEvent.Reason);
        }

        if (!string.IsNullOrWhiteSpace(metricEvent.FailureKind))
        {
            tags.Add("failure_kind", metricEvent.FailureKind);
        }

        if (!string.IsNullOrWhiteSpace(metricEvent.Stage))
        {
            tags.Add("stage", metricEvent.Stage);
        }

        return tags;
    }

    private static string BuildGaugeKey(string metricName, string? smtpHost)
    {
        return $"{metricName}|{smtpHost ?? string.Empty}";
    }

    private static string? ExtractGaugeHost(string gaugeKey)
    {
        var separatorIndex = gaugeKey.IndexOf('|', StringComparison.Ordinal);
        if (separatorIndex < 0 || separatorIndex == gaugeKey.Length - 1)
        {
            return null;
        }

        var value = gaugeKey[(separatorIndex + 1)..];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
