using System;
using System.IO;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class BrandMigrationTests
{
    [Fact]
    public void CurrentProjectUsesHuaxiaziFilesAndDoesNotKeepLegacyProjectFiles()
    {
        var root = RepoRoot();

        Assert.True(File.Exists(Path.Combine(root, "Huaxiazi.csproj")));
        Assert.True(Directory.Exists(Path.Combine(root, "Huaxiazi.Tests")));
        Assert.False(File.Exists(Path.Combine(root, "Prompt" + "Float.csproj")));
        Assert.False(Directory.Exists(Path.Combine(root, "Prompt" + "Float.Tests")));
    }

    [Fact]
    public void CurrentReleaseUsesHuaxiaziArtifactNames()
    {
        var root = RepoRoot();
        var publish = File.ReadAllText(Path.Combine(root, "publish.ps1"));

        Assert.Contains("Huaxiazi.exe", publish);
        Assert.Contains("Huaxiazi-Portable.zip", publish);
        Assert.Contains("Huaxiazi-Setup.exe", publish);
        Assert.DoesNotContain("Vesp" + "er.exe", publish);
        Assert.DoesNotContain("Prompt" + "Float", publish, StringComparison.OrdinalIgnoreCase);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Huaxiazi.csproj")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate Huaxiazi.csproj.");
    }
}
