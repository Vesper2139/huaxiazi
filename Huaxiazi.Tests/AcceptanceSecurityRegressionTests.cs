using System.IO;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class AcceptanceSecurityRegressionTests
{
    [Fact]
    public void ApiService_DisablesAutomaticRedirectsForCredentialBearingRequests()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "Services", "AIService.cs"));
        Assert.Contains("AllowAutoRedirect = false", source);
        Assert.DoesNotContain("Timeout = Timeout.InfiniteTimeSpan", source);
    }

    [Fact]
    public void CorruptConfigBackups_RedactSecretsAndHaveRetentionPolicy()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "Services", "ConfigService.cs"));
        Assert.Contains("RedactSensitiveJson", source);
        Assert.Contains("MaxCorruptBackups", source);
        Assert.DoesNotContain("File.Copy(sourcePath, $\"{sourcePath}.{stamp}.corrupt\", overwrite: false)", source);
    }

    private static string RepoRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "Huaxiazi.sln")))
            directory = Path.GetDirectoryName(directory);
        return directory ?? throw new InvalidOperationException("未找到解决方案根目录。");
    }
}
