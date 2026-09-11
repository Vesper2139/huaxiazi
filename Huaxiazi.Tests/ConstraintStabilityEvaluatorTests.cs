using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class ConstraintStabilityEvaluatorTests
{
    [Fact]
    public void Evaluate_PassesStablePersonalizationAndAnchors()
    {
        var report = ConstraintStabilityEvaluator.Evaluate(
            ["简洁回复；保留日期 9 月 20 日", "简洁回复；保留日期 9 月 20 日"],
            new ConstraintSpec { Required = ["回复"], Forbidden = ["夸大"], Anchors = ["9 月 20 日"] });

        Assert.True(report.Passed);
        Assert.Equal(1d, report.ConstraintPassRate);
        Assert.Equal(1d, report.CrossRunConsistency);
    }

    [Fact]
    public void Evaluate_AllowsWordingVariationWhenConstraintStatesStayStable()
    {
        var report = ConstraintStabilityEvaluator.Evaluate(
            ["简洁地回复，保留日期 9 月 20 日", "请用直接措辞回答，并保留日期 9 月 20 日"],
            new ConstraintSpec { Required = ["回"], Forbidden = ["夸大"], Anchors = ["9 月 20 日"] });

        Assert.True(report.Passed);
        Assert.Equal(1d, report.CrossRunConsistency);
    }

    [Fact]
    public void Evaluate_FailsWhenPersonalizationDropsAnchorOrAddsForbiddenText()
    {
        var report = ConstraintStabilityEvaluator.Evaluate(
            ["简洁回复；保留日期 9 月 20 日", "夸大承诺"],
            new ConstraintSpec { Required = ["简洁"], Forbidden = ["夸大"], Anchors = ["9 月 20 日"] });

        Assert.False(report.Passed);
        Assert.True(report.ConstraintPassRate < 1d);
        Assert.True(report.AnchorPreservationRate < 1d);
    }

    [Fact]
    public void Evaluate_SupportsApprovedWordingVariantsWithoutWeakeningStrictRequirements()
    {
        var report = ConstraintStabilityEvaluator.Evaluate(
            ["请以专业、克制的语气回复。", "请用正式且准确的表达。"],
            new ConstraintSpec
            {
                RequiredAnyOf = [["专业", "正式"]],
                Required = ["请"],
                Forbidden = ["夸大"]
            });

        Assert.True(report.Passed);
        Assert.Equal(1d, report.ConstraintPassRate);
    }

    [Fact]
    public void Evaluate_RejectsEmptyApprovedVariantGroup()
    {
        var report = ConstraintStabilityEvaluator.Evaluate(
            ["输出结果"],
            new ConstraintSpec { RequiredAnyOf = [[]] });

        Assert.False(report.Passed);
        Assert.Equal(0d, report.ConstraintPassRate);
    }
}
