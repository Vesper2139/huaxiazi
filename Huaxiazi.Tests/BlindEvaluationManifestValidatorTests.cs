using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO;
using Huaxiazi.DatasetBuilder;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class BlindEvaluationManifestValidatorTests
{
    [Fact]
    public void Validate_AcceptsManifestBoundToExactDatasetSnapshot()
    {
        using var fixture = new ManifestFixture();

        var report = fixture.Validate();

        Assert.True(report.Valid);
        Assert.Empty(report.Issues);
    }

    [Fact]
    public void Validate_RequiresOutputContractFingerprint()
    {
        using var fixture = new ManifestFixture();
        var manifest = JsonNode.Parse(fixture.ManifestJson)!;
        manifest["candidate"]!.AsObject().Remove("output_contract_bundle_sha256");

        var report = BlindEvaluationManifestValidator.Validate(
            manifest.ToJsonString(), fixture.DatasetPath, fixture.PredictionsPath, fixture.ReportPath);

        Assert.Contains(report.Issues, issue => issue.Field == "output_contract_bundle_sha256");
    }

    [Fact]
    public void Validate_RejectsDatasetChangedAfterManifestWasWritten()
    {
        using var fixture = new ManifestFixture();
        File.AppendAllText(fixture.DatasetPath, "\n");

        var report = fixture.Validate();

        Assert.Contains(report.Issues, issue => issue.Code == "dataset-hash-mismatch");
    }

    [Fact]
    public void Validate_RejectsNonRfc3339RunTimestamp()
    {
        using var fixture = new ManifestFixture();
        var manifest = JsonNode.Parse(fixture.ManifestJson)!;
        manifest["created_at_utc"] = "10/02/2026 00:00:00 +00:00";

        var report = BlindEvaluationManifestValidator.Validate(
            manifest.ToJsonString(), fixture.DatasetPath, fixture.PredictionsPath, fixture.ReportPath);

        Assert.Contains(report.Issues, issue => issue.Code == "utc-timestamp" && issue.Field == "created_at_utc");
    }

    [Fact]
    public void Validate_RejectsManifestCreatedBeforeExecutionCompleted()
    {
        using var fixture = new ManifestFixture();
        var manifest = JsonNode.Parse(fixture.ManifestJson)!;
        manifest["created_at_utc"] = "2026-10-02T00:01:30Z";

        var report = BlindEvaluationManifestValidator.Validate(
            manifest.ToJsonString(), fixture.DatasetPath, fixture.PredictionsPath, fixture.ReportPath);

        Assert.Contains(report.Issues, issue => issue.Code == "manifest-time-order" && issue.Field == "created_at_utc");
    }

    [Fact]
    public void Validate_RejectsGoldWithoutCurrentHumanReviewEvidence()
    {
        using var fixture = new ManifestFixture(validReviewEvidence: false);

        var report = fixture.Validate();

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "review-evidence");
    }

    [Fact]
    public void Validate_RejectsPIIInGoldSnapshot()
    {
        using var fixture = new ManifestFixture(sensitiveGoldInput: true);

        var report = fixture.Validate();

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "pii");
    }

    [Fact]
    public void Validate_RejectsMissingFamilySplitBinding()
    {
        using var fixture = new ManifestFixture(familySplitHash: new string('0', 64));

        var report = fixture.Validate();

        Assert.Contains(report.Issues, issue => issue.Code == "family-split-hash-mismatch");
    }

    [Fact]
    public void Validate_RejectsContentTelemetryEnabled()
    {
        using var fixture = new ManifestFixture(contentTelemetryEnabled: true);

        var report = fixture.Validate();

        Assert.Contains(report.Issues, issue => issue.Code == "content-telemetry-enabled");
    }

    [Fact]
    public void Validate_RejectsUnrecognizedHardwareProfileProvenance()
    {
        using var fixture = new ManifestFixture();
        var manifest = JsonNode.Parse(fixture.ManifestJson)!;
        manifest["execution"]!["hardware_profile_source"] = "verified_by_system";

        var report = BlindEvaluationManifestValidator.Validate(
            manifest.ToJsonString(), fixture.DatasetPath, fixture.PredictionsPath, fixture.ReportPath);

        Assert.Contains(report.Issues, issue => issue.Code == "hardware-profile-source" && issue.Field == "execution.hardware_profile_source");
    }

    [Fact]
    public void Validate_RejectsFrozenDatasetWithoutUnlockConfirmation()
    {
        using var fixture = new ManifestFixture();
        var manifest = JsonNode.Parse(fixture.ManifestJson)!;
        manifest["execution"]!["frozen_test_unlock_confirmed"] = false;

        var report = BlindEvaluationManifestValidator.Validate(
            manifest.ToJsonString(), fixture.DatasetPath, fixture.PredictionsPath, fixture.ReportPath);

        Assert.Contains(report.Issues, issue => issue.Code == "frozen-test-unlock-unconfirmed");
    }

    [Fact]
    public void Validate_RejectsPredictionArtifactChangedAfterScoring()
    {
        using var fixture = new ManifestFixture();
        File.AppendAllText(fixture.PredictionsPath, " ");

        var report = fixture.Validate();

        Assert.Contains(report.Issues, issue => issue.Code == "artifact-hash-mismatch" && issue.Field == "artifacts.predictions_sha256");
    }

    [Fact]
    public void Validate_RejectsRunManifestWithoutMetricSnapshot()
    {
        using var fixture = new ManifestFixture(includeMetrics: false);

        var report = fixture.Validate();

        Assert.Contains(report.Issues, issue => issue.Code == "required-object" && issue.Field == "metrics");
    }

    [Fact]
    public void Validate_AcceptsCostEstimateBoundToRateCardEvidence()
    {
        using var fixture = new ManifestFixture();
        var manifest = JsonNode.Parse(fixture.ManifestJson)!;
        manifest["metrics"]!["api_token_usage"]!["cache_read_input_tokens"] = 45;
        manifest["metrics"]!["api_token_usage"]!["cache_creation_input_tokens"] = 12;
        manifest["metrics"]!["api_cost_accounting"] = new JsonObject
        {
            ["status"] = "estimated",
            ["amount"] = 0.0125,
            ["currency"] = "USD",
            ["basis_kind"] = "provider_rate_card",
            ["basis_ref"] = "vault:rate-card-2026-10",
            ["basis_sha256"] = new string('e', 64),
            ["assessed_at_utc"] = "2026-10-03T12:00:00Z"
        };

        var report = BlindEvaluationManifestValidator.Validate(
            manifest.ToJsonString(), fixture.DatasetPath, fixture.PredictionsPath, fixture.ReportPath);

        Assert.True(report.Valid, string.Join("; ", report.Issues.Select(issue => issue.Field + " " + issue.Message)));
    }

    [Fact]
    public void Validate_RejectsNegativeCachedTokenUsage()
    {
        using var fixture = new ManifestFixture();
        var manifest = JsonNode.Parse(fixture.ManifestJson)!;
        manifest["metrics"]!["api_token_usage"]!["cache_read_input_tokens"] = -1;

        var report = BlindEvaluationManifestValidator.Validate(
            manifest.ToJsonString(), fixture.DatasetPath, fixture.PredictionsPath, fixture.ReportPath);

        Assert.Contains(report.Issues, issue => issue.Code == "invalid-metric" &&
            issue.Field == "metrics.api_token_usage.cache_read_input_tokens");
    }

    [Fact]
    public void Validate_RejectsEstimatedCostWithoutRateCardEvidence()
    {
        using var fixture = new ManifestFixture();
        var manifest = JsonNode.Parse(fixture.ManifestJson)!;
        manifest["metrics"]!["api_cost_accounting"] = new JsonObject
        {
            ["status"] = "estimated",
            ["amount"] = 0.0125,
            ["currency"] = "USD",
            ["basis_kind"] = "provider_rate_card",
            ["basis_ref"] = null,
            ["basis_sha256"] = null,
            ["assessed_at_utc"] = "2026-10-03T12:00:00Z"
        };

        var report = BlindEvaluationManifestValidator.Validate(
            manifest.ToJsonString(), fixture.DatasetPath, fixture.PredictionsPath, fixture.ReportPath);

        Assert.Contains(report.Issues, issue => issue.Code == "api-cost-evidence" && issue.Field == "metrics.api_cost_accounting");
    }

    [Fact]
    public void Validate_RejectsLocalCandidateWithReportedApiCost()
    {
        using var fixture = new ManifestFixture();
        var manifest = JsonNode.Parse(fixture.ManifestJson)!;
        manifest["candidate"]!["provider"] = "Local/Ollama/OpenAICompatible";
        manifest["metrics"]!["api_cost_accounting"] = new JsonObject
        {
            ["status"] = "estimated",
            ["amount"] = 0.0125,
            ["currency"] = "USD",
            ["basis_kind"] = "provider_rate_card",
            ["basis_ref"] = "vault:rate-card-2026-10",
            ["basis_sha256"] = new string('e', 64),
            ["assessed_at_utc"] = "2026-10-03T12:00:00Z"
        };

        var report = BlindEvaluationManifestValidator.Validate(
            manifest.ToJsonString(), fixture.DatasetPath, fixture.PredictionsPath, fixture.ReportPath);

        Assert.Contains(report.Issues, issue => issue.Code == "api-cost-provider" && issue.Field == "metrics.api_cost_accounting.status");
    }

    [Fact]
    public void Validate_RejectsCloudCandidateMarkedCostNotApplicable()
    {
        using var fixture = new ManifestFixture();
        var manifest = JsonNode.Parse(fixture.ManifestJson)!;
        manifest["candidate"]!["provider"] = "Cloud/OpenAI/Responses";
        manifest["metrics"]!["api_cost_accounting"] = new JsonObject
        {
            ["status"] = "not_applicable",
            ["amount"] = null,
            ["currency"] = null,
            ["basis_kind"] = null,
            ["basis_ref"] = null,
            ["basis_sha256"] = null,
            ["assessed_at_utc"] = null
        };

        var report = BlindEvaluationManifestValidator.Validate(
            manifest.ToJsonString(), fixture.DatasetPath, fixture.PredictionsPath, fixture.ReportPath);

        Assert.Contains(report.Issues, issue => issue.Code == "api-cost-provider" && issue.Field == "metrics.api_cost_accounting.status");
    }

    [Fact]
    public void Validate_RejectsManagedLocalRunWithoutVerifiedArtifactProvenance()
    {
        using var fixture = new ManifestFixture();
        var manifest = JsonNode.Parse(fixture.ManifestJson)!;
        manifest["candidate"]!["provider"] = "Local/ManagedLocal/OpenAICompatible";

        var report = BlindEvaluationManifestValidator.Validate(
            manifest.ToJsonString(), fixture.DatasetPath, fixture.PredictionsPath, fixture.ReportPath);

        Assert.Contains(report.Issues, issue => issue.Code == "local-artifact-provenance" && issue.Field == "candidate.local_artifacts");
    }

    [Fact]
    public void Validate_AcceptsManagedLocalRunWithValidArtifactProvenance()
    {
        using var fixture = new ManifestFixture();
        var manifest = JsonNode.Parse(fixture.ManifestJson)!;
        manifest["candidate"]!["provider"] = "Local/ManagedLocal/OpenAICompatible";
        manifest["candidate"]!["local_artifacts"] = ValidLocalArtifacts();

        var report = BlindEvaluationManifestValidator.Validate(
            manifest.ToJsonString(), fixture.DatasetPath, fixture.PredictionsPath, fixture.ReportPath);

        Assert.True(report.Valid, string.Join("; ", report.Issues.Select(issue => issue.Field + " " + issue.Message)));
    }

    [Fact]
    public void Validate_RejectsManagedLocalRunWithInvalidModelHash()
    {
        using var fixture = new ManifestFixture();
        var manifest = JsonNode.Parse(fixture.ManifestJson)!;
        manifest["candidate"]!["provider"] = "Local/ManagedLocal/OpenAICompatible";
        manifest["candidate"]!["local_artifacts"] = ValidLocalArtifacts();
        manifest["candidate"]!["local_artifacts"]!["model_sha256"] = "not-a-hash";

        var report = BlindEvaluationManifestValidator.Validate(
            manifest.ToJsonString(), fixture.DatasetPath, fixture.PredictionsPath, fixture.ReportPath);

        Assert.Contains(report.Issues, issue => issue.Code == "invalid-sha256" && issue.Field == "candidate.local_artifacts.model_sha256");
    }

    private static JsonObject ValidLocalArtifacts() => new()
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

    [Fact]
    public void Validate_RejectsRunBoundToDifferentAdmissionProtocol()
    {
        using var fixture = new ManifestFixture();
        var manifest = JsonNode.Parse(fixture.ManifestJson)!;
        manifest["data"]!["admission_protocol_version"] = "stale-policy";
        manifest["data"]!["admission_policy_sha256"] = new string('0', 64);

        var report = BlindEvaluationManifestValidator.Validate(
            manifest.ToJsonString(), fixture.DatasetPath, fixture.PredictionsPath, fixture.ReportPath);

        Assert.Contains(report.Issues, issue => issue.Code == "admission-policy-mismatch");
    }

    [Fact]
    public void Validate_RejectsScoringReportBoundToDifferentAdmissionProtocolEvenWhenArtifactHashMatches()
    {
        using var fixture = new ManifestFixture();
        var reportNode = JsonNode.Parse(File.ReadAllText(fixture.ReportPath))!;
        reportNode["admission_policy_sha256"] = new string('0', 64);
        File.WriteAllText(fixture.ReportPath, reportNode.ToJsonString());
        var manifestNode = JsonNode.Parse(fixture.ManifestJson)!;
        manifestNode["artifacts"]!["evaluation_report_sha256"] =
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture.ReportPath)));

        var report = BlindEvaluationManifestValidator.Validate(
            manifestNode.ToJsonString(), fixture.DatasetPath, fixture.PredictionsPath, fixture.ReportPath);

        Assert.Contains(report.Issues, issue => issue.Code == "admission-policy-mismatch" && issue.Field == "report.admission_policy_sha256");
    }

    [Fact]
    public void Validate_RejectsEvaluationReportFromDifferentSplit()
    {
        using var fixture = new ManifestFixture();
        var manifest = JsonNode.Parse(fixture.ManifestJson)!;
        manifest["data"]!["evaluation_split"] = "development";
        manifest["data"]!["evaluation_role"] = "development_diagnostic";
        manifest["data"]!["evaluation_sample_count"] = 1;
        var reportNode = JsonNode.Parse(File.ReadAllText(fixture.ReportPath))!;
        reportNode["evaluation_split"] = "frozen_test";
        File.WriteAllText(fixture.ReportPath, reportNode.ToJsonString());
        manifest["artifacts"]!["evaluation_report_sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture.ReportPath)));

        var report = BlindEvaluationManifestValidator.Validate(
            manifest.ToJsonString(), fixture.DatasetPath, fixture.PredictionsPath, fixture.ReportPath);

        Assert.Contains(report.Issues, issue => issue.Code == "evaluation-split-mismatch");
    }

    [Fact]
    public void Validate_RejectsPairwiseComparisonsOutsideDeclaredEvaluationSplit()
    {
        using var fixture = new ManifestFixture();
        var manifest = JsonNode.Parse(fixture.ManifestJson)!;
        manifest["data"]!["evaluation_split"] = "frozen_test";
        manifest["data"]!["evaluation_role"] = "locked_final_evaluation";
        manifest["data"]!["evaluation_sample_count"] = 1;
        var reportNode = JsonNode.Parse(File.ReadAllText(fixture.ReportPath))!;
        reportNode["evaluation_split"] = "frozen_test";
        File.WriteAllText(fixture.ReportPath, reportNode.ToJsonString());
        manifest["artifacts"]!["evaluation_report_sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture.ReportPath)));
        var comparisonsPath = Path.Combine(Path.GetDirectoryName(fixture.DatasetPath)!, Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(comparisonsPath, "{\"id\":\"blind-a\"}" + Environment.NewLine);
        manifest["artifacts"]!["comparisons_sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(comparisonsPath)));
        try
        {
            var report = BlindEvaluationManifestValidator.Validate(
                manifest.ToJsonString(), fixture.DatasetPath, fixture.PredictionsPath, fixture.ReportPath, comparisonsPath);

            Assert.Contains(report.Issues, issue => issue.Code == "comparison-split-mismatch");
        }
        finally { File.Delete(comparisonsPath); }
    }

    private sealed class ManifestFixture : IDisposable
    {
        public string DatasetPath { get; }
        public string PredictionsPath { get; }
        public string ReportPath { get; }
        public string ManifestJson { get; }

        public ManifestFixture(string? familySplitHash = null, bool contentTelemetryEnabled = false, bool includeMetrics = true, bool validReviewEvidence = true, bool sensitiveGoldInput = false)
        {
            Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts"));
            DatasetPath = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "huaxiazi-manifest-" + Guid.NewGuid().ToString("N") + ".jsonl");
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
            File.WriteAllLines(DatasetPath,
            [
                JsonSerializer.Serialize(Gold("blind-a", "family-a", "development", validReviewEvidence, sensitiveGoldInput), jsonOptions),
                JsonSerializer.Serialize(Gold("blind-b", "family-b", "frozen_test", validReviewEvidence, sensitiveGoldInput), jsonOptions)
            ], new UTF8Encoding(false));
            PredictionsPath = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "huaxiazi-predictions-" + Guid.NewGuid().ToString("N") + ".jsonl");
            ReportPath = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "huaxiazi-report-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(PredictionsPath, "{\"id\":\"blind-a\",\"candidate_id\":\"alias-a\",\"output\":\"sanitized-fixture\"}\n", new UTF8Encoding(false));
            File.WriteAllText(ReportPath, JsonSerializer.Serialize(new
            {
                passed = true,
                dataset_valid = true,
                admission_protocol_id = BlindEvaluationAdmissionProtocol.ProtocolId,
                admission_protocol_version = BlindEvaluationAdmissionProtocol.Version,
                admission_policy_sha256 = BlindEvaluationAdmissionProtocol.PolicySha256
            }) + "\n", new UTF8Encoding(false));
            var bytes = File.ReadAllBytes(DatasetPath);
            var manifest = new
            {
                manifest_version = BlindEvaluationAdmissionProtocol.RunManifestVersion,
                run_id = "20261002-unit-test",
                run_kind = "baseline",
                created_at_utc = "2026-10-02T00:03:00Z",
                code_revision = "0123456789abcdef0123456789abcdef01234567",
                data = new
                {
                    dataset_id = "blind-eval-unit-test-v1",
                    admission_protocol_id = BlindEvaluationAdmissionProtocol.ProtocolId,
                    admission_protocol_version = BlindEvaluationAdmissionProtocol.Version,
                    admission_policy_sha256 = BlindEvaluationAdmissionProtocol.PolicySha256,
                    dataset_sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                    sample_count = 2,
                    semantic_family_split_sha256 = familySplitHash ?? ComputeFamilySplitHash(),
                    source_authorization_review = "review-record-001"
                },
                artifacts = new
                {
                    predictions_sha256 = HashFile(PredictionsPath),
                    evaluation_report_sha256 = HashFile(ReportPath),
                    comparisons_sha256 = (string?)null
                },
                candidate = new
                {
                    workflow = "polish",
                    provider = "example-provider",
                    model_id_and_version = "example-model-2026-10",
                    runtime_version = "n/a",
                    system_prompt_bundle_sha256 = new string('a', 64),
                    user_message_bundle_sha256 = new string('b', 64),
                    output_contract_bundle_sha256 = new string('c', 64),
                    sampling = new { temperature = 0.2, top_p = 0.9, max_output_tokens = 512, reasoning_level = "not_supported" }
                },
                execution = new
                {
                    hardware_profile = "test-host",
                    privacy_mode = "cloud-explicitly-authorized",
                    content_telemetry_enabled = contentTelemetryEnabled,
                    frozen_test_unlock_confirmed = true,
                    request_count = 2,
                    started_at_utc = "2026-10-02T00:01:00Z",
                    completed_at_utc = "2026-10-02T00:02:00Z"
                },
                metrics = includeMetrics ? new
                {
                    schema_valid_rate = 1d,
                    fact_constraint_retention_rate = 1d,
                    high_risk_key_fact_reversals = 0,
                    direct_usability_rate = 1d,
                    tone_match_rate = 1d,
                    clarification_decision_accuracy = 1d,
                    pairwise_preference_rate = (double?)null,
                    pairwise_confidence_interval_95 = (double[]?)null,
                    pairwise_comparable_count = 0,
                    pairwise_decisive_family_count = (int?)null,
                    api_latency_p50_ms = 100d,
                    api_latency_p95_ms = 200d,
                    api_token_usage = (object?)new { input_tokens = 100, output_tokens = 200 },
                    local_model_load_ms = (double?)null,
                    local_tokens_per_second = (double?)null,
                    local_peak_memory_bytes = (long?)null
                } : null
            };
            ManifestJson = JsonSerializer.Serialize(manifest);
        }

        private string ComputeFamilySplitHash()
        {
            var canonical = "family-a\tdevelopment\nfamily-b\tfrozen_test\n";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        }

        private static BlindEvaluationRecord Gold(string id, string family, string split, bool validReviewEvidence, bool sensitiveGoldInput)
        {
            var record = new BlindEvaluationRecord
            {
                Id = id,
                Task = "polish",
                SemanticFamilyId = family,
                Source = new("project_owned", "unit-fixture", "synthetic"),
                Input = sensitiveGoldInput ? "请联系 13800138000" : "fixture " + id,
                InputStyle = "colloquial",
                Constraints = [],
                RiskLevel = "low",
                Split = split,
                ExpectedDecision = "produce",
                Annotations = new([], "neutral", [], false, [])
            };
            var labels = new BlindEvaluationGoldLabelSet(record.Task, record.InputStyle, record.Constraints,
                record.RiskLevel, record.ExpectedDecision, record.ReferenceOutput, record.Annotations);
            return record with
            {
                HumanReview = validReviewEvidence
                    ? new(["reviewer-a", "reviewer-b"], "accepted", null, [new("reviewer-a", labels), new("reviewer-b", labels)])
                    : new(["reviewer-a", "reviewer-b"], "accepted")
            };
        }

        private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

        public BlindEvaluationManifestReport Validate() =>
            BlindEvaluationManifestValidator.Validate(ManifestJson, DatasetPath, PredictionsPath, ReportPath);

        public void Dispose()
        {
            if (File.Exists(DatasetPath)) File.Delete(DatasetPath);
            if (File.Exists(PredictionsPath)) File.Delete(PredictionsPath);
            if (File.Exists(ReportPath)) File.Delete(ReportPath);
        }
    }
}
