using Huaxiazi.Models;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class ProviderInferencePresetTests
{
    [Theory]
    [InlineData(InferenceLevel.Low, 0.2, 0.8, 1024, 120)]
    [InlineData(InferenceLevel.Medium, 0.4, 1.0, 2048, 120)]
    [InlineData(InferenceLevel.High, 0.3, 0.95, 4096, 180)]
    public void Apply_MapsReadableInferenceLevelsToCompatibleLegacyParameters(
        InferenceLevel level, double temperature, double topP, int maxTokens, int timeoutSeconds)
    {
        var profile = new ProviderProfile
        {
            Temperature = 1.7,
            TopP = 0.2,
            MaxTokens = 512,
            TimeoutSeconds = 30
        };

        ProviderInferencePresets.Apply(profile, level);

        Assert.Equal(level, profile.InferenceLevel);
        Assert.Equal(temperature, profile.Temperature);
        Assert.Equal(topP, profile.TopP);
        Assert.Equal(maxTokens, profile.MaxTokens);
        Assert.Equal(timeoutSeconds, profile.TimeoutSeconds);
        Assert.Contains($"temperature={temperature:0.0}", ProviderInferencePresets.Describe(level));
        Assert.Contains($"max_tokens={maxTokens}", ProviderInferencePresets.Describe(level));
        Assert.Contains($"超时={timeoutSeconds} 秒", ProviderInferencePresets.Describe(level));
        Assert.Contains("具体传参与推理行为以当前模型能力说明为准", ProviderInferencePresets.Describe(level));
    }

    [Fact]
    public void Apply_CustomLeavesExistingValuesForExpertEditing()
    {
        var profile = new ProviderProfile
        {
            Temperature = 1.1,
            TopP = 0.7,
            MaxTokens = 3333,
            TimeoutSeconds = 77
        };

        ProviderInferencePresets.Apply(profile, InferenceLevel.Custom);

        Assert.Equal(InferenceLevel.Custom, profile.InferenceLevel);
        Assert.Equal(1.1, profile.Temperature);
        Assert.Equal(0.7, profile.TopP);
        Assert.Equal(3333, profile.MaxTokens);
        Assert.Equal(77, profile.TimeoutSeconds);
        Assert.Contains("保留当前采样", ProviderInferencePresets.Describe(InferenceLevel.Custom));
    }

    [Fact]
    public void Apply_MediumDoesNotUseExperimentalOllamaBudgetForProductPreset()
    {
        var profile = new ProviderProfile
        {
            Type = ProviderType.Local,
            Platform = ProviderPlatform.Ollama,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "http://127.0.0.1:11434/v1",
            Model = "qwen3:4b"
        };

        ProviderInferencePresets.Apply(profile, InferenceLevel.Medium);

        Assert.Equal(0.4, profile.Temperature);
        Assert.Equal(1.0, profile.TopP);
        Assert.Equal(2048, profile.MaxTokens);
        Assert.Equal(120, profile.TimeoutSeconds);
        Assert.DoesNotContain("内部合成短文本诊断", ProviderInferencePresets.Describe(InferenceLevel.Medium, profile));
    }

    [Theory]
    [InlineData(ProviderPlatform.Ollama, ProviderType.Local, "http://127.0.0.1:11434/v1", "qwen3:4b", 2048)]
    [InlineData(ProviderPlatform.Ollama, ProviderType.Local, "http://127.0.0.1:11434/v1", "qwen3", 2048)]
    [InlineData(ProviderPlatform.Ollama, ProviderType.Local, "http://127.0.0.1:11434/v1", "qwen3:8b", 2048)]
    [InlineData(ProviderPlatform.Ollama, ProviderType.Local, "http://127.0.0.1:11435/v1", "qwen3:4b", 2048)]
    [InlineData(ProviderPlatform.Ollama, ProviderType.Cloud, "http://127.0.0.1:11434/v1", "qwen3:4b", 2048)]
    [InlineData(ProviderPlatform.Qwen, ProviderType.Cloud, "https://api.example.test/v1", "qwen3:4b", 2048)]
    public void Apply_MediumKeepsGenericBudgetForUnverifiedProfiles(
        ProviderPlatform platform, ProviderType type, string apiBase, string model, int expectedMaxTokens)
    {
        var profile = new ProviderProfile
        {
            Type = type,
            Platform = platform,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = apiBase,
            Model = model
        };

        ProviderInferencePresets.Apply(profile, InferenceLevel.Medium);

        Assert.Equal(expectedMaxTokens, profile.MaxTokens);
        Assert.DoesNotContain("内部合成短文本诊断", ProviderInferencePresets.Describe(InferenceLevel.Medium, profile));
    }
}
