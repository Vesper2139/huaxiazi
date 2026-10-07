using System;
using System.IO;
using Huaxiazi.Models;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class GenerationDiagnosticsReportServiceTests
{
    [Fact]
    public void BuildReport_UsesNearestRankPercentilesAndMarksIncompleteTokenTotalsUnknown()
    {
        var root = CreateRoot();
        var diagnostics = new GenerationDiagnosticsService(root);
        var generationId = Guid.NewGuid().ToString("N");
        var profile = Profile("model-a");
        try
        {
            diagnostics.Record(generationId, profile, ApplicationMode.Polish, new ProviderRequestTelemetry("req-1", 100, 10, 4, 200, "success", RuntimeBackend: "Vulkan"));
            diagnostics.Record(generationId, profile, ApplicationMode.Polish, new ProviderRequestTelemetry("req-2", 200, 20, 8, 200, "success", RuntimeBackend: "Vulkan"));
            diagnostics.Record(generationId, profile, ApplicationMode.Polish, new ProviderRequestTelemetry("req-3", 1000, null, 12, 429, "rate_limited", RuntimeBackend: "Vulkan"));
            diagnostics.RecordWorkflow(generationId, ApplicationMode.Polish,
                new GenerationQualityObservation("final", true, true, true, false, 0), requestCount: 3);

            var report = new GenerationDiagnosticsReportService(diagnostics).Build();

            var group = Assert.Single(report.RequestGroups);
            Assert.Equal(3, group.RequestCount);
            Assert.Equal("Vulkan", group.RuntimeBackend);
            Assert.Equal(2, group.SuccessCount);
            Assert.Equal(2d / 3, group.SuccessRate);
            Assert.Equal(200, group.P50LatencyMilliseconds);
            Assert.Equal(1000, group.P95LatencyMilliseconds);
            Assert.Equal(2, group.InputTokenObservationCount);
            Assert.Equal(1, group.InputTokenUnknownCount);
            Assert.Null(group.InputTokensTotal);
            Assert.Equal(24, group.OutputTokensTotal);
            Assert.Equal("nearest_rank", report.PercentileMethod);
            Assert.Equal("not_calculated_no_rate_card", report.CostStatus);
            Assert.Equal(3, report.SourceRequestCount);
            Assert.Equal(1, report.SourceWorkflowCount);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void BuildReport_SummarizesRoutingPolicyAndExplicitFallbackAttempts()
    {
        var root = CreateRoot();
        var diagnostics = new GenerationDiagnosticsService(root);
        var generationId = Guid.NewGuid().ToString("N");
        try
        {
            diagnostics.Record(generationId, Profile("local-model"), ApplicationMode.Polish,
                new ProviderRequestTelemetry(null, 100, 1, 1, 503, "provider_error"),
                ProviderRoutingMode.PreferLocal, "prefer-local", "primary");
            diagnostics.Record(generationId, Profile("cloud-model"), ApplicationMode.Polish,
                new ProviderRequestTelemetry(null, 150, 1, 1, 200, "success"),
                ProviderRoutingMode.PreferLocal, "prefer-local", "fallback");

            var report = new GenerationDiagnosticsReportService(diagnostics).Build();

            Assert.Equal(2, report.RoutingPolicyCounts[nameof(ProviderRoutingMode.PreferLocal)]);
            Assert.Equal(2, report.RouteReasonCounts["prefer-local"]);
            Assert.Equal(1, report.RouteAttemptCounts["primary"]);
            Assert.Equal(1, report.RouteAttemptCounts["fallback"]);
            var json = System.Text.Json.JsonSerializer.Serialize(report);
            Assert.DoesNotContain("private-profile", json, StringComparison.Ordinal);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void BuildReport_AttributesQualityToModelOnlyWhenGenerationUsedOneProviderModel()
    {
        var root = CreateRoot();
        var diagnostics = new GenerationDiagnosticsService(root);
        var singleModelGeneration = Guid.NewGuid().ToString("N");
        var fallbackGeneration = Guid.NewGuid().ToString("N");
        var first = Profile("model-a");
        var second = Profile("model-b");
        try
        {
            diagnostics.Record(singleModelGeneration, first, ApplicationMode.PromptOptimize,
                new ProviderRequestTelemetry("req-a1", 80, 1, 1, 503, "provider_error"));
            diagnostics.Record(singleModelGeneration, first, ApplicationMode.PromptOptimize,
                new ProviderRequestTelemetry("req-a2", 120, 2, 2, 200, "success"));
            diagnostics.Record(fallbackGeneration, first, ApplicationMode.PromptOptimize,
                new ProviderRequestTelemetry("req-b1", 90, 3, 3, 503, "provider_error"));
            diagnostics.Record(fallbackGeneration, second, ApplicationMode.PromptOptimize,
                new ProviderRequestTelemetry("req-b2", 150, 4, 4, 200, "success"));
            diagnostics.RecordWorkflow(singleModelGeneration, ApplicationMode.PromptOptimize,
                new GenerationQualityObservation("final", true, true, true, true, 0), requestCount: 2);
            diagnostics.RecordWorkflow(fallbackGeneration, ApplicationMode.PromptOptimize,
                new GenerationQualityObservation("blocked", false, false, false, true, 2), requestCount: 2);

            var report = new GenerationDiagnosticsReportService(diagnostics).Build();

            var taskGroup = Assert.Single(report.WorkflowGroupsByTask);
            Assert.Equal(2, taskGroup.WorkflowCount);
            Assert.Equal(2, taskGroup.QualityGateObservationCount);
            Assert.Equal(1, taskGroup.QualityGatePassedCount);
            Assert.Equal(.5, taskGroup.QualityGatePassRate);
            Assert.Equal(1, report.UnattributedWorkflowCount);
            var modelA = Assert.Single(report.RequestGroups, group => group.Model == "model-a");
            Assert.Equal(1, modelA.AttributedWorkflowCount);
            Assert.Equal(1, modelA.QualityGatePassedCount);
            var modelB = Assert.Single(report.RequestGroups, group => group.Model == "model-b");
            Assert.Equal(0, modelB.AttributedWorkflowCount);
            Assert.Null(modelB.QualityGatePassRate);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void BuildReport_SeparatesActualCpuAndVulkanBackendsForSameLocalModel()
    {
        var root = CreateRoot();
        var diagnostics = new GenerationDiagnosticsService(root);
        var profile = Profile("qwen-local");
        try
        {
            diagnostics.Record(Guid.NewGuid().ToString("N"), profile, ApplicationMode.Polish,
                new ProviderRequestTelemetry(null, 100, 1, 1, 200, "success", RuntimeBackend: "CPU"));
            diagnostics.Record(Guid.NewGuid().ToString("N"), profile, ApplicationMode.Polish,
                new ProviderRequestTelemetry(null, 50, 1, 1, 200, "success", RuntimeBackend: "Vulkan"));
            diagnostics.Record(Guid.NewGuid().ToString("N"), profile, ApplicationMode.Polish,
                new ProviderRequestTelemetry(null, 50, 1, 1, 200, "success", RuntimeBackend: "untrusted backend with local path"));

            var groups = new GenerationDiagnosticsReportService(diagnostics).Build().RequestGroups;

            Assert.Equal(3, groups.Count);
            Assert.Contains(groups, group => group.Model == "qwen-local" && group.RuntimeBackend == "CPU" && group.RequestCount == 1);
            Assert.Contains(groups, group => group.Model == "qwen-local" && group.RuntimeBackend == "Vulkan" && group.RequestCount == 1);
            Assert.Contains(groups, group => group.Model == "qwen-local" && group.RuntimeBackend is null && group.RequestCount == 1);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void BuildReport_SummarizesLocalStartupMemoryAndApproximateOutputRate()
    {
        var root = CreateRoot();
        var diagnostics = new GenerationDiagnosticsService(root);
        var profile = Profile("qwen-local");
        try
        {
            diagnostics.Record(Guid.NewGuid().ToString("N"), profile, ApplicationMode.Polish,
                new ProviderRequestTelemetry(null, 1000, 40, 10, 200, "success", RuntimeBackend: "CPU",
                    RuntimeStartupToReadyMilliseconds: 1200, RuntimePeakWorkingSetBytes: 900_000_000,
                    IsFirstRequestAfterRuntimeStart: true));
            diagnostics.Record(Guid.NewGuid().ToString("N"), profile, ApplicationMode.Polish,
                new ProviderRequestTelemetry(null, 500, 20, 10, 200, "success", RuntimeBackend: "CPU",
                    RuntimePeakWorkingSetBytes: 1_100_000_000, IsFirstRequestAfterRuntimeStart: false));
            var cloudProfile = Profile("cloud-model");
            cloudProfile.Type = ProviderType.Cloud;
            cloudProfile.Platform = ProviderPlatform.OpenAI;
            diagnostics.Record(Guid.NewGuid().ToString("N"), cloudProfile, ApplicationMode.Polish,
                new ProviderRequestTelemetry(null, 1000, 40, 10, 200, "success", RuntimeBackend: "CPU",
                    RuntimeStartupToReadyMilliseconds: 1200, RuntimePeakWorkingSetBytes: 900_000_000,
                    IsFirstRequestAfterRuntimeStart: true));

            var groups = new GenerationDiagnosticsReportService(diagnostics).Build().RequestGroups;
            var local = Assert.Single(groups, group => group.Model == "qwen-local");
            var cloud = Assert.Single(groups, group => group.Model == "cloud-model");

            Assert.Equal(1, local.RuntimeStartupObservationCount);
            Assert.Equal(1200, local.P50RuntimeStartupToReadyMilliseconds);
            Assert.Equal(1200, local.P95RuntimeStartupToReadyMilliseconds);
            Assert.Equal(2, local.RuntimeWorkingSetObservationCount);
            Assert.Equal(1_100_000_000, local.RuntimePeakWorkingSetBytes);
            Assert.Equal(2, local.OutputRateObservationCount);
            Assert.Equal(10, local.P50OutputTokensPerSecond);
            Assert.Equal(20, local.P95OutputTokensPerSecond);
            Assert.Equal(1, local.FirstRequestAfterRuntimeStartCount);
            Assert.Equal(1000, local.P50FirstRequestLatencyMilliseconds);
            Assert.Equal(1, local.SubsequentRuntimeRequestCount);
            Assert.Equal(500, local.P50SubsequentRequestLatencyMilliseconds);
            Assert.Equal(0, local.UnclassifiedLocalRequestCount);
            Assert.Equal(0, cloud.OutputRateObservationCount);
            Assert.Null(cloud.P50OutputTokensPerSecond);
            Assert.Equal(0, cloud.RuntimeStartupObservationCount);
            Assert.Equal(0, cloud.RuntimeWorkingSetObservationCount);
            Assert.Equal(0, cloud.FirstRequestAfterRuntimeStartCount);
            Assert.Equal(0, cloud.UnclassifiedLocalRequestCount);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void BuildReport_CountsLocalContextPreflightOutcomesWithoutCountingCloudOrLegacyRequests()
    {
        var root = CreateRoot();
        var diagnostics = new GenerationDiagnosticsService(root);
        var local = Profile("local-model");
        try
        {
            diagnostics.Record(Guid.NewGuid().ToString("N"), local, ApplicationMode.Polish,
                new ProviderRequestTelemetry(null, 20, 20, 5, 200, "success", RuntimeBackend: "CPU", ContextPreflightOutcome: "counted"));
            diagnostics.Record(Guid.NewGuid().ToString("N"), local, ApplicationMode.Polish,
                new ProviderRequestTelemetry(null, 20, 20, 5, 200, "success", RuntimeBackend: "CPU", ContextPreflightOutcome: "unavailable"));
            diagnostics.Record(Guid.NewGuid().ToString("N"), local, ApplicationMode.Polish,
                new ProviderRequestTelemetry(null, 20, 3000, null, 200, "context_limit_exceeded", RuntimeBackend: "CPU", ContextPreflightOutcome: "exceeded"));
            diagnostics.Record(Guid.NewGuid().ToString("N"), local, ApplicationMode.Polish,
                new ProviderRequestTelemetry(null, 20, 20, 5, 200, "success", RuntimeBackend: "CPU"));
            var cloud = Profile("cloud-model");
            cloud.Type = ProviderType.Cloud;
            cloud.Platform = ProviderPlatform.OpenAI;
            diagnostics.Record(Guid.NewGuid().ToString("N"), cloud, ApplicationMode.Polish,
                new ProviderRequestTelemetry(null, 20, 20, 5, 200, "success", ContextPreflightOutcome: "counted"));

            var groups = new GenerationDiagnosticsReportService(diagnostics).Build().RequestGroups;
            var localGroup = Assert.Single(groups, group => group.Model == "local-model");
            var cloudGroup = Assert.Single(groups, group => group.Model == "cloud-model");

            Assert.Equal(3, localGroup.ContextPreflightOutcomeCounts["counted"] +
                localGroup.ContextPreflightOutcomeCounts["unavailable"] + localGroup.ContextPreflightOutcomeCounts["exceeded"]);
            Assert.Equal(1, localGroup.ContextPreflightOutcomeCounts["counted"]);
            Assert.Equal(1, localGroup.ContextPreflightOutcomeCounts["unavailable"]);
            Assert.Equal(1, localGroup.ContextPreflightOutcomeCounts["exceeded"]);
            Assert.Empty(cloudGroup.ContextPreflightOutcomeCounts);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void WriteSummary_CreatesLocalReport_AndClearRemovesIt()
    {
        var root = CreateRoot();
        var diagnostics = new GenerationDiagnosticsService(root);
        try
        {
            var writtenPath = new GenerationDiagnosticsReportService(diagnostics).WriteSummary();

            Assert.Equal(diagnostics.SummaryFilePath, writtenPath);
            Assert.True(File.Exists(writtenPath));
            Assert.Contains("requestGroups", File.ReadAllText(writtenPath), StringComparison.Ordinal);
            diagnostics.Clear();
            Assert.False(File.Exists(writtenPath));
        }
        finally { TryDelete(root); }
    }

    private static ProviderProfile Profile(string model) => new()
    {
        Type = ProviderType.Local,
        Platform = ProviderPlatform.ManagedLocal,
        Protocol = ProviderProtocol.OpenAICompatible,
        Model = model
    };

    private static string CreateRoot()
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), "out", "test-artifacts", "generation-diagnostics-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
    }
}
