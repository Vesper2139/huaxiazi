using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

public enum LocalModelDownloadStatus
{
    Success,
    HashMismatch,
    SizeMismatch,
    Cancelled,
    Error
}

public sealed class LocalModelDownloadResult
{
    public LocalModelDownloadStatus Status { get; init; }
    public InstalledLocalModel? InstalledModel { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed record LocalModelDownloadProgress(long ReceivedBytes, long TotalBytes)
{
    public double Fraction => TotalBytes <= 0 ? 0 : Math.Clamp((double)ReceivedBytes / TotalBytes, 0, 1);
}

public sealed class LocalModelDownloadService
{
    private const long MaximumModelBytes = 64L * 1024 * 1024 * 1024;
    private const int MaximumRedirects = 3;
    private readonly LocalModelStore _store;
    private readonly HttpClient _client;
    private readonly TimeSpan _downloadTimeout;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public LocalModelDownloadService(LocalModelStore store, HttpClient? client = null, TimeSpan? downloadTimeout = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _downloadTimeout = downloadTimeout ?? TimeSpan.FromHours(6);
        if (_downloadTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(downloadTimeout));
    }

    public async Task<LocalModelDownloadResult> DownloadAsync(
        LocalModelDescriptor descriptor,
        IProgress<LocalModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_downloadTimeout);
        cancellationToken = timeoutSource.Token;
        if (!TryValidateDescriptor(descriptor, out var sourceUri, out var validationError))
            return Error(validationError);

        var artifactKey = SafeSegment(descriptor.Id) + "@" + SafeSegment(descriptor.Version);
        var downloadDirectory = Path.Combine(_store.Root, "downloads");
        var partialPath = Path.Combine(downloadDirectory, artifactKey + ".partial");
        var resumePath = Path.Combine(downloadDirectory, artifactKey + ".resume.json");
        Directory.CreateDirectory(downloadDirectory);

        try
        {
            var resume = LoadResume(resumePath);
            long existingLength = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
            if (existingLength > 0 && (resume is null || !resume.Matches(descriptor)))
            {
                DeleteIfExists(partialPath);
                DeleteIfExists(resumePath);
                existingLength = 0;
                resume = null;
            }
            if (existingLength > descriptor.SizeBytes)
            {
                DeleteIfExists(partialPath);
                DeleteIfExists(resumePath);
                existingLength = 0;
                resume = null;
            }

            EnsureDiskSpace(downloadDirectory, descriptor.SizeBytes - existingLength);
            using var response = await SendWithRedirectsAsync(
                sourceUri!, descriptor.AllowedRedirectHosts, existingLength, resume?.ETag, cancellationToken).ConfigureAwait(false);

            if (existingLength > 0 && response.StatusCode != HttpStatusCode.PartialContent)
            {
                DeleteIfExists(partialPath);
                DeleteIfExists(resumePath);
                return await DownloadAsync(descriptor, progress, cancellationToken).ConfigureAwait(false);
            }
            response.EnsureSuccessStatusCode();
            if (existingLength > 0 && response.Content.Headers.ContentRange is { From: { } from, Length: { } length } &&
                (from != existingLength || length != descriptor.SizeBytes))
            {
                DeleteIfExists(partialPath);
                DeleteIfExists(resumePath);
                return Error("服务器返回的断点范围与模型清单不一致。");
            }

            var etag = response.Headers.ETag?.Tag ?? resume?.ETag ?? string.Empty;
            SaveResume(resumePath, new DownloadResumeMetadata
            {
                Id = descriptor.Id,
                Version = descriptor.Version,
                Url = descriptor.DownloadUrl,
                Sha256 = descriptor.Sha256,
                SizeBytes = descriptor.SizeBytes,
                ETag = etag
            });

            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(
                partialPath,
                existingLength == 0 ? FileMode.Create : FileMode.Append,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[81920];
                var received = existingLength;
                progress?.Report(new LocalModelDownloadProgress(received, descriptor.SizeBytes));
                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;
                    if (received > descriptor.SizeBytes || received > MaximumModelBytes)
                        throw new InvalidDataException("模型下载大小超过签名清单声明值。");
                    progress?.Report(new LocalModelDownloadProgress(received, descriptor.SizeBytes));
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var actualLength = new FileInfo(partialPath).Length;
            if (actualLength != descriptor.SizeBytes)
                return new LocalModelDownloadResult { Status = LocalModelDownloadStatus.SizeMismatch, Message = "模型下载尚未完成，可稍后继续。" };
            var actualHash = await ComputeSha256Async(partialPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actualHash, descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                DeleteIfExists(partialPath);
                DeleteIfExists(resumePath);
                return new LocalModelDownloadResult { Status = LocalModelDownloadStatus.HashMismatch, Message = "模型 SHA-256 校验失败，未安装。" };
            }
            await ValidateGgufAsync(partialPath, cancellationToken).ConfigureAwait(false);

            var finalDirectory = Path.Combine(_store.Root, "base", SafeSegment(descriptor.Id), SafeSegment(descriptor.Version));
            var finalPath = Path.Combine(finalDirectory, "model.gguf");
            Directory.CreateDirectory(finalDirectory);
            File.Move(partialPath, finalPath, true);
            if (!await HasExpectedSha256Async(finalPath, descriptor.Sha256, cancellationToken).ConfigureAwait(false))
            {
                DeleteIfExists(finalPath);
                throw new InvalidDataException("模型安装后的 SHA-256 校验失败，未安装。");
            }
            DeleteIfExists(resumePath);
            var installed = new InstalledLocalModel
            {
                InstallationId = descriptor.Id + "@" + descriptor.Version,
                CatalogId = descriptor.Id,
                Version = descriptor.Version,
                DisplayName = descriptor.DisplayName,
                FilePath = finalPath,
                Sha256 = actualHash,
                SizeBytes = actualLength,
                IsUserImported = false,
                State = LocalModelState.Available
            };
            _store.RegisterInstalledModel(installed);
            progress?.Report(new LocalModelDownloadProgress(descriptor.SizeBytes, descriptor.SizeBytes));
            return new LocalModelDownloadResult
            {
                Status = LocalModelDownloadStatus.Success,
                InstalledModel = installed,
                Message = "模型下载、校验并安装完成。"
            };
        }
        catch (OperationCanceledException)
        {
            return new LocalModelDownloadResult { Status = LocalModelDownloadStatus.Cancelled, Message = "模型下载已暂停，可稍后继续。" };
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Error(exception is InvalidDataException ? exception.Message : "模型下载失败，可稍后继续。");
        }
    }

    private async Task<HttpResponseMessage> SendWithRedirectsAsync(
        Uri initialUri,
        IReadOnlyCollection<string> allowedRedirectHosts,
        long existingLength,
        string? etag,
        CancellationToken cancellationToken)
    {
        var uri = initialUri;
        var allowedHosts = new HashSet<string>(allowedRedirectHosts ?? [], StringComparer.OrdinalIgnoreCase)
        {
            initialUri.IdnHost
        };
        for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (existingLength > 0)
            {
                request.Headers.Range = new RangeHeaderValue(existingLength, null);
                if (!string.IsNullOrWhiteSpace(etag)) request.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue(etag));
            }
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is < 300 or >= 400) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new HttpRequestException("模型下载重定向缺少目标地址。");
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
            if (uri.Scheme != Uri.UriSchemeHttps || !allowedHosts.Contains(uri.IdnHost))
                throw new HttpRequestException("模型下载重定向目标不在签名目录允许列表中。");
        }
        throw new HttpRequestException("模型下载重定向次数过多。");
    }

    private DownloadResumeMetadata? LoadResume(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<DownloadResumeMetadata>(File.ReadAllText(path), _jsonOptions)
                : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }

    private void SaveResume(string path, DownloadResumeMetadata metadata)
    {
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(metadata, _jsonOptions));
        File.Move(temporaryPath, path, true);
    }

    private static bool TryValidateDescriptor(LocalModelDescriptor descriptor, out Uri? uri, out string error)
    {
        uri = null;
        error = string.Empty;
        if (!Uri.TryCreate(descriptor.DownloadUrl, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            error = "模型下载地址必须使用 HTTPS。";
            return false;
        }
        if (descriptor.SizeBytes is <= 4 or > MaximumModelBytes || descriptor.Sha256.Length != 64 || !descriptor.Sha256.All(Uri.IsHexDigit))
        {
            error = "模型清单大小或 SHA-256 无效。";
            return false;
        }
        return true;
    }

    private static void EnsureDiskSpace(string directory, long requiredBytes)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(directory));
        if (string.IsNullOrWhiteSpace(root)) throw new IOException("无法确定模型目录所在磁盘。");
        var drive = new DriveInfo(root);
        var reserve = Math.Max(64L * 1024 * 1024, requiredBytes / 20);
        if (drive.AvailableFreeSpace < requiredBytes + reserve)
            throw new IOException("磁盘空间不足，无法下载模型。");
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    internal static async Task<bool> HasExpectedSha256Async(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return false;
        var actual = await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
        return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task ValidateGgufAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4, FileOptions.Asynchronous);
        var magic = new byte[4];
        if (await stream.ReadAsync(magic.AsMemory(), cancellationToken).ConfigureAwait(false) != 4 ||
            magic[0] != 'G' || magic[1] != 'G' || magic[2] != 'U' || magic[3] != 'F')
            throw new InvalidDataException("下载文件不是有效的 GGUF 模型。");
    }

    private static string SafeSegment(string value)
    {
        var safe = new string(value.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.').ToArray());
        if (string.IsNullOrWhiteSpace(safe) || safe is "." or "..") throw new InvalidDataException("模型标识包含无效字符。");
        return safe;
    }

    private static void DeleteIfExists(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static LocalModelDownloadResult Error(string message) => new()
    {
        Status = LocalModelDownloadStatus.Error,
        Message = message
    };

    private sealed class DownloadResumeMetadata
    {
        public string Id { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public string ETag { get; set; } = string.Empty;

        public bool Matches(LocalModelDescriptor descriptor) =>
            string.Equals(Id, descriptor.Id, StringComparison.Ordinal) &&
            string.Equals(Version, descriptor.Version, StringComparison.Ordinal) &&
            string.Equals(Url, descriptor.DownloadUrl, StringComparison.Ordinal) &&
            string.Equals(Sha256, descriptor.Sha256, StringComparison.OrdinalIgnoreCase) &&
            SizeBytes == descriptor.SizeBytes;
    }
}
