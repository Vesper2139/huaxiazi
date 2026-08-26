using System;
using PromptFloat.Models;

namespace PromptFloat.Services;

/// <summary>
/// A scalar critically-damped spring. The analytic integration keeps animation stable when
/// WPF drops frames and guarantees a new target continues from the currently rendered value.
/// </summary>
public sealed class PoseSpring
{
    private readonly double _angularFrequency;

    public PoseSpring(double initialValue, double responseSeconds = 0.2)
    {
        Value = Target = initialValue;
        _angularFrequency = 4.6 / Math.Max(0.05, responseSeconds);
    }

    public double Value { get; private set; }
    public double Velocity { get; private set; }
    public double Target { get; set; }

    public void Step(TimeSpan elapsed)
    {
        var seconds = Math.Clamp(elapsed.TotalSeconds, 0, 0.05);
        if (seconds <= 0) return;
        var displacement = Value - Target;
        var coefficient = Velocity + _angularFrequency * displacement;
        var decay = Math.Exp(-_angularFrequency * seconds);
        Value = Target + (displacement + coefficient * seconds) * decay;
        Velocity = (coefficient - _angularFrequency * (displacement + coefficient * seconds)) * decay;
        if (Math.Abs(Value - Target) < 0.0001 && Math.Abs(Velocity) < 0.001)
        {
            Value = Target;
            Velocity = 0;
        }
    }

    public void Snap(double value)
    {
        Value = Target = value;
        Velocity = 0;
    }
}

/// <summary>
/// Pure pose state machine used by every default-face instance. It owns no Window coordinates
/// and only produces render transforms, so direct manipulation never drifts the actual window.
/// </summary>
public sealed class CompanionPoseController
{
    private readonly IRandomSource _random;
    private readonly IAnimationClock _clock;
    private readonly LocalCompanionDriver _driver;
    private readonly IdleBehaviorScheduler _idleScheduler;
    private CompanionIntent _intent = new(CompanionVisualState.Idle, CompanionActionKind.None, CompanionDirection.None, 1, TimeSpan.Zero);
    private TimeSpan _intentExpires;
    private TimeSpan _microExpires;
    private CompanionIdleBehavior? _microBehavior;
    private TimeSpan _nextBlink;
    private TimeSpan _blinkUntil;
    private double _gazeX;
    private double _gazeY;

    private readonly PoseSpring _scaleX = new(1);
    private readonly PoseSpring _scaleY = new(1);
    private readonly PoseSpring _offsetX = new(0);
    private readonly PoseSpring _offsetY = new(0);
    private readonly PoseSpring _rotation = new(0);
    private readonly PoseSpring _renderedGazeX = new(0, 0.14);
    private readonly PoseSpring _renderedGazeY = new(0, 0.14);
    private readonly PoseSpring _eyeOpen = new(1, 0.12);
    private readonly PoseSpring _eyeScale = new(1);
    private readonly PoseSpring _mouthCurve = new(0);

    public CompanionPoseController(IRandomSource random, IAnimationClock clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _random = random ?? throw new ArgumentNullException(nameof(random));
        _driver = new LocalCompanionDriver(_random);
        _idleScheduler = new IdleBehaviorScheduler(random, clock);
        _idleScheduler.Reset();
        ScheduleNextBlink();
    }

    public CompanionVisualState BaseState { get; private set; } = CompanionVisualState.Idle;
    /// <summary>当前空闲微表情，供不同皮肤投影为各自的素材帧。</summary>
    public CompanionIdleBehavior? ActiveIdleBehavior => _microBehavior;
    public bool HasActiveAction => _intent.Action != CompanionActionKind.None && _clock.Elapsed < _intentExpires;

    public void SetBaseState(CompanionVisualState state) => BaseState = state;

    public void SetGaze(double x, double y)
    {
        var nextX = Math.Clamp(x, -8, 8);
        var nextY = Math.Clamp(y, -5, 5);
        if (Math.Abs(nextX - _gazeX) + Math.Abs(nextY - _gazeY) > 0.45)
        {
            _microBehavior = null;
            _idleScheduler.Reset();
        }
        _gazeX = nextX;
        _gazeY = nextY;
    }

    public void Apply(CompanionEvent input)
    {
        _intent = _driver.Resolve(input with { BaseState = input.BaseState });
        if (_intent.Action == CompanionActionKind.None) return;
        _intentExpires = _intent.Duration == TimeSpan.MaxValue
            ? TimeSpan.MaxValue
            : _clock.Elapsed + _intent.Duration;
        _microBehavior = null;
    }

