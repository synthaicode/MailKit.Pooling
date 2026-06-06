using MailKit.Pooling.Abstractions;

namespace MailKit.Pooling.Tests.TestDoubles;

internal sealed class FakeClock : IClock
{
    private readonly object sync = new();
    private readonly List<PendingDelay> delays = [];
    private int delayCallCount;

    public FakeClock(DateTimeOffset initialTime)
    {
        UtcNow = initialTime;
    }

    public DateTimeOffset UtcNow { get; private set; }

    public int DelayCallCount => Volatile.Read(ref delayCallCount);

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref delayCallCount);

        lock (sync)
        {
            if (delay <= TimeSpan.Zero)
            {
                return Task.CompletedTask;
            }

            var pendingDelay = new PendingDelay(UtcNow + delay, cancellationToken);
            delays.Add(pendingDelay);
            return pendingDelay.Task;
        }
    }

    public void Advance(TimeSpan delta)
    {
        List<PendingDelay> ready;

        lock (sync)
        {
            UtcNow += delta;
            ready = [.. delays.Where(delay => delay.ReadyAt <= UtcNow)];
            delays.RemoveAll(delay => delay.ReadyAt <= UtcNow);
        }

        foreach (var delay in ready)
        {
            delay.SetCompleted();
        }
    }

    private sealed class PendingDelay
    {
        private readonly TaskCompletionSource completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PendingDelay(DateTimeOffset readyAt, CancellationToken cancellationToken)
        {
            ReadyAt = readyAt;

            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(() => completionSource.TrySetCanceled(cancellationToken));
            }
        }

        public DateTimeOffset ReadyAt { get; }

        public Task Task => completionSource.Task;

        public void SetCompleted()
        {
            completionSource.TrySetResult();
        }
    }
}
