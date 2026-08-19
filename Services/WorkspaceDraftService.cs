using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PromptFloat.Models;

namespace PromptFloat.Services;

/// <summary>以原子替换方式保存工作区草稿；损坏文件会被隔离，避免阻断应用启动。</summary>
public sealed class WorkspaceDraftService
{
    private readonly string _draftPath;
    private readonly string _corruptPath;
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public WorkspaceDraftService(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _draftPath = Path.Combine(rootDirectory, "workspace-draft.json");
        _corruptPath = Path.Combine(rootDirectory, "workspace-draft.corrupt.json");
    }

    public WorkspaceDraft? Load()
    {
        if (!File.Exists(_draftPath)) return null;
        try
        {
            var json = File.ReadAllText(_draftPath, Encoding.UTF8);
            return JsonSerializer.Deserialize<WorkspaceDraft>(json, _options);
        }
        catch
        {
            try { File.Move(_draftPath, _corruptPath, true); }
            catch { }
            return null;
        }
    }

    public void Save(WorkspaceDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var directory = Path.GetDirectoryName(_draftPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _draftPath + ".tmp";
        var json = JsonSerializer.Serialize(draft, _options);
        File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
        File.Move(temporaryPath, _draftPath, true);
    }

    public void Clear()
    {
        if (File.Exists(_draftPath)) File.Delete(_draftPath);
    }
}
