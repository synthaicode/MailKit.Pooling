using System.Diagnostics;
using System.Net.Http.Json;
using MailKit.Pooling.Errors;
using MailKit.Pooling.MailKit;
using MailKit.Pooling.Options;
using MailKit.Pooling.Pooling;
using MailKit.Pooling.Sending;
using MimeKit;
using Xunit.Sdk;

namespace MailKit.Pooling.IntegrationTests.Smtp4Dev;

public sealed class Smtp4DevMultiHostTests
{
    private static readonly Uri PrimaryApiBaseAddress = new("http://localhost:5080");
    private static readonly Uri SecondaryApiBaseAddress = new("http://localhost:5081");
    private static readonly string ComposeFilePath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docker", "compose.smtp.yml"));

    [Fact]
    public async Task Priority_Fails_Over_To_Secondary_When_Primary_Is_Stopped()
    {
        await using var testLock = await Smtp4DevTestLock.AcquireAsync();
        await EnsureMultiHostStartedAsync();

        var options = new SmtpPoolOptions
        {
            Hosts =
            [
                new SmtpHostOptions
                {
                    Host = "localhost",
                    Port = 2525,
                    SecureSocketOptions = "None",
                    Priority = 0,
                    Weight = 1,
                },
                new SmtpHostOptions
                {
                    Host = "localhost",
                    Port = 2526,
                    SecureSocketOptions = "None",
                    Priority = 10,
                    Weight = 1,
                },
            ],
            MaxPoolSize = 1,
            AcquireTimeout = TimeSpan.FromSeconds(5),
            ConnectTimeout = TimeSpan.FromMilliseconds(400),
            ReconnectCooldown = TimeSpan.FromSeconds(2),
            RetryBaseDelay = TimeSpan.FromMilliseconds(200),
            MaxRetryAttempts = 1,
            JitterRatio = 0d,
            KeepAliveInterval = TimeSpan.Zero,
        };

        var factory = new MailKitSmtpConnectionFactory(options, new DefaultMailKitSmtpClientFactory());
        await using (var primaryPool = new SmtpPool(options, factory))
        {
            var primarySender = new SmtpSender(primaryPool, new DefaultSmtpErrorClassifier(), options);
            var primarySubject = $"mailkit-pooling-primary-{Guid.NewGuid():N}";
            var primaryResult = await primarySender.SendAsync(CreateMessage(primarySubject));

            Assert.Equal("localhost:2525", primaryResult.EndpointKey);
            Assert.True(await WaitForMessageAsync(PrimaryApiBaseAddress, primarySubject, TimeSpan.FromSeconds(5)));
            Assert.False(await WaitForMessageAsync(SecondaryApiBaseAddress, primarySubject, TimeSpan.FromMilliseconds(500)));
        }

        try
        {
            await RunDockerComposeAsync("stop", "smtp4dev-1");
            await WaitForAvailabilityAsync(PrimaryApiBaseAddress, isAvailable: false, TimeSpan.FromSeconds(10));
            await WaitForAvailabilityAsync(SecondaryApiBaseAddress, isAvailable: true, TimeSpan.FromSeconds(10));

            await using var failoverPool = new SmtpPool(options, factory);
            var failoverSender = new SmtpSender(failoverPool, new DefaultSmtpErrorClassifier(), options);
            var failoverSubject = $"mailkit-pooling-failover-{Guid.NewGuid():N}";
            var failoverResult = await failoverSender.SendAsync(CreateMessage(failoverSubject));

            Assert.Equal(2, failoverResult.Attempts);
            Assert.Equal("localhost:2526", failoverResult.EndpointKey);
            Assert.True(await WaitForMessageAsync(SecondaryApiBaseAddress, failoverSubject, TimeSpan.FromSeconds(5)));
        }
        finally
        {
            await RunDockerComposeAsync("start", "smtp4dev-1");
            await WaitForAvailabilityAsync(PrimaryApiBaseAddress, isAvailable: true, TimeSpan.FromSeconds(15));
        }
    }

