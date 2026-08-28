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
    }
}
