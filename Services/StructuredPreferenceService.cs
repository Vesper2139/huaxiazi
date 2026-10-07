using System;
using System.Linq;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

public sealed class StructuredPreferenceService
{
    public void RecordEdit(ExpressionPreferenceProfile profile, string? generated, string? edited, string? scenario)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var before = generated ?? string.Empty;
        var after = edited ?? string.Empty;
        profile.EditCount++;
        if (before.Length > 0 && after.Length <= before.Length * 0.8) profile.ShorteningEdits++;
        if (before.Length > 0 && after.Length >= before.Length * 1.25) profile.ExpansionEdits++;
        if (!string.IsNullOrWhiteSpace(scenario))
        {
            var key = scenario.Trim();
            profile.ScenarioUsage.TryGetValue(key, out var count);
            profile.ScenarioUsage[key] = count + 1;
        }
        foreach (var phrase in CannedExpressionCatalog.Values.Where(phrase => before.Contains(phrase, StringComparison.Ordinal) && !after.Contains(phrase, StringComparison.Ordinal)))
        {
            profile.RemovedCannedExpressions.TryGetValue(phrase, out var count);
            profile.RemovedCannedExpressions[phrase] = count + 1;
        }
    }

    public void RecordEdit(ExpressionPreferenceProfile profile, string? generated, string? edited, ApplicationMode task, string? scenario)
    {
        var normalizedScenario = NormalizeScenario(task, scenario);
        RecordEdit(profile, generated, edited, normalizedScenario);
        var signals = GetOrCreateSignals(profile, task, normalizedScenario);
        signals.EditCount++;
        var before = generated ?? string.Empty;
        var after = edited ?? string.Empty;
        if (before.Length > 0 && after.Length <= before.Length * 0.8) signals.ShorteningEdits++;
        if (before.Length > 0 && after.Length >= before.Length * 1.25) signals.ExpansionEdits++;
        signals.UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    public string BuildInstructions(ExpressionPreferenceProfile profile, ApplicationMode task) =>
        BuildInstructions(profile, task, null, includeLegacyForbiddenExpressions: true);

    public string BuildInstructions(ExpressionPreferenceProfile profile, ApplicationMode task, string? scenario)
        => BuildInstructions(profile, task, scenario, includeLegacyForbiddenExpressions: true);

    public string BuildInstructions(ExpressionPreferenceProfile profile, ApplicationMode task, string? scenario, bool includeLegacyForbiddenExpressions)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Normalize();
        var taskKey = GetTaskPreferenceKey(task);
        ExpressionPreferenceSet? preference = null;
        if (!string.IsNullOrWhiteSpace(scenario) &&
            profile.TaskPreferences.TryGetValue(GetTaskPreferenceKey(task, scenario), out var scenarioPreference) &&
            scenarioPreference.UserConfirmed)
            preference = scenarioPreference;
        else if (profile.TaskPreferences.TryGetValue(taskKey, out var taskPreference))
            preference = taskPreference;
        var parts = new System.Collections.Generic.List<string>();
        if (preference?.UserConfirmed == true)
        {
            preference.Normalize();
            if (preference.PreferredLength == "concise") parts.Add("偏好简洁、直接的成稿");
            else if (preference.PreferredLength == "detailed") parts.Add("偏好保留较完整的背景和步骤");
            if (preference.PreferredTone == "professional") parts.Add("偏好专业、克制的语气");
            else if (preference.PreferredTone == "warm") parts.Add("偏好温和、亲切的语气");
            parts.Add(preference.PreserveOriginalWording
                ? "尽量保留原有措辞，只修改确有必要的部分"
                : "允许为清晰度和自然度调整原有措辞，但不得改变事实与立场");
        }

        var forbidden = (preference?.UserConfirmed == true ? preference.ForbiddenExpressions : [])
            .Concat(includeLegacyForbiddenExpressions ? profile.ForbiddenExpressions : [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToArray();
        if (forbidden.Length > 0) parts.Add("避免使用这些表达：" + string.Join("、", forbidden));
        return parts.Count == 0 ? string.Empty : "用户确认的本任务/场景长期偏好（低于本轮明确要求）：" + string.Join("；", parts);
    }

    public static string GetTaskPreferenceKey(ApplicationMode task, string? scenario = null)
    {
        var taskKey = task == ApplicationMode.Polish ? "polish" : "prompt-optimize";
        var normalizedScenario = NormalizeScenario(task, scenario);
        return string.IsNullOrWhiteSpace(normalizedScenario) ? taskKey : taskKey + "|" + normalizedScenario;
    }

    public void RecordAcceptance(ExpressionPreferenceProfile profile) => profile.AcceptedCount++;
    public void RecordRetry(ExpressionPreferenceProfile profile) => profile.RetryCount++;
    public void RecordUndo(ExpressionPreferenceProfile profile) => profile.UndoCount++;
    public void RecordStyleChoice(ExpressionPreferenceProfile profile) => profile.StyleChoiceCount++;

    public void RecordStyleChoice(ExpressionPreferenceProfile profile, string? outputStyle)
    {
        ArgumentNullException.ThrowIfNull(profile);
        RecordStyleChoice(profile);
        if (!OutputStyleCatalog.Values.Contains(outputStyle, StringComparer.Ordinal)) return;
        var normalized = outputStyle!;
        profile.StyleChoiceUsage ??= new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);
        profile.StyleChoiceUsage.TryGetValue(normalized, out var count);
        profile.StyleChoiceUsage[normalized] = count + 1;
    }

    public void RecordAcceptance(ExpressionPreferenceProfile profile, ApplicationMode task, string? scenario)
    {
        RecordAcceptance(profile);
        var signals = GetOrCreateSignals(profile, task, scenario);
        signals.AcceptedCount++;
        signals.UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    public void RecordRetry(ExpressionPreferenceProfile profile, ApplicationMode task, string? scenario)
    {
        RecordRetry(profile);
        var signals = GetOrCreateSignals(profile, task, scenario);
        signals.RetryCount++;
        signals.UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    public void RecordUndo(ExpressionPreferenceProfile profile, ApplicationMode task, string? scenario)
    {
        RecordUndo(profile);
        var signals = GetOrCreateSignals(profile, task, scenario);
        signals.UndoCount++;
        signals.UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Correlates a finished output's edit direction with the user's outcome without retaining its text.
    /// Ambiguous or unfinished outcomes are intentionally excluded from the preference evidence.
    /// </summary>
    public bool RecordObservedOutputOutcome(
        ExpressionPreferenceProfile profile,
        ApplicationMode task,
        string? scenario,
        bool wasShortened,
        bool wasExpanded,
        bool accepted,
        bool rejected,
        string? outputStyle = null,
        System.Collections.Generic.IReadOnlyCollection<string>? removedCannedExpressions = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (accepted == rejected) return false;

        var normalizedStyle = OutputStyleCatalog.Values.Contains(outputStyle, StringComparer.Ordinal)
            ? outputStyle
            : null;
        var normalizedRemovedExpressions = (removedCannedExpressions ?? Array.Empty<string>())
            .Where(phrase => CannedExpressionCatalog.Values.Contains(phrase, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (!wasShortened && !wasExpanded && normalizedStyle is null && normalizedRemovedExpressions.Length == 0) return false;

        var signals = GetOrCreateSignals(profile, task, scenario);
        if (accepted)
        {
            if (wasShortened) signals.AcceptedShortenedOutputs++;
            if (wasExpanded) signals.AcceptedExpandedOutputs++;
            if (normalizedStyle is not null)
            {
                signals.AcceptedOutputStyles ??= new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);
                signals.AcceptedOutputStyles.TryGetValue(normalizedStyle, out var count);
                signals.AcceptedOutputStyles[normalizedStyle] = count + 1;
            }
            RecordCannedExpressionOutcomes(signals.AcceptedRemovedCannedExpressions, normalizedRemovedExpressions);
        }
        else
        {
            if (wasShortened) signals.RejectedShortenedOutputs++;
            if (wasExpanded) signals.RejectedExpandedOutputs++;
            if (normalizedStyle is not null)
            {
                signals.RejectedOutputStyles ??= new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);
                signals.RejectedOutputStyles.TryGetValue(normalizedStyle, out var count);
                signals.RejectedOutputStyles[normalizedStyle] = count + 1;
            }
            RecordCannedExpressionOutcomes(signals.RejectedRemovedCannedExpressions, normalizedRemovedExpressions);
        }
        signals.UpdatedAtUtc = DateTimeOffset.UtcNow;
        return true;
    }

    private static void RecordCannedExpressionOutcomes(
        System.Collections.Generic.Dictionary<string, int> destination,
        System.Collections.Generic.IReadOnlyCollection<string>? phrases)
    {
        if (phrases is null) return;
        foreach (var phrase in phrases.Distinct(StringComparer.Ordinal))
        {
            if (!CannedExpressionCatalog.Values.Contains(phrase, StringComparer.Ordinal)) continue;
            destination.TryGetValue(phrase, out var count);
            destination[phrase] = count + 1;
        }
    }

    private static ExpressionInteractionSignalSet GetOrCreateSignals(ExpressionPreferenceProfile profile, ApplicationMode task, string? scenario)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.InteractionSignals ??= new System.Collections.Generic.Dictionary<string, ExpressionInteractionSignalSet>(StringComparer.Ordinal);
        var key = GetTaskPreferenceKey(task, scenario);
        if (!profile.InteractionSignals.TryGetValue(key, out var signals) || signals is null)
        {
            signals = new ExpressionInteractionSignalSet();
            profile.InteractionSignals[key] = signals;
        }
        return signals;
    }

    private static string? NormalizeScenario(ApplicationMode task, string? scenario)
    {
        var candidate = scenario?.Trim();
        if (string.IsNullOrWhiteSpace(candidate) || string.Equals(candidate, "其他", StringComparison.Ordinal)) return null;
        var allowed = task == ApplicationMode.Polish
            ? ["私人沟通", "职场沟通", "公开发布", "正式材料"]
            : PromptCategoryMetadata.AllCategories.Select(category => category.GetDisplayName()).ToArray();
        return allowed.FirstOrDefault(value => string.Equals(value, candidate, StringComparison.Ordinal));
    }
}
