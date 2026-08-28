using Huaxiazi.Models;
using Huaxiazi.Services;
using System.Text.Json;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class CompanionMotionSystemTests
{
    [Theory]
    [InlineData(8, 5)]
    [InlineData(-8, 5)]
    [InlineData(8, -5)]
    [InlineData(-8, -5)]
    public void FaceKinematics_MovesTheWholeFaceWhileKeepingEyesAboveTheMouth(double gazeX, double gazeY)
    {
        var projection = CompanionFaceKinematics.Project(new CompanionPose
        {
            GazeX = gazeX,
            GazeY = gazeY,
            MouthCurve = 0.8
        });

        Assert.Equal(Math.Sign(gazeX), Math.Sign(projection.HeadX));
        Assert.Equal(Math.Sign(gazeY), Math.Sign(projection.HeadY));
        Assert.InRange(Math.Abs(projection.EyeX), 0.1, 4.2);
        Assert.InRange(Math.Abs(projection.EyeY), 0.1, 2.1);
        Assert.True(projection.MouthBaselineY - projection.EyeBaselineY >= 5.2,
            $"Face collapsed vertically: eye={projection.EyeBaselineY}, mouth={projection.MouthBaselineY}");
    }

    [Fact]
    public void FaceKinematics_UsesACompactReadableEyeMouthGap()
    {
        Assert.InRange(CompanionFaceKinematics.FeatureGap, 10.4, 10.6);
        Assert.InRange(CompanionFaceKinematics.MouthBaseline, 28.9, 29.1);
    }

    [Theory]
    [InlineData(CompanionVisualState.Thinking)]
    [InlineData(CompanionVisualState.Working)]
    public void FaceKinematics_ProcessingStatesKeepMouthBelowTheEyes(CompanionVisualState state)
    {
        var projection = CompanionFaceKinematics.Project(new CompanionPose
        {
            GazeY = -5,
            MouthCurve = -0.1
        });

        var processingOffset = CompanionFaceKinematics.GetStateMouthVisualOffset(
            state);

        Assert.True(processingOffset >= 1.4,
            "Processing feedback needs a deliberate mouth offset so the animated face remains legible.");
        Assert.True(projection.MouthBaselineY + processingOffset - projection.EyeBaselineY >= 5.2,
            "The processing pose must retain a readable eye/mouth separation.");
    }

    [Fact]
    public void WorkingPose_HasAContinuousStateSignatureInsteadOfAStaticExpression()
    {
        var clock = new ManualAnimationClock();
        var controller = new CompanionPoseController(new SequenceRandomSource(0.5), clock);
        controller.SetBaseState(CompanionVisualState.Working);

        clock.Advance(TimeSpan.FromMilliseconds(120));
        var first = controller.Advance(TimeSpan.FromMilliseconds(120), animationsEnabled: false, reduceMotion: false);
        clock.Advance(TimeSpan.FromMilliseconds(260));
        var second = controller.Advance(TimeSpan.FromMilliseconds(260), animationsEnabled: false, reduceMotion: false);

        Assert.NotEqual(first.GazeX, second.GazeX);
        Assert.NotEqual(first.ScaleY, second.ScaleY);
    }

    [Fact]
    public void NewSettings_DefaultToLocalCompanionDriver()
    {
        var settings = new AppSettings();

        Assert.Equal(CompanionDriverMode.Local, settings.CompanionDriverMode);
    }

    [Fact]
    public void CompanionDriverMode_PersistsAsReadableStableText()
    {
        var json = JsonSerializer.Serialize(new AppSettings { CompanionDriverMode = CompanionDriverMode.EmotionAssistant });
        var restored = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.Contains("\"companionDriverMode\":\"EmotionAssistant\"", json);
        Assert.Equal(CompanionDriverMode.EmotionAssistant, restored?.CompanionDriverMode);
    }

    [Fact]
    public void LocalDriver_CollapsesFailuresIntoOnlyTwoVisualVariants()
    {
        var random = new SequenceRandomSource(0.1, 0.9, 0.2, 0.8);
        var driver = new LocalCompanionDriver(random);

        var variants = Enumerable.Range(0, 4)
            .Select(_ => driver.Resolve(CompanionEvent.Failure("HTTP 400")))
            .Select(intent => intent.Action)
            .ToArray();

        Assert.Equal(
            [CompanionActionKind.FailureConfused, CompanionActionKind.FailureConcerned,
             CompanionActionKind.FailureConfused, CompanionActionKind.FailureConcerned],
            variants);
    }

    [Fact]
    public void LocalDriver_MapsDirectManipulationWithoutReplacingTheBaseState()
    {
        var driver = new LocalCompanionDriver(new SequenceRandomSource(0.5));

        var intent = driver.Resolve(new CompanionEvent(
            CompanionEventKind.DragMoved,
            Direction: CompanionDirection.NorthEast,
            BaseState: CompanionVisualState.Working));

        Assert.Equal(CompanionVisualState.Working, intent.BaseState);
        Assert.Equal(CompanionActionKind.Drag, intent.Action);
        Assert.Equal(CompanionDirection.NorthEast, intent.Direction);
    }

    [Theory]
    [InlineData(CompanionEventKind.Pasted, CompanionActionKind.Paste)]
    [InlineData(CompanionEventKind.Submitted, CompanionActionKind.Submit)]
    [InlineData(CompanionEventKind.ViewChanged, CompanionActionKind.SwitchView)]
    [InlineData(CompanionEventKind.Undo, CompanionActionKind.Undo)]
    [InlineData(CompanionEventKind.Redo, CompanionActionKind.Redo)]
    [InlineData(CompanionEventKind.DiffScanned, CompanionActionKind.Scan)]
    [InlineData(CompanionEventKind.HistoryOpened, CompanionActionKind.LookBack)]
    [InlineData(CompanionEventKind.Resized, CompanionActionKind.Resize)]
    public void LocalDriver_MapsEveryDailyOperationWithoutModelInput(
        CompanionEventKind eventKind,
        CompanionActionKind expected)
    {
        var driver = new LocalCompanionDriver(new SequenceRandomSource(0.5));

        var intent = driver.Resolve(new CompanionEvent(eventKind));

        Assert.Equal(expected, intent.Action);
    }

    [Fact]
    public void PoseComposer_AddsDirectionalDragWithoutMutatingTheBasePose()
    {
        var original = new CompanionPose
        {
            ScaleX = 1.02,
            ScaleY = 0.99,
            OffsetY = -0.4,
            EyeOpen = 0.82
        };
        var intent = new CompanionIntent(
            CompanionVisualState.Working,
            CompanionActionKind.Drag,
            CompanionDirection.NorthEast,
            1,
            TimeSpan.FromMilliseconds(120));

        var composed = PoseComposer.Compose(original, CompanionPose.Identity, intent);

        Assert.Equal(1.02, original.ScaleX);
        Assert.True(composed.ScaleX > original.ScaleX);
        Assert.True(composed.OffsetX > 0);
        Assert.True(composed.OffsetY < original.OffsetY);
        Assert.Equal(original.EyeOpen, composed.EyeOpen);
    }

    [Fact]
    public void PoseComposer_DragStartWithoutDirectionStillProvidesVisiblePickupFeedback()
    {
        var intent = new CompanionIntent(
            CompanionVisualState.Idle,
            CompanionActionKind.Drag,
            CompanionDirection.None,
            1,
            TimeSpan.MaxValue);

        var composed = PoseComposer.Compose(CompanionPose.Identity, CompanionPose.Identity, intent);

        Assert.True(composed.ScaleX > 1);
        Assert.True(composed.ScaleY < 1);
    }

    [Theory]
    [InlineData(CompanionVisualState.Error, CompanionVisualState.Happy, false)]
    [InlineData(CompanionVisualState.Working, CompanionVisualState.Warning, true)]
    [InlineData(CompanionVisualState.Idle, CompanionVisualState.Curious, true)]
    [InlineData(CompanionVisualState.Curious, CompanionVisualState.Listening, true)]
    public void VectorAnimationController_UsesStablePriorityArbitration(
        CompanionVisualState current,
        CompanionVisualState next,
        bool expected)
    {
        Assert.Equal(expected, VectorAnimationController.CanInterrupt(current, next));
    }

    [Fact]
    public void AssistantProtocol_LocalModeLeavesPromptAndContentByteForByteUnchanged()
    {
        const string prompt = "只优化表达，不改变事实。";
        const string content = "正文\n<HUAXIAZI_EMOTION>{\"emotion\":\"Supportive\",\"intensity\":0.8}</HUAXIAZI_EMOTION>";

        var decorated = AssistantEmotionProtocol.DecorateSystemPrompt(prompt, CompanionDriverMode.Local);
        var parsed = AssistantEmotionProtocol.ParseContent(content, CompanionDriverMode.Local);

        Assert.Equal(prompt, decorated);
        Assert.Equal(content, parsed.Content);
        Assert.Null(parsed.Hint);
    }

    [Fact]
    public void AssistantProtocol_EmotionModeStripsTerminalMetadataAndReturnsValidatedHint()
    {
        const string content = "优化后的正文\n<HUAXIAZI_EMOTION>{\"emotion\":\"Supportive\",\"intensity\":0.8}</HUAXIAZI_EMOTION>";

        var parsed = AssistantEmotionProtocol.ParseContent(content, CompanionDriverMode.EmotionAssistant);

        Assert.Equal("优化后的正文", parsed.Content);
        Assert.Equal(AssistantEmotionKind.Supportive, parsed.Hint?.Emotion);
        Assert.Equal(0.8, parsed.Hint?.Intensity);
    }

    [Fact]
    public void IdleScheduler_DoesNotRepeatThePreviousMicroExpression()
    {
        var clock = new ManualAnimationClock();
        var scheduler = new IdleBehaviorScheduler(new SequenceRandomSource(0, 0, 0, 0), clock);
        scheduler.Reset();

        clock.Advance(TimeSpan.FromSeconds(30));
        var first = scheduler.Poll(eligible: true);
        clock.Advance(TimeSpan.FromSeconds(30));
        var second = scheduler.Poll(eligible: true);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void IdleScheduler_SuppressesRandomBehaviorWhileInteractionOwnsThePose()
    {
        var clock = new ManualAnimationClock();
        var scheduler = new IdleBehaviorScheduler(new SequenceRandomSource(0), clock);
        scheduler.Reset();
        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Null(scheduler.Poll(eligible: false));
    }

    [Fact]
    public void IdleScheduler_UsesABudgetInsteadOfFiringContinuously()
    {
        var clock = new ManualAnimationClock();
        var scheduler = new IdleBehaviorScheduler(new SequenceRandomSource(0), clock);
        scheduler.Reset();
        var fired = 0;

        for (var i = 0; i < 10; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(5.1));
            if (scheduler.Poll(eligible: true) is not null) fired++;
        }

        Assert.InRange(fired, 1, 4);
    }

    [Fact]
    public void IdleScheduler_ReservesProminentGesturesForSustainedIdleTime()
    {
        var clock = new ManualAnimationClock();
        var scheduler = new IdleBehaviorScheduler(new SequenceRandomSource(0), clock);
        scheduler.Reset();
        clock.Advance(TimeSpan.FromSeconds(40));

        var behavior = scheduler.Poll(eligible: true);

        Assert.Contains(behavior, new CompanionIdleBehavior?[]
        {
            CompanionIdleBehavior.LookAround,
            CompanionIdleBehavior.GentleSway,
            CompanionIdleBehavior.Drowsy
        });
    }

    [Fact]
    public void PoseSpring_ConvergesSmoothlyWithoutOvershootingItsTarget()
    {
        var spring = new PoseSpring(0, responseSeconds: 0.18);
        spring.Target = 1;
        var previous = spring.Value;

        for (var i = 0; i < 90; i++)
        {
            spring.Step(TimeSpan.FromSeconds(1d / 60));
            Assert.InRange(spring.Value, previous, 1.0001);
            previous = spring.Value;
        }

        Assert.InRange(spring.Value, 0.995, 1.0001);
    }

    [Fact]
    public void PoseController_InterruptsFromTheCurrentlyRenderedPose()
    {
        var clock = new ManualAnimationClock();
        var controller = new CompanionPoseController(new SequenceRandomSource(0.4), clock);
        controller.SetBaseState(CompanionVisualState.Working);
        controller.Apply(new CompanionEvent(
            CompanionEventKind.DragMoved,
            Direction: CompanionDirection.East,
            BaseState: CompanionVisualState.Working));

        var dragging = controller.Advance(TimeSpan.FromMilliseconds(80), animationsEnabled: true, reduceMotion: false);
        Assert.True(dragging.OffsetX > 0);

        controller.Apply(new CompanionEvent(
            CompanionEventKind.Pressed,
            BaseState: CompanionVisualState.Working));
        var interrupted = controller.Advance(TimeSpan.FromMilliseconds(16), animationsEnabled: true, reduceMotion: false);

        Assert.True(interrupted.OffsetX > 0);
        Assert.True(interrupted.ScaleY < 1);
        Assert.Equal(CompanionVisualState.Working, controller.BaseState);
    }

    [Fact]
    public void PoseController_IdleBlinkIsNaturalAndDoesNotChangeBusinessState()
    {
        var clock = new ManualAnimationClock();
        var controller = new CompanionPoseController(new SequenceRandomSource(0), clock);
        clock.Advance(TimeSpan.FromSeconds(2.81));

        var pose = controller.Advance(TimeSpan.FromMilliseconds(16), animationsEnabled: false, reduceMotion: false);

        Assert.True(pose.EyeOpen < 0.3);
        Assert.Equal(CompanionVisualState.Idle, controller.BaseState);
    }

    [Fact]
    public void PoseController_ExposesTheCurrentIdleMicroExpressionForSkinProjection()
    {
        var clock = new ManualAnimationClock();
        var controller = new CompanionPoseController(new SequenceRandomSource(0), clock);
        clock.Advance(TimeSpan.FromSeconds(30));

        controller.Advance(TimeSpan.FromMilliseconds(16), animationsEnabled: false, reduceMotion: false);

        Assert.NotNull(controller.ActiveIdleBehavior);
    }

    private sealed class SequenceRandomSource(params double[] values) : IRandomSource
    {
        private readonly double[] _values = values.Length == 0 ? [0.5] : values;
        private int _index;

        public double NextDouble()
        {
            var value = _values[_index % _values.Length];
            _index++;
            return value;
        }
    }

    private sealed class ManualAnimationClock : IAnimationClock
    {
        public TimeSpan Elapsed { get; private set; }
        public void Advance(TimeSpan duration) => Elapsed += duration;
    }
}
