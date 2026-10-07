using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Huaxiazi.Models;
using Huaxiazi.Services;

namespace Huaxiazi.BlindEvaluationRunner;

/// <summary>Content-free identity of the exact local model and runtime binaries used in a baseline.</summary>
public sealed record BlindLocalArtifactProvenance(
    string ModelInstallationId,
    string ModelVersion,
    long ModelSizeBytes,
    string ModelSha256,
    string? AdapterInstallationId,
    string? AdapterSha256,
    string RuntimeFlavor,
    string? RuntimeFileVersion,
    string RuntimeSha256);

public static class BlindLocalArtifactProvenanceResolver
{
    public static BlindLocalArtifactProvenance Resolve(ProviderProfile profile, string modelRoot, string runtimeRoot)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        if (profile.Platform != ProviderPlatform.ManagedLocal || profile.Type != ProviderType.Local)
            throw new InvalidOperationException("本地制品 provenance 只适用于 ManagedLocal 候选。");
        if (string.IsNullOrWhiteSpace(profile.LocalModelInstallationId))
            throw new InvalidOperationException("ManagedLocal 候选缺少模型 installation ID。");

        var store = new LocalModelStore(modelRoot);
        var model = store.GetInstalledModels().SingleOrDefault(item => item.InstallationId == profile.LocalModelInstallationId)
            ?? throw new InvalidOperationException("ManagedLocal 候选引用的模型未在指定模型目录中安装。");
        var modelFile = VerifyRegisteredFile(model.FilePath, model.Sha256, model.SizeBytes, "模型");
        var modelVersion = RequiredMetadata(model.Version, "模型版本");

        string? adapterInstallationId = null;
        string? adapterSha256 = null;
        if (!string.IsNullOrWhiteSpace(profile.LocalAdapterInstallationId))
        {
            var adapter = store.GetInstalledAdapters().SingleOrDefault(item => item.InstallationId == profile.LocalAdapterInstallationId)
                ?? throw new InvalidOperationException("ManagedLocal 候选引用的适配器未在指定模型目录中安装。");
            if (!string.Equals(model.Sha256, adapter.BaseModelSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("本地 LoRA 适配器的基座 SHA-256 与模型不匹配。");
            VerifyRegisteredFile(adapter.FilePath, adapter.Sha256, expectedSize: null, "LoRA 适配器");
            adapterInstallationId = adapter.InstallationId;
            adapterSha256 = NormalizeSha256(adapter.Sha256, "LoRA 适配器注册 SHA-256");
        }

        var resolvedRuntimeRoot = Path.GetFullPath(runtimeRoot);
        var vulkanPath = Path.Combine(resolvedRuntimeRoot, "vulkan", "llama-server.exe");
        var cpuPath = Path.Combine(resolvedRuntimeRoot, "cpu", "llama-server.exe");
        var runtimeFlavor = File.Exists(vulkanPath) ? "vulkan" : File.Exists(cpuPath) ? "cpu" : null;
        if (runtimeFlavor is null)
            throw new FileNotFoundException("指定的本地运行时目录不包含 cpu/vulkan/llama-server.exe。", resolvedRuntimeRoot);
        var runtimePath = runtimeFlavor == "vulkan" ? vulkanPath : cpuPath;

        var runtimeSha256 = HashFile(runtimePath);
        var fileVersion = FileVersionInfo.GetVersionInfo(runtimePath).ProductVersion;
        if (string.IsNullOrWhiteSpace(fileVersion)) fileVersion = FileVersionInfo.GetVersionInfo(runtimePath).FileVersion;
        if (string.IsNullOrWhiteSpace(fileVersion)) fileVersion = null;

        return new BlindLocalArtifactProvenance(
            model.InstallationId,
            modelVersion,
            modelFile.Length,
            model.Sha256.ToLowerInvariant(),
            adapterInstallationId,
            adapterSha256,
            runtimeFlavor,
            fileVersion,
            runtimeSha256);
    }

    private static FileInfo VerifyRegisteredFile(string path, string expectedSha256, long? expectedSize, string label)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"已登记的{label}文件不存在。", path);
        var file = new FileInfo(path);
        if (expectedSize.HasValue && file.Length != expectedSize.Value)
            throw new InvalidDataException($"{label}文件大小与注册信息不一致。");
        var actualHash = HashFile(path);
        if (!string.Equals(actualHash, NormalizeSha256(expectedSha256, $"{label}注册 SHA-256"), StringComparison.Ordinal))
            throw new InvalidDataException($"{label}文件 SHA-256 与本地注册信息不一致。");
        return file;
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string NormalizeSha256(string value, string label)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException($"{label} 无效。");
        return value.ToLowerInvariant();
    }

    private static string RequiredMetadata(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException($"{label} 未登记。");
        return value.Trim();
    }
}
