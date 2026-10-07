using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Drawing;
using System.Runtime.InteropServices;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

/// <summary>安装纯数据皮肤包；永不加载包内 XAML、程序集或脚本。</summary>
public sealed class SkinPackageService
{
    private const int MaxEntries = 64;
    private const long MaxUncompressedBytes = 24 * 1024 * 1024;
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".json", ".png", ".jpg", ".jpeg", ".webp" };
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
        { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
    private readonly string _catalogRoot;
    private readonly Action<string>? _diagnosticSink;

    public SkinPackageService(string catalogRoot, Action<string>? diagnosticSink = null)
    {
        _catalogRoot = Path.GetFullPath(catalogRoot ?? throw new ArgumentNullException(nameof(catalogRoot)));
        _diagnosticSink = diagnosticSink;
    }

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
            dto.Companion?.VectorPath, dto.Companion?.SpriteSheetPath,
            dto.Companion?.SpriteSheetColumns ?? SkinContract.CompanionSpriteSheetColumns,
            dto.Companion?.SpriteSheetRows ?? SkinContract.CompanionSpriteSheetRows,
            dto.Companion?.IdleVariantsPath,
            dto.Companion?.IdleVariantsColumns ?? SkinContract.CompanionIdleVariantsColumns);
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
                    string.IsNullOrWhiteSpace(dto.ThemeResource))
                {
                    ReportDiagnostic(path, "清单字段不完整或版本不支持");
                    continue;
                }
                var installPath = Path.GetDirectoryName(path)!;
                var themePath = Path.GetFullPath(Path.Combine(installPath, dto.ThemeResource));
                if (!IsInsideDirectory(themePath, installPath) || !File.Exists(themePath))
                {
                    ReportDiagnostic(path, "主题资源不存在或越界");
                    continue;
                }
                using (var themeStream = File.OpenRead(themePath)) SkinContract.ValidateTheme(themeStream);
                if (string.Equals(dto.Companion?.Kind, "image", StringComparison.OrdinalIgnoreCase))
                {
                    if (dto.Companion?.States is null)
                    {
                        ReportDiagnostic(path, "图片角色缺少状态素材映射");
                        continue;
                    }
                    var complete = SkinContract.CompanionStates.All(state =>
                    {
                        if (!dto.Companion.States.TryGetValue(state, out var relative) || string.IsNullOrWhiteSpace(relative)) return false;
                        var assetPath = Path.GetFullPath(Path.Combine(installPath, relative));
                        return IsInsideDirectory(assetPath, installPath) && IsValidCompanionStateFile(assetPath);
                    });
                    if (!complete)
                    {
                        ReportDiagnostic(path, "图片角色状态素材不完整或未通过几何校验");
                        continue;
                    }
                }
                if (string.Equals(dto.Companion?.Kind, "spritesheet", StringComparison.OrdinalIgnoreCase) &&
                    !HasCompleteSpriteSheet(dto.Companion, installPath))
                {
                    ReportDiagnostic(path, "精灵表角色资源不完整或尺寸不符合契约");
                    continue;
                }
                var discoveredCompanion = dto.Companion;
                if (discoveredCompanion is not null && string.Equals(discoveredCompanion.Kind, "vector-layered", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(discoveredCompanion.VectorPath))
                    {
                        ReportDiagnostic(path, "矢量角色缺少资源路径");
                        continue;
                    }
                    var vectorPath = Path.GetFullPath(Path.Combine(installPath, discoveredCompanion.VectorPath));
                    if (!IsInsideDirectory(vectorPath, installPath) || !File.Exists(vectorPath))
                    {
                        ReportDiagnostic(path, "矢量角色资源不存在或越界");
                        continue;
                    }
                    using var vectorStream = File.OpenRead(vectorPath);
                    VectorCharacterManifest.Parse(vectorStream);
                }
                manifests.Add(new SkinManifest(dto.Id, dto.DisplayName, dto.ThemeResource, false, dto.Version,
                    dto.Preview, dto.Companion?.Kind ?? "vector", dto.Companion?.States, installPath,
                    dto.Companion?.VectorPath, dto.Companion?.SpriteSheetPath,
                    dto.Companion?.SpriteSheetColumns ?? SkinContract.CompanionSpriteSheetColumns,
                    dto.Companion?.SpriteSheetRows ?? SkinContract.CompanionSpriteSheetRows,
                    dto.Companion?.IdleVariantsPath,
                    dto.Companion?.IdleVariantsColumns ?? SkinContract.CompanionIdleVariantsColumns));
            }
            catch (JsonException exception) { ReportDiagnostic(path, exception.Message); }
            catch (IOException exception) { ReportDiagnostic(path, exception.Message); }
            catch (InvalidDataException exception) { ReportDiagnostic(path, exception.Message); }
            catch (UnauthorizedAccessException exception) { ReportDiagnostic(path, exception.Message); }
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
                var entry = archive.GetEntry(normalized);
                if (normalized.StartsWith('/') || normalized.Split('/').Any(part => part == "..") || entry is null)
                    throw new InvalidDataException($"图片角色状态素材无效：{state}。");
                if (!string.Equals(Path.GetExtension(normalized), ".png", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"图片角色的 {state} 状态素材必须是透明 PNG。");
                ValidateCompanionStateBitmap(entry, state);
            }
        }
        if (string.Equals(dto.Companion?.Kind, "spritesheet", StringComparison.OrdinalIgnoreCase))
            ValidateSpriteSheetCompanion(dto.Companion, archive);
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
        var deviceName = normalized.TrimEnd('.', ' ').Split('.')[0];
        if (normalized.Length == 0 || normalized.Length > 64 || normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            normalized.Contains("..") || ReservedDeviceNames.Contains(deviceName))
            throw new InvalidDataException($"{label}无效。");
        return normalized;
    }

    private void ReportDiagnostic(string path, string message)
    {
        try { _diagnosticSink?.Invoke($"皮肤包已跳过：{path}：{message}"); }
        catch { }
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
        public string? SpriteSheetPath { get; set; }
        public int SpriteSheetColumns { get; set; } = SkinContract.CompanionSpriteSheetColumns;
        public int SpriteSheetRows { get; set; } = SkinContract.CompanionSpriteSheetRows;
        public string? IdleVariantsPath { get; set; }
        public int IdleVariantsColumns { get; set; } = SkinContract.CompanionIdleVariantsColumns;
    }

    private static void ValidateSpriteSheetCompanion(CompanionPackage? companion, ZipArchive archive)
    {
        if (companion is null || string.IsNullOrWhiteSpace(companion.SpriteSheetPath) ||
            string.IsNullOrWhiteSpace(companion.IdleVariantsPath))
            throw new InvalidDataException("精灵表角色缺少主状态表或待机变体表。");
        if (companion.SpriteSheetColumns != SkinContract.CompanionSpriteSheetColumns ||
            companion.SpriteSheetRows != SkinContract.CompanionSpriteSheetRows ||
            companion.IdleVariantsColumns != SkinContract.CompanionIdleVariantsColumns)
            throw new InvalidDataException("精灵表角色必须使用共享的 4×3 状态布局和 4 列待机布局。");
        if (archive.GetEntry(companion.SpriteSheetPath.Replace('\\', '/')) is null ||
            archive.GetEntry(companion.IdleVariantsPath.Replace('\\', '/')) is null)
            throw new InvalidDataException("精灵表角色资源不存在。");
        ValidateBitmapDimensions(archive.GetEntry(companion.SpriteSheetPath.Replace('\\', '/'))!,
            SkinContract.CompanionSpriteSheetWidth, SkinContract.CompanionSpriteSheetHeight,
            "主状态精灵表");
        ValidateBitmapDimensions(archive.GetEntry(companion.IdleVariantsPath.Replace('\\', '/'))!,
            SkinContract.CompanionIdleVariantsWidth, SkinContract.CompanionIdleVariantsHeight,
            "待机变体精灵表");
    }

    private static void ValidateBitmapDimensions(ZipArchiveEntry entry, int expectedWidth, int expectedHeight, string label)
    {
        using var stream = entry.Open();
        Bitmap bitmap;
        try { bitmap = new Bitmap(stream); }
        catch (ArgumentException exception) { throw new InvalidDataException($"{label}必须是有效图片。", exception); }
        catch (ExternalException exception) { throw new InvalidDataException($"{label}必须是有效图片。", exception); }
        using (bitmap)
        {
        if (bitmap.Width != expectedWidth || bitmap.Height != expectedHeight)
            throw new InvalidDataException($"{label}必须是 {expectedWidth}×{expectedHeight}。实际为 {bitmap.Width}×{bitmap.Height}。");
        }
    }

    private static void ValidateCompanionStateBitmap(ZipArchiveEntry entry, string state)
    {
        using var stream = entry.Open();
        Bitmap bitmap;
        try { bitmap = new Bitmap(stream); }
        catch (ArgumentException exception) { throw new InvalidDataException($"{state} 状态素材必须是透明 PNG。", exception); }
        catch (ExternalException exception) { throw new InvalidDataException($"{state} 状态素材必须是透明 PNG。", exception); }
        using (bitmap)
        {
        ValidateCompanionStateBitmap(bitmap, state);
        }
    }

    private static void ValidateCompanionStateBitmap(Bitmap bitmap, string state)
    {
        if (bitmap.Width != SkinContract.CompanionStateImageSize || bitmap.Height != SkinContract.CompanionStateImageSize)
            throw new InvalidDataException(
                $"{state} 状态素材必须是 {SkinContract.CompanionStateImageSize}×{SkinContract.CompanionStateImageSize}。" +
                $"实际为 {bitmap.Width}×{bitmap.Height}。");

        var minX = bitmap.Width;
        var minY = bitmap.Height;
        var maxX = -1;
        var maxY = -1;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            if (bitmap.GetPixel(x, y).A < 16) continue;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        if (maxX < 0)
            throw new InvalidDataException($"{state} 状态素材不包含可见角色内容。");
        var margin = SkinContract.CompanionStateSafetyMargin;
        if (minX < margin || minY < margin || maxX >= bitmap.Width - margin || maxY >= bitmap.Height - margin)
            throw new InvalidDataException($"{state} 状态素材必须在画布四周保留至少 {margin}px 透明安全区。");
    }

    private static bool IsValidCompanionStateFile(string path)
    {
        if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            using var bitmap = new Bitmap(path);
            ValidateCompanionStateBitmap(bitmap, Path.GetFileNameWithoutExtension(path));
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (ExternalException) { return false; }
        catch (InvalidDataException) { return false; }
    }

    private static bool HasCompleteSpriteSheet(CompanionPackage? companion, string installPath)
    {
        if (companion is null || companion.SpriteSheetColumns != SkinContract.CompanionSpriteSheetColumns ||
            companion.SpriteSheetRows != SkinContract.CompanionSpriteSheetRows ||
            companion.IdleVariantsColumns != SkinContract.CompanionIdleVariantsColumns ||
            string.IsNullOrWhiteSpace(companion.SpriteSheetPath) || string.IsNullOrWhiteSpace(companion.IdleVariantsPath))
            return false;
        var sheet = Path.GetFullPath(Path.Combine(installPath, companion.SpriteSheetPath));
        var idle = Path.GetFullPath(Path.Combine(installPath, companion.IdleVariantsPath));
        if (!IsInsideDirectory(sheet, installPath) || !IsInsideDirectory(idle, installPath) ||
            !File.Exists(sheet) || !File.Exists(idle)) return false;
        try
        {
            using var sheetBitmap = new Bitmap(sheet);
            using var idleBitmap = new Bitmap(idle);
            return sheetBitmap.Width == SkinContract.CompanionSpriteSheetWidth &&
                   sheetBitmap.Height == SkinContract.CompanionSpriteSheetHeight &&
                   idleBitmap.Width == SkinContract.CompanionIdleVariantsWidth &&
                   idleBitmap.Height == SkinContract.CompanionIdleVariantsHeight;
        }
        catch (ArgumentException) { return false; }
        catch (ExternalException) { return false; }
    }
}
