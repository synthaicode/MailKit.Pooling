namespace MailKit.Pooling.Abstractions;

internal interface ISmtpConnectionFactory
{
    Task<ISmtpClientAdapter> CreateAuthenticatedClientAsync(
        Options.SmtpHostOptions host,
        CancellationToken cancellationToken);
}
