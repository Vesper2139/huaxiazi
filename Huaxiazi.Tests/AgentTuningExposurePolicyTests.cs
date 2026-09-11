using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class AgentTuningExposurePolicyTests
{
    [Fact]
    public void Catalog_ExposesOnlySafeProductControlsByDefault()
    {
        Assert.Equal(AgentTuningExposure.Basic, AgentTuningExposurePolicy.Get("response_style").Exposure);
        Assert.Equal(AgentTuningExposure.Basic, AgentTuningExposurePolicy.Get("verbosity").Exposure);
        Assert.Equal(AgentTuningExposure.Advanced, AgentTuningExposurePolicy.Get("preferred_skills").Exposure);
        Assert.Equal(AgentTuningExposure.Advanced, AgentTuningExposurePolicy.Get("memory_consent").Exposure);
    }

    [Fact]
    public void Catalog_LocksSafetyAndArchitectureInternals()
    {
        foreach (var id in new[] { "system_prompt", "developer_prompt", "layer_weights", "safety_thresholds", "prompt_injection_rules", "test_split" })
            Assert.Equal(AgentTuningExposure.Internal, AgentTuningExposurePolicy.Get(id).Exposure);
    }

    [Fact]
    public void Get_UnknownOptionReturnsInternalFailClosedDescriptor()
    {
        var option = AgentTuningExposurePolicy.Get("unknown");

        Assert.Equal(AgentTuningExposure.Internal, option.Exposure);
        Assert.False(option.UserEditable);
    }
}
