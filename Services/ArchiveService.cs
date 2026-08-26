using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;
using PromptFloat.Models;

namespace PromptFloat.Services;

public sealed record ArchiveIntegrityResult(bool IsHealthy, string Message);

public enum ArchiveExportFormat
{
    Text,
    Markdown,
    Json
}

public sealed class ArchiveService
{
    private const int CurrentSchemaVersion = 2;
    private readonly string _root;
    private readonly string _draftsDirectory;
    private readonly string _trashDirectory;
    private readonly string _connectionString;
    private readonly int _yearArchiveThreshold;
    private bool _yearArchiveActivated;

    public ArchiveService(string rootDirectory, int yearArchiveThreshold = 100)
    {
        ArgumentNullException.ThrowIfNull(rootDirectory);
        _root = Path.GetFullPath(rootDirectory);
        if (yearArchiveThreshold < 1) throw new ArgumentOutOfRangeException(nameof(yearArchiveThreshold));
        _yearArchiveThreshold = yearArchiveThreshold;
        _draftsDirectory = Path.Combine(_root, "data", "drafts");
        _trashDirectory = Path.Combine(_root, "data", "trash");
        var databasePath = Path.Combine(_root, "data", "huaxiazi.db");

        Directory.CreateDirectory(_draftsDirectory);
        Directory.CreateDirectory(_trashDirectory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();
        Initialize();
    }

    public ContentRevision SavePolishRevision(ArchiveDraft draft, DateTimeOffset createdAt)
        => SaveRevision(draft, createdAt);

    public ContentRevision SaveRevision(ArchiveDraft draft, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(draft.FinalText) && string.IsNullOrWhiteSpace(draft.OriginalText))
        {
            throw new ArgumentException("原文和成稿不能同时为空。", nameof(draft));
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        Guid itemId;
        string scenario;
        string topic;
        int version;

        if (draft.ItemId is { } existingId)
        {
            (scenario, topic) = ReadItem(connection, transaction, existingId);
            itemId = existingId;
            version = ReadNextVersion(connection, transaction, itemId);
        }
        else
        {
            itemId = Guid.NewGuid();
            scenario = SanitizePart(draft.Scenario, "其他", 12);
            topic = SanitizePart(draft.Topic, "未命名表达", 24);
            version = 1;
            topic = DisambiguateTopic(createdAt, scenario, topic, version);

            using var insertItem = connection.CreateCommand();
            insertItem.Transaction = transaction;
            insertItem.CommandText = "INSERT INTO content_items(id, mode, scenario, topic, created_utc) VALUES($id, $mode, $scenario, $topic, $created);";
            insertItem.Parameters.AddWithValue("$id", itemId.ToString("D"));
            insertItem.Parameters.AddWithValue("$mode", draft.Mode.ToString());
            insertItem.Parameters.AddWithValue("$scenario", scenario);
            insertItem.Parameters.AddWithValue("$topic", topic);
            insertItem.Parameters.AddWithValue("$created", createdAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            insertItem.ExecuteNonQuery();
        }

        var revisionId = Guid.NewGuid();
        var fileName = BuildFileName(createdAt, scenario, topic, version);
        var finalPath = Path.Combine(_draftsDirectory, fileName);
        var temporaryPath = finalPath + "." + revisionId.ToString("N") + ".tmp";
        File.WriteAllText(temporaryPath, draft.FinalText, new UTF8Encoding(false));
        var movedToFinal = false;

        try
        {
            File.Move(temporaryPath, finalPath, false);
            movedToFinal = true;
            using var insertRevision = connection.CreateCommand();
            insertRevision.Transaction = transaction;
            insertRevision.CommandText = """
                INSERT INTO content_revisions(
                    id, item_id, version, original_text, final_text, context_json, style,
                    model_profile_id, model_name, relative_path, created_utc)
                VALUES($id, $item, $version, $original, $final, $context, $style,
                    $profile, $model, $path, $created);
                """;
            insertRevision.Parameters.AddWithValue("$id", revisionId.ToString("D"));
            insertRevision.Parameters.AddWithValue("$item", itemId.ToString("D"));
            insertRevision.Parameters.AddWithValue("$version", version);
            insertRevision.Parameters.AddWithValue("$original", draft.OriginalText ?? string.Empty);
            insertRevision.Parameters.AddWithValue("$final", draft.FinalText);
            insertRevision.Parameters.AddWithValue("$context", draft.ContextJson ?? "{}");
            insertRevision.Parameters.AddWithValue("$style", draft.Style ?? string.Empty);
            insertRevision.Parameters.AddWithValue("$profile", draft.ModelProfileId ?? string.Empty);
            insertRevision.Parameters.AddWithValue("$model", draft.ModelName ?? string.Empty);
            insertRevision.Parameters.AddWithValue("$path", Path.GetRelativePath(_root, finalPath));
            insertRevision.Parameters.AddWithValue("$created", createdAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            insertRevision.ExecuteNonQuery();
            transaction.Commit();
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            if (movedToFinal && File.Exists(finalPath)) File.Delete(finalPath);
            throw;
        }

        ApplyYearArchiveIfNeeded(revisionId);
        using var refreshedConnection = OpenConnection();
        return ReadRevision(refreshedConnection, revisionId)
            ?? throw new InvalidOperationException("成稿已写入，但无法重新读取归档索引。");
    }

    public IReadOnlyList<ContentRevision> GetRevisions(Guid itemId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE r.item_id = $item AND r.deleted_utc IS NULL ORDER BY r.version;";
        command.Parameters.AddWithValue("$item", itemId.ToString("D"));
        return ReadRevisions(command);
    }

    public IReadOnlyList<ContentRevision> Search(
        string? query,
        bool includeDeleted = false,
        ApplicationMode? mode = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var filters = new List<string>();
        if (!includeDeleted) filters.Add("r.deleted_utc IS NULL");
        filters.Add("(i.topic LIKE $pattern OR i.scenario LIKE $pattern OR r.original_text LIKE $pattern OR r.final_text LIKE $pattern)");
        if (mode.HasValue) filters.Add("i.mode = $mode");
        if (from.HasValue) filters.Add("r.created_utc >= $from");
        if (to.HasValue) filters.Add("r.created_utc <= $to");
        command.CommandText = SelectColumns + " WHERE " + string.Join(" AND ", filters) + " ORDER BY r.created_utc DESC;";
        var trimmed = query?.Trim() ?? string.Empty;
        command.Parameters.AddWithValue("$pattern", "%" + trimmed + "%");
        if (mode.HasValue) command.Parameters.AddWithValue("$mode", mode.Value.ToString());
        if (from.HasValue) command.Parameters.AddWithValue("$from", from.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        if (to.HasValue) command.Parameters.AddWithValue("$to", to.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        return ReadRevisions(command);
    }

    public ArchiveIntegrityResult CheckIntegrity()
    {
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            var messages = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read()) messages.Add(reader.GetString(0));
            var healthy = messages.Count == 1 && string.Equals(messages[0], "ok", StringComparison.OrdinalIgnoreCase);
            return new ArchiveIntegrityResult(healthy, healthy ? "ok" : string.Join(Environment.NewLine, messages));
        }
        catch (Exception exception)
        {
            return new ArchiveIntegrityResult(false, exception.Message);
        }
    }

    public void SoftDeleteRevision(Guid revisionId, DateTimeOffset deletedAt)
    {
        using var connection = OpenConnection();
        var revision = ReadRevision(connection, revisionId);
        if (revision is null || revision.DeletedAt.HasValue)
        {
            return;
        }

        var trashPath = GetTrashPath(revision);
        if (File.Exists(revision.FilePath))
        {
            File.Move(revision.FilePath, trashPath, true);
        }

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE content_revisions SET deleted_utc = $deleted WHERE id = $id;";
        command.Parameters.AddWithValue("$deleted", deletedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", revisionId.ToString("D"));
        command.ExecuteNonQuery();
    }

    public void RestoreRevision(Guid revisionId)
    {
        using var connection = OpenConnection();
        var revision = ReadRevision(connection, revisionId);
        if (revision is null || !revision.DeletedAt.HasValue)
        {
            return;
        }

        var trashPath = GetTrashPath(revision);
        Directory.CreateDirectory(Path.GetDirectoryName(revision.FilePath)!);
        if (File.Exists(trashPath))
        {
            File.Move(trashPath, revision.FilePath, false);
        }

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE content_revisions SET deleted_utc = NULL WHERE id = $id;";
        command.Parameters.AddWithValue("$id", revisionId.ToString("D"));
        command.ExecuteNonQuery();
    }

    public void SetFavorite(Guid revisionId, bool value) => SetItemFlag(revisionId, "is_favorite", value);

    public void SetArchived(Guid revisionId, bool value) => SetItemFlag(revisionId, "is_archived", value);

    private void SetItemFlag(Guid revisionId, string column, bool value)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE content_items SET {column} = $value WHERE id = (SELECT item_id FROM content_revisions WHERE id = $revision);";
        command.Parameters.AddWithValue("$value", value ? 1 : 0);
        command.Parameters.AddWithValue("$revision", revisionId.ToString("D"));
        command.ExecuteNonQuery();
    }

    public string ExportRevision(Guid revisionId, string destinationDirectory)
        => ExportRevision(revisionId, destinationDirectory, ArchiveExportFormat.Text);

    public string ExportRevision(Guid revisionId, string destinationDirectory, ArchiveExportFormat format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        using var connection = OpenConnection();
        var revision = ReadRevision(connection, revisionId)
            ?? throw new InvalidOperationException("找不到要导出的版本。");
        Directory.CreateDirectory(destinationDirectory);
        var extension = format switch
        {
            ArchiveExportFormat.Markdown => ".md",
            ArchiveExportFormat.Json => ".json",
            _ => ".txt"
        };
        var target = Path.Combine(destinationDirectory, Path.GetFileNameWithoutExtension(revision.FilePath) + extension);
        if (File.Exists(target))
        {
            var stem = Path.GetFileNameWithoutExtension(target);
            var targetExtension = Path.GetExtension(target);
            for (var suffix = 2; File.Exists(target); suffix++)
            {
                target = Path.Combine(destinationDirectory, $"{stem}-{suffix}{targetExtension}");
            }
        }
        var content = format switch
        {
            ArchiveExportFormat.Markdown => $"# {revision.Topic}\n\n- 模式：{revision.Mode}\n- 场景：{revision.Scenario}\n- 创建时间：{revision.CreatedAt:O}\n- 模型：{revision.ModelName}\n\n## 原文\n\n{revision.OriginalText}\n\n## 优化稿\n\n{revision.FinalText}\n",
            ArchiveExportFormat.Json => System.Text.Json.JsonSerializer.Serialize(new
            {
                revision.Id,
                revision.ItemId,
                revision.Version,
                revision.Mode,
                revision.Topic,
                revision.Scenario,
                revision.OriginalText,
                revision.FinalText,
                revision.ContextJson,
                revision.Style,
                revision.ModelProfileId,
                revision.ModelName,
                revision.CreatedAt
            }, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }),
            _ => revision.FinalText
        };
        File.WriteAllText(target, content, new UTF8Encoding(false));
        return target;
    }

    public IReadOnlyList<string> ExportRevisions(IEnumerable<Guid> revisionIds, string destinationDirectory)
    {
        ArgumentNullException.ThrowIfNull(revisionIds);
        var exported = new List<string>();
        foreach (var revisionId in revisionIds.Distinct())
        {
            exported.Add(ExportRevision(revisionId, destinationDirectory));
        }
        return exported;
    }

    public void PermanentlyDeleteAll()
    {
        CreateSafetyBackup("before-clear");
        using (var connection = OpenConnection())
        using (var transaction = connection.BeginTransaction())
        {
            using var revisions = connection.CreateCommand();
            revisions.Transaction = transaction;
            revisions.CommandText = "DELETE FROM content_revisions;";
            revisions.ExecuteNonQuery();
            using var items = connection.CreateCommand();
            items.Transaction = transaction;
            items.CommandText = "DELETE FROM content_items;";
            items.ExecuteNonQuery();
            transaction.Commit();
        }

        DeleteTextFiles(_draftsDirectory);
        DeleteTextFiles(_trashDirectory);
    }

    private string CreateSafetyBackup(string reason)
    {
        var backupDirectory = Path.Combine(_root, "backups");
        Directory.CreateDirectory(backupDirectory);
        var path = Path.Combine(backupDirectory, $"{reason}-{DateTime.Now:yyyyMMdd-HHmmssfff}.db");
        using var source = OpenConnection();
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        destination.Open();
        source.BackupDatabase(destination);
        return path;
    }

    public int PurgeDeletedBefore(DateTimeOffset cutoff)
    {
        using var connection = OpenConnection();
        using var find = connection.CreateCommand();
        find.CommandText = SelectColumns + " WHERE r.deleted_utc IS NOT NULL AND r.deleted_utc < $cutoff;";
        find.Parameters.AddWithValue("$cutoff", cutoff.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        var expired = ReadRevisions(find);
        if (expired.Count == 0) return 0;

        using (var transaction = connection.BeginTransaction())
        {
            foreach (var revision in expired)
            {
                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM content_revisions WHERE id = $id;";
                delete.Parameters.AddWithValue("$id", revision.Id.ToString("D"));
                delete.ExecuteNonQuery();
            }

            using var removeEmptyItems = connection.CreateCommand();
            removeEmptyItems.Transaction = transaction;
            removeEmptyItems.CommandText = "DELETE FROM content_items WHERE NOT EXISTS (SELECT 1 FROM content_revisions r WHERE r.item_id = content_items.id);";
            removeEmptyItems.ExecuteNonQuery();
            transaction.Commit();
        }

        foreach (var revision in expired)
        {
            var trashPath = GetTrashPath(revision);
            if (File.Exists(trashPath)) File.Delete(trashPath);
        }
        return expired.Count;
    }

    private const string SelectColumns = """
        SELECT r.id, r.item_id, r.version, i.mode, r.original_text, r.final_text,
               i.scenario, i.topic, r.context_json, r.style, r.model_profile_id,
               r.model_name, r.relative_path, r.created_utc, r.deleted_utc,
               i.is_favorite, i.is_archived
        FROM content_revisions r
        JOIN content_items i ON i.id = r.item_id
        """;

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS content_items(
                id TEXT PRIMARY KEY,
                mode TEXT NOT NULL,
                scenario TEXT NOT NULL,
                topic TEXT NOT NULL,
                created_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS content_revisions(
                id TEXT PRIMARY KEY,
                item_id TEXT NOT NULL REFERENCES content_items(id),
                version INTEGER NOT NULL,
                original_text TEXT NOT NULL,
                final_text TEXT NOT NULL,
                context_json TEXT NOT NULL,
                style TEXT NOT NULL,
                model_profile_id TEXT NOT NULL,
                model_name TEXT NOT NULL,
                relative_path TEXT NOT NULL UNIQUE,
                created_utc TEXT NOT NULL,
                deleted_utc TEXT NULL,
                UNIQUE(item_id, version)
            );
            CREATE INDEX IF NOT EXISTS ix_revisions_item ON content_revisions(item_id, version);
            CREATE INDEX IF NOT EXISTS ix_revisions_deleted ON content_revisions(deleted_utc);
            CREATE INDEX IF NOT EXISTS ix_revisions_created ON content_revisions(created_utc DESC);
            """;
        command.ExecuteNonQuery();
        using var readVersion = connection.CreateCommand();
        readVersion.CommandText = "PRAGMA user_version;";
        var currentVersion = Convert.ToInt32(readVersion.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (currentVersion < 2)
        {
            EnsureColumn(connection, "content_items", "is_favorite", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, "content_items", "is_archived", "INTEGER NOT NULL DEFAULT 0");
        }
        if (currentVersion < CurrentSchemaVersion)
        {
            using var setVersion = connection.CreateCommand();
            setVersion.CommandText = $"PRAGMA user_version={CurrentSchemaVersion};";
            setVersion.ExecuteNonQuery();
        }
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        using var inspect = connection.CreateCommand();
        inspect.CommandText = $"PRAGMA table_info({table});";
        using (var reader = inspect.ExecuteReader())
        {
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
            }
        }

        using var migrate = connection.CreateCommand();
        migrate.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        migrate.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static (string Scenario, string Topic) ReadItem(SqliteConnection connection, SqliteTransaction transaction, Guid itemId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT scenario, topic FROM content_items WHERE id = $id;";
        command.Parameters.AddWithValue("$id", itemId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException("找不到要追加版本的成稿。");
        }
        return (reader.GetString(0), reader.GetString(1));
    }

    private static int ReadNextVersion(SqliteConnection connection, SqliteTransaction transaction, Guid itemId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(version), 0) + 1 FROM content_revisions WHERE item_id = $item;";
        command.Parameters.AddWithValue("$item", itemId.ToString("D"));
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private ContentRevision? ReadRevision(SqliteConnection connection, Guid revisionId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE r.id = $id;";
        command.Parameters.AddWithValue("$id", revisionId.ToString("D"));
        return ReadRevisions(command).SingleOrDefault();
    }

    private IReadOnlyList<ContentRevision> ReadRevisions(SqliteCommand command)
    {
        var results = new List<ContentRevision>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!TryResolveStoredPath(reader.GetString(12), out var filePath)) continue;
            results.Add(new ContentRevision
            {
                Id = Guid.Parse(reader.GetString(0)),
                ItemId = Guid.Parse(reader.GetString(1)),
                Version = reader.GetInt32(2),
                Mode = Enum.TryParse<ApplicationMode>(reader.GetString(3), out var mode) ? mode : ApplicationMode.Polish,
                OriginalText = reader.GetString(4),
                FinalText = reader.GetString(5),
                Scenario = reader.GetString(6),
                Topic = reader.GetString(7),
                ContextJson = reader.GetString(8),
                Style = reader.GetString(9),
                ModelProfileId = reader.GetString(10),
                ModelName = reader.GetString(11),
                FilePath = filePath,
                CreatedAt = DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DeletedAt = reader.IsDBNull(14)
                    ? null
                    : DateTimeOffset.Parse(reader.GetString(14), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                IsFavorite = reader.GetInt32(15) != 0,
                IsArchived = reader.GetInt32(16) != 0
            });
        }
        return results;
    }

    private bool TryResolveStoredPath(string relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) return false;

        try
        {
            var candidate = Path.GetFullPath(Path.Combine(_root, relativePath));
            var rootPrefix = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) return false;
            fullPath = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private string DisambiguateTopic(DateTimeOffset createdAt, string scenario, string topic, int version)
    {
        if (!DraftFileExists(createdAt, scenario, topic, version))
        {
            return topic;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{topic}-{suffix}";
            if (!DraftFileExists(createdAt, scenario, candidate, version))
            {
                return candidate;
            }
        }
    }

    private bool DraftFileExists(DateTimeOffset createdAt, string scenario, string topic, int version)
    {
        var fileName = BuildFileName(createdAt, scenario, topic, version);
        return File.Exists(Path.Combine(_draftsDirectory, fileName))
            || File.Exists(Path.Combine(_draftsDirectory, createdAt.ToString("yyyy", CultureInfo.InvariantCulture), fileName));
    }

    private void ApplyYearArchiveIfNeeded(Guid savedRevisionId)
    {
        using var connection = OpenConnection();
        var scanAllRootDrafts = !_yearArchiveActivated;
        if (scanAllRootDrafts)
        {
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM content_revisions WHERE deleted_utc IS NULL;";
            if (Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture) < _yearArchiveThreshold)
            {
                return;
            }
        }

        using var select = connection.CreateCommand();
        select.CommandText = scanAllRootDrafts
            ? "SELECT id, relative_path FROM content_revisions WHERE deleted_utc IS NULL;"
            : "SELECT id, relative_path FROM content_revisions WHERE deleted_utc IS NULL AND id = $id;";
        if (!scanAllRootDrafts) select.Parameters.AddWithValue("$id", savedRevisionId.ToString("D"));
        var candidates = new List<(Guid Id, string Source, string Target)>();
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                if (!TryResolveStoredPath(reader.GetString(1), out var source)) continue;
                if (!string.Equals(Path.GetDirectoryName(source), _draftsDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var fileName = Path.GetFileName(source);
                var year = fileName.Length >= 4 && int.TryParse(fileName[..4], out _)
                    ? fileName[..4]
                    : DateTimeOffset.Now.Year.ToString(CultureInfo.InvariantCulture);
                candidates.Add((Guid.Parse(reader.GetString(0)), source, Path.Combine(_draftsDirectory, year, fileName)));
            }
        }
        if (candidates.Count == 0)
        {
            _yearArchiveActivated = true;
            return;
        }

        var moved = new List<(string Source, string Target)>();
        using var transaction = connection.BeginTransaction();
        try
        {
            foreach (var candidate in candidates)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(candidate.Target)!);
                File.Move(candidate.Source, candidate.Target, false);
                moved.Add((candidate.Source, candidate.Target));

                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE content_revisions SET relative_path = $path WHERE id = $id;";
                update.Parameters.AddWithValue("$path", Path.GetRelativePath(_root, candidate.Target));
                update.Parameters.AddWithValue("$id", candidate.Id.ToString("D"));
                update.ExecuteNonQuery();
            }
            transaction.Commit();
            _yearArchiveActivated = true;
        }
        catch
        {
            transaction.Rollback();
            foreach (var entry in moved.AsEnumerable().Reverse())
            {
                if (File.Exists(entry.Target) && !File.Exists(entry.Source))
                {
                    File.Move(entry.Target, entry.Source, false);
                }
            }
            throw;
        }
    }

    private static string BuildFileName(DateTimeOffset createdAt, string scenario, string topic, int version) =>
        $"{createdAt:yyyyMMdd-HHmm}__{scenario}__{topic}__v{version:00}.txt";

    private static string SanitizePart(string? value, string fallback, int maximumLength)
    {
        var source = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(source.Where(ch => !invalid.Contains(ch) && ch != '_').ToArray()).Trim();
        if (cleaned.Length == 0) cleaned = fallback;
        return cleaned.Length <= maximumLength ? cleaned : cleaned[..maximumLength];
    }

    private string GetTrashPath(ContentRevision revision) =>
        Path.Combine(_trashDirectory, revision.Id.ToString("N") + "__" + Path.GetFileName(revision.FilePath));

    private static void DeleteTextFiles(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.GetFiles(directory, "*.txt", SearchOption.AllDirectories))
        {
            File.Delete(path);
        }
        foreach (var child in Directory.GetDirectories(directory))
        {
            if (!Directory.EnumerateFileSystemEntries(child).Any()) Directory.Delete(child, true);
        }
    }
}
