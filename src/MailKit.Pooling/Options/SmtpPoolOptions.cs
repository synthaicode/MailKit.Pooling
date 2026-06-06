namespace MailKit.Pooling.Options;

/// <summary>
/// Configures the SMTP pool, retry, timeout, and host-selection behavior.
/// </summary>
public sealed class SmtpPoolOptions
{
    /// <summary>
    /// Gets or sets the compatibility single-host configuration.
    /// </summary>
    public SmtpHostOptions Host { get; set; } = new();

    /// <summary>
    /// Gets or sets the preferred multi-host configuration list.
    /// </summary>
    public List<SmtpHostOptions> Hosts { get; set; } = [];

    /// <summary>
    /// Gets or sets the minimum number of warm idle connections to maintain.
    /// </summary>
    public int MinPoolSize { get; set; } = 0;

    /// <summary>
    /// Gets or sets the maximum number of live pooled connections.
    /// </summary>
    public int MaxPoolSize { get; set; } = 8;

    /// <summary>
    /// Gets or sets the maximum time to wait for a lease.
    /// </summary>
    public TimeSpan AcquireTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Gets or sets how long an idle connection may remain in the pool.
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Gets or sets the interval after which idle connections are validated before reuse.
    /// </summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gets or sets the timeout applied to SMTP connect operations.
    /// </summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Gets or sets the timeout applied to SMTP authentication operations.
    /// </summary>
    public TimeSpan AuthenticateTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Gets or sets the timeout applied to SMTP send operations.
    /// </summary>
    public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the host cooldown after connection failures.
    /// </summary>
    public TimeSpan ReconnectCooldown { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the upper bound for exponential reconnect cooldown.
    /// </summary>
    public TimeSpan MaxReconnectCooldown { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets a value indicating whether reconnect cooldown uses exponential backoff.
    /// </summary>
    public bool UseExponentialBackoff { get; set; } = true;

    /// <summary>
    /// Gets or sets the retry and cooldown jitter ratio.
    /// </summary>
    public double JitterRatio { get; set; } = 0.2d;

    /// <summary>
    /// Gets or sets the maximum automatic retry attempts for retryable failures.
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 0;

    /// <summary>
    /// Gets or sets the base delay for retry backoff.
    /// </summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets or sets a value indicating whether idle keep-alive validation is enabled.
    /// </summary>
    public bool EnableKeepAlive { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether built-in metrics emission is enabled.
    /// </summary>
    public bool EnableMetrics { get; set; } = true;

    /// <summary>
    /// Returns the configured SMTP host candidates, preferring <see cref="Hosts"/> when present.
    /// </summary>
    public IReadOnlyList<SmtpHostOptions> GetConfiguredHosts()
    {
        if (Hosts.Count > 0)
        {
            return Hosts;
        }

        return string.IsNullOrWhiteSpace(Host.Host)
            ? []
            : [Host];
    }
}
