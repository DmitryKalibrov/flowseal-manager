namespace FlowsealManager.Core.Infrastructure;

public sealed class AppPaths
{
    public AppPaths(string? dataRoot = null)
    {
        DataRoot = dataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "FlowsealManager");
        UserDataRoot = dataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FlowsealManager");
    }

    public string DataRoot { get; }

    public string UserDataRoot { get; }

    public string ComponentsRoot => Path.Combine(DataRoot, "components");

    public string TelegramVersionsRoot => Path.Combine(ComponentsRoot, "tg-ws-proxy");

    public string ZapretVersionsRoot => Path.Combine(ComponentsRoot, "zapret");

    public string TempRoot => Path.Combine(DataRoot, "temp");

    public string LogsRoot => Path.Combine(UserDataRoot, "logs");

    public string BackupsRoot => Path.Combine(UserDataRoot, "backups");

    public string HostsBackupsRoot => Path.Combine(BackupsRoot, "hosts");

    public string SettingsFile => Path.Combine(UserDataRoot, "settings.json");

    public string LogFile => Path.Combine(LogsRoot, "manager.log");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(UserDataRoot);
        Directory.CreateDirectory(ComponentsRoot);
        Directory.CreateDirectory(TelegramVersionsRoot);
        Directory.CreateDirectory(ZapretVersionsRoot);
        Directory.CreateDirectory(TempRoot);
        Directory.CreateDirectory(LogsRoot);
        Directory.CreateDirectory(HostsBackupsRoot);
    }

    public string TelegramDirectory(string version) =>
        Path.Combine(TelegramVersionsRoot, SafeVersion(version));

    public string TelegramExecutable(string version) =>
        Path.Combine(TelegramDirectory(version), "TgWsProxy.exe");

    public string ZapretDirectory(string version) =>
        Path.Combine(ZapretVersionsRoot, SafeVersion(version));

    public static string SafeVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new ArgumentException("Version cannot be empty.", nameof(version));
        }

        var safe = version.Trim();
        if (safe.Length > 80 ||
            safe is "." or ".." ||
            safe.EndsWith('.') ||
            safe.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
        {
            throw new ArgumentException("Invalid version.", nameof(version));
        }

        return safe;
    }
}
