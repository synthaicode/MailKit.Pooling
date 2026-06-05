namespace MailKit.Pooling.Abstractions;

public interface ISmtpClientAdapter : IAsyncDisposable
{
    bool IsConnected { get; }
    bool IsAuthenticated { get; }
    string EndpointKey { get; }

    Task ConnectAsync(CancellationToken cancellationToken);
    Task AuthenticateAsync(CancellationToken cancellationToken);
    Task NoOpAsync(CancellationToken cancellationToken);
    Task SendAsync(object message, CancellationToken cancellationToken);
    Task DisconnectAsync(bool quit, CancellationToken cancellationToken);
}
