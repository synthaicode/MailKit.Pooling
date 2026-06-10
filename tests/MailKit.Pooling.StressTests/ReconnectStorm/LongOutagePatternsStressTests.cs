using PooledMailKit.Errors;
using PooledMailKit.MailKit;
using PooledMailKit.Metrics;
using PooledMailKit.Options;
using PooledMailKit.Pooling;
using PooledMailKit.Sending;
using PooledMailKit.StressTests.Helpers;

namespace PooledMailKit.StressTests.ReconnectStorm;

public sealed class LongOutagePatternsStressTests
{
    [ManualStressFact]
    [Trait("Category", "Stress")]
    public async Task Sustained_Long_Outage_Still_Suppresses_Reconnects_And_Recovers()
    {
        await using var harness = await Smtp4DevStressHarness.AcquireAsync();
        await harness.EnsureStartedAsync();

        var metrics = new InMemorySmtpPoolMetrics();
        var options = new SmtpPoolOptions
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
            SmtpSendTimeout = TimeSpan.FromSeconds(5),
            ReconnectCooldown = TimeSpan.FromSeconds(2),
            // Pin recovery latency to the base cooldown; backoff growth is covered
            // by deterministic unit tests.
            MaxReconnectCooldown = TimeSpan.FromSeconds(2),
            RetryBaseDelay = TimeSpan.FromMilliseconds(250),
            MaxRetryAttempts = 0,
            JitterRatio = 0d,
        };

        var factory = new MailKitSmtpConnectionFactory(options, new DefaultMailKitSmtpClientFactory());
        await using var pool = new SmtpPool(options, factory, metrics: metrics);
        var sender = new SmtpSender(pool, new DefaultSmtpErrorClassifier(), options, metrics: metrics);

        var warmupSuccesses = await SendBatchAsync(sender, harness, "long-outage-warm", 6, 2);
        Assert.True(warmupSuccesses > 0);

        var attemptsBeforeOutage = CountReconnectAttempts(metrics);
        var failuresBeforeOutage = metrics.Count(SmtpMetricNames.PoolConnectionCreateFailures);
        var suppressedBeforeOutage = metrics.Count(SmtpMetricNames.PoolReconnectSuppressed);

        var outageDuration = TimeSpan.FromSeconds(20);
        await harness.StopAsync();
        var outageResult = await SendContinuouslyDuringOutageAsync(
            sender,
            harness,
            "long-outage",
            outageDuration,
            concurrency: 4,
            interval: TimeSpan.FromMilliseconds(500));

        await harness.RestoreAsync();
        await Task.Delay(options.ReconnectCooldown + TimeSpan.FromMilliseconds(500));

        var recoverySuccesses = await SendBatchAsync(sender, harness, "long-outage-recover", 6, 2);
        var finalSuccesses = await SendBatchAsync(sender, harness, "long-outage-final", 4, 2);

        var provisional = new LongOutageScenarioResult(
            "sustained-20s",
            (int)outageDuration.TotalSeconds,
            outageResult.AttemptCount,
            outageResult.FailureCount,
            CountReconnectAttempts(metrics) - attemptsBeforeOutage,
            metrics.Count(SmtpMetricNames.PoolConnectionCreateFailures) - failuresBeforeOutage,
            metrics.Count(SmtpMetricNames.PoolReconnectSuppressed) - suppressedBeforeOutage,
            recoverySuccesses,
            finalSuccesses,
            metrics.ClassificationCounts(),
            string.Empty);

        var resultFilePath = await harness.WriteJsonResultAsync("long-outage-sustained", provisional);
        var result = provisional with { ResultFilePath = resultFilePath };
        await harness.WriteJsonResultAsync("long-outage-sustained-latest", result);

        Assert.True(result.OutageAttemptCount > 0);
        Assert.True(result.OutageFailureCount > 0);
        Assert.True(result.SuppressedReconnectCount > 0);
        Assert.True(result.ReconnectAttempts > 0);
        Assert.True(result.RecoverySuccessCount > 0);
        Assert.True(result.FinalSuccessCount > 0);
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

    private static async Task<(int AttemptCount, int FailureCount)> SendContinuouslyDuringOutageAsync(
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
