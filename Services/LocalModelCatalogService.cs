using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

public sealed class LocalModelCatalogService
{
    private const int MaximumCatalogBytes = 2 * 1024 * 1024;
    private readonly Func<string, string?> _signatureVerifier;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Immutable in-app snapshot of the curated open-weight GGUF downloads.
    /// The URLs, sizes and SHA-256 values are pinned so a click can download and
    /// install without requiring a separately configured catalog endpoint.
    /// </summary>
    public static LocalModelCatalog BuiltInCatalog => new()
    {
        SchemaVersion = 1,
        CatalogVersion = "builtin-2026.10",
        Models =
        [
            new LocalModelDescriptor
            {
                Id = "qwen3-1.7b-q4-k-m",
                Version = "2026.09",
                DisplayName = "Qwen3 1.7B Q4_K_M（轻量）",
                Tier = "Light",
                FileName = "Qwen_Qwen3-1.7B-Q4_K_M.gguf",
                DownloadUrl = "https://huggingface.co/bartowski/Qwen_Qwen3-1.7B-GGUF/resolve/main/Qwen_Qwen3-1.7B-Q4_K_M.gguf?download=true",
                SizeBytes = 1282439584,
                Sha256 = "72c5c3cb38fa32d5256e2fe30d03e7a64c6c79e668ad84057e3bd66e250b24fb",
                ContextTokens = 32768,
                MinimumMemoryBytes = 4L * 1024 * 1024 * 1024,
                LicenseId = "Apache-2.0",
                LicenseUrl = "https://www.apache.org/licenses/LICENSE-2.0",
                RuntimeVersion = "llama.cpp",
                AllowedRedirectHosts = ["cdn-lfs.huggingface.co", "cas-bridge.xethub.hf.co", "hf.co"]
            },
            new LocalModelDescriptor
            {
                Id = "qwen3-4b-instruct-2507-q4-k-m",
                Version = "2026.09",
                DisplayName = "Qwen3 4B Instruct 2507 Q4_K_M（标准）",
                Tier = "Standard",
                FileName = "qwen3-4b-instruct-2507-Q4_K_M.gguf",
                DownloadUrl = "https://huggingface.co/Open4bits/Qwen3-4B-Instruct-2507-GGUF/resolve/main/qwen3-4b-instruct-2507-Q4_K_M.gguf?download=true",
                SizeBytes = 2497279136,
                Sha256 = "1571ec5115bcfed4b4327fc27b5f44ea284806caf5331eef89326191c9b031d6",
                ContextTokens = 32768,
                MinimumMemoryBytes = 8L * 1024 * 1024 * 1024,
                LicenseId = "Apache-2.0",
                LicenseUrl = "https://www.apache.org/licenses/LICENSE-2.0",
                RuntimeVersion = "llama.cpp",
                AllowedRedirectHosts = ["cdn-lfs.huggingface.co", "cas-bridge.xethub.hf.co", "hf.co"]
            },
            new LocalModelDescriptor
            {
                Id = "qwen3-4b-q4-k-m",
                Version = "2026.10",
                DisplayName = "Qwen3 4B Q4_K_M（官方量化）",
                Tier = "Standard",
                FileName = "Qwen3-4B-Q4_K_M.gguf",
                DownloadUrl = "https://huggingface.co/Qwen/Qwen3-4B-GGUF/resolve/bc640142c66e1fdd12af0bd68f40445458f3869b/Qwen3-4B-Q4_K_M.gguf?download=true",
                SizeBytes = 2_497_280_256,
                Sha256 = "7485fe6f11af29433bc51cab58009521f205840f5b4ae3a32fa7f92e8534fdf5",
                ContextTokens = 32768,
                MinimumMemoryBytes = 8L * 1024 * 1024 * 1024,
                LicenseId = "Apache-2.0",
                LicenseUrl = "https://www.apache.org/licenses/LICENSE-2.0",
                RuntimeVersion = "b11424",
                AllowedRedirectHosts = ["cdn-lfs.huggingface.co", "cas-bridge.xethub.hf.co", "hf.co"]
            },
            new LocalModelDescriptor
            {
                Id = "qwen3-8b-q4-k-m",
                Version = "2026.09",
                DisplayName = "Qwen3 8B Q4_K_M（高质量）",
                Tier = "High",
                FileName = "Qwen_Qwen3-8B-Q4_K_M.gguf",
                DownloadUrl = "https://huggingface.co/bartowski/Qwen_Qwen3-8B-GGUF/resolve/main/Qwen_Qwen3-8B-Q4_K_M.gguf?download=true",
                SizeBytes = 5027784224,
                Sha256 = "54fffa050078e984116639c83dfb64b5aa6d4cd474e018b076777c632bbccccd",
                ContextTokens = 32768,
                MinimumMemoryBytes = 12L * 1024 * 1024 * 1024,
                LicenseId = "Apache-2.0",
                LicenseUrl = "https://www.apache.org/licenses/LICENSE-2.0",
                RuntimeVersion = "llama.cpp",
                AllowedRedirectHosts = ["cdn-lfs.huggingface.co", "cas-bridge.xethub.hf.co", "hf.co"]
            }
        ]
    };

