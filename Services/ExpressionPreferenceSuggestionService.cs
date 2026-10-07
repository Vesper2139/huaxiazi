using System;
using System.Collections.Generic;
using System.Linq;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

/// <summary>A weak, user-reviewable preference hint derived from content-free interaction counts.</summary>
public sealed record ExpressionPreferenceCandidate(
    string Key,
    string ScopeKey,
    string Value,
    int SupportingOutputs,
    int OpposingOutputs);

public sealed class ExpressionPreferenceSuggestionService
{
    private const int MinimumSupportingOutputs = 3;

    public ExpressionPreferenceCandidate? GetLengthCandidate(
        ExpressionPreferenceProfile profile,
        ApplicationMode task,
        string? scenario,
        bool includeIgnored = false)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var scopeKey = StructuredPreferenceService.GetTaskPreferenceKey(task, scenario);
        if (profile.InteractionSignals is null || !profile.InteractionSignals.TryGetValue(scopeKey, out var signals)) return null;
        if (signals.AcceptedOutputStyles is null || signals.AcceptedOutputStyles.Count != 1 ||
            signals.RejectedOutputStyles is null || signals.RejectedOutputStyles.Count != 0)
            return null;

        var value = signals.AcceptedShortenedOutputs > 0 &&
                    signals.AcceptedExpandedOutputs == 0 &&
                    signals.RejectedShortenedOutputs == 0 &&
                    signals.RejectedExpandedOutputs == 0
            ? "concise"
            : signals.AcceptedExpandedOutputs > 0 &&
              signals.AcceptedShortenedOutputs == 0 &&
              signals.RejectedShortenedOutputs == 0 &&
              signals.RejectedExpandedOutputs == 0
                ? "detailed"
                : null;
        if (value is null) return null;

        // A confirmed preference is the user's decision for this scope; never nudge it with the same signal.
        if (profile.TaskPreferences is not null && profile.TaskPreferences.TryGetValue(scopeKey, out var preference) && preference?.UserConfirmed == true)
            return null;

        var key = $"preferredLength|{scopeKey}|{value}";
        if (!includeIgnored && profile.IgnoredSuggestionKeys?.Contains(key, StringComparer.Ordinal) == true) return null;

        return new ExpressionPreferenceCandidate(
            key,
            scopeKey,
            value,
            value == "concise" ? signals.AcceptedShortenedOutputs : signals.AcceptedExpandedOutputs,
            value == "concise" ? signals.RejectedShortenedOutputs : signals.RejectedExpandedOutputs);
    }

    public ExpressionPreferenceCandidate? GetToneCandidate(
        ExpressionPreferenceProfile profile,
        ApplicationMode task,
        string? scenario,
        bool includeIgnored = false)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var scopeKey = StructuredPreferenceService.GetTaskPreferenceKey(task, scenario);
        if (profile.InteractionSignals is null || !profile.InteractionSignals.TryGetValue(scopeKey, out var signals)) return null;
        if (signals.AcceptedOutputStyles is null || signals.AcceptedOutputStyles.Count != 1 ||
            signals.RejectedOutputStyles is null || signals.RejectedOutputStyles.Count != 0)
            return null;

        var selectedStyle = signals.AcceptedOutputStyles.Single();
        var tone = selectedStyle.Key switch
        {
            "专业" or "正式" => "professional",
            "亲切" => "warm",
            _ => null
        };
        if (tone is null || selectedStyle.Value < MinimumSupportingOutputs) return null;

        if (profile.TaskPreferences is not null && profile.TaskPreferences.TryGetValue(scopeKey, out var preference) && preference?.UserConfirmed == true)
            return null;

        var key = $"preferredTone|{scopeKey}|{tone}";
        if (!includeIgnored && profile.IgnoredSuggestionKeys?.Contains(key, StringComparer.Ordinal) == true) return null;

        return new ExpressionPreferenceCandidate(key, scopeKey, tone, selectedStyle.Value, 0);
    }

    public IReadOnlyList<ExpressionPreferenceCandidate> GetForbiddenExpressionCandidates(
        ExpressionPreferenceProfile profile,
        ApplicationMode task,
        string? scenario,
        bool includeIgnored = false)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var scopeKey = StructuredPreferenceService.GetTaskPreferenceKey(task, scenario);
        if (profile.InteractionSignals is null || !profile.InteractionSignals.TryGetValue(scopeKey, out var signals)) return [];
        if (signals.AcceptedOutputStyles is null || signals.AcceptedOutputStyles.Count != 1 ||
            signals.RejectedOutputStyles is null || signals.RejectedOutputStyles.Count != 0)
            return [];

        ExpressionPreferenceSet? preference = null;
        profile.TaskPreferences?.TryGetValue(scopeKey, out preference);
        var confirmedExpressions = preference?.UserConfirmed == true
            ? preference.ForbiddenExpressions ?? []
            : [];
        var candidates = new List<ExpressionPreferenceCandidate>();
        foreach (var phrase in CannedExpressionCatalog.Values)
        {
            var supportingOutputs = 0;
            var opposingOutputs = 0;
            signals.AcceptedRemovedCannedExpressions?.TryGetValue(phrase, out supportingOutputs);
            signals.RejectedRemovedCannedExpressions?.TryGetValue(phrase, out opposingOutputs);
            if (supportingOutputs < MinimumSupportingOutputs || opposingOutputs != 0 ||
                confirmedExpressions.Contains(phrase, StringComparer.OrdinalIgnoreCase))
                continue;

            var key = $"forbiddenExpression|{scopeKey}|{phrase}";
            if (!includeIgnored && profile.IgnoredSuggestionKeys?.Contains(key, StringComparer.Ordinal) == true) continue;
            candidates.Add(new ExpressionPreferenceCandidate(key, scopeKey, phrase, supportingOutputs, opposingOutputs));
        }
        return candidates;
    }
}
