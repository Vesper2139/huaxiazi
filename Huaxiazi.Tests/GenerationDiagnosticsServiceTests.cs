using System;
using System.IO;
using System.Text.Json;
using Huaxiazi.Models;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class GenerationDiagnosticsServiceTests
{
    [Fact]
    public void Record_WritesOnlyBoundedContentFreeMetadata_AndClearRemovesIt()
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), "out", "test-artifacts", "generation-diagnostics-" + Guid.NewGuid().ToString("N"));
        var service = new GenerationDiagnosticsService(root, maxEntries: 3);
        var profile = new ProviderProfile
        {
            Id = "private-profile-name",
            Name = "私有配置名",
            ApiBase = "https://private.example/path?token=private-secret",
            Model = "qwen-local"
        };

        try
        {
            service.Record(profile, ApplicationMode.Polish, new ProviderRequestTelemetry("req-123", 1250.5, 100, 25, 200, "success", 10, 2));
            service.Record(profile, ApplicationMode.Polish, new ProviderRequestTelemetry("req-456", 1500, 120, 30, 200, "success"));
            service.Record(profile, ApplicationMode.PromptOptimize, new ProviderRequestTelemetry("req-789", 1750, 140, 35, 429, "rate_limited"));
            service.Record(profile, ApplicationMode.PromptOptimize, new ProviderRequestTelemetry("req-888", 1800, 150, 40, 200, "success"));
            service.Record(profile, ApplicationMode.PromptOptimize, new ProviderRequestTelemetry("req-999", 1900, 160, 45, 200, "success"));

            var entries = service.ReadRecent();
            Assert.Equal(3, entries.Count);
            Assert.Equal("req-789", entries[0].RequestId);
            Assert.Equal("req-999", entries[2].RequestId);
            Assert.Equal("qwen-local", entries[0].Model);
            Assert.Equal(nameof(ApplicationMode.PromptOptimize), entries[0].Task);
            Assert.Equal("rate_limited", entries[0].Outcome);

            var json = File.ReadAllText(service.FilePath);
            Assert.DoesNotContain("private-profile-name", json, StringComparison.Ordinal);
            Assert.DoesNotContain("私有配置名", json, StringComparison.Ordinal);
            Assert.DoesNotContain("private.example", json, StringComparison.Ordinal);
            Assert.DoesNotContain("private-secret", json, StringComparison.Ordinal);
            Assert.DoesNotContain("contextPreflightOutcome", json, StringComparison.Ordinal);
            Assert.DoesNotContain("systemPrompt", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("userInput", json, StringComparison.OrdinalIgnoreCase);

            service.Clear();
            Assert.Empty(service.ReadRecent());
            Assert.False(File.Exists(service.FilePath));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Record_NormalizesUntrustedMetadataAndRejectsInvalidOutcome()
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), "out", "test-artifacts", "generation-diagnostics-" + Guid.NewGuid().ToString("N"));
        var service = new GenerationDiagnosticsService(root);
        try
        {
            service.Record(new ProviderProfile { Model = "bad\r\nmodel" }, ApplicationMode.Polish, new ProviderRequestTelemetry("bad\r\nid", double.NaN, -1, 2, 999, "provider returned raw body"));

            var entry = Assert.Single(service.ReadRecent());
            Assert.Equal("bad model", entry.Model);
            Assert.Null(entry.RequestId);
            Assert.Null(entry.LatencyMilliseconds);
            Assert.Null(entry.InputTokens);
            Assert.Null(entry.HttpStatusCode);
            Assert.Equal("unknown", entry.Outcome);
            Assert.DoesNotContain("provider returned raw body", File.ReadAllText(service.FilePath), StringComparison.Ordinal);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Record_PreservesContextLimitOutcomeAsNormalizedCategory()
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), "out", "test-artifacts", "generation-diagnostics-context-limit-" + Guid.NewGuid().ToString("N"));
        var service = new GenerationDiagnosticsService(root);
        try
        {
            service.Record(new ProviderProfile { Model = "claude-sonnet-4-5" }, ApplicationMode.Polish,
                new ProviderRequestTelemetry("req-context-1", 90, null, null, 200, "context_limit_exceeded"));

            Assert.Equal("context_limit_exceeded", Assert.Single(service.ReadRecent()).Outcome);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Record_StoresOnlyWhitelistedContextPreflightOutcomesForManagedLocal()
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), "out", "test-artifacts", "generation-diagnostics-preflight-" + Guid.NewGuid().ToString("N"));
        var service = new GenerationDiagnosticsService(root);
        var local = new ProviderProfile { Type = ProviderType.Local, Platform = ProviderPlatform.ManagedLocal, Model = "local-model" };
        var cloud = new ProviderProfile { Type = ProviderType.Cloud, Platform = ProviderPlatform.OpenAI, Model = "cloud-model" };
        try
        {
            service.Record(local, ApplicationMode.Polish,
                new ProviderRequestTelemetry(null, 15, 20, 5, 200, "success", RuntimeBackend: "CPU", ContextPreflightOutcome: "counted"));
            service.Record(local, ApplicationMode.Polish,
                new ProviderRequestTelemetry(null, 15, 20, 5, 200, "success", RuntimeBackend: "CPU", ContextPreflightOutcome: "untrusted-private-value"));
            service.Record(cloud, ApplicationMode.Polish,
                new ProviderRequestTelemetry(null, 15, 20, 5, 200, "success", ContextPreflightOutcome: "counted"));

            var entries = service.ReadRecent();

            Assert.Equal("counted", entries[0].ContextPreflightOutcome);
            Assert.Null(entries[1].ContextPreflightOutcome);
            Assert.Null(entries[2].ContextPreflightOutcome);
            var json = File.ReadAllText(service.FilePath);
            Assert.DoesNotContain("untrusted-private-value", json, StringComparison.Ordinal);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void AppSettings_LocalGenerationDiagnosticsAreDisabledByDefaultAndRoundTripWhenEnabled()
    {
        Assert.False(new AppSettings().LocalGenerationDiagnosticsEnabled);
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"localGenerationDiagnosticsEnabled\":true}");
        Assert.NotNull(settings);
        Assert.True(settings.LocalGenerationDiagnosticsEnabled);
    }

    [Fact]
    public void AppTelemetryObserver_StaysOffByDefaultAndRecordsTaskWhenUserOptsIn()
    {
        var original = App.Settings.Clone();
        var root = Path.Combine(Directory.GetCurrentDirectory(), "out", "test-artifacts", "generation-diagnostics-app-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            App.ReplaceSettings(new AppSettings { DataDirectory = root });
            var profile = new ProviderProfile { Model = "test-model", ApiBase = "https://private.example" };
            Assert.Null(App.CreateGenerationTelemetryObserver(profile, ApplicationMode.Polish));

            App.ReplaceSettings(new AppSettings { DataDirectory = root, LocalGenerationDiagnosticsEnabled = true });
            var observer = Assert.IsType<Action<ProviderRequestTelemetry>>(
                App.CreateGenerationTelemetryObserver(profile, ApplicationMode.PromptOptimize));
            observer(new ProviderRequestTelemetry("request-1", 250, 50, 10, 200, "success"));

            var service = new GenerationDiagnosticsService(Path.Combine(root, "diagnostics"));
            var entry = Assert.Single(service.ReadRecent());
            Assert.Equal(nameof(ApplicationMode.PromptOptimize), entry.Task);
            Assert.Equal("test-model", entry.Model);

            App.ReplaceSettings(new AppSettings { DataDirectory = root, LocalGenerationDiagnosticsEnabled = true, IncognitoMode = true });
            Assert.Null(App.CreateGenerationTelemetryObserver(profile, ApplicationMode.Polish));
        }
        finally
        {
            App.ReplaceSettings(original);
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void DiagnosticsScope_CorrelatesRequestAndWorkflowGateWithoutSavingIssueText()
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), "out", "test-artifacts", "generation-diagnostics-scope-" + Guid.NewGuid().ToString("N"));
        var service = new GenerationDiagnosticsService(root);
        try
        {
            var scope = new GenerationDiagnosticsScope(service, ApplicationMode.Polish);
            scope.RecordRequest(new ProviderProfile { Model = "model-a" }, new ProviderRequestTelemetry("req-a", 100, 20, 5, 200, "success"));
            scope.Complete(new GenerationQualityObservation("final", true, true, true, false, 0));

            var request = Assert.Single(service.ReadRecent());
            var workflow = Assert.Single(service.ReadWorkflowRecent());
            Assert.Equal(scope.GenerationId, request.GenerationId);
            Assert.Equal(request.GenerationId, workflow.GenerationId);
            Assert.Equal(nameof(ApplicationMode.Polish), workflow.Task);
            Assert.Equal("final", workflow.Outcome);
            Assert.True(workflow.OutputContractValid);
            Assert.True(workflow.QualityGatePassed);
            Assert.Equal(1, workflow.RequestCount);
            scope.Complete(new GenerationQualityObservation("blocked", false, false, false, true, 3));
            Assert.Single(service.ReadWorkflowRecent());
            service.Clear();
            Assert.Empty(service.ReadRecent());
            Assert.Empty(service.ReadWorkflowRecent());
            Assert.False(File.Exists(service.FilePath));
            Assert.False(File.Exists(service.WorkflowFilePath));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void DiagnosticsScope_RecordsContentFreeRoutingPolicyAndFallbackAttempt()
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), "out", "test-artifacts", "generation-diagnostics-route-" + Guid.NewGuid().ToString("N"));
        var service = new GenerationDiagnosticsService(root);
        try
        {
            var scope = new GenerationDiagnosticsScope(
                service,
                ApplicationMode.Polish,
                routingMode: ProviderRoutingMode.PreferLocal,
                routeReason: "prefer-local",
                primaryProfileId: "private-local-profile",
                fallbackProfileId: "private-cloud-profile");
            scope.RecordRequest(new ProviderProfile { Id = "private-local-profile", Name = "本地私人配置", Model = "local-model" },
                new ProviderRequestTelemetry(null, 25, null, null, null, "network_error"));
            scope.RecordRequest(new ProviderProfile { Id = "private-cloud-profile", Name = "云端私人配置", Model = "cloud-model" },
                new ProviderRequestTelemetry(null, 50, null, null, 200, "success"));

            var entries = service.ReadRecent();
            Assert.Equal(2, entries.Count);
            Assert.All(entries, entry =>
            {
                Assert.Equal(nameof(ProviderRoutingMode.PreferLocal), entry.RoutingMode);
                Assert.Equal("prefer-local", entry.RouteReason);
            });
            Assert.Equal("primary", entries[0].RouteAttempt);
            Assert.Equal("fallback", entries[1].RouteAttempt);

            var json = File.ReadAllText(service.FilePath);
            Assert.DoesNotContain("private-local-profile", json, StringComparison.Ordinal);
            Assert.DoesNotContain("private-cloud-profile", json, StringComparison.Ordinal);
            Assert.DoesNotContain("本地私人配置", json, StringComparison.Ordinal);
            Assert.DoesNotContain("云端私人配置", json, StringComparison.Ordinal);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [Theory]
    [InlineData("automatic-fast")]
    [InlineData("automatic-balanced")]
    [InlineData("automatic-reasoning")]
    public void DiagnosticsScope_RecordsAutomaticTierReasonWithoutRequestContent(string routeReason)
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), "out", "test-artifacts", "generation-diagnostics-automatic-" + Guid.NewGuid().ToString("N"));
        var service = new GenerationDiagnosticsService(root);
        try
        {
            var scope = new GenerationDiagnosticsScope(
                service,
                ApplicationMode.Polish,
                routingMode: ProviderRoutingMode.Automatic,
                routeReason: routeReason);
            scope.RecordRequest(new ProviderProfile { Id = "private-profile", Name = "私人配置", Model = "test-model" },
                new ProviderRequestTelemetry(null, 25, null, null, 200, "success"));

            var entry = Assert.Single(service.ReadRecent());
            Assert.Equal(nameof(ProviderRoutingMode.Automatic), entry.RoutingMode);
            Assert.Equal(routeReason, entry.RouteReason);

            var json = File.ReadAllText(service.FilePath);
            Assert.DoesNotContain("private-profile", json, StringComparison.Ordinal);
            Assert.DoesNotContain("私人配置", json, StringComparison.Ordinal);
            Assert.DoesNotContain("用户输入文本", json, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DiagnosticsScope_SeparatesAutomaticPrimaryAndConfiguredFallbackAttempts()
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), "out", "test-artifacts", "generation-diagnostics-automatic-fallback-" + Guid.NewGuid().ToString("N"));
        var service = new GenerationDiagnosticsService(root);
        try
        {
            var scope = new GenerationDiagnosticsScope(
                service,
                ApplicationMode.PromptOptimize,
                routingMode: ProviderRoutingMode.Automatic,
                routeReason: "automatic-reasoning",
                primaryProfileId: "primary-profile",
                fallbackProfileId: "fallback-profile");
            scope.RecordRequest(new ProviderProfile { Id = "primary-profile", Model = "reasoning-model" },
                new ProviderRequestTelemetry(null, 25, null, null, 503, "provider_error"));
            scope.RecordRequest(new ProviderProfile { Id = "fallback-profile", Model = "backup-model" },
                new ProviderRequestTelemetry(null, 40, null, null, 200, "success"));

            var entries = service.ReadRecent();
            Assert.Equal(2, entries.Count);
            Assert.All(entries, entry =>
            {
                Assert.Equal(nameof(ProviderRoutingMode.Automatic), entry.RoutingMode);
                Assert.Equal("automatic-reasoning", entry.RouteReason);
            });
            Assert.Equal(new[] { "primary", "fallback" }, entries.Select(entry => entry.RouteAttempt));
            Assert.DoesNotContain("primary-profile", File.ReadAllText(service.FilePath), StringComparison.Ordinal);
            Assert.DoesNotContain("fallback-profile", File.ReadAllText(service.FilePath), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DiagnosticsScope_StorageFailureNeverEscapesIntoGenerationFlow()
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), "out", "test-artifacts", "generation-diagnostics-not-directory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(root)!);
        File.WriteAllText(root, "not a directory");
        try
        {
            var scope = new GenerationDiagnosticsScope(new GenerationDiagnosticsService(root), ApplicationMode.Polish);
            var exception = Record.Exception(() =>
            {
                scope.RecordRequest(new ProviderProfile(), new ProviderRequestTelemetry("req-a", 10, null, null, 200, "success"));
                scope.Complete(new GenerationQualityObservation("final", true, true, true, false, 0));
            });

            Assert.Null(exception);
            Assert.Equal(1, scope.RequestCount);
        }
        finally
        {
            try { if (File.Exists(root)) File.Delete(root); } catch { }
        }
    }

    [Fact]
    public void DiagnosticsScope_StopsWritingWhenUserDisablesCollectionDuringGeneration()
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), "out", "test-artifacts", "generation-diagnostics-disabled-" + Guid.NewGuid().ToString("N"));
        var service = new GenerationDiagnosticsService(root);
        var enabled = true;
        try
        {
            var scope = new GenerationDiagnosticsScope(service, ApplicationMode.Polish, () => enabled);
            scope.RecordRequest(new ProviderProfile(), new ProviderRequestTelemetry("req-before", 10, null, null, 200, "success"));
            enabled = false;
            scope.RecordRequest(new ProviderProfile(), new ProviderRequestTelemetry("req-after", 10, null, null, 200, "success"));
            scope.Complete(new GenerationQualityObservation("final", true, true, true, false, 0));

            Assert.Single(service.ReadRecent());
            Assert.Empty(service.ReadWorkflowRecent());
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }
}
