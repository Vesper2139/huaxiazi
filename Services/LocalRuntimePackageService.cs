using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Huaxiazi.Services;

public enum LocalRuntimeFlavor { Cpu, Vulkan }

public sealed record LocalRuntimePackageDescriptor(
    string Version,
    LocalRuntimeFlavor Flavor,
    Uri DownloadUri,
    long SizeBytes,
    long ExpandedSizeBytes,
    string Sha256,
    string ReportedBuildCommit,
    string AttestationSourceCommit,
    bool IsPrerelease = true,
    bool CompilerSourceVerified = false)
{
    public string DisplayName => $"llama.cpp {Version} · {(Flavor == LocalRuntimeFlavor.Cpu ? "CPU" : "Vulkan")}";
    public string CompilerSourceCommit { get; init; } = string.Empty;
    public bool SupportsChatCompletionTokenCountEndpoint { get; init; }
}

public sealed record LocalRuntimePackageProgress(long ReceivedBytes, long TotalBytes)
{
    public double Fraction => TotalBytes <= 0 ? 0 : Math.Clamp((double)ReceivedBytes / TotalBytes, 0, 1);
}

public static class LocalRuntimePackageCatalog
{
    public static IReadOnlyList<LocalRuntimePackageDescriptor> Packages { get; } =
    [
        new("b11424", LocalRuntimeFlavor.Cpu,
            new Uri("https://github.com/ggml-org/llama.cpp/releases/download/b11424/llama-b11424-bin-win-cpu-x64.zip"),
            19_394_018, 49_485_085, "d613ef281e23e91b0c9cba171da421223b3b346c6b20ef4825230e08efacb5c7",
            "6c59c4007", "c06f84160a30c66d7b5a2829ae9b3ea15275cbc3", CompilerSourceVerified: true)
        { CompilerSourceCommit = "6c59c40076c00eab49754dc955d7652d93f9e125", SupportsChatCompletionTokenCountEndpoint = true },
        new("b11424", LocalRuntimeFlavor.Vulkan,
            new Uri("https://github.com/ggml-org/llama.cpp/releases/download/b11424/llama-b11424-bin-win-vulkan-x64.zip"),
            33_332_009, 94_790_429, "97de9ac35768f0eb85b88e8a6c4a1c409597fc4eb0d307a34cf36c2988f146e9",
            "6c59c4007", "c06f84160a30c66d7b5a2829ae9b3ea15275cbc3", CompilerSourceVerified: true)
        { CompilerSourceCommit = "6c59c40076c00eab49754dc955d7652d93f9e125", SupportsChatCompletionTokenCountEndpoint = true }
    ];

