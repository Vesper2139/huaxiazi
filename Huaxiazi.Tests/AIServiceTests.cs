using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
    private const string EmotionStructuredSchema = """
        {"type":"object","properties":{"answer":{"type":"string"},"companion_emotion":{"type":["string","null"],"enum":["Supportive","Cheerful",null]},"companion_intensity":{"type":["number","null"]}},"required":["answer","companion_emotion","companion_intensity"],"additionalProperties":false}
        """;

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
        private readonly HttpStatusCode _statusCode = response.StatusCode;
        private readonly string? _reasonPhrase = response.ReasonPhrase;
        private readonly string _body = response.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var clone = new HttpResponseMessage(_statusCode)
            {
                ReasonPhrase = _reasonPhrase,
                Content = new StringContent(_body, Encoding.UTF8, response.Content?.Headers.ContentType?.MediaType ?? "application/json")
            };
            foreach (var header in response.Headers)
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return Task.FromResult(clone);
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }

    private sealed class SequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public int Calls { get; private set; }
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return _responses.Count > 0
                ? _responses.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
    }

    [Fact]
    public async Task GenerateAsync_InternalDiagnosticSeedIsSentToLocalOllamaRequest()
    {
        var profile = new ProviderProfile
        {
            Id = "diagnostic-ollama",
            Type = ProviderType.Local,
            Platform = ProviderPlatform.Ollama,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "http://127.0.0.1:11434/v1",
            Model = "qwen3:4b",
            Temperature = 0.4,
            TopP = 1,
            MaxTokens = 4096
        };
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}", Encoding.UTF8, "application/json")
        });
        using var service = CreateAIServiceWithDiagnosticSeed(profile, handler, 20261007);

        await service.GenerateAsync("system", "user");

        using var request = JsonDocument.Parse(handler.Body!);
        Assert.Equal(20261007, request.RootElement.GetProperty("seed").GetInt32());
        Assert.Equal("qwen3:4b", request.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task GenerateAsync_SeedIsOmittedFromDefaultRequests()
    {
        var profile = new ProviderProfile
        {
            Id = "diagnostic-ollama",
            Type = ProviderType.Local,
            Platform = ProviderPlatform.Ollama,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "http://127.0.0.1:11434/v1",
            Model = "qwen3:4b"
        };
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}", Encoding.UTF8, "application/json")
        });
        using var service = new AIService(profile, string.Empty, handler);

        await service.GenerateAsync("system", "user");

        using var request = JsonDocument.Parse(handler.Body!);
        Assert.False(request.RootElement.TryGetProperty("seed", out _));
    }

    [Fact]
    public void AIService_RejectsDiagnosticSeedForNonLocalOllamaProfiles()
    {
        var cloudProfile = new ProviderProfile
        {
            Id = "cloud-openai",
            Type = ProviderType.Cloud,
            Platform = ProviderPlatform.OpenAI,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.openai.com/v1",
            Model = "gpt-4o-mini"
        };
        var constructor = typeof(AIService).GetConstructors()
            .SingleOrDefault(candidate => candidate.GetParameters().Any(parameter => parameter.Name == "diagnosticSeed"));
        Assert.NotNull(constructor);

        var exception = Record.Exception(() => constructor!.Invoke([
            cloudProfile,
            string.Empty,
            new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)),
            null,
            null,
            7]));

        var invocationException = Assert.IsType<System.Reflection.TargetInvocationException>(exception);
        var argumentException = Assert.IsType<ArgumentException>(invocationException.InnerException);
        Assert.Contains("本地 Ollama", argumentException.Message, StringComparison.Ordinal);
    }

    private static AIService CreateAIServiceWithDiagnosticSeed(ProviderProfile profile, HttpMessageHandler handler, int seed)
    {
        var constructor = typeof(AIService).GetConstructors()
            .SingleOrDefault(candidate => candidate.GetParameters().Any(parameter => parameter.Name == "diagnosticSeed"));
        Assert.NotNull(constructor);
        return (AIService)constructor!.Invoke([profile, string.Empty, handler, null, null, seed]);
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
    public void ParseContent_OpenAiCompatibleObjectContent_ReturnsText()
    {
        var json = "{\"choices\":[{\"message\":{\"content\":{\"type\":\"output_text\",\"text\":\"结构化结果\"}}}]}";

        var result = TestHelpers.ParseContentViaReflection(json);

        Assert.Equal("结构化结果", result);
    }

    [Fact]
    public void ParseContent_OpenAiCompatibleServerSentEvents_ReturnsJoinedFinalText()
    {
        var body = "data: {\"choices\":[{\"delta\":{\"content\":\"你好\"}}]}\n\n" +
                   "data: {\"choices\":[{\"delta\":{\"content\":\"，世界\"}}]}\n\n" +
                   "data: [DONE]\n";

        var result = TestHelpers.ParseContentViaReflection(body);

        Assert.Equal("你好，世界", result);
    }

    [Theory]
    [InlineData("```markdown\n目标：整理用户需求\n```", "目标：整理用户需求")]
    [InlineData("```\n目标：整理用户需求\n```", "目标：整理用户需求")]
    public void ParseContent_RemovesTransportMarkdownFence(string wrapped, string expected)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = wrapped } } }
        });

        var result = TestHelpers.ParseContentViaReflection(json);

        Assert.Equal(expected, result);
        Assert.DoesNotContain("```", result);
    }

    [Fact]
    public void ParseContent_RemovesTransportThinkingSuffix()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        content = "目标：整理用户需求\n\nTake time to think through this carefully before responding."
                    }
                }
            }
        });

        var result = TestHelpers.ParseContentViaReflection(json);

        Assert.Equal("目标：整理用户需求", result);
        Assert.DoesNotContain("Take time to think", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseContent_RemovesThinkingSuffixWhenWrappedInClosingQuote()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new { message = new { content = "结果\nTake time to think through this carefully before responding.”" } }
            }
        });

        var result = TestHelpers.ParseContentViaReflection(json);

        Assert.Equal("结果", result);
    }

    [Fact]
    public void ParseContent_ResponsesApiOutputTextFallback_ReturnsText()
    {
        var json = "{\"output_text\":\"兼容结果\"}";

        var result = TestHelpers.ParseContentViaReflection(json);

        Assert.Equal("兼容结果", result);
    }

    [Fact]
    public void ParseContent_ResponsesApiOutputMessageFallback_ReturnsText()
    {
        var json = "{\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"嵌套兼容结果\"}]}]}";

        var result = TestHelpers.ParseContentViaReflection(json);

        Assert.Equal("嵌套兼容结果", result);
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
            Platform = ProviderPlatform.Ollama,
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
    public async Task GenerateStructuredAsync_UnknownOllamaModel_UsesPromptSchemaWithoutNativeFormat()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"answer\\\":\\\"完成\\\"}\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Type = ProviderType.Local,
            Platform = ProviderPlatform.Ollama,
            ApiBase = "http://localhost:11434/v1",
            Model = "qwen3:4b"
        };
        using var service = new AIService(profile, null, handler);

        var result = await service.GenerateStructuredAsync("system", "user", "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}");

        Assert.Contains("answer", result);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.False(body.RootElement.TryGetProperty("response_format", out _));
        Assert.Contains("required", body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task GenerateStructuredAsync_ExperimentalOllamaProfileUsesGenericMediumBudget()
    {
        const string schema = "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}";
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"answer\\\":\\\"完成\\\"}\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Type = ProviderType.Local,
            Platform = ProviderPlatform.Ollama,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "http://127.0.0.1:11434/v1",
            Model = "qwen3:4b"
        };
        ProviderInferencePresets.Apply(profile, InferenceLevel.Medium);
        using var service = new AIService(profile, null, handler);

        await service.GenerateStructuredAsync("system", "user", schema);

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("qwen3:4b", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(2048, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("response_format", out _));
    }

    [Theory]
    [InlineData(InferenceLevel.Low, null)]
    [InlineData(InferenceLevel.Medium, null)]
    [InlineData(InferenceLevel.High, null)]
    public async Task GenerateStructuredAsync_ExperimentalOllamaQwen3DoesNotSendProductThinkingControl(
        InferenceLevel level, string? expectedReasoningEffort)
    {
        const string schema = "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}";
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"answer\\\":\\\"完成\\\"}\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Type = ProviderType.Local,
            Platform = ProviderPlatform.Ollama,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "http://127.0.0.1:11434/v1",
            Model = "qwen3:4b"
        };
        ProviderInferencePresets.Apply(profile, level);
        using var service = new AIService(profile, null, handler);

        await service.GenerateStructuredAsync("system", "user", schema);

        using var body = JsonDocument.Parse(handler.Body!);
        if (expectedReasoningEffort is null)
            Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
        else
            Assert.Equal(expectedReasoningEffort, body.RootElement.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task GenerateStructuredAsync_UnknownOpenAiCompatibleModel_UsesPromptSchemaWithoutNativeFormat()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"answer\\\":\\\"完成\\\"}\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.CustomOpenAICompatible,
            ApiBase = "https://api.example.test/v1",
            Model = "vendor/unknown-model"
        };
        const string schema = "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}";
        using var service = new AIService(profile, "test-key", handler);

        await service.GenerateStructuredAsync("system", "user", schema);

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.False(body.RootElement.TryGetProperty("response_format", out _));
        Assert.Contains(schema, body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task GenerateStructuredAsync_YiLightning_UsesDocumentedChatParametersAndLocalSchemaFallback()
    {
        const string schema = "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}";
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"answer\\\":\\\"完成\\\"}\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Yi,
            ApiBase = "https://api.lingyiwanwu.com/v1",
            Model = "yi-lightning",
            Temperature = 0.7,
            TopP = 0.8
        };
        using var service = new AIService(profile, "yi-test-key", handler);

        await service.GenerateStructuredAsync("system", "user", schema);

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("yi-lightning", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(0.7, body.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(0.8, body.RootElement.GetProperty("top_p").GetDouble());
        Assert.Equal(2048, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("response_format", out _));
        Assert.Contains(schema, body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task GenerateStructuredAsync_YiLarge_UsesDocumentedChatParametersAndLocalSchemaFallback()
    {
        const string schema = "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}";
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"answer\\\":\\\"完成\\\"}\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Yi,
            ApiBase = "https://api.lingyiwanwu.com/v1",
            Model = "yi-large",
            Temperature = 0.6,
            TopP = 0.75
        };
        using var service = new AIService(profile, "yi-test-key", handler);

        await service.GenerateStructuredAsync("system", "user", schema);

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("yi-large", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(0.6, body.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(0.75, body.RootElement.GetProperty("top_p").GetDouble());
        Assert.Equal(2048, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("response_format", out _));
        Assert.Contains(schema, body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task GenerateStructuredAsync_Baichuan4Turbo_ClampsDocumentedRangesAndUsesJsonMode()
    {
        const string schema = "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}";
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"answer\\\":\\\"完成\\\"}\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Baichuan,
            ApiBase = "https://api.baichuan-ai.com/v1",
            Model = "Baichuan4-Turbo",
            Temperature = 1.5,
            TopP = 1.0,
            MaxTokens = 4096
        };
        using var service = new AIService(profile, "baichuan-test-key", handler);

        await service.GenerateStructuredAsync("system", "user", schema);

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(1.0, body.RootElement.GetProperty("temperature").GetDouble());
        Assert.True(body.RootElement.GetProperty("top_p").GetDouble() < 1.0);
        Assert.Equal(2048, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal("json_object", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Contains(schema, body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task GenerateStructuredAsync_Step5PreviewUsesNativeSchemaAndDocumentedReasoning()
    {
        const string schema = "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}";
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"answer\\\":\\\"完成\\\"}\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.StepFun,
            ApiBase = "https://api.stepfun.com/v1",
            Model = "step-5-preview",
            Temperature = 1.2,
            TopP = 0.8,
            InferenceLevel = InferenceLevel.High
        };
        using var service = new AIService(profile, "stepfun-test-key", handler);

        await service.GenerateStructuredAsync("system", "user", schema);

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(1.2, body.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(0.8, body.RootElement.GetProperty("top_p").GetDouble());
        Assert.Equal("high", body.RootElement.GetProperty("reasoning_effort").GetString());
        var format = body.RootElement.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("json_schema").GetProperty("strict").GetBoolean());
        Assert.Equal("answer", format.GetProperty("json_schema").GetProperty("schema")
            .GetProperty("properties").EnumerateObject().Single().Name);
    }

    [Fact]
    public async Task GenerateStructuredAsync_MiniMaxM3SeparatesReasoningAndUsesCompletionTokenField()
    {
        const string schema = "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}";
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"answer\\\":\\\"完成\\\"}\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.MiniMax,
            ApiBase = "https://api.minimax.cn/v1",
            Model = "MiniMax-M3",
            Temperature = 1.2,
            TopP = 0.9,
            MaxTokens = 4096
        };
        using var service = new AIService(profile, "minimax-test-key", handler);

        await service.GenerateStructuredAsync("system", "user", schema);

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(1.2, body.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(0.9, body.RootElement.GetProperty("top_p").GetDouble());
        Assert.Equal(4096, body.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("max_tokens", out _));
        Assert.True(body.RootElement.GetProperty("reasoning_split").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
        Assert.False(body.RootElement.TryGetProperty("response_format", out _));
        Assert.Contains(schema, body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task GenerateStructuredAsync_Grok47UsesDocumentedChatParametersAndNativeJsonSchema()
    {
        const string schema = "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}";
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"answer\\\":\\\"完成\\\"}\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Grok,
            ApiBase = "https://api.x.ai/v1",
            Model = "grok-4.7",
            Temperature = 0.7,
            TopP = 0.95,
            MaxTokens = 4096,
            InferenceLevel = InferenceLevel.High
        };
        using var service = new AIService(profile, "xai-test-key", handler);

        await service.GenerateStructuredAsync("system", "user", schema);

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("grok-4.7", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(0.7, body.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(0.95, body.RootElement.GetProperty("top_p").GetDouble());
        Assert.Equal(4096, body.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("max_tokens", out _));
        Assert.Equal("high", body.RootElement.GetProperty("reasoning_effort").GetString());
        var format = body.RootElement.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("json_schema").GetProperty("strict").GetBoolean());
        Assert.Equal("answer", format.GetProperty("json_schema").GetProperty("schema")
            .GetProperty("properties").EnumerateObject().Single().Name);
        Assert.DoesNotContain(schema, body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateStructuredAsync_GrokPresetProtocolUsesResponsesAndNativeJsonSchemaWithoutStorage()
    {
        const string schema = "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}";
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"id\":\"resp_grok_test\",\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"{\\\"answer\\\":\\\"完成\\\"}\"}]}]}",
                Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Grok,
            Protocol = ProviderProtocol.OpenAIResponses,
            ApiBase = "https://api.x.ai/v1",
            Model = "grok-4.3",
            Temperature = 0.7,
            TopP = 0.95,
            MaxTokens = 4096,
            InferenceLevel = InferenceLevel.High
        };
        using var service = new AIService(profile, "xai-test-key", handler);

        var result = await service.GenerateStructuredAsync("private system", "private user", schema);

        Assert.Equal("https://api.x.ai/v1/responses", handler.Request!.RequestUri!.ToString());
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("grok-4.3", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("private system", body.RootElement.GetProperty("instructions").GetString());
        Assert.Equal("private user", body.RootElement.GetProperty("input").GetString());
        Assert.False(body.RootElement.GetProperty("store").GetBoolean());
        Assert.Equal(4096, body.RootElement.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal(0.7, body.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(0.95, body.RootElement.GetProperty("top_p").GetDouble());
        Assert.Equal("high", body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.False(body.RootElement.TryGetProperty("max_completion_tokens", out _));
        var format = body.RootElement.GetProperty("text").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("strict").GetBoolean());
        Assert.Contains("完成", result);
    }

    [Fact]
    public async Task GenerateAsync_MiniMaxM27RemovesProviderInlineThinkingFromFinalText()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"<think>private internal reasoning</think>这是最终答复。\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.MiniMax,
            ApiBase = "https://api.minimax.cn/v1",
            Model = "MiniMax-M2.7"
        };
        using var service = new AIService(profile, "minimax-test-key", handler);

        var result = await service.GenerateAsync("system", "user");

        Assert.Equal("这是最终答复。", result);
        Assert.DoesNotContain("private internal reasoning", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mimo-v2.5-pro", InferenceLevel.Low, "disabled", true)]
    [InlineData("mimo-v2.5-pro", InferenceLevel.Medium, "enabled", false)]
    [InlineData("mimo-v2.5", InferenceLevel.High, "enabled", false)]
    [InlineData("mimo-v2.5", InferenceLevel.Custom, "enabled", false)]
    public async Task GenerateAsync_MiMoV25MapsThinkingAndCompletionBudget(string model, InferenceLevel level, string expectedThinking, bool sendsSampling)
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"MiMo result\",\"reasoning_content\":\"private internal reasoning\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.MiMo,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.xiaomimimo.com/v1",
            Model = model,
            Temperature = 0.4,
            TopP = 0.8,
            MaxTokens = 2048,
            InferenceLevel = level
        };
        using var service = new AIService(profile, "mimo-test-key", handler);

        var result = await service.GenerateAsync("system", "user");

        Assert.Equal("MiMo result", result);
        Assert.DoesNotContain("private internal reasoning", result, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(2048, body.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("max_tokens", out _));
        Assert.Equal(sendsSampling, body.RootElement.TryGetProperty("temperature", out _));
        Assert.Equal(sendsSampling, body.RootElement.TryGetProperty("top_p", out _));
        if (sendsSampling)
        {
            Assert.Equal(0.4, body.RootElement.GetProperty("temperature").GetDouble());
            Assert.Equal(0.8, body.RootElement.GetProperty("top_p").GetDouble());
        }
        Assert.Equal(expectedThinking, body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
    }

    [Fact]
    public async Task GenerateStructuredAsync_MiMoV25KeepsSchemaInPromptAndUsesLocalValidation()
    {
        const string schema = "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}";
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"answer\\\":\\\"完成\\\"}\"},\"finish_reason\":\"stop\"}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.MiMo,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.xiaomimimo.com/v1",
            Model = "mimo-v2.5-pro"
        };
        using var service = new AIService(profile, "mimo-test-key", handler);

        var result = await service.GenerateStructuredAsync("system", "user", schema);

        Assert.Contains("完成", result);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("json_object", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Contains(schema, body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task TestConnectionAsync_MiMoV25UsesCheapThinkingDisabledProbeAndCompletionTokenField()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.MiMo,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.xiaomimimo.com/v1",
            Model = "mimo-v2.5-pro"
        };
        using var service = new AIService(profile, "mimo-test-key", handler);

        var result = await service.TestConnectionAsync();

        Assert.Equal(ConnectionTestStatus.Success, result.Status);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(8, body.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal("disabled", body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(body.RootElement.TryGetProperty("max_tokens", out _));
        Assert.False(body.RootElement.TryGetProperty("temperature", out _));
        Assert.False(body.RootElement.TryGetProperty("top_p", out _));
    }

    [Theory]
    [InlineData(InferenceLevel.Low, "disabled", true)]
    [InlineData(InferenceLevel.High, "enabled", false)]
    public async Task GenerateAsync_MiMoV26MakesSamplingConditionalOnThinking(InferenceLevel level, string expectedThinking, bool sendsSampling)
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"MiMo result\"},\"finish_reason\":\"stop\"}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.MiMo,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.xiaomimimo.com/v1",
            Model = "mimo-v2.6-pro",
            Temperature = 2.0,
            TopP = 0.0,
            MaxTokens = 2048,
            InferenceLevel = level
        };
        using var service = new AIService(profile, "mimo-test-key", handler);

        var result = await service.GenerateAsync("system", "user");

        Assert.Equal("MiMo result", result);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(2048, body.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal(expectedThinking, body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(sendsSampling, body.RootElement.TryGetProperty("temperature", out _));
        Assert.Equal(sendsSampling, body.RootElement.TryGetProperty("top_p", out _));
        if (sendsSampling)
        {
            Assert.Equal(1.5, body.RootElement.GetProperty("temperature").GetDouble());
            Assert.Equal(0.01, body.RootElement.GetProperty("top_p").GetDouble());
        }
    }

    [Fact]
    public async Task GenerateStructuredAsync_MiMoV26UsesJsonModeAndKeepsLocalSchemaValidation()
    {
        const string schema = "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}";
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"answer\\\":\\\"完成\\\"}\"},\"finish_reason\":\"stop\"}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.MiMo,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.xiaomimimo.com/v1",
            Model = "mimo-v2.6-pro"
        };
        using var service = new AIService(profile, "mimo-test-key", handler);

        var result = await service.GenerateStructuredAsync("system", "user", schema);

        Assert.Contains("完成", result);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("json_object", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Contains(schema, body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task GenerateStructuredAsync_OpenAiGpt4oMini_UsesVerifiedNativeSchema()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"answer\\\":\\\"完成\\\"}\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.OpenAI,
            ApiBase = "https://api.openai.com/v1",
            Model = "gpt-4o-mini",
            MaxTokens = 32_768,
            Temperature = 0.7,
            TopP = 0.8
        };
        using var service = new AIService(profile, "test-key", handler);

        await service.GenerateStructuredAsync("system", "user", "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("json_schema", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal(16_384, body.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("max_tokens", out _));
        Assert.Equal(0.7, body.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(0.8, body.RootElement.GetProperty("top_p").GetDouble());
    }

    [Fact]
    public async Task GenerateStructuredAsync_OpenAiResponsesGpt4oMini_UsesBoundedOutputAndDocumentedSampling()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"id\":\"resp_gpt4o_mini\",\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"{\\\"answer\\\":\\\"完成\\\"}\"}]}]}",
                Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.OpenAI,
            Protocol = ProviderProtocol.OpenAIResponses,
            ApiBase = "https://api.openai.com/v1",
            Model = "gpt-4o-mini",
            MaxTokens = 32_768,
            Temperature = 0.7,
            TopP = 0.8
        };
        using var service = new AIService(profile, "test-key", handler);

        await service.GenerateStructuredAsync("system", "user",
            "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(16_384, body.RootElement.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal(0.7, body.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(0.8, body.RootElement.GetProperty("top_p").GetDouble());
        Assert.True(body.RootElement.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean());
    }

    [Fact]
    public async Task GenerateStructuredAsync_OpenAiResponses_UsesResponsesContractAndDoesNotStoreInput()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"id\":\"resp_test_1\",\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"{\\\"answer\\\":\\\"完成\\\"}\"}]}],\"usage\":{\"input_tokens\":12,\"output_tokens\":3}}",
                Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Type = ProviderType.Cloud,
            Platform = ProviderPlatform.OpenAI,
            Protocol = ProviderProtocol.OpenAIResponses,
            ApiBase = "https://api.openai.com/v1",
            Model = "gpt-4.1-mini"
        };
        ProviderRequestTelemetry? telemetry = null;
        using var service = new AIService(profile, "test-key", handler, telemetryObserver: value => telemetry = value);

        var result = await service.GenerateStructuredAsync("private system", "private user input",
            "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}");

        Assert.Contains("完成", result);
        Assert.Equal("https://api.openai.com/v1/responses", handler.Request!.RequestUri!.ToString());
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("private system", body.RootElement.GetProperty("instructions").GetString());
        Assert.Equal("private user input", body.RootElement.GetProperty("input").GetString());
        Assert.False(body.RootElement.GetProperty("store").GetBoolean());
        Assert.Equal(2048, body.RootElement.GetProperty("max_output_tokens").GetInt32());
        var format = body.RootElement.GetProperty("text").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.Equal("huaxiazi_answer", format.GetProperty("name").GetString());
        Assert.True(format.GetProperty("strict").GetBoolean());
        Assert.Equal(0.4, body.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(1.0, body.RootElement.GetProperty("top_p").GetDouble());
        Assert.Equal("12", telemetry!.InputTokens?.ToString());
        Assert.Equal("3", telemetry.OutputTokens?.ToString());
        Assert.Equal("resp_test_1", telemetry.RequestId);
    }

    [Fact]
    public async Task GenerateStructuredAsync_UnknownOpenAiResponsesModel_UsesPromptSchemaWithoutNativeFormat()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"id\":\"resp_test_unknown\",\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"{\\\"answer\\\":\\\"完成\\\"}\"}]}]}",
                Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.OpenAI,
            Protocol = ProviderProtocol.OpenAIResponses,
            ApiBase = "https://api.openai.com/v1",
            Model = "gpt-4-turbo"
        };
        const string schema = "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}";
        using var service = new AIService(profile, "test-key", handler);

        await service.GenerateStructuredAsync("system", "user", schema);

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.False(body.RootElement.TryGetProperty("text", out _));
        Assert.Contains(schema, body.RootElement.GetProperty("instructions").GetString());
    }

    [Fact]
    public async Task GenerateStructuredAsync_OpenAiResponses_IncompleteOutputIsRejected()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"id\":\"resp_test_2\",\"status\":\"incomplete\",\"incomplete_details\":{\"reason\":\"max_output_tokens\"},\"output\":[]}",
                Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Type = ProviderType.Cloud,
            Platform = ProviderPlatform.OpenAI,
            Protocol = ProviderProtocol.OpenAIResponses,
            ApiBase = "https://api.openai.com/v1",
            Model = "gpt-4.1-mini"
        };
        using var service = new AIService(profile, "test-key", handler);

        var exception = await Assert.ThrowsAsync<GenerationFailureException>(() =>
            service.GenerateStructuredAsync("system", "user", "{\"type\":\"object\"}"));

        Assert.Equal(GenerationFailureKind.Incomplete, exception.Kind);
        Assert.Contains("未完整", exception.Message);
    }

    [Fact]
    public async Task GenerateStructuredAsync_OpenAiResponses_RefusalIsClassifiedAndDoesNotExposeRefusalTextToDiagnostics()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"id\":\"resp_refusal_1\",\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"refusal\",\"refusal\":\"sensitive refusal text\"}]}]}",
                Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Type = ProviderType.Cloud,
            Platform = ProviderPlatform.OpenAI,
            Protocol = ProviderProtocol.OpenAIResponses,
            ApiBase = "https://api.openai.com/v1",
            Model = "gpt-4.1-mini"
        };
        ProviderRequestTelemetry? telemetry = null;
        using var service = new AIService(profile, "test-key", handler, telemetryObserver: value => telemetry = value);

        var exception = await Assert.ThrowsAsync<GenerationFailureException>(() =>
            service.GenerateStructuredAsync("system", "user", "{\"type\":\"object\"}"));

        Assert.Equal(GenerationFailureKind.Refused, exception.Kind);
        Assert.DoesNotContain("sensitive refusal text", exception.Message);
        Assert.Equal("refused", telemetry!.Outcome);
    }

    [Fact]
    public async Task GenerateStructuredAsync_OpenAiCompatible_LengthFinishReasonRejectsPartialJson()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                id = "chatcmpl_truncated",
                choices = new[] { new { finish_reason = "length", message = new { content = "{\"answer\":\"unfinished\"}" } } },
                usage = new { prompt_tokens = 8, completion_tokens = 4 }
            }), Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Type = ProviderType.Cloud,
            Platform = ProviderPlatform.OpenAI,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.openai.com/v1",
            Model = "gpt-4.1-mini"
        };
        ProviderRequestTelemetry? telemetry = null;
        using var service = new AIService(profile, "test-key", handler, telemetryObserver: value => telemetry = value);

        var exception = await Assert.ThrowsAsync<GenerationFailureException>(() =>
            service.GenerateStructuredAsync("system", "user", "{\"type\":\"object\"}"));

        Assert.Equal(GenerationFailureKind.Incomplete, exception.Kind);
        Assert.DoesNotContain("unfinished", exception.Message);
        Assert.Equal("incomplete", telemetry!.Outcome);
        Assert.Equal(4, telemetry.OutputTokens);
    }

    [Theory]
    [InlineData("content_filter")]
    [InlineData("stop")]
    public async Task GenerateAsync_OpenAiCompatible_RefusalIsClassifiedBeforeReturningContent(string finishReason)
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"id\":\"chatcmpl_refused\",\"choices\":[{\"finish_reason\":\"" + finishReason + "\",\"message\":{\"content\":\"filtered text\",\"refusal\":\"sensitive refusal text\"}}]}",
                Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Type = ProviderType.Cloud,
            Platform = ProviderPlatform.OpenAI,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.openai.com/v1",
            Model = "gpt-4.1-mini"
        };
        ProviderRequestTelemetry? telemetry = null;
        using var service = new AIService(profile, "test-key", handler, telemetryObserver: value => telemetry = value);

        var exception = await Assert.ThrowsAsync<GenerationFailureException>(() => service.GenerateAsync("system", "user"));

        Assert.Equal(GenerationFailureKind.Refused, exception.Kind);
        Assert.DoesNotContain("sensitive refusal text", exception.Message);
        Assert.Equal("refused", telemetry!.Outcome);
    }

    [Fact]
    public async Task GenerateAsync_OpenAiCompatible_ContentFilterFinishReasonIsRefusalWithoutText()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                id = "chatcmpl_filtered",
                choices = new[] { new { finish_reason = "content_filter", message = new { content = "partial filtered output" } } }
            }), Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Type = ProviderType.Cloud,
            Platform = ProviderPlatform.OpenAI,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.openai.com/v1",
            Model = "gpt-4.1-mini"
        };
        using var service = new AIService(profile, "test-key", handler);

        var exception = await Assert.ThrowsAsync<GenerationFailureException>(() => service.GenerateAsync("system", "user"));

        Assert.Equal(GenerationFailureKind.Refused, exception.Kind);
        Assert.DoesNotContain("partial filtered output", exception.Message);
    }

    [Fact]
    public async Task GenerateStructuredAsync_OpenAiResponses_MapsOnlyVerifiedModelCapabilities()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"status\":\"completed\",\"output_text\":\"{}\"}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Type = ProviderType.Cloud,
            Platform = ProviderPlatform.OpenAI,
            Protocol = ProviderProtocol.OpenAIResponses,
            ApiBase = "https://api.openai.com/v1",
            Model = "gpt-6-astra",
            Temperature = 0.2,
            TopP = 0.8,
            InferenceLevel = InferenceLevel.High
        };
        using var service = new AIService(profile, "test-key", handler);

        await service.GenerateStructuredAsync("system", "user", "{\"type\":\"object\"}");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.False(body.RootElement.TryGetProperty("temperature", out _));
        Assert.False(body.RootElement.TryGetProperty("top_p", out _));
        Assert.Equal("high", body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.True(body.RootElement.TryGetProperty("max_output_tokens", out _));
    }

    [Fact]
    public async Task GenerateAsync_OpenAiResponses_UnknownModelUsesServiceSamplingDefaults()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"status\":\"completed\",\"output_text\":\"ok\"}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Type = ProviderType.Cloud,
            Platform = ProviderPlatform.OpenAI,
            Protocol = ProviderProtocol.OpenAIResponses,
            ApiBase = "https://api.openai.com/v1",
            Model = "gpt-future-unknown",
            Temperature = 0.1,
            TopP = 0.6,
            InferenceLevel = InferenceLevel.High
        };
        using var service = new AIService(profile, "test-key", handler);

        await service.GenerateAsync("system", "user");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.False(body.RootElement.TryGetProperty("temperature", out _));
        Assert.False(body.RootElement.TryGetProperty("top_p", out _));
        Assert.False(body.RootElement.TryGetProperty("reasoning", out _));
        Assert.True(body.RootElement.TryGetProperty("max_output_tokens", out _));
    }

    [Fact]
    public async Task GenerateAsync_OpenAiResponses_RejectsOtherProviderPlatforms()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var profile = new ProviderProfile
        {
            Type = ProviderType.Cloud,
            Platform = ProviderPlatform.OpenRouter,
            Protocol = ProviderProtocol.OpenAIResponses,
            ApiBase = "https://openrouter.ai/api/v1",
            Model = "openai/gpt-4.1-mini"
        };
        using var service = new AIService(profile, "test-key", handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateAsync("system", "user"));

        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task GenerateStructuredAsync_OpenAiCompatible_PreservesNullableEmotionContract()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"answer\\\":\\\"你好\\\",\\\"companion_emotion\\\":\\\"Supportive\\\",\\\"companion_intensity\\\":0.7}\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile { Type = ProviderType.Cloud, Protocol = ProviderProtocol.OpenAICompatible, ApiBase = "https://api.openai.com/v1", Model = "gpt-4.1-mini" };
        using var service = new AIService(profile, "test-key", handler);

        await service.GenerateStructuredAsync("system", "user", EmotionStructuredSchema);

        using var body = JsonDocument.Parse(handler.Body!);
        var schema = body.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema");
        Assert.Equal("[\"string\",\"null\"]", schema.GetProperty("properties").GetProperty("companion_emotion").GetProperty("type").GetRawText());
        Assert.Equal("[\"Supportive\",\"Cheerful\",null]", schema.GetProperty("properties").GetProperty("companion_emotion").GetProperty("enum").GetRawText());
        Assert.Equal("[\"number\",\"null\"]", schema.GetProperty("properties").GetProperty("companion_intensity").GetProperty("type").GetRawText());
        Assert.False(schema.GetProperty("properties").GetProperty("companion_intensity").TryGetProperty("minimum", out _));
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
    public async Task GenerateAsync_LocalNonOpenAiProtocol_RemoteHttp_IsRejected()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var profile = new ProviderProfile
        {
            Type = ProviderType.Local,
            Platform = ProviderPlatform.CustomOpenAICompatible,
            Protocol = ProviderProtocol.AnthropicMessages,
            ApiBase = "http://remote-server.example.com",
            Model = "claude-test"
        };
        using var service = new AIService(profile, "secret", handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GenerateAsync("system", "user"));

        Assert.Contains("HTTPS", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task GenerateAsync_OfficialPlatformEndpointTampering_IsRejectedBeforeSendingKey()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var profile = new ProviderProfile
        {
            Type = ProviderType.Cloud,
            Platform = ProviderPlatform.OpenAI,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://attacker.example/v1",
            Model = "gpt-test"
        };
        using var service = new AIService(profile, "sk-sensitive", handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GenerateAsync("system", "user"));

        Assert.Contains("官方", error.Message);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task GenerateAsync_LoopbackHttpWithApiKey_IsRejectedBeforeSendingCredential()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var profile = new ProviderProfile
        {
            Type = ProviderType.Local,
            Platform = ProviderPlatform.Ollama,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "http://localhost:11434/v1",
            Model = "qwen3"
        };
        using var service = new AIService(profile, "local-secret", handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GenerateAsync("system", "user"));

        Assert.Contains("API Key", error.Message, StringComparison.OrdinalIgnoreCase);
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
            Platform = ProviderPlatform.Ollama,
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
            Platform = ProviderPlatform.CustomOpenAICompatible,
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
            Platform = ProviderPlatform.CustomOpenAICompatible,
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
            Platform = ProviderPlatform.CustomOpenAICompatible,
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
    public async Task GenerateAsync_DeepSeekV4_SendsExplicitThinkingControls()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"DeepSeek v4 result\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.DeepSeek,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.deepseek.com",
            Model = "deepseek-v4-flash",
            InferenceLevel = InferenceLevel.High
        };
        using var service = new AIService(profile, "deepseek-key", handler);

        var result = await service.GenerateAsync("system", "user");

        Assert.Equal("DeepSeek v4 result", result);
        Assert.Contains("\"thinking\":{\"type\":\"enabled\"}", handler.Body);
        Assert.Contains("\"reasoning_effort\":\"high\"", handler.Body);
        Assert.DoesNotContain("\"temperature\"", handler.Body);
        Assert.Contains("\"top_p\":1", handler.Body);
    }

    [Fact]
    public async Task GenerateAsync_DeepSeekFlash_UsesCurrentThinkingControlsAndClampsTopP()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"DeepSeek flash result\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.DeepSeek,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.deepseek.com",
            Model = "deepseek-flash",
            InferenceLevel = InferenceLevel.Medium,
            TopP = 0.8
        };
        using var service = new AIService(profile, "deepseek-key", handler);

        var result = await service.GenerateAsync("system", "user");

        Assert.Equal("DeepSeek flash result", result);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("enabled", body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("high", body.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Equal(0.95, body.RootElement.GetProperty("top_p").GetDouble());
        Assert.False(body.RootElement.TryGetProperty("temperature", out _));
    }

    [Fact]
    public async Task GenerateAsync_DeepSeekFlash_CustomInferenceUsesProviderDefaultEffort()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"DeepSeek flash result\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.DeepSeek,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.deepseek.com",
            Model = "deepseek-flash",
            InferenceLevel = InferenceLevel.Custom
        };
        using var service = new AIService(profile, "deepseek-key", handler);

        await service.GenerateAsync("system", "user");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("enabled", body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
        Assert.Equal(1.0, body.RootElement.GetProperty("top_p").GetDouble());
        Assert.False(body.RootElement.TryGetProperty("temperature", out _));
    }

    [Fact]
    public async Task GenerateStructuredAsync_DeepSeekFlashUsesJsonModeAndKeepsLocalSchemaValidation()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"answer\\\":\\\"已处理\\\"}\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.DeepSeek,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.deepseek.com",
            Model = "deepseek-flash"
        };
        const string schema = "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}";
        using var service = new AIService(profile, "deepseek-key", handler);

        var result = await service.GenerateStructuredAsync("system", "user", schema);

        Assert.Contains("answer", result);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("json_object", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Contains(schema, body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Theory]
    [InlineData("insufficient_system_resource", GenerationFailureKind.ProviderUnavailable)]
    [InlineData("aborted", GenerationFailureKind.Incomplete)]
    public async Task GenerateAsync_DeepSeekInterruptedFinishReasonIsNotDeliveredAsComplete(string finishReason, GenerationFailureKind expectedKind)
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"{{\"choices\":[{{\"finish_reason\":\"{finishReason}\",\"message\":{{\"content\":\"partial text\"}}}}]}}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.DeepSeek,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.deepseek.com",
            Model = "deepseek-flash"
        };
        using var service = new AIService(profile, "deepseek-key", handler);

        var exception = await Assert.ThrowsAsync<GenerationFailureException>(() => service.GenerateAsync("system", "user"));

        Assert.Equal(expectedKind, exception.Kind);
    }

    [Theory]
    [InlineData("glm-5.3", InferenceLevel.Low, "low")]
    [InlineData("glm-5.3", InferenceLevel.Medium, "high")]
    [InlineData("glm-5.3", InferenceLevel.High, "max")]
    [InlineData("glm-5.2", InferenceLevel.Low, "none")]
    [InlineData("glm-5.2", InferenceLevel.Medium, "low")]
    [InlineData("glm-5.2", InferenceLevel.High, "max")]
    public async Task GenerateAsync_ZhipuCurrentModels_MapsInferenceLevelToSupportedEffort(string model, InferenceLevel level, string expectedEffort)
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"GLM result\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Zhipu,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://open.bigmodel.cn/api/paas/v4",
            Model = model,
            InferenceLevel = level
        };
        using var service = new AIService(profile, "glm-key", handler);

        await service.GenerateAsync("system", "user");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(expectedEffort, body.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Equal(0.4, body.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(1.0, body.RootElement.GetProperty("top_p").GetDouble());
    }

    [Fact]
    public async Task GenerateAsync_ZhipuCurrentModels_CustomInferenceUsesProviderDefaultEffort()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"GLM result\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Zhipu,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://open.bigmodel.cn/api/paas/v4",
            Model = "glm-5.3",
            InferenceLevel = InferenceLevel.Custom
        };
        using var service = new AIService(profile, "glm-key", handler);

        await service.GenerateAsync("system", "user");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Theory]
    [InlineData("glm-5.3")]
    [InlineData("glm-5.2")]
    public async Task GenerateStructuredAsync_ZhipuCurrentModels_UsesJsonModeAndKeepsSchemaForLocalValidation(string model)
    {
        const string schema = "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}";
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"answer\\\":\\\"完成\\\"}\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Zhipu,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://open.bigmodel.cn/api/paas/v4",
            Model = model
        };
        using var service = new AIService(profile, "glm-key", handler);

        await service.GenerateStructuredAsync("保持格式", "原文", schema);

        using var body = JsonDocument.Parse(handler.Body!);
        var format = body.RootElement.GetProperty("response_format");
        Assert.Equal("json_object", format.GetProperty("type").GetString());
        Assert.False(format.TryGetProperty("json_schema", out _));
        var systemMessage = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Contains(schema, systemMessage);
    }

    [Theory]
    [InlineData("4.0Ultra")]
    [InlineData("max-32k")]
    [InlineData("generalv3.5")]
    [InlineData("generalv3")]
    [InlineData("pro-128k")]
    [InlineData("lite")]
    public async Task GenerateStructuredAsync_SparkHttpModels_UsesJsonModeAndKeepsSchemaForLocalValidation(string model)
    {
        const string schema = "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}";
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"answer\\\":\\\"完成\\\"}\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Spark,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://spark-api-open.xf-yun.com/v1",
            Model = model
        };
        using var service = new AIService(profile, "spark-key", handler);

        await service.GenerateStructuredAsync("保持格式", "原文", schema);

        using var body = JsonDocument.Parse(handler.Body!);
        var format = body.RootElement.GetProperty("response_format");
        Assert.Equal("json_object", format.GetProperty("type").GetString());
        Assert.False(format.TryGetProperty("json_schema", out _));
        Assert.Equal("0.4", body.RootElement.GetProperty("temperature").GetRawText());
        Assert.Equal("1", body.RootElement.GetProperty("top_p").GetRawText());
        var systemMessage = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Contains(schema, systemMessage);
    }

    [Theory]
    [InlineData("4.0Ultra", 32768)]
    [InlineData("max-32k", 32768)]
    [InlineData("generalv3.5", 8192)]
    [InlineData("generalv3", 8192)]
    [InlineData("pro-128k", 32768)]
    [InlineData("lite", 4096)]
    public async Task GenerateAsync_SparkClampsPublishedOutputLimitAndOpenTopPRange(string model, int maximumOutputTokens)
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"answer\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Spark,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://spark-api-open.xf-yun.com/v1",
            Model = model,
            MaxTokens = 32768,
            TopP = 0
        };
        using var service = new AIService(profile, "spark-key", handler);

        await service.GenerateAsync("system", "user");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(maximumOutputTokens, body.RootElement.GetProperty("max_tokens").GetInt32());
        var topP = body.RootElement.GetProperty("top_p").GetDouble();
        Assert.True(topP > 0);
        Assert.True(topP <= 1);
    }

    [Fact]
    public async Task GenerateAsync_SparkUnknownModelSuffixDoesNotInheritVersionLimitsOrJsonMode()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"answer\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Spark,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://spark-api-open.xf-yun.com/v1",
            Model = "generalv3.5-custom",
            MaxTokens = 32768
        };
        using var service = new AIService(profile, "spark-key", handler);

        await service.GenerateAsync("system", "user");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(32768, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("response_format", out _));
    }

    [Fact]
    public async Task GenerateStructuredAsync_OpenRouterSparkName_DoesNotInheritSparkJsonMode()
    {
        const string schema = "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}";
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"answer\\\":\\\"完成\\\"}\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.OpenRouter,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://openrouter.ai/api/v1",
            Model = "iflytek/spark-generalv3.5"
        };
        using var service = new AIService(profile, "router-key", handler);

        await service.GenerateStructuredAsync("保持格式", "原文", schema);

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.False(body.RootElement.TryGetProperty("response_format", out _));
        Assert.Contains(schema, body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Theory]
    [InlineData("openai/gpt-oss-20b")]
    [InlineData("openai/gpt-oss-120b")]
    [InlineData("qwen/qwen3.8-27b")]
    public async Task GenerateAsync_GroqReasoningModels_SendSupportedEffort(string model)
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"Groq result\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Groq,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.groq.com/openai/v1",
            Model = model,
            InferenceLevel = InferenceLevel.High
        };
        using var service = new AIService(profile, "groq-key", handler);

        await service.GenerateAsync("system", "user");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("high", body.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Equal(2048, body.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("max_tokens", out _));
    }

    [Theory]
    [InlineData("openai/gpt-oss-20b", "include_reasoning", "false")]
    [InlineData("openai/gpt-oss-120b", "include_reasoning", "false")]
    [InlineData("qwen/qwen3.8-27b", "reasoning_format", "hidden")]
    public async Task GenerateAsync_GroqReasoningModelsSuppressReasoningFromResponse(string model, string field, string expected)
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"final answer\",\"reasoning\":\"private reasoning\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Groq,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.groq.com/openai/v1",
            Model = model,
            InferenceLevel = InferenceLevel.High
        };
        using var service = new AIService(profile, "groq-key", handler);

        var result = await service.GenerateAsync("system", "user");

        using var body = JsonDocument.Parse(handler.Body!);
        if (field == "include_reasoning") Assert.False(body.RootElement.GetProperty(field).GetBoolean());
        else Assert.Equal(expected, body.RootElement.GetProperty(field).GetString());
        Assert.Equal("final answer", result);
        Assert.DoesNotContain("private reasoning", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_GroqUnverifiedModelSuffixDoesNotInheritReasoningControls()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"answer\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Groq,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.groq.com/openai/v1",
            Model = "openai/gpt-oss-120b-custom-suffix",
            InferenceLevel = InferenceLevel.High
        };
        using var service = new AIService(profile, "groq-key", handler);

        await service.GenerateAsync("system", "user");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
        Assert.False(body.RootElement.TryGetProperty("include_reasoning", out _));
        Assert.False(body.RootElement.TryGetProperty("reasoning_format", out _));
    }

    [Theory]
    [InlineData("Pro/deepseek-ai/DeepSeek-V4")]
    [InlineData("deepseek-ai/DeepSeek-V4-Flash")]
    [InlineData("Pro/zai-org/GLM-5.2")]
    public async Task GenerateAsync_SiliconFlowReasoningModels_EnableThinkingAndPreserveFinalTokenLimit(string model)
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"最终答复\",\"reasoning_content\":\"private reasoning\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.SiliconFlow,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.siliconflow.cn/v1",
            Model = model,
            InferenceLevel = InferenceLevel.High
        };
        using var service = new AIService(profile, "siliconflow-key", handler);

        var result = await service.GenerateAsync("system", "user");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.True(body.RootElement.GetProperty("enable_thinking").GetBoolean());
        Assert.Equal("high", body.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Equal(2048, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("thinking_budget", out _));
        Assert.Equal("最终答复", result);
        Assert.DoesNotContain("private reasoning", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(InferenceLevel.Low)]
    [InlineData(InferenceLevel.Medium)]
    public async Task GenerateAsync_SiliconFlowReasoningModels_DoNotClaimUnsupportedLowerEffort(InferenceLevel level)
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"答复\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.SiliconFlow,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.siliconflow.cn/v1",
            Model = "deepseek-ai/DeepSeek-V4-Flash",
            InferenceLevel = level
        };
        using var service = new AIService(profile, "siliconflow-key", handler);

        await service.GenerateAsync("system", "user");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.True(body.RootElement.GetProperty("enable_thinking").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Theory]
    [InlineData("Pro/deepseek-ai/DeepSeek-V4")]
    [InlineData("deepseek-ai/DeepSeek-V4-Flash")]
    [InlineData("Pro/zai-org/GLM-5.2")]
    public async Task GenerateStructuredAsync_SiliconFlowReasoningModels_UseJsonModeAndKeepLocalSchemaValidation(string model)
    {
        const string schema = "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}";
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"answer\\\":\\\"完成\\\"}\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.SiliconFlow,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.siliconflow.cn/v1",
            Model = model
        };
        using var service = new AIService(profile, "siliconflow-key", handler);

        var result = await service.GenerateStructuredAsync("follow schema", "input", schema);

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("json_object", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Contains(schema, body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Contains("完成", result);
    }

    [Fact]
    public async Task GenerateStructuredAsync_SiliconFlowDeepSeekV3_UsesJsonModeAndPassesSchemaForLocalValidation()
    {
        const string schema = "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}";
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"answer\\\":\\\"完成\\\"}\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.SiliconFlow,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.siliconflow.cn/v1",
            Model = "deepseek-ai/DeepSeek-V3"
        };
        using var service = new AIService(profile, "siliconflow-key", handler);

        var result = await service.GenerateStructuredAsync("follow schema", "input", schema);

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("json_object", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.False(body.RootElement.GetProperty("response_format").TryGetProperty("json_schema", out _));
        Assert.Contains(schema, body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Contains("完成", result);
    }

    [Fact]
    public async Task GenerateStructuredAsync_SiliconFlowQwen25_UsesJsonModeAndDocumentedSamplingParameters()
    {
        const string schema = "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}";
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"answer\\\":\\\"完成\\\"}\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.SiliconFlow,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.siliconflow.cn/v1",
            Model = "Qwen/Qwen2.5-7B-Instruct",
            Temperature = 0.4,
            TopP = 0.9
        };
        using var service = new AIService(profile, "siliconflow-key", handler);

        await service.GenerateStructuredAsync("follow schema", "input", schema);

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(0.4, body.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(0.9, body.RootElement.GetProperty("top_p").GetDouble());
        Assert.Equal("json_object", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.False(body.RootElement.GetProperty("response_format").TryGetProperty("json_schema", out _));
        Assert.Contains(schema, body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task GenerateAsync_OpenRouterSiliconFlowModel_DoesNotInheritSiliconFlowThinkingControls()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"答复\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.OpenRouter,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://openrouter.ai/api/v1",
            Model = "deepseek-ai/DeepSeek-V4-Flash",
            InferenceLevel = InferenceLevel.High
        };
        using var service = new AIService(profile, "router-key", handler);

        await service.GenerateAsync("system", "user");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.False(body.RootElement.TryGetProperty("enable_thinking", out _));
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public void SiliconFlowPreset_OffersCurrentReasoningModelsWithoutChangingDefault()
    {
        var preset = ProviderPlatformCatalog.Options.Single(option => option.Platform == ProviderPlatform.SiliconFlow);

        Assert.Equal("deepseek-ai/DeepSeek-V3", preset.DefaultModel);
        Assert.Contains(preset.Models!, model => model.ModelId == "Pro/deepseek-ai/DeepSeek-V4");
        Assert.Contains(preset.Models!, model => model.ModelId == "deepseek-ai/DeepSeek-V4-Flash");
        Assert.Contains(preset.Models!, model => model.ModelId == "Pro/zai-org/GLM-5.2");
    }

    [Fact]
    public async Task GenerateStructuredAsync_MistralLargeLatest_UsesDocumentedJsonModeAndLocalSchema()
    {
        const string schema = "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}";
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"answer\\\":\\\"完成\\\"}\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Mistral,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.mistral.ai/v1",
            Model = "mistral-large-latest"
        };
        using var service = new AIService(profile, "mistral-key", handler);

        await service.GenerateStructuredAsync("返回约定结构", "原文", schema);

        using var body = JsonDocument.Parse(handler.Body!);
        var format = body.RootElement.GetProperty("response_format");
        Assert.Equal("json_object", format.GetProperty("type").GetString());
        Assert.False(format.TryGetProperty("json_schema", out _));
        Assert.Equal(2048, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Contains(schema, body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Theory]
    [InlineData("mistral-small-latest")]
    [InlineData("mistral-medium-3-5")]
    public async Task GenerateAsync_MistralReasoningModels_SendSelectedEffortAndReturnOnlyFinalText(string model)
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"choices\":[{\"message\":{\"content\":[{\"type\":\"thinking\",\"thinking\":[{\"type\":\"text\",\"text\":\"private reasoning\"}]},{\"type\":\"text\",\"text\":\"最终答复\"}]}}]}",
                Encoding.UTF8,
                "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Mistral,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.mistral.ai/v1",
            Model = model,
            InferenceLevel = InferenceLevel.High
        };
        using var service = new AIService(profile, "mistral-key", handler);

        var result = await service.GenerateAsync("system", "user");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("high", body.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Equal("最终答复", result);
        Assert.DoesNotContain("private reasoning", result, StringComparison.Ordinal);
    }

    [Fact]
    public void MistralPreset_OffersCurrentSmall4AndMedium35ModelsWithoutChangingDefault()
    {
        var preset = ProviderPlatformCatalog.Options.Single(option => option.Platform == ProviderPlatform.Mistral);
        var models = Assert.IsAssignableFrom<IReadOnlyList<ModelDefinition>>(preset.Models);

        Assert.Equal("mistral-small-latest", preset.DefaultModel);
        Assert.Contains(models, model => model.ModelId == "mistral-small-2603");
        Assert.Contains(models, model => model.ModelId == "mistral-medium-3-5");
        Assert.Contains(models, model => model.ModelId == "ministral-8b-2512");
        Assert.Contains("已弃用", models.Single(model => model.ModelId == "open-mistral-nemo").DisplayName);
        Assert.Contains("Ministral 3 8B", models.Single(model => model.ModelId == "open-mistral-nemo").DisplayName);
    }

    [Fact]
    public async Task GenerateStructuredAsync_Ministral3_8B_UsesDocumentedNativeSchema()
    {
        const string schema = "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}";
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"answer\\\":\\\"完成\\\"}\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Mistral,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.mistral.ai/v1",
            Model = "ministral-8b-2512"
        };
        using var service = new AIService(profile, "mistral-key", handler);

        await service.GenerateStructuredAsync("返回约定结构", "原文", schema);

        using var body = JsonDocument.Parse(handler.Body!);
        var format = body.RootElement.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("json_schema").GetProperty("strict").GetBoolean());
        Assert.Equal("ministral-8b-2512", body.RootElement.GetProperty("model").GetString());
    }

    [Theory]
    [InlineData("openai/gpt-oss-20b")]
    [InlineData("openai/gpt-oss-120b")]
    [InlineData("qwen/qwen3.8-27b")]
    public async Task GenerateStructuredAsync_GroqStrictModels_UsesStrictJsonSchema(string model)
    {
        const string schema = "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}";
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"answer\\\":\\\"完成\\\"}\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Groq,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.groq.com/openai/v1",
            Model = model
        };
        using var service = new AIService(profile, "groq-key", handler);

        await service.GenerateStructuredAsync("保持格式", "原文", schema);

        using var body = JsonDocument.Parse(handler.Body!);
        var format = body.RootElement.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("json_schema").GetProperty("strict").GetBoolean());
    }

    [Fact]
    public async Task GenerateStructuredAsync_GroqLlamaModel_UsesJsonMode()
    {
        const string schema = "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}";
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"answer\\\":\\\"完成\\\"}\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Groq,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.groq.com/openai/v1",
            Model = "llama-3.3-70b-versatile"
        };
        using var service = new AIService(profile, "groq-key", handler);

        await service.GenerateStructuredAsync("保持格式", "原文", schema);

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("json_object", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
    }

    [Fact]
    public async Task GenerateStructuredAsync_TogetherQwen35UsesNativeSchemaAndClampsTemperature()
    {
        const string schema = "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}";
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"answer\\\":\\\"完成\\\"}\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Together,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.together.ai/v1",
            Model = "Qwen/Qwen3.5-9B",
            Temperature = 1.7,
            TopP = 0.82,
            MaxTokens = 4096
        };
        using var service = new AIService(profile, "together-key", handler);

        await service.GenerateStructuredAsync("保留事实并输出结构化结果", "原文", schema);

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("Qwen/Qwen3.5-9B", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(1.0, body.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(0.82, body.RootElement.GetProperty("top_p").GetDouble());
        Assert.Equal(4096, body.RootElement.GetProperty("max_tokens").GetInt32());
        var format = body.RootElement.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("json_schema").GetProperty("strict").GetBoolean());
        Assert.Equal("answer", format.GetProperty("json_schema").GetProperty("schema")
            .GetProperty("required")[0].GetString());
    }

    [Fact]
    public async Task GenerateAsync_OpenRouterGroqModel_DoesNotInheritGroqReasoning()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"Router result\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.OpenRouter,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://openrouter.ai/api/v1",
            Model = "openai/gpt-oss-120b",
            InferenceLevel = InferenceLevel.High
        };
        using var service = new AIService(profile, "router-key", handler);

        await service.GenerateAsync("system", "user");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task GenerateAsync_OpenRouterGlm53_DoesNotInheritZhipuCapabilities()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"GLM result\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.OpenRouter,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://openrouter.ai/api/v1",
            Model = "zhipu/glm-5.3",
            InferenceLevel = InferenceLevel.High
        };
        using var service = new AIService(profile, "router-key", handler);

        await service.GenerateAsync("system", "user");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
        Assert.Equal("0.4", body.RootElement.GetProperty("temperature").GetRawText());
        Assert.Equal("1", body.RootElement.GetProperty("top_p").GetRawText());
    }

    [Theory]
    [InlineData(InferenceLevel.Low, "low")]
    [InlineData(InferenceLevel.Medium, "high")]
    [InlineData(InferenceLevel.High, "max")]
    public async Task GenerateAsync_KimiK3_MapsInferenceLevelToSupportedEffort(InferenceLevel level, string expectedEffort)
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"Kimi result\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Kimi,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.moonshot.cn/v1",
            Model = "kimi-k3",
            InferenceLevel = level
        };
        using var service = new AIService(profile, "kimi-key", handler);

        await service.GenerateAsync("system", "user");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(expectedEffort, body.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.False(body.RootElement.TryGetProperty("temperature", out _));
        Assert.False(body.RootElement.TryGetProperty("top_p", out _));
    }

    [Fact]
    public async Task GenerateAsync_KimiK3_CustomInferenceUsesProviderDefaultEffort()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"Kimi result\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Kimi,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.moonshot.cn/v1",
            Model = "kimi-k3",
            InferenceLevel = InferenceLevel.Custom
        };
        using var service = new AIService(profile, "kimi-key", handler);

        await service.GenerateAsync("system", "user");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
        Assert.False(body.RootElement.TryGetProperty("temperature", out _));
        Assert.False(body.RootElement.TryGetProperty("top_p", out _));
    }

    [Theory]
    [InlineData("kimi-k2.6", InferenceLevel.Low, "disabled")]
    [InlineData("kimi-k2.6", InferenceLevel.Medium, null)]
    [InlineData("kimi-k2.6", InferenceLevel.High, null)]
    [InlineData("kimi-k2.6", InferenceLevel.Custom, null)]
    [InlineData("kimi-k2.7-code", InferenceLevel.Low, null)]
    [InlineData("kimi-k2.7-code", InferenceLevel.High, null)]
    [InlineData("kimi-k2.7-code-highspeed", InferenceLevel.Low, null)]
    public async Task GenerateAsync_KimiK2Models_MapsOnlySupportedThinkingControl(
        string model, InferenceLevel inferenceLevel, string? expectedThinkingType)
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"Kimi result\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Kimi,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.moonshot.cn/v1",
            Model = model,
            Temperature = 0.4,
            TopP = 0.8,
            InferenceLevel = inferenceLevel
        };
        using var service = new AIService(profile, "kimi-key", handler);

        await service.GenerateAsync("system", "user");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.False(body.RootElement.TryGetProperty("temperature", out _));
        Assert.False(body.RootElement.TryGetProperty("top_p", out _));
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
        if (expectedThinkingType is null)
        {
            Assert.False(body.RootElement.TryGetProperty("thinking", out _));
        }
        else
        {
            Assert.Equal(expectedThinkingType, body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        }
    }

    [Fact]
    public async Task GenerateAsync_OpenRouterKimiK3_DoesNotInheritMoonshotCapabilities()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"Kimi result\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.OpenRouter,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://openrouter.ai/api/v1",
            Model = "kimi-k3",
            InferenceLevel = InferenceLevel.High
        };
        using var service = new AIService(profile, "router-key", handler);

        await service.GenerateAsync("system", "user");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
        Assert.Equal(0.4, body.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(1.0, body.RootElement.GetProperty("top_p").GetDouble());
    }

    [Theory]
    [InlineData("qwen3.8-max", "max_completion_tokens", "json_schema")]
    [InlineData("qwen3.8-max-0902", "max_completion_tokens", "json_schema")]
    [InlineData("qwen3.8-flash", "max_completion_tokens", "json_schema")]
    [InlineData("qwen3.8-2.4t-a95b", "max_tokens", "json_object")]
    [InlineData("qwen3.8-27b", "max_tokens", "json_object")]
    [InlineData("qwen-plus", "max_tokens", "json_object")]
    [InlineData("qwen-turbo", "max_tokens", "json_object")]
    [InlineData("qwen-max", "max_tokens", "json_object")]
    [InlineData("qwen-long", "max_tokens", "json_object")]
    public async Task GenerateStructuredAsync_QwenModels_UsesDocumentedOutputContract(
        string model,
        string tokenLimitField,
        string responseFormatType)
    {
        const string schema = "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}";
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"answer\\\":\\\"完成\\\"}\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Qwen,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://dashscope.aliyuncs.com/compatible-mode/v1",
            Model = model
        };
        using var service = new AIService(profile, "qwen-key", handler);

        await service.GenerateStructuredAsync("返回约定结构", "原文", schema);

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.True(body.RootElement.TryGetProperty(tokenLimitField, out _));
        var otherTokenLimitField = tokenLimitField == "max_tokens" ? "max_completion_tokens" : "max_tokens";
        Assert.False(body.RootElement.TryGetProperty(otherTokenLimitField, out _));
        var format = body.RootElement.GetProperty("response_format");
        Assert.Equal(responseFormatType, format.GetProperty("type").GetString());
        Assert.Equal(responseFormatType == "json_schema", format.TryGetProperty("json_schema", out _));
    }

    [Fact]
    public async Task GenerateStructuredAsync_QwenUnknownModelSuffixDoesNotInheritModelCapabilities()
    {
        const string schema = "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}";
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"answer\\\":\\\"完成\\\"}\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Qwen,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://dashscope.aliyuncs.com/compatible-mode/v1",
            Model = "qwen3.8-max-experimental"
        };
        using var service = new AIService(profile, "qwen-key", handler);

        await service.GenerateStructuredAsync("返回约定结构", "原文", schema);

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.True(body.RootElement.TryGetProperty("max_tokens", out _));
        Assert.False(body.RootElement.TryGetProperty("max_completion_tokens", out _));
        Assert.False(body.RootElement.TryGetProperty("response_format", out _));
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task GenerateAsync_QwenClampsSamplingToDocumentedExclusiveBounds()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"Qwen result\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Qwen,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://dashscope.aliyuncs.com/compatible-mode/v1",
            Model = "qwen-plus",
            Temperature = 2.0,
            TopP = 0.0
        };
        using var service = new AIService(profile, "qwen-key", handler);

        await service.GenerateAsync("system", "user");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(double.BitDecrement(2.0), body.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(double.BitIncrement(0.0), body.RootElement.GetProperty("top_p").GetDouble());
    }

    [Theory]
    [InlineData("qwen3.8-max", InferenceLevel.Low, "low")]
    [InlineData("qwen3.8-flash", InferenceLevel.Medium, "medium")]
    [InlineData("qwen3.8-27b", InferenceLevel.High, "xhigh")]
    public async Task GenerateAsync_Qwen38_MapsInferenceLevelToSupportedEffort(string model, InferenceLevel level, string expectedEffort)
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"Qwen result\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Qwen,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://dashscope.aliyuncs.com/compatible-mode/v1",
            Model = model,
            InferenceLevel = level
        };
        using var service = new AIService(profile, "qwen-key", handler);

        await service.GenerateAsync("system", "user");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(expectedEffort, body.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.False(body.RootElement.TryGetProperty("thinking_budget", out _));
    }

    [Fact]
    public async Task GenerateAsync_Qwen38_CustomInferenceUsesProviderDefaultEffort()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"Qwen result\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Qwen,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://dashscope.aliyuncs.com/compatible-mode/v1",
            Model = "qwen3.8-max",
            InferenceLevel = InferenceLevel.Custom
        };
        using var service = new AIService(profile, "qwen-key", handler);

        await service.GenerateAsync("system", "user");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task GenerateAsync_OpenRouterQwen38_DoesNotInheritAlibabaCapabilities()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"Qwen result\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.OpenRouter,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://openrouter.ai/api/v1",
            Model = "qwen3.8-max",
            InferenceLevel = InferenceLevel.High
        };
        using var service = new AIService(profile, "router-key", handler);

        await service.GenerateAsync("system", "user");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task GenerateAsync_DoubaoSeed20Lite260215_OmitsIgnoredSamplingParameters()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"Doubao result\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Doubao,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://ark.cn-beijing.volces.com/api/v3",
            Model = "doubao-seed-2-0-lite-260215",
            Temperature = 0.4,
            TopP = 0.8
        };
        using var service = new AIService(profile, "doubao-key", handler);

        await service.GenerateAsync("system", "user");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.False(body.RootElement.TryGetProperty("temperature", out _));
        Assert.False(body.RootElement.TryGetProperty("top_p", out _));
    }

    [Theory]
    [InlineData(InferenceLevel.Low, "low")]
    [InlineData(InferenceLevel.Medium, "medium")]
    [InlineData(InferenceLevel.High, "high")]
    public async Task GenerateAsync_DoubaoSeed20Lite260428_MapsInferenceLevel(InferenceLevel level, string expectedEffort)
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"Doubao result\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Doubao,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://ark.cn-beijing.volces.com/api/v3",
            Model = "doubao-seed-2-0-lite-260428",
            InferenceLevel = level
        };
        using var service = new AIService(profile, "doubao-key", handler);

        await service.GenerateAsync("system", "user");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(expectedEffort, body.RootElement.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task GenerateAsync_OpenRouterDoubaoId_DoesNotInheritArkCapabilities()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"Doubao result\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.OpenRouter,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://openrouter.ai/api/v1",
            Model = "doubao-seed-2-0-lite-260428",
            InferenceLevel = InferenceLevel.High
        };
        using var service = new AIService(profile, "router-key", handler);

        await service.GenerateAsync("system", "user");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task GenerateAsync_DeepSeekV4_EmptyFinalContentRetriesWithThinkingDisabled()
    {
        var handler = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":null,\"reasoning_content\":\"思考\"}}]}", Encoding.UTF8, "application/json")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"最终答案\"}}]}", Encoding.UTF8, "application/json")
            });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.DeepSeek,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.deepseek.com",
            Model = "deepseek-v4-flash",
            InferenceLevel = InferenceLevel.Medium,
            TopP = 0.8
        };
        using var service = new AIService(profile, "deepseek-key", handler);

        var result = await service.GenerateAsync("system", "user");

        Assert.Equal("最终答案", result);
        Assert.Equal(2, handler.Calls);
        using var thinkingBody = JsonDocument.Parse(handler.Bodies[0]);
        using var retryBody = JsonDocument.Parse(handler.Bodies[1]);
        Assert.Equal(0.95, thinkingBody.RootElement.GetProperty("top_p").GetDouble());
        Assert.False(thinkingBody.RootElement.TryGetProperty("temperature", out _));
        Assert.Equal("disabled", retryBody.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(0.4, retryBody.RootElement.GetProperty("temperature").GetDouble());
        Assert.False(retryBody.RootElement.TryGetProperty("top_p", out _));
        Assert.False(retryBody.RootElement.TryGetProperty("reasoning_effort", out _));
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
    public async Task GenerateAsync_OversizedProviderResponseIsRejected()
    {
        var profile = new ProviderProfile { Type = ProviderType.Cloud };
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', 5 * 1024 * 1024), Encoding.UTF8, "application/json")
        };
        using var service = new AIService(profile, "key", new StaticResponseHandler(response));

        var error = await Assert.ThrowsAsync<GenerationFailureException>(() => service.GenerateAsync("system", "user"));

        Assert.Contains("过大", error.Message);
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
    public async Task GenerateAsync_ForbiddenIsPermissionFailure_NotAuthenticationFailure()
    {
        var telemetry = new List<ProviderRequestTelemetry>();
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("{\"error\":{\"type\":\"permission_error\",\"message\":\"secret detail\"}}", Encoding.UTF8, "application/json")
        };
        using var service = new AIService(new ProviderProfile(), "key", new StaticResponseHandler(response), telemetryObserver: telemetry.Add);

        var error = await Assert.ThrowsAsync<GenerationFailureException>(() => service.GenerateAsync("system", "user"));

        Assert.Equal(GenerationFailureKind.PermissionDenied, error.Kind);
        Assert.Contains("权限不足", error.Message);
        Assert.DoesNotContain("secret detail", error.Message);
        Assert.Equal("permission_denied", Assert.Single(telemetry).Outcome);
    }

    [Fact]
    public async Task GenerateAsync_PaymentRequiredIsBillingFailure_WithoutLeakingProviderBody()
    {
        var telemetry = new List<ProviderRequestTelemetry>();
        var response = new HttpResponseMessage(HttpStatusCode.PaymentRequired)
        {
            Content = new StringContent("{\"error\":{\"message\":\"private billing detail\"}}", Encoding.UTF8, "application/json")
        };
        using var service = new AIService(new ProviderProfile(), "key", new StaticResponseHandler(response), telemetryObserver: telemetry.Add);

        var error = await Assert.ThrowsAsync<GenerationFailureException>(() => service.GenerateAsync("system", "user"));

        Assert.Equal(GenerationFailureKind.BillingIssue, error.Kind);
        Assert.Contains("账单", error.Message);
        Assert.DoesNotContain("private billing detail", error.Message);
        Assert.Equal("billing_issue", Assert.Single(telemetry).Outcome);
    }

    [Fact]
    public async Task GenerateAsync_GeminiInvalidApiKeyErrorInfo_IsAuthenticationFailure()
    {
        var telemetry = new List<ProviderRequestTelemetry>();
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "{\"error\":{\"code\":400,\"message\":\"private credential detail\",\"status\":\"INVALID_ARGUMENT\",\"details\":[{\"@type\":\"type.googleapis.com/google.rpc.ErrorInfo\",\"reason\":\"API_KEY_INVALID\",\"domain\":\"googleapis.com\",\"metadata\":{\"service\":\"generativelanguage.googleapis.com\"}}]}}",
                Encoding.UTF8, "application/json")
        };
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Gemini,
            Protocol = ProviderProtocol.GeminiGenerateContent,
            ApiBase = "https://generativelanguage.googleapis.com/v1beta",
            Model = "gemini-2.5-flash"
        };
        using var service = new AIService(profile, "key", new StaticResponseHandler(response), telemetryObserver: telemetry.Add);

        var error = await Assert.ThrowsAsync<GenerationFailureException>(() => service.GenerateAsync("system", "user"));

        Assert.Equal(GenerationFailureKind.Authentication, error.Kind);
        Assert.Contains("API Key", error.Message);
        Assert.DoesNotContain("private credential detail", error.Message);
        Assert.Equal("authentication", Assert.Single(telemetry).Outcome);
    }

    [Fact]
    public async Task GenerateAsync_GeminiUnrecognizedInvalidArgument_RemainsRequestRejected()
    {
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "{\"error\":{\"code\":400,\"message\":\"private schema detail\",\"status\":\"INVALID_ARGUMENT\",\"details\":[{\"@type\":\"type.googleapis.com/google.rpc.ErrorInfo\",\"reason\":\"INVALID_ARGUMENT\",\"domain\":\"googleapis.com\",\"metadata\":{\"service\":\"generativelanguage.googleapis.com\"}}]}}",
                Encoding.UTF8, "application/json")
        };
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Gemini,
            Protocol = ProviderProtocol.GeminiGenerateContent,
            ApiBase = "https://generativelanguage.googleapis.com/v1beta",
            Model = "gemini-2.5-flash"
        };
        using var service = new AIService(profile, "key", new StaticResponseHandler(response));

        var error = await Assert.ThrowsAsync<GenerationFailureException>(() => service.GenerateAsync("system", "user"));

        Assert.Equal(GenerationFailureKind.RequestRejected, error.Kind);
        Assert.DoesNotContain("private schema detail", error.Message);
    }

    [Fact]
    public async Task GenerateAsync_GeminiFailedPrecondition_IsAccountPrerequisiteFailure()
    {
        var telemetry = new List<ProviderRequestTelemetry>();
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Gemini,
            Protocol = ProviderProtocol.GeminiGenerateContent,
            ApiBase = "https://generativelanguage.googleapis.com/v1beta",
            Model = "gemini-2.5-flash"
        };
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "{\"error\":{\"code\":400,\"message\":\"private region/billing detail\",\"status\":\"FAILED_PRECONDITION\"}}",
                Encoding.UTF8, "application/json")
        };
        using var service = new AIService(profile, "key", new StaticResponseHandler(response), telemetryObserver: telemetry.Add);

        var error = await Assert.ThrowsAsync<GenerationFailureException>(() => service.GenerateAsync("system", "user"));

        Assert.Equal(GenerationFailureKind.AccountPrerequisite, error.Kind);
        Assert.Contains("计费", error.Message);
        Assert.Contains("地区", error.Message);
        Assert.DoesNotContain("private region/billing detail", error.Message);
        Assert.Equal("account_prerequisite", Assert.Single(telemetry).Outcome);
    }

    [Fact]
    public async Task GenerateAsync_RequestTooLarge_HasActionableFailureWithoutBodyLeak()
    {
        var telemetry = new List<ProviderRequestTelemetry>();
        var response = new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge)
        {
            Content = new StringContent("private proxy detail", Encoding.UTF8, "text/plain")
        };
        using var service = new AIService(new ProviderProfile(), "key", new StaticResponseHandler(response), telemetryObserver: telemetry.Add);

        var error = await Assert.ThrowsAsync<GenerationFailureException>(() => service.GenerateAsync("system", "user"));

        Assert.Equal(GenerationFailureKind.RequestTooLarge, error.Kind);
        Assert.Contains("请求体", error.Message);
        Assert.Contains("附件", error.Message);
        Assert.DoesNotContain("private proxy detail", error.Message);
        Assert.Equal("request_too_large", Assert.Single(telemetry).Outcome);
    }

    [Fact]
    public async Task GenerateAsync_JsonHttpError_DoesNotPersistProviderMessage()
    {
        var profile = new ProviderProfile { Type = ProviderType.Cloud };
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":{\"message\":\"temperature is not supported\",\"request_id\":\"secret-id\"}}", Encoding.UTF8, "application/json")
        };
        using var service = new AIService(profile, "key", new StaticResponseHandler(response));

        var error = await Assert.ThrowsAsync<GenerationFailureException>(() => service.GenerateAsync("system", "user"));

        Assert.DoesNotContain("temperature is not supported", error.Message);
        Assert.DoesNotContain("secret-id", error.Message);
        Assert.Equal(GenerationFailureKind.RequestRejected, error.Kind);
    }

    [Fact]
    public async Task GenerateStructuredAsync_ExplicitFormatParameterError_IsClassifiedWithoutLeakingProviderBody()
    {
        var telemetry = new List<ProviderRequestTelemetry>();
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "{\"error\":{\"param\":\"response_format\",\"code\":\"unsupported_parameter\",\"message\":\"secret user text\"}}",
                Encoding.UTF8, "application/json")
        };
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.OpenAI,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.openai.com/v1",
            Model = "gpt-4o-mini"
        };
        using var service = new AIService(profile, "key", new StaticResponseHandler(response), telemetryObserver: telemetry.Add);

        var error = await Assert.ThrowsAsync<GenerationFailureException>(() =>
            service.GenerateStructuredAsync("system", "user", "{\"type\":\"object\"}"));

        Assert.Equal(GenerationFailureKind.StructuredOutputUnsupported, error.Kind);
        Assert.Equal(HttpStatusCode.BadRequest, error.HttpStatusCode);
        Assert.DoesNotContain("secret user text", error.Message);
        Assert.Equal("structured_output_unsupported", Assert.Single(telemetry).Outcome);
    }

    [Fact]
    public async Task GenerateStructuredAsync_AnthropicSchemaCompilationLimit_RequestsLocalFallbackWithoutLeakingBody()
    {
        var telemetry = new List<ProviderRequestTelemetry>();
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"Schema is too complex for compilation.\"},\"request_id\":\"req_private\"}",
                Encoding.UTF8, "application/json")
        };
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Anthropic,
            Protocol = ProviderProtocol.AnthropicMessages,
            ApiBase = "https://api.anthropic.com",
            Model = "claude-sonnet-4-5"
        };
        using var service = new AIService(profile, "anthropic-key", new StaticResponseHandler(response), telemetryObserver: telemetry.Add);

        var error = await Assert.ThrowsAsync<GenerationFailureException>(() =>
            service.GenerateStructuredAsync("system", "user", "{\"type\":\"object\"}"));

        Assert.Equal(GenerationFailureKind.StructuredOutputUnsupported, error.Kind);
        Assert.DoesNotContain("Schema is too complex", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("req_private", error.Message, StringComparison.Ordinal);
        Assert.Equal("structured_output_unsupported", Assert.Single(telemetry).Outcome);
    }

    [Fact]
    public async Task GenerateStructuredAsync_AnthropicUnrelatedInvalidRequest_DoesNotRequestSchemaFallback()
    {
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"temperature is not supported\"},\"request_id\":\"req_private\"}",
                Encoding.UTF8, "application/json")
        };
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Anthropic,
            Protocol = ProviderProtocol.AnthropicMessages,
            ApiBase = "https://api.anthropic.com",
            Model = "claude-sonnet-4-5"
        };
        using var service = new AIService(profile, "anthropic-key", new StaticResponseHandler(response));

        var error = await Assert.ThrowsAsync<GenerationFailureException>(() =>
            service.GenerateStructuredAsync("system", "user", "{\"type\":\"object\"}"));

        Assert.Equal(GenerationFailureKind.RequestRejected, error.Kind);
        Assert.DoesNotContain("temperature is not supported", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("req_private", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateStructuredAsync_UnknownCapabilityResponseFormatError_RemainsGenericRequestRejection()
    {
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "{\"error\":{\"param\":\"response_format\",\"code\":\"unsupported_parameter\",\"message\":\"private detail\"}}",
                Encoding.UTF8, "application/json")
        };
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.CustomOpenAICompatible,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.example.test/v1",
            Model = "vendor/unknown-model"
        };
        using var service = new AIService(profile, "test-key", new StaticResponseHandler(response));

        var error = await Assert.ThrowsAsync<GenerationFailureException>(() =>
            service.GenerateStructuredAsync("system", "user", "{\"type\":\"object\"}"));

        Assert.Equal(GenerationFailureKind.RequestRejected, error.Kind);
        Assert.DoesNotContain("private detail", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateStructuredAsync_UnrelatedBadRequest_DoesNotBecomeSchemaFallbackSignal()
    {
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "{\"error\":{\"param\":\"temperature\",\"code\":\"unsupported_value\",\"message\":\"secret detail\"}}",
                Encoding.UTF8, "application/json")
        };
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.OpenAI,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.openai.com/v1",
            Model = "gpt-4o-mini"
        };
        using var service = new AIService(profile, "key", new StaticResponseHandler(response));

        var error = await Assert.ThrowsAsync<GenerationFailureException>(() =>
            service.GenerateStructuredAsync("system", "user", "{\"type\":\"object\"}"));

        Assert.Equal(GenerationFailureKind.RequestRejected, error.Kind);
        Assert.DoesNotContain("secret detail", error.Message);
    }

    [Fact]
    public async Task GenerateAsync_UserCancellation_RemainsCancellation()
    {
        var profile = new ProviderProfile
        {
            Type = ProviderType.Local,
            Platform = ProviderPlatform.Ollama,
            ApiBase = "http://localhost:11434/v1"
        };
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
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.False(body.RootElement.TryGetProperty("output_config", out _));
    }

    [Theory]
    [InlineData("max_tokens", GenerationFailureKind.Incomplete)]
    [InlineData("model_context_window_exceeded", GenerationFailureKind.ContextLimitExceeded)]
    [InlineData("refusal", GenerationFailureKind.Refused)]
    [InlineData("tool_use", GenerationFailureKind.InvalidResponse)]
    [InlineData("pause_turn", GenerationFailureKind.InvalidResponse)]
    public async Task GenerateAsync_Anthropic_DoesNotReturnTextForNonFinalStopReason(
        string stopReason,
        GenerationFailureKind expectedKind)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"{{\"content\":[{{\"type\":\"text\",\"text\":\"部分输出\"}}],\"stop_reason\":\"{stopReason}\"}}",
                Encoding.UTF8,
                "application/json")
        };
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Anthropic,
            Protocol = ProviderProtocol.AnthropicMessages,
            ApiBase = "https://api.anthropic.com",
            Model = "claude-sonnet-4-5"
        };
        using var service = new AIService(profile, "anthropic-key", new StaticResponseHandler(response));

        var error = await Assert.ThrowsAsync<GenerationFailureException>(() => service.GenerateAsync("system", "user"));

        Assert.Equal(expectedKind, error.Kind);
        Assert.DoesNotContain("部分输出", error.Message);
        if (stopReason == "model_context_window_exceeded")
            Assert.Contains("上下文窗口", error.Message);
    }

    [Fact]
    public async Task GenerateAsync_AnthropicContextWindowExceeded_ReportsDistinctActionAndTelemetry()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"content\":[{\"type\":\"text\",\"text\":\"部分输出\"}],\"stop_reason\":\"model_context_window_exceeded\"}",
                Encoding.UTF8,
                "application/json")
        };
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Anthropic,
            Protocol = ProviderProtocol.AnthropicMessages,
            ApiBase = "https://api.anthropic.com",
            Model = "claude-sonnet-4-5"
        };
        var telemetry = new List<ProviderRequestTelemetry>();
        using var service = new AIService(profile, "anthropic-key", new StaticResponseHandler(response), telemetryObserver: telemetry.Add);

        var error = await Assert.ThrowsAsync<GenerationFailureException>(() => service.GenerateAsync("system", "user"));

        Assert.Contains("上下文窗口", error.Message);
        Assert.DoesNotContain("提高输出上限", error.Message);
        Assert.Equal("context_limit_exceeded", Assert.Single(telemetry).Outcome);
        Assert.DoesNotContain("部分输出", error.Message);
    }

    [Theory]
    [InlineData("end_turn")]
    [InlineData("stop_sequence")]
    public async Task GenerateAsync_Anthropic_ReturnsTextForCompletedStopReasons(string stopReason)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"{{\"content\":[{{\"type\":\"text\",\"text\":\"完整结果\"}}],\"stop_reason\":\"{stopReason}\"}}",
                Encoding.UTF8,
                "application/json")
        };
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Anthropic,
            Protocol = ProviderProtocol.AnthropicMessages,
            ApiBase = "https://api.anthropic.com",
            Model = "claude-sonnet-4-5"
        };
        using var service = new AIService(profile, "anthropic-key", new StaticResponseHandler(response));

        var result = await service.GenerateAsync("system", "user");

        Assert.Equal("完整结果", result);
    }

    [Fact]
    public async Task GenerateAsync_Anthropic_RejectsUnknownFutureStopReasonWithoutDeliveringText()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"content\":[{\"type\":\"text\",\"text\":\"兼容结果\"}],\"stop_reason\":\"future_reason\"}",
                Encoding.UTF8,
                "application/json")
        };
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Anthropic,
            Protocol = ProviderProtocol.AnthropicMessages,
            ApiBase = "https://api.anthropic.com",
            Model = "claude-sonnet-4-5"
        };
        using var service = new AIService(profile, "anthropic-key", new StaticResponseHandler(response));

        var error = await Assert.ThrowsAsync<GenerationFailureException>(() => service.GenerateAsync("system", "user"));

        Assert.Equal(GenerationFailureKind.InvalidResponse, error.Kind);
        Assert.DoesNotContain("兼容结果", error.Message);
    }

    [Fact]
    public async Task GenerateStructuredAsync_AnthropicSonnet55UsesSupportedEffortAndNativeSchemaWithoutSampling()
    {
        const string schema = "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}";
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"content\":[{\"type\":\"text\",\"text\":\"{\\\"answer\\\":\\\"完成\\\"}\"}],\"stop_reason\":\"end_turn\"}",
                Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Anthropic,
            Protocol = ProviderProtocol.AnthropicMessages,
            ApiBase = "https://api.anthropic.com",
            Model = "claude-sonnet-5-5",
            Temperature = 0.4,
            TopP = 1.0,
            MaxTokens = 4096,
            InferenceLevel = InferenceLevel.High
        };
        using var service = new AIService(profile, "anthropic-test-key", handler);

        var result = await service.GenerateStructuredAsync("system", "user", schema);

        Assert.Contains("完成", result);
        Assert.Equal("https://api.anthropic.com/v1/messages", handler.Request!.RequestUri!.ToString());
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("claude-sonnet-5-5", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(4096, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("temperature", out _));
        Assert.False(body.RootElement.TryGetProperty("top_p", out _));
        var outputConfig = body.RootElement.GetProperty("output_config");
        Assert.Equal("high", outputConfig.GetProperty("effort").GetString());
        var format = outputConfig.GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.Equal("answer", format.GetProperty("schema").GetProperty("properties")
            .EnumerateObject().Single().Name);
    }

    [Fact]
    public async Task GenerateStructuredAsync_Anthropic_SendsSchemaInOutputConfig()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"content\":[{\"type\":\"text\",\"text\":\"{\\\"answer\\\":\\\"Claude\\\"}\"}]}", Encoding.UTF8, "application/json")
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

        var result = await service.GenerateStructuredAsync("system", "user", "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}");

        Assert.Equal("{\"answer\":\"Claude\"}", result);
        using var body = JsonDocument.Parse(handler.Body!);
        var format = body.RootElement.GetProperty("output_config").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.Equal("object", format.GetProperty("schema").GetProperty("type").GetString());
        Assert.False(body.RootElement.TryGetProperty("output_format", out _));
    }

    [Fact]
    public async Task GenerateStructuredAsync_UnknownAnthropicModel_UsesPromptSchemaWithoutNativeFormat()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"content\":[{\"type\":\"text\",\"text\":\"{\\\"answer\\\":\\\"Claude\\\"}\"}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Anthropic,
            Protocol = ProviderProtocol.AnthropicMessages,
            ApiBase = "https://api.anthropic.com",
            Model = "claude-opus-4-1"
        };
        const string schema = "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}";
        using var service = new AIService(profile, "anthropic-key", handler);

        await service.GenerateStructuredAsync("system", "user", schema);

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.False(body.RootElement.TryGetProperty("output_config", out _));
        Assert.Contains(schema, body.RootElement.GetProperty("system").GetString());
    }

    [Fact]
    public async Task GenerateStructuredAsync_Anthropic_PreservesNullableEmotionContract()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"content\":[{\"type\":\"text\",\"text\":\"{\\\"answer\\\":\\\"你好\\\",\\\"companion_emotion\\\":\\\"Supportive\\\",\\\"companion_intensity\\\":0.7}\"}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile { Platform = ProviderPlatform.Anthropic, Protocol = ProviderProtocol.AnthropicMessages, ApiBase = "https://api.anthropic.com", Model = "claude-sonnet-4-5" };
        using var service = new AIService(profile, "test-key", handler);

        await service.GenerateStructuredAsync("system", "user", EmotionStructuredSchema);

        using var body = JsonDocument.Parse(handler.Body!);
        var outputConfig = body.RootElement.GetProperty("output_config");
        var schema = outputConfig.GetProperty("format").GetProperty("schema");
        Assert.Equal("[\"string\",\"null\"]", schema.GetProperty("properties").GetProperty("companion_emotion").GetProperty("type").GetRawText());
        Assert.Equal("[\"Supportive\",\"Cheerful\",null]", schema.GetProperty("properties").GetProperty("companion_emotion").GetProperty("enum").GetRawText());
        Assert.Equal("[\"number\",\"null\"]", schema.GetProperty("properties").GetProperty("companion_intensity").GetProperty("type").GetRawText());
        Assert.False(schema.GetProperty("properties").GetProperty("companion_intensity").TryGetProperty("minimum", out _));
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
        var profile = new ProviderProfile { Platform = ProviderPlatform.Anthropic, Protocol = ProviderProtocol.AnthropicMessages, ApiBase = "https://api.anthropic.com", Model = "claude" };
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
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.False(body.RootElement.GetProperty("generationConfig").TryGetProperty("responseFormat", out _));
    }

    [Theory]
    [InlineData("gemini-3.8-flash")]
    [InlineData("gemini-3.7-flash")]
    [InlineData("gemini-3.6-flash")]
    [InlineData("gemini-3.5-flash")]
    [InlineData("gemini-3.5-flash-lite")]
    [InlineData("gemini-3.1-flash-lite")]
    [InlineData("gemini-3.1-pro-preview")]
    [InlineData("gemini-3-flash-preview")]
    public async Task GenerateAsync_Gemini3_UsesThinkingLevelAndProviderSamplingDefaults(string model)
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Gemini 结果\"}]},\"finishReason\":\"STOP\"}]}",
                Encoding.UTF8,
                "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Gemini,
            Protocol = ProviderProtocol.GeminiGenerateContent,
            ApiBase = "https://generativelanguage.googleapis.com/v1beta",
            Model = model,
            Temperature = 0.3,
            TopP = 0.95,
            InferenceLevel = InferenceLevel.High
        };
        using var service = new AIService(profile, "gemini-key", handler);

        await service.GenerateAsync("system", "user");

        using var body = JsonDocument.Parse(handler.Body!);
        var config = body.RootElement.GetProperty("generationConfig");
        Assert.False(config.TryGetProperty("temperature", out _));
        Assert.False(config.TryGetProperty("topP", out _));
        Assert.Equal("HIGH", config.GetProperty("thinkingConfig").GetProperty("thinkingLevel").GetString());
    }

    [Fact]
    public async Task GenerateAsync_Gemini25_PreservesSamplingAndOmitsThinkingLevel()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Gemini 结果\"}]},\"finishReason\":\"STOP\"}]}",
                Encoding.UTF8,
                "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Gemini,
            Protocol = ProviderProtocol.GeminiGenerateContent,
            ApiBase = "https://generativelanguage.googleapis.com/v1beta",
            Model = "gemini-2.5-flash",
            Temperature = 0.3,
            TopP = 0.95,
            InferenceLevel = InferenceLevel.High
        };
        using var service = new AIService(profile, "gemini-key", handler);

        await service.GenerateAsync("system", "user");

        using var body = JsonDocument.Parse(handler.Body!);
        var config = body.RootElement.GetProperty("generationConfig");
        Assert.Equal(0.3, config.GetProperty("temperature").GetDouble());
        Assert.Equal(0.95, config.GetProperty("topP").GetDouble());
        Assert.False(config.TryGetProperty("thinkingConfig", out _));
    }

    [Theory]
    [InlineData("MAX_TOKENS", GenerationFailureKind.Incomplete)]
    [InlineData("SAFETY", GenerationFailureKind.Refused)]
    [InlineData("RECITATION", GenerationFailureKind.Refused)]
    [InlineData("LANGUAGE", GenerationFailureKind.Refused)]
    [InlineData("BLOCKLIST", GenerationFailureKind.Refused)]
    [InlineData("PROHIBITED_CONTENT", GenerationFailureKind.Refused)]
    [InlineData("SPII", GenerationFailureKind.Refused)]
    [InlineData("MALFORMED_FUNCTION_CALL", GenerationFailureKind.InvalidResponse)]
    [InlineData("UNEXPECTED_TOOL_CALL", GenerationFailureKind.InvalidResponse)]
    public async Task GenerateAsync_Gemini_DoesNotReturnTextForNonFinalFinishReason(
        string finishReason,
        GenerationFailureKind expectedKind)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"{{\"candidates\":[{{\"content\":{{\"parts\":[{{\"text\":\"部分输出\"}}]}},\"finishReason\":\"{finishReason}\"}}]}}",
                Encoding.UTF8,
                "application/json")
        };
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Gemini,
            Protocol = ProviderProtocol.GeminiGenerateContent,
            ApiBase = "https://generativelanguage.googleapis.com/v1beta",
            Model = "gemini-2.5-flash"
        };
        using var service = new AIService(profile, "gemini-key", new StaticResponseHandler(response));

        var error = await Assert.ThrowsAsync<GenerationFailureException>(() => service.GenerateAsync("system", "user"));

        Assert.Equal(expectedKind, error.Kind);
        Assert.DoesNotContain("部分输出", error.Message);
    }

    [Theory]
    [InlineData("SAFETY")]
    [InlineData("BLOCKLIST")]
    [InlineData("PROHIBITED_CONTENT")]
    [InlineData("OTHER")]
    public async Task GenerateAsync_Gemini_RejectsPromptBlockedWithoutCandidates(string blockReason)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"{{\"promptFeedback\":{{\"blockReason\":\"{blockReason}\"}}}}",
                Encoding.UTF8,
                "application/json")
        };
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Gemini,
            Protocol = ProviderProtocol.GeminiGenerateContent,
            ApiBase = "https://generativelanguage.googleapis.com/v1beta",
            Model = "gemini-2.5-flash"
        };
        using var service = new AIService(profile, "gemini-key", new StaticResponseHandler(response));

        var error = await Assert.ThrowsAsync<GenerationFailureException>(() => service.GenerateAsync("system", "user"));

        Assert.Equal(GenerationFailureKind.Refused, error.Kind);
    }

    [Fact]
    public async Task GenerateAsync_Gemini_ReturnsTextForStopAndUnknownFutureFinishReasons()
    {
        foreach (var finishReason in new[] { "STOP", "FUTURE_PROVIDER_REASON" })
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"candidates\":[{{\"content\":{{\"parts\":[{{\"text\":\"Gemini 结果\"}}]}},\"finishReason\":\"{finishReason}\"}}]}}",
                    Encoding.UTF8,
                    "application/json")
            };
            var profile = new ProviderProfile
            {
                Platform = ProviderPlatform.Gemini,
                Protocol = ProviderProtocol.GeminiGenerateContent,
                ApiBase = "https://generativelanguage.googleapis.com/v1beta",
                Model = "gemini-2.5-flash"
            };
            using var service = new AIService(profile, "gemini-key", new StaticResponseHandler(response));

            var result = await service.GenerateAsync("system", "user");

            Assert.Equal("Gemini 结果", result);
        }
    }

    [Fact]
    public async Task GenerateStructuredAsync_Gemini_SendsSchemaInResponseFormat()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"{\\\"answer\\\":\\\"Gemini\\\"}\"}]}}]}", Encoding.UTF8, "application/json")
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

        var result = await service.GenerateStructuredAsync("system", "user", "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}");

        Assert.Equal("{\"answer\":\"Gemini\"}", result);
        using var body = JsonDocument.Parse(handler.Body!);
        var format = body.RootElement.GetProperty("generationConfig").GetProperty("responseFormat").GetProperty("text");
        Assert.Equal("application/json", format.GetProperty("mimeType").GetString());
        Assert.Equal("object", format.GetProperty("schema").GetProperty("type").GetString());
        Assert.False(body.RootElement.GetProperty("generationConfig").TryGetProperty("responseSchema", out _));
    }

    [Fact]
    public async Task GenerateStructuredAsync_UnknownGeminiModel_UsesPromptSchemaWithoutNativeFormat()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"{\\\"answer\\\":\\\"Gemini\\\"}\"}]}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Gemini,
            Protocol = ProviderProtocol.GeminiGenerateContent,
            ApiBase = "https://generativelanguage.googleapis.com/v1beta",
            Model = "gemini-1.5-flash"
        };
        const string schema = "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}";
        using var service = new AIService(profile, "gemini-key", handler);

        await service.GenerateStructuredAsync("system", "user", schema);

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.False(body.RootElement.GetProperty("generationConfig").TryGetProperty("responseFormat", out _));
        Assert.Contains(schema, body.RootElement.GetProperty("system_instruction").GetProperty("parts")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task GenerateStructuredAsync_Gemini_PreservesNullableEmotionContract()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"{\\\"answer\\\":\\\"你好\\\",\\\"companion_emotion\\\":\\\"Supportive\\\",\\\"companion_intensity\\\":0.7}\"}]}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile { Platform = ProviderPlatform.Gemini, Protocol = ProviderProtocol.GeminiGenerateContent, ApiBase = "https://generativelanguage.googleapis.com/v1beta", Model = "gemini-2.5-flash" };
        using var service = new AIService(profile, "test-key", handler);

        await service.GenerateStructuredAsync("system", "user", EmotionStructuredSchema);

        using var body = JsonDocument.Parse(handler.Body!);
        var schema = body.RootElement.GetProperty("generationConfig").GetProperty("responseFormat").GetProperty("text").GetProperty("schema");
        Assert.Equal("[\"string\",\"null\"]", schema.GetProperty("properties").GetProperty("companion_emotion").GetProperty("type").GetRawText());
        Assert.Equal("[\"Supportive\",\"Cheerful\",null]", schema.GetProperty("properties").GetProperty("companion_emotion").GetProperty("enum").GetRawText());
        Assert.Equal("[\"number\",\"null\"]", schema.GetProperty("properties").GetProperty("companion_intensity").GetProperty("type").GetRawText());
        Assert.False(schema.GetProperty("properties").GetProperty("companion_intensity").TryGetProperty("minimum", out _));
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

    [Fact]
    public async Task GenerateAsync_OpenAiGpt6Astra_AppliesResolvedModelCapabilities()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"结果\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.OpenAI,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.openai.com/v1",
            Model = "reasoning-default",
            EnableModelMapping = true,
            ModelMapping = new Dictionary<string, string> { ["reasoning-default"] = "gpt-6-astra" },
            Temperature = 0.3,
            TopP = 0.95,
            MaxTokens = 4096,
            InferenceLevel = InferenceLevel.High
        };
        using var service = new AIService(profile, "key", handler);

        await service.GenerateAsync("system", "user");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("gpt-6-astra", body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.TryGetProperty("temperature", out _));
        Assert.False(body.RootElement.TryGetProperty("top_p", out _));
        Assert.False(body.RootElement.TryGetProperty("max_tokens", out _));
        Assert.Equal(4096, body.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal("high", body.RootElement.GetProperty("reasoning_effort").GetString());
    }

    [Theory]
    [InlineData("o3", InferenceLevel.Low, "low")]
    [InlineData("o3-2025-04-16", InferenceLevel.Medium, "medium")]
    [InlineData("o4-mini", InferenceLevel.High, "high")]
    [InlineData("o4-mini-2025-04-16", InferenceLevel.Low, "low")]
    public async Task GenerateAsync_OpenAiOSeriesChat_UsesExactReasoningContract(string model, InferenceLevel level, string effort)
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"结果\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.OpenAI,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.openai.com/v1",
            Model = model,
            Temperature = 0.3,
            TopP = 0.95,
            MaxTokens = 4096,
            InferenceLevel = level
        };
        using var service = new AIService(profile, "key", handler);

        await service.GenerateAsync("system instructions", "user input");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(model, body.RootElement.GetProperty("model").GetString());
        Assert.Equal("developer", body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal(4096, body.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("max_tokens", out _));
        Assert.False(body.RootElement.TryGetProperty("temperature", out _));
        Assert.False(body.RootElement.TryGetProperty("top_p", out _));
        Assert.Equal(effort, body.RootElement.GetProperty("reasoning_effort").GetString());
    }

    [Theory]
    [InlineData("o3", InferenceLevel.Low, "low")]
    [InlineData("o4-mini-2025-04-16", InferenceLevel.High, "high")]
    public async Task GenerateAsync_OpenAiOSeriesResponses_UsesResponsesReasoningContract(string model, InferenceLevel level, string effort)
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"id\":\"resp_o_series\",\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"结果\"}]}]}",
                Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.OpenAI,
            Protocol = ProviderProtocol.OpenAIResponses,
            ApiBase = "https://api.openai.com/v1",
            Model = model,
            Temperature = 0.3,
            TopP = 0.95,
            MaxTokens = 4096,
            InferenceLevel = level
        };
        using var service = new AIService(profile, "key", handler);

        await service.GenerateAsync("system instructions", "user input");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("/v1/responses", handler.Request!.RequestUri!.AbsolutePath);
        Assert.Equal(4096, body.RootElement.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal(effort, body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.False(body.RootElement.TryGetProperty("temperature", out _));
        Assert.False(body.RootElement.TryGetProperty("top_p", out _));
    }

    [Theory]
    [InlineData("gpt-6.1-sol", InferenceLevel.Low, "low")]
    [InlineData("gpt-6.1-sol", InferenceLevel.Medium, "medium")]
    [InlineData("gpt-6.1-sol", InferenceLevel.High, "high")]
    [InlineData("gpt-6-sol", InferenceLevel.Low, "low")]
    [InlineData("gpt-6-sol", InferenceLevel.Medium, "medium")]
    [InlineData("gpt-6-sol", InferenceLevel.High, "high")]
    [InlineData("gpt-6-luna", InferenceLevel.Low, "low")]
    [InlineData("gpt-6-luna", InferenceLevel.Medium, "medium")]
    [InlineData("gpt-6-luna", InferenceLevel.High, "high")]
    public async Task GenerateAsync_OpenAiGpt6ChatModels_UseReasoningWithoutSampling(string model, InferenceLevel level, string effort)
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"结果\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.OpenAI,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.openai.com/v1",
            Model = model,
            Temperature = 0.3,
            TopP = 0.95,
            MaxTokens = 4096,
            InferenceLevel = level
        };
        using var service = new AIService(profile, "key", handler);

        await service.GenerateAsync("system", "user");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(model, body.RootElement.GetProperty("model").GetString());
        Assert.Equal("max_completion_tokens", body.RootElement.EnumerateObject().Single(property => property.Name is "max_completion_tokens" or "max_tokens").Name);
        Assert.Equal(4096, body.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal(effort, body.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.False(body.RootElement.TryGetProperty("temperature", out _));
        Assert.False(body.RootElement.TryGetProperty("top_p", out _));
        Assert.False(body.RootElement.TryGetProperty("max_tokens", out _));
    }

    [Theory]
    [InlineData("gpt-6.1-sol", InferenceLevel.Low, "low")]
    [InlineData("gpt-6-sol", InferenceLevel.Medium, "medium")]
    [InlineData("gpt-6-luna", InferenceLevel.High, "high")]
    public async Task GenerateAsync_OpenAiGpt6ResponsesModels_UseResponsesReasoningContract(string model, InferenceLevel level, string effort)
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"id\":\"resp_gpt6\",\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"结果\"}]}]}",
                Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.OpenAI,
            Protocol = ProviderProtocol.OpenAIResponses,
            ApiBase = "https://api.openai.com/v1",
            Model = model,
            Temperature = 0.3,
            TopP = 0.95,
            MaxTokens = 4096,
            InferenceLevel = level
        };
        using var service = new AIService(profile, "key", handler);

        await service.GenerateAsync("system", "user");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("/v1/responses", handler.Request!.RequestUri!.AbsolutePath);
        Assert.Equal(4096, body.RootElement.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal(effort, body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.False(body.RootElement.TryGetProperty("temperature", out _));
        Assert.False(body.RootElement.TryGetProperty("top_p", out _));
        Assert.False(body.RootElement.TryGetProperty("max_completion_tokens", out _));
    }

    [Fact]
    public async Task GenerateAsync_OpenRouterModelWithOpenAiId_DoesNotInheritOpenAiCapabilities()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"结果\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.OpenRouter,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://openrouter.ai/api/v1",
            Model = "openai/gpt-6-astra",
            Temperature = 0.3,
            TopP = 0.95,
            MaxTokens = 4096,
            InferenceLevel = InferenceLevel.High
        };
        using var service = new AIService(profile, "key", handler);

        await service.GenerateAsync("system", "user");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("openai/gpt-6-astra", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(0.3, body.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(0.95, body.RootElement.GetProperty("top_p").GetDouble());
        Assert.Equal(4096, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("max_completion_tokens", out _));
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task GenerateStructuredAsync_OpenRouterCatalogCapabilities_RequiresMatchingEndpointParameters()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"result\\\":\\\"ok\\\"}\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.OpenRouter,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://openrouter.ai/api/v1",
            Model = "vendor/model",
            Temperature = 0.3,
            TopP = 0.95,
            MaxTokens = 4096
        };
        var modelIdProperty = typeof(ProviderProfile).GetProperty("OpenRouterCapabilitiesModelId");
        var parametersProperty = typeof(ProviderProfile).GetProperty("OpenRouterSupportedParameters");
        Assert.NotNull(modelIdProperty);
        Assert.NotNull(parametersProperty);
        modelIdProperty!.SetValue(profile, "vendor/model");
        parametersProperty!.SetValue(profile, new List<string> { "response_format", "structured_outputs", "max_tokens" });
        using var service = new AIService(profile, "key", handler);

        await service.GenerateStructuredAsync("system", "user",
            "{\"type\":\"object\",\"properties\":{\"result\":{\"type\":\"string\"}},\"required\":[\"result\"],\"additionalProperties\":false}");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("json_schema", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.True(body.RootElement.GetProperty("provider").GetProperty("require_parameters").GetBoolean());
        Assert.Equal(4096, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("temperature", out _));
        Assert.False(body.RootElement.TryGetProperty("top_p", out _));
    }

    [Fact]
    public async Task GenerateStructuredAsync_OpenRouterWithoutStructuredOutputsSignal_UsesPromptSchemaFallback()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"result\\\":\\\"ok\\\"}\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.OpenRouter,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://openrouter.ai/api/v1",
            Model = "vendor/model",
            OpenRouterCapabilitiesModelId = "vendor/model",
            OpenRouterSupportedParameters = ["response_format", "max_tokens"],
            MaxTokens = 4096
        };
        using var service = new AIService(profile, "key", handler);

        await service.GenerateStructuredAsync("system", "user",
            "{\"type\":\"object\",\"properties\":{\"result\":{\"type\":\"string\"}},\"required\":[\"result\"],\"additionalProperties\":false}");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.False(body.RootElement.TryGetProperty("response_format", out _));
        Assert.False(body.RootElement.TryGetProperty("provider", out _));
        Assert.Contains("JSON Schema", body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task GenerateStructuredAsync_OpenRouterCapabilitySnapshotForDifferentModel_IsIgnored()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"result\\\":\\\"ok\\\"}\"}}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.OpenRouter,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://openrouter.ai/api/v1",
            Model = "vendor/current-model",
            OpenRouterCapabilitiesModelId = "vendor/previous-model",
            OpenRouterSupportedParameters = ["response_format", "structured_outputs", "max_tokens"],
            MaxTokens = 4096
        };
        using var service = new AIService(profile, "key", handler);

        await service.GenerateStructuredAsync("system", "user",
            "{\"type\":\"object\",\"properties\":{\"result\":{\"type\":\"string\"}},\"required\":[\"result\"],\"additionalProperties\":false}");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.False(body.RootElement.TryGetProperty("response_format", out _));
        Assert.False(body.RootElement.TryGetProperty("provider", out _));
        Assert.Equal("vendor/current-model", body.RootElement.GetProperty("model").GetString());
        Assert.Contains("JSON Schema", body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Theory]
    [InlineData("claude-opus-4-7")]
    [InlineData("claude-opus-4-8")]
    [InlineData("claude-opus-5")]
    [InlineData("claude-opus-5-5")]
    [InlineData("claude-sonnet-4-6")]
    [InlineData("claude-sonnet-5")]
    [InlineData("claude-sonnet-5-5")]
    public async Task GenerateAsync_AnthropicPostOpus46Models_OmitUnsupportedSamplingAndApplyEffort(string model)
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"content\":[{\"type\":\"text\",\"text\":\"结果\"}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Anthropic,
            Protocol = ProviderProtocol.AnthropicMessages,
            ApiBase = "https://api.anthropic.com",
            Model = model,
            Temperature = 0.4,
            TopP = 0.95,
            InferenceLevel = InferenceLevel.High
        };
        using var service = new AIService(profile, "key", handler);

        await service.GenerateAsync("system", "user");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(model, body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.TryGetProperty("temperature", out _));
        Assert.False(body.RootElement.TryGetProperty("top_p", out _));
        Assert.Equal("high", body.RootElement.GetProperty("output_config").GetProperty("effort").GetString());
    }

    [Fact]
    public async Task GenerateAsync_AnthropicSonnet45_KeepsExistingSamplingCompatibility()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"content\":[{\"type\":\"text\",\"text\":\"结果\"}]}", Encoding.UTF8, "application/json")
        });
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Anthropic,
            Protocol = ProviderProtocol.AnthropicMessages,
            ApiBase = "https://api.anthropic.com",
            Model = "claude-sonnet-4-5",
            Temperature = 0.4,
            TopP = 0.95
        };
        using var service = new AIService(profile, "key", handler);

        await service.GenerateAsync("system", "user");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(0.4, body.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(0.95, body.RootElement.GetProperty("top_p").GetDouble());
        Assert.False(body.RootElement.TryGetProperty("output_config", out _));
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
        var profile = new ProviderProfile { Platform = ProviderPlatform.Gemini, Protocol = ProviderProtocol.GeminiGenerateContent, ApiBase = "https://generativelanguage.googleapis.com/v1beta", Model = "gemini" };
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
    public async Task TestConnectionAsync_AnthropicRefusal_IsNotReportedAsProtocolMismatch()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"content\":[{\"type\":\"text\",\"text\":\"private refusal text\"}],\"stop_reason\":\"refusal\"}",
                Encoding.UTF8,
                "application/json")
        };
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Anthropic,
            Protocol = ProviderProtocol.AnthropicMessages,
            ApiBase = "https://api.anthropic.com",
            Model = "claude-sonnet-4-5"
        };
        using var service = new AIService(profile, "anthropic-key", new StaticResponseHandler(response));

        var result = await service.TestConnectionAsync();

        Assert.Equal(ConnectionTestStatus.ProviderError, result.Status);
        Assert.False(result.CanSaveAsVerified);
        Assert.Contains("拒绝", result.UserMessage);
        Assert.DoesNotContain("private refusal text", result.UserMessage);
        Assert.DoesNotContain("private refusal text", result.DiagnosticSummary);
    }

    [Fact]
    public async Task TestConnectionAsync_GeminiTruncation_IsNotReportedAsProtocolMismatch()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"partial probe\"}]},\"finishReason\":\"MAX_TOKENS\"}]}",
                Encoding.UTF8,
                "application/json")
        };
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Gemini,
            Protocol = ProviderProtocol.GeminiGenerateContent,
            ApiBase = "https://generativelanguage.googleapis.com/v1beta",
            Model = "gemini-2.5-flash"
        };
        using var service = new AIService(profile, "gemini-key", new StaticResponseHandler(response));

        var result = await service.TestConnectionAsync();

        Assert.Equal(ConnectionTestStatus.ProviderError, result.Status);
        Assert.False(result.CanSaveAsVerified);
        Assert.Contains("未完成", result.UserMessage);
        Assert.DoesNotContain("partial probe", result.UserMessage);
        Assert.DoesNotContain("partial probe", result.DiagnosticSummary);
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
    public async Task TestConnectionAsync_TransientFailure_RetriesOnceBeforeReportingFailure()
    {
        var handler = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"OK\"}}]}", Encoding.UTF8, "application/json")
            });
        var profile = new ProviderProfile
        {
            Type = ProviderType.Local,
            Platform = ProviderPlatform.Ollama,
            ApiBase = "http://localhost:11434/v1",
            Model = "test-model"
        };
        using var service = new AIService(profile, null, handler, (_, _) => Task.CompletedTask);

        var result = await service.TestConnectionAsync();

        Assert.Equal(ConnectionTestStatus.Success, result.Status);
        Assert.Equal(2, handler.Calls);
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
    [InlineData(HttpStatusCode.Unauthorized, ConnectionTestStatus.AuthFailed)]
    [InlineData(HttpStatusCode.Forbidden, ConnectionTestStatus.PermissionDenied)]
    [InlineData(HttpStatusCode.PaymentRequired, ConnectionTestStatus.BillingIssue)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, ConnectionTestStatus.RequestTooLarge)]
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
    public async Task TestConnectionAsync_ForbiddenReportsPermissionDenied()
    {
        using var service = new AIService(new ProviderProfile(), "key",
            new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.Forbidden)));

        var result = await service.TestConnectionAsync();

        Assert.Equal(ConnectionTestStatus.PermissionDenied, result.Status);
        Assert.False(result.CanSaveAsVerified);
        Assert.Contains("权限", result.UserMessage);
    }

    [Fact]
    public async Task TestConnectionAsync_PaymentRequiredReportsBillingIssue()
    {
        using var service = new AIService(new ProviderProfile(), "key",
            new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.PaymentRequired)
            {
                Content = new StringContent("{\"error\":{\"message\":\"private billing detail\"}}", Encoding.UTF8, "application/json")
            }));

        var result = await service.TestConnectionAsync();

        Assert.Equal(ConnectionTestStatus.BillingIssue, result.Status);
        Assert.False(result.CanSaveAsVerified);
        Assert.Contains("账单", result.UserMessage);
        Assert.DoesNotContain("private billing detail", result.UserMessage);
    }

    [Fact]
    public async Task TestConnectionAsync_GeminiInvalidApiKeyErrorInfo_ReportsAuthFailure()
    {
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Gemini,
            Protocol = ProviderProtocol.GeminiGenerateContent,
            ApiBase = "https://generativelanguage.googleapis.com/v1beta",
            Model = "gemini-2.5-flash"
        };
        using var service = new AIService(profile, "key",
            new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    "{\"error\":{\"code\":400,\"message\":\"private credential detail\",\"status\":\"INVALID_ARGUMENT\",\"details\":[{\"@type\":\"type.googleapis.com/google.rpc.ErrorInfo\",\"reason\":\"API_KEY_INVALID\",\"domain\":\"googleapis.com\",\"metadata\":{\"service\":\"generativelanguage.googleapis.com\"}}]}}",
                    Encoding.UTF8, "application/json")
            }));

        var result = await service.TestConnectionAsync();

        Assert.Equal(ConnectionTestStatus.AuthFailed, result.Status);
        Assert.False(result.CanSaveAsVerified);
        Assert.Contains("API Key", result.UserMessage);
        Assert.DoesNotContain("private credential detail", result.UserMessage);
    }

    [Fact]
    public async Task TestConnectionAsync_GeminiFailedPrecondition_ReportsAccountPrerequisite()
    {
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Gemini,
            Protocol = ProviderProtocol.GeminiGenerateContent,
            ApiBase = "https://generativelanguage.googleapis.com/v1beta",
            Model = "gemini-2.5-flash"
        };
        using var service = new AIService(profile, "key",
            new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    "{\"error\":{\"code\":400,\"message\":\"private region detail\",\"status\":\"FAILED_PRECONDITION\"}}",
                    Encoding.UTF8, "application/json")
            }));

        var result = await service.TestConnectionAsync();

        Assert.Equal(ConnectionTestStatus.AccountPrerequisite, result.Status);
        Assert.False(result.CanSaveAsVerified);
        Assert.Contains("计费", result.UserMessage);
        Assert.Contains("地区", result.UserMessage);
        Assert.DoesNotContain("private region detail", result.UserMessage);
    }

    [Fact]
    public async Task TestConnectionAsync_RequestTooLarge_ReportsActionableStatus()
    {
        using var service = new AIService(new ProviderProfile(), "key",
            new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge)
            {
                Content = new StringContent("private proxy detail", Encoding.UTF8, "text/plain")
            }));

        var result = await service.TestConnectionAsync();

        Assert.Equal(ConnectionTestStatus.RequestTooLarge, result.Status);
        Assert.False(result.CanSaveAsVerified);
        Assert.Contains("请求体", result.UserMessage);
        Assert.DoesNotContain("private proxy detail", result.UserMessage);
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
