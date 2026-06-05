using MailKit.Pooling.Abstractions;
using MailKit.Pooling.DependencyInjection;
using MailKit.Pooling.Metrics;
using MailKit.Pooling.Options;
using MailKit.Pooling.Pooling;
using Microsoft.Extensions.DependencyInjection;

namespace MailKit.Pooling.Tests.DependencyInjection;

public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public async Task AddMailKitPooling_Registers_Core_Services()
    {
        var services = new ServiceCollection();

        services.AddMailKitPooling(options =>
        {
            options.Host = new SmtpHostOptions
            {
                Host = "localhost",
                Port = 2525,
            };
            options.MaxPoolSize = 4;
        });

        var provider = services.BuildServiceProvider();

        try
        {
            Assert.NotNull(provider.GetService<SmtpPool>());
            Assert.NotNull(provider.GetService<ISmtpConnectionFactory>());
            Assert.NotNull(provider.GetService<IClock>());
            Assert.NotNull(provider.GetService<ISmtpSender>());
            Assert.NotNull(provider.GetService<ISmtpPoolMetrics>());
            Assert.NotNull(provider.GetService<SmtpPoolOptions>());
        }
        finally
        {
            await provider.DisposeAsync();
        }
    }

    [Fact]
    public void AddMailKitPooling_Rejects_Missing_Host()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentException>(() => services.AddMailKitPooling(_ => { }));

        Assert.Contains("Host.Host", exception.Message);
    }
}
