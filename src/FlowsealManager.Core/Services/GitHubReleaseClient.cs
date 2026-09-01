using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using FlowsealManager.Core.Models;

namespace FlowsealManager.Core.Services;

public sealed class GitHubReleaseClient
{
    private readonly HttpClient _httpClient;

    public GitHubReleaseClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("FlowsealManager", "1.0"));
        }

        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<GitHubRelease> GetLatestAsync(
        ComponentKind component,
        CancellationToken cancellationToken = default)
    {
        var repository = component switch
        {
            ComponentKind.TelegramProxy => "tg-ws-proxy",
            ComponentKind.Zapret => "zapret-discord-youtube",
            _ => throw new ArgumentOutOfRangeException(nameof(component), component, null)
        };

        return await GetLatestAsync("Flowseal", repository, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitHubRelease> GetLatestAsync(
        string owner,
        string repository,
        CancellationToken cancellationToken = default)
    {
        if (!IsSafeRepositoryPart(owner) || !IsSafeRepositoryPart(repository))
        {
            throw new ArgumentException("Invalid GitHub repository name.");
        }

        var uri = $"https://api.github.com/repos/{owner}/{repository}/releases/latest";
        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("GitHub returned an empty release document.");
    }

    private static bool IsSafeRepositoryPart(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    public static GitHubAsset SelectAsset(ComponentKind component, GitHubRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        var expectedName = component switch
        {
            ComponentKind.TelegramProxy when RuntimeInformation.OSArchitecture == Architecture.Arm64 =>
                "TgWsProxy_windows_arm64.exe",
            ComponentKind.TelegramProxy => "TgWsProxy_windows.exe",
            ComponentKind.Zapret => $"zapret-discord-youtube-{release.TagName}.zip",
            _ => throw new ArgumentOutOfRangeException(nameof(component), component, null)
        };

        return release.Assets.FirstOrDefault(asset =>
                   string.Equals(asset.Name, expectedName, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidDataException($"The official release does not contain {expectedName}.");
    }
}
