using System;
using System.Linq;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

public sealed class StructuredPreferenceService
{
    private static readonly string[] Canned = ["首先", "其次", "最后", "感谢您的理解与支持", "希望以上内容对您有所帮助", "后续我会及时"];

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
        foreach (var phrase in Canned.Where(phrase => before.Contains(phrase, StringComparison.Ordinal) && !after.Contains(phrase, StringComparison.Ordinal)))
        {
            profile.RemovedCannedExpressions.TryGetValue(phrase, out var count);
            profile.RemovedCannedExpressions[phrase] = count + 1;
        }
    }

    public string BuildInstructions(ExpressionPreferenceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var parts = new System.Collections.Generic.List<string>();
        if (profile.ShorteningEdits > profile.ExpansionEdits) parts.Add("偏好简洁、直接的成稿");
        if (profile.ExpansionEdits > profile.ShorteningEdits) parts.Add("偏好保留较完整的背景和步骤");
        if (profile.RemovedCannedExpressions.Count > 0) parts.Add("避免用户经常删除的模板套话");
        if (profile.ForbiddenExpressions.Count > 0) parts.Add("禁止使用：" + string.Join("、", profile.ForbiddenExpressions.Take(10)));
        return string.Join("；", parts);
    }

    public void RecordAcceptance(ExpressionPreferenceProfile profile) => profile.AcceptedCount++;
    public void RecordRetry(ExpressionPreferenceProfile profile) => profile.RetryCount++;
    public void RecordUndo(ExpressionPreferenceProfile profile) => profile.UndoCount++;
    public void RecordStyleChoice(ExpressionPreferenceProfile profile) => profile.StyleChoiceCount++;
}
