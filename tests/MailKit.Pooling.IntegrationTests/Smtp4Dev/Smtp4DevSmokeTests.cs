using System.Net.Http.Json;
using System.Diagnostics;
using MailKit.Pooling.Errors;
using MailKit.Pooling.MailKit;
using MailKit.Pooling.Options;
using MailKit.Pooling.Pooling;
using MailKit.Pooling.Sending;
using MimeKit;
using Xunit.Sdk;

namespace MailKit.Pooling.IntegrationTests.Smtp4Dev;

public sealed class Smtp4DevSmokeTests
{
    private static readonly Uri Smtp4DevApiBaseAddress = new("http://localhost:5080");
    private static readonly string ComposeFilePath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docker", "compose.smtp.yml"));

    [Fact]
    public async Task SendAsync_Delivers_Message_To_Smtp4Dev()
    {
        await using var testLock = await Smtp4DevTestLock.AcquireAsync();
        await EnsureSmtp4DevStartedAsync();

        var subject = $"mailkit-pooling-integration-{Guid.NewGuid():N}";
        var options = CreateOptions(maxPoolSize: 8);

        var factory = new MailKitSmtpConnectionFactory(options, new DefaultMailKitSmtpClientFactory());
        await using var pool = new SmtpPool(options, factory);
        var sender = new SmtpSender(pool, new DefaultSmtpErrorClassifier(), options);

        var result = await sender.SendAsync(CreateMessage(subject));

        Assert.Equal(1, result.Attempts);

        var delivered = await WaitForMessageAsync(subject, TimeSpan.FromSeconds(5));
        Assert.True(delivered, $"smtp4dev did not expose message '{subject}' via API within the timeout.");
    }

    [Fact]
    public async Task Repeated_Sends_Reuse_A_Single_Connection()
    {
        await using var testLock = await Smtp4DevTestLock.AcquireAsync();
        await EnsureSmtp4DevStartedAsync();

        var options = CreateOptions(maxPoolSize: 1);
        var factory = new MailKitSmtpConnectionFactory(options, new DefaultMailKitSmtpClientFactory());
        await using var pool = new SmtpPool(options, factory);
        var sender = new SmtpSender(pool, new DefaultSmtpErrorClassifier(), options);

        var firstSubject = $"mailkit-pooling-reuse-{Guid.NewGuid():N}";
        var secondSubject = $"mailkit-pooling-reuse-{Guid.NewGuid():N}";

        var firstResult = await sender.SendAsync(CreateMessage(firstSubject));
        var secondResult = await sender.SendAsync(CreateMessage(secondSubject));

        Assert.Equal(firstResult.ConnectionId, secondResult.ConnectionId);
        Assert.Equal(1, firstResult.Attempts);
        Assert.Equal(1, secondResult.Attempts);

        var deliveredFirst = await WaitForMessageAsync(firstSubject, TimeSpan.FromSeconds(5));
        var deliveredSecond = await WaitForMessageAsync(secondSubject, TimeSpan.FromSeconds(5));
        Assert.True(deliveredFirst);
        Assert.True(deliveredSecond);
    }

    [Fact]
    public async Task Concurrent_Sends_Stay_Within_Pool_Bounds()
    {
        await using var testLock = await Smtp4DevTestLock.AcquireAsync();
        await EnsureSmtp4DevStartedAsync();

        var options = CreateOptions(maxPoolSize: 2);
        var factory = new MailKitSmtpConnectionFactory(options, new DefaultMailKitSmtpClientFactory());
        await using var pool = new SmtpPool(options, factory);
        var sender = new SmtpSender(pool, new DefaultSmtpErrorClassifier(), options);

        var subjects = Enumerable.Range(0, 6)
            .Select(_ => $"mailkit-pooling-concurrency-{Guid.NewGuid():N}")
            .ToArray();

        var tasks = subjects
            .Select(subject => sender.SendAsync(CreateMessage(subject)))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        var snapshot = pool.GetSnapshot();

        Assert.All(results, result => Assert.Equal(1, result.Attempts));
        Assert.True(results.Select(result => result.ConnectionId).Distinct().Count() <= options.MaxPoolSize);
        Assert.True(snapshot.TotalConnections <= options.MaxPoolSize);
        Assert.True(snapshot.LeasedConnections == 0);

        foreach (var subject in subjects)
        {
            Assert.True(await WaitForMessageAsync(subject, TimeSpan.FromSeconds(5)), $"Message '{subject}' was not found in smtp4dev.");
        }
    }