    public CompanionPose Advance(TimeSpan elapsed, bool animationsEnabled, bool reduceMotion)
    {
        if (_intent.Action != CompanionActionKind.None && _clock.Elapsed >= _intentExpires)
            _intent = _intent with { Action = CompanionActionKind.None, Direction = CompanionDirection.None };

        var eligibleForIdle = BaseState == CompanionVisualState.Idle && !HasActiveAction;
        if (!reduceMotion && _idleScheduler.Poll(eligibleForIdle) is { } behavior)
        {
            _microBehavior = behavior;
            _microExpires = _clock.Elapsed + (behavior is CompanionIdleBehavior.LookAround or CompanionIdleBehavior.GentleSway or CompanionIdleBehavior.Drowsy
                ? TimeSpan.FromMilliseconds(1500)
                : TimeSpan.FromMilliseconds(620));
        }
        if (_clock.Elapsed >= _microExpires) _microBehavior = null;

        var canBlink = BaseState is CompanionVisualState.Idle or CompanionVisualState.Curious or CompanionVisualState.Listening;
        if (!reduceMotion && canBlink && _clock.Elapsed >= _nextBlink)
        {
            _blinkUntil = _clock.Elapsed + TimeSpan.FromMilliseconds(165);
            ScheduleNextBlink();
        }

        var basePose = BasePose(BaseState, _clock.Elapsed);
        var microPose = reduceMotion ? CompanionPose.Identity : MicroPose(_clock.Elapsed, _microBehavior);
        var activeIntent = reduceMotion
            ? _intent with { Action = CompanionActionKind.None }
            : _intent with { BaseState = BaseState };
        var target = PoseComposer.Compose(basePose, microPose, activeIntent);
        var blinking = canBlink && _clock.Elapsed < _blinkUntil;
        target = new CompanionPose
        {
            ScaleX = target.ScaleX,
            ScaleY = target.ScaleY,
            OffsetX = target.OffsetX,
            OffsetY = target.OffsetY,
            Rotation = target.Rotation,
            GazeX = target.GazeX + (BaseState is CompanionVisualState.Sleeping or CompanionVisualState.Error ? 0 : _gazeX),
            GazeY = target.GazeY + (BaseState is CompanionVisualState.Sleeping or CompanionVisualState.Error ? 0 : _gazeY),
            EyeOpen = blinking ? Math.Min(target.EyeOpen, 0.08) : target.EyeOpen,
            EyeScale = target.EyeScale,
            MouthCurve = target.MouthCurve
        };

        SetTargets(target);
        if (!animationsEnabled)
            SnapToTargets();
        else
            StepAll(elapsed);
        return CurrentPose();
    }

    private void ScheduleNextBlink()
    {
        _nextBlink = _clock.Elapsed + TimeSpan.FromSeconds(2.8 + _random.NextDouble() * 3.7);
    }

    private static CompanionPose BasePose(CompanionVisualState state, TimeSpan elapsed)
    {
        var seconds = elapsed.TotalSeconds;
        return state switch
        {
            CompanionVisualState.Sleeping => new CompanionPose
            {
                ScaleX = 1 + Math.Sin(seconds * 1.05) * 0.004,
                ScaleY = 0.97 + Math.Sin(seconds * 1.05) * 0.008,
                OffsetY = 0.7,
                EyeOpen = 0.08
            },
            CompanionVisualState.Happy => new CompanionPose
            {
                ScaleX = 1.02 + Math.Sin(seconds * 3.1) * 0.006,
                ScaleY = 1.02 + Math.Sin(seconds * 3.1) * 0.008,
                OffsetY = -0.35 - Math.Sin(seconds * 3.1) * 0.22,
                EyeOpen = 0.9,
                MouthCurve = 1 + Math.Sin(seconds * 2.4) * 0.08
            },
            CompanionVisualState.Curious => new CompanionPose
            {
                Rotation = -2.2 + Math.Sin(seconds * 2.1) * 0.35,
                GazeY = -0.25,
                EyeScale = 1.08,
                MouthCurve = 0.15
            },
            CompanionVisualState.Thinking => new CompanionPose
            {
                Rotation = -1.4 + Math.Sin(seconds * 1.8) * 0.55,
                GazeX = -1.1 + Math.Sin(seconds * 2.5) * 0.65,
                GazeY = -0.55 + Math.Cos(seconds * 1.7) * 0.25,
                EyeOpen = 0.72,
                MouthCurve = -0.1
            },
            CompanionVisualState.Listening => new CompanionPose
            {
                GazeX = Math.Sin(seconds * 1.9) * 0.28,
                GazeY = 0.8 + Math.Sin(seconds * 2.2) * 0.2,
                EyeScale = 1.04 + Math.Sin(seconds * 2.2) * 0.012
            },
            CompanionVisualState.Working => new CompanionPose
            {
                ScaleX = 1.01 + Math.Sin(seconds * 4.4) * 0.005,
                ScaleY = 0.99 - Math.Sin(seconds * 4.4) * 0.007,
                GazeX = Math.Sin(seconds * 5.2) * 1.7,
                GazeY = Math.Cos(seconds * 2.6) * 0.35,
                EyeOpen = 0.78,
                MouthCurve = -0.08 + Math.Sin(seconds * 3.2) * 0.05
            },
            CompanionVisualState.Surprised => new CompanionPose { ScaleX = 1.035, ScaleY = 1.035, OffsetY = 0.35, EyeScale = 1.16 },
            CompanionVisualState.Warning => new CompanionPose
            {
                OffsetX = Math.Sin(seconds * 5.4) * 0.28,
                OffsetY = 0.45,
                EyeOpen = 0.68,
                MouthCurve = -0.7
            },
            CompanionVisualState.Error => new CompanionPose
            {
                Rotation = Math.Sin(seconds * 4.2) * 0.45,
                OffsetY = 0.65,
                EyeOpen = 0.58,
                MouthCurve = -1
            },
            _ => CompanionPose.Identity
        };
    }

