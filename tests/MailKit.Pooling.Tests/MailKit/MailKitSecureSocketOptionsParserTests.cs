using PooledMailKit.MailKit;
using MailKit.Security;

namespace PooledMailKit.Tests.MailKit;

public sealed class MailKitSecureSocketOptionsParserTests
{
    [Fact]
    public void Parse_Accepts_Known_Value_Case_Insensitively()
    {
        var parsed = MailKitSecureSocketOptionsParser.Parse("starttlswhenavailable");

        Assert.Equal(SecureSocketOptions.StartTlsWhenAvailable, parsed);
    }

    [Fact]
    public void Parse_Rejects_Unknown_Value()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MailKitSecureSocketOptionsParser.Parse("NoSuchMode"));
    }
}
