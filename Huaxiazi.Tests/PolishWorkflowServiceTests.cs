using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Huaxiazi.Models;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class PolishWorkflowServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "HuaxiaziWorkflow_" + Guid.NewGuid().ToString("N"));

    private sealed class StaticClient(string response) : ITextGenerationClient
    {
        public Task<string> GenerateAsync(string systemPrompt, string userInput, CancellationToken cancellationToken = default) =>
            Task.FromResult(response);
    }

    private sealed class SequenceClient(params string[] responses) : ITextGenerationClient
    {
        private int _index;
        public int Calls => _index;

        public Task<string> GenerateAsync(string systemPrompt, string userInput, CancellationToken cancellationToken = default)
        {
            var response = responses[Math.Min(_index, responses.Length - 1)];
            _index++;
            return Task.FromResult(response);
        }
    }

    private sealed class StructuredClient(string response) : ITextGenerationClient, IStructuredTextGenerationClient
    {
        public int StructuredCalls { get; private set; }
        public string Schema { get; private set; } = string.Empty;

        public Task<string> GenerateAsync(string systemPrompt, string userInput, CancellationToken cancellationToken = default) =>
            Task.FromResult("plain-text-path-must-not-be-used");

        public Task<string> GenerateStructuredAsync(string systemPrompt, string userInput, string jsonSchema, CancellationToken cancellationToken = default)
        {
            StructuredCalls++;
            Schema = jsonSchema;
            return Task.FromResult(response);
        }
    }

    private sealed class SchemaRejectingSequenceClient(params string[] textResponses) : ITextGenerationClient, IStructuredTextGenerationClient
    {
        private int _textIndex;
        public int StructuredCalls { get; private set; }
        public int PlainCalls { get; private set; }

        public Task<string> GenerateAsync(string systemPrompt, string userInput, CancellationToken cancellationToken = default)
        {
            PlainCalls++;
            return Task.FromResult(textResponses[Math.Min(_textIndex++, textResponses.Length - 1)]);
        }

        public Task<string> GenerateStructuredAsync(string systemPrompt, string userInput, string jsonSchema, CancellationToken cancellationToken = default)
        {
            StructuredCalls++;
            return Task.FromException<string>(new GenerationFailureException(
                GenerationFailureKind.StructuredOutputUnsupported, "schema unsupported", System.Net.HttpStatusCode.BadRequest));
        }
    }

    [Fact]
    public async Task ExecuteAsync_ArchivesProviderProfileAndCompleteContext()
    {
        var archive = new ArchiveService(_root);
        var request = new PolishRequest
        {
            OriginalText = "原文",
            Recipient = "客户",
            Channel = "邮件",
            Purpose = "说明",
            Formality = "正式",
            Scenario = "职场沟通",
            OutputStyle = "自然",
            Persona = "产品经理",
            CustomStyleInstructions = "简洁",
            ModelProfileId = "profile-a",
            ModelName = "model-a"
        };

        var result = await new PolishWorkflowService(
            new StaticClient("{\"kind\":\"final\",\"content\":\"成稿\"}"),
            new PolishPromptBuilderService(), archive)
            .ExecuteAsync(request, false, true, null, DateTimeOffset.UtcNow);

        Assert.NotNull(result.SavedRevision);
        var saved = result.SavedRevision!;
        Assert.Equal("profile-a", saved.ModelProfileId);
        Assert.Equal("model-a", saved.ModelName);
        using var context = JsonDocument.Parse(saved.ContextJson);
        Assert.Equal("产品经理", context.RootElement.GetProperty("Persona").GetString());
        Assert.Equal("简洁", context.RootElement.GetProperty("CustomStyleInstructions").GetString());
        Assert.Equal(JsonValueKind.Array, context.RootElement.GetProperty("selectedSkillIds").ValueKind);
        Assert.Equal(JsonValueKind.Object, context.RootElement.GetProperty("skillWeights").ValueKind);
    }

    [Fact]
    public async Task ExecuteAsync_RequiresArchiveBeforeProviderCallWhenAutoArchiveIsEnabled()
    {
        var client = new SequenceClient("{\"kind\":\"final\",\"content\":\"成稿\"}");
        var workflow = new PolishWorkflowService(client, new PolishPromptBuilderService(), archive: null);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.ExecuteAsync(
            new PolishRequest { OriginalText = "原文" },
            clarificationEnabled: false,
            autoArchive: true,
            existingItemId: null,
            createdAt: DateTimeOffset.UtcNow));

        Assert.Contains("ArchiveService", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_FinalResponse_AutoArchivesOneRevision()
    {
        var client = new StaticClient("""
            {"kind":"final","scenario":"职场沟通","topic":"进度说明","content":"我会晚两天完成，并及时同步进展。"}
            """);
        var archive = new ArchiveService(_root);
        var workflow = new PolishWorkflowService(client, new PolishPromptBuilderService(), archive);

        var result = await workflow.ExecuteAsync(
            new PolishRequest { OriginalText = "我要晚两天", Channel = "微信" },
            clarificationEnabled: true,
            autoArchive: true,
            existingItemId: null,
            createdAt: new DateTimeOffset(2026, 8, 11, 17, 0, 0, TimeSpan.FromHours(8)));

        Assert.Equal(PolishResponseKind.Final, result.Response.Kind);
        Assert.NotNull(result.SavedRevision);
        Assert.Equal("我要晚两天", result.SavedRevision!.OriginalText);
        Assert.Equal("我会晚两天完成，并及时同步进展。", File.ReadAllText(result.SavedRevision.FilePath));
    }

    [Fact]
    public async Task ExecuteAsync_UsesNativePolishSchemaAndKeepsResponseParsing()
    {
        var client = new StructuredClient("""
            {"kind":"final","scenario":"职场沟通","topic":"进度","content":"我会及时同步进度。","questions":[]}
            """);
        var workflow = new PolishWorkflowService(client, new PolishPromptBuilderService(), archive: null);

        var result = await workflow.ExecuteAsync(
            new PolishRequest { OriginalText = "我会同步进度" },
            clarificationEnabled: true,
            autoArchive: false,
            existingItemId: null,
            createdAt: DateTimeOffset.UtcNow);

        Assert.Equal(1, client.StructuredCalls);
        using var schema = JsonDocument.Parse(client.Schema);
        Assert.Contains(schema.RootElement.GetProperty("properties").GetProperty("questions").GetProperty("type").EnumerateArray(), type => type.GetString() == "array");
        Assert.Equal(new[] { "final", "needs_clarification" }, schema.RootElement.GetProperty("properties").GetProperty("kind").GetProperty("enum").EnumerateArray().Select(value => value.GetString()).OrderBy(value => value, StringComparer.Ordinal));
        Assert.Equal(PolishResponseKind.Final, result.Response.Kind);
        Assert.Equal("我会及时同步进度。", result.Response.Content);
    }

    [Fact]
    public async Task ExecuteAsync_RepairsAfterSchemaDowngradeWithoutRetryingRejectedSchema()
    {
        var client = new SchemaRejectingSequenceClient(
            "not-json",
            "{\"kind\":\"final\",\"content\":\"我会及时同步进度。\"}");
        var workflow = new PolishWorkflowService(client, new PolishPromptBuilderService(), archive: null);

        var result = await workflow.ExecuteAsync(
            new PolishRequest { OriginalText = "我会同步进度" },
            clarificationEnabled: true,
            autoArchive: false,
            existingItemId: null,
            createdAt: DateTimeOffset.UtcNow);

        Assert.Equal(PolishResponseKind.Final, result.Response.Kind);
        Assert.True(result.WasRepaired);
        Assert.Equal("我会及时同步进度。", result.Response.Content);
        Assert.Equal(1, client.StructuredCalls);
        Assert.Equal(2, client.PlainCalls);
    }

    [Fact]
    public async Task ExecuteAsync_EmotionModeKeepsEmotionMetadataInsideStructuredResponse()
    {
        var client = new StructuredClient("""
            {"kind":"final","scenario":"","topic":"","content":"我会及时同步。","questions":[],"companion_emotion":"supportive","companion_intensity":0.7}
            """);
        var workflow = new PolishWorkflowService(
            client,
            new PolishPromptBuilderService(),
            archive: null,
            companionModeProvider: () => CompanionDriverMode.EmotionAssistant);

        var result = await workflow.ExecuteAsync(
            new PolishRequest { OriginalText = "我会同步进度" },
            clarificationEnabled: true,
            autoArchive: false,
            existingItemId: null,
            createdAt: DateTimeOffset.UtcNow);

        Assert.Equal(1, client.StructuredCalls);
        using var schema = JsonDocument.Parse(client.Schema);
        Assert.Contains(schema.RootElement.GetProperty("properties").GetProperty("companion_emotion").GetProperty("enum").EnumerateArray(), value => value.GetString() == "Supportive");
        Assert.Contains(schema.RootElement.GetProperty("properties").GetProperty("companion_intensity").GetProperty("type").EnumerateArray(), value => value.GetString() == "number");
        Assert.False(schema.RootElement.GetProperty("properties").GetProperty("companion_intensity").TryGetProperty("minimum", out _));
        Assert.False(schema.RootElement.GetProperty("properties").GetProperty("companion_intensity").TryGetProperty("maximum", out _));
        Assert.Equal(PolishResponseKind.Final, result.Response.Kind);
        Assert.Equal(AssistantEmotionKind.Supportive, result.CompanionEmotion?.Emotion);
        Assert.Equal(0.7, result.CompanionEmotion?.Intensity);
    }

    [Fact]
    public async Task ExecuteAsync_RepairsJsonThatPassesSchemaButHasUnknownDecision()
    {
        var client = new SequenceClient(
            "{\"kind\":\"unknown\",\"scenario\":\"\",\"topic\":\"\",\"content\":\"\",\"questions\":[]}",
            "{\"kind\":\"final\",\"scenario\":\"\",\"topic\":\"\",\"content\":\"已完成。\",\"questions\":[]}");
        var workflow = new PolishWorkflowService(client, new PolishPromptBuilderService(), archive: null);

        var result = await workflow.ExecuteAsync(
            new PolishRequest { OriginalText = "完成了" },
            clarificationEnabled: true,
            autoArchive: false,
            existingItemId: null,
            createdAt: DateTimeOffset.UtcNow);

        Assert.Equal(2, client.Calls);
        Assert.Equal(PolishResponseKind.Final, result.Response.Kind);
        Assert.Equal("已完成。", result.Response.Content);
        Assert.True(result.WasRepaired);
    }

    [Fact]
    public async Task ExecuteAsync_RespectsIndependentOriginalAndResultRetention()
    {
        var archive = new ArchiveService(_root);
        var result = await new PolishWorkflowService(
            new StaticClient("{\"kind\":\"final\",\"content\":\"隐私成稿\"}"),
            new PolishPromptBuilderService(), archive).ExecuteAsync(
                new PolishRequest { OriginalText = "敏感原文", SaveOriginalText = false, SaveOptimizedText = true },
                false, true, null, DateTimeOffset.UtcNow);

        Assert.NotNull(result.SavedRevision);
        Assert.Empty(result.SavedRevision!.OriginalText);
        Assert.Equal("隐私成稿", result.SavedRevision.FinalText);
    }

    [Fact]
    public async Task ExecuteAsync_ClarificationResponse_DoesNotArchive()
    {
        var client = new StaticClient("""
            {"kind":"needs_clarification","questions":["这段话发给谁？"]}
            """);
        var archive = new ArchiveService(_root);
        var workflow = new PolishWorkflowService(client, new PolishPromptBuilderService(), archive);

        var result = await workflow.ExecuteAsync(
            new PolishRequest { OriginalText = "帮我改一下" },
            clarificationEnabled: true,
            autoArchive: true,
            existingItemId: null,
            createdAt: DateTimeOffset.Now);

        Assert.Equal(PolishResponseKind.NeedsClarification, result.Response.Kind);
        Assert.Null(result.SavedRevision);
        Assert.Empty(archive.Search(string.Empty));
    }

    [Fact]
    public async Task ExecuteAsync_ClarifiesMissingProblemEvidenceBeforeCallingGenerationClient()
    {
        var client = new SequenceClient("{\"kind\":\"final\",\"content\":\"我会同步当前进展。\"}");
        var workflow = new PolishWorkflowService(client, new PolishPromptBuilderService(), archive: null);

        var result = await workflow.ExecuteAsync(
            new PolishRequest
            {
                OriginalText = "这件事需要尽快处理。问题分析，语气自然一点。",
                Purpose = "问题分析"
            },
            clarificationEnabled: true,
            autoArchive: false,
            existingItemId: null,
            createdAt: DateTimeOffset.UtcNow);

        Assert.Equal(PolishResponseKind.NeedsClarification, result.Response.Kind);
        Assert.Contains(result.Response.Questions, question => question.Contains("具体问题", StringComparison.Ordinal));
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_WhenClarificationIsDisabledAndFactsAreMissing_FailsBeforeGeneration()
    {
        var client = new SequenceClient("{\"kind\":\"final\",\"content\":\"这件事我已处理，后续同步进度。\"}");
        var workflow = new PolishWorkflowService(client, new PolishPromptBuilderService(), archive: null);

        var result = await workflow.ExecuteAsync(
            new PolishRequest
            {
                OriginalText = "这件事需要尽快处理。",
                Purpose = "问题分析"
            },
            clarificationEnabled: false,
            autoArchive: false,
            existingItemId: null,
            createdAt: DateTimeOffset.UtcNow);

        Assert.Equal(PolishResponseKind.Invalid, result.Response.Kind);
        Assert.Contains(result.ValidationIssues, issue => issue.Contains("具体问题或异常表现", StringComparison.Ordinal));
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidResponse_DoesNotArchiveRawText()
    {
        var archive = new ArchiveService(_root);
        var workflow = new PolishWorkflowService(
            new StaticClient("模型没有按协议返回"),
            new PolishPromptBuilderService(),
            archive);

        var result = await workflow.ExecuteAsync(
            new PolishRequest { OriginalText = "原文" },
            clarificationEnabled: true,
            autoArchive: true,
            existingItemId: null,
            createdAt: DateTimeOffset.Now);

        Assert.Equal(PolishResponseKind.Invalid, result.Response.Kind);
        Assert.Null(result.SavedRevision);
        Assert.Empty(archive.Search(string.Empty));
    }

    [Fact]
    public async Task ExecuteAsync_FidelityFailure_RepairsOnceBeforeArchiving()
    {
        var client = new SequenceClient(
            "{\"kind\":\"final\",\"content\":\"项目交付时间会调整。\"}",
            "{\"kind\":\"final\",\"scenario\":\"职场沟通\",\"topic\":\"延期说明\",\"content\":\"原计划8月20日交付，目前可能顺延2天。\"}");
        var archive = new ArchiveService(_root);
        var intelligence = new SmartContextAnalyzer().Analyze("原定8月20日交付，可能晚2天。");

        var result = await new PolishWorkflowService(client, new PolishPromptBuilderService(), archive)
            .ExecuteAsync(
                new PolishRequest
                {
                    OriginalText = "原定8月20日交付，可能晚2天。",
                    Intelligence = intelligence
                },
                false, true, null, DateTimeOffset.UtcNow);

        Assert.Equal(2, client.Calls);
        Assert.Equal(PolishResponseKind.Final, result.Response.Kind);
        Assert.True(result.WasRepaired);
        Assert.NotNull(result.SavedRevision);
        Assert.Equal("原计划8月20日交付，目前可能顺延2天。", result.SavedRevision!.FinalText);
    }

    [Fact]
    public async Task ExecuteAsync_FailedRepair_ReturnsQualityIssuesAndDoesNotArchive()
    {
        var client = new SequenceClient(
            "{\"kind\":\"final\",\"content\":\"交付时间会调整。\"}",
            "{\"kind\":\"final\",\"content\":\"时间需要调整。\"}");
        var archive = new ArchiveService(_root);
        var original = "原定8月20日交付，可能晚2天。";

        var result = await new PolishWorkflowService(client, new PolishPromptBuilderService(), archive)
            .ExecuteAsync(new PolishRequest
            {
                OriginalText = original,
                Intelligence = new SmartContextAnalyzer().Analyze(original)
            }, false, true, null, DateTimeOffset.UtcNow);

        Assert.Equal(PolishResponseKind.Invalid, result.Response.Kind);
        Assert.Contains(result.ValidationIssues, issue => issue.Contains("8月20日"));
        Assert.Null(result.SavedRevision);
        Assert.Empty(archive.Search(string.Empty));
    }

    [Fact]
    public async Task ExecuteAsync_AttackingInputCannotBeReversedIntoPraise()
    {
        var client = new SequenceClient(
            "{\"kind\":\"final\",\"content\":\"你真可爱\"}",
            "{\"kind\":\"final\",\"content\":\"你的说法让我很不舒服，请停止这种不尊重的表达。\"}");
        var workflow = new PolishWorkflowService(client, new PolishPromptBuilderService(), new ArchiveService(_root));

        var result = await workflow.ExecuteAsync(
            new PolishRequest { OriginalText = "你属猪" },
            clarificationEnabled: false,
            autoArchive: false,
            existingItemId: null,
            createdAt: DateTimeOffset.UtcNow);

        Assert.Equal(2, client.Calls);
        Assert.True(result.WasRepaired);
        Assert.Equal("你的说法让我很不舒服，请停止这种不尊重的表达。", result.Response.Content);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
