using System;
using System.Collections.Generic;
using System.Text.Json;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

public static class PolishResponseParser
{
    public static PolishResponse Parse(string? responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return Invalid(responseText);
        }

        try
        {
            responseText = StripJsonFence(responseText);
            using var document = JsonDocument.Parse(responseText);
            var root = document.RootElement;
            if (!root.TryGetProperty("kind", out var kindElement))
            {
                return Invalid(responseText);
            }

            var kind = kindElement.GetString();
            if (string.Equals(kind, "final", StringComparison.OrdinalIgnoreCase))
            {
                var content = ReadString(root, "content").Trim();
                if (content.Length == 0)
                {
                    return Invalid(responseText);
                }

                return new PolishResponse
                {
                    Kind = PolishResponseKind.Final,
                    Scenario = ReadString(root, "scenario").Trim(),
                    Topic = ReadString(root, "topic").Trim(),
                    Content = content,
                    RawText = responseText
                };
            }

            if (string.Equals(kind, "needs_clarification", StringComparison.OrdinalIgnoreCase) &&
                root.TryGetProperty("questions", out var questionsElement) &&
                questionsElement.ValueKind == JsonValueKind.Array)
            {
                var questions = new List<string>();
                foreach (var item in questionsElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var question = item.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(question))
                    {
                        questions.Add(question);
                    }
                    if (questions.Count == 3)
                    {
                        break;
                    }
                }

                if (questions.Count > 0)
                {
                    return new PolishResponse
                    {
                        Kind = PolishResponseKind.NeedsClarification,
                        Questions = questions,
                        RawText = responseText
                    };
                }
            }
        }
        catch (JsonException)
        {
            // Invalid model output is deliberately not treated as a final draft.
        }

        return Invalid(responseText);
    }

    private static string StripJsonFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;
        var firstLineEnd = trimmed.IndexOf('\n');
        if (firstLineEnd < 0) return trimmed;
        var closing = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (closing <= firstLineEnd) return trimmed;
        return trimmed[(firstLineEnd + 1)..closing].Trim();
    }

    private static string ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : string.Empty;

    private static PolishResponse Invalid(string? rawText) => new()
    {
        Kind = PolishResponseKind.Invalid,
        RawText = rawText ?? string.Empty
    };
}
