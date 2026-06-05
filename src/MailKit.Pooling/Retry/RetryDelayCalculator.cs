namespace MailKit.Pooling.Retry;

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

        if (baseDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(baseDelay), "Retry base delay must not be negative.");
        }

        if (jitterRatio < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(jitterRatio), "Jitter ratio must not be negative.");
        }

        if (baseDelay == TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var multiplier = useExponentialBackoff
            ? Math.Pow(2d, retryAttempt - 1)
            : 1d;

        var delayTicks = baseDelay.Ticks * multiplier;
        var cappedTicks = delayTicks >= long.MaxValue
            ? long.MaxValue
            : (long) delayTicks;

        if (jitterRatio == 0d)
        {
            return TimeSpan.FromTicks(cappedTicks);
        }

        var jitterFactor = 1d + ((Random.Shared.NextDouble() * 2d) - 1d) * jitterRatio;
        var jitteredTicks = cappedTicks * jitterFactor;
        var boundedTicks = jitteredTicks <= 0d
            ? 0L
            : jitteredTicks >= long.MaxValue
                ? long.MaxValue
                : (long) jitteredTicks;

        return TimeSpan.FromTicks(boundedTicks);
    }
}
