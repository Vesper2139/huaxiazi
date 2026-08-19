using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PromptFloat.Models;

namespace PromptFloat.Views;

public partial class CompanionFace : UserControl
{
    internal readonly record struct GazeTarget(double X, double Y, double Rotation);

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(CompanionVisualState), typeof(CompanionFace),
        new PropertyMetadata(CompanionVisualState.Idle, OnStateChanged));

    private readonly DispatcherTimer _gazeTimer = new() { Interval = TimeSpan.FromMilliseconds(34) };
    private readonly DispatcherTimer _blinkTimer = new() { Interval = TimeSpan.FromSeconds(4.6) };
    private double _gazeX;
    private double _gazeY;
    private double _gazeAngle;
    private int _expressionTransitionVersion;

    public CompanionVisualState State
    {
        get => (CompanionVisualState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public CompanionFace()
    {
        InitializeComponent();
        Loaded += (_, _) => StartMotion();
        Unloaded += (_, _) => StopMotion();
        ApplyExpression(CompanionVisualState.Idle, animate: false);
    }

    public void PlayPressFeedback()
    {
        if (!App.Settings.AnimationsEnabled) return;
        BodyScale.BeginAnimation(ScaleTransform.ScaleXProperty, Pulse(1, 0.92, 1, 170));
        BodyScale.BeginAnimation(ScaleTransform.ScaleYProperty, Pulse(1, 1.08, 1, 170));
    }

    public void PlayClickFeedback()
    {
        if (!App.Settings.AnimationsEnabled) return;
        BodyScale.BeginAnimation(ScaleTransform.ScaleXProperty, Pulse(1, 1.12, 1, 260));
        BodyScale.BeginAnimation(ScaleTransform.ScaleYProperty, Pulse(1, 0.88, 1, 260));
        BlinkScale.BeginAnimation(ScaleTransform.ScaleYProperty, Pulse(1, 0.08, 1, 230));
        BodyOffset.BeginAnimation(TranslateTransform.YProperty, Pulse(0, -3.2, 0, 280));
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
        SetCurrentValue(StateProperty, CompanionVisualState.Dragging);
        // DragMove enters a nested native move loop immediately after this method.
        // Commit the focused face synchronously so it is visible during the drag,
        // while ordinary state changes continue to use the softer blink transition.
        _expressionTransitionVersion++;
        BlinkScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        BlinkScale.ScaleY = 1;
        CommitExpression(CompanionVisualState.Dragging, animateMotion: App.Settings.AnimationsEnabled);
        if (!App.Settings.AnimationsEnabled) return;
        BodyScale.BeginAnimation(ScaleTransform.ScaleXProperty, Pulse(1, 0.94, 1, 180));
        BodyScale.BeginAnimation(ScaleTransform.ScaleYProperty, Pulse(1, 1.06, 1, 180));
        BodyOffset.BeginAnimation(TranslateTransform.YProperty, Pulse(0, 1.2, 0, 180));
    }

    public void PlayDragEndFeedback(CompanionVisualState restingState = CompanionVisualState.Idle)
    {
        SetCurrentValue(StateProperty, restingState);
        if (!App.Settings.AnimationsEnabled) return;
        BodyScale.BeginAnimation(ScaleTransform.ScaleXProperty, Pulse(1, 1.05, 1, 210));
        BodyScale.BeginAnimation(ScaleTransform.ScaleYProperty, Pulse(1, 0.95, 1, 210));
    }

    public void PlayExpandFeedbackThen(Action completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        SetCurrentValue(StateProperty, CompanionVisualState.Expanding);
        if (!App.Settings.AnimationsEnabled)
        {
            completed();
            return;
        }

        BodyScale.BeginAnimation(ScaleTransform.ScaleXProperty, Pulse(1, 0.84, 1.04, 220));
        BodyScale.BeginAnimation(ScaleTransform.ScaleYProperty, Pulse(1, 1.12, 0.97, 220));
        ExpressionRotation.BeginAnimation(RotateTransform.AngleProperty, Pulse(0, -5, 0, 220));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(185) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            SetCurrentValue(StateProperty, CompanionVisualState.Happy);
            completed();
        };
        timer.Start();
    }

    private static void OnStateChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is CompanionFace face)
        {
            face.ApplyExpression((CompanionVisualState)args.NewValue, animate: true);
        }
    }

    private void StartMotion()
    {
        _gazeTimer.Tick -= GazeTimerOnTick;
        _gazeTimer.Tick += GazeTimerOnTick;
        _blinkTimer.Tick -= BlinkTimerOnTick;
        _blinkTimer.Tick += BlinkTimerOnTick;
        _gazeTimer.Start();
        _blinkTimer.Start();
    }

    private void StopMotion()
    {
        _gazeTimer.Stop();
        _blinkTimer.Stop();
    }

    private void GazeTimerOnTick(object? sender, EventArgs e)
    {
        if (!IsVisible || !App.Settings.AnimationsEnabled || State is CompanionVisualState.Sleeping or CompanionVisualState.Error or CompanionVisualState.Dragging or CompanionVisualState.Expanding)
        {
            EaseGazeTo(0, 0);
            return;
        }

        if (!GetCursorPos(out var cursor)) return;
        var center = PointToScreen(new Point(ActualWidth / 2, ActualHeight / 2));
        var dx = cursor.X - center.X;
        var dy = cursor.Y - center.Y;
        var target = CalculateGazeTarget(dx, dy);
        EaseGazeTo(target.X, target.Y, target.Rotation);
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
        _gazeX += (targetX - _gazeX) * 0.28;
        _gazeY += (targetY - _gazeY) * 0.28;
        var rotation = targetRotation ?? Math.Clamp(targetX * 0.8, -4, 4);
        _gazeAngle += (rotation - _gazeAngle) * 0.24;
        GazeOffset.X = _gazeX;
        GazeOffset.Y = _gazeY;
        GazeRotation.Angle = _gazeAngle;
    }

    private void BlinkTimerOnTick(object? sender, EventArgs e)
    {
        if (!App.Settings.AnimationsEnabled || State is not (CompanionVisualState.Idle or CompanionVisualState.Curious)) return;
        BlinkScale.BeginAnimation(ScaleTransform.ScaleYProperty, Pulse(1, 0.06, 1, 250));
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

        if (!animateMotion || !App.Settings.AnimationsEnabled) return;
        switch (state)
        {
            case CompanionVisualState.Happy:
                BodyOffset.BeginAnimation(TranslateTransform.YProperty, Pulse(0, -2.5, 0, 360));
                break;
            case CompanionVisualState.Curious:
            case CompanionVisualState.Thinking:
                ExpressionRotation.BeginAnimation(RotateTransform.AngleProperty, Pulse(0, -5, 0, 420));
                break;
            case CompanionVisualState.Error:
            case CompanionVisualState.Warning:
                BodyOffset.BeginAnimation(TranslateTransform.XProperty, Pulse(0, -2, 0, 280));
                break;
            case CompanionVisualState.Dragging:
                BodyOffset.BeginAnimation(TranslateTransform.YProperty, Pulse(0, 1.1, 0, 200));
                break;
            case CompanionVisualState.Expanding:
                ExpressionRotation.BeginAnimation(RotateTransform.AngleProperty, Pulse(0, -4, 0, 260));
                break;
        }
    }

    private static DoubleAnimationUsingKeyFrames Pulse(double start, double middle, double end, int milliseconds)
    {
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        var animation = new DoubleAnimationUsingKeyFrames { Duration = duration };
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(start, KeyTime.FromPercent(0)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(middle, KeyTime.FromPercent(0.45), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(end, KeyTime.FromPercent(1), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        return animation;
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
