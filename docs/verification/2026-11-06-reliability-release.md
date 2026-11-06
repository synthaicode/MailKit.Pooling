# Reliability release verification — 2026-11-06

Target: PooledMailKit 0.2.2, based on source bddc93f0c069e223e4fac20ed006fc5fb3167b89.
Both net8.0 and net10.0 are retained. This records bounded observations, not an
assertion of universal reliability or production throughput.

## Scope and finding disposition

| Finding | Corrected behavior | Verification basis |
| --- | --- | --- |
| P-001 | Shutdown rejects and disposes late creation | Pool regression and real SMTP profiler shutdown scenario |
| P-002 | One acquisition budget covers refill/create/keepalive; late leases rejected | Fake-clock, delayed-timer, cancellation, demand/refill tests and profiler acquisition scenario |
| P-003 | Cooldown begins at observed failure time | Slow warmup failure regression |
| P-004 | Warmup publication wakes availability waiters | Controlled clock/wait regression |
| S-01 / S-02 | Metrics faults preserve accepted result/original failure and cleanup | Actual MeterListener fault regressions and accepted/failure profiler scenarios |
| SP-01 | Creation metric exception cannot corrupt reservation accounting | Capacity-one accounting regression and creation-metric profiler scenario |
| SP-02 / SP-03 | Disposal/keepalive metrics faults cannot interrupt cleanup | Multiple-client disposal and keepalive fault regressions |
| S-03 / S-04 | Reject non-finite jitter and undefined socket enum values | Boundary regressions |
| SIM-S-01 | Validate/clamp supported timer bounds | Operation timeout and retry boundary regressions |
| SIM-S-02 | Host-weight intermediates use wide arithmetic | Large equal-weight selection regression |
| S-05 | Retain targets and disclose .NET 8 support deadline | Deployment handoff; actual consumer runtime servicing remains unverified |

## Additional failure discovered during release testing

The first enabled manual stress run passed net8.0 (9 tests) but failed net10.0
primary-outage failover: secondary accepted 0 messages, with 17 primary failures
and 4 recovery successes. The overall acquisition deadline could cancel a pending
connection before its connection timeout; treating that internal cancellation
as caller cancellation omitted host cooldown, repeatedly selecting the failed
primary. This observation is retained rather than replaced by the later result.

The fix records per-acquisition original caller cancellation, active creation
ownership and exactly-once failure attribution under the pool lock. Budget expiry
penalizes the active connection attempt before cancellation is dispatched;
caller cancellation and capacity-only waits do not penalize hosts. Seven added
deterministic cases cover demand/refill, caller cancellation, queue expiry and
per-attempt reset. The unchanged targeted net10.0 stress test then passed:
69 secondary acceptances during the outage, 2 primary failures and 4 recovery
successes. This does not establish all possible scheduler interleavings.

## Executed local verification

- Release solution build: successful, 0 warnings / 0 errors.
- Unit tests: 121 passed per target, including 18 pool reliability cases.
- Targeted previously failing net10.0 partial-outage stress: passed.
- Dependency audit including transitive dependencies: no vulnerable package
  entries reported by NuGet for the updated graph. This is not a formal
  security review or exploitability assessment.

Final full-suite, rebuilt profiler and package/publication results are recorded below.

## Evidence boundaries

Profiler intervention uses windows-chaos-agent source e97b80f7be263ae9e16143ce3f28ed5a0a9e610a,
running its CLR profiler in a controlled .NET 8 Linux container against rebuilt
DLL/PDB call sites. Plans are regenerated from source and matching PDB, with
mismatched-target and zero-injection controls. Runtime acceptance, rewriting and
firing are checked separately. This does not verify standalone injection into
arbitrary Windows services or every framework/platform combination.

Arbitrary synchronous work cannot be forcibly interrupted by these async
budgets: the controlled synchronous profiler delay may exceed elapsed timeout,
but late successful acquisition must be rejected and cleaned up. Metrics
isolation handles exceptions, not a synchronously blocked subscriber. Dynamic
mutation of options and internet delivery are outside this verification scope.

