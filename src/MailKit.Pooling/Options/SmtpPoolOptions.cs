namespace MailKit.Pooling.Options;

public sealed class SmtpPoolOptions
{
    public SmtpHostOptions Host { get; set; } = new();
    public List<SmtpHostOptions> Hosts { get; set; } = [];
    public int MinPoolSize { get; set; } = 0;
    public int MaxPoolSize { get; set; } = 8;
    public TimeSpan AcquireTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan AuthenticateTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan ReconnectCooldown { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaxReconnectCooldown { get; set; } = TimeSpan.FromMinutes(5);
    public bool UseExponentialBackoff { get; set; } = true;
    public double JitterRatio { get; set; } = 0.2d;
    public int MaxRetryAttempts { get; set; } = 0;
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);
    public bool EnableKeepAlive { get; set; } = true;
    public bool EnableMetrics { get; set; } = true;

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
