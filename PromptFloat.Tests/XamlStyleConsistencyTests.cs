using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace PromptFloat.Tests;

/// <summary>
/// 轻量 XAML 一致性守护（测试工程刻意不启用 WPF，因此用静态扫描）：
/// 防止「TextBox 误用 PasswordBox 样式」这类只有在运行时打开窗口才会触发的
/// XamlParseException 回归。任何把 GlassPasswordBox 挂到 TextBox 上的改动都会在此失败。
/// </summary>
public class XamlStyleConsistencyTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Huaxiazi.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new InvalidOperationException("未找到仓库根目录（Huaxiazi.sln）");
    }

    [Fact]
    public void ApiKeyTextBox_PlainTextField_UsesTextBoxTargetedStyle()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "ProviderProfileEditView.xaml"));
        Assert.Contains("x:Name=\"ApiKeyTextBox\" Style=\"{StaticResource CompactSettingsField}\"", xaml);
    }

    [Fact]
    public void SettingsView_UsesFriendlyPlatformSelectionAndHidesProtocolConcept()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "ProviderProfileEditView.xaml"));

        Assert.Contains("ItemsSource=\"{Binding ProviderPlatformView}\"", xaml);
        Assert.Contains("SelectedItem=\"{Binding SelectedProviderPlatform, Mode=TwoWay}\"", xaml);
        Assert.DoesNotContain("Text=\"协议\"", xaml);
    }

    [Fact]
    public void SettingsView_ProfileSelectionSynchronizesProtectedApiKeyField()
    {
        var root = RepoRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "Views", "ProviderProfileEditView.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "Views", "ProviderProfileEditView.xaml.cs"));

        Assert.Contains("x:Name=\"ApiKeyBox\"", xaml);
        Assert.Contains("SynchronizeApiKeyEditors", codeBehind);
        Assert.DoesNotContain("App.SecretStore.Read", codeBehind);
    }

    [Fact]
    public void GlassPlainTextBox_Style_TargetsTextBox()
    {
        var styles = File.ReadAllText(Path.Combine(RepoRoot(), "Resources", "Styles", "GlobalStyles.xaml"));
        Assert.Matches(new Regex(@"x:Key=""GlassPlainTextBox""\s+TargetType=""TextBox"""), styles);
    }

    [Fact]
    public void NoTextBox_ReferencesPasswordBoxStyle()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(RepoRoot(), "Views"), "*.xaml"))
        {
            var xaml = File.ReadAllText(file);
            Assert.DoesNotMatch(
                new Regex(@"<TextBox\b[^>]*Style=""\{StaticResource GlassPasswordBox\}"""),
                xaml);
        }
    }

    [Fact]
    public void SettingsView_UsesModuleNavigationAndSubtleScrolling()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "SettingsView.xaml"));

        Assert.Contains("ItemsSource=\"{Binding Sections}\"", xaml);
        Assert.Contains("Style=\"{StaticResource OverlayScrollViewer}\"", xaml);
        Assert.Contains("Style=\"{StaticResource GlassPanel}\"", xaml);
    }

    [Fact]
    public void TextInputStyles_ProvideVerticalBreathingRoom()
    {
        var styles = File.ReadAllText(Path.Combine(RepoRoot(), "Resources", "Styles", "GlobalStyles.xaml"));

        Assert.Contains("<Setter Property=\"Padding\" Value=\"12,8\"", styles);
    }

    [Fact]
    public void SettingsHost_UsesUnifiedFramelessWindowChrome()
    {
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "MainWindow.xaml.cs"));

        Assert.Contains("WindowStyle = WindowStyle.None", code);
        Assert.Contains("WindowChrome.SetWindowChrome", code);
    }

    [Fact]
    public void MainWindow_ActionsShareOneCompactEditorDock()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "MainWindow.xaml"));

        Assert.Contains("x:Name=\"EditorDockBottom\"", xaml);
        Assert.Contains("x:Name=\"EditorPrimaryAction\"", xaml);
        Assert.Contains("x:Name=\"HistoryButton\"", xaml);
        Assert.DoesNotContain("x:Name=\"QuickActionBar\"", xaml);
    }

    [Fact]
    public void MainWindow_UsesCompactOuterInsets()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "MainWindow.xaml"));

        Assert.Contains("Margin=\"4,3,4,4\"", xaml);
    }

    [Fact]
    public void MainWindow_UsesReferenceWideCompactProportions()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "MainWindow.xaml"));

        Assert.Contains("Width=\"520\" Height=\"176\"", xaml);
        Assert.Contains("MinWidth=\"420\" MinHeight=\"158\"", xaml);
    }

    [Fact]
    public void SurfaceTokens_LiveInSeparateCompleteSkinDictionaries()
    {
        var styles = File.ReadAllText(Path.Combine(RepoRoot(), "Resources", "Styles", "GlobalStyles.xaml"));
        var light = File.ReadAllText(Path.Combine(RepoRoot(), "Resources", "Themes", "Light.xaml"));
        var dark = File.ReadAllText(Path.Combine(RepoRoot(), "Resources", "Themes", "Dark.xaml"));

        Assert.DoesNotContain("<Color x:Key=\"GlassTopColor\">", styles);
        Assert.Contains("<Color x:Key=\"GlassTopColor\">", light);
        Assert.Contains("<Color x:Key=\"GlassTopColor\">", dark);
        Assert.Contains("<CornerRadius x:Key=\"WindowRadius\">10</CornerRadius>", light);
        Assert.Contains("<CornerRadius x:Key=\"WindowRadius\">14</CornerRadius>", dark);
    }

    [Fact]
    public void SettingsView_UsesLabelFieldGridLikeReference()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "ProviderProfileEditView.xaml"));

        Assert.Contains("x:Name=\"ProviderNameTextBox\"", xaml);
        Assert.Contains("x:Name=\"ProviderRemarkTextBox\"", xaml);
        Assert.Contains("<ColumnDefinition Width=\"10\"", xaml);
    }

    [Fact]
    public void MainWindow_PlaceholderUsesEditorContentInset()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "MainWindow.xaml"));
        var styles = File.ReadAllText(Path.Combine(RepoRoot(), "Resources", "Styles", "GlobalStyles.xaml"));

        Assert.Contains("Padding=\"12,8,12,8\"", xaml);
        Assert.Contains("x:Name=\"Watermark\"", styles);
        Assert.Contains("Margin=\"{TemplateBinding Padding}\"", styles);
    }

    [Fact]
    public void MainWindow_BottomDockUsesCompactControlHeight()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "MainWindow.xaml"));

        Assert.Contains("x:Name=\"EditorDockBottom\" Grid.Row=\"1\" MinHeight=\"34\" Height=\"34\"", xaml);
        Assert.Contains("VerticalAlignment=\"Center\"", xaml);
    }

    [Fact]
    public void MainWindow_UsesWindowChromeResizeBorder()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "MainWindow.xaml"));

        Assert.Contains("<shell:WindowChrome.WindowChrome>", xaml);
        Assert.Contains("ResizeBorderThickness=\"6\"", xaml);
        Assert.Contains("<ResizeGrip", xaml);
    }

    [Fact]
    public void App_SupportsVisibleMainWindowForUiVerification()
    {
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "App.xaml.cs"));

        Assert.Contains("--show-main", code);
    }

    [Fact]
    public void MainWindow_UsesStableShellAndSharedSkinControlMetrics()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "MainWindow.xaml"));
        var styles = File.ReadAllText(Path.Combine(RepoRoot(), "Resources", "Styles", "GlobalStyles.xaml"));

        Assert.Contains("<RowDefinition Height=\"Auto\" />", xaml);
        Assert.Contains("<RowDefinition Height=\"*\"", xaml);
        Assert.Contains("x:Name=\"EditorDockBottom\"", xaml);
        Assert.Contains("x:Name=\"TitleBand\" Grid.Row=\"0\" MinHeight=\"{DynamicResource SkinTitleBarHeight}\"", xaml);
        Assert.Contains("Property=\"MinHeight\" Value=\"32\"", styles);
        Assert.Contains("Property=\"MinHeight\" Value=\"36\"", styles);
    }

    [Fact]
    public void MainWindow_HasPinAndCloseInTitleBarAndCompanionForCollapse()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "MainWindow.xaml.cs"));

        Assert.Contains("x:Name=\"PinButton\"", xaml);
        Assert.Contains("CloseButton_OnClick", code);
        Assert.DoesNotContain("SettingsButton", xaml);
        Assert.DoesNotContain("CollapseToBallButton", xaml);
        Assert.Contains("CompanionDragHandle_OnMouseLeftButtonDown", code);
        Assert.Contains("e.ClickCount >= 2", code);
    }

    [Fact]
    public void DesignSystem_UsesFlatSurfacesAndOverlayScrollViewer()
    {
        var styles = File.ReadAllText(Path.Combine(RepoRoot(), "Resources", "Styles", "GlobalStyles.xaml"));

        Assert.Contains("x:Key=\"OverlayScrollViewer\"", styles);
        Assert.Contains("x:Key=\"OverlayScrollBar\"", styles);
        Assert.DoesNotContain("DropShadowEffect", styles);
        Assert.DoesNotContain("GlowOrangeBrush", styles);
        Assert.DoesNotContain("IceGlowBrush", styles);
    }

    [Fact]
    public void FloatingBall_HasNoDecorativeHalo()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "FloatingBallWindow.xaml"));

        Assert.DoesNotContain("GlowHalo", xaml);
        Assert.DoesNotContain("HaloStop", xaml);
        Assert.DoesNotContain("RadialGradientBrush", xaml);
    }

    [Fact]
    public void SettingsView_ExposesArchiveAndUpdateActionsFromLiveNavigation()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "SettingsView.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(RepoRoot(), "ViewModels", "SettingsViewModel.cs"));

        Assert.Contains("ExportArchiveCommand", xaml);
        Assert.Contains("SoftDeleteArchiveCommand", xaml);
        Assert.Contains("RestoreArchiveCommand", xaml);
        Assert.Contains("Click=\"PermanentlyClearArchiveButton_OnClick\"", xaml);
        Assert.Contains("CheckUpdateButton_OnClick", xaml);
        Assert.Contains("private void ExportArchive", viewModel);
        Assert.Contains("private void PermanentlyClearArchive", viewModel);
    }

    [Fact]
    public void SettingsView_UsesProfessionalExpressionModulesAndProviderActions()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "SettingsView.xaml"));
        var list = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "ProviderProfileListView.xaml"));
        var editor = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "ProviderProfileEditView.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(RepoRoot(), "ViewModels", "SettingsViewModel.cs"));

        foreach (var section in new[] { "模型与 API", "表达能力", "历史与会话", "界面与显示", "窗口与行为", "快捷键", "数据管理", "关于与更新" })
        {
            Assert.Contains(section, viewModel);
        }
        Assert.Contains("Style=\"{StaticResource OverlayScrollViewer}\"", xaml);
        Assert.Contains("Command=\"{Binding AddProviderCommand}\"", list);
        Assert.Contains("Command=\"{Binding DuplicateProviderCommand}\"", list);
        Assert.Contains("Click=\"DeleteSelectedProfile_OnClick\"", list);
        Assert.Contains("Command=\"{Binding TestConnectionCommand}\"", editor);
        Assert.Contains("x:Name=\"ApiKeyBox\"", editor);
        Assert.Contains("x:Name=\"ApiKeyTextBox\"", editor);
    }
}
