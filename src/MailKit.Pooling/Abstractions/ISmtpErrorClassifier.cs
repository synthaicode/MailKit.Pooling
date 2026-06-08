using PooledMailKit.Errors;

namespace PooledMailKit.Abstractions;

internal interface ISmtpErrorClassifier
{
    SmtpFailureClassification Classify(Exception exception, SmtpSendStage stage);
}
