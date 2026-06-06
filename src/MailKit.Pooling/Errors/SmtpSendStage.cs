namespace MailKit.Pooling.Errors;

/// <summary>
/// Describes the SMTP stage reached by a send operation.
/// </summary>
public enum SmtpSendStage
{
    /// <summary>
    /// No SMTP connection has been established yet.
    /// </summary>
    BeforeConnect,

    /// <summary>
    /// The TCP and SMTP session is connected.
    /// </summary>
    Connected,

    /// <summary>
    /// SMTP authentication has completed.
    /// </summary>
    Authenticated,

    /// <summary>
    /// Envelope commands have started.
    /// </summary>
    EnvelopeStarted,

    /// <summary>
    /// The SMTP DATA phase has started.
    /// </summary>
    DataStarted,

    /// <summary>
    /// The SMTP DATA phase completed locally.
    /// </summary>
    DataCompleted,

    /// <summary>
    /// The server accepted the message.
    /// </summary>
    ServerAccepted,
}
