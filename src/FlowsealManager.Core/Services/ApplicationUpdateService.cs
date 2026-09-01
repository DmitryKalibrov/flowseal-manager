using System.Diagnostics;
using System.Security.Cryptography;
using FlowsealManager.Core.Infrastructure;
using FlowsealManager.Core.Models;

namespace FlowsealManager.Core.Services;

public sealed class ApplicationUpdateService
{
    public const string RepositoryOwner = "DmitryKalibrov";
    public const string RepositoryName = "flowseal-manager";
    public const string InstallerAssetName = "FlowsealManager-Setup.exe";
    private const long MaximumInstallerBytes = 750_000_000;

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
        _ = currentExecutable;
        _ = startMinimized;
        CleanupAbandonedUpdates();
        var release = await _releases.GetLatestAsync(
            RepositoryOwner,
            RepositoryName,
            cancellationToken).ConfigureAwait(false);
        if (!ReleaseVersion.IsNewer(release.TagName, currentReleaseVersion))
        {
            return null;
        }

        var installerAsset = SelectInstallerAsset(release);
        var operationRoot = Path.Combine(_paths.ApplicationUpdatesRoot, Guid.NewGuid().ToString("N"));
        var installerPath = Path.Combine(operationRoot, InstallerAssetName);
        Directory.CreateDirectory(operationRoot);

        try
        {
            await _logger.InfoAsync(
                $"Загружаю Flowseal Manager {ReleaseVersion.Normalize(release.TagName)}…",
                cancellationToken).ConfigureAwait(false);
            await DownloadAndVerifyAsync(installerAsset, installerPath, cancellationToken).ConfigureAwait(false);
            var buildVersion = ValidateInstallerExecutable(installerPath, release.TagName);
            return new PreparedApplicationUpdate(
                ReleaseVersion.Normalize(release.TagName),
                buildVersion,
                installerPath);
        }
        catch
        {
            TryDeleteDirectory(operationRoot);
            throw;
        }
    }

    public static GitHubAsset SelectInstallerAsset(GitHubRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        var matches = release.Assets
            .Where(asset => string.Equals(asset.Name, InstallerAssetName, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException($"В релизе должен быть один {InstallerAssetName}.");
        }

        var asset = matches[0];
        if (asset.Size is < 1_000_000 or > MaximumInstallerBytes || !TryGetGitHubSha256(asset, out _))
        {
            throw new InvalidDataException("Некорректные метаданные файла обновления.");
        }

        ValidateApplicationAssetUrl(asset.DownloadUrl);
        return asset;
    }

    private async Task DownloadAndVerifyAsync(
        GitHubAsset asset,
        string destination,
        CancellationToken cancellationToken)
    {
        if (!TryGetGitHubSha256(asset, out var expectedDigest))
        {
            throw new InvalidDataException("GitHub не передал SHA-256 файла обновления.");
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
        if (length != asset.Size || length > MaximumInstallerBytes)
        {
            throw new InvalidDataException("Загруженный установщик имеет неожиданный размер.");
        }

        await using var stream = File.OpenRead(destination);
        var actualDigest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(actualDigest, expectedDigest))
        {
            throw new InvalidDataException("Проверка SHA-256 обновления не пройдена.");
        }
    }

    private static bool TryGetGitHubSha256(GitHubAsset asset, out byte[] digest)
    {
        digest = [];
        const string prefix = "sha256:";
        if (asset.Digest?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) != true)
        {
            return false;
        }

        var value = asset.Digest[prefix.Length..];
        if (value.Length != 64 || value.Any(character => !char.IsAsciiHexDigit(character)))
        {
            return false;
        }

        digest = Convert.FromHexString(value);
        return true;
    }

    private static string ValidateInstallerExecutable(string path, string releaseTag)
    {
        using (var stream = File.OpenRead(path))
        {
            if (stream.Length < 1_000_000 || stream.ReadByte() != 'M' || stream.ReadByte() != 'Z')
            {
                throw new InvalidDataException("Загружен некорректный установщик.");
            }
        }

        var version = FileVersionInfo.GetVersionInfo(path);
        var releaseVersion = ReleaseVersion.Parse(releaseTag).ToString(3);
        if (!Version.TryParse(version.FileVersion?.Trim(), out var buildVersion) ||
            buildVersion.Revision < 0 ||
            !string.Equals(version.ProductName?.Trim(), "Flowseal Manager", StringComparison.Ordinal) ||
            version.ProductVersion?.Trim().StartsWith(releaseVersion, StringComparison.Ordinal) != true)
        {
            throw new InvalidDataException("Версия установщика не совпадает с релизом.");
        }

        return buildVersion.ToString(4);
    }

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

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch
        {
            // A later maintenance run can remove an abandoned installer.
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
