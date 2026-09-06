using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

/// <summary>安装纯数据皮肤包；永不加载包内 XAML、程序集或脚本。</summary>
public sealed class SkinPackageService
{
    private const int MaxEntries = 64;
    private const long MaxUncompressedBytes = 24 * 1024 * 1024;
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".json", ".png", ".jpg", ".jpeg", ".webp" };
    private readonly string _catalogRoot;

    public SkinPackageService(string catalogRoot) =>
        _catalogRoot = Path.GetFullPath(catalogRoot ?? throw new ArgumentNullException(nameof(catalogRoot)));

    public SkinManifest Install(string packagePath)
    {
        if (!File.Exists(packagePath)) throw new FileNotFoundException("皮肤包不存在。", packagePath);
        Directory.CreateDirectory(_catalogRoot);
        using var archive = ZipFile.OpenRead(packagePath);
        ValidateEntries(archive);

        var manifestEntry = archive.GetEntry("skin.json") ?? throw new InvalidDataException("皮肤包缺少 skin.json。");
        PackageManifest dto;
        using (var stream = manifestEntry.Open())
        {
            dto = JsonSerializer.Deserialize<PackageManifest>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("skin.json 无法解析。");
        }
        ValidateManifest(dto, archive);

        var id = NormalizeSegment(dto.Id!, "皮肤 ID");
        var version = NormalizeSegment(dto.Version!, "皮肤版本");
        var finalDirectory = Path.Combine(_catalogRoot, id, version);
        var temporaryDirectory = finalDirectory + ".install-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            long written = 0;
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                var destination = Path.GetFullPath(Path.Combine(temporaryDirectory, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!destination.StartsWith(temporaryDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("皮肤包包含越界路径。");
                written = SafeArchiveExtraction.ExtractToFile(entry, destination, written, MaxUncompressedBytes);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(finalDirectory)!);
            if (Directory.Exists(finalDirectory)) Directory.Delete(finalDirectory, recursive: true);
            Directory.Move(temporaryDirectory, finalDirectory);
        }
        catch
        {
            if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true);
            throw;
        }

        return new SkinManifest(id, dto.DisplayName!, dto.ThemeResource!, false, version,
            dto.Preview, dto.Companion?.Kind ?? "vector", dto.Companion?.States, finalDirectory,
            dto.Companion?.VectorPath);
    }

    public IReadOnlyList<SkinManifest> DiscoverInstalled()
    {
        if (!Directory.Exists(_catalogRoot)) return Array.Empty<SkinManifest>();
        var manifests = new List<SkinManifest>();
        foreach (var path in Directory.EnumerateFiles(_catalogRoot, "skin.json", SearchOption.AllDirectories))
        {
            try
            {
                var dto = JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (dto is null || dto.SchemaVersion != 1 || string.IsNullOrWhiteSpace(dto.Id) ||
                    string.IsNullOrWhiteSpace(dto.DisplayName) || string.IsNullOrWhiteSpace(dto.Version) ||
                    string.IsNullOrWhiteSpace(dto.ThemeResource)) continue;
                var installPath = Path.GetDirectoryName(path)!;
                var themePath = Path.GetFullPath(Path.Combine(installPath, dto.ThemeResource));
                if (!IsInsideDirectory(themePath, installPath) || !File.Exists(themePath)) continue;
                using (var themeStream = File.OpenRead(themePath)) SkinContract.ValidateTheme(themeStream);
                if (string.Equals(dto.Companion?.Kind, "image", StringComparison.OrdinalIgnoreCase))
                {
                    if (dto.Companion?.States is null) continue;
                    var complete = SkinContract.CompanionStates.All(state =>
                    {
                        if (!dto.Companion.States.TryGetValue(state, out var relative) || string.IsNullOrWhiteSpace(relative)) return false;
                        var assetPath = Path.GetFullPath(Path.Combine(installPath, relative));
                        return IsInsideDirectory(assetPath, installPath) && File.Exists(assetPath);
                    });
                    if (!complete) continue;
                }
                var discoveredCompanion = dto.Companion;
                if (discoveredCompanion is not null && string.Equals(discoveredCompanion.Kind, "vector-layered", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(discoveredCompanion.VectorPath)) continue;
                    var vectorPath = Path.GetFullPath(Path.Combine(installPath, discoveredCompanion.VectorPath));
                    if (!IsInsideDirectory(vectorPath, installPath) || !File.Exists(vectorPath)) continue;
                    using var vectorStream = File.OpenRead(vectorPath);
                    VectorCharacterManifest.Parse(vectorStream);
                }
                manifests.Add(new SkinManifest(dto.Id, dto.DisplayName, dto.ThemeResource, false, dto.Version,
                    dto.Preview, dto.Companion?.Kind ?? "vector", dto.Companion?.States, installPath,
                    dto.Companion?.VectorPath));
            }
            catch (JsonException) { }
            catch (IOException) { }
            catch (InvalidDataException) { }
            catch (UnauthorizedAccessException) { }
        }
        return manifests.OrderBy(skin => skin.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public void Uninstall(SkinManifest manifest)
    {
        if (manifest.IsBuiltIn || string.IsNullOrWhiteSpace(manifest.InstallPath))
            throw new InvalidOperationException("内置皮肤不能卸载。");
        var installPath = Path.GetFullPath(manifest.InstallPath);
        if (!installPath.StartsWith(_catalogRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("皮肤安装目录不在受管目录内。");
        if (Directory.Exists(installPath)) Directory.Delete(installPath, recursive: true);
        var idDirectory = Path.GetDirectoryName(installPath);
        if (idDirectory is not null && Directory.Exists(idDirectory) && !Directory.EnumerateFileSystemEntries(idDirectory).Any())
            Directory.Delete(idDirectory);
    }

    private static void ValidateEntries(ZipArchive archive)
    {
        if (archive.Entries.Count > MaxEntries) throw new InvalidDataException("皮肤包文件数量过多。");
        foreach (var entry in archive.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (normalized.StartsWith('/') || normalized.Split('/').Any(part => part == ".."))
                throw new InvalidDataException("皮肤包包含越界路径。");
            if (!string.IsNullOrEmpty(entry.Name) && !AllowedExtensions.Contains(Path.GetExtension(entry.Name)))
                throw new InvalidDataException($"皮肤包包含不允许的文件：{entry.FullName}");
        }
    }

    private static void ValidateManifest(PackageManifest dto, ZipArchive archive)
    {
        if (dto.SchemaVersion != 1) throw new InvalidDataException("不支持的皮肤清单版本。");
        if (string.IsNullOrWhiteSpace(dto.Id) || string.IsNullOrWhiteSpace(dto.DisplayName) ||
            string.IsNullOrWhiteSpace(dto.Version) || string.IsNullOrWhiteSpace(dto.ThemeResource))
            throw new InvalidDataException("皮肤清单字段不完整。");
        if (archive.GetEntry(dto.ThemeResource) is null) throw new InvalidDataException("皮肤主题令牌文件不存在。");
        using (var themeStream = archive.GetEntry(dto.ThemeResource)!.Open())
            SkinContract.ValidateTheme(themeStream);
        if (!string.IsNullOrWhiteSpace(dto.Preview) && archive.GetEntry(dto.Preview) is null)
            throw new InvalidDataException("皮肤预览图不存在。");
        if (string.Equals(dto.Companion?.Kind, "image", StringComparison.OrdinalIgnoreCase))
        {
            var states = dto.Companion?.States
                ?? throw new InvalidDataException("图片角色缺少状态素材映射。");
            foreach (var state in SkinContract.CompanionStates)
            {
                if (!states.TryGetValue(state, out var path) || string.IsNullOrWhiteSpace(path))
                    throw new InvalidDataException($"图片角色缺少 {state} 状态素材。");
                var normalized = path.Replace('\\', '/');
                if (normalized.StartsWith('/') || normalized.Split('/').Any(part => part == "..") || archive.GetEntry(normalized) is null)
                    throw new InvalidDataException($"图片角色状态素材无效：{state}。");
            }
        }
        var companion = dto.Companion;
        if (companion is not null && string.Equals(companion.Kind, "vector-layered", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(companion.VectorPath))
                throw new InvalidDataException("矢量角色缺少 vectorPath。");
            var vectorEntry = archive.GetEntry(companion.VectorPath.Replace('\\', '/'))
                ?? throw new InvalidDataException("矢量角色资源不存在。");
            using var vectorStream = vectorEntry.Open();
            VectorCharacterManifest.Parse(vectorStream);
        }
    }

    private static string NormalizeSegment(string value, string label)
    {
        var normalized = value.Trim();
        if (normalized.Length > 64 || normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || normalized.Contains(".."))
            throw new InvalidDataException($"{label}无效。");
        return normalized;
    }

    private static bool IsInsideDirectory(string path, string directory)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class PackageManifest
    {
        public int SchemaVersion { get; set; }
        public string? Id { get; set; }
        public string? DisplayName { get; set; }
        public string? Version { get; set; }
        public string? ThemeResource { get; set; }
        public string? Preview { get; set; }
        public CompanionPackage? Companion { get; set; }
    }

    private sealed class CompanionPackage
    {
        public string Kind { get; set; } = "vector";
        public Dictionary<string, string> States { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string? VectorPath { get; set; }
    }
}
