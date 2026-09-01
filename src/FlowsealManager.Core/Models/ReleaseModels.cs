using System.Text.Json.Serialization;

namespace FlowsealManager.Core.Models;

public enum ComponentKind
{
    TelegramProxy,
    Zapret
}

public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; init; } = string.Empty;

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; init; } = string.Empty;

    [JsonPropertyName("assets")]
    public IReadOnlyList<GitHubAsset> Assets { get; init; } = [];
}

public sealed class GitHubAsset
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("browser_download_url")]
    public string DownloadUrl { get; init; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonPropertyName("digest")]
    public string? Digest { get; init; }
}

public sealed record ComponentUpdateResult(
    ComponentKind Component,
    string Version,
    bool Changed,
    string InstallDirectory,
    string Message);
