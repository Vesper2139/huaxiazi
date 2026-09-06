using System.Net;
using System.Net.Http;
using System.Text;
using System.IO;
using Huaxiazi.Models;
using Huaxiazi.Services;
using Huaxiazi.ViewModels;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class CompanionGenerationIntegrationTests : IDisposable
{
    private readonly AppSettings _originalSettings = App.Settings.Clone();

    public void Dispose() => App.ReplaceSettings(_originalSettings);

    [Fact]
    public async Task PolishWorkflow_LocalModeDoesNotDecorateTheExistingPrompt()
    {
        App.ReplaceSettings(new AppSettings { CompanionDriverMode = CompanionDriverMode.Local, IncognitoMode = true });
        var request = new PolishRequest { OriginalText = "帮我改写这句话" };
        var builder = new PolishPromptBuilderService();
        var client = new CapturingClient("{\"kind\":\"final\",\"content\":\"改写结果\"}");
        var root = Path.Combine(Path.GetTempPath(), "CompanionLocalWorkflow_" + Guid.NewGuid().ToString("N"));

        try
        {
            var result = await new PolishWorkflowService(client, builder, new ArchiveService(root))
                .ExecuteAsync(request, false, false, null, DateTimeOffset.UtcNow);

            Assert.Equal(builder.BuildSystemPrompt(request, false), client.SystemPrompts.Single());
            Assert.Null(result.CompanionEmotion);
            Assert.Equal(1, client.Calls);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task PolishWorkflow_EmotionModeUsesTheSameCallAndReturnsTheAcceptedHint()
    {
        App.ReplaceSettings(new AppSettings { CompanionDriverMode = CompanionDriverMode.EmotionAssistant, IncognitoMode = true });
        var response = "{\"kind\":\"final\",\"content\":\"我理解你的顾虑。\"}\n" +
                       "<HUAXIAZI_EMOTION>{\"emotion\":\"Supportive\",\"intensity\":0.7}</HUAXIAZI_EMOTION>";
        var client = new CapturingClient(response);
        var root = Path.Combine(Path.GetTempPath(), "CompanionEmotionWorkflow_" + Guid.NewGuid().ToString("N"));

        try
        {
            var result = await new PolishWorkflowService(client, new PolishPromptBuilderService(), new ArchiveService(root))
                .ExecuteAsync(new PolishRequest { OriginalText = "我有点担心延期" }, false, false, null, DateTimeOffset.UtcNow);

            Assert.Equal(1, client.Calls);
            Assert.Contains("HUAXIAZI_EMOTION", client.SystemPrompts.Single());
            Assert.Equal("我理解你的顾虑。", result.Response.Content);
            Assert.Equal(AssistantEmotionKind.Supportive, result.CompanionEmotion?.Emotion);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task AIService_BadRequestThrowsStructuredFailureWithoutPersistingProviderDetails()
    {
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            ReasonPhrase = "Bad Request",
            Content = new StringContent("{\"error\":{\"message\":\"invalid input\"}}", Encoding.UTF8, "application/json")
        };
        using var service = new AIService(new ProviderProfile(), "test-key", new StaticHandler(response));

        var error = await Assert.ThrowsAsync<GenerationFailureException>(
            () => service.GenerateAsync("system", "user"));

        Assert.Equal(GenerationFailureKind.RequestRejected, error.Kind);
        Assert.Equal(HttpStatusCode.BadRequest, error.HttpStatusCode);
        Assert.DoesNotContain("invalid input", error.Message);
    }

    [Fact]
    public async Task PromptOptimization_EmotionModeUsesTheAcceptedResultWithoutAnotherRequest()
    {
        App.ReplaceSettings(new AppSettings { CompanionDriverMode = CompanionDriverMode.EmotionAssistant });
        var client = new CapturingClient(
            "请生成一份结构清晰、可直接执行的项目周报。\n" +
            "<HUAXIAZI_EMOTION>{\"emotion\":\"Encouraging\",\"intensity\":0.6}</HUAXIAZI_EMOTION>");
        var request = PromptRequest.Create("请生成一份项目周报", PromptCategory.General, PromptDepth.Standard);
        var plan = new ProfessionalizationPlanner().Create(new ProfessionalizationRequest
        {
            Input = request.UserInput,
            Mode = ApplicationMode.PromptOptimize,
            Category = request.Category,
            Depth = request.Depth
        });

        var result = await new PromptOptimizationWorkflowService(client, new PromptBuilderService())
            .ExecuteAsync(request, plan);

        Assert.Equal(1, client.Calls);
        Assert.Contains("HUAXIAZI_EMOTION", client.SystemPrompts.Single());
        Assert.DoesNotContain("HUAXIAZI_EMOTION", result.Content);
        Assert.Equal(AssistantEmotionKind.Encouraging, result.CompanionEmotion?.Emotion);
    }

    [Theory]
    [InlineData(AssistantEmotionKind.Neutral, CompanionVisualState.Idle)]
    [InlineData(AssistantEmotionKind.Attentive, CompanionVisualState.Listening)]
    [InlineData(AssistantEmotionKind.Supportive, CompanionVisualState.Happy)]
    [InlineData(AssistantEmotionKind.Encouraging, CompanionVisualState.Happy)]
    [InlineData(AssistantEmotionKind.Concerned, CompanionVisualState.Warning)]
    [InlineData(AssistantEmotionKind.Cautious, CompanionVisualState.Curious)]
    public void AssistantEmotionMapsOnlyToSafeSemanticVisualStates(
        AssistantEmotionKind emotion,
        CompanionVisualState expected)
    {
        Assert.Equal(expected, CompanionEmotionMapper.ToVisualState(new AssistantEmotionHint(emotion, 0.7)));
    }

    [Fact]
    public async Task MainViewModel_KeepsStructuredFailureForLogsWhileShowingReadableFeedback()
    {
        App.ReplaceSettings(new AppSettings { IncognitoMode = true });
        var root = Path.Combine(Path.GetTempPath(), "CompanionFailureVm_" + Guid.NewGuid().ToString("N"));
        var failure = new GenerationFailureException(
            GenerationFailureKind.Authentication,
            "HTTP 401：API Key 无效。",
            HttpStatusCode.Unauthorized,
            false);
        var vm = new MainViewModel(
            new WorkspaceDraftService(root),
            new ArchiveService(root),
            (_, _) => new ThrowingClient(failure))
        {
            UserInput = "请润色这句话"
        };

        try
        {
            await vm.OptimizeCommand.ExecuteAsync(null);

            Assert.Same(failure, vm.LastGenerationFailure);
            Assert.True(vm.HasError);
            Assert.Contains("API Key", vm.OperationalNotice);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class CapturingClient(params string[] responses) : ITextGenerationClient
    {
        private int _index;
        public int Calls => _index;
        public List<string> SystemPrompts { get; } = [];

        public Task<string> GenerateAsync(string systemPrompt, string userInput, CancellationToken cancellationToken = default)
        {
            SystemPrompts.Add(systemPrompt);
            var response = responses[Math.Min(_index, responses.Length - 1)];
            _index++;
            return Task.FromResult(response);
        }
    }

    private sealed class StaticHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }

    private sealed class ThrowingClient(Exception error) : ITextGenerationClient
    {
        public Task<string> GenerateAsync(string systemPrompt, string userInput, CancellationToken cancellationToken = default) =>
            Task.FromException<string>(error);
    }
}
