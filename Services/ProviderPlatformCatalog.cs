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
            [new("GPT-4o mini", "gpt-4o-mini"), new("GPT-4o", "gpt-4o"), new("GPT-4.1 mini", "gpt-4.1-mini"), new("GPT-6 Astra", "gpt-6-astra"), new("GPT-6.1 Sol", "gpt-6.1-sol"), new("GPT-6 Sol", "gpt-6-sol"), new("GPT-6 Luna", "gpt-6-luna")], Tier: ProviderPresetTier.Common),
        new(ProviderPlatform.Anthropic, "Claude (Anthropic)", "https://api.anthropic.com", "claude-sonnet-5-5", ProviderProtocol.AnthropicMessages, ProviderType.Cloud, false,
            "https://console.anthropic.com", "https://console.anthropic.com/settings/keys", "在 Anthropic 控制台创建 API Key。",
            [new("Claude Sonnet 5.5（当前默认）", "claude-sonnet-5-5"), new("Claude Opus 5.5", "claude-opus-5-5"),
             new("Claude Sonnet 5", "claude-sonnet-5"), new("Claude Opus 5", "claude-opus-5"),
             new("Claude Sonnet 4.6", "claude-sonnet-4-6"), new("Claude Opus 4.8", "claude-opus-4-8"),
             new("Claude Opus 4.7", "claude-opus-4-7"), new("Claude Opus 4.6", "claude-opus-4-6"),
             new("Claude Haiku 4.5", "claude-haiku-4-5"), new("Claude Sonnet 4.5（已弃用，2026-11-30 退役）", "claude-sonnet-4-5")],
            Tier: ProviderPresetTier.Common, VerifiedOn: "2026-10-07"),
        new(ProviderPlatform.Gemini, "Gemini (Google)", "https://generativelanguage.googleapis.com/v1beta", "gemini-3.5-flash", ProviderProtocol.GeminiGenerateContent, ProviderType.Cloud, false,
            "https://ai.google.dev", "https://aistudio.google.com/app/apikey", "在 Google AI Studio 创建 API Key。",
            [new("Gemini 3.8 Flash", "gemini-3.8-flash"), new("Gemini 3.5 Flash", "gemini-3.5-flash"), new("Gemini 3.1 Pro（预览）", "gemini-3.1-pro-preview")], Tier: ProviderPresetTier.Common),
        new(ProviderPlatform.DeepSeek, "DeepSeek", "https://api.deepseek.com/v1", "deepseek-flash", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, false,
            "https://platform.deepseek.com", "https://platform.deepseek.com/api_keys", "在 DeepSeek 开放平台创建 API Key。",
            [new("DeepSeek V4.1 Flash（当前）", "deepseek-flash"),
             new("DeepSeek V4 Pro（兼容别名，当前路由 V4.1 Flash）", "deepseek-v4-pro")],
            Tier: ProviderPresetTier.Common, VerifiedOn: "2026-10-07"),
        new(ProviderPlatform.MiMo, "MiMo（小米）", "https://api.xiaomimimo.com/v1", "mimo-v2.6-pro", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, false,
            "https://mimo.mi.com", "https://platform.xiaomimimo.com", "使用 Xiaomi MiMo OpenAI 兼容接口；请从官方控制台创建 API Key。",
            [new("MiMo V2.6 Pro（默认）", "mimo-v2.6-pro"), new("MiMo V2.6 Flash", "mimo-v2.6-flash"),
             new("MiMo V2.6 Pro UltraSpeed（需服务资格）", "mimo-v2.6-pro-ultraspeed"),
             new("MiMo V2.5 Pro（即将退役，2026-10-21）", "mimo-v2.5-pro"),
             new("MiMo V2.5（即将退役，2026-10-21）", "mimo-v2.5")],
            Tier: ProviderPresetTier.More, VerifiedOn: "2026-10-07"),
        new(ProviderPlatform.Qwen, "通义千问（阿里云百炼）", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, false,
            "https://bailian.console.aliyun.com", "https://bailian.console.aliyun.com/?apiKey=1", "在阿里云百炼控制台创建 API Key。",
            [new("Qwen 3.8 Max", "qwen3.8-max"), new("Qwen 3.8 Flash", "qwen3.8-flash"), new("Qwen 3.8 27B", "qwen3.8-27b"), new("Qwen Plus", "qwen-plus"), new("Qwen Turbo", "qwen-turbo"), new("Qwen Max", "qwen-max"), new("Qwen Long", "qwen-long")], Tier: ProviderPresetTier.Common, VerifiedOn: "2026-10-06"),
        new(ProviderPlatform.Doubao, "豆包（火山方舟）", "https://ark.cn-beijing.volces.com/api/v3", "doubao-seed-2-0-lite-260428", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, false,
            "https://console.volcengine.com/ark", "https://console.volcengine.com/ark/region:ark+cn-beijing/apiKey", "在火山方舟控制台创建 API Key。",
            [new("Doubao Seed 2.0 Lite（推荐）", "doubao-seed-2-0-lite-260428")], Tier: ProviderPresetTier.Common, VerifiedOn: "2026-10-06"),
        new(ProviderPlatform.Kimi, "Kimi（月之暗面）", "https://api.moonshot.cn/v1", "kimi-k2.6", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, false,
            "https://platform.moonshot.cn", "https://platform.moonshot.cn/console/api-keys", "在 Moonshot 开放平台创建 API Key。",
            [new("Kimi K2.6（通用）", "kimi-k2.6"), new("Kimi K3（旗舰）", "kimi-k3"), new("Kimi K2.7 Code（代码专用）", "kimi-k2.7-code"), new("Kimi K2.7 Code 高速版", "kimi-k2.7-code-highspeed")], Tier: ProviderPresetTier.Common, VerifiedOn: "2026-10-06"),
        new(ProviderPlatform.Zhipu, "智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", "glm-4-flash", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, false,
            "https://open.bigmodel.cn", "https://open.bigmodel.cn/usercenter/apikeys", "在智谱开放平台创建 API Key。",
            [new("GLM-4 Flash", "glm-4-flash"), new("GLM-4 Air", "glm-4-air"), new("GLM-4 Plus", "glm-4-plus"), new("GLM-4.5", "glm-4.5"), new("GLM-5.3", "glm-5.3"), new("GLM-5.2", "glm-5.2")], Tier: ProviderPresetTier.Common, VerifiedOn: "2026-10-06"),
        new(ProviderPlatform.SiliconFlow, "硅基流动", "https://api.siliconflow.cn/v1", "deepseek-ai/DeepSeek-V3", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, false,
            "https://siliconflow.cn", "https://cloud.siliconflow.cn/account/ak", "在硅基流动云平台创建 API Key。",
            [new("DeepSeek-V3", "deepseek-ai/DeepSeek-V3"), new("DeepSeek-V4 Pro", "Pro/deepseek-ai/DeepSeek-V4"), new("DeepSeek-V4 Flash", "deepseek-ai/DeepSeek-V4-Flash"), new("GLM-5.2 Pro", "Pro/zai-org/GLM-5.2"), new("Qwen2.5-7B-Instruct", "Qwen/Qwen2.5-7B-Instruct"), new("GLM-4-9B-Chat", "THUDM/glm-4-9b-chat")], Tier: ProviderPresetTier.Common, VerifiedOn: "2026-10-06"),
        new(ProviderPlatform.StepFun, "阶跃星辰", "https://api.stepfun.com/v1", "step-3.7-flash", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, false,
            "https://platform.stepfun.com", "https://platform.stepfun.com/api-key", "在阶跃星辰开放平台创建 API Key。",
            [new("Step 5 Preview", "step-5-preview"), new("Step 3.7 Flash", "step-3.7-flash"), new("Step 3.5 Flash", "step-3.5-flash"), new("Step 3.5 Flash 2603", "step-3.5-flash-2603"), new("Step-2 16K（旧型号）", "step-2-16k"), new("Step-1 32K（旧型号）", "step-1-32k")], VerifiedOn: "2026-10-07"),
        new(ProviderPlatform.MiniMax, "MiniMax", "https://api.minimax.cn/v1", "MiniMax-M3", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, false,
            "https://platform.minimax.cn", "https://platform.minimax.cn/user-center/basic-information/interface-key", "在 MiniMax 开放平台创建 API Key。",
            [new("MiniMax M3", "MiniMax-M3"), new("MiniMax M2.7", "MiniMax-M2.7"), new("MiniMax M2.7 Highspeed", "MiniMax-M2.7-highspeed"),
             new("MiniMax Text-01（历史型号）", "MiniMax-Text-01"), new("abab6.5s-chat（历史型号）", "abab6.5s-chat")], VerifiedOn: "2026-10-07"),
        new(ProviderPlatform.OpenRouter, "OpenRouter", "https://openrouter.ai/api/v1", "deepseek/deepseek-chat", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, false,
            "https://openrouter.ai", "https://openrouter.ai/keys", "在 OpenRouter 创建 API Key，可聚合数百家模型。",
            [new("DeepSeek V3", "deepseek/deepseek-chat"), new("GPT-4o mini", "openai/gpt-4o-mini"), new("Claude Sonnet", "anthropic/claude-3.5-sonnet")], Tier: ProviderPresetTier.Common),
        new(ProviderPlatform.Grok, "xAI Grok", "https://api.x.ai/v1", "grok-4.3", ProviderProtocol.OpenAIResponses, ProviderType.Cloud, false,
            "https://x.ai", "https://console.x.ai", "在 xAI 控制台创建 API Key。",
            [new("Grok 4.3", "grok-4.3"), new("Grok 4.7", "grok-4.7"),
             new("Grok 2 (历史型号)", "grok-2-latest"), new("Grok Beta（历史型号）", "grok-beta")], VerifiedOn: "2026-10-07"),
        new(ProviderPlatform.Mistral, "Mistral", "https://api.mistral.ai/v1", "mistral-small-latest", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, false,
            "https://console.mistral.ai", "https://console.mistral.ai/api-keys", "在 Mistral 控制台创建 API Key。",
            [new("Mistral Small (latest)", "mistral-small-latest"), new("Mistral Small 4", "mistral-small-2603"), new("Mistral Medium 3.5", "mistral-medium-3-5"),
             new("Mistral Nemo（已弃用，2026-05-22；新接入建议 Ministral 3 8B）", "open-mistral-nemo"),
             new("Ministral 3 8B（固定版本 v25.12）", "ministral-8b-2512"), new("Mistral Large (latest)", "mistral-large-latest")], VerifiedOn: "2026-10-07"),
        new(ProviderPlatform.Groq, "Groq", "https://api.groq.com/openai/v1", "openai/gpt-oss-120b", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, false,
            "https://console.groq.com", "https://console.groq.com/keys", "在 Groq 控制台创建 API Key（超高速推理）。",
            [new("GPT-OSS 120B（Groq 建议用于替换 Llama 3.3）", "openai/gpt-oss-120b"),
             new("GPT-OSS 20B（Groq 建议用于替换 Llama 3.1）", "openai/gpt-oss-20b"),
             new("Qwen 3.8 27B", "qwen/qwen3.8-27b"),
             new("Llama 3.3 70B Versatile（已于 2026-08-16 退役；部分企业账户例外）", "llama-3.3-70b-versatile"),
             new("Llama 3.1 8B Instant（已于 2026-08-16 退役；部分企业账户例外）", "llama-3.1-8b-instant")], VerifiedOn: "2026-10-07"),
        new(ProviderPlatform.Baichuan, "百川智能", "https://api.baichuan-ai.com/v1", "Baichuan4-Turbo", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, false,
            "https://platform.baichuan-ai.com", "https://platform.baichuan-ai.com/console/apikey", "在百川开放平台创建 API Key。",
            [new("Baichuan4-Turbo", "Baichuan4-Turbo"), new("Baichuan4-Air", "Baichuan4-Air"), new("Baichuan4", "Baichuan4"),
             new("Baichuan3-Turbo", "Baichuan3-Turbo"), new("Baichuan3-Turbo-128k", "Baichuan3-Turbo-128k"), new("Baichuan2-Turbo", "Baichuan2-Turbo")], VerifiedOn: "2026-10-07"),
        new(ProviderPlatform.Spark, "讯飞星火", "https://spark-api-open.xf-yun.com/v1", "4.0Ultra", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, false,
            "https://xinghuo.xfyun.cn", "https://console.xfyun.cn/services/bm35", "在讯飞开放平台创建 API Key。",
            [new("Spark 4.0 Ultra（当前 Ultra / X1.5）", "4.0Ultra"),
             new("Spark Max-32K", "max-32k"),
             new("Spark Max（generalv3.5；服务端已升级到 Ultra）", "generalv3.5"),
             new("Spark Pro（generalv3）", "generalv3"),
             new("Spark Pro-128K", "pro-128k"),
             new("Spark Lite", "lite")], VerifiedOn: "2026-10-07"),
        new(ProviderPlatform.Yi, "零一万物 Yi", "https://api.lingyiwanwu.com/v1", "yi-large", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, false,
            "https://platform.lingyiwanwu.com", "https://platform.lingyiwanwu.com/api-keys", "在零一万物开放平台创建 API Key。",
            [new("Yi-Large", "yi-large"), new("Yi-Lightning", "yi-lightning")]),
        new(ProviderPlatform.Together, "Together", "https://api.together.ai/v1", "meta-llama/Llama-3.3-70B-Instruct-Turbo", ProviderProtocol.OpenAICompatible, ProviderType.Cloud, false,
            "https://www.together.ai", "https://api.together.xyz/settings/api-keys", "在 Together 创建 API Key（开源模型聚合）。",
            [new("Llama 3.3 70B Turbo", "meta-llama/Llama-3.3-70B-Instruct-Turbo"),
             new("Thinking Machines Inkling", "thinkingmachines/Inkling"),
             new("Qwen 3.5 9B", "Qwen/Qwen3.5-9B"), new("Kimi K3", "moonshotai/Kimi-K3"),
             new("GLM 5.3 Flash", "zai-org/GLM-5.3-Flash"), new("GLM 5.3", "zai-org/GLM-5.3"),
             new("GLM 5.2", "zai-org/GLM-5.2"),
             new("DeepSeek V4 Flash 0731", "deepseek-ai/DeepSeek-V4-Flash-0731"),
             new("DeepSeek V4 Pro 0813", "deepseek-ai/DeepSeek-V4-Pro-0813"),
             new("DeepSeek V4.1 Flash", "deepseek-ai/DeepSeek-V4.1-Flash"),
             new("MiniMax M3", "MiniMaxAI/MiniMax-M3"), new("GPT-OSS 120B", "openai/gpt-oss-120b"),
             new("Qwen2.5 72B（历史型号）", "Qwen/Qwen2.5-72B-Instruct")], VerifiedOn: "2026-10-07"),
        new(ProviderPlatform.Ollama, "Ollama（本地）", "http://localhost:11434/v1", "qwen3", ProviderProtocol.OpenAICompatible, ProviderType.Local, false,
            "https://ollama.com", "", "本机运行 Ollama 后，在下方填入已拉取的模型名。",
            [new("Qwen3", "qwen3"), new("DeepSeek-R1", "deepseek-r1"), new("Llama 3.1", "llama3.1")], Tier: ProviderPresetTier.Local, RequiresApiKey: false),
        new(ProviderPlatform.LmStudio, "LM Studio（本地）", "http://localhost:1234/v1", "local-model", ProviderProtocol.OpenAICompatible, ProviderType.Local, false,
            "https://lmstudio.ai", "", "本机运行 LM Studio 并加载模型后，填入模型名。",
            [new("本地模型", "local-model")], Tier: ProviderPresetTier.Local, RequiresApiKey: false),
        new(ProviderPlatform.ManagedLocal, "话匣子本地模型", "http://127.0.0.1:0/v1", "managed-local", ProviderProtocol.OpenAICompatible, ProviderType.Local, false,
            "", "", "由话匣子管理模型文件和本地推理运行时。请在“本地模型”设置中安装模型。",
            [new("托管本地模型", "managed-local")], Tier: ProviderPresetTier.Local, RequiresApiKey: false),
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
            _ when apiBase.Contains("xiaomimimo.com") => ProviderPlatform.MiMo,
            _ when apiBase.Contains("dashscope") || apiBase.Contains("maas.aliyuncs.com") => ProviderPlatform.Qwen,
            _ when apiBase.Contains("ark.cn-beijing.volces.com") => ProviderPlatform.Doubao,
            _ when apiBase.Contains("moonshot.cn") => ProviderPlatform.Kimi,
            _ when apiBase.Contains("bigmodel.cn") => ProviderPlatform.Zhipu,
            _ when apiBase.Contains("siliconflow.cn") => ProviderPlatform.SiliconFlow,
            _ when apiBase.Contains("stepfun.com") => ProviderPlatform.StepFun,
            _ when apiBase.Contains("minimax.chat") || apiBase.Contains("minimaxi") || apiBase.Contains("api.minimax.cn") => ProviderPlatform.MiniMax,
            _ when apiBase.Contains("openrouter.ai") => ProviderPlatform.OpenRouter,
            _ when apiBase.Contains("api.x.ai") => ProviderPlatform.Grok,
            _ when apiBase.Contains("api.mistral.ai") => ProviderPlatform.Mistral,
            _ when apiBase.Contains("api.groq.com") => ProviderPlatform.Groq,
            _ when apiBase.Contains("baichuan-ai.com") => ProviderPlatform.Baichuan,
            _ when apiBase.Contains("xf-yun.com") => ProviderPlatform.Spark,
            _ when apiBase.Contains("lingyiwanwu.com") => ProviderPlatform.Yi,
            _ when apiBase.Contains("api.together.ai") || apiBase.Contains("api.together.xyz") => ProviderPlatform.Together,
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
