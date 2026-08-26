using System;
using System.Collections.Generic;
using System.Text.Json;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

public interface IRandomSource
{
    double NextDouble();
}

public sealed class SystemRandomSource : IRandomSource
{
    private readonly Random _random = new();
    public double NextDouble() => _random.NextDouble();
}

public interface IAnimationClock
{
    TimeSpan Elapsed { get; }
}

public sealed class StopwatchAnimationClock : IAnimationClock
{
    private readonly System.Diagnostics.Stopwatch _stopwatch = System.Diagnostics.Stopwatch.StartNew();
    public TimeSpan Elapsed => _stopwatch.Elapsed;
}

public sealed class LocalCompanionDriver(IRandomSource random)
{
    public CompanionIntent Resolve(CompanionEvent input)
    {
        var action = input.Kind switch
        {
            CompanionEventKind.MouseApproach or CompanionEventKind.HoverStarted => CompanionActionKind.Attend,
            CompanionEventKind.Pressed => CompanionActionKind.Press,
            CompanionEventKind.Released or CompanionEventKind.DragEnded => CompanionActionKind.Release,
            CompanionEventKind.DragStarted or CompanionEventKind.DragMoved => CompanionActionKind.Drag,
            CompanionEventKind.SnappedToEdge => CompanionActionKind.Snap,
            CompanionEventKind.Expanding => CompanionActionKind.Expand,
            CompanionEventKind.Collapsing => CompanionActionKind.Collapse,
            CompanionEventKind.PinChanged => CompanionActionKind.Pin,
            CompanionEventKind.InputStarted or CompanionEventKind.InputPaused => CompanionActionKind.Listen,
            CompanionEventKind.Pasted => CompanionActionKind.Paste,
            CompanionEventKind.Submitted => CompanionActionKind.Submit,
            CompanionEventKind.ViewChanged => CompanionActionKind.SwitchView,
            CompanionEventKind.Undo => CompanionActionKind.Undo,
            CompanionEventKind.Redo => CompanionActionKind.Redo,
            CompanionEventKind.DiffScanned => CompanionActionKind.Scan,
            CompanionEventKind.HistoryOpened => CompanionActionKind.LookBack,
            CompanionEventKind.Confirmed => CompanionActionKind.Confirm,
            CompanionEventKind.InvalidOperation => CompanionActionKind.Remind,
            CompanionEventKind.SettingsOpened => CompanionActionKind.OpenSettings,
            CompanionEventKind.Closing => CompanionActionKind.Close,
            CompanionEventKind.Resized => CompanionActionKind.Resize,
            CompanionEventKind.Failure => random.NextDouble() < 0.5
                ? CompanionActionKind.FailureConfused
                : CompanionActionKind.FailureConcerned,
            _ => CompanionActionKind.None
        };

        return new CompanionIntent(input.BaseState, action, input.Direction, 1, DurationFor(action));
    }

    private static TimeSpan DurationFor(CompanionActionKind action) => action switch
    {
        CompanionActionKind.Drag => TimeSpan.MaxValue,
        CompanionActionKind.FailureConcerned or CompanionActionKind.FailureConfused => TimeSpan.FromMilliseconds(950),
        CompanionActionKind.Scan or CompanionActionKind.LookBack => TimeSpan.FromMilliseconds(620),
        _ => TimeSpan.FromMilliseconds(280)
    };
}

