using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using MimeKit;

namespace MailKit.Pooling.StressTests.Helpers;

internal sealed class Smtp4DevStressHarness : IAsyncDisposable
{
    private static readonly Uri ApiBaseAddress = new("http://localhost:5080");
    private static readonly string ComposeFilePath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docker", "compose.smtp.yml"));

    private readonly Semaphore semaphore;

    private Smtp4DevStressHarness(Semaphore semaphore)
    {
        this.semaphore = semaphore;
    }

    public static async Task<Smtp4DevStressHarness> AcquireAsync()
    {
        var semaphore = new Semaphore(1, 1, @"Global\MailKit.Pooling.Smtp4DevStress");
        var acquired = await Task.Run(() => semaphore.WaitOne(TimeSpan.FromMinutes(2))).ConfigureAwait(false);
        if (!acquired)
        {
            semaphore.Dispose();
            throw new TimeoutException("Timed out while waiting for the smtp4dev stress lock.");
        }

        return new Smtp4DevStressHarness(semaphore);
    }

    public async Task EnsureStartedAsync()
    {
        await RunDockerComposeAsync("up", "-d", "--force-recreate").ConfigureAwait(false);
        await WaitForAvailabilityAsync(isAvailable: true, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        await RunDockerComposeAsync("down").ConfigureAwait(false);
        await WaitForAvailabilityAsync(isAvailable: false, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
    }

    public async Task RestoreAsync()
    {
        await RunDockerComposeAsync("up", "-d", "--force-recreate").ConfigureAwait(false);
        await WaitForAvailabilityAsync(isAvailable: true, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
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
        semaphore.Release();
        semaphore.Dispose();
        return ValueTask.CompletedTask;
    }

    private static HttpClient CreateHttpClient()
    {
        return new HttpClient
        {
            BaseAddress = ApiBaseAddress,
            Timeout = TimeSpan.FromSeconds(3),
        };
    }

    private static async Task WaitForAvailabilityAsync(bool isAvailable, TimeSpan timeout)
    {
        using var httpClient = CreateHttpClient();
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

            await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
        }

        throw new TimeoutException($"smtp4dev availability '{isAvailable}' was not reached in time.");
    }

    private static async Task RunDockerComposeAsync(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("compose");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(ComposeFilePath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"docker compose failed with exit code {process.ExitCode}.{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
        }
    }

    private sealed record PagedResult<T>(IReadOnlyList<T> Results);

    private sealed record MessageSummary(string Subject);
}
