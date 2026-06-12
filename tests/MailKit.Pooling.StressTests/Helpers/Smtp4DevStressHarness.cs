using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using MimeKit;

namespace PooledMailKit.StressTests.Helpers;

internal sealed class Smtp4DevStressHarness : IAsyncDisposable
{
    private static readonly string ComposeFilePath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docker", "compose.smtp.yml"));
    private const string ManageDockerEnvironmentVariable = "MAILKIT_POOLING_STRESS_MANAGED_BY_HARNESS";
    private const string SmtpHostEnvironmentVariable = "MAILKIT_POOLING_STRESS_SMTP_HOST";
    private const string SmtpPortEnvironmentVariable = "MAILKIT_POOLING_STRESS_SMTP_PORT";
    private const string ApiBaseEnvironmentVariable = "MAILKIT_POOLING_STRESS_API_BASE";
    private const string SecondaryApiBaseEnvironmentVariable = "MAILKIT_POOLING_STRESS_SECONDARY_API_BASE";
    private static readonly string StressLockFilePath = Path.Combine(Path.GetTempPath(), "MailKit.Pooling.Smtp4DevTests.lock");

    private readonly Semaphore? semaphore;
    private readonly FileStream? lockFileStream;
    private readonly bool manageDockerLifecycle;
    private readonly Uri apiBaseAddress;
    private readonly Uri secondaryApiBaseAddress;

    private Smtp4DevStressHarness(
        Semaphore? semaphore,
        FileStream? lockFileStream,
        bool manageDockerLifecycle,
        string smtpHost,
        int smtpPort,
        Uri apiBaseAddress,
        Uri secondaryApiBaseAddress)
    {
        this.semaphore = semaphore;
        this.lockFileStream = lockFileStream;
        this.manageDockerLifecycle = manageDockerLifecycle;
        SmtpHost = smtpHost;
        SmtpPort = smtpPort;
        this.apiBaseAddress = apiBaseAddress;
        this.secondaryApiBaseAddress = secondaryApiBaseAddress;
    }

    public string SmtpHost { get; }

    public int SmtpPort { get; }

    public Uri SecondaryApiBaseAddress => secondaryApiBaseAddress;

