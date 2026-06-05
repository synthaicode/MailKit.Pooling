namespace MailKit.Pooling.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
    Task Delay(TimeSpan delay, CancellationToken cancellationToken);
}
