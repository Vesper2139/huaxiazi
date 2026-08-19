using System.Text.Json.Serialization;

namespace PromptFloat.Models;

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
        SecretId = SecretId
    };
}
