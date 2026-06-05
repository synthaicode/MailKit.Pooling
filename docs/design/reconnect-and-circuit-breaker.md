# Reconnect and Circuit Breaker

## MVP policy

The MVP covers single-host reconnect cooldown. Full multi-host circuit breaking remains a later extension.

## Required behavior

- A broken connection is disposed immediately.
- The pool must not recreate replacement connections endlessly with zero delay.
- Reconnect suppression is controlled by `ReconnectCooldown`.
- A future extension may apply exponential backoff and jitter on repeated host-level failures.

## Future multi-host direction

Design the single-host cooldown model so it can later become host-scoped state with:

- per-host cooldown windows
- host selection suppression when a host is unhealthy
- failover to alternate hosts when available

## Open decisions

- whether backoff is part of the first implementation or the second milestone
- whether host suppression is represented as a circuit-breaker abstraction or a host health window abstraction
- whether keepalive failures contribute to host-level suppression immediately or only after a threshold
