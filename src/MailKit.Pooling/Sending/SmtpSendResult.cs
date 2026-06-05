namespace MailKit.Pooling.Sending;

public sealed record SmtpSendResult(
    Guid ConnectionId,
    string EndpointKey,
    DateTimeOffset AcceptedAtUtc,
    int Attempts);
