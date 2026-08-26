using PromptFloat.Models;
using PromptFloat.Services;
using Xunit;

namespace PromptFloat.Tests;

public sealed class ProviderProfileTests
{
    [Fact]
    public void NormalizeProviderProfiles_EmptyCollection_CreatesUsableDefaultProfile()
    {
        var settings = new AppSettings
        {
            ProviderProfiles = [],
            ActiveProviderProfileId = "missing"
        };

        settings.NormalizeProviderProfiles();

        var profile = Assert.Single(settings.ProviderProfiles);
        Assert.Equal(profile.Id, settings.ActiveProviderProfileId);
        Assert.Equal("默认模型", profile.Name);
        Assert.Equal(ProviderType.Cloud, profile.Type);
        Assert.Equal("https://api.openai.com/v1", profile.ApiBase);
    }

    [Fact]
    public void NormalizeProviderProfiles_MissingActiveId_SelectsFirstProfile()
    {
        var settings = new AppSettings
        {
            ProviderProfiles =
            [
                new ProviderProfile { Id = "local", Name = "本地", Type = ProviderType.Local, ApiBase = "http://localhost:11434/v1", Model = "qwen" }
            ],
            ActiveProviderProfileId = "missing"
        };

        settings.NormalizeProviderProfiles();

        Assert.Equal("local", settings.ActiveProviderProfileId);
        Assert.Equal("local", settings.GetActiveProviderProfile().Id);
    }

    [Fact]
    public void NormalizeProviderProfiles_ClampsAdvancedParametersToSafeRanges()
    {
        var profile = new ProviderProfile { Temperature = 9, TopP = -2, MaxTokens = 99_999 };
        var settings = new AppSettings { ProviderProfiles = [profile] };

        settings.NormalizeProviderProfiles();

        Assert.Equal(2, profile.Temperature);
        Assert.Equal(0, profile.TopP);
        Assert.Equal(32768, profile.MaxTokens);
    }

    [Theory]
    [InlineData("https://api.anthropic.com", ProviderPlatform.Anthropic, ProviderProtocol.AnthropicMessages)]
    [InlineData("https://generativelanguage.googleapis.com/v1beta", ProviderPlatform.Gemini, ProviderProtocol.GeminiGenerateContent)]
    [InlineData("https://api.deepseek.com", ProviderPlatform.DeepSeek, ProviderProtocol.OpenAICompatible)]
    [InlineData("http://localhost:11434/v1", ProviderPlatform.Ollama, ProviderProtocol.OpenAICompatible)]
    [InlineData("http://localhost:1234/v1", ProviderPlatform.LmStudio, ProviderProtocol.OpenAICompatible)]
    public void ExplicitLegacyMigration_InfersPlatformFromApiBase(string apiBase, ProviderPlatform platform, ProviderProtocol protocol)
    {
        var profile = new ProviderProfile { ApiBase = apiBase, Platform = ProviderPlatform.OpenAI };

        ProviderPlatformCatalog.InferLegacyPlatform(profile);

        Assert.Equal(platform, profile.Platform);
        Assert.Equal(protocol, profile.Protocol);
    }
}
