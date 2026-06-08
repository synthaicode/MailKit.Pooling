using PooledMailKit.Retry;

namespace PooledMailKit.Tests.Retry;

public sealed class RetryDelayCalculatorTests
{
    [Fact]
    public void CalculateNextDelay_Uses_BaseDelay_For_FirstRetry_When_ExponentialDisabled()
    {
        var delay = RetryDelayCalculator.CalculateNextDelay(
            retryAttempt: 1,
            baseDelay: TimeSpan.FromSeconds(2),
            useExponentialBackoff: false,
            jitterRatio: 0d);

        Assert.Equal(TimeSpan.FromSeconds(2), delay);
    }

    [Fact]
    public void CalculateNextDelay_Doubles_Delay_When_ExponentialEnabled()
    {
        var delay = RetryDelayCalculator.CalculateNextDelay(
            retryAttempt: 3,
            baseDelay: TimeSpan.FromSeconds(2),
            useExponentialBackoff: true,
            jitterRatio: 0d);

        Assert.Equal(TimeSpan.FromSeconds(8), delay);
    }
}
