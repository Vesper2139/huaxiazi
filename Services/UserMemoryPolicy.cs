using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Huaxiazi.Services;

public enum UserMemoryTier { Session, Preference, Profile, Sensitive }

public sealed record UserMemoryItem(string Key, string Value, UserMemoryTier Tier, bool UserApproved = false, DateTimeOffset? ExpiresAt = null, DateTimeOffset? UpdatedAt = null);

public static class UserMemoryPolicy
{
    public static IReadOnlyList<UserMemoryItem> SelectForPrompt(IEnumerable<UserMemoryItem> memories, DateTimeOffset now, int maxCharacters = 4_000)
    {
        if (maxCharacters < 0) throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        var selected = new List<UserMemoryItem>();
        var used = "<user_memory>\n".Length + "</user_memory>".Length;
        var eligible = memories.Where(item => IsEligible(item, now))
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(item => Priority(item.Tier))
                .ThenByDescending(item => item.UpdatedAt ?? DateTimeOffset.MinValue)
                .ThenBy(item => item.Key, StringComparer.Ordinal).First());
        foreach (var item in eligible.OrderBy(item => Priority(item.Tier)).ThenBy(item => item.Key, StringComparer.Ordinal))
        {
            // Account for the actual escaped XML representation, including tags.
            var cost = RenderedItem(item).Length;
            if (used + cost > maxCharacters) continue;
            selected.Add(item);
            used += cost;
        }
        return selected;
    }

    public static string RenderPromptContext(IEnumerable<UserMemoryItem> memories, DateTimeOffset now, int maxCharacters = 4_000)
    {
        var selected = SelectForPrompt(memories, now, maxCharacters);
        if (selected.Count == 0) return string.Empty;
        var builder = new System.Text.StringBuilder("<user_memory>\n");
        foreach (var item in selected)
            builder.Append("<item tier=\"").Append(item.Tier.ToString().ToLowerInvariant()).Append("\" key=\"")
                .Append(Escape(item.Key)).Append("\">").Append(Escape(Redact(item.Value))).AppendLine("</item>");
        builder.Append("</user_memory>");
        return builder.ToString();
    }

    private static string RenderedItem(UserMemoryItem item) =>
        "<item tier=\"" + item.Tier.ToString().ToLowerInvariant() + "\" key=\"" + Escape(item.Key) + "\">" + Escape(Redact(item.Value)) + "</item>\n";

    public static string Redact(string value)
    {
        var text = value ?? string.Empty;
        text = Regex.Replace(text, @"(?<!\d)(?:\+?\d[\d\s-]{7,}\d)(?!\d)", "[电话已脱敏]");
        text = Regex.Replace(text, @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", "[邮箱已脱敏]", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\b(?:sk|rk|api|token)[-_][A-Za-z0-9_-]{12,}\b", "[密钥已脱敏]", RegexOptions.IgnoreCase);
        return text;
    }

    private static string Escape(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal);

    private static bool IsEligible(UserMemoryItem item, DateTimeOffset now) =>
        !string.IsNullOrWhiteSpace(item.Key) && !string.IsNullOrWhiteSpace(item.Value) &&
        item.Tier != UserMemoryTier.Sensitive && item.UserApproved &&
        (!item.ExpiresAt.HasValue || item.ExpiresAt.Value > now) &&
        !PromptInjectionSanitizer.LooksLikePromptInjection(item.Value) &&
        !Regex.IsMatch(item.Value, @"(?:系统提示|system prompt|忽略之前|reveal.*instruction)", RegexOptions.IgnoreCase);

    private static int Priority(UserMemoryTier tier) => tier switch { UserMemoryTier.Session => 0, UserMemoryTier.Preference => 1, UserMemoryTier.Profile => 2, _ => 9 };
}