    [Fact]
    public async Task Equal_Priority_Weights_Distribute_Connections_Across_Real_Smtp_Endpoints()
    {
        await using var testLock = await Smtp4DevTestLock.AcquireAsync();
        await EnsureMultiHostStartedAsync();

        var options = new SmtpPoolOptions
        {
            Hosts =
            [
                new SmtpHostOptions
                {
                    Host = "localhost",
                    Port = 2525,
                    SecureSocketOptions = "None",
                    Priority = 0,
                    Weight = 3,
                },
                new SmtpHostOptions
                {
                    Host = "localhost",
                    Port = 2526,
                    SecureSocketOptions = "None",
                    Priority = 0,
                    Weight = 1,
                },
            ],
            MinPoolSize = 4,
            MaxPoolSize = 4,
            AcquireTimeout = TimeSpan.FromSeconds(5),
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ReconnectCooldown = TimeSpan.Zero,
            KeepAliveInterval = TimeSpan.FromMinutes(1),
        };

        var factory = new MailKitSmtpConnectionFactory(options, new DefaultMailKitSmtpClientFactory());
        await using var pool = new SmtpPool(options, factory);
        await pool.WarmupAsync();

        var leases = new List<SmtpConnectionLease>();
        try
        {
            for (var index = 0; index < 4; index++)
            {
                leases.Add(await pool.AcquireLeaseAsync());
            }

            var groupedEndpoints = leases
                .GroupBy(lease => lease.EndpointKey, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

            Assert.Equal(3, groupedEndpoints["localhost:2525"]);
            Assert.Equal(1, groupedEndpoints["localhost:2526"]);

            var messageTasks = leases.Select((lease, index) =>
            {
                var subject = $"mailkit-pooling-weight-{index}-{Guid.NewGuid():N}";
                var apiBaseAddress = lease.EndpointKey == "localhost:2525"
                    ? PrimaryApiBaseAddress
                    : SecondaryApiBaseAddress;

                return SendAndVerifyAsync(lease, subject, apiBaseAddress);
            });

            await Task.WhenAll(messageTasks);
        }
        finally
        {
            foreach (var lease in leases)
            {
                await lease.ReturnAsync();
            }
        }
    }

    private static async Task SendAndVerifyAsync(
        SmtpConnectionLease lease,
        string subject,
        Uri apiBaseAddress)
    {
        await lease.Client.SendAsync(CreateMessage(subject), CancellationToken.None);
        Assert.True(await WaitForMessageAsync(apiBaseAddress, subject, TimeSpan.FromSeconds(5)));
    }

    private static MimeMessage CreateMessage(string subject)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("from@example.com"));
        message.To.Add(MailboxAddress.Parse("to@example.com"));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = "multi-host integration body" };
        return message;
    }

    private static async Task EnsureMultiHostStartedAsync()
    {
        await RunDockerComposeAsync("up", "-d");
        await WaitForAvailabilityAsync(PrimaryApiBaseAddress, isAvailable: true, TimeSpan.FromSeconds(30));
        await WaitForAvailabilityAsync(SecondaryApiBaseAddress, isAvailable: true, TimeSpan.FromSeconds(30));
    }

    private static async Task<bool> WaitForMessageAsync(Uri apiBaseAddress, string subject, TimeSpan timeout)
    {
        using var httpClient = CreateHttpClient(apiBaseAddress);
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

    private static async Task WaitForAvailabilityAsync(Uri apiBaseAddress, bool isAvailable, TimeSpan timeout)
    {
        using var httpClient = CreateHttpClient(apiBaseAddress);
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
            throw SkipException.ForSkip($"smtp4dev at {apiBaseAddress} did not become available in time.");
        }

        throw new TimeoutException($"smtp4dev at {apiBaseAddress} did not stop responding in time.");
    }

    private static HttpClient CreateHttpClient(Uri apiBaseAddress)
    {
        return new HttpClient
        {
            BaseAddress = apiBaseAddress,
            Timeout = TimeSpan.FromSeconds(2),
        };
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

    private sealed record PagedResult<T>(IReadOnlyList<T> Results);

    private sealed record MessageSummary(string Subject);

    private sealed class Smtp4DevTestLock : IAsyncDisposable
    {
        private readonly Semaphore semaphore;

        private Smtp4DevTestLock(Semaphore semaphore)
        {
            this.semaphore = semaphore;
        }

        public static async Task<Smtp4DevTestLock> AcquireAsync()
        {
            var semaphore = new Semaphore(1, 1, @"Global\MailKit.Pooling.Smtp4DevTests");
            var acquired = await Task.Run(() => semaphore.WaitOne(TimeSpan.FromMinutes(2))).ConfigureAwait(false);
            if (!acquired)
            {
                semaphore.Dispose();
                throw new TimeoutException("Timed out while waiting for the shared smtp4dev test lock.");
            }

            return new Smtp4DevTestLock(semaphore);
        }

        public ValueTask DisposeAsync()
        {
            semaphore.Release();
            semaphore.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
