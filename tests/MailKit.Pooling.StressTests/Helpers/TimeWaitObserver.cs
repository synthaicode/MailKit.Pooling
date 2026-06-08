using System.ComponentModel;
using System.Diagnostics;

namespace PooledMailKit.StressTests.Helpers;

internal interface ITimeWaitObserver
{
    Task<TimeWaitSample> ObserveAsync(int smtpPort, CancellationToken cancellationToken = default);
}

internal sealed record TimeWaitSample(bool IsAvailable, int Count, string Source);

internal static class TimeWaitObserverFactory
{
    public static ITimeWaitObserver CreateDefault()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsTimeWaitObserver();
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxTimeWaitObserver();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacOsTimeWaitObserver();
        }

        return new UnsupportedTimeWaitObserver();
    }
}

internal sealed class WindowsTimeWaitObserver : ITimeWaitObserver
{
    public async Task<TimeWaitSample> ObserveAsync(int smtpPort, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new TimeWaitSample(false, 0, "unsupported-os");
        }

        var command = $"(Get-NetTCPConnection -State TimeWait -ErrorAction SilentlyContinue | Where-Object {{ $_.RemotePort -eq {smtpPort} }} | Measure-Object).Count";
        return await CommandTimeWaitObserver.RunAsync(
            "powershell",
            ["-NoProfile", "-Command", command],
            stdout => TimeWaitOutputParser.TryParseWindowsCount(stdout, out var count) ? count : null,
            "Get-NetTCPConnection",
            cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class LinuxTimeWaitObserver : ITimeWaitObserver
{
    public async Task<TimeWaitSample> ObserveAsync(int smtpPort, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
        {
            return new TimeWaitSample(false, 0, "unsupported-os");
        }

        var ssResult = await CommandTimeWaitObserver.RunAsync(
            "/bin/sh",
            ["-c", "ss -tan state time-wait"],
            stdout => TimeWaitOutputParser.CountLinuxSsTimeWait(stdout, smtpPort),
            "ss",
            cancellationToken).ConfigureAwait(false);

        if (ssResult.IsAvailable)
        {
            return ssResult;
        }

        return await CommandTimeWaitObserver.RunAsync(
            "/bin/sh",
            ["-c", "netstat -tan"],
            stdout => TimeWaitOutputParser.CountLinuxNetstatTimeWait(stdout, smtpPort),
            "netstat",
            cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class MacOsTimeWaitObserver : ITimeWaitObserver
{
    public async Task<TimeWaitSample> ObserveAsync(int smtpPort, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return new TimeWaitSample(false, 0, "unsupported-os");
        }

        return await CommandTimeWaitObserver.RunAsync(
            "/bin/sh",
            ["-c", "netstat -anv -p tcp"],
            stdout => TimeWaitOutputParser.CountMacOsNetstatTimeWait(stdout, smtpPort),
            "netstat",
            cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class UnsupportedTimeWaitObserver : ITimeWaitObserver
{
    public Task<TimeWaitSample> ObserveAsync(int smtpPort, CancellationToken cancellationToken = default)
    {
        _ = smtpPort;
        _ = cancellationToken;
        return Task.FromResult(new TimeWaitSample(false, 0, "unsupported-os"));
    }
}

internal static class TimeWaitOutputParser
{
    public static bool TryParseWindowsCount(string stdout, out int count)
    {
        return int.TryParse(stdout.Trim(), out count);
    }

    public static int? CountLinuxSsTimeWait(string stdout, int smtpPort)
    {
        var count = 0;
        foreach (var line in EnumerateLines(stdout))
        {
            if (!line.Contains("TIME-WAIT", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var tokens = line.Split((char[])null!, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 5)
            {
                continue;
            }

            if (TryParsePortFromColonEndpoint(tokens[^1], out var remotePort) && remotePort == smtpPort)
            {
                count++;
            }
        }

        return count;
    }

    public static int? CountLinuxNetstatTimeWait(string stdout, int smtpPort)
    {
        var count = 0;
        foreach (var line in EnumerateLines(stdout))
        {
            if (!line.Contains("TIME_WAIT", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var tokens = line.Split((char[])null!, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 6)
            {
                continue;
            }

            if (TryParsePortFromColonEndpoint(tokens[^2], out var remotePort) && remotePort == smtpPort)
            {
                count++;
            }
        }

        return count;
    }

    public static int? CountMacOsNetstatTimeWait(string stdout, int smtpPort)
    {
        var count = 0;
        foreach (var line in EnumerateLines(stdout))
        {
            if (!line.Contains("TIME_WAIT", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var tokens = line.Split((char[])null!, StringSplitOptions.RemoveEmptyEntries);
            var stateIndex = Array.FindIndex(tokens, token => token.Equals("TIME_WAIT", StringComparison.OrdinalIgnoreCase));
            if (stateIndex < 3)
            {
                continue;
            }

            if (TryParsePortFromDotEndpoint(tokens[stateIndex - 1], out var remotePort) && remotePort == smtpPort)
            {
                count++;
            }
        }

        return count;
    }

    private static IEnumerable<string> EnumerateLines(string stdout)
    {
        return stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool TryParsePortFromColonEndpoint(string endpoint, out int port)
    {
        port = 0;
        var separatorIndex = endpoint.LastIndexOf(':');
        if (separatorIndex < 0 || separatorIndex == endpoint.Length - 1)
        {
            return false;
        }

        return int.TryParse(endpoint[(separatorIndex + 1)..], out port);
    }

    private static bool TryParsePortFromDotEndpoint(string endpoint, out int port)
    {
        port = 0;
        var separatorIndex = endpoint.LastIndexOf('.');
        if (separatorIndex < 0 || separatorIndex == endpoint.Length - 1)
        {
            return false;
        }

        return int.TryParse(endpoint[(separatorIndex + 1)..], out port);
    }
}

internal static class CommandTimeWaitObserver
{
    public static async Task<TimeWaitSample> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        Func<string, int?> countParser,
        string source,
        CancellationToken cancellationToken)
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
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                return new TimeWaitSample(false, 0, $"{source}-exit-{process.ExitCode}:{stderr.Trim()}");
            }

            var count = countParser(stdout);
            if (!count.HasValue)
            {
                return new TimeWaitSample(false, 0, $"{source}-unexpected-output");
            }

            return new TimeWaitSample(true, count.Value, source);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return new TimeWaitSample(false, 0, $"{source}-unavailable:{ex.GetType().Name}");
        }
    }
}
