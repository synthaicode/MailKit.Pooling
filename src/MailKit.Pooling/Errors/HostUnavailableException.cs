namespace PooledMailKit.Errors;

internal sealed class HostUnavailableException : InvalidOperationException
{
    public HostUnavailableException(string message)
        : base(message)
    {
    }
}
