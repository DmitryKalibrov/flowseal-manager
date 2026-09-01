using System.Text.Json.Serialization;

namespace FlowsealManager.Core.Models;

public sealed class ApplicationUpdateManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("releaseVersion")]
    public string ReleaseVersion { get; init; } = string.Empty;

    [JsonPropertyName("buildVersion")]
    public string BuildVersion { get; init; } = string.Empty;

    [JsonPropertyName("packages")]
    public IReadOnlyList<ApplicationPackageManifest> Packages { get; init; } = [];
}

public sealed class ApplicationPackageManifest
{
    [JsonPropertyName("runtimeIdentifier")]
    public string RuntimeIdentifier { get; init; } = string.Empty;

    [JsonPropertyName("assetName")]
    public string AssetName { get; init; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonPropertyName("executable")]
    public string Executable { get; init; } = string.Empty;
}

public sealed class ApplicationUpdatePlan
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("parentProcessId")]
    public int ParentProcessId { get; init; }

    [JsonPropertyName("currentReleaseVersion")]
    public string CurrentReleaseVersion { get; init; } = string.Empty;

    [JsonPropertyName("currentExecutableSha256")]
    public string CurrentExecutableSha256 { get; init; } = string.Empty;

    [JsonPropertyName("targetReleaseVersion")]
    public string TargetReleaseVersion { get; init; } = string.Empty;

    [JsonPropertyName("targetDirectory")]
    public string TargetDirectory { get; init; } = string.Empty;

    [JsonPropertyName("stagingDirectory")]
    public string StagingDirectory { get; init; } = string.Empty;

    [JsonPropertyName("executable")]
    public string Executable { get; init; } = string.Empty;

    [JsonPropertyName("backupDirectory")]
    public string BackupDirectory { get; init; } = string.Empty;

    [JsonPropertyName("successMarker")]
    public string SuccessMarker { get; init; } = string.Empty;

    [JsonPropertyName("startMinimized")]
    public bool StartMinimized { get; init; }

    [JsonPropertyName("files")]
    public IReadOnlyList<string> Files { get; init; } = [];

    [JsonPropertyName("existingFiles")]
    public IReadOnlyList<string> ExistingFiles { get; init; } = [];

    [JsonPropertyName("fileSha256")]
    public IReadOnlyDictionary<string, string> FileSha256 { get; init; } =
        new Dictionary<string, string>();
}

public sealed record PreparedApplicationUpdate(
    string ReleaseVersion,
    string BuildVersion,
    string RunnerExecutable,
    string PlanPath,
    string PlanSha256);
