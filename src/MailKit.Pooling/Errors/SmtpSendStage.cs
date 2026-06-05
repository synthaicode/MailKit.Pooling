namespace MailKit.Pooling.Errors;

public enum SmtpSendStage
{
    BeforeConnect,
    Connected,
    Authenticated,
    EnvelopeStarted,
    DataStarted,
    DataCompleted,
    ServerAccepted,
}
