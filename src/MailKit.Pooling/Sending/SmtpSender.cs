using MailKit.Net.Smtp;
using PooledMailKit.Abstractions;
using PooledMailKit.Errors;
using PooledMailKit.Internal;
using PooledMailKit.Metrics;
using PooledMailKit.Options;
using PooledMailKit.Pooling;
using PooledMailKit.Retry;
using MimeKit;

namespace PooledMailKit.Sending;

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

                // The server accepted the message; lease cleanup must not turn the
                // accepted send into a reported failure or cancellation.
                await CompleteLeaseBestEffortAsync(lease, shouldDiscardConnection: false).ConfigureAwait(false);
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

                if (classification.Kind == SmtpFailureKind.Unclassified)
                {
                    // Not an SMTP outcome: bug-class and foreign exceptions
                    // surface raw after pool hygiene instead of masquerading
                    // as a classified send failure.
                    throw;
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
                        await clock.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
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

    private bool ShouldRetry(SmtpFailureClassification classification, int completedAttempts)
    {
        return classification.IsRetryAllowed
            && completedAttempts <= options.MaxRetryAttempts;
    }

    private static async Task CompleteLeaseBestEffortAsync(
        SmtpConnectionLease lease,
        bool shouldDiscardConnection)
    {
        var completionTask = shouldDiscardConnection
            ? lease.InvalidateAsync(CancellationToken.None).AsTask()
            : lease.ReturnAsync(CancellationToken.None).AsTask();

        try
        {
            await completionTask.WaitAsync(LeaseCleanupTimeout).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Intentional swallow: lease cleanup must never turn an already-determined send outcome into a different failure.
        catch
        {
            // Preserve the original send outcome even when cleanup is slow or broken.
            // Observe the abandoned task so a late fault never becomes unobserved.
            _ = completionTask.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
#pragma warning restore CA1031
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

        if (exception is SmtpCommandException commandException)
        {
            return ResolveCommandStage(commandException);
        }

        // When SendAsync fails without stage detail, prefer a conservative ambiguity boundary.
        return SmtpSendStage.DataStarted;
    }

    private static SmtpSendStage ResolveCommandStage(SmtpCommandException exception)
    {
        // MailKit reports which SMTP command was rejected, which pins the stage:
        // MAIL FROM / RCPT TO failures happen before DATA, and MessageNotAccepted
        // is the server's reply to the completed DATA payload.
        return exception.ErrorCode switch
        {
            SmtpErrorCode.SenderNotAccepted or SmtpErrorCode.RecipientNotAccepted => SmtpSendStage.EnvelopeStarted,
            SmtpErrorCode.MessageNotAccepted => SmtpSendStage.DataCompleted,
            _ => SmtpSendStage.DataStarted,
        };
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
