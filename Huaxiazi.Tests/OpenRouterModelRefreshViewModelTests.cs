using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Huaxiazi.Models;
using Huaxiazi.Services;
using Huaxiazi.ViewModels;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class OpenRouterModelRefreshViewModelTests
{
    [Fact]
    public async Task RefreshCommand_PersistsCapabilitiesForTheCurrentlyConfiguredModel()
    {
        var original = App.Settings.Clone();
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", "openrouter-capabilities-refresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var profile = new ProviderProfile
            {
                Id = "router-profile",
                Name = "OpenRouter",
                Platform = ProviderPlatform.OpenRouter,
                Protocol = ProviderProtocol.OpenAICompatible,
                ApiBase = "https://openrouter.ai/api/v1",
                Model = "vendor/model",
                SecretId = "router-key"
            };
            App.ReplaceSettings(new AppSettings { DataDirectory = root, ProviderProfiles = [profile], ActiveProviderProfileId = profile.Id });
            var secrets = new MemorySecretStore();
            secrets.Save(profile.SecretId, "sk-or-v1-test-key");
            using var client = new HttpClient(new CatalogSequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"data":[{"id":"vendor/model","name":"Vendor Model","architecture":{"input_modalities":["text"],"output_modalities":["text"]},"supported_parameters":["response_format","structured_outputs","max_tokens","temperature","private_control"]}]}
                    """, Encoding.UTF8, "application/json")
            }));
            var vm = new SettingsViewModel(
                archiveService: new ArchiveService(root),
                secretStore: secrets,
                skillCatalogLoader: () => [],
                openRouterModelCatalogService: new OpenRouterModelCatalogService(client));

            await vm.RefreshOpenRouterModelsCommand.ExecuteAsync(null);

            var selectedProfile = vm.SelectedProviderProfile!;
            Assert.Equal("vendor/model", selectedProfile.OpenRouterCapabilitiesModelId);
            Assert.Equal(new[] { "max_tokens", "response_format", "structured_outputs", "temperature" }, selectedProfile.OpenRouterSupportedParameters);
            Assert.Contains("候选参数", vm.SelectedProviderCapabilitySummary);
            Assert.Contains("全部请求参数", vm.SelectedProviderCapabilitySummary);
            Assert.True(vm.HasChanges);
        }
        finally
        {
            App.ReplaceSettings(original);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RefreshCommand_ClearsSnapshotWhenCurrentModelIsAbsentFromNewCatalog()
    {
        var original = App.Settings.Clone();
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", "openrouter-capabilities-stale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var profile = new ProviderProfile
            {
                Id = "router-profile",
                Name = "OpenRouter",
                Platform = ProviderPlatform.OpenRouter,
                Protocol = ProviderProtocol.OpenAICompatible,
                ApiBase = "https://openrouter.ai/api/v1",
                Model = "vendor/removed-model",
                OpenRouterCapabilitiesModelId = "vendor/removed-model",
                OpenRouterSupportedParameters = ["response_format", "max_tokens"],
                SecretId = "router-key"
            };
            App.ReplaceSettings(new AppSettings { DataDirectory = root, ProviderProfiles = [profile], ActiveProviderProfileId = profile.Id });
            var secrets = new MemorySecretStore();
            secrets.Save(profile.SecretId, "sk-or-v1-test-key");
            using var client = new HttpClient(new CatalogSequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"data":[{"id":"vendor/current-model","name":"Current Model","architecture":{"input_modalities":["text"],"output_modalities":["text"]},"supported_parameters":["response_format","max_tokens"]}]}
                    """, Encoding.UTF8, "application/json")
            }));
            var vm = new SettingsViewModel(
                archiveService: new ArchiveService(root),
                secretStore: secrets,
                skillCatalogLoader: () => [],
                openRouterModelCatalogService: new OpenRouterModelCatalogService(client));

            await vm.RefreshOpenRouterModelsCommand.ExecuteAsync(null);

            Assert.Equal("", vm.SelectedProviderProfile!.OpenRouterCapabilitiesModelId);
            Assert.Null(vm.SelectedProviderProfile.OpenRouterSupportedParameters);
            Assert.Contains("未核验", vm.SelectedProviderCapabilitySummary);
            Assert.True(vm.HasChanges);
        }
        finally
        {
            App.ReplaceSettings(original);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RefreshCommand_UsesConfiguredKeyAndRetainsCurrentModelAcrossCatalogRefreshFailure()
    {
        var original = App.Settings.Clone();
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", "openrouter-model-refresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var profile = new ProviderProfile
            {
                Id = "router-profile",
                Name = "OpenRouter",
                Platform = ProviderPlatform.OpenRouter,
                Protocol = ProviderProtocol.OpenAICompatible,
                ApiBase = "https://openrouter.ai/api/v1",
                Model = "legacy/custom-model",
                SecretId = "router-key"
            };
            App.ReplaceSettings(new AppSettings { DataDirectory = root, ProviderProfiles = [profile], ActiveProviderProfileId = profile.Id });
            var secrets = new MemorySecretStore();
            secrets.Save(profile.SecretId, "sk-or-v1-test-key");
            var calls = 0;
            var handler = new CatalogSequenceHandler(_ =>
            {
                calls++;
                return calls == 1
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""
                            {"data":[{"id":"openai/gpt-test","name":"GPT Test","architecture":{"input_modalities":["text"],"output_modalities":["text"]}}]}
                            """, Encoding.UTF8, "application/json")
                    }
                    : new HttpResponseMessage(HttpStatusCode.Forbidden);
            });
            using var client = new HttpClient(handler);
            var vm = new SettingsViewModel(
                archiveService: new ArchiveService(root),
                secretStore: secrets,
                skillCatalogLoader: () => [],
                openRouterModelCatalogService: new OpenRouterModelCatalogService(client));

            Assert.True(vm.IsOpenRouterModelRefreshVisible);
            Assert.Equal("legacy/custom-model", vm.ModelId);

            await vm.RefreshOpenRouterModelsCommand.ExecuteAsync(null);
            Assert.Contains(vm.AvailableModels, model => model.ModelId == "openai/gpt-test");
            Assert.Contains(vm.AvailableModels, model => model.ModelId == "legacy/custom-model");
            Assert.Equal("legacy/custom-model", vm.ModelId);
            Assert.Contains("尚未核验", vm.SelectedProviderCapabilitySummary);
            Assert.False(vm.HasChanges);

            await vm.RefreshOpenRouterModelsCommand.ExecuteAsync(null);
            Assert.Contains(vm.AvailableModels, model => model.ModelId == "openai/gpt-test");
            Assert.Contains("失败", vm.OpenRouterModelCatalogStatus);
            Assert.Equal(2, calls);
        }
        finally
        {
            App.ReplaceSettings(original);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public void Save(string id, string secret) => _values[id] = secret;
        public string? Read(string id) => _values.GetValueOrDefault(id);
        public bool Exists(string id) => _values.ContainsKey(id);
        public void Delete(string id) => _values.Remove(id);
    }

    private sealed class CatalogSequenceHandler(Func<HttpRequestMessage, HttpResponseMessage> factory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(factory(request));
    }
}
