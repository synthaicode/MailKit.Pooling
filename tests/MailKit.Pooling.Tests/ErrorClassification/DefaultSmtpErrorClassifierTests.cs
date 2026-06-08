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
    public void HostUnavailable_Stays_Explicit()
    {
        var result = classifier.Classify(
            new HostUnavailableException("cooldown active"),
            SmtpSendStage.BeforeConnect);

        Assert.Equal(SmtpFailureKind.HostUnavailable, result.Kind);
        Assert.False(result.IsRetryAllowed);
        Assert.False(result.ShouldDiscardConnection);
    }
}
