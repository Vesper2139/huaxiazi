using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class OpenRouterModelCatalogServiceTests
{
    [Fact]
    public async Task FetchAsync_ReturnsTextCapableModelsAndUsesOfficialCatalogEndpoint()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHandler(request =>
        {
            capturedRequest = request;
            return JsonResponse("""
                {"data":[
                  {"id":"zeta/chat","name":"Zeta Chat","architecture":{"modality":"text->text","input_modalities":["text"],"output_modalities":["text"]},"supported_parameters":["response_format","structured_outputs","max_tokens","temperature","provider_secret"]},
                  {"id":"vision/model","name":"Vision","architecture":{"modality":"text+image->text","input_modalities":["text","image"],"output_modalities":["text"]}},
                  {"id":"audio/model","name":"Audio","architecture":{"modality":"audio->audio","input_modalities":["audio"],"output_modalities":["audio"]}},
                  {"id":"text/chat","name":"Text Chat","architecture":{"input_modalities":["text"],"output_modalities":["text"]}},
                  {"id":"zeta/chat","name":"Duplicate","architecture":{"modality":"text->text","input_modalities":["text"],"output_modalities":["text"]}},
                  {"id":"unknown/model","name":"Unknown","architecture":{"modality":"text->text"}},
                  {"id":"broken"}
                ]}
                """);
        });
        using var client = new HttpClient(handler);
        var service = new OpenRouterModelCatalogService(client);

        var models = await service.FetchAsync("sk-or-v1-test-key");

        Assert.Equal(new[] { "Text Chat", "Vision", "Zeta Chat" }, models.Select(model => model.DisplayName));
        Assert.Equal(new[] { "text/chat", "vision/model", "zeta/chat" }, models.Select(model => model.ModelId));
        Assert.Null(models[0].SupportedParameters);
        var modelCapabilityProperty = models[2].GetType().GetProperty("SupportedParameters");
        Assert.NotNull(modelCapabilityProperty);
        var modelParameters = Assert.IsAssignableFrom<IEnumerable<string>>(modelCapabilityProperty!.GetValue(models[2]));
        Assert.Equal(new[] { "max_tokens", "response_format", "structured_outputs", "temperature" }, modelParameters.OrderBy(value => value, StringComparer.Ordinal));
        Assert.Equal("https://openrouter.ai/api/v1/models", capturedRequest!.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization!.Scheme);
        Assert.Equal("sk-or-v1-test-key", capturedRequest.Headers.Authorization.Parameter);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task FetchAsync_RejectsMissingKeyWithoutSendingRequest(string? apiKey)
    {
        var calls = 0;
        using var client = new HttpClient(new StubHandler(_ =>
        {
            calls++;
            return JsonResponse("{\"data\":[]}");
        }));
        var service = new OpenRouterModelCatalogService(client);

        await Assert.ThrowsAsync<ArgumentException>(() => service.FetchAsync(apiKey!));

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task FetchAsync_RejectsMalformedCatalogInsteadOfReplacingModelsWithEmptyList()
    {
        using var client = new HttpClient(new StubHandler(_ => JsonResponse("{\"data\":{}}")));
        var service = new OpenRouterModelCatalogService(client);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.FetchAsync("sk-or-v1-test-key"));
    }

    [Fact]
    public async Task FetchAsync_RejectsFailedHttpResponseWithoutSurfacingResponseBody()
    {
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("secret diagnostic must not be shown")
        }));
        var service = new OpenRouterModelCatalogService(client);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => service.FetchAsync("sk-or-v1-test-key"));

        Assert.DoesNotContain("secret diagnostic", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk-or-v1-test-key", exception.Message, StringComparison.Ordinal);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
