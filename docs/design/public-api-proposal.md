# Initial Public API Proposal

## Consumer-facing direction

The primary API should be sender-oriented, not raw-client-oriented.

Candidate entry points:

- `ISmtpSender`
- `SmtpSendRequest`
- `SmtpSendResult`
- `SmtpPoolOptions`
- `IServiceCollection` registration extensions in `MailKit.Pooling.DependencyInjection`

## Internal abstractions

- `ISmtpClientAdapter`
- `ISmtpConnectionFactory`
- `IClock`
- `ISmtpErrorClassifier`
- `ISmtpPoolMetrics`

## Candidate exceptions / result types

- `SmtpPoolExhaustedException`
- `UnknownAfterDataException`
- `SmtpFailureClassification`

## Design note

The public API should not require callers to manage connection leases manually unless they are implementing advanced pipeline behavior. The default path should expose a guarded send operation and surface ambiguous outcomes explicitly.
