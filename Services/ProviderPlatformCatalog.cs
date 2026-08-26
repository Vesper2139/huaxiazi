using System.Collections.Generic;
using System.Linq;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

public enum ProviderPresetTier
{
    Common,
    More,
    Local,
    Custom
}

public sealed record ProviderPlatformOption(
    ProviderPlatform Platform,
    string DisplayName,
    string ApiBase,
    string DefaultModel,
    ProviderProtocol Protocol,
    ProviderType Type,
    bool ApiBaseEditable,
    string WebsiteUrl = "",
    string ApiKeyUrl = "",
    string HelpText = "",
    IReadOnlyList<ModelDefinition>? Models = null,
    ProviderPresetTier Tier = ProviderPresetTier.More,
    bool RequiresApiKey = true,
    string VerifiedOn = "2026-08-20")
{
    public string TierDisplayName => Tier switch
    {
        ProviderPresetTier.Common => "常用平台",
        ProviderPresetTier.Local => "本地模型",
        ProviderPresetTier.Custom => "自定义接口",
        _ => "更多平台"
    };
}

public static class ProviderPlatformCatalog
{
    public static IReadOnlyList<ProviderPlatformOption> Options { get; } =
    [
        new(ProviderPlatform.OpenAI, "OpenAI", "https://api.openai.com/v1", "gpt-4o-mini", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, false,
            "https://platform.openai.com", "https://platform.openai.com/api-keys", "在 OpenAI 平台创建 API Key。",
            [new("GPT-4o mini", "gpt-4o-mini"), new("GPT-4o", "gpt-4o"), new("GPT-4.1 mini", "gpt-4.1-mini")], Tier: ProviderPresetTier.Common),
        new(ProviderPlatform.Anthropic, "Claude (Anthropic)", "https://api.anthropic.com", "claude-sonnet-4-5", ProviderProtocol.AnthropicMessages, ProviderType.Cloud, false,
            "https://console.anthropic.com", "https://console.anthropic.com/settings/keys", "在 Anthropic 控制台创建 API Key。",
            [new("Claude Sonnet 4.5", "claude-sonnet-4-5"), new("Claude Haiku 4.5", "claude-haiku-4-5"), new("Claude Opus 4.1", "claude-opus-4-1")], Tier: ProviderPresetTier.Common),
        new(ProviderPlatform.Gemini, "Gemini (Google)", "https://generativelanguage.googleapis.com/v1beta", "gemini-3.5-flash", ProviderProtocol.GeminiGenerateContent, ProviderType.Cloud, false,
            "https://ai.google.dev", "https://aistudio.google.com/app/apikey", "在 Google AI Studio 创建 API Key。",
            [new("Gemini 3.5 Flash", "gemini-3.5-flash"), new("Gemini 3.5 Pro", "gemini-3.5-pro")], Tier: ProviderPresetTier.Common),
        new(ProviderPlatform.DeepSeek, "DeepSeek", "https://api.deepseek.com/v1", "deepseek-v4-flash", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, false,
            "https://platform.deepseek.com", "https://platform.deepseek.com/api_keys", "在 DeepSeek 开放平台创建 API Key。",
            [new("DeepSeek V4 Flash", "deepseek-v4-flash"), new("DeepSeek V4 Pro", "deepseek-v4-pro")], Tier: ProviderPresetTier.Common),
        new(ProviderPlatform.Qwen, "通义千问（阿里云百炼）", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, false,
            "https://bailian.console.aliyun.com", "https://bailian.console.aliyun.com/?apiKey=1", "在阿里云百炼控制台创建 API Key。",
            [new("Qwen Plus", "qwen-plus"), new("Qwen Turbo", "qwen-turbo"), new("Qwen Max", "qwen-max"), new("Qwen Long", "qwen-long")], Tier: ProviderPresetTier.Common),
        new(ProviderPlatform.Doubao, "豆包（火山方舟）", "https://ark.cn-beijing.volces.com/api/v3", "doubao-seed-2-0-lite-260215", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, false,
            "https://console.volcengine.com/ark", "https://console.volcengine.com/ark/region:ark+cn-beijing/apiKey", "在火山方舟控制台创建 API Key。",
            [new("Doubao Seed 2.0 Lite", "doubao-seed-2-0-lite-260215")], Tier: ProviderPresetTier.Common),
        new(ProviderPlatform.Kimi, "Kimi（月之暗面）", "https://api.moonshot.cn/v1", "kimi-k2-0905-preview", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, false,
            "https://platform.moonshot.cn", "https://platform.moonshot.cn/console/api-keys", "在 Moonshot 开放平台创建 API Key。",
            [new("Kimi K2", "kimi-k2-0905-preview"), new("Moonshot v1 8K", "moonshot-v1-8k"), new("Moonshot v1 32K", "moonshot-v1-32k"), new("Moonshot v1 128K", "moonshot-v1-128k")], Tier: ProviderPresetTier.Common),
        new(ProviderPlatform.Zhipu, "智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", "glm-4-flash", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, false,
            "https://open.bigmodel.cn", "https://open.bigmodel.cn/usercenter/apikeys", "在智谱开放平台创建 API Key。",
            [new("GLM-4 Flash", "glm-4-flash"), new("GLM-4 Air", "glm-4-air"), new("GLM-4 Plus", "glm-4-plus"), new("GLM-4.5", "glm-4.5")], Tier: ProviderPresetTier.Common),
        new(ProviderPlatform.SiliconFlow, "硅基流动", "https://api.siliconflow.cn/v1", "deepseek-ai/DeepSeek-V3", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, false,
            "https://siliconflow.cn", "https://cloud.siliconflow.cn/account/ak", "在硅基流动云平台创建 API Key。",
            [new("DeepSeek-V3", "deepseek-ai/DeepSeek-V3"), new("Qwen2.5-7B-Instruct", "Qwen/Qwen2.5-7B-Instruct"), new("GLM-4-9B-Chat", "THUDM/glm-4-9b-chat")], Tier: ProviderPresetTier.Common),
        new(ProviderPlatform.StepFun, "阶跃星辰", "https://api.stepfun.com/v1", "step-2-16k", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, false,
            "https://platform.stepfun.com", "https://platform.stepfun.com/api-key", "在阶跃星辰开放平台创建 API Key。",
            [new("Step-2 16K", "step-2-16k"), new("Step-1 32K", "step-1-32k")]),
        new(ProviderPlatform.MiniMax, "MiniMax", "https://api.minimax.chat/v1", "MiniMax-Text-01", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, false,
            "https://platform.minimaxi.com", "https://platform.minimaxi.com/user-center/basic-information/interface-key", "在 MiniMax 开放平台创建 API Key。",
            [new("MiniMax Text-01", "MiniMax-Text-01"), new("abab6.5s-chat", "abab6.5s-chat")]),
        new(ProviderPlatform.OpenRouter, "OpenRouter", "https://openrouter.ai/api/v1", "deepseek/deepseek-chat", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, false,
            "https://openrouter.ai", "https://openrouter.ai/keys", "在 OpenRouter 创建 API Key，可聚合数百家模型。",
            [new("DeepSeek V3", "deepseek/deepseek-chat"), new("GPT-4o mini", "openai/gpt-4o-mini"), new("Claude Sonnet", "anthropic/claude-3.5-sonnet")], Tier: ProviderPresetTier.Common),
        new(ProviderPlatform.Grok, "xAI Grok", "https://api.x.ai/v1", "grok-2-latest", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, false,
            "https://x.ai", "https://console.x.ai", "在 xAI 控制台创建 API Key。",
            [new("Grok 2 (latest)", "grok-2-latest"), new("Grok Beta", "grok-beta")]),
        new(ProviderPlatform.Mistral, "Mistral", "https://api.mistral.ai/v1", "mistral-small-latest", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, false,
            "https://console.mistral.ai", "https://console.mistral.ai/api-keys", "在 Mistral 控制台创建 API Key。",
            [new("Mistral Small (latest)", "mistral-small-latest"), new("Mistral Nemo", "open-mistral-nemo"), new("Mistral Large (latest)", "mistral-large-latest")]),
        new(ProviderPlatform.Groq, "Groq", "https://api.groq.com/openai/v1", "llama-3.3-70b-versatile", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, false,
            "https://console.groq.com", "https://console.groq.com/keys", "在 Groq 控制台创建 API Key（超高速推理）。",
            [new("Llama 3.3 70B Versatile", "llama-3.3-70b-versatile"), new("Llama 3.1 8B Instant", "llama-3.1-8b-instant")]),
        new(ProviderPlatform.Baichuan, "百川智能", "https://api.baichuan-ai.com/v1", "Baichuan4-Turbo", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, false,
            "https://platform.baichuan-ai.com", "https://platform.baichuan-ai.com/console/apikey", "在百川开放平台创建 API Key。",
            [new("Baichuan4-Turbo", "Baichuan4-Turbo")]),
        new(ProviderPlatform.Spark, "讯飞星火", "https://spark-api-open.xf-yun.com/v1", "generalv3.5", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, false,
            "https://xinghuo.xfyun.cn", "https://console.xfyun.cn/services/bm35", "在讯飞开放平台创建 API Key。",
            [new("Spark V3.5", "generalv3.5"), new("Spark Lite", "general")]),
        new(ProviderPlatform.Yi, "零一万物 Yi", "https://api.lingyiwanwu.com/v1", "yi-large", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, false,
            "https://platform.lingyiwanwu.com", "https://platform.lingyiwanwu.com/api-keys", "在零一万物开放平台创建 API Key。",
            [new("Yi-Large", "yi-large"), new("Yi-Lightning", "yi-lightning")]),
        new(ProviderPlatform.Together, "Together", "https://api.together.xyz/v1", "meta-llama/Llama-3.3-70B-Instruct-Turbo", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, false,
            "https://www.together.ai", "https://api.together.xyz/settings/api-keys", "在 Together 创建 API Key（开源模型聚合）。",
            [new("Llama 3.3 70B Turbo", "meta-llama/Llama-3.3-70B-Instruct-Turbo"), new("Qwen2.5 72B", "Qwen/Qwen2.5-72B-Instruct")]),
        new(ProviderPlatform.Ollama, "Ollama（本地）", "http://localhost:11434/v1", "qwen3", ProviderProtocol.OpenAICompatible, ProviderType.Local, false,
            "https://ollama.com", "", "本机运行 Ollama 后，在下方填入已拉取的模型名。",
            [new("Qwen3", "qwen3"), new("DeepSeek-R1", "deepseek-r1"), new("Llama 3.1", "llama3.1")], Tier: ProviderPresetTier.Local, RequiresApiKey: false),
        new(ProviderPlatform.LmStudio, "LM Studio（本地）", "http://localhost:1234/v1", "local-model", ProviderProtocol.OpenAICompatible, ProviderType.Local, false,
            "https://lmstudio.ai", "", "本机运行 LM Studio 并加载模型后，填入模型名。",
            [new("本地模型", "local-model")], Tier: ProviderPresetTier.Local, RequiresApiKey: false),
        new(ProviderPlatform.CustomOpenAICompatible, "自定义 OpenAI 兼容接口", "https://", "", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, true,
            "", "", "任意 OpenAI 兼容接口，需自行填写 API 地址与模型 ID。", [], Tier: ProviderPresetTier.Custom)
    ];

