# Contributing

Thanks for contributing to `MailKit.Pooling`.

This repository is no longer design-only. It contains a working SMTP pooling
implementation, Docker-backed integration tests, manual stress validation, and
NuGet packaging work.

## Contribution Boundary

`MailKit.Pooling` is intentionally focused on SMTP connection control on top of
MailKit.

Good contributions usually improve one or more of these areas:

- connection pooling and lease control
- timeout, retry, keepalive, and reconnect suppression behavior
- multi-host selection and host-level cooldown behavior
- SMTP failure classification
- metrics, diagnostics, and validation coverage
- packaging, documentation, and release automation

Out of scope unless the repository direction changes:

- template rendering
- notification orchestration
- durable queuing or delivery guarantees
- non-SMTP transport abstraction
- marketing/bulk campaign features

## Public API Discipline

The NuGet-facing API is intentionally narrow.

Before making a type or member `public`, confirm that it belongs in the package
surface for long-term compatibility. Most pool, adapter, factory, classifier,
clock, and metrics implementation types should remain `internal`.

When public API changes are intentional:

- update `src/MailKit.Pooling/PublicAPI.Shipped.txt`
- keep `src/MailKit.Pooling/PublicAPI.Unshipped.txt` accurate
- update README and docs if consumer-facing behavior changes

## Design Rules

- Do not expose raw MailKit `SmtpClient` lifecycle management as the main
  consumer API.
- Preserve one-lease-per-connection exclusivity. Do not make a single SMTP
  client concurrently usable.
- Keep retry behavior classification-driven. Do not blindly retry after the
  `DATA` ambiguity boundary.
- Keep host-level cooldown and reconnect suppression explicit and observable.
- Keep tests decoupled from direct MailKit construction where an adapter
  boundary already exists.

## Validation Expectations

Fast validation:

```powershell
dotnet build C:\dev\MailKit.Pooling\MailKit.Pooling.sln -m:1
dotnet test C:\dev\MailKit.Pooling\MailKit.Pooling.sln --no-build
```

Run build and test serially, not in parallel.

Docker-backed integration already runs in the normal test path. Manual
stress/resource validation remains opt-in:

```powershell
$env:MAILKIT_POOLING_RUN_STRESS='1'
dotnet test C:\dev\MailKit.Pooling\tests\MailKit.Pooling.StressTests\MailKit.Pooling.StressTests.csproj --no-build
```

Re-run manual stress when changes affect:

- pooling state transitions
- retry or timeout behavior
- reconnect suppression
- Docker-backed stress/integration orchestration

## Documentation Expectations

Keep these aligned with the code:

- `README.md`
- `site/index.html`
- `docs/operation/metrics-and-logging.md`
- `docs/release/`

Do not claim performance, TIME_WAIT mitigation, or resilience behavior without
recorded evidence. If behavior changes, update the relevant verification notes
or remove stale claims.

## Release-Facing Changes

If a contribution touches package shape, versioning, or publish automation,
also review:

- `docs/release/nuget-publish-checklist.md`
- `docs/release/0.1.0.md`
- `CHANGELOG.md`

## Pull Request Notes

A good change summary should state:

- what behavior changed
- what validation was run
- what was not re-validated
- whether consumer-facing docs were updated
