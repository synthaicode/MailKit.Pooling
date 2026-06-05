# MailKit.Pooling Use Cases

`MailKit.Pooling` is intended for application code that must keep using SMTP,
must keep using MailKit, and needs safer connection lifecycle control than
`new SmtpClient()` per send.

## Primary Use Cases

### 1. Web APIs that send email inside request handling

Use this library when an ASP.NET Core API sends transactional mail during
request processing and would otherwise create and dispose SMTP connections for
every call.

Typical examples:

- account signup verification
- password reset messages
- invoice or receipt delivery
- contact-form relay

Why this library fits:

- reuses SMTP connections instead of creating one per request
- bounds pool wait with `AcquireTimeout`
- avoids reusing clearly broken connections
- makes retry behavior explicit around ambiguous SMTP outcomes

### 2. Background workers that send steady transactional traffic

Use this library when a worker service sends mail continuously or in bursts and
needs predictable connection reuse.

Typical examples:

- outbox processors
- queue consumers
- scheduled billing or statement jobs
- retry workers that still send over SMTP

Why this library fits:

- reduces connect/disconnect churn
- keeps a bounded number of live SMTP connections
- supports keepalive and idle reuse
- suppresses immediate reconnect loops during outages

### 3. Systems with strict SMTP infrastructure constraints

Use this library when SMTP servers, relays, firewalls, or NAT infrastructure
make per-send connection churn operationally expensive.

Typical examples:

- corporate relay environments
- SMTP behind load balancers or appliances
- high-volume applications where TIME_WAIT growth matters
- environments sensitive to ephemeral port exhaustion

Why this library fits:

- minimizes unnecessary TCP churn
- adds explicit cooldown after connection creation failures
- supports validation with resource-oriented stress tests

### 4. Multi-host SMTP failover on the application side

Use this library when the application must choose between multiple SMTP
endpoints without delegating all failover behavior to upstream infrastructure.

Typical examples:

- primary and secondary SMTP relays
- same-priority relay pool with weighted distribution
- staged cutover from one SMTP host to another

Why this library fits:

- supports `Hosts[]`
- prefers lower `Priority` first
- distributes within the same priority by `Weight`
- applies cooldown per host so one bad endpoint does not poison all hosts

## Secondary Use Cases

These are valid, but the library is not the complete solution by itself.

### 1. SMTP behind an application outbox

This library works well as the SMTP execution layer behind a durable outbox,
but it is not the outbox itself.

### 2. SMTP inside internal platform libraries

Platform teams can wrap `ISmtpSender` inside a company-specific mail service,
as long as they preserve the library's failure semantics and do not hide
ambiguous delivery outcomes.

## Non-Use Cases

Do not choose this library when the real problem is outside SMTP connection
control.

### 1. Template rendering or notification orchestration

This package does not manage:

- email templates
- localization
- notification routing
- campaign composition

### 2. Durable delivery guarantees

This package does not provide:

- persistent retry queues
- guaranteed exactly-once delivery
- message deduplication storage
- end-to-end workflow orchestration

`UnknownAfterData` is surfaced precisely because SMTP can become ambiguous.

### 3. Non-SMTP transport abstraction

This package is not a generic email transport layer for:

- Microsoft Graph
- SES APIs
- SendGrid APIs
- webhook-based mail services

### 4. Bulk marketing or mass-mail infrastructure

This package can reduce SMTP connection churn, but it is not a bulk delivery
system with list management, deliverability tooling, or campaign analytics.

## Practical Decision Rule

Choose `MailKit.Pooling` when all of the following are true:

- the application sends mail through SMTP
- MailKit is already acceptable as the protocol client
- connection churn, reconnect behavior, or bounded concurrency are real issues
- the team wants a sender-oriented API without exposing raw SMTP lifecycle to
  application code

Do not choose it as a substitute for:

- an outbox
- a queue
- a notification domain model
- a marketing platform
