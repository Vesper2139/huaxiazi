using System;
using System.Collections.Generic;
using System.Linq;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

public sealed class AgentContextInput
{
    public string SystemPrompt { get; init; } = string.Empty;
    public string DeveloperPrompt { get; init; } = string.Empty;
    public bool DeveloperStable { get; init; } = true;
    public IReadOnlyList<string> FidelityAnchors { get; init; } = [];
    public string Task { get; init; } = string.Empty;
    public string SkillInstructions { get; init; } = string.Empty;
    public string Personalization { get; init; } = string.Empty;
    public ExpressionSkillRouteResult? SkillRoute { get; init; }
    public IReadOnlyList<UserMemoryItem> Memories { get; init; } = [];
    public DateTimeOffset MemoryNow { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<RetrievedContext> Knowledge { get; init; } = [];
    public IReadOnlyList<ToolDescription> Tools { get; init; } = [];
}

public static class AgentContextPipeline
{
    public static ComposedPrompt Compose(AgentContextInput input, int maxCharacters = 24_000)
    {
        ArgumentNullException.ThrowIfNull(input);
        var layers = new List<PromptLayer>();
        var systemPrompt = input.SystemPrompt ?? string.Empty;
        if (!systemPrompt.Contains("不可覆盖的安全边界", StringComparison.Ordinal))
        {
            var secured = new System.Text.StringBuilder(systemPrompt);
            PromptSecurityPolicy.AppendTrustBoundary(secured);
            systemPrompt = secured.ToString();
        }
        Add(layers, "system", systemPrompt, 1000, order: 10, stable: true, required: true);
        Add(layers, "developer", input.DeveloperPrompt, 900, order: 20, stable: input.DeveloperStable, required: true);
        if (input.FidelityAnchors.Count > 0)
            Add(layers, "facts", "必须保留以下事实锚点（不得改写、删除或强化）：\n" + string.Join("\n", input.FidelityAnchors.Select(EscapeText)), 850, order: 25, required: true);
        if (input.SkillRoute is { UsedFallback: false } route)
        {
            var skillText = route.Instructions;
            if (!string.IsNullOrWhiteSpace(input.SkillInstructions))
                skillText += "\n\n<execution_strategy>\n" + input.SkillInstructions.Trim() + "\n</execution_strategy>";
            var weights = NormalizeWeights(route.SkillWeights);
            if (weights.Count > 0)
                skillText += "\n<skill_weights>" + string.Join(",", weights.Select(pair => pair.Key + "=" + pair.Value.ToString("0.000"))) + "</skill_weights>";
            Add(layers, "skills", skillText, 700, order: 30);
        }
        else Add(layers, "skills", input.SkillInstructions, 700, order: 30);
        var memory = UserMemoryPolicy.RenderPromptContext(input.Memories, input.MemoryNow);
        Add(layers, "user_memory", memory, 500, order: 40);
        if (!string.IsNullOrWhiteSpace(input.Personalization))
            Add(layers, "personalization", $"<personalization>\n{EscapeText(input.Personalization.Trim())}\n</personalization>", 480, order: 45);
        if (input.Knowledge.Count > 0) Add(layers, "knowledge", KnowledgeContextRenderer.Render(input.Knowledge), 450, order: 50);
        if (input.Tools.Count > 0)
        {
            var tools = input.Tools.Select(tool => ToolDescriptionPolicy.Project(tool)).Select(tool => $"{tool.Name}: {tool.Description} ({tool.Safety})");
            Add(layers, "tools", string.Join("\n", tools), 400, order: 60);
        }
        Add(layers, "task", input.Task, 800, order: 70, required: true);
        return PromptLayerComposer.Compose(layers, maxCharacters);
    }

    private static void Add(ICollection<PromptLayer> layers, string name, string content, int priority, int order, bool stable = false, bool required = false)
    {
        if (!string.IsNullOrWhiteSpace(content)) layers.Add(new PromptLayer(name, content, priority, stable, required, order));
    }

    private static string EscapeText(string value) => (value ?? string.Empty)
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);

    private static IReadOnlyDictionary<string, double> NormalizeWeights(IReadOnlyDictionary<string, double> source)
    {
        var valid = source.Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && double.IsFinite(pair.Value) && pair.Value >= 0).ToArray();
        var total = valid.Sum(pair => pair.Value);
        if (valid.Length == 0 || total <= 0) return new Dictionary<string, double>(StringComparer.Ordinal);
        var values = valid.ToDictionary(pair => pair.Key, pair => Math.Round(pair.Value / total, 4), StringComparer.Ordinal);
        var residual = Math.Round(1d - values.Values.Sum(), 4);
        var key = values.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).First().Key;
        values[key] = Math.Round(values[key] + residual, 4);
        return values;
    }
}
