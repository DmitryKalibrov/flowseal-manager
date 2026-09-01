using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using FlowsealManager.Core.Infrastructure;
using FlowsealManager.Core.Models;

namespace FlowsealManager.Core.Services;

public sealed class ApplicationUpdateService
{
    public const string RepositoryOwner = "DmitryKalibrov";
    public const string RepositoryName = "flowseal-manager";
    public const string ManifestAssetName = "update-manifest.json";
    private const long MaximumManifestBytes = 1_048_576;
    private const long MaximumPackageBytes = 500_000_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly HttpClient _httpClient;
    private readonly GitHubReleaseClient _releases;
    private readonly AppPaths _paths;
    private readonly FileLogger _logger;

    public ApplicationUpdateService(
        HttpClient httpClient,
        GitHubReleaseClient releases,
        AppPaths paths,
        FileLogger logger)
    {
        _httpClient = httpClient;
        _releases = releases;
        _paths = paths;
        _logger = logger;
    }

    public async Task<PreparedApplicationUpdate?> PrepareLatestAsync(
        string currentReleaseVersion,
        string currentExecutable,
        bool startMinimized,
        CancellationToken cancellationToken = default)
    {
        CleanupAbandonedUpdates();
        var release = await _releases.GetLatestAsync(
            RepositoryOwner,
            RepositoryName,
            cancellationToken).ConfigureAwait(false);
        if (!ReleaseVersion.IsNewer(release.TagName, currentReleaseVersion))
        {
            return null;
        }

        var manifestAsset = FindAsset(release, ManifestAssetName);
        var operationRoot = Path.Combine(_paths.ApplicationUpdatesRoot, Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(operationRoot, "staging");
        var packagePath = Path.Combine(operationRoot, "package.zip");
        var manifestPath = Path.Combine(operationRoot, ManifestAssetName);
        Directory.CreateDirectory(operationRoot);

        try
        {
            await DownloadAndVerifyAsync(
                manifestAsset,
                manifestPath,
                MaximumManifestBytes,
                null,
                cancellationToken).ConfigureAwait(false);
            var manifest = await ReadManifestAsync(manifestPath, release.TagName, cancellationToken)
                .ConfigureAwait(false);
            var package = SelectPackage(manifest);
            var packageAsset = FindAsset(release, package.AssetName);
            if (packageAsset.Size != package.Size)
            {
                throw new InvalidDataException("Размер пакета не совпадает с манифестом обновления.");
            }

            await _logger.InfoAsync(
                $"Загружаю Flowseal Manager {ReleaseVersion.Normalize(manifest.ReleaseVersion)}…",
                cancellationToken).ConfigureAwait(false);
            await DownloadAndVerifyAsync(
                packageAsset,
                packagePath,
                MaximumPackageBytes,
                package.Sha256,
                cancellationToken).ConfigureAwait(false);

            Directory.CreateDirectory(staging);
            ComponentUpdater.ExtractSafely(packagePath, staging);
            var executable = Path.Combine(staging, package.Executable);
            ValidateApplicationExecutable(executable, manifest);
            var files = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(staging, file))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (files.Length == 0 || !files.Contains(package.Executable, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Пакет обновления не содержит приложение.");
            }

            var fileSha256 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var relativePath in files)
            {
                fileSha256[relativePath] = await ComputeSha256Async(
                    Path.Combine(staging, relativePath),
                    cancellationToken).ConfigureAwait(false);
            }
            var targetDirectory = Path.GetDirectoryName(Path.GetFullPath(currentExecutable))!;
            var existingFiles = Directory.EnumerateFiles(targetDirectory, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(targetDirectory, file))
                .Where(IsManagedApplicationFile)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var planPath = Path.Combine(operationRoot, "update-plan.json");
            var runnerDirectory = Path.Combine(operationRoot, "runner");
            Directory.CreateDirectory(runnerDirectory);
            var runner = Path.Combine(runnerDirectory, "FlowsealManager.Updater.exe");
            File.Copy(executable, runner, true);
            var plan = new ApplicationUpdatePlan
            {
                SchemaVersion = 1,
                ParentProcessId = Environment.ProcessId,
                CurrentReleaseVersion = ReleaseVersion.Normalize(currentReleaseVersion),
                CurrentExecutableSha256 = await ComputeSha256Async(currentExecutable, cancellationToken)
                    .ConfigureAwait(false),
                TargetReleaseVersion = ReleaseVersion.Normalize(manifest.ReleaseVersion),
                TargetDirectory = targetDirectory,
                StagingDirectory = staging,
                Executable = package.Executable,
                BackupDirectory = Path.Combine(operationRoot, "backup"),
                SuccessMarker = Path.Combine(operationRoot, "started.ok"),
                StartMinimized = startMinimized,
                Files = files,
                ExistingFiles = existingFiles,
                FileSha256 = fileSha256
            };
            await WriteJsonAtomicallyAsync(planPath, plan, cancellationToken).ConfigureAwait(false);
            var planSha256 = await ComputeSha256Async(planPath, cancellationToken).ConfigureAwait(false);
            return new PreparedApplicationUpdate(
                ReleaseVersion.Normalize(manifest.ReleaseVersion),
                manifest.BuildVersion,
                runner,
                planPath,
                planSha256);
        }
        catch
        {
            TryDeleteDirectory(operationRoot);
            throw;
        }
        finally
        {
            if (File.Exists(packagePath)) File.Delete(packagePath);
        }
    }

