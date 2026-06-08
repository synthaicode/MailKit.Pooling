using MailKit.Net.Smtp;

namespace PooledMailKit.MailKit;

internal sealed class DefaultMailKitSmtpClientFactory : IMailKitSmtpClientFactory
{
    public SmtpClient Create()
    {
        return new SmtpClient();
    }
}
