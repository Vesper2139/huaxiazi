using System;
using System.IO;
using PromptFloat.Models;
using PromptFloat.Services;
using Xunit;

namespace PromptFloat.Tests;

public sealed class ExternalSkillStrategyResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "VesperStrategyResolver_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Resolve_LoadsAnEnabledDefaultSkillThroughTheSameExternalRuntimePath()
    {
        var presets = Path.Combine(_root, "presets", "text-polisher");
        Directory.CreateDirectory(presets);
        File.WriteAllText(Path.Combine(presets, "SKILL.md"), """
            ---
            name: text-polisher
            description: Mature external polishing workflow.
            metadata:
              vesper.modes: "polish"
            ---
            Preserve the user's facts and return one professional draft.
            """);
        var installed = Path.Combine(_root, "installed");
        new AgentSkillPackageService(installed).ImportPresets(Path.Combine(_root, "presets"));

        var resolved = ExternalSkillStrategyResolver.Resolve(installed, true, "text-polisher", ApplicationMode.Polish);

        Assert.Equal("text-polisher", resolved.Id);
        Assert.Contains("Preserve the user's facts", resolved.Instructions);
    }

    [Fact]
    public void Resolve_ReturnsNoStrategyWhenTheGlobalSwitchIsOff()
    {
        var resolved = ExternalSkillStrategyResolver.Resolve(_root, false, "anything", ApplicationMode.Polish);

        Assert.Equal(string.Empty, resolved.Id);
        Assert.Equal(string.Empty, resolved.Instructions);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
