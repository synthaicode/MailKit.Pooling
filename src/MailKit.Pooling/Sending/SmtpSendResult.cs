namespace PooledMailKit.Sending;

/// <summary>
/// Represents a successful SMTP send result.
/// </summary>
public sealed class SmtpSendResult
{
    /// <summary>
    /// Initializes a new successful send result.
    /// </summary>
    /// <param name="connectionId">The pooled connection identifier used for the send.</param>
    /// <param name="endpointKey">The logical SMTP endpoint key.</param>
    /// <param name="acceptedAtUtc">The UTC timestamp when the send completed.</param>
    /// <param name="attempts">The number of attempts used for the successful send.</param>
    public SmtpSendResult(
        Guid connectionId,
        string endpointKey,
        DateTimeOffset acceptedAtUtc,
        int attempts)
    {
        ConnectionId = connectionId;
        EndpointKey = endpointKey;
        AcceptedAtUtc = acceptedAtUtc;
        Attempts = attempts;
    }

    /// <summary>
    /// Gets the pooled connection identifier used for the send.
    /// </summary>
    public Guid ConnectionId { get; }

    /// <summary>
    /// Gets the logical SMTP endpoint key used for the send.
    /// </summary>
    public string EndpointKey { get; }

    /// <summary>
    /// Gets the UTC timestamp when the send completed successfully.
    /// </summary>
    public DateTimeOffset AcceptedAtUtc { get; }

    /// <summary>
    /// Gets the number of attempts used for the successful send.
    /// </summary>
    public int Attempts { get; }
}
