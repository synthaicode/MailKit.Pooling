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
    private readonly Dictionary<string, Dictionary<string, double>> gaugeValues = new(StringComparer.Ordinal);

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

            if (!gaugeValues.TryGetValue(metricEvent.Name, out var hostValues))
            {
                hostValues = new Dictionary<string, double>(StringComparer.Ordinal);
                gaugeValues.Add(metricEvent.Name, hostValues);
            }

            hostValues[metricEvent.SmtpHost ?? string.Empty] = metricEvent.Value;
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

        lock (sync)
        {
            if (!gaugeValues.TryGetValue(metricName, out var hostValues))
            {
                return measurements;
            }

            foreach (var pair in hostValues)
            {
                var tags = new TagList();
                if (!string.IsNullOrWhiteSpace(pair.Key))
                {
                    tags.Add("smtp.host", pair.Key);
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

}
