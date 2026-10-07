using System.Security.Cryptography;
using System.Text;

namespace Huaxiazi.BlindEvaluationRunner;

public sealed record InternalDiagnosticPreferenceProfile(string Id, string Instructions, string Sha256);

/// <summary>Named, fixed preference interventions for internal development-only contrasts.</summary>
public static class InternalDiagnosticPreferenceProfiles
{
    private const string NaturalConcisePreserveVoiceInstructions =
        "优先使用自然、简洁的表达，避免过度正式和套话；尽量保留原文语气，只作必要润色；" +
        "保留全部事实、数字、日期、立场和不确定性。本轮明确要求优先于该偏好。";

    public static InternalDiagnosticPreferenceProfile? Resolve(string? id)
    {
        if (id is null) return null;
        if (!string.Equals(id, "natural-concise-preserve-voice", StringComparison.Ordinal))
            throw new ArgumentException("不支持该诊断偏好档；只允许 natural-concise-preserve-voice。", nameof(id));

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NaturalConcisePreserveVoiceInstructions)))
            .ToLowerInvariant();
        return new InternalDiagnosticPreferenceProfile(id, NaturalConcisePreserveVoiceInstructions, hash);
    }
}
