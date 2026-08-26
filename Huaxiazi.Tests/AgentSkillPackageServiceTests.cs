using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Huaxiazi.Models;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class AgentSkillPackageServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HuaxiaziAgentSkills_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Inspect_StandardPromptSkill_IsReadyAndMapsHuaxiaziMode()
    {
        var source = CreateSkill("professional-email", """
            ---
            name: professional-email
            description: Rewrite rough workplace messages as concise professional emails.
            license: MIT
            metadata:
              author: example
              version: "1.2.0"
              huaxiazi.modes: "polish"
            ---
            Preserve facts and produce one sendable draft.
            """);

        var candidate = Assert.Single(new AgentSkillPackageService(Path.Combine(_root, "installed")).Inspect(source));

        Assert.Equal(SkillCompatibilityStatus.Ready, candidate.Status);
        Assert.Contains(ApplicationMode.Polish, candidate.Modes);
        Assert.Equal("MIT", candidate.License);
    }

    [Fact]
    public void Inspect_StandardSkillWithoutHuaxiaziMetadata_NeedsUserMapping()
    {
        var source = CreateSkill("clear-writing", """
            ---
            name: clear-writing
            description: Improves clarity and structure of writing.
            ---
            Rewrite the supplied text clearly.
            """);

        var candidate = Assert.Single(new AgentSkillPackageService(Path.Combine(_root, "installed")).Inspect(source));

        Assert.Equal(SkillCompatibilityStatus.NeedsMapping, candidate.Status);
        Assert.NotEqual(ApplicationMode.PromptOptimize, candidate.SuggestedMode);
    }

    [Fact]
    public void Inspect_SkillWithScripts_IsPreservedButNeverRunnable()
    {
        var source = CreateSkill("shell-writer", """
            ---
            name: shell-writer
            description: Rewrites text by running a shell script.
            allowed-tools: Bash
            metadata:
              huaxiazi.modes: "polish"
            ---
            Run scripts/rewrite.ps1.
            """);
        Directory.CreateDirectory(Path.Combine(source, "scripts"));
        File.WriteAllText(Path.Combine(source, "scripts", "rewrite.ps1"), "Write-Output test");

        var candidate = Assert.Single(new AgentSkillPackageService(Path.Combine(_root, "installed")).Inspect(source));

        Assert.Equal(SkillCompatibilityStatus.RequiresTools, candidate.Status);
        Assert.False(candidate.CanEnable);
        var reviewPath = new AgentSkillPackageService(Path.Combine(_root, "installed")).PreserveForReview(candidate);
        Assert.True(File.Exists(Path.Combine(reviewPath, "scripts", "rewrite.ps1")));
    }

    [Fact]
    public void Install_CopiesSnapshotAndExportProducesStandardSkillZip()
    {
        var source = CreateSkill("text-polisher-extra", """
            ---
            name: text-polisher-extra
            description: Polishes Chinese text while preserving facts.
            metadata:
              huaxiazi.modes: "polish"
            ---
            Produce one concise final draft.
            """);
        var service = new AgentSkillPackageService(Path.Combine(_root, "installed"));
        var candidate = Assert.Single(service.Inspect(source));

        var installed = service.Install(candidate, ApplicationMode.Polish);
        Directory.Delete(source, true);
        var export = Path.Combine(_root, "export.zip");
        service.Export(installed.Name, export);

        Assert.True(File.Exists(Path.Combine(installed.InstallPath, "SKILL.md")));
        using var zip = ZipFile.OpenRead(export);
        Assert.Contains(zip.Entries, entry => entry.FullName.EndsWith("SKILL.md", StringComparison.Ordinal));
    }

    [Fact]
    public void Inspect_ZipTraversalEntry_IsRejected()
    {
        Directory.CreateDirectory(_root);
        var zipPath = Path.Combine(_root, "bad.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            archive.CreateEntry("../outside/SKILL.md");

        Assert.Throws<InvalidDataException>(() =>
            new AgentSkillPackageService(Path.Combine(_root, "installed")).Inspect(zipPath));
    }

    [Fact]
    public void ImportPresets_FirstRunCreatesExternalSnapshotWithoutChangingTheSourcePackage()
    {
        var presetRoot = Path.Combine(_root, "presets");
        var source = CreateSkillAt(presetRoot, "text-polisher", StandardSkill("text-polisher", "polish", "Original preset instructions."));
        var original = File.ReadAllText(Path.Combine(source, "SKILL.md"));
        var service = new AgentSkillPackageService(Path.Combine(_root, "installed"));

        var imported = Assert.Single(service.ImportPresets(presetRoot));

        Assert.Equal(AgentSkillSource.Preset, imported.Source);
        Assert.True(imported.IsEnabled);
        Assert.NotEqual(source, imported.Candidate.PackageRoot);
        Assert.Equal(original, File.ReadAllText(Path.Combine(source, "SKILL.md")));
        Assert.Equal("Original preset instructions.", service.LoadInstalled("text-polisher", ApplicationMode.Polish)?.Instructions);
    }

    [Fact]
    public void SetEnabled_PersistsAcrossServiceInstancesAndControlsRuntimeLoading()
    {
        var presetRoot = Path.Combine(_root, "presets");
        CreateSkillAt(presetRoot, "text-polisher", StandardSkill("text-polisher", "polish", "Preset instructions."));
        var installedRoot = Path.Combine(_root, "installed");
        var service = new AgentSkillPackageService(installedRoot);
        service.ImportPresets(presetRoot);

        service.SetEnabled("text-polisher", false);

        var reloaded = new AgentSkillPackageService(installedRoot);
        Assert.False(Assert.Single(reloaded.ListInstalled()).IsEnabled);
        Assert.Null(reloaded.LoadInstalled("text-polisher", ApplicationMode.Polish));
        reloaded.SetEnabled("text-polisher", true);
        Assert.NotNull(reloaded.LoadInstalled("text-polisher", ApplicationMode.Polish));
    }

    [Fact]
    public void ImportPresets_UpdatesTheReadOnlyBaselineButPreservesTheUsersDisabledState()
    {
        var presetRoot = Path.Combine(_root, "presets");
        var source = CreateSkillAt(presetRoot, "text-polisher", StandardSkill("text-polisher", "polish", "Version one."));
        var service = new AgentSkillPackageService(Path.Combine(_root, "installed"));
        service.ImportPresets(presetRoot);
        service.SetEnabled("text-polisher", false);
        File.WriteAllText(Path.Combine(source, "SKILL.md"), StandardSkill("text-polisher", "polish", "Version two."));

        var updated = Assert.Single(service.ImportPresets(presetRoot));

        Assert.False(updated.IsEnabled);
        Assert.Equal("Version two.", updated.Instructions);
        Assert.Null(service.LoadInstalled("text-polisher", ApplicationMode.Polish));
    }

    [Fact]
    public void CloneForEditing_CreatesEditableUserSkillAndPreservesPresetBaseline()
    {
        var presetRoot = Path.Combine(_root, "presets");
        var source = CreateSkillAt(presetRoot, "text-polisher", StandardSkill("text-polisher", "polish", "Preset instructions."));
        var service = new AgentSkillPackageService(Path.Combine(_root, "installed"));
        service.ImportPresets(presetRoot);

        var clone = service.CloneForEditing("text-polisher", "text-polisher-custom");
        service.SaveInstructions(clone.Name, "My edited instructions.");

        Assert.Equal(AgentSkillSource.User, clone.Source);
        Assert.True(clone.CanEdit);
        Assert.Equal("My edited instructions.", service.LoadInstalled(clone.Name, ApplicationMode.Polish)?.Instructions);
        Assert.Equal("Preset instructions.", service.LoadInstalled("text-polisher", ApplicationMode.Polish)?.Instructions);
        Assert.Contains("Preset instructions.", File.ReadAllText(Path.Combine(source, "SKILL.md")));
    }

    [Fact]
    public void Remove_RejectsPresetButDeletesUserManagedSkill()
    {
        var presetRoot = Path.Combine(_root, "presets");
        CreateSkillAt(presetRoot, "text-polisher", StandardSkill("text-polisher", "polish", "Preset instructions."));
        var service = new AgentSkillPackageService(Path.Combine(_root, "installed"));
        service.ImportPresets(presetRoot);
        var clone = service.CloneForEditing("text-polisher", "text-polisher-custom");

        Assert.Throws<InvalidOperationException>(() => service.Remove("text-polisher"));
        service.Remove(clone.Name);

        Assert.DoesNotContain(service.ListInstalled(), item => item.Name == clone.Name);
    }

    private string CreateSkill(string name, string content)
    {
        var directory = Path.Combine(_root, "sources", name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "SKILL.md"), content);
        return directory;
    }

    private static string CreateSkillAt(string root, string name, string content)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "SKILL.md"), content);
        return directory;
    }

    private static string StandardSkill(string name, string mode, string instructions) => $$"""
        ---
        name: {{name}}
        description: Mature external expression strategy.
        license: MIT
        metadata:
          author: Huaxiazi
          version: "1.0.0"
          huaxiazi.modes: "{{mode}}"
        ---
        {{instructions}}
        """;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
