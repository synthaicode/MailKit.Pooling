namespace MailKit.Pooling.Abstractions;

internal interface IClock
{
    DateTimeOffset UtcNow { get; }
    Task Delay(TimeSpan delay, CancellationToken cancellationToken);
}
