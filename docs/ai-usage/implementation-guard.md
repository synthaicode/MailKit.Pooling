# AI Implementation Guard

## Forbidden

- Do not expose raw `MailKit.Net.Smtp.SmtpClient` construction as the primary application API.
- Do not create and dispose a fresh SMTP client per send operation.
- Do not build a pool without `MaxPoolSize`, `AcquireTimeout`, and explicit state transitions.
- Do not auto-retry because an exception merely looks transient.
- Do not treat post-`DATA` ambiguity as a normal retryable case.
- Do not return a failed or broken connection to the reusable pool.
- Do not allow a single connection to perform concurrent sends.
- Do not implement reconnect as immediate unbounded recreation.

## Required

- Keep MailKit behind an adapter boundary.
- Keep the failure classifier independent from the retry implementation.
- Keep time and cooldown logic behind an `IClock`-style abstraction.
- Keep the pool state machine unit-testable without a real SMTP server.
- Keep observability seams present from the first functional implementation.
- Write design docs before deepening the code surface.

## Current guard status

This repository state is intentionally pre-implementation. The next coding phase should only begin after the public API, state transitions, and open decisions are reviewed.
