using PooledMailKit.Abstractions;
using PooledMailKit.Errors;
using PooledMailKit.MailKit;
using PooledMailKit.Metrics;
using PooledMailKit.Options;
using PooledMailKit.Pooling;
using PooledMailKit.Sending;
using Microsoft.Extensions.DependencyInjection;

namespace PooledMailKit.DependencyInjection;

/// <summary>
/// Registers MailKit.Pooling services into dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the MailKit.Pooling sender, pool, and supporting services.
    /// </summary>
    /// <param name="services">The service collection to update.</param>
    /// <param name="configure">The pool configuration callback.</param>
    /// <returns>The original service collection.</returns>
    public static IServiceCollection AddMailKitPooling(
        this IServiceCollection services,
        Action<SmtpPoolOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new SmtpPoolOptions();
        configure(options);
        Validate(options);

        services.AddSingleton(options);
        services.AddSingleton<IClock>(_ => SystemClock.Instance);
        services.AddSingleton<ISmtpPoolMetrics>(_ => options.EnableMetrics
            ? new SystemDiagnosticsSmtpPoolMetrics()
            : NoOpSmtpPoolMetrics.Instance);
        services.AddSingleton<ISmtpErrorClassifier, DefaultSmtpErrorClassifier>();
        services.AddSingleton<IMailKitSmtpClientFactory, DefaultMailKitSmtpClientFactory>();
        services.AddSingleton<ISmtpConnectionFactory, MailKitSmtpConnectionFactory>();
        services.AddSingleton<SmtpPool>();
        services.AddSingleton<ISmtpSender, SmtpSender>();

        return services;
    }

    private static void Validate(SmtpPoolOptions options)
    {
        var hosts = options.GetConfiguredHosts();
        if (hosts.Count == 0)
        {
            throw new ArgumentException("At least one SMTP host must be configured.", nameof(options));
        }

        if (hosts.Any(host => string.IsNullOrWhiteSpace(host.Host)))
        {
            throw new ArgumentException("Each configured SMTP host must include Host.", nameof(options));
        }

        if (hosts.Any(static host => host.Weight <= 0))
        {
            throw new ArgumentException("Each configured SMTP host must have Weight greater than zero.", nameof(options));
        }
    }
}