public static class PoseComposer
{
    public static CompanionPose Compose(CompanionPose basePose, CompanionPose microPose, CompanionIntent intent)
    {
        var x = basePose.OffsetX + microPose.OffsetX;
        var y = basePose.OffsetY + microPose.OffsetY;
        var scaleX = basePose.ScaleX * microPose.ScaleX;
        var scaleY = basePose.ScaleY * microPose.ScaleY;
        var rotation = basePose.Rotation + microPose.Rotation;
        var gazeX = basePose.GazeX + microPose.GazeX;
        var gazeY = basePose.GazeY + microPose.GazeY;
        var eyeOpen = basePose.EyeOpen * microPose.EyeOpen;
        var eyeScale = basePose.EyeScale * microPose.EyeScale;
        var mouthCurve = basePose.MouthCurve + microPose.MouthCurve;

        var (dx, dy) = DirectionVector(intent.Direction);
        switch (intent.Action)
        {
            case CompanionActionKind.Drag:
                if (intent.Direction == CompanionDirection.None)
                {
                    scaleX *= 1.04;
                    scaleY *= 0.95;
                }
                scaleX += Math.Abs(dx) * 0.10 * intent.Intensity;
                scaleY += Math.Abs(dy) * 0.07 * intent.Intensity;
                x += dx * 1.6 * intent.Intensity;
                y += dy * 1.6 * intent.Intensity;
                rotation += (dx * 4 - dy * 2) * intent.Intensity;
                gazeX += dx * 2.2;
                gazeY += dy * 1.5;
                break;
            case CompanionActionKind.Press:
                scaleX *= 1.035;
                scaleY *= 0.94;
                break;
            case CompanionActionKind.Release:
            case CompanionActionKind.Confirm:
                scaleX *= 1.025;
                scaleY *= 1.025;
                y -= 0.35;
                break;
            case CompanionActionKind.Attend:
            case CompanionActionKind.OpenSettings:
                scaleX *= 1.018;
                scaleY *= 1.018;
                rotation -= 1.2;
                break;
            case CompanionActionKind.Paste:
                scaleX *= 1.035;
                scaleY *= 1.035;
                eyeScale *= 1.1;
                break;
            case CompanionActionKind.Submit:
                scaleX *= 0.97;
                scaleY *= 0.97;
                y += 0.35;
                break;
            case CompanionActionKind.SwitchView:
            case CompanionActionKind.Undo:
            case CompanionActionKind.Redo:
                x += intent.Action == CompanionActionKind.Undo ? -0.8 : 0.8;
                gazeX += intent.Action == CompanionActionKind.Undo ? -1.5 : 1.5;
                break;
            case CompanionActionKind.Scan:
                gazeX += Math.Max(1, dx * 2);
                break;
            case CompanionActionKind.LookBack:
                rotation -= 2;
                gazeX -= 1.8;
                break;
            case CompanionActionKind.Pin:
                rotation += 1.4;
                gazeX += 1.5;
                break;
            case CompanionActionKind.Listen:
                gazeY += 1.1;
                eyeScale *= 1.035;
                break;
            case CompanionActionKind.Snap:
                scaleX *= Math.Abs(dx) > 0 ? 0.97 : 1;
                scaleY *= Math.Abs(dy) > 0 ? 0.97 : 1;
                x += dx * 0.55;
                y += dy * 0.55;
                break;
            case CompanionActionKind.Resize:
                scaleX += Math.Abs(dx) * 0.025;
                scaleY += Math.Abs(dy) * 0.025;
                break;
            case CompanionActionKind.Expand:
                scaleX *= 0.94;
                scaleY *= 0.94;
                rotation -= 2.5;
                break;
            case CompanionActionKind.Collapse:
                scaleX *= 1.035;
                scaleY *= 1.035;
                rotation += 1.8;
                break;
            case CompanionActionKind.Remind:
                rotation -= 1.8;
                gazeX -= 0.8;
                eyeOpen *= 0.72;
                break;
            case CompanionActionKind.Close:
                eyeOpen = 0.08;
                scaleX *= 0.98;
                scaleY *= 0.98;
                break;
            case CompanionActionKind.FailureConcerned:
                y += 0.8;
                eyeOpen *= 0.72;
                mouthCurve -= 0.35;
                break;
            case CompanionActionKind.FailureConfused:
                rotation -= 2.4;
                gazeX -= 1.2;
                eyeOpen *= 0.82;
                mouthCurve -= 0.18;
                break;
        }

        return new CompanionPose
        {
            ScaleX = scaleX,
            ScaleY = scaleY,
            OffsetX = x,
            OffsetY = y,
            Rotation = rotation,
            GazeX = gazeX,
            GazeY = gazeY,
            EyeOpen = eyeOpen,
            EyeScale = eyeScale,
            MouthCurve = mouthCurve
        };
    }

    private static (double X, double Y) DirectionVector(CompanionDirection direction) => direction switch
    {
        CompanionDirection.North => (0, -1),
        CompanionDirection.NorthEast => (0.707, -0.707),
        CompanionDirection.East => (1, 0),
        CompanionDirection.SouthEast => (0.707, 0.707),
        CompanionDirection.South => (0, 1),
        CompanionDirection.SouthWest => (-0.707, 0.707),
        CompanionDirection.West => (-1, 0),
        CompanionDirection.NorthWest => (-0.707, -0.707),
        _ => (0, 0)
    };
}

public sealed class IdleBehaviorScheduler(IRandomSource random, IAnimationClock clock)
{
    private static readonly CompanionIdleBehavior[] SubtleBehaviors =
        [CompanionIdleBehavior.Glance, CompanionIdleBehavior.Refocus];
    private static readonly CompanionIdleBehavior[] MicroExpressions =
        [CompanionIdleBehavior.Squint, CompanionIdleBehavior.SoftSmile, CompanionIdleBehavior.CuriousLook];
    private static readonly CompanionIdleBehavior[] ProminentBehaviors =
        [CompanionIdleBehavior.LookAround, CompanionIdleBehavior.GentleSway, CompanionIdleBehavior.Drowsy];
    private TimeSpan _nextSubtle;
    private TimeSpan _nextExpression;
    private TimeSpan _nextProminent;
    private TimeSpan _idleSince;
    private bool _wasEligible = true;
    private CompanionIdleBehavior? _last;
    private readonly Queue<TimeSpan> _recentActions = new();

