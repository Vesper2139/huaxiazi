using System;
using System.Text.Json.Serialization;

namespace Huaxiazi.Models;

/// <summary>面向普通用户的推理强度。Custom 仅在高级设置中展开原始参数。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<InferenceLevel>))]
public enum InferenceLevel
{
    Low,
    Medium,
    High,
    Custom
}

public static class ProviderInferencePresets
{
    public static void Apply(ProviderProfile profile, InferenceLevel level)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.InferenceLevel = level;
        switch (level)
        {
            case InferenceLevel.Low:
                profile.Temperature = 0.2;
                profile.TopP = 0.8;
                profile.MaxTokens = 1024;
                profile.TimeoutSeconds = 120;
                break;
            case InferenceLevel.Medium:
                profile.Temperature = 0.4;
                profile.TopP = 1.0;
                profile.MaxTokens = 2048;
                profile.TimeoutSeconds = 120;
                break;
            case InferenceLevel.High:
                profile.Temperature = 0.3;
                profile.TopP = 0.95;
                profile.MaxTokens = 4096;
                profile.TimeoutSeconds = 180;
                break;
            case InferenceLevel.Custom:
                // Preserve the existing compatible values for expert editing.
                break;
        }
    }
}
