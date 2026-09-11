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
            .Where(item => item.IsEnabled && item.Status is (SkillCompatibilityStatus.Ready or SkillCompatibilityStatus.NeedsMapping) && item.Mode == context.Mode)
            .Where(item => !context.AvoidSkillIds.Contains(item.Id, StringComparer.OrdinalIgnoreCase))
            .Select(item => new { Record = item, Score = Score(item, context) })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Record.Id, StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 0) return Fallback();
        var selected = candidates[0].Record;
        // Compose at most three high-confidence, non-duplicate skills. The
        // highest score wins ties; user skills receive a deterministic bonus.
        var selectedCandidates = candidates.Where(item => item.Score >= Math.Max(20, candidates[0].Score - 25))
            .Take(3).ToArray();
        var projected = selectedCandidates
            .Select(item => (item.Record, Instructions: ProjectInstructions(item.Record.Instructions, item.Record.ExcludedSections)))
            .Where(item => !string.IsNullOrWhiteSpace(item.Instructions)).ToArray();
        // Allocate weights only after projection/sanitization. A candidate whose
        // instructions were entirely removed must not retain a phantom weight in
        // the composed prompt or downstream telemetry.
        var projectedIds = projected.Select(item => item.Record.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var weights = SkillWeightAllocator.Allocate(selectedCandidates
            .Where(item => projectedIds.Contains(item.Record.Id))
            .Select(item => (item.Record.Id, (double)item.Score)));
        var instructions = string.Join("\n\n--- Skill 组合边界 ---\n", projected.Select(item => item.Instructions));
        var conflict = projected.Select(item => item.Instructions).SelectMany(text => text.Split('\n')).GroupBy(line => line.Trim(), StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1 && group.Key.StartsWith("不得", StringComparison.Ordinal))
            || HasSemanticConflict(projected.Select(item => item.Instructions).ToArray());
        if (conflict)
        {
            instructions = ProjectInstructions(selected.Instructions, selected.ExcludedSections);
            weights = string.IsNullOrWhiteSpace(instructions)
                ? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { [selected.Id] = 1d };
        }
        if (string.IsNullOrWhiteSpace(instructions)) return Fallback();
        return new ExpressionSkillRouteResult
        {
            SkillId = selected.Id,
            DisplayName = selected.EffectiveDisplayName,
            Instructions = instructions,
            Reason = selectedCandidates.Length == 1 ? "根据当前任务与场景自动匹配" : "根据任务相关性组合多个 Skill，并在冲突时回退主 Skill",
            UsedFallback = false,
            SelectedSkills = selectedCandidates.Select(item => item.Record.Id).ToArray(),
            SkillWeights = weights,
            ConflictDetected = conflict
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
        if (skill.Source == AgentSkillSource.User && score > 0) score += 10;
        if (context.PreferredSkillIds.Contains(skill.Id, StringComparer.OrdinalIgnoreCase)) score += 100;
        return score;
    }

    internal static string ProjectInstructions(string source, IReadOnlyList<string> excludedSections)
    {
        var normalizedSource = NormalizeUntrustedText(source);
        // Treat a package as a single untrusted document for injection checks;
        // otherwise an attacker can split a forbidden instruction across lines.
        var detectionText = normalizedSource.Replace('\n', ' ');
        var documentContainsUnsafeInstruction = PromptInjectionSanitizer.LooksLikePromptInjection(detectionText) || RequestsExternalCapability(detectionText);
        var excluded = new HashSet<string>(excludedSections, StringComparer.OrdinalIgnoreCase);
        var output = new StringBuilder();
        var skipLevel = 0;
        foreach (var line in normalizedSource.Split('\n'))
        {
            var heading = Regex.Match(line, @"^(?<marks>#{1,6})\s+(?<title>.+?)\s*$");
            if (heading.Success)
            {
                var level = heading.Groups["marks"].Value.Length;
                if (skipLevel > 0 && level <= skipLevel) skipLevel = 0;
                if (excluded.Contains(heading.Groups["title"].Value.Trim())) { skipLevel = level; continue; }
            }
            var lineContainsUnsafeFragment = documentContainsUnsafeInstruction &&
                Regex.IsMatch(line, @"(?i)\b(?:ignore|disregard|forget|override|reveal|leak|previous|system|prompt|instructions?|rules?|secret|token)\b|(?:忽略|无视|覆盖|忘记|之前|系统提示|系统规则|密钥|令牌)", RegexOptions.CultureInvariant);
            if (skipLevel == 0 && !RequestsExternalCapability(line) && !PromptInjectionSanitizer.LooksLikePromptInjection(line) && !lineContainsUnsafeFragment)
                output.AppendLine(line);
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

    private static bool HasSemanticConflict(IReadOnlyList<string> instructions)
    {
        var opposingPairs = new[]
        {
            (new[] { "简洁", "精简", "短句" }, new[] { "详细", "完整展开", "长篇" }),
            (new[] { "正式", "公文" }, new[] { "口语", "随意", "聊天" }),
            (new[] { "保留原文", "不改写" }, new[] { "全面改写", "重写全部" })
        };
        for (var left = 0; left < instructions.Count; left++)
        for (var right = left + 1; right < instructions.Count; right++)
            foreach (var (first, second) in opposingPairs)
                if (ContainsAny(instructions[left], first) && ContainsAny(instructions[right], second) ||
                    ContainsAny(instructions[left], second) && ContainsAny(instructions[right], first))
                    return true;
        return false;
    }

    private static bool ContainsAny(string value, IEnumerable<string> terms) => terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeUntrustedText(string source) =>
        source.Normalize(NormalizationForm.FormKC)
            .Replace("\u200B", string.Empty, StringComparison.Ordinal)
            .Replace("\u200C", string.Empty, StringComparison.Ordinal)
            .Replace("\u200D", string.Empty, StringComparison.Ordinal)
            .Replace("\uFEFF", string.Empty, StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

    private static ExpressionSkillRouteResult Fallback() => new()
    {
        DisplayName = "话匣子默认表达",
        Reason = "没有已启用且适配当前场景的 Skill",
        UsedFallback = true
    };
}
