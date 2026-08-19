using System.Collections.Generic;
using System.Linq;
using PromptFloat.Models;

namespace PromptFloat.Services;

public sealed record ProviderPlatformOption(
    ProviderPlatform Platform,
    string DisplayName,
    string ApiBase,
    string DefaultModel,
    ProviderProtocol Protocol,
    ProviderType Type,
    bool ApiBaseEditable);

public static class ProviderPlatformCatalog
{
    public static IReadOnlyList<ProviderPlatformOption> Options { get; } =
    [
        new(ProviderPlatform.OpenAI, "OpenAI", "https://api.openai.com/v1", "gpt-4o-mini", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, false),
        new(ProviderPlatform.Anthropic, "Claude (Anthropic)", "https://api.anthropic.com", "claude-sonnet-4-5", ProviderProtocol.AnthropicMessages, ProviderType.Cloud, false),
        new(ProviderPlatform.Gemini, "Gemini (Google)", "https://generativelanguage.googleapis.com/v1beta", "gemini-2.5-flash", ProviderProtocol.GeminiGenerateContent, ProviderType.Cloud, false),
        new(ProviderPlatform.DeepSeek, "DeepSeek", "https://api.deepseek.com", "deepseek-v4-flash", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, false),
        new(ProviderPlatform.Qwen, "通义千问（阿里云百炼）", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, false),
        new(ProviderPlatform.Doubao, "豆包（火山方舟）", "https://ark.cn-beijing.volces.com/api/v3", "doubao-seed-1-6-flash-250828", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, false),
        new(ProviderPlatform.Ollama, "Ollama（本地）", "http://localhost:11434/v1", "qwen3", ProviderProtocol.OpenAICompatible, ProviderType.Local, false),
        new(ProviderPlatform.LmStudio, "LM Studio（本地）", "http://localhost:1234/v1", "local-model", ProviderProtocol.OpenAICompatible, ProviderType.Local, false),
        new(ProviderPlatform.CustomOpenAICompatible, "自定义 OpenAI 兼容接口", "https://", "", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, true)
    ];

    public static ProviderPlatformOption Get(ProviderPlatform platform) =>
        Options.First(option => option.Platform == platform);

    public static void ApplyPreset(ProviderProfile profile, ProviderPlatform platform)
    {
        var option = Get(platform);
        profile.Platform = option.Platform;
        profile.Protocol = option.Protocol;
        profile.Type = option.Type;
        profile.ApiBase = option.ApiBase;
        profile.Model = option.DefaultModel;
        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name is "默认模型" or "新模型")
            profile.Name = option.DisplayName;
    }

    public static void InferLegacyPlatform(ProviderProfile profile)
    {
        if (profile.Platform != ProviderPlatform.OpenAI || string.IsNullOrWhiteSpace(profile.ApiBase)) return;
        var apiBase = profile.ApiBase.ToLowerInvariant();
        var platform = apiBase switch
        {
            _ when apiBase.Contains("anthropic.com") => ProviderPlatform.Anthropic,
            _ when apiBase.Contains("generativelanguage.googleapis.com") => ProviderPlatform.Gemini,
            _ when apiBase.Contains("deepseek.com") => ProviderPlatform.DeepSeek,
            _ when apiBase.Contains("dashscope") || apiBase.Contains("maas.aliyuncs.com") => ProviderPlatform.Qwen,
            _ when apiBase.Contains("ark.cn-beijing.volces.com") => ProviderPlatform.Doubao,
            _ when apiBase.Contains("localhost:11434") || apiBase.Contains("127.0.0.1:11434") => ProviderPlatform.Ollama,
            _ when apiBase.Contains("localhost:1234") || apiBase.Contains("127.0.0.1:1234") => ProviderPlatform.LmStudio,
            _ when apiBase.Contains("api.openai.com") => ProviderPlatform.OpenAI,
            _ => ProviderPlatform.CustomOpenAICompatible
        };

        profile.Platform = platform;
        var option = Get(platform);
        profile.Protocol = option.Protocol;
        profile.Type = option.Type;
    }
}
