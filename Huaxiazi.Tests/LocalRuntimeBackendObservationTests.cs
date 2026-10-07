using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class LocalRuntimeBackendObservationTests
{
    [Fact]
    public void RuntimeEndpoint_DistinguishesCandidateFromConfirmedBackend()
    {
        var candidate = new LocalRuntimeEndpoint(
            new System.Uri("http://127.0.0.1:51821"), "secret", "model", backend: "Vulkan");
        var confirmed = new LocalRuntimeEndpoint(
            new System.Uri("http://127.0.0.1:51821"), "secret", "model", backend: "Vulkan", confirmedBackend: "Vulkan");

        Assert.Equal("Vulkan", candidate.BackendCandidate);
        Assert.Null(candidate.ConfirmedBackend);
        Assert.Equal("Vulkan 候选（实际设备未确认）", candidate.DisplayBackend);
        Assert.Equal("Vulkan", confirmed.ConfirmedBackend);
        Assert.Equal("Vulkan", confirmed.DisplayBackend);
    }

    [Fact]
    public void VulkanCandidate_DoesNotConfirmVulkanFromRequestedLayerCountAlone()
    {
        var observation = new LocalRuntimeBackendObservation(useVulkanCandidate: true);

        observation.Observe("llm_load_tensors: offloaded 37/37 layers to GPU");

        Assert.Null(observation.GetConfirmedBackend());
    }

    [Fact]
    public void VulkanCandidate_ConfirmsVulkanFromPositiveVulkanDeviceBuffer()
    {
        var observation = new LocalRuntimeBackendObservation(useVulkanCandidate: true);

        observation.Observe("llm_load_tensors: offloaded 37/37 layers to GPU");
        observation.Observe("llm_load_tensors: Vulkan0 model buffer size = 2375.91 MiB");

        Assert.Equal("Vulkan", observation.GetConfirmedBackend());
    }

    [Fact]
    public void VulkanCandidate_ConfirmsCpuWhenLoaderHasNoDriversAndModelLoadsOnCpu()
    {
        var observation = new LocalRuntimeBackendObservation(useVulkanCandidate: true);

        observation.Observe("[Vulkan Loader] ERROR | DRIVER: vkCreateInstance: Found no drivers!");
        observation.Observe("llm_load_tensors: offloaded 37/37 layers to GPU");
        observation.Observe("llm_load_tensors: CPU_Mapped model buffer size = 2362.55 MiB");

        Assert.Null(observation.GetConfirmedBackend());
        observation.Observe("llama_server: model loaded");

        Assert.Equal("CPU", observation.GetConfirmedBackend());
        Assert.True(observation.VulkanDriverUnavailable);

        var endpoint = new LocalRuntimeEndpoint(
            new System.Uri("http://127.0.0.1:51821"), "secret", "model", backend: "Vulkan");
        endpoint.AttachBackendObservation(observation.GetConfirmedBackend, () => observation.VulkanDriverUnavailable);
        Assert.Equal("CPU（Vulkan 驱动不可用）", endpoint.DisplayBackend);
    }

    [Fact]
    public void VulkanCandidate_DoesNotConfirmWhenDriverErrorConflictsWithGpuBufferEvidence()
    {
        var observation = new LocalRuntimeBackendObservation(useVulkanCandidate: true);

        observation.Observe("[Vulkan Loader] ERROR | DRIVER: vkCreateInstance: Found no drivers!");
        observation.Observe("llama_server: model loaded");
        observation.Observe("llm_load_tensors: Vulkan0 model buffer size = 2375.91 MiB");

        Assert.Null(observation.GetConfirmedBackend());
    }

    [Fact]
    public void VulkanCandidate_DoesNotClaimCpuWhenRuntimeReportsZeroGpuLayers()
    {
        var observation = new LocalRuntimeBackendObservation(useVulkanCandidate: true);

        observation.Observe("llm_load_tensors: offloaded 0/37 layers to GPU");

        Assert.Null(observation.GetConfirmedBackend());
    }

    [Fact]
    public void VulkanCandidate_RemainsUnconfirmedWhenNoBackendEvidenceWasObserved()
    {
        var observation = new LocalRuntimeBackendObservation(useVulkanCandidate: true);

        observation.Observe("llama_model_loader: loaded model metadata");

        Assert.Null(observation.GetConfirmedBackend());
    }

    [Fact]
    public void CpuCandidate_IsConfirmedCpuWithoutRetainingRuntimeLogText()
    {
        var observation = new LocalRuntimeBackendObservation(useVulkanCandidate: false);

        observation.Observe("model path contains private user data");

        Assert.Equal("CPU", observation.GetConfirmedBackend());
        Assert.DoesNotContain(
            observation.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
            field => field.FieldType == typeof(string));
    }
}
