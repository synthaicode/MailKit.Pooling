# Initial Test Plan

## Unit tests

- pool size never exceeds `MaxPoolSize`
- `AcquireTimeout` produces explicit failure
- released healthy connections are reused
- broken connections are discarded
- cooldown blocks immediate recreation
- single connection is never used concurrently
- `UnknownAfterData` is classified correctly
- permanent failures are not retried
- metrics counters move with state transitions

## Component tests

- fake SMTP adapter emits stage-aware failures
- timeout before `DATA` and after `DATA` classify differently
- keepalive failure marks the connection unusable

## Integration tests

- `smtp4dev` basic send succeeds
- repeated sends reuse connections
- concurrent sends stay within pool bounds
- server stop causes discard and failure classification
- recovery after cooldown works

## Stress tests

- compare naive per-send clients with pooled reuse
- observe TIME_WAIT pressure difference
- validate reconnect storm suppression
- validate waiter and timeout behavior under high concurrency