    private static CompanionPose MicroPose(TimeSpan elapsed, CompanionIdleBehavior? behavior)
    {
        var seconds = elapsed.TotalSeconds;
        var breath = Math.Sin(seconds * 1.65);
        var pose = new CompanionPose { ScaleX = 1 + breath * 0.006, ScaleY = 1 + breath * 0.009, OffsetY = -breath * 0.16 };
        return behavior switch
        {
            CompanionIdleBehavior.Glance => Copy(pose, gazeX: 2.2),
            CompanionIdleBehavior.Squint => Copy(pose, eyeOpen: 0.68),
            CompanionIdleBehavior.SoftSmile => Copy(pose, mouthCurve: 0.55),
            CompanionIdleBehavior.CuriousLook => Copy(pose, rotation: -1.8, eyeScale: 1.05),
            CompanionIdleBehavior.Refocus => Copy(pose, gazeX: -1.2, gazeY: 0.5),
            CompanionIdleBehavior.LookAround => Copy(pose, gazeX: Math.Sin(seconds * 5) * 2.4),
            CompanionIdleBehavior.GentleSway => Copy(pose, rotation: Math.Sin(seconds * 3) * 1.3),
            CompanionIdleBehavior.Drowsy => Copy(pose, eyeOpen: 0.55, offsetY: pose.OffsetY + 0.3),
            _ => pose
        };
    }

    private static CompanionPose Copy(
        CompanionPose pose,
        double? offsetY = null,
        double? rotation = null,
        double? gazeX = null,
        double? gazeY = null,
        double? eyeOpen = null,
        double? eyeScale = null,
        double? mouthCurve = null) => new()
    {
        ScaleX = pose.ScaleX,
        ScaleY = pose.ScaleY,
        OffsetX = pose.OffsetX,
        OffsetY = offsetY ?? pose.OffsetY,
        Rotation = rotation ?? pose.Rotation,
        GazeX = gazeX ?? pose.GazeX,
        GazeY = gazeY ?? pose.GazeY,
        EyeOpen = eyeOpen ?? pose.EyeOpen,
        EyeScale = eyeScale ?? pose.EyeScale,
        MouthCurve = mouthCurve ?? pose.MouthCurve
    };

    private void SetTargets(CompanionPose pose)
    {
        _scaleX.Target = pose.ScaleX;
        _scaleY.Target = pose.ScaleY;
        _offsetX.Target = pose.OffsetX;
        _offsetY.Target = pose.OffsetY;
        _rotation.Target = pose.Rotation;
        _renderedGazeX.Target = pose.GazeX;
        _renderedGazeY.Target = pose.GazeY;
        _eyeOpen.Target = pose.EyeOpen;
        _eyeScale.Target = pose.EyeScale;
        _mouthCurve.Target = pose.MouthCurve;
    }

    private void StepAll(TimeSpan elapsed)
    {
        foreach (var spring in Springs()) spring.Step(elapsed);
    }

    private void SnapToTargets()
    {
        foreach (var spring in Springs()) spring.Snap(spring.Target);
    }

    private PoseSpring[] Springs() =>
        [_scaleX, _scaleY, _offsetX, _offsetY, _rotation, _renderedGazeX, _renderedGazeY, _eyeOpen, _eyeScale, _mouthCurve];

    private CompanionPose CurrentPose() => new()
    {
        ScaleX = _scaleX.Value,
        ScaleY = _scaleY.Value,
        OffsetX = _offsetX.Value,
        OffsetY = _offsetY.Value,
        Rotation = _rotation.Value,
        GazeX = _renderedGazeX.Value,
        GazeY = _renderedGazeY.Value,
        EyeOpen = _eyeOpen.Value,
        EyeScale = _eyeScale.Value,
        MouthCurve = _mouthCurve.Value
    };
}
