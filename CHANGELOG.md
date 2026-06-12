# Changelog

All notable changes to `PooledMailKit` will be documented in this file.

The format is intentionally simple and release-oriented.

## [Unreleased]

Error-policy alignment release, derived from a source-level error-policy
extraction (inventory, category-by-disposition matrix, contradiction list).

### Changed (breaking)

- Exception types outside the known SMTP failure families (programming
  errors, foreign adapter faults) are no longer wrapped in
  `SmtpSendFailedException` with `ConnectionCorrupted`. The sender still
  discards the connection and records the classification metric, then
  rethrows the original exception unchanged. The new classification value is
  `SmtpFailureKind.Unclassified`. Callers that relied on
  `SmtpSendFailedException` as the single catch surface must add handling
  for raw exceptions, which now indicate bugs rather than SMTP outcomes.
- Configuration validation is unified in one internal validator used by both
  `AddMailKitPooling` and direct `SmtpPool` construction. Range violations
  now consistently throw `ArgumentOutOfRangeException` (previously the DI
  path threw `ArgumentException` for `JitterRatio` and host `Weight`).

### Added

- Eager startup validation for settings that previously failed only at the
  first connection attempt: unparseable `SecureSocketOptions` names,
  `Password` missing while `UserName` is set (use an explicit empty string
  for passwordless authentication), negative `RetryBaseDelay`, and negative
  `MaxRetryAttempts`.
- `pooledmailkit.pool.lease.return_ignored.count` metric: a lease return for
  an unknown connection id is ignored by design (idempotent completion) and
  is now observable.

### Fixed

- The pool availability signal is now an edge-triggered pulse instead of a
  counting semaphore. Returns without waiters no longer accumulate stale
  permits that could wake a later blocked acquirer without a real return
  event (and the `SemaphoreFullException` guard is gone structurally).
- A faulting adapter `DisposeAsync` no longer aborts disposal of the
  remaining pooled connections.

### Tooling

- `CA1031` (catching general exception types) is now enforced as a build
  error; every intentional catch-all/swallow site carries
  `#pragma warning disable CA1031` with its justification on the pragma line.

## [0.1.1] - 2026-06-10

Behavior-correction release. See `docs/release/0.1.1.md` for details.

### Fixed

- Send stage is now inferred from `SmtpCommandException.ErrorCode`, making
  `RetryableTemporaryFailure` reachable and envelope rejections report
  `EnvelopeStarted` instead of `DataStarted`.
- `4xx`/`5xx` replies to the completed `DATA` payload are classified as
  definitive outcomes instead of `UnknownAfterData`, and keep the connection.
- Host reconnect cooldown is applied only on connection creation failures;
  discarded leases (message failures, cancellations, keep-alive failures) no
  longer suppress host connection creation.
- Multi-host failover now happens inside a single acquire instead of relying
  on send-level retries.
- `MinPoolSize` warm refill failures on the acquire/return path no longer fail
  the caller's send; `WarmupAsync` still surfaces them.
- A server-accepted send can no longer be reported as a failure when lease
  cleanup is slow, broken, or raced by cancellation.
- Retry delays use the injected `IClock` instead of `Task.Delay`.
- Disposing the pool wakes blocked acquires safely.
- Removed the unused per-endpoint host dictionary, which rejected duplicate
  `host:port` configurations.
- `JitterRatio` is validated to `[0, 1]`.
- Pool state metrics snapshots are taken under the pool lock.

### Validation

- `dotnet build C:\dev\MailKit.Pooling\MailKit.Pooling.sln -m:1`
- `dotnet test C:\dev\MailKit.Pooling\MailKit.Pooling.sln --no-build`
- manual stress:
  - `MAILKIT_POOLING_RUN_STRESS=1 dotnet test C:\dev\MailKit.Pooling\tests\MailKit.Pooling.StressTests\MailKit.Pooling.StressTests.csproj --no-build`

## [0.1.0] - 2026-06-06

Initial public package release.

### Added

- MailKit-based SMTP connection pooling with bounded acquisition and connection reuse.
- SMTP sender API with classified failures, retry handling, and timeout control.
- Multi-host configuration with priority-based failover and same-priority weight distribution.
- Reconnect cooldown and reconnect suppression behavior to reduce reconnect storms.
- Keep-alive validation, warmup, idle timeout handling, and pool pressure controls.
- Metrics contract and `System.Diagnostics.Metrics`-based instrumentation.
- Docker-backed integration and stress validation with `smtp4dev`.
- GitHub Pages documentation, option tuning guidance, and validation notes.
- Public API baseline files:
  - `PublicAPI.Shipped.txt`
  - `PublicAPI.Unshipped.txt`

### Validation

- `dotnet build C:\dev\MailKit.Pooling\MailKit.Pooling.sln -m:1`
- `dotnet test C:\dev\MailKit.Pooling\MailKit.Pooling.sln --no-build`
- manual stress:
  - `MAILKIT_POOLING_RUN_STRESS=1 dotnet test C:\dev\MailKit.Pooling\tests\MailKit.Pooling.StressTests\MailKit.Pooling.StressTests.csproj --no-build`

### Notes

- Release packaging now includes the package README, overview image, and package icon.
- Build and test should be run serially. Running them in parallel can cause `testhost` DLL lock failures during solution builds.
