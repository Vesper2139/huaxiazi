using Huaxiazi.Models;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class ProductModelTests
{
    [Fact]
    public void NormalizeProductModes_KeepsBothPrimaryTasksReachableForLegacyConfigurations()
    {
        var settings = new AppSettings
        {
            EnabledModes = [ApplicationMode.Polish],
            DefaultMode = ApplicationMode.Polish
        };

        settings.NormalizeProductModes();

        Assert.Contains(ApplicationMode.Polish, settings.EnabledModes);
        Assert.Contains(ApplicationMode.PromptOptimize, settings.EnabledModes);
    }

    [Fact]
    public void NewSettings_ProvideEditableBuiltInOptimizationPresets()
    {
        var settings = new AppSettings();

        Assert.Contains(settings.OptimizationPresets, preset => preset.Name == "编程");
        Assert.Contains(settings.OptimizationPresets, preset => preset.Name == "文案");
        Assert.Contains(settings.OptimizationPresets, preset => preset.Name == "学术");
        Assert.Contains(settings.OptimizationPresets, preset => preset.Name == "汇报");

        var clone = settings.Clone();
        clone.OptimizationPresets[0].Name = "已修改";
        Assert.NotEqual("已修改", settings.OptimizationPresets[0].Name);
    }

    [Fact]
    public void NormalizeDisplaySettings_ClampsUnsafeSizesAndOpacity()
    {
        var settings = new AppSettings
        {
            EditorFontSize = 100,
            UiScale = 0.1,
            EditorDefaultHeight = 20,
            WindowOpacity = 0.2,
            FloatingBallOpacity = 2,
            FloatingBallSize = 500
        };

        settings.NormalizeDisplaySettings();

        Assert.Equal(24, settings.EditorFontSize);
        Assert.Equal(0.8, settings.UiScale);
        Assert.Equal(28, settings.EditorDefaultHeight);
        Assert.Equal(0.65, settings.WindowOpacity);
        Assert.Equal(1, settings.FloatingBallOpacity);
        Assert.Equal(96, settings.FloatingBallSize);
    }

    [Fact]
    public void NormalizeDisplaySettings_PreservesUserSelectedFloatingBallSizeWithinBounds()
    {
        var settings = new AppSettings { FloatingBallSize = 60 };

        settings.NormalizeDisplaySettings();

        Assert.Equal(60, settings.FloatingBallSize);
    }
    [Fact]
    public void NormalizeProductModes_EmptySelection_EnablesBothTasksAndKeepsValidDefault()
    {
        var settings = new AppSettings
        {
            EnabledModes = [],
            DefaultMode = ApplicationMode.PromptOptimize
        };

        settings.NormalizeProductModes();

        Assert.Equal([ApplicationMode.Polish, ApplicationMode.PromptOptimize], settings.EnabledModes);
        Assert.Equal(ApplicationMode.PromptOptimize, settings.DefaultMode);
    }

    [Fact]
    public void NormalizeProductModes_LegacySingleMode_KeepsTheConfiguredDefaultReachable()
    {
        var settings = new AppSettings
        {
            EnabledModes = [ApplicationMode.PromptOptimize],
            DefaultMode = ApplicationMode.Polish
        };

        settings.NormalizeProductModes();

        Assert.Equal(ApplicationMode.Polish, settings.DefaultMode);
    }

    [Fact]
    public void NewSettings_DefaultToPrivatePolishWorkflow()
    {
        var settings = new AppSettings();

        Assert.Equal(ApplicationMode.Polish, settings.DefaultMode);
        Assert.Contains(ApplicationMode.Polish, settings.EnabledModes);
        Assert.Contains(ApplicationMode.PromptOptimize, settings.EnabledModes);
        Assert.False(settings.ClipboardAutoRead);
        Assert.True(settings.AutoArchive);
        Assert.True(settings.ClarificationEnabled);
        Assert.Equal("自然", settings.OutputStyle);
        Assert.Equal("System", settings.ThemeMode);
    }

    [Fact]
    public void NormalizeResidentEntrypoints_BothDisabled_ReenablesTray()
    {
        var settings = new AppSettings { TrayEnabled = false, FloatingBallEnabled = false };

        settings.NormalizeResidentEntrypoints();

        Assert.True(settings.TrayEnabled);
        Assert.False(settings.FloatingBallEnabled);
    }
}
