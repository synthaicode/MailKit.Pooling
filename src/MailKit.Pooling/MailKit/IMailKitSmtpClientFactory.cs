using MailKit.Net.Smtp;

namespace MailKit.Pooling.MailKit;

public interface IMailKitSmtpClientFactory
{
    SmtpClient Create();
}
