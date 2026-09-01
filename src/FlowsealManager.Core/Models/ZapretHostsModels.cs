namespace FlowsealManager.Core.Models;

public sealed record ZapretHostsStatus(
    bool IsInstalled,
    bool? IsCurrent,
    int InstalledEntries,
    int OfficialEntries,
    DateTimeOffset? InstalledAt);

public sealed record ZapretHostsChange(
    ZapretHostsStatus Status,
    string BackupPath);
