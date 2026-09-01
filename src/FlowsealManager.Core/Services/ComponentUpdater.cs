using System.IO.Compression;
using System.Security.Cryptography;
using FlowsealManager.Core.Infrastructure;
using FlowsealManager.Core.Models;

namespace FlowsealManager.Core.Services;

public sealed class ComponentUpdater
{
    private const string TelegramLicense = """
        MIT License

        Copyright (c) 2026 Flowseal

        Permission is hereby granted, free of charge, to any person obtaining a copy
        of this software and associated documentation files (the "Software"), to deal
        in the Software without restriction, including without limitation the rights
        to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
        copies of the Software, and to permit persons to whom the Software is
        furnished to do so, subject to the following conditions:

        The above copyright notice and this permission notice shall be included in all
        copies or substantial portions of the Software.

        THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
        IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
        FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
        AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
        LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
        OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
        SOFTWARE.
        """;

    private const string ZapretLicense = """
        MIT License

        Copyright (c) 2016-2026 bol-van
        Copyright (c) 2024-2026 Flowseal

        Permission is hereby granted, free of charge, to any person obtaining a copy
        of this software and associated documentation files (the "Software"), to deal
        in the Software without restriction, including without limitation the rights
        to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
        copies of the Software, and to permit persons to whom the Software is
        furnished to do so, subject to the following conditions:

        The above copyright notice and this permission notice shall be included in all
        copies or substantial portions of the Software.

        THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
        IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
        FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
        AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
        LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
        OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
        SOFTWARE.

        ---

        This release contains binary files originating from the project by bol-van,
        available at https://github.com/bol-van/zapret/ under the MIT License.

        This release also includes and depends on WinDivert
        (https://github.com/basil00/WinDivert), licensed under your choice of:

        1. GNU Lesser General Public License (LGPL) Version 3, or
        2. GNU General Public License (GPL) Version 2.

        Binary distributions of WinDivert are included as-is, without modification.
        The corresponding source code and license terms are available at the URL above.

        Notice copied from the official repository:
        https://github.com/Flowseal/zapret-discord-youtube/blob/main/LICENSE.txt
        """;

    private static readonly string[] UserFiles =
    [
        "list-general-user.txt",
        "list-exclude-user.txt",
        "ipset-general-user.txt",
        "ipset-exclude-user.txt"
    ];

    private readonly HttpClient _httpClient;
    private readonly GitHubReleaseClient _releases;
    private readonly AppPaths _paths;
    private readonly FileLogger _logger;

