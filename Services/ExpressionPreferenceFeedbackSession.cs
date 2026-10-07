using System;
using System.Collections.Generic;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

/// <summary>Tracks content-free edit signals for one generated output until the user accepts or rejects it.</summary>
public sealed class ExpressionPreferenceFeedbackSession
{
    private bool _active;
    public bool IsActive => _active;
    private bool _wasShortened;
    private bool _wasExpanded;
    private readonly HashSet<string> _removedCannedExpressions = new(StringComparer.Ordinal);
    private ApplicationMode _task;
    private string? _scenario;
    private string _outputStyle = "自然";

    public void Start(ApplicationMode task, string? scenario, string? outputStyle, bool enabled)
    {
        _active = enabled;
        _task = task;
        _scenario = scenario;
        _outputStyle = OutputStyleCatalog.Normalize(outputStyle);
        _wasShortened = false;
        _wasExpanded = false;
        _removedCannedExpressions.Clear();
    }

    public void RecordEdit(string? generated, string? edited)
    {
        if (!_active) return;
        _removedCannedExpressions.Clear();
        var before = generated ?? string.Empty;
        var after = edited ?? string.Empty;
        if (before.Length == 0) return;
        if (after.Length <= before.Length * 0.8) _wasShortened = true;
        if (after.Length >= before.Length * 1.25) _wasExpanded = true;
        foreach (var phrase in CannedExpressionCatalog.Values)
            if (before.Contains(phrase, StringComparison.Ordinal) && !after.Contains(phrase, StringComparison.Ordinal))
                _removedCannedExpressions.Add(phrase);
    }

    public bool Complete(
        ExpressionPreferenceProfile profile,
        StructuredPreferenceService preferenceService,
        bool accepted,
        bool rejected)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(preferenceService);
        if (!_active) return false;

        var recorded = preferenceService.RecordObservedOutputOutcome(
            profile, _task, _scenario, _wasShortened, _wasExpanded, accepted, rejected, _outputStyle,
            _removedCannedExpressions);
        Cancel();
        return recorded;
    }

    public void Cancel()
    {
        _active = false;
        _wasShortened = false;
        _wasExpanded = false;
        _removedCannedExpressions.Clear();
        _scenario = null;
        _outputStyle = "自然";
    }
}
