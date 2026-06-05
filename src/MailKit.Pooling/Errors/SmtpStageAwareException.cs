namespace MailKit.Pooling.Errors;

public sealed class SmtpStageAwareException : Exception, ISmtpStageAwareException
{
    public SmtpStageAwareException(string message, SmtpSendStage stage, Exception? innerException = null)
        : base(message, innerException)
    {
        Stage = stage;
    }

    public SmtpSendStage Stage { get; }
}
