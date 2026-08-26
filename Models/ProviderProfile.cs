using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Huaxiazi.Models;

[JsonConverter(typeof(JsonStringEnumConverter<ProviderType>))]
public enum ProviderType
{
    Cloud,
    Local
}

[JsonConverter(typeof(JsonStringEnumConverter<ProviderPlatform>))]
public enum ProviderPlatform
{
    OpenAI,
    Anthropic,
    Gemini,
    DeepSeek,
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
    Ollama,
    LmStudio,
    CustomOpenAICompatible
}

[JsonConverter(typeof(JsonStringEnumConverter<ProviderProtocol>))]
public enum ProviderProtocol
{
    OpenAICompatible,
    AnthropicMessages,
    GeminiGenerateContent
}

/// <summary>模型信息：显示名称 → 实际 Model ID。</summary>
public sealed record ModelDefinition(string DisplayName, string ModelId);

/// <summary>模型映射条目：统一模型名 → 实际 Model ID（供 UI 编辑集合）。</summary>
public sealed class ModelMappingEntry
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
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
    public int TimeoutSeconds { get; set; } = 120;
    public double Temperature { get; set; } = 0.4;
    public double TopP { get; set; } = 1.0;
    public int MaxTokens { get; set; } = 2048;
    public string SecretId { get; set; } = "provider-default";
    public string Remark { get; set; } = string.Empty;
    public bool EnableModelMapping { get; set; }
    public Dictionary<string, string> ModelMapping { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public ProviderProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        Type = Type,
        Platform = Platform,
        Protocol = Protocol,
        ApiBase = ApiBase,
        Model = Model,
        TimeoutSeconds = TimeoutSeconds,
        Temperature = Temperature,
        TopP = TopP,
        MaxTokens = MaxTokens,
        SecretId = SecretId,
        Remark = Remark,
        EnableModelMapping = EnableModelMapping,
        ModelMapping = new Dictionary<string, string>(ModelMapping, StringComparer.OrdinalIgnoreCase)
    };
}
