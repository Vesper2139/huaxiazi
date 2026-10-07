using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Huaxiazi.Models;

[JsonConverter(typeof(JsonStringEnumConverter<ProviderType>))]
public enum ProviderType
{
    Cloud,
    Local
}

[JsonConverter(typeof(JsonStringEnumConverter<ProviderRoutingMode>))]
public enum ProviderRoutingMode
{
    Manual,
    LocalOnly,
    PreferLocal,
    PreferCloud,
    Automatic
}

[JsonConverter(typeof(JsonStringEnumConverter<ProviderPlatform>))]
public enum ProviderPlatform
{
    OpenAI,
    Anthropic,
    Gemini,
    DeepSeek,
    MiMo,
    Qwen,
    Doubao,
    Kimi,
    Zhipu,
    SiliconFlow,
    StepFun,
    MiniMax,
    OpenRouter,
    Grok,
    Mistral,
    Groq,
    Baichuan,
    Spark,
    Yi,
    Together,
    ManagedLocal,
    Ollama,
    LmStudio,
    CustomOpenAICompatible
}

[JsonConverter(typeof(JsonStringEnumConverter<ProviderProtocol>))]
public enum ProviderProtocol
{
    OpenAICompatible,
    OpenAIResponses,
    AnthropicMessages,
    GeminiGenerateContent
}

/// <summary>模型信息：显示名称 → 实际 Model ID。</summary>
public sealed record ModelDefinition(string DisplayName, string ModelId)
{
    /// <summary>目录提供的模型级候选参数；null 表示目录没有提供，不能当作端点能力证明。</summary>
    public IReadOnlySet<string>? SupportedParameters { get; init; }
}

/// <summary>模型映射条目：统一模型名 → 实际 Model ID（供 UI 编辑集合）。</summary>
public sealed class ModelMappingEntry : INotifyPropertyChanged
{
    private string _key = string.Empty;
    private string _value = string.Empty;

    public string Key
    {
        get => _key;
        set => SetField(ref _key, value ?? string.Empty);
    }

    public string Value
    {
        get => _value;
        set => SetField(ref _value, value ?? string.Empty);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField(ref string field, string value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class ProviderProfile
{
    public string Id { get; set; } = "default";
    public string Name { get; set; } = "默认模型";
    public ProviderType Type { get; set; } = ProviderType.Cloud;
    public ProviderPlatform Platform { get; set; } = ProviderPlatform.OpenAI;
    public ProviderProtocol Protocol { get; set; } = ProviderProtocol.OpenAICompatible;
    public string ApiBase { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "gpt-4o-mini";
    /// <summary>OpenRouter 参数快照绑定的实际型号；不匹配时必须忽略快照。</summary>
    public string OpenRouterCapabilitiesModelId { get; set; } = string.Empty;
    /// <summary>OpenRouter 模型目录的候选参数快照；null 表示未核验。</summary>
    public List<string>? OpenRouterSupportedParameters { get; set; }
    public int TimeoutSeconds { get; set; } = 120;
    public double Temperature { get; set; } = 0.4;
    public double TopP { get; set; } = 1.0;
    public int MaxTokens { get; set; } = 2048;
    /// <summary>普通用户可理解的推理强度；旧配置缺失时兼容为 Medium。</summary>
    public InferenceLevel InferenceLevel { get; set; } = InferenceLevel.Medium;
    public string SecretId { get; set; } = "provider-default";
    public string Remark { get; set; } = string.Empty;
    public bool EnableModelMapping { get; set; }
    public Dictionary<string, string> ModelMapping { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string LocalModelInstallationId { get; set; } = string.Empty;
    public string LocalAdapterInstallationId { get; set; } = string.Empty;
    public string LocalRuntimeProfileId { get; set; } = "balanced";
    public LocalRuntimeOptions LocalRuntimeOptions { get; set; } = new();

    public ProviderProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        Type = Type,
        Platform = Platform,
        Protocol = Protocol,
        ApiBase = ApiBase,
        Model = Model,
        OpenRouterCapabilitiesModelId = OpenRouterCapabilitiesModelId,
        OpenRouterSupportedParameters = OpenRouterSupportedParameters is null ? null : new List<string>(OpenRouterSupportedParameters),
        TimeoutSeconds = TimeoutSeconds,
        Temperature = Temperature,
        TopP = TopP,
        MaxTokens = MaxTokens,
        InferenceLevel = InferenceLevel,
        SecretId = SecretId,
        Remark = Remark,
        EnableModelMapping = EnableModelMapping,
        ModelMapping = new Dictionary<string, string>(ModelMapping, StringComparer.OrdinalIgnoreCase),
        LocalModelInstallationId = LocalModelInstallationId,
        LocalAdapterInstallationId = LocalAdapterInstallationId,
        LocalRuntimeProfileId = LocalRuntimeProfileId,
        LocalRuntimeOptions = new LocalRuntimeOptions
        {
            ContextSize = LocalRuntimeOptions.ContextSize,
            CpuThreads = LocalRuntimeOptions.CpuThreads,
            GpuMode = LocalRuntimeOptions.GpuMode,
            BatchSize = LocalRuntimeOptions.BatchSize,
            KeepLoaded = LocalRuntimeOptions.KeepLoaded,
            AdapterScale = LocalRuntimeOptions.AdapterScale,
            Seed = LocalRuntimeOptions.Seed,
            RepeatPenalty = LocalRuntimeOptions.RepeatPenalty
        }
    };
}
