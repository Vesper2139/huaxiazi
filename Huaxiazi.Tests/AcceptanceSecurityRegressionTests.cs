using System.IO;
using System.Reflection;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class AcceptanceSecurityRegressionTests
{
    [Theory]
    [InlineData("https://platform.openai.com/path", true)]
    [InlineData("https://platform.deepseek.com/docs", true)]
    [InlineData("https://example.com/path", false)]
    [InlineData("http://platform.openai.com/path", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("ms-msdt:/id/PCWDiagnostic", false)]
    [InlineData("javascript:alert(1)", false)]
    public void ProviderExternalLinkPolicy_AllowsOnlyHttpAndHttps(string value, bool expected)
    {
        var method = typeof(Huaxiazi.Views.ProviderProfileEditView).GetMethod(
            "IsAllowedExternalUrl", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        Assert.Equal(expected, method!.Invoke(null, [value]));
    }
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
