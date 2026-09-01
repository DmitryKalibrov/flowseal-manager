namespace FlowsealManager.Core.Services;

public static class ReleaseVersion
{
    public static bool IsNewer(string candidate, string current) =>
        Parse(candidate) > Parse(current);

    public static Version Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException("Release version is empty.");
        }

        var normalized = value.Trim();
        if (normalized.StartsWith('v'))
        {
            normalized = normalized[1..];
        }

        if (normalized.Contains('-', StringComparison.Ordinal) ||
            !Version.TryParse(normalized, out var version) ||
            version.Major < 0 || version.Minor < 0 || version.Build < 0)
        {
            throw new InvalidDataException($"Unsupported release version: {value}.");
        }

        return version;
    }

    public static string Normalize(string value)
    {
        var version = Parse(value);
        return $"v{version.Major}.{version.Minor}.{version.Build}";
    }
}
