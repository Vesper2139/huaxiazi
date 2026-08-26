using System.Collections.Generic;
using System.Linq;
using Huaxiazi.Models;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class ProviderPlatformTests
{
    [Theory]
    [InlineData(ProviderPlatform.OpenAI, "https://api.openai.com/v1", "gpt-4o-mini", ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderPlatform.Anthropic, "https://api.anthropic.com", "claude-sonnet-4-5", ProviderProtocol.AnthropicMessages)]
    [InlineData(ProviderPlatform.Gemini, "https://generativelanguage.googleapis.com/v1beta", "gemini-3.5-flash", ProviderProtocol.GeminiGenerateContent)]
    [InlineData(ProviderPlatform.DeepSeek, "https://api.deepseek.com/v1", "deepseek-v4-flash", ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderPlatform.Qwen, "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus", ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderPlatform.Doubao, "https://ark.cn-beijing.volces.com/api/v3", "doubao-seed-2-0-lite-260215", ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderPlatform.Kimi, "https://api.moonshot.cn/v1", "kimi-k2-0905-preview", ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderPlatform.Zhipu, "https://open.bigmodel.cn/api/paas/v4", "glm-4-flash", ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderPlatform.SiliconFlow, "https://api.siliconflow.cn/v1", "deepseek-ai/DeepSeek-V3", ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderPlatform.StepFun, "https://api.stepfun.com/v1", "step-2-16k", ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderPlatform.MiniMax, "https://api.minimax.chat/v1", "MiniMax-Text-01", ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderPlatform.OpenRouter, "https://openrouter.ai/api/v1", "deepseek/deepseek-chat", ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderPlatform.Grok, "https://api.x.ai/v1", "grok-2-latest", ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderPlatform.Mistral, "https://api.mistral.ai/v1", "mistral-small-latest", ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderPlatform.Groq, "https://api.groq.com/openai/v1", "llama-3.3-70b-versatile", ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderPlatform.Baichuan, "https://api.baichuan-ai.com/v1", "Baichuan4-Turbo", ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderPlatform.Spark, "https://spark-api-open.xf-yun.com/v1", "generalv3.5", ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderPlatform.Yi, "https://api.lingyiwanwu.com/v1", "yi-large", ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderPlatform.Together, "https://api.together.xyz/v1", "meta-llama/Llama-3.3-70B-Instruct-Turbo", ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderPlatform.Ollama, "http://localhost:11434/v1", "qwen3", ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderPlatform.LmStudio, "http://localhost:1234/v1", "local-model", ProviderProtocol.OpenAICompatible)]
    public void ApplyPreset_UsesPlatformOfficialDefaults(ProviderPlatform platform, string apiBase, string model, ProviderProtocol protocol)
    {
        var profile = new ProviderProfile();

        ProviderPlatformCatalog.ApplyPreset(profile, platform);

        Assert.Equal(platform, profile.Platform);
        Assert.Equal(apiBase, profile.ApiBase);
        Assert.Equal(model, profile.Model);
        Assert.Equal(protocol, profile.Protocol);
    }

    [Fact]
    public void Catalog_ExposesFriendlyPlatformNamesInsteadOfCloudLocalProtocol()
    {
        var names = ProviderPlatformCatalog.Options.Select(option => option.DisplayName).ToArray();

        Assert.Contains("OpenAI", names);
        Assert.Contains("Claude (Anthropic)", names);
        Assert.Contains("Gemini (Google)", names);
        Assert.Contains("DeepSeek", names);
        Assert.Contains("通义千问（阿里云百炼）", names);
        Assert.Contains("豆包（火山方舟）", names);
        Assert.Contains("Kimi（月之暗面）", names);
        Assert.Contains("智谱 GLM", names);
        Assert.Contains("硅基流动", names);
        Assert.Contains("阶跃星辰", names);
        Assert.Contains("MiniMax", names);
        Assert.Contains("OpenRouter", names);
        Assert.Contains("xAI Grok", names);
        Assert.Contains("Mistral", names);
        Assert.Contains("Groq", names);
        Assert.Contains("百川智能", names);
        Assert.Contains("讯飞星火", names);
        Assert.Contains("零一万物 Yi", names);
        Assert.Contains("Together", names);
        Assert.Contains("Ollama（本地）", names);
        Assert.Contains("LM Studio（本地）", names);
        Assert.Contains("自定义 OpenAI 兼容接口", names);
    }

    [Fact]
    public void Catalog_ExposesModelListsContainingDefaultModelPerProvider()
    {
        foreach (var option in ProviderPlatformCatalog.Options)
        {
            if (string.IsNullOrWhiteSpace(option.DefaultModel)) continue; // 自定义接口无预设模型
            var models = Assert.IsAssignableFrom<IReadOnlyList<ModelDefinition>>(option.Models);
            Assert.NotEmpty(models);
            Assert.Contains(models, model => model.ModelId == option.DefaultModel);
        }
    }

    [Fact]
    public void Catalog_GroupsCommonLocalMoreAndCustomPlatforms()
    {
        Assert.Equal(ProviderPresetTier.Common, ProviderPlatformCatalog.Get(ProviderPlatform.DeepSeek).Tier);
        Assert.Equal(ProviderPresetTier.Common, ProviderPlatformCatalog.Get(ProviderPlatform.OpenAI).Tier);
        Assert.Equal(ProviderPresetTier.Local, ProviderPlatformCatalog.Get(ProviderPlatform.Ollama).Tier);
        Assert.Equal(ProviderPresetTier.Custom, ProviderPlatformCatalog.Get(ProviderPlatform.CustomOpenAICompatible).Tier);
        Assert.Equal(ProviderPresetTier.More, ProviderPlatformCatalog.Get(ProviderPlatform.Groq).Tier);
    }

    [Fact]
    public void Catalog_CommonTierMatchesTheProductApprovedProviderSet()
    {
        var common = ProviderPlatformCatalog.Options
            .Where(option => option.Tier == ProviderPresetTier.Common)
            .Select(option => option.Platform)
            .ToHashSet();

        Assert.Equal(
            new HashSet<ProviderPlatform>
            {
                ProviderPlatform.DeepSeek,
                ProviderPlatform.Qwen,
                ProviderPlatform.Doubao,
                ProviderPlatform.Kimi,
                ProviderPlatform.Zhipu,
                ProviderPlatform.SiliconFlow,
                ProviderPlatform.OpenAI,
                ProviderPlatform.Anthropic,
                ProviderPlatform.Gemini,
                ProviderPlatform.OpenRouter
            },
            common);
    }

    [Fact]
    public void Catalog_DeclaresSecretRequirementAndVerificationDate()
    {
        var cloud = ProviderPlatformCatalog.Get(ProviderPlatform.DeepSeek);
        var local = ProviderPlatformCatalog.Get(ProviderPlatform.Ollama);

        Assert.True(cloud.RequiresApiKey);
        Assert.False(local.RequiresApiKey);
        Assert.Equal("2026-08-20", cloud.VerifiedOn);
    }

    [Fact]
    public void Catalog_UnknownPersistedPlatformFallsBackToCustomInsteadOfCrashing()
    {
        var option = ProviderPlatformCatalog.Get((ProviderPlatform)999);

        Assert.Equal(ProviderPlatform.CustomOpenAICompatible, option.Platform);
    }

    [Fact]
    public void NormalizeProviderProfiles_DoesNotReclassifyAnExplicitPlatformFromItsEndpoint()
    {
        var profile = new ProviderProfile
        {
            Id = "company-proxy",
            Name = "公司代理",
            Platform = ProviderPlatform.OpenAI,
            ApiBase = "https://proxy.example.com/v1",
            Model = "company-model"
        };
        var settings = new AppSettings
        {
            ProviderProfiles = [profile],
            ActiveProviderProfileId = profile.Id
        };

        settings.NormalizeProviderProfiles();

        Assert.Equal(ProviderPlatform.OpenAI, profile.Platform);
    }

    [Fact]
    public void InferLegacyPlatform_RecognizesNewChineseVendors()
    {
        Assert.Equal(ProviderPlatform.Kimi, InferPlatform("https://api.moonshot.cn/v1"));
        Assert.Equal(ProviderPlatform.Zhipu, InferPlatform("https://open.bigmodel.cn/api/paas/v4"));
        Assert.Equal(ProviderPlatform.SiliconFlow, InferPlatform("https://api.siliconflow.cn/v1"));
        Assert.Equal(ProviderPlatform.StepFun, InferPlatform("https://api.stepfun.com/v1"));
        Assert.Equal(ProviderPlatform.MiniMax, InferPlatform("https://api.minimax.chat/v1"));
        Assert.Equal(ProviderPlatform.OpenRouter, InferPlatform("https://openrouter.ai/api/v1"));
        Assert.Equal(ProviderPlatform.Grok, InferPlatform("https://api.x.ai/v1"));
        Assert.Equal(ProviderPlatform.Mistral, InferPlatform("https://api.mistral.ai/v1"));
        Assert.Equal(ProviderPlatform.Groq, InferPlatform("https://api.groq.com/openai/v1"));
        Assert.Equal(ProviderPlatform.Baichuan, InferPlatform("https://api.baichuan-ai.com/v1"));
        Assert.Equal(ProviderPlatform.Spark, InferPlatform("https://spark-api-open.xf-yun.com/v1"));
        Assert.Equal(ProviderPlatform.Yi, InferPlatform("https://api.lingyiwanwu.com/v1"));
        Assert.Equal(ProviderPlatform.Together, InferPlatform("https://api.together.xyz/v1"));
    }

    private static ProviderPlatform InferPlatform(string apiBase)
    {
        var profile = new ProviderProfile { ApiBase = apiBase };
        ProviderPlatformCatalog.InferLegacyPlatform(profile);
        return profile.Platform;
    }
}
