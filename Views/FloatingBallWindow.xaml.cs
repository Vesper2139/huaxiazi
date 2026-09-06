using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Huaxiazi.Services;
using Huaxiazi.Models;

namespace Huaxiazi.Views;

/// <summary>
/// 收缩态悬浮球。双击展开主窗口；长按/拖动移动位置。
/// 精灵视觉尺寸固定 44×44，窗口外壳 60×60 留出热区余量。
/// 关闭/移动时记忆位置到配置。
/// </summary>
public partial class FloatingBallWindow : Window
{
    public FloatingBallWindow()
    {
        InitializeComponent();
        WindowPlacementService.Attach(this);
        ApplyDisplayPreferences(App.Settings);
    }

    private void FloatingBall_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (double.IsNaN(Left) || double.IsNaN(Top))
        {
            Left = SystemParameters.WorkArea.Width - Width - 20;
            Top = SystemParameters.WorkArea.Height - Height - 20;
        }
    }

    internal void ApplyDisplayPreferences(AppSettings settings)
    {
        settings.NormalizeDisplaySettings();
        var size = Math.Clamp(settings.FloatingBallSize, 28, 72);
        var hadPosition = IsVisible && !double.IsNaN(Left) && !double.IsNaN(Top);
        var center = hadPosition ? new Point(Left + Width / 2, Top + Height / 2) : default;
        Width = size + 16;
        Height = size + 16;
        Orb.Width = size;
        Orb.Height = size;
        Opacity = settings.FloatingBallOpacity;
        if (hadPosition)
        {
            Left = center.X - Width / 2;
            Top = center.Y - Height / 2;
        }
    }

    /// <summary>
    /// Synchronizes the compact companion with the workflow state owned by the main view model.
    /// The floating ball has no independent request pipeline; it is only a visual projection.
    /// </summary>
    internal void SetCompanionState(CompanionVisualState state)
    {
        CompanionFace.State = state;
        var status = state switch
        {
            CompanionVisualState.Working => "已开始处理",
            CompanionVisualState.Thinking => "正在处理",
            CompanionVisualState.Curious => "需要补充信息",
            CompanionVisualState.Happy => "处理完成",
            CompanionVisualState.Error => "处理失败",
            _ => "准备就绪"
        };
        // 悬浮球不显示长文本，悬停时用简短状态提供可发现的反馈，避免挤压球体布局。
        Orb.ToolTip = $"{status} · 双击展开 · 拖动移动 · 右键菜单";
        System.Windows.Automation.AutomationProperties.SetHelpText(Orb, status);
    }

    private const double DragThreshold = 3.0;
    private bool _dragging;
    private Point _lastDragPosition;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (e.ClickCount >= 2)
        {
            CompanionFace.PlayExpandFeedbackThen(() =>
            {
                ((App)App.Current).ShowMainWindow();
            });
            e.Handled = true;
            return;
        }

        var dragStart = new Point(Left, Top);
        _lastDragPosition = dragStart;
        _dragging = true;
        CompanionFace.PlayDragStartFeedback();
        var app = (App)App.Current;
        app.BeginFloatingBallDrag();
        try { DragMove(); }
        catch (InvalidOperationException) { }

        _dragging = false;
        var moved = Math.Abs(Left - dragStart.X) > DragThreshold ||
                    Math.Abs(Top - dragStart.Y) > DragThreshold;
        var beforeSnap = new Point(Left, Top);
        app.CompleteFloatingBallDrag(
            new Point(Left, Top), new Size(ActualWidth, ActualHeight),
            WindowPlacementService.GetCurrentWorkAreaDip(this));
        if (moved)
        {
            CompanionFace.PlayDragEndFeedback();
            var snapX = Left - beforeSnap.X;
            var snapY = Top - beforeSnap.Y;
            if (Math.Abs(snapX) > 0.5 || Math.Abs(snapY) > 0.5)
                CompanionFace.PlayOperationFeedback(new CompanionEvent(
                    CompanionEventKind.SnappedToEdge,
                    Direction: DirectionFromDelta(snapX, snapY)));
        }
        else
        {
            CompanionFace.PlayClickFeedback();
            CompanionFace.ReturnToIdle();
        }
        e.Handled = true;
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        // 非点击：悬停到悬浮球时切好奇表情
        CompanionFace.PlayHoverFeedback();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        CompanionFace.PlayHoverEndFeedback();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        App.Settings.BallLeft = Left;
        App.Settings.BallTop = Top;
        base.OnClosing(e);
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (IsVisible && _dragging)
        {
            if (App.Settings.RememberFloatingBallPosition)
            {
                App.Settings.BallLeft = Left;
                App.Settings.BallTop = Top;
            }
            CompanionFace.PlayDragDirection(Left - _lastDragPosition.X, Top - _lastDragPosition.Y);
            _lastDragPosition = new Point(Left, Top);
        }
    }

    private void OpenMenu_OnClick(object sender, RoutedEventArgs e)
    {
        CompanionFace.PlayOperationFeedback(new CompanionEvent(CompanionEventKind.Expanding));
        ((App)App.Current).ShowMainWindow();
    }

    private void SettingsMenu_OnClick(object sender, RoutedEventArgs e)
    {
        CompanionFace.PlayOperationFeedback(new CompanionEvent(CompanionEventKind.SettingsOpened));
        ((App)App.Current).OpenSettingsView();
    }

    private static CompanionDirection DirectionFromDelta(double x, double y)
    {
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

    private void ExitMenu_OnClick(object sender, RoutedEventArgs e)
    {
        ((App)App.Current).ExitApp();
    }
}
