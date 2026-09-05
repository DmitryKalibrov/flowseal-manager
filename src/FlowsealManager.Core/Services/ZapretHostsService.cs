using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using FlowsealManager.Core.Models;

namespace FlowsealManager.Core.Services;

public sealed class ZapretHostsService
{
    public const string OfficialSourceUrl =
        "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main/.service/hosts";

    public const string BeginMarker = "# BEGIN FLOWSEAL MANAGER / ZAPRET HOSTS";
    public const string EndMarker = "# END FLOWSEAL MANAGER / ZAPRET HOSTS";
    public const string RobloxCdnEntry = "18.65.39.105 tr.rbxcdn.com";

    private const int MaximumDownloadBytes = 256 * 1024;
    private readonly HttpClient _httpClient;
    private readonly string _hostsFile;
    private readonly string _backupRoot;

    public ZapretHostsService(
        HttpClient httpClient,
        string backupRoot,
        string? hostsFile = null)
    {
        _httpClient = httpClient;
        _backupRoot = backupRoot;
        _hostsFile = hostsFile ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "drivers",
            "etc",
            "hosts");
    }

    public string HostsFile => _hostsFile;

    public async Task<string> DownloadOfficialAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            OfficialSourceUrl + $"?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        request.Headers.UserAgent.ParseAdd("FlowsealManager/1.0");
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumDownloadBytes)
        {
            throw new InvalidDataException("Официальный список hosts имеет неожиданный размер.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (memory.Length + read > MaximumDownloadBytes)
            {
                throw new InvalidDataException("Официальный список hosts имеет неожиданный размер.");
            }

            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return BuildManagedContent(Encoding.UTF8.GetString(memory.ToArray()));
    }

    public ZapretHostsStatus Inspect(string? officialContent = null)
    {
        var raw = ReadHostsFile();
        var managed = ExtractManagedBlock(raw);
        var official = officialContent is null ? null : BuildManagedContent(officialContent);
        var installedEntries = managed is null ? 0 : CountEntries(managed.Content);
        return new ZapretHostsStatus(
            managed is not null,
            official is null || managed is null
                ? null
                : ManagedPayload(managed.Content) == official,
            installedEntries,
            official is null ? 0 : CountEntries(official),
            managed?.InstalledAt);
    }

    public async Task<ZapretHostsChange> InstallOrUpdateAsync(
        string officialContent,
        CancellationToken cancellationToken = default)
    {
        var normalized = BuildManagedContent(officialContent);
        var originalBytes = ReadHostsBytes();
        var raw = DecodePreservingBytes(originalBytes);
        var newline = DetectNewline(raw);
        var managed = ExtractManagedBlock(raw);
        var installedAt = DateTimeOffset.Now;
        var block = string.Join(
            newline,
            BeginMarker,
            $"# Source: {OfficialSourceUrl}",
            $"# Installed: {installedAt:O}",
            normalized.Replace("\n", newline, StringComparison.Ordinal),
            EndMarker);
        var changed = managed is null
            ? AppendBlock(raw, block, newline)
            : raw[..managed.Start] + block + newline + raw[managed.End..];
        var backup = await BackupAndWriteAsync(originalBytes, changed, cancellationToken).ConfigureAwait(false);
        return new ZapretHostsChange(Inspect(normalized), backup);
    }

    public async Task<ZapretHostsChange?> RemoveAsync(CancellationToken cancellationToken = default)
    {
        var originalBytes = ReadHostsBytes();
        var raw = DecodePreservingBytes(originalBytes);
        var managed = ExtractManagedBlock(raw);
        if (managed is null)
        {
            return null;
        }

        var changed = RemoveBlock(raw, managed);
        var backup = await BackupAndWriteAsync(originalBytes, changed, cancellationToken).ConfigureAwait(false);
        return new ZapretHostsChange(Inspect(), backup);
    }

    public static string NormalizeOfficial(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidDataException("Официальный список hosts пуст.");
        }

        var normalizedLines = new List<string>();
        var hostCount = 0;
        foreach (var sourceLine in SplitLines(content))
        {
            var line = sourceLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var commentIndex = line.IndexOf('#');
            if (commentIndex >= 0) line = line[..commentIndex].TrimEnd();
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || !IPAddress.TryParse(fields[0], out var address))
            {
                throw new InvalidDataException($"Некорректная строка официального hosts: {sourceLine}");
            }

            var domains = new List<string>();
            foreach (var field in fields.Skip(1))
            {
                domains.Add(NormalizeDomain(field));
                hostCount++;
            }

            normalizedLines.Add($"{address} {string.Join(' ', domains)}");
        }

        if (hostCount < 10 ||
            !normalizedLines.Any(line => line.EndsWith(" discord.com", StringComparison.OrdinalIgnoreCase)) ||
            !normalizedLines.Any(line => line.EndsWith(" raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Полученный файл не похож на официальный hosts Zapret.");
        }

        return string.Join('\n', normalizedLines);
    }

    public static string BuildManagedContent(string officialContent)
    {
        var normalized = NormalizeOfficial(officialContent);
        var lines = new List<string>();
        foreach (var line in SplitLines(normalized))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var domains = fields
                .Skip(1)
                .Where(domain => !domain.Equals("tr.rbxcdn.com", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (domains.Length > 0)
            {
                lines.Add($"{fields[0]} {string.Join(' ', domains)}");
            }
        }

        lines.Add(RobloxCdnEntry);
        return string.Join('\n', lines);
    }

    private async Task<string> BackupAndWriteAsync(
        byte[] originalBytes,
        string changed,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_backupRoot);
        var hash = Convert.ToHexString(SHA256.HashData(originalBytes))[..10];
        var backup = Path.Combine(
            _backupRoot,
            $"hosts-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{hash}.bak");
        await File.WriteAllBytesAsync(backup, originalBytes, cancellationToken).ConfigureAwait(false);

        var hostsDirectory = Path.GetDirectoryName(_hostsFile)
            ?? throw new InvalidOperationException("Не удалось определить каталог системного hosts.");
        Directory.CreateDirectory(hostsDirectory);
        var temporary = Path.Combine(hostsDirectory, $"hosts.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(
                temporary,
                Encoding.Latin1.GetBytes(changed),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporary, _hostsFile, true);
        }
        catch
        {
            if (!File.Exists(_hostsFile) && File.Exists(backup))
            {
                File.Copy(backup, _hostsFile, true);
            }

            throw;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }

        return backup;
    }

    private byte[] ReadHostsBytes() => File.Exists(_hostsFile) ? File.ReadAllBytes(_hostsFile) : [];

    private string ReadHostsFile() => DecodePreservingBytes(ReadHostsBytes());

    private static string DecodePreservingBytes(byte[] bytes)
    {
        if (bytes.Length >= 2 &&
            ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
        {
            throw new InvalidDataException(
                "Системный hosts сохранён в UTF-16. Сначала пересохраните его как ANSI или UTF-8.");
        }

        return Encoding.Latin1.GetString(bytes);
    }

    private static ManagedBlock? ExtractManagedBlock(string raw)
    {
        var begin = FindMarker(raw, BeginMarker);
        var end = FindMarker(raw, EndMarker);
        if (begin.Count == 0 && end.Count == 0) return null;
        if (begin.Count != 1 || end.Count != 1 || end[0].Start <= begin[0].Start)
        {
            throw new InvalidDataException(
                "Блок Flowseal Manager в hosts повреждён. Восстановите файл из резервной копии.");
        }

        var blockEnd = EndOfLine(raw, end[0].End);
        var contentStart = EndOfLine(raw, begin[0].End);
        if (contentStart < raw.Length && raw[contentStart] == '\r') contentStart++;
        if (contentStart < raw.Length && raw[contentStart] == '\n') contentStart++;
        var content = raw[contentStart..end[0].Start];
        DateTimeOffset? installedAt = null;
        var installedLine = SplitLines(content)
            .FirstOrDefault(line => line.TrimStart().StartsWith("# Installed:", StringComparison.Ordinal));
        if (installedLine is not null)
        {
            DateTimeOffset.TryParse(
                installedLine[(installedLine.IndexOf(':') + 1)..].Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed);
            installedAt = parsed == default ? null : parsed;
        }

        return new ManagedBlock(begin[0].Start, blockEnd, content, installedAt);
    }

    private static string ManagedPayload(string content) => string.Join(
        '\n',
        SplitLines(content)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#')));

    private static List<(int Start, int End)> FindMarker(string raw, string marker)
    {
        var result = new List<(int Start, int End)>();
        foreach (var line in SplitLinesWithEnd(raw))
        {
            var valueEnd = line.End;
            while (valueEnd > line.Start && raw[valueEnd - 1] is '\r' or '\n') valueEnd--;
            if (raw[line.Start..valueEnd].Trim() == marker)
            {
                result.Add((line.Start, valueEnd));
            }
        }
        return result;
    }

    private static IReadOnlyList<(int Start, int End)> SplitLinesWithEnd(string value)
    {
        var result = new List<(int Start, int End)>();
        var start = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\n') continue;
            result.Add((start, index + 1));
            start = index + 1;
        }

        if (start < value.Length || value.Length == 0) result.Add((start, value.Length));
        return result;
    }

    private static IEnumerable<string> SplitLines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static int CountEntries(string content) => SplitLines(content)
        .Count(line =>
        {
            var trimmed = line.Trim();
            return trimmed.Length > 0 && !trimmed.StartsWith('#');
        });

    private static string NormalizeDomain(string value)
    {
        var domain = value.Trim().TrimEnd('.');
        try
        {
            domain = new IdnMapping().GetAscii(domain).ToLowerInvariant();
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException($"Некорректный домен в официальном hosts: {value}", exception);
        }

        if (domain.Length is 0 or > 253 ||
            domain.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-'))
        {
            throw new InvalidDataException($"Некорректный домен в официальном hosts: {value}");
        }

        return domain;
    }

    private static string AppendBlock(string raw, string block, string newline)
    {
        if (raw.Length == 0) return block + newline;
        var separator = raw.EndsWith("\r\n", StringComparison.Ordinal) || raw.EndsWith('\n')
            ? string.Empty
            : newline;
        return raw + separator + block + newline;
    }

    private static string RemoveBlock(string raw, ManagedBlock block) =>
        raw[..block.Start] + raw[block.End..];

    private static string DetectNewline(string raw) =>
        raw.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static int EndOfLine(string raw, int offset)
    {
        while (offset < raw.Length && raw[offset] is not '\r' and not '\n') offset++;
        if (offset < raw.Length && raw[offset] == '\r') offset++;
        if (offset < raw.Length && raw[offset] == '\n') offset++;
        return offset;
    }

    private sealed record ManagedBlock(
        int Start,
        int End,
        string Content,
        DateTimeOffset? InstalledAt);
}
