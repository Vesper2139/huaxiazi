using System;
using System.Text;

namespace Huaxiazi.Services;

/// <summary>Deterministic context budgeting for long prompt stacks.</summary>
public static class PromptContextBudget
{
    /// <summary>
    /// Conservative, tokenizer-independent estimate used for telemetry and
    /// routing only. It must never be treated as an exact provider token count.
    /// CJK characters usually consume roughly one token; contiguous ASCII text
    /// is estimated at four characters per token.
    /// </summary>
    public static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var tokens = 0;
        var asciiRun = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var c = rune.Value;
            var isAscii = c is >= 0x20 and <= 0x7E;
            if (isAscii) { asciiRun++; continue; }
            if (asciiRun > 0) { tokens += (asciiRun + 3) / 4; asciiRun = 0; }
            tokens++;
        }
        if (asciiRun > 0) tokens += (asciiRun + 3) / 4;
        return tokens;
    }

    public static string Enforce(string text, int maxCharacters = 24_000)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length <= maxCharacters) return text;
        if (maxCharacters < 512) throw new ArgumentOutOfRangeException(nameof(maxCharacters));

        // Keep the high-priority system prefix and the trust boundary suffix;
        // discard the least reliable middle context on a line boundary.
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        var head = (int)(maxCharacters * 0.68);
        var tail = maxCharacters - head - 80;
        var prefix = normalized[..head];
        var start = prefix.LastIndexOf('\n');
        if (start > head / 2) prefix = prefix[..start];
        var suffix = normalized[^tail..];
        var end = suffix.IndexOf('\n');
        if (end > 0) suffix = suffix[end..];
        return new StringBuilder(prefix.TrimEnd())
            .AppendLine()
            .AppendLine("[上下文预算：中间低优先级内容已省略]")
            .Append(suffix.TrimStart())
            .ToString();
    }
}
