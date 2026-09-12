using System;
using System.IO;
using Xunit;

namespace Huaxiazi.Tests;

/// <summary>甲方验收报告确认的两个真实缺陷的回归测试。</summary>
public sealed class AcceptanceDefectTests
{
    [Fact]
    public void MainWindow_UsesCompactChromeAndPrioritizesEditorSpace()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "MainWindow.xaml"));

        Assert.Contains("MinHeight=\"158\"", xaml);
        Assert.Contains("x:Name=\"CompanionHost\"", xaml);
        Assert.Contains("MinHeight=\"{DynamicResource SkinTitleBarHeight}\"", xaml);
        Assert.Contains("x:Name=\"TitleFeedback\"", xaml);
        Assert.Contains("Property=\"MinWidth\" Value=\"68\"", xaml);
        Assert.Contains("Padding=\"12,8,12,8\"", xaml);
        Assert.DoesNotContain("x:Name=\"StatusNotice\"", xaml);
        Assert.DoesNotContain("x:Name=\"QuickActionBar\"", xaml);
        Assert.DoesNotContain("MinHeight=\"114\"", xaml);
        Assert.DoesNotContain("MinHeight=\"40\"", xaml);
    }

    [Fact]
    public void MainWindow_FeedbackDoesNotConsumeEditorLayoutAndIconsAreVectorBased()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "MainWindow.xaml"));
        var icons = File.ReadAllText(Path.Combine(RepoRoot(), "Resources", "Icons", "AppIcons.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(RepoRoot(), "ViewModels", "MainViewModel.cs"));

        Assert.Contains("x:Name=\"TitleFeedback\"", xaml);
        Assert.Contains("ToolTip=\"{Binding OperationalNotice}\"", xaml);
        Assert.Contains("Data=\"{StaticResource IconRedo}\"", xaml);
        Assert.Contains("Data=\"{StaticResource IconBrandSpark}\"", xaml);
        Assert.Contains("x:Key=\"IconRedo\"", icons);
        Assert.DoesNotContain("Content=\"↷\"", xaml);
        Assert.DoesNotContain("✦", viewModel);
    }

    [Fact]
    public void FloatingBall_ProvidesRoomForTheCompanionShadow()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "FloatingBallWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "FloatingBallWindow.xaml.cs"));

        Assert.Contains("Width=\"60\" Height=\"60\"", xaml);
        Assert.Contains("Width=\"{DynamicResource SkinCompanionSize}\" Height=\"{DynamicResource SkinCompanionSize}\"", xaml);
        Assert.Contains("Math.Clamp(settings.FloatingBallSize, 28, 72)", code);
        Assert.Contains("Width = size + 16", code);
        Assert.Contains("Height = size + 16", code);
    }

    [Fact]
    public void DeliveryScripts_UseSingleOutDirectoryContract()
    {
        var publish = File.ReadAllText(Path.Combine(RepoRoot(), "publish.ps1"));
        var build = File.ReadAllText(Path.Combine(RepoRoot(), "build.ps1"));
        var ignore = File.ReadAllText(Path.Combine(RepoRoot(), ".gitignore"));

        Assert.Contains("out/publish/win-x64", publish);
        Assert.Contains("Join-Path $ScriptDir \"release\"", publish);
        Assert.Contains("out/reports", publish);
        Assert.Contains("out\\publish\\win-x64", build);
        Assert.Contains("out/", ignore);
        Assert.True(File.Exists(Path.Combine(RepoRoot(), "Directory.Build.props")));
        Assert.DoesNotContain("Join-Path $ScriptDir \"artifacts\"", publish);
        Assert.DoesNotContain("Join-Path $ScriptDir \"dist\"", build);
    }

    [Fact]
    public void DeliveryScript_ProducesOnlyThreeStableClientArtifacts()
    {
        var publish = File.ReadAllText(Path.Combine(RepoRoot(), "publish.ps1"));

        Assert.Contains("PublishSingleFile=true", publish);
        Assert.Contains("IncludeNativeLibrariesForSelfExtract=true", publish);
        Assert.Contains("IncludeAllContentForSelfExtract=true", publish);
        Assert.Contains("Join-Path $PackagesDir \"Huaxiazi.exe\"", publish);
        Assert.Contains("Join-Path $PackagesDir \"Huaxiazi-Portable.zip\"", publish);
        Assert.Contains("Join-Path $PackagesDir \"Huaxiazi-Setup.exe\"", publish);
        Assert.DoesNotContain("release/version.json", publish);
    }

    [Fact]
    public void SettingsView_DeclaresEveryLocallyReferencedConverterResource()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "SettingsView.xaml"));

        Assert.Contains("x:Key=\"SectionVisibility\"", xaml);
        Assert.Contains("x:Key=\"InverseBool\"", xaml);
    }

    [Fact]
    public void ActiveSettingsView_DeclaresArchiveBindingsAndDestructiveConfirmationHandler()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "SettingsView.xaml"));

        Assert.Contains("Command=\"{Binding LoadArchiveCommand}\"", xaml);
        Assert.Contains("ItemsSource=\"{Binding ArchiveItems}\"", xaml);
        Assert.Contains("SelectedItem=\"{Binding SelectedArchiveItem}\"", xaml);
        Assert.Contains("Click=\"PermanentlyClearArchiveButton_OnClick\"", xaml);
    }

    private static string RepoRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "Huaxiazi.sln")))
            directory = Path.GetDirectoryName(directory);

        return directory ?? throw new InvalidOperationException("未找到解决方案根目录。");
    }

    [Fact]
    public void MainWindow_WiresResultEditSaveAndUndoArchiveCommands()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "MainWindow.xaml"));

        Assert.Contains("Command=\"{Binding BeginEditResultCommand}\"", xaml);
        Assert.Contains("Command=\"{Binding SaveEditedResultCommand}\"", xaml);
        Assert.Contains("Command=\"{Binding UndoArchiveCommand}\"", xaml);
    }

    [Fact]
    public void ThemeService_BrandDarkAndHoverNotHardcodedOrange()
    {
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "Services", "ThemeService.cs"));

        Assert.DoesNotContain("0xE9, 0x54, 0x3B", code);
        Assert.DoesNotContain("0xFF, 0x74, 0x58", code);
    }
}
