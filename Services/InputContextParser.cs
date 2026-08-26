using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Huaxiazi.Services;

/// <summary>
/// Parses the optional inline context block users can write directly in the editor.
/// Keeping this format in the input means no extra context panel is needed.
/// </summary>
public static class InputContextParser
{
    private static readonly Regex BlockPattern = new(
        "【上下文】(?<content>.*?)【/上下文】",
        RegexOptions.Singleline | RegexOptions.Compiled);

    public static ParsedInputContext Parse(string? input)
    {
        var source = input ?? string.Empty;
        var match = BlockPattern.Match(source);
        if (!match.Success)
            return new ParsedInputContext(source.Trim(), string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var freeform = new List<string>();
        foreach (var line in match.Groups["content"].Value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var segment in line.Split(new[] { '；', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = segment.IndexOf('：');
                if (separator < 0) separator = segment.IndexOf(':');
                if (separator <= 0)
                {
                    if (!string.IsNullOrWhiteSpace(segment)) freeform.Add(segment.Trim());
                    continue;
                }

                var key = segment[..separator].Trim();
                var value = segment[(separator + 1)..].Trim();
                if (!string.IsNullOrWhiteSpace(value)) values[key] = value;
            }
        }

        var body = (source[..match.Index] + "\n" + source[(match.Index + match.Length)..]).Trim();
        var recipient = First(values, "对象", "收件人");
        var channel = First(values, "渠道", "平台");
        var purpose = First(values, "目的", "任务");
        var formality = First(values, "语气", "正式度", "风格");
        var scenario = First(values, "场景");
        var weight = First(values, "优先级", "权重");

        var instructions = new StringBuilder("用户在输入中声明了以下上下文约束，请将其作为高权重要求优先参考，不要擅自添加未声明事实：");
        foreach (var pair in values) instructions.Append('\n').Append(pair.Key).Append('：').Append(pair.Value);
        foreach (var line in freeform) instructions.Append('\n').Append(line);

        return new ParsedInputContext(body, recipient, channel, purpose, formality, scenario, weight,
            instructions.Length == 0 ? string.Empty : instructions.ToString());
    }

    private static string First(IReadOnlyDictionary<string, string> values, params string[] keys)
        => keys.Select(key => values.TryGetValue(key, out var value) ? value : string.Empty)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
}

public sealed record ParsedInputContext(
    string Body,
    string Recipient,
    string Channel,
    string Purpose,
    string Formality,
    string Scenario,
    string Weight,
    string Instructions);
