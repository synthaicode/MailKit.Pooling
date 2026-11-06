using PooledMailKit.Internal;

namespace PooledMailKit.Retry;

internal static class RetryDelayCalculator
{
    public static TimeSpan CalculateNextDelay(
        int retryAttempt,
        TimeSpan baseDelay,
        bool useExponentialBackoff,
        double jitterRatio)
    {
        if (retryAttempt <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retryAttempt), "Retry attempt must be positive.");
        }

        if (baseDelay < TimeSpan.Zero || baseDelay > TimeoutExecution.MaximumSupportedDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(baseDelay), "Retry base delay must be nonnegative and supported by the timer.");
        }

        if (!double.IsFinite(jitterRatio) || jitterRatio is < 0d or > 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(jitterRatio), "Jitter ratio must be finite and between zero and one.");
        }

        if (baseDelay == TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var multiplier = useExponentialBackoff
            ? Math.Pow(2d, retryAttempt - 1)
            : 1d;

        var delayTicks = baseDelay.Ticks * multiplier;
        var maximumTicks = TimeoutExecution.MaximumSupportedDelay.Ticks;
        var cappedTicks = delayTicks >= maximumTicks
            ? maximumTicks
            : (long) delayTicks;

        if (jitterRatio == 0d)
        {
            return TimeSpan.FromTicks(cappedTicks);
        }

        var jitterFactor = 1d + ((Random.Shared.NextDouble() * 2d) - 1d) * jitterRatio;
        var jitteredTicks = cappedTicks * jitterFactor;
        var boundedTicks = jitteredTicks <= 0d
            ? 0L
            : jitteredTicks >= maximumTicks
                ? maximumTicks
                : (long) jitteredTicks;

        return TimeSpan.FromTicks(boundedTicks);
    }
}
