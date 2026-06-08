using MailKit.Net.Smtp;
using PooledMailKit.MailKit;

namespace PooledMailKit.Tests.MailKit;

public sealed class MailKitSmtpClientAdapterTests
{
    [Fact]
    public async Task SendAsync_Rejects_Non_MimeMessage()
    {
        await using var adapter = new MailKitSmtpClientAdapter(
            new SmtpClient(),
            "smtp://localhost:25",
            authenticationSatisfied: true);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => adapter.SendAsync(new object(), CancellationToken.None));

        Assert.Contains("MimeMessage", exception.Message);
    }
}
