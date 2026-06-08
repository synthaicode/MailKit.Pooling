using MailKit.Net.Smtp;

namespace PooledMailKit.MailKit;

internal interface IMailKitSmtpClientFactory
{
    SmtpClient Create();
}
