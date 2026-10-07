using Huaxiazi.BlindEvaluationRunner;
using Huaxiazi.DatasetBuilder;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class BlindPromptSnapshotCompilerTests
{
    [Fact]
    public void SplitSelector_DevelopmentExcludesFrozenTestRows()
    {
        var records = new[] { Gold("polish") with { Split = "development" }, Gold("polish") with { Id = "test", Split = "frozen_test" } };

        var selected = BlindSnapshotSplitSelector.Select(records, "development", confirmFrozenTestLocked: false);

        Assert.Equal(new[] { "sample-1" }, selected.Select(record => record.Id));
    }

    [Fact]
    public void SplitSelector_RequiresExplicitLockConfirmationForFrozenTest()
    {
        var records = new[] { Gold("polish") with { Split = "development" }, Gold("polish") with { Id = "test", Split = "frozen_test" } };

        Assert.Throws<InvalidOperationException>(() => BlindSnapshotSplitSelector.Select(records, "frozen_test", confirmFrozenTestLocked: false));
        var selected = BlindSnapshotSplitSelector.Select(records, "frozen_test", confirmFrozenTestLocked: true);
        Assert.Equal(new[] { "test" }, selected.Select(record => record.Id));
    }

    [Fact]
    public void SplitSelector_AllRequiresLockConfirmationWhenFrozenTestRowsAreIncluded()
    {
        var records = new[] { Gold("polish") with { Split = "development" }, Gold("polish") with { Id = "test", Split = "frozen_test" } };

        Assert.Throws<InvalidOperationException>(() => BlindSnapshotSplitSelector.Select(records, "all", confirmFrozenTestLocked: false));
        Assert.Equal(2, BlindSnapshotSplitSelector.Select(records, "all", confirmFrozenTestLocked: true).Count);
    }

    [Fact]
    public void Compile_Polish_UsesProductionPromptBuilderAndOnlyRequestContext()
    {
        var record = Gold("polish") with
        {
            Input = "周五交付三份报告，请帮我回复。",
            Constraints = ["保留周五和三份报告", "语气克制"],
            ReferenceOutput = "不得泄漏的参考成稿",
            Context = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["scenario"] = Json("职场沟通"),
                ["recipient"] = Json("项目组")
            }
        };
        var labels = new BlindEvaluationGoldLabelSet(
            record.Task, record.InputStyle, record.Constraints, record.RiskLevel,
            record.ExpectedDecision, "reviewer-secret-evidence", record.Annotations);
        record = record with
        {
            HumanReview = new BlindEvaluationHumanReview(
                ["reviewer-a", "reviewer-b"], "accepted", null,
                [new("reviewer-a", labels), new("reviewer-b", labels)])
        };

        var snapshot = BlindPromptSnapshotCompiler.Compile(record);
        var serializedSnapshot = System.Text.Json.JsonSerializer.Serialize(snapshot);

        Assert.Equal(record.Id, snapshot.Id);
        Assert.Equal("polish", snapshot.Task);
        Assert.Equal(record.Split, snapshot.Split);
        Assert.Equal(record.Input, snapshot.UserMessage);
        Assert.Contains("保留周五和三份报告", snapshot.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("项目组", snapshot.SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("不得泄漏的参考成稿", snapshot.SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("reviewer-a", snapshot.SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("reviewer-a", serializedSnapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("reviewer-secret-evidence", serializedSnapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("independent_annotations", serializedSnapshot, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_PromptOptimization_UsesProductionCategoryAndDepthContext()
    {
        var record = Gold("prompt_optimize") with
        {
            Context = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["category"] = Json("Research"),
                ["depth"] = Json("Detailed")
            }
        };

        var snapshot = BlindPromptSnapshotCompiler.Compile(record);

        Assert.Contains("学术研究", snapshot.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("详细", snapshot.SystemPrompt, StringComparison.Ordinal);
        Assert.Equal(record.Input, snapshot.UserMessage);
    }

    [Fact]
    public void Compile_MultiTurnPolish_CapturesEveryTurnAndPriorAssistantPlaceholder()
    {
        var record = Gold("polish") with
        {
            ConversationId = "conversation-1",
            Turns = [new(1, "通知大家周三开放。"), new(2, "改成周五开放。周三不要再写。")],
            Input = "改成周五开放。周三不要再写。"
        };

        var snapshot = BlindPromptSnapshotCompiler.Compile(record);

        Assert.Equal(2, snapshot.ConversationTurns?.Count);
        Assert.Equal("通知大家周三开放。", snapshot.ConversationTurns![0].UserMessage);
        using var secondTurn = System.Text.Json.JsonDocument.Parse(snapshot.ConversationTurns[1].UserMessage);
        Assert.Equal("改成周五开放。周三不要再写。", secondTurn.RootElement.GetProperty("current_user_input").GetString());
        Assert.Contains("上一轮助手回复", secondTurn.RootElement.GetProperty("conversation_history")[1].GetProperty("content").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PromptBundleHashInput_IncludesEarlierTurnPrompts()
    {
        var record = Gold("polish") with
        {
            ConversationId = "conversation-hash",
            Turns = [new(1, "先写一句问候。"), new(2, "请改成简短祝福。")],
            Input = "请改成简短祝福。"
        };
        var changedEarlierTurn = record with
        {
            Turns = [new(1, "先写一句正式问候。"), record.Turns![1]]
        };

        var originalHashInput = BlindPromptSnapshotCompiler.BuildBundleHashLine(BlindPromptSnapshotCompiler.Compile(record));
        var changedHashInput = BlindPromptSnapshotCompiler.BuildBundleHashLine(BlindPromptSnapshotCompiler.Compile(changedEarlierTurn));

        Assert.NotEqual(originalHashInput, changedHashInput);
    }

    [Fact]
    public void PromptBundleHashInput_IncludesOutputContractFingerprint()
    {
        var first = new BlindPromptSnapshot("sample-1", "polish", "development", "system", "user")
        {
            OutputContractSha256 = new string('a', 64)
        };
        var second = first with { OutputContractSha256 = new string('b', 64) };

        Assert.NotEqual(
            BlindPromptSnapshotCompiler.BuildBundleHashLine(first),
            BlindPromptSnapshotCompiler.BuildBundleHashLine(second));
    }

    [Fact]
    public void Compile_UsesDifferentOutputContractFingerprintForEmotionMetadataMode()
    {
        var local = BlindPromptSnapshotCompiler.Compile(Gold("polish"));
        var emotion = BlindPromptSnapshotCompiler.Compile(Gold("polish") with
        {
            Context = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["companion_driver_mode"] = Json("EmotionAssistant")
            }
        });

        Assert.Matches("^[a-f0-9]{64}$", local.OutputContractSha256);
        Assert.Matches("^[a-f0-9]{64}$", emotion.OutputContractSha256);
        Assert.NotEqual(local.OutputContractSha256, emotion.OutputContractSha256);
    }

    [Fact]
    public void Compile_RespectsTheCompanionPromptProtocolSetting()
    {
        var record = Gold("polish") with
        {
            Context = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["companion_driver_mode"] = Json("EmotionAssistant")
            }
        };

        var snapshot = BlindPromptSnapshotCompiler.Compile(record);

        Assert.Contains("<HUAXIAZI_EMOTION>", snapshot.SystemPrompt, StringComparison.Ordinal);
    }

    private static BlindEvaluationRecord Gold(string task) => new()
    {
        Id = "sample-1",
        Task = task,
        SemanticFamilyId = "family-1",
        Source = new("project_owned", "project-dataset-v1", "synthetic"),
        Input = "帮我写一个研究计划。",
        Constraints = ["不得编造事实"],
        RiskLevel = "low",
        Split = "development",
        ExpectedDecision = "produce",
        ReferenceOutput = "不得泄漏的参考成稿",
        Annotations = new(["gold fact"], "专业", ["分点"], false, []),
        HumanReview = new(["reviewer-a", "reviewer-b"], "accepted")
    };

    private static System.Text.Json.JsonElement Json(string value) =>
        System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(value)).RootElement.Clone();
}
