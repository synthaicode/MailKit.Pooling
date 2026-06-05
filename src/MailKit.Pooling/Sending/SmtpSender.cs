using MailKit.Pooling.Abstractions;
using MailKit.Pooling.Errors;
using MailKit.Pooling.Internal;
using MailKit.Pooling.Metrics;
using MailKit.Pooling.Options;
using MailKit.Pooling.Pooling;
using MailKit.Pooling.Retry;
using MimeKit;

namespace MailKit.Pooling.Sending;

public sealed class SmtpSender : ISmtpSender
{
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

        while (true)
        {
            attempts++;
            SmtpConnectionLease? lease = null;

            try
            {
                lease = await pool.AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
                await TimeoutExecution.ExecuteAsync(
                    token => lease.Client.SendAsync(message, token),
                    options.SendTimeout,
                    "send",
                    cancellationToken).ConfigureAwait(false);
                await lease.ReturnAsync(cancellationToken).ConfigureAwait(false);
                metrics.Record(new SmtpPoolMetricEvent(SmtpMetricNames.SendSuccesses, 1, lease.EndpointKey));

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
                    await lease.InvalidateAsync(CancellationToken.None).ConfigureAwait(false);
                }

                throw;
            }
            catch (Exception exception)
            {
                var stage = ResolveStage(exception, lease is null);
                var classificationTarget = ResolveClassificationTarget(exception);
                var classification = classifier.Classify(classificationTarget, stage);
                metrics.Record(new SmtpPoolMetricEvent(
                    SmtpMetricNames.ErrorClassifications,
                    1,
                    lease?.EndpointKey,
                    $"{classification.Kind}:{classification.Stage}"));

                if (lease is not null)
                {
                    if (classification.ShouldDiscardConnection)
                    {
                        await lease.InvalidateAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await lease.ReturnAsync(cancellationToken).ConfigureAwait(false);
                    }
                }

                if (ShouldRetry(classification, attempts))
                {
                    metrics.Record(new SmtpPoolMetricEvent(
                        SmtpMetricNames.Retries,
                        1,
                        lease?.EndpointKey,
                        classification.Kind.ToString()));
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

                metrics.Record(new SmtpPoolMetricEvent(
                    SmtpMetricNames.SendFailures,
                    1,
                    lease?.EndpointKey,
                    classification.Kind.ToString()));
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
}
