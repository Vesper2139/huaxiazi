using System.Text.Json;
using Huaxiazi.BlindEvaluationRunner;
using Huaxiazi.DatasetBuilder;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class BlindWorkflowExecutorTests
{
    [Theory]
    [InlineData("polish")]
    [InlineData("prompt_optimize")]
    public void Create_AppliesDiagnosticPreferenceOverrideToBothRequestAndPlan(string task)
    {
        var record = new BlindEvaluationRecord
        {
            Id = "preference-contrast",
            Task = task,
            SemanticFamilyId = "family-preference-contrast",
            Source = new("project_synthetic_legacy", "internal-v1", "synthetic"),
            Input = "请周五前回复是否参加。",
            Constraints = ["保留周五和是否参加这两个事实"],
            RiskLevel = "low",
            Split = "development",
            ExpectedDecision = "produce"
        };

        var request = BlindWorkflowRequestFactory.Create(record, diagnosticPreferenceInstructions:
            "诊断偏好：自然简洁，并保留原文语气。\n本轮明确要求优先于该偏好。");

        var preference = task == "polish"
            ? request.PolishRequest!.PreferenceInstructions
            : request.PromptRequest!.PreferenceInstructions;
        Assert.Contains("诊断偏好：自然简洁", preference, StringComparison.Ordinal);
        Assert.Contains("用户本次明确要求", request.Plan.StrategyInstructions, StringComparison.Ordinal);
        Assert.Contains("周五和是否参加", request.Plan.StrategyInstructions, StringComparison.Ordinal);
        Assert.Contains("本地风格偏好", request.Plan.StrategyInstructions, StringComparison.Ordinal);
        Assert.Contains("自然简洁", request.Plan.StrategyInstructions, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_RejectsDiagnosticPreferenceWhenRecordAlreadyContainsPreference()
    {
        var record = new BlindEvaluationRecord
        {
            Id = "preference-confound",
            Task = "polish",
            SemanticFamilyId = "family-preference-confound",
            Source = new("project_synthetic_legacy", "internal-v1", "synthetic"),
            Input = "请周五前回复。",
            RiskLevel = "low",
            Split = "development",
            ExpectedDecision = "produce",
            Context = new Dictionary<string, JsonElement>
            {
                ["preference_instructions"] = JsonSerializer.SerializeToElement("既有样本偏好")
            }
        };

        Assert.Throws<ArgumentException>(() => BlindWorkflowRequestFactory.Create(record,
            diagnosticPreferenceInstructions: "对照实验偏好"));
    }

    [Fact]
    public async Task ExecuteConversationAsync_ReplaysEachTurnAndFeedsPriorAssistantOutputForward()
    {
        var record = new BlindEvaluationRecord
        {
            Id = "conversation-17", Task = "polish", SemanticFamilyId = "family-conversation-17",
            Source = new("project_owned", "dataset-v1", "synthetic"),
            ConversationId = "conversation-17",
            Turns = [new(1, "写一句简短问候。"), new(2, "改成祝福语，不要问候。")],
            Input = "改成祝福语，不要问候。", InputStyle = "colloquial", Constraints = ["以最后一轮更正为准"],
            RiskLevel = "low", Split = "development", ExpectedDecision = "produce",
            Annotations = new(["周五开放"], "专业、清楚", [], false, []),
            HumanReview = new(["reviewer-a", "reviewer-b"], "accepted")
        };
        var client = new CapturingSequenceClient(
            "{\"kind\":\"final\",\"content\":\"写一句简短问候。\"}",
            "{\"kind\":\"final\",\"content\":\"改成祝福语，不要问候。\"}");

        var prediction = await BlindWorkflowExecutor.ExecuteConversationAsync(record, client);

        Assert.Equal("completed", prediction.Status);
        Assert.Equal("改成祝福语，不要问候。", prediction.Output);
        Assert.Equal(2, client.UserMessages.Count);
        using var finalRequest = JsonDocument.Parse(client.UserMessages[1]);
        Assert.Equal("改成祝福语，不要问候。", finalRequest.RootElement.GetProperty("current_user_input").GetString());
        Assert.Equal(2, finalRequest.RootElement.GetProperty("conversation_history").GetArrayLength());
        Assert.Equal("写一句简短问候。", finalRequest.RootElement.GetProperty("conversation_history")[1].GetProperty("content").GetString());
    }

    [Theory]
    [InlineData("polish")]
    [InlineData("prompt_optimize")]
    public async Task ExecuteAsync_RunsTheSelectedProductWorkflowWithoutCopyingGold(string task)
    {
        var record = new BlindEvaluationRecord
        {
            Id = "blind-17",
            Task = task,
            SemanticFamilyId = "family-a",
            Source = new("project_owned", "dataset-v1", "synthetic"),
            Input = "写一个研究计划。",
            Constraints = ["保留研究计划这个目标"],
            RiskLevel = "low",
            Split = "development",
            ExpectedDecision = "produce",
            ReferenceOutput = "GOLD_REFERENCE_SENTINEL",
            Annotations = new(["GOLD_FACT_SENTINEL"], "GOLD_TONE_SENTINEL", [], false, []),
            HumanReview = new(["PRIVATE_REVIEWER_SENTINEL_A", "PRIVATE_REVIEWER_SENTINEL_B"], "accepted"),
            Context = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["reference_output"] = JsonSerializer.SerializeToElement("GOLD_CONTEXT_SENTINEL"),
                ["human_review"] = JsonSerializer.SerializeToElement("PRIVATE_REVIEW_CONTEXT_SENTINEL")
            }
        };
        var client = new CapturingClient(task == "polish"
            ? "{\"kind\":\"final\",\"content\":\"写一个研究计划。\",\"scenario\":\"\",\"topic\":\"\"}"
            : "{\"answer\":\"研究计划：说明目标、方法和时间安排。\"}");

        var workflow = BlindWorkflowRequestFactory.Create(record);
        var snapshot = BlindPromptSnapshotCompiler.Compile(record);
        var prediction = await BlindWorkflowExecutor.ExecuteAsync(workflow, client);

        Assert.Equal("blind-17", prediction.Id);
        Assert.Equal("blind-17", workflow.RecordId);
        Assert.Equal(task, workflow.Task);
        Assert.Equal("development", workflow.Split);
        Assert.DoesNotContain(typeof(BlindEvaluationRecord),
            typeof(BlindWorkflowRequest).GetProperties().Select(property => property.PropertyType));
        Assert.NotEmpty(client.SystemPrompts);
        Assert.Equal(snapshot.SystemPrompt, client.SystemPrompts[0]);
        Assert.Equal(snapshot.UserMessage, client.UserMessages[0]);
        var providerPayload = string.Join("\n", client.SystemPrompts.Concat(client.UserMessages));
        Assert.DoesNotContain("GOLD_REFERENCE_SENTINEL", providerPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("GOLD_FACT_SENTINEL", providerPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("GOLD_TONE_SENTINEL", providerPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("GOLD_CONTEXT_SENTINEL", providerPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_REVIEWER_SENTINEL", providerPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_REVIEW_CONTEXT_SENTINEL", providerPayload, StringComparison.Ordinal);
        Assert.Contains(prediction.Status, new[] { "completed", "blocked", "invalid" });
    }

    [Theory]
    [InlineData(true, "needs_clarification")]
    [InlineData(false, "invalid")]
    public async Task ExecuteAsync_PromptOptimizationWithMissingTaskInfoDoesNotCallModel(bool clarificationEnabled, string expectedStatus)
    {
        var record = new BlindEvaluationRecord
        {
            Id = "prompt-missing-info",
            Task = "prompt_optimize",
            SemanticFamilyId = "family-prompt-missing-info",
            Source = new("project_owned", "dataset-v1", "synthetic"),
            Input = "帮我优化一下",
            RiskLevel = "low",
            Split = "development",
            ExpectedDecision = "clarify",
            Context = new Dictionary<string, JsonElement>
            {
                ["clarification_enabled"] = JsonSerializer.SerializeToElement(clarificationEnabled)
            }
        };
        var client = new CapturingClient("不应生成");
        var workflow = BlindWorkflowRequestFactory.Create(record);

        var prediction = await BlindWorkflowExecutor.ExecuteAsync(workflow, client);

        Assert.Equal(expectedStatus, prediction.Status);
        Assert.Empty(client.SystemPrompts);
        if (clarificationEnabled)
            Assert.NotEmpty(prediction.ClarificationQuestions);
    }

    private sealed class CapturingClient(string response) : ITextGenerationClient, IStructuredTextGenerationClient
    {
        public List<string> SystemPrompts { get; } = [];
        public List<string> UserMessages { get; } = [];

        public Task<string> GenerateAsync(string systemPrompt, string userInput, CancellationToken cancellationToken = default)
        {
            SystemPrompts.Add(systemPrompt);
            UserMessages.Add(userInput);
            return Task.FromResult(response);
        }

        public Task<string> GenerateStructuredAsync(string systemPrompt, string userInput, string jsonSchema, CancellationToken cancellationToken = default)
        {
            SystemPrompts.Add(systemPrompt);
            UserMessages.Add(userInput);
            return Task.FromResult(response);
        }
    }

    private sealed class CapturingSequenceClient(params string[] responses) : ITextGenerationClient
    {
        private readonly Queue<string> _responses = new(responses);
        public List<string> UserMessages { get; } = [];

        public Task<string> GenerateAsync(string systemPrompt, string userInput, CancellationToken cancellationToken = default)
        {
            UserMessages.Add(userInput);
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
