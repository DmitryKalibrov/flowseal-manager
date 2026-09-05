namespace FlowsealManager.Core.Services;

public sealed record TelegramTempDrive(
    string RootPath,
    DriveType DriveType,
    long AvailableFreeSpace,
    bool HasManagedDirectory);

public static class TelegramRuntimeEnvironment
{
    public const long MinimumFreeSpaceBytes = 256L * 1024 * 1024;
    private const string ManagedDirectoryName = "FlowsealManagerTemp";
    private const string TelegramDirectoryName = "TgWsProxy";

    public static string ResolveTempDirectory()
    {
        var systemTempDirectory = Path.GetFullPath(Path.GetTempPath());
        var drives = new List<TelegramTempDrive>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                var managedDirectory = ManagedDirectory(drive.RootDirectory.FullName);
                drives.Add(new TelegramTempDrive(
                    drive.RootDirectory.FullName,
                    drive.DriveType,
                    drive.AvailableFreeSpace,
                    Directory.Exists(managedDirectory)));
            }
            catch (IOException)
            {
                // A removable or disconnected drive can disappear during enumeration.
            }
            catch (UnauthorizedAccessException)
            {
                // An inaccessible drive is not a viable temporary location.
            }
        }

        var selected = SelectTempDirectory(systemTempDirectory, drives);
        try
        {
            Directory.CreateDirectory(selected);
            var probePath = Path.Combine(selected, $".write-test-{Guid.NewGuid():N}");
            using (File.Create(probePath, 1, FileOptions.DeleteOnClose))
            {
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Не удалось подготовить временную папку TG WS Proxy: {selected}. " +
                "Освободите место на системном диске или проверьте доступ к другому локальному диску.",
                exception);
        }

        return selected;
    }

    public static string SelectTempDirectory(
        string systemTempDirectory,
        IEnumerable<TelegramTempDrive> drives)
    {
        if (string.IsNullOrWhiteSpace(systemTempDirectory))
        {
            throw new ArgumentException("System temporary directory cannot be empty.", nameof(systemTempDirectory));
        }

        var normalizedSystemTemp = Path.GetFullPath(systemTempDirectory);
        var systemRoot = Path.GetPathRoot(normalizedSystemTemp);
        var candidates = drives
            .Where(drive => drive.DriveType == DriveType.Fixed)
            .Where(drive => drive.AvailableFreeSpace >= MinimumFreeSpaceBytes)
            .ToArray();
        var systemDrive = candidates.FirstOrDefault(drive =>
            string.Equals(
                NormalizeRoot(drive.RootPath),
                NormalizeRoot(systemRoot),
                StringComparison.OrdinalIgnoreCase));
        if (systemDrive is not null)
        {
            return normalizedSystemTemp;
        }

        var alternate = candidates
            .Where(drive => !string.Equals(
                NormalizeRoot(drive.RootPath),
                NormalizeRoot(systemRoot),
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(drive => drive.HasManagedDirectory)
            .ThenByDescending(drive => drive.AvailableFreeSpace)
            .FirstOrDefault();
        if (alternate is null)
        {
            throw new InvalidOperationException(
                "TG WS Proxy не запущен: для распаковки требуется не менее 256 МБ " +
                "на системном или другом локальном диске. Освободите место и повторите запуск.");
        }

        return ManagedDirectory(alternate.RootPath);
    }

    private static string ManagedDirectory(string? rootPath) =>
        Path.Combine(
            rootPath ?? throw new ArgumentException("Drive root cannot be empty.", nameof(rootPath)),
            ManagedDirectoryName,
            TelegramDirectoryName);

    private static string NormalizeRoot(string? rootPath) =>
        string.IsNullOrWhiteSpace(rootPath)
            ? string.Empty
            : Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
