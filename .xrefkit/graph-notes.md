# Structure Graph Profile — MailKit.Pooling

This `.xrefkit/` directory persists the **requirement-independent** parts of the
structure-graph backstop for this repository: the relation/classification layer
that XDDP's *Where* step (spec-out → traceability matrix) traverses.

Design reference: XRefKit `knowledge/source_analysis/160_structure_graph_tm_backstop.md`
(xid `163AD9936979`).

## Persisted here (requirement-independent)

| File | Holds |
|------|-------|
| `graph-profile.json` | identity, summary, hub classification, name-coupling classification, external-boundary definition |
| `graph-rules.yml` | traversal / pruning rules |
| `graph-baseline.json` | accepted baseline for drift detection |
| `graph-notes.md` | this file |

## NOT persisted here (per-change artifacts)

- change-requirement traceability matrix (TM)
- impacted-boundary list
- per-PR traversal results
- transient seeds

## Profile summary

Strongly-typed (Options pattern), multi-target (net8.0 + net10.0; DocID collapses
the duplication). The change centre is `SmtpPool` — ctor fan-in **57** (single-
point concentration) and `AcquireLeaseAsync` the core hot path (fan-in 31 /
fan-out 20). Name coupling is **low** and not required for the TM: shared
literals are test hosts / env values, already type-bound through `SmtpPoolOptions`.

The test doubles (`FakeClock`, `FakeSmtpClientAdapter`, `FakeSmtpConnectionFactory`,
fan-in 44 each) are high-degree but are **transit fixtures** — pruned during
traversal so they do not flood the TM (design principle 5).

## Key scheme

Nodes keyed by Roslyn documentation-comment id (DocID). Deterministic, stable
across body-only edits; collapses multi-target duplication automatically.
