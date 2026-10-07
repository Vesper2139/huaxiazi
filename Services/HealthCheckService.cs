using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

public enum HealthCheckScope { Local, External }
public enum HealthCheckStatus { Healthy, Warning, Failed }

public sealed record HealthCheckItem(
    string Name,
    HealthCheckScope Scope,
    HealthCheckStatus Status,
    string Message);

public sealed class HealthCheckReport
{
    public required DateTimeOffset CheckedAt { get; init; }
    public required IReadOnlyList<HealthCheckItem> Items { get; init; }
    public bool HasLocalFailures => Items.Any(item => item.Scope == HealthCheckScope.Local && item.Status == HealthCheckStatus.Failed);
    public bool HasExternalFailures => Items.Any(item => item.Scope == HealthCheckScope.External && item.Status == HealthCheckStatus.Failed);
    public string Summary => $"Local {Items.Count(item => item.Scope == HealthCheckScope.Local && item.Status == HealthCheckStatus.Healthy)}/{Items.Count(item => item.Scope == HealthCheckScope.Local)} · " +
                             $"External {(HasExternalFailures ? "异常" : "正常")} · {CheckedAt.ToLocalTime():HH:mm:ss}";
}

/// <summary>启动健康检查。只记录状态与脱敏错误，不记录 API Key 或用户内容。</summary>
public sealed class HealthCheckService
{
    private readonly HttpClient _httpClient;
    private readonly LocalModelStore _localModelStore;
    private readonly string _runtimeRoot;
    private readonly Func<string, CancellationToken, Task> _runtimeExecutableProbe;
    private readonly Func<bool, IReadOnlyList<string>> _runtimeDependencyProbe;

    public HealthCheckService(HttpMessageHandler? handler = null, LocalModelStore? localModelStore = null,
        string? runtimeRoot = null, Func<string, CancellationToken, Task>? runtimeExecutableProbe = null,
        Func<bool, IReadOnlyList<string>>? runtimeDependencyProbe = null)
    {
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _httpClient.Timeout = TimeSpan.FromSeconds(4);
        _localModelStore = localModelStore ?? new LocalModelStore();
        _runtimeRoot = Path.GetFullPath(runtimeRoot ?? LocalRuntimePaths.GetRuntimeRoot());
        _runtimeExecutableProbe = runtimeExecutableProbe ?? LocalRuntimePackageService.ProbeInstalledExecutableAsync;
        _runtimeDependencyProbe = runtimeDependencyProbe ?? InspectRuntimeDependencies;
    }

    public async Task<HealthCheckReport> CheckAsync(
        AppSettings settings,
        string dataRoot,
        Func<string, string?> secretReader,
        bool hotkeyRegistered,
        CancellationToken cancellationToken = default)
        => await CheckAsync(settings, dataRoot, secretReader, new HotkeyRegistrationResult
        {
            Actions = new Dictionary<GlobalHotkeyAction, HotkeyActionRegistration>
            {
                [GlobalHotkeyAction.ToggleWindow] = new(hotkeyRegistered,
                    hotkeyRegistered ? "已注册" : "至少一组全局快捷键注册失败")
            }
        }, cancellationToken);

    public async Task<HealthCheckReport> CheckAsync(
        AppSettings settings,
        string dataRoot,
        Func<string, string?> secretReader,
        HotkeyRegistrationResult hotkeyRegistration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(secretReader);
        var items = new List<HealthCheckItem>(6);
        ProviderProfile? provider = null;

        try
        {
            settings.NormalizeProductModes();
            settings.NormalizeProviderProfiles();
            provider = settings.GetActiveProviderProfile();
            items.Add(Healthy("Config", HealthCheckScope.Local, "配置结构和活动模型有效。"));
        }
        catch (Exception exception)
        {
            items.Add(Failed("Config", HealthCheckScope.Local, exception.Message));
        }

        try
        {
            Directory.CreateDirectory(dataRoot);
            var probe = Path.Combine(dataRoot, $".health-write-probe-{Guid.NewGuid():N}");
            try
            {
                await File.WriteAllTextAsync(probe, "ok", cancellationToken);
            }
            finally
            {
                try
                {
                    if (File.Exists(probe)) File.Delete(probe);
                }
                catch (IOException)
                {
                    // The write already proved the directory is usable.
                }
                catch (UnauthorizedAccessException)
                {
                    // Ignore a transient ACL/endpoint-protection race during cleanup.
                }
            }
            items.Add(Healthy("Storage", HealthCheckScope.Local, "数据目录可读写。"));
        }
        catch (Exception exception)
        {
            items.Add(Failed("Storage", HealthCheckScope.Local, exception.Message));
        }

        try
        {
            var integrity = new ArchiveService(dataRoot).CheckIntegrity();
            if (!integrity.IsHealthy) throw new InvalidDataException("资料库完整性检查失败：" + integrity.Message);
            items.Add(Healthy("Database", HealthCheckScope.Local, "资料库完整性检查通过。"));
        }
        catch (Exception exception)
        {
            items.Add(Failed("Database", HealthCheckScope.Local, exception.Message));
        }

        string? apiKey = null;
        try
        {
            apiKey = provider is null ? null : secretReader(provider.SecretId);
            items.Add(Healthy("Credential", HealthCheckScope.Local,
                string.IsNullOrWhiteSpace(apiKey) ? "凭据存储可访问，当前未保存 Key。" : "凭据存储可访问。"));
        }
        catch (Exception exception)
        {
            items.Add(Failed("Credential", HealthCheckScope.Local, exception.Message));
        }

        items.Add(hotkeyRegistration.AllSucceeded
            ? Healthy("HotKey", HealthCheckScope.Local, hotkeyRegistration.HasConfiguredActions
                ? "已配置的全局快捷键已注册。"
                : "未配置全局快捷键，可通过悬浮球或托盘使用。")
            : Warning("HotKey", HealthCheckScope.Local, hotkeyRegistration.Summary));

        items.Add(await CheckProviderAsync(provider, apiKey, cancellationToken));
        return new HealthCheckReport { CheckedAt = DateTimeOffset.Now, Items = items };
    }

