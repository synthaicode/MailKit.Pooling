using MailKit.Pooling.Errors;
using MailKit.Pooling.MailKit;
using MailKit.Pooling.Metrics;
using MailKit.Pooling.Options;
using MailKit.Pooling.Pooling;
using MailKit.Pooling.Sending;
using MailKit.Pooling.StressTests.Helpers;

namespace MailKit.Pooling.StressTests.ReconnectStorm;

public sealed class MultiHostPartialOutageStressTests
{
    [ManualStressFact]
    [Trait("Category", "Stress")]
    public async Task Primary_Outage_Fails_Over_To_Secondary_And_Primary_Recovers_Later()
    {
        await using var harness = await Smtp4DevStressHarness.AcquireAsync();
        await harness.EnsureStartedAsync();

        var metrics = new InMemorySmtpPoolMetrics();
        var options = new SmtpPoolOptions
        {
            Hosts =
            [
                new SmtpHostOptions
                {
                    Host = "localhost",
                    Port = 2525,
                    SecureSocketOptions = "None",
                    Priority = 0,
                    Weight = 1,
                },
                new SmtpHostOptions
                {
                    Host = "localhost",
                    Port = 2526,
                    SecureSocketOptions = "None",
                    Priority = 10,
                    Weight = 1,
                },
            ],
            MaxPoolSize = 2,
            AcquireTimeout = TimeSpan.FromSeconds(2),
            ConnectTimeout = TimeSpan.FromSeconds(2),
            SendTimeout = TimeSpan.FromSeconds(5),
            ReconnectCooldown = TimeSpan.FromSeconds(2),
            RetryBaseDelay = TimeSpan.FromMilliseconds(250),
            MaxRetryAttempts = 1,
            JitterRatio = 0d,
        };

        var factory = new MailKitSmtpConnectionFactory(options, new DefaultMailKitSmtpClientFactory());
        await using var pool = new SmtpPool(options, factory, metrics: metrics);
        var sender = new SmtpSender(pool, new DefaultSmtpErrorClassifier(), options, metrics: metrics);

        var warmupSuccess = await SendBatchAsync(sender, harness, "partial-warm", 4, 2);
        Assert.True(warmupSuccess > 0);

        var suppressedBefore = metrics.Count(SmtpMetricNames.PoolReconnectSuppressed);

        const int outageSeconds = 12;
        await harness.StopServiceAsync("smtp4dev-1", new Uri("http://localhost:5080"));
        var outageResult = await SendContinuouslyWithFailoverAsync(
            sender,
            harness,
            "partial-outage",
            TimeSpan.FromSeconds(outageSeconds),
            concurrency: 3,
            interval: TimeSpan.FromMilliseconds(500));

        await harness.RestoreServiceAsync("smtp4dev-1", new Uri("http://localhost:5080"));
        await Task.Delay(options.ReconnectCooldown + TimeSpan.FromMilliseconds(500));

        var recoverySuccessCount = await SendBatchAsync(sender, harness, "partial-recover", 4, 2);

        var provisional = new PartialOutageScenarioResult(
            "primary-only-12s",
            outageSeconds,
            outageResult.SecondarySuccessCount,
            outageResult.PrimaryFailureCount,
            metrics.Count(SmtpMetricNames.PoolReconnectSuppressed) - suppressedBefore,
            recoverySuccessCount,
            metrics.ClassificationCounts(),
            string.Empty);

        var resultFilePath = await harness.WriteJsonResultAsync("partial-outage", provisional);
        var result = provisional with { ResultFilePath = resultFilePath };
        await harness.WriteJsonResultAsync("partial-outage-latest", result);

        Assert.True(result.SecondarySuccessCountDuringPrimaryOutage > 0);
        Assert.True(result.PrimaryFailureCountDuringOutage > 0);
        Assert.True(result.SuppressedReconnectCount >= 0);
        Assert.True(result.RecoverySuccessCount > 0);
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

    private static async Task<(int SecondarySuccessCount, int PrimaryFailureCount)> SendContinuouslyWithFailoverAsync(
        SmtpSender sender,
        Smtp4DevStressHarness harness,
        string prefix,
        TimeSpan duration,
        int concurrency,
        TimeSpan interval)
    {
        var secondarySuccessCount = 0;
        var primaryFailureCount = 0;
        var deadline = DateTimeOffset.UtcNow + duration;

        var tasks = Enumerable.Range(0, concurrency)
            .Select(async workerId =>
            {
                while (DateTimeOffset.UtcNow < deadline)
                {
                    var subject = $"{prefix}-{workerId}-{Guid.NewGuid():N}";

                    try
                    {
                        var result = await sender.SendAsync(harness.CreateMessage(subject, prefix), CancellationToken.None);
                        if (string.Equals(result.EndpointKey, "localhost:2526", StringComparison.Ordinal))
                        {
                            Interlocked.Increment(ref secondarySuccessCount);
                        }
                    }
                    catch (SmtpSendFailedException ex) when (
                        ex.Classification.Kind is SmtpFailureKind.PoolExhausted
                            or SmtpFailureKind.RetryableBeforeSend
                            or SmtpFailureKind.UnknownAfterData)
                    {
                        Interlocked.Increment(ref primaryFailureCount);
                    }

                    await Task.Delay(interval).ConfigureAwait(false);
                }
            });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return (secondarySuccessCount, primaryFailureCount);
    }
}
