using Huaxiazi.Models;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class DiagnosticInfoServiceTests
{
    [Fact]
    public void CreateRedactedJson_DoesNotContainSecretsOrContent()
    {
        var settings = new AppSettings
        {
            ApiKey = "sk-sensitive",
            UserPersona = "私人画像",
            CustomStyleInstructions = "私密长期指令",
            History = ["私人历史正文"]
        };

        var json = DiagnosticInfoService.CreateRedactedJson(settings, "HttpRequestException");

        Assert.DoesNotContain("sk-sensitive", json);
        Assert.DoesNotContain("私人画像", json);
        Assert.DoesNotContain("私密长期指令", json);
        Assert.DoesNotContain("私人历史正文", json);
        Assert.Contains("HttpRequestException", json);
        Assert.Contains("configVersion", json);
    }
}
