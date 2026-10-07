using System;
using System.Globalization;
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
    private sealed record Preset(double Temperature, double TopP, int MaxTokens, int TimeoutSeconds);

    private static readonly Preset LowPreset = new(0.2, 0.8, 1024, 120);
    private static readonly Preset MediumPreset = new(0.4, 1.0, 2048, 120);
    private static readonly Preset HighPreset = new(0.3, 0.95, 4096, 180);

    public static void Apply(ProviderProfile profile, InferenceLevel level)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.InferenceLevel = level;
        var preset = Resolve(level);
        if (preset is null) return;

        profile.Temperature = preset.Temperature;
        profile.TopP = preset.TopP;
        profile.MaxTokens = preset.MaxTokens;
        profile.TimeoutSeconds = preset.TimeoutSeconds;
    }

    public static string Describe(InferenceLevel level) => Describe(level, profile: null);

    public static string Describe(InferenceLevel level, ProviderProfile? profile)
    {
        var preset = Resolve(level);
        if (preset is null)
            return "自定义档保留当前采样、输出上限和超时值。具体传参与推理行为以当前模型能力说明为准。";

        var description = $"此档写入配置：temperature={preset.Temperature.ToString("0.0", CultureInfo.InvariantCulture)}，" +
               $"top_p={preset.TopP.ToString("0.##", CultureInfo.InvariantCulture)}，max_tokens={preset.MaxTokens}，" +
               $"超时={preset.TimeoutSeconds} 秒。写入配置不代表 Provider 都会接收或使用这些值；" +
               "具体传参与推理行为以当前模型能力说明为准。";
        return description;
    }

    private static Preset? Resolve(InferenceLevel level) => level switch
    {
        InferenceLevel.Low => LowPreset,
        InferenceLevel.Medium => MediumPreset,
        InferenceLevel.High => HighPreset,
        _ => null
    };
}
