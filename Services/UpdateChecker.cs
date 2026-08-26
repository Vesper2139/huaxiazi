using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Huaxiazi.Services;

/// <summary>
/// 自动更新检查状态。
/// 所有异常路径（离线 / HTTP 错误 / 坏 JSON）均归类为 <see cref="Error"/>，由调用方安静降级处理，
/// 绝不向上抛出未处理异常，避免引入任何网络硬依赖或在启动时强制联网。
/// </summary>
public enum UpdateStatus
{
    /// <summary>未配置更新源（<c>updateCheckUrl</c> 为空），功能禁用。</summary>
    Disabled,

    /// <summary>已是最新版本。</summary>
    UpToDate,

    /// <summary>发现新版本（远端版本号大于当前程序集版本）。</summary>
    UpdateAvailable,

    /// <summary>检查失败（离线 / HTTP 错误 / 坏 JSON / 解析失败），安全降级。</summary>
    Error
}

/// <summary>
/// 更新检查结果。
/// </summary>
public sealed class UpdateCheckResult
{
    /// <summary>检查状态。</summary>
    public UpdateStatus Status { get; init; } = UpdateStatus.Error;

    /// <summary>当前程序集版本。</summary>
    public Version? CurrentVersion { get; init; }

    /// <summary>远端最新版本（仅在成功解析时非空）。</summary>
    public Version? LatestVersion { get; init; }

    /// <summary>远端发布页 / 下载地址（version.json 的 <c>url</c> 字段，可能为空）。</summary>
    public string? DownloadUrl { get; init; }

    public string? Sha256 { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    public string? ReleaseNotes { get; init; }

    /// <summary>面向用户的状态文案（已本地化、可直接展示）。</summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// 轻量可降级的自动更新检查服务。
/// 从可配置 URL 拉取 <c>version.json</c>（<c>{ "version": "x.y.z", "url": "https://...release" }</c>），
/// 与安全读取的当前程序集版本（<see cref="GetCurrentVersion"/>）做语义化比较。
///
/// 设计原则（安全优先）：
/// <list type="bullet">
///   <item>不引入任何网络硬依赖：HttpClient 在每次检查时按需创建并在使用后立即释放；</item>
///   <item>不强制启动检查：仅由 UI 在用户主动点击“检查更新”时调用；</item>
///   <item>全面降级：URL 为空=禁用；离线 / HTTP 失败 / 坏 JSON 均返回 <see cref="UpdateStatus.Error"/> 且不抛异常。</item>
/// </list>
/// </summary>
public sealed class UpdateChecker
{
    /// <summary>HTTP 请求的默认超时时间（秒），避免长时间阻塞 UI。</summary>
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<HttpClient> _clientFactory;

    /// <summary>
    /// 默认构造：每次检查创建带超时的 <see cref="HttpClient"/>，使用完毕后释放。
    /// </summary>
    public UpdateChecker() : this(() => new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = DefaultTimeout })
    {
    }

    /// <summary>
    /// 注入 <see cref="HttpClient"/>（便于单元测试用桩处理器替换网络层）。
    /// 注意：传入的客户端将在每次 <see cref="CheckAsync"/> 调用结束后被释放。
    /// </summary>
    public UpdateChecker(HttpClient client) : this(() => client)
    {
    }

    private UpdateChecker(Func<HttpClient> clientFactory)
    {
        _clientFactory = clientFactory ?? (() => new HttpClient { Timeout = DefaultTimeout });
    }

    /// <summary>
    /// 读取当前程序集版本（<c>Huaxiazi</c> 的 <c>AssemblyVersion</c>）。
    /// 任何异常均回退到 <c>0.0.0.0</c>，确保永不崩溃。
    /// </summary>
    public static Version GetCurrentVersion()
    {
        try
        {
            var assembly = typeof(UpdateChecker).Assembly;
            return assembly.GetName().Version ?? new Version(0, 0, 0, 0);
        }
        catch
        {
            return new Version(0, 0, 0, 0);
        }
    }

    /// <summary>
    /// 拉取远端 <c>version.json</c> 并与当前版本比较。安全降级，绝不抛未处理异常。
    /// </summary>
    /// <param name="updateCheckUrl">更新源 URL；为空或空白时直接返回 <see cref="UpdateStatus.Disabled"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>更新检查结果。</returns>
    public async Task<UpdateCheckResult> CheckAsync(string? updateCheckUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(updateCheckUrl))
        {
            return new UpdateCheckResult
            {
                Status = UpdateStatus.Disabled,
                CurrentVersion = GetCurrentVersion(),
                Message = "未配置更新源"
            };
        }

        if (!Uri.TryCreate(updateCheckUrl, UriKind.Absolute, out var urlUri) || urlUri.Scheme != Uri.UriSchemeHttps)
        {
            return new UpdateCheckResult
            {
                Status = UpdateStatus.Error,
                CurrentVersion = GetCurrentVersion(),
                Message = "更新源必须使用 HTTPS 地址。"
            };
        }

        string raw;
        try
        {
            var client = _clientFactory();
            using var response = await client.GetAsync(updateCheckUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult
                {
                    Status = UpdateStatus.Error,
                    CurrentVersion = GetCurrentVersion(),
                    Message = "检查失败"
                };
            }

            raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 离线 / DNS 解析失败 / 超时 / 任何网络异常：安静降级，不向上抛出。
            return new UpdateCheckResult
            {
                Status = UpdateStatus.Error,
                CurrentVersion = GetCurrentVersion(),
                Message = "检查失败"
            };
        }

        var result = Evaluate(raw, GetCurrentVersion());
        if (result.Status == UpdateStatus.UpdateAvailable &&
            (!Uri.TryCreate(result.DownloadUrl, UriKind.Absolute, out var downloadUri) ||
             downloadUri.Scheme != Uri.UriSchemeHttps ||
             !string.Equals(downloadUri.Host, urlUri.Host, StringComparison.OrdinalIgnoreCase)))
        {
            return new UpdateCheckResult
            {
                Status = UpdateStatus.Error,
                CurrentVersion = result.CurrentVersion,
                Message = "更新包必须来自与更新清单相同的 HTTPS 主机。"
            };
        }
        return result;
    }

