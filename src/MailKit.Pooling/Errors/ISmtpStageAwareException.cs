namespace MailKit.Pooling.Errors;

internal interface ISmtpStageAwareException
{
    SmtpSendStage Stage { get; }
}
