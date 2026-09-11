using System;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Huaxiazi.Services;

public sealed record PromptLayer(string Name, string Content, int Priority, bool Stable = false, bool Required = false, int Order = int.MaxValue);
public sealed record ComposedPrompt(
    string Text,
    string StablePrefixHash,
    IReadOnlyList<string> IncludedLayers,
    IReadOnlyList<string> OmittedLayers,
    int UsedCharacters = 0,
    int BudgetCharacters = 0,
    bool RequiredOverflow = false,
    IReadOnlyList<string>? CompressedLayers = null,
    int EstimatedTokens = 0);

/// <summary>Orders prompt layers for cache reuse and applies explicit priority-based omission.</summary>
public static class PromptLayerComposer
{
    public static ComposedPrompt Compose(IEnumerable<PromptLayer> layers, int maxCharacters = 24_000)
    {
        if (layers is null) throw new ArgumentNullException(nameof(layers));
        if (maxCharacters < 512) throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        var all = layers.Where(layer => !string.IsNullOrWhiteSpace(layer.Content)).ToArray();
        // Stability controls cache-friendly prefix placement.  Within that group, an
        // explicit order models the protocol (system -> developer -> skills -> ... -> task).
        // Legacy callers that omit Order retain deterministic priority ordering.
        var ordered = all.OrderByDescending(layer => layer.Stable)
            .ThenBy(layer => layer.Order)
            .ThenByDescending(layer => layer.Priority)
            .ThenByDescending(layer => layer.Required)
            .ThenBy(layer => layer.Name, StringComparer.Ordinal).ToArray();
        // Selection and rendering are deliberately separate. Selection uses
        // priority, while rendering uses protocol order; a large low-priority
        // Skill cannot consume the budget merely because it appears early.
        var included = new List<PromptLayer>();
        var omitted = new List<string>();
        var compressed = new List<string>();
        var used = 0;
        var requiredOverflow = false;
        foreach (var layer in ordered.Where(layer => layer.Required))
        {
            included.Add(layer);
            used += Cost(layer);
            if (used > maxCharacters) requiredOverflow = true;
        }
        var optional = ordered.Where(layer => !layer.Required)
            .OrderByDescending(layer => layer.Priority)
            .ThenBy(layer => layer.Order)
            .ThenBy(layer => layer.Name, StringComparer.Ordinal);
        foreach (var layer in optional)
        {
            var cost = Cost(layer);
            if (used + cost <= maxCharacters) { included.Add(layer); used += cost; }
            else
            {
                var remaining = maxCharacters - used;
                var compact = Compress(layer, remaining);
                if (compact is not null)
                {
                    included.Add(compact);
                    used += Cost(compact);
                    compressed.Add(layer.Name);
                }
                else omitted.Add(layer.Name);
            }
        }
        included = included.OrderByDescending(layer => layer.Stable)
            .ThenBy(layer => layer.Order)
            .ThenByDescending(layer => layer.Priority)
            .ThenByDescending(layer => layer.Required)
            .ThenBy(layer => layer.Name, StringComparer.Ordinal).ToList();
        var text = string.Join("\n\n", included.Select(layer => $"<{layer.Name}>\n{layer.Content.Trim()}\n</{layer.Name}>"));
        var stable = string.Join("\n", included.Where(layer => layer.Stable).Select(layer => layer.Name + ":" + layer.Content.Trim()));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stable))).ToLowerInvariant();
        return new ComposedPrompt(text, hash, included.Select(layer => layer.Name).ToArray(), omitted, used, maxCharacters, requiredOverflow, compressed,
            PromptContextBudget.EstimateTokens(text));
    }

    private static int Cost(PromptLayer layer) => layer.Content.Length + layer.Name.Length + 8;

    private static PromptLayer? Compress(PromptLayer layer, int remaining)
    {
        // Keep enough room for an explicit marker and both ends of the layer;
        // never compress a layer into an empty or ambiguous fragment.
        const int markerLength = 30;
        if (layer.Priority < 50 || remaining <= markerLength + layer.Name.Length + 32 || layer.Content.Length < 128) return null;
        var contentBudget = remaining - layer.Name.Length - 8;
        var marker = "\n[该层上下文已按预算压缩，中间内容省略]\n";
        contentBudget -= marker.Length;
        if (contentBudget < 64) return null;
        var head = Math.Max(32, (int)(contentBudget * .7));
        var tail = Math.Max(16, contentBudget - head);
        if (head + tail > layer.Content.Length) return null;
        var content = layer.Content.Trim();
        var compact = SafePrefix(content, head) + marker + SafeSuffix(content, tail);
        return layer with { Content = compact };
    }

    private static string SafePrefix(string value, int length)
    {
        var end = Math.Min(length, value.Length);
        if (end > 0 && end < value.Length && char.IsHighSurrogate(value[end - 1]) && char.IsLowSurrogate(value[end])) end--;
        return value[..Math.Max(0, end)];
    }

    private static string SafeSuffix(string value, int length)
    {
        var start = Math.Max(0, value.Length - length);
        if (start > 0 && start < value.Length && char.IsLowSurrogate(value[start]) && char.IsHighSurrogate(value[start - 1])) start++;
        return value[start..];
    }
}
