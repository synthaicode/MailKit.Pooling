# MailKit.Pooling Option Tuning Guide

This document describes how to choose operational values for
`MailKit.Pooling` options.

There is no single correct configuration. The right values depend on:

- expected concurrency
- SMTP server latency
- acceptable end-user wait time
- how expensive reconnects are in the target environment
- whether SMTP endpoints are stable or frequently degraded

The goal is to start with bounded, conservative values and then tune using
observed behavior.

## Tuning Order

Choose values in this order:

1. decide how many concurrent sends the application should allow
2. set pool sizing from that concurrency
3. set timeouts from real SMTP latency expectations
4. set reconnect suppression from outage behavior
5. set retry limits from delivery-risk tolerance
6. tune multi-host priority and weight from topology intent

## Pool Size

### `MaxPoolSize`

What it controls:

- the hard upper bound of simultaneously live SMTP connections

How to choose it:

- start from the maximum number of concurrent sends your application should
  allow
- in many applications, this is equal to worker concurrency or a little lower
- do not start from CPU count; this is primarily an I/O and SMTP-server limit
- do not set it higher than the SMTP relay or upstream infrastructure can
  reasonably accept

Safe starting rule:

- web APIs: start with a small number such as `4` to `16`
- background workers: start with the same number as effective send concurrency
  if that number is already bounded

Increase it when:

- callers frequently hit `PoolExhausted`
- SMTP latency is normal, but too many callers are waiting
- the SMTP server clearly tolerates more parallel sessions

Decrease it when:

- the SMTP server throttles or disconnects under parallel load
- network devices or relays become unstable with many live sessions
- idle connection count remains high for long periods without throughput gain

### `MinPoolSize`

What it controls:

- how many idle connections the pool tries to keep warm

How to choose it:

- start with `0` unless you have a known steady traffic floor
- use a positive value only when the cost of first-send latency matters and
  the application sends mail often enough to justify warmed connections

Safe starting rule:

- bursty or low-volume applications: `0`
- steady worker traffic: `1` to `MaxPoolSize / 2` depending on how constant
  throughput is

Increase it when:

- the first sends after idle periods are too slow because connect/authenticate
  happens too often

Decrease it when:

- idle connections remain open with little traffic
- SMTP servers or relays dislike long-lived unused sessions

## Waiting And Backpressure

### `AcquireTimeout`

What it controls:

- how long a caller waits for a lease when the pool is saturated

How to choose it:

- set it from caller-facing tolerance, not from SMTP latency alone
- for request/response APIs, keep it short enough that callers fail fast
- for background work, it can be longer if queue latency is acceptable

Safe starting rule:

- synchronous API path: `2` to `15` seconds
- background worker: `10` to `30` seconds

Increase it when:

- short bursts cause avoidable `PoolExhausted` failures
- the caller can safely wait longer than it currently does

Decrease it when:

- upstream requests should fail fast rather than pile up
- queue growth or thread retention becomes a bigger problem than occasional
  send failure

## Connection Lifetime

### `IdleTimeout`

What it controls:

- how long an idle connection can sit in the pool before disposal

How to choose it:

- keep it shorter than the period at which the SMTP server, firewall, or load
  balancer tends to silently drop idle sessions
- if you do not know that value, start conservatively

Safe starting rule:

- `1` to `5` minutes is a reasonable initial range

Increase it when:

- connects are expensive and long-lived idle sessions are known to be stable

Decrease it when:

- stale idle connections often fail on reuse
- network devices aggressively drop idle flows

### `KeepAliveInterval`

What it controls:

- how old an idle connection can be before the pool uses `NOOP` health
  validation before reuse

How to choose it:

- set it below the suspected idle-drop threshold
- if the environment is stable, it can be longer than `IdleTimeout` or even
  effectively irrelevant
- if the environment silently kills idle SMTP sessions, keep it shorter

Safe starting rule:

- `30` to `120` seconds in uncertain environments

Increase it when:

- `NOOP` checks add unnecessary round-trips and idle sessions are stable

Decrease it when:

- reused idle connections frequently fail

## Operation Timeouts

### `ConnectTimeout`

What it controls:

- the maximum time allowed for TCP connect and SMTP connect setup

How to choose it:

- base it on normal network RTT plus server responsiveness
- it should be long enough for legitimate network slowness, but short enough to
  avoid hanging during outages

