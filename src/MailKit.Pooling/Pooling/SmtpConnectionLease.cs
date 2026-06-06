using MailKit.Pooling.Abstractions;

namespace MailKit.Pooling.Pooling;

internal sealed class SmtpConnectionLease : IAsyncDisposable
{
    private readonly SmtpPool pool;
    private int completed;

    internal SmtpConnectionLease(
        SmtpPool pool,
        Guid connectionId,
        ISmtpClientAdapter client,
        DateTimeOffset leasedAtUtc)
    {
        this.pool = pool;
        ConnectionId = connectionId;
        Client = client;
        EndpointKey = client.EndpointKey;
        LeasedAtUtc = leasedAtUtc;
    }

    public Guid ConnectionId { get; }

    public string EndpointKey { get; }

    public ISmtpClientAdapter Client { get; }

    public DateTimeOffset LeasedAtUtc { get; }

    public ValueTask ReturnAsync(CancellationToken cancellationToken = default)
    {
        return CompleteAsync(isReusable: true, cancellationToken);
    }

    public ValueTask InvalidateAsync(CancellationToken cancellationToken = default)
    {
        return CompleteAsync(isReusable: false, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        return CompleteAsync(isReusable: true, CancellationToken.None);
    }

    private ValueTask CompleteAsync(bool isReusable, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref completed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        return pool.ReturnLeaseAsync(ConnectionId, isReusable, LeasedAtUtc, cancellationToken);
    }
}
