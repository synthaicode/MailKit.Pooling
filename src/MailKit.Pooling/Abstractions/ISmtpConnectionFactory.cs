namespace MailKit.Pooling.Abstractions;

public interface ISmtpConnectionFactory
{
    Task<ISmtpClientAdapter> CreateAuthenticatedClientAsync(
        Options.SmtpHostOptions host,
        CancellationToken cancellationToken);
}
