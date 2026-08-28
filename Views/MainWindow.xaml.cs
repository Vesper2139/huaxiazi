using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Documents;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Shell;
using System.Windows.Threading;
using System.Windows.Media.Animation;
using System.Threading.Tasks;
using Huaxiazi.Models;
using Huaxiazi.Services;
using Huaxiazi.ViewModels;

namespace Huaxiazi.Views;

/// <summary>
/// 主窗口（展开态，深色透明玻璃）。
/// 负责 UI 事件（ESC 收起、Ctrl+Enter 优化、拖拽、菜单路由、置顶/关闭/历史）。
/// 业务逻辑全部委托给 MainViewModel。原文/优化稿/Diff 共用同一个编辑器。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _autosaveTimer;
    private readonly DispatcherTimer _inputPauseTimer;
    private readonly DispatcherTimer _diffRefreshTimer;
    private Window? _settingsWindow;
    private bool _settingsAutoSaving;
    private bool _companionDragging;
    private Point _companionLastDragPosition;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
        _autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(App.Settings.AutosaveDelayMilliseconds) };
        _autosaveTimer.Tick += (_, _) =>
        {
            _autosaveTimer.Stop();
            _vm.SaveDraftIfDirty();
        };
        _inputPauseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(520) };
        _inputPauseTimer.Tick += (_, _) =>
        {
            _inputPauseTimer.Stop();
            CompanionHost.PlayOperationFeedback(new CompanionEvent(CompanionEventKind.InputPaused, BaseState: _vm.CompanionState));
        };
        _diffRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _diffRefreshTimer.Tick += (_, _) =>
        {
            _diffRefreshTimer.Stop();
            RefreshDiffOverlay();
        };
        _vm.PropertyChanged += ViewModel_OnPropertyChanged;
        App.SettingsChanged += App_SettingsChanged;
        Closed += (_, _) => App.SettingsChanged -= App_SettingsChanged;
        WindowPlacementService.Attach(this);
        SizeChanged += MainWindow_OnSizeChanged;
    }

    private void App_SettingsChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => App_SettingsChanged(sender, e)));
            return;
        }
        _vm.ReloadPreferences();
        ApplyDisplayPreferences(App.Settings);
        ApplyToolbarLayout(ActualWidth > 0 ? ActualWidth : Width);
    }

    private void MainWindow_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // The default 520-DIP window cannot carry every secondary action on one rail.
        // Keep the daily editing path stable and reveal result/history tools once there is room.
        ApplyToolbarLayout(e.NewSize.Width);
        if (IsLoaded && e.PreviousSize.Width > 0 && e.PreviousSize.Height > 0)
        {
            var direction = DirectionFromDelta(e.NewSize.Width - e.PreviousSize.Width, e.NewSize.Height - e.PreviousSize.Height);
            CompanionHost.PlayOperationFeedback(new CompanionEvent(CompanionEventKind.Resized, Direction: direction, BaseState: _vm.CompanionState));
        }
    }

    private void ApplyToolbarLayout(double windowWidth)
    {
        var compact = windowWidth < 500;
        if (HistoryButton is not null) HistoryButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        if (DiffToggleButton is not null) DiffToggleButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        if (ResultActionGroup is not null) ResultActionGroup.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        if (EditorDockBottom is null || EditorDockGrid is null || EditingActionGroup is null) return;

        EditorDockBottom.Height = 34;
        EditorDockGrid.RowDefinitions[1].Height = new GridLength(0);
        Grid.SetRow(EditingActionGroup, 0);
        Grid.SetColumn(EditingActionGroup, 1);
        Grid.SetColumnSpan(EditingActionGroup, 1);
        EditingActionGroup.Margin = new Thickness(0);
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
        ApplyToolbarLayout(ActualWidth > 0 ? ActualWidth : Width);
        RefreshDiffOverlay();
        UpdateModeCarouselVisual(animate: false);
        if (Application.Current is App app) app.UpdateCompanionState(_vm.CompanionState);
    }

    private void ViewModel_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsBusy))
        {
            // IsBusy is the source of truth for request feedback. Keep this independent from
            // pointer events so Enter, accessibility and programmatic commands animate equally.
            if (_vm.IsBusy) CompanionHost.PlayGenerationStarted(_vm.CompanionState);
            else CompanionHost.PlayGenerationFinished(_vm.CompanionState);
        }
        if (e.PropertyName is nameof(MainViewModel.CompanionState) or nameof(MainViewModel.IsBusy))
        {
            if (Application.Current is App app) app.UpdateCompanionState(_vm.CompanionState);
        }
        if (e.PropertyName is nameof(MainViewModel.ShowDiff) or nameof(MainViewModel.ViewMode))
        {
            _diffRefreshTimer.Stop();
            RefreshDiffOverlay();
        }
        else if (e.PropertyName is nameof(MainViewModel.UserInput) or nameof(MainViewModel.OptimizedResult))
        {
            _diffRefreshTimer.Stop();
            if (_vm.ShowDiff) _diffRefreshTimer.Start();
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
        if (e.PropertyName == nameof(MainViewModel.HasError) && _vm.HasError)
            CompanionHost.PlayOperationFeedback(new CompanionEvent(CompanionEventKind.Failure, _vm.ErrorMessage, BaseState: CompanionVisualState.Error));
        if (e.PropertyName == nameof(MainViewModel.CurrentMode))
            UpdateModeCarouselVisual(animate: true);
    }

    private void RefreshDiffOverlay()
    {
        if (DiffOverlay is null) return;
        DiffOverlay.Inlines.Clear();
        if (!_vm.ShowDiff) return;
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
        CompanionHost.PlayOperationFeedback(new CompanionEvent(CompanionEventKind.PinChanged, BaseState: _vm.CompanionState));
    }

    /// <summary>右上角关闭：按 CloseBehavior 执行（收起/托盘/退出）。</summary>
    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        CompanionHost.PlayOperationFeedback(new CompanionEvent(CompanionEventKind.Closing, BaseState: _vm.CompanionState));
        if (App.Settings.CloseBehavior == "Exit") ((App)App.Current).ExitApp();
        else ((App)App.Current).CollapseToFloatingBall();
    }

    private void TaskbarMinimizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        ShowInTaskbar = true;
        WindowState = WindowState.Minimized;
    }

    /// <summary>底部「历史」：弹出最近记录。</summary>
    private void HistoryButton_OnClick(object sender, RoutedEventArgs e)
    {
        CompanionHost.PlayOperationFeedback(new CompanionEvent(CompanionEventKind.HistoryOpened, BaseState: _vm.CompanionState));
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
        if (e.ClickCount >= 2)
        {
            // 双击精灵：收起为悬浮球
            ((App)App.Current).CollapseToFloatingBall();
            e.Handled = true;
            return;
        }

        if (e.ButtonState != MouseButtonState.Pressed) return;
        var start = new Point(Left, Top);
        _companionLastDragPosition = start;
        _companionDragging = true;
        CompanionHost.PlayDragStartFeedback();
        try { DragMove(); }
        catch (InvalidOperationException) { }
        finally { _companionDragging = false; }

        var moved = Math.Abs(Left - start.X) > 3 || Math.Abs(Top - start.Y) > 3;
        CompanionHost.PlayDragEndFeedback(_vm.CompanionState);
        if (!moved) CompanionHost.PlayClickFeedback();
        e.Handled = true;
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (!_companionDragging) return;
        CompanionHost.PlayDragDirection(
            Left - _companionLastDragPosition.X,
            Top - _companionLastDragPosition.Y);
        _companionLastDragPosition = new Point(Left, Top);
    }

    private void CompanionDragHandle_OnMouseEnter(object sender, MouseEventArgs e)
    {
        // 非点击：悬停精灵时切好奇表情（仅空闲时生效，忙态不覆盖）
        CompanionHost.PlayHoverFeedback();
    }

    private void CompanionDragHandle_OnMouseLeave(object sender, MouseEventArgs e)
    {
        CompanionHost.PlayHoverEndFeedback();
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
        CompanionHost.PlayOperationFeedback(new CompanionEvent(CompanionEventKind.SettingsOpened, BaseState: _vm.CompanionState));
        OpenSettingsView();
    }

    private void OperationButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: CompanionEventKind kind })
            CompanionHost.PlayOperationFeedback(new CompanionEvent(kind, BaseState: _vm.CompanionState));
    }

    private void ModeCarouselButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_vm.IsBusy) return;
        CompanionHost.PlayOperationFeedback(new CompanionEvent(CompanionEventKind.ViewChanged, BaseState: _vm.CompanionState));
    }

    private void UpdateModeCarouselVisual(bool animate)
    {
        if (ModeMorphTransform is null || ModeMorphIndicator is null
            || PolishModeLabel is null || PromptModeLabel is null) return;

        var targetX = _vm.IsPromptOptimizeMode ? 46d : 0d;
        var currentX = ModeMorphTransform.X;
        StopModeCarouselAnimations();

        ModeMorphTransform.X = targetX;
        // 激活段文字置于品牌高亮之上（白）；非激活段文字弱化置于轨道之上。
        if (!animate || !App.Settings.AnimationsEnabled || Math.Abs(currentX - targetX) <= 0.5) return;

        var positionAnimation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        positionAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(currentX, TimeSpan.Zero));
        positionAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(targetX, TimeSpan.FromMilliseconds(200), new CubicEase { EasingMode = EasingMode.EaseInOut }));
        ModeMorphTransform.BeginAnimation(TranslateTransform.XProperty, positionAnimation);
    }

    private void StopModeCarouselAnimations()
    {
        ModeMorphTransform?.BeginAnimation(TranslateTransform.XProperty, null);
    }

    private void ModeSwitcher_OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 滚轮经过控件时不改变任务模式，只有用户先聚焦切换器才允许键盘/滚轮操作。
        if (e.Delta == 0 || _vm.IsBusy) return;
        if (!ModeSwitcher.IsKeyboardFocusWithin) return;
        var target = e.Delta > 0 ? ApplicationMode.Polish : ApplicationMode.PromptOptimize;
        _vm.SelectModeCommand.Execute(target);
        e.Handled = true;
    }

    private void MainTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || !MainTextBox.IsKeyboardFocusWithin) return;
        CompanionHost.PlayOperationFeedback(new CompanionEvent(CompanionEventKind.InputStarted, BaseState: _vm.CompanionState));
        _inputPauseTimer.Stop();
        _inputPauseTimer.Start();
    }

    private void MainTextBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && App.Settings.EnterToSend)
        {
            e.Handled = true;
            CompanionHost.PlayOperationFeedback(new CompanionEvent(CompanionEventKind.Submitted, BaseState: _vm.CompanionState));
            _ = _vm.OptimizeCommand.ExecuteAsync(null);
            return;
        }
        if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            CompanionHost.PlayOperationFeedback(new CompanionEvent(CompanionEventKind.Pasted, BaseState: _vm.CompanionState));
    }

    private static CompanionDirection DirectionFromDelta(double x, double y)
    {
        if (Math.Abs(x) < 0.5 && Math.Abs(y) < 0.5) return CompanionDirection.None;
        var horizontal = Math.Abs(x) < 0.5 ? 0 : Math.Sign(x);
        var vertical = Math.Abs(y) < 0.5 ? 0 : Math.Sign(y);
        return (horizontal, vertical) switch
        {
            (0, -1) => CompanionDirection.North,
            (1, -1) => CompanionDirection.NorthEast,
            (1, 0) => CompanionDirection.East,
            (1, 1) => CompanionDirection.SouthEast,
            (0, 1) => CompanionDirection.South,
            (-1, 1) => CompanionDirection.SouthWest,
            (-1, 0) => CompanionDirection.West,
            _ => CompanionDirection.NorthWest
        };
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
            Title = "话匣子设置",
            Owner = this,
            Content = view,
            Width = 900,
            Height = 650,
            MinWidth = 760,
            MinHeight = 540,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ResizeMode = ResizeMode.CanResize,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = false
        };
        // 背景随皮肤字典实时换肤（直接取 FindResource 会把当前皮肤烘焙进窗口）
        _settingsWindow.SetResourceReference(Window.BackgroundProperty, "BgBrush");
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
        _settingsWindow.Closing += (_, args) =>
        {
            if (!view.IsCommitted && view.HasChanges && !_settingsAutoSaving)
            {
                args.Cancel = true;
                _settingsAutoSaving = true;
                _ = AutoSaveSettingsAndCloseAsync(_settingsWindow, view);
                return;
            }
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

    private async Task AutoSaveSettingsAndCloseAsync(Window window, SettingsView view)
    {
        try
        {
            await view.CommitAndCloseAsync();
        }
        finally
        {
            _settingsAutoSaving = false;
        }
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
            CompanionHost.PlayOperationFeedback(new CompanionEvent(CompanionEventKind.Submitted, BaseState: _vm.CompanionState));
            _ = _vm.OptimizeCommand.ExecuteAsync(null);
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            var operation = e.Key switch
            {
                Key.Z => CompanionEventKind.Undo,
                Key.Y => CompanionEventKind.Redo,
                Key.V => CompanionEventKind.Pasted,
                Key.C when _vm.ShowResultToggle => CompanionEventKind.Confirmed,
                _ => (CompanionEventKind?)null
            };
            if (operation is { } kind)
                CompanionHost.PlayOperationFeedback(new CompanionEvent(kind, BaseState: _vm.CompanionState));
        }

        base.OnKeyDown(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _autosaveTimer.Stop();
        _inputPauseTimer.Stop();
        StopModeCarouselAnimations();
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

    internal void ApplyDisplayPreferences(Huaxiazi.Models.AppSettings settings)
    {
        settings.NormalizeDisplaySettings();
        Opacity = settings.WindowOpacity;
        MainTextBox.FontSize = settings.EditorFontSize;
        var renderedLineHeight = Math.Ceiling(MainTextBox.FontSize * MainTextBox.FontFamily.LineSpacing);
        var safeEditorHeight = renderedLineHeight + MainTextBox.Padding.Top + MainTextBox.Padding.Bottom + 2;
        MainTextBox.MinHeight = Math.Max(settings.EditorDefaultHeight, safeEditorHeight);
        var scale = settings.UiScale;
        RootGrid.LayoutTransform = new ScaleTransform(scale, scale);
        MinWidth = 420 * scale;
        var baseMinHeight = Math.Max(152, MainTextBox.MinHeight + 76);
        MinHeight = baseMinHeight * scale;
        Width = Math.Max(420, settings.MainWindowWidth) * scale;
        Height = Math.Max(baseMinHeight, settings.MainWindowHeight) * scale;
        ApplyToolbarLayout(Width);
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

        BeginAnimation(OpacityProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        offset.BeginAnimation(TranslateTransform.XProperty, null);
        offset.BeginAnimation(TranslateTransform.YProperty, null);
        CompanionTransitionOffset.BeginAnimation(TranslateTransform.XProperty, null);
        CompanionTransitionOffset.BeginAnimation(TranslateTransform.YProperty, null);
        var baseOpacity = Math.Clamp(App.Settings.WindowOpacity, 0.2, 1);
        Opacity = baseOpacity;
        scale.ScaleX = scale.ScaleY = 1;
        offset.X = offset.Y = 0;
        CompanionTransitionOffset.X = CompanionTransitionOffset.Y = 0;
        if (!App.Settings.AnimationsEnabled) return;

        UpdateLayout();
        var companionCenter = CompanionDragHandle.TranslatePoint(new Point(CompanionDragHandle.ActualWidth / 2, CompanionDragHandle.ActualHeight / 2), RootGrid);
        WindowSurface.RenderTransformOrigin = new Point(
            Math.Clamp(companionCenter.X / Math.Max(1, WindowSurface.ActualWidth), 0, 1),
            Math.Clamp(companionCenter.Y / Math.Max(1, WindowSurface.ActualHeight), 0, 1));
        var travel = ballCenter.HasValue
            ? ballCenter.Value - new Point(Left + companionCenter.X, Top + companionCenter.Y)
            : new Vector(-5, -4);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(200);

        // 窗口级淡入（终点=用户透明度设置，结束后回落到基础值）+ 均匀微缩放，从球位向外弹出
        BeginAnimation(OpacityProperty, new DoubleAnimation(0.15, baseOpacity, duration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, 1, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, 1, duration) { EasingFunction = ease });
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
            Math.Clamp(companionCenter.X / Math.Max(1, WindowSurface.ActualWidth), 0, 1),
            Math.Clamp(companionCenter.Y / Math.Max(1, WindowSurface.ActualHeight), 0, 1));
        var travel = targetBallCenter.HasValue
            ? targetBallCenter.Value - new Point(Left + companionCenter.X, Top + companionCenter.Y)
            : new Vector(-5, -4);
        var baseOpacity = Math.Clamp(App.Settings.WindowOpacity, 0.2, 1);

        RootGrid.IsHitTestVisible = false;
        var duration = TimeSpan.FromMilliseconds(180);
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var opacity = new DoubleAnimation(baseOpacity, 0.15, duration) { EasingFunction = ease };
        var completionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        var completionInvoked = false;
        void CompleteOnce()
        {
            if (completionInvoked) return;
            completionInvoked = true;
            completionTimer.Stop();
            RootGrid.IsHitTestVisible = true;
            // 收起完成立即复位透明度基础值，避免下一次展开从 0.15 起步
            BeginAnimation(OpacityProperty, null);
            Opacity = baseOpacity;
            completed();
        }
        completionTimer.Tick += (_, _) => CompleteOnce();
        opacity.Completed += (_, _) => CompleteOnce();
        completionTimer.Start();
        BeginAnimation(OpacityProperty, opacity);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.97, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.97, duration) { EasingFunction = ease });
        CompanionTransitionOffset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, travel.X, duration) { EasingFunction = ease });
        CompanionTransitionOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, travel.Y, duration) { EasingFunction = ease });
    }

    internal void ExecuteGlobalHotkeyAction(GlobalHotkeyAction action)
    {
        switch (action)
        {
            case GlobalHotkeyAction.QuickPolish:
                _vm.SelectModeCommand.Execute(Huaxiazi.Models.ApplicationMode.Polish);
                _vm.PasteFromClipboardCommand.Execute(null);
                if (!string.IsNullOrWhiteSpace(_vm.UserInput)) _ = _vm.OptimizeCommand.ExecuteAsync(null);
                break;
            case GlobalHotkeyAction.QuickPromptOptimize:
                _vm.SelectModeCommand.Execute(Huaxiazi.Models.ApplicationMode.PromptOptimize);
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
