using System;
using System.IO;

namespace PromptFloat.Services;

/// <summary>Manages dormant v1.4 knowledge data without opening or querying its database.</summary>
public sealed class LegacyKnowledgeDataService
{
    public LegacyKnowledgeDataService(string dataRoot) => DatabasePath = Path.Combine(Path.GetFullPath(dataRoot), "knowledge", "knowledge.db");
    public string DatabasePath { get; }
    public bool Exists => File.Exists(DatabasePath);

    public void Export(string destination)
    {
        if (!Exists) throw new FileNotFoundException("未检测到旧版知识数据。", DatabasePath);
        var target = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(DatabasePath, target, true);
    }

    public void DeletePermanently()
    {
        if (File.Exists(DatabasePath)) File.Delete(DatabasePath);
        var wal = DatabasePath + "-wal";
        var shm = DatabasePath + "-shm";
        if (File.Exists(wal)) File.Delete(wal);
        if (File.Exists(shm)) File.Delete(shm);
    }
}
