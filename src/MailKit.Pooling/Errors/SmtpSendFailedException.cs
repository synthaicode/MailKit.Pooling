namespace MailKit.Pooling.Errors;

public sealed class SmtpSendFailedException : Exception
{
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

    public SmtpFailureClassification Classification { get; }

    public int Attempts { get; }
}
