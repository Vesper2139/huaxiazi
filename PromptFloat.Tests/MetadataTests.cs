using PromptFloat.Models;
using Xunit;

namespace PromptFloat.Tests;

/// <summary>
/// 验证 PromptCategory / PromptDepth 的元数据（中文名、优化重点、结构模板）。
/// 这些是注入 SystemPrompt 占位符的来源，必须与规格一致。
/// </summary>
public class MetadataTests
{
    [Fact]
    public void AssemblyMetadata_UsesVesperAsVisibleProductName()
    {
        var assembly = typeof(AppSettings).Assembly;
        Assert.Equal("Vesper", assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyProductAttribute), false)
            .Cast<System.Reflection.AssemblyProductAttribute>().Single().Product);
        Assert.Equal("Vesper", assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyTitleAttribute), false)
            .Cast<System.Reflection.AssemblyTitleAttribute>().Single().Title);
    }

    [Fact]
    public void AssemblyMetadata_UsesCurrentReleaseVersion()
    {
        var version = typeof(AppSettings).Assembly.GetName().Version;

        Assert.Equal(new System.Version(1, 5, 0, 0), version);
    }

    [Fact]
    public void PromptCategory_HasExactlySixCategories()
    {
        Assert.Equal(6, PromptCategoryMetadata.AllCategories.Count);
    }

    [Theory]
    [InlineData(PromptCategory.General, "通用任务")]
    [InlineData(PromptCategory.Coding, "编程开发")]
    [InlineData(PromptCategory.Writing, "文案写作")]
    [InlineData(PromptCategory.Analysis, "数据分析")]
    [InlineData(PromptCategory.Research, "学术研究")]
    [InlineData(PromptCategory.Creative, "创意设计")]
    public void PromptCategory_GetDisplayName_ReturnsExpectedChinese(PromptCategory category, string expected)
    {
        Assert.Equal(expected, category.GetDisplayName());
    }

    [Theory]
    [InlineData(PromptCategory.General, "目标、背景、约束、输出")]
    [InlineData(PromptCategory.Coding, "技术栈、架构、功能、边界、代码规范")]
    [InlineData(PromptCategory.Writing, "受众、语气、结构、长度、表达目标")]
    [InlineData(PromptCategory.Analysis, "数据来源、指标、分析方法、输出格式")]
    [InlineData(PromptCategory.Research, "研究问题、方法、证据、引用、严谨性")]
    [InlineData(PromptCategory.Creative, "风格、构图、视觉元素、限制条件")]
    public void PromptCategory_GetOptimizationFocus_ContainsExpected(PromptCategory category, string expected)
    {
        var focus = category.GetOptimizationFocus();
        Assert.False(string.IsNullOrWhiteSpace(focus));
        Assert.Contains(expected, focus);
    }

    [Fact]
    public void PromptDepth_HasExactlyThreeDepths()
    {
        Assert.Equal(3, PromptDepthMetadata.AllDepths.Count);
    }

    [Theory]
    [InlineData(PromptDepth.Concise, "简洁")]
    [InlineData(PromptDepth.Standard, "标准")]
    [InlineData(PromptDepth.Detailed, "详细")]
    public void PromptDepth_GetDisplayName_ReturnsExpectedChinese(PromptDepth depth, string expected)
    {
        Assert.Equal(expected, depth.GetDisplayName());
    }

    [Theory]
    [InlineData(PromptDepth.Concise,
        "目标 / 核心要求 / 关键限制 / 输出格式")]
    [InlineData(PromptDepth.Standard,
        "角色 / 任务目标 / 背景 / 具体要求 / 约束条件 / 执行步骤 / 输出格式")]
    [InlineData(PromptDepth.Detailed,
        "角色定位 / 任务背景 / 核心目标 / 输入信息 / 详细任务 / 执行流程 / 约束条件 / 判断标准 / 异常情况 / 输出结构 / 质量要求 / 禁止事项 / 最终交付要求")]
    public void PromptDepth_GetStructureTemplate_ContainsExpected(PromptDepth depth, string expectedSegment)
    {
        var tpl = depth.GetStructureTemplate();
        Assert.StartsWith("结构模板：", tpl);
        Assert.Contains(expectedSegment, tpl);
    }
}