    public static async Task<Smtp4DevStressHarness> AcquireAsync()
    {
        Semaphore? semaphore = null;
        FileStream? lockFileStream = null;

        if (OperatingSystem.IsWindows())
        {
            semaphore = new Semaphore(1, 1, @"Global\MailKit.Pooling.Smtp4DevTests");
            var acquired = await Task.Run(() => semaphore.WaitOne(TimeSpan.FromMinutes(2))).ConfigureAwait(false);
            if (!acquired)
            {
                semaphore.Dispose();
                throw new TimeoutException("Timed out while waiting for the smtp4dev stress lock.");
            }
        }
        else
        {
            lockFileStream = await AcquireFileLockAsync(TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        }

        var manageDockerLifecycle = !string.Equals(
            Environment.GetEnvironmentVariable(ManageDockerEnvironmentVariable),
            "0",
            StringComparison.Ordinal);
        var smtpHost = Environment.GetEnvironmentVariable(SmtpHostEnvironmentVariable) ?? "localhost";
        var smtpPort = int.TryParse(Environment.GetEnvironmentVariable(SmtpPortEnvironmentVariable), out var parsedPort)
            ? parsedPort
            : 2525;
        var apiBaseAddress = new Uri(
            Environment.GetEnvironmentVariable(ApiBaseEnvironmentVariable)
            ?? "http://localhost:5080");
        var secondaryApiBaseAddress = new Uri(
            Environment.GetEnvironmentVariable(SecondaryApiBaseEnvironmentVariable)
            ?? "http://localhost:5081");

        return new Smtp4DevStressHarness(
            semaphore,
            lockFileStream,
            manageDockerLifecycle,
            smtpHost,
            smtpPort,
            apiBaseAddress,
            secondaryApiBaseAddress);
    }

    public async Task EnsureStartedAsync()
    {
        if (manageDockerLifecycle)
        {
            await RunDockerComposeAsync("up", "-d").ConfigureAwait(false);
        }

        await WaitForAvailabilityAsync(isAvailable: true, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        if (!manageDockerLifecycle)
        {
            throw new InvalidOperationException("StopAsync is not available when smtp4dev lifecycle is managed externally.");
        }

        await RunDockerComposeAsync("stop").ConfigureAwait(false);
        await WaitForAvailabilityAsync(isAvailable: false, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
    }

    public async Task RestoreAsync()
    {
        if (!manageDockerLifecycle)
        {
            throw new InvalidOperationException("RestoreAsync is not available when smtp4dev lifecycle is managed externally.");
        }

        await RunDockerComposeAsync("up", "-d").ConfigureAwait(false);
        await WaitForAvailabilityAsync(isAvailable: true, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
    }

    public async Task StopServiceAsync(string serviceName, Uri apiBaseAddress)
    {
        if (!manageDockerLifecycle)
        {
            throw new InvalidOperationException("StopServiceAsync is not available when smtp4dev lifecycle is managed externally.");
        }

        await RunDockerComposeAsync("stop", serviceName).ConfigureAwait(false);
        await WaitForAvailabilityAsync(apiBaseAddress, isAvailable: false, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
    }

    public async Task RestoreServiceAsync(string serviceName, Uri apiBaseAddress)
    {
        if (!manageDockerLifecycle)
        {
            throw new InvalidOperationException("RestoreServiceAsync is not available when smtp4dev lifecycle is managed externally.");
        }

        await RunDockerComposeAsync("up", "-d", serviceName).ConfigureAwait(false);
        await WaitForAvailabilityAsync(apiBaseAddress, isAvailable: true, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
    }

    public MimeMessage CreateMessage(string subject, string body)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("from@example.com"));
        message.To.Add(MailboxAddress.Parse("to@example.com"));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };
        return message;
    }

    public async Task<bool> WaitForMessageAsync(string subject, TimeSpan timeout)
    {
        using var httpClient = CreateHttpClient();
        var startedAt = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - startedAt < timeout)
        {
            var page = await httpClient.GetFromJsonAsync<PagedResult<MessageSummary>>(
                $"/api/Messages?searchTerms={Uri.EscapeDataString(subject)}&pageSize=100").ConfigureAwait(false);

            if (page?.Results?.Any(message => string.Equals(message.Subject, subject, StringComparison.Ordinal)) == true)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }

        return false;
    }

    public async Task<string> WriteJsonResultAsync<T>(string scenarioName, T result, CancellationToken cancellationToken = default)
    {
        var directoryPath = Path.Combine(AppContext.BaseDirectory, "StressResults");
        Directory.CreateDirectory(directoryPath);
        var filePath = Path.Combine(
            directoryPath,
            $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{scenarioName}.json");

        await File.WriteAllTextAsync(
            filePath,
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken).ConfigureAwait(false);

        return filePath;
    }

    public ValueTask DisposeAsync()
    {
        if (semaphore is not null)
        {
            semaphore.Release();
            semaphore.Dispose();
        }

        lockFileStream?.Dispose();
        return ValueTask.CompletedTask;
    }

    private static async Task<FileStream> AcquireFileLockAsync(TimeSpan timeout)
    {
        var startedAt = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - startedAt < timeout)
        {
            try
            {
                return new FileStream(
                    StressLockFilePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
            }
        }

        throw new TimeoutException("Timed out while waiting for the smtp4dev stress lock.");
    }

    private HttpClient CreateHttpClient()
    {
        return CreateHttpClient(apiBaseAddress);
    }

    private static HttpClient CreateHttpClient(Uri apiBaseAddress)
    {
        return new HttpClient
        {
            BaseAddress = apiBaseAddress,
            Timeout = TimeSpan.FromSeconds(3),
        };
    }

    private async Task WaitForAvailabilityAsync(bool isAvailable, TimeSpan timeout)
    {
        await WaitForAvailabilityAsync(apiBaseAddress, isAvailable, timeout).ConfigureAwait(false);
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
#pragma warning disable CA1031 // Intentional swallow: any probe failure simply means smtp4dev is not reachable yet; the poll loop retries until the timeout.
            catch
            {
                if (!isAvailable)
                {
                    return;
                }
            }
#pragma warning restore CA1031

            await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
        }

        throw new TimeoutException($"smtp4dev availability '{isAvailable}' was not reached in time.");
    }

    private static async Task RunDockerComposeAsync(params string[] arguments)
    {
        var composeViaDocker = await TryRunProcessAsync(
            "docker",
            ["compose", "-f", ComposeFilePath, .. arguments]).ConfigureAwait(false);
        if (composeViaDocker.ExitCode == 0)
        {
            return;
        }

        var composeViaDockerCompose = await TryRunProcessAsync(
            "docker-compose",
            ["-f", ComposeFilePath, .. arguments]).ConfigureAwait(false);
        if (composeViaDockerCompose.ExitCode == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"docker compose failed.{Environment.NewLine}" +
            $"docker:{Environment.NewLine}{composeViaDocker.StdOut}{Environment.NewLine}{composeViaDocker.StdErr}{Environment.NewLine}" +
            $"docker-compose:{Environment.NewLine}{composeViaDockerCompose.StdOut}{Environment.NewLine}{composeViaDockerCompose.StdErr}");
    }

    private static async Task<ProcessResult> TryRunProcessAsync(string fileName, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().ConfigureAwait(false);

            return new ProcessResult(
                process.ExitCode,
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return new ProcessResult(-1, string.Empty, ex.ToString());
        }
    }

    private sealed record PagedResult<T>(IReadOnlyList<T> Results);

    private sealed record MessageSummary(string Subject);

    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
}
