using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PromptFloat.Services;
using PromptFloat.Views;
using PromptFloat.Models;

namespace PromptFloat.Views;

/// <summary>
/// 收缩态悬浮球（40×40 正圆）。
/// 可自由拖动；单击恢复主窗口；右键菜单：打开 / 设置 / 用户模型 / 退出。
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
        // 若尚未设定位置（启动时已设定），给一个默认右下角
        if (double.IsNaN(Left) || double.IsNaN(Top))
        {
            Left = SystemParameters.WorkArea.Width - Width - 20;
            Top = SystemParameters.WorkArea.Height - Height - 20;
        }
    }

    internal void ApplyDisplayPreferences(PromptFloat.Models.AppSettings settings)
    {
        settings.NormalizeDisplaySettings();
        // 精灵在收起和展开状态使用同一视觉尺寸，避免形变和命中区域跳动。
        Width = 60;
        Height = 60;
        Opacity = settings.FloatingBallOpacity;
    }

    /// <summary>判断拖拽的位移阈值（像素）。超过该值视为“拖拽”而非“单击”。</summary>
    private const double DragThreshold = 3.0;

    /// <summary>
    /// 精灵本身就是拖动入口。DragMove 返回时鼠标已经释放，因此在同一条控制流中
    /// 完成“拖动 / 点击”判定，避免 MouseUp 被系统窗口移动循环吞掉。
    /// </summary>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var dragStart = new Point(Left, Top);
        CompanionFace.PlayDragStartFeedback();
        var app = (App)App.Current;
        app.BeginFloatingBallDrag();
        try { DragMove(); }
        catch (InvalidOperationException) { }

        var moved = Math.Abs(Left - dragStart.X) > DragThreshold ||
                    Math.Abs(Top - dragStart.Y) > DragThreshold;
        app.CompleteFloatingBallDrag(
            new Point(Left, Top), new Size(ActualWidth, ActualHeight),
            WindowPlacementService.GetCurrentWorkAreaDip(this));
        if (moved) CompanionFace.PlayDragEndFeedback();
        else CompanionFace.PlayExpandFeedbackThen(app.ShowMainWindow);
        e.Handled = true;
    }

    /// <summary>窗口关闭前记忆位置。</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        App.Settings.BallLeft = Left;
        App.Settings.BallTop = Top;
        base.OnClosing(e);
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (IsVisible)
        {
            App.Settings.BallLeft = Left;
            App.Settings.BallTop = Top;
        }
    }

    private void OpenMenu_OnClick(object sender, RoutedEventArgs e)
    {
        ((App)App.Current).ShowMainWindow();
    }

    private void SettingsMenu_OnClick(object sender, RoutedEventArgs e)
    {
        ((App)App.Current).OpenSettingsView();
    }

    private void ExitMenu_OnClick(object sender, RoutedEventArgs e)
    {
        ((App)App.Current).ExitApp();
    }
}
