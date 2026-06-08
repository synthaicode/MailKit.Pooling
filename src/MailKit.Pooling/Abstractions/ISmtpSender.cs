using MimeKit;

namespace PooledMailKit.Abstractions;

/// <summary>
/// Sends SMTP messages through the pooled MailKit transport path.
/// </summary>
public interface ISmtpSender
{
    /// <summary>
    /// Sends a message through the configured SMTP pool.
    /// </summary>
    /// <param name="message">The MIME message to send.</param>
    /// <param name="cancellationToken">The caller cancellation token.</param>
    /// <returns>The accepted send result.</returns>
    Task<Sending.SmtpSendResult> SendAsync(MimeMessage message, CancellationToken cancellationToken = default);
}
