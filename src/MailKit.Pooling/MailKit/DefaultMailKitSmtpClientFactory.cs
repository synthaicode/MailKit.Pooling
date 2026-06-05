using MailKit.Net.Smtp;

namespace MailKit.Pooling.MailKit;

public sealed class DefaultMailKitSmtpClientFactory : IMailKitSmtpClientFactory
{
    public SmtpClient Create()
    {
        return new SmtpClient();
    }
}
