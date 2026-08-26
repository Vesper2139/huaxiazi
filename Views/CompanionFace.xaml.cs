using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Huaxiazi.Models;
using Huaxiazi.Services;

namespace Huaxiazi.Views;

public partial class CompanionFace : UserControl
{
    internal readonly record struct GazeTarget(double X, double Y, double Rotation);

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(CompanionVisualState), typeof(CompanionFace),
        new PropertyMetadata(CompanionVisualState.Idle, OnStateChanged));

    private readonly DispatcherTimer _idleGestureTimer = new() { Interval = TimeSpan.FromSeconds(6.5) };
    private readonly DispatcherTimer _idleVariantResetTimer = new() { Interval = TimeSpan.FromMilliseconds(1250) };
    private readonly Random _idleRandom = new();
    private int _expressionTransitionVersion;
    private CompanionIdleBehavior? _activeIdleBehavior;
    private string? _loadedSpriteSkin;
    private string? _loadedVectorSkin;
    private readonly CompanionPoseController _poseController = new(new SystemRandomSource(), new StopwatchAnimationClock());
    private IDisposable? _frameSubscription;

    public CompanionVisualState State
    {
        get => (CompanionVisualState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public CompanionFace()
    {
        InitializeComponent();
        _idleVariantResetTimer.Tick += (_, _) =>
        {
            _idleVariantResetTimer.Stop();
            _idleVariantResetTimer.Interval = TimeSpan.FromMilliseconds(1250);
            if (State == CompanionVisualState.Idle) CommitExpression(CompanionVisualState.Idle, animateMotion: false);
        };
        Loaded += (_, _) => StartMotion();
        Unloaded += (_, _) =>
        {
            StopMotion();
            // 窗口隐藏/回收时复位表情，避免下次复用时停在 Dragging/Expanding 中间态
            SetCurrentValue(StateProperty, CompanionVisualState.Idle);
        };
        ApplyExpression(CompanionVisualState.Idle, animate: false);
    }

    public void PlayPressFeedback()
    {
        PlayOperationFeedback(new CompanionEvent(CompanionEventKind.Pressed, BaseState: State));
    }

    /// <summary>
    /// 立即把精灵表情复位到空闲态（用于单击/未拖动等不会进入拖拽结束动画的路径，
    /// 避免表情卡在 Dragging/Expanding 等中间状态）。
    /// </summary>
    public void ReturnToIdle()
    {
        SetCurrentValue(StateProperty, CompanionVisualState.Idle);
    }

    /// <summary>
    /// 悬停（非点击）反馈：仅在空闲时切到好奇表情 + 轻微歪头/呼吸，体现"看到你"。
    /// 若精灵正忙于生成/报错等操作状态，则悬停不覆盖。
    /// </summary>
    public void PlayHoverFeedback()
    {
        if (State is not (CompanionVisualState.Idle or CompanionVisualState.Sleeping or CompanionVisualState.Happy)) return;
        if (!Huaxiazi.Services.VectorAnimationController.CanInterrupt(State, CompanionVisualState.Curious)) return;
        PlayOperationFeedback(new CompanionEvent(CompanionEventKind.HoverStarted, BaseState: State));
        SetCurrentValue(StateProperty, CompanionVisualState.Curious);
    }

    /// <summary>
    /// 悬停结束：若有绑定（主窗口精灵由 VM 驱动）清除本地覆盖让 VM 接管，否则回到空闲。
    /// </summary>
    public void PlayHoverEndFeedback()
    {
        if (State != CompanionVisualState.Curious) return;
        PlayOperationFeedback(new CompanionEvent(CompanionEventKind.HoverEnded, BaseState: CompanionVisualState.Idle));
        if (GetBindingExpression(StateProperty) is not null) ClearValue(StateProperty);
        else SetCurrentValue(StateProperty, CompanionVisualState.Idle);
    }

    public void PlayClickFeedback()
    {
        PlayOperationFeedback(new CompanionEvent(CompanionEventKind.Released, BaseState: State));
        if (State is CompanionVisualState.Idle or CompanionVisualState.Curious or CompanionVisualState.Sleeping or CompanionVisualState.Happy)
            PlayVisualOnlyExpression(CompanionVisualState.Happy, TimeSpan.FromMilliseconds(280));
    }

    public void PlayClickFeedbackThen(Action completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        PlayClickFeedback();
        if (!App.Settings.AnimationsEnabled)
        {
            completed();
            return;
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(230) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            completed();
        };
        timer.Start();
    }

    public void PlayDragStartFeedback()
    {
        _poseController.SetBaseState(State);
        PlayOperationFeedback(new CompanionEvent(CompanionEventKind.DragStarted, BaseState: State));
        SetCurrentValue(StateProperty, CompanionVisualState.Dragging);
        // DragMove enters a nested native move loop immediately after this method.
        // Commit the focused face synchronously so it is visible during the drag,
        // while ordinary state changes continue to use the softer blink transition.
        _expressionTransitionVersion++;
        SkinSpriteHost.BeginAnimation(OpacityProperty, null);
        SkinSpriteHost.Opacity = 1;
        BlinkScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        BlinkScale.ScaleY = 1;
        CommitExpression(CompanionVisualState.Dragging, animateMotion: App.Settings.AnimationsEnabled);
    }

    /// <summary>拖动过程中按屏幕位移更新八向姿态；角色保持完全不透明。</summary>
    public void PlayDragDirection(double deltaX, double deltaY)
    {
        if (State != CompanionVisualState.Dragging) return;
        SkinSpriteHost.BeginAnimation(OpacityProperty, null);
        SkinSpriteHost.Opacity = 1;
        var length = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
        if (length < 1) return;
        PlayOperationFeedback(new CompanionEvent(
            CompanionEventKind.DragMoved,
            Direction: DirectionFromDelta(deltaX, deltaY),
            BaseState: _poseController.BaseState));
        var nx = Math.Clamp(deltaX / 80d, -1d, 1d);
        var ny = Math.Clamp(deltaY / 80d, -1d, 1d);
        BodyScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        BodyScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        BodyOffset.BeginAnimation(TranslateTransform.XProperty, null);
        BodyOffset.BeginAnimation(TranslateTransform.YProperty, null);
        ExpressionRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        BodyScale.ScaleX = 1 + Math.Abs(nx) * 0.12;
        BodyScale.ScaleY = 1 + Math.Abs(ny) * 0.08;
        BodyOffset.X = nx * 1.6;
        BodyOffset.Y = ny * 1.6;
        ExpressionRotation.Angle = nx * 4 - ny * 2;
    }

    public void PlayDragEndFeedback(CompanionVisualState restingState = CompanionVisualState.Idle)
    {
        PlayOperationFeedback(new CompanionEvent(CompanionEventKind.DragEnded, BaseState: restingState));
        SetCurrentValue(StateProperty, restingState);
        SkinSpriteHost.BeginAnimation(OpacityProperty, null);
        SkinSpriteHost.Opacity = 1;
        if (!App.Settings.AnimationsEnabled)
        {
            BodyScale.ScaleX = BodyScale.ScaleY = 1;
            BodyOffset.X = BodyOffset.Y = 0;
            ExpressionRotation.Angle = 0;
            return;
        }
    }

    public void PlayExpandFeedbackThen(Action completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        PlayOperationFeedback(new CompanionEvent(CompanionEventKind.Expanding, BaseState: State));
        SetCurrentValue(StateProperty, CompanionVisualState.Expanding);
        if (!App.Settings.AnimationsEnabled)
        {
            SetCurrentValue(StateProperty, CompanionVisualState.Idle);
            completed();
            return;
        }

        // 版本守卫：期间若状态被其它路径改写（再次点击/拖动/卸载），不再覆盖为 Happy
        var version = ++_expressionTransitionVersion;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(185) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (version != _expressionTransitionVersion || !IsLoaded) return;
            SetCurrentValue(StateProperty, CompanionVisualState.Happy);
            completed();
        };
        timer.Start();
    }

    private static void OnStateChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is CompanionFace face)
        {
            face._activeIdleBehavior = null;
            if ((CompanionVisualState)args.NewValue is not (CompanionVisualState.Dragging or CompanionVisualState.Expanding))
                face._poseController.SetBaseState((CompanionVisualState)args.NewValue);
            face.ApplyExpression((CompanionVisualState)args.NewValue, animate: true);
        }
    }

    public void PlayOperationFeedback(CompanionEvent companionEvent)
    {
        _poseController.Apply(companionEvent with { BaseState = companionEvent.BaseState });
    }

    private void StartMotion()
    {
        App.SkinService.SkinChanged -= SkinServiceOnSkinChanged;
        App.SkinService.SkinChanged += SkinServiceOnSkinChanged;
        _idleGestureTimer.Tick -= IdleGestureTimerOnTick;
        _idleGestureTimer.Tick += IdleGestureTimerOnTick;
        // 默认角色的凝视、眨眼与呼吸全部由共享 WPF 帧时钟合成。
        // 图片皮肤仍可低频切换它们自己的待机帧，但不再各自创建高频计时器。
        if (SkinSpriteHost.Visibility == Visibility.Visible) _idleGestureTimer.Start();
        _frameSubscription?.Dispose();
        _frameSubscription = null;
        // A loaded event can be raised by a designer/test without a presentation source.
        // Subscribing such a control would retain an HWND/Dispatcher that never owned it.
        var applicationDispatcher = Application.Current?.Dispatcher;
        if (PresentationSource.FromVisual(this) is not null &&
            (applicationDispatcher is null || ReferenceEquals(applicationDispatcher, Dispatcher)))
        {
            _frameSubscription = CompanionFrameClock.Subscribe(AdvancePose);
        }
    }

    private void StopMotion()
    {
        App.SkinService.SkinChanged -= SkinServiceOnSkinChanged;
        _idleGestureTimer.Stop();
        _idleVariantResetTimer.Stop();
        _frameSubscription?.Dispose();
        _frameSubscription = null;
    }

    private void AdvancePose(TimeSpan elapsed)
    {
        if (!IsVisible) return;
        if (PresentationSource.FromVisual(this) is not null && GetCursorPos(out var cursor))
        {
            var center = PointToScreen(new Point(ActualWidth / 2, ActualHeight / 2));
            var gaze = State is CompanionVisualState.Sleeping or CompanionVisualState.Error or CompanionVisualState.Dragging or CompanionVisualState.Expanding
                ? new GazeTarget(0, 0, 0)
                : CalculateGazeTarget(cursor.X - center.X, cursor.Y - center.Y);
            _poseController.SetGaze(gaze.X, gaze.Y);
        }
        if (State is not (CompanionVisualState.Dragging or CompanionVisualState.Expanding))
            _poseController.SetBaseState(State);
        var systemReduceMotion = SystemParameters.HighContrast || !SystemParameters.ClientAreaAnimation;
        var reduceMotion = !App.Settings.AnimationsEnabled || systemReduceMotion;
        var pose = _poseController.Advance(elapsed, App.Settings.AnimationsEnabled && !systemReduceMotion, reduceMotion);
        ApplyRenderedPose(pose);
    }

    private void ApplyRenderedPose(CompanionPose pose)
    {
        BodyScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        BodyScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        BodyOffset.BeginAnimation(TranslateTransform.XProperty, null);
        BodyOffset.BeginAnimation(TranslateTransform.YProperty, null);
        ExpressionRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        BlinkScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        BlinkScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        BodyScale.ScaleX = pose.ScaleX;
        BodyScale.ScaleY = pose.ScaleY;
        BodyOffset.X = pose.OffsetX;
        BodyOffset.Y = pose.OffsetY;
        ExpressionRotation.Angle = pose.Rotation;
        BlinkScale.ScaleX = Math.Clamp(pose.EyeScale, 0.85, 1.2);
        BlinkScale.ScaleY = Math.Clamp(pose.EyeOpen * pose.EyeScale, 0.06, 1.2);
        ApplyFaceProjection(pose);
        if (_poseController.BaseState == CompanionVisualState.Idle && _poseController.ActiveIdleBehavior != _activeIdleBehavior)
        {
            _activeIdleBehavior = _poseController.ActiveIdleBehavior;
            if (_activeIdleBehavior is { } idleBehavior)
                ApplyIdleBehaviorVisual(idleBehavior);
            else
                CommitExpression(CompanionVisualState.Idle, animateMotion: false);
        }
        SkinSpriteGaze.X = pose.GazeX * 0.72;
        SkinSpriteGaze.Y = pose.GazeY * 0.72;
        VectorSpriteHost.ApplyGaze(pose.GazeX, pose.GazeY);
        if (_poseController.BaseState == CompanionVisualState.Idle)
        {
            Mouth.Data = pose.MouthCurve > 0.08
                ? Geometry.Parse(FormattableString.Invariant($"M18 28 Q22 {28 + pose.MouthCurve * 3.5:0.##} 26 28"))
                : Geometry.Parse("M19.5 28 L24.5 28");
        }
        SkinSpriteHost.Opacity = 1;
    }

    private void ApplyFaceProjection(CompanionPose pose)
    {
        var projection = CompanionFaceKinematics.Project(pose);
        OrbGazeOffset.X = projection.OrbX;
        OrbGazeOffset.Y = projection.OrbY;
        GazeRotation.Angle = projection.OrbRotation;
        HeadOffset.X = projection.HeadX;
        HeadOffset.Y = projection.HeadY;
        HeadRotation.Angle = projection.HeadRotation;
        GazeOffset.X = projection.EyeX;
        GazeOffset.Y = projection.EyeY;
        MouthPoseOffset.X = projection.MouthX;
        MouthPoseOffset.Y = projection.MouthY;
        MouthScale.ScaleX = projection.MouthScaleX;
        MouthScale.ScaleY = projection.MouthScaleY;
        MouthRotation.Angle = Math.Clamp(pose.GazeX * 0.035, -0.28, 0.28);
    }

    private static CompanionDirection DirectionFromDelta(double x, double y)
    {
        if (Math.Abs(x) < 1 && Math.Abs(y) < 1) return CompanionDirection.None;
        var angle = Math.Atan2(y, x) * 180 / Math.PI;
        return angle switch
        {
            >= -22.5 and < 22.5 => CompanionDirection.East,
            >= 22.5 and < 67.5 => CompanionDirection.SouthEast,
            >= 67.5 and < 112.5 => CompanionDirection.South,
            >= 112.5 and < 157.5 => CompanionDirection.SouthWest,
            >= 157.5 or < -157.5 => CompanionDirection.West,
            >= -157.5 and < -112.5 => CompanionDirection.NorthWest,
            >= -112.5 and < -67.5 => CompanionDirection.North,
            _ => CompanionDirection.NorthEast
        };
    }

    private void SkinServiceOnSkinChanged(object? sender, string skinId) =>
        Dispatcher.BeginInvoke(new Action(() => CommitExpression(State, animateMotion: false)));

    /// <summary>
    /// 空闲生命力：光标远离且精灵空闲时，周期性做轻微"呼吸" + 歪头，
    /// 让悬浮球不点击也有生动的动态。
    /// </summary>
    private void IdleGestureTimerOnTick(object? sender, EventArgs e)
    {
        if (!IsVisible || !App.Settings.AnimationsEnabled || State != CompanionVisualState.Idle) return;
        if (!GetCursorPos(out var cursor)) return;
        var center = PointToScreen(new Point(ActualWidth / 2, ActualHeight / 2));
        var dx = cursor.X - center.X;
        var dy = cursor.Y - center.Y;
        if (Math.Sqrt((dx * dx) + (dy * dy)) < 380) return; // 光标在附近时不触发空闲手势

        // 低频微表情只作用于 Idle，不改写业务状态，也不会覆盖生成/错误/拖动等状态。
        ApplyIdleVariant(_idleRandom.Next(4));
        _idleVariantResetTimer.Stop();
        _idleVariantResetTimer.Start();
    }

    /// <summary>
    /// 将共享姿态调度器的微表情投影到皮肤自己的状态帧；不改写业务 State。
    /// </summary>
    private void ApplyIdleBehaviorVisual(CompanionIdleBehavior behavior)
    {
        if (State != CompanionVisualState.Idle) return;
        var skinId = Application.Current?.TryFindResource("ThemeId") as string ?? "";
        var visualState = behavior switch
        {
            CompanionIdleBehavior.SoftSmile => CompanionVisualState.Happy,
            CompanionIdleBehavior.Squint or CompanionIdleBehavior.Drowsy => CompanionVisualState.Sleeping,
            CompanionIdleBehavior.CuriousLook or CompanionIdleBehavior.Glance or CompanionIdleBehavior.LookAround => skinId == "MaoDie"
                ? CompanionVisualState.Listening
                : CompanionVisualState.Curious,
            CompanionIdleBehavior.Refocus => CompanionVisualState.Listening,
            CompanionIdleBehavior.GentleSway => CompanionVisualState.Happy,
            _ => CompanionVisualState.Idle
        };
        if (visualState == CompanionVisualState.Idle) return;
        ApplyExpression(visualState, animate: App.Settings.AnimationsEnabled);
        _idleVariantResetTimer.Stop();
        _idleVariantResetTimer.Start();
    }

    private void PlayVisualOnlyExpression(CompanionVisualState visualState, TimeSpan duration)
    {
        _activeIdleBehavior = null;
        ApplyExpression(visualState, animate: App.Settings.AnimationsEnabled);
        _idleVariantResetTimer.Stop();
        _idleVariantResetTimer.Interval = duration;
        _idleVariantResetTimer.Start();
    }

    internal static GazeTarget CalculateGazeTarget(double dx, double dy)
    {
        var distance = Math.Sqrt((dx * dx) + (dy * dy));
        if (distance >= 560) return new GazeTarget(0, 0, 0);

        // 近距离明确跟随，360–560px 之间平滑衰减，避免旧 420px 阈值处瞬间归中。
        var visibility = distance <= 360 ? 1 : (560 - distance) / 200;
        var x = Math.Clamp(dx / 24, -8, 8) * visibility;
        var y = Math.Clamp(dy / 32, -5, 5) * visibility;
        return new GazeTarget(x, y, Math.Clamp(x, -8, 8));
    }

    private void EaseGazeTo(double targetX, double targetY, double? targetRotation = null)
    {
        _poseController.SetGaze(targetX, targetY);
        var gazeX = Math.Clamp(targetX, -8, 8);
        var gazeY = Math.Clamp(targetY, -5, 5);
        ApplyFaceProjection(new CompanionPose { GazeX = gazeX, GazeY = gazeY });
        // 位图角色不能依赖已隐藏的 FaceLayer；复用同一凝视向量，避免特殊皮肤
        // 变成静态头像。独立变换不会覆盖用于校准透明画布的 SkinSpriteOffset。
        SkinSpriteGaze.X = gazeX * 0.72;
        SkinSpriteGaze.Y = gazeY * 0.72;
        VectorSpriteHost.ApplyGaze(gazeX, gazeY);
        // Preserve the caller's optional emphasis without breaking the shared face rig.
        GazeRotation.Angle += (targetRotation ?? gazeX * 0.8) * 0.08;
    }

    private void ApplyExpression(CompanionVisualState state, bool animate)
    {
        if (animate && App.Settings.AnimationsEnabled)
        {
            BeginExpressionTransition(state);
            return;
        }

        _expressionTransitionVersion++;
        CommitExpression(state, animateMotion: false);
    }

    private void BeginExpressionTransition(CompanionVisualState state)
    {
        var version = ++_expressionTransitionVersion;
        if (VectorSpriteHost.Visibility == Visibility.Visible)
        {
            var fadeOut = new DoubleAnimation(VectorSpriteHost.Opacity, 0.18, TimeSpan.FromMilliseconds(80))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
                FillBehavior = FillBehavior.Stop
            };
            fadeOut.Completed += (_, _) =>
            {
                if (version != _expressionTransitionVersion) return;
                VectorSpriteHost.BeginAnimation(OpacityProperty, null);
                VectorSpriteHost.Opacity = 0.18;
                CommitExpression(state, animateMotion: true);
                VectorSpriteHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0.18, 1, TimeSpan.FromMilliseconds(135))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            };
            VectorSpriteHost.BeginAnimation(OpacityProperty, fadeOut);
            return;
        }
        if (SkinSpriteHost.Visibility == Visibility.Visible)
        {
            SkinSpriteTransitionScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            SkinSpriteTransitionScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            SkinSpriteTransitionScale.ScaleX = SkinSpriteTransitionScale.ScaleY = 1;
            var fadeOut = new DoubleAnimation(SkinSpriteHost.Opacity, 0.18, TimeSpan.FromMilliseconds(80))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
                FillBehavior = FillBehavior.Stop
            };
            var squeeze = new DoubleAnimation(1, 0.985, TimeSpan.FromMilliseconds(80))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
                FillBehavior = FillBehavior.Stop
            };
            fadeOut.Completed += (_, _) =>
            {
                if (version != _expressionTransitionVersion) return;
                SkinSpriteHost.BeginAnimation(OpacityProperty, null);
                SkinSpriteHost.Opacity = 0.18;
                CommitExpression(state, animateMotion: true);
                SkinSpriteHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0.18, 1, TimeSpan.FromMilliseconds(135))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
                SkinSpriteTransitionScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.985, 1, TimeSpan.FromMilliseconds(135))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
                SkinSpriteTransitionScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.985, 1, TimeSpan.FromMilliseconds(135))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            };
            SkinSpriteHost.BeginAnimation(OpacityProperty, fadeOut);
            SkinSpriteTransitionScale.BeginAnimation(ScaleTransform.ScaleXProperty, squeeze);
            SkinSpriteTransitionScale.BeginAnimation(ScaleTransform.ScaleYProperty, squeeze);
            return;
        }

        var close = new DoubleAnimation(BlinkScale.ScaleY, 0.08, TimeSpan.FromMilliseconds(70))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.Stop
        };
        close.Completed += (_, _) =>
        {
            if (version != _expressionTransitionVersion) return;
            BlinkScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            BlinkScale.ScaleY = 0.08;
            CommitExpression(state, animateMotion: true);
            BlinkScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.08, 1, TimeSpan.FromMilliseconds(125))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        };
        BlinkScale.BeginAnimation(ScaleTransform.ScaleYProperty, close);
    }

    private void CommitExpression(CompanionVisualState state, bool animateMotion)
    {
        ApplySkinVisual(state);
        var expression = state switch
        {
            CompanionVisualState.Sleeping => ("M11.5 20 Q14.5 23 17.5 20", "M26.5 20 Q29.5 23 32.5 20", "M19.5 28 L24.5 28"),
            CompanionVisualState.Happy => ("M11.5 20 Q14.5 16 17.5 20", "M26.5 20 Q29.5 16 32.5 20", "M18 27 Q22 31 26 27"),
            CompanionVisualState.Curious => ("M14.5 17.5 L14.5 22.5", "M26.5 20 Q29.5 16 32.5 20", "M20.4 28 A1.6 1.6 0 1 0 23.6 28 A1.6 1.6 0 1 0 20.4 28"),
            CompanionVisualState.Thinking => ("M11.5 20 L17.5 20", "M26.5 20 L32.5 20", "M19.5 28 L24.5 28"),
            CompanionVisualState.Listening => ("M14.5 17.5 L14.5 22.5", "M29.5 17.5 L29.5 22.5", "M19.5 28 L24.5 28"),
            CompanionVisualState.Working => ("M12 20 L17 20", "M27 20 L32 20", "M19.5 28 L24.5 28"),
            CompanionVisualState.Surprised => ("M14.5 17.5 L14.5 22.5", "M29.5 17.5 L29.5 22.5", "M19.5 28 A2.5 3 0 1 0 24.5 28 A2.5 3 0 1 0 19.5 28"),
            CompanionVisualState.Warning => ("M14.5 17.5 L14.5 22.5", "M26.5 20 Q29.5 16 32.5 20", "M18 29 Q20 26.5 22 29 Q24 31.5 26 29"),
            CompanionVisualState.Error => ("M11.5 17 L17.5 23 M17.5 17 L11.5 23", "M26.5 17 L32.5 23 M32.5 17 L26.5 23", "M18 29 Q20 26.5 22 29 Q24 31.5 26 29"),
            CompanionVisualState.Dragging => ("M12 20 L17 20", "M27 20 L32 20", "M18 28 Q22 25 26 28"),
            CompanionVisualState.Expanding => ("M11.5 20 Q14.5 16 17.5 20", "M26.5 20 Q29.5 16 32.5 20", "M20 28 A2 2.4 0 1 0 24 28 A2 2.4 0 1 0 20 28"),
            _ => ("M14.5 17.5 L14.5 22.5", "M29.5 17.5 L29.5 22.5", "M19.5 28 L24.5 28")
        };

        LeftEye.Data = Geometry.Parse(expression.Item1);
        RightEye.Data = Geometry.Parse(expression.Item2);
        Mouth.Data = Geometry.Parse(expression.Item3);
        var showRing = state is CompanionVisualState.Thinking or CompanionVisualState.Working;
        ProgressRing.Opacity = showRing ? 0.78 : 0;
        LeftSignal.Opacity = RightSignal.Opacity = state == CompanionVisualState.Listening ? 0.8 : 0;

        RingRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        ExpressionRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        if (showRing && App.Settings.AnimationsEnabled)
        {
            RingRotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.4))
            {
                RepeatBehavior = RepeatBehavior.Forever
            });
        }

        // Body transforms are exclusively rendered by CompanionPoseController. Starting
        // independent WPF animations here would be cleared by the next shared frame.
    }

    private void ApplySkinVisual(CompanionVisualState state)
    {
        var skinId = Application.Current?.TryFindResource("ThemeId") as string ?? "";
        var manifest = App.SkinService.GetSkin(skinId);
        if (manifest?.CompanionProfile.UsesSpriteSheet == true)
        {
            VectorSpriteHost.Visibility = Visibility.Collapsed;
            FaceLayer.Visibility = Visibility.Collapsed;
            ProgressRing.Visibility = Visibility.Collapsed;
            LeftSignal.Visibility = RightSignal.Visibility = Visibility.Collapsed;
            CompanionSurface.Visibility = Visibility.Collapsed;
            SkinSpriteHost.Visibility = Visibility.Visible;
            SkinSpriteScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            SkinSpriteScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            SkinSpriteScale.ScaleX = SkinSpriteScale.ScaleY = 1;
            SkinSpriteOffset.BeginAnimation(TranslateTransform.YProperty, null);
            SkinSpriteOffset.Y = 0;
            var sheetPath = manifest.CompanionProfile.SpriteSheetPath;
            if (string.IsNullOrWhiteSpace(sheetPath)) return;
            var sheetSourcePath = manifest.IsBuiltIn
                ? $"pack://application:,,,/Huaxiazi;component/{sheetPath.TrimStart('/')}"
                : ResolveExternalSkinPath(manifest, sheetPath);
            if (string.IsNullOrWhiteSpace(sheetSourcePath)) return;
            var sheetCacheKey = $"{skinId}:sheet:{sheetSourcePath}";
            if (!string.Equals(_loadedSpriteSkin, sheetCacheKey, StringComparison.Ordinal))
            {
                SkinSpriteBrush.ImageSource = new System.Windows.Media.Imaging.BitmapImage(new Uri(sheetSourcePath, UriKind.Absolute));
                _loadedSpriteSkin = sheetCacheKey;
            }
            var columns = manifest.CompanionProfile.SpriteSheetColumns;
            var rows = manifest.CompanionProfile.SpriteSheetRows;
            // 罗小黑拖动使用无速度线的中性首帧，方向感由拖动变换提供。
            var frameState = (skinId, state) switch
            {
                // 耄耋对陌生指针的第一反应是露牙哈气，而不是温顺地抬头好奇。
                ("MaoDie", CompanionVisualState.Curious) => CompanionVisualState.Warning,
                // 罗小黑拖动使用无速度线的中性首帧，方向感由拖动变换提供。
                ("LuoXiaoHei", CompanionVisualState.Dragging) => CompanionVisualState.Idle,
                _ => state
            };
            var index = Math.Clamp((int)frameState, 0, columns * rows - 1);
            var col = index % columns;
            var row = index / columns;
            SkinSpriteBrush.ViewboxUnits = BrushMappingMode.RelativeToBoundingBox;
            SkinSpriteBrush.Viewbox = new Rect((double)col / columns, (double)row / rows, 1d / columns, 1d / rows);
            return;
        }
        CompanionSurface.Visibility = Visibility.Visible;
        if (manifest?.CompanionProfile.UsesVectorLayers == true)
        {
            SkinSpriteHost.Visibility = Visibility.Collapsed;
            FaceLayer.Visibility = Visibility.Collapsed;
            ProgressRing.Visibility = Visibility.Collapsed;
            LeftSignal.Visibility = RightSignal.Visibility = Visibility.Collapsed;
            VectorSpriteHost.Visibility = Visibility.Visible;
            if (!string.Equals(_loadedVectorSkin, skinId, StringComparison.Ordinal))
            {
                using var stream = LoadVectorResource(manifest);
                VectorSpriteHost.Load(Huaxiazi.Services.VectorCharacterManifest.Parse(stream));
                _loadedVectorSkin = skinId;
            }
            VectorSpriteHost.ApplyState(state, animate: App.Settings.AnimationsEnabled);
            VectorSpriteHost.ApplyGaze(0, 0);
            return;
        }

        VectorSpriteHost.Visibility = Visibility.Collapsed;
        string? sourcePath = null;
        string? externalStatePath = null;
        if (manifest is not null && manifest.CompanionProfile.UsesImages &&
            manifest.CompanionProfile.TryGetAsset(state, out var relativePath))
        {
            if (manifest.IsBuiltIn)
                sourcePath = $"pack://application:,,,/Huaxiazi;component/{relativePath.TrimStart('/')}";
            else if (manifest.InstallPath is not null)
            {
                var installRoot = System.IO.Path.GetFullPath(manifest.InstallPath)
                    .TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
                var candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(installRoot, relativePath));
                if (candidate.StartsWith(installRoot, StringComparison.OrdinalIgnoreCase))
                    externalStatePath = candidate;
            }
        }
        if (externalStatePath is not null) sourcePath = externalStatePath;
        var raster = sourcePath is not null;
        SkinSpriteHost.Visibility = raster ? Visibility.Visible : Visibility.Collapsed;
        FaceLayer.Visibility = raster ? Visibility.Collapsed : Visibility.Visible;
        ProgressRing.Visibility = raster ? Visibility.Collapsed : Visibility.Visible;
        LeftSignal.Visibility = RightSignal.Visibility = raster ? Visibility.Collapsed : Visibility.Visible;
        if (!raster) return;

        // 位图角色的每个状态都是独立资源。缓存键必须包含状态，否则拖动、展开、
        // 错误等交互只会一直显示首次载入的待机图。
        var imageCacheKey = skinId + ":" + state;
        if (!string.Equals(_loadedSpriteSkin, imageCacheKey, StringComparison.Ordinal))
        {
            SkinSpriteBrush.ImageSource = new System.Windows.Media.Imaging.BitmapImage(new Uri(sourcePath!, UriKind.Absolute));
            _loadedSpriteSkin = imageCacheKey;
        }
        SkinSpriteBrush.ViewboxUnits = BrushMappingMode.RelativeToBoundingBox;
        SkinSpriteBrush.Viewbox = new Rect(0, 0, 1, 1);
    }

    private void ApplyIdleVariant(int variant)
    {
        var skinId = Application.Current?.TryFindResource("ThemeId") as string ?? "";
        var manifest = App.SkinService.GetSkin(skinId);
        var profile = manifest?.CompanionProfile;
        if (State != CompanionVisualState.Idle || profile?.UsesSpriteSheet != true || string.IsNullOrWhiteSpace(profile.IdleVariantsPath)) return;
        var path = manifest!.IsBuiltIn
            ? $"pack://application:,,,/Huaxiazi;component/{profile.IdleVariantsPath.TrimStart('/')}"
            : ResolveExternalSkinPath(manifest, profile.IdleVariantsPath);
        if (string.IsNullOrWhiteSpace(path)) return;
        var cacheKey = $"{skinId}:idle-variants:{path}";
        if (!string.Equals(_loadedSpriteSkin, cacheKey, StringComparison.Ordinal))
        {
            SkinSpriteBrush.ImageSource = new System.Windows.Media.Imaging.BitmapImage(new Uri(path, UriKind.Absolute));
            _loadedSpriteSkin = cacheKey;
        }
        var columns = profile.IdleVariantsColumns;
        var col = Math.Clamp(variant, 0, columns - 1);
        SkinSpriteBrush.ViewboxUnits = BrushMappingMode.RelativeToBoundingBox;
        SkinSpriteBrush.Viewbox = new Rect((double)col / columns, 0, 1d / columns, 1);
    }

    private static string? ResolveExternalSkinPath(Huaxiazi.Services.SkinManifest manifest, string relativePath)
    {
        if (manifest.InstallPath is null) return null;
        var installRoot = System.IO.Path.GetFullPath(manifest.InstallPath).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        var candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(installRoot, relativePath));
        return candidate.StartsWith(installRoot, StringComparison.OrdinalIgnoreCase) ? candidate : null;
    }

    private static Stream LoadVectorResource(Huaxiazi.Services.SkinManifest manifest)
    {
        var relativePath = manifest.CompanionVectorPath;
        if (string.IsNullOrWhiteSpace(relativePath)) throw new InvalidDataException("矢量角色资源路径为空。");
        if (!manifest.IsBuiltIn && !string.IsNullOrWhiteSpace(manifest.InstallPath))
        {
            var root = Path.GetFullPath(manifest.InstallPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var external = Path.GetFullPath(Path.Combine(root, relativePath));
            if (!external.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("矢量角色路径越界。");
            if (!File.Exists(external)) throw new InvalidDataException($"矢量角色资源不存在：{relativePath}");
            return File.OpenRead(external);
        }
        var filePath = Path.Combine(AppContext.BaseDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(filePath)) return File.OpenRead(filePath);
        var resource = Application.GetResourceStream(new Uri($"/Huaxiazi;component/{relativePath.TrimStart('/')}", UriKind.Relative));
        if (resource is null) throw new InvalidDataException($"矢量角色资源不存在：{relativePath}");
        return resource.Stream;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
