using System;
using System.Text.Json.Serialization;

namespace PromptFloat.Models;

[JsonConverter(typeof(JsonStringEnumConverter<CompanionDriverMode>))]
public enum CompanionDriverMode
{
    Local,
    EmotionAssistant
}

public enum CompanionEventKind
{
    MouseApproach,
    HoverStarted,
    HoverEnded,
    Pressed,
    Released,
    DragStarted,
    DragMoved,
    DragEnded,
    SnappedToEdge,
    Expanding,
    Collapsing,
    PinChanged,
    InputStarted,
    InputPaused,
    Pasted,
    Submitted,
    ViewChanged,
    Undo,
    Redo,
    DiffScanned,
    HistoryOpened,
    Confirmed,
    InvalidOperation,
    SettingsOpened,
    Closing,
    Resized,
    Failure
}

public enum CompanionActionKind
{
    None,
    Attend,
    Press,
    Release,
    Drag,
    Snap,
    Expand,
    Collapse,
    Pin,
    Listen,
    Paste,
    Submit,
    SwitchView,
    Undo,
    Redo,
    Scan,
    LookBack,
    Confirm,
    Remind,
    OpenSettings,
    Close,
    Resize,
    FailureConfused,
    FailureConcerned
}

public enum CompanionDirection
{
    None,
    North,
    NorthEast,
    East,
    SouthEast,
    South,
    SouthWest,
    West,
    NorthWest
}

public readonly record struct CompanionEvent(
    CompanionEventKind Kind,
    string? Detail = null,
    CompanionDirection Direction = CompanionDirection.None,
    CompanionVisualState BaseState = CompanionVisualState.Idle,
    DateTimeOffset? OccurredAt = null)
{
    public static CompanionEvent Failure(string? detail = null, CompanionVisualState baseState = CompanionVisualState.Error) =>
        new(CompanionEventKind.Failure, detail, CompanionDirection.None, baseState);
}

public readonly record struct CompanionIntent(
    CompanionVisualState BaseState,
    CompanionActionKind Action,
    CompanionDirection Direction,
    double Intensity,
    TimeSpan Duration);

public enum AssistantEmotionKind
{
    Neutral,
    Attentive,
    Supportive,
    Encouraging,
    Concerned,
    Cautious
}

public sealed record AssistantEmotionHint(AssistantEmotionKind Emotion, double Intensity);

public sealed record CompanionAnnotatedText(string Content, AssistantEmotionHint? Hint);

public sealed class CompanionPose
{
    public static CompanionPose Identity => new();

    public double ScaleX { get; init; } = 1;
    public double ScaleY { get; init; } = 1;
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }
    public double Rotation { get; init; }
    public double GazeX { get; init; }
    public double GazeY { get; init; }
    public double EyeOpen { get; init; } = 1;
    public double EyeScale { get; init; } = 1;
    public double MouthCurve { get; init; }
}

public enum CompanionIdleBehavior
{
    Glance,
    Squint,
    SoftSmile,
    CuriousLook,
    Refocus,
    LookAround,
    GentleSway,
    Drowsy
}
