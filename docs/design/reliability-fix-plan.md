# Reliability fixes: approved review feedback

The user authorized fixing the source-review findings and publishing PooledMailKit.
This change realizes the existing public acquisition, pooling, retry and outcome
contracts; it adds no public API or dynamic-configuration contract.

| Findings | Required invariant and scoped change | Regression evidence |
|---|---|---|
| P-001 | A completed creation must not publish into a disposed pool; an unadopted client retains an explicit cleanup owner | TCS-gated warmup/demand creation across shutdown |
| P-002 | Acquisition deadline begins before refill and covers async creation/health waits; late results must not be returned as a successful lease and are cleaned up | Fake-clock/controlled async wait and late-success tests; caller cancellation remains distinct |
| P-003/P-004 | Failure cooldown starts at observed failure; publishing warm idle capacity wakes existing waiters | Slow failure controlled clock and explicit publication signal tests |
| S-01/S-02/SP-01/SP-02/SP-03 | Optional metrics exceptions cannot redefine accepted SMTP results, skip mandatory cleanup, or re-run accounting updates | Actual throwing MeterListener controls for success/failure/creation/shutdown/expiry/keepalive |
| S-03/S-04/SIM-S-01 | Configuration accepted at startup must be finite/defined and within the actual timer API range; computed retry delay must remain representable | NaN/undefined enum and max-valid/first-invalid boundary tests |
| SIM-S-02 | Positive host weights retain their ratio without overflowing intermediate sums | Equal large weights and standard weights compared |

Cancellation/deadline ownership must account for asynchronous operations completing
after the caller has timed out. No new per-acquire unbounded Task.Run is introduced,
and arbitrary synchronous external code cannot be forcibly interrupted by these
async deadline helpers. Record that execution boundary without claiming a strict
wall-clock preemption guarantee for synchronous third-party callbacks.

Existing post-DATA ambiguous outcomes remain non-retryable. Accepted sends remain
accepted despite cleanup/measurement faults. Failed connections are never returned
for reuse. Pending counts are settled exactly once and real live capacity stays
within MaxPoolSize, including delayed creation/disposal.

Dynamic mutation of public options remains outside this patch: ownership and
reconfiguration semantics were not established. Retain net8/net10 targets; record
net8 support-end proximity rather than changing consumer deployment policy.
Dependency advisories are separately evaluated from functional fixes.

Run build and test serially to avoid shared testhost locks. Verify unit/component,
Docker SMTP integration and required manual stress scenarios; rerun representative
windows-chaos-agent plans against source/PDB-matched rebuilt binaries and compare
controls. Pack nupkg/snupkg, validate clean consumer use and publish only the tested
main commit through the existing NuGet workflow.

The release stress run exposed a P-002 interaction with failover: an acquisition
budget cancelling an active connect must record that host's failure/cooldown
exactly once before returning; genuine caller cancellation and capacity-only
expiration do not penalize any host. The correction and seven deterministic
controls remain inside the existing failure/cooldown contract. Preserve the
unchanged manual partial-outage test as independent runtime confirmation.

The user additionally requires Portable PDBs in the regular nupkg for both
frameworks. Validate each packaged PDB against the corresponding DLL debug
identity, in addition to producing a separate snupkg.

Publication CI exposed an existing real-SMTP recovery test whose 100ms whole
acquisition budget was shorter than its 200ms connection timeout and whose
readiness check covered only HTTP. Correct the harness budget relationship to
Connect1s < Acquire2s < Cooldown4s, add bounded15s independent SMTP/EHLO/NOOP
readiness with disposal/no send, preserve all recovery/cooldown assertions, and
serialize workflow tests as originally planned. Product logic is unchanged;
retain failed-run evidence and prove corrected CI before public push.
