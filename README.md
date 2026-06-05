# MailKit.Pooling

`MailKit.Pooling` is a .NET OSS library for guarded SMTP connection reuse on top of [MailKit](https://github.com/jstedfast/MailKit).

It exists to stop application teams from implementing unsafe SMTP client lifecycle code such as:

- creating and disposing a new SMTP client per request
- leaking TIME_WAIT and ephemeral ports under sustained traffic
- reusing broken SMTP sessions after send failures
- reconnecting immediately and repeatedly during server-side outages
- waiting forever when the pool is exhausted
- retrying blindly when the server may already have accepted the message body

This repository intentionally focuses on SMTP connection control. It does not provide template rendering, notification orchestration, durable queuing, or bulk-marketing features.

## Current repository status

This repository currently contains:

- design documents for the MVP
- a working SMTP pool implementation on top of MailKit
- a sender-oriented API with retry, timeout, and classification behavior
- dependency-injection integration
- unit, component, integration, and manual stress/resource validation paths

It does not yet contain every planned operational feature or public observability surface.

## MVP scope

The current MVP provides guarded SMTP pooling with:

- MailKit-based connect, authenticate, send, and keepalive execution
- single-host and multi-host endpoint configuration with priority and weight
- `MinPoolSize`, `MaxPoolSize`, and `AcquireTimeout`
- one-send-per-connection exclusivity
- failed-connection disposal
- reconnect cooldown, including host-level cooldown for multi-host selection
- keepalive health checks
- explicit error classification
- dependency-injection and logging integration
- unit-testable state transitions via abstractions

## Non-goals

The initial package does not aim to provide:

- HTML or template generation
- durable queues or delivery guarantees
- SMTP server hosting
- SES / Graph API / non-SMTP transports
- marketing campaign features

## Planned package shape

- `MailKit.Pooling`
- `MailKit.Pooling.DependencyInjection`

See the design documents under `docs/` for the current boundary, API direction, and open decisions.

For intended usage scenarios, see `docs/design/use-cases.md`.
For guidance on selecting option values, see `docs/design/option-tuning.md`.

## Quick Start

Register the pool once in DI, then send through `ISmtpSender`.

```csharp
using MailKit.Pooling.Abstractions;
using MailKit.Pooling.DependencyInjection;
using MailKit.Pooling.Options;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

var services = new ServiceCollection();

services.AddMailKitPooling(options =>
{
    options.Hosts.Add(new SmtpHostOptions
    {
        Host = "smtp-primary.example.com",
        Port = 587,
        SecureSocketOptions = "StartTls",
        UserName = "smtp-user",
        Password = "smtp-password",
        Priority = 0,
        Weight = 3,
    });

    options.Hosts.Add(new SmtpHostOptions
    {
        Host = "smtp-secondary.example.com",
        Port = 587,
        SecureSocketOptions = "StartTls",
        UserName = "smtp-user",
        Password = "smtp-password",
        Priority = 10,
        Weight = 1,
    });

    options.MinPoolSize = 0;
    options.MaxPoolSize = 8;
    options.AcquireTimeout = TimeSpan.FromSeconds(15);
    options.IdleTimeout = TimeSpan.FromMinutes(2);
    options.KeepAliveInterval = TimeSpan.FromMinutes(1);
    options.ConnectTimeout = TimeSpan.FromSeconds(15);
    options.AuthenticateTimeout = TimeSpan.FromSeconds(15);
    options.SendTimeout = TimeSpan.FromSeconds(30);
    options.ReconnectCooldown = TimeSpan.FromSeconds(30);
    options.MaxRetryAttempts = 1;
    options.RetryBaseDelay = TimeSpan.FromSeconds(2);
});

var provider = services.BuildServiceProvider();
var sender = provider.GetRequiredService<ISmtpSender>();

var message = new MimeMessage();
message.From.Add(MailboxAddress.Parse("from@example.com"));
message.To.Add(MailboxAddress.Parse("to@example.com"));
message.Subject = "Hello";
message.Body = new TextPart("plain") { Text = "Hello from MailKit.Pooling" };

var result = await sender.SendAsync(message);
Console.WriteLine($"Sent via {result.EndpointKey} in {result.Attempts} attempt(s).");
```

If you only have one SMTP endpoint, configuring `options.Host` still works as a compatibility path. New configuration should prefer `options.Hosts`. Lower `Priority` values are preferred first. `Weight` applies within hosts that share the same `Priority`.

## Intended Use Cases

`MailKit.Pooling` is intended for SMTP-based application code that needs safer connection lifecycle control, not for full notification orchestration.

- web APIs that send transactional email during request handling
- background workers or outbox executors that send steady SMTP traffic
- environments where TCP churn, TIME_WAIT, or reconnect storms are operational concerns
- multi-host SMTP relay setups that need application-side priority and weight handling

It is not intended to replace:

- template rendering
- durable delivery workflows
- non-SMTP transport abstraction
- bulk marketing infrastructure

See `docs/design/use-cases.md` for the fuller boundary and decision rule.

## Choosing Option Values

The example values in `Quick Start` are starting points only.

Choose values in this order:

1. set expected concurrent send volume
2. size `MaxPoolSize` and `MinPoolSize`
3. set `AcquireTimeout` from caller-facing wait tolerance
4. set `ConnectTimeout`, `AuthenticateTimeout`, and `SendTimeout` from real SMTP latency
5. set `ReconnectCooldown`, `MaxRetryAttempts`, and `RetryBaseDelay` from outage and retry tolerance
6. set host `Priority` and `Weight` from failover and load-sharing intent

Practical defaults for many transactional systems are:

- `MinPoolSize = 0`
- `MaxPoolSize = 4` to `16`
- `AcquireTimeout = 2` to `15` seconds for API paths
- `IdleTimeout = 1` to `5` minutes
- `KeepAliveInterval = 30` to `120` seconds when idle drops are suspected
- `ReconnectCooldown = 5` to `30` seconds
- `MaxRetryAttempts = 0` or `1`

See `docs/design/option-tuning.md` for per-option decision rules, increase/decrease signals, and multi-host tuning guidance.

## Send Failures

`ISmtpSender.SendAsync()` returns `SmtpSendResult` on success.

On failure, the main exception surface is:

- `SmtpSendFailedException`
  - thrown when SMTP send/connect/authenticate/acquire work failed after library classification
  - inspect `Classification.Kind`, `Classification.Stage`, and `Attempts`
  - `InnerException` keeps the original MailKit, timeout, socket, or protocol exception

- `SmtpPoolExhaustedException`
  - raised internally for bounded acquire timeout
  - when this happens through `ISmtpSender`, it is normally wrapped into `SmtpSendFailedException` with classification `PoolExhausted`

- `OperationCanceledException`
  - returned as-is when the caller's `CancellationToken` is canceled
  - caller cancellation is not wrapped into `SmtpSendFailedException`

Typical handling looks like:

```csharp
try
{
    await sender.SendAsync(message, cancellationToken);
}
catch (SmtpSendFailedException ex) when (ex.Classification.Kind == SmtpFailureKind.PoolExhausted)
{
    // Pool wait exceeded AcquireTimeout.
}
catch (SmtpSendFailedException ex) when (ex.Classification.Kind == SmtpFailureKind.UnknownAfterData)
{
    // Delivery may already be ambiguous. Do not blindly resend.
}
```

## Verification Notes

The repository includes:

- fast unit and component tests
- Docker-backed integration tests using `smtp4dev`
- manual stress/resource tests gated behind `MAILKIT_POOLING_RUN_STRESS=1`
- a dated validation record under `docs/verification/`

The latest checked-in record is `docs/verification/2026-06-05-validation.md`.

That record includes:

- Docker-backed multi-host integration on two real SMTP endpoints
- priority failover verification from `localhost:2525` to `localhost:2526`
- weight distribution verification for a real `3:1` split
- the latest manual stress/resource artifact references

Recent manual stress/resource evidence against `smtp4dev` produced the following sample results on Windows:

- naive per-send MailKit: 40 sends, 40 connection creations, 1145 ms, TIME_WAIT 0 -> 1
- pooled sender: 40 sends, 8 connection creations, 277 ms, TIME_WAIT 1 -> 1
- reconnect suppression scenario: 8 reconnect attempts, 4 suppressed reconnects, 12 outage failures, 6 recovery successes, 4 final successes

These numbers come from the generated JSON artifacts under `tests/MailKit.Pooling.StressTests/bin/Debug/net8.0/StressResults/` and should be treated as environment-specific observations, not universal benchmarks.

Additional Linux validation was also exercised in Docker using the stress test container and the same `smtp4dev` target. In that run, the TIME_WAIT observer source was `ss`, and the sample result was:

- naive per-send MailKit: 40 sends, 40 connection creations, 625 ms, TIME_WAIT 0 -> 0
- pooled sender: 40 sends, 8 connection creations, 231 ms, TIME_WAIT 0 -> 0

Linux reconnect-storm validation was also exercised in Docker with the stress runner container managing `smtp4dev` lifecycle through the Docker socket. In that run, the sample reconnect result was:

- reconnect suppression scenario: 6 reconnect attempts, 15 suppressed reconnects, 12 outage failures, 6 recovery successes, 4 final successes

## Remaining Gaps

The following areas are still incomplete or intentionally limited:

- metrics currently flow through an internal event-style abstraction and `System.Diagnostics.Metrics`, but the stable public metrics surface is not finalized
- TIME_WAIT observation is implemented for Windows, Linux, and macOS in the stress harness; recorded validation currently covers Windows and Linux, while macOS remains unverified
- stress/resource scenarios are manual and are not part of normal fast test execution
- reconnect-storm validation exists, but harsher and longer-running outage patterns have not been broadened yet
- README claims should remain evidence-based; update benchmark-oriented statements only when new measurements are collected on the intended target environment
