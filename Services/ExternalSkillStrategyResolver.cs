using System;
using System.IO;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

/// <summary>Single runtime boundary for preset and user-managed prompt-only Skills.</summary>
public static class ExternalSkillStrategyResolver
{
    public static (string Id, string Instructions) Resolve(
        string installRoot,
        bool globallyEnabled,
        string? configuredSkillId,
        ApplicationMode mode)
    {
        if (!globallyEnabled || string.IsNullOrWhiteSpace(configuredSkillId))
            return (string.Empty, string.Empty);
        try
        {
            var skill = new AgentSkillPackageService(installRoot).LoadInstalled(configuredSkillId, mode);
            return skill is { CanEnable: true }
                ? (skill.Name, skill.Instructions)
                : (string.Empty, string.Empty);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return (string.Empty, string.Empty);
        }
    }
}
