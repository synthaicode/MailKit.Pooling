using MailKit.Net.Smtp;

namespace MailKit.Pooling.MailKit;

internal sealed class DefaultMailKitSmtpClientFactory : IMailKitSmtpClientFactory
{
    public SmtpClient Create()
    {
        return new SmtpClient();
    }
}
