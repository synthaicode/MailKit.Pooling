using System.Security.Authentication;
using MailKit.Net.Smtp;
using PooledMailKit.Errors;

namespace PooledMailKit.ComponentTests.ErrorClassification;

public sealed class MailKitExceptionClassificationTests
{
    private readonly DefaultSmtpErrorClassifier classifier = new();

    [Fact]
    public void Authentication_Exception_Is_Permanent()
    {
        var result = classifier.Classify(
            new AuthenticationException("bad credentials"),
            SmtpSendStage.Connected);

        Assert.Equal(SmtpFailureKind.PermanentFailure, result.Kind);
        Assert.False(result.IsRetryAllowed);
        Assert.True(result.ShouldDiscardConnection);
    }

    [Fact]
    public void Authentication_Status_Code_Is_Permanent()
    {
        var ex = new SmtpCommandException(
            SmtpErrorCode.UnexpectedStatusCode,
            SmtpStatusCode.AuthenticationInvalidCredentials,
            "invalid credentials");

        var result = classifier.Classify(ex, SmtpSendStage.Connected);

        Assert.Equal(SmtpFailureKind.PermanentFailure, result.Kind);
        Assert.False(result.IsRetryAllowed);
    }

    [Fact]
    public void Recipient_Rejection_Is_Permanent()
    {
        var ex = new SmtpCommandException(
            SmtpErrorCode.RecipientNotAccepted,
            SmtpStatusCode.MailboxUnavailable,
            "recipient rejected");

        var result = classifier.Classify(ex, SmtpSendStage.EnvelopeStarted);

        Assert.Equal(SmtpFailureKind.PermanentFailure, result.Kind);
        Assert.False(result.IsRetryAllowed);
        Assert.True(result.ShouldDiscardConnection);
    }

    [Fact]
    public void Smtp_4xx_Is_Retryable_Before_Data()
    {
        var ex = new SmtpCommandException(
            SmtpErrorCode.UnexpectedStatusCode,
            SmtpStatusCode.ErrorInProcessing,
            "temporary processing failure");

        var result = classifier.Classify(ex, SmtpSendStage.EnvelopeStarted);

        Assert.Equal(SmtpFailureKind.RetryableTemporaryFailure, result.Kind);
        Assert.True(result.IsRetryAllowed);
        Assert.True(result.ShouldDiscardConnection);
    }

    [Fact]
    public void Smtp_4xx_After_Data_Without_Known_Outcome_Is_UnknownAfterData()
    {
        var ex = new SmtpCommandException(
            SmtpErrorCode.UnexpectedStatusCode,
            SmtpStatusCode.ErrorInProcessing,
            "temporary processing failure");

        var result = classifier.Classify(ex, SmtpSendStage.DataStarted);

        Assert.Equal(SmtpFailureKind.UnknownAfterData, result.Kind);
        Assert.False(result.IsRetryAllowed);
        Assert.True(result.ShouldDiscardConnection);
    }

    [Fact]
    public void Smtp_4xx_Reply_To_Data_Payload_Is_Definitive_And_Retryable()
    {
        var ex = new SmtpCommandException(
            SmtpErrorCode.MessageNotAccepted,
            SmtpStatusCode.ErrorInProcessing,
            "temporary processing failure");

        var result = classifier.Classify(ex, SmtpSendStage.DataCompleted);

        Assert.Equal(SmtpFailureKind.RetryableTemporaryFailure, result.Kind);
        Assert.True(result.IsRetryAllowed);
        Assert.False(result.ShouldDiscardConnection);
    }

    [Fact]
    public void Smtp_5xx_Is_Permanent()
    {
        var ex = new SmtpCommandException(
            SmtpErrorCode.UnexpectedStatusCode,
            SmtpStatusCode.TransactionFailed,
            "transaction failed");

        var result = classifier.Classify(ex, SmtpSendStage.EnvelopeStarted);

        Assert.Equal(SmtpFailureKind.PermanentFailure, result.Kind);
        Assert.False(result.IsRetryAllowed);
        Assert.True(result.ShouldDiscardConnection);
    }

    [Fact]
    public void Smtp_5xx_Reply_To_Data_Payload_Is_Permanent_And_Keeps_Connection()
    {
        var ex = new SmtpCommandException(
            SmtpErrorCode.MessageNotAccepted,
            SmtpStatusCode.TransactionFailed,
            "transaction failed");

        var result = classifier.Classify(ex, SmtpSendStage.DataCompleted);

        Assert.Equal(SmtpFailureKind.PermanentFailure, result.Kind);
        Assert.False(result.IsRetryAllowed);
        Assert.False(result.ShouldDiscardConnection);
    }

    [Fact]
    public void Protocol_Exception_Before_Data_Is_Retryable()
    {
        var result = classifier.Classify(
            new SmtpProtocolException("unexpected disconnect"),
            SmtpSendStage.Connected);

        Assert.Equal(SmtpFailureKind.RetryableBeforeSend, result.Kind);
        Assert.True(result.IsRetryAllowed);
        Assert.True(result.ShouldDiscardConnection);
    }
}
