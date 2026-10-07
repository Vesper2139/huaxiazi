using System;
using System.Linq;
using System.Net;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

internal sealed record ProviderRoute(
    ProviderProfile Profile,
    string Reason,
    ProviderProfile? ConfiguredFallback,
    ModelTier? SelectedTier = null,
    string? ModelSelectionReason = null);

internal static class ProviderRouter
{
    public static string DescribeReason(string reason, string? modelSelectionReason = null)
    {
        var description = reason switch
        {
        "manual-active-profile" => "主窗口手动选择",
        "task-profile" => "当前任务绑定",
        "local-only-task-profile" => "仅本地 · 润色/提示词任务绑定",
        "local-only-selected-local-profile" => "仅本地 · 自动选取本地模型",
        "prefer-local" => "优先本地",
        "prefer-cloud" => "优先云端",
        "automatic-fast" => "自动路由 · Fast 档",
        "automatic-balanced" => "自动路由 · Balanced 档",
        "automatic-reasoning" => "自动路由 · Reasoning 档",
        "configured-fallback-no-preferred-profile" => "使用明确配置的备用模型",
        _ => "路由策略选择"
        };
        return reason.StartsWith("automatic-", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(modelSelectionReason)
            ? $"{description} · {modelSelectionReason}"
            : description;
    }

    public static ProviderRoute Select(
        AppSettings settings,
        ApplicationMode task,
        ModelTier? modelTier = null,
        string? modelSelectionReason = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.NormalizeProviderProfiles();

        var taskProfileId = task == ApplicationMode.Polish
            ? settings.PolishProviderProfileId
            : settings.PromptOptimizeProviderProfileId;
        var taskProfile = FindProfile(settings, taskProfileId);
        var fallback = FindProfile(settings, settings.FallbackProviderProfileId);

        switch (settings.ProviderRoutingMode)
        {
            case ProviderRoutingMode.Manual:
            {
                var primary = taskProfile ?? settings.GetActiveProviderProfile();
                var configuredFallback = fallback is not null &&
                    !string.Equals(fallback.Id, primary.Id, StringComparison.OrdinalIgnoreCase)
                        ? fallback
                        : null;
                return new(primary,
                    taskProfile is null ? "manual-active-profile" : "task-profile",
                    configuredFallback);
            }

            case ProviderRoutingMode.LocalOnly:
            {
                var local = IsStrictlyLocal(taskProfile) ? taskProfile
                    : settings.ProviderProfiles.FirstOrDefault(IsStrictlyLocal);
                if (local is null)
                    throw new InvalidOperationException("当前为“仅本地”模式，但没有可用的本地模型配置。请添加本地模型后重试；此模式不会改用云端模型。");
                return new(local, taskProfile == local ? "local-only-task-profile" : "local-only-selected-local-profile", null);
            }

            case ProviderRoutingMode.Automatic:
            {
                if (modelTier is null)
                    throw new InvalidOperationException("自动路由需要先根据当前任务确定模型档位。");
                var profileId = (task, modelTier.Value) switch
                {
                    (ApplicationMode.Polish, ModelTier.Fast) => settings.PolishFastProviderProfileId,
                    (ApplicationMode.Polish, ModelTier.Balanced) => settings.PolishBalancedProviderProfileId,
                    (ApplicationMode.Polish, ModelTier.Reasoning) => settings.PolishReasoningProviderProfileId,
                    (ApplicationMode.PromptOptimize, ModelTier.Fast) => settings.PromptOptimizeFastProviderProfileId,
                    (ApplicationMode.PromptOptimize, ModelTier.Balanced) => settings.PromptOptimizeBalancedProviderProfileId,
                    (ApplicationMode.PromptOptimize, ModelTier.Reasoning) => settings.PromptOptimizeReasoningProviderProfileId,
                    _ => string.Empty
                };
                var selected = FindProfile(settings, profileId);
                if (selected is null)
                {
                    var taskLabel = task == ApplicationMode.Polish ? "润色" : "提示词优化";
                    throw new InvalidOperationException($"自动路由未为{taskLabel}绑定 {modelTier.Value} 档模型。请在设置中选择对应 Provider profile；不会静默切换到其他档位。");
                }
                var configuredFallback = fallback is not null &&
                    !string.Equals(fallback.Id, selected.Id, StringComparison.OrdinalIgnoreCase)
                        ? fallback
                        : null;
                return new(selected, $"automatic-{modelTier.Value.ToString().ToLowerInvariant()}", configuredFallback, modelTier, modelSelectionReason);
            }

            case ProviderRoutingMode.PreferLocal:
            case ProviderRoutingMode.PreferCloud:
            {
                var preferredLocal = settings.ProviderRoutingMode == ProviderRoutingMode.PreferLocal;
                var preferred = taskProfile is not null && IsProviderKind(taskProfile, preferredLocal)
                    ? taskProfile
                    : settings.ProviderProfiles.FirstOrDefault(profile => IsProviderKind(profile, preferredLocal));
                if (preferred is not null)
                    return new(preferred, preferredLocal ? "prefer-local" : "prefer-cloud",
                        fallback is not null && !string.Equals(fallback.Id, preferred.Id, StringComparison.OrdinalIgnoreCase) ? fallback : null);

                if (fallback is not null && IsProviderKind(fallback, !preferredLocal))
                    return new(fallback, "configured-fallback-no-preferred-profile", null);

                var preference = preferredLocal ? "本地" : "云端";
                throw new InvalidOperationException($"当前为“优先{preference}”模式，但没有可用的首选模型或明确配置的备用模型。");
            }

            default:
                throw new InvalidOperationException("未知的模型路由策略。请在设置中重新选择模型策略。");
        }
    }

    public static bool IsStrictlyLocal(ProviderProfile? profile)
    {
        if (profile is null || profile.Type != ProviderType.Local) return false;
        if (profile.Platform == ProviderPlatform.ManagedLocal) return true;
        if (!Uri.TryCreate(profile.ApiBase, UriKind.Absolute, out var endpoint)) return false;
        if (endpoint.IsLoopback) return true;

        var host = endpoint.Host.TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }

    private static bool IsProviderKind(ProviderProfile profile, bool local) =>
        local ? IsStrictlyLocal(profile) : profile.Type == ProviderType.Cloud;

    private static ProviderProfile? FindProfile(AppSettings settings, string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : settings.ProviderProfiles.FirstOrDefault(profile =>
                string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase));
}

internal static class PreferenceDisclosurePolicy
{
    public static bool CanSendLegacyPreferences(AppSettings settings, ProviderRoute route)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(route);
        return ProviderRouter.IsStrictlyLocal(route.Profile) &&
               (route.ConfiguredFallback is null || ProviderRouter.IsStrictlyLocal(route.ConfiguredFallback));
    }

    public static bool CanSendConfirmedPreferences(AppSettings settings, ProviderRoute route)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(route);
        if (settings.ShareConfirmedPreferencesWithCloud) return true;
        return ProviderRouter.IsStrictlyLocal(route.Profile) &&
               (route.ConfiguredFallback is null || ProviderRouter.IsStrictlyLocal(route.ConfiguredFallback));
    }
}
