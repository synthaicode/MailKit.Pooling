using PooledMailKit.Abstractions;
using PooledMailKit.Options;

namespace PooledMailKit.Tests.TestDoubles;

internal sealed class FakeSmtpConnectionFactory : ISmtpConnectionFactory
{
    private readonly Queue<Func<ISmtpClientAdapter>> factories = [];

    public int CreateCalls { get; private set; }

    public List<string> RequestedHosts { get; } = [];

    public void Enqueue(ISmtpClientAdapter client)
    {
        factories.Enqueue(() => client);
    }

    public void EnqueueFailure(Exception exception)
    {
        factories.Enqueue(() => throw exception);
    }

    public Task<ISmtpClientAdapter> CreateAuthenticatedClientAsync(
        SmtpHostOptions host,
        CancellationToken cancellationToken)
    {
        CreateCalls++;
        RequestedHosts.Add(host.ToEndpointKey());

        if (factories.Count == 0)
        {
            throw new InvalidOperationException("No fake SMTP clients were enqueued.");
        }

        return Task.FromResult(factories.Dequeue().Invoke());
    }
}
