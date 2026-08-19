using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PromptFloat.Models;

namespace PromptFloat.Services;

/// <summary>Learns only repeated signals from explicit edits; it does not train a model.</summary>
public sealed class AdaptivePreferenceService
{
    private static readonly string[] Candidates = ["感谢您的理解与支持", "希望以上内容对您有所帮助", "携手", "赋能", "首先", "其次", "最后", "综上所述"];

    public string BuildInstructions(IEnumerable<ContentRevision>? revisions)
    {
        if (revisions is null) return "";
        var counts = Candidates.ToDictionary(phrase => phrase, _ => 0, StringComparer.Ordinal);
        var eligibleEdits = 0;
        var substantialShortenings = 0;
        foreach (var revision in revisions.Take(100))
        {
            if (!TryReadEdit(revision.ContextJson, out var generated)) continue;
            foreach (var phrase in Candidates)
                if (generated.Contains(phrase, StringComparison.Ordinal) && !revision.FinalText.Contains(phrase, StringComparison.Ordinal)) counts[phrase]++;
            var generatedLength = MeaningfulLength(generated);
            var editedLength = MeaningfulLength(revision.FinalText);
            if (generatedLength >= 12 && editedLength > 0)
            {
                eligibleEdits++;
                if ((double)editedLength / generatedLength <= 0.75) substantialShortenings++;
            }
        }
        var repeated = counts.Where(pair => pair.Value >= 2).Select(pair => pair.Key).ToArray();
        var instructions = new List<string>();
        if (repeated.Length > 0)
            instructions.Add("根据用户多次主动删改，避免使用：" + string.Join("、", repeated));
        if (substantialShortenings >= 3 && substantialShortenings * 3 >= eligibleEdits * 2)
            instructions.Add("用户多次显著缩短成稿：优先简洁，减少铺垫和重复");
        return string.Join("；", instructions);
    }

    private static int MeaningfulLength(string text) => text.Count(char.IsLetterOrDigit);

    private static bool TryReadEdit(string? json, out string generated)
    {
        generated = "";
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var root = document.RootElement;
            if (!root.TryGetProperty("UserEdited", out var edited) || edited.ValueKind != JsonValueKind.True) return false;
            if (!root.TryGetProperty("GeneratedText", out var value) || value.ValueKind != JsonValueKind.String) return false;
            generated = value.GetString() ?? "";
            return generated.Length > 0;
        }
        catch (JsonException) { return false; }
    }
}
