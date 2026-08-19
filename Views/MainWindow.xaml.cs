using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Documents;
using System.Windows.Controls;
using System.Windows.Shell;
using System.Windows.Threading;
using System.Windows.Media.Animation;
using PromptFloat.Models;
using PromptFloat.Services;
using PromptFloat.ViewModels;

namespace PromptFloat.Views;

/// <summary>
/// 主窗口（展开态，深色透明玻璃）。
/// 负责 UI 事件（ESC 收起、Ctrl+Enter 优化、拖拽、菜单路由、置顶/关闭/历史）。
/// 业务逻辑全部委托给 MainViewModel。原文/优化稿/Diff 共用同一个编辑器。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _autosaveTimer;
    private Window? _settingsWindow;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
        _vm.PropertyChanged += ViewModel_OnPropertyChanged;
        _autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(App.Settings.AutosaveDelayMilliseconds) };
        _autosaveTimer.Tick += (_, _) =>
        {
            _autosaveTimer.Stop();
            _vm.SaveDraftIfDirty();
        };
        WindowPlacementService.Attach(this);
        SizeChanged += MainWindow_OnSizeChanged;
    }

    private void MainWindow_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Compact: low-frequency history is routed through the ellipsis menu instead of shrinking controls.
        if (HistoryButton is not null) HistoryButton.Visibility = e.NewSize.Width < 520 ? Visibility.Collapsed : Visibility.Visible;
        if (DiffToggleButton is not null) DiffToggleButton.Visibility = e.NewSize.Width < 480 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        Topmost = App.Settings.AlwaysOnTop;
        PinButton.IsChecked = App.Settings.AlwaysOnTop;
        // 默认不读取剪贴板；只有用户在设置中明确开启时才预填。
        if (App.Settings.ClipboardAutoRead && string.IsNullOrWhiteSpace(_vm.UserInput))
        {
            var clip = App.ClipboardService.GetText();
            if (!string.IsNullOrWhiteSpace(clip) && clip.Length <= 2000)
            {
                _vm.UserInput = clip;
            }
        }
        _vm.ShowDiff = App.Settings.ShowDiff;
        ApplyDisplayPreferences(App.Settings);
        RefreshDiffOverlay();
    }

    private void ViewModel_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.ShowDiff)
            or nameof(MainViewModel.UserInput)
            or nameof(MainViewModel.OptimizedResult)
            or nameof(MainViewModel.ViewMode))
        {
            RefreshDiffOverlay();
        }

        if (e.PropertyName is nameof(MainViewModel.UserInput)
            or nameof(MainViewModel.OptimizedResult)
            or nameof(MainViewModel.ViewMode)
            or nameof(MainViewModel.CurrentMode)
            or nameof(MainViewModel.SelectedCategory)
            or nameof(MainViewModel.SelectedDepth)
            or nameof(MainViewModel.Recipient)
            or nameof(MainViewModel.Channel)
            or nameof(MainViewModel.Purpose)
            or nameof(MainViewModel.Formality)
            or nameof(MainViewModel.Scenario)
            or nameof(MainViewModel.ActiveProviderProfile))
        {
            _autosaveTimer.Stop();
            _autosaveTimer.Start();
        }
    }

    private void RefreshDiffOverlay()
    {
        if (DiffOverlay is null) return;
        DiffOverlay.Inlines.Clear();
        foreach (var line in DiffEngine.Diff(_vm.UserInput, _vm.OptimizedResult))
        {
            var run = new Run(string.IsNullOrEmpty(line.Text) ? " " : line.Text);
            switch (line.Kind)
            {
                case DiffLineKind.Added:
                    run.Background = new SolidColorBrush(Color.FromArgb(0x35, 0x35, 0xB9, 0x8E));
                    break;
                case DiffLineKind.Removed:
                    run.Foreground = new SolidColorBrush(Color.FromRgb(0xB4, 0xC4, 0xD4));
                    run.TextDecorations = TextDecorations.Strikethrough;
                    break;
            }
            DiffOverlay.Inlines.Add(run);
            DiffOverlay.Inlines.Add(new LineBreak());
        }
    }

    /// <summary>置顶开关：立即生效并回写配置。</summary>
    private void PinButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (PinButton is null) return;
        Topmost = PinButton.IsChecked == true;
        App.Settings.AlwaysOnTop = Topmost;
    }

    /// <summary>右上角关闭：收起为悬浮球（应用保持常驻）。</summary>
    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (App.Settings.CloseBehavior == "Exit") ((App)App.Current).ExitApp();
        else ((App)App.Current).CollapseToFloatingBall();
    }

    private void CollapseToBallButton_OnClick(object sender, RoutedEventArgs e)
    {
        ((App)App.Current).CollapseToFloatingBall();
    }

    private void TaskbarMinimizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        ShowInTaskbar = true;
        WindowState = WindowState.Minimized;
    }

    /// <summary>底部「历史」：弹出最近记录。</summary>
    private void HistoryButton_OnClick(object sender, RoutedEventArgs e)
    {
        _vm.RefreshHistoryCommand.Execute(null);
        HistoryPopup.IsOpen = true;
    }

    /// <summary>历史条目点击：载入该条原文。</summary>
    private void HistoryItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ContentRevision revision })
        {
            _vm.LoadRevisionCommand.Execute(revision);
            HistoryPopup.IsOpen = false;
        }
    }

    /// <summary>拖拽条：按下空白处拖动窗口（按钮区域不触发拖拽，避免吞掉点击）。</summary>
    private void DragHandle_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 仅在拖拽条非按钮区域启动拖拽，确保标题栏按钮的点击正常触发。
        if (e.OriginalSource is Button)
        {
            return;
        }
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CompanionDragHandle_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        var start = new Point(Left, Top);
        CompanionHost.PlayDragStartFeedback();
        try { DragMove(); }
        catch (InvalidOperationException) { }

        var moved = Math.Abs(Left - start.X) > 3 || Math.Abs(Top - start.Y) > 3;
        CompanionHost.PlayDragEndFeedback(_vm.CompanionState);
        if (!moved) CompanionHost.PlayClickFeedback();
        e.Handled = true;
    }

    /// <summary>“更多”按钮：左键展开上下文菜单。</summary>
    private void MenuButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } cm })
        {
            cm.PlacementTarget = (Button)sender;
            cm.IsOpen = true;
        }
    }

    /// <summary>打开设置窗口。</summary>
    private void SettingsMenu_OnClick(object sender, RoutedEventArgs e)
    {
        OpenSettingsView();
    }

    internal void OpenSettingsView()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var view = new SettingsView();
        _settingsWindow = new Window
        {
            Title = "Vesper 设置",
            Owner = this,
            Content = view,
            Width = 900,
            Height = 650,
            MinWidth = 760,
            MinHeight = 540,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ResizeMode = ResizeMode.CanResize,
            ShowInTaskbar = false,
            Background = (Brush)FindResource("BgBrush"),
            WindowStyle = WindowStyle.None,
            AllowsTransparency = false
        };
        var workArea = WindowPlacementService.GetCurrentWorkAreaDip(this);
        _settingsWindow.Left = workArea.Left + Math.Max(0, (workArea.Width - _settingsWindow.Width) / 2);
        _settingsWindow.Top = workArea.Top + Math.Max(0, (workArea.Height - _settingsWindow.Height) / 2);
        WindowChrome.SetWindowChrome(_settingsWindow, new WindowChrome
        {
            CaptionHeight = 0,
            CornerRadius = new CornerRadius(16),
            GlassFrameThickness = new Thickness(0),
            ResizeBorderThickness = new Thickness(6)
        });
        WindowPlacementService.Attach(_settingsWindow);
        view.CloseRequested += (_, _) => _settingsWindow?.Close();
        _settingsWindow.Closing += (_, _) =>
        {
            if (!view.IsCommitted) view.CancelPreview();
        };
        _settingsWindow.Closed += (_, _) =>
        {
            _vm.ReloadPreferences();
            ApplyDisplayPreferences(App.Settings);
            _settingsWindow = null;
        };
        _settingsWindow.Show();
    }

    /// <summary>收起为悬浮球。</summary>
    private void MinimizeMenu_OnClick(object sender, RoutedEventArgs e)
    {
        ((App)App.Current).CollapseToFloatingBall();
    }

    /// <summary>退出整个应用。</summary>
    private void ExitMenu_OnClick(object sender, RoutedEventArgs e)
    {
        ((App)App.Current).ExitApp();
    }

    /// <summary>
    /// 键盘快捷键：ESC 收起为悬浮球；Ctrl+Enter 直接优化。
    /// </summary>
    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            var application = (App)App.Current;
            if (App.Settings.EscapeBehavior == "Tray") application.HideMainWindowToTray();
            else if (App.Settings.EscapeBehavior == "Hide") application.CollapseToFloatingBall();
            else { base.OnKeyDown(e); return; }
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            _ = _vm.OptimizeCommand.ExecuteAsync(null);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _autosaveTimer.Stop();
        _vm.SaveDraftIfDirty();
        if (App.Current is App application && !application.IsExiting)
        {
            e.Cancel = true;
            if (App.Settings.CloseBehavior == "Exit") application.ExitApp();
            else if (App.Settings.CloseBehavior == "Tray") application.HideMainWindowToTray();
            else application.CollapseToFloatingBall();
            return;
        }
        base.OnClosing(e);
    }

    internal void ApplyDisplayPreferences(PromptFloat.Models.AppSettings settings)
    {
        settings.NormalizeDisplaySettings();
        Opacity = settings.WindowOpacity;
        MainTextBox.FontSize = settings.EditorFontSize;
        MainTextBox.MinHeight = settings.EditorDefaultHeight;
        var scale = settings.UiScale;
        RootGrid.LayoutTransform = new ScaleTransform(scale, scale);
        MinWidth = 420 * scale;
        var baseMinHeight = Math.Max(152, settings.EditorDefaultHeight + 76);
        MinHeight = baseMinHeight * scale;
        Width = Math.Max(420, settings.MainWindowWidth) * scale;
        Height = Math.Max(baseMinHeight, settings.MainWindowHeight) * scale;
    }

    internal void PlayRevealAnimation(Point? ballCenter = null)
    {
        if (WindowSurface.RenderTransform is not TransformGroup group ||
            group.Children.Count < 2 ||
            group.Children[0] is not ScaleTransform scale ||
            group.Children[1] is not TranslateTransform offset)
        {
            return;
        }

        WindowSurface.BeginAnimation(OpacityProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        offset.BeginAnimation(TranslateTransform.XProperty, null);
        offset.BeginAnimation(TranslateTransform.YProperty, null);
        CompanionTransitionOffset.BeginAnimation(TranslateTransform.XProperty, null);
        CompanionTransitionOffset.BeginAnimation(TranslateTransform.YProperty, null);
        WindowSurface.Opacity = 1;
        scale.ScaleX = scale.ScaleY = 1;
        offset.X = offset.Y = 0;
        CompanionTransitionOffset.X = CompanionTransitionOffset.Y = 0;
        if (!App.Settings.AnimationsEnabled) return;

        UpdateLayout();
        var companionCenter = CompanionDragHandle.TranslatePoint(new Point(CompanionDragHandle.ActualWidth / 2, CompanionDragHandle.ActualHeight / 2), RootGrid);
        WindowSurface.RenderTransformOrigin = new Point(
            companionCenter.X / Math.Max(1, WindowSurface.ActualWidth),
            companionCenter.Y / Math.Max(1, WindowSurface.ActualHeight));
        var travel = ballCenter.HasValue
            ? ballCenter.Value - new Point(Left + companionCenter.X, Top + companionCenter.Y)
            : new Vector(-5, -4);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(260);
        WindowSurface.BeginAnimation(OpacityProperty, new DoubleAnimation(0.16, 1, TimeSpan.FromMilliseconds(210)) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.08, 1, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.24, 1, duration) { EasingFunction = ease });
        CompanionTransitionOffset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(travel.X, 0, duration) { EasingFunction = ease });
        CompanionTransitionOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(travel.Y, 0, duration) { EasingFunction = ease });
    }

    internal void PlayCollapseAnimation(Action completed)
        => PlayCollapseAnimation(null, completed);

    internal void PlayCollapseAnimation(Point? targetBallCenter, Action completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        if (!App.Settings.AnimationsEnabled ||
            WindowSurface.RenderTransform is not TransformGroup group ||
            group.Children.Count < 2 ||
            group.Children[0] is not ScaleTransform scale ||
            group.Children[1] is not TranslateTransform offset)
        {
            completed();
            return;
        }

        UpdateLayout();
        var companionCenter = CompanionDragHandle.TranslatePoint(new Point(CompanionDragHandle.ActualWidth / 2, CompanionDragHandle.ActualHeight / 2), RootGrid);
        WindowSurface.RenderTransformOrigin = new Point(
            companionCenter.X / Math.Max(1, WindowSurface.ActualWidth),
            companionCenter.Y / Math.Max(1, WindowSurface.ActualHeight));
        var travel = targetBallCenter.HasValue
            ? targetBallCenter.Value - new Point(Left + companionCenter.X, Top + companionCenter.Y)
            : new Vector(-5, -4);

        RootGrid.IsHitTestVisible = false;
        var duration = TimeSpan.FromMilliseconds(220);
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var opacity = new DoubleAnimation(1, 0.12, duration) { EasingFunction = ease };
        opacity.Completed += (_, _) =>
        {
            RootGrid.IsHitTestVisible = true;
            completed();
        };
        WindowSurface.BeginAnimation(OpacityProperty, opacity);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.08, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.24, duration) { EasingFunction = ease });
        CompanionTransitionOffset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, travel.X, duration) { EasingFunction = ease });
        CompanionTransitionOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, travel.Y, duration) { EasingFunction = ease });
    }

    internal void ExecuteGlobalHotkeyAction(GlobalHotkeyAction action)
    {
        switch (action)
        {
            case GlobalHotkeyAction.QuickPolish:
                _vm.SelectModeCommand.Execute(PromptFloat.Models.ApplicationMode.Polish);
                _vm.PasteFromClipboardCommand.Execute(null);
                if (!string.IsNullOrWhiteSpace(_vm.UserInput)) _ = _vm.OptimizeCommand.ExecuteAsync(null);
                break;
            case GlobalHotkeyAction.QuickPromptOptimize:
                _vm.SelectModeCommand.Execute(PromptFloat.Models.ApplicationMode.PromptOptimize);
                _vm.PasteFromClipboardCommand.Execute(null);
                if (!string.IsNullOrWhiteSpace(_vm.UserInput)) _ = _vm.OptimizeCommand.ExecuteAsync(null);
                break;
            case GlobalHotkeyAction.CopyResult:
                _vm.CopyResultCommand.Execute(null);
                break;
        }
    }

    internal void SetSourceApplicationContext(SourceApplicationContext context) =>
        _vm.SetSourceApplicationContext(context);
}
