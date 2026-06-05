using MailKit.Pooling.Internal;

namespace MailKit.Pooling.Tests.Internal;

public sealed class TimeoutExecutionTests
{
    [Fact]
    public async Task ExecuteAsync_Throws_TimeoutException_When_InternalTimeout_Expires()
    {
        var exception = await Assert.ThrowsAsync<TimeoutException>(
            () => TimeoutExecution.ExecuteAsync(
                cancellationToken => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
                TimeSpan.FromMilliseconds(50),
                "send",
                CancellationToken.None));

        Assert.Contains("send", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_Preserves_Caller_Cancellation()
    {
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => TimeoutExecution.ExecuteAsync(
                cancellationToken => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
                TimeSpan.FromSeconds(30),
                "send",
                cancellationTokenSource.Token));
    }
}