## Final full-suite and local package results

Command: `dotnet test MailKit.Pooling.sln -c Release --no-build -m:1
-p:TestTfmsInParallel=false`, with `MAILKIT_POOLING_RUN_STRESS=1` for that child
process. All tests passed, with no skips: unit 121, component 10, integration 8
and stress 9 **per target**, 296 total. The earlier targeted partial-outage
retest is additional to this full-suite count.

Explicit analyzer builds emitted SARIF 2.1 for each target. Each contains eight
CA1031 diagnostics suppressed in source at documented best-effort cleanup and
optional-observer boundaries, plus three informational notes: CA1068 (private
cancellation argument ordering), CA1859 (existing concrete-return optimization)
and CA1513 (throw helper preference). There are no unsuppressed warning/error
results; build output reports zero warnings/errors. This baseline does not
establish behavioral correctness. The staged XRefKit diff gate was clean for
all 18 changed source, test and documentation files.

Local `dotnet pack --no-build --no-restore` produced both nupkg and snupkg
without warnings. Archive inspection verified exactly one DLL and Portable PDB
for each lib/net8.0 and lib/net10.0 pair in the **regular nupkg**, comparing
CodeView GUID/age/stamp against the portable PDB identity. Each PDB has 41 source
documents. The snupkg independently contains both PDBs. Nuspec version is 0.2.2,
with the approved per-framework PKCS dependency groups and README/license/icon.

A separate consumer referencing only the local package restored into a fresh
private package cache, resolved the public DI/sender API, sent through smtp4dev
and observed its unique message through the SMTP server API on .NET 8.0.33 and
.NET 10.0.14. It did not use a project reference or internal pool types. This
confirms controlled local acceptance, not internet delivery.

## Final rebuilt profiler retest

All 15 cases passed after the final host-failover fix: five scenarios each with
a mismatched-target control, zero-injection control and one active injection.
Scenarios were delayed creation across shutdown, delayed acquisition, metrics
failure after accepted DATA, metrics failure while handling a rejected send,
and creation-metrics failure under a capacity-one accounting constraint.
Controls fired zero times; active interventions fired once. Plans were accepted
and runtime IL rewriting was observed; original DLL and PDB files were unchanged.
The local tested net8.0 DLL SHA-256 was
`ae321dd8860fa43fd7abaef0f7e66c4e95c89846dab7213691f72b53befab6b8`;
its matching PDB SHA-256 was
`60747d92d1e532731d32cc92ce5f097d93f18f6380fb94c57a50a2b34985a862`.
These identify this local verification build, not the later GitHub publication
build whose repository commit and packaged DLL/PDB identities must be checked
separately.

## Release CI correction

The first tag-triggered publication run 42177573637 stopped at Test, before Pack
or Push. Both targets passed unit/component tests but failed the existing SMTP
recovery integration assertion with PoolExhausted. That test allowed only 100 ms
for whole acquisition while allowing 200 ms for connection setup, and checked
only the HTTP API after restarting smtp4dev. The exact contribution of host
latency, readiness and concurrent execution is not established by that log.

The test now uses AcquireTimeout 2 s, ConnectTimeout 1 s and reconnect cooldown
4 s, preserving ConnectTimeout < AcquireTimeout < cooldown. Its original
cooldown-exhaustion classification and single-attempt recovery assertions are
retained. An independent bounded SMTP greeting/EHLO + NOOP probe checks readiness
before initial/recovered sends without sending messages or mutating pool options.
The workflow runs project/framework tests serially, matching the approved local
verification plan. No production code or strict deadline regression was changed.
The failed tag is retained; corrected main is published via the workflow's
explicit version input, and the public package repository commit is verified.

The corrected integration project rebuilt with zero warnings/errors and passed
all eight integration tests per target, with no skips (16 total). Product source
is byte-for-byte unchanged from the already reviewed reliability commit.
