using PromptFloat.Models;
using PromptFloat.Services;
using Xunit;

namespace PromptFloat.Tests;

public sealed class PolishResponseParserTests
{
    [Fact]
    public void Parse_JsonFence_ReturnsFinalEnvelope()
    {
        var response = PolishResponseParser.Parse("```json\n{\"kind\":\"final\",\"content\":\"清晰文本\"}\n```");

        Assert.Equal(PolishResponseKind.Final, response.Kind);
        Assert.Equal("清晰文本", response.Content);
    }
    [Fact]
    public void Parse_FinalEnvelope_ReturnsValidatedFinalResult()
    {
        const string json = """
            {
              "kind": "final",
              "scenario": "职场沟通",
              "topic": "项目延期说明",
              "content": "项目需要晚两天完成，我会及时同步最新进展。"
            }
            """;

        var result = PolishResponseParser.Parse(json);

        Assert.Equal(PolishResponseKind.Final, result.Kind);
        Assert.Equal("职场沟通", result.Scenario);
        Assert.Equal("项目延期说明", result.Topic);
        Assert.Equal("项目需要晚两天完成，我会及时同步最新进展。", result.Content);
        Assert.Empty(result.Questions);
    }

    [Fact]
    public void Parse_ClarificationEnvelope_LimitsQuestionsToThreeAndDropsBlankOnes()
    {
        const string json = """
            {
              "kind": "needs_clarification",
              "questions": ["发给谁？", "", "希望多正式？", "是否需要说明原因？", "多余问题"]
            }
            """;

        var result = PolishResponseParser.Parse(json);

        Assert.Equal(PolishResponseKind.NeedsClarification, result.Kind);
        Assert.Equal(["发给谁？", "希望多正式？", "是否需要说明原因？"], result.Questions);
        Assert.Empty(result.Content);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"kind\":\"final\",\"content\":\"\"}")]
    [InlineData("{\"kind\":\"needs_clarification\",\"questions\":[]}")]
    public void Parse_InvalidEnvelope_ReturnsInvalidWithoutTreatingRawTextAsFinal(string input)
    {
        var result = PolishResponseParser.Parse(input);

        Assert.Equal(PolishResponseKind.Invalid, result.Kind);
        Assert.Empty(result.Content);
        Assert.Empty(result.Questions);
    }
}
