using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Huaxiazi.Models;
using Huaxiazi.ViewModels;
using Huaxiazi.Views;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class ResponsiveLayoutRegressionTests
{
    [Fact]
    public void MainToolbar_AtCompactWidthHidesSecondaryActionsWithoutOverlapping()
    {
        var code = System.IO.File.ReadAllText(System.IO.Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "Views", "MainWindow.xaml.cs"));
        Assert.Contains("var compact = windowWidth < 500", code);
        Assert.Contains("HistoryButton.Visibility = compact", code);
        Assert.Contains("DiffToggleButton.Visibility = compact", code);
        Assert.Contains("ResultActionGroup.Visibility = compact", code);
    }

    [Fact]
    public void Clarification_IsRenderedInsideEditorSurfaceWithoutOverlay()
    {
        var xaml = System.IO.File.ReadAllText(System.IO.Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "Views", "MainWindow.xaml"));
        Assert.Contains("x:Name=\"EditorContentHost\"", xaml);
        Assert.Contains("x:Name=\"ClarificationPanel\" Grid.Row=\"0\"", xaml);
        Assert.DoesNotContain("Panel.ZIndex=\"20\"", xaml);
        Assert.DoesNotContain("Width=\"420\" MaxWidth=\"420\"", xaml);
        Assert.Contains("Text=\"{Binding ClarificationPrompt}\"", xaml);
        Assert.Contains("Style=\"{StaticResource ClarificationContinueButton}\"", xaml);
        Assert.Contains("Tag=\"补充信息（可选）\"", xaml);
    }

    [Fact]
    public void MainToolbar_AtDefaultWidthKeepsEveryActionInOneRow()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            var settings = new AppSettings { AnimationsEnabled = false, IncognitoMode = true, MainWindowWidth = 520 };
            App.ReplaceSettings(settings);
            var window = new MainWindow();

            window.ApplyDisplayPreferences(settings);
            window.Show();
            window.UpdateLayout();

            var editingActions = Assert.IsType<StackPanel>(window.FindName("EditingActionGroup"));
            Assert.Equal(Visibility.Visible, window.HistoryButton.Visibility);
            Assert.Equal(Visibility.Visible, window.DiffToggleButton.Visibility);
            Assert.Equal(Visibility.Visible, window.ResultActionGroup.Visibility);
            Assert.Equal(0, Grid.GetRow(editingActions));
            Assert.Equal(34, window.EditorDockBottom.Height, 0);
            Assert.True(window.EditorDockBottom.DesiredSize.Width <= 520);
            window.Close();
        });
    }

    [Fact]
    public void MainToolbar_UsesLogicalWidthWhenUiScaleEnlargesTheWindow()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            var settings = new AppSettings
            {
                AnimationsEnabled = false,
                IncognitoMode = true,
                UiScale = 1.35,
                MainWindowWidth = 520
            };
            App.ReplaceSettings(settings);
            var window = new MainWindow();

            window.ApplyDisplayPreferences(settings);
            window.Show();
            window.UpdateLayout();

            Assert.Equal(702, window.Width, 0);
            var editingActions = Assert.IsType<StackPanel>(window.FindName("EditingActionGroup"));
            Assert.Equal(Visibility.Visible, window.HistoryButton.Visibility);
            Assert.Equal(Visibility.Visible, window.DiffToggleButton.Visibility);
            Assert.Equal(Visibility.Visible, window.ResultActionGroup.Visibility);
            Assert.Equal(0, Grid.GetRow(editingActions));
            window.Close();
        });
    }

    [Fact]
    public void ModeSegment_ClickSwitchesBetweenPolishAndPromptModes()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            App.ReplaceSettings(new AppSettings { AnimationsEnabled = false, IncognitoMode = true });
            var window = new MainWindow();
            var viewModel = Assert.IsType<MainViewModel>(window.DataContext);
            viewModel.CurrentMode = ApplicationMode.Polish;

            window.Show();
            window.UpdateLayout();
            window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.DataBind, new Action(() => { }));
            var polishSegment = Assert.IsType<ToggleButton>(window.FindName("PolishSegment"));
            var promptSegment = Assert.IsType<ToggleButton>(window.FindName("PromptSegment"));
            Assert.False(viewModel.IsBusy);

            Assert.NotNull(promptSegment.Command);
            Assert.Equal(ApplicationMode.PromptOptimize, promptSegment.CommandParameter);
            promptSegment.Command.Execute(promptSegment.CommandParameter);
            window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.DataBind, new Action(() => { }));

            Assert.Equal(ApplicationMode.PromptOptimize, viewModel.CurrentMode);
            Assert.Equal("提示词", viewModel.ModeToggleLabel);

            polishSegment.Command.Execute(polishSegment.CommandParameter);
            window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.DataBind, new Action(() => { }));
            Assert.Equal(ApplicationMode.Polish, viewModel.CurrentMode);
            Assert.Equal("润色", viewModel.ModeToggleLabel);
            window.Close();
        });
    }

    [Fact]
    public void ModeSegment_HighlightsActiveSideAndKeepsBothLabelsVisible()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            App.ReplaceSettings(new AppSettings { AnimationsEnabled = false, IncognitoMode = true });
            var window = new MainWindow();
            var viewModel = Assert.IsType<MainViewModel>(window.DataContext);
            viewModel.CurrentMode = ApplicationMode.Polish;
            var polish = Assert.IsType<TextBlock>(window.FindName("PolishModeLabel"));
            var prompt = Assert.IsType<TextBlock>(window.FindName("PromptModeLabel"));
            var indicator = Assert.IsType<Border>(window.FindName("ModeMorphIndicator"));
            var indicatorTransform = Assert.IsType<TranslateTransform>(window.FindName("ModeMorphTransform"));
            var modeSwitcher = Assert.IsType<Grid>(window.FindName("ModeSwitcher"));

            window.Show();
            window.UpdateLayout();
            window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.DataBind, new Action(() => { }));
            // 工作区草稿可能来自其他测试或上一次运行；验收本身应固定从润色态开始。
            viewModel.CurrentMode = ApplicationMode.Polish;
            window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.DataBind, new Action(() => { }));
            Assert.False(viewModel.IsBusy);
            Assert.True(viewModel.IsPolishMode);

            Assert.Equal(92, modeSwitcher.Width, 0);
            Assert.Equal("润色", polish.Text);
            Assert.Equal("提示词", prompt.Text);
            // 默认润色激活：高亮在左、润色段白字、提示词段弱化且两者均可见。
            Assert.Equal(0, indicatorTransform.X, 0);
            Assert.Equal(Colors.White, ((SolidColorBrush)polish.Foreground).Color);
            Assert.NotEqual(Colors.White, ((SolidColorBrush)prompt.Foreground).Color);
            Assert.Equal(1, polish.Opacity, 0);
            Assert.Equal(1, prompt.Opacity, 0);

            viewModel.CurrentMode = ApplicationMode.PromptOptimize;
            window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.DataBind, new Action(() => { }));
            Assert.Equal(46, indicatorTransform.X, 0);
            Assert.Equal(Colors.White, ((SolidColorBrush)prompt.Foreground).Color);
            Assert.NotEqual(Colors.White, ((SolidColorBrush)polish.Foreground).Color);
            window.Close();
        });
    }

    [Fact]
    public void ModeCarousel_ThumbUsesTheActiveSkinBrandColor()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            App.ReplaceSettings(new AppSettings { AnimationsEnabled = false, IncognitoMode = true });
            var firstBrand = new SolidColorBrush(Color.FromRgb(0x24, 0xC8, 0x8A));
            var secondBrand = new SolidColorBrush(Color.FromRgb(0xF0, 0x7A, 0x45));
            Application.Current.Resources["BrandBrush"] = firstBrand;
            try
            {
                var window = new MainWindow();
                var indicator = Assert.IsType<Border>(window.FindName("ModeMorphIndicator"));
                window.Show();
                window.UpdateLayout();
                Assert.Same(firstBrand, indicator.Background);

                Application.Current.Resources["BrandBrush"] = secondBrand;
                window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.DataBind, new Action(() => { }));
                Assert.Same(secondBrand, indicator.Background);
                window.Close();
            }
            finally
            {
                Application.Current.Resources.Remove("BrandBrush");
            }
        });
    }

    [Fact]
    public void ModeSegment_UsesUnifiedApplicationTypography()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            App.ReplaceSettings(new AppSettings { AnimationsEnabled = false, IncognitoMode = true });
            var window = new MainWindow();
            var polish = Assert.IsType<TextBlock>(window.FindName("PolishModeLabel"));
            var prompt = Assert.IsType<TextBlock>(window.FindName("PromptModeLabel"));
            var indicator = Assert.IsType<Border>(window.FindName("ModeMorphIndicator"));

            Assert.Contains("Segoe UI Variable", polish.FontFamily.Source);
            Assert.Equal(FontWeights.SemiBold, polish.FontWeight);
            Assert.Equal(FontWeights.SemiBold, prompt.FontWeight);
            Assert.Equal(polish.FontFamily.Source, prompt.FontFamily.Source);
            Assert.True(indicator.Width > 0, "滑动高亮应有可见宽度");
            window.Close();
        });
    }

    [Fact]
    public void SkillDetails_NarrowPaneKeepsModeLabelAndActionsOnSeparateLines()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            var view = new ExpressionAbilitySettingsView();
            var layout = Assert.IsType<Grid>(view.FindName("SkillSummaryAndActionsGrid"));
            var mode = Assert.IsType<TextBlock>(view.FindName("SkillModeLabel"));
            var actions = Assert.IsAssignableFrom<Panel>(view.FindName("SkillActionsPanel"));

            layout.Visibility = Visibility.Visible;
            mode.Visibility = Visibility.Visible;
            actions.Visibility = Visibility.Visible;
            layout.Measure(new Size(420, 120));
            layout.Arrange(new Rect(0, 0, 420, layout.DesiredSize.Height));
            layout.UpdateLayout();

            var modeBounds = mode.TransformToAncestor(layout).TransformBounds(new Rect(mode.RenderSize));
            var actionBounds = actions.TransformToAncestor(layout).TransformBounds(new Rect(actions.RenderSize));
            Assert.True(modeBounds.Bottom <= actionBounds.Top,
                $"模式标签与操作按钮发生重叠：mode={modeBounds}, actions={actionBounds}");
        });
    }

    [Theory]
    [InlineData(true, Visibility.Visible, Visibility.Collapsed)]
    [InlineData(false, Visibility.Collapsed, Visibility.Visible)]
    public void SkillPane_ShowsExactlyOneOfDetailsOrEmptyState(
        bool hasSelection, Visibility expectedDetails, Visibility expectedEmpty)
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            var view = new ExpressionAbilitySettingsView
            {
                DataContext = new SkillPaneBindingState
                {
                    HasSelectedAgentSkill = hasSelection,
                    IsSkillDetailsVisible = hasSelection,
                    IsSkillEditorOpen = false
                }
            };
            var details = Assert.IsType<ScrollViewer>(view.FindName("SkillDetailsScrollViewer"));
            var empty = Assert.IsType<StackPanel>(view.FindName("SkillEmptyState"));

            view.Measure(new Size(760, 560));
            view.Arrange(new Rect(0, 0, 760, 560));
            view.UpdateLayout();

            Assert.Equal(expectedDetails, details.Visibility);
            Assert.Equal(expectedEmpty, empty.Visibility);
        });
    }

    [Fact]
    public void SkillManager_ConstrainsTheTwoColumnFrameAndScrollsLongDetailsIndependently()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            var view = new ExpressionAbilitySettingsView();
            var frame = Assert.IsType<Grid>(view.FindName("SkillColumnsFrame"));
            var scroll = Assert.IsType<ScrollViewer>(view.FindName("SkillDetailsScrollViewer"));
            var details = Assert.IsType<StackPanel>(view.FindName("SkillDetailsPane"));

            view.ExpressionOverviewPane.Visibility = Visibility.Collapsed;
            view.ExpressionSkillsPane.Visibility = Visibility.Visible;
            scroll.Visibility = Visibility.Visible;
            details.Children.Add(new Border { Height = 900 });
            view.Measure(new Size(760, 700));
            view.Arrange(new Rect(0, 0, 760, 700));
            view.UpdateLayout();

            Assert.Equal(440, frame.Height, 0);
            Assert.True(scroll.ScrollableHeight > 0,
                $"长详情应在右栏内部滚动：extent={scroll.ExtentHeight}, viewport={scroll.ViewportHeight}");
        });
    }

    [Fact]
    public void SkillManager_LongListAndEditorUseCompactDarkScrollbars()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            var view = new ExpressionAbilitySettingsView();
            var list = Assert.IsType<ListBox>(view.FindName("AgentSkillList"));
            var editorPane = Assert.IsType<Grid>(view.FindName("SkillEditorPane"));
            var editor = editorPane.Children.OfType<TextBox>().Single();

            view.ExpressionOverviewPane.Visibility = Visibility.Collapsed;
            view.ExpressionSkillsPane.Visibility = Visibility.Visible;
            editorPane.Visibility = Visibility.Visible;
            list.ItemsSource = Enumerable.Range(1, 40).Select(index => $"Skill {index}");
            editor.Text = string.Join(Environment.NewLine, Enumerable.Range(1, 80).Select(index => $"Line {index}"));
            view.Measure(new Size(760, 700));
            view.Arrange(new Rect(0, 0, 760, 700));
            view.UpdateLayout();

            var visibleVerticalBars = VisualDescendants<ScrollBar>(view)
                .Where(bar => bar.Orientation == Orientation.Vertical && bar.Visibility == Visibility.Visible)
                .ToList();
            Assert.True(visibleVerticalBars.Count >= 2, "Skill 列表和编辑器都应出现纵向滚动条。");
            Assert.All(visibleVerticalBars, bar => Assert.InRange(bar.ActualWidth, 1, 8));
        });
    }

    private static System.Collections.Generic.IEnumerable<T> VisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in VisualDescendants<T>(child)) yield return descendant;
        }
    }

    private sealed class SkillPaneBindingState
    {
        public bool HasSelectedAgentSkill { get; init; }
        public bool IsSkillDetailsVisible { get; init; }
        public bool IsSkillEditorOpen { get; init; }
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
            finally { TestHelpers.ResetWpfApplication(); }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
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
}
