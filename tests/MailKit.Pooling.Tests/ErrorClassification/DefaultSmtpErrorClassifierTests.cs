using PooledMailKit.Errors;

namespace PooledMailKit.Tests.ErrorClassification;

public sealed class DefaultSmtpErrorClassifierTests
{
    private readonly DefaultSmtpErrorClassifier classifier = new();

    [Fact]
    public void PoolExhausted_Is_Explicit_And_Not_Retryable()
    {
        var result = classifier.Classify(
            new SmtpPoolExhaustedException("acquire timed out"),
            SmtpSendStage.BeforeConnect);

        Assert.Equal(SmtpFailureKind.PoolExhausted, result.Kind);
        Assert.False(result.IsRetryAllowed);
        Assert.False(result.ShouldDiscardConnection);
    }

    [Fact]
    public void Timeout_Before_Data_Is_Retryable_Before_Send()
    {
        var result = classifier.Classify(
            new TimeoutException("connect timed out"),
            SmtpSendStage.Connected);

        Assert.Equal(SmtpFailureKind.RetryableBeforeSend, result.Kind);
        Assert.True(result.IsRetryAllowed);
        Assert.True(result.ShouldDiscardConnection);
    }

    [Fact]
    public void Timeout_After_Data_Is_UnknownAfterData()
    {
        var result = classifier.Classify(
            new TimeoutException("response timed out"),
            SmtpSendStage.DataStarted);

        Assert.Equal(SmtpFailureKind.UnknownAfterData, result.Kind);
        Assert.False(result.IsRetryAllowed);
        Assert.True(result.ShouldDiscardConnection);
    }

    [Fact]
    public void Protocol_Failure_Before_Data_Is_Retryable_On_New_Connection()
    {
        var result = classifier.Classify(
            new global::MailKit.Net.Smtp.SmtpProtocolException("bad reply"),
            SmtpSendStage.Authenticated);

        Assert.Equal(SmtpFailureKind.RetryableBeforeSend, result.Kind);
        Assert.True(result.IsRetryAllowed);
        Assert.True(result.ShouldDiscardConnection);
    }

    [Fact]
    public void Temporary_Smtp_Status_After_Data_Without_Known_Outcome_Is_UnknownAfterData()
    {
        var exception = new global::MailKit.Net.Smtp.SmtpCommandException(
            global::MailKit.Net.Smtp.SmtpErrorCode.UnexpectedStatusCode,
            global::MailKit.Net.Smtp.SmtpStatusCode.ErrorInProcessing,
            "temporary failure after DATA");

        var result = classifier.Classify(exception, SmtpSendStage.DataStarted);

        Assert.Equal(SmtpFailureKind.UnknownAfterData, result.Kind);
        Assert.False(result.IsRetryAllowed);
        Assert.True(result.ShouldDiscardConnection);
    }

    [Fact]
    public void Temporary_Rejection_Of_Data_Payload_Is_Definitive_And_Retryable()
    {
        var exception = new global::MailKit.Net.Smtp.SmtpCommandException(
            global::MailKit.Net.Smtp.SmtpErrorCode.MessageNotAccepted,
            global::MailKit.Net.Smtp.SmtpStatusCode.ErrorInProcessing,
            "451 temporary rejection of the DATA payload");

        var result = classifier.Classify(exception, SmtpSendStage.DataCompleted);

        Assert.Equal(SmtpFailureKind.RetryableTemporaryFailure, result.Kind);
        Assert.True(result.IsRetryAllowed);
        Assert.False(result.ShouldDiscardConnection);
    }

    [Fact]
    public void Unknown_Exception_Family_Is_Unclassified_And_Discards_Connection()
    {
        var result = classifier.Classify(
            new InvalidOperationException("bug-class failure"),
            SmtpSendStage.DataStarted);

        Assert.Equal(SmtpFailureKind.Unclassified, result.Kind);
        Assert.False(result.IsRetryAllowed);
        Assert.True(result.ShouldDiscardConnection);
    }

    [Fact]
    public void Permanent_Rejection_Of_Data_Payload_Is_Definitive_And_Not_Retryable()
    {
        var exception = new global::MailKit.Net.Smtp.SmtpCommandException(
            global::MailKit.Net.Smtp.SmtpErrorCode.MessageNotAccepted,
            global::MailKit.Net.Smtp.SmtpStatusCode.TransactionFailed,
            "554 transaction failed");

        var result = classifier.Classify(exception, SmtpSendStage.DataCompleted);

        Assert.Equal(SmtpFailureKind.PermanentFailure, result.Kind);
        Assert.False(result.IsRetryAllowed);
        Assert.False(result.ShouldDiscardConnection);
    }
}
