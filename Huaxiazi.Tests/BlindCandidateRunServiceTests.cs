using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Huaxiazi.BlindEvaluationRunner;
using Huaxiazi.DatasetBuilder;
using Huaxiazi.Models;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class BlindCandidateRunServiceTests
{
    [Theory]
    [InlineData("frozen_test")]
    [InlineData("all")]
    public async Task RunAsync_RequiresFrozenTestConfirmationAtServiceBoundaryBeforeWritingOrCallingProvider(string split)
    {
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "blind-run-frozen-confirmation-" + Guid.NewGuid().ToString("N"));
        var reviewerDirectory = Path.Combine(root, "reviewer");
        var sealedPath = Path.Combine(root, "custodian", "candidate-map.json");
        Directory.CreateDirectory(root);
        var observations = new List<ProviderRequestTelemetry>();
        using var client = new CapturingClient(observations);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => BlindCandidateRunService.RunAsync(
            [Record() with { Split = "frozen_test" }], LocalConfiguration(), reviewerDirectory, sealedPath,
            new string('a', 64), split, [], client));

        Assert.Contains("frozen_test", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, client.CallCount);
        Assert.False(Directory.Exists(reviewerDirectory));
        Assert.False(File.Exists(sealedPath));
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task RunAsync_RejectsMissingHardwareProfileBeforeWritingOrCallingProvider()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "blind-run-hardware-profile-" + Guid.NewGuid().ToString("N"));
        var reviewerDirectory = Path.Combine(root, "reviewer");
        var sealedPath = Path.Combine(root, "custodian", "candidate-map.json");
        Directory.CreateDirectory(root);
        using var client = new CapturingClient([]);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => BlindCandidateRunService.RunAsync(
            [Record()], LocalConfiguration(), reviewerDirectory, sealedPath,
            new string('a', 64), "development", [], client, hardwareProfile: " "));

        Assert.Contains("hardware", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, client.CallCount);
        Assert.False(Directory.Exists(reviewerDirectory));
        Assert.False(File.Exists(sealedPath));
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task RunAsync_RunsFrozenTestOnlyAfterExplicitServiceConfirmation()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "blind-run-frozen-confirmed-" + Guid.NewGuid().ToString("N"));
        var reviewerDirectory = Path.Combine(root, "reviewer");
        var sealedPath = Path.Combine(root, "custodian", "candidate-map.json");
        Directory.CreateDirectory(root);
        var observations = new List<ProviderRequestTelemetry>();
        using var client = new CapturingClient(observations);

        try
        {
            var result = await BlindCandidateRunService.RunAsync(
                [Record() with { Split = "frozen_test" }], LocalConfiguration(), reviewerDirectory, sealedPath,
                new string('a', 64), "frozen_test", observations, client, confirmFrozenTestLocked: true, hardwareProfile: "test-host");

            Assert.Equal(1, result.SampleCount);
            Assert.Equal(1, result.ProviderRequestCount);
            Assert.Equal(1, client.CallCount);
            using var sealedManifest = JsonDocument.Parse(File.ReadAllText(sealedPath));
            var outputContractHash = sealedManifest.RootElement.GetProperty("output_contract_bundle_sha256").GetString();
            Assert.Matches("^[a-f0-9]{64}$", outputContractHash!);
            Assert.Equal(2, sealedManifest.RootElement.GetProperty("manifest_version").GetInt32());
            Assert.Equal("frozen_test", sealedManifest.RootElement.GetProperty("split").GetString());
            Assert.True(sealedManifest.RootElement.GetProperty("frozen_test_unlock_confirmed").GetBoolean());
            using var runInfo = JsonDocument.Parse(File.ReadAllText(Path.Combine(reviewerDirectory, "run-info.json")));
            Assert.Equal(2, runInfo.RootElement.GetProperty("run_info_version").GetInt32());
            Assert.Equal(outputContractHash, runInfo.RootElement.GetProperty("output_contract_bundle_sha256").GetString());
            Assert.True(runInfo.RootElement.GetProperty("frozen_test_unlock_confirmed").GetBoolean());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_RejectsRecordsThatDoNotMatchRequestedSplitBeforeWritingOrCallingProvider()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "blind-run-split-mismatch-" + Guid.NewGuid().ToString("N"));
        var reviewerDirectory = Path.Combine(root, "reviewer");
        var sealedPath = Path.Combine(root, "custodian", "candidate-map.json");
        Directory.CreateDirectory(root);
        using var client = new CapturingClient([]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => BlindCandidateRunService.RunAsync(
            [Record()], LocalConfiguration(), reviewerDirectory, sealedPath,
            new string('a', 64), "frozen_test", [], client, confirmFrozenTestLocked: true));

        Assert.Contains("split", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, client.CallCount);
        Assert.False(Directory.Exists(reviewerDirectory));
        Assert.False(File.Exists(sealedPath));
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task RunAsync_ClassifiesUnauthorizedExecutionFailureAsRunnerError()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "blind-run-execution-error-" + Guid.NewGuid().ToString("N"));
        var reviewerDirectory = Path.Combine(root, "reviewer");
        var sealedPath = Path.Combine(root, "custodian", "candidate-map.json");
        Directory.CreateDirectory(root);
        using var client = new CapturingClient([], generationException: new UnauthorizedAccessException("simulated access failure"));

        try
        {
            await BlindCandidateRunService.RunAsync([Record()], LocalConfiguration(), reviewerDirectory, sealedPath,
                new string('f', 64), "development", [], client, hardwareProfile: "test-host");

            var predictionJson = File.ReadAllText(Path.Combine(reviewerDirectory, "predictions.jsonl"));
            using var prediction = JsonDocument.Parse(predictionJson);
            Assert.Equal("error", prediction.RootElement.GetProperty("status").GetString());
            Assert.Equal("runner_error", prediction.RootElement.GetProperty("telemetry").GetProperty("error_category").GetString());
            Assert.Equal(1, client.CallCount);
            var parsedPrediction = JsonSerializer.Deserialize<BlindEvaluationPrediction>(predictionJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })!;
            var score = BlindEvaluationScorer.Evaluate([Record()], [parsedPrediction]);
            Assert.Empty(score.Issues);
            Assert.Equal(1, Assert.Single(score.Candidates).ErrorCountsByCategory["runner_error"]);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_ClassifiesWorkflowBlockedResultAsFailedAttempt()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "blind-run-workflow-blocked-" + Guid.NewGuid().ToString("N"));
        var reviewerDirectory = Path.Combine(root, "reviewer");
        var sealedPath = Path.Combine(root, "custodian", "candidate-map.json");
        Directory.CreateDirectory(root);
        var record = Record() with { Task = "prompt_optimize" };
        var votes = record.HumanReview.IndependentAnnotations!;
        record = record with
        {
            HumanReview = record.HumanReview with
            {
                IndependentAnnotations = votes.Select(vote => vote with
                {
                    Labels = vote.Labels with { Task = "prompt_optimize" }
                }).ToArray()
            }
        };
        using var client = new CapturingClient([], response: "");

        try
        {
            await BlindCandidateRunService.RunAsync([record], LocalConfiguration(), reviewerDirectory, sealedPath,
                new string('a', 64), "development", [], client, hardwareProfile: "test-host");

            var predictionJson = File.ReadAllText(Path.Combine(reviewerDirectory, "predictions.jsonl"));
            using var prediction = JsonDocument.Parse(predictionJson);
            Assert.Equal("error", prediction.RootElement.GetProperty("status").GetString());
            Assert.False(prediction.RootElement.GetProperty("schema_valid").GetBoolean());
            Assert.Equal("workflow_rejected", prediction.RootElement.GetProperty("telemetry").GetProperty("error_category").GetString());

            var parsedPrediction = JsonSerializer.Deserialize<BlindEvaluationPrediction>(predictionJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })!;
            var score = BlindEvaluationScorer.Evaluate([record], [parsedPrediction]);
            Assert.Empty(score.Issues);
            Assert.Equal(1, Assert.Single(score.Candidates).FailedRequests);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_RejectsGoldWithoutCurrentHumanReviewEvidenceBeforeWritingArtifacts()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "blind-run-invalid-review-" + Guid.NewGuid().ToString("N"));
        var reviewerDirectory = Path.Combine(root, "reviewer");
        var sealedPath = Path.Combine(root, "custodian", "candidate-map.json");
        Directory.CreateDirectory(root);
        using var client = new CapturingClient([]);
        var invalidRecord = Record() with { HumanReview = new(["reviewer-a", "reviewer-b"], "accepted") };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => BlindCandidateRunService.RunAsync(
            [invalidRecord], LocalConfiguration(), reviewerDirectory, sealedPath, new string('a', 64), "all", [], client));

        Assert.Contains("human_review", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(reviewerDirectory));
        Assert.False(File.Exists(sealedPath));
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task RunAsync_RejectsPIIInGoldBeforeWritingArtifacts()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "blind-run-pii-" + Guid.NewGuid().ToString("N"));
        var reviewerDirectory = Path.Combine(root, "reviewer");
        var sealedPath = Path.Combine(root, "custodian", "candidate-map.json");
        Directory.CreateDirectory(root);
        using var client = new CapturingClient([]);
        var sensitiveRecord = Record() with { Input = "请联系 13800138000" };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => BlindCandidateRunService.RunAsync(
            [sensitiveRecord], LocalConfiguration(), reviewerDirectory, sealedPath, new string('a', 64), "all", [], client));

        Assert.Contains("pii", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(reviewerDirectory));
        Assert.False(File.Exists(sealedPath));
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task RunAsync_WritesBlindPredictionsAndKeepsCandidateIdentityInSeparateSealedFile()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "blind-run-service-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(root, "reviewer-run");
        var sealedPath = Path.Combine(root, "custodian", "candidate-map.json");
        Directory.CreateDirectory(root);
        var observations = new List<ProviderRequestTelemetry>();
        using var client = new CapturingClient(observations);
        var config = new BlindCandidateRuntimeConfiguration(new ProviderProfile
        {
            Id = "local-qwen",
            Name = "Qwen local baseline",
            Type = ProviderType.Local,
            Platform = ProviderPlatform.Ollama,
            ApiBase = "http://localhost:11434/v1",
            Model = "qwen3:4b"
        }, string.Empty, string.Empty);

        try
        {
            var result = await BlindCandidateRunService.RunAsync(
                [Record()], config, output, sealedPath, "a".PadLeft(64, 'a'), "development", observations, client,
                hardwareProfile: "win-x64-cpu-16gb");

            Assert.Matches("^candidate-[a-f0-9]{16}$", result.CandidateId);
            Assert.Equal(1, result.ProviderRequestCount);
            using var prediction = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "predictions.jsonl")));
            Assert.Equal(result.CandidateId, prediction.RootElement.GetProperty("candidate_id").GetString());
            Assert.Equal("success", prediction.RootElement.GetProperty("status").GetString());
            Assert.DoesNotContain("qwen3", File.ReadAllText(Path.Combine(output, "predictions.jsonl")), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Qwen local baseline", File.ReadAllText(Path.Combine(output, "predictions.jsonl")), StringComparison.Ordinal);
            Assert.DoesNotContain("GOLD_SECRET", File.ReadAllText(Path.Combine(output, "predictions.jsonl")), StringComparison.Ordinal);
            Assert.Contains("qwen3:4b", File.ReadAllText(sealedPath), StringComparison.Ordinal);
            using var sealedHardware = JsonDocument.Parse(File.ReadAllText(sealedPath));
            Assert.Equal("win-x64-cpu-16gb", sealedHardware.RootElement.GetProperty("hardware_profile").GetString());
            using var runHardware = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "run-info.json")));
            Assert.Equal("win-x64-cpu-16gb", runHardware.RootElement.GetProperty("hardware_profile").GetString());
            Assert.DoesNotContain("qwen3", File.ReadAllText(Path.Combine(output, "run-info.json")), StringComparison.OrdinalIgnoreCase);
            using var predictionTelemetry = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "predictions.jsonl")));
            Assert.Equal(200, predictionTelemetry.RootElement.GetProperty("telemetry").GetProperty("local_tokens_per_second").GetDouble());
            var requestTelemetryText = File.ReadAllText(Path.Combine(output, "request-telemetry.jsonl"));
            Assert.Contains("request-1", requestTelemetryText, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-answer", requestTelemetryText, StringComparison.Ordinal);
            Assert.Equal(1, client.CallCount);
            Assert.True(File.Exists(Path.Combine(output, "request-telemetry.jsonl")));
            await Assert.ThrowsAsync<IOException>(() => BlindCandidateRunService.RunAsync(
                [Record()], config, output, sealedPath, "a".PadLeft(64, 'a'), "development", observations, client,
                hardwareProfile: "win-x64-cpu-16gb"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_WritesRequestUsageAndManagedLocalRuntimeMetricsWithoutContent()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "blind-run-local-metrics-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(root, "reviewer-run");
        var sealedPath = Path.Combine(root, "custodian", "candidate-map.json");
        var observations = new List<ProviderRequestTelemetry>();
        using var client = new CapturingClient(observations, runtimeMetrics: new LocalRuntimeMetrics(321.5, 987654));
        try
        {
            await BlindCandidateRunService.RunAsync([Record()], LocalConfiguration(), output, sealedPath,
                new string('e', 64), "development", observations, client, hardwareProfile: "test-host");

            using var prediction = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "predictions.jsonl")));
            var telemetry = prediction.RootElement.GetProperty("telemetry");
            Assert.Equal(10, telemetry.GetProperty("latency_milliseconds").GetDouble());
            Assert.Equal(987654, telemetry.GetProperty("local_peak_memory_bytes").GetInt64());
            Assert.Equal(321.5, telemetry.GetProperty("local_model_load_milliseconds").GetDouble());
            Assert.Equal(200, telemetry.GetProperty("local_tokens_per_second").GetDouble());
            var requestTelemetry = File.ReadAllText(Path.Combine(output, "request-telemetry.jsonl"));
            Assert.Contains("request-1", requestTelemetry, StringComparison.Ordinal);
            Assert.DoesNotContain("你好", requestTelemetry, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_RefusesToPlaceSealedCandidateMapInsideReviewerDirectory()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "blind-run-guard-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(root, "reviewer-run");
        Directory.CreateDirectory(root);
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => BlindCandidateRunService.RunAsync(
                [Record()], LocalConfiguration(), output, Path.Combine(output, "candidate-map.json"),
                "b".PadLeft(64, 'b'), "development", [], new CapturingClient([])));

            Assert.Contains("评审输出目录之外", exception.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(output));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_WithholdsCandidateOutputThatTriggersSensitiveDataHeuristics()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "blind-run-pii-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(root, "reviewer-run");
        var sealedPath = Path.Combine(root, "custodian", "candidate-map.json");
        Directory.CreateDirectory(root);
        using var client = new CapturingClient([], "{\"kind\":\"final\",\"content\":\"联系 alice@example.com\",\"scenario\":\"\",\"topic\":\"\"}");
        try
        {
            await BlindCandidateRunService.RunAsync([Record()], LocalConfiguration(), output, sealedPath,
                "c".PadLeft(64, 'c'), "development", [], client, hardwareProfile: "test-host");

            var predictionText = File.ReadAllText(Path.Combine(output, "predictions.jsonl"));
            Assert.DoesNotContain("alice@example.com", predictionText, StringComparison.Ordinal);
            using var prediction = JsonDocument.Parse(predictionText);
            Assert.Equal("error", prediction.RootElement.GetProperty("status").GetString());
            Assert.Equal("other", prediction.RootElement.GetProperty("telemetry").GetProperty("error_category").GetString());
            Assert.Equal(1, JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "run-info.json")))
                .RootElement.GetProperty("sensitive_output_withheld_count").GetInt32());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_CancellationProducesACompleteCancelledPredictionWithoutCallingTheClient()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "blind-run-cancel-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(root, "reviewer-run");
        var sealedPath = Path.Combine(root, "custodian", "candidate-map.json");
        Directory.CreateDirectory(root);
        using var client = new CapturingClient([]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            var result = await BlindCandidateRunService.RunAsync([Record()], LocalConfiguration(), output, sealedPath,
                "d".PadLeft(64, 'd'), "development", [], client, cancellation.Token, hardwareProfile: "test-host");

            Assert.Equal(1, result.CancelledCount);
            Assert.Equal(0, result.ProviderRequestCount);
            Assert.Equal(0, client.CallCount);
            using var prediction = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "predictions.jsonl")));
            Assert.Equal("cancelled", prediction.RootElement.GetProperty("status").GetString());
            Assert.Equal("cancelled", prediction.RootElement.GetProperty("telemetry").GetProperty("error_category").GetString());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAndFinalize_PreservesRawPredictionHashWhileScoringReviewedCopy()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "blind-run-finalize-" + Guid.NewGuid().ToString("N"));
        var reviewerDirectory = Path.Combine(root, "reviewer");
        var sealedPath = Path.Combine(root, "custodian", "candidate-map.json");
        var goldPath = Path.Combine(root, "blind-eval.jsonl");
        var reportPath = Path.Combine(reviewerDirectory, "report.json");
        var reviewedPredictionsPath = Path.Combine(reviewerDirectory, "reviewed-predictions.jsonl");
        var manifestPath = Path.Combine(root, "custodian", "run-manifest.json");
        var record = Record();
        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true };
        var goldJsonl = JsonSerializer.Serialize(record, jsonOptions) + Environment.NewLine;
        Directory.CreateDirectory(root);
        File.WriteAllText(goldPath, goldJsonl, new UTF8Encoding(false));
        var datasetHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(goldJsonl))).ToLowerInvariant();
        var observations = new List<ProviderRequestTelemetry>();
        using var client = new CapturingClient(observations);

        try
        {
            await BlindCandidateRunService.RunAsync([record], LocalConfiguration(), reviewerDirectory, sealedPath,
                datasetHash, "all", observations, client, hardwareProfile: "test-host");
            var rawPath = Path.Combine(reviewerDirectory, "predictions.jsonl");
            var rawText = File.ReadAllText(rawPath);
            var prediction = JsonSerializer.Deserialize<BlindEvaluationPrediction>(rawText, jsonOptions)!;
            var reviewed = prediction with
            {
                Reviews =
                [
                    new("reviewer-a", true, true, true, true, false, true, DirectUsabilityScore: 4),
                    new("reviewer-b", true, true, true, true, false, true, DirectUsabilityScore: 4)
                ]
            };
            File.WriteAllText(reviewedPredictionsPath, JsonSerializer.Serialize(reviewed, jsonOptions) + Environment.NewLine, new UTF8Encoding(false));
            var scoring = BlindEvaluationScorer.Evaluate([record], [reviewed]);
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new
            {
                evaluation_split = "development",
                dataset_valid = true,
                admission_protocol_id = BlindEvaluationAdmissionProtocol.ProtocolId,
                admission_protocol_version = BlindEvaluationAdmissionProtocol.Version,
                admission_policy_sha256 = BlindEvaluationAdmissionProtocol.PolicySha256,
                dataset_issue_count = 0,
                dataset_issues = Array.Empty<BlindEvaluationIssue>(),
                scoring
            }, jsonOptions));

            var finalized = BlindEvaluationManifestFinalizer.Finalize(
                goldPath, rawPath, reviewedPredictionsPath, reportPath, null,
                Path.Combine(reviewerDirectory, "run-info.json"), sealedPath, manifestPath,
                new("synthetic-test", "approved-synthetic-only", "test-host", "fake-runtime", "test-revision", "integration-run"));

            Assert.True(finalized.Valid, string.Join("; ", finalized.Issues.Select(issue => issue.Message)));
            Assert.True(File.Exists(manifestPath));
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            Assert.Equal("all", JsonDocument.Parse(File.ReadAllText(sealedPath)).RootElement.GetProperty("split").GetString());
            Assert.Equal(1, manifest.RootElement.GetProperty("data").GetProperty("sample_count").GetInt32());
            Assert.Equal("development", manifest.RootElement.GetProperty("data").GetProperty("evaluation_split").GetString());
            Assert.Equal("development_diagnostic", manifest.RootElement.GetProperty("data").GetProperty("evaluation_role").GetString());
            Assert.NotEqual(rawText, File.ReadAllText(reviewedPredictionsPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static BlindCandidateRuntimeConfiguration LocalConfiguration() => new(new ProviderProfile
    {
        Type = ProviderType.Local,
        Platform = ProviderPlatform.Ollama,
        ApiBase = "http://127.0.0.1:11434/v1",
        Model = "qwen3"
    }, string.Empty, string.Empty);

    private static BlindEvaluationRecord Record()
    {
        var record = new BlindEvaluationRecord
        {
            Id = "blind-1",
            Task = "polish",
            SemanticFamilyId = "family-1",
            Source = new("project_owned", "test", "synthetic"),
            Input = "你好",
            InputStyle = "colloquial",
            Constraints = [],
            RiskLevel = "low",
            Split = "development",
            ExpectedDecision = "produce",
            ReferenceOutput = "GOLD_SECRET",
            Annotations = new([], "自然", [], false, [])
        };
        var labels = new BlindEvaluationGoldLabelSet(record.Task, record.InputStyle, record.Constraints,
            record.RiskLevel, record.ExpectedDecision, record.ReferenceOutput, record.Annotations);
        return record with
        {
            HumanReview = new(["reviewer-a", "reviewer-b"], "accepted", null,
                [new("reviewer-a", labels), new("reviewer-b", labels)])
        };
    }

    private sealed class CapturingClient(
        List<ProviderRequestTelemetry> observations,
        string response = "{\"kind\":\"final\",\"content\":\"你好\",\"scenario\":\"\",\"topic\":\"\"}",
        LocalRuntimeMetrics? runtimeMetrics = null,
        Exception? generationException = null) : ITextGenerationClient, ILocalRuntimeMetricsClient, IDisposable
    {
        public int CallCount { get; private set; }

        public LocalRuntimeMetrics? GetRuntimeMetrics() => runtimeMetrics;

        public Task<string> GenerateAsync(string systemPrompt, string userInput, CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (generationException is not null) throw generationException;
            observations.Add(new("request-1", 10, 8, 2, 200, "success"));
            return Task.FromResult(response);
        }

        public void Dispose() { }
    }
}