    private async Task<HealthCheckItem> CheckProviderAsync(
        ProviderProfile? provider,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        if (provider is null) return Failed("Provider", HealthCheckScope.External, "没有可用的活动模型配置。");
        if (provider.Platform == ProviderPlatform.ManagedLocal)
        {
            if (string.IsNullOrWhiteSpace(provider.LocalModelInstallationId))
                return Failed("Provider", HealthCheckScope.External, "未选择受管本地模型。");
            if (!_localModelStore.GetInstalledModels().Any(model => model.InstallationId == provider.LocalModelInstallationId))
                return Failed("Provider", HealthCheckScope.External, "所选本地模型尚未安装。");
            var gpuMode = provider.LocalRuntimeOptions?.GpuMode ?? LocalGpuMode.Auto;
            var candidates = LocalRuntimeManager.ResolveRuntimeCandidates(_runtimeRoot, gpuMode);
            if (candidates.Count == 0)
                return Failed("Provider", HealthCheckScope.Local, "本地推理运行时未安装，当前无法生成。");

            var probeFailures = new List<string>();
            foreach (var candidate in candidates)
            {
                var missingDependencies = _runtimeDependencyProbe(candidate.UseVulkan);
                if (missingDependencies.Count > 0)
                {
                    probeFailures.Add($"{(candidate.UseVulkan ? "Vulkan" : "CPU")}: {DescribeMissingRuntimeDependencies(missingDependencies)}");
                    continue;
                }

                try
                {
                    using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    probeTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                    await _runtimeExecutableProbe(candidate.ExecutablePath, probeTimeout.Token).ConfigureAwait(false);
                    var backend = candidate.UseVulkan ? "Vulkan" : "CPU";
                    var fallbackNote = probeFailures.Count == 0
                        ? string.Empty
                        : $"；{string.Join("；", probeFailures)}；已验证 CPU 回退路径";
                    return Healthy("Provider", HealthCheckScope.Local,
                        $"本地运行时 {backend} 可执行文件版本探测通过{fallbackNote}；未启动模型、未发起外部网络请求。");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    probeFailures.Add($"{(candidate.UseVulkan ? "Vulkan" : "CPU")}: {exception.Message}");
                }
            }
            return Failed("Provider", HealthCheckScope.Local,
                "本地运行时可执行文件版本探测失败：" + string.Join("；", probeFailures));
        }
        if (!ProviderEndpointPolicy.TryValidate(provider, apiKey, out var endpoint, out var validationError))
            return Failed("Provider", HealthCheckScope.External, validationError);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, endpoint);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return Failed("Provider", HealthCheckScope.External, "端点返回认证失败，请检查 API Key 和访问权限。");
            return (int)response.StatusCode >= 500
                ? Failed("Provider", HealthCheckScope.External, $"端点返回 HTTP {(int)response.StatusCode}。")
                : Healthy("Provider", HealthCheckScope.External, $"端点可达（HTTP {(int)response.StatusCode}）。");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failed("Provider", HealthCheckScope.External, "端点不可达：" + exception.Message);
        }
    }

    private static IReadOnlyList<string> InspectRuntimeDependencies(bool useVulkan)
    {
        if (!OperatingSystem.IsWindows()) return [];

        var required = new List<string>(capacity: 4)
        {
            "MSVCP140.dll",
            "VCRUNTIME140.dll",
            "VCRUNTIME140_1.dll"
        };
        if (useVulkan) required.Add("vulkan-1.dll");

        var missing = new List<string>();
        foreach (var library in required)
        {
            // Match the managed runtime's OS-installed prerequisites; do not let an unrelated PATH entry
            // make the health check report a dependency that the separately launched runtime cannot resolve.
            if (!NativeLibrary.TryLoad(library, typeof(HealthCheckService).Assembly,
                    DllImportSearchPath.System32, out var handle))
            {
                missing.Add(library);
                continue;
            }
            NativeLibrary.Free(handle);
        }
        return missing;
    }

    private static string DescribeMissingRuntimeDependencies(IReadOnlyList<string> missing)
    {
        var missingText = string.Join(", ", missing);
        if (missing.Any(name => name.StartsWith("VCRUNTIME", StringComparison.OrdinalIgnoreCase) ||
                                name.StartsWith("MSVCP", StringComparison.OrdinalIgnoreCase)))
            return $"缺少 Microsoft Visual C++ x64 运行库（{missingText}）。请安装 Microsoft Visual C++ 2015–2022 x64 Redistributable： https://aka.ms/vs/17/release/vc_redist.x64.exe ，然后重试";
        if (missing.Contains("vulkan-1.dll", StringComparer.OrdinalIgnoreCase))
            return "缺少 Vulkan loader（vulkan-1.dll）；请更新显卡驱动，或在本地模型设置中切换为 CPU 后端";
        return $"缺少本地运行时依赖：{missingText}";
    }

    private static HealthCheckItem Healthy(string name, HealthCheckScope scope, string message) =>
        new(name, scope, HealthCheckStatus.Healthy, message);

    private static HealthCheckItem Failed(string name, HealthCheckScope scope, string message) =>
        new(name, scope, HealthCheckStatus.Failed, message);

    private static HealthCheckItem Warning(string name, HealthCheckScope scope, string message) =>
        new(name, scope, HealthCheckStatus.Warning, message);
}
