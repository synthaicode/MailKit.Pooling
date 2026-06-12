namespace PooledMailKit.Errors;

/// <summary>
/// Classifies send failures by retry and ambiguity behavior.
/// </summary>
public enum SmtpFailureKind
{
    /// <summary>
    /// The failure happened before message delivery became ambiguous and may be retried.
    /// </summary>
    RetryableBeforeSend,

    /// <summary>
    /// The server reported a temporary failure that may be retried within budget.
    /// </summary>
    RetryableTemporaryFailure,

    /// <summary>
    /// The failure is permanent and should not be retried automatically.
    /// </summary>
    PermanentFailure,

    /// <summary>
    /// The SMTP connection is no longer safe to reuse.
    /// </summary>
    ConnectionCorrupted,

    /// <summary>
    /// Delivery may already have crossed the SMTP DATA ambiguity boundary.
    /// </summary>
    UnknownAfterData,

    /// <summary>
    /// No connection lease could be acquired before the configured timeout.
    /// </summary>
    PoolExhausted,

    /// <summary>
    /// The failure type is outside the known SMTP failure families (likely a
    /// programming error or a foreign adapter fault). The sender discards the
    /// connection and rethrows the original exception unchanged instead of
    /// wrapping it in <see cref="SmtpSendFailedException"/>.
    /// </summary>
    Unclassified,
}
