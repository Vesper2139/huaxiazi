using PromptFloat.Services;
using Xunit;

namespace PromptFloat.Tests;

public sealed class InputContextParserTests
{
    [Fact]
    public void Parse_ExtractsWeightedContextAndLeavesOnlyUserBody()
    {
        var input = "【上下文】\n对象：客户\n目的：说明延期\n语气：专业、克制\n优先级：高\n【/上下文】\n请帮我写一段说明。";

        var parsed = InputContextParser.Parse(input);

        Assert.Equal("请帮我写一段说明。", parsed.Body);
        Assert.Equal("客户", parsed.Recipient);
        Assert.Equal("说明延期", parsed.Purpose);
        Assert.Equal("专业、克制", parsed.Formality);
        Assert.Equal("高", parsed.Weight);
        Assert.Contains("优先级：高", parsed.Instructions);
    }

    [Fact]
    public void Parse_WithoutContext_ReturnsOriginalBodyAndNoInstructions()
    {
        var parsed = InputContextParser.Parse("直接润色这句话");

        Assert.Equal("直接润色这句话", parsed.Body);
        Assert.Equal(string.Empty, parsed.Instructions);
    }
}
