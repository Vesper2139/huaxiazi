using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO;
using Huaxiazi.DatasetBuilder;
using Huaxiazi.BlindEvaluationRunner;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class BlindEvaluationManifestFinalizerTests
{
    [Fact]
    public void Finalize_RejectsFrozenDatasetWithoutPersistedUnlockConfirmation()
    {
        using var fixture = new Fixture();
        fixture.SetGoldSplit("frozen_test");

        var exception = Assert.Throws<InvalidOperationException>(() => BlindEvaluationManifestFinalizer.Finalize(
            fixture.GoldPath, fixture.RawPredictionsPath, fixture.PredictionsPath, fixture.ReportPath, null,
            fixture.RunInfoPath, fixture.SealedMapPath, fixture.ManifestPath,
            new("unit-set-v1", "authorized-review-17", "test-host", "test-runtime-1", "revision-abc", "20261002-run-1")));

        Assert.Contains("frozen_test", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.ManifestPath));
    }

    [Fact]
    public void Finalize_RejectsAllSplitReportForFinalManifest()
    {
        using var fixture = new Fixture();
        var report = JsonNode.Parse(File.ReadAllText(fixture.ReportPath))!;
        report["evaluation_split"] = "all";
        File.WriteAllText(fixture.ReportPath, report.ToJsonString());

        var exception = Assert.Throws<InvalidOperationException>(() => BlindEvaluationManifestFinalizer.Finalize(
            fixture.GoldPath, fixture.RawPredictionsPath, fixture.PredictionsPath, fixture.ReportPath, null,
            fixture.RunInfoPath, fixture.SealedMapPath, fixture.ManifestPath,
            new("unit-set-v1", "authorized-review-17", "test-host", "test-runtime-1", "revision-abc", "20261002-run-1")));

        Assert.Contains("development 或 frozen_test", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.ManifestPath));
    }

    [Fact]
    public void Finalize_RejectsOutputContractFingerprintMismatchBetweenRunAndSealedMap()
    {
        using var fixture = new Fixture();
        var runInfo = JsonNode.Parse(File.ReadAllText(fixture.RunInfoPath))!;
        runInfo["output_contract_bundle_sha256"] = new string('e', 64);
        File.WriteAllText(fixture.RunInfoPath, runInfo.ToJsonString());

        var exception = Assert.Throws<InvalidOperationException>(() => BlindEvaluationManifestFinalizer.Finalize(
            fixture.GoldPath, fixture.RawPredictionsPath, fixture.PredictionsPath, fixture.ReportPath, null,
            fixture.RunInfoPath, fixture.SealedMapPath, fixture.ManifestPath,
            new("unit-set-v1", "authorized-review-17", "test-host", "test-runtime-1", "revision-abc", "20261002-output-contract")));

        Assert.Contains("输出契约", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.ManifestPath));
    }

    [Fact]
    public void Finalize_RecordsVerifiedFrozenTestUnlockConfirmation()
    {
        using var fixture = new Fixture();
        fixture.SetGoldSplit("frozen_test");
        fixture.SetFrozenTestConfirmation(true);

        var result = BlindEvaluationManifestFinalizer.Finalize(
            fixture.GoldPath, fixture.RawPredictionsPath, fixture.PredictionsPath, fixture.ReportPath, null,
            fixture.RunInfoPath, fixture.SealedMapPath, fixture.ManifestPath,
            new("unit-set-v1", "authorized-review-17", "test-host", "test-runtime-1", "revision-abc", "20261002-run-1"));

        Assert.True(result.Valid, string.Join("; ", result.Issues.Select(issue => issue.Message)));
        using var manifest = JsonDocument.Parse(File.ReadAllText(fixture.ManifestPath));
        Assert.True(manifest.RootElement.GetProperty("execution").GetProperty("frozen_test_unlock_confirmed").GetBoolean());
        Assert.Equal("frozen_test", manifest.RootElement.GetProperty("data").GetProperty("evaluation_split").GetString());
        Assert.Equal("locked_final_evaluation", manifest.RootElement.GetProperty("data").GetProperty("evaluation_role").GetString());
    }

    [Fact]
    public void Finalize_WritesValidatedManifestBoundToRunAndReviewArtifacts()
    {
        using var fixture = new Fixture();

        var result = BlindEvaluationManifestFinalizer.Finalize(
            fixture.GoldPath, fixture.RawPredictionsPath, fixture.PredictionsPath, fixture.ReportPath, null,
            fixture.RunInfoPath, fixture.SealedMapPath, fixture.ManifestPath,
            new("unit-set-v1", "authorized-review-17", "test-host", "test-runtime-1", "revision-abc", "20261002-run-1"));

        Assert.True(result.Valid, string.Join("; ", result.Issues.Select(issue => issue.Message)));
        Assert.True(File.Exists(fixture.ManifestPath));
        using var manifest = JsonDocument.Parse(File.ReadAllText(fixture.ManifestPath));
        var root = manifest.RootElement;
        Assert.Equal("candidate", root.GetProperty("run_kind").GetString());
        Assert.Equal(BlindEvaluationAdmissionProtocol.RunManifestVersion, root.GetProperty("manifest_version").GetInt32());
        Assert.Equal("polish", root.GetProperty("candidate").GetProperty("workflow").GetString());
        Assert.Equal(BlindEvaluationAdmissionProtocol.ProtocolId, root.GetProperty("data").GetProperty("admission_protocol_id").GetString());
        Assert.Equal(BlindEvaluationAdmissionProtocol.Version, root.GetProperty("data").GetProperty("admission_protocol_version").GetString());
        Assert.Equal(BlindEvaluationAdmissionProtocol.PolicySha256, root.GetProperty("data").GetProperty("admission_policy_sha256").GetString());
        Assert.Equal(1, root.GetProperty("data").GetProperty("sample_count").GetInt32());
        Assert.Equal("development", root.GetProperty("data").GetProperty("evaluation_split").GetString());
        Assert.Equal("development_diagnostic", root.GetProperty("data").GetProperty("evaluation_role").GetString());
        Assert.Equal(1, root.GetProperty("data").GetProperty("evaluation_sample_count").GetInt32());
        Assert.Equal(1, root.GetProperty("execution").GetProperty("request_count").GetInt32());
        Assert.Equal("operator_declared_unverified", root.GetProperty("execution").GetProperty("hardware_profile_source").GetString());
        Assert.Equal(new string('a', 64), root.GetProperty("candidate").GetProperty("system_prompt_bundle_sha256").GetString());
        Assert.Equal(new string('b', 64), root.GetProperty("candidate").GetProperty("user_message_bundle_sha256").GetString());
        Assert.Equal(new string('d', 64), root.GetProperty("candidate").GetProperty("output_contract_bundle_sha256").GetString());
        Assert.Equal(100, root.GetProperty("metrics").GetProperty("local_latency_p50_ms").GetDouble());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("metrics").GetProperty("api_latency_p50_ms").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("metrics").GetProperty("pairwise_preference_rate").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("metrics").GetProperty("pairwise_confidence_interval_95").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("metrics").GetProperty("pairwise_decisive_family_count").ValueKind);
        Assert.Equal("not_applicable", root.GetProperty("metrics").GetProperty("api_cost_accounting").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("metrics").GetProperty("api_cost_accounting").GetProperty("amount").ValueKind);
    }

    [Fact]
    public void Finalize_RecordsCloudCostAsNotReportedUntilBillingEvidenceExists()
    {
        using var fixture = new Fixture();
        fixture.SetUsage(100, 20, 40, 10);
        var sealedMap = JsonNode.Parse(File.ReadAllText(fixture.SealedMapPath))!;
        sealedMap["provider"] = "Cloud";
        File.WriteAllText(fixture.SealedMapPath, sealedMap.ToJsonString());

        var result = BlindEvaluationManifestFinalizer.Finalize(
            fixture.GoldPath, fixture.RawPredictionsPath, fixture.PredictionsPath, fixture.ReportPath, null,
            fixture.RunInfoPath, fixture.SealedMapPath, fixture.ManifestPath,
            new("unit-set-v1", "authorized-review-17", "test-host", "test-runtime-1", "revision-abc", "20261002-run-1"));

        Assert.True(result.Valid, string.Join("; ", result.Issues.Select(issue => issue.Message)));
        using var manifest = JsonDocument.Parse(File.ReadAllText(fixture.ManifestPath));
        var accounting = manifest.RootElement.GetProperty("metrics").GetProperty("api_cost_accounting");
        Assert.Equal("not_reported", accounting.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, accounting.GetProperty("amount").ValueKind);
        var usage = manifest.RootElement.GetProperty("metrics").GetProperty("api_token_usage");
        Assert.Equal(100, usage.GetProperty("input_tokens").GetInt64());
        Assert.Equal(20, usage.GetProperty("output_tokens").GetInt64());
        Assert.Equal(40, usage.GetProperty("cache_read_input_tokens").GetInt64());
        Assert.Equal(10, usage.GetProperty("cache_creation_input_tokens").GetInt64());
    }

    [Fact]
    public void Finalize_RejectsHardwareProfileThatDiffersFromCandidateRun()
    {
        using var fixture = new Fixture();

        var exception = Assert.Throws<InvalidOperationException>(() => BlindEvaluationManifestFinalizer.Finalize(
            fixture.GoldPath, fixture.RawPredictionsPath, fixture.PredictionsPath, fixture.ReportPath, null,
            fixture.RunInfoPath, fixture.SealedMapPath, fixture.ManifestPath,
            new("unit-set-v1", "authorized-review-17", "win-x64-gpu-32gb", "test-runtime-1", "revision-abc", "20261002-run-1")));

        Assert.Contains("硬件档位", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.ManifestPath));
    }

    [Fact]
    public void Finalize_CarriesVerifiedManagedLocalArtifactIdentityIntoFinalManifest()
    {
        using var fixture = new Fixture();
        var sealedMap = JsonNode.Parse(File.ReadAllText(fixture.SealedMapPath))!;
        sealedMap["platform"] = "ManagedLocal";
        sealedMap["local_artifacts"] = new JsonObject
        {
            ["model_installation_id"] = "model-install-1",
            ["model_version"] = "4b-test",
            ["model_size_bytes"] = 1024,
            ["model_sha256"] = new string('c', 64),
            ["adapter_installation_id"] = null,
            ["adapter_sha256"] = null,
            ["runtime_flavor"] = "cpu",
            ["runtime_file_version"] = null,
            ["runtime_sha256"] = new string('d', 64)
        };
        File.WriteAllText(fixture.SealedMapPath, sealedMap.ToJsonString());

        var result = BlindEvaluationManifestFinalizer.Finalize(
            fixture.GoldPath, fixture.RawPredictionsPath, fixture.PredictionsPath, fixture.ReportPath, null,
            fixture.RunInfoPath, fixture.SealedMapPath, fixture.ManifestPath,
            new("unit-set-v1", "authorized-review-17", "test-host", "test-runtime-1", "revision-abc", "20261002-run-1"));

        Assert.True(result.Valid, string.Join("; ", result.Issues.Select(issue => issue.Message)));
        using var manifest = JsonDocument.Parse(File.ReadAllText(fixture.ManifestPath));
        Assert.Equal(new string('c', 64), manifest.RootElement.GetProperty("candidate").GetProperty("local_artifacts").GetProperty("model_sha256").GetString());
    }

    [Fact]
    public void Finalize_RejectsManagedLocalCandidateWithoutArtifactIdentity()
    {
        using var fixture = new Fixture();
        var sealedMap = JsonNode.Parse(File.ReadAllText(fixture.SealedMapPath))!;
        sealedMap["platform"] = "ManagedLocal";
        File.WriteAllText(fixture.SealedMapPath, sealedMap.ToJsonString());

        var exception = Assert.Throws<InvalidOperationException>(() => BlindEvaluationManifestFinalizer.Finalize(
            fixture.GoldPath, fixture.RawPredictionsPath, fixture.PredictionsPath, fixture.ReportPath, null,
            fixture.RunInfoPath, fixture.SealedMapPath, fixture.ManifestPath,
            new("unit-set-v1", "authorized-review-17", "test-host", "test-runtime-1", "revision-abc", "20261002-run-1")));

        Assert.Contains("ManagedLocal", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.ManifestPath));
    }

    [Fact]
    public void Finalize_RejectsGoldWithoutCurrentHumanReviewEvidence()
    {
        using var fixture = new Fixture(validHumanReviewEvidence: false);

        var exception = Assert.Throws<InvalidOperationException>(() => BlindEvaluationManifestFinalizer.Finalize(
            fixture.GoldPath, fixture.RawPredictionsPath, fixture.PredictionsPath, fixture.ReportPath, null,
            fixture.RunInfoPath, fixture.SealedMapPath, fixture.ManifestPath,
            new("unit-set-v1", "authorized-review-17", "test-host", "test-runtime-1", "revision-abc", "20261002-run-1")));

        Assert.Contains("human_review", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.ManifestPath));
    }

    [Fact]
    public void Finalize_RejectsPIIInGoldEvenWhenReportClaimsAdmissionPassed()
    {
        using var fixture = new Fixture(includePiiInput: true);

        var exception = Assert.Throws<InvalidOperationException>(() => BlindEvaluationManifestFinalizer.Finalize(
            fixture.GoldPath, fixture.RawPredictionsPath, fixture.PredictionsPath, fixture.ReportPath, null,
            fixture.RunInfoPath, fixture.SealedMapPath, fixture.ManifestPath,
            new("unit-set-v1", "authorized-review-17", "test-host", "test-runtime-1", "revision-abc", "20261002-run-1")));

        Assert.Contains("pii", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.ManifestPath));
    }

    [Fact]
    public void Finalize_RejectsAliasMismatchAndDoesNotWriteManifest()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.PredictionsPath,
            "{\"id\":\"case-1\",\"candidate_id\":\"candidate-wrong\",\"output\":\"ok\",\"schema_valid\":true,\"reviews\":[],\"adjudication\":null,\"telemetry\":null,\"status\":\"success\"}\n");

        Assert.Throws<InvalidOperationException>(() => BlindEvaluationManifestFinalizer.Finalize(
            fixture.GoldPath, fixture.RawPredictionsPath, fixture.PredictionsPath, fixture.ReportPath, null,
            fixture.RunInfoPath, fixture.SealedMapPath, fixture.ManifestPath,
            new("unit-set-v1", "authorized-review-17", "test-host", "test-runtime-1", "revision-abc", "20261002-run-1")));
        Assert.False(File.Exists(fixture.ManifestPath));
    }

    [Fact]
    public void Finalize_RejectsEditsToCandidateOutputInReviewerCopy()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.PredictionsPath, File.ReadAllText(fixture.PredictionsPath)
            .Replace("\"output\":\"ok\"", "\"output\":\"changed after generation\"", StringComparison.Ordinal));

        var exception = Assert.Throws<InvalidOperationException>(() => BlindEvaluationManifestFinalizer.Finalize(
            fixture.GoldPath, fixture.RawPredictionsPath, fixture.PredictionsPath, fixture.ReportPath, null,
            fixture.RunInfoPath, fixture.SealedMapPath, fixture.ManifestPath,
            new("unit-set-v1", "authorized-review-17", "test-host", "test-runtime-1", "revision-abc", "20261002-run-1")));

        Assert.Contains("候选输出或运行遥测", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.ManifestPath));
    }

    [Fact]
    public void Finalize_RejectsPartialSplitRunAsFinalQualityEvidence()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.RunInfoPath, File.ReadAllText(fixture.RunInfoPath).Replace("\"split\":\"all\"", "\"split\":\"development\""));
        File.WriteAllText(fixture.SealedMapPath, File.ReadAllText(fixture.SealedMapPath).Replace("\"split\":\"all\"", "\"split\":\"development\""));

        var exception = Assert.Throws<InvalidOperationException>(() => BlindEvaluationManifestFinalizer.Finalize(
            fixture.GoldPath, fixture.RawPredictionsPath, fixture.PredictionsPath, fixture.ReportPath, null,
            fixture.RunInfoPath, fixture.SealedMapPath, fixture.ManifestPath,
            new("unit-set-v1", "authorized-review-17", "test-host", "test-runtime-1", "revision-abc", "20261002-run-1")));

        Assert.Contains("split=all", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.ManifestPath));
    }

    [Fact]
    public void Finalize_RejectsScoreReportThatDoesNotMatchReviewedPredictions()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.ReportPath, File.ReadAllText(fixture.ReportPath)
            .Replace("\"schema_valid_rate\": 1", "\"schema_valid_rate\": 0.5", StringComparison.Ordinal));

        var exception = Assert.Throws<InvalidOperationException>(() => BlindEvaluationManifestFinalizer.Finalize(
            fixture.GoldPath, fixture.RawPredictionsPath, fixture.PredictionsPath, fixture.ReportPath, null,
            fixture.RunInfoPath, fixture.SealedMapPath, fixture.ManifestPath,
            new("unit-set-v1", "authorized-review-17", "test-host", "test-runtime-1", "revision-abc", "20261002-run-1")));

        Assert.Contains("评分报告与当前 gold", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.ManifestPath));
    }

    [Fact]
    public void Finalize_RejectsScoreReportBoundToDifferentAdmissionPolicy()
    {
        using var fixture = new Fixture();
        var report = JsonNode.Parse(File.ReadAllText(fixture.ReportPath))!;
        report["admission_policy_sha256"] = new string('0', 64);
        File.WriteAllText(fixture.ReportPath, report.ToJsonString());

        var exception = Assert.Throws<InvalidOperationException>(() => BlindEvaluationManifestFinalizer.Finalize(
            fixture.GoldPath, fixture.RawPredictionsPath, fixture.PredictionsPath, fixture.ReportPath, null,
            fixture.RunInfoPath, fixture.SealedMapPath, fixture.ManifestPath,
            new("unit-set-v1", "authorized-review-17", "test-host", "test-runtime-1", "revision-abc", "20261002-run-1")));

        Assert.Contains("准入协议", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.ManifestPath));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "blind-finalize-" + Guid.NewGuid().ToString("N"));
        private string ReviewerDirectory => Path.Combine(_root, "reviewer");
        public string GoldPath => Path.Combine(ReviewerDirectory, "gold.jsonl");
        public string PredictionsPath => Path.Combine(ReviewerDirectory, "predictions.jsonl");
        public string RawPredictionsPath => Path.Combine(ReviewerDirectory, "candidate-predictions.jsonl");
        public string ReportPath => Path.Combine(ReviewerDirectory, "report.json");
        public string RunInfoPath => Path.Combine(ReviewerDirectory, "run-info.json");
        public string SealedMapPath => Path.Combine(_root, "custodian", "candidate-map.json");
        public string ManifestPath => Path.Combine(_root, "custodian", "final-run-manifest.json");

        public void SetGoldSplit(string split)
        {
            var gold = JsonNode.Parse(File.ReadAllText(GoldPath))!;
            gold["split"] = split;
            File.WriteAllText(GoldPath, gold.ToJsonString() + Environment.NewLine);
            var hash = HashFile(GoldPath);
            var run = JsonNode.Parse(File.ReadAllText(RunInfoPath))!;
            run["dataset_sha256"] = hash;
            File.WriteAllText(RunInfoPath, run.ToJsonString());
            var sealedMap = JsonNode.Parse(File.ReadAllText(SealedMapPath))!;
            sealedMap["dataset_sha256"] = hash;
            File.WriteAllText(SealedMapPath, sealedMap.ToJsonString());

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
            var goldRecord = JsonSerializer.Deserialize<BlindEvaluationRecord>(File.ReadAllText(GoldPath), options)!;
            var predictionRecord = JsonSerializer.Deserialize<BlindEvaluationPrediction>(File.ReadAllText(PredictionsPath), options)!;
            var report = JsonNode.Parse(File.ReadAllText(ReportPath))!;
            report["evaluation_split"] = split;
            report["scoring"] = JsonSerializer.SerializeToNode(BlindEvaluationScorer.Evaluate([goldRecord], [predictionRecord]), options);
            File.WriteAllText(ReportPath, report.ToJsonString());
        }

        public void SetFrozenTestConfirmation(bool confirmed)
        {
            var run = JsonNode.Parse(File.ReadAllText(RunInfoPath))!;
            run["frozen_test_unlock_confirmed"] = confirmed;
            File.WriteAllText(RunInfoPath, run.ToJsonString());
            var sealedMap = JsonNode.Parse(File.ReadAllText(SealedMapPath))!;
            sealedMap["frozen_test_unlock_confirmed"] = confirmed;
            File.WriteAllText(SealedMapPath, sealedMap.ToJsonString());
        }

        public void SetUsage(int inputTokens, int outputTokens, int cacheReadInputTokens, int cacheCreationInputTokens)
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
            var raw = JsonNode.Parse(File.ReadAllText(RawPredictionsPath))!;
            var reviewed = JsonNode.Parse(File.ReadAllText(PredictionsPath))!;
            foreach (var prediction in new[] { raw, reviewed })
            {
                prediction["telemetry"]!["api_input_tokens"] = inputTokens;
                prediction["telemetry"]!["api_output_tokens"] = outputTokens;
                prediction["telemetry"]!["api_cache_read_input_tokens"] = cacheReadInputTokens;
                prediction["telemetry"]!["api_cache_creation_input_tokens"] = cacheCreationInputTokens;
            }
            File.WriteAllText(RawPredictionsPath, raw.ToJsonString() + Environment.NewLine);
            File.WriteAllText(PredictionsPath, reviewed.ToJsonString() + Environment.NewLine);
            var runInfo = JsonNode.Parse(File.ReadAllText(RunInfoPath))!;
            runInfo["predictions_sha256"] = HashFile(RawPredictionsPath);
            File.WriteAllText(RunInfoPath, runInfo.ToJsonString());

            var goldRecord = JsonSerializer.Deserialize<BlindEvaluationRecord>(File.ReadAllText(GoldPath), options)!;
            var predictionRecord = JsonSerializer.Deserialize<BlindEvaluationPrediction>(File.ReadAllText(PredictionsPath), options)!;
            var report = JsonNode.Parse(File.ReadAllText(ReportPath))!;
            report["scoring"] = JsonSerializer.SerializeToNode(BlindEvaluationScorer.Evaluate([goldRecord], [predictionRecord]), options);
            File.WriteAllText(ReportPath, report.ToJsonString());
        }

        public Fixture(bool validHumanReviewEvidence = true, bool includePiiInput = false)
        {
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(ReviewerDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(SealedMapPath)!);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
            var gold = new BlindEvaluationRecord
            {
                Id = "case-1", Task = "polish", SemanticFamilyId = "family-1",
                Source = new("project_owned", "test", "synthetic"), Input = includePiiInput ? "请联系 13800138000" : "hello", InputStyle = "colloquial",
                Constraints = [], RiskLevel = "low", Split = "development", ExpectedDecision = "produce",
                Annotations = new([], "neutral", [], false, [])
            };
            var labels = new BlindEvaluationGoldLabelSet(gold.Task, gold.InputStyle, gold.Constraints,
                gold.RiskLevel, gold.ExpectedDecision, gold.ReferenceOutput, gold.Annotations);
            gold = gold with
            {
                HumanReview = validHumanReviewEvidence
                    ? new(["gold-a", "gold-b"], "accepted", null, [new("gold-a", labels), new("gold-b", labels)])
                    : new(["gold-a", "gold-b"], "accepted")
            };
            File.WriteAllText(GoldPath, JsonSerializer.Serialize(gold, options) + Environment.NewLine);
            File.WriteAllText(PredictionsPath, """
                {"id":"case-1","candidate_id":"candidate-123","output":"ok","schema_valid":true,"reviews":[{"reviewer_id":"reviewer-a","fact_constraint_retained":true,"directly_usable":true,"tone_matched":true,"clarification_decision_correct":true,"high_risk_key_fact_reversed":false,"safety_pass":true,"fidelity_score":3,"task_completion_score":4,"naturalness_score":4,"direct_usability_score":4,"safety_score":5},{"reviewer_id":"reviewer-b","fact_constraint_retained":true,"directly_usable":true,"tone_matched":true,"clarification_decision_correct":true,"high_risk_key_fact_reversed":false,"safety_pass":true,"fidelity_score":3,"task_completion_score":4,"naturalness_score":4,"direct_usability_score":4,"safety_score":5}],"adjudication":null,"telemetry":{"latency_milliseconds":100,"api_input_tokens":null,"api_output_tokens":null,"local_tokens_per_second":2,"local_peak_memory_bytes":null,"local_model_load_milliseconds":null,"error_category":null},"status":"success"}
                """ + Environment.NewLine);
            File.WriteAllText(RawPredictionsPath, """
                {"id":"case-1","candidate_id":"candidate-123","output":"ok","schema_valid":true,"reviews":[],"adjudication":null,"telemetry":{"latency_milliseconds":100,"api_input_tokens":null,"api_output_tokens":null,"local_tokens_per_second":2,"local_peak_memory_bytes":null,"local_model_load_milliseconds":null,"error_category":null},"status":"success"}
                """ + Environment.NewLine);
            var goldRecord = JsonSerializer.Deserialize<BlindEvaluationRecord>(File.ReadAllText(GoldPath), options)!;
            var predictionRecord = JsonSerializer.Deserialize<BlindEvaluationPrediction>(File.ReadAllText(PredictionsPath), options)!;
            var scoring = BlindEvaluationScorer.Evaluate([goldRecord], [predictionRecord]);
            File.WriteAllText(ReportPath, JsonSerializer.Serialize(new
            {
                evaluation_split = "development",
                dataset_valid = true,
                admission_protocol_id = BlindEvaluationAdmissionProtocol.ProtocolId,
                admission_protocol_version = BlindEvaluationAdmissionProtocol.Version,
                admission_policy_sha256 = BlindEvaluationAdmissionProtocol.PolicySha256,
                dataset_issue_count = 0,
                dataset_issues = Array.Empty<string>(),
                scoring
            }, new JsonSerializerOptions(options) { WriteIndented = true }) + Environment.NewLine);
            File.WriteAllText(RunInfoPath, """
                {"run_info_version":2,"run_kind":"candidate","started_at_utc":"2026-10-02T00:00:00Z","completed_at_utc":"2026-10-02T00:01:00Z","candidate_id":"candidate-123","split":"all","frozen_test_unlock_confirmed":false,"sample_count":1,"dataset_sha256":"DATA_HASH","prompt_bundle_sha256":"PROMPT_HASH","output_contract_bundle_sha256":"OUTPUT_CONTRACT_HASH","predictions_sha256":"PREDICTION_HASH","provider_request_count":1,"sensitive_output_withheld_count":0,"content_telemetry_enabled":false,"hardware_profile":"test-host"}
                """.Replace("DATA_HASH", HashFile(GoldPath)).Replace("PROMPT_HASH", new string('c', 64)).Replace("OUTPUT_CONTRACT_HASH", new string('d', 64)).Replace("PREDICTION_HASH", HashFile(RawPredictionsPath)));
            File.WriteAllText(SealedMapPath, """
                {"manifest_version":2,"candidate_id":"candidate-123","provider":"Local","platform":"Ollama","protocol":"OpenAICompatible","resolved_model_id":"qwen3:4b","dataset_sha256":"DATA_HASH","split":"all","frozen_test_unlock_confirmed":false,"prompt_bundle_sha256":"PROMPT_HASH","output_contract_bundle_sha256":"OUTPUT_CONTRACT_HASH","system_prompt_bundle_sha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","user_message_bundle_sha256":"BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB","hardware_profile":"test-host","profile":{"temperature":0.2,"top_p":0.9,"max_tokens":512,"inference_level":"Balanced"}}
                """.Replace("DATA_HASH", HashFile(GoldPath)).Replace("PROMPT_HASH", new string('c', 64)).Replace("OUTPUT_CONTRACT_HASH", new string('d', 64)));
        }

        private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