    [Fact]
    public async Task Server_Stop_Causes_Discard_And_Retryable_Failure_Classification()
    {
        await using var testLock = await Smtp4DevTestLock.AcquireAsync();
        await EnsureSmtp4DevStartedAsync();

        var options = CreateOptions(
            maxPoolSize: 1,
            reconnectCooldown: TimeSpan.FromMilliseconds(300),
            keepAliveInterval: TimeSpan.Zero,
            acquireTimeout: TimeSpan.FromSeconds(2),
            connectTimeout: TimeSpan.FromMilliseconds(200));
        var factory = new MailKitSmtpConnectionFactory(options, new DefaultMailKitSmtpClientFactory());
        await using var pool = new SmtpPool(options, factory);
        var sender = new SmtpSender(pool, new DefaultSmtpErrorClassifier(), options);

        await sender.SendAsync(CreateMessage($"mailkit-pooling-warm-{Guid.NewGuid():N}"));

        try
        {
            await StopSmtp4DevAsync();
            await WaitForSmtp4DevAvailabilityAsync(isAvailable: false, TimeSpan.FromSeconds(10));

            var exception = await Assert.ThrowsAsync<SmtpSendFailedException>(
                () => sender.SendAsync(CreateMessage($"mailkit-pooling-down-{Guid.NewGuid():N}")));

            var snapshot = pool.GetSnapshot();
            Assert.True(
                exception.Classification.Kind is SmtpFailureKind.ConnectionCorrupted or SmtpFailureKind.RetryableBeforeSend);
            Assert.True(exception.Classification.ShouldDiscardConnection);
            Assert.True(snapshot.TotalConnections <= 1);
            Assert.Equal(0, snapshot.IdleConnections);
        }
        finally
        {
            await StartSmtp4DevAsync();
            await WaitForSmtp4DevAvailabilityAsync(isAvailable: true, TimeSpan.FromSeconds(15));
        }
    }

    [Fact]
    public async Task Pool_Recovers_After_Cooldown_When_Server_Returns()
    {
        await using var testLock = await Smtp4DevTestLock.AcquireAsync();
        await EnsureSmtp4DevStartedAsync();

        var options = CreateOptions(
            maxPoolSize: 1,
            reconnectCooldown: TimeSpan.FromMilliseconds(400),
            keepAliveInterval: TimeSpan.Zero,
            acquireTimeout: TimeSpan.FromMilliseconds(100),
            connectTimeout: TimeSpan.FromMilliseconds(200));
        var factory = new MailKitSmtpConnectionFactory(options, new DefaultMailKitSmtpClientFactory());
        await using var pool = new SmtpPool(options, factory);
        var sender = new SmtpSender(pool, new DefaultSmtpErrorClassifier(), options);

        await sender.SendAsync(CreateMessage($"mailkit-pooling-warm-{Guid.NewGuid():N}"));

        try
        {
            await StopSmtp4DevAsync();
            await WaitForSmtp4DevAvailabilityAsync(isAvailable: false, TimeSpan.FromSeconds(10));

            await Assert.ThrowsAsync<SmtpSendFailedException>(
                () => sender.SendAsync(CreateMessage($"mailkit-pooling-fail-{Guid.NewGuid():N}")));

            var cooldownException = await Assert.ThrowsAsync<SmtpSendFailedException>(
                () => sender.SendAsync(CreateMessage($"mailkit-pooling-cooldown-{Guid.NewGuid():N}")));

            Assert.Equal(SmtpFailureKind.PoolExhausted, cooldownException.Classification.Kind);

            await ResumeSmtp4DevAsync();
            await WaitForSmtp4DevAvailabilityAsync(isAvailable: true, TimeSpan.FromSeconds(15));
            await Task.Delay(options.ReconnectCooldown + TimeSpan.FromMilliseconds(100));

            var recovered = await sender.SendAsync(CreateMessage($"mailkit-pooling-recovered-{Guid.NewGuid():N}"));

            Assert.Equal(1, recovered.Attempts);
        }
        finally
        {
            await ResumeSmtp4DevAsync();
            await WaitForSmtp4DevAvailabilityAsync(isAvailable: true, TimeSpan.FromSeconds(15));
        }
    }

