using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Huaxiazi.Models;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class HealthCheckServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HuaxiaziHealth_" + Guid.NewGuid().ToString("N"));

    private sealed class StubHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode));
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    [Fact]
    public async Task CheckAsync_ReportsFiveLocalChecksAndOneExternalProviderCheck()
    {
        var settings = LocalSettings();
        var service = new HealthCheckService(new StubHandler(HttpStatusCode.NotFound));

        var report = await service.CheckAsync(settings, _root, _ => null, hotkeyRegistered: true);

        Assert.Equal(6, report.Items.Count);
        Assert.Equal(5, report.Items.Count(item => item.Scope == HealthCheckScope.Local));
        Assert.Single(report.Items, item => item.Scope == HealthCheckScope.External);
        Assert.Contains(report.Items, item => item.Name == "Config" && item.Status == HealthCheckStatus.Healthy);
        Assert.Contains(report.Items, item => item.Name == "Database" && item.Status == HealthCheckStatus.Healthy);
        Assert.Contains(report.Items, item => item.Name == "Credential" && item.Status == HealthCheckStatus.Healthy);
        Assert.Contains(report.Items, item => item.Name == "HotKey" && item.Status == HealthCheckStatus.Healthy);
        Assert.Contains(report.Items, item => item.Name == "Storage" && item.Status == HealthCheckStatus.Healthy);
        Assert.Contains(report.Items, item => item.Name == "Provider" && item.Status == HealthCheckStatus.Healthy);
    }

    [Fact]
    public async Task CheckAsync_CloudProviderWithoutCredential_FailsOnlyExternalProviderCheck()
    {
        var settings = LocalSettings();
        settings.ProviderProfiles[0].Type = ProviderType.Cloud;
        settings.ProviderProfiles[0].ApiBase = "https://api.example.test/v1";
        var service = new HealthCheckService(new StubHandler(HttpStatusCode.OK));

        var report = await service.CheckAsync(settings, _root, _ => null, hotkeyRegistered: true);

        Assert.False(report.HasLocalFailures);
        var provider = Assert.Single(report.Items, item => item.Name == "Provider");
        Assert.Equal(HealthCheckScope.External, provider.Scope);
        Assert.Equal(HealthCheckStatus.Failed, provider.Status);
        Assert.Contains("API Key", provider.Message);
    }

    [Fact]
    public async Task CheckAsync_TamperedOfficialEndpoint_IsRejectedWithoutNetworkRequest()
    {
        var settings = LocalSettings();
        settings.ProviderProfiles[0] = new ProviderProfile
        {
            Id = "cloud",
            Name = "Claude",
            Type = ProviderType.Cloud,
            Platform = ProviderPlatform.Anthropic,
            Protocol = ProviderProtocol.AnthropicMessages,
            ApiBase = "https://attacker.example",
            Model = "claude",
            SecretId = "provider-cloud"
        };
        settings.ActiveProviderProfileId = "cloud";
        var handler = new CountingHandler();
        var service = new HealthCheckService(handler);

        var report = await service.CheckAsync(settings, _root, _ => "secret", hotkeyRegistered: true);

        var provider = Assert.Single(report.Items, item => item.Name == "Provider");
        Assert.Equal(HealthCheckStatus.Failed, provider.Status);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task CheckAsync_HotkeyRegistrationFailure_IsNonBlockingWarning()
    {
        var report = await new HealthCheckService(new StubHandler(HttpStatusCode.OK))
            .CheckAsync(LocalSettings(), _root, _ => null, hotkeyRegistered: false);

        Assert.False(report.HasLocalFailures);
        Assert.Contains(report.Items, item => item.Name == "HotKey" && item.Status == HealthCheckStatus.Warning);
    }

    [Fact]
    public async Task CheckAsync_PartialHotkeyRegistration_ListsEachFailedAction()
    {
        var registration = new HotkeyRegistrationResult
        {
            Actions = new System.Collections.Generic.Dictionary<GlobalHotkeyAction, HotkeyActionRegistration>
            {
                [GlobalHotkeyAction.ToggleWindow] = new(true, "已注册"),
                [GlobalHotkeyAction.QuickPolish] = new(false, "已被占用"),
                [GlobalHotkeyAction.CopyResult] = new(true, "已注册")
            }
        };

        var report = await new HealthCheckService(new StubHandler(HttpStatusCode.OK))
            .CheckAsync(LocalSettings(), _root, _ => null, registration);

        var hotkey = Assert.Single(report.Items, item => item.Name == "HotKey");
        Assert.Equal(HealthCheckStatus.Warning, hotkey.Status);
        Assert.Contains("QuickPolish", hotkey.Message);
        Assert.DoesNotContain("ToggleWindow:", hotkey.Message);
    }

    [Fact]
    public async Task CheckAsync_UnconfiguredOptionalHotkeys_DoNotProduceWarning()
    {
        var registration = HotkeyRegistrationBatch.Execute(
            new System.Collections.Generic.Dictionary<GlobalHotkeyAction, string>
            {
                [GlobalHotkeyAction.ToggleWindow] = "Ctrl+Shift+H",
                [GlobalHotkeyAction.QuickPolish] = "",
                [GlobalHotkeyAction.QuickPromptOptimize] = "",
                [GlobalHotkeyAction.CopyResult] = ""
            }, (_, _) => true);

        var report = await new HealthCheckService(new StubHandler(HttpStatusCode.OK))
            .CheckAsync(LocalSettings(), _root, _ => null, registration);

        var hotkey = Assert.Single(report.Items, item => item.Name == "HotKey");
        Assert.Equal(HealthCheckStatus.Healthy, hotkey.Status);
        Assert.False(report.HasLocalFailures);
    }

    [Fact]
    public async Task CheckAsync_NoHotkeysConfigured_IsHealthyBecauseResidentEntrypointsRemainAvailable()
    {
        var registration = HotkeyRegistrationBatch.Execute(
            Enum.GetValues<GlobalHotkeyAction>().ToDictionary(action => action, _ => string.Empty),
            (_, _) => false);

        var report = await new HealthCheckService(new StubHandler(HttpStatusCode.OK))
            .CheckAsync(LocalSettings(), _root, _ => null, registration);

        var hotkey = Assert.Single(report.Items, item => item.Name == "HotKey");
        Assert.Equal(HealthCheckStatus.Healthy, hotkey.Status);
        Assert.Contains("未配置", hotkey.Message);
    }

    private static AppSettings LocalSettings() => new()
    {
        ProviderProfiles =
        [
            new ProviderProfile
            {
                Id = "local", Name = "本机模型", Type = ProviderType.Local,
                Platform = ProviderPlatform.Ollama,
                ApiBase = "http://localhost:11434/v1", Model = "local-test"
            }
        ],
        ActiveProviderProfileId = "local"
    };

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }
}
