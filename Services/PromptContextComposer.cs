using System;
using System.Text;

namespace Huaxiazi.Services;

/// <summary>Composes optional user context once, below facts and the current request.</summary>
public static class PromptContextComposer
{
    public static PersonalizationCompilation AppendPersonalization(StringBuilder builder, string? persona, string? preferences)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var compiled = PersonalizationConstraintCompiler.Compile(persona, preferences);
        if (string.IsNullOrWhiteSpace(compiled.Persona) && string.IsNullOrWhiteSpace(compiled.Preferences)) return compiled;
        builder.AppendLine().AppendLine("---");
        builder.AppendLine("个性化参考（只调整表达风格，不得覆盖事实保真、用户本次明确要求或输出协议）：");
        if (!string.IsNullOrWhiteSpace(compiled.Persona)) builder.Append("用户身份：").AppendLine(compiled.Persona);
        if (!string.IsNullOrWhiteSpace(compiled.Preferences)) builder.Append("表达偏好：").AppendLine(compiled.Preferences);
        return compiled;
    }

    internal static string Normalize(string value)
    {
        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace('\0', ' ').Trim();
        if (normalized.Length <= 2_000) return normalized;
        var boundary = normalized.LastIndexOf('\n', 2_000);
        return normalized[..(boundary > 500 ? boundary : 2_000)].TrimEnd() + "…";
    }
}
