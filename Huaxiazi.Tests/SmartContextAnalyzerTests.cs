using Huaxiazi.Models;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class SmartContextAnalyzerTests
{
    [Fact]
    public void Analyze_WorkplaceDelay_PreservesNumbersAndInfersConservativeContext()
    {
        var result = new SmartContextAnalyzer().Analyze("王总，原定8月20日交付，现在可能要晚2天，需求又改了。");

        Assert.Equal("职场沟通", result.Scenario);
        Assert.Equal("说明延期", result.Purpose);
        Assert.Equal("专业、克制", result.Formality);
        Assert.Equal(TextRiskLevel.High, result.RiskLevel);
        Assert.Contains("8月20日", result.FidelityAnchors);
        Assert.Contains("2天", result.FidelityAnchors);
        Assert.True(result.ContainsUncertainty);
    }

    [Fact]
    public void Analyze_PublicPost_DoesNotInventRecipientOrChannelBeyondEvidence()
    {
        var result = new SmartContextAnalyzer().Analyze("帮我把这段朋友圈写得自然一点，别太营销。");

        Assert.Equal("公开发布", result.Scenario);
        Assert.Equal("朋友圈", result.Channel);
        Assert.Equal(string.Empty, result.Recipient);
        Assert.True(result.Confidence >= 0.7);
    }

    [Fact]
    public void Merge_ExplicitContextAlwaysOverridesInference()
    {
        var inferred = new SmartContextAnalyzer().Analyze("准备发到公众号，说明项目延期。");
        var explicitContext = new ParsedInputContext(
            "正文", "老朋友", "微信", "解释近况", "随意", "私人沟通", "", "");

        var merged = SmartContextAnalyzer.Merge(explicitContext, inferred, "其他");

        Assert.Equal("老朋友", merged.Recipient);
        Assert.Equal("微信", merged.Channel);
        Assert.Equal("解释近况", merged.Purpose);
        Assert.Equal("随意", merged.Formality);
        Assert.Equal("私人沟通", merged.Scenario);
    }
}
