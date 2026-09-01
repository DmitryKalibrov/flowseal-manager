using System.Diagnostics;

namespace FlowsealManager.Core.Services;

public sealed class StartupTaskManager
{
    public const string TaskName = "FlowsealManager";

    private readonly string _executablePath;

    public StartupTaskManager(string executablePath)
    {
        _executablePath = Path.GetFullPath(executablePath);
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        var arguments = enabled
            ? BuildCreateArguments(_executablePath)
            : new[] { "/Delete", "/F", "/TN", TaskName };

        var result = await RunAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 && !(result.ExitCode == 1 && !enabled))
        {
            throw new InvalidOperationException(
                $"Не удалось изменить задачу автозапуска (код {result.ExitCode}): {result.Error}");
        }
    }

    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["/Query", "/TN", TaskName], cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    public static IReadOnlyList<string> BuildCreateArguments(string executablePath)
    {
        var taskCommand = $"\"{Path.GetFullPath(executablePath)}\" --minimized";
        return
        [
            "/Create",
            "/F",
            "/TN", TaskName,
            "/SC", "ONLOGON",
            "/RL", "HIGHEST",
            "/TR", taskCommand
        ];
    }

    private static async Task<ProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"),
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
            ?? throw new InvalidOperationException("Не удалось запустить schtasks.exe.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new ProcessResult(
            process.ExitCode,
            await outputTask.ConfigureAwait(false),
            await errorTask.ConfigureAwait(false));
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
