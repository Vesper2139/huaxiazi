using Huaxiazi.Models;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class ExpressionPreferenceFeedbackSessionTests
{
    [Fact]
    public void AcceptedEditedOutput_PersistsOnlyDirectionAndTaskScenarioCounts()
    {
        var profile = new ExpressionPreferenceProfile();
        var session = new ExpressionPreferenceFeedbackSession();
        var generated = "generated-private-content-" + "x".PadRight(100, 'x');
        var edited = "edited-private-content-" + "y".PadRight(60, 'y');

        session.Start(ApplicationMode.Polish, "职场沟通", "正式", enabled: true);
        session.RecordEdit(generated, edited);
        Assert.True(session.Complete(profile, new StructuredPreferenceService(), accepted: true, rejected: false));

        var signals = profile.InteractionSignals["polish|职场沟通"];
        Assert.Equal(1, signals.AcceptedShortenedOutputs);
        Assert.Equal(0, signals.RejectedShortenedOutputs);
        Assert.Equal(1, signals.AcceptedOutputStyles["正式"]);
        Assert.Empty(signals.RejectedOutputStyles);
        var serialized = System.Text.Json.JsonSerializer.Serialize(profile);
        Assert.DoesNotContain(generated, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(edited, serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void RetriedExpandedOutput_IsRecordedAsRejectedAndDoesNotCreateStyleCandidate()
    {
        var profile = new ExpressionPreferenceProfile();
        var session = new ExpressionPreferenceFeedbackSession();

        session.Start(ApplicationMode.PromptOptimize, "编程开发", "简洁", enabled: true);
        session.RecordEdit(new string('a', 100), new string('b', 130));
        Assert.True(session.Complete(profile, new StructuredPreferenceService(), accepted: false, rejected: true));

        var signals = profile.InteractionSignals["prompt-optimize|编程开发"];
        Assert.Equal(1, signals.RejectedExpandedOutputs);
        Assert.Equal(1, signals.RejectedOutputStyles["简洁"]);
        Assert.Null(new ExpressionPreferenceSuggestionService().GetLengthCandidate(profile, ApplicationMode.PromptOptimize, "编程开发"));
    }

    [Fact]
    public void MixedAcceptedDirections_AreRecordedButDoNotProduceAnUnambiguousCandidate()
    {
        var profile = new ExpressionPreferenceProfile();
        var session = new ExpressionPreferenceFeedbackSession();

        session.Start(ApplicationMode.Polish, null, "亲切", enabled: true);
        session.RecordEdit(new string('a', 100), new string('b', 70));
        session.RecordEdit(new string('c', 100), new string('d', 140));
        Assert.True(session.Complete(profile, new StructuredPreferenceService(), accepted: true, rejected: false));

        var signals = profile.InteractionSignals["polish"];
        Assert.Equal(1, signals.AcceptedShortenedOutputs);
        Assert.Equal(1, signals.AcceptedExpandedOutputs);
        Assert.Equal(1, signals.AcceptedOutputStyles["亲切"]);
        Assert.Null(new ExpressionPreferenceSuggestionService().GetLengthCandidate(profile, ApplicationMode.Polish, null));
    }

    [Fact]
    public void DisabledOrAmbiguousSession_DoesNotPersistPreferenceEvidence()
    {
        var profile = new ExpressionPreferenceProfile();
        var session = new ExpressionPreferenceFeedbackSession();

        session.Start(ApplicationMode.Polish, null, "自然", enabled: false);
        session.RecordEdit(new string('a', 100), new string('b', 60));
        Assert.False(session.Complete(profile, new StructuredPreferenceService(), accepted: true, rejected: false));

        session.Start(ApplicationMode.Polish, null, "自然", enabled: true);
        session.RecordEdit(new string('a', 100), new string('b', 60));
        Assert.False(session.Complete(profile, new StructuredPreferenceService(), accepted: true, rejected: true));
        Assert.Empty(profile.InteractionSignals);
    }

    [Fact]
    public void AcceptedOutput_TracksTheAppliedStyleEvenWithoutLengthEdits()
    {
        var profile = new ExpressionPreferenceProfile();
        var session = new ExpressionPreferenceFeedbackSession();

        session.Start(ApplicationMode.Polish, "正式材料", "正式", enabled: true);

        Assert.True(session.Complete(profile, new StructuredPreferenceService(), accepted: true, rejected: false));
        var acceptedStyle = Assert.Single(profile.InteractionSignals["polish|正式材料"].AcceptedOutputStyles);
        Assert.Equal("正式", acceptedStyle.Key);
        Assert.Equal(1, acceptedStyle.Value);
        Assert.Null(new ExpressionPreferenceSuggestionService().GetLengthCandidate(profile, ApplicationMode.Polish, "正式材料"));
    }

    [Fact]
    public void ExplicitStyleSettingChoice_IsRecordedBySelectedValue()
    {
        var profile = new ExpressionPreferenceProfile();

        new StructuredPreferenceService().RecordStyleChoice(profile, "克制");

        Assert.Equal(1, profile.StyleChoiceCount);
        Assert.Equal(1, profile.StyleChoiceUsage["克制"]);
    }

    [Fact]
    public void AcceptedRemovalOfKnownCannedPhrase_StoresOnlyScopedCountAndNotOutputText()
    {
        var profile = new ExpressionPreferenceProfile();
        var session = new ExpressionPreferenceFeedbackSession();
        const string generated = "private-generated-您好，首先说明进度。希望以上内容对您有所帮助。";
        const string edited = "private-edited-您好，说明进度。";

        session.Start(ApplicationMode.Polish, "职场沟通", "自然", enabled: true);
        session.RecordEdit(generated, edited);
        Assert.True(session.Complete(profile, new StructuredPreferenceService(), accepted: true, rejected: false));

        var signals = profile.InteractionSignals["polish|职场沟通"];
        Assert.Equal(1, signals.AcceptedRemovedCannedExpressions["首先"]);
        Assert.Equal(1, signals.AcceptedRemovedCannedExpressions["希望以上内容对您有所帮助"]);
        Assert.Empty(signals.RejectedRemovedCannedExpressions);
        var serialized = System.Text.Json.JsonSerializer.Serialize(profile);
        Assert.DoesNotContain("private-generated", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("private-edited", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("说明进度", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectedOutput_RecordsRemovedCannedPhraseAsOpposingEvidence()
    {
        var profile = new ExpressionPreferenceProfile();
        var session = new ExpressionPreferenceFeedbackSession();

        session.Start(ApplicationMode.Polish, "公开发布", "自然", enabled: true);
        session.RecordEdit("首先说明活动信息。其余事实。", "说明活动信息。其余事实。");
        Assert.True(session.Complete(profile, new StructuredPreferenceService(), accepted: false, rejected: true));

        var signals = profile.InteractionSignals["polish|公开发布"];
        Assert.Equal(1, signals.RejectedRemovedCannedExpressions["首先"]);
        Assert.Empty(signals.AcceptedRemovedCannedExpressions);
    }

    [Fact]
    public void LatestEditSnapshot_DoesNotSuggestPhraseRemovedOnlyInAnEarlierDraft()
    {
        var profile = new ExpressionPreferenceProfile();
        var session = new ExpressionPreferenceFeedbackSession();

        session.Start(ApplicationMode.Polish, "职场沟通", "自然", enabled: true);
        session.RecordEdit("首先说明进度。", "说明进度。");
        session.RecordEdit("首先说明进度。", "首先说明进度。");
        Assert.True(session.Complete(profile, new StructuredPreferenceService(), accepted: true, rejected: false));

        Assert.Empty(profile.InteractionSignals["polish|职场沟通"].AcceptedRemovedCannedExpressions);
    }
}
