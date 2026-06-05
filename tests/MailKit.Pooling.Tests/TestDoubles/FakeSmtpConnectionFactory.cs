using MailKit.Pooling.Abstractions;

namespace MailKit.Pooling.Tests.TestDoubles;

internal sealed class FakeSmtpConnectionFactory : ISmtpConnectionFactory
{
    private readonly Queue<Func<ISmtpClientAdapter>> factories = [];

    public int CreateCalls { get; private set; }

    public void Enqueue(ISmtpClientAdapter client)
    {
        factories.Enqueue(() => client);
    }

    public void EnqueueFailure(Exception exception)
    {
        factories.Enqueue(() => throw exception);
    }

    public Task<ISmtpClientAdapter> CreateAuthenticatedClientAsync(CancellationToken cancellationToken)
    {
        CreateCalls++;

        if (factories.Count == 0)
        {
            throw new InvalidOperationException("No fake SMTP clients were enqueued.");
        }

        return Task.FromResult(factories.Dequeue().Invoke());
    }
}