    public static ProviderPlatformOption Get(ProviderPlatform platform) =>
        Options.FirstOrDefault(option => option.Platform == platform)
        ?? Options.First(option => option.Platform == ProviderPlatform.CustomOpenAICompatible);

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
            _ when apiBase.Contains("moonshot.cn") => ProviderPlatform.Kimi,
            _ when apiBase.Contains("bigmodel.cn") => ProviderPlatform.Zhipu,
            _ when apiBase.Contains("siliconflow.cn") => ProviderPlatform.SiliconFlow,
            _ when apiBase.Contains("stepfun.com") => ProviderPlatform.StepFun,
            _ when apiBase.Contains("minimax.chat") || apiBase.Contains("minimaxi") => ProviderPlatform.MiniMax,
            _ when apiBase.Contains("openrouter.ai") => ProviderPlatform.OpenRouter,
            _ when apiBase.Contains("api.x.ai") => ProviderPlatform.Grok,
            _ when apiBase.Contains("api.mistral.ai") => ProviderPlatform.Mistral,
            _ when apiBase.Contains("api.groq.com") => ProviderPlatform.Groq,
            _ when apiBase.Contains("baichuan-ai.com") => ProviderPlatform.Baichuan,
            _ when apiBase.Contains("xf-yun.com") => ProviderPlatform.Spark,
            _ when apiBase.Contains("lingyiwanwu.com") => ProviderPlatform.Yi,
            _ when apiBase.Contains("api.together.xyz") => ProviderPlatform.Together,
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
