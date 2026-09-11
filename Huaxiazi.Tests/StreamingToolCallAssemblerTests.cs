using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class StreamingToolCallAssemblerTests
{
    [Fact]
    public void Assembler_BuffersArgumentsUntilToolEndThenCompletes()
    {
        var assembler = new StreamingToolCallAssembler();
        assembler.Push(new StreamEvent(StreamEventKind.TextDelta, Data: "准备调用"));
        assembler.Push(new StreamEvent(StreamEventKind.ToolStart, Name: "search"));
        assembler.Push(new StreamEvent(StreamEventKind.ToolArgumentsDelta, Data: "{\"q\":"));
        assembler.Push(new StreamEvent(StreamEventKind.ToolArgumentsDelta, Data: "\"天气\"}"));
        assembler.Push(new StreamEvent(StreamEventKind.ToolEnd));
        var call = assembler.CompleteTool();
        assembler.Push(new StreamEvent(StreamEventKind.Done));

        Assert.Equal("search", call.Name);
        Assert.Equal("{\"q\":\"天气\"}", call.ArgumentsJson);
        Assert.Equal("准备调用", assembler.Text);
        Assert.True(assembler.IsDone);
    }

    [Fact]
    public void Assembler_RejectsDoneBeforeToolArgumentsAreClosed()
    {
        var assembler = new StreamingToolCallAssembler();
        assembler.Push(new StreamEvent(StreamEventKind.ToolStart, Name: "search"));
        assembler.Push(new StreamEvent(StreamEventKind.ToolArgumentsDelta, Data: "{"));

        Assert.Throws<InvalidOperationException>(() => assembler.Push(new StreamEvent(StreamEventKind.Done)));
    }
}
