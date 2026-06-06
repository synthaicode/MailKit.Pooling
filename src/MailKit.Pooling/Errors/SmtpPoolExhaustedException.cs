namespace MailKit.Pooling.Errors;

internal sealed class SmtpPoolExhaustedException : TimeoutException
{
    public SmtpPoolExhaustedException(string message)
        : base(message)
    {
    }
}
