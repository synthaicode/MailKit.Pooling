namespace PooledMailKit.Internal;

internal static class TimeoutExecution
{
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

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive or infinite.");
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
