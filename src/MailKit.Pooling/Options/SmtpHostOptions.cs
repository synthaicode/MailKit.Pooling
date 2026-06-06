namespace MailKit.Pooling.Options;

/// <summary>
/// Configures a single SMTP endpoint candidate for the pool.
/// </summary>
public sealed class SmtpHostOptions
{
    /// <summary>
    /// Gets or sets the SMTP host name.
    /// </summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the SMTP port.
    /// </summary>
    public int Port { get; set; } = 587;

    /// <summary>
    /// Gets or sets the SMTP user name when authentication is required.
    /// </summary>
    public string? UserName { get; set; }

    /// <summary>
    /// Gets or sets the SMTP password when authentication is required.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Gets or sets the MailKit secure socket option name.
    /// </summary>
    public string SecureSocketOptions { get; set; } = "StartTlsWhenAvailable";

    /// <summary>
    /// Gets or sets the host priority. Lower values are preferred first.
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// Gets or sets the weight within hosts that share the same priority.
    /// </summary>
    public int Weight { get; set; } = 1;

    /// <summary>
    /// Builds the logical endpoint key used for diagnostics and results.
    /// </summary>
    public string ToEndpointKey()
    {
        return $"{Host}:{Port}";
    }
}
