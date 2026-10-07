using System.IO;
using Huaxiazi.DatasetBuilder;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class ImmutableArtifactWriterTests
{
    [Fact]
    public void WriteNew_CreatesArtifactAndParentDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "HuaxiaziArtifactWriter_" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "run", "report.json");

        try
        {
            ImmutableArtifactWriter.WriteNew(path, "report");

            Assert.Equal("report", File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WriteNew_RefusesToOverwriteAnExistingArtifact()
    {
        var root = Path.Combine(Path.GetTempPath(), "HuaxiaziArtifactWriter_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "report.json");
        File.WriteAllText(path, "original");

        try
        {
            Assert.Throws<IOException>(() => ImmutableArtifactWriter.WriteNew(path, "replacement"));
            Assert.Equal("original", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
