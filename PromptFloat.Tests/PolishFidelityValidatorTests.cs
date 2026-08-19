using PromptFloat.Models;
using PromptFloat.Services;
using Xunit;

namespace PromptFloat.Tests;

public sealed class PolishFidelityValidatorTests
{
    [Fact]
    public void Validate_ReportsMissingDateAndQuantity()
    {
        var context = new SmartContextAnalyzer().Analyze("原定8月20日交付，可能晚2天。");

        var result = PolishFidelityValidator.Validate(
            "原定8月20日交付，可能晚2天。",
            "项目交付时间可能会有所调整。",
            context);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Contains("8月20日"));
        Assert.Contains(result.Issues, issue => issue.Contains("2天"));
    }

    [Fact]
    public void Validate_RejectsStrengthenedCommitmentWhenOriginalIsUncertain()
    {
        var context = new SmartContextAnalyzer().Analyze("我可能周五完成。");

        var result = PolishFidelityValidator.Validate(
            "我可能周五完成。",
            "我保证周五一定完成。",
            context);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Contains("不确定"));
    }

    [Fact]
    public void Validate_AcceptsFaithfulRewrite()
    {
        var context = new SmartContextAnalyzer().Analyze("原定8月20日交付，可能晚2天。");

        var result = PolishFidelityValidator.Validate(
            "原定8月20日交付，可能晚2天。",
            "原计划8月20日交付，目前可能需要顺延2天。",
            context);

        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
    }
}
