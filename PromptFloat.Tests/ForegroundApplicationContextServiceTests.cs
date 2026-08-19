using PromptFloat.Services;
using Xunit;

namespace PromptFloat.Tests;

public sealed class ForegroundApplicationContextServiceTests
{
    [Theory]
    [InlineData("OUTLOOK", "邮件", "职场沟通")]
    [InlineData("WXWORK", "企业微信", "职场沟通")]
    [InlineData("WECHAT", "微信", "")]
    [InlineData("CODE", "代码编辑器", "")]
    [InlineData("DingTalk.exe", "钉钉", "职场沟通")]
    [InlineData("Feishu", "飞书", "职场沟通")]
    [InlineData("THUNDERBIRD", "邮件", "职场沟通")]
    [InlineData("PYCHARM64", "代码编辑器", "")]
    [InlineData("TEAMS", "Teams", "职场沟通")]
    public void FromProcessName_MapsOnlyCoarseNonSensitiveContext(string process, string channel, string scenario)
    {
        var context = ForegroundApplicationContextService.FromProcessName(process);

        Assert.Equal(channel, context.Channel);
        Assert.Equal(scenario, context.Scenario);
        Assert.DoesNotContain("标题", context.Evidence);
    }

    [Fact]
    public void FromProcessName_UnknownOrBrowserProcess_DoesNotInventCommunicationContext()
    {
        Assert.Equal(string.Empty, ForegroundApplicationContextService.FromProcessName("chrome").Channel);
        Assert.Equal(string.Empty, ForegroundApplicationContextService.FromProcessName("unknown-app").Channel);
    }

    [Fact]
    public void Analyze_UsesSourceApplicationOnlyWhenTextHasNoStrongerEvidence()
    {
        var analyzer = new SmartContextAnalyzer();

        var inferred = analyzer.Analyze("麻烦确认一下附件。", ForegroundApplicationContextService.FromProcessName("OUTLOOK"));
        var explicitText = analyzer.Analyze("这段准备发朋友圈。", ForegroundApplicationContextService.FromProcessName("OUTLOOK"));

        Assert.Equal("邮件", inferred.Channel);
        Assert.Equal("职场沟通", inferred.Scenario);
        Assert.Equal("朋友圈", explicitText.Channel);
        Assert.Equal("公开发布", explicitText.Scenario);
    }
}
