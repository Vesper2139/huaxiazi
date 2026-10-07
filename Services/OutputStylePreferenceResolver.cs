using System;
using System.Collections.Generic;
using System.Linq;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

/// <summary>Resolves explicit task/scenario style overrides over the legacy global style.</summary>
public static class OutputStylePreferenceResolver
{
    private static readonly string[] PolishScenarios =
        ["私人沟通", "职场沟通", "公开发布", "正式材料", "其他"];

    private static readonly HashSet<string> PromptOptimizeScenarios = PromptCategoryMetadata.AllCategories
        .Select(category => category.GetDisplayName())
        .ToHashSet(StringComparer.Ordinal);

    public static string CreateKey(ApplicationMode mode, string? scenario) =>
        $"{mode}|{scenario?.Trim() ?? string.Empty}";

    public static string Resolve(
        ApplicationMode mode,
        string? scenario,
        string? globalStyle,
        IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides is not null)
        {
            var scenarioKey = CreateKey(mode, scenario);
            if (!string.IsNullOrWhiteSpace(scenario) &&
                overrides.TryGetValue(scenarioKey, out var scenarioStyle) &&
                IsSupportedStyle(scenarioStyle))
            {
                return scenarioStyle;
            }

            if (overrides.TryGetValue(CreateKey(mode, string.Empty), out var taskStyle) &&
                IsSupportedStyle(taskStyle))
            {
                return taskStyle;
            }
        }

        return OutputStyleCatalog.Normalize(globalStyle);
    }

    public static Dictionary<string, string> NormalizeOverrides(IReadOnlyDictionary<string, string>? overrides)
    {
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        if (overrides is null) return normalized;

        foreach (var pair in overrides)
        {
            if (!TryParseScope(pair.Key, out var mode, out var scenario) || !IsSupportedStyle(pair.Value)) continue;
            normalized[CreateKey(mode, scenario)] = pair.Value;
        }

        return normalized;
    }

    private static bool TryParseScope(string? key, out ApplicationMode mode, out string scenario)
    {
        mode = default;
        scenario = string.Empty;
        if (string.IsNullOrWhiteSpace(key)) return false;

        var separator = key.IndexOf('|');
        if (separator < 0 || !Enum.TryParse(key[..separator], ignoreCase: false, out mode) ||
            mode is not (ApplicationMode.Polish or ApplicationMode.PromptOptimize))
        {
            return false;
        }

        scenario = key[(separator + 1)..].Trim();
        if (scenario.Contains('|', StringComparison.Ordinal)) return false;
        if (scenario.Length == 0) return true;

        return mode == ApplicationMode.Polish
            ? PolishScenarios.Contains(scenario, StringComparer.Ordinal)
            : PromptOptimizeScenarios.Contains(scenario);
    }

    private static bool IsSupportedStyle(string? style) =>
        OutputStyleCatalog.Values.Contains(style, StringComparer.Ordinal);
}