    public static bool SupportsChatCompletionTokenCountEndpoint(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return false;
        string fullPath;
        try { fullPath = Path.GetFullPath(executablePath); }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
        catch (PathTooLongException) { return false; }

        var versionDirectory = Directory.GetParent(fullPath);
        var versionsDirectory = versionDirectory?.Parent;
        var flavorDirectory = versionsDirectory?.Parent;
        if (!string.Equals(Path.GetFileName(fullPath), "llama-server.exe", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(versionsDirectory?.Name, "versions", StringComparison.OrdinalIgnoreCase) ||
            versionDirectory is null || flavorDirectory is null) return false;

        return Packages.Any(package => package.SupportsChatCompletionTokenCountEndpoint &&
            string.Equals(versionDirectory.Name, package.Version, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(flavorDirectory.Name, package.Flavor.ToString(), StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class LocalRuntimePackageService : IDisposable
{
    private const int MaxArchiveEntries = 2_000;
    private static readonly TimeSpan TransientSendRetryDelay = TimeSpan.FromMilliseconds(250);
    private readonly string _installRoot;
    private readonly string _licensePath;
    private readonly IReadOnlyList<(string SourcePath, string InstalledName)> _additionalLicenseFiles;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly IReadOnlyList<LocalRuntimePackageDescriptor> _catalog;
    private readonly Func<string, string, CancellationToken, Task> _executableProbe;

    public LocalRuntimePackageService(string? installRoot = null, HttpClient? client = null)
        : this(installRoot ?? LocalRuntimePaths.DefaultInstallRoot,
            Path.Combine(AppContext.BaseDirectory, "Resources", "Licenses", "llama.cpp-MIT.txt"),
            client, LocalRuntimePackageCatalog.Packages, ProbeExecutableAsync,
            [
                (Path.Combine(AppContext.BaseDirectory, "Resources", "Licenses", "Vulkan-Headers-LICENSE.md"), "LICENSE-Vulkan-Headers.md"),
                (Path.Combine(AppContext.BaseDirectory, "Resources", "Licenses", "Vulkan-Headers-Apache-2.0.txt"), "LICENSE-Vulkan-Headers-Apache-2.0.txt"),
                (Path.Combine(AppContext.BaseDirectory, "Resources", "Licenses", "SPIRV-Headers-LICENSE.txt"), "LICENSE-SPIRV-Headers.txt"),
                (Path.Combine(AppContext.BaseDirectory, "Resources", "Licenses", "llama-ui-third-party-notices.txt"), "THIRD-PARTY-NOTICES-llama-ui.txt")
            ]) { }

    internal LocalRuntimePackageService(string installRoot, string licensePath, HttpClient? client,
        IReadOnlyList<LocalRuntimePackageDescriptor> catalog,
        Func<string, string, CancellationToken, Task>? executableProbe = null,
        IReadOnlyList<(string SourcePath, string InstalledName)>? additionalLicenseFiles = null)
    {
        _installRoot = Path.GetFullPath(installRoot);
        _licensePath = Path.GetFullPath(licensePath);
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _executableProbe = executableProbe ?? ProbeExecutableAsync;
        _additionalLicenseFiles = additionalLicenseFiles?.Select(file =>
            (Path.GetFullPath(file.SourcePath), Path.GetFileName(file.InstalledName))).ToArray() ?? [];
        _ownsClient = client is null;
        _client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromMinutes(10) };
    }

    public IReadOnlyList<LocalRuntimePackageDescriptor> Packages => _catalog;

    public bool IsInstalled(LocalRuntimePackageDescriptor descriptor)
    {
        var executable = GetVersionedExecutablePath(_installRoot, descriptor.Flavor, descriptor.Version);
        if (!File.Exists(executable)) return false;
        var directory = Path.GetDirectoryName(executable)!;
        try
        {
            var manifest = JsonSerializer.Deserialize<InstalledRuntimeManifest>(
                File.ReadAllText(Path.Combine(directory, "huaxiazi-runtime.json")));
            return manifest is not null && manifest.Version == descriptor.Version &&
                manifest.Flavor == descriptor.Flavor.ToString() &&
                string.Equals(manifest.ArchiveSha256, descriptor.Sha256, StringComparison.OrdinalIgnoreCase) &&
                RequiredFiles(descriptor.Flavor).All(name => File.Exists(Path.Combine(directory, name)));
        }
        catch (IOException) { return false; }
        catch (JsonException) { return false; }
    }

    public async Task InstallAsync(LocalRuntimePackageDescriptor descriptor,
        IProgress<LocalRuntimePackageProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        EnsureCatalogMember(descriptor);
        if (IsInstalled(descriptor))
        {
            await SetCurrentVersionAsync(descriptor, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (!File.Exists(_licensePath)) throw new FileNotFoundException("运行时许可文件缺失，已停止安装。", _licensePath);
        foreach (var license in _additionalLicenseFiles)
            if (!File.Exists(license.SourcePath))
                throw new FileNotFoundException("运行时第三方许可文件缺失，已停止安装。", license.SourcePath);
        Directory.CreateDirectory(_installRoot);
        EnsureDiskSpace(_installRoot, checked(descriptor.SizeBytes + descriptor.ExpandedSizeBytes + 8L * 1024 * 1024));

        var downloads = Path.Combine(_installRoot, ".downloads");
        Directory.CreateDirectory(downloads);
        var archivePath = Path.Combine(downloads, $"{descriptor.Version}-{descriptor.Flavor.ToString().ToLowerInvariant()}.zip");
        await DownloadVerifiedArchiveAsync(descriptor, archivePath, progress, cancellationToken).ConfigureAwait(false);

        var flavorRoot = Path.Combine(_installRoot, descriptor.Flavor.ToString().ToLowerInvariant());
        var versionsRoot = Path.Combine(flavorRoot, "versions");
        var versionRoot = Path.Combine(versionsRoot, descriptor.Version);
        var stagingRoot = Path.Combine(versionsRoot, $".{descriptor.Version}-{Guid.NewGuid():N}.staging");
        Directory.CreateDirectory(versionsRoot);
        try
        {
            ExtractAndValidate(archivePath, stagingRoot, descriptor);
            await _executableProbe(Path.Combine(stagingRoot, "llama-server.exe"), descriptor.ReportedBuildCommit,
                cancellationToken).ConfigureAwait(false);
            File.Copy(_licensePath, Path.Combine(stagingRoot, "LICENSE-llama.cpp"), overwrite: false);
            foreach (var license in _additionalLicenseFiles)
                File.Copy(license.SourcePath, Path.Combine(stagingRoot, license.InstalledName), overwrite: false);
            var manifest = new InstalledRuntimeManifest(descriptor.Version, descriptor.Flavor.ToString(),
                descriptor.Sha256, descriptor.SizeBytes, descriptor.ReportedBuildCommit,
                descriptor.AttestationSourceCommit, descriptor.CompilerSourceCommit, descriptor.IsPrerelease, descriptor.CompilerSourceVerified,
                DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(Path.Combine(stagingRoot, "huaxiazi-runtime.json"),
                JsonSerializer.Serialize(manifest), cancellationToken).ConfigureAwait(false);
            if (Directory.Exists(versionRoot)) Directory.Delete(versionRoot, recursive: true);
            Directory.Move(stagingRoot, versionRoot);
            await SetCurrentVersionAsync(descriptor, cancellationToken).ConfigureAwait(false);
            File.Delete(archivePath);
        }
        finally
        {
            if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true);
        }
    }

    public async Task UninstallAsync(LocalRuntimePackageDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        EnsureCatalogMember(descriptor);
        var versionRoot = Path.GetDirectoryName(GetVersionedExecutablePath(_installRoot, descriptor.Flavor, descriptor.Version))!;
        var pointerPath = GetPointerPath(_installRoot, descriptor.Flavor);
        if (File.Exists(pointerPath))
        {
            var pointer = await ReadPointerAsync(pointerPath, cancellationToken).ConfigureAwait(false);
            if (string.Equals(pointer?.Version, descriptor.Version, StringComparison.OrdinalIgnoreCase))
                File.Delete(pointerPath);
        }
        if (Directory.Exists(versionRoot)) Directory.Delete(versionRoot, recursive: true);
    }

    public static string? ResolveInstalledExecutable(string runtimeRoot, LocalRuntimeFlavor flavor)
    {
        var flavorRoot = Path.Combine(Path.GetFullPath(runtimeRoot), flavor.ToString().ToLowerInvariant());
        var pointerPath = GetPointerPath(runtimeRoot, flavor);
        if (File.Exists(pointerPath))
        {
            try
            {
                var pointer = JsonSerializer.Deserialize<RuntimePointer>(File.ReadAllText(pointerPath));
                if (!string.IsNullOrWhiteSpace(pointer?.Version) && pointer.Version is not "." and not ".." &&
                    pointer.Version.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
                {
                    var versioned = GetVersionedExecutablePath(runtimeRoot, flavor, pointer.Version);
                    if (File.Exists(versioned)) return versioned;
                }
            }
            catch (IOException) { }
            catch (JsonException) { }
        }
        var legacy = Path.Combine(flavorRoot, "llama-server.exe");
        return File.Exists(legacy) ? legacy : null;
    }

    private async Task DownloadVerifiedArchiveAsync(LocalRuntimePackageDescriptor descriptor, string archivePath,
        IProgress<LocalRuntimePackageProgress>? progress, CancellationToken cancellationToken)
    {
        var currentLength = File.Exists(archivePath) ? new FileInfo(archivePath).Length : 0;
        if (currentLength > descriptor.SizeBytes) { File.Delete(archivePath); currentLength = 0; }
        if (currentLength == descriptor.SizeBytes)
        {
            if (await HasExpectedSha256Async(archivePath, descriptor.Sha256, cancellationToken).ConfigureAwait(false)) return;
            File.Delete(archivePath);
            currentLength = 0;
        }
        var downloadUri = descriptor.DownloadUri;
        var redirects = 0;
        var restartedFromZero = false;
        while (true)
        {
            using var response = await SendGetWithTransientRetryAsync(downloadUri, currentLength, cancellationToken).ConfigureAwait(false);
            var requestUri = response.RequestMessage?.RequestUri ?? downloadUri;
            if (IsRedirect(response.StatusCode))
            {
                if (redirects >= 3 || response.Headers.Location is null) throw new HttpRequestException("运行时下载重定向次数超限。");
                var redirected = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(requestUri, response.Headers.Location);
                if (redirected.Scheme != Uri.UriSchemeHttps || !IsTrustedDownloadHost(redirected.Host))
                    throw new HttpRequestException("运行时下载重定向到不受信任的地址。");
                // Keep only the final asset URL in memory; the request remains unauthenticated.
                redirects++;
                downloadUri = redirected;
                continue;
            }

            if (currentLength > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && !restartedFromZero)
            {
                File.Delete(archivePath);
                currentLength = 0;
                restartedFromZero = true;
                continue;
            }

            response.EnsureSuccessStatusCode();
            var append = currentLength > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (append && !HasExpectedContentRange(response.Content.Headers.ContentRange, currentLength, descriptor.SizeBytes))
            {
                File.Delete(archivePath);
                throw new InvalidDataException("运行时下载续传范围与请求偏移或清单大小不匹配，部分文件已删除。");
            }
            if (currentLength > 0 && !append) currentLength = 0;
            await using var output = new FileStream(archivePath, append ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[128 * 1024];
            var received = currentLength;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                received = checked(received + read);
                if (received > descriptor.SizeBytes) throw new InvalidDataException("运行时压缩包超过清单大小。");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                progress?.Report(new LocalRuntimePackageProgress(received, descriptor.SizeBytes));
            }
            if (received != descriptor.SizeBytes) throw new IOException("运行时下载未完成，已保留部分文件以便续传。");
            break;
        }
        if (!await HasExpectedSha256Async(archivePath, descriptor.Sha256, cancellationToken).ConfigureAwait(false))
        {
            File.Delete(archivePath);
            throw new InvalidDataException("运行时 SHA-256 校验失败，压缩包已删除且未安装。");
        }
    }

    private async Task<HttpResponseMessage> SendGetWithTransientRetryAsync(Uri uri, long currentLength,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (currentLength > 0) request.Headers.Range = new RangeHeaderValue(currentLength, null);
            try
            {
                return await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException exception) when (attempt == 0 && IsTransientSendFailure(exception))
            {
                await Task.Delay(TransientSendRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransientSendFailure(HttpRequestException exception) =>
        exception.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.ResponseEnded ||
        (exception.HttpRequestError == HttpRequestError.SecureConnectionError && HasInnerIOException(exception));

    private static bool HasInnerIOException(Exception exception)
    {
        for (var current = exception.InnerException; current is not null; current = current.InnerException)
            if (current is IOException) return true;
        return false;
    }

    private static bool HasExpectedContentRange(ContentRangeHeaderValue? range, long requestedOffset, long expectedTotal) =>
        range is { Unit: "bytes", From: not null, To: not null, Length: not null } &&
        range.From.Value == requestedOffset && range.To.Value >= range.From.Value &&
        range.To.Value < expectedTotal && range.Length.Value == expectedTotal;

    private static async Task<bool> HasExpectedSha256Async(string path, string expected, CancellationToken token)
    {
        byte[] actual;
        await using (var stream = File.OpenRead(path))
            actual = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
        try { return CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expected)); }
        catch (FormatException) { return false; }
    }

    private static async Task ProbeExecutableAsync(string executablePath, string expectedBuildCommit, CancellationToken token)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!
        };
        startInfo.ArgumentList.Add("--version");
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动本地运行时版本检查。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            try { await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false); } catch { }
            token.ThrowIfCancellationRequested();
            throw new TimeoutException("本地运行时未能在 15 秒内完成版本检查。");
        }
        var output = (await stdoutTask.ConfigureAwait(false)) + "\n" + (await stderrTask.ConfigureAwait(false));
        if (process.ExitCode != 0 || !output.Contains("version:", StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(expectedBuildCommit) &&
             !output.Contains(expectedBuildCommit, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("本地运行时启动检查失败，或报告的 build commit 与固定包清单不匹配。");
    }

    public static async Task ProbeInstalledExecutableAsync(string executablePath, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var manifestPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executablePath))!, "huaxiazi-runtime.json");
        string? expectedCommit = null;
        if (File.Exists(manifestPath))
        {
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, token).ConfigureAwait(false));
            if (manifest.RootElement.TryGetProperty("ReportedBuildCommit", out var buildCommit))
                expectedCommit = buildCommit.GetString();
        }
        await ProbeExecutableAsync(executablePath, expectedCommit ?? string.Empty, token).ConfigureAwait(false);
    }

    private static void ExtractAndValidate(string archivePath, string destination, LocalRuntimePackageDescriptor descriptor)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count is 0 or > MaxArchiveEntries) throw new InvalidDataException("运行时压缩包条目数量无效。");
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        Directory.CreateDirectory(destination);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(part => part is ".." or "."))
                throw new InvalidDataException("运行时压缩包包含越界路径。");
            var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixMode == 0xA000) throw new InvalidDataException("运行时压缩包不允许包含符号链接。");
            var target = Path.GetFullPath(Path.Combine(destination, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("运行时压缩包包含越界路径。");
            if (!seen.Add(target)) throw new InvalidDataException("运行时压缩包包含重复路径。");
            expanded = checked(expanded + entry.Length);
            if (expanded > descriptor.ExpandedSizeBytes) throw new InvalidDataException("运行时解压大小超过清单限制。");
            if (name.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
        if (expanded != descriptor.ExpandedSizeBytes) throw new InvalidDataException("运行时解压大小与清单不一致。");
        foreach (var required in RequiredFiles(descriptor.Flavor))
            if (!File.Exists(Path.Combine(destination, required))) throw new InvalidDataException($"运行时缺少必要文件：{required}");
    }

    private static IEnumerable<string> RequiredFiles(LocalRuntimeFlavor flavor)
    {
        yield return "llama-server.exe";
        yield return "llama-server-impl.dll";
        yield return "llama.dll";
        yield return "ggml-base.dll";
        yield return "LICENSE-LLVM-OpenMP";
        if (flavor == LocalRuntimeFlavor.Vulkan) yield return "ggml-vulkan.dll";
    }

    private async Task SetCurrentVersionAsync(LocalRuntimePackageDescriptor descriptor, CancellationToken token)
    {
        var flavorRoot = Path.Combine(_installRoot, descriptor.Flavor.ToString().ToLowerInvariant());
        Directory.CreateDirectory(flavorRoot);
        var pointerPath = GetPointerPath(_installRoot, descriptor.Flavor);
        var temporary = pointerPath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new RuntimePointer(descriptor.Version)), token).ConfigureAwait(false);
        File.Move(temporary, pointerPath, overwrite: true);
    }

