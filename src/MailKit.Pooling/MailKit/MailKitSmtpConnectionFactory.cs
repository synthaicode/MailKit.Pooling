using MailKit.Pooling.Abstractions;
using MailKit.Pooling.Internal;
using MailKit.Pooling.Options;

namespace MailKit.Pooling.MailKit;

public sealed class MailKitSmtpConnectionFactory : ISmtpConnectionFactory
{
    private readonly SmtpPoolOptions options;
    private readonly IMailKitSmtpClientFactory clientFactory;

    public MailKitSmtpConnectionFactory(
        SmtpPoolOptions options,
        IMailKitSmtpClientFactory clientFactory)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    }

    public async Task<ISmtpClientAdapter> CreateAuthenticatedClientAsync(CancellationToken cancellationToken)
    {
        var secureSocketOptions = MailKitSecureSocketOptionsParser.Parse(options.Host.SecureSocketOptions);
        var authenticationRequired = !string.IsNullOrWhiteSpace(options.Host.UserName);
        var client = clientFactory.Create();
        var adapter = new MailKitSmtpClientAdapter(
            client,
            BuildEndpointKey(),
            authenticationSatisfied: !authenticationRequired);

        try
        {
            await TimeoutExecution.ExecuteAsync(
                token => client.ConnectAsync(
                    options.Host.Host,
                    options.Host.Port,
                    secureSocketOptions,
                    token),
                options.ConnectTimeout,
                "connect",
                cancellationToken).ConfigureAwait(false);

            if (authenticationRequired)
            {
                var userName = options.Host.UserName!;
                await TimeoutExecution.ExecuteAsync(
                    token => client.AuthenticateAsync(
                        userName,
                        options.Host.Password ?? string.Empty,
                        token),
                    options.AuthenticateTimeout,
                    "authenticate",
                    cancellationToken).ConfigureAwait(false);
            }

            return adapter;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private string BuildEndpointKey()
    {
        return $"{options.Host.Host}:{options.Host.Port}";
    }
}
