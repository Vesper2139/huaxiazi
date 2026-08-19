using System.Linq;
using PromptFloat.Models;
using PromptFloat.Services;
using Xunit;

namespace PromptFloat.Tests;

public sealed class ProviderPlatformTests
{
    [Theory]
    [InlineData(ProviderPlatform.OpenAI, "https://api.openai.com/v1", "gpt-4o-mini", ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderPlatform.Anthropic, "https://api.anthropic.com", "claude-sonnet-4-5", ProviderProtocol.AnthropicMessages)]
    [InlineData(ProviderPlatform.Gemini, "https://generativelanguage.googleapis.com/v1beta", "gemini-2.5-flash", ProviderProtocol.GeminiGenerateContent)]
    [InlineData(ProviderPlatform.DeepSeek, "https://api.deepseek.com", "deepseek-v4-flash", ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderPlatform.Qwen, "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus", ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderPlatform.Doubao, "https://ark.cn-beijing.volces.com/api/v3", "doubao-seed-1-6-flash-250828", ProviderProtocol.OpenAICompatible)]
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
        Assert.Contains("Ollama（本地）", names);
        Assert.Contains("LM Studio（本地）", names);
        Assert.Contains("自定义 OpenAI 兼容接口", names);
    }
}
