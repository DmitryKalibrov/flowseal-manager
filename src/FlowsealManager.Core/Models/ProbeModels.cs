namespace FlowsealManager.Core.Models;

public enum ServiceKind
{
    YouTube,
    Discord
}

public sealed record ProbeResult(
    string Name,
    ServiceKind Service,
    bool IsSuccess,
    TimeSpan Duration,
    string Detail);

public sealed record HealthReport(
    DateTimeOffset CheckedAtUtc,
    IReadOnlyList<ProbeResult> Results)
{
    public bool YouTubeAvailable =>
        Results.Any(result => result.Service == ServiceKind.YouTube) &&
        Results.Where(result => result.Service == ServiceKind.YouTube).All(result => result.IsSuccess);

    public bool DiscordAvailable =>
        Results.Any(result => result.Service == ServiceKind.Discord) &&
        Results.Where(result => result.Service == ServiceKind.Discord).All(result => result.IsSuccess);

    public bool AllRequiredAvailable => YouTubeAvailable && DiscordAvailable;

    public int SuccessfulChecks => Results.Count(result => result.IsSuccess);

    public int CoverageScore => Results
        .Where(result => result.IsSuccess)
        .Sum(result => ProbeWeight(result.Name));

    public int MaximumCoverageScore => Results.Sum(result => ProbeWeight(result.Name));

    public static int ProbeWeight(string name) => name switch
    {
        "Discord API" => 5,
        "Discord Gateway WebSocket" => 5,
        "Discord CDN" => 3,
        "YouTube" => 4,
        "Googlevideo edge" => 4,
        "YouTube thumbnails" => 1,
        _ => 1
    };
}

public sealed record StrategyEvaluation(
    string Strategy,
    ZapretCustomization Customization,
    HealthReport FirstCheck,
    HealthReport? ConfirmationCheck,
    string? FailureReason = null)
{
    public bool IsConfirmed =>
        FirstCheck.AllRequiredAvailable && ConfirmationCheck?.AllRequiredAvailable == true;

    public int StableCoverageScore => ConfirmationCheck is null
        ? FirstCheck.CoverageScore
        : FirstCheck.Results
            .Where(first => first.IsSuccess &&
                            ConfirmationCheck.Results.Any(second =>
                                second.IsSuccess &&
                                string.Equals(second.Name, first.Name, StringComparison.Ordinal)))
            .Sum(result => HealthReport.ProbeWeight(result.Name));

    public int CombinedCoverageScore =>
        FirstCheck.CoverageScore + (ConfirmationCheck?.CoverageScore ?? 0);

    public int StableSuccessfulChecks => ConfirmationCheck is null
        ? FirstCheck.SuccessfulChecks
        : FirstCheck.Results.Count(first => first.IsSuccess &&
            ConfirmationCheck.Results.Any(second =>
                second.IsSuccess &&
                string.Equals(second.Name, first.Name, StringComparison.Ordinal)));

    public HealthReport BestReport => ConfirmationCheck is not null &&
                                      ConfirmationCheck.CoverageScore >= FirstCheck.CoverageScore
        ? ConfirmationCheck
        : FirstCheck;
}

public sealed record StrategySelectionResult(
    string? Strategy,
    ZapretCustomization? Customization,
    StrategyEvaluation? Winner,
    IReadOnlyList<StrategyEvaluation> Evaluations);
