using System.Threading;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Huaxiazi.Models;
using Huaxiazi.Services;
using Huaxiazi.ViewModels;
using Huaxiazi.Views;
using Xunit;

namespace Huaxiazi.Tests;

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
    public void CompanionFace_DefaultVectorFaceUsesSharedHeadAndLocalEyeTransforms()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            App.ReplaceSettings(new AppSettings { AnimationsEnabled = false, IncognitoMode = true });
            var face = new CompanionFace();

            var head = Assert.IsType<TranslateTransform>(face.FindName("HeadOffset"));
            var eye = Assert.IsType<TranslateTransform>(face.FindName("GazeOffset"));
            var mouth = Assert.IsType<TranslateTransform>(face.FindName("MouthPoseOffset"));
            typeof(CompanionFace).GetMethod("EaseGazeTo", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(face, new object?[] { 8d, 5d, 3d });

            Assert.True(head.X > 0 && head.Y > 0);
            Assert.True(eye.X > 0 && eye.Y > 0);
            Assert.True(mouth.X > 0 && mouth.Y > 0);
            Assert.True(Math.Abs(eye.Y - mouth.Y) < 2.6);
        });
    }

    [Fact]
    public void CompanionFace_DefaultVectorFaceRaisesEyesAboveTheMouth()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            App.ReplaceSettings(new AppSettings { AnimationsEnabled = false, IncognitoMode = true });
            var face = new CompanionFace();
            var eyes = Assert.IsAssignableFrom<FrameworkElement>(face.FindName("EyeLayer"));
            var offset = Assert.IsType<TranslateTransform>(face.FindName("EyeVisualOffset"));

            Assert.Equal(-1.5, offset.Y, 2);
            Assert.True(CompanionFaceKinematics.FeatureGap >= 8.5);
            Assert.Equal(Visibility.Visible, eyes.Visibility);
        });
    }

    [Fact]
    public void CompanionFace_UsesDifferentExpressionsForWorkingAndThinking()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            App.ReplaceSettings(new AppSettings { AnimationsEnabled = false, IncognitoMode = true });
            var face = new CompanionFace();
            var leftEye = Assert.IsType<Path>(face.FindName("LeftEye"));

            face.State = CompanionVisualState.Working;
            var workingEye = leftEye.Data.ToString();
            face.State = CompanionVisualState.Thinking;
            var thinkingEye = leftEye.Data.ToString();

            Assert.NotEqual(workingEye, thinkingEye);
        });
    }

    [Fact]
    public void FloatingBall_ForwardsWorkflowStateToItsCompanionFace()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            App.ReplaceSettings(new AppSettings { AnimationsEnabled = false, IncognitoMode = true });
            var window = new FloatingBallWindow();

            window.SetCompanionState(CompanionVisualState.Working);

            var face = Assert.IsType<CompanionFace>(window.FindName("CompanionFace"));
            Assert.Equal(CompanionVisualState.Working, face.State);
            var orb = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("Orb"));
            Assert.Contains("已开始处理", Assert.IsType<string>(orb.ToolTip));
            window.Close();
        });
    }

    [Fact]
    public void CompanionFace_SpecialSkinUsesTransparentSpriteSheetProfile()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            App.ReplaceSettings(new AppSettings { SkinId = "LuoXiaoHei", AnimationsEnabled = false, IncognitoMode = true });
            App.SkinService.ApplySkin("LuoXiaoHei", Application.Current.Resources);
            var face = new CompanionFace { State = CompanionVisualState.Dragging };

            var sprite = Assert.IsType<Rectangle>(face.FindName("SkinSpriteHost"));
            Assert.Equal(Visibility.Visible, sprite.Visibility);
            Assert.Equal(Visibility.Collapsed, face.FindName("FaceLayer") is FrameworkElement vector ? vector.Visibility : Visibility.Visible);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)face.FindName("CompanionSurface")!).Visibility);
        });
    }

    [Theory]
    [InlineData("MaoDie")]
    public void CompanionFace_BuiltInImageSkinChangesAssetForEveryInteractionState(string skinId)
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            App.ReplaceSettings(new AppSettings { SkinId = skinId, AnimationsEnabled = false, IncognitoMode = true });
            new Huaxiazi.Services.SkinService(Application.Current.Resources).ApplySkin(skinId);
            var face = new CompanionFace { State = CompanionVisualState.Idle };
            var brush = Assert.IsType<ImageBrush>(Assert.IsType<System.Windows.Shapes.Rectangle>(
                face.FindName("SkinSpriteHost")).Fill);
            var loadedUris = new HashSet<Uri>();
            foreach (var state in Enum.GetValues<CompanionVisualState>())
            {
                face.State = state;
                var image = Assert.IsType<System.Windows.Media.Imaging.BitmapImage>(brush.ImageSource);
                Assert.True(image.PixelWidth > 0);
                Assert.EndsWith("/sprite-sheet.png", image.UriSource.OriginalString, StringComparison.OrdinalIgnoreCase);
                loadedUris.Add(image.UriSource);
            }
            Assert.Single(loadedUris);
        });
    }

    [Fact]
    public void CompanionFace_ImageSkinAnimatesTheSpriteLayerBetweenExpressions()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            App.ReplaceSettings(new AppSettings { SkinId = "MaoDie", AnimationsEnabled = true, IncognitoMode = true });
            new Huaxiazi.Services.SkinService(Application.Current.Resources).ApplySkin("MaoDie");
            var face = new CompanionFace { State = CompanionVisualState.Idle };
            var sprite = Assert.IsType<System.Windows.Shapes.Rectangle>(face.FindName("SkinSpriteHost"));

            face.State = CompanionVisualState.Dragging;

            Assert.True(sprite.HasAnimatedProperties);
        });
    }

    [Fact]
    public void CompanionFace_RasterSpriteUsesOverscanToFillTheOrb()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            App.ReplaceSettings(new AppSettings { SkinId = "MaoDie", AnimationsEnabled = false, IncognitoMode = true });
            new Huaxiazi.Services.SkinService(Application.Current.Resources).ApplySkin("MaoDie");
            var face = new CompanionFace();
            var sprite = Assert.IsType<Rectangle>(face.FindName("SkinSpriteHost"));
            var transform = Assert.IsType<TransformGroup>(sprite.RenderTransform);
            var scale = Assert.IsType<ScaleTransform>(transform.Children[0]);
            Assert.Equal(1, scale.ScaleX);
            Assert.Equal(Stretch.UniformToFill, Assert.IsType<ImageBrush>(sprite.Fill).Stretch);
        });
    }

    [Fact]
    public void CompanionFace_RasterSpriteAnchorsArtworkCenterInsteadOfTransparentCanvasCenter()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            App.ReplaceSettings(new AppSettings { SkinId = "MaoDie", AnimationsEnabled = false, IncognitoMode = true });
            new Huaxiazi.Services.SkinService(Application.Current.Resources).ApplySkin("MaoDie");
            var face = new CompanionFace();
            var sprite = Assert.IsType<Rectangle>(face.FindName("SkinSpriteHost"));
            var group = Assert.IsType<TransformGroup>(sprite.RenderTransform);
            var offset = Assert.IsType<TranslateTransform>(group.Children[1]);
            Assert.Equal(0, offset.Y);
        });
    }

    [Fact]
    public void CompanionFace_ImageSkinHasAnIndependentGazeTransform()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            App.ReplaceSettings(new AppSettings { SkinId = "MaoDie", AnimationsEnabled = true, IncognitoMode = true });
            var face = new CompanionFace();
            var gaze = Assert.IsType<TranslateTransform>(face.FindName("SkinSpriteGaze"));
            Assert.Equal(0, gaze.X);
            Assert.Equal(0, gaze.Y);
            typeof(CompanionFace).GetMethod("EaseGazeTo", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(face, new object?[] { 6d, -4d, 5d });
            Assert.NotEqual(0, gaze.X);
            Assert.NotEqual(0, gaze.Y);
        });
    }

    [Fact]
    public void CompanionFace_LuoXiaoHeiUsesTransparentSpriteSheetInsteadOfVectorRenderer()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            App.ReplaceSettings(new AppSettings { SkinId = "LuoXiaoHei", AnimationsEnabled = false, IncognitoMode = true });
            App.SkinService.ApplySkin("LuoXiaoHei", Application.Current!.Resources);
            var face = new CompanionFace();
            var raster = Assert.IsType<Rectangle>(face.FindName("SkinSpriteHost"));
            Assert.Equal(Visibility.Visible, raster.Visibility);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)face.FindName("CompanionSurface")!).Visibility);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)face.FindName("VectorSpriteHost")!).Visibility);
            Assert.Equal(CompanionVisualState.Idle, face.State);
        });
    }

    [Fact]
    public void LuoXiaoHei_UsesTheRegeneratedSpriteSheetAsset()
    {
        EnsureApplicationResources();
        var manifest = App.SkinService.GetSkin("LuoXiaoHei");
        Assert.NotNull(manifest);
        Assert.EndsWith("sprite-sheet-v2.png", manifest!.CompanionSpriteSheetPath!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MainWindow_TitleBandUsesTheActiveSkinHeightContract()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            App.ReplaceSettings(new AppSettings { SkinId = "MaoDie", IncognitoMode = true });
            var window = new MainWindow();
            var titleBand = Assert.IsType<Grid>(window.FindName("TitleBand"));
            var titleHeight = (double)Application.Current!.Resources["SkinTitleBarHeight"];
            Assert.Equal(titleHeight, titleBand.MinHeight);
            Assert.Equal(34, titleBand.MinHeight);
            window.Close();
        });
    }

    [Fact]
    public void MainWindow_TitleBandSeparatesCompanionWithoutInflatingEditorInset()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            App.ReplaceSettings(new AppSettings { SkinId = "MaoDie", IncognitoMode = true });
            var window = new MainWindow();
            var titleBand = Assert.IsType<Grid>(window.FindName("TitleBand"));
            var editor = Assert.IsType<TextBox>(window.FindName("MainTextBox"));
            Assert.Equal(34, titleBand.MinHeight);
            Assert.Equal(new Thickness(12, 8, 12, 8), editor.Padding);
            window.Close();
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
            // 动画关闭时必须已退出 Expanding 中间态——否则"表情卡住"复现
            Assert.Equal(CompanionVisualState.Idle, face.State);
        });
    }

    [Fact]
    public void CompanionFace_DragDirectionKeepsSpriteOpaqueAndMapsEightWayMotion()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            App.ReplaceSettings(new AppSettings { SkinId = "MaoDie", AnimationsEnabled = true, IncognitoMode = true });
            new Huaxiazi.Services.SkinService(Application.Current.Resources).ApplySkin("MaoDie");
            var face = new CompanionFace();
            face.PlayDragStartFeedback();
            face.PlayDragDirection(48, -48);
            var sprite = Assert.IsType<Rectangle>(face.FindName("SkinSpriteHost"));
            Assert.Equal(1, sprite.Opacity);
            var scale = Assert.IsType<ScaleTransform>(face.FindName("BodyScale"));
            Assert.True(scale.ScaleX > 1 && scale.ScaleY > 1);
            var body = Assert.IsType<TranslateTransform>(face.FindName("BodyOffset"));
            Assert.True(body.X > 0 && body.Y < 0);
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
    public void CompanionFace_HoverFeedback_ShowsCuriousWhenIdle_AndRestoresOnEnd()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            App.ReplaceSettings(new AppSettings { AnimationsEnabled = false, IncognitoMode = true });
            var face = new CompanionFace();
            Assert.Equal(CompanionVisualState.Idle, face.State);

            face.PlayHoverFeedback();
            Assert.Equal(CompanionVisualState.Curious, face.State);

            face.PlayHoverEndFeedback();
            Assert.Equal(CompanionVisualState.Idle, face.State);
        });
    }

    [Fact]
    public void CompanionFace_MaoDieHoverRendersTheHissingFrame()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            App.ReplaceSettings(new AppSettings { SkinId = "MaoDie", AnimationsEnabled = false, IncognitoMode = true });
            App.SkinService.ApplySkin("MaoDie", Application.Current!.Resources);
            var face = new CompanionFace();

            face.PlayHoverFeedback();

            Assert.Equal(CompanionVisualState.Curious, face.State);
            var sprite = Assert.IsType<Rectangle>(face.FindName("SkinSpriteHost"));
            var brush = Assert.IsType<ImageBrush>(sprite.Fill);
            Assert.Equal(new Rect(0, 2d / 3d, 0.25, 1d / 3d), brush.Viewbox);
        });
    }

    [Fact]
    public void CompanionFace_HoverFeedback_DoesNotOverrideBusyState()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            App.ReplaceSettings(new AppSettings { AnimationsEnabled = false, IncognitoMode = true });
            var face = new CompanionFace { State = CompanionVisualState.Thinking };

            face.PlayHoverFeedback();

            Assert.Equal(CompanionVisualState.Thinking, face.State); // 忙态不被悬停覆盖
        });
    }

    [Fact]
    public void CompanionFace_ClickUsesAVisualOnlyHappyMomentForRasterSkins()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            App.ReplaceSettings(new AppSettings { SkinId = "MaoDie", AnimationsEnabled = false, IncognitoMode = true });
            App.SkinService.ApplySkin("MaoDie", Application.Current!.Resources);
            var face = new CompanionFace { State = CompanionVisualState.Idle };

            face.PlayClickFeedback();

            Assert.Equal(CompanionVisualState.Idle, face.State);
            var brush = Assert.IsType<ImageBrush>(Assert.IsType<Rectangle>(face.FindName("SkinSpriteHost")).Fill);
            Assert.Equal(new Rect(0.5, 0, 0.25, 1d / 3d), brush.Viewbox);
        });
    }

    [Fact]
    public void CompanionFace_IdleMicroExpressionProjectsToTheActiveSkinWithoutChangingBusinessState()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            App.ReplaceSettings(new AppSettings { SkinId = "MaoDie", AnimationsEnabled = false, IncognitoMode = true });
            App.SkinService.ApplySkin("MaoDie", Application.Current!.Resources);
            var face = new CompanionFace { State = CompanionVisualState.Idle };
            var method = typeof(CompanionFace).GetMethod("ApplyIdleBehaviorVisual", BindingFlags.Instance | BindingFlags.NonPublic)!;

            method.Invoke(face, new object?[] { CompanionIdleBehavior.SoftSmile });

            Assert.Equal(CompanionVisualState.Idle, face.State);
            var brush = Assert.IsType<ImageBrush>(Assert.IsType<Rectangle>(face.FindName("SkinSpriteHost")).Fill);
            Assert.Equal(new Rect(0.5, 0, 0.25, 1d / 3d), brush.Viewbox);
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
            var notice = Assert.IsType<Border>(window.FindName("TitleFeedback"));
            Assert.Equal(Visibility.Visible, notice.Visibility);
            var titleBand = Assert.IsAssignableFrom<DependencyObject>(window.FindName("TitleBand"));
            Assert.True(IsDescendantOf(notice, titleBand));
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

            Assert.Equal(520, window.Width);
            Assert.Equal(176, window.Height);
            Assert.Null(window.FindName("QuickActionBar"));
            Assert.Null(window.FindName("SettingsButton"));
            Assert.Null(window.FindName("CollapseToBallButton"));
            Assert.NotNull(window.FindName("PinButton"));
            var provider = Assert.IsType<ComboBox>(window.FindName("ProviderSelector"));
            var editorDock = Assert.IsType<Border>(window.FindName("EditorDockBottom"));
            Assert.False(IsDescendantOf(provider, editorDock));
            window.Close();
        });
    }

    [Fact]
    public void MainWindow_ResultActionsLiveInTheDockInsteadOfCoveringTheEditor()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            App.ReplaceSettings(new AppSettings { IncognitoMode = true });
            var window = new MainWindow();
            var group = Assert.IsAssignableFrom<DependencyObject>(window.FindName("ResultActionGroup"));
            var dock = Assert.IsAssignableFrom<DependencyObject>(window.FindName("EditorDockBottom"));
            var editor = Assert.IsAssignableFrom<DependencyObject>(window.FindName("MainTextBox"));

            Assert.True(IsDescendantOf(group, dock));
            Assert.False(IsDescendantOf(group, editor));
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
            // R16：揭示动画改为窗口级淡入 + 均匀微缩放（不再缩放面板透明度，避免露出平铺底色带）
            Assert.Equal(expectsAnimation, window.HasAnimatedProperties);
            var transform = Assert.IsType<TransformGroup>(surface.RenderTransform);
            var scale = Assert.IsType<ScaleTransform>(transform.Children[0]);
            Assert.Equal(expectsAnimation, scale.HasAnimatedProperties);
            if (!expectsAnimation)
            {
                Assert.Equal(1, window.Opacity);
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

    [Fact]
    public void CompanionFace_ExternalStateChangeCancelsPendingExpandCompletion()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            App.ReplaceSettings(new AppSettings { IncognitoMode = true, AnimationsEnabled = true });
            var face = new CompanionFace();
            var window = new Window { Content = face, Width = 100, Height = 100 };
            window.Show();
            window.UpdateLayout();
            var completed = false;

            face.PlayExpandFeedbackThen(() => completed = true);
            face.State = CompanionVisualState.Error;
            PumpFor(TimeSpan.FromMilliseconds(260));

            Assert.Equal(CompanionVisualState.Error, face.State);
            Assert.False(completed);
            window.Close();
        });
    }

    [Fact]
    public void MainWindow_InterruptedCollapseStillInvokesCompletionExactlyOnce()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            App.ReplaceSettings(new AppSettings { IncognitoMode = true, AnimationsEnabled = true });
            var window = new MainWindow();
            window.Show();
            window.UpdateLayout();
            var completions = 0;

            window.PlayCollapseAnimation(() => completions++);
            window.PlayRevealAnimation();
            PumpFor(TimeSpan.FromMilliseconds(650));

            Assert.Equal(1, completions);
            Assert.True(Assert.IsType<Grid>(window.FindName("RootGrid")).IsHitTestVisible);
            window.Close();
        });
    }

    [Fact]
    public void FloatingBall_ProgrammaticPlacementDoesNotOverwriteRememberedUserPosition()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            App.ReplaceSettings(new AppSettings
            {
                IncognitoMode = true,
                RememberFloatingBallPosition = true,
                BallLeft = 120,
                BallTop = 140
            });
            var window = new FloatingBallWindow();
            window.Show();
            window.UpdateLayout();

            window.Left = 300;
            window.Top = 320;
            PumpFor(TimeSpan.FromMilliseconds(50));

            Assert.Equal(120, App.Settings.BallLeft);
            Assert.Equal(140, App.Settings.BallTop);
            window.Close();
        });
    }

    private static void PumpFor(TimeSpan duration)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static void RunSta(System.Action action)
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

    private static void EnsureApplicationResources()
    {
        if (Application.Current is { } existing && !ReferenceEquals(existing.Dispatcher, System.Windows.Threading.Dispatcher.CurrentDispatcher))
            TestHelpers.ResetWpfApplication();

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
