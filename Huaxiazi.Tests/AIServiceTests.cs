using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Huaxiazi.Models;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

/// <summary>
/// 验证 AIService：
///  - ParseContent（private static）对正常/异常 JSON 的容错——通过反射调用，未改动源码；
///  - GenerateAsync 的 API Key 守卫（纯逻辑，不发起真实网络请求）。
///
/// 说明：任务建议“将 ParseContent 改为 internal 并加 [InternalsVisibleTo("Huaxiazi.Tests")]”
/// 以便直接测试。本测试采用反射方式达到同样目的且不修改源码；若团队希望更干净的写法，
/// 可采纳该重构建议（见 QA 报告）。
/// </summary>
public class AIServiceTests
{
    private sealed class CapturingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return response;
        }
    }

    private sealed class StaticResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }

    #region ParseContent（通过反射调用私有静态方法）

    [Fact]
    public void ParseContent_ValidJson_ReturnsContent()
    {
        var json = "{\"choices\":[{\"message\":{\"content\":\"优化后的提示词\"}}]}";
        var result = TestHelpers.ParseContentViaReflection(json);
        Assert.Equal("优化后的提示词", result);
    }

    [Fact]
    public void ParseContent_OpenAiCompatibleContentArray_ReturnsTextBlocks()
    {
        var json = "{\"choices\":[{\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"DeepSeek reply\"}]}}]}";

        var result = TestHelpers.ParseContentViaReflection(json);

        Assert.Equal("DeepSeek reply", result);
    }

    [Fact]
    public void ParseContent_ContentArray_DoesNotExposeReasoningBlocks()
    {
        var json = "{\"choices\":[{\"message\":{\"content\":[{\"type\":\"reasoning\",\"text\":\"private chain\"},{\"type\":\"text\",\"text\":\"final answer\"}]}}]}";

        var result = TestHelpers.ParseContentViaReflection(json);

        Assert.Equal("final answer", result);
        Assert.DoesNotContain("private chain", result);
    }

    [Fact]
    public void ParseContent_ContentIsNull_ThrowsClearError()
    {
        var json = "{\"choices\":[{\"message\":{\"content\":null}}]}";
        var error = Assert.Throws<InvalidOperationException>(() => TestHelpers.ParseContentViaReflection(json));
        Assert.Contains("为空", error.Message);
    }

    [Fact]
    public void ParseContent_ContentIsWhitespace_ThrowsClearError()
    {
        var json = "{\"choices\":[{\"message\":{\"content\":\"   \"}}]}";
        var error = Assert.Throws<InvalidOperationException>(() => TestHelpers.ParseContentViaReflection(json));
        Assert.Contains("为空", error.Message);
    }

    [Fact]
    public void ParseContent_MissingChoices_Throws()
    {
        var json = "{\"foo\":1}";
        var ex = Assert.Throws<InvalidOperationException>(() => TestHelpers.ParseContentViaReflection(json));
        Assert.Contains("choices", ex.Message);
    }

    [Fact]
    public void ParseContent_EmptyChoices_Throws()
    {
        var json = "{\"choices\":[]}";
        var ex = Assert.Throws<InvalidOperationException>(() => TestHelpers.ParseContentViaReflection(json));
        Assert.Contains("choices", ex.Message);
    }

    [Fact]
    public void ParseContent_MissingMessage_Throws()
    {
        var json = "{\"choices\":[{}]}";
        var ex = Assert.Throws<InvalidOperationException>(() => TestHelpers.ParseContentViaReflection(json));
        Assert.Contains("message", ex.Message);
    }

    [Fact]
    public void ParseContent_MissingContent_Throws()
    {
        var json = "{\"choices\":[{\"message\":{}}]}";
        var ex = Assert.Throws<InvalidOperationException>(() => TestHelpers.ParseContentViaReflection(json));
        Assert.Contains("content", ex.Message);
    }

    [Fact]
    public void ParseContent_InvalidJson_Throws()
    {
        var json = "not-a-json";
        Assert.ThrowsAny<Exception>(() => TestHelpers.ParseContentViaReflection(json));
    }

    #endregion

    #region GenerateAsync 参数/守卫（不发起真实网络请求）

    [Fact]
    public void Constructor_NullSettings_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new AIService(null!, null));
    }

    [Fact]
    public async Task GenerateAsync_WithoutApiKey_Throws()
    {
        var svc = new AIService(new ProviderProfile { Type = ProviderType.Cloud, ApiBase = "https://api.openai.com/v1", Model = "gpt-4o-mini" }, apiKey: "");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.GenerateAsync("system", "user"));
        Assert.Contains("API Key", ex.Message);
    }

    [Fact]
    public async Task GenerateAsync_LocalProfileWithoutKey_IsAllowed()
    {
        var profile = new ProviderProfile
        {
            Type = ProviderType.Local,
            ApiBase = "http://localhost:11434/v1",
            Model = "qwen"
        };
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"本地结果\"}}]}", Encoding.UTF8, "application/json")
        };
        using var service = new AIService(profile, apiKey: null, new StaticResponseHandler(response));

        var result = await service.GenerateAsync("system", "user");

        Assert.Equal("本地结果", result);
    }

    [Fact]
    public async Task GenerateAsync_OpenAICompatible_RemoteHttp_IsRejected()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var profile = new ProviderProfile
        {
            Type = ProviderType.Cloud,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "http://remote-server.example.com/v1",
            Model = "custom-model"
        };
        using var service = new AIService(profile, "secret-key", handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateAsync("system", "user"));

        Assert.Contains("HTTPS", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task GenerateAsync_OpenAICompatible_LocalhostHttp_IsAllowed()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"本地结果\"}}]}", Encoding.UTF8, "application/json")
        };
        var profile = new ProviderProfile
        {
            Type = ProviderType.Local,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "http://localhost:11434/v1",
            Model = "local-model"
        };
        using var service = new AIService(profile, null, new StaticResponseHandler(response));

        var result = await service.GenerateAsync("system", "user");

        Assert.Equal("本地结果", result);
    }

    [Fact]
    public async Task GenerateAsync_DeepSeekLegacyBase_UsesV1ChatCompletionsEndpoint()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"正常\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.DeepSeek,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.deepseek.com",
            Model = "deepseek-chat"
        };
        using var service = new AIService(profile, "secret-key", handler);

        _ = await service.GenerateAsync("system", "user");

        Assert.Equal("https://api.deepseek.com/v1/chat/completions", handler.Request?.RequestUri?.ToString());
    }

    [Fact]
    public async Task GenerateAsync_ModelMappingEnabled_Hit_SendsMappedModel()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"OK\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.example.com/v1",
            Model = "claude-sonnet",
            EnableModelMapping = true,
            ModelMapping = new() { ["claude-sonnet"] = "deepseek-chat" }
        };
        using var service = new AIService(profile, "key", handler);

        await service.GenerateAsync("system", "user");

        Assert.NotNull(handler.Body);
        Assert.Contains("\"model\":\"deepseek-chat\"", handler.Body);
    }

    [Fact]
    public async Task GenerateAsync_ModelMappingEnabled_Miss_SendsOriginalModel()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"OK\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.example.com/v1",
            Model = "unmapped-alias",
            EnableModelMapping = true,
            ModelMapping = new() { ["claude-sonnet"] = "deepseek-chat" }
        };
        using var service = new AIService(profile, "key", handler);

        await service.GenerateAsync("system", "user");

        Assert.NotNull(handler.Body);
        Assert.Contains("\"model\":\"unmapped-alias\"", handler.Body);
    }

    [Fact]
    public async Task GenerateAsync_ModelMappingDisabled_SendsOriginalModel()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"OK\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.example.com/v1",
            Model = "alias-model",
            EnableModelMapping = false,
            ModelMapping = new() { ["alias-model"] = "real-model" }
        };
        using var service = new AIService(profile, "key", handler);

        await service.GenerateAsync("system", "user");

        Assert.NotNull(handler.Body);
        Assert.Contains("\"model\":\"alias-model\"", handler.Body);
    }

    [Fact]
    public async Task GenerateAsync_DeepSeek_UsesOfficialOpenAiCompatibleChatEndpoint()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"DeepSeek result\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.DeepSeek,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.deepseek.com",
            Model = "deepseek-chat"
        };
        using var service = new AIService(profile, "deepseek-key", handler);

        var result = await service.GenerateAsync("system", "user");

        Assert.Equal("DeepSeek result", result);
        Assert.Equal("https://api.deepseek.com/v1/chat/completions", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer deepseek-key", handler.Request.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task TestConnectionAsync_OpenAICompatible_RemoteHttp_IsRejected()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var profile = new ProviderProfile
        {
            Type = ProviderType.Cloud,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "http://remote-server.example.com/v1",
            Model = "custom-model"
        };
        using var service = new AIService(profile, "secret-key", handler);

        var result = await service.TestConnectionAsync();

        Assert.Equal(ConnectionTestStatus.InvalidEndpoint, result.Status);
        Assert.Contains("HTTPS", result.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task GenerateAsync_CloudHttpEndpoint_IsRejectedBeforeSendingApiKey()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var profile = new ProviderProfile
        {
            Type = ProviderType.Cloud,
            ApiBase = "http://api.example.test/v1",
            Model = "cloud-model"
        };
        using var service = new AIService(profile, "must-not-leak", handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateAsync("system", "user"));

        Assert.Contains("HTTPS", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task TestConnectionAsync_CloudHttpEndpoint_IsRejectedBeforeSendingApiKey()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var profile = new ProviderProfile
        {
            Type = ProviderType.Cloud,
            ApiBase = "http://api.example.test/v1",
            Model = "cloud-model"
        };
        using var service = new AIService(profile, "must-not-leak", handler);

        var result = await service.TestConnectionAsync();

        Assert.Equal(ConnectionTestStatus.InvalidEndpoint, result.Status);
        Assert.Contains("HTTPS", result.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task GenerateAsync_CloudProfileWithoutKey_ThrowsBeforeNetworkCall()
    {
        var profile = new ProviderProfile { Type = ProviderType.Cloud };
        using var service = new AIService(profile, apiKey: null, new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateAsync("system", "user"));

        Assert.Contains("API Key", error.Message);
    }

    [Fact]
    public async Task GenerateAsync_HttpError_DoesNotExposeResponseBody()
    {
        var profile = new ProviderProfile { Type = ProviderType.Cloud };
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("secret-response-body")
        };
        using var service = new AIService(profile, "key", new StaticResponseHandler(response));

        var error = await Assert.ThrowsAsync<GenerationFailureException>(() => service.GenerateAsync("system", "user"));

        Assert.DoesNotContain("secret-response-body", error.Message);
        Assert.Contains("401", error.Message);
        Assert.Equal(GenerationFailureKind.Authentication, error.Kind);
    }

    [Fact]
    public async Task GenerateAsync_JsonHttpError_ShowsProviderMessageWithoutLeakingOtherFields()
    {
        var profile = new ProviderProfile { Type = ProviderType.Cloud };
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":{\"message\":\"temperature is not supported\",\"request_id\":\"secret-id\"}}", Encoding.UTF8, "application/json")
        };
        using var service = new AIService(profile, "key", new StaticResponseHandler(response));

        var error = await Assert.ThrowsAsync<GenerationFailureException>(() => service.GenerateAsync("system", "user"));

        Assert.Contains("temperature is not supported", error.Message);
        Assert.DoesNotContain("secret-id", error.Message);
        Assert.Equal(GenerationFailureKind.RequestRejected, error.Kind);
    }

    [Fact]
    public async Task GenerateAsync_UserCancellation_RemainsCancellation()
    {
        var profile = new ProviderProfile { Type = ProviderType.Local };
        using var source = new CancellationTokenSource();
        source.Cancel();
        using var service = new AIService(profile, null, new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GenerateAsync("system", "user", source.Token));
    }

    [Fact]
    public async Task GenerateAsync_Anthropic_UsesMessagesApiAndParsesTextBlock()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"content\":[{\"type\":\"text\",\"text\":\"Claude 结果\"}]}", Encoding.UTF8, "application/json")
        };
        var handler = new CapturingHandler(response);
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Anthropic,
            Protocol = ProviderProtocol.AnthropicMessages,
            ApiBase = "https://api.anthropic.com",
            Model = "claude-sonnet-4-5"
        };
        using var service = new AIService(profile, "anthropic-key", handler);

        var result = await service.GenerateAsync("system", "user");

        Assert.Equal("Claude 结果", result);
        Assert.Equal("https://api.anthropic.com/v1/messages", handler.Request!.RequestUri!.ToString());
        Assert.Equal("anthropic-key", handler.Request.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", handler.Request.Headers.GetValues("anthropic-version").Single());
        Assert.Contains("\"system\":\"system\"", handler.Body);
        Assert.Contains("\"max_tokens\":", handler.Body);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task GenerateAsync_Anthropic_EmptyText_ThrowsClearError(string? text)
    {
        var jsonText = text is null ? "null" : $"\"{text}\"";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"{{\"content\":[{{\"type\":\"text\",\"text\":{jsonText}}}]}}", Encoding.UTF8, "application/json")
        };
        var profile = new ProviderProfile { Protocol = ProviderProtocol.AnthropicMessages, ApiBase = "https://api.anthropic.com", Model = "claude" };
        using var service = new AIService(profile, "key", new StaticResponseHandler(response));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateAsync("system", "user"));

        Assert.Contains("为空", error.Message);
    }

    [Fact]
    public async Task GenerateAsync_Gemini_UsesGenerateContentAndParsesCandidate()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Gemini 结果\"}]}}]}", Encoding.UTF8, "application/json")
        };
        var handler = new CapturingHandler(response);
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Gemini,
            Protocol = ProviderProtocol.GeminiGenerateContent,
            ApiBase = "https://generativelanguage.googleapis.com/v1beta",
            Model = "gemini-2.5-flash"
        };
        using var service = new AIService(profile, "gemini-key", handler);

        var result = await service.GenerateAsync("system", "user");

        Assert.Equal("Gemini 结果", result);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent", handler.Request!.RequestUri!.ToString());
        Assert.Equal("gemini-key", handler.Request.Headers.GetValues("x-goog-api-key").Single());
        Assert.Contains("\"system_instruction\"", handler.Body);
        Assert.Contains("\"contents\"", handler.Body);
    }

    [Fact]
    public async Task GenerateAsync_OpenAi_UsesConfiguredSamplingAndTokenLimits()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"结果\"}}]}", Encoding.UTF8, "application/json")
        };
        var handler = new CapturingHandler(response);
        var profile = new ProviderProfile
        {
            ApiBase = "https://api.openai.com/v1", Model = "gpt-test",
            Temperature = 0.75, TopP = 0.85, MaxTokens = 3072
        };
        using var service = new AIService(profile, "key", handler);

        await service.GenerateAsync("system", "user");

        Assert.Contains("\"temperature\":0.75", handler.Body);
        Assert.Contains("\"top_p\":0.85", handler.Body);
        Assert.Contains("\"max_tokens\":3072", handler.Body);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task GenerateAsync_Gemini_EmptyText_ThrowsClearError(string? text)
    {
        var jsonText = text is null ? "null" : $"\"{text}\"";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"{{\"candidates\":[{{\"content\":{{\"parts\":[{{\"text\":{jsonText}}}]}}}}]}}", Encoding.UTF8, "application/json")
        };
        var profile = new ProviderProfile { Protocol = ProviderProtocol.GeminiGenerateContent, ApiBase = "https://generativelanguage.googleapis.com/v1beta", Model = "gemini" };
        using var service = new AIService(profile, "key", new StaticResponseHandler(response));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateAsync("system", "user"));

        Assert.Contains("为空", error.Message);
    }

    [Fact]
    public async Task TestConnectionAsync_Unauthorized_IsAuthFailureInsteadOfSuccess()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"error\":{\"message\":\"invalid api key\"}}")
        };
        using var service = new AIService(new ProviderProfile(), "secret-key", new StaticResponseHandler(response));

        var result = await service.TestConnectionAsync();

        Assert.Equal(ConnectionTestStatus.AuthFailed, result.Status);
        Assert.False(result.CanSaveAsVerified);
        Assert.Equal(HttpStatusCode.Unauthorized, result.HttpStatusCode);
    }

    [Fact]
    public async Task TestConnectionAsync_RateLimited_ReturnsActionableStatus()
    {
        using var service = new AIService(new ProviderProfile(), "key",
            new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.TooManyRequests)));

        var result = await service.TestConnectionAsync();

        Assert.Equal(ConnectionTestStatus.RateLimited, result.Status);
        Assert.Contains("限流", result.UserMessage);
    }

    [Fact]
    public async Task TestConnectionAsync_SuccessWithInvalidPayload_IsInvalidResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"unexpected\":true}", Encoding.UTF8, "application/json")
        };
        using var service = new AIService(new ProviderProfile(), "key", new StaticResponseHandler(response));

        var result = await service.TestConnectionAsync();

        Assert.Equal(ConnectionTestStatus.InvalidResponse, result.Status);
        Assert.False(result.CanSaveAsVerified);
    }

    [Fact]
    public async Task TestConnectionAsync_Success_ReturnsSanitizedDiagnostic()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"OK\"}}]}", Encoding.UTF8, "application/json")
        };
        response.Headers.TryAddWithoutValidation("x-request-id", "req-safe-123");
        var profile = new ProviderProfile { Platform = ProviderPlatform.DeepSeek, ApiBase = "https://api.deepseek.com", Model = "deepseek-v4-flash" };
        using var service = new AIService(profile, "super-secret-key", new StaticResponseHandler(response));

        var result = await service.TestConnectionAsync();

        Assert.Equal(ConnectionTestStatus.Success, result.Status);
        Assert.True(result.CanSaveAsVerified);
        Assert.Contains("DeepSeek", result.DiagnosticSummary);
        Assert.Contains("api.deepseek.com", result.DiagnosticSummary);
        Assert.Contains("req-safe-123", result.DiagnosticSummary);
        Assert.DoesNotContain("super-secret-key", result.DiagnosticSummary);
        Assert.DoesNotContain("连接测试", result.DiagnosticSummary);
        Assert.DoesNotContain("choices", result.DiagnosticSummary);
    }

    [Fact]
    public async Task TestConnectionAsync_DeepSeekCompatibleContentArray_ReturnsSuccess()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"OK\"}]}}]}", Encoding.UTF8, "application/json")
        };
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.DeepSeek,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.deepseek.com",
            Model = "deepseek-v4-flash"
        };
        using var service = new AIService(profile, "deepseek-secret", new StaticResponseHandler(response));

        var result = await service.TestConnectionAsync();

        Assert.Equal(ConnectionTestStatus.Success, result.Status);
        Assert.True(result.CanSaveAsVerified);
    }

    [Fact]
    public async Task TestConnectionAsync_MissingCloudKey_DoesNotSendRequest()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var service = new AIService(new ProviderProfile(), string.Empty, handler);

        var result = await service.TestConnectionAsync();

        Assert.Equal(ConnectionTestStatus.MissingKey, result.Status);
        Assert.Null(handler.Request);
    }

    [Fact]
    public void AIService_DoesNotExposeRawRequestResponseDiagnostics()
    {
        Assert.Null(typeof(AIService).GetMethod("DiagnoseAsync"));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, ConnectionTestStatus.ProviderError)]
    [InlineData(HttpStatusCode.Forbidden, ConnectionTestStatus.AuthFailed)]
    [InlineData(HttpStatusCode.NotFound, ConnectionTestStatus.ModelUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError, ConnectionTestStatus.ProviderError)]
    public async Task TestConnectionAsync_MapsProviderHttpFailures(HttpStatusCode code, ConnectionTestStatus expected)
    {
        using var service = new AIService(new ProviderProfile(), "key",
            new StaticResponseHandler(new HttpResponseMessage(code)));

        var result = await service.TestConnectionAsync();

        Assert.Equal(expected, result.Status);
        Assert.False(result.CanSaveAsVerified);
    }

    [Fact]
    public async Task TestConnectionAsync_NetworkFailure_IsSanitized()
    {
        using var service = new AIService(new ProviderProfile(), "key",
            new ThrowingHandler(new HttpRequestException("secret proxy detail")));

        var result = await service.TestConnectionAsync();

        Assert.Equal(ConnectionTestStatus.NetworkError, result.Status);
        Assert.DoesNotContain("secret proxy detail", result.UserMessage);
        Assert.DoesNotContain("secret proxy detail", result.DiagnosticSummary);
    }

    [Fact]
    public async Task TestConnectionAsync_InternalCancellation_IsReportedAsTimeout()
    {
        using var service = new AIService(new ProviderProfile(), "key",
            new ThrowingHandler(new OperationCanceledException()));

        var result = await service.TestConnectionAsync();

        Assert.Equal(ConnectionTestStatus.Timeout, result.Status);
    }

    #endregion
}
