using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Huaxiazi.Services;

public enum UpdateDownloadStatus
{
    Success,
    HashMismatch,
    SignatureInvalid,
    Cancelled,
    Error
}

/// <summary>安装包发布者校验抽象，便于隔离测试且不允许仅凭哈希执行未知文件。</summary>
internal interface IAuthenticodeVerifier
{
    bool Verify(string filePath, out string? publisher);
}

internal sealed class AuthenticodeVerifier : IAuthenticodeVerifier
{
    private readonly string _trustedCertificateSha256;

    internal AuthenticodeVerifier()
    {
        _trustedCertificateSha256 = typeof(AuthenticodeVerifier).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "UpdateSignerCertificateSha256")?.Value ?? string.Empty;
    }

    public bool Verify(string filePath, out string? publisher)
    {
        publisher = null;
        try
        {
            if (string.IsNullOrWhiteSpace(_trustedCertificateSha256) ||
                !WinTrust.VerifyEmbeddedSignature(filePath)) return false;
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
            publisher = certificate.GetNameInfo(X509NameType.SimpleName, false);
            return CertificateMatchesPin(certificate, _trustedCertificateSha256);
        }
        catch
        {
            return false;
        }
    }

    internal static bool CertificateMatchesPin(X509Certificate2 certificate, string trustedCertificateSha256)
    {
        if (certificate is null || string.IsNullOrWhiteSpace(trustedCertificateSha256)) return false;
        var normalized = trustedCertificateSha256.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace(":", string.Empty, StringComparison.Ordinal);
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character))) return false;
        var actual = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(actual), Convert.FromHexString(normalized));
    }
}

public sealed class UpdateDownloadResult
{
    public UpdateDownloadStatus Status { get; init; }
    public string? FilePath { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// 下载更新安装包并在落盘前验证 SHA-256 与 Authenticode 发布者签名。
/// 所有校验成功前只保留不可执行的临时文件。
/// </summary>
public sealed class UpdateDownloadService
{
    private const long MaximumDownloadBytes = 512L * 1024 * 1024;
    private readonly HttpClient _client;
    private readonly IAuthenticodeVerifier _signatureVerifier;

    public UpdateDownloadService() : this(
        new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) },
        new AuthenticodeVerifier())
    {
    }

    public UpdateDownloadService(HttpClient client) : this(client, new AuthenticodeVerifier())
    {
    }

    internal UpdateDownloadService(HttpClient client, IAuthenticodeVerifier signatureVerifier)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _signatureVerifier = signatureVerifier ?? throw new ArgumentNullException(nameof(signatureVerifier));
    }

    public async Task<UpdateDownloadResult> DownloadAsync(
        string downloadUrl,
        string expectedSha256,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var temporaryPath = destinationPath + ".download";
        try
        {
            if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps)
            {
                return Error("下载地址必须使用 HTTPS 协议。");
            }
            if (!IsValidSha256(expectedSha256))
            {
                return Error("更新清单缺少有效的 SHA-256。");
            }

            var directory = Path.GetDirectoryName(destinationPath);
            if (string.IsNullOrWhiteSpace(directory)) return Error("下载目录无效。");
            Directory.CreateDirectory(directory);
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);

            using var response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            if (total is > MaximumDownloadBytes)
                throw new DownloadTooLargeException();
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long received = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                received += read;
                if (received > MaximumDownloadBytes)
                    throw new DownloadTooLargeException();
                if (total is > 0) progress?.Report((double)received / total.Value);
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Close();

            var actual = Convert.ToHexString(hash.GetHashAndReset());
            if (!actual.Equals(expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(temporaryPath);
                return new UpdateDownloadResult
                {
                    Status = UpdateDownloadStatus.HashMismatch,
                    Message = "安装包校验失败，文件已删除。"
                };
            }

            if (!_signatureVerifier.Verify(temporaryPath, out _))
            {
                DeleteTemporaryFile(temporaryPath);
                return new UpdateDownloadResult
                {
                    Status = UpdateDownloadStatus.SignatureInvalid,
                    Message = "安装包未通过话匣子发布者签名校验，文件已删除。"
                };
            }

            File.Move(temporaryPath, destinationPath, true);
            progress?.Report(1);
            return new UpdateDownloadResult
            {
                Status = UpdateDownloadStatus.Success,
                FilePath = destinationPath,
                Message = "下载与校验完成。"
            };
        }
        catch (DownloadTooLargeException)
        {
            DeleteTemporaryFile(temporaryPath);
            return Error("安装包超过 512 MB 大小上限，已停止下载。");
        }
        catch (OperationCanceledException)
        {
            DeleteTemporaryFile(temporaryPath);
            return new UpdateDownloadResult { Status = UpdateDownloadStatus.Cancelled, Message = "下载已取消。" };
        }
        catch
        {
            DeleteTemporaryFile(temporaryPath);
            return Error("下载失败。");
        }
    }

    private static bool IsValidSha256(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length != 64) return false;
        foreach (var character in value.Trim())
        {
            if (!Uri.IsHexDigit(character)) return false;
        }
        return true;
    }

    private static void DeleteTemporaryFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private static UpdateDownloadResult Error(string message) => new()
    {
        Status = UpdateDownloadStatus.Error,
        Message = message
    };

    private sealed class DownloadTooLargeException : Exception;
}
