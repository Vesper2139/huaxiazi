using System;
using System.Collections.Generic;
using System.Linq;

namespace Huaxiazi.Services;

public sealed record PersonalizationCompilation(
    string Persona,
    string Preferences,
    IReadOnlyList<string> RejectedFragments);

/// <summary>Compiles user-provided style preferences into a safe, lower-priority context.</summary>
public static class PersonalizationConstraintCompiler
{
    private const int MaxCompiledCharacters = 4_000;
    public static PersonalizationCompilation Compile(string? persona, string? preferences)
    {
        var rejected = new List<string>();
        var safePersona = Filter(persona, rejected);
        var safePreferences = Filter(preferences, rejected);
        return new(safePersona, safePreferences, rejected);
    }

    private static string Filter(string? source, ICollection<string> rejected)
    {
        if (string.IsNullOrWhiteSpace(source)) return string.Empty;
        var fragments = source.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split(new[] { '\n', '；', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var safe = fragments.Where(fragment =>
        {
            var normalized = fragment.Trim();
            if (normalized.Length == 0) return false;
            if (PromptInjectionSanitizer.LooksLikePromptInjection(normalized) || RequestsCapability(normalized)) { rejected.Add(normalized); return false; }
            // Duplicate preferences add token noise and can make the model
            // overweight a phrase merely because it was repeated. Keep the
            // first occurrence in a deterministic, case-insensitive manner.
            return seen.Add(normalized);
        });
        var compiled = string.Join("；", safe).Trim();
        if (compiled.Length <= MaxCompiledCharacters) return compiled;
        var boundary = compiled.LastIndexOf('；', MaxCompiledCharacters);
        return compiled[..(boundary > 500 ? boundary : MaxCompiledCharacters)].TrimEnd() + "…";
    }

    private static bool RequestsCapability(string value) => System.Text.RegularExpressions.Regex.IsMatch(value,
        @"(?i)\b(?:shell|powershell|mcp|browser|slack|google\s*drive)\b|(?:读取|扫描|搜索).*(?:文件|邮箱|日历|外部资料)",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}
