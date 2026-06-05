namespace MailKit.Pooling.Pooling;

public sealed record SmtpPoolSnapshot(
    int TotalConnections,
    int IdleConnections,
    int LeasedConnections,
    int WaitingCallers,
    DateTimeOffset? NextCreationAllowedAt);
