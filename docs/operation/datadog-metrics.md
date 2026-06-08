# MailKit.Pooling Metrics with Datadog

This document describes how to collect `MailKit.Pooling` metrics in Datadog.

`MailKit.Pooling` emits metrics through `System.Diagnostics.Metrics`.
For Datadog, the usual path is:

1. enable `MailKit.Pooling` metrics
2. register `Meter("MailKit.Pooling")` in OpenTelemetry
3. export OTLP metrics to the Datadog Agent or Datadog OTLP intake

## What MailKit.Pooling Emits

The library emits metrics under the `mailkit.*` contract, including:

- `mailkit.pool.connections.active`
- `mailkit.pool.connections.idle`
- `mailkit.pool.host.cooldown.active`
- `mailkit.pool.host.available`
- `mailkit.pool.acquire.wait_time`
- `mailkit.pool.lease.duration`
- `mailkit.pool.connections.created`
- `mailkit.pool.connections.dropped`
- `mailkit.pool.keepalive.failure.count`
- `mailkit.send.duration`
- `mailkit.send.failed.count`
- `mailkit.send.definitely_not_accepted.count`
- `mailkit.send.ambiguous.count`
- `mailkit.send.classification.count`

For the full metric contract, see `metrics-and-logging.md`.

## MailKit.Pooling Configuration

Metrics are enabled by default, but the setting can be made explicit:

```csharp
using MailKit.Pooling.DependencyInjection;
using MailKit.Pooling.Options;

builder.Services.AddMailKitPooling(options =>
{
    options.Host = new SmtpHostOptions
    {
        Host = "smtp.example.com",
        Port = 587,
        SecureSocketOptions = "StartTls",
        UserName = "smtp-user",
        Password = "smtp-password",
    };

    options.EnableMetrics = true;
});
```

## OpenTelemetry Registration

Datadog collection does not require direct access to internal `MailKit.Pooling`
metrics types.

The application only needs to subscribe to the `MailKit.Pooling` meter:

```csharp
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

builder.Services
    .AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("mail-service"))
    .WithMetrics(metrics =>
    {
        metrics
            .AddMeter("MailKit.Pooling")
            .AddAspNetCoreInstrumentation()
            .AddRuntimeInstrumentation();
    });
```

Important:

- meter name: `MailKit.Pooling`
- metric names: `mailkit.*`

## Datadog via OTLP to the Datadog Agent

One common setup is OTLP export to a local Datadog Agent:

```csharp
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

builder.Services
    .AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("mail-service"))
    .WithMetrics(metrics =>
    {
        metrics
            .AddMeter("MailKit.Pooling")
            .AddAspNetCoreInstrumentation()
            .AddRuntimeInstrumentation()
            .AddOtlpExporter(options =>
            {
                options.Endpoint = new Uri("http://localhost:4318/v1/metrics");
                options.Protocol = OtlpExportProtocol.HttpProtobuf;
            });
    });
```

This example assumes the Datadog Agent is configured to receive OTLP metrics.

## Datadog via OTLP Intake

If the environment uses Datadog OTLP intake directly instead of a local Agent,
the OTLP exporter endpoint and authentication are configured on the exporter
side rather than in `MailKit.Pooling`.

The application-side `MailKit.Pooling` setup does not change:

- keep `EnableMetrics = true`
- keep `.AddMeter("MailKit.Pooling")`
- change only the OTLP exporter destination

## Metric Semantics to Watch

Datadog dashboards and monitors are typically most useful when grouped around
these questions:

### Pool pressure

- `mailkit.pool.connections.active`
- `mailkit.pool.connections.idle`
- `mailkit.pool.acquire.wait_time`
- `mailkit.pool.acquire.exhausted.count`

### Connection churn

- `mailkit.pool.connections.created`
- `mailkit.pool.connections.dropped`
- `mailkit.pool.keepalive.failure.count`

### Host degradation

- `mailkit.pool.host.cooldown.active`
- `mailkit.pool.host.available`
- `mailkit.pool.reconnect.suppressed`

### Delivery outcomes

- `mailkit.send.failed.count`
- `mailkit.send.definitely_not_accepted.count`
- `mailkit.send.ambiguous.count`
- `mailkit.send.classification.count`

## Tags

The intended low-cardinality tags are:

- `smtp.host`
- `reason`
- `failure_kind`
- `stage`

These tags are suitable for Datadog facets, grouping, and alert filters.

Do not add high-cardinality message-level tags such as:

- recipient address
- subject
- message id

## Datadog Cost Note

These metrics may count as Datadog custom metrics depending on the collection
path and product configuration.

Review Datadog custom-metric governance before enabling broad production
dashboards or per-host alerting at scale.

## OTLP Type Mapping Note

OpenTelemetry metric instruments do not always appear in Datadog with a
one-to-one type mapping.

In particular:

- counters are generally ingested as count-style metrics
- gauges are ingested as gauge-style metrics
- histograms may be mapped into Datadog distribution-oriented output

Check the Datadog OpenTelemetry metric type mapping when designing alert logic
around percentiles or histogram-derived views.

## Recommended Starting Dashboard

A practical first Datadog dashboard for `MailKit.Pooling` is:

1. active vs idle connections
2. acquire wait p95
3. connections created and dropped
4. reconnect suppressed
5. send failed count
6. definitely-not-accepted count
7. ambiguous count
8. classification count by `failure_kind`

## References

- Datadog OpenTelemetry overview:
  `https://docs.datadoghq.com/opentelemetry/`
- Datadog OpenTelemetry metrics:
  `https://docs.datadoghq.com/metrics/open_telemetry/`
- Datadog OTLP metrics intake:
  `https://docs.datadoghq.com/opentelemetry/setup/otlp_ingest/metrics/`
- Datadog OTLP metric type mapping:
  `https://docs.datadoghq.com/opentelemetry/reference/otlp_metric_types/`
- Datadog custom metrics:
  `https://docs.datadoghq.com/metrics/custom_metrics/`
