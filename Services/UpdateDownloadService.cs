using System;
using System.IO;
using System.Net.Http;
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
    public bool Verify(string filePath, out string? publisher)
    {
        publisher = null;
        try
        {
            // CreateFromSignedFile 要求 PE 带 Authenticode 签名；随后用系统证书链
            // 验证完整性与信任链。发布者名称只作为诊断，不写入日志或用户配置。
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
            publisher = certificate.GetNameInfo(X509NameType.SimpleName, false);
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
            return chain.Build(certificate) &&
                   !string.IsNullOrWhiteSpace(publisher) &&
                   publisher.Contains("Huaxiazi", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
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
    private readonly HttpClient _client;
    private readonly IAuthenticodeVerifier _signatureVerifier;

    public UpdateDownloadService() : this(new HttpClient { Timeout = TimeSpan.FromMinutes(10) }, new AuthenticodeVerifier())
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
}
