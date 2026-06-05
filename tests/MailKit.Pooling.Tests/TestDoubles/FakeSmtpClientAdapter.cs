using MailKit.Pooling.Abstractions;

namespace MailKit.Pooling.Tests.TestDoubles;

internal sealed class FakeSmtpClientAdapter : ISmtpClientAdapter
{
    public Func<CancellationToken, Task>? OnNoOpAsync { get; set; }

    public Func<object, CancellationToken, Task>? OnSendAsync { get; set; }

    public bool IsConnected { get; set; } = true;

    public bool IsAuthenticated { get; set; } = true;

    public string EndpointKey { get; init; } = "smtp://primary";

    public int SendCalls { get; private set; }

    public int NoOpCalls { get; private set; }

    public int DisconnectCalls { get; private set; }

    public int DisposeCalls { get; private set; }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task AuthenticateAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task NoOpAsync(CancellationToken cancellationToken)
    {
        NoOpCalls++;
        return OnNoOpAsync?.Invoke(cancellationToken) ?? Task.CompletedTask;
    }

    public Task SendAsync(object message, CancellationToken cancellationToken)
    {
        SendCalls++;
        return OnSendAsync?.Invoke(message, cancellationToken) ?? Task.CompletedTask;
    }

    public Task DisconnectAsync(bool quit, CancellationToken cancellationToken)
    {
        DisconnectCalls++;
        IsConnected = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DisposeCalls++;
        return ValueTask.CompletedTask;
    }
}
