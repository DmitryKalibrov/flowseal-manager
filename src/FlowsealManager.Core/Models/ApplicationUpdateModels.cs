namespace FlowsealManager.Core.Models;

public sealed record PreparedApplicationUpdate(
    string ReleaseVersion,
    string BuildVersion,
    string InstallerExecutable);