    public static ApplicationPackageManifest SelectPackage(ApplicationUpdateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var runtimeIdentifier = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException("Поддерживаются только Windows x64 и ARM64.")
        };
        return manifest.Packages.SingleOrDefault(package =>
                   string.Equals(package.RuntimeIdentifier, runtimeIdentifier, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidDataException($"В релизе нет пакета {runtimeIdentifier}.");
    }

    public static void ValidateManifest(ApplicationUpdateManifest manifest, string releaseTag)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SchemaVersion != 1 ||
            !string.Equals(
                ReleaseVersion.Normalize(manifest.ReleaseVersion),
                ReleaseVersion.Normalize(releaseTag),
                StringComparison.OrdinalIgnoreCase) ||
            !Version.TryParse(manifest.BuildVersion, out var buildVersion) ||
            buildVersion.Revision < 0 ||
            manifest.Packages.Count is < 1 or > 4)
        {
            throw new InvalidDataException("Некорректный манифест обновления.");
        }

        foreach (var package in manifest.Packages)
        {
            var expectedAsset = $"FlowsealManager-{package.RuntimeIdentifier}.zip";
            if (package.RuntimeIdentifier is not ("win-x64" or "win-arm64") ||
                !string.Equals(package.AssetName, expectedAsset, StringComparison.Ordinal) ||
                !string.Equals(package.Executable, "FlowsealManager.exe", StringComparison.Ordinal) ||
                package.Size is < 1_000_000 or > MaximumPackageBytes ||
                package.Sha256.Length != 64 ||
                package.Sha256.Any(character => !char.IsAsciiHexDigit(character)))
            {
                throw new InvalidDataException("Некорректное описание пакета обновления.");
            }
        }

        if (manifest.Packages.Select(package => package.RuntimeIdentifier)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Packages.Count)
        {
            throw new InvalidDataException("Манифест содержит повторяющиеся пакеты.");
        }
    }

    private async Task<ApplicationUpdateManifest> ReadManifestAsync(
        string path,
        string releaseTag,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var manifest = await JsonSerializer.DeserializeAsync<ApplicationUpdateManifest>(
                stream,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("Манифест обновления пуст.");
        ValidateManifest(manifest, releaseTag);
        return manifest;
    }

    private async Task DownloadAndVerifyAsync(
        GitHubAsset asset,
        string destination,
        long maximumBytes,
        string? expectedManifestDigest,
        CancellationToken cancellationToken)
    {
        ValidateApplicationAssetUrl(asset.DownloadUrl);
        if (asset.Size is <= 0 || asset.Size > maximumBytes)
        {
            throw new InvalidDataException("Недопустимый размер файла обновления.");
        }

        using var response = await _httpClient.GetAsync(
            asset.DownloadUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var output = new FileStream(
                         destination,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         81920,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }

        var length = new FileInfo(destination).Length;
        if (length != asset.Size || length > maximumBytes)
        {
            throw new InvalidDataException("Загруженный файл имеет неожиданный размер.");
        }

        await using var stream = File.OpenRead(destination);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        var githubDigest = asset.Digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true
            ? asset.Digest["sha256:".Length..]
            : null;
        if ((githubDigest is not null && !DigestEquals(actual, githubDigest)) ||
            (expectedManifestDigest is not null && !DigestEquals(actual, expectedManifestDigest)))
        {
            throw new InvalidDataException("Проверка SHA-256 обновления не пройдена.");
        }
    }

    private static bool DigestEquals(string actual, string expected)
    {
        if (expected.Length != 64 || expected.Any(character => !char.IsAsciiHexDigit(character)))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(actual),
            Convert.FromHexString(expected));
    }

    private static GitHubAsset FindAsset(GitHubRelease release, string name) =>
        release.Assets.SingleOrDefault(asset => string.Equals(asset.Name, name, StringComparison.Ordinal))
        ?? throw new InvalidDataException($"В релизе отсутствует {name}.");

    private static void ValidateApplicationAssetUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith(
                $"/{RepositoryOwner}/{RepositoryName}/releases/download/",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("GitHub вернул неожиданный адрес обновления.");
        }
    }

    private static void ValidateApplicationExecutable(string path, ApplicationUpdateManifest manifest)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length < 1_000_000 || stream.ReadByte() != 'M' || stream.ReadByte() != 'Z')
        {
            throw new InvalidDataException("В пакете находится некорректный исполняемый файл.");
        }

        var version = FileVersionInfo.GetVersionInfo(path);
        if (!string.Equals(version.FileVersion, manifest.BuildVersion, StringComparison.Ordinal) ||
            version.ProductVersion?.StartsWith(manifest.ReleaseVersion + "+", StringComparison.Ordinal) != true)
        {
            throw new InvalidDataException("Версии исполняемого файла не совпадают с манифестом.");
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static bool IsManagedApplicationFile(string relativePath) =>
        string.Equals(relativePath, "FlowsealManager.exe", StringComparison.OrdinalIgnoreCase) ||
        relativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
        relativePath.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) ||
        relativePath.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase);

    private static async Task WriteJsonAtomicallyAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, path, true);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch
        {
            // A later maintenance run can remove an abandoned staging directory.
        }
    }

    private void CleanupAbandonedUpdates()
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(_paths.ApplicationUpdatesRoot))
            {
                if (Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddDays(-7))
                {
                    TryDeleteDirectory(directory);
                }
            }
        }
        catch
        {
            // Cleanup must never block a new update check.
        }
    }
}
