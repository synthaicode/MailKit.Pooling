using MailKit.Pooling.Errors;
using MailKit.Pooling.Options;
using MailKit.Pooling.Pooling;
using MailKit.Pooling.Sending;
using MailKit.Pooling.Tests.TestDoubles;
using MimeKit;

namespace MailKit.Pooling.Tests.Sending;

public sealed class SmtpSenderTests
{
    [Fact]
    public async Task SendAsync_Returns_Result_And_Reuses_Connection()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-05T00:00:00Z"));
        var factory = new FakeSmtpConnectionFactory();
        var client = new FakeSmtpClientAdapter();
        factory.Enqueue(client);

        var options = CreateOptions();
        await using var pool = new SmtpPool(options, factory, clock);
        var sender = new SmtpSender(pool, new DefaultSmtpErrorClassifier(), options, clock);
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("from@example.com"));
        message.To.Add(MailboxAddress.Parse("to@example.com"));
        message.Subject = "test";
        message.Body = new TextPart("plain") { Text = "body" };

        var firstResult = await sender.SendAsync(message);
        var secondResult = await sender.SendAsync(message);

        Assert.Equal(2, client.SendCalls);
        Assert.Equal(firstResult.ConnectionId, secondResult.ConnectionId);
        Assert.Equal("smtp://primary", firstResult.EndpointKey);
        Assert.Equal(1, firstResult.Attempts);
        Assert.Equal(1, secondResult.Attempts);
    }

    [Fact]
    public async Task SendAsync_Uses_StageAware_Failure_And_Invalidates_Connection()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-05T00:00:00Z"));
        var factory = new FakeSmtpConnectionFactory();
        var firstClient = new FakeSmtpClientAdapter
        {
            OnSendAsync = static (_, _) => Task.FromException(
                new SmtpStageAwareException(
                    "temporary failure",
                    SmtpSendStage.EnvelopeStarted,
                    new TimeoutException("socket timed out"))),
        };
        var replacementClient = new FakeSmtpClientAdapter();
        factory.Enqueue(firstClient);
        factory.Enqueue(replacementClient);

        var options = CreateOptions();
        await using var pool = new SmtpPool(options, factory, clock);
        var sender = new SmtpSender(pool, new DefaultSmtpErrorClassifier(), options, clock);
        var message = CreateMessage();

        var exception = await Assert.ThrowsAsync<SmtpSendFailedException>(() => sender.SendAsync(message));

        Assert.Equal(SmtpFailureKind.RetryableBeforeSend, exception.Classification.Kind);
        Assert.Equal(SmtpSendStage.EnvelopeStarted, exception.Classification.Stage);
        Assert.True(exception.Classification.ShouldDiscardConnection);
        Assert.Equal(1, exception.Attempts);
        Assert.Equal(1, firstClient.DisposeCalls);

        clock.Advance(TimeSpan.FromSeconds(31));
        var result = await sender.SendAsync(message);
        Assert.Same(replacementClient, await GetCurrentClientAsync(pool));
        Assert.Equal(result.EndpointKey, replacementClient.EndpointKey);
    }

    [Fact]
    public async Task SendAsync_Converts_PoolExhausted_To_SmtpSendFailedException()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-05T00:00:00Z"));
        var factory = new FakeSmtpConnectionFactory();
        factory.Enqueue(new FakeSmtpClientAdapter());

        var options = CreateOptions(maxPoolSize: 1, acquireTimeout: TimeSpan.FromSeconds(10));
        await using var pool = new SmtpPool(
            options,
            factory,
            clock);

        var sender = new SmtpSender(pool, new DefaultSmtpErrorClassifier(), options, clock);
        var lease = await pool.AcquireLeaseAsync();

        var pendingSend = sender.SendAsync(CreateMessage());
        await Task.Yield();
        clock.Advance(TimeSpan.FromSeconds(10));

        var exception = await Assert.ThrowsAsync<SmtpSendFailedException>(async () => await pendingSend);

        Assert.Equal(SmtpFailureKind.PoolExhausted, exception.Classification.Kind);
        Assert.Equal(SmtpSendStage.BeforeConnect, exception.Classification.Stage);
        Assert.Equal(1, exception.Attempts);

        await lease.ReturnAsync();
    }

    [Fact]
    public async Task SendAsync_Converts_Internal_SendTimeout_To_Classified_Failure()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-05T00:00:00Z"));
        var factory = new FakeSmtpConnectionFactory();
        var client = new FakeSmtpClientAdapter
        {
            OnSendAsync = static async (_, cancellationToken) =>
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
        };
        factory.Enqueue(client);

        var options = CreateOptions(sendTimeout: TimeSpan.FromMilliseconds(50), reconnectCooldown: TimeSpan.Zero);
        await using var pool = new SmtpPool(options, factory, clock);
        var sender = new SmtpSender(pool, new DefaultSmtpErrorClassifier(), options, clock);

        var exception = await Assert.ThrowsAsync<SmtpSendFailedException>(() => sender.SendAsync(CreateMessage()));

        Assert.Equal(SmtpFailureKind.UnknownAfterData, exception.Classification.Kind);
        Assert.Equal(SmtpSendStage.DataStarted, exception.Classification.Stage);
        Assert.True(exception.Classification.ShouldDiscardConnection);
        Assert.Equal(1, exception.Attempts);
        Assert.IsType<TimeoutException>(exception.InnerException);
        Assert.Equal(1, client.DisposeCalls);
    }

    [Fact]
    public async Task SendAsync_Preserves_Caller_Cancellation_And_Invalidates_Connection()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-05T00:00:00Z"));
        var factory = new FakeSmtpConnectionFactory();
        var sendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeSmtpClientAdapter
        {
            OnSendAsync = async (_, cancellationToken) =>
            {
                sendStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
        };
        factory.Enqueue(client);

        var options = CreateOptions(sendTimeout: TimeSpan.FromSeconds(30), reconnectCooldown: TimeSpan.Zero);
        await using var pool = new SmtpPool(options, factory, clock);
        var sender = new SmtpSender(pool, new DefaultSmtpErrorClassifier(), options, clock);

        using var cancellationTokenSource = new CancellationTokenSource();
        var sendTask = sender.SendAsync(CreateMessage(), cancellationTokenSource.Token);
        await sendStarted.Task;
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await sendTask);

        Assert.Equal(1, client.DisposeCalls);
    }

    [Fact]
    public async Task SendAsync_CallerCancellation_Does_Not_Block_On_Slow_LeaseCleanup()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-05T00:00:00Z"));
        var factory = new FakeSmtpConnectionFactory();
        var sendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeSmtpClientAdapter
        {
            OnSendAsync = async (_, cancellationToken) =>
            {
                sendStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
            OnDisconnectAsync = static (_, _) => Task.Delay(Timeout.InfiniteTimeSpan),
        };
        factory.Enqueue(client);

        var options = CreateOptions(sendTimeout: TimeSpan.FromSeconds(30), reconnectCooldown: TimeSpan.Zero);
        await using var pool = new SmtpPool(options, factory, clock);
        var sender = new SmtpSender(pool, new DefaultSmtpErrorClassifier(), options, clock);

        using var cancellationTokenSource = new CancellationTokenSource();
        var sendTask = sender.SendAsync(CreateMessage(), cancellationTokenSource.Token);
        await sendStarted.Task;
        cancellationTokenSource.Cancel();

        var completedTask = await Task.WhenAny(sendTask, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.Same(sendTask, completedTask);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await sendTask);
    }

    [Fact]
    public async Task SendAsync_Preserves_Original_Failure_When_CallerCancellation_Races_With_Cleanup()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-05T00:00:00Z"));
        var factory = new FakeSmtpConnectionFactory();
        using var cancellationTokenSource = new CancellationTokenSource();
        var client = new FakeSmtpClientAdapter
        {
            OnSendAsync = (_, _) =>
            {
                cancellationTokenSource.Cancel();
                return Task.FromException(
                    new SmtpStageAwareException(
                        "retryable failure",
                        SmtpSendStage.EnvelopeStarted,
                        new TimeoutException("socket timed out")));
            },
            OnDisconnectAsync = static (_, cancellationToken) => cancellationToken.IsCancellationRequested
                ? Task.FromCanceled(cancellationToken)
                : Task.CompletedTask,
        };
        factory.Enqueue(client);

        var options = CreateOptions(sendTimeout: TimeSpan.FromSeconds(30), reconnectCooldown: TimeSpan.Zero);
        await using var pool = new SmtpPool(options, factory, clock);
        var sender = new SmtpSender(pool, new DefaultSmtpErrorClassifier(), options, clock);

        var exception = await Assert.ThrowsAsync<SmtpSendFailedException>(
            () => sender.SendAsync(CreateMessage(), cancellationTokenSource.Token));

        Assert.Equal(SmtpFailureKind.RetryableBeforeSend, exception.Classification.Kind);
        Assert.Equal(SmtpSendStage.EnvelopeStarted, exception.Classification.Stage);
        Assert.Equal(1, exception.Attempts);
    }

    [Fact]
    public async Task SendAsync_Retries_RetryableFailure_Within_Budget()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-05T00:00:00Z"));
        var factory = new FakeSmtpConnectionFactory();
        var firstClient = new FakeSmtpClientAdapter
        {
            OnSendAsync = static (_, _) => Task.FromException(
                new SmtpStageAwareException(
                    "retryable failure",
                    SmtpSendStage.EnvelopeStarted,
                    new TimeoutException("socket timed out"))),
        };
        var replacementClient = new FakeSmtpClientAdapter();
        factory.Enqueue(firstClient);
        factory.Enqueue(replacementClient);

        var options = CreateOptions(
            sendTimeout: TimeSpan.FromSeconds(30),
            reconnectCooldown: TimeSpan.Zero,
            maxRetryAttempts: 1,
            retryBaseDelay: TimeSpan.Zero);
        await using var pool = new SmtpPool(options, factory, clock);
        var sender = new SmtpSender(pool, new DefaultSmtpErrorClassifier(), options, clock);

        var result = await sender.SendAsync(CreateMessage());

        Assert.Equal(2, result.Attempts);
        Assert.Equal(1, firstClient.SendCalls);
        Assert.Equal(1, replacementClient.SendCalls);
        Assert.Equal(1, firstClient.DisposeCalls);
    }

    [Fact]
    public async Task SendAsync_Stops_When_RetryBudget_Is_Exhausted()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-05T00:00:00Z"));
        var factory = new FakeSmtpConnectionFactory();
        var firstClient = new FakeSmtpClientAdapter
        {
            OnSendAsync = static (_, _) => Task.FromException(
                new SmtpStageAwareException(
                    "retryable failure",
                    SmtpSendStage.EnvelopeStarted,
                    new TimeoutException("socket timed out"))),
        };
        var secondClient = new FakeSmtpClientAdapter
        {
            OnSendAsync = static (_, _) => Task.FromException(
                new SmtpStageAwareException(
                    "retryable failure",
                    SmtpSendStage.EnvelopeStarted,
                    new TimeoutException("socket timed out"))),
        };
        factory.Enqueue(firstClient);
        factory.Enqueue(secondClient);

        var options = CreateOptions(
            sendTimeout: TimeSpan.FromSeconds(30),
            reconnectCooldown: TimeSpan.Zero,
            maxRetryAttempts: 1,
            retryBaseDelay: TimeSpan.Zero);
        await using var pool = new SmtpPool(options, factory, clock);
        var sender = new SmtpSender(pool, new DefaultSmtpErrorClassifier(), options, clock);

        var exception = await Assert.ThrowsAsync<SmtpSendFailedException>(() => sender.SendAsync(CreateMessage()));

        Assert.Equal(SmtpFailureKind.RetryableBeforeSend, exception.Classification.Kind);
        Assert.Equal(2, exception.Attempts);
        Assert.Equal(1, firstClient.DisposeCalls);
        Assert.Equal(1, secondClient.DisposeCalls);
    }

    private static async Task<FakeSmtpClientAdapter> GetCurrentClientAsync(SmtpPool pool)
    {
        var lease = await pool.AcquireLeaseAsync();
        try
        {
            return (FakeSmtpClientAdapter) lease.Client;
        }
        finally
        {
            await lease.ReturnAsync();
        }
    }

    private static MimeMessage CreateMessage()
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("from@example.com"));
        message.To.Add(MailboxAddress.Parse("to@example.com"));
        message.Subject = "test";
        message.Body = new TextPart("plain") { Text = "body" };
        return message;
    }

    private static SmtpPoolOptions CreateOptions(
        int maxPoolSize = 1,
        TimeSpan? acquireTimeout = null,
        TimeSpan? sendTimeout = null,
        TimeSpan? reconnectCooldown = null,
        int maxRetryAttempts = 0,
        TimeSpan? retryBaseDelay = null)
    {
        return new SmtpPoolOptions
        {
            Host = new SmtpHostOptions
            {
                Host = "localhost",
            },
            MaxPoolSize = maxPoolSize,
            MinPoolSize = 0,
            AcquireTimeout = acquireTimeout ?? TimeSpan.FromSeconds(15),
            SendTimeout = sendTimeout ?? TimeSpan.FromSeconds(30),
            ReconnectCooldown = reconnectCooldown ?? TimeSpan.FromSeconds(30),
            MaxRetryAttempts = maxRetryAttempts,
            RetryBaseDelay = retryBaseDelay ?? TimeSpan.FromSeconds(2),
            JitterRatio = 0d,
        };
    }
}
