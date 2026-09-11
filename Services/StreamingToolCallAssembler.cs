using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Huaxiazi.Services;

public enum StreamEventKind { TextDelta, ToolStart, ToolArgumentsDelta, ToolEnd, Done }
public sealed record StreamEvent(StreamEventKind Kind, string Name = "", string Data = "");
public sealed record AssembledToolCall(string Name, string ArgumentsJson);

/// <summary>Incremental, fail-closed assembler for streamed model/tool events.</summary>
public sealed class StreamingToolCallAssembler
{
    private readonly StringBuilder _text = new();
    private readonly StringBuilder _arguments = new();
    private string? _toolName;
    private bool _toolOpen;
    private bool _done;

    public string Text => _text.ToString();
    public bool IsDone => _done;
    public void Push(StreamEvent eventData)
    {
        if (_done) throw new InvalidOperationException("stream_already_done");
        switch (eventData.Kind)
        {
            case StreamEventKind.TextDelta when !_toolOpen: _text.Append(eventData.Data); break;
            case StreamEventKind.TextDelta: throw new InvalidOperationException("text_during_tool_call");
            case StreamEventKind.ToolStart when !_toolOpen:
                if (string.IsNullOrWhiteSpace(eventData.Name)) throw new InvalidDataException("tool_name_missing");
                _toolName = eventData.Name; _arguments.Clear(); _toolOpen = true; break;
            case StreamEventKind.ToolArgumentsDelta when _toolOpen: _arguments.Append(eventData.Data); break;
            case StreamEventKind.ToolArgumentsDelta: throw new InvalidOperationException("tool_not_started");
            case StreamEventKind.ToolEnd when _toolOpen:
                ValidateJson(_arguments.ToString()); _toolOpen = false; break;
            case StreamEventKind.ToolEnd: throw new InvalidOperationException("tool_not_started");
            case StreamEventKind.Done when !_toolOpen: _done = true; break;
            case StreamEventKind.Done: throw new InvalidOperationException("tool_call_incomplete");
            default: throw new InvalidOperationException("invalid_stream_transition");
        }
    }

    public AssembledToolCall CompleteTool()
    {
        if (_toolOpen || string.IsNullOrWhiteSpace(_toolName)) throw new InvalidOperationException("tool_call_incomplete");
        var result = new AssembledToolCall(_toolName, _arguments.ToString());
        _toolName = null;
        return result;
    }

    private static void ValidateJson(string json)
    {
        try { using var _ = JsonDocument.Parse(json); }
        catch (JsonException) { throw new InvalidDataException("tool_arguments_invalid_json"); }
    }
}
