using MailKit.Pooling.Errors;
using MailKit.Pooling.MailKit;
using MailKit.Pooling.Metrics;
using MailKit.Pooling.Options;
using MailKit.Pooling.Pooling;
using MailKit.Pooling.Sending;
using MailKit.Pooling.StressTests.Helpers;

namespace MailKit.Pooling.StressTests.ReconnectStorm;

public sealed class ReconnectSuppressionStressTests
{
    [ManualStressFact]
    [Trait("Category", "Stress")]
    public async Task Suppresses_Reconnect_Storm_And_Recovers_After_Restore()
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
            ReconnectCooldown = TimeSpan.FromSeconds(2),
            RetryBaseDelay = TimeSpan.FromMilliseconds(250),
            MaxRetryAttempts = 0,
            JitterRatio = 0d,
        };

        var factory = new MailKitSmtpConnectionFactory(options, new DefaultMailKitSmtpClientFactory());
        await using var pool = new SmtpPool(options, factory, metrics: metrics);
        var sender = new SmtpSender(pool, new DefaultSmtpErrorClassifier(), options, metrics: metrics);

        var warmupSuccesses = await SendBatchAsync(sender, harness, "storm-warm", 6, 2);
        await harness.StopAsync();

        var outageFailures = await SendFailingBatchAsync(sender, harness, "storm-outage", 12, 4);
        await harness.RestoreAsync();
        await Task.Delay(options.ReconnectCooldown + TimeSpan.FromMilliseconds(250));

        var recoverySuccesses = await SendBatchAsync(sender, harness, "storm-recover", 6, 2);
        var finalSuccesses = await SendBatchAsync(sender, harness, "storm-final", 4, 2);

        var provisional = new ReconnectSuppressionScenarioResult(
            warmupSuccesses,
            outageFailures,
            recoverySuccesses,
            (int) metrics.Sum(SmtpMetricNames.PoolConnectionsCreated),
            (int) metrics.Sum(SmtpMetricNames.PoolReconnectSuppressed),
            (int) metrics.Sum(SmtpMetricNames.SendRetryCount),
            finalSuccesses,
            metrics.ClassificationCounts(),
            string.Empty);
        var resultFilePath = await harness.WriteJsonResultAsync("reconnect-suppression", provisional);
        var result = provisional with { ResultFilePath = resultFilePath };
        await harness.WriteJsonResultAsync("reconnect-suppression-latest", result);

        Assert.True(result.SuppressedReconnectCount > 0);
        Assert.True(result.ReconnectAttempts > 0);
        Assert.True(result.OutageFailureCount > 0);
        Assert.True(result.RecoverySuccessCount > 0);
        Assert.True(result.FinalSuccessCount > 0);
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

    private static async Task<int> SendFailingBatchAsync(
        SmtpSender sender,
        Smtp4DevStressHarness harness,
        string prefix,
        int totalSends,
        int concurrency)
    {
        var failureCount = 0;
        var subjects = Enumerable.Range(0, totalSends)
            .Select(_ => $"{prefix}-{Guid.NewGuid():N}")
            .ToArray();

        await Parallel.ForEachAsync(
            subjects,
            new ParallelOptions { MaxDegreeOfParallelism = concurrency },
            async (subject, cancellationToken) =>
            {
                try
                {
                    await sender.SendAsync(harness.CreateMessage(subject, prefix), cancellationToken);
                }
                catch (SmtpSendFailedException)
                {
                    Interlocked.Increment(ref failureCount);
                }
            });

        return failureCount;
    }
}
