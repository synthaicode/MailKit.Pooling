namespace MailKit.Pooling.Errors;

public interface ISmtpStageAwareException
{
    SmtpSendStage Stage { get; }
}
