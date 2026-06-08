using PooledMailKit.Abstractions;
using PooledMailKit.Internal;
using PooledMailKit.Options;

namespace PooledMailKit.MailKit;

internal sealed class MailKitSmtpConnectionFactory : ISmtpConnectionFactory
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

    public async Task<ISmtpClientAdapter> CreateAuthenticatedClientAsync(
        SmtpHostOptions host,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);

        var secureSocketOptions = MailKitSecureSocketOptionsParser.Parse(host.SecureSocketOptions);
        var authenticationRequired = !string.IsNullOrWhiteSpace(host.UserName);
        var client = clientFactory.Create();
        var adapter = new MailKitSmtpClientAdapter(
            client,
            host.ToEndpointKey(),
            authenticationSatisfied: !authenticationRequired);

        try
        {
            await TimeoutExecution.ExecuteAsync(
                token => client.ConnectAsync(
                    host.Host,
                    host.Port,
                    secureSocketOptions,
                    token),
                options.ConnectTimeout,
                "connect",
                cancellationToken).ConfigureAwait(false);

            if (authenticationRequired)
            {
                var userName = host.UserName!;
                await TimeoutExecution.ExecuteAsync(
                    token => client.AuthenticateAsync(
                        userName,
                        host.Password ?? string.Empty,
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
}