    public ComponentUpdater(
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

    public async Task<ComponentUpdateResult> EnsureLatestAsync(
        ComponentKind component,
        string? currentVersion,
        CancellationToken cancellationToken = default)
    {
        var release = await _releases.GetLatestAsync(component, cancellationToken).ConfigureAwait(false);
        var destination = component == ComponentKind.TelegramProxy
            ? _paths.TelegramDirectory(release.TagName)
            : _paths.ZapretDirectory(release.TagName);

        if (IsValidInstallation(component, destination))
        {
            return new ComponentUpdateResult(
                component,
                release.TagName,
                !string.Equals(currentVersion, release.TagName, StringComparison.OrdinalIgnoreCase),
                destination,
                $"Установлена актуальная версия {release.TagName}");
        }

        var asset = GitHubReleaseClient.SelectAsset(component, release);
        await _logger.InfoAsync($"Загрузка {asset.Name} из официального релиза Flowseal…", cancellationToken)
            .ConfigureAwait(false);

        var download = Path.Combine(_paths.TempRoot, $"{Guid.NewGuid():N}-{asset.Name}");
        var staging = Path.Combine(_paths.TempRoot, $"staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_paths.TempRoot);

        try
        {
            await DownloadAndVerifyAsync(asset, download, cancellationToken).ConfigureAwait(false);

            if (component == ComponentKind.TelegramProxy)
            {
                Directory.CreateDirectory(staging);
                var executable = Path.Combine(staging, "TgWsProxy.exe");
                File.Move(download, executable);
                ValidatePortableExecutable(executable);
                await File.WriteAllTextAsync(
                    Path.Combine(staging, "LICENSE.Flowseal.txt"),
                    TelegramLicense,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Directory.CreateDirectory(staging);
                ExtractSafely(download, staging);
                var root = FindZapretRoot(staging);
                if (!string.Equals(root, staging, StringComparison.OrdinalIgnoreCase))
                {
                    var normalized = Path.Combine(_paths.TempRoot, $"normalized-{Guid.NewGuid():N}");
                    Directory.Move(root, normalized);
                    Directory.Delete(staging, true);
                    staging = normalized;
                }

                ValidateZapret(staging);
                PreserveZapretUserFiles(currentVersion, staging);
                await File.WriteAllTextAsync(
                    Path.Combine(staging, "LICENSE.txt"),
                    ZapretLicense,
                    cancellationToken).ConfigureAwait(false);
            }

            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, true);
            }

            Directory.Move(staging, destination);
            await _logger.InfoAsync($"{component}: версия {release.TagName} установлена.", cancellationToken)
                .ConfigureAwait(false);

            return new ComponentUpdateResult(
                component,
                release.TagName,
                true,
                destination,
                $"Обновлено до {release.TagName}");
        }
        finally
        {
            if (File.Exists(download))
            {
                File.Delete(download);
            }

            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, true);
            }
        }
    }

    public static bool IsValidInstallation(ComponentKind component, string path) => component switch
    {
        ComponentKind.TelegramProxy => File.Exists(Path.Combine(path, "TgWsProxy.exe")),
        ComponentKind.Zapret =>
            File.Exists(Path.Combine(path, "service.bat")) &&
            File.Exists(Path.Combine(path, "bin", "winws.exe")) &&
            Directory.EnumerateFiles(Path.Combine(path, "bin"), "WinDivert*.sys").Any(),
        _ => false
    };

    public static void ExtractSafely(string archive, string destination)
    {
        const long maximumUncompressedBytes = 1_500_000_000;
        var destinationRoot = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(archive);
        long uncompressedBytes = 0;
        foreach (var entry in zip.Entries)
        {
            uncompressedBytes = checked(uncompressedBytes + entry.Length);
            if (uncompressedBytes > maximumUncompressedBytes)
            {
                throw new InvalidDataException("The release archive is unexpectedly large.");
            }

            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The release archive contains an unsafe path.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
    }

    private async Task DownloadAndVerifyAsync(
        GitHubAsset asset,
        string destination,
        CancellationToken cancellationToken)
    {
        ValidateOfficialAssetUrl(asset.DownloadUrl);
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
        if (length == 0 || asset.Size > 0 && length != asset.Size)
        {
            throw new InvalidDataException("The downloaded release asset has an unexpected size.");
        }

        if (asset.Digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true)
        {
            await using var stream = File.OpenRead(destination);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
                .ToLowerInvariant();
            var expected = asset.Digest["sha256:".Length..].ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actual),
                    Convert.FromHexString(expected)))
            {
                throw new InvalidDataException("GitHub SHA-256 verification failed.");
            }
        }
    }

    private static void ValidateOfficialAssetUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith("/Flowseal/", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.Contains("/releases/download/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("GitHub returned an unexpected release asset URL.");
        }
    }

    private static void ValidatePortableExecutable(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        if (stream.Length < 1024 || stream.ReadByte() != 'M' || stream.ReadByte() != 'Z')
        {
            throw new InvalidDataException("Telegram release asset is not a valid Windows executable.");
        }
    }

    private static string FindZapretRoot(string staging)
    {
        if (File.Exists(Path.Combine(staging, "service.bat")))
        {
            return staging;
        }

        var candidates = Directory.EnumerateFiles(staging, "service.bat", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .Where(path => path is not null)
            .Cast<string>()
            .ToArray();
        return candidates.Length == 1
            ? candidates[0]
            : throw new InvalidDataException("The zapret archive has an unexpected layout.");
    }

    private static void ValidateZapret(string root)
    {
        if (!IsValidInstallation(ComponentKind.Zapret, root) ||
            !Directory.EnumerateFiles(root, "general*.bat", SearchOption.TopDirectoryOnly).Any())
        {
            throw new InvalidDataException("The zapret release is incomplete.");
        }
    }

    private void PreserveZapretUserFiles(string? currentVersion, string newRoot)
    {
        if (string.IsNullOrWhiteSpace(currentVersion))
        {
            return;
        }

        var oldRoot = _paths.ZapretDirectory(currentVersion);
        foreach (var fileName in UserFiles)
        {
            var source = Path.Combine(oldRoot, "lists", fileName);
            var destination = Path.Combine(newRoot, "lists", fileName);
            if (File.Exists(source))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, true);
            }
        }

        PreserveIpSetMode(oldRoot, newRoot);

        var gameFilter = Path.Combine(oldRoot, "utils", "game_filter.enabled");
        if (File.Exists(gameFilter))
        {
            Directory.CreateDirectory(Path.Combine(newRoot, "utils"));
            File.Copy(gameFilter, Path.Combine(newRoot, "utils", "game_filter.enabled"), true);
        }

        foreach (var activeFake in new[] { "ACTIVE_DISCORD_UDP.bin", "ACTIVE_GAME_UDP.bin" })
        {
            var source = Path.Combine(oldRoot, "bin", activeFake);
            if (File.Exists(source))
            {
                Directory.CreateDirectory(Path.Combine(newRoot, "bin"));
                File.Copy(source, Path.Combine(newRoot, "bin", activeFake), true);
            }
        }
    }

    private static void PreserveIpSetMode(string oldRoot, string newRoot)
    {
        var oldPath = Path.Combine(oldRoot, "lists", "ipset-all.txt");
        var newPath = Path.Combine(newRoot, "lists", "ipset-all.txt");
        if (!File.Exists(oldPath) || !File.Exists(newPath))
        {
            return;
        }

        var oldEntries = File.ReadLines(oldPath)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToArray();
        var wasAnyIp = oldEntries.Length == 0;
        var wasNoIpRanges = oldEntries.Length == 1 &&
                            string.Equals(oldEntries[0], "203.0.113.113/32", StringComparison.OrdinalIgnoreCase);
        if (!wasAnyIp && !wasNoIpRanges)
        {
            return;
        }

        File.Copy(newPath, newPath + ".backup", true);
        File.WriteAllText(
            newPath,
            wasAnyIp ? string.Empty : "203.0.113.113/32" + Environment.NewLine);
    }
}
