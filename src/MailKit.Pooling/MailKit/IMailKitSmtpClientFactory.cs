using MailKit.Net.Smtp;

namespace MailKit.Pooling.MailKit;

internal interface IMailKitSmtpClientFactory
{
    SmtpClient Create();
}
