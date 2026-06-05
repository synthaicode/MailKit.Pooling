namespace MailKit.Pooling.Abstractions;

public interface ISmtpConnectionFactory
{
    Task<ISmtpClientAdapter> CreateAuthenticatedClientAsync(CancellationToken cancellationToken);
}
