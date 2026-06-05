namespace MailKit.Pooling.Errors;

public sealed record SmtpFailureClassification(
    SmtpFailureKind Kind,
    SmtpSendStage Stage,
    bool IsRetryAllowed,
    bool ShouldDiscardConnection,
    string Reason)
{
    public bool IsTerminal => !IsRetryAllowed;
}
