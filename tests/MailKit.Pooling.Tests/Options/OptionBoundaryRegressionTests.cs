using MailKit.Security;
using PooledMailKit.Internal;
using PooledMailKit.MailKit;
using PooledMailKit.Options;
using PooledMailKit.Pooling;
using PooledMailKit.Retry;
using PooledMailKit.Tests.TestDoubles;

namespace PooledMailKit.Tests.Options;

public sealed class OptionBoundaryRegressionTests
{
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.01d)]
    [InlineData(1.01d)]
    public void Invalid_Jitter_Is_Rejected_At_Configuration_And_Calculation(double ratio)
    {
        var options = CreateOptions();
        options.JitterRatio = ratio;

        Assert.Throws<ArgumentOutOfRangeException>(() => SmtpPoolOptionsValidator.Validate(options));
        Assert.Throws<ArgumentOutOfRangeException>(() => RetryDelayCalculator.CalculateNextDelay(
            1, TimeSpan.FromMilliseconds(200), false, ratio));
    }

    [Theory]
    [InlineData("999")]
    [InlineData("-1")]
    public void Undefined_Numeric_Socket_Mode_Is_Rejected_Before_Connection(string mode)
    {
        var options = CreateOptions();
        options.Host.SecureSocketOptions = mode;

        Assert.Throws<ArgumentOutOfRangeException>(() => MailKitSecureSocketOptionsParser.Parse(mode));
        Assert.Throws<ArgumentOutOfRangeException>(() => SmtpPoolOptionsValidator.Validate(options));
    }

    [Fact]
    public void All_Defined_Socket_Modes_Still_Parse_By_Name_And_Numeric_Value()
    {
        foreach (var mode in Enum.GetValues<SecureSocketOptions>())
        {
            Assert.Equal(mode, MailKitSecureSocketOptionsParser.Parse(mode.ToString()));
            Assert.Equal(mode, MailKitSecureSocketOptionsParser.Parse(((int)mode).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
    }

    [Theory]
    [InlineData("AcquireTimeout")]
    [InlineData("ConnectTimeout")]
    [InlineData("AuthenticateTimeout")]
    [InlineData("SmtpSendTimeout")]
    [InlineData("RetryBaseDelay")]
    [InlineData("ReconnectCooldown")]
    [InlineData("MaxReconnectCooldown")]
    [InlineData("MinPoolRefillDelay")]
    public void Timer_Boundary_Is_Checked_Before_Runtime_Use(string setting)
    {
        var options = CreateOptions();
        SetDelay(options, setting, TimeoutExecution.MaximumSupportedDelay);
        SmtpPoolOptionsValidator.Validate(options);

        SetDelay(options, setting, TimeoutExecution.MaximumSupportedDelay + TimeSpan.FromMilliseconds(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => SmtpPoolOptionsValidator.Validate(options));
    }

    [Fact]
    public void Infinite_Operation_Timeouts_Remain_Supported_But_Acquisition_Must_Be_Finite()
    {
        var options = CreateOptions();
        options.ConnectTimeout = Timeout.InfiniteTimeSpan;
        options.AuthenticateTimeout = Timeout.InfiniteTimeSpan;
        options.SmtpSendTimeout = Timeout.InfiniteTimeSpan;
        SmtpPoolOptionsValidator.Validate(options);

        options.AcquireTimeout = Timeout.InfiniteTimeSpan;
        Assert.Throws<ArgumentOutOfRangeException>(() => SmtpPoolOptionsValidator.Validate(options));
        options.AcquireTimeout = TimeSpan.MaxValue;
        Assert.Throws<ArgumentOutOfRangeException>(() => SmtpPoolOptionsValidator.Validate(options));
    }

    [Fact]
    public async Task Largest_Supported_Timeout_Executes_And_Unsupported_Timeout_Does_Not_Start_Operation()
    {
        var calls = 0;
        Task Operation(CancellationToken token)
        {
            calls++;
            return Task.CompletedTask;
        }

        var maximum = TimeSpan.FromMilliseconds(4_294_967_294d);
        Assert.Equal(maximum, TimeoutExecution.MaximumSupportedDelay);
        using var runtimeTimer = new CancellationTokenSource();
        runtimeTimer.CancelAfter(maximum);
        Assert.Throws<ArgumentOutOfRangeException>(() => runtimeTimer.CancelAfter(maximum + TimeSpan.FromMilliseconds(1)));

        await TimeoutExecution.ExecuteAsync(Operation, maximum, "boundary", CancellationToken.None);
        Assert.Equal(1, calls);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => TimeoutExecution.ExecuteAsync(
            Operation, maximum + TimeSpan.FromMilliseconds(1), "boundary", CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(1024)]
    [InlineData(int.MaxValue)]
    public async Task Exponential_Backoff_Always_Produces_A_Runtime_Supported_Delay(int attempt)
    {
        var delay = RetryDelayCalculator.CalculateNextDelay(
            attempt, TimeoutExecution.MaximumSupportedDelay, true, 0d);
        Assert.Equal(TimeoutExecution.MaximumSupportedDelay, delay);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(delay, canceled.Token));
    }

    [Fact]
    public void Positive_Jitter_Cannot_Push_Backoff_Above_Timer_Ceiling()
    {
        for (var sample = 0; sample < 16; sample++)
        {
            var delay = RetryDelayCalculator.CalculateNextDelay(
                int.MaxValue, TimeoutExecution.MaximumSupportedDelay, true, 1d);
            Assert.InRange(delay, TimeSpan.Zero, TimeoutExecution.MaximumSupportedDelay);
        }
    }

    [Fact]
    public async Task Equal_Large_Host_Weights_Keep_The_Same_Balanced_Selection()
    {
        var factory = new FakeSmtpConnectionFactory();
        for (var index = 0; index < 6; index++)
        {
            factory.Enqueue(new FakeSmtpClientAdapter());
        }
        var options = CreateOptions();
        options.MaxPoolSize = 6;
        options.Hosts = [
            new SmtpHostOptions { Host = "a", Port = 25, Weight = 1_500_000_000 },
            new SmtpHostOptions { Host = "b", Port = 25, Weight = 1_500_000_000 },
        ];
        await using var pool = new SmtpPool(options, factory);
        var leases = new List<SmtpConnectionLease>();
        for (var index = 0; index < 6; index++)
        {
            leases.Add(await pool.AcquireLeaseAsync());
        }

        Assert.Equal(new[] { "a:25", "b:25", "a:25", "b:25", "a:25", "b:25" }, factory.RequestedHosts);
        foreach (var lease in leases)
        {
            await lease.ReturnAsync();
        }
    }

    private static SmtpPoolOptions CreateOptions() => new()
    {
        Host = new SmtpHostOptions { Host = "smtp.test", Port = 25 },
        JitterRatio = 0d,
        MaxReconnectCooldown = TimeoutExecution.MaximumSupportedDelay,
    };

    private static void SetDelay(SmtpPoolOptions options, string name, TimeSpan delay)
    {
        switch (name)
        {
            case "AcquireTimeout": options.AcquireTimeout = delay; break;
            case "ConnectTimeout": options.ConnectTimeout = delay; break;
            case "AuthenticateTimeout": options.AuthenticateTimeout = delay; break;
            case "SmtpSendTimeout": options.SmtpSendTimeout = delay; break;
            case "RetryBaseDelay": options.RetryBaseDelay = delay; break;
            case "ReconnectCooldown": options.ReconnectCooldown = delay; break;
            case "MaxReconnectCooldown": options.MaxReconnectCooldown = delay; break;
            case "MinPoolRefillDelay": options.MinPoolRefillDelay = delay; break;
            default: throw new ArgumentOutOfRangeException(nameof(name));
        }
    }
}
