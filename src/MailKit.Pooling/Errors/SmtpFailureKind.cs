namespace MailKit.Pooling.Errors;

public enum SmtpFailureKind
{
    RetryableBeforeSend,
    RetryableTemporaryFailure,
    PermanentFailure,
    ConnectionCorrupted,
    UnknownAfterData,
    PoolExhausted,
    HostUnavailable,
}
