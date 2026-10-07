using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Huaxiazi.Services;

public interface ILocalRuntimePrerequisiteService
{
    Task EnsureReadyAsync(CancellationToken cancellationToken = default);
}

/// <summary>Installs the pinned Microsoft x64 CRT only when managed llama.cpp needs it.</summary>
public sealed class LocalRuntimePrerequisiteService : ILocalRuntimePrerequisiteService, IDisposable
{
    public static Version RequiredVersion { get; } = new(14, 51, 36247, 0);
    public const string ManualInstallUri = "https://aka.ms/vc14/vc_redist.x64.exe";
    private const string InstallerSha256 = "843068991DAAA1F73AD9F6239BCE4D0F6A07A51F18C37EA2A867E9BECA71295C";
    private const long MaximumInstallerBytes = 64 * 1024 * 1024;
    private static readonly Uri InstallerUri = new("https://aka.ms/vs/18/release/14.51.36247/VC_Redist.x64.exe");
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Func<Version?> _installedVersionReader;
    private readonly Func<string, bool> _authenticodeVerifier;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<int>> _installerRunner;
    private readonly string _downloadDirectory;

    public LocalRuntimePrerequisiteService()
        : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromMinutes(3) },
            ReadInstalledVersion, VerifyMicrosoftSignature, RunInstallerAsync,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Huaxiazi", "downloads"),
            InstallerSha256, ownsHttpClient: true)
    {
    }

    internal LocalRuntimePrerequisiteService(HttpClient httpClient, Func<Version?> installedVersionReader,
        Func<string, bool> authenticodeVerifier,
        Func<ProcessStartInfo, CancellationToken, Task<int>> installerRunner,
        string downloadDirectory, string? expectedSha256 = null)
        : this(httpClient, installedVersionReader, authenticodeVerifier, installerRunner, downloadDirectory,
            expectedSha256 ?? InstallerSha256, ownsHttpClient: false)
    {
    }

    private LocalRuntimePrerequisiteService(HttpClient httpClient, Func<Version?> installedVersionReader,
        Func<string, bool> authenticodeVerifier, Func<ProcessStartInfo, CancellationToken, Task<int>> installerRunner,
        string downloadDirectory, string expectedSha256, bool ownsHttpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _installedVersionReader = installedVersionReader ?? throw new ArgumentNullException(nameof(installedVersionReader));
        _authenticodeVerifier = authenticodeVerifier ?? throw new ArgumentNullException(nameof(authenticodeVerifier));
        _installerRunner = installerRunner ?? throw new ArgumentNullException(nameof(installerRunner));
        _downloadDirectory = Path.GetFullPath(downloadDirectory);
        _ownsHttpClient = ownsHttpClient;
        if (expectedSha256.Length != 64 || !IsHex(expectedSha256))
            throw new ArgumentException("Expected SHA-256 must contain 64 hexadecimal characters.", nameof(expectedSha256));
        ExpectedSha256 = expectedSha256.ToUpperInvariant();
    }

    internal string ExpectedSha256 { get; }

    public async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("受管 llama.cpp 运行时只支持 Windows。");
        if (IsCurrentOrNewer(_installedVersionReader())) return;

        var installerPath = Path.Combine(_downloadDirectory, $"vc_redist.x64-{Guid.NewGuid():N}.exe");
        try
        {
            Directory.CreateDirectory(_downloadDirectory);
            await DownloadInstallerAsync(installerPath, cancellationToken).ConfigureAwait(false);
            var hash = await ComputeSha256Async(installerPath, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hash), Convert.FromHexString(ExpectedSha256)))
                throw new InvalidDataException("Microsoft Visual C++ Redistributable 安装包 SHA-256 校验失败；为保护系统，安装已停止。");
            if (!_authenticodeVerifier(installerPath))
                throw new InvalidDataException("Microsoft Visual C++ Redistributable 安装包的 Authenticode 签名无效；为保护系统，安装已停止。");

            int exitCode;
            try
            {
                exitCode = await _installerRunner(BuildInstallerStartInfo(installerPath), cancellationToken).ConfigureAwait(false);
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
            {
                throw new InvalidOperationException(ManualInstallMessage("你取消了 UAC 管理员授权"), exception);
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                throw new InvalidOperationException(ManualInstallMessage("无法启动 Microsoft 安装程序"), exception);
            }

            if (exitCode is not (0 or 3010) && !IsCurrentOrNewer(_installedVersionReader()))
                throw new InvalidOperationException(ManualInstallMessage($"安装程序退出代码为 {exitCode}"));
            if (!IsCurrentOrNewer(_installedVersionReader()))
                throw new InvalidOperationException(ManualInstallMessage("安装程序结束后仍未检测到所需运行库版本"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (InvalidDataException) { throw; }
        catch (InvalidOperationException) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException or OperationCanceledException)
        {
            throw new InvalidOperationException(ManualInstallMessage("下载或读取 Microsoft 安装包失败；请检查网络后重试"), exception);
        }
        finally
        {
            try { if (File.Exists(installerPath)) File.Delete(installerPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            try
            {
                if (Directory.Exists(_downloadDirectory) && Directory.GetFileSystemEntries(_downloadDirectory).Length == 0)
                    Directory.Delete(_downloadDirectory);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static ProcessStartInfo BuildInstallerStartInfo(string installerPath) => new()
    {
        FileName = installerPath,
        Arguments = "/install /passive /norestart",
        UseShellExecute = true,
        Verb = "runas",
        WindowStyle = ProcessWindowStyle.Normal
    };

    internal static bool IsCurrentOrNewer(Version? version) => version is not null && version >= RequiredVersion;

    private async Task DownloadInstallerAsync(string destinationPath, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, InstallerUri);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumInstallerBytes)
            throw new InvalidDataException("Microsoft 安装包大小超出安全限制。");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > MaximumInstallerBytes) throw new InvalidDataException("Microsoft 安装包大小超出安全限制。");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        if (total == 0) throw new InvalidDataException("Microsoft 安装服务返回空文件。");
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static Version? ReadInstalledVersion()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var runtime = machine.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64");
            if (runtime?.GetValue("Installed") is not int installed || installed != 1) return null;
            var value = runtime.GetValue("Version") as string;
            return Version.TryParse(value?.TrimStart('v', 'V'), out var version) ? version : null;
        }
        catch (System.Security.SecurityException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static bool VerifyMicrosoftSignature(string filePath)
    {
        try
        {
            if (!WinTrust.VerifyEmbeddedSignature(filePath)) return false;
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
            return string.Equals(certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
                "Microsoft Corporation", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static async Task<int> RunInstallerAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 Microsoft 安装程序。");
        // Once the user approves UAC, let the system installer finish before cleanup/recheck.
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        return process.ExitCode;
    }

    private static string ManualInstallMessage(string reason) =>
        $"本地模型需要 Microsoft Visual C++ x64 {RequiredVersion} 或更新版本；{reason}。本地推理已停止，没有切换到云端。你可以从微软官方页面手动安装：{ManualInstallUri}";

    private static bool IsHex(string value)
    {
        foreach (var character in value) if (!Uri.IsHexDigit(character)) return false;
        return true;
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
