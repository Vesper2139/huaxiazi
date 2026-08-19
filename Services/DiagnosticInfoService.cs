using System;
using System.Text.Json;
using PromptFloat.Models;

namespace PromptFloat.Services;

public static class DiagnosticInfoService
{
    public static string CreateRedactedJson(AppSettings settings, string? lastErrorType = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var profile = settings.GetActiveProviderProfile();
        var host = Uri.TryCreate(profile.ApiBase, UriKind.Absolute, out var endpoint)
            ? endpoint.Host
            : "invalid";
        var payload = new
        {
            generatedAt = DateTimeOffset.UtcNow,
            appVersion = UpdateChecker.GetCurrentVersion().ToString(),
            osVersion = Environment.OSVersion.VersionString,
            runtime = Environment.Version.ToString(),
            configVersion = settings.ConfigVersion,
            enabledModes = settings.EnabledModes,
            themeMode = settings.ThemeMode,
            provider = new
            {
                profile.Type,
                host,
                profile.Model,
                profile.TimeoutSeconds
            },
            lastErrorType = string.IsNullOrWhiteSpace(lastErrorType) ? null : lastErrorType
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }
}
