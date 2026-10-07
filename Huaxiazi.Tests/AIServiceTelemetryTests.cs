using System.Net;
using System.Net.Http;
using System.Text;
using Huaxiazi.Models;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class AIServiceTelemetryTests
{
    [Fact]
    public async Task GenerateAsync_ReportsOnlyProviderFinishReasonForSuccessfulResponse()
    {
        var telemetry = new List<ProviderRequestTelemetry>();
        using var client = new AIService(Profile(), "test-key", new StaticResponseHandler(
            "{\"id\":\"chatcmpl-stop\",\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"private-answer\"}}]}"),
            telemetryObserver: telemetry.Add);

        await client.GenerateAsync("private-system", "private-input");

        var observation = Assert.Single(telemetry);
        var finishReason = typeof(ProviderRequestTelemetry).GetProperty("FinishReason");
        Assert.NotNull(finishReason);
        Assert.Equal("stop", finishReason!.GetValue(observation));
        var serialized = System.Text.Json.JsonSerializer.Serialize(observation);
        Assert.DoesNotContain("private", serialized, StringComparison.Ordinal);
        Assert.Contains("stop", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_IncompleteResponseTelemetryRetainsOnlyLengthFinishReason()
    {
        var telemetry = new List<ProviderRequestTelemetry>();
        using var client = new AIService(Profile(), "test-key", new StaticResponseHandler(
            "{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"private-partial-answer\"}}]}"),
            telemetryObserver: telemetry.Add);

        var exception = await Assert.ThrowsAsync<GenerationFailureException>(() => client.GenerateAsync("private-system", "private-input"));

        Assert.Equal(GenerationFailureKind.Incomplete, exception.Kind);
        var observation = Assert.Single(telemetry);
        var finishReason = typeof(ProviderRequestTelemetry).GetProperty("FinishReason");
        Assert.NotNull(finishReason);
        Assert.Equal("length", finishReason!.GetValue(observation));
        var serialized = System.Text.Json.JsonSerializer.Serialize(observation);
        Assert.DoesNotContain("private", serialized, StringComparison.Ordinal);
        Assert.Contains("length", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_DropsUnrecognizedFinishReasonFromTelemetry()
    {
        var telemetry = new List<ProviderRequestTelemetry>();
        using var client = new AIService(Profile(), "test-key", new StaticResponseHandler(
            "{\"choices\":[{\"finish_reason\":\"private-answer-fragment\",\"message\":{\"content\":\"answer\"}}]}"),
            telemetryObserver: telemetry.Add);

        await client.GenerateAsync("system", "input");

        var observation = Assert.Single(telemetry);
        var finishReason = typeof(ProviderRequestTelemetry).GetProperty("FinishReason");
        Assert.NotNull(finishReason);
        Assert.Null(finishReason!.GetValue(observation));
        Assert.DoesNotContain("private-answer-fragment", System.Text.Json.JsonSerializer.Serialize(observation), StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateProviderProfile_RejectsCandidateEndpointBeforeAnyRunArtifactsAreCreated()
    {
        var unsafeLocalProfile = new ProviderProfile
        {
            Type = ProviderType.Local,
            Protocol = ProviderProtocol.OpenAICompatible,
            Platform = ProviderPlatform.Ollama,
            ApiBase = "http://192.0.2.40:11434/v1"
        };

        Assert.Throws<InvalidOperationException>(() => AIService.ValidateProviderProfile(unsafeLocalProfile, string.Empty));
    }

    [Fact]
    public async Task GenerateAsync_ReportsOnlyRequestTimingIdAndUsageMetadata()
    {
        var telemetry = new List<ProviderRequestTelemetry>();
        using var client = new AIService(Profile(), "test-key", new StaticResponseHandler(
            "{\"id\":\"chatcmpl-test-1\",\"choices\":[{\"message\":{\"content\":\"secret-answer-text\"}}],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":3,\"prompt_tokens_details\":{\"cached_tokens\":4}}}"),
            telemetryObserver: telemetry.Add);

        var result = await client.GenerateAsync("sensitive system prompt", "sensitive user text");

        Assert.Equal("secret-answer-text", result);
        var observation = Assert.Single(telemetry);
        Assert.Equal("chatcmpl-test-1", observation.RequestId);
        Assert.Equal(12, observation.InputTokens);
        Assert.Equal(3, observation.OutputTokens);
        Assert.Equal(4, observation.CacheReadInputTokens);
        Assert.Null(observation.CacheCreationInputTokens);
        Assert.Equal((int)HttpStatusCode.OK, observation.HttpStatusCode);
        Assert.Equal("success", observation.Outcome);
        Assert.DoesNotContain("sensitive", System.Text.Json.JsonSerializer.Serialize(observation), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-answer-text", System.Text.Json.JsonSerializer.Serialize(observation), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_InvalidSuccessPayloadStillReportsContentFreeUsageAndFinishMetadata()
    {
        var telemetry = new List<ProviderRequestTelemetry>();
        using var client = new AIService(Profile(), "test-key", new StaticResponseHandler(
            "{\"id\":\"chatcmpl-invalid-1\",\"choices\":[{\"finish_reason\":\"stop\",\"message\":{}}],\"usage\":{\"prompt_tokens\":17,\"completion_tokens\":4}}"),
            telemetryObserver: telemetry.Add);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GenerateAsync("private-system", "private-input"));

        var observation = Assert.Single(telemetry);
        Assert.Equal("chatcmpl-invalid-1", observation.RequestId);
        Assert.Equal(17, observation.InputTokens);
        Assert.Equal(4, observation.OutputTokens);
        Assert.Equal("stop", observation.FinishReason);
        Assert.Equal("invalid_response", observation.Outcome);
        var serialized = System.Text.Json.JsonSerializer.Serialize(observation);
        Assert.DoesNotContain("private", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("chatcmpl-invalid-1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_OversizedResponseTelemetryRetainsHeaderRequestId()
    {
        const string requestId = "req-oversized-1";
        var telemetry = new List<ProviderRequestTelemetry>();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', 4 * 1024 * 1024 + 1), Encoding.UTF8, "application/json")
        };
        response.Headers.Add("x-request-id", requestId);
        using var client = new AIService(Profile(), "test-key", new FixedResponseHandler(response), telemetryObserver: telemetry.Add);

        var exception = await Assert.ThrowsAsync<GenerationFailureException>(() => client.GenerateAsync("system", "input"));

        Assert.Equal(GenerationFailureKind.ProviderUnavailable, exception.Kind);
        var observation = Assert.Single(telemetry);
        Assert.Equal(requestId, observation.RequestId);
        Assert.Equal((int)HttpStatusCode.OK, observation.HttpStatusCode);
        Assert.Equal("invalid_response", observation.Outcome);
        Assert.Null(observation.InputTokens);
        Assert.Null(observation.OutputTokens);
    }

    [Fact]
    public async Task GenerateAsync_AnthropicAwsErrorRetainsDocumentedAwsRequestIdHeader()
    {
        const string requestId = "aws-request-123456789";
        var telemetry = new List<ProviderRequestTelemetry>();
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":{\"type\":\"invalid_request_error\",\"message\":\"private provider detail\"}}", Encoding.UTF8, "application/json")
        };
        response.Headers.Add("x-amzn-requestid", requestId);
        response.Headers.Add("request-id", "anthropic-secondary-123");
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Anthropic,
            Protocol = ProviderProtocol.AnthropicMessages,
            ApiBase = "https://api.anthropic.com",
            Model = "claude-sonnet-4-5"
        };
        using var client = new AIService(profile, "test-key", new FixedResponseHandler(response), telemetryObserver: telemetry.Add);

        await Assert.ThrowsAsync<GenerationFailureException>(() => client.GenerateAsync("system", "input"));

        var observation = Assert.Single(telemetry);
        Assert.Equal(requestId, observation.RequestId);
        Assert.Equal((int)HttpStatusCode.BadRequest, observation.HttpStatusCode);
        Assert.Equal("provider_error", observation.Outcome);
        Assert.DoesNotContain("private provider detail", System.Text.Json.JsonSerializer.Serialize(observation), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestConnectionAsync_AnthropicAwsDiagnosticPrefersPrimaryAwsRequestId()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"content\":[{\"type\":\"text\",\"text\":\"OK\"}],\"stop_reason\":\"end_turn\"}",
                Encoding.UTF8,
                "application/json")
        };
        response.Headers.Add("x-amzn-requestid", "aws-request-primary");
        response.Headers.Add("request-id", "anthropic-request-secondary");
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Anthropic,
            Protocol = ProviderProtocol.AnthropicMessages,
            ApiBase = "https://api.anthropic.com",
            Model = "claude-sonnet-4-5"
        };
        using var client = new AIService(profile, "test-key", new FixedResponseHandler(response));

        var result = await client.TestConnectionAsync();

        Assert.Equal(ConnectionTestStatus.Success, result.Status);
        Assert.Contains("aws-request-primary", result.DiagnosticSummary);
        Assert.DoesNotContain("anthropic-request-secondary", result.DiagnosticSummary);
    }

    [Fact]
    public async Task GenerateAsync_TransientRetryReportsEachAttemptWithItsOwnRequestIdAndOutcome()
    {
        var telemetry = new List<ProviderRequestTelemetry>();
        var handler = new SequenceResponseHandler(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("private transient error", Encoding.UTF8, "text/plain")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"id\":\"chatcmpl-final\",\"choices\":[{\"message\":{\"content\":\"answer\"}}]}",
                    Encoding.UTF8,
                    "application/json")
            });
        handler.Responses[0].Headers.Add("x-request-id", "req-attempt-1");
        handler.Responses[1].Headers.Add("x-request-id", "req-attempt-2");
        using var client = new AIService(Profile(), "test-key", handler,
            delayAsync: (_, _) => Task.CompletedTask,
            telemetryObserver: telemetry.Add);

        var result = await client.GenerateAsync("system", "input");

        Assert.Equal("answer", result);
        Assert.Equal(2, handler.RequestCount);
        Assert.Collection(telemetry,
            first =>
            {
                Assert.Equal("req-attempt-1", first.RequestId);
                Assert.Equal((int)HttpStatusCode.ServiceUnavailable, first.HttpStatusCode);
                Assert.Equal("provider_error", first.Outcome);
            },
            second =>
            {
                Assert.Equal("req-attempt-2", second.RequestId);
                Assert.Equal((int)HttpStatusCode.OK, second.HttpStatusCode);
                Assert.Equal("success", second.Outcome);
            });
        Assert.DoesNotContain("private transient error", System.Text.Json.JsonSerializer.Serialize(telemetry), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("in_progress")]
    public async Task GenerateAsync_OpenAiResponsesNonterminalStateNeverDeliversPartialText(string status)
    {
        var telemetry = new List<ProviderRequestTelemetry>();
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.OpenAI,
            Protocol = ProviderProtocol.OpenAIResponses,
            ApiBase = "https://api.openai.com/v1",
            Model = "gpt-4.1-mini"
        };
        using var client = new AIService(profile, "test-key", new StaticResponseHandler(
            "{\"id\":\"resp-nonterminal\",\"status\":\"" + status + "\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"private partial text\"}]}]}"),
            telemetryObserver: telemetry.Add);

        var exception = await Assert.ThrowsAsync<GenerationFailureException>(() => client.GenerateAsync("system", "input"));

        Assert.Equal(GenerationFailureKind.Incomplete, exception.Kind);
        var observation = Assert.Single(telemetry);
        Assert.Equal("incomplete", observation.Outcome);
        Assert.Equal(status, observation.FinishReason);
        Assert.Equal("resp-nonterminal", observation.RequestId);
        Assert.DoesNotContain("private partial text", System.Text.Json.JsonSerializer.Serialize(observation), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_ObserverFailureDoesNotChangeGenerationResult()
    {
        using var client = new AIService(Profile(), "test-key", new StaticResponseHandler(
            "{\"choices\":[{\"message\":{\"content\":\"answer\"}}]}"),
            telemetryObserver: _ => throw new InvalidOperationException("observer failure"));

        var result = await client.GenerateAsync("system", "input");

        Assert.Equal("answer", result);
    }

    private static ProviderProfile Profile() => new()
    {
        Type = ProviderType.Cloud,
        Platform = ProviderPlatform.OpenAI,
        Protocol = ProviderProtocol.OpenAICompatible,
        ApiBase = "https://api.openai.com/v1",
        Model = "gpt-test"
    };

    private sealed class StaticResponseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    private sealed class FixedResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }

    private sealed class SequenceResponseHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int _requestCount;

        public IReadOnlyList<HttpResponseMessage> Responses { get; } = responses;
        public int RequestCount => _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _requestCount) - 1;
            if (index >= responses.Length) throw new InvalidOperationException("Unexpected extra request.");
            return Task.FromResult(responses[index]);
        }
    }
}
