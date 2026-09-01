namespace FlowsealManager.Core.Models;

public sealed record LegacyServiceInfo(string Name, bool IsRunning);

public sealed record LegacyCleanupResult(
    IReadOnlyList<string> RemovedServices,
    IReadOnlyList<string> Errors,
    int StoppedWinwsProcesses);
