namespace PooledMailKit.Abstractions;

internal interface ISmtpConnectionFactory
{
    Task<ISmtpClientAdapter> CreateAuthenticatedClientAsync(
        Options.SmtpHostOptions host,
        CancellationToken cancellationToken);
}
