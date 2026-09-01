namespace FlowsealManager.Core.Models;

public sealed class AppSettings
{
    public bool CheckUpdatesOnStart { get; set; } = true;

    public bool StartAtLogon { get; set; }

    public bool StartTelegramOnLaunch { get; set; } = true;

    public bool StartZapretOnLaunch { get; set; } = true;

    public bool AutoSelectStrategy { get; set; } = true;

    public string? TelegramVersion { get; set; }

    public string? ZapretVersion { get; set; }

    public string? SelectedStrategy { get; set; }

    public int SelectedCoverageScore { get; set; }

    public int MonitorIntervalMinutes { get; set; } = 5;

    public int FailedChecksBeforeSwitch { get; set; } = 3;

    public DateTimeOffset? LastSuccessfulCheckUtc { get; set; }
}
