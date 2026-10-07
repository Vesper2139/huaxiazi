using System.Net;
using System.Net.Http;
using Huaxiazi.Models;
using Huaxiazi.Services;

namespace Huaxiazi.BlindEvaluationRunner;

public sealed class BlindCandidateClientSession : IDisposable
{
    private readonly IDisposable[] _resources;

    internal BlindCandidateClientSession(
        ITextGenerationClient client,
        BlindLocalArtifactProvenance? localArtifacts,
        params IDisposable[] resources)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        LocalArtifacts = localArtifacts;
        _resources = resources;
    }

    public ITextGenerationClient Client { get; }
    public BlindLocalArtifactProvenance? LocalArtifacts { get; }

    public void Dispose()
    {
        for (var index = _resources.Length - 1; index >= 0; index--)
            _resources[index].Dispose();
    }
}

public static class BlindCandidateClientFactory
{
    public static BlindCandidateClientSession Create(
        BlindCandidateRuntimeConfiguration candidate,
        IList<ProviderRequestTelemetry> observations,
        string? runtimeRoot = null,
        string? modelRoot = null) =>
        CreateCore(candidate, observations, runtimeRoot, modelRoot, null, null);

    internal static BlindCandidateClientSession CreateWithOverrides(
        BlindCandidateRuntimeConfiguration candidate,
        IList<ProviderRequestTelemetry> observations,
        ILocalRuntimeHost? runtimeHostOverride,
        HttpMessageHandler? localHandlerOverride) =>
        CreateCore(candidate, observations, null, null, runtimeHostOverride, localHandlerOverride);

    private static BlindCandidateClientSession CreateCore(
        BlindCandidateRuntimeConfiguration candidate,
        IList<ProviderRequestTelemetry> observations,
        string? runtimeRoot,
        string? modelRoot,
        ILocalRuntimeHost? runtimeHostOverride,
        HttpMessageHandler? localHandlerOverride)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(candidate.Profile);
        var profile = candidate.Profile.Clone();

        if (profile.Platform == ProviderPlatform.ManagedLocal)
        {
            if (profile.Type != ProviderType.Local || string.IsNullOrWhiteSpace(profile.LocalModelInstallationId))
                throw new InvalidOperationException("ManagedLocal 候选必须绑定已安装模型。");
            if (runtimeHostOverride is null && string.IsNullOrWhiteSpace(runtimeRoot))
                throw new InvalidOperationException("运行 ManagedLocal 候选必须显式提供 --runtime-root <runtimes/local>。");

            var resources = new List<IDisposable>();
            ILocalRuntimeHost runtimeHost;
            BlindLocalArtifactProvenance? localArtifacts = null;
            if (runtimeHostOverride is not null)
            {
                runtimeHost = runtimeHostOverride;
            }
            else
            {
                localArtifacts = BlindLocalArtifactProvenanceResolver.Resolve(
                    profile, modelRoot ?? LocalModelStore.DefaultRoot, runtimeRoot!);
                var manager = new LocalRuntimeManager(new LocalModelStore(modelRoot), runtimeRoot);
                runtimeHost = manager;
                resources.Add(manager);
            }

            var handler = localHandlerOverride ?? new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All
            };
            var httpClient = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(profile.TimeoutSeconds)
            };
            resources.Add(httpClient);
            var localClient = new LocalTextGenerationClient(runtimeHost, profile, httpClient, observations.Add);
            resources.Add(localClient);
            return new BlindCandidateClientSession(localClient, localArtifacts, resources.ToArray());
        }

        AIService.ValidateProviderProfile(profile, candidate.ApiKey);
        var httpHandler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All
        };
        var cloudOrExternalLocalClient = new AIService(profile, candidate.ApiKey, httpHandler,
            telemetryObserver: observations.Add);
        return new BlindCandidateClientSession(cloudOrExternalLocalClient, null, cloudOrExternalLocalClient);
    }
}
