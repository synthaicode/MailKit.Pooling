# Metrics and Logging

## Operational goal

Operators must be able to explain why SMTP sends slowed down, failed, or stopped reusing connections.

## Baseline signals

The implementation should be structured so the following can be observed from the first functional version:

- active leased connection count
- idle connection count
- waiter count
- discarded connection count
- failed connection creation count
- keepalive failure count
- acquire timeout count
- retryable failure count
- permanent failure count
- unknown-after-data count
- cooldown suppression count

## Integration surfaces

- `Microsoft.Extensions.Logging` for event logs
- a metrics abstraction that can later map to `System.Diagnostics.Metrics`, `EventSource`, or callbacks

## Logging expectations

- log pool acquisition wait start and timeout
- log connection creation, health-check failure, and disposal reasons
- log error classification results with send stage
- avoid logging message bodies or credentials

## Open decision

The concrete public metrics surface is not fixed yet. The implementation should avoid hard-coding a single telemetry backend into the pool core.
