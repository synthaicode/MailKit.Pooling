# Changelog

All notable changes to `PooledMailKit` will be documented in this file.

The format is intentionally simple and release-oriented.

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
