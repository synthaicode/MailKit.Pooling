using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using MailKit.Pooling.Abstractions;

namespace MailKit.Pooling.Metrics;

public sealed class SystemDiagnosticsSmtpPoolMetrics : ISmtpPoolMetrics, IDisposable
{
    private readonly Meter meter = new("MailKit.Pooling");
    private readonly Dictionary<string, Histogram<double>> instruments = new(StringComparer.Ordinal);

    public void Record(SmtpPoolMetricEvent metricEvent)
    {
        ArgumentNullException.ThrowIfNull(metricEvent);

        var tags = new TagList();
        if (!string.IsNullOrWhiteSpace(metricEvent.EndpointKey))
        {
            tags.Add("endpoint", metricEvent.EndpointKey);
        }

        if (!string.IsNullOrWhiteSpace(metricEvent.Reason))
        {
            tags.Add("reason", metricEvent.Reason);
        }

        GetInstrument(metricEvent.Name).Record(metricEvent.Value, tags);
    }

    public void Dispose()
    {
        meter.Dispose();
    }

    private Histogram<double> GetInstrument(string metricName)
    {
        lock (instruments)
        {
            if (!instruments.TryGetValue(metricName, out var instrument))
            {
                instrument = meter.CreateHistogram<double>(
                    metricName,
                    description: string.Create(CultureInfo.InvariantCulture, $"MailKit.Pooling metric '{metricName}'."));
                instruments.Add(metricName, instrument);
            }

            return instrument;
        }
    }
}