    public LocalModelCatalogService()
        : this(UpdateManifestSignature.VerifyAndExtractWithEmbeddedKey)
    {
    }

    internal LocalModelCatalogService(Func<string, string?> signatureVerifier)
    {
        _signatureVerifier = signatureVerifier ?? throw new ArgumentNullException(nameof(signatureVerifier));
    }

    public static bool IsSafeDirectDownloadLink(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        !string.IsNullOrWhiteSpace(uri.Host);

    public async Task<LocalModelCatalog> FetchAsync(string catalogUrl, HttpClient httpClient, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(catalogUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("本地模型目录地址必须使用 HTTPS。");
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumCatalogBytes)
            throw new InvalidDataException("本地模型目录超过允许大小。");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > MaximumCatalogBytes) throw new InvalidDataException("本地模型目录超过允许大小。");
            buffer.Write(chunk, 0, read);
        }
        return ParseVerifiedCatalog(Encoding.UTF8.GetString(buffer.ToArray()));
    }

    public LocalModelCatalog ParseVerifiedCatalog(string signedEnvelope)
    {
        var payload = _signatureVerifier(signedEnvelope);
        if (payload is null) throw new InvalidDataException("本地模型目录签名无效。");
        if (Encoding.UTF8.GetByteCount(payload) > MaximumCatalogBytes)
            throw new InvalidDataException("本地模型目录超过允许大小。");
        LocalModelCatalog catalog;
        try
        {
            catalog = JsonSerializer.Deserialize<LocalModelCatalog>(payload, _jsonOptions)
                ?? throw new InvalidDataException("本地模型目录为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("本地模型目录格式无效。", exception);
        }
        Validate(catalog);
        return catalog;
    }

    private static void Validate(LocalModelCatalog catalog)
    {
        if (catalog.SchemaVersion != 1) throw new InvalidDataException("不支持的本地模型目录版本。");
        if (string.IsNullOrWhiteSpace(catalog.CatalogVersion)) throw new InvalidDataException("本地模型目录缺少版本号。");
        catalog.Models ??= [];
        catalog.Adapters ??= [];
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in catalog.Models)
        {
            if (string.IsNullOrWhiteSpace(model.Id) || string.IsNullOrWhiteSpace(model.Version) ||
                string.IsNullOrWhiteSpace(model.DisplayName) || !ids.Add(model.Id + "@" + model.Version))
                throw new InvalidDataException("本地模型目录包含无效或重复的模型标识。");
            if (!IsSafeDirectDownloadLink(model.DownloadUrl))
                throw new InvalidDataException("本地模型下载地址必须使用 HTTPS。");
            if (!IsSha256(model.Sha256) || model.SizeBytes <= 4)
                throw new InvalidDataException("本地模型缺少有效的文件大小或 SHA-256。");
            if (!string.Equals(Path.GetExtension(model.FileName), ".gguf", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(model.FileName) != model.FileName)
                throw new InvalidDataException("本地模型文件名必须是安全的 GGUF 文件名。");
            if (string.IsNullOrWhiteSpace(model.LicenseId) ||
                !Uri.TryCreate(model.LicenseUrl, UriKind.Absolute, out var licenseUri) || licenseUri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("本地模型缺少有效的许可证信息。");
            if (string.IsNullOrWhiteSpace(model.RuntimeVersion) || model.ContextTokens < 512 || model.MinimumMemoryBytes <= 0)
                throw new InvalidDataException("本地模型缺少运行时兼容或硬件要求。");
            model.AllowedRedirectHosts ??= [];
            if (model.AllowedRedirectHosts.Any(host => string.IsNullOrWhiteSpace(host) || host.Contains('/') || host.Contains(':')))
                throw new InvalidDataException("模型目录包含无效的重定向主机。");
        }
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);
}
