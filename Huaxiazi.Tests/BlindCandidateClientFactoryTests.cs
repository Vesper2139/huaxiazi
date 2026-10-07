using System.Net;
using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Huaxiazi.BlindEvaluationRunner;
using Huaxiazi.Models;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class BlindCandidateClientFactoryTests
{
    [Fact]
    public async Task Create_ManagedLocalSealsVerifiedModelAndSelectedRuntimeBinaryHashes()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "managed-provenance-" + Guid.NewGuid().ToString("N"));
        var modelRoot = Path.Combine(root, "models");
        var runtimeRoot = Path.Combine(root, "runtimes", "local");
        Directory.CreateDirectory(root);
        try
        {
            var sourceModel = Path.Combine(root, "model.gguf");
            var modelBytes = Encoding.ASCII.GetBytes("GGUF-test-model-bytes");
            await File.WriteAllBytesAsync(sourceModel, modelBytes);
            var store = new LocalModelStore(modelRoot);
            var installedModel = await store.ImportModelAsync(sourceModel, "test model");
            var cpuRuntime = Path.Combine(runtimeRoot, "cpu", "llama-server.exe");
            var vulkanRuntime = Path.Combine(runtimeRoot, "vulkan", "llama-server.exe");
            var runtimeBytes = Encoding.ASCII.GetBytes("vulkan-runtime-test-binary");
            Directory.CreateDirectory(Path.GetDirectoryName(cpuRuntime)!);
            Directory.CreateDirectory(Path.GetDirectoryName(vulkanRuntime)!);
            await File.WriteAllBytesAsync(cpuRuntime, Encoding.ASCII.GetBytes("cpu-runtime"));
            await File.WriteAllBytesAsync(vulkanRuntime, runtimeBytes);

            var profile = ManagedLocalProfile(installedModel.InstallationId);
            using var session = BlindCandidateClientFactory.Create(
                new BlindCandidateRuntimeConfiguration(profile, "", ""), [], runtimeRoot, modelRoot);
            var localArtifactsProperty = typeof(BlindCandidateClientSession).GetProperty("LocalArtifacts");
            Assert.NotNull(localArtifactsProperty);
            var localArtifacts = JsonDocument.Parse(JsonSerializer.Serialize(localArtifactsProperty!.GetValue(session), new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            }));

            Assert.Equal(installedModel.InstallationId, localArtifacts.RootElement.GetProperty("model_installation_id").GetString());
            Assert.Equal(installedModel.Sha256, localArtifacts.RootElement.GetProperty("model_sha256").GetString());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(runtimeBytes)).ToLowerInvariant(),
                localArtifacts.RootElement.GetProperty("runtime_sha256").GetString());
            Assert.Equal("vulkan", localArtifacts.RootElement.GetProperty("runtime_flavor").GetString());
            Assert.DoesNotContain(modelRoot, localArtifacts.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(runtimeRoot, localArtifacts.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Create_ManagedLocalRejectsModelBytesChangedAfterInstallation()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "managed-provenance-tamper-" + Guid.NewGuid().ToString("N"));
        var modelRoot = Path.Combine(root, "models");
        var runtimeRoot = Path.Combine(root, "runtimes", "local");
        Directory.CreateDirectory(root);
        try
        {
            var sourceModel = Path.Combine(root, "model.gguf");
            await File.WriteAllBytesAsync(sourceModel, Encoding.ASCII.GetBytes("GGUForiginal-model-bytes"));
            var installedModel = await new LocalModelStore(modelRoot).ImportModelAsync(sourceModel, "tamper test model");
            await File.WriteAllBytesAsync(installedModel.FilePath, Encoding.ASCII.GetBytes("GGUFmodified-model-bytes"));

            var exception = Assert.Throws<InvalidDataException>(() => BlindCandidateClientFactory.Create(
                new BlindCandidateRuntimeConfiguration(ManagedLocalProfile(installedModel.InstallationId), "", ""),
                [], runtimeRoot, modelRoot));

            Assert.Contains("SHA-256", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Create_ManagedLocalUsesProductRuntimeClientAndKeepsTelemetryContentFree()
    {
        var runtimeHost = new FakeRuntimeHost();
        var handler = new CapturingHandler();
        var observations = new List<ProviderRequestTelemetry>();
        var profile = new ProviderProfile
        {
            Id = "managed-local-candidate",
            Type = ProviderType.Local,
            Platform = ProviderPlatform.ManagedLocal,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "http://127.0.0.1:0/v1",
            Model = "installed-model-id",
            LocalModelInstallationId = "installed-model-id"
        };

        using var session = BlindCandidateClientFactory.CreateWithOverrides(
            new BlindCandidateRuntimeConfiguration(profile, "", ""), observations,
            runtimeHostOverride: runtimeHost, localHandlerOverride: handler);
        var structured = Assert.IsAssignableFrom<IStructuredTextGenerationClient>(session.Client);
        var output = await structured.GenerateStructuredAsync("private system", "private user", "{\"type\":\"object\"}");

        Assert.Equal("{\"kind\":\"final\"}", output);
        Assert.Equal(1, runtimeHost.StartCount);
        Assert.True(handler.RequestUri!.IsLoopback);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("local-runtime-secret", handler.AuthorizationParameter);
        Assert.Equal("installed-model-id", handler.ModelId);
        Assert.Equal("json_schema", handler.ResponseFormatType);
        var runtimeMetrics = Assert.IsAssignableFrom<ILocalRuntimeMetricsClient>(session.Client).GetRuntimeMetrics();
        Assert.Equal(321.5, runtimeMetrics?.StartupToReadyMilliseconds);
        Assert.Equal(987654, runtimeMetrics?.PeakWorkingSetBytes);
        var observation = Assert.Single(observations);
        Assert.Equal("success", observation.Outcome);
        Assert.Equal(17, observation.InputTokens);
        Assert.Equal(5, observation.OutputTokens);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(observation), StringComparison.Ordinal);
    }

    private sealed class FakeRuntimeHost : ILocalRuntimeHost, ILocalRuntimeDiagnostics
    {
        public int StartCount { get; private set; }

        public Task<LocalRuntimeEndpoint> EnsureStartedAsync(ProviderProfile profile, CancellationToken cancellationToken = default)
        {
            StartCount++;
            Assert.Equal("installed-model-id", profile.LocalModelInstallationId);
            return Task.FromResult(new LocalRuntimeEndpoint(
                new Uri("http://127.0.0.1:4321"), "local-runtime-secret", "installed-model-id"));
        }

        public void Stop() { }

        public LocalRuntimeMetrics? GetMetrics() => new(321.5, 987654);
    }

    private static ProviderProfile ManagedLocalProfile(string installationId) => new()
    {
        Id = "managed-local-candidate",
        Type = ProviderType.Local,
        Platform = ProviderPlatform.ManagedLocal,
        Protocol = ProviderProtocol.OpenAICompatible,
        ApiBase = "http://127.0.0.1:0/v1",
        Model = installationId,
        LocalModelInstallationId = installationId
    };

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }
        public string? ModelId { get; private set; }
        public string? ResponseFormatType { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            ModelId = body.RootElement.GetProperty("model").GetString();
            ResponseFormatType = body.RootElement.GetProperty("response_format").GetProperty("type").GetString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"id\":\"local-request-1\",\"choices\":[{\"message\":{\"content\":\"{\\\"kind\\\":\\\"final\\\"}\"}}],\"usage\":{\"prompt_tokens\":17,\"completion_tokens\":5}}",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
