using System;
using System.IO.Compression;
using System.IO;
using Huaxiazi.Models;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class DataManagementServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HuaxiaziData_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void GetStatistics_ReturnsRevisionCountSizeAndLatestUpdate()
    {
        var archive = new ArchiveService(_root);
        archive.SaveRevision(new ArchiveDraft { OriginalText = "原文", FinalText = "成稿" }, DateTimeOffset.UtcNow);

        var stats = new DataManagementService().GetStatistics(_root, archive);

        Assert.Equal(1, stats.RevisionCount);
        Assert.True(stats.TotalBytes > 0);
        Assert.NotNull(stats.LastUpdatedAt);
    }

    [Theory]
    [InlineData(ArchiveExportFormat.Markdown, ".md", "## 优化稿")]
    [InlineData(ArchiveExportFormat.Json, ".json", "\"originalText\"")]
    public void ExportRevision_WritesSelectedStructuredFormat(ArchiveExportFormat format, string extension, string marker)
    {
        var archive = new ArchiveService(_root);
        var revision = archive.SaveRevision(new ArchiveDraft { OriginalText = "导出原文", FinalText = "导出成稿", Topic = "导出测试" }, DateTimeOffset.UtcNow);
        var destination = Path.Combine(_root, "exports");

        var path = archive.ExportRevision(revision.Id, destination, format);

        Assert.Equal(extension, Path.GetExtension(path));
        Assert.Contains(marker, File.ReadAllText(path));
        Assert.Contains("导出原文", File.ReadAllText(path));
        Assert.Contains("导出成稿", File.ReadAllText(path));
    }

    [Fact]
    public void RestoreBackup_RejectsExcessiveEntryCountBeforeWriting()
    {
        var backup = Path.Combine(_root, "oversized.zip");
        Directory.CreateDirectory(_root);
        using (var archive = ZipFile.Open(backup, ZipArchiveMode.Create))
        {
            for (var index = 0; index < 2001; index++)
                archive.CreateEntry($"data/{index}.txt");
        }

        var restoredRoot = Path.Combine(_root, "restored");

        Assert.Throws<InvalidDataException>(() => new DataManagementService().RestoreBackup(backup, restoredRoot));
        Assert.False(Directory.Exists(restoredRoot));
    }

    [Fact]
    public void BackupAndRestore_RoundTripsArchiveDirectory()
    {
        var archive = new ArchiveService(_root);
        archive.SaveRevision(new ArchiveDraft { OriginalText = "备份原文", FinalText = "备份成稿" }, DateTimeOffset.UtcNow);
        var service = new DataManagementService();
        var backup = Path.Combine(_root, "backups", "library.zip");
        service.CreateBackup(_root, backup);
        var restoredRoot = Path.Combine(_root, "restored");

        service.RestoreBackup(backup, restoredRoot);
        var restoredArchive = new ArchiveService(restoredRoot);

        Assert.Equal("备份成稿", Assert.Single(restoredArchive.Search(null)).FinalText);
    }

    [Fact]
    public void ImportJson_RestoresExportedHistoryIntoAnotherLibrary()
    {
        var sourceArchive = new ArchiveService(_root);
        var source = sourceArchive.SaveRevision(new ArchiveDraft
        {
            Mode = ApplicationMode.PromptOptimize,
            OriginalText = "导入原文",
            FinalText = "导入成稿",
            Topic = "导入测试"
        }, DateTimeOffset.UtcNow);
        var exported = sourceArchive.ExportRevision(source.Id, Path.Combine(_root, "exports"), ArchiveExportFormat.Json);
        var destinationRoot = Path.Combine(_root, "imported");
        var destinationArchive = new ArchiveService(destinationRoot);

        var imported = new DataManagementService().ImportJson(exported, destinationArchive);

        Assert.Equal(ApplicationMode.PromptOptimize, imported.Mode);
        Assert.Equal("导入原文", imported.OriginalText);
        Assert.Equal("导入成稿", imported.FinalText);
    }

    [Fact]
    public void Migrate_CopiesVerifiedLibraryAndKeepsSourceAsRollback()
    {
        var archive = new ArchiveService(_root);
        archive.SaveRevision(new ArchiveDraft { OriginalText = "迁移原文", FinalText = "迁移成稿" }, DateTimeOffset.UtcNow);
        var destination = Path.Combine(Path.GetTempPath(), "HuaxiaziMigrated_" + Guid.NewGuid().ToString("N"));
        try
        {
            new DataManagementService().Migrate(_root, destination);

            Assert.Single(new ArchiveService(destination).Search(null));
            Assert.Single(archive.Search(null));
        }
        finally { try { if (Directory.Exists(destination)) Directory.Delete(destination, true); } catch { } }
    }

    [Fact]
    public void ClearCache_RemovesOnlyDisposableCacheAndIncompleteUpdates()
    {
        var cache = Path.Combine(_root, "cache");
        var updates = Path.Combine(_root, "updates");
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(cache);
        Directory.CreateDirectory(updates);
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(cache, "preview.bin"), "cache");
        File.WriteAllText(Path.Combine(updates, "download.tmp"), "partial");
        File.WriteAllText(Path.Combine(updates, "verified.exe"), "installer");
        File.WriteAllText(Path.Combine(data, "library.db"), "history");

        var removed = new DataManagementService().ClearCache(_root);

        Assert.Equal(2, removed);
        Assert.False(Directory.Exists(cache));
        Assert.False(File.Exists(Path.Combine(updates, "download.tmp")));
        Assert.True(File.Exists(Path.Combine(updates, "verified.exe")));
        Assert.True(File.Exists(Path.Combine(data, "library.db")));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }
}
