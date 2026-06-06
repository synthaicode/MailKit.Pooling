# Public API Direction

## Consumer-facing direction

The primary API should be sender-oriented, not raw-client-oriented.

Current intended entry points:

- `ISmtpSender`
- `SmtpSendResult`
- `SmtpPoolOptions`
- `SmtpHostOptions`
- `SmtpSendFailedException`
- `SmtpFailureClassification`
- `SmtpFailureKind`
- `SmtpSendStage`
- `IServiceCollection` registration extensions in `MailKit.Pooling.DependencyInjection`

## Internal abstractions

- `ISmtpClientAdapter`
- `ISmtpConnectionFactory`
- `IClock`
- `ISmtpErrorClassifier`
- `ISmtpPoolMetrics`
- `SmtpPool`
- `SmtpConnectionLease`
- MailKit-specific factories, adapters, and parsers
- metrics implementation details

## Candidate exceptions / result types

- `SmtpFailureClassification`
- `SmtpSendFailedException`

## Design note

The public API should not require callers to manage connection leases manually unless they are implementing advanced pipeline behavior. The default path should expose a guarded send operation and surface ambiguous outcomes explicitly.
