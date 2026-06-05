using MailKit.Pooling.Abstractions;
using MailKit.Pooling.Errors;
using MailKit.Pooling.MailKit;
using MailKit.Pooling.Metrics;
using MailKit.Pooling.Options;
using MailKit.Pooling.Pooling;
using MailKit.Pooling.Sending;
using Microsoft.Extensions.DependencyInjection;

namespace MailKit.Pooling.DependencyInjection;

public static class ServiceCollectionExtensions
{
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
        if (string.IsNullOrWhiteSpace(options.Host.Host))
        {
            throw new ArgumentException("SmtpPoolOptions.Host.Host must be configured.", nameof(options));
        }
    }
}
