using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using FlowsealManager.Core.Models;

namespace FlowsealManager.Core.Services;

public sealed class ZapretCustomizationService
{
    private const string EmptyDomainPlaceholder = "flowseal-manager.invalid";
    private const string EmptyIpPlaceholder = "203.0.113.113/32";

    public ZapretCustomization Load(string zapretRoot)
    {
        var fakeOptions = GetFakeOptions(zapretRoot);
        return new ZapretCustomization(
            ReadGameFilterMode(zapretRoot),
            ReadIpSetMode(zapretRoot),
            ReadUserList(zapretRoot, "list-general-user.txt", ["domain.example.abc", EmptyDomainPlaceholder]),
            ReadUserList(zapretRoot, "list-exclude-user.txt", ["domain.example.abc", EmptyDomainPlaceholder]),
            ReadUserList(zapretRoot, "ipset-general-user.txt", [EmptyIpPlaceholder]),
            ReadUserList(zapretRoot, "ipset-exclude-user.txt", [EmptyIpPlaceholder]),
            fakeOptions.ActiveDiscordFile,
            fakeOptions.ActiveGameFile);
    }

    public ZapretFakeOptions GetFakeOptions(string zapretRoot)
    {
        var bin = Path.Combine(zapretRoot, "bin");
        if (!Directory.Exists(bin))
        {
            return new ZapretFakeOptions([], null, null);
        }

        var files = Directory.EnumerateFiles(bin, "*.bin", SearchOption.TopDirectoryOnly)
            .Where(path => !Path.GetFileName(path).StartsWith("ACTIVE_", StringComparison.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new ZapretFakeOptions(
            files.Select(Path.GetFileName).OfType<string>().ToArray(),
            FindMatchingFile(Path.Combine(bin, "ACTIVE_DISCORD_UDP.bin"), files),
            FindMatchingFile(Path.Combine(bin, "ACTIVE_GAME_UDP.bin"), files));
    }

    public async Task SaveAsync(
        string zapretRoot,
        ZapretCustomization customization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(customization);
        var lists = Path.Combine(zapretRoot, "lists");
        var utils = Path.Combine(zapretRoot, "utils");
        var bin = Path.Combine(zapretRoot, "bin");
        if (!Directory.Exists(lists) || !Directory.Exists(bin))
        {
            throw new DirectoryNotFoundException("Каталог zapret повреждён или ещё не установлен.");
        }

        var includedDomains = NormalizeDomains(customization.IncludedDomains, EmptyDomainPlaceholder);
        var excludedDomains = NormalizeDomains(customization.ExcludedDomains, EmptyDomainPlaceholder);
        var includedIpRanges = NormalizeIpRanges(customization.IncludedIpRanges, EmptyIpPlaceholder);
        var excludedIpRanges = NormalizeIpRanges(customization.ExcludedIpRanges, EmptyIpPlaceholder);
        ValidateFakeTemplate(bin, customization.DiscordFakeFile);
        ValidateFakeTemplate(bin, customization.GameFakeFile);

        Directory.CreateDirectory(utils);
        await SaveGameFilterAsync(utils, customization.GameFilterMode, cancellationToken).ConfigureAwait(false);
        await SaveIpSetModeAsync(lists, customization.IpSetMode, cancellationToken).ConfigureAwait(false);
        await WriteAtomicAsync(
            Path.Combine(lists, "list-general-user.txt"),
            includedDomains,
            cancellationToken).ConfigureAwait(false);
        await WriteAtomicAsync(
            Path.Combine(lists, "list-exclude-user.txt"),
            excludedDomains,
            cancellationToken).ConfigureAwait(false);
        await WriteAtomicAsync(
            Path.Combine(lists, "ipset-general-user.txt"),
            includedIpRanges,
            cancellationToken).ConfigureAwait(false);
        await WriteAtomicAsync(
            Path.Combine(lists, "ipset-exclude-user.txt"),
            excludedIpRanges,
            cancellationToken).ConfigureAwait(false);

        CopyFakeTemplate(bin, customization.DiscordFakeFile, "ACTIVE_DISCORD_UDP.bin");
        CopyFakeTemplate(bin, customization.GameFakeFile, "ACTIVE_GAME_UDP.bin");
    }

    public static string NormalizeDomains(string value, string emptyPlaceholder = EmptyDomainPlaceholder)
    {
        var idn = new IdnMapping();
        var normalized = SplitUserLines(value)
            .Select(line =>
            {
                var candidate = line.Trim().TrimEnd('.');
                if (candidate.Contains("://", StringComparison.Ordinal) ||
                    candidate.Contains('/') ||
                    candidate.Contains(':') ||
                    candidate.Contains('*'))
                {
                    throw new InvalidDataException($"Некорректный домен: {line}. Укажите домен без протокола и пути.");
                }

                try
                {
                    candidate = idn.GetAscii(candidate).ToLowerInvariant();
                }
                catch (ArgumentException exception)
                {
                    throw new InvalidDataException($"Некорректный домен: {line}.", exception);
                }

                if (candidate.Length > 253 ||
                    candidate.Split('.').Length < 2 ||
                    candidate.Split('.').Any(label =>
                        label.Length is < 1 or > 63 ||
                        label.StartsWith('-') ||
                        label.EndsWith('-') ||
                        label.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')))
                {
                    throw new InvalidDataException($"Некорректный домен: {line}.");
                }

                return candidate;
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return BuildFileContent(normalized, emptyPlaceholder);
    }

    public static string NormalizeIpRanges(string value, string emptyPlaceholder = EmptyIpPlaceholder)
    {
        var normalized = SplitUserLines(value)
            .Select(line =>
            {
                var parts = line.Split('/', 2, StringSplitOptions.TrimEntries);
                if (!IPAddress.TryParse(parts[0], out var address))
                {
                    throw new InvalidDataException($"Некорректный IP-адрес: {line}.");
                }

                if (parts.Length == 1)
                {
                    return address.ToString();
                }

                var maximum = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
                if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) ||
                    prefix < 0 || prefix > maximum)
                {
                    throw new InvalidDataException($"Некорректная CIDR-маска: {line}.");
                }

                return $"{address}/{prefix}";
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return BuildFileContent(normalized, emptyPlaceholder);
    }

    private static GameFilterMode ReadGameFilterMode(string root)
    {
        var flag = Path.Combine(root, "utils", "game_filter.enabled");
        if (!File.Exists(flag))
        {
            return GameFilterMode.Disabled;
        }

        return File.ReadLines(flag).FirstOrDefault()?.Trim().ToLowerInvariant() switch
        {
            "all" => GameFilterMode.TcpAndUdp,
            "tcp" => GameFilterMode.TcpOnly,
            _ => GameFilterMode.UdpOnly
        };
    }

    private static async Task SaveGameFilterAsync(
        string utils,
        GameFilterMode mode,
        CancellationToken cancellationToken)
    {
        var flag = Path.Combine(utils, "game_filter.enabled");
        if (mode == GameFilterMode.Disabled)
        {
            if (File.Exists(flag)) File.Delete(flag);
            return;
        }

        var content = mode switch
        {
            GameFilterMode.TcpAndUdp => "all",
            GameFilterMode.TcpOnly => "tcp",
            GameFilterMode.UdpOnly => "udp",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };
        await WriteAtomicAsync(flag, content + Environment.NewLine, cancellationToken).ConfigureAwait(false);
    }

    private static IpSetMode ReadIpSetMode(string root) =>
        ReadIpSetModeFromFile(Path.Combine(root, "lists", "ipset-all.txt"));

    private static async Task SaveIpSetModeAsync(
        string lists,
        IpSetMode mode,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(lists, "ipset-all.txt");
        var backup = path + ".backup";
        if (mode == IpSetMode.OfficialList)
        {
            if (File.Exists(backup))
            {
                File.Copy(backup, path, true);
            }

            return;
        }

        if (File.Exists(path) && ReadIpSetModeFromFile(path) == IpSetMode.OfficialList)
        {
            File.Copy(path, backup, true);
        }

        var content = mode == IpSetMode.AnyIp
            ? string.Empty
            : EmptyIpPlaceholder + Environment.NewLine;
        await WriteAtomicAsync(path, content, cancellationToken).ConfigureAwait(false);
    }

    private static IpSetMode ReadIpSetModeFromFile(string path)
    {
        if (!File.Exists(path)) return IpSetMode.OfficialList;
        var entries = File.ReadLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToArray();
        if (entries.Length == 0) return IpSetMode.AnyIp;
        return entries.Length == 1 && string.Equals(entries[0], EmptyIpPlaceholder, StringComparison.OrdinalIgnoreCase)
            ? IpSetMode.NoIpRanges
            : IpSetMode.OfficialList;
    }

    private static string ReadUserList(string root, string fileName, IReadOnlyCollection<string> placeholders)
    {
        var path = Path.Combine(root, "lists", fileName);
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        return string.Join(Environment.NewLine, File.ReadLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 &&
                           !line.StartsWith('#') &&
                           !placeholders.Contains(line, StringComparer.OrdinalIgnoreCase)));
    }

    private static IEnumerable<string> SplitUserLines(string value) =>
        (value ?? string.Empty)
        .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(line => !line.StartsWith('#'));

    private static string BuildFileContent(IReadOnlyList<string> lines, string placeholder)
    {
        var actual = lines.Count == 0 ? [placeholder] : lines;
        return "# Управляется Flowseal Manager" + Environment.NewLine +
               string.Join(Environment.NewLine, actual) + Environment.NewLine;
    }

    private static async Task WriteAtomicAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void CopyFakeTemplate(string bin, string? selectedFile, string activeFileName)
    {
        if (string.IsNullOrWhiteSpace(selectedFile))
        {
            return;
        }

        ValidateFakeTemplate(bin, selectedFile);
        var fileName = Path.GetFileName(selectedFile);
        var source = Path.Combine(bin, fileName);
        var destination = Path.Combine(bin, activeFileName);
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(source, temporary, true);
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void ValidateFakeTemplate(string bin, string? selectedFile)
    {
        if (string.IsNullOrWhiteSpace(selectedFile)) return;
        var fileName = Path.GetFileName(selectedFile);
        if (!string.Equals(fileName, selectedFile, StringComparison.Ordinal) ||
            fileName.StartsWith("ACTIVE_", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(fileName), ".bin", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Выбран недопустимый файл подменного пакета.");
        }

        var source = Path.Combine(bin, fileName);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("Файл подменного пакета не найден.", source);
        }
    }

    private static string? FindMatchingFile(string activeFile, IReadOnlyList<string> candidates)
    {
        if (!File.Exists(activeFile))
        {
            return null;
        }

        var activeHash = SHA256.HashData(File.ReadAllBytes(activeFile));
        foreach (var candidate in candidates)
        {
            if (SHA256.HashData(File.ReadAllBytes(candidate)).SequenceEqual(activeHash))
            {
                return Path.GetFileName(candidate);
            }
        }

        return null;
    }
}
