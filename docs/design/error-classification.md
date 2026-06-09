# Error Classification

## Why this exists

Retry behavior is unsafe unless the library knows both the failure category and the SMTP send stage at the time of failure.

## Proposed failure kinds

- `RetryableBeforeSend`
- `RetryableTemporaryFailure`
- `PermanentFailure`
- `ConnectionCorrupted`
- `UnknownAfterData`
- `PoolExhausted`

## Stage model

- `BeforeConnect`
- `Connected`
- `Authenticated`
- `EnvelopeStarted`
- `DataStarted`
- `DataCompleted`
- `ServerAccepted`

## Core rules

- SMTP `5xx` is non-retryable by default.
- Authentication failure is non-retryable.
- Recipient rejection is non-retryable.
- SMTP `4xx` is retryable only within an explicit attempt budget.
- Timeouts or disconnects after `DataStarted` are classified as `UnknownAfterData` unless the protocol outcome is known.
- Protocol corruption or broken transport invalidates the connection for reuse.

## MVP design consequence

Retry policy must be implemented after this classifier contract is fixed. The repository skeleton therefore exposes the failure model first and leaves retry orchestration for a later implementation step.
