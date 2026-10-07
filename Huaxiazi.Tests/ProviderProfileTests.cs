using Huaxiazi.Models;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class ProviderProfileTests
{
    [Fact]
    public void ManagedLocalPlatform_IsAvailableForApplicationManagedModels()
    {
        Assert.True(Enum.TryParse<ProviderPlatform>("ManagedLocal", out var platform));
        Assert.Equal("ManagedLocal", platform.ToString());
    }

    [Fact]
    public void Clone_ManagedLocalProfile_PreservesInstallationAndRuntimeReferences()
    {
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.ManagedLocal,
            Type = ProviderType.Local,
            LocalModelInstallationId = "qwen3-4b@2507",
            LocalAdapterInstallationId = "huaxiazi-polish@1",
            LocalRuntimeProfileId = "balanced"
        };

        var clone = profile.Clone();

        Assert.Equal("qwen3-4b@2507", clone.LocalModelInstallationId);
        Assert.Equal("huaxiazi-polish@1", clone.LocalAdapterInstallationId);
        Assert.Equal("balanced", clone.LocalRuntimeProfileId);
    }

    [Fact]
    public void Clone_OpenRouterCapabilitySnapshot_PreservesValuesWithoutSharingMutableList()
    {
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.OpenRouter,
            Model = "vendor/model",
            OpenRouterCapabilitiesModelId = "vendor/model",
            OpenRouterSupportedParameters = ["response_format", "max_tokens"]
        };

        var clone = profile.Clone();
        Assert.Equal("vendor/model", clone.OpenRouterCapabilitiesModelId);
        Assert.NotNull(clone.OpenRouterSupportedParameters);
        Assert.NotSame(profile.OpenRouterSupportedParameters, clone.OpenRouterSupportedParameters);
        clone.OpenRouterSupportedParameters!.Add("temperature");

        Assert.Equal(new[] { "response_format", "max_tokens", "temperature" }, clone.OpenRouterSupportedParameters);
        Assert.Equal(new[] { "response_format", "max_tokens" }, profile.OpenRouterSupportedParameters);
    }

    [Fact]
    public void Clone_OpenAiResponsesProfile_PreservesProtocolAndLegacyProfileDefaultRemainsChatCompletions()
    {
        var responses = new ProviderProfile
        {
            Platform = ProviderPlatform.OpenAI,
            Protocol = ProviderProtocol.OpenAIResponses,
            ApiBase = "https://api.openai.com/v1",
            Model = "gpt-4.1-mini"
        };

        var clone = responses.Clone();
        var oldProfile = new ProviderProfile { Platform = ProviderPlatform.OpenAI };

        Assert.Equal(ProviderProtocol.OpenAIResponses, clone.Protocol);
        Assert.Equal(ProviderProtocol.OpenAICompatible, oldProfile.Protocol);
    }

    [Fact]
    public void GeminiPreset_OffersCurrentFlashAndProModelIds()
    {
        var models = ProviderPlatformCatalog.Get(ProviderPlatform.Gemini).Models!;

        Assert.Contains(models, model => model.ModelId == "gemini-3.8-flash");
        Assert.Contains(models, model => model.ModelId == "gemini-3.1-pro-preview");
        Assert.DoesNotContain(models, model => model.ModelId == "gemini-3.5-pro");
    }

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
