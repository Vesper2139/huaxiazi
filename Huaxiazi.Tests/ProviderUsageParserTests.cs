using Huaxiazi.Models;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class ProviderUsageParserTests
{
    [Theory]
    [InlineData("OpenAICompatible", """{"id":"chatcmpl-1","usage":{"prompt_tokens":42,"completion_tokens":7}}""", 42, 7, "chatcmpl-1")]
    [InlineData("OpenAICompatible", """{"id":"resp-1","usage":{"input_tokens":35,"output_tokens":9}}""", 35, 9, "resp-1")]
    [InlineData("AnthropicMessages", """{"id":"msg-1","usage":{"input_tokens":10,"cache_creation_input_tokens":2,"cache_read_input_tokens":3,"output_tokens":4}}""", 15, 4, "msg-1")]
    [InlineData("GeminiGenerateContent", """{"responseId":"gem-1","usageMetadata":{"promptTokenCount":22,"candidatesTokenCount":6}}""", 22, 6, "gem-1")]
    public void Parse_MapsOfficialProtocolUsageToComparableInputAndOutputCounts(
        string protocolName,
        string json,
        int expectedInput,
        int expectedOutput,
        string expectedRequestId)
    {
        var protocol = Enum.Parse<ProviderProtocol>(protocolName);

        var usage = ProviderUsageMetadataParser.Parse(protocol, json);

        Assert.Equal(expectedInput, usage.InputTokens);
        Assert.Equal(expectedOutput, usage.OutputTokens);
        Assert.Equal(expectedRequestId, usage.RequestId);
    }

    [Theory]
    [InlineData("OpenAICompatible", """{"id":"resp-cache","usage":{"input_tokens":100,"output_tokens":20,"input_tokens_details":{"cached_tokens":40,"cache_write_tokens":10}}}""", 40, 10)]
    [InlineData("OpenAICompatible", """{"id":"chat-cache","usage":{"prompt_tokens":100,"completion_tokens":20,"prompt_tokens_details":{"cached_tokens":40}}}""", 40, null)]
    [InlineData("AnthropicMessages", """{"id":"msg-cache","usage":{"input_tokens":50,"cache_creation_input_tokens":10,"cache_read_input_tokens":40,"output_tokens":20}}""", 40, 10)]
    [InlineData("GeminiGenerateContent", """{"responseId":"gem-cache","usageMetadata":{"promptTokenCount":100,"cachedContentTokenCount":40,"candidatesTokenCount":20}}""", 40, null)]
    public void Parse_PreservesCachedReadAndCacheCreationCounts(
        string protocolName,
        string json,
        int expectedCacheReadTokens,
        int? expectedCacheCreationTokens)
    {
        var usage = ProviderUsageMetadataParser.Parse(Enum.Parse<ProviderProtocol>(protocolName), json);

        Assert.Equal(expectedCacheReadTokens, usage.CacheReadInputTokens);
        Assert.Equal(expectedCacheCreationTokens, usage.CacheCreationInputTokens);
    }

    [Fact]
    public void Parse_InvalidJsonReturnsUnknownCountsWithoutThrowing()
    {
        var usage = ProviderUsageMetadataParser.Parse(ProviderProtocol.OpenAICompatible, "not json");

        Assert.Null(usage.InputTokens);
        Assert.Null(usage.OutputTokens);
    }

    [Theory]
    [InlineData("{\"usage\":{\"input_tokens\":-1,\"output_tokens\":4}}")]
    [InlineData("{\"usage\":{\"prompt_tokens\":3.5,\"completion_tokens\":4}}")]
    public void Parse_InvalidInputCountDoesNotDiscardValidOutputCount(string json)
    {
        var usage = ProviderUsageMetadataParser.Parse(ProviderProtocol.OpenAICompatible, json);

        Assert.Null(usage.InputTokens);
        Assert.Equal(4, usage.OutputTokens);
    }

    [Theory]
    [InlineData("alice@example.com")]
    [InlineData("prompt text with spaces")]
    public void Parse_RejectsUntrustedOrOverlongRequestIds(string untrustedId)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            id = untrustedId,
            usage = new { prompt_tokens = 3, completion_tokens = 2 }
        });

        var usage = ProviderUsageMetadataParser.Parse(ProviderProtocol.OpenAICompatible, json);

        Assert.Null(usage.RequestId);
        Assert.Equal(3, usage.InputTokens);
        Assert.Equal(2, usage.OutputTokens);
    }

    [Fact]
    public void Parse_RejectsOverlongRequestIds()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { id = new string('x', 257) });

        var usage = ProviderUsageMetadataParser.Parse(ProviderProtocol.OpenAICompatible, json);

        Assert.Null(usage.RequestId);
    }

    [Theory]
    [InlineData("max_messages")]
    [InlineData("steered")]
    public void Parse_RecognizesDocumentedOpenAiResponsesIncompleteReasons(string reason)
    {
        var usage = ProviderUsageMetadataParser.Parse(
            ProviderProtocol.OpenAIResponses,
            "{\"status\":\"incomplete\",\"incomplete_details\":{\"reason\":\"" + reason + "\"}}");

        Assert.Equal(reason, usage.FinishReason);
    }

    [Theory]
    [InlineData("tool_execution_timeout")]
    [InlineData("off_topic")]
    public void Parse_DropsUnrecognizedOpenAiResponsesReason(string reason)
    {
        var usage = ProviderUsageMetadataParser.Parse(
            ProviderProtocol.OpenAIResponses,
            "{\"incomplete_details\":{\"reason\":\"" + reason + "\"}}");

        Assert.Null(usage.FinishReason);
    }
}
