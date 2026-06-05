using MailKit.Net.Smtp;
using MailKit.Pooling.Abstractions;
using MimeKit;

namespace MailKit.Pooling.MailKit;

public sealed class MailKitSmtpClientAdapter : ISmtpClientAdapter
{
    private readonly SmtpClient client;
    private readonly string endpointKey;
    private readonly bool authenticationSatisfied;

    public MailKitSmtpClientAdapter(
        SmtpClient client,
        string endpointKey,
        bool authenticationSatisfied)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.endpointKey = string.IsNullOrWhiteSpace(endpointKey)
            ? throw new ArgumentException("Endpoint key must not be empty.", nameof(endpointKey))
            : endpointKey;
        this.authenticationSatisfied = authenticationSatisfied;
    }

    public bool IsConnected => client.IsConnected;

    public bool IsAuthenticated => authenticationSatisfied || client.IsAuthenticated;

    public string EndpointKey => endpointKey;

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Use MailKitSmtpConnectionFactory to create connected adapters.");
    }

    public Task AuthenticateAsync(CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Use MailKitSmtpConnectionFactory to create authenticated adapters.");
    }

    public Task NoOpAsync(CancellationToken cancellationToken)
    {
        return client.NoOpAsync(cancellationToken);
    }

    public Task SendAsync(object message, CancellationToken cancellationToken)
    {
        if (message is not MimeMessage mimeMessage)
        {
            throw new ArgumentException(
                $"The MailKit adapter expects {nameof(MimeMessage)} instances.",
                nameof(message));
        }

        return client.SendAsync(mimeMessage, cancellationToken);
    }

    public Task DisconnectAsync(bool quit, CancellationToken cancellationToken)
    {
        return client.DisconnectAsync(quit, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        client.Dispose();
        return ValueTask.CompletedTask;
    }
}
