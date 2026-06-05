using System.Diagnostics;
using MailKit.Net.Smtp;
using MailKit.Pooling.Errors;
using MailKit.Pooling.MailKit;
using MailKit.Pooling.Metrics;
using MailKit.Pooling.Options;
using MailKit.Pooling.Pooling;
using MailKit.Pooling.Sending;
using MailKit.Security;
using MailKit.Pooling.StressTests.Helpers;

namespace MailKit.Pooling.StressTests.ResourceComparison;

public sealed class NaiveVsPooledComparisonTests
{
    [ManualStressFact]
    [Trait("Category", "Stress")]
    public async Task Compare_Naive_And_Pooled_Smtp_Usage()
    {
        await using var harness = await Smtp4DevStressHarness.AcquireAsync();
        await harness.EnsureStartedAsync();

        var totalSends = 40;
        var concurrency = 8;
        var timeWaitObserver = new WindowsTimeWaitObserver();

        var naive = await RunNaiveAsync(harness, totalSends, concurrency, timeWaitObserver);
        var pooled = await RunPooledAsync(harness, totalSends, concurrency, timeWaitObserver);
        var provisional = new ComparisonScenarioResult(totalSends, concurrency, string.Empty, naive, pooled);
        var resultFilePath = await harness.WriteJsonResultAsync("naive-vs-pooled", provisional);
        var result = provisional with { ResultFilePath = resultFilePath };
        await harness.WriteJsonResultAsync("naive-vs-pooled-latest", result);

        Assert.Equal(totalSends, naive.SuccessCount + naive.FailureCount);
        Assert.Equal(totalSends, pooled.SuccessCount + pooled.FailureCount);
        Assert.True(pooled.ConnectionCreationCount <= naive.ConnectionCreationCount);
    }

    private static async Task<SendRunResult> RunNaiveAsync(
        Smtp4DevStressHarness harness,
        int totalSends,
        int concurrency,
        ITimeWaitObserver timeWaitObserver)
    {
        var before = await timeWaitObserver.ObserveAsync(2525);
        var stopwatch = Stopwatch.StartNew();
        var failures = 0;
        var successes = 0;

        var subjects = Enumerable.Range(0, totalSends)
            .Select(_ => $"mailkit-naive-{Guid.NewGuid():N}")
            .ToArray();

        await Parallel.ForEachAsync(
            subjects,
            new ParallelOptions { MaxDegreeOfParallelism = concurrency },
            async (subject, cancellationToken) =>
            {
                try
                {
                    using var client = new SmtpClient();
                    await client.ConnectAsync("localhost", 2525, SecureSocketOptions.None, cancellationToken);
                    await client.SendAsync(harness.CreateMessage(subject, "naive body"), cancellationToken);
                    await client.DisconnectAsync(true, cancellationToken);
                    Interlocked.Increment(ref successes);
                }
                catch
                {
                    Interlocked.Increment(ref failures);
                }
            });

        stopwatch.Stop();
        await Task.Delay(TimeSpan.FromSeconds(1));
        var after = await timeWaitObserver.ObserveAsync(2525);

        return new SendRunResult(
            "naive",
            totalSends,
            successes,
            failures,
            stopwatch.ElapsedMilliseconds,
            totalSends,
            ActiveConnections: null,
            IdleConnections: null,
            ErrorClassifications: null,
            new TimeWaitDeltaResult(before.IsAvailable && after.IsAvailable, before.Count, after.Count, before.Source));
    }

    private static async Task<SendRunResult> RunPooledAsync(
        Smtp4DevStressHarness harness,
        int totalSends,
        int concurrency,
        ITimeWaitObserver timeWaitObserver)
    {
        var options = new SmtpPoolOptions
        {
            Host = new SmtpHostOptions
            {
                Host = "localhost",
                Port = 2525,
                SecureSocketOptions = "None",
            },
            MaxPoolSize = concurrency,
            AcquireTimeout = TimeSpan.FromSeconds(10),
            ReconnectCooldown = TimeSpan.FromMilliseconds(500),
            RetryBaseDelay = TimeSpan.FromMilliseconds(250),
            MaxRetryAttempts = 1,
            JitterRatio = 0d,
        };

        var metrics = new InMemorySmtpPoolMetrics();
        var factory = new MailKitSmtpConnectionFactory(options, new DefaultMailKitSmtpClientFactory());
        await using var pool = new SmtpPool(options, factory, metrics: metrics);
        var sender = new SmtpSender(pool, new DefaultSmtpErrorClassifier(), options, metrics: metrics);

        var before = await timeWaitObserver.ObserveAsync(2525);
        var stopwatch = Stopwatch.StartNew();
        var failures = 0;
        var successes = 0;

        var subjects = Enumerable.Range(0, totalSends)
            .Select(_ => $"mailkit-pooled-{Guid.NewGuid():N}")
            .ToArray();

        await Parallel.ForEachAsync(
            subjects,
            new ParallelOptions { MaxDegreeOfParallelism = concurrency },
            async (subject, cancellationToken) =>
            {
                try
                {
                    await sender.SendAsync(harness.CreateMessage(subject, "pooled body"), cancellationToken);
                    Interlocked.Increment(ref successes);
                }
                catch
                {
                    Interlocked.Increment(ref failures);
                }
            });

        stopwatch.Stop();
        await Task.Delay(TimeSpan.FromSeconds(1));
        var after = await timeWaitObserver.ObserveAsync(2525);
        var snapshot = pool.GetSnapshot();

        return new SendRunResult(
            "pooled",
            totalSends,
            successes,
            failures,
            stopwatch.ElapsedMilliseconds,
            (int) metrics.Sum(SmtpMetricNames.ConnectionsCreated),
            snapshot.LeasedConnections,
            snapshot.IdleConnections,
            metrics.ClassificationCounts(),
            new TimeWaitDeltaResult(before.IsAvailable && after.IsAvailable, before.Count, after.Count, before.Source));
    }
}
