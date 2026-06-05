# Validation Record: 2026-06-05

## Scope

This record captures the latest verification run for:

- multi-host priority failover on real SMTP endpoints
- multi-host weight distribution on real SMTP endpoints
- manual stress/resource validation

## Commands

```powershell
dotnet build C:\dev\MailKit.Pooling\tests\MailKit.Pooling.IntegrationTests\MailKit.Pooling.IntegrationTests.csproj -m:1 --no-restore
dotnet test C:\dev\MailKit.Pooling\tests\MailKit.Pooling.IntegrationTests\MailKit.Pooling.IntegrationTests.csproj --no-build --filter "FullyQualifiedName~Smtp4DevMultiHostTests"

dotnet build C:\dev\MailKit.Pooling\tests\MailKit.Pooling.StressTests\MailKit.Pooling.StressTests.csproj -m:1 --no-restore
$env:MAILKIT_POOLING_RUN_STRESS='1'
dotnet test C:\dev\MailKit.Pooling\tests\MailKit.Pooling.StressTests\MailKit.Pooling.StressTests.csproj --no-build

docker compose -f C:\dev\MailKit.Pooling\docker\compose.smtp.yml down
```

## Integration Cases

Environment:

- Docker `smtp4dev-1`: SMTP `localhost:2525`, API `http://localhost:5080`
- Docker `smtp4dev-2`: SMTP `localhost:2526`, API `http://localhost:5081`

Verified cases:

- `Smtp4DevMultiHostTests.Priority_Fails_Over_To_Secondary_When_Primary_Is_Stopped`
  - primary host: `localhost:2525`, `Priority = 0`, `Weight = 1`
  - secondary host: `localhost:2526`, `Priority = 10`, `Weight = 1`
  - baseline send delivered through primary
  - after stopping `smtp4dev-1`, the next send retried and completed through secondary

- `Smtp4DevMultiHostTests.Equal_Priority_Weights_Distribute_Connections_Across_Real_Smtp_Endpoints`
  - host A: `localhost:2525`, `Priority = 0`, `Weight = 3`
  - host B: `localhost:2526`, `Priority = 0`, `Weight = 1`
  - `MinPoolSize = 4`, `MaxPoolSize = 4`
  - warmup plus lease acquisition produced a `3:1` connection split across real SMTP endpoints

Result:

- filtered integration run: `2 passed`

## Manual Stress Cases

Verified cases:

- `NaiveVsPooledComparisonTests.Compare_Naive_And_Pooled_Smtp_Usage`
- `ReconnectSuppressionStressTests.Suppresses_Reconnect_Storm_And_Recovers_After_Restore`

Result:

- manual stress run: `2 passed`

Artifacts:

- `tests/MailKit.Pooling.StressTests/bin/Debug/net8.0/StressResults/20260605-122115-naive-vs-pooled-latest.json`
- `tests/MailKit.Pooling.StressTests/bin/Debug/net8.0/StressResults/20260605-122137-reconnect-suppression-latest.json`

Observed sample values:

- naive per-send MailKit
  - `40` sends
  - `40` successes
  - `1145 ms`
  - `40` connection creations
  - TIME_WAIT `0 -> 1`

- pooled sender
  - `40` sends
  - `40` successes
  - `277 ms`
  - `8` connection creations
  - idle connections after run: `8`
  - TIME_WAIT `1 -> 1`

- reconnect suppression
  - reconnect attempts: `8`
  - suppressed reconnects: `4`
  - outage failures: `12`
  - recovery successes: `6`
  - final successes: `4`

## Notes

- Stress execution required Docker access beyond the default sandbox.
- TIME_WAIT observation was validated on Windows via `Get-NetTCPConnection`.
- Linux TIME_WAIT observation was also exercised later in Docker with the stress runner container against `smtp4dev-1` on the compose network. That run used `ss` as the observer source and produced `naive: 625 ms, TIME_WAIT 0 -> 0` and `pooled: 231 ms, TIME_WAIT 0 -> 0`.
- Linux reconnect-storm validation was also exercised later in Docker with the stress runner container and Docker socket access. That run produced `reconnect attempts: 6`, `suppressed reconnects: 15`, `outage failures: 12`, `recovery successes: 6`, and `final successes: 4`.
- macOS observer implementation exists in the stress harness but remains unverified.
- These measurements are environment-specific observations, not universal guarantees.
