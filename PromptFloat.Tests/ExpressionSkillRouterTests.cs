using System;
using System.IO;
using System.Linq;
using PromptFloat.Models;
using PromptFloat.Services;
using Xunit;

namespace PromptFloat.Tests;

public sealed class ExpressionSkillRouterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "VesperSkillRouter_" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(ApplicationMode.PromptOptimize, "帮我把这个模糊需求改成提示词", "", "github-prompt-optimizer")]
    [InlineData(ApplicationMode.Polish, "整理本周项目进展和下周计划，发给领导", "职场沟通", "anthropic-internal-comms")]
    [InlineData(ApplicationMode.Polish, "把这份 API 操作说明改成清晰的教程", "正式材料", "github-documentation-writer")]
    public void Route_SelectsOneMostSpecificEnabledSkill(
        ApplicationMode mode,
        string input,
        string scenario,
        string expectedId)
    {
        var installed = InstallPresetCatalog();

        var result = new ExpressionSkillRouter(installed).Route(new ExpressionSkillRoutingContext
        {
            Mode = mode,
            Input = input,
            Scenario = scenario,
            Category = PromptCategory.General
        });

        Assert.Equal(expectedId, result.SkillId);
        Assert.False(result.UsedFallback);
        Assert.False(string.IsNullOrWhiteSpace(result.Instructions));
    }

    [Fact]
    public void Route_FallsBackWhenNoEnabledSkillMatches()
    {
        var installed = InstallPresetCatalog();
        var packages = new AgentSkillPackageService(installed);
        packages.SetEnabled("anthropic-internal-comms", false);
        packages.SetEnabled("github-documentation-writer", false);

        var result = new ExpressionSkillRouter(installed).Route(new ExpressionSkillRoutingContext
        {
            Mode = ApplicationMode.Polish,
            Input = "今晚晚点回家，不用等我吃饭。",
            Scenario = "私人沟通"
        });

        Assert.True(result.UsedFallback);
        Assert.Equal(string.Empty, result.SkillId);
        Assert.Equal("Vesper 默认表达", result.DisplayName);
    }

    [Fact]
    public void Route_ProjectsOutToolAndApprovalSectionsWithoutChangingTheStoredSnapshot()
    {
        var installed = InstallPresetCatalog();
        var stored = File.ReadAllText(Path.Combine(installed, "anthropic-internal-comms", "SKILL.md"));

        var result = new ExpressionSkillRouter(installed).Route(new ExpressionSkillRoutingContext
        {
            Mode = ApplicationMode.Polish,
            Input = "整理项目进展，发给领导",
            Scenario = "职场沟通"
        });

        Assert.Contains("Slack", stored);
        Assert.DoesNotContain("Slack", result.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Tools Available", result.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("clear and concise", result.Instructions, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PresetCatalog_ImportsUnmodifiedUpstreamSkillWithProvenanceAndStableId()
    {
        var installed = InstallPresetCatalog();

        var record = Assert.Single(
            new AgentSkillPackageService(installed).ListInstalled(),
            item => item.Id == "github-prompt-optimizer");

        Assert.Equal("prompt-optimizer", record.UpstreamName);
        Assert.Equal("提示词优化", record.DisplayName);
        Assert.Equal("https://github.com/github/awesome-copilot", record.SourceRepository);
        Assert.Equal("MIT", record.License);
        Assert.Equal(ApplicationMode.PromptOptimize, record.Mode);
        Assert.True(record.IsEnabled);
        Assert.Contains("prompt", record.RoutingTags);
    }

    [Fact]
    public void Route_UsesAnEnabledUserSkillAsTheModeFallbackAndStopsAfterItIsDisabled()
    {
        var installed = Path.Combine(_root, "user-installed");
        var source = Path.Combine(_root, "user-skill");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "SKILL.md"), """
            ---
            name: my-writing-style
            description: My preferred general writing style.
            ---
            Keep sentences direct and natural.
            """);
        var packages = new AgentSkillPackageService(installed);
        var candidate = Assert.Single(packages.Inspect(source));
        packages.Install(candidate, ApplicationMode.Polish);

        var router = new ExpressionSkillRouter(installed);
        Assert.Equal("my-writing-style", router.Route(new ExpressionSkillRoutingContext
        {
            Mode = ApplicationMode.Polish,
            Input = "今晚晚点回家"
        }).SkillId);

        packages.SetEnabled("my-writing-style", false);
        Assert.True(router.Route(new ExpressionSkillRoutingContext
        {
            Mode = ApplicationMode.Polish,
            Input = "今晚晚点回家"
        }).UsedFallback);
    }

    private string InstallPresetCatalog()
    {
        var presets = Path.Combine(_root, "presets");
        Directory.CreateDirectory(presets);
        WriteSkill("prompt-optimizer", "Turn rough requests into one copy-ready prompt.");
        WriteSkill("internal-comms", "Write clear and concise internal updates.\n\n## Tools Available\nUse Slack and Drive to gather facts.");
        WriteSkill("documentation-writer", "Create clear technical tutorials and reference documentation.");
        File.WriteAllText(Path.Combine(presets, "catalog.json"), """
            {
              "schemaVersion": 1,
              "skills": [
                {
                  "id": "github-prompt-optimizer",
                  "packagePath": "prompt-optimizer",
                  "displayName": "提示词优化",
                  "mode": "PromptOptimize",
                  "defaultEnabled": true,
                  "sourceRepository": "https://github.com/github/awesome-copilot",
                  "sourceRevision": "test-revision",
                  "license": "MIT",
                  "routingTags": ["prompt", "通用"]
                },
                {
                  "id": "anthropic-internal-comms",
                  "packagePath": "internal-comms",
                  "displayName": "职场沟通",
                  "mode": "Polish",
                  "defaultEnabled": true,
                  "sourceRepository": "https://github.com/anthropics/skills",
                  "sourceRevision": "test-revision",
                  "license": "Apache-2.0",
                  "routingTags": ["职场沟通", "汇报", "项目进展", "内部通知", "FAQ"],
                  "excludedSections": ["Tools Available"]
                },
                {
                  "id": "github-documentation-writer",
                  "packagePath": "documentation-writer",
                  "displayName": "技术文档",
                  "mode": "Polish",
                  "defaultEnabled": true,
                  "sourceRepository": "https://github.com/github/awesome-copilot",
                  "sourceRevision": "test-revision",
                  "license": "MIT",
                  "routingTags": ["技术文档", "README", "教程", "操作说明", "API", "参考文档"]
                }
              ]
            }
            """);
        var installed = Path.Combine(_root, "installed");
        new AgentSkillPackageService(installed).ImportPresets(presets);
        return installed;

        void WriteSkill(string name, string body)
        {
            var directory = Path.Combine(presets, name);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "SKILL.md"), $"---\nname: {name}\ndescription: Test {name}.\n---\n{body}\n");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
