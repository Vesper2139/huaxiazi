using PromptFloat.Models;
using Xunit;

namespace PromptFloat.Tests;

/// <summary>
/// 验证 AppSettings.GetDefaultCategory() / GetDefaultDepth() 对“中文默认值”的解析。
/// 这两个方法把配置里的中文显示名（如“编程开发”“详细”）解析回枚举，
/// 解析失败应安全回退到 General / Standard。
/// </summary>
public class AppSettingsTests
{
    [Fact]
    public void Defaults_DoNotStartWithWindowsOrReadClipboard()
    {
        var settings = new AppSettings();

        Assert.False(settings.StartWithWindows);
        Assert.False(settings.ClipboardAutoRead);
        Assert.Equal("其他", settings.DefaultPolishScenario);
    }

    [Fact]
    public void NormalizePromptSettings_RepairsEmptyCategoriesAndClampsHistoryLimit()
    {
        var settings = new AppSettings
        {
            EnabledPromptCategories = [],
            PromptHistoryLimit = 500
        };

        settings.NormalizePromptSettings();

        Assert.Equal([PromptCategory.General], settings.EnabledPromptCategories);
        Assert.Equal(100, settings.PromptHistoryLimit);
    }
    [Theory]
    [InlineData("通用任务", PromptCategory.General)]
    [InlineData("编程开发", PromptCategory.Coding)]
    [InlineData("文案写作", PromptCategory.Writing)]
    [InlineData("数据分析", PromptCategory.Analysis)]
    [InlineData("学术研究", PromptCategory.Research)]
    [InlineData("创意设计", PromptCategory.Creative)]
    public void GetDefaultCategory_ParsesChineseDisplayName(string value, PromptCategory expected)
    {
        var s = new AppSettings { DefaultCategory = value };
        Assert.Equal(expected, s.GetDefaultCategory());
    }

    [Fact]
    public void GetDefaultCategory_UnknownValue_FallsBackToGeneral()
    {
        var s = new AppSettings { DefaultCategory = "不存在的类别" };
        Assert.Equal(PromptCategory.General, s.GetDefaultCategory());
    }

    [Fact]
    public void GetDefaultCategory_EmptyValue_FallsBackToGeneral()
    {
        var s = new AppSettings { DefaultCategory = "" };
        Assert.Equal(PromptCategory.General, s.GetDefaultCategory());
    }

    [Theory]
    [InlineData("简洁", PromptDepth.Concise)]
    [InlineData("标准", PromptDepth.Standard)]
    [InlineData("详细", PromptDepth.Detailed)]
    public void GetDefaultDepth_ParsesChineseDisplayName(string value, PromptDepth expected)
    {
        var s = new AppSettings { DefaultDepth = value };
        Assert.Equal(expected, s.GetDefaultDepth());
    }

    [Fact]
    public void GetDefaultDepth_UnknownValue_FallsBackToStandard()
    {
        var s = new AppSettings { DefaultDepth = "超详细" };
        Assert.Equal(PromptDepth.Standard, s.GetDefaultDepth());
    }

    [Fact]
    public void UserPersona_DefaultsToEmptyString()
    {
        var s = new AppSettings();
        Assert.Equal(string.Empty, s.UserPersona);
    }
}
