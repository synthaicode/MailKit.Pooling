# Connection Pool Policy

## Intent

The pool owns SMTP connection creation, lease assignment, health evaluation, and disposal.

Application code should request a send operation from a higher-level sender API. It should not `new` and `Dispose` `SmtpClient` instances directly.

## Core rules

- The pool predefines `MinPoolSize` and `MaxPoolSize`.
- A lease is exclusive: one connection, one in-flight send.
- If no idle lease is available and `MaxPoolSize` has been reached, the caller waits only until `AcquireTimeout`.
- On timeout, the pool returns a pool-exhaustion error rather than waiting indefinitely.
- Connections that fail send, keepalive, or protocol validation are discarded.
- Discarded connections are not recreated immediately when the host is in cooldown.
- Keepalive uses `NOOP` semantics to validate idle sessions before reuse.

## Proposed state model

Connection-level states:

- `Created`
- `Connecting`
- `Authenticating`
- `Idle`
- `Leased`
- `CheckingHealth`
- `Broken`
- `Disposed`
- `CooldownBlocked`

Pool-level tracked counters:

- active leased connections
- idle connections
- pending waiters
- discarded connections
- failed connection creations
- cooldown rejections

## Warmup policy candidates

Open decision:

- eager warmup on startup
- lazy warmup on first use
- explicit warmup API

The MVP skeleton keeps this unresolved while preserving an abstraction boundary that allows any of the three.
