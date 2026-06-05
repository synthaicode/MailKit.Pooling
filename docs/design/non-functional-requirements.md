# Non-Functional Requirements

## Purpose

The library treats SMTP sending as a resource-sensitive operation. Its main purpose is to centralize connection lifecycle control so application code does not create and destroy `MailKit.Net.Smtp.SmtpClient` instances per send.

## Required qualities

- Resource safety: avoid excessive TCP churn, TIME_WAIT accumulation, and unnecessary ephemeral port consumption.
- Bounded concurrency: never create unbounded connections and never wait forever for a lease.
- Failure containment: never return a known-broken connection to the pool.
- Duplicate-send safety: do not auto-retry requests that may already have crossed the `DATA` boundary.
- Observability: expose enough logging and metrics to understand active, idle, waiting, disposed, and failed connection behavior.
- Testability: core pool state transitions must be unit-testable without a real SMTP server or real MailKit client.
- Operational predictability: reconnect storms must be limited through cooldown and backoff-oriented design.

## Mandatory constraints

- `MaxPoolSize` is mandatory in the effective runtime configuration.
- `AcquireTimeout` is mandatory; infinite waits are rejected.
- A single SMTP connection must not be used concurrently for multiple sends.
- Retry decisions must depend on error classification and send stage.
- `UnknownAfterData` must be surfaced explicitly and must not be auto-retried by default.

## Initial target

- Target framework candidate: .NET 8 or later.
- MVP transport topology: single host.
- Future extension point: multi-host failover without rewriting the pool core.
