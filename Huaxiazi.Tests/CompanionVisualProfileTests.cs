using System.Collections.Generic;
using Huaxiazi.Models;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class CompanionVisualProfileTests
{
    [Fact]
    public void VisualOverrides_AreResolvedByProfileWithoutSkinIdLogicInRenderer()
    {
        var profile = CompanionVisualProfile.Create(
            "spritesheet",
            null,
            frameStateOverrides: new Dictionary<string, string> { ["Dragging"] = "Idle" },
            idleBehaviorOverrides: new Dictionary<string, string> { ["CuriousLook"] = "Listening" });

        Assert.Equal(CompanionVisualState.Idle, profile.ResolveFrameState(CompanionVisualState.Dragging));
        Assert.Equal(CompanionVisualState.Listening,
            profile.ResolveIdleBehavior(CompanionIdleBehavior.CuriousLook, CompanionVisualState.Curious));
        Assert.Equal(CompanionVisualState.Warning,
            profile.ResolveFrameState(CompanionVisualState.Warning));
    }

    [Fact]
    public void ImageProfiles_ResolveFrameOverridesBeforeLoadingStateAssets()
    {
        var profile = CompanionVisualProfile.Create(
            "image",
            new Dictionary<string, string>
            {
                [nameof(CompanionVisualState.Curious)] = "curious.png",
                [nameof(CompanionVisualState.Warning)] = "warning.png"
            },
            frameStateOverrides: new Dictionary<string, string>
            {
                [nameof(CompanionVisualState.Curious)] = nameof(CompanionVisualState.Warning)
            });

        var resolved = profile.ResolveFrameState(CompanionVisualState.Curious);

        Assert.Equal(CompanionVisualState.Warning, resolved);
        Assert.True(profile.TryGetAsset(resolved, out var asset));
        Assert.Equal("warning.png", asset);
    }
}
