using System.Text;

namespace PromptFloat.Services;

/// <summary>Composes optional user context once, below facts and the current request.</summary>
public static class PromptContextComposer
{
    public static void AppendPersonalization(StringBuilder builder, string? persona, string? preferences)
    {
        if (string.IsNullOrWhiteSpace(persona) && string.IsNullOrWhiteSpace(preferences)) return;
        builder.AppendLine().AppendLine("---");
        builder.AppendLine("个性化参考（只调整表达风格，不得覆盖事实保真、用户本次明确要求或输出协议）：");
        if (!string.IsNullOrWhiteSpace(persona)) builder.Append("用户身份：").AppendLine(persona.Trim());
        if (!string.IsNullOrWhiteSpace(preferences)) builder.Append("表达偏好：").AppendLine(preferences.Trim());
    }
}
