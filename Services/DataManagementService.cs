using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

public sealed record DataStatistics(int RevisionCount, long TotalBytes, DateTimeOffset? LastUpdatedAt);

public sealed class DataManagementService
{
    private sealed class ImportedRecord
    {
        public ApplicationMode Mode { get; set; } = ApplicationMode.Polish;
        public string Topic { get; set; } = "导入记录";
        public string Scenario { get; set; } = "其他";
        public string OriginalText { get; set; } = string.Empty;
        public string FinalText { get; set; } = string.Empty;
        public string ContextJson { get; set; } = "{}";
        public string Style { get; set; } = string.Empty;
        public string ModelProfileId { get; set; } = string.Empty;
        public string ModelName { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    }

    public ContentRevision ImportJson(string jsonPath, ArchiveService archive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonPath);
        ArgumentNullException.ThrowIfNull(archive);
        var record = JsonSerializer.Deserialize<ImportedRecord>(File.ReadAllText(jsonPath), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidDataException("导入文件不是有效的话匣子 JSON 记录。");
        if (string.IsNullOrWhiteSpace(record.FinalText)) throw new InvalidDataException("导入记录缺少优化稿。 ");
        return archive.SaveRevision(new ArchiveDraft
        {
            Mode = record.Mode,
            Topic = record.Topic,
            Scenario = record.Scenario,
            OriginalText = record.OriginalText,
            FinalText = record.FinalText,
            ContextJson = record.ContextJson,
            Style = record.Style,
            ModelProfileId = record.ModelProfileId,
            ModelName = record.ModelName
        }, record.CreatedAt);
    }

    public ContentRevision ImportRecord(string path, ArchiveService archive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(archive);
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".json" => ImportJson(path, archive),
            ".md" => ImportMarkdown(path, archive),
            ".txt" => ImportPlainText(path, archive),
            _ => throw new InvalidDataException("仅支持导入话匣子 JSON、Markdown 或纯文本文件。")
        };
    }

