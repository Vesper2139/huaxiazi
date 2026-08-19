using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PromptFloat.Models;
using PromptFloat.ViewModels;
using PromptFloat.Views;
using Xunit;

namespace PromptFloat.Tests;

public sealed class CompanionUiContractTests
{
    [Fact]
    public void Settings_ExposeOnlySystemLightAndDarkAppearanceModes()
    {
        var viewModel = new SettingsViewModel();

        Assert.Collection(
            viewModel.ThemeModes,
            option => Assert.Equal("System", option.Value),
            option => Assert.Equal("Light", option.Value),
            option => Assert.Equal("Dark", option.Value));
    }

    [Fact]
    public void FloatingBall_UsesTheCompanionFaceAsItsPrimaryVisual()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            App.ReplaceSettings(new AppSettings { IncognitoMode = true });
            var window = new FloatingBallWindow();

            Assert.NotNull(window.FindName("CompanionFace"));
            Assert.Equal(60, window.Width);
            Assert.Equal(60, window.Height);
            var orb = Assert.IsType<Border>(window.FindName("Orb"));
            Assert.Equal(44, orb.Width);
            Assert.Equal(44, orb.Height);
            window.Close();
        });
    }

    [Fact]
    public void CompanionFace_UsesOneLightweightStrokeSystemForEyesAndMouth()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            App.ReplaceSettings(new AppSettings { AnimationsEnabled = false, IncognitoMode = true });
            var face = new CompanionFace();
            var leftEye = Assert.IsType<System.Windows.Shapes.Path>(face.FindName("LeftEye"));
            var rightEye = Assert.IsType<System.Windows.Shapes.Path>(face.FindName("RightEye"));
            var mouth = Assert.IsType<System.Windows.Shapes.Path>(face.FindName("Mouth"));

            Assert.Equal(leftEye.StrokeThickness, rightEye.StrokeThickness);
            Assert.Equal(leftEye.StrokeThickness, mouth.StrokeThickness);
            Assert.InRange(leftEye.StrokeThickness, 1.8, 2.4);
        });
    }

    [Fact]
    public void CompanionFace_ExposesDedicatedDragAndExpandInteractionAnimations()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            App.ReplaceSettings(new AppSettings { AnimationsEnabled = false, IncognitoMode = true });
            var face = new CompanionFace();

            face.PlayDragStartFeedback();
            Assert.Equal(CompanionVisualState.Dragging, face.State);
            face.PlayDragEndFeedback(CompanionVisualState.Curious);
            Assert.Equal(CompanionVisualState.Curious, face.State);

            var completed = false;
            face.PlayExpandFeedbackThen(() => completed = true);
            Assert.True(completed);
            Assert.Equal(CompanionVisualState.Expanding, face.State);
        });
    }

    [Fact]
    public void DragExpression_IsVisibleBeforeWindowDragEntersItsBlockingMoveLoop()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            App.ReplaceSettings(new AppSettings { AnimationsEnabled = true, IncognitoMode = true });
            var face = new CompanionFace();
            var mouth = Assert.IsType<System.Windows.Shapes.Path>(face.FindName("Mouth"));
            var idleGeometry = mouth.Data.ToString(System.Globalization.CultureInfo.InvariantCulture);

            face.PlayDragStartFeedback();

            Assert.Equal(CompanionVisualState.Dragging, face.State);
            Assert.NotEqual(idleGeometry, mouth.Data.ToString(System.Globalization.CultureInfo.InvariantCulture));
        });
    }

    [Fact]
    public void MainWindow_SurfacesCompanionAndOperationalNotice()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            App.ReplaceSettings(new AppSettings { IncognitoMode = true });
            var window = new MainWindow();
            var viewModel = Assert.IsType<MainViewModel>(window.DataContext);
            viewModel.UserInput = string.Empty;
            viewModel.OptimizeCommand.Execute(null);

            Assert.NotNull(window.FindName("CompanionHost"));
            var notice = Assert.IsType<Border>(window.FindName("FeedbackOverlay"));
            Assert.Equal(Visibility.Visible, notice.Visibility);
            window.Close();
        });
    }

    [Fact]
    public void MainWindow_UsesReferenceWorkspaceHierarchyWithoutInventingFeatures()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            App.ReplaceSettings(new AppSettings { IncognitoMode = true });
            var window = new MainWindow();

            Assert.Equal(600, window.Width);
            Assert.Equal(210, window.Height);
            Assert.Null(window.FindName("QuickActionBar"));
            Assert.NotNull(window.FindName("SettingsButton"));
            var provider = Assert.IsType<ComboBox>(window.FindName("ProviderSelector"));
            var editorDock = Assert.IsType<Border>(window.FindName("EditorDockBottom"));
            Assert.False(IsDescendantOf(provider, editorDock));
            window.Close();
        });
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void MainWindow_RevealMotionRespectsTheAnimationPreference(bool animationsEnabled, bool expectsAnimation)
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            App.ReplaceSettings(new AppSettings { IncognitoMode = true, AnimationsEnabled = animationsEnabled });
            var window = new MainWindow();

            window.PlayRevealAnimation();

            var surface = Assert.IsType<Border>(window.FindName("WindowSurface"));
            Assert.Equal(expectsAnimation, surface.HasAnimatedProperties);
            var transform = Assert.IsType<TransformGroup>(surface.RenderTransform);
            var scale = Assert.IsType<ScaleTransform>(transform.Children[0]);
            Assert.Equal(expectsAnimation, scale.HasAnimatedProperties);
            if (!expectsAnimation)
            {
                Assert.Equal(1, surface.Opacity);
                Assert.Equal(1, scale.ScaleX);
                Assert.Equal(1, scale.ScaleY);
            }
            window.Close();
        });
    }

    [Fact]
    public void MainWindow_CollapseWithoutAnimationsCompletesSynchronously()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            App.ReplaceSettings(new AppSettings { IncognitoMode = true, AnimationsEnabled = false });
            var window = new MainWindow();
            var completed = false;

            window.PlayCollapseAnimation(() => completed = true);

            Assert.True(completed);
            var surface = Assert.IsType<Border>(window.FindName("WindowSurface"));
            Assert.False(surface.HasAnimatedProperties);
            window.Close();
        });
    }

    private static void RunSta(System.Action action)
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

    private static void EnsureApplicationResources()
    {
        var application = Application.Current ?? new Application();
        var resources = application.Resources;
        resources.MergedDictionaries.Clear();
        resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new System.Uri("/Huaxiazi;component/Resources/Themes/Dark.xaml", System.UriKind.Relative)
        });
        resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new System.Uri("/Huaxiazi;component/Resources/Styles/GlobalStyles.xaml", System.UriKind.Relative)
        });
        resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new System.Uri("/Huaxiazi;component/Resources/Icons/AppIcons.xaml", System.UriKind.Relative)
        });
    }

    private static bool IsDescendantOf(DependencyObject child, DependencyObject ancestor)
    {
        DependencyObject? current = child;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor)) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }
}
