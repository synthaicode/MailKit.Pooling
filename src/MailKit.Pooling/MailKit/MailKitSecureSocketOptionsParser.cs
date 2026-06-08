using MailKit.Security;

namespace PooledMailKit.MailKit;

internal static class MailKitSecureSocketOptionsParser
{
    public static SecureSocketOptions Parse(string value)
    {
        if (Enum.TryParse<SecureSocketOptions>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        throw new ArgumentOutOfRangeException(
            nameof(value),
            value,
            "SecureSocketOptions must match a MailKit.Security.SecureSocketOptions value.");
    }
}
