using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FlowsealManager.Core.Infrastructure;
using Microsoft.Win32;

namespace FlowsealManager.Core.Services;

public sealed class ComponentProcessManager
{
    private readonly AppPaths _paths;
    private readonly FileLogger _logger;
    private readonly LegacyServiceManager _legacyServices;
    private int? _managedTelegramRootProcessId;

    public ComponentProcessManager(AppPaths paths, FileLogger logger)
    {
        _paths = paths;
        _logger = logger;
        _legacyServices = new LegacyServiceManager(logger);
    }

    public IReadOnlyList<string> GetStrategies(string zapretVersion)
    {
        var root = _paths.ZapretDirectory(zapretVersion);
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.EnumerateFiles(root, "general*.bat", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Cast<string>()
            .OrderBy(name => string.Equals(name, "general.bat", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(name => name, NaturalStringComparer.Instance)
            .ToArray();
    }

    public async Task StartTelegramAsync(string version, CancellationToken cancellationToken = default)
    {
        var executable = _paths.TelegramExecutable(version);
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("TG WS Proxy is not installed.", executable);
        }

        DisableTelegramOwnAutostart();

        var telegramProcesses = GetTelegramProcessSnapshot();
        var instanceCount = CountProcessTrees(
            telegramProcesses.Select(process => (process.ProcessId, process.ParentProcessId)));
        var managerOwnsInstance = _managedTelegramRootProcessId is int managedProcessId &&
                                  telegramProcesses.Any(process => process.ProcessId == managedProcessId);
        if (instanceCount == 1 && managerOwnsInstance &&
            !HasTelegramTrayWindow(telegramProcesses) && IsTelegramProxyListening())
        {
            return;
        }

        if (telegramProcesses.Length > 0)
        {
            await StopTelegramAsync(version, cancellationToken).ConfigureAwait(false);
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }

        var tempDirectory = TelegramRuntimeEnvironment.ResolveTempDirectory();
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        // The official Windows build falls back to its headless proxy loop when
        // pystray cannot load a backend. Flowseal Manager remains the only tray app.
        startInfo.Environment["PYSTRAY_BACKEND"] = "flowseal_manager_headless";
        startInfo.Environment["TEMP"] = tempDirectory;
        startInfo.Environment["TMP"] = tempDirectory;

        using var launchedProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Не удалось запустить TG WS Proxy.");
        _managedTelegramRootProcessId = launchedProcess.Id;
        var started = await WaitUntilAsync(
            () => IsTelegramRunning(version),
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);
        if (!started)
        {
            _managedTelegramRootProcessId = null;
            await StopTelegramAsync(version, cancellationToken).ConfigureAwait(false);
            var endpoint = ReadTelegramProxyEndpoint();
            throw new InvalidOperationException(
                $"TG WS Proxy не запустился: локальный порт {endpoint.Address}:{endpoint.Port} " +
                "не открылся за 15 секунд.");
        }

        await _logger.InfoAsync(
            $"TG WS Proxy запущен в фоне без отдельной иконки; временная папка: {tempDirectory}.",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task StopTelegramAsync(string? version, CancellationToken cancellationToken = default)
    {
        _managedTelegramRootProcessId = null;
        foreach (var process in Process.GetProcessesByName("TgWsProxy"))
        {
            using (process)
            {
                await StopProcessAsync(process, cancellationToken).ConfigureAwait(false);
            }
        }

        await _logger.InfoAsync("TG WS Proxy остановлен.", cancellationToken).ConfigureAwait(false);
    }

    public async Task StartZapretAsync(
        string version,
        string strategy,
        CancellationToken cancellationToken = default)
    {
        var root = _paths.ZapretDirectory(version);
        var batch = Path.GetFullPath(Path.Combine(root, strategy));
        var rootWithSeparator = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        if (!batch.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(batch) ||
            !string.Equals(Path.GetExtension(batch), ".bat", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(batch).StartsWith("general", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Unknown zapret strategy.", nameof(strategy));
        }

        var stoppedManagedZapret = IsZapretRunning();
        await StopZapretAsync(cancellationToken).ConfigureAwait(false);
        var externalWinwsCount = ExternalZapretProcessCount();
        if (externalWinwsCount > 0)
        {
            throw new ZapretConflictException(
                $"Найдены посторонние процессы winws.exe: {externalWinwsCount}. " +
                "Нажмите «Удалить старые службы».");
        }

        var detectedServices = await _legacyServices.DetectAsync(cancellationToken).ConfigureAwait(false);
        var conflicts = detectedServices
            .Where(service => !LegacyServiceManager.IsTransientWinDivert(service.Name))
            .ToArray();
        if (conflicts.Length > 0)
        {
            throw new ZapretConflictException(
                $"Найдены старые службы: {string.Join(", ", conflicts.Select(service => service.Name))}. " +
                "Нажмите «Удалить старые службы».");
        }

        var transientDrivers = detectedServices
            .Where(service => LegacyServiceManager.IsTransientWinDivert(service.Name))
            .Select(service => service.Name)
            .ToArray();
        if (transientDrivers.Length > 0)
        {
            if (!stoppedManagedZapret)
            {
                throw new ZapretConflictException(
                    $"Найдена уже работающая служба {string.Join(", ", transientDrivers)}. " +
                    "Нажмите «Удалить старые службы».");
            }

            var cleanup = await _legacyServices.CleanupAsync(
                    transientDrivers,
                    cancellationToken,
                    transientDriverCleanup: true,
                    stopWinwsProcesses: false)
                .ConfigureAwait(false);
            if (cleanup.Errors.Count > 0)
            {
                throw new ZapretConflictException(
                    "Не удалось перезапустить драйвер WinDivert. Нажмите «Удалить старые службы».");
            }
        }

        EnsureZapretUserLists(root);
        await EnableTcpTimestampsAsync(cancellationToken).ConfigureAwait(false);
        var launch = ZapretStrategyParser.Parse(batch);
        var startInfo = new ProcessStartInfo
        {
            FileName = launch.Executable,
            WorkingDirectory = Path.GetDirectoryName(launch.Executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in launch.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var launcher = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to launch the zapret strategy.");

        var started = await WaitUntilAsync(
            () => IsZapretRunning(version),
            TimeSpan.FromSeconds(20),
            cancellationToken).ConfigureAwait(false);
        if (!started)
        {
            throw new InvalidOperationException(
                "winws.exe did not start. Check Windows Defender/antivirus and the manager log.");
        }

        await _logger.InfoAsync($"Запущена стратегия {strategy}.", cancellationToken).ConfigureAwait(false);
    }

    public async Task StopZapretAsync(CancellationToken cancellationToken = default)
    {
        var stoppedManagedProcess = false;
        foreach (var process in Process.GetProcessesByName("winws"))
        {
            using (process)
            {
                var path = TryGetExecutablePath(process);
                if (path is null || !IsUnder(path, _paths.ZapretVersionsRoot))
                {
                    continue;
                }

                await StopProcessAsync(process, cancellationToken).ConfigureAwait(false);
                stoppedManagedProcess = true;
            }
        }

        if (stoppedManagedProcess)
        {
            var transientDrivers = (await _legacyServices.DetectAsync(cancellationToken).ConfigureAwait(false))
                .Where(service => LegacyServiceManager.IsTransientWinDivert(service.Name))
                .Select(service => service.Name)
                .ToArray();
            if (transientDrivers.Length > 0)
            {
                await _legacyServices.CleanupAsync(
                    transientDrivers,
                    cancellationToken,
                    transientDriverCleanup: true,
                    stopWinwsProcesses: false).ConfigureAwait(false);
            }
        }
    }

    public bool IsTelegramRunning(string? version)
    {
        return TelegramInstanceCount() > 0 && IsTelegramProxyListening();
    }

    public bool IsTelegramProxyListening()
    {
        var endpoint = ReadTelegramProxyEndpoint();
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Any(listener => listener.Port == endpoint.Port &&
                                 (listener.Address.Equals(endpoint.Address) ||
                                  listener.Address.Equals(IPAddress.Any) ||
                                  listener.Address.Equals(IPAddress.IPv6Any)));
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    public int TelegramInstanceCount()
    {
        var processes = GetTelegramProcessSnapshot();
        return CountProcessTrees(processes.Select(process => (process.ProcessId, process.ParentProcessId)));
    }

    public static int CountProcessTrees(IEnumerable<(int ProcessId, int ParentProcessId)> processes)
    {
        var snapshot = processes.Distinct().ToArray();
        var processIds = snapshot.Select(process => process.ProcessId).ToHashSet();
        return snapshot.Count(process => !processIds.Contains(process.ParentProcessId));
    }

    public int ExternalZapretProcessCount()
    {
        var count = 0;
        foreach (var process in Process.GetProcessesByName("winws"))
        {
            using (process)
            {
                var path = TryGetExecutablePath(process);
                if (path is null || !IsUnder(path, _paths.ZapretVersionsRoot))
                {
                    count++;
                }
            }
        }

        return count;
    }

    public bool IsZapretRunning(string? version = null)
    {
        var root = string.IsNullOrWhiteSpace(version)
            ? _paths.ZapretVersionsRoot
            : _paths.ZapretDirectory(version);
        foreach (var process in Process.GetProcessesByName("winws"))
        {
            using (process)
            {
                var path = TryGetExecutablePath(process);
                if (path is not null && IsUnder(path, root))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string? TryGetExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static TelegramProcessInfo[] GetTelegramProcessSnapshot()
    {
        var parentProcessIds = GetParentProcessIds();
        return Process.GetProcessesByName("TgWsProxy")
            .Select(process =>
            {
                using (process)
                {
                    return new TelegramProcessInfo(
                        process.Id,
                        parentProcessIds.GetValueOrDefault(process.Id));
                }
            })
            .ToArray();
    }

    private static IPEndPoint ReadTelegramProxyEndpoint()
    {
        var fallback = new IPEndPoint(IPAddress.Loopback, 1080);
        try
        {
            var configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TgWsProxy",
                "config.json");
            if (!File.Exists(configPath))
            {
                return fallback;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = document.RootElement;
            if (!root.TryGetProperty("host", out var hostProperty) ||
                hostProperty.ValueKind != JsonValueKind.String ||
                !IPAddress.TryParse(hostProperty.GetString(), out var address) ||
                !IPAddress.IsLoopback(address) ||
                !root.TryGetProperty("port", out var portProperty) ||
                !portProperty.TryGetInt32(out var port) ||
                port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
            {
                return fallback;
            }

            return new IPEndPoint(address, port);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return fallback;
        }
    }

    private static Dictionary<int, int> GetParentProcessIds()
    {
        var result = new Dictionary<int, int>();
        if (!OperatingSystem.IsWindows())
        {
            return result;
        }

        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == InvalidHandleValue)
        {
            return result;
        }

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry))
            {
                return result;
            }

            do
            {
                result[(int)entry.ProcessId] = (int)entry.ParentProcessId;
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            }
            while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return result;
    }

    private static bool HasTelegramTrayWindow(IReadOnlyCollection<TelegramProcessInfo> processes)
    {
        if (!OperatingSystem.IsWindows() || processes.Count == 0)
        {
            return false;
        }

        var processIds = processes.Select(process => (uint)process.ProcessId).ToHashSet();
        var found = false;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var processId);
            if (!processIds.Contains(processId))
            {
                return true;
            }

            var className = new StringBuilder(256);
            GetClassName(window, className, className.Capacity);
            found = className.ToString().EndsWith("SystemTrayIcon", StringComparison.Ordinal);
            return !found;
        }, nint.Zero);
        return found;
    }

    private static bool IsUnder(string candidate, string root)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) +
                       Path.DirectorySeparatorChar;
        var fullCandidate = Path.GetFullPath(candidate);
        return fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task StopProcessAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            process.CloseMainWindow();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(true);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between checks.
        }
    }

    private static async Task<bool> WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (condition())
            {
                return true;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        return condition();
    }

    private static void DisableTelegramOwnAutostart()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            writable: true);
        key?.DeleteValue("TgWsProxy", throwOnMissingValue: false);
    }

    private static void EnsureZapretUserLists(string root)
    {
        var lists = Path.Combine(root, "lists");
        Directory.CreateDirectory(lists);
        var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ipset-exclude-user.txt"] = "203.0.113.113/32" + Environment.NewLine,
            ["ipset-general-user.txt"] = "203.0.113.113/32" + Environment.NewLine,
            ["list-general-user.txt"] = "# Never leave this file empty" + Environment.NewLine +
                                        "domain.example.abc" + Environment.NewLine,
            ["list-exclude-user.txt"] = "domain.example.abc" + Environment.NewLine
        };
        foreach (var (fileName, content) in defaults)
        {
            var path = Path.Combine(lists, fileName);
            if (!File.Exists(path))
            {
                File.WriteAllText(path, content);
            }
        }
    }

    private static async Task EnableTcpTimestampsAsync(CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe"),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        info.ArgumentList.Add("interface");
        info.ArgumentList.Add("tcp");
        info.ArgumentList.Add("set");
        info.ArgumentList.Add("global");
        info.ArgumentList.Add("timestamps=enabled");
        using var process = Process.Start(info);
        if (process is not null)
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private const uint Th32csSnapProcess = 0x00000002;
    private static readonly nint InvalidHandleValue = new(-1);

    private sealed record TelegramProcessInfo(int ProcessId, int ParentProcessId);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    private delegate bool EnumWindowsCallback(nint window, nint parameter);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(nint window, StringBuilder className, int maximumCount);

}

internal sealed class NaturalStringComparer : IComparer<string>
{
    public static NaturalStringComparer Instance { get; } = new();

    public int Compare(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return StringComparer.OrdinalIgnoreCase.Compare(left, right);
        }

        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            if (char.IsDigit(left[leftIndex]) && char.IsDigit(right[rightIndex]))
            {
                var leftStart = leftIndex;
                var rightStart = rightIndex;
                while (leftIndex < left.Length && char.IsDigit(left[leftIndex])) leftIndex++;
                while (rightIndex < right.Length && char.IsDigit(right[rightIndex])) rightIndex++;
                var leftNumber = long.Parse(left.AsSpan(leftStart, leftIndex - leftStart));
                var rightNumber = long.Parse(right.AsSpan(rightStart, rightIndex - rightStart));
                var numeric = leftNumber.CompareTo(rightNumber);
                if (numeric != 0) return numeric;
                continue;
            }

            var character = char.ToUpperInvariant(left[leftIndex])
                .CompareTo(char.ToUpperInvariant(right[rightIndex]));
            if (character != 0) return character;
            leftIndex++;
            rightIndex++;
        }

        return left.Length.CompareTo(right.Length);
    }
}
