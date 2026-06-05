namespace MailKit.Pooling.Errors;

public sealed class HostUnavailableException : InvalidOperationException
{
    public HostUnavailableException(string message)
        : base(message)
    {
    }
}
