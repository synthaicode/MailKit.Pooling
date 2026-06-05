using System.Diagnostics;

namespace MailKit.Pooling.StressTests.Helpers;

internal interface ITimeWaitObserver
{
    Task<TimeWaitSample> ObserveAsync(int smtpPort, CancellationToken cancellationToken = default);
}

internal sealed record TimeWaitSample(bool IsAvailable, int Count, string Source);

internal sealed class WindowsTimeWaitObserver : ITimeWaitObserver
{
    public async Task<TimeWaitSample> ObserveAsync(int smtpPort, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new TimeWaitSample(false, 0, "unsupported-os");
        }

        var command = $"(Get-NetTCPConnection -State TimeWait -ErrorAction SilentlyContinue | Where-Object {{ $_.RemotePort -eq {smtpPort} }} | Measure-Object).Count";
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            return new TimeWaitSample(false, 0, $"powershell-exit-{process.ExitCode}:{stderr.Trim()}");
        }

        if (!int.TryParse(stdout.Trim(), out var count))
        {
            return new TimeWaitSample(false, 0, $"unexpected-output:{stdout.Trim()}");
        }

        return new TimeWaitSample(true, count, "Get-NetTCPConnection");
    }
}
