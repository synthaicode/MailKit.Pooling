using MailKit.Pooling.Errors;
using MailKit.Pooling.MailKit;
using MailKit.Pooling.Metrics;
using MailKit.Pooling.Options;
using MailKit.Pooling.Pooling;
using MailKit.Pooling.Sending;
using MailKit.Pooling.StressTests.Helpers;

namespace MailKit.Pooling.StressTests.ReconnectStorm;

public sealed class FlappingPatternsStressTests
{
    [ManualStressFact]
    [Trait("Category", "Stress")]
    public async Task Repeated_Flapping_Outage_Still_Suppresses_Reconnects_And_Recovers()
    {
        await using var harness = await Smtp4DevStressHarness.AcquireAsync();
        await harness.EnsureStartedAsync();

        var metrics = new InMemorySmtpPoolMetrics();
        var options = CreateOptions(harness);
        var factory = new MailKitSmtpConnectionFactory(options, new DefaultMailKitSmtpClientFactory());
        await using var pool = new SmtpPool(options, factory, metrics: metrics);
        var sender = new SmtpSender(pool, new DefaultSmtpErrorClassifier(), options, metrics: metrics);

        await SendBatchAsync(sender, harness, "flap-warm", 4, 2);

        var attemptsBefore = CountReconnectAttempts(metrics);
        var failuresBefore = metrics.Count(SmtpMetricNames.PoolConnectionCreateFailures);
        var suppressedBefore = metrics.Count(SmtpMetricNames.PoolReconnectSuppressed);

        const int cycleCount = 3;
        const int downSeconds = 5;
        const int upSeconds = 5;
        var attemptCount = 0;
        var failureCount = 0;

        for (var cycle = 0; cycle < cycleCount; cycle++)
        {
            await harness.StopAsync();
            var duringDown = await SendContinuouslyAsync(
                sender,
                harness,
                $"flap-down-{cycle}",
                TimeSpan.FromSeconds(downSeconds),
                concurrency: 3,
                interval: TimeSpan.FromMilliseconds(500));
            attemptCount += duringDown.AttemptCount;
            failureCount += duringDown.FailureCount;

            await harness.RestoreAsync();
            await Task.Delay(options.ReconnectCooldown + TimeSpan.FromMilliseconds(250));

            var duringUp = await SendContinuouslyAsync(
                sender,
                harness,
                $"flap-up-{cycle}",
                TimeSpan.FromSeconds(upSeconds),
                concurrency: 2,
                interval: TimeSpan.FromMilliseconds(750));
            attemptCount += duringUp.AttemptCount;
            failureCount += duringUp.FailureCount;
        }

        var recoverySuccessCount = await SendBatchAsync(sender, harness, "flap-final", 4, 2);

        var provisional = new FlappingScenarioResult(
            "3x-5s-down-5s-up",
            cycleCount,
            downSeconds,
            upSeconds,
            attemptCount,
            failureCount,
            CountReconnectAttempts(metrics) - attemptsBefore,
            metrics.Count(SmtpMetricNames.PoolConnectionCreateFailures) - failuresBefore,
            metrics.Count(SmtpMetricNames.PoolReconnectSuppressed) - suppressedBefore,
            recoverySuccessCount,
            metrics.ClassificationCounts(),
            string.Empty);

        var resultFilePath = await harness.WriteJsonResultAsync("flapping-outage", provisional);
        var result = provisional with { ResultFilePath = resultFilePath };
        await harness.WriteJsonResultAsync("flapping-outage-latest", result);

        Assert.True(result.AttemptCount > 0);
        Assert.True(result.FailureCount > 0);
        Assert.True(result.SuppressedReconnectCount > 0);
        Assert.True(result.ReconnectAttempts > 0);
        Assert.True(result.RecoverySuccessCount > 0);
    }

    private static SmtpPoolOptions CreateOptions(Smtp4DevStressHarness harness)
    {
        return new SmtpPoolOptions
        {
            Host = new SmtpHostOptions
            {
                Host = harness.SmtpHost,
                Port = harness.SmtpPort,
                SecureSocketOptions = "None",
            },
            MaxPoolSize = 2,
            AcquireTimeout = TimeSpan.FromSeconds(2),
            ConnectTimeout = TimeSpan.FromSeconds(2),
            SendTimeout = TimeSpan.FromSeconds(5),
            ReconnectCooldown = TimeSpan.FromSeconds(2),
            RetryBaseDelay = TimeSpan.FromMilliseconds(250),
            MaxRetryAttempts = 0,
            JitterRatio = 0d,
        };
    }

    private static int CountReconnectAttempts(InMemorySmtpPoolMetrics metrics)
    {
        return (int)(metrics.Sum(SmtpMetricNames.PoolConnectionsCreated) + metrics.Sum(SmtpMetricNames.PoolConnectionCreateFailures));
    }

    private static async Task<int> SendBatchAsync(
        SmtpSender sender,
        Smtp4DevStressHarness harness,
        string prefix,
        int totalSends,
        int concurrency)
    {
        var successCount = 0;
        var subjects = Enumerable.Range(0, totalSends)
            .Select(_ => $"{prefix}-{Guid.NewGuid():N}")
            .ToArray();

        await Parallel.ForEachAsync(
            subjects,
            new ParallelOptions { MaxDegreeOfParallelism = concurrency },
            async (subject, cancellationToken) =>
            {
                await sender.SendAsync(harness.CreateMessage(subject, prefix), cancellationToken);
                Interlocked.Increment(ref successCount);
            });

        return successCount;
    }

    private static async Task<(int AttemptCount, int FailureCount)> SendContinuouslyAsync(
        SmtpSender sender,
        Smtp4DevStressHarness harness,
        string prefix,
        TimeSpan duration,
        int concurrency,
        TimeSpan interval)
    {
        var attemptCount = 0;
        var failureCount = 0;
        var deadline = DateTimeOffset.UtcNow + duration;

        var tasks = Enumerable.Range(0, concurrency)
            .Select(async workerId =>
            {
                while (DateTimeOffset.UtcNow < deadline)
                {
                    var subject = $"{prefix}-{workerId}-{Guid.NewGuid():N}";
                    Interlocked.Increment(ref attemptCount);

                    try
                    {
                        await sender.SendAsync(harness.CreateMessage(subject, prefix), CancellationToken.None);
                    }
                    catch (SmtpSendFailedException)
                    {
                        Interlocked.Increment(ref failureCount);
                    }

                    await Task.Delay(interval).ConfigureAwait(false);
                }
            });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return (attemptCount, failureCount);
    }
}
