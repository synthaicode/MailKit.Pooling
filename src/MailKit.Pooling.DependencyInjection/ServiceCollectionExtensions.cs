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
    /// <remarks>Dispose the root <see cref="IServiceProvider"/> or host to ensure pooled SMTP connections are released.</remarks>
    /// <returns>The original service collection.</returns>
    public static IServiceCollection AddMailKitPooling(
        this IServiceCollection services,
        Action<SmtpPoolOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new SmtpPoolOptions();
        configure(options);
        SmtpPoolOptionsValidator.Validate(options);

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
}
