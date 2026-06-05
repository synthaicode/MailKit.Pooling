namespace MailKit.Pooling.Errors;

public sealed class SmtpPoolExhaustedException : TimeoutException
{
    public SmtpPoolExhaustedException(string message)
        : base(message)
    {
    }
}
