using PooledMailKit.Abstractions;
using PooledMailKit.DependencyInjection;
using PooledMailKit.Metrics;
using PooledMailKit.Options;
using PooledMailKit.Pooling;
using Microsoft.Extensions.DependencyInjection;

namespace PooledMailKit.Tests.DependencyInjection;

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

        Assert.Contains("At least one SMTP host", exception.Message);
    }

    [Fact]
    public async Task AddMailKitPooling_Accepts_Multiple_Hosts()
    {
        var services = new ServiceCollection();

        services.AddMailKitPooling(options =>
        {
            options.Hosts =
            [
                new SmtpHostOptions { Host = "smtp-a.local", Port = 2525 },
                new SmtpHostOptions { Host = "smtp-b.local", Port = 2526 },
            ];
        });

        var provider = services.BuildServiceProvider();

        try
        {
            var options = provider.GetRequiredService<SmtpPoolOptions>();
            Assert.Equal(2, options.GetConfiguredHosts().Count);
        }
        finally
        {
            await provider.DisposeAsync();
        }
    }

    [Fact]
    public void AddMailKitPooling_Rejects_Out_Of_Range_JitterRatio()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => services.AddMailKitPooling(options =>
        {
            options.Host = new SmtpHostOptions { Host = "localhost" };
            options.JitterRatio = 1.5d;
        }));

        Assert.Contains("JitterRatio", exception.Message);
    }

    [Fact]
    public void AddMailKitPooling_Rejects_Non_Positive_Host_Weight()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => services.AddMailKitPooling(options =>
        {
            options.Hosts =
            [
                new SmtpHostOptions { Host = "smtp-a.local", Port = 2525, Weight = 0 },
            ];
        }));

        Assert.Contains("Weight greater than zero", exception.Message);
    }

    [Fact]
    public void AddMailKitPooling_Rejects_Unparseable_SecureSocketOptions_At_Registration()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => services.AddMailKitPooling(options =>
        {
            options.Host = new SmtpHostOptions
            {
                Host = "localhost",
                SecureSocketOptions = "StartTlsWhenAvailble",
            };
        }));

        Assert.Contains("SecureSocketOptions", exception.Message);
    }

    [Fact]
    public void AddMailKitPooling_Rejects_Missing_Password_When_UserName_Is_Set()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentException>(() => services.AddMailKitPooling(options =>
        {
            options.Host = new SmtpHostOptions
            {
                Host = "localhost",
                UserName = "mailer",
            };
        }));

        Assert.Contains("Password", exception.Message);
    }

    [Fact]
    public void AddMailKitPooling_Accepts_Explicit_Empty_Password_With_UserName()
    {
        var services = new ServiceCollection();

        services.AddMailKitPooling(options =>
        {
            options.Host = new SmtpHostOptions
            {
                Host = "localhost",
                UserName = "mailer",
                Password = "",
            };
        });
    }

    [Fact]
    public void AddMailKitPooling_Rejects_Negative_RetryBaseDelay_At_Registration()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => services.AddMailKitPooling(options =>
        {
            options.Host = new SmtpHostOptions { Host = "localhost" };
            options.RetryBaseDelay = TimeSpan.FromSeconds(-1);
        }));

        Assert.Contains("RetryBaseDelay", exception.Message);
    }

    [Theory]
    [InlineData(nameof(SmtpPoolOptions.ConnectTimeout))]
    [InlineData(nameof(SmtpPoolOptions.AuthenticateTimeout))]
    [InlineData(nameof(SmtpPoolOptions.SmtpSendTimeout))]
    public void AddMailKitPooling_Rejects_NonPositive_OperationTimeouts_At_Registration(string propertyName)
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => services.AddMailKitPooling(options =>
        {
            options.Host = new SmtpHostOptions { Host = "localhost" };
            typeof(SmtpPoolOptions).GetProperty(propertyName)!.SetValue(options, TimeSpan.Zero);
        }));

        Assert.Contains(propertyName, exception.Message);
    }

    [Fact]
    public void AddMailKitPooling_Rejects_ReconnectCooldown_Above_Maximum_At_Registration()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => services.AddMailKitPooling(options =>
        {
            options.Host = new SmtpHostOptions { Host = "localhost" };
            options.ReconnectCooldown = TimeSpan.FromSeconds(10);
            options.MaxReconnectCooldown = TimeSpan.FromSeconds(5);
        }));

        Assert.Contains("MaxReconnectCooldown", exception.Message);
    }
}
