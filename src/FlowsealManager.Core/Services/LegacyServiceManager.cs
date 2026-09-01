using System.Diagnostics;
using FlowsealManager.Core.Infrastructure;
using FlowsealManager.Core.Models;

namespace FlowsealManager.Core.Services;

public sealed class LegacyServiceManager
{
    private static readonly string[] KnownServices =
    [
        "zapret",
        "WinDivert",
        "WinDivert14",
        "GoodbyeDPI",
        "discordfix_zapret",
        "winws1",
        "winws2"
    ];

    private readonly FileLogger _logger;

    public LegacyServiceManager(FileLogger logger)
    {
        _logger = logger;
    }

    public static bool IsTransientWinDivert(string serviceName) =>
        serviceName.StartsWith("WinDivert", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<LegacyServiceInfo>> DetectAsync(
        CancellationToken cancellationToken = default)
    {
        var results = new List<LegacyServiceInfo>();
        foreach (var service in KnownServices)
        {
            var query = await RunScAsync(["query", service], cancellationToken).ConfigureAwait(false);
            if (query.ExitCode == 0)
            {
                results.Add(new LegacyServiceInfo(
                    service,
                    query.Output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase)));
            }
        }

        return results;
    }

    public async Task<LegacyCleanupResult> CleanupAsync(
        IReadOnlyCollection<string> serviceNames,
        CancellationToken cancellationToken = default,
        bool transientDriverCleanup = false,
        bool stopWinwsProcesses = true)
    {
        var allowed = serviceNames
            .Where(name => KnownServices.Contains(name, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var stoppedProcesses = stopWinwsProcesses
            ? await StopAllWinwsAsync(cancellationToken).ConfigureAwait(false)
            : 0;
        if (allowed.Length == 0)
        {
            return new LegacyCleanupResult([], [], stoppedProcesses);
        }

        var removed = new List<string>();
        var errors = new List<string>();

        foreach (var service in allowed)
        {
            await RunScAsync(["stop", service], cancellationToken).ConfigureAwait(false);
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            var delete = await RunScAsync(["delete", service], cancellationToken).ConfigureAwait(false);
            var remaining = await WaitUntilDeletedAsync(service, cancellationToken).ConfigureAwait(false);
            if (remaining.ExitCode != 0)
            {
                removed.Add(service);
                var message = transientDriverCleanup
                    ? $"Временный драйвер {service} сброшен перед сменой стратегии."
                    : $"Удалена старая служба {service}.";
                await _logger.InfoAsync(message, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                errors.Add($"{service}: {FirstUsefulLine(delete.Error, delete.Output)}");
            }
        }

        return new LegacyCleanupResult(removed, errors, stoppedProcesses);
    }

    private static async Task<ScResult> WaitUntilDeletedAsync(
        string service,
        CancellationToken cancellationToken)
    {
        ScResult? query = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            query = await RunScAsync(["query", service], cancellationToken).ConfigureAwait(false);
            if (query.ExitCode != 0)
            {
                return query;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        return query ?? new ScResult(1, string.Empty, "service query failed");
    }

    private static async Task<int> StopAllWinwsAsync(CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var process in Process.GetProcessesByName("winws"))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                        count++;
                    }
                }
                catch (InvalidOperationException)
                {
                    // Process exited between enumeration and termination.
                }
            }
        }

        return count;
    }

    private static async Task<ScResult> RunScAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("Не удалось запустить sc.exe.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new ScResult(
            process.ExitCode,
            await outputTask.ConfigureAwait(false),
            await errorTask.ConfigureAwait(false));
    }

    private static string FirstUsefulLine(params string[] values) =>
        values.SelectMany(value => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0) ?? "неизвестная ошибка";

    private sealed record ScResult(int ExitCode, string Output, string Error);
}
