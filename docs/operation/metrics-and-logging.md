# Metrics and Logging

This document defines the intended operational telemetry surface for
`MailKit.Pooling`.

The purpose of the metrics surface is to let operators answer these questions:

- is the pool saturated or oversized?
- are connections being recreated too often?
- is a specific SMTP host degraded?
- are sends failing before `DATA` or becoming ambiguous after `DATA`?
- is reconnect suppression actually preventing reconnect storms?

## Design Principles

### 1. Distinguish current state from cumulative events

Use:

- gauges for current pool state
- counters for cumulative events
- histograms for latency and wait distributions

### 2. Keep host-level visibility

In multi-host configurations, operators must be able to tell which SMTP host is
causing trouble. Host tagging is therefore part of the intended design.

### 3. Keep tags low-cardinality

Tags should describe infrastructure and failure categories, not message-level
data.

Do not tag metrics with:

- recipient address
- message id
- subject
- template id if it creates high cardinality

### 4. Treat ambiguity as first-class

`UnknownAfterData` is operationally important enough to expose separately, not
only as a general send failure.

## Proposed Metrics Contract

### Pool State

#### `mailkit.pool.connections.active`

- instrument: `gauge`
- meaning: number of currently leased SMTP connections
- interpretation:
  - spikes indicate real send pressure
  - a sustained plateau at `MaxPoolSize` indicates saturation pressure

#### `mailkit.pool.connections.idle`

- instrument: `gauge`
- meaning: number of currently idle pooled connections
- interpretation:
  - `0` during sustained traffic may indicate an undersized pool
  - a consistently high value may indicate over-allocation

#### `mailkit.pool.acquire.wait_time`

- instrument: `histogram`
- unit: time
- meaning: time spent waiting to acquire a connection lease
- interpretation:
  - rising percentiles indicate pool pressure before outright exhaustion

#### `mailkit.pool.acquire.exhausted.count`

- instrument: `counter`
- meaning: number of pool-exhaustion outcomes
- interpretation:
  - non-zero values indicate callers timed out waiting for a lease

### Connection Lifecycle

#### `mailkit.pool.connections.created`

- instrument: `counter`
- meaning: cumulative number of SMTP connections created
- interpretation:
  - unusually high values relative to send volume suggest churn or repeated reconnects

#### `mailkit.pool.connections.dropped`

- instrument: `counter`
- meaning: cumulative number of pooled connections discarded
- tags:
  - `reason`
- suggested reasons:
  - `broken`
  - `idle_timeout`
  - `auth_failure`
  - `keepalive_failure`
  - `shutdown`
- interpretation:
  - high `broken` or `keepalive_failure` counts indicate unstable sessions

#### `mailkit.pool.connection.create.failures`

- instrument: `counter`
- meaning: cumulative number of connection creation failures
- interpretation:
  - useful for host outage detection and repeated connect failures

#### `mailkit.pool.reconnect.suppressed`

- instrument: `counter`
- meaning: number of reconnect attempts suppressed because the host was in cooldown
- interpretation:
  - directly indicates reconnect storm suppression activity

### Send Path

#### `mailkit.send.duration`

- instrument: `histogram`
- unit: time
- meaning: total send operation duration
- interpretation:
  - long tails can indicate SMTP server slowness or infrastructure degradation

#### `mailkit.send.success.count`

- instrument: `counter`
- meaning: cumulative number of successful sends

#### `mailkit.send.failed.count`

- instrument: `counter`
- meaning: cumulative number of failed sends
- tags:
  - `failure_kind`
  - `stage`
- interpretation:
  - lets operators distinguish `PoolExhausted`, `AuthFailure`,
    `RetryableBeforeSend`, `UnknownAfterData`, and similar categories

#### `mailkit.send.ambiguous.count`

- instrument: `counter`
- meaning: number of post-`DATA` ambiguous outcomes
- interpretation:
  - this is operationally important for duplicate-send risk analysis

#### `mailkit.send.retry.count`

- instrument: `counter`
- meaning: cumulative number of retries attempted by the library

#### `mailkit.send.classification.count`

- instrument: `counter`
- meaning: cumulative number of classified failures
- tags:
  - `failure_kind`
  - `stage`
- interpretation:
  - allows a category breakdown even when callers only look at aggregate send failure counts

## Tagging Rules

### Required tag design

The intended tag set is:

- `smtp.host`
- `reason`
- `failure_kind`
- `stage`

### `smtp.host`

Attach `smtp.host` to all host-specific metrics so that multi-host failures can
be isolated later.

Use the logical SMTP endpoint identity, not message-level metadata.

### `reason`

Use for connection disposal or similar lifecycle events.

Keep the vocabulary fixed and documented.

### `failure_kind`

Map directly from `SmtpFailureKind`.

### `stage`

Map from the send stage used during classification.

## Logging Expectations

Metrics are not enough on their own. Structured logs should also exist for:

- pool acquisition timeout
- connection creation failure
- connection disposal reason
- host cooldown entry
- failure classification result
- ambiguous post-`DATA` outcomes

Do not log:

- message bodies
- credentials
- high-cardinality recipient data by default

## Current Implementation Gap

The current code already emits internal metric events, but the stable public
metrics contract is not fully aligned yet.

In particular, the implementation still needs to move toward:

- gauges for current state instead of treating every metric alike
- explicit histograms for wait and duration
- stable public metric names under the `mailkit.*` prefix
- consistent `smtp.host` tagging

This document is the intended contract to implement toward.
