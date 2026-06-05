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

## Remaining Gaps

The following areas are still incomplete or intentionally limited:

- metrics currently flow through an internal event-style abstraction and `System.Diagnostics.Metrics`, but the stable public metrics surface is not finalized
- TIME_WAIT observation is Windows-first via `Get-NetTCPConnection`; Linux and macOS observers are not implemented
- stress/resource scenarios are manual and are not part of normal fast test execution
- reconnect-storm validation exists, but harsher and longer-running outage patterns have not been broadened yet
- README claims should remain evidence-based; update benchmark-oriented statements only when new measurements are collected on the intended target environment
