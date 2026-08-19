using System.IO;
using Xunit;

namespace PromptFloat.Tests;

public sealed class SettingsProductUiTests
{
    [Fact]
    public void MainEditor_ExplainsInlineContextSyntaxWithoutContextPanel()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "MainWindow.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(RepoRoot(), "ViewModels", "MainViewModel.cs"));

        Assert.DoesNotContain("x:Name=\"ContextPopup\"", xaml);
        Assert.DoesNotContain("Header=\"优化上下文\"", xaml);
        Assert.Contains("【上下文】", viewModel);
        Assert.Contains("【/上下文】", viewModel);
    }

    [Fact]
    public void SettingsView_UsesProductLanguageAndCapturedHotkeys()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "SettingsView.xaml"));

        Assert.DoesNotContain(">Dark<", xaml);
        Assert.DoesNotContain(">WarmOrange<", xaml);
        Assert.Contains("Text=\"{Binding Hotkey, UpdateSourceTrigger=PropertyChanged}\" IsReadOnly=\"True\"", xaml);
        Assert.Contains("PreviewKeyDown=\"HotkeyRecorder_OnPreviewKeyDown\"", xaml);
        Assert.Contains("恢复全部默认", xaml);
    }

    [Fact]
    public void SettingsView_UsesSlidersAndContextualHistoryActions()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "SettingsView.xaml"));

        Assert.Contains("x:Name=\"EditorFontSizeSlider\"", xaml);
        Assert.Contains("x:Name=\"WindowOpacitySlider\"", xaml);
        Assert.Contains("IsEnabled=\"{Binding IsFloatingBallSettingsEnabled}\"", xaml);
        Assert.Contains("x:Name=\"ArchiveContextToolbar\"", xaml);
    }

    private static string RepoRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "Huaxiazi.sln")))
            directory = Path.GetDirectoryName(directory);
        return directory ?? throw new DirectoryNotFoundException("未找到解决方案根目录。");
    }
}
