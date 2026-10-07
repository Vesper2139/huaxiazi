using Huaxiazi.Models;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class ExpressionPreferenceSuggestionServiceTests
{
    private readonly ExpressionPreferenceSuggestionService _service = new();

    [Fact]
    public void GetCandidate_UsesOnlyDirectionalEditsAndReturnsRawEvidence()
    {
        var profile = new ExpressionPreferenceProfile
        {
            InteractionSignals = new Dictionary<string, ExpressionInteractionSignalSet>
            {
                ["polish|职场沟通"] = new()
                {
                    AcceptedShortenedOutputs = 4,
                    AcceptedOutputStyles = new Dictionary<string, int> { ["自然"] = 4 }
                }
            }
        };

        var candidate = _service.GetLengthCandidate(profile, ApplicationMode.Polish, "职场沟通");

        Assert.NotNull(candidate);
        Assert.Equal("concise", candidate.Value);
        Assert.Equal(4, candidate.SupportingOutputs);
        Assert.Equal(0, candidate.OpposingOutputs);
        Assert.Equal("polish|职场沟通", candidate.ScopeKey);
    }

    [Fact]
    public void GetToneCandidate_RequiresRepeatedAcceptedExplicitStyleInOneScope()
    {
        var profile = new ExpressionPreferenceProfile
        {
            InteractionSignals = new Dictionary<string, ExpressionInteractionSignalSet>
            {
                ["polish|职场沟通"] = new()
                {
                    AcceptedOutputStyles = new Dictionary<string, int> { ["正式"] = 3 }
                }
            }
        };

        var candidate = _service.GetToneCandidate(profile, ApplicationMode.Polish, "职场沟通");

        Assert.NotNull(candidate);
        Assert.Equal("professional", candidate.Value);
        Assert.Equal(3, candidate.SupportingOutputs);
        Assert.Equal(0, candidate.OpposingOutputs);
        Assert.Equal("polish|职场沟通", candidate.ScopeKey);
    }

    [Fact]
    public void GetToneCandidate_IsSuppressedByMixedRejectedConfirmedOrIgnoredEvidence()
    {
        var profile = new ExpressionPreferenceProfile
        {
            InteractionSignals = new Dictionary<string, ExpressionInteractionSignalSet>
            {
                ["polish|职场沟通"] = new()
                {
                    AcceptedOutputStyles = new Dictionary<string, int> { ["亲切"] = 3 },
                    RejectedOutputStyles = new Dictionary<string, int> { ["亲切"] = 1 }
                },
                ["polish|私人沟通"] = new()
                {
                    AcceptedOutputStyles = new Dictionary<string, int> { ["亲切"] = 2, ["自然"] = 1 }
                },
                ["polish|公开发布"] = new()
                {
                    AcceptedOutputStyles = new Dictionary<string, int> { ["专业"] = 3 }
                },
                ["polish|正式材料"] = new()
                {
                    AcceptedOutputStyles = new Dictionary<string, int> { ["专业"] = 3 }
                }
            },
            TaskPreferences = new Dictionary<string, ExpressionPreferenceSet>
            {
                ["polish|公开发布"] = new() { UserConfirmed = true, PreferredTone = "warm" }
            }
        };

        Assert.Null(_service.GetToneCandidate(profile, ApplicationMode.Polish, "职场沟通"));
        Assert.Null(_service.GetToneCandidate(profile, ApplicationMode.Polish, "私人沟通"));
        Assert.Null(_service.GetToneCandidate(profile, ApplicationMode.Polish, "公开发布"));

        var scenarioCandidate = _service.GetToneCandidate(profile, ApplicationMode.Polish, "正式材料");
        Assert.NotNull(scenarioCandidate);
        profile.IgnoredSuggestionKeys.Add(scenarioCandidate.Key);
        Assert.Null(_service.GetToneCandidate(profile, ApplicationMode.Polish, "正式材料"));
        Assert.Equal("professional", _service.GetToneCandidate(profile, ApplicationMode.Polish, "正式材料", includeIgnored: true)?.Value);
    }

    [Fact]
    public void GetCandidate_ReturnsDetailedWhenExpansionEditsOutnumberShorteningEdits()
    {
        var profile = new ExpressionPreferenceProfile
        {
            InteractionSignals = new Dictionary<string, ExpressionInteractionSignalSet>
            {
                ["prompt-optimize|编程开发"] = new()
                {
                    AcceptedExpandedOutputs = 3,
                    AcceptedOutputStyles = new Dictionary<string, int> { ["正式"] = 3 }
                }
            }
        };

        var candidate = _service.GetLengthCandidate(profile, ApplicationMode.PromptOptimize, "编程开发");

        Assert.NotNull(candidate);
        Assert.Equal("detailed", candidate.Value);
        Assert.Equal(3, candidate.SupportingOutputs);
        Assert.Equal(0, candidate.OpposingOutputs);
    }

    [Fact]
    public void GetCandidate_IsAbsentWithoutDirectionalEvidenceOrWhenEvidenceTies()
    {
        var profile = new ExpressionPreferenceProfile
        {
            InteractionSignals = new Dictionary<string, ExpressionInteractionSignalSet>
            {
                ["polish"] = new() { EditCount = 9, AcceptedCount = 8, RetryCount = 2 },
                ["polish|私人沟通"] = new() { ShorteningEdits = 2, ExpansionEdits = 2 },
                ["polish|公开发布"] = new()
                {
                    AcceptedShortenedOutputs = 2,
                    RejectedShortenedOutputs = 1,
                    AcceptedOutputStyles = new Dictionary<string, int> { ["自然"] = 2 },
                    RejectedOutputStyles = new Dictionary<string, int> { ["自然"] = 1 }
                }
            }
        };

        Assert.Null(_service.GetLengthCandidate(profile, ApplicationMode.Polish, null));
        Assert.Null(_service.GetLengthCandidate(profile, ApplicationMode.Polish, "私人沟通"));
        Assert.Null(_service.GetLengthCandidate(profile, ApplicationMode.Polish, "公开发布"));
    }

    [Fact]
    public void GetCandidate_IsSuppressedWhenAcceptedOutputsUseDifferentStyles()
    {
        var profile = new ExpressionPreferenceProfile
        {
            InteractionSignals = new Dictionary<string, ExpressionInteractionSignalSet>
            {
                ["polish|职场沟通"] = new()
                {
                    AcceptedShortenedOutputs = 2,
                    AcceptedOutputStyles = new Dictionary<string, int> { ["自然"] = 1, ["正式"] = 1 }
                }
            }
        };

        Assert.Null(_service.GetLengthCandidate(profile, ApplicationMode.Polish, "职场沟通"));
    }

    [Fact]
    public void LegacyInteractionSignalsWithoutStyleFields_NormalizeToEmptyMaps()
    {
        var profile = System.Text.Json.JsonSerializer.Deserialize<ExpressionPreferenceProfile>(
            "{\"interactionSignals\":{\"polish\":{\"acceptedCount\":2,\"shorteningEdits\":1}}}")!;

        profile.Normalize();

        Assert.Empty(profile.StyleChoiceUsage);
        Assert.Empty(profile.InteractionSignals["polish"].AcceptedOutputStyles);
        Assert.Empty(profile.InteractionSignals["polish"].RejectedOutputStyles);
        Assert.Empty(profile.InteractionSignals["polish"].AcceptedRemovedCannedExpressions);
        Assert.Empty(profile.InteractionSignals["polish"].RejectedRemovedCannedExpressions);
        Assert.Null(_service.GetLengthCandidate(profile, ApplicationMode.Polish, null));
    }

    [Fact]
    public void InteractionSignalNormalization_KeepsOnlyKnownPositiveCannedPhraseCounts()
    {
        var profile = System.Text.Json.JsonSerializer.Deserialize<ExpressionPreferenceProfile>(
            "{\"interactionSignals\":{\"polish\":{\"acceptedRemovedCannedExpressions\":{\"首先\":3,\"自由文本\":9,\"其次\":0},\"rejectedRemovedCannedExpressions\":{\"最后\":-1}}}}")!;

        profile.Normalize();

        Assert.Equal(new Dictionary<string, int> { ["首先"] = 3 }, profile.InteractionSignals["polish"].AcceptedRemovedCannedExpressions);
        Assert.Empty(profile.InteractionSignals["polish"].RejectedRemovedCannedExpressions);
    }

    [Fact]
    public void RecordObservedOutcome_PreservesKnownPhraseSignalWhenStyleMetadataIsMissing()
    {
        var profile = new ExpressionPreferenceProfile();

        var recorded = new StructuredPreferenceService().RecordObservedOutputOutcome(
            profile,
            ApplicationMode.Polish,
            "职场沟通",
            wasShortened: false,
            wasExpanded: false,
            accepted: true,
            rejected: false,
            outputStyle: null,
            removedCannedExpressions: ["首先"]);

        Assert.True(recorded);
        Assert.Equal(1, profile.InteractionSignals["polish|职场沟通"].AcceptedRemovedCannedExpressions["首先"]);
        Assert.Empty(new ExpressionPreferenceSuggestionService().GetForbiddenExpressionCandidates(profile, ApplicationMode.Polish, "职场沟通"));
    }

    [Fact]
    public void GetCandidate_IsHiddenForConfirmedLengthOrUserDismissalAndIsScopeSpecific()
    {
        var profile = new ExpressionPreferenceProfile
        {
            InteractionSignals = new Dictionary<string, ExpressionInteractionSignalSet>
            {
                ["polish"] = new()
                {
                    AcceptedShortenedOutputs = 3,
                    AcceptedOutputStyles = new Dictionary<string, int> { ["自然"] = 3 }
                },
                ["polish|职场沟通"] = new()
                {
                    AcceptedExpandedOutputs = 2,
                    AcceptedOutputStyles = new Dictionary<string, int> { ["正式"] = 2 }
                }
            },
            TaskPreferences = new Dictionary<string, ExpressionPreferenceSet>
            {
                ["polish"] = new() { UserConfirmed = true, PreferredLength = "detailed" }
            }
        };

        Assert.Null(_service.GetLengthCandidate(profile, ApplicationMode.Polish, null));
        Assert.Equal("detailed", _service.GetLengthCandidate(profile, ApplicationMode.Polish, "职场沟通")?.Value);

        var scenarioCandidate = _service.GetLengthCandidate(profile, ApplicationMode.Polish, "职场沟通")!;
        profile.IgnoredSuggestionKeys.Add(scenarioCandidate.Key);
        Assert.Null(_service.GetLengthCandidate(profile, ApplicationMode.Polish, "职场沟通"));
    }

    [Fact]
    public void RecordObservedOutcome_CorrelatesEditsWithAcceptanceOrRejectionWithoutText()
    {
        var profile = new ExpressionPreferenceProfile();
        var recorder = new StructuredPreferenceService();

        Assert.True(recorder.RecordObservedOutputOutcome(profile, ApplicationMode.Polish, "职场沟通", true, false, true, false));
        Assert.True(recorder.RecordObservedOutputOutcome(profile, ApplicationMode.Polish, "职场沟通", false, true, false, true));
        Assert.False(recorder.RecordObservedOutputOutcome(profile, ApplicationMode.Polish, "职场沟通", true, false, true, true));
        Assert.False(recorder.RecordObservedOutputOutcome(profile, ApplicationMode.Polish, "私人沟通", false, false, false, true));

        var signals = profile.InteractionSignals["polish|职场沟通"];
        Assert.Equal(1, signals.AcceptedShortenedOutputs);
        Assert.Equal(1, signals.RejectedExpandedOutputs);
        Assert.Equal(0, signals.AcceptedExpandedOutputs);
        Assert.Equal(0, signals.RejectedShortenedOutputs);
        Assert.DoesNotContain("text", System.Text.Json.JsonSerializer.Serialize(profile), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetForbiddenExpressionCandidates_RequiresRepeatedAcceptedRemovalAndNoOpposingEvidence()
    {
        var profile = new ExpressionPreferenceProfile
        {
            InteractionSignals = new Dictionary<string, ExpressionInteractionSignalSet>
            {
                ["polish|职场沟通"] = new()
                {
                    AcceptedOutputStyles = new Dictionary<string, int> { ["自然"] = 3 },
                    AcceptedRemovedCannedExpressions = new Dictionary<string, int> { ["首先"] = 3, ["其次"] = 2 },
                    RejectedRemovedCannedExpressions = new Dictionary<string, int> { ["其次"] = 1 }
                }
            }
        };

        var candidates = _service.GetForbiddenExpressionCandidates(profile, ApplicationMode.Polish, "职场沟通");

        var candidate = Assert.Single(candidates);
        Assert.Equal("首先", candidate.Value);
        Assert.Equal(3, candidate.SupportingOutputs);
        Assert.Equal(0, candidate.OpposingOutputs);
        Assert.Equal("polish|职场沟通", candidate.ScopeKey);
    }

    [Fact]
    public void GetForbiddenExpressionCandidates_HidesConfirmedOrIgnoredItemsAndKeepsScopesSeparate()
    {
        var profile = new ExpressionPreferenceProfile
        {
            InteractionSignals = new Dictionary<string, ExpressionInteractionSignalSet>
            {
                ["polish|职场沟通"] = new()
                {
                    AcceptedOutputStyles = new Dictionary<string, int> { ["自然"] = 3 },
                    AcceptedRemovedCannedExpressions = new Dictionary<string, int> { ["首先"] = 4, ["其次"] = 3 }
                },
                ["polish|私人沟通"] = new()
                {
                    AcceptedOutputStyles = new Dictionary<string, int> { ["自然"] = 3 },
                    AcceptedRemovedCannedExpressions = new Dictionary<string, int> { ["首先"] = 3 }
                }
            },
            TaskPreferences = new Dictionary<string, ExpressionPreferenceSet>
            {
                ["polish|职场沟通"] = new() { UserConfirmed = true, ForbiddenExpressions = ["首先"] }
            }
        };

        var candidates = _service.GetForbiddenExpressionCandidates(profile, ApplicationMode.Polish, "职场沟通");

        var candidate = Assert.Single(candidates);
        Assert.Equal("其次", candidate.Value);
        profile.IgnoredSuggestionKeys.Add(candidate.Key);
        Assert.Empty(_service.GetForbiddenExpressionCandidates(profile, ApplicationMode.Polish, "职场沟通"));
        Assert.Equal("首先", Assert.Single(_service.GetForbiddenExpressionCandidates(profile, ApplicationMode.Polish, "私人沟通")).Value);
    }
}
