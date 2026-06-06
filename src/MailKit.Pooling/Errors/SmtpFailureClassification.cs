namespace MailKit.Pooling.Errors;

/// <summary>
/// Describes how a send failure was classified for retry and connection handling.
/// </summary>
public sealed class SmtpFailureClassification
{
    /// <summary>
    /// Initializes a new classification value.
    /// </summary>
    /// <param name="kind">The failure category.</param>
    /// <param name="stage">The SMTP send stage where the failure occurred.</param>
    /// <param name="isRetryAllowed">Whether the failure may be retried automatically.</param>
    /// <param name="shouldDiscardConnection">Whether the current connection should be discarded.</param>
    /// <param name="reason">A short machine-readable reason string.</param>
    public SmtpFailureClassification(
        SmtpFailureKind kind,
        SmtpSendStage stage,
        bool isRetryAllowed,
        bool shouldDiscardConnection,
        string reason)
    {
        Kind = kind;
        Stage = stage;
        IsRetryAllowed = isRetryAllowed;
        ShouldDiscardConnection = shouldDiscardConnection;
        Reason = reason;
    }

    /// <summary>
    /// Gets the failure category.
    /// </summary>
    public SmtpFailureKind Kind { get; }

    /// <summary>
    /// Gets the SMTP stage where the failure occurred.
    /// </summary>
    public SmtpSendStage Stage { get; }

    /// <summary>
    /// Gets a value indicating whether the library may retry this failure.
    /// </summary>
    public bool IsRetryAllowed { get; }

    /// <summary>
    /// Gets a value indicating whether the current connection should be discarded.
    /// </summary>
    public bool ShouldDiscardConnection { get; }

    /// <summary>
    /// Gets a short reason string for diagnostics and metrics.
    /// </summary>
    public string Reason { get; }

    /// <summary>
    /// Gets a value indicating whether the failure should be treated as terminal.
    /// </summary>
    public bool IsTerminal => !IsRetryAllowed;
}