Safe starting rule:

- same-region SMTP: `5` to `15` seconds
- cross-region or high-latency environments: `10` to `30` seconds

Increase it when:

- healthy servers regularly connect just beyond the current threshold

Decrease it when:

- outages cause too many long-hanging connect attempts

### `AuthenticateTimeout`

What it controls:

- the maximum time allowed for SMTP authentication

How to choose it:

- start close to `ConnectTimeout`
- if auth backends are known to be slower than connect, make it slightly larger

### `SmtpSendTimeout`

What it controls:

- the maximum time allowed for the send operation

How to choose it:

- base it on message size and SMTP server responsiveness
- larger attachments need a larger timeout
- tiny transactional messages should not need a very long timeout

Safe starting rule:

- small transactional messages: `15` to `30` seconds
- larger messages or slower links: `30` to `120` seconds

Increase it when:

- valid sends are timing out for large messages

Decrease it when:

- hangs during degraded server behavior last too long

## Failure Suppression And Retry

### `ReconnectCooldown`

What it controls:

- how long a failed host stays suppressed before new connection creation is
  attempted again

How to choose it:

- make it long enough to prevent reconnect loops during outages
- make it short enough that recovery does not lag too far behind real server
  recovery

Safe starting rule:

- `5` to `30` seconds for most applications

Increase it when:

- outage periods create reconnect storms
- infrastructure teams want fewer repeated attempts against broken relays

Decrease it when:

- SMTP endpoints recover quickly and the application stays blocked longer than
  necessary

### `MaxRetryAttempts`

What it controls:

- how many times the library retries only explicitly retryable failures

How to choose it:

- keep it small
- this library is intentionally not a durable retry engine
- prefer `0` or `1` unless there is a strong reason for more

Safe starting rule:

- request/response APIs: `0` or `1`
- background workers: `1` or `2` if duplicate-risk boundaries are understood

Increase it when:

- transient failures are common and quick retry materially helps

Decrease it when:

- caller latency matters more than transient recovery
- upstream systems already have their own retry layer

### `RetryBaseDelay`

What it controls:

- the base wait before retry attempts

How to choose it:

- keep it large enough to avoid immediate hammering
- keep it small enough that a single retry still fits caller latency budgets

Safe starting rule:

- `250 ms` to `2 s`

## Multi-Host Configuration

### `Priority`

What it controls:

- which SMTP host group is preferred first

How to choose it:

- use lower values for primary hosts
- use higher values for standby or disaster-recovery hosts
- use the same value only when hosts are intended to share traffic at the same
  service tier

Typical pattern:

- primary: `0`
- secondary: `10`
- tertiary: `20`

### `Weight`

What it controls:

- relative connection distribution inside the same priority group

How to choose it:

- set weights in proportion to desired connection share
- `3` and `1` means approximately three times as many new connections go to the
  first host as to the second host within the same priority

Use equal weights when:

- hosts should share load evenly

Use unequal weights when:

- one relay is stronger than another
- the team wants a gradual migration from one host to another

## Practical Starting Profile

For a typical transactional application:

```csharp
options.MinPoolSize = 0;
options.MaxPoolSize = 8;
options.AcquireTimeout = TimeSpan.FromSeconds(10);
options.IdleTimeout = TimeSpan.FromMinutes(2);
options.KeepAliveInterval = TimeSpan.FromMinutes(1);
options.ConnectTimeout = TimeSpan.FromSeconds(10);
options.AuthenticateTimeout = TimeSpan.FromSeconds(10);
options.SmtpSendTimeout = TimeSpan.FromSeconds(30);
options.ReconnectCooldown = TimeSpan.FromSeconds(15);
options.MaxRetryAttempts = 1;
options.RetryBaseDelay = TimeSpan.FromSeconds(1);
```

This is a starting point, not a recommendation for every deployment.

## Signals That Tuning Is Needed

Revisit values when you see:

- frequent `PoolExhausted` failures
- many idle connections with little traffic
- repeated stale-connection failures after idle reuse
- long waits during SMTP outages
- reconnect suppression that is either too aggressive or too weak
- retries stretching request latency beyond acceptable limits

## Important Boundary

Do not use option tuning as a substitute for missing architecture.

If the real problem is:

- durable retry
- deduplication
- outbox processing
- bulk delivery scheduling

then the fix belongs outside `MailKit.Pooling`.
