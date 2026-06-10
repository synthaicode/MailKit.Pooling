using System.Diagnostics.Metrics;
using PooledMailKit.Metrics;

namespace PooledMailKit.Tests.Metrics;

public sealed class SystemDiagnosticsSmtpPoolMetricsTests
{
    [Fact]
    public void Metrics_Are_Observable_Through_The_PooledMailKit_Meter()
    {
        using var metrics = new SystemDiagnosticsSmtpPoolMetrics();
        using var listener = new MeterListener();
        var publishedInstruments = new List<(string MeterName, string InstrumentName)>();
        var observedMeasurements = new List<ObservedMeasurement>();

        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (!string.Equals(instrument.Meter.Name, "PooledMailKit", StringComparison.Ordinal))
            {
                return;
            }

            publishedInstruments.Add((instrument.Meter.Name, instrument.Name));
            meterListener.EnableMeasurementEvents(instrument);
        };

        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
        {
            observedMeasurements.Add(new ObservedMeasurement(
                instrument.Meter.Name,
                instrument.Name,
                measurement,
                tags.ToArray()));
        });

        listener.Start();

        metrics.Record(new SmtpPoolMetricEvent(
            SmtpMetricNames.PoolConnectionsCreated,
            SmtpMetricInstrumentKind.Counter,
            1,
            "smtp-a.local:2525"));
        metrics.Record(new SmtpPoolMetricEvent(
            SmtpMetricNames.PoolLeaseDuration,
            SmtpMetricInstrumentKind.Histogram,
            42,
            "smtp-a.local:2525"));
        metrics.Record(new SmtpPoolMetricEvent(
            SmtpMetricNames.PoolHostAvailable,
            SmtpMetricInstrumentKind.Gauge,
            1,
            "smtp-a.local:2525"));

        listener.RecordObservableInstruments();

        Assert.Contains(
            publishedInstruments,
            published => published == ("PooledMailKit", SmtpMetricNames.PoolConnectionsCreated));
        Assert.Contains(
            publishedInstruments,
            published => published == ("PooledMailKit", SmtpMetricNames.PoolLeaseDuration));
        Assert.Contains(
            publishedInstruments,
            published => published == ("PooledMailKit", SmtpMetricNames.PoolHostAvailable));

        Assert.Contains(
            observedMeasurements,
            measurement => measurement.MeterName == "PooledMailKit"
                && measurement.InstrumentName == SmtpMetricNames.PoolConnectionsCreated
                && measurement.Value == 1
                && HasTag(measurement.Tags, "smtp.host", "smtp-a.local:2525"));
        Assert.Contains(
            observedMeasurements,
            measurement => measurement.MeterName == "PooledMailKit"
                && measurement.InstrumentName == SmtpMetricNames.PoolLeaseDuration
                && measurement.Value == 42
                && HasTag(measurement.Tags, "smtp.host", "smtp-a.local:2525"));
        Assert.Contains(
            observedMeasurements,
            measurement => measurement.MeterName == "PooledMailKit"
                && measurement.InstrumentName == SmtpMetricNames.PoolHostAvailable
                && measurement.Value == 1
                && HasTag(measurement.Tags, "smtp.host", "smtp-a.local:2525"));
    }

    private static bool HasTag(KeyValuePair<string, object?>[] tags, string key, string expectedValue)
    {
        return tags.Any(tag =>
            string.Equals(tag.Key, key, StringComparison.Ordinal)
            && string.Equals(tag.Value?.ToString(), expectedValue, StringComparison.Ordinal));
    }

    private sealed record ObservedMeasurement(
        string MeterName,
        string InstrumentName,
        double Value,
        KeyValuePair<string, object?>[] Tags);
}
