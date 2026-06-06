namespace MailKit.Pooling.Errors;

internal sealed class HostUnavailableException : InvalidOperationException
{
    public HostUnavailableException(string message)
        : base(message)
    {
    }
}