    private void EnsureCatalogMember(LocalRuntimePackageDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!_catalog.Contains(descriptor) || descriptor.SizeBytes <= 0 || descriptor.ExpandedSizeBytes <= 0 ||
            descriptor.Sha256.Length != 64 || descriptor.DownloadUri.Scheme != Uri.UriSchemeHttps ||
            !IsTrustedDownloadHost(descriptor.DownloadUri.Host))
            throw new InvalidOperationException("运行时包未列入固定白名单，拒绝下载或安装。");
    }

    private static void EnsureDiskSpace(string path, long bytes)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
        if (drive.AvailableFreeSpace < bytes) throw new IOException("磁盘空间不足，未开始下载运行时。");
    }

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
    private static bool IsTrustedDownloadHost(string host) => host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    private static string GetPointerPath(string root, LocalRuntimeFlavor flavor) =>
        Path.Combine(Path.GetFullPath(root), flavor.ToString().ToLowerInvariant(), "current.json");
    private static string GetVersionedExecutablePath(string root, LocalRuntimeFlavor flavor, string version)
    {
        if (string.IsNullOrWhiteSpace(version) || version is "." or ".." ||
            version.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
            throw new ArgumentException("运行时版本标识无效。", nameof(version));
        return Path.Combine(Path.GetFullPath(root), flavor.ToString().ToLowerInvariant(), "versions", version, "llama-server.exe");
    }
    private static async Task<RuntimePointer?> ReadPointerAsync(string path, CancellationToken token) =>
        JsonSerializer.Deserialize<RuntimePointer>(await File.ReadAllTextAsync(path, token).ConfigureAwait(false));

    public void Dispose() { if (_ownsClient) _client.Dispose(); }

    private sealed record RuntimePointer(string Version);
    private sealed record InstalledRuntimeManifest(string Version, string Flavor, string ArchiveSha256, long ArchiveSize,
        string ReportedBuildCommit, string AttestationSourceCommit, string CompilerSourceCommit, bool IsPrerelease, bool CompilerSourceVerified,
        DateTimeOffset InstalledAtUtc);
}
