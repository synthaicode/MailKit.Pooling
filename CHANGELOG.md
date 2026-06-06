# Changelog

All notable changes to `MailKit.Pooling` will be documented in this file.

The format is intentionally simple and release-oriented.

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
