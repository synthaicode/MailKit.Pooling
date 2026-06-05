using MailKit.Pooling.Errors;

namespace MailKit.Pooling.Abstractions;

public interface ISmtpErrorClassifier
{
    SmtpFailureClassification Classify(Exception exception, SmtpSendStage stage);
}