    [Fact]
    public async Task Retry_Succeeds_When_Server_Returns_Before_Next_Attempt()
    {
        await using var testLock = await Smtp4DevTestLock.AcquireAsync();
        await EnsureSmtp4DevStartedAsync();

        var options = CreateOptions(
            maxPoolSize: 1,
            reconnectCooldown: TimeSpan.Zero,
            keepAliveInterval: TimeSpan.Zero,
            connectTimeout: TimeSpan.FromSeconds(5),
            maxRetryAttempts: 2,
            retryBaseDelay: TimeSpan.FromSeconds(5));
        var factory = new MailKitSmtpConnectionFactory(options, new DefaultMailKitSmtpClientFactory());
        await using var pool = new SmtpPool(options, factory);
        var sender = new SmtpSender(pool, new DefaultSmtpErrorClassifier(), options);

        await sender.SendAsync(CreateMessage($"mailkit-pooling-warm-{Guid.NewGuid():N}"));
        await StopSmtp4DevAsync();
        await WaitForSmtp4DevAvailabilityAsync(isAvailable: false, TimeSpan.FromSeconds(10));

        try
        {
            var restartTask = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500));
                await ResumeSmtp4DevAsync();
                await WaitForSmtp4DevAvailabilityAsync(isAvailable: true, TimeSpan.FromSeconds(30));
            });

            var result = await sender.SendAsync(CreateMessage($"mailkit-pooling-retry-{Guid.NewGuid():N}"));
            await restartTask;

            Assert.InRange(result.Attempts, 2, 3);
        }
        finally
        {
            await ResumeSmtp4DevAsync();
            await WaitForSmtp4DevAvailabilityAsync(isAvailable: true, TimeSpan.FromSeconds(30));
        }
    }

    private static MimeMessage CreateMessage(string subject)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("from@example.com"));
        message.To.Add(MailboxAddress.Parse("to@example.com"));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = "integration body" };
        return message;
    }

    private static SmtpPoolOptions CreateOptions(
        int maxPoolSize,
        TimeSpan? reconnectCooldown = null,
        TimeSpan? keepAliveInterval = null,
        TimeSpan? acquireTimeout = null,
        TimeSpan? connectTimeout = null,
        int maxRetryAttempts = 1,
        TimeSpan? retryBaseDelay = null)
    {
        return new SmtpPoolOptions
        {
            Host = new SmtpHostOptions
            {
                Host = "localhost",
                Port = 2525,
                SecureSocketOptions = "None",
            },
            MaxPoolSize = maxPoolSize,
            AcquireTimeout = acquireTimeout ?? TimeSpan.FromSeconds(15),
            ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(15),
            KeepAliveInterval = keepAliveInterval ?? TimeSpan.FromMinutes(1),
            ReconnectCooldown = reconnectCooldown ?? TimeSpan.Zero,
            MaxRetryAttempts = maxRetryAttempts,
            RetryBaseDelay = retryBaseDelay ?? TimeSpan.FromMilliseconds(10),
            JitterRatio = 0d,
        };
    }

    private static async Task EnsureSmtp4DevStartedAsync()
    {
        await StartSmtp4DevAsync();
        await WaitForSmtp4DevAvailabilityAsync(isAvailable: true, TimeSpan.FromSeconds(15));
    }

    private static async Task WaitForSmtp4DevAvailabilityAsync(bool isAvailable, TimeSpan timeout)
    {
        using var httpClient = new HttpClient
        {
            BaseAddress = Smtp4DevApiBaseAddress,
            Timeout = TimeSpan.FromSeconds(2),
        };

        var startedAt = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - startedAt < timeout)
        {
            try
            {
                using var response = await httpClient.GetAsync("/api/Messages").ConfigureAwait(false);
                if (response.IsSuccessStatusCode == isAvailable)
                {
                    return;
                }
            }
            catch
            {
                if (!isAvailable)
                {
                    return;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }

        if (isAvailable)
        {
            throw SkipException.ForSkip("smtp4dev did not become available in time.");
        }

        throw new TimeoutException("smtp4dev did not stop responding in time.");
    }

    private static async Task EnsureSmtp4DevAvailableAsync()
    {
        using var httpClient = new HttpClient
        {
            BaseAddress = Smtp4DevApiBaseAddress,
            Timeout = TimeSpan.FromSeconds(2),
        };

        try
        {
            using var response = await httpClient.GetAsync("/api/Messages").ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception exception)
        {
            throw SkipException.ForSkip(
                $"smtp4dev is not available on http://localhost:5080. Start docker/compose.smtp.yml before running integration tests. {exception.Message}");
        }
    }

    private static async Task<bool> WaitForMessageAsync(string subject, TimeSpan timeout)
    {
        using var httpClient = new HttpClient
        {
            BaseAddress = Smtp4DevApiBaseAddress,
            Timeout = TimeSpan.FromSeconds(2),
        };

        var startedAt = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - startedAt < timeout)
        {
            var page = await httpClient.GetFromJsonAsync<PagedResult<MessageSummary>>(
                $"/api/Messages?searchTerms={Uri.EscapeDataString(subject)}&pageSize=20").ConfigureAwait(false);

            if (page?.Results?.Any(message => string.Equals(message.Subject, subject, StringComparison.Ordinal)) == true)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }

        return false;
    }

    private sealed record PagedResult<T>(IReadOnlyList<T> Results);

    private sealed record MessageSummary(string Subject);

    private static Task StartSmtp4DevAsync()
    {
        return RunDockerComposeAsync("up", "-d");
    }

    private static Task StopSmtp4DevAsync()
    {
        return RunDockerComposeAsync("stop");
    }

    private static async Task ResumeSmtp4DevAsync()
    {
        await RunDockerComposeAsync("up", "-d");
    }

    private static async Task RunDockerComposeAsync(params string[] arguments)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        processStartInfo.ArgumentList.Add("compose");
        processStartInfo.ArgumentList.Add("-f");
        processStartInfo.ArgumentList.Add(ComposeFilePath);
        foreach (var argument in arguments)
        {
            processStartInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = processStartInfo };
        process.Start();
        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync().ConfigureAwait(false);
        var standardOutput = await standardOutputTask.ConfigureAwait(false);
        var standardError = await standardErrorTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"docker compose failed with exit code {process.ExitCode}.{Environment.NewLine}{standardOutput}{Environment.NewLine}{standardError}");
        }
    }

}
