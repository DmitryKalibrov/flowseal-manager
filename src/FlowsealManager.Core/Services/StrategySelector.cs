using FlowsealManager.Core.Infrastructure;
using FlowsealManager.Core.Models;

namespace FlowsealManager.Core.Services;

public sealed class StrategySelector
{
    private const int StrongestStrategiesToTune = 3;
    private const int FinalistsToConfirm = 3;

    private readonly ComponentProcessManager _processes;
    private readonly ConnectivityProbe _probe;
    private readonly ZapretCustomizationService _customizationService;
    private readonly FileLogger _logger;

    public StrategySelector(
        ComponentProcessManager processes,
        ConnectivityProbe probe,
        ZapretCustomizationService customizationService,
        FileLogger logger)
    {
        _processes = processes;
        _probe = probe;
        _customizationService = customizationService;
        _logger = logger;
    }

    public async Task<StrategySelectionResult> SelectAsync(
        string version,
        string zapretRoot,
        string? preferredStrategy,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var strategies = _processes.GetStrategies(version).ToList();
        if (!string.IsNullOrWhiteSpace(preferredStrategy) && strategies.Remove(preferredStrategy))
        {
            strategies.Insert(0, preferredStrategy);
        }

        var originalCustomization = _customizationService.Load(zapretRoot);
        var evaluations = new List<StrategyEvaluation>();
        var selectionCompleted = false;
        try
        {
            await ApplyCustomizationAsync(zapretRoot, originalCustomization, cancellationToken).ConfigureAwait(false);
            foreach (var strategy in strategies)
            {
                var evaluation = await EvaluateAsync(
                    version,
                    strategy,
                    originalCustomization,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                evaluations.Add(evaluation);

                var confirmed = await ConfirmPerfectAsync(
                    version,
                    zapretRoot,
                    evaluations,
                    evaluations.Count - 1,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                if (confirmed is not null)
                {
                    selectionCompleted = true;
                    return CreateResult(confirmed, evaluations);
                }
            }

            var strongestStrategies = evaluations
                .Where(evaluation => evaluation.FailureReason is null)
                .OrderByDescending(evaluation => evaluation.FirstCheck.CoverageScore)
                .ThenByDescending(evaluation => evaluation.FirstCheck.SuccessfulChecks)
                .Select(evaluation => evaluation.Strategy)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(StrongestStrategiesToTune)
                .ToArray();

            var parameterCandidates = BuildParameterCandidates(originalCustomization).Skip(1).ToArray();
            foreach (var strategy in strongestStrategies)
            {
                foreach (var customization in parameterCandidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ApplyCustomizationAsync(zapretRoot, customization, cancellationToken).ConfigureAwait(false);
                    var evaluation = await EvaluateAsync(
                        version,
                        strategy,
                        customization,
                        progress,
                        cancellationToken).ConfigureAwait(false);
                    evaluations.Add(evaluation);

                    var confirmed = await ConfirmPerfectAsync(
                        version,
                        zapretRoot,
                        evaluations,
                        evaluations.Count - 1,
                        progress,
                        cancellationToken).ConfigureAwait(false);
                    if (confirmed is not null)
                    {
                        selectionCompleted = true;
                        return CreateResult(confirmed, evaluations);
                    }
                }
            }

            var finalists = evaluations
                .Where(evaluation => evaluation.FailureReason is null &&
                                     evaluation.FirstCheck.SuccessfulChecks > 0 &&
                                     evaluation.ConfirmationCheck is null)
                .OrderByDescending(evaluation => evaluation.FirstCheck.CoverageScore)
                .ThenByDescending(evaluation => evaluation.FirstCheck.SuccessfulChecks)
                .ThenBy(evaluation => SuccessfulDuration(evaluation.FirstCheck))
                .Take(FinalistsToConfirm)
                .ToArray();

            foreach (var finalist in finalists)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var index = evaluations.FindIndex(evaluation => ReferenceEquals(evaluation, finalist));
                await ApplyCustomizationAsync(zapretRoot, finalist.Customization, cancellationToken).ConfigureAwait(false);
                evaluations[index] = await ConfirmAsync(
                    version,
                    finalist,
                    progress,
                    cancellationToken).ConfigureAwait(false);
            }

            var best = ChooseBest(evaluations, preferredStrategy, originalCustomization);
            if (best is null)
            {
                await _processes.StopZapretAsync(cancellationToken).ConfigureAwait(false);
                progress?.Report("Ни одна комбинация не дала устойчивых доступных соединений");
                return new StrategySelectionResult(null, null, null, evaluations);
            }

            await ApplyCustomizationAsync(zapretRoot, best.Customization, cancellationToken).ConfigureAwait(false);
            await _processes.StartZapretAsync(version, best.Strategy, cancellationToken).ConfigureAwait(false);
            progress?.Report(
                $"Лучшее покрытие: {best.StableSuccessfulChecks}/{best.BestReport.Results.Count} · " +
                $"{best.Strategy} · {ZapretCustomizationLabels.Summary(best.Customization)}");
            selectionCompleted = true;
            return CreateResult(best, evaluations);
        }
        finally
        {
            if (!selectionCompleted)
            {
                try
                {
                    await ApplyCustomizationAsync(zapretRoot, originalCustomization, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    await _logger.InfoAsync(
                        "Не удалось восстановить параметры zapret после прерванного подбора: " + exception.Message)
                        .ConfigureAwait(false);
                }
            }
        }
    }

    public static IReadOnlyList<ZapretCustomization> BuildParameterCandidates(ZapretCustomization current)
    {
        var candidates = new List<ZapretCustomization> { current };
        foreach (var ipSetMode in Enum.GetValues<IpSetMode>())
        {
            foreach (var gameFilterMode in Enum.GetValues<GameFilterMode>())
            {
                var candidate = current with
                {
                    GameFilterMode = gameFilterMode,
                    IpSetMode = ipSetMode
                };
                if (!candidates.Contains(candidate))
                {
                    candidates.Add(candidate);
                }
            }
        }

        return candidates;
    }

    public static StrategyEvaluation? ChooseBest(
        IEnumerable<StrategyEvaluation> evaluations,
        string? preferredStrategy,
        ZapretCustomization preferredCustomization)
    {
        return evaluations
            .Where(evaluation => evaluation.FailureReason is null &&
                                 evaluation.ConfirmationCheck is not null &&
                                 evaluation.StableSuccessfulChecks > 0)
            .OrderByDescending(evaluation => evaluation.StableCoverageScore)
            .ThenByDescending(evaluation => evaluation.StableSuccessfulChecks)
            .ThenByDescending(evaluation => evaluation.CombinedCoverageScore)
            .ThenByDescending(evaluation => evaluation.Customization == preferredCustomization)
            .ThenByDescending(evaluation => string.Equals(
                evaluation.Strategy,
                preferredStrategy,
                StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    private async Task<StrategyEvaluation> EvaluateAsync(
        string version,
        string strategy,
        ZapretCustomization customization,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var label = EvaluationLabel(strategy, customization);
        progress?.Report($"Проверяю {label}…");
        try
        {
            await _processes.StartZapretAsync(version, strategy, cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            var first = await _probe.CheckAsync(cancellationToken).ConfigureAwait(false);
            await _logger.InfoAsync(FormatReport(label, first), cancellationToken).ConfigureAwait(false);
            return new StrategyEvaluation(strategy, customization, first, null);
        }
        catch (ZapretConflictException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var empty = new HealthReport(DateTimeOffset.UtcNow, []);
            await _logger.InfoAsync($"{label}: ошибка запуска — {exception.Message}", cancellationToken)
                .ConfigureAwait(false);
            return new StrategyEvaluation(strategy, customization, empty, null, exception.Message);
        }
    }

    private async Task<StrategyEvaluation?> ConfirmPerfectAsync(
        string version,
        string zapretRoot,
        List<StrategyEvaluation> evaluations,
        int index,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var evaluation = evaluations[index];
        if (!evaluation.FirstCheck.AllRequiredAvailable)
        {
            return null;
        }

        await ApplyCustomizationAsync(zapretRoot, evaluation.Customization, cancellationToken).ConfigureAwait(false);
        var confirmation = await ConfirmAsync(version, evaluation, progress, cancellationToken).ConfigureAwait(false);
        evaluations[index] = confirmation;
        if (!confirmation.IsConfirmed)
        {
            return null;
        }

        progress?.Report(
            $"Подтверждён максимум: {evaluation.Strategy} · " +
            ZapretCustomizationLabels.Summary(evaluation.Customization));
        return confirmation;
    }

    private async Task<StrategyEvaluation> ConfirmAsync(
        string version,
        StrategyEvaluation evaluation,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var label = EvaluationLabel(evaluation.Strategy, evaluation.Customization);
        progress?.Report($"Подтверждаю {label}…");
        await _processes.StartZapretAsync(version, evaluation.Strategy, cancellationToken).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        var confirmation = await _probe.CheckAsync(cancellationToken).ConfigureAwait(false);
        await _logger.InfoAsync(
            FormatReport(label + " (повтор)", confirmation),
            cancellationToken).ConfigureAwait(false);
        return evaluation with { ConfirmationCheck = confirmation };
    }

    private Task ApplyCustomizationAsync(
        string zapretRoot,
        ZapretCustomization customization,
        CancellationToken cancellationToken) =>
        _customizationService.SaveAsync(zapretRoot, customization, cancellationToken);

    private static StrategySelectionResult CreateResult(
        StrategyEvaluation winner,
        IReadOnlyList<StrategyEvaluation> evaluations) =>
        new(winner.Strategy, winner.Customization, winner, evaluations);

    private static double SuccessfulDuration(HealthReport report) => report.Results
        .Where(result => result.IsSuccess)
        .Sum(result => result.Duration.TotalMilliseconds);

    private static string EvaluationLabel(string strategy, ZapretCustomization customization) =>
        $"{strategy} · {ZapretCustomizationLabels.Summary(customization)}";

    public static string FormatReport(string strategy, HealthReport report)
    {
        var details = string.Join(", ", report.Results.Select(result =>
            $"{result.Name}: {(result.IsSuccess ? "OK" : result.Detail)}"));
        return $"{strategy} — {details}";
    }
}
