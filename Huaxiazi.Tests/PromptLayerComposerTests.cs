using System.Linq;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class PromptLayerComposerTests
{
    [Fact]
    public void Compose_PutsStableLayersFirstAndProducesStableHash()
    {
        var layers = new[]
        {
            new PromptLayer("task", "当前任务", 100),
            new PromptLayer("system", "固定安全规则", 100, Stable: true, Required: true),
            new PromptLayer("skill", "表达技能", 50, Stable: true)
        };

        var first = PromptLayerComposer.Compose(layers);
        var second = PromptLayerComposer.Compose(layers);

        Assert.StartsWith("<system>", first.Text);
        Assert.Equal(first.StablePrefixHash, second.StablePrefixHash);
        Assert.Equal(new[] { "system", "skill", "task" }, first.IncludedLayers);
    }

    [Fact]
    public void Compose_OmitsLowPriorityLayersWhenBudgetIsTight()
    {
        var result = PromptLayerComposer.Compose(new[]
        {
            new PromptLayer("system", "安全规则", 100, Stable: true, Required: true),
            new PromptLayer("memory", new string('m', 2000), 1),
            new PromptLayer("task", "任务", 90)
        }, 600);

        Assert.Contains("system", result.IncludedLayers);
        Assert.Contains("task", result.IncludedLayers);
        Assert.Contains("memory", result.OmittedLayers);
        Assert.Equal(600, result.BudgetCharacters);
        Assert.False(result.RequiredOverflow);
    }

    [Fact]
    public void Compose_ReportsWhenRequiredProtocolExceedsBudget()
    {
        var result = PromptLayerComposer.Compose(new[]
        {
            new PromptLayer("system", new string('s', 700), 100, Required: true)
        }, 600);

        Assert.True(result.RequiredOverflow);
        Assert.True(result.UsedCharacters > result.BudgetCharacters);
        Assert.Contains("system", result.IncludedLayers);
    }

    [Fact]
    public void Compose_SelectsOptionalLayersByPriorityThenRendersProtocolOrder()
    {
        var result = PromptLayerComposer.Compose(new[]
        {
            new PromptLayer("system", "安全", 100, Required: true, Order: 10),
            new PromptLayer("task", "任务", 90, Required: true, Order: 70),
            new PromptLayer("knowledge", new string('k', 300), 80, Order: 50),
            new PromptLayer("tools", new string('t', 300), 10, Order: 60)
        }, 512);

        Assert.Contains("knowledge", result.IncludedLayers);
        Assert.Contains("tools", result.OmittedLayers);
        Assert.True(Array.IndexOf(result.IncludedLayers.ToArray(), "knowledge") < Array.IndexOf(result.IncludedLayers.ToArray(), "task"));
    }

    [Fact]
    public void Compose_CompressesHighPriorityContextBeforeDroppingIt()
    {
        var result = PromptLayerComposer.Compose(new[]
        {
            new PromptLayer("system", "安全", 100, Required: true),
            new PromptLayer("task", "任务", 90, Required: true),
            new PromptLayer("knowledge", new string('k', 2000), 80, Order: 50)
        }, 600);

        Assert.Contains("knowledge", result.IncludedLayers);
        Assert.Contains("knowledge", result.CompressedLayers!);
        Assert.Contains("上下文已按预算压缩", result.Text);
        Assert.DoesNotContain("knowledge", result.OmittedLayers);
    }

    [Fact]
    public void Compose_DropsLayerWhenItCannotBeSafelyCompressed()
    {
        var result = PromptLayerComposer.Compose(new[]
        {
            new PromptLayer("system", new string('s', 450), 100, Required: true),
            new PromptLayer("knowledge", new string('k', 140), 80)
        }, 512);

        Assert.Contains("knowledge", result.OmittedLayers);
        Assert.DoesNotContain("knowledge", result.CompressedLayers!);
        Assert.True(result.UsedCharacters <= result.BudgetCharacters);
    }

    [Fact]
    public void Compose_ReportsDeterministicTokenEstimateForTelemetry()
    {
        var result = PromptLayerComposer.Compose([
            new PromptLayer("system", "安全规则", 100, Required: true),
            new PromptLayer("task", "Write a concise answer", 90, Required: true)
        ]);

        Assert.Equal(PromptContextBudget.EstimateTokens(result.Text), result.EstimatedTokens);
        Assert.True(result.EstimatedTokens > 0);
        Assert.Equal(result.EstimatedTokens, PromptContextBudget.EstimateTokens(result.Text));
    }

    [Fact]
    public void Compose_CompressionDoesNotSplitUtf16SurrogatePairs()
    {
        var result = PromptLayerComposer.Compose([
            new PromptLayer("system", "安全", 100, Required: true),
            new PromptLayer("knowledge", string.Concat(Enumerable.Repeat("事实😀", 200)), 80)
        ], 600);

        for (var index = 0; index < result.Text.Length; index++)
        {
            if (char.IsHighSurrogate(result.Text[index]))
                Assert.True(index + 1 < result.Text.Length && char.IsLowSurrogate(result.Text[index + 1]));
            if (char.IsLowSurrogate(result.Text[index]))
                Assert.True(index > 0 && char.IsHighSurrogate(result.Text[index - 1]));
        }
    }
}
