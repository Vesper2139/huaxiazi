using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class SkillWeightAllocatorTests
{
    [Fact]
    public void Allocate_UsesSoftmaxAndCapsDominantSkill()
    {
        var weights = SkillWeightAllocator.Allocate(
            [("tone", 100), ("facts", 95), ("format", 90)], temperature: 20, maximumWeight: .7);

        Assert.Equal(3, weights.Count);
        Assert.True(weights["tone"] <= .7);
        Assert.Equal(1d, weights.Values.Sum());
        Assert.True(weights["tone"] > weights["facts"]);
    }

    [Fact]
    public void Allocate_SingleSkillGetsFullWeight()
    {
        var weights = SkillWeightAllocator.Allocate([("tone", 12)]);

        Assert.Equal(1d, weights["tone"]);
    }

    [Fact]
    public void Allocate_DeduplicatesIdsCaseInsensitively()
    {
        var weights = SkillWeightAllocator.Allocate([("Tone", 10), ("tone", 20), ("facts", 10)]);

        Assert.Equal(2, weights.Count);
        Assert.Contains("Tone", weights.Keys);
    }

    [Fact]
    public void Allocate_IsInvariantToCandidateInputOrder()
    {
        var first = SkillWeightAllocator.Allocate([("zeta", 100), ("alpha", 100), ("beta", 90)]);
        var second = SkillWeightAllocator.Allocate([("beta", 90), ("alpha", 100), ("zeta", 100)]);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Allocate_RoundingResidualNeverBreaksDominantMaximum()
    {
        var weights = SkillWeightAllocator.Allocate(
            [("dominant", 100), ("support", 99)], temperature: 20, maximumWeight: .7);

        Assert.True(weights["dominant"] <= .7);
        Assert.Equal(1d, weights.Values.Sum());
    }
}
