namespace PooledMailKit.Errors;

internal interface ISmtpStageAwareException
{
    SmtpSendStage Stage { get; }
}
