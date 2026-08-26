using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

public sealed class ExpressionSkillRouter(string installRoot) : IExpressionSkillRouter
{
    private readonly AgentSkillPackageService _packages = new(installRoot);

    public ExpressionSkillRouteResult Route(ExpressionSkillRoutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var candidates = _packages.ListInstalled()
            .Where(item => item.IsEnabled && item.Status is SkillCompatibilityStatus.Ready or SkillCompatibilityStatus.NeedsMapping && item.Mode == context.Mode)
            .Select(item => new { Record = item, Score = Score(item, context) })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Record.Id, StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 0) return Fallback();
        var selected = candidates[0].Record;
        var instructions = ProjectInstructions(selected.Instructions, selected.ExcludedSections);
        if (string.IsNullOrWhiteSpace(instructions)) return Fallback();
        return new ExpressionSkillRouteResult
        {
            SkillId = selected.Id,
            DisplayName = selected.EffectiveDisplayName,
            Instructions = instructions,
            Reason = "根据当前任务与场景自动匹配",
            UsedFallback = false
        };
    }

    private static int Score(AgentSkillRecord skill, ExpressionSkillRoutingContext context)
    {
        if (skill.Source == AgentSkillSource.User && skill.RoutingTags.Count == 0) return 20;
        if (context.Mode == ApplicationMode.PromptOptimize)
            return skill.RoutingTags.Any(tag => tag.Equals("prompt", StringComparison.OrdinalIgnoreCase)) ? 100 : 10;
        var input = context.Input ?? string.Empty;
        var scenario = context.Scenario ?? string.Empty;
        var score = 0;
        foreach (var tag in skill.RoutingTags.Where(tag => !string.IsNullOrWhiteSpace(tag)))
        {
            if (scenario.Contains(tag, StringComparison.OrdinalIgnoreCase)) score = Math.Max(score, 90);
            if (input.Contains(tag, StringComparison.OrdinalIgnoreCase)) score = Math.Max(score, 80);
        }
        return score;
    }

    private static string ProjectInstructions(string source, IReadOnlyList<string> excludedSections)
    {
        var excluded = new HashSet<string>(excludedSections, StringComparer.OrdinalIgnoreCase);
        var output = new StringBuilder();
        var skipLevel = 0;
        foreach (var line in source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var heading = Regex.Match(line, @"^(?<marks>#{1,6})\s+(?<title>.+?)\s*$");
            if (heading.Success)
            {
                var level = heading.Groups["marks"].Value.Length;
                if (skipLevel > 0 && level <= skipLevel) skipLevel = 0;
                if (excluded.Contains(heading.Groups["title"].Value.Trim())) { skipLevel = level; continue; }
            }
            if (skipLevel == 0 && !RequestsExternalCapability(line)) output.AppendLine(line);
        }
        var projected = output.ToString().Trim();
        if (projected.Length <= 12_000) return projected;
        var boundary = projected.LastIndexOf('\n', 12_000);
        return projected[..(boundary > 0 ? boundary : 12_000)].TrimEnd();
    }

    private static bool RequestsExternalCapability(string line)
    {
        var text = line.Trim();
        if (text.Length == 0) return false;
        return Regex.IsMatch(text, @"\b(?:Google Drive|Slack|MCP|Shell|PowerShell|browser)\b", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(text, @"\b(?:pull|read|scan|search|gather)\b.*\b(?:files?|documents?|email|calendar|sources?|context)\b", RegexOptions.IgnoreCase);
    }

    private static ExpressionSkillRouteResult Fallback() => new()
    {
        DisplayName = "话匣子默认表达",
        Reason = "没有已启用且适配当前场景的 Skill",
        UsedFallback = true
    };
}
