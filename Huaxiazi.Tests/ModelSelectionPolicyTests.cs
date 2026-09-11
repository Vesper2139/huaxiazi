using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class ModelSelectionPolicyTests
{
    [Fact]
    public void Select_EscalatesForRiskToolsAndLongContext()
    {
        var result = ModelSelectionPolicy.Select(new ModelSelectionRequest(1000, 20000, true, true, false));

        Assert.Equal(ModelTier.Reasoning, result.Tier);
        Assert.True(result.RequiresFallback);
        Assert.True(result.MinimumSafetyScore >= .99);
    }

    [Fact]
    public void Select_UsesFastTierOnlyForSimpleLowLatencyTask()
    {
        var result = ModelSelectionPolicy.Select(new ModelSelectionRequest(100, 100, false, false, false, 1000));

        Assert.Equal(ModelTier.Fast, result.Tier);
        Assert.False(result.RequiresFallback);
    }

    [Fact]
    public void Select_RejectsNegativeMeasurementsInsteadOfDowngradingSafety()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ModelSelectionPolicy.Select(new ModelSelectionRequest(-1, 100, false, false, false)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModelSelectionPolicy.Select(new ModelSelectionRequest(100, -1, false, false, false)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModelSelectionPolicy.Select(new ModelSelectionRequest(100, 100, false, false, false, -1)));
    }
}
