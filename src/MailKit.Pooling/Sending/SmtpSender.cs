using MailKit.Pooling.Abstractions;
using MailKit.Pooling.Errors;
using MailKit.Pooling.Internal;
using MailKit.Pooling.Metrics;
using MailKit.Pooling.Options;
using MailKit.Pooling.Pooling;
using MailKit.Pooling.Retry;
using MimeKit;

namespace MailKit.Pooling.Sending;

internal sealed class SmtpSender : ISmtpSender
{
    private static readonly TimeSpan LeaseCleanupTimeout = TimeSpan.FromSeconds(1);
    private readonly SmtpPool pool;
    private readonly ISmtpErrorClassifier classifier;
    private readonly ISmtpPoolMetrics metrics;
    private readonly SmtpPoolOptions options;
    private readonly IClock clock;

    public SmtpSender(
        SmtpPool pool,
        ISmtpErrorClassifier classifier,
        SmtpPoolOptions options,
        IClock? clock = null,
        ISmtpPoolMetrics? metrics = null)
    {
        this.pool = pool ?? throw new ArgumentNullException(nameof(pool));
        this.classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.clock = clock ?? SystemClock.Instance;
        this.metrics = metrics ?? NoOpSmtpPoolMetrics.Instance;
    }

    public async Task<SmtpSendResult> SendAsync(MimeMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var attempts = 0;
        var sendStartedAt = clock.UtcNow;

        while (true)
        {
            attempts++;
            SmtpConnectionLease? lease = null;

            try
            {
                lease = await pool.AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
                await TimeoutExecution.ExecuteAsync(
                    token => lease.Client.SendAsync(message, token),
                    options.SmtpSendTimeout,
                    "send",
                    cancellationToken).ConfigureAwait(false);
                await lease.ReturnAsync(cancellationToken).ConfigureAwait(false);
                RecordSendDuration(sendStartedAt, clock.UtcNow, lease.EndpointKey);
                metrics.Record(new SmtpPoolMetricEvent(
                    SmtpMetricNames.SendSuccessCount,
                    SmtpMetricInstrumentKind.Counter,
                    1,
                    lease.EndpointKey));

                return new SmtpSendResult(
                    lease.ConnectionId,
                    lease.EndpointKey,
                    clock.UtcNow,
                    attempts);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (lease is not null)
                {
                    await CompleteLeaseBestEffortAsync(lease, shouldDiscardConnection: true).ConfigureAwait(false);
                }

                throw;
            }
            catch (Exception exception)
            {
                var stage = ResolveStage(exception, lease is null);
                var classificationTarget = ResolveClassificationTarget(exception);
                var classification = classifier.Classify(classificationTarget, stage);
                metrics.Record(new SmtpPoolMetricEvent(
                    SmtpMetricNames.SendClassificationCount,
                    SmtpMetricInstrumentKind.Counter,
                    1,
                    lease?.EndpointKey,
                    FailureKind: classification.Kind.ToString(),
                    Stage: classification.Stage.ToString()));

                if (lease is not null)
                {
                    if (classification.ShouldDiscardConnection)
                    {
                        await CompleteLeaseBestEffortAsync(lease, shouldDiscardConnection: true).ConfigureAwait(false);
                    }
                    else
                    {
                        await CompleteLeaseBestEffortAsync(lease, shouldDiscardConnection: false).ConfigureAwait(false);
                    }
                }

                if (ShouldRetry(classification, attempts))
                {
                    metrics.Record(new SmtpPoolMetricEvent(
                        SmtpMetricNames.SendRetryCount,
                        SmtpMetricInstrumentKind.Counter,
                        1,
                        lease?.EndpointKey,
                        FailureKind: classification.Kind.ToString(),
                        Stage: classification.Stage.ToString()));
                    var retryDelay = RetryDelayCalculator.CalculateNextDelay(
                        attempts,
                        options.RetryBaseDelay,
                        options.UseExponentialBackoff,
                        options.JitterRatio);

                    if (retryDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                RecordSendDuration(sendStartedAt, clock.UtcNow, lease?.EndpointKey);
                metrics.Record(new SmtpPoolMetricEvent(
                    SmtpMetricNames.SendFailedCount,
                    SmtpMetricInstrumentKind.Counter,
                    1,
                    lease?.EndpointKey,
                    FailureKind: classification.Kind.ToString(),
                    Stage: classification.Stage.ToString()));
                if (classification.Kind == SmtpFailureKind.UnknownAfterData)
                {
                    metrics.Record(new SmtpPoolMetricEvent(
                        SmtpMetricNames.SendAmbiguousCount,
                        SmtpMetricInstrumentKind.Counter,
                        1,
                        lease?.EndpointKey,
                        FailureKind: classification.Kind.ToString(),
                        Stage: classification.Stage.ToString()));
                }
                else
                {
                    metrics.Record(new SmtpPoolMetricEvent(
                        SmtpMetricNames.SendDefinitelyNotAcceptedCount,
                        SmtpMetricInstrumentKind.Counter,
                        1,
                        lease?.EndpointKey,
                        FailureKind: classification.Kind.ToString(),
                        Stage: classification.Stage.ToString()));
                }
                throw new SmtpSendFailedException(
                    $"SMTP send failed with classification '{classification.Kind}' after {attempts} attempt(s).",
                    classification,
                    attempts,
                    exception);
            }
        }
    }

    private bool ShouldRetry(SmtpFailureClassification classification, int attempts)
    {
        return classification.IsRetryAllowed
            && attempts <= options.MaxRetryAttempts;
    }

    private static async Task CompleteLeaseBestEffortAsync(
        SmtpConnectionLease lease,
        bool shouldDiscardConnection)
    {
        try
        {
            var completionTask = shouldDiscardConnection
                ? lease.InvalidateAsync(CancellationToken.None).AsTask()
                : lease.ReturnAsync(CancellationToken.None).AsTask();
            await completionTask.WaitAsync(LeaseCleanupTimeout).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the original send outcome even when cleanup is slow or broken.
        }
    }

    private static SmtpSendStage ResolveStage(Exception exception, bool failedBeforeLease)
    {
        if (failedBeforeLease)
        {
            return SmtpSendStage.BeforeConnect;
        }

        if (exception is ISmtpStageAwareException stageAware)
        {
            return stageAware.Stage;
        }

        // When SendAsync fails without stage detail, prefer a conservative ambiguity boundary.
        return SmtpSendStage.DataStarted;
    }

    private static Exception ResolveClassificationTarget(Exception exception)
    {
        if (exception is SmtpStageAwareException { InnerException: not null } stageAware)
        {
            return stageAware.InnerException!;
        }

        return exception;
    }

    private void RecordSendDuration(DateTimeOffset startedAt, DateTimeOffset completedAt, string? smtpHost)
    {
        var duration = completedAt - startedAt;
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        metrics.Record(new SmtpPoolMetricEvent(
            SmtpMetricNames.SendDuration,
            SmtpMetricInstrumentKind.Histogram,
            duration.TotalMilliseconds,
            smtpHost));
    }
}
