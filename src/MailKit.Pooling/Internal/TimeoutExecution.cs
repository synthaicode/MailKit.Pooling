namespace PooledMailKit.Internal;

internal static class TimeoutExecution
{
    // .NET 8/10 timer APIs support at most UInt32.MaxValue - 1 milliseconds.
    internal static readonly TimeSpan MaximumSupportedDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1d);

    public static async Task ExecuteAsync(
        Func<CancellationToken, Task> operation,
        TimeSpan timeout,
        string operationName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (timeout == Timeout.InfiniteTimeSpan)
        {
            await operation(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (timeout <= TimeSpan.Zero || timeout > MaximumSupportedDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive and supported by the timer, or infinite.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await operation(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"SMTP operation '{operationName}' timed out after {timeout}.",
                exception);
        }
    }
}