    public DataStatistics GetStatistics(string rootDirectory, ArchiveService archive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(archive);
        var revisions = archive.Search(null, includeDeleted: true);
        var files = EnumerateLibraryFiles(rootDirectory).ToList();
        var totalBytes = files.Sum(path => new FileInfo(path).Length);
        DateTimeOffset? lastUpdated = revisions.Count == 0 ? null : revisions.Max(item => item.CreatedAt);
        if (files.Count > 0)
        {
            var fileUpdate = files.Max(path => new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero));
            if (lastUpdated is null || fileUpdate > lastUpdated) lastUpdated = fileUpdate;
        }
        return new DataStatistics(revisions.Count, totalBytes, lastUpdated);
    }

    public void CreateBackup(string rootDirectory, string backupPath, string? configPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(backupPath))!);
        var temporaryPath = backupPath + ".tmp";
        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        using (var archive = ZipFile.Open(temporaryPath, ZipArchiveMode.Create))
        {
            foreach (var file in EnumerateLibraryFiles(rootDirectory))
            {
                var relative = Path.GetRelativePath(rootDirectory, file);
                archive.CreateEntryFromFile(file, relative, CompressionLevel.Optimal);
            }
            var effectiveConfigPath = configPath;
            if (string.IsNullOrWhiteSpace(effectiveConfigPath))
            {
                var localConfig = Path.Combine(rootDirectory, "config.json");
                effectiveConfigPath = File.Exists(localConfig) ? localConfig : ConfigService.ConfigPath;
            }
            if (File.Exists(effectiveConfigPath))
                archive.CreateEntryFromFile(effectiveConfigPath, "config.json", CompressionLevel.Optimal);
            var notice = archive.CreateEntry("恢复说明.txt", CompressionLevel.Optimal);
            using var writer = new StreamWriter(notice.Open());
            writer.Write("备份包含本地资料库、草稿与配置。API Key 由 Windows DPAPI 绑定当前用户与设备，不随备份迁移；换机或重装后请重新填写。");
        }
        File.Move(temporaryPath, backupPath, true);
    }

    private static ContentRevision ImportMarkdown(string path, ArchiveService archive)
    {
        var content = File.ReadAllText(path);
        var original = ExtractMarkdownSection(content, "原文");
        var final = ExtractMarkdownSection(content, "优化稿");
        if (string.IsNullOrWhiteSpace(final))
            throw new InvalidDataException("Markdown 导入文件缺少“## 优化稿”内容。");
        var title = content.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("# ", StringComparison.Ordinal));
        return archive.SaveRevision(new ArchiveDraft
        {
            Topic = string.IsNullOrWhiteSpace(title) ? "Markdown 导入" : title[2..].Trim(),
            OriginalText = original,
            FinalText = final
        }, new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero));
    }

    private static ContentRevision ImportPlainText(string path, ArchiveService archive)
    {
        var content = File.ReadAllText(path).Trim();
        if (string.IsNullOrWhiteSpace(content)) throw new InvalidDataException("不能导入空文本文件。");
        return archive.SaveRevision(new ArchiveDraft
        {
            Topic = Path.GetFileNameWithoutExtension(path),
            FinalText = content
        }, new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero));
    }

    private static string ExtractMarkdownSection(string markdown, string heading)
    {
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var start = Array.FindIndex(lines, line => string.Equals(line.Trim(), $"## {heading}", StringComparison.Ordinal));
        if (start < 0) return string.Empty;
        var end = Array.FindIndex(lines, start + 1, line => line.TrimStart().StartsWith("## ", StringComparison.Ordinal));
        if (end < 0) end = lines.Length;
        return string.Join("\n", lines.Skip(start + 1).Take(end - start - 1)).Trim();
    }

    public void RestoreBackup(string backupPath, string destinationRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        var fullRoot = Path.GetFullPath(destinationRoot) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(fullRoot);
        using var archive = ZipFile.OpenRead(backupPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var target = Path.GetFullPath(Path.Combine(fullRoot, entry.FullName));
            if (!target.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("备份包含不安全路径。");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    public void Migrate(string sourceRoot, string destinationRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        var source = Path.GetFullPath(sourceRoot);
        var destination = Path.GetFullPath(destinationRoot);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) return;
        if (destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("新数据目录不能位于当前数据目录内部。");
        Directory.CreateDirectory(destination);
        foreach (var file in EnumerateLibraryFiles(source))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
        _ = new ArchiveService(destination).Search(null, includeDeleted: true);
    }

    /// <summary>
    /// 清理可再生缓存和未完成的更新下载。已校验安装包、配置、历史资料库与草稿均不会被触碰。
    /// </summary>
    public int ClearCache(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var root = Path.GetFullPath(rootDirectory);
        var removed = 0;
        var cacheDirectory = Path.Combine(root, "cache");
        if (Directory.Exists(cacheDirectory))
        {
            removed += Directory.EnumerateFiles(cacheDirectory, "*", SearchOption.AllDirectories).Count();
            Directory.Delete(cacheDirectory, recursive: true);
        }

        var updatesDirectory = Path.Combine(root, "updates");
        if (Directory.Exists(updatesDirectory))
        {
            foreach (var temporaryFile in Directory.EnumerateFiles(updatesDirectory, "*.tmp", SearchOption.TopDirectoryOnly))
            {
                File.Delete(temporaryFile);
                removed++;
            }
        }
        return removed;
    }

    private static System.Collections.Generic.IEnumerable<string> EnumerateLibraryFiles(string rootDirectory)
    {
        var dataDirectory = Path.Combine(rootDirectory, "data");
        if (Directory.Exists(dataDirectory))
            foreach (var file in Directory.EnumerateFiles(dataDirectory, "*", SearchOption.AllDirectories)) yield return file;
        var draft = Path.Combine(rootDirectory, "workspace-draft.json");
        if (File.Exists(draft)) yield return draft;
    }
}
