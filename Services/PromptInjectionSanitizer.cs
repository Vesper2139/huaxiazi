using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Huaxiazi.Services;

/// <summary>
/// One shared detector for untrusted text that tries to change the agent's instruction hierarchy.
/// It is deliberately phrase-based: ordinary references to prompts or rules remain data, while
/// imperative override/disclosure requests are removed at trust-boundary crossings.
/// </summary>
public static class PromptInjectionSanitizer
{
    private static readonly Regex UnsafeInstruction = new(
        @"(?ix)(?:\b(?:ignore|disregard|forget|override|bypass|reveal|leak|exfiltrate|disclose|print|show)\b\s+.*\b(?:previous|prior|system|hidden|developer|prompt|instruction|rule|secret|api\s*key|token)\b)|(?:\b(?:system|hidden|developer)\s+(?:prompt|instruction|rule)s?\b\s*[:：]?\s*(?:reveal|show|print|leak|ignore|override))|(?:忽略|无视|覆盖|忘记|绕过|泄露|输出|显示).*(?:之前|前置|系统提示|系统规则|隐藏提示|开发者指令|密钥|令牌|安全边界)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool LooksLikePromptInjection(string? value) =>
        !string.IsNullOrWhiteSpace(value) && UnsafeInstruction.IsMatch(Normalize(value));

    public static string RemoveUnsafeLines(string? source, string replacement = "[已过滤]")
    {
        if (string.IsNullOrWhiteSpace(source)) return string.Empty;
        var lines = Normalize(source).Split('\n');
        return string.Join('\n', lines.Select(line => LooksLikePromptInjection(line) ? replacement : line)).Trim();
    }

    public static string ReplaceUnsafePhrases(string? source, string replacement = "[已过滤]")
    {
        if (string.IsNullOrWhiteSpace(source)) return string.Empty;
        return UnsafeInstruction.Replace(Normalize(source), replacement).Trim();
    }

    private static string Normalize(string value) => value.Normalize(NormalizationForm.FormKC)
        .Replace("\u200B", string.Empty, StringComparison.Ordinal)
        .Replace("\u200C", string.Empty, StringComparison.Ordinal)
        .Replace("\u200D", string.Empty, StringComparison.Ordinal)
        .Replace("\uFEFF", string.Empty, StringComparison.Ordinal)
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n');
}
