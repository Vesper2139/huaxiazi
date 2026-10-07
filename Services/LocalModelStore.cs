using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

public sealed class LocalModelStore
{
    private static readonly byte[] GgufMagic = [(byte)'G', (byte)'G', (byte)'U', (byte)'F'];
    private readonly string _root;
    private readonly string _registryPath;
    private readonly Action<Exception>? _registryErrorSink;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public LocalModelStore(string? root = null, Action<Exception>? registryErrorSink = null)
    {
        _root = Path.GetFullPath(root ?? DefaultRoot);
        _registryPath = Path.Combine(_root, "registry.json");
        _registryErrorSink = registryErrorSink;
    }

    public static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Huaxiazi", "models");

    public string Root => _root;
    public Exception? LastRegistryLoadError { get; private set; }

    public IReadOnlyList<InstalledLocalModel> GetInstalledModels() => LoadRegistry().Models
        .Where(model => IsManagedPath(model.FilePath))
        .ToList();

    public IReadOnlyList<InstalledLocalAdapter> GetInstalledAdapters() => LoadRegistry().Adapters
        .Where(adapter => IsManagedPath(adapter.FilePath))
        .ToList();

    public async Task<InstalledLocalModel> ImportModelAsync(
        string sourcePath,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!string.Equals(Path.GetExtension(sourcePath), ".gguf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("仅支持导入 GGUF 模型文件。");
        await ValidateGgufAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var sha256 = await ComputeSha256Async(sourcePath, cancellationToken).ConfigureAwait(false);
        var destinationDirectory = Path.Combine(_root, "imports", sha256);
        var destinationPath = Path.Combine(destinationDirectory, "model.gguf");
        EnsureManagedPath(destinationPath);
        Directory.CreateDirectory(destinationDirectory);
        await CopyAtomicallyAsync(sourcePath, destinationPath, cancellationToken).ConfigureAwait(false);

        var model = new InstalledLocalModel
        {
            InstallationId = "import-" + sha256[..16],
            CatalogId = string.Empty,
            Version = "imported",
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? Path.GetFileNameWithoutExtension(sourcePath) : displayName.Trim(),
            FilePath = destinationPath,
            Sha256 = sha256,
            SizeBytes = new FileInfo(destinationPath).Length,
            IsUserImported = true,
            State = LocalModelState.Available
        };
        var registry = LoadRegistry();
        registry.Models.RemoveAll(item => item.InstallationId == model.InstallationId);
        registry.Models.Add(model);
        SaveRegistry(registry);
        return model;
    }

    public async Task<InstalledLocalAdapter> ImportAdapterAsync(
        string adapterPath,
        string manifestPath,
        string baseModelInstallationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        await ValidateGgufAsync(adapterPath, cancellationToken).ConfigureAwait(false);
        LocalAdapterDescriptor descriptor;
        try
        {
            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            descriptor = JsonSerializer.Deserialize<LocalAdapterDescriptor>(json, _jsonOptions)
                ?? throw new InvalidDataException("LoRA 清单为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("LoRA 清单不是有效 JSON。", exception);
        }

        ValidateAdapterDescriptor(descriptor);
        var registry = LoadRegistry();
        var baseModel = registry.Models.FirstOrDefault(model => model.InstallationId == baseModelInstallationId)
            ?? throw new InvalidDataException("找不到 LoRA 对应的基础模型。");
        if (!string.Equals(baseModel.Sha256, descriptor.BaseModelSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("LoRA 清单绑定的基础模型与当前模型不一致。");
        var actualSha256 = await ComputeSha256Async(adapterPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actualSha256, descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("LoRA 文件 SHA-256 校验失败。");

        var destinationDirectory = Path.Combine(_root, "adapters", SafeSegment(descriptor.Id), SafeSegment(descriptor.Version));
        var destinationPath = Path.Combine(destinationDirectory, "adapter.gguf");
        EnsureManagedPath(destinationPath);
        Directory.CreateDirectory(destinationDirectory);
        await CopyAtomicallyAsync(adapterPath, destinationPath, cancellationToken).ConfigureAwait(false);
        var adapter = new InstalledLocalAdapter
        {
            InstallationId = descriptor.Id + "@" + descriptor.Version,
            Id = descriptor.Id,
            Version = descriptor.Version,
            DisplayName = descriptor.DisplayName,
            FilePath = destinationPath,
            Sha256 = actualSha256,
            BaseModelSha256 = descriptor.BaseModelSha256.ToLowerInvariant(),
            DefaultScale = Math.Clamp(descriptor.DefaultScale, 0, 2)
        };
        registry.Adapters.RemoveAll(item => item.InstallationId == adapter.InstallationId);
        registry.Adapters.Add(adapter);
        SaveRegistry(registry);
        return adapter;
    }

    public void RemoveModel(string installationId, string? activeInstallationId = null)
    {
        if (string.Equals(installationId, activeInstallationId, StringComparison.Ordinal))
            throw new InvalidOperationException("当前正在使用的本地模型不能删除。");
        var registry = LoadRegistry();
        var model = registry.Models.FirstOrDefault(item => item.InstallationId == installationId)
            ?? throw new InvalidOperationException("本地模型不存在。");
        EnsureManagedPath(model.FilePath);
        if (registry.Adapters.Any(adapter => string.Equals(adapter.BaseModelSha256, model.Sha256, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("请先删除绑定到该基础模型的 LoRA 适配器。");
        if (File.Exists(model.FilePath)) File.Delete(model.FilePath);
        DeleteEmptyParents(Path.GetDirectoryName(model.FilePath));
        registry.Models.Remove(model);
        SaveRegistry(registry);
    }

    internal void RegisterInstalledModel(InstalledLocalModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        EnsureManagedPath(model.FilePath);
        var registry = LoadRegistry();
        registry.Models.RemoveAll(item => item.InstallationId == model.InstallationId);
        registry.Models.Add(model);
        SaveRegistry(registry);
    }

    private LocalModelRegistry LoadRegistry()
    {
        try
        {
            if (!File.Exists(_registryPath))
            {
                LastRegistryLoadError = null;
                return new LocalModelRegistry();
            }
            var registry = JsonSerializer.Deserialize<LocalModelRegistry>(File.ReadAllText(_registryPath), _jsonOptions);
            if (registry is null || registry.SchemaVersion != 1)
                throw new InvalidDataException("本地模型注册表版本不受支持。");
            registry.Models ??= [];
            registry.Adapters ??= [];
            LastRegistryLoadError = null;
            return registry;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            LastRegistryLoadError = exception;
            _registryErrorSink?.Invoke(exception);
            return new LocalModelRegistry();
        }
    }

    private void SaveRegistry(LocalModelRegistry registry)
    {
        Directory.CreateDirectory(_root);
        var temporaryPath = _registryPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(registry, _jsonOptions));
        File.Move(temporaryPath, _registryPath, true);
    }

    private static async Task ValidateGgufAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("GGUF 文件不存在。", path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        var magic = new byte[4];
        var read = await stream.ReadAsync(magic.AsMemory(), cancellationToken).ConfigureAwait(false);
        if (read != 4 || !magic.SequenceEqual(GgufMagic))
            throw new InvalidDataException("文件不是有效的 GGUF 模型或适配器。");
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task CopyAtomicallyAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        if (File.Exists(destinationPath)) return;
        var temporaryPath = destinationPath + ".importing";
        try
        {
            await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Close();
            File.Move(temporaryPath, destinationPath, false);
        }
        catch
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            throw;
        }
    }

    private static void ValidateAdapterDescriptor(LocalAdapterDescriptor descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor.Id) || string.IsNullOrWhiteSpace(descriptor.Version) ||
            string.IsNullOrWhiteSpace(descriptor.DisplayName))
            throw new InvalidDataException("LoRA 清单缺少 id、version 或 displayName。");
        if (!IsSha256(descriptor.BaseModelSha256) || !IsSha256(descriptor.Sha256))
            throw new InvalidDataException("LoRA 清单缺少有效的 SHA-256。");
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static string SafeSegment(string value)
    {
        var safe = new string(value.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.').ToArray());
        if (string.IsNullOrWhiteSpace(safe) || safe is "." or "..") throw new InvalidDataException("模型标识包含无效字符。");
        return safe;
    }

    private bool IsManagedPath(string path)
    {
        try { EnsureManagedPath(path); return true; }
        catch { return false; }
    }

    private void EnsureManagedPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var prefix = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("模型路径不在受管目录内。");
    }

    private void DeleteEmptyParents(string? directory)
    {
        while (!string.IsNullOrWhiteSpace(directory) && !string.Equals(directory, _root, StringComparison.OrdinalIgnoreCase))
        {
            EnsureManagedPath(directory);
            if (!Directory.Exists(directory) || Directory.EnumerateFileSystemEntries(directory).Any()) return;
            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory);
        }
    }
}
