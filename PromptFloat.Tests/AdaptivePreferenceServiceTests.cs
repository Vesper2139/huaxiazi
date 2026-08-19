using System;
using System.Collections.Generic;
using PromptFloat.Models;
using PromptFloat.Services;
using Xunit;

namespace PromptFloat.Tests;

public sealed class AdaptivePreferenceServiceTests
{
    [Fact]
    public void BuildInstructions_LearnsOnlyRepeatedExplicitUserEdits()
    {
        var revisions = new List<ContentRevision>
        {
            Edited("感谢您的理解与支持，我们会尽快推进。", "我们会尽快推进。"),
            Edited("感谢您的理解与支持，后续有进展我会同步。", "后续有进展我会同步。"),
            Edited("首先说明一下当前情况。", "先说明一下当前情况。")
        };

        var instructions = new AdaptivePreferenceService().BuildInstructions(revisions);

        Assert.Contains("感谢您的理解与支持", instructions);
        Assert.DoesNotContain("首先", instructions);
    }

    [Fact]
    public void BuildInstructions_IgnoresOrdinaryGeneratedRevisions()
    {
        var revisions = new[]
        {
            new ContentRevision { FinalText = "感谢您的理解与支持", ContextJson = "{}" }
        };

        var instructions = new AdaptivePreferenceService().BuildInstructions(revisions);

        Assert.Equal(string.Empty, instructions);
    }

    [Fact]
    public void BuildInstructions_LearnsRepeatedSubstantialShorteningAsStructuredPreference()
    {
        var revisions = new[]
        {
            Edited("关于这个问题，我想先简单说明一下，目前项目还在继续推进当中。", "项目还在推进。"),
            Edited("在这里也跟大家同步一下，我们后续有新的进展会及时告诉大家。", "有新进展我会同步。"),
            Edited("首先还是非常感谢大家一直以来的关注，活动将在周五正式开始。", "活动周五开始。")
        };

        var instructions = new AdaptivePreferenceService().BuildInstructions(revisions);

        Assert.Contains("优先简洁", instructions);
        Assert.Contains("减少铺垫和重复", instructions);
    }

    [Fact]
    public void BuildInstructions_DoesNotLearnRewriteStrengthFromTooFewEdits()
    {
        var revisions = new[]
        {
            Edited("关于这个问题，我想先说明一下，目前项目还在推进。", "项目还在推进。"),
            Edited("在这里同步一下，后续有进展会告诉大家。", "有进展我会同步。")
        };

        var instructions = new AdaptivePreferenceService().BuildInstructions(revisions);

        Assert.DoesNotContain("优先简洁", instructions);
    }

    private static ContentRevision Edited(string generated, string final) => new()
    {
        Id = Guid.NewGuid(),
        ItemId = Guid.NewGuid(),
        FinalText = final,
        ContextJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            UserEdited = true,
            GeneratedText = generated
        })
    };
}
