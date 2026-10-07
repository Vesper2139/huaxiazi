using System;
using System.Text.Json;
using Huaxiazi.Models;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class ProviderRouterTests
{
    [Fact]
    public void ProviderRoutingModeSupportsExplicitAutomaticOptIn()
    {
        Assert.True(Enum.TryParse<ProviderRoutingMode>("Automatic", out var mode));
        Assert.Equal("Automatic", mode.ToString());
    }

    [Fact]
    public void AutomaticModeSelectsTheTaskAndTierBoundProfileAndConfiguredFallback()
    {
        var settings = new AppSettings
        {
            ProviderProfiles = [Cloud("fast", "https://fast.example.com/v1"), Local("balanced"), Cloud("reasoning", "https://reasoning.example.com/v1"), Cloud("backup", "https://backup.example.com/v1")],
            ActiveProviderProfileId = "backup",
            ProviderRoutingMode = ProviderRoutingMode.Automatic,
            PolishFastProviderProfileId = "fast",
            PolishBalancedProviderProfileId = "balanced",
            PolishReasoningProviderProfileId = "reasoning",
            PromptOptimizeFastProviderProfileId = "reasoning",
            FallbackProviderProfileId = "backup"
        };

        var route = ProviderRouter.Select(settings, ApplicationMode.Polish, ModelTier.Balanced);

        Assert.Equal("balanced", route.Profile.Id);
        Assert.Equal("backup", route.ConfiguredFallback?.Id);
        Assert.Equal(ModelTier.Balanced, route.SelectedTier);
        Assert.Equal("automatic-balanced", route.Reason);
    }

    [Theory]
    [InlineData(ApplicationMode.Polish, ModelTier.Fast, "polish-fast")]
    [InlineData(ApplicationMode.Polish, ModelTier.Balanced, "polish-balanced")]
    [InlineData(ApplicationMode.Polish, ModelTier.Reasoning, "polish-reasoning")]
    [InlineData(ApplicationMode.PromptOptimize, ModelTier.Fast, "prompt-fast")]
    [InlineData(ApplicationMode.PromptOptimize, ModelTier.Balanced, "prompt-balanced")]
    [InlineData(ApplicationMode.PromptOptimize, ModelTier.Reasoning, "prompt-reasoning")]
    public void AutomaticModeUsesTheExactBindingForEachTaskAndTier(ApplicationMode task, ModelTier tier, string expectedProfile)
    {
        var settings = new AppSettings
        {
            ProviderProfiles =
            [
                Cloud("polish-fast", "https://polish-fast.example.com/v1"),
                Cloud("polish-balanced", "https://polish-balanced.example.com/v1"),
                Cloud("polish-reasoning", "https://polish-reasoning.example.com/v1"),
                Cloud("prompt-fast", "https://prompt-fast.example.com/v1"),
                Cloud("prompt-balanced", "https://prompt-balanced.example.com/v1"),
                Cloud("prompt-reasoning", "https://prompt-reasoning.example.com/v1")
            ],
            ActiveProviderProfileId = "polish-fast",
            ProviderRoutingMode = ProviderRoutingMode.Automatic,
            PolishFastProviderProfileId = "polish-fast",
            PolishBalancedProviderProfileId = "polish-balanced",
            PolishReasoningProviderProfileId = "polish-reasoning",
            PromptOptimizeFastProviderProfileId = "prompt-fast",
            PromptOptimizeBalancedProviderProfileId = "prompt-balanced",
            PromptOptimizeReasoningProviderProfileId = "prompt-reasoning"
        };

        var route = ProviderRouter.Select(settings, task, tier);

        Assert.Equal(expectedProfile, route.Profile.Id);
        Assert.Equal(tier, route.SelectedTier);
    }

    [Fact]
    public void AutomaticModeRequiresAResolvedModelTier()
    {
        var settings = new AppSettings
        {
            ProviderProfiles = [Local("fast")],
            ActiveProviderProfileId = "fast",
            ProviderRoutingMode = ProviderRoutingMode.Automatic
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProviderRouter.Select(settings, ApplicationMode.Polish));

        Assert.Contains("确定模型档位", exception.Message);
    }

    [Theory]
    [InlineData(ApplicationMode.Polish, "润色")]
    [InlineData(ApplicationMode.PromptOptimize, "提示词优化")]
    public void AutomaticModeFailsClosedWhenExactTaskTierBindingIsMissing(ApplicationMode task, string taskLabel)
    {
        var settings = new AppSettings
        {
            ProviderProfiles = [Cloud("active", "https://api.example.com/v1")],
            ActiveProviderProfileId = "active",
            ProviderRoutingMode = ProviderRoutingMode.Automatic
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProviderRouter.Select(settings, task, ModelTier.Reasoning));

        Assert.Contains(taskLabel, exception.Message);
        Assert.Contains("Reasoning", exception.Message);
    }

    [Fact]
    public void LegacyConfigWithoutRoutingFields_DeserializesToManualMode()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{}")!;

        settings.NormalizeProviderProfiles();

        Assert.Equal(ProviderRoutingMode.Manual, settings.ProviderRoutingMode);
        Assert.Equal(string.Empty, settings.PolishProviderProfileId);
        Assert.Equal(string.Empty, settings.PromptOptimizeProviderProfileId);
        Assert.Equal(string.Empty, settings.FallbackProviderProfileId);
    }

    [Fact]
    public void RoutingSettings_RoundTripThroughConfigSerialization()
    {
        var settings = new AppSettings
        {
            ProviderProfiles = [Local("local"), Cloud("backup", "https://api.example.com/v1")],
            ActiveProviderProfileId = "local",
            ProviderRoutingMode = ProviderRoutingMode.PreferLocal,
            PolishProviderProfileId = "local",
            PromptOptimizeProviderProfileId = "backup",
            FallbackProviderProfileId = "backup"
        };

        var copy = settings.Clone();

        Assert.Equal(ProviderRoutingMode.PreferLocal, copy.ProviderRoutingMode);
        Assert.Equal("local", copy.PolishProviderProfileId);
        Assert.Equal("backup", copy.PromptOptimizeProviderProfileId);
        Assert.Equal("backup", copy.FallbackProviderProfileId);
    }

    [Fact]
    public void MissingRoutingFields_KeepExistingManualProviderBehavior()
    {
        var settings = new AppSettings
        {
            ProviderProfiles = [Cloud("cloud", "https://api.example.com/v1")],
            ActiveProviderProfileId = "cloud"
        };

        var route = ProviderRouter.Select(settings, ApplicationMode.Polish);

        Assert.Equal(ProviderRoutingMode.Manual, settings.ProviderRoutingMode);
        Assert.Equal("cloud", route.Profile.Id);
        Assert.Equal("manual-active-profile", route.Reason);
    }

    [Fact]
    public void ManualMode_UsesExplicitTaskProfileWhenConfigured()
    {
        var settings = new AppSettings
        {
            ProviderProfiles = [Cloud("active", "https://api.example.com/v1"), Local("polish")],
            ActiveProviderProfileId = "active",
            PolishProviderProfileId = "polish"
        };

        var route = ProviderRouter.Select(settings, ApplicationMode.Polish, ModelTier.Reasoning);

        Assert.Equal("polish", route.Profile.Id);
        Assert.Equal("task-profile", route.Reason);
    }

    [Fact]
    public void ManualMode_UsesExplicitlyConfiguredFallbackWithoutChangingPrimarySelection()
    {
        var settings = new AppSettings
        {
            ProviderProfiles = [Cloud("active", "https://api.example.com/v1"), Local("backup")],
            ActiveProviderProfileId = "active",
            FallbackProviderProfileId = "backup"
        };

        var route = ProviderRouter.Select(settings, ApplicationMode.Polish);

        Assert.Equal("active", route.Profile.Id);
        Assert.Equal("manual-active-profile", route.Reason);
        Assert.Equal("backup", route.ConfiguredFallback?.Id);
    }

    [Fact]
    public void LocalOnly_UsesLocalTaskProfileAndNeverReturnsCloudFallback()
    {
        var settings = new AppSettings
        {
            ProviderProfiles = [Cloud("active", "https://api.example.com/v1"), Local("polish")],
            ActiveProviderProfileId = "active",
            PolishProviderProfileId = "polish",
            FallbackProviderProfileId = "active",
            ProviderRoutingMode = ProviderRoutingMode.LocalOnly
        };

        var route = ProviderRouter.Select(settings, ApplicationMode.Polish);

        Assert.Equal("polish", route.Profile.Id);
        Assert.Null(route.ConfiguredFallback);
        Assert.True(ProviderRouter.IsStrictlyLocal(route.Profile));
    }

    [Theory]
    [InlineData("https://api.example.com/v1")]
    [InlineData("http://192.168.1.12:11434/v1")]
    public void LocalOnly_RejectsNonLoopbackEndpointsEvenWhenProfileSaysLocal(string apiBase)
    {
        var settings = new AppSettings
        {
            ProviderProfiles = [
                Cloud("active", "https://api.example.com/v1"),
                Local("misconfigured", apiBase)
            ],
            ActiveProviderProfileId = "active",
            ProviderRoutingMode = ProviderRoutingMode.LocalOnly
        };

        var error = Assert.Throws<InvalidOperationException>(() => ProviderRouter.Select(settings, ApplicationMode.Polish));

        Assert.Contains("不会改用云端", error.Message);
    }

    [Fact]
    public void PreferLocal_UsesConfiguredCloudFallbackOnlyWhenNoLocalProfileExists()
    {
        var settings = new AppSettings
        {
            ProviderProfiles = [Cloud("backup", "https://api.example.com/v1")],
            ActiveProviderProfileId = "backup",
            FallbackProviderProfileId = "backup",
            ProviderRoutingMode = ProviderRoutingMode.PreferLocal
        };

        var route = ProviderRouter.Select(settings, ApplicationMode.Polish);

        Assert.Equal("backup", route.Profile.Id);
        Assert.Equal("configured-fallback-no-preferred-profile", route.Reason);
    }

    [Fact]
    public void PreferLocal_RefusesImplicitCloudFallback()
    {
        var settings = new AppSettings
        {
            ProviderProfiles = [Cloud("active", "https://api.example.com/v1")],
            ActiveProviderProfileId = "active",
            ProviderRoutingMode = ProviderRoutingMode.PreferLocal
        };

        Assert.Throws<InvalidOperationException>(() => ProviderRouter.Select(settings, ApplicationMode.Polish));
    }

    [Fact]
    public void ConfirmedPreferences_AreIncludedOnlyWhenEveryPossibleTargetIsLocalOrCloudConsentIsEnabled()
    {
        var settings = new AppSettings
        {
            ProviderProfiles = [Local("local"), Cloud("backup", "https://api.example.com/v1")],
            ActiveProviderProfileId = "local",
            PolishProviderProfileId = "local",
            FallbackProviderProfileId = "backup",
            ProviderRoutingMode = ProviderRoutingMode.PreferLocal
        };

        var localWithCloudFallback = ProviderRouter.Select(settings, ApplicationMode.Polish);
        Assert.False(PreferenceDisclosurePolicy.CanSendConfirmedPreferences(settings, localWithCloudFallback));

        settings.ShareConfirmedPreferencesWithCloud = true;
        Assert.True(PreferenceDisclosurePolicy.CanSendConfirmedPreferences(settings, localWithCloudFallback));

        settings.ShareConfirmedPreferencesWithCloud = false;
        settings.ProviderRoutingMode = ProviderRoutingMode.LocalOnly;
        var localOnly = ProviderRouter.Select(settings, ApplicationMode.Polish);
        Assert.True(PreferenceDisclosurePolicy.CanSendConfirmedPreferences(settings, localOnly));

        settings.ProviderProfiles = [Local("lan", "http://192.168.1.12:11434/v1")];
        settings.ActiveProviderProfileId = "lan";
        settings.ProviderRoutingMode = ProviderRoutingMode.Manual;
        var unverifiedLocalEndpoint = ProviderRouter.Select(settings, ApplicationMode.Polish);
        Assert.False(PreferenceDisclosurePolicy.CanSendConfirmedPreferences(settings, unverifiedLocalEndpoint));
    }

    private static ProviderProfile Cloud(string id, string apiBase) => new()
    {
        Id = id,
        Type = ProviderType.Cloud,
        Platform = ProviderPlatform.OpenAI,
        Protocol = ProviderProtocol.OpenAICompatible,
        ApiBase = apiBase,
        Model = "cloud-model"
    };

    private static ProviderProfile Local(string id, string apiBase = "http://localhost:11434/v1") => new()
    {
        Id = id,
        Type = ProviderType.Local,
        Platform = ProviderPlatform.Ollama,
        Protocol = ProviderProtocol.OpenAICompatible,
        ApiBase = apiBase,
        Model = "local-model"
    };
}
