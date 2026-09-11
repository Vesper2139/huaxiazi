using System;
using System.Collections.Generic;

namespace Huaxiazi.Services;

public enum ModelTier { Fast, Balanced, Reasoning }
public sealed record ModelSelectionRequest(int InputCharacters, int ContextCharacters, bool RequiresTools, bool HighRisk, bool RequiresDeepReasoning, int LatencyBudgetMs = 8_000);
public sealed record ModelSelectionResult(ModelTier Tier, string Reason, bool RequiresFallback, double MinimumSafetyScore);

/// <summary>Task-aware model routing; safety can only escalate the tier, never downgrade it.</summary>
public static class ModelSelectionPolicy
{
    public static ModelSelectionResult Select(ModelSelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.InputCharacters < 0) throw new ArgumentOutOfRangeException(nameof(request.InputCharacters));
        if (request.ContextCharacters < 0) throw new ArgumentOutOfRangeException(nameof(request.ContextCharacters));
        if (request.LatencyBudgetMs < 0) throw new ArgumentOutOfRangeException(nameof(request.LatencyBudgetMs));
        var tier = ModelTier.Fast;
        var reasons = new List<string>();
        if (request.InputCharacters + request.ContextCharacters > 12_000) { tier = Max(tier, ModelTier.Balanced); reasons.Add("长上下文"); }
        if (request.RequiresTools) { tier = Max(tier, ModelTier.Balanced); reasons.Add("工具调用"); }
        if (request.RequiresDeepReasoning) { tier = Max(tier, ModelTier.Reasoning); reasons.Add("复杂推理"); }
        if (request.HighRisk) { tier = Max(tier, ModelTier.Reasoning); reasons.Add("高风险任务"); }
        if (request.LatencyBudgetMs < 2_000 && !request.HighRisk && !request.RequiresDeepReasoning) { tier = ModelTier.Fast; reasons.Add("低延迟预算"); }
        var safety = request.HighRisk ? .99 : tier == ModelTier.Fast ? .95 : .97;
        return new ModelSelectionResult(tier, reasons.Count == 0 ? "普通短文本任务" : string.Join("、", reasons), request.HighRisk || request.RequiresTools, safety);
    }

    private static ModelTier Max(ModelTier left, ModelTier right) => (ModelTier)Math.Max((int)left, (int)right);
}
