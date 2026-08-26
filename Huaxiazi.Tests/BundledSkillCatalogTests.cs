using System;
using System.IO;
using System.Linq;
using Huaxiazi.Models;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class BundledSkillCatalogTests : IDisposable
{
    private readonly string _installed = Path.Combine(Path.GetTempPath(), "HuaxiaziBundledSkills_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ShippedCatalog_ImportsThreeLicensedOfflineSkillsThatCanBeRouted()
    {
        var presets = Path.Combine(RepoRoot(), "Skills");
        var packages = new AgentSkillPackageService(_installed);

        var imported = packages.ImportPresets(presets);

        Assert.Equal(3, imported.Count);
        Assert.All(imported, skill =>
        {
            Assert.True(skill.IsEnabled);
            Assert.False(string.IsNullOrWhiteSpace(skill.SourceRepository));
            Assert.False(string.IsNullOrWhiteSpace(skill.SourceRevision));
            Assert.False(string.IsNullOrWhiteSpace(skill.License));
            Assert.True(File.Exists(Path.Combine(skill.Candidate.PackageRoot, "LICENSE.txt")));
        });
        Assert.DoesNotContain(imported, skill => skill.UpstreamName == "text-polisher");
        var localizedDescription = typeof(AgentSkillRecord).GetProperty("EffectiveDescription");
        Assert.NotNull(localizedDescription);
        Assert.All(imported, skill =>
        {
            var description = Assert.IsType<string>(localizedDescription.GetValue(skill));
            Assert.Matches("[\\u4e00-\\u9fff]", description);
        });

        var router = new ExpressionSkillRouter(_installed);
        Assert.Equal("github-prompt-optimizer", router.Route(new ExpressionSkillRoutingContext
        {
            Mode = ApplicationMode.PromptOptimize,
            Input = "优化这个提示词"
        }).SkillId);
        var communication = router.Route(new ExpressionSkillRoutingContext
        {
            Mode = ApplicationMode.Polish,
            Scenario = "职场沟通",
            Input = "整理项目进展"
        });
        Assert.Equal("anthropic-internal-comms", communication.SkillId);
        Assert.Contains("Progress", communication.Instructions);
        Assert.DoesNotContain("Apache License", communication.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Google Drive", communication.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gather as much context", communication.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("github-documentation-writer", router.Route(new ExpressionSkillRoutingContext
        {
            Mode = ApplicationMode.Polish,
            Scenario = "正式材料",
            Input = "改写 API 操作说明"
        }).SkillId);
    }

    [Fact]
    public void CatalogUpgrade_DoesNotInterpretOrDeleteUnrelatedSkillMetadata()
    {
        var legacyRoot = Path.Combine(_installed, "legacy-presets");
        var legacySkill = Path.Combine(legacyRoot, "text-polisher");
        Directory.CreateDirectory(legacySkill);
        File.WriteAllText(Path.Combine(legacySkill, "SKILL.md"), """
            ---
            name: text-polisher
            description: Legacy Huaxiazi preset.
            metadata:
              huaxiazi.modes: "polish"
            ---
            Preserve facts.
            """);
        var packages = new AgentSkillPackageService(_installed);
        packages.ImportPresets(legacyRoot);
        packages.CloneForEditing("text-polisher", "text-polisher-custom");

        packages.ImportPresets(Path.Combine(RepoRoot(), "Skills"));

        var records = packages.ListInstalled();
        Assert.Contains(records, item => item.Id == "text-polisher" && item.Source == AgentSkillSource.Preset);
        Assert.Contains(records, item => item.Id == "text-polisher-custom" && item.Source == AgentSkillSource.User);
    }

    private static string RepoRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "Huaxiazi.sln")))
            directory = Path.GetDirectoryName(directory);
        return directory ?? throw new DirectoryNotFoundException("未找到解决方案根目录。");
    }

    public void Dispose()
    {
        if (Directory.Exists(_installed)) Directory.Delete(_installed, true);
    }
}