    /// <summary>
    /// 纯逻辑：解析 <c>version.json</c> 文本并与给定当前版本比较。
    /// 该方法不触及网络，便于单元测试直接覆盖“坏 JSON / 版本比较”等路径。
    /// </summary>
    /// <param name="remoteJson">远端 version.json 文本。</param>
    /// <param name="currentVersion">当前版本（通常由 <see cref="GetCurrentVersion"/> 提供）。</param>
    /// <returns>更新检查结果；解析失败返回 <see cref="UpdateStatus.Error"/>（安静）。</returns>
    internal UpdateCheckResult Evaluate(string? remoteJson, Version currentVersion)
    {
        if (string.IsNullOrWhiteSpace(remoteJson))
        {
            return new UpdateCheckResult
            {
                Status = UpdateStatus.Error,
                CurrentVersion = currentVersion,
                Message = "检查失败"
            };
        }

        Version? latest;
        string? downloadUrl;
        string? sha256;
        DateTimeOffset? publishedAt;
        string? releaseNotes;
        try
        {
            using var doc = JsonDocument.Parse(remoteJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("version", out var versionElement))
            {
                return new UpdateCheckResult
                {
                    Status = UpdateStatus.Error,
                    CurrentVersion = currentVersion,
                    Message = "检查失败"
                };
            }

            var versionText = versionElement.GetString() ?? string.Empty;
            if (!TryParseVersion(versionText, out latest) || latest is null)
            {
                return new UpdateCheckResult
                {
                    Status = UpdateStatus.Error,
                    CurrentVersion = currentVersion,
                    Message = "检查失败"
                };
            }

            downloadUrl = root.TryGetProperty("url", out var urlElement)
                ? urlElement.GetString()
                : null;
            sha256 = root.TryGetProperty("sha256", out var shaElement)
                ? shaElement.GetString()
                : null;
            publishedAt = root.TryGetProperty("publishedAt", out var publishedElement)
                && DateTimeOffset.TryParse(publishedElement.GetString(), out var parsedPublishedAt)
                    ? parsedPublishedAt
                    : null;
            releaseNotes = root.TryGetProperty("releaseNotes", out var notesElement)
                ? notesElement.GetString()
                : null;
        }
        catch
        {
            // 坏 JSON / 字段类型不符：安静降级。
            return new UpdateCheckResult
            {
                Status = UpdateStatus.Error,
                CurrentVersion = currentVersion,
                Message = "检查失败"
            };
        }

        var comparison = CompareVersions(currentVersion, latest);
        if (comparison < 0)
        {
            return new UpdateCheckResult
            {
                Status = UpdateStatus.UpdateAvailable,
                CurrentVersion = currentVersion,
                LatestVersion = latest,
                DownloadUrl = downloadUrl,
                Sha256 = sha256,
                PublishedAt = publishedAt,
                ReleaseNotes = releaseNotes,
                Message = "发现新版本"
            };
        }

        return new UpdateCheckResult
        {
            Status = UpdateStatus.UpToDate,
            CurrentVersion = currentVersion,
            LatestVersion = latest,
            DownloadUrl = downloadUrl,
            Sha256 = sha256,
            PublishedAt = publishedAt,
            ReleaseNotes = releaseNotes,
            Message = "已是最新"
        };
    }

    /// <summary>
    /// 比较两个版本号。返回负数表示 <paramref name="a"/> 较旧，0 表示相等，正数表示较新。
    /// </summary>
    internal static int CompareVersions(Version a, Version b) => a.CompareTo(b);

    /// <summary>
    /// 尝试解析语义化版本字符串（支持 <c>x.y</c> / <c>x.y.z</c> / <c>x.y.z.w</c>）。
    /// </summary>
    internal static bool TryParseVersion(string? text, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return Version.TryParse(text.Trim(), out version);
    }
}
