using System.Collections.Generic;
using System.Text.Json;
using Huaxiazi.Models;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class OutputStylePreferenceResolverTests
{
    [Fact]
    public void Resolve_UsesExactScenarioOverrideBeforeTaskAndGlobalDefaults()
    {
        var overrides = new Dictionary<string, string>
        {
            ["Polish|"] = "专业",
            ["Polish|职场沟通"] = "克制"
        };

        Assert.Equal("克制", OutputStylePreferenceResolver.Resolve(
            ApplicationMode.Polish, "职场沟通", "自然", overrides));
        Assert.Equal("专业", OutputStylePreferenceResolver.Resolve(
            ApplicationMode.Polish, "公开发布", "自然", overrides));
        Assert.Equal("自然", OutputStylePreferenceResolver.Resolve(
            ApplicationMode.PromptOptimize, "编程开发", "自然", overrides));
    }

    [Fact]
    public void Resolve_IgnoresInvalidOverrideAndPreservesGlobalFallback()
    {
        var overrides = new Dictionary<string, string>
        {
            ["Polish|职场沟通"] = "未定义风格"
        };

        Assert.Equal("正式", OutputStylePreferenceResolver.Resolve(
            ApplicationMode.Polish, "职场沟通", "正式", overrides));
    }

    [Fact]
    public void NormalizeOverrides_RemovesUnknownScopesAndStyles()
    {
        var normalized = OutputStylePreferenceResolver.NormalizeOverrides(new Dictionary<string, string>
        {
            ["Polish|"] = "专业",
            ["Polish|职场沟通"] = "未知风格",
            ["PromptOptimize|编程开发"] = "简洁",
            ["Unexpected|anything"] = "正式",
            ["Polish|not-a-scenario"] = "克制"
        });

        Assert.Equal(2, normalized.Count);
        Assert.Equal("专业", normalized["Polish|"]);
        Assert.Equal("简洁", normalized["PromptOptimize|编程开发"]);
    }

    [Fact]
    public void AppSettings_LegacyConfigDefaultsToGlobalStyleAndClonesScopedOverrides()
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>("{\"configVersion\":21,\"outputStyle\":\"正式\"}")!;
        legacy.NormalizePromptSettings();

        Assert.Equal("正式", legacy.OutputStyle);
        Assert.Empty(legacy.OutputStyleOverrides);

        legacy.OutputStyleOverrides["Polish|职场沟通"] = "克制";
        var clone = legacy.Clone();
        clone.OutputStyleOverrides["Polish|职场沟通"] = "亲切";

        Assert.Equal("克制", legacy.OutputStyleOverrides["Polish|职场沟通"]);
        Assert.Equal("亲切", clone.OutputStyleOverrides["Polish|职场沟通"]);
    }
}
