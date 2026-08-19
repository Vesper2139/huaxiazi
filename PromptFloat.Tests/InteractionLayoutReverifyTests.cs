using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using PromptFloat.Models;
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

            var scale = Assert.IsType<ScaleTransform>(face.FindName("BodyScale"));
            var blink = Assert.IsType<ScaleTransform>(face.FindName("BlinkScale"));
            Assert.True(scale.HasAnimatedProperties);
            Assert.True(blink.HasAnimatedProperties);
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

        Assert.Contains("Width=\"600\" Height=\"210\"", xaml);
        Assert.Contains("x:Name=\"VesperWordmark\"", xaml);
        Assert.Contains("x:Name=\"CompanionHost\"", xaml);
        Assert.Contains("Text=\"VES\"", xaml);
        Assert.Contains("Text=\"PER\"", xaml);
        Assert.DoesNotContain("NavigationBrandIcon", xaml);
        Assert.DoesNotContain("TEXT COMPANION", xaml);
        Assert.Contains("<Border Width=\"34\" Height=\"30\"", xaml);
        Assert.Contains("<Viewbox Width=\"34\" Height=\"34\"", xaml);
        Assert.DoesNotContain("<Border Width=\"44\" Height=\"44\" Background=\"Transparent\" />", xaml);
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
        var defaults = File.ReadAllText(Path.Combine(RepoRoot(), "Config", "default-config.json"));

        Assert.Contains("Data=\"{StaticResource IconResult}\"", xaml);
        Assert.Contains("Data=\"{StaticResource IconPlus}\"", xaml);
        Assert.Contains("Data=\"{StaticResource IconEye}\"", xaml);
        Assert.DoesNotContain("Text=\"✓\"", xaml);
        Assert.DoesNotContain("Text=\"＋\"", xaml);
        Assert.Null(typeof(AppSettings).GetProperty("AccentTheme"));
        Assert.Null(typeof(PromptFloat.ViewModels.SettingsViewModel).GetProperty("AccentTheme"));
        Assert.DoesNotContain("accentTheme", defaults, StringComparison.OrdinalIgnoreCase);
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
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
