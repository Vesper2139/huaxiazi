using System;
using System.IO;
using PromptFloat.Services;
using Xunit;

namespace PromptFloat.Tests;

public sealed class LegacyKnowledgeDataServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "VesperLegacyKnowledge_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Constructor_DoesNotOpenMoveOrDeleteLegacyDatabase()
    {
        var path = Path.Combine(_root, "knowledge", "knowledge.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        var service = new LegacyKnowledgeDataService(_root);
        Assert.True(service.Exists);
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(path));
    }

    [Fact]
    public void ExportAndExplicitDelete_AreUserControlledOperations()
    {
        var path = Path.Combine(_root, "knowledge", "knowledge.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "legacy");
        var service = new LegacyKnowledgeDataService(_root);
        var export = Path.Combine(_root, "export", "knowledge.db");
        service.Export(export);
        service.DeletePermanently();
        Assert.Equal("legacy", File.ReadAllText(export));
        Assert.False(service.Exists);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
