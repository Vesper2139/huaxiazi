using System;
using System.IO;
using System.Linq;
using Huaxiazi.Models;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class ArchiveServiceTests : IDisposable
{
    [Fact]
    public void Search_FiltersByModeAndInclusiveDateRange()
    {
        var service = new ArchiveService(_root);
        service.SaveRevision(new ArchiveDraft { Mode = ApplicationMode.Polish, FinalText = "旧润色" }, new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero));
        service.SaveRevision(new ArchiveDraft { Mode = ApplicationMode.PromptOptimize, FinalText = "目标提示词" }, new DateTimeOffset(2026, 8, 14, 10, 0, 0, TimeSpan.Zero));
        service.SaveRevision(new ArchiveDraft { Mode = ApplicationMode.PromptOptimize, FinalText = "未来提示词" }, new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero));

        var results = service.Search(null, false, ApplicationMode.PromptOptimize,
            new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 14, 23, 59, 59, TimeSpan.Zero));

        Assert.Equal("目标提示词", Assert.Single(results).FinalText);
    }
    [Fact]
    public void Initialize_DoesNotDowngradeNewerSchemaVersion()
    {
        _ = new ArchiveService(_root);
        var databasePath = Path.Combine(_root, "data", "huaxiazi.db");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=9;";
            command.ExecuteNonQuery();
        }

        _ = new ArchiveService(_root);

        using (var verify = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            verify.Open();
            using var read = verify.CreateCommand();
            read.CommandText = "PRAGMA user_version;";
            Assert.Equal(9L, (long)read.ExecuteScalar()!);
        }
    }

    [Fact]
    public void Initialize_VersionOneDatabaseWithVersionTwoColumns_MigratesIdempotently()
    {
        var dataDirectory = Path.Combine(_root, "data");
        Directory.CreateDirectory(dataDirectory);
        var databasePath = Path.Combine(dataDirectory, "huaxiazi.db");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE content_items (
                    id TEXT PRIMARY KEY,
                    mode INTEGER NOT NULL,
                    scenario TEXT NOT NULL,
                    topic TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    is_favorite INTEGER NOT NULL DEFAULT 0,
                    is_archived INTEGER NOT NULL DEFAULT 0
                );
                PRAGMA user_version=1;
                """;
            command.ExecuteNonQuery();
        }

        _ = new ArchiveService(_root);

        using var verify = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath};Pooling=False");
        verify.Open();
        using var read = verify.CreateCommand();
        read.CommandText = "PRAGMA user_version;";
        Assert.Equal(2L, (long)read.ExecuteScalar()!);
    }

    [Fact]
    public void SaveRevision_WhenDestinationAlreadyExists_DoesNotDeletePreexistingFile()
    {
        var service = new ArchiveService(_root);
        var timestamp = new DateTimeOffset(2026, 8, 11, 16, 31, 0, TimeSpan.FromHours(8));
        var first = service.SaveRevision(new ArchiveDraft
        {
            OriginalText = "原文",
            FinalText = "第一版",
            Scenario = "私人沟通",
            Topic = "聚会回复"
        }, timestamp);
        var occupiedPath = Path.Combine(Path.GetDirectoryName(first.FilePath)!, "20260811-1631__私人沟通__聚会回复__v02.txt");
        File.WriteAllText(occupiedPath, "原有文件");

        Assert.ThrowsAny<IOException>(() => service.SaveRevision(new ArchiveDraft
        {
            ItemId = first.ItemId,
            OriginalText = "原文",
            FinalText = "第二版"
        }, timestamp));

        Assert.True(File.Exists(occupiedPath));
        Assert.Equal("原有文件", File.ReadAllText(occupiedPath));
    }

    [Fact]
    public void Search_IgnoresDatabasePathsThatEscapeTheDataRoot()
    {
        var service = new ArchiveService(_root);
        var saved = service.SaveRevision(new ArchiveDraft { FinalText = "正常内容" }, DateTimeOffset.UtcNow);
        var databasePath = Path.Combine(_root, "data", "huaxiazi.db");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE content_revisions SET relative_path = '..\\..\\outside.txt' WHERE id = $id;";
            command.Parameters.AddWithValue("$id", saved.Id.ToString("D"));
            command.ExecuteNonQuery();
        }

        Assert.Empty(service.Search(null));
    }

    [Fact]
    public void GetRevisions_ExcludesSoftDeletedByDefault()
    {
        var service = new ArchiveService(_root);
        var saved = service.SavePolishRevision(new ArchiveDraft { OriginalText = "a", FinalText = "b" }, DateTimeOffset.UtcNow);
        service.SoftDeleteRevision(saved.Id, DateTimeOffset.UtcNow);

        Assert.Empty(service.GetRevisions(saved.ItemId));
        Assert.Single(service.Search(string.Empty, includeDeleted: true));
    }
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HuaxiaziArchive_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SavePolishRevision_FirstRevision_WritesFinalOnlyAndIndexesOriginal()
    {
        var service = new ArchiveService(_root);
        var draft = new ArchiveDraft
        {
            OriginalText = "原文内容",
            FinalText = "可以直接发送的成稿。",
            Scenario = "职场沟通",
            Topic = "项目延期说明",
            ContextJson = "{\"channel\":\"微信\"}",
            Style = "自然"
        };

        var saved = service.SavePolishRevision(draft, new DateTimeOffset(2026, 8, 11, 16, 30, 0, TimeSpan.FromHours(8)));

        Assert.Equal(1, saved.Version);
        Assert.Equal("20260811-1630__职场沟通__项目延期说明__v01.txt", Path.GetFileName(saved.FilePath));
        Assert.Equal("可以直接发送的成稿。", File.ReadAllText(saved.FilePath));
        Assert.DoesNotContain("原文内容", File.ReadAllText(saved.FilePath));

        var indexed = Assert.Single(service.GetRevisions(saved.ItemId));
        Assert.Equal("原文内容", indexed.OriginalText);
        Assert.Equal("可以直接发送的成稿。", indexed.FinalText);
    }

    [Fact]
    public void SavePolishRevision_ExistingItem_CreatesNextVersionWithoutOverwriting()
    {
        var service = new ArchiveService(_root);
        var first = service.SavePolishRevision(new ArchiveDraft
        {
            OriginalText = "原文",
            FinalText = "第一版",
            Scenario = "私人沟通",
            Topic = "聚会回复"
        }, new DateTimeOffset(2026, 8, 11, 16, 31, 0, TimeSpan.FromHours(8)));

        var second = service.SavePolishRevision(new ArchiveDraft
        {
            ItemId = first.ItemId,
            OriginalText = "原文",
            FinalText = "第二版",
            Scenario = "其他",
            Topic = "这个值不应改变版本链主题"
        }, new DateTimeOffset(2026, 8, 11, 16, 32, 0, TimeSpan.FromHours(8)));

        Assert.Equal(2, second.Version);
        Assert.EndsWith("20260811-1632__私人沟通__聚会回复__v02.txt", second.FilePath);
        Assert.Equal("第一版", File.ReadAllText(first.FilePath));
        Assert.Equal("第二版", File.ReadAllText(second.FilePath));
        Assert.Equal(2, service.GetRevisions(first.ItemId).Count);
    }

    [Fact]
    public void SavePolishRevision_UnrelatedSameMinuteCollision_DisambiguatesTopicAndKeepsV01()
    {
        var service = new ArchiveService(_root);
        var timestamp = new DateTimeOffset(2026, 8, 11, 16, 33, 0, TimeSpan.FromHours(8));
        service.SavePolishRevision(new ArchiveDraft
        {
            OriginalText = "一",
            FinalText = "第一份",
            Scenario = "公开发布",
            Topic = "旅行感想"
        }, timestamp);

        var second = service.SavePolishRevision(new ArchiveDraft
        {
            OriginalText = "二",
            FinalText = "第二份",
            Scenario = "公开发布",
            Topic = "旅行感想"
        }, timestamp);

        Assert.Equal(1, second.Version);
        Assert.EndsWith("20260811-1633__公开发布__旅行感想-2__v01.txt", second.FilePath);
    }

    [Fact]
    public void DeleteAndRestoreRevision_MovesFileAndControlsSearchVisibility()
    {
        var service = new ArchiveService(_root);
        var saved = service.SavePolishRevision(new ArchiveDraft
        {
            OriginalText = "需要检索的原文",
            FinalText = "需要检索的成稿",
            Scenario = "正式材料",
            Topic = "课程总结"
        }, DateTimeOffset.Now);

        service.SoftDeleteRevision(saved.Id, DateTimeOffset.Now);

        Assert.False(File.Exists(saved.FilePath));
        Assert.Empty(service.Search("需要检索"));
        Assert.Single(service.Search("需要检索", includeDeleted: true));

        service.RestoreRevision(saved.Id);

        Assert.True(File.Exists(saved.FilePath));
        Assert.Single(service.Search("需要检索"));
    }

    [Fact]
    public void ExportRevision_CopiesFinalTextToSelectedDirectory()
    {
        var service = new ArchiveService(_root);
        var saved = service.SavePolishRevision(new ArchiveDraft
        {
            OriginalText = "原文",
            FinalText = "导出的成稿",
            Scenario = "其他",
            Topic = "导出测试"
        }, DateTimeOffset.Now);
        var destination = Path.Combine(_root, "export");

        var exported = service.ExportRevision(saved.Id, destination);

        Assert.True(File.Exists(exported));
        Assert.Equal("导出的成稿", File.ReadAllText(exported));
    }

    [Fact]
    public void ExportRevisions_ExportsEverySelectedVersion()
    {
        var service = new ArchiveService(_root);
        var first = service.SavePolishRevision(new ArchiveDraft
        {
            FinalText = "批量一",
            Scenario = "其他",
            Topic = "批量一"
        }, DateTimeOffset.Now.AddMinutes(-1));
        var second = service.SavePolishRevision(new ArchiveDraft
        {
            FinalText = "批量二",
            Scenario = "其他",
            Topic = "批量二"
        }, DateTimeOffset.Now);
        var destination = Path.Combine(_root, "batch-export");

        var paths = service.ExportRevisions([first.Id, second.Id], destination);

        Assert.Equal(2, paths.Count);
        Assert.All(paths, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public void PermanentlyDeleteAll_RemovesIndexedContentAndDraftFiles()
    {
        var service = new ArchiveService(_root);
        service.SavePolishRevision(new ArchiveDraft
        {
            OriginalText = "原文",
            FinalText = "成稿",
            Scenario = "其他",
            Topic = "清空测试"
        }, DateTimeOffset.Now);

        service.PermanentlyDeleteAll();

        Assert.Empty(service.Search(string.Empty, includeDeleted: true));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "data", "drafts"), "*.txt", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "data", "trash"), "*.txt", SearchOption.AllDirectories));
    }

    [Fact]
    public void PermanentlyDeleteAll_DoesNotLeaveRecoverableSafetyBackup()
    {
        var service = new ArchiveService(_root);
        service.SaveRevision(new ArchiveDraft { FinalText = "不可误删的记录" }, DateTimeOffset.UtcNow);

        service.PermanentlyDeleteAll();

        var backupDirectory = Path.Combine(_root, "backups");
        Assert.True(!Directory.Exists(backupDirectory) ||
                    Directory.GetFiles(backupDirectory, "before-clear-*.db").Length == 0);
    }

    [Fact]
    public void SavePolishRevision_WhenThresholdReached_MovesAllDraftsIntoYearFolders()
    {
        var service = new ArchiveService(_root, yearArchiveThreshold: 2);
        var first = service.SavePolishRevision(new ArchiveDraft
        {
            OriginalText = "原文一",
            FinalText = "成稿一",
            Scenario = "其他",
            Topic = "年度归档一"
        }, new DateTimeOffset(2025, 12, 31, 12, 0, 0, TimeSpan.FromHours(8)));
        var second = service.SavePolishRevision(new ArchiveDraft
        {
            OriginalText = "原文二",
            FinalText = "成稿二",
            Scenario = "其他",
            Topic = "年度归档二"
        }, new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.FromHours(8)));

        var refreshedFirst = Assert.Single(service.GetRevisions(first.ItemId));
        var refreshedSecond = Assert.Single(service.GetRevisions(second.ItemId));
        Assert.Equal("2025", new DirectoryInfo(Path.GetDirectoryName(refreshedFirst.FilePath)!).Name);
        Assert.Equal("2026", new DirectoryInfo(Path.GetDirectoryName(refreshedSecond.FilePath)!).Name);
        Assert.True(File.Exists(refreshedFirst.FilePath));
        Assert.True(File.Exists(refreshedSecond.FilePath));
        Assert.Equal(refreshedSecond.FilePath, second.FilePath);
    }

    [Fact]
    public void SaveRevision_AfterYearArchivingIsActive_DoesNotRescanUnrelatedRows()
    {
        var service = new ArchiveService(_root, yearArchiveThreshold: 1);
        var first = service.SaveRevision(new ArchiveDraft
        {
            FinalText = "第一份",
            Topic = "年度归档一"
        }, new DateTimeOffset(2025, 12, 31, 12, 0, 0, TimeSpan.Zero));
        var databasePath = Path.Combine(_root, "data", "huaxiazi.db");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE content_revisions SET relative_path = $path WHERE id = $id;";
            command.Parameters.AddWithValue("$path", "damaged\0path");
            command.Parameters.AddWithValue("$id", first.Id.ToString("D"));
            command.ExecuteNonQuery();
        }

        var second = service.SaveRevision(new ArchiveDraft
        {
            FinalText = "第二份",
            Topic = "年度归档二"
        }, new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal("2026", new DirectoryInfo(Path.GetDirectoryName(second.FilePath)!).Name);
    }

    [Fact]
    public void PurgeDeletedBefore_RemovesOnlyExpiredTrashAndDatabaseRows()
    {
        var service = new ArchiveService(_root);
        var now = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
        var expired = service.SavePolishRevision(new ArchiveDraft
        {
            FinalText = "过期成稿",
            Scenario = "其他",
            Topic = "过期"
        }, now.AddDays(-40));
        var recent = service.SavePolishRevision(new ArchiveDraft
        {
            FinalText = "近期成稿",
            Scenario = "其他",
            Topic = "近期"
        }, now.AddDays(-2));
        service.SoftDeleteRevision(expired.Id, now.AddDays(-31));
        service.SoftDeleteRevision(recent.Id, now.AddDays(-1));

        var purged = service.PurgeDeletedBefore(now.AddDays(-30));

        Assert.Equal(1, purged);
        var remaining = Assert.Single(service.Search(string.Empty, includeDeleted: true));
        Assert.Equal(recent.Id, remaining.Id);
    }

    [Fact]
    public void FavoriteAndArchiveFlags_PersistAcrossEveryRevisionOfAnItem()
    {
        var service = new ArchiveService(_root);
        var first = service.SaveRevision(new ArchiveDraft { OriginalText = "原文", FinalText = "第一版" }, DateTimeOffset.UtcNow);
        var second = service.SaveRevision(new ArchiveDraft { ItemId = first.ItemId, OriginalText = "原文", FinalText = "第二版" }, DateTimeOffset.UtcNow.AddMinutes(1));

        service.SetFavorite(second.Id, true);
        service.SetArchived(first.Id, true);

        var revisions = service.GetRevisions(first.ItemId);
        Assert.All(revisions, revision => Assert.True(revision.IsFavorite));
        Assert.All(revisions, revision => Assert.True(revision.IsArchived));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
