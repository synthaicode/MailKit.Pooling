using MimeKit;

namespace MailKit.Pooling.Abstractions;

public interface ISmtpSender
{
    Task<Sending.SmtpSendResult> SendAsync(MimeMessage message, CancellationToken cancellationToken = default);
}
