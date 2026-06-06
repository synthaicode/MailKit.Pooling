namespace MailKit.Pooling.Pooling;

internal sealed record SmtpPoolSnapshot(
    int TotalConnections,
    int IdleConnections,
    int LeasedConnections,
    int WaitingCallers,
    DateTimeOffset? NextCreationAllowedAt);
