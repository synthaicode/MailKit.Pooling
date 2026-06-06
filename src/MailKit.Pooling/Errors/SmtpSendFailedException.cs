namespace MailKit.Pooling.Errors;

/// <summary>
/// Represents an SMTP send failure after MailKit.Pooling classification.
/// </summary>
public sealed class SmtpSendFailedException : Exception
{
    /// <summary>
    /// Initializes a new exception instance.
    /// </summary>
    /// <param name="message">The exception message.</param>
    /// <param name="classification">The failure classification.</param>
    /// <param name="attempts">The number of send attempts performed.</param>
    /// <param name="innerException">The original underlying exception.</param>
    public SmtpSendFailedException(
        string message,
        SmtpFailureClassification classification,
        int attempts,
        Exception innerException)
        : base(message, innerException)
    {
        Classification = classification;
        Attempts = attempts;
    }

    /// <summary>
    /// Gets the normalized failure classification.
    /// </summary>
    public SmtpFailureClassification Classification { get; }

    /// <summary>
    /// Gets the number of attempts that were performed.
    /// </summary>
    public int Attempts { get; }
}
