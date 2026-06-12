using PooledMailKit.MailKit;

namespace PooledMailKit.Options;

/// <summary>
/// Owns every <see cref="SmtpPoolOptions"/> validation rule so the dependency
/// injection registration path and direct <c>SmtpPool</c> construction enforce
/// identical rules with identical exception types: structural problems throw
/// <see cref="ArgumentException"/> and range violations throw
/// <see cref="ArgumentOutOfRangeException"/>.
/// </summary>
internal static class SmtpPoolOptionsValidator
{
    /// <summary>
    /// Validates the complete options graph eagerly so configuration mistakes
    /// fail before steady state instead of surfacing as transient-looking
    /// connection failures at the first send.
    /// </summary>
    public static void Validate(SmtpPoolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var hosts = options.GetConfiguredHosts();
        if (hosts.Count == 0)
        {
            throw new ArgumentException("At least one SMTP host must be configured.", nameof(options));
        }

        foreach (var host in hosts)
        {
            ValidateHost(host);
        }

        if (options.MaxPoolSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxPoolSize must be greater than zero.");
        }

        if (options.MinPoolSize < 0 || options.MinPoolSize > options.MaxPoolSize)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MinPoolSize must be between zero and MaxPoolSize.");
        }

        if (options.AcquireTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "AcquireTimeout must be greater than zero.");
        }

        if (options.JitterRatio is < 0d or > 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "JitterRatio must be between zero and one.");
        }

        if (options.RetryBaseDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "RetryBaseDelay must not be negative.");
        }

        if (options.MaxRetryAttempts < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxRetryAttempts must not be negative.");
        }
    }

    private static void ValidateHost(SmtpHostOptions host)
    {
        if (string.IsNullOrWhiteSpace(host.Host))
        {
            throw new ArgumentException("Each configured SMTP host must include Host.", nameof(host));
        }

        if (host.Weight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(host), "Each configured SMTP host must have Weight greater than zero.");
        }

        // Parse eagerly: an unparseable name would otherwise surface at the
        // first connection attempt as a repeating create failure with cooldown.
        MailKitSecureSocketOptionsParser.Parse(host.SecureSocketOptions);

        if (!string.IsNullOrWhiteSpace(host.UserName) && host.Password is null)
        {
            throw new ArgumentException(
                "Password must be set when UserName is configured; use an empty string explicitly for passwordless authentication.",
                nameof(host));
        }
    }
}
