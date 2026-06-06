using MailKit.Pooling.Errors;

namespace MailKit.Pooling.Abstractions;

internal interface ISmtpErrorClassifier
{
    SmtpFailureClassification Classify(Exception exception, SmtpSendStage stage);
}