    public void Reset()
    {
        _idleSince = clock.Elapsed;
        ScheduleAll();
    }

    public CompanionIdleBehavior? Poll(bool eligible)
    {
        if (!eligible)
        {
            _wasEligible = false;
            return null;
        }
        if (!_wasEligible)
        {
            _wasEligible = true;
            _idleSince = clock.Elapsed;
            ScheduleAll();
            return null;
        }
        while (_recentActions.Count > 0 && clock.Elapsed - _recentActions.Peek() >= TimeSpan.FromMinutes(1))
            _recentActions.Dequeue();
        if (_recentActions.Count >= 4)
        {
            return null;
        }

        CompanionIdleBehavior[]? family = null;
        if (clock.Elapsed - _idleSince >= TimeSpan.FromSeconds(15) && clock.Elapsed >= _nextProminent)
        {
            family = ProminentBehaviors;
            _nextProminent = clock.Elapsed + TimeSpan.FromSeconds(25 + random.NextDouble() * 30);
        }
        else if (clock.Elapsed >= _nextExpression)
        {
            family = MicroExpressions;
            _nextExpression = clock.Elapsed + TimeSpan.FromSeconds(8 + random.NextDouble() * 12);
        }
        else if (clock.Elapsed >= _nextSubtle)
        {
            family = SubtleBehaviors;
            _nextSubtle = clock.Elapsed + TimeSpan.FromSeconds(5 + random.NextDouble() * 6);
        }
        if (family is null) return null;

        var index = Math.Min(family.Length - 1, (int)(random.NextDouble() * family.Length));
        var selected = family[index];
        if (_last == selected) selected = family[(index + 1) % family.Length];
        _last = selected;
        _recentActions.Enqueue(clock.Elapsed);
        return selected;
    }

    private void ScheduleAll()
    {
        _nextSubtle = clock.Elapsed + TimeSpan.FromSeconds(5 + random.NextDouble() * 6);
        _nextExpression = clock.Elapsed + TimeSpan.FromSeconds(8 + random.NextDouble() * 12);
        _nextProminent = clock.Elapsed + TimeSpan.FromSeconds(25 + random.NextDouble() * 30);
    }
}

public static class AssistantEmotionProtocol
{
    private const string OpenTag = "<HUAXIAZI_EMOTION>";
    private const string CloseTag = "</HUAXIAZI_EMOTION>";

    public static string DecorateSystemPrompt(string prompt, CompanionDriverMode mode)
    {
        if (mode != CompanionDriverMode.EmotionAssistant) return prompt;
        return prompt + "\n\n在正文最后另起一行输出 <HUAXIAZI_EMOTION>{\"emotion\":\"Neutral|Attentive|Supportive|Encouraging|Concerned|Cautious\",\"intensity\":0.0}</HUAXIAZI_EMOTION>。情绪只表达对用户的共情，不得附和攻击、危险、违规或错误事实。";
    }

    public static CompanionAnnotatedText ParseContent(string content, CompanionDriverMode mode)
    {
        if (mode != CompanionDriverMode.EmotionAssistant) return new CompanionAnnotatedText(content, null);
        var trimmed = content.TrimEnd();
        if (!trimmed.EndsWith(CloseTag, StringComparison.Ordinal)) return new CompanionAnnotatedText(content, null);
        var start = trimmed.LastIndexOf(OpenTag, StringComparison.Ordinal);
        if (start < 0) return new CompanionAnnotatedText(content, null);

        var jsonStart = start + OpenTag.Length;
        var json = trimmed[jsonStart..^CloseTag.Length];
        AssistantEmotionHint? hint = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var rawEmotion = root.TryGetProperty("emotion", out var emotionElement) ? emotionElement.GetString() : null;
            var intensity = root.TryGetProperty("intensity", out var intensityElement) && intensityElement.TryGetDouble(out var value)
                ? Math.Clamp(value, 0, 1)
                : 0.5;
            if (Enum.TryParse<AssistantEmotionKind>(rawEmotion, true, out var emotion))
                hint = new AssistantEmotionHint(emotion, intensity);
        }
        catch (JsonException)
        {
            // Metadata is optional; invalid metadata must never damage the accepted text.
        }

        return new CompanionAnnotatedText(trimmed[..start].TrimEnd(), hint);
    }
}

public static class CompanionEmotionMapper
{
    public static CompanionVisualState ToVisualState(AssistantEmotionHint? hint) => hint?.Emotion switch
    {
        AssistantEmotionKind.Attentive => CompanionVisualState.Listening,
        AssistantEmotionKind.Supportive or AssistantEmotionKind.Encouraging => CompanionVisualState.Happy,
        AssistantEmotionKind.Concerned => CompanionVisualState.Warning,
        AssistantEmotionKind.Cautious => CompanionVisualState.Curious,
        _ => CompanionVisualState.Idle
    };
}
