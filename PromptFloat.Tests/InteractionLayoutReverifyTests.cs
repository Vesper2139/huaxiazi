using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using PromptFloat.Models;
using PromptFloat.Services;
using PromptFloat.Views;
using Xunit;

namespace PromptFloat.Tests;

public sealed class InteractionLayoutReverifyTests
{
    [Fact]
    public void Companion_UsesIndependentGazeAndExpressionRotations()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "CompanionFace.xaml"));
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "CompanionFace.xaml.cs"));

        Assert.Contains("x:Name=\"GazeRotation\"", xaml);
        Assert.Contains("x:Name=\"ExpressionRotation\"", xaml);
        Assert.DoesNotContain("BodyRotation", xaml);
        Assert.DoesNotContain("BodyRotation", code);
    }

    [Theory]
    [InlineData(220, 0, 8, 0, 8)]
    [InlineData(-220, 140, -8, 4.375, -8)]
    [InlineData(500, 0, 2.4, 0, 2.4)]
    [InlineData(560, 0, 0, 0, 0)]
    public void Companion_CalculatesVisibleBoundedGaze(
        double dx, double dy, double expectedX, double expectedY, double expectedRotation)
    {
        var method = typeof(CompanionFace).GetMethod(
            "CalculateGazeTarget",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        Assert.NotNull(method);
        var target = method.Invoke(null, [dx, dy]);
        Assert.NotNull(target);
        var targetType = target.GetType();

        Assert.Equal(expectedX, Assert.IsType<double>(targetType.GetProperty("X")?.GetValue(target)), 0);
        Assert.Equal(expectedY, Assert.IsType<double>(targetType.GetProperty("Y")?.GetValue(target)), 0);
        Assert.Equal(expectedRotation, Assert.IsType<double>(targetType.GetProperty("Rotation")?.GetValue(target)), 0);
    }

    [Fact]
    public void Companion_GazeFadesSmoothlyInsteadOfSnappingAtTheOldDistanceBoundary()
    {
        var near = InvokeGaze(419, 0);
        var beyond = InvokeGaze(421, 0);

        Assert.InRange(Math.Abs(near.X - beyond.X), 0, 0.2);
        Assert.True(beyond.X > 0);
    }

    [Fact]
    public void Companion_ClickFeedbackProducesAVisibleAnimation()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            App.ReplaceSettings(new AppSettings { AnimationsEnabled = true, IncognitoMode = true });
            var face = new CompanionFace();

            var method = typeof(CompanionFace).GetMethod("PlayClickFeedback");
            Assert.NotNull(method);
            method.Invoke(face, null);

            var controllerField = typeof(CompanionFace).GetField("_poseController", BindingFlags.Instance | BindingFlags.NonPublic);
            var controller = Assert.IsType<CompanionPoseController>(controllerField?.GetValue(face));
            Assert.True(controller.HasActiveAction);

            var pose = controller.Advance(TimeSpan.FromMilliseconds(16), animationsEnabled: true, reduceMotion: false);
            Assert.True(pose.ScaleX > 1);
            Assert.True(pose.ScaleY > 1);
        });
    }

    [Fact]
    public void DiffPreview_DebouncesTextChangesInsteadOfRecomputingEveryKeystroke()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            App.ReplaceSettings(new AppSettings { AnimationsEnabled = false, IncognitoMode = true });
            var window = new MainWindow();
            var viewModelField = typeof(MainWindow).GetField("_vm", BindingFlags.Instance | BindingFlags.NonPublic);
            var timerField = typeof(MainWindow).GetField("_diffRefreshTimer", BindingFlags.Instance | BindingFlags.NonPublic);
            var viewModel = Assert.IsType<PromptFloat.ViewModels.MainViewModel>(viewModelField?.GetValue(window));

            viewModel.ViewMode = ViewMode.Optimized;
            viewModel.ShowDiff = true;
            viewModel.UserInput = "正在输入";

            var timer = Assert.IsType<System.Windows.Threading.DispatcherTimer>(timerField?.GetValue(window));
            Assert.True(timer.IsEnabled);
            window.Close();
        });
    }

    [Fact]
    public void Companion_IsTheExplicitDragAndClickHandle()
    {
        var face = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "CompanionFace.xaml"));
        var main = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "MainWindow.xaml"));
        var mainCode = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "MainWindow.xaml.cs"));
        var floatingCode = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "FloatingBallWindow.xaml.cs"));

        Assert.Contains("IsHitTestVisible=\"True\"", face);
        Assert.Contains("x:Name=\"CompanionDragHandle\"", main);
        Assert.Contains("MouseLeftButtonDown=\"CompanionDragHandle_OnMouseLeftButtonDown\"", main);
        Assert.Contains("CompanionHost.PlayClickFeedback", mainCode);
        Assert.Contains("CompanionFace.PlayExpandFeedbackThen", floatingCode);
        Assert.DoesNotContain("OnMouseLeftButtonUp", floatingCode);
    }

    [Fact]
    public void MainWindow_EmbedsTheFloatingCompanionWithoutARedundantSubtitle()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "MainWindow.xaml"));

        Assert.Contains("Width=\"520\" Height=\"176\"", xaml);
        Assert.Contains("x:Name=\"VesperWordmark\"", xaml);
        Assert.Contains("x:Name=\"CompanionHost\"", xaml);
        Assert.Contains("Text=\"VES\"", xaml);
        Assert.Contains("Text=\"PER\"", xaml);
        Assert.DoesNotContain("NavigationBrandIcon", xaml);
        Assert.DoesNotContain("TEXT COMPANION", xaml);
        // 旧布局的 34×30 spacer 已移除
        Assert.DoesNotContain("<Border Width=\"34\" Height=\"30\"", xaml);
        // 精灵容器与 Viewbox 使用同一受约束度量；所有皮肤共享交互命中范式。
        Assert.Contains("<Viewbox Width=\"{DynamicResource SkinCompanionSize}\" Height=\"{DynamicResource SkinCompanionSize}\"", xaml);
        Assert.Contains("x:Name=\"CompanionDragHandle\"", xaml);
        Assert.Contains("Background=\"Transparent\"", xaml);
    }

    [Fact]
    public void CompanionExpressions_TransitionThroughABlinkInsteadOfHardCutting()
    {
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "CompanionFace.xaml.cs"));

        Assert.Contains("BeginExpressionTransition", code);
        Assert.Contains("close.Completed", code);
        Assert.Contains("CommitExpression", code);
    }

    [Fact]
    public void DefaultCompanion_UsesSharedFrameClockAndExposesSemanticOperationFeedback()
    {
        var face = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "CompanionFace.xaml.cs"));
        var clock = File.ReadAllText(Path.Combine(RepoRoot(), "Services", "CompanionFrameClock.cs"));

        Assert.Contains("CompanionPoseController", face);
        Assert.Contains("CompanionFrameClock.Subscribe", face);
        Assert.Contains("PlayOperationFeedback", face);
        Assert.Contains("CompositionTarget.Rendering", clock);
    }

    [Fact]
    public void MainWindow_RoutesDailyEditingOperationsToTheLocalCompanionDriver()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "MainWindow.xaml.cs"));

        Assert.Contains("OperationButton_OnClick", xaml);
        Assert.Contains("MainTextBox_OnTextChanged", xaml);
        var implementation = xaml + code;
        Assert.Contains("CompanionEventKind.Pasted", implementation);
        Assert.Contains("CompanionEventKind.Submitted", implementation);
        Assert.Contains("CompanionEventKind.DiffScanned", implementation);
        Assert.Contains("CompanionEventKind.Resized", implementation);
        Assert.Contains("CompanionEventKind.Failure", implementation);
    }

    [Fact]
    public void DragDirection_UsesIncrementalMovementRatherThanOnlyTheOriginalQuadrant()
    {
        var floating = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "FloatingBallWindow.xaml.cs"));
        var main = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "MainWindow.xaml.cs"));

        Assert.Contains("_lastDragPosition", floating);
        Assert.Contains("Left - _lastDragPosition.X", floating);
        Assert.Contains("_companionLastDragPosition", main);
        Assert.Contains("Left - _companionLastDragPosition.X", main);
    }

    [Fact]
    public void FloatingTransition_UsesTheBallCoordinatesAndASeparateWindowSurface()
    {
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "App.xaml.cs"));
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "MainWindow.xaml"));

        Assert.Contains("x:Name=\"WindowSurface\"", xaml);
        Assert.Contains("PlayRevealAnimation(ballCenter)", app);
        Assert.Contains("PlayCollapseAnimation(targetBallCenter", app);
    }

    [Fact]
    public void Settings_HasAClippedContentFrameAndVisibleThemeChoices()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "SettingsView.xaml"));

        Assert.Contains("x:Name=\"SettingsContentFrame\"", xaml);
        Assert.Contains("ClipToBounds=\"True\"", xaml);
        Assert.Contains("x:Name=\"ThemeChoiceList\"", xaml);
        Assert.Contains("<UniformGrid Columns=\"3\" />", xaml);
        Assert.Contains("SelectedValue=\"{Binding ThemeMode}\"", xaml);
        Assert.DoesNotContain("MaxWidth=\"720\"", xaml);
        Assert.DoesNotContain("Margin=\"16,8,4,8\"", xaml);
    }

    [Fact]
    public void Settings_UsesVectorControlsAndHasNoDeadAccentSetting()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "SettingsView.xaml"));
        var ability = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "ExpressionAbilitySettingsView.xaml"));
        var providerEditor = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "ProviderProfileEditView.xaml"));
        var defaults = File.ReadAllText(Path.Combine(RepoRoot(), "Config", "default-config.json"));

        Assert.Contains("Data=\"{StaticResource IconResult}\"", xaml);
        Assert.Contains("Data=\"{StaticResource IconPlus}\"", ability);
        Assert.Contains("Data=\"{StaticResource IconEye}\"", providerEditor);
        Assert.DoesNotContain("Text=\"✓\"", xaml);
        Assert.DoesNotContain("Text=\"＋\"", xaml);
        Assert.Null(typeof(AppSettings).GetProperty("AccentTheme"));
        Assert.Null(typeof(PromptFloat.ViewModels.SettingsViewModel).GetProperty("AccentTheme"));
        Assert.DoesNotContain("accentTheme", defaults, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Settings_ExplainsTheTokenCostOfEmotionAssistantMode()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "SettingsView.xaml"));
        var defaults = File.ReadAllText(Path.Combine(RepoRoot(), "Config", "default-config.json"));

        Assert.Contains("CompanionDriverMode", xaml);
        Assert.Contains("额外请求", xaml);
        Assert.Contains("略微增加用量", xaml);
        Assert.Contains("\"companionDriverMode\": \"Local\"", defaults);
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
            finally { TestHelpers.ResetWpfApplication(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    private static (double X, double Y, double Rotation) InvokeGaze(double dx, double dy)
    {
        var method = typeof(CompanionFace).GetMethod(
            "CalculateGazeTarget",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!;
        var target = method.Invoke(null, [dx, dy])!;
        var type = target.GetType();
        return (
            Assert.IsType<double>(type.GetProperty("X")!.GetValue(target)),
            Assert.IsType<double>(type.GetProperty("Y")!.GetValue(target)),
            Assert.IsType<double>(type.GetProperty("Rotation")!.GetValue(target)));
    }

    private static void EnsureApplicationResources()
    {
        if (Application.Current is { } existing && !ReferenceEquals(existing.Dispatcher, System.Windows.Threading.Dispatcher.CurrentDispatcher))
            TestHelpers.ResetWpfApplication();

        var application = Application.Current ?? new Application();
        application.Resources.MergedDictionaries.Clear();
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Huaxiazi;component/Resources/Themes/Dark.xaml", UriKind.Relative)
        });
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Huaxiazi;component/Resources/Styles/GlobalStyles.xaml", UriKind.Relative)
        });
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Huaxiazi;component/Resources/Icons/AppIcons.xaml", UriKind.Relative)
        });
    }

    private static string RepoRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "Huaxiazi.sln")))
            directory = Path.GetDirectoryName(directory);
        return directory ?? throw new InvalidOperationException("未找到解决方案根目录。");
    }
}
