using System.Security.Cryptography;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Huaxiazi.DatasetBuilder;
using Huaxiazi.BlindEvaluationRunner;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class BlindEvaluationCohortFinalizerTests
{
    [Fact]
    public void Finalize_SealsMultiCandidateReportAndAllCandidateArtifacts()
    {
        using var fixture = new Fixture();

        var result = BlindEvaluationCohortFinalizer.Finalize(
            fixture.GoldPath, [fixture.CandidateA, fixture.CandidateB], fixture.ReportPath,
            fixture.ComparisonsPath, fixture.PairwiseAnswersPath, fixture.PairwiseReviewPath, fixture.PairwiseMapPath, fixture.ManifestPath,
            new("cohort-set-v1", "authorized-review-18", "test-host", "test-runtime-1", "revision-abc", "20261002-cohort-1"));

        Assert.True(result.Valid, string.Join("; ", result.Issues.Select(issue => issue.Message)));
        using var manifest = JsonDocument.Parse(File.ReadAllText(fixture.ManifestPath));
        Assert.Equal(BlindEvaluationAdmissionProtocol.RunManifestVersion, manifest.RootElement.GetProperty("manifest_version").GetInt32());
        Assert.Equal(BlindEvaluationAdmissionProtocol.ProtocolId, manifest.RootElement.GetProperty("data").GetProperty("admission_protocol_id").GetString());
        Assert.Equal(BlindEvaluationAdmissionProtocol.PolicySha256, manifest.RootElement.GetProperty("data").GetProperty("admission_policy_sha256").GetString());
        var root = manifest.RootElement;
        Assert.Equal("cohort", root.GetProperty("run_kind").GetString());
        Assert.Equal(2, root.GetProperty("candidates").GetArrayLength());
        Assert.Equal(1, root.GetProperty("data").GetProperty("sample_count").GetInt32());
        Assert.Equal("development", root.GetProperty("data").GetProperty("evaluation_split").GetString());
        Assert.Equal("development_diagnostic", root.GetProperty("data").GetProperty("evaluation_role").GetString());
        Assert.False(root.GetProperty("data").GetProperty("frozen_test_unlock_confirmed").GetBoolean());
        Assert.True(root.GetProperty("artifacts").GetProperty("comparisons_sha256").GetString()!.Length == 64);
        foreach (var candidate in root.GetProperty("candidates").EnumerateArray())
        {
            Assert.False(candidate.GetProperty("execution").GetProperty("frozen_test_unlock_confirmed").GetBoolean());
            Assert.Equal("operator_declared_unverified", candidate.GetProperty("execution").GetProperty("hardware_profile_source").GetString());
            Assert.Equal("not_applicable", candidate.GetProperty("execution").GetProperty("api_cost_accounting").GetProperty("status").GetString());
        }
    }

    [Fact]
    public void Finalize_RejectsDifferentOutputContractsWithinOneCohort()
    {
        using var fixture = new Fixture(differentOutputContracts: true);

        var exception = Assert.Throws<InvalidOperationException>(() => BlindEvaluationCohortFinalizer.Finalize(
            fixture.GoldPath, [fixture.CandidateA, fixture.CandidateB], fixture.ReportPath,
            fixture.ComparisonsPath, fixture.PairwiseAnswersPath, fixture.PairwiseReviewPath, fixture.PairwiseMapPath, fixture.ManifestPath,
            new("cohort-set-v1", "authorized-review-18", "test-host", "test-runtime-1", "revision-abc", "20261002-cohort-output-contract")));

        Assert.Contains("输出契约", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.ManifestPath));
    }

    [Fact]
    public void Finalize_RejectsFrozenGoldWithoutPersistedUnlockConfirmation()
    {
        using var fixture = new Fixture();
        fixture.SetGoldSplit("frozen_test");

        var exception = Assert.Throws<InvalidOperationException>(() => BlindEvaluationCohortFinalizer.Finalize(
            fixture.GoldPath, [fixture.CandidateA, fixture.CandidateB], fixture.ReportPath,
            fixture.ComparisonsPath, fixture.PairwiseAnswersPath, fixture.PairwiseReviewPath, fixture.PairwiseMapPath, fixture.ManifestPath,
            new("cohort-set-v1", "authorized-review-18", "test-host", "test-runtime-1", "revision-abc", "20261002-cohort-1")));

        Assert.Contains("冻结集", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.ManifestPath));
    }

    [Fact]
    public void Finalize_RecordsFrozenUnlockConfirmationForEveryCandidate()
    {
        using var fixture = new Fixture();
        fixture.SetGoldSplit("frozen_test");
        fixture.SetCandidateConfirmation(fixture.CandidateA, true);
        fixture.SetCandidateConfirmation(fixture.CandidateB, true);

        var result = BlindEvaluationCohortFinalizer.Finalize(
            fixture.GoldPath, [fixture.CandidateA, fixture.CandidateB], fixture.ReportPath,
            fixture.ComparisonsPath, fixture.PairwiseAnswersPath, fixture.PairwiseReviewPath, fixture.PairwiseMapPath, fixture.ManifestPath,
            new("cohort-set-v1", "authorized-review-18", "test-host", "test-runtime-1", "revision-abc", "20261002-cohort-1"));

        Assert.True(result.Valid, string.Join("; ", result.Issues.Select(issue => issue.Message)));
        using var manifest = JsonDocument.Parse(File.ReadAllText(fixture.ManifestPath));
        Assert.All(manifest.RootElement.GetProperty("candidates").EnumerateArray(), candidate =>
            Assert.True(candidate.GetProperty("execution").GetProperty("frozen_test_unlock_confirmed").GetBoolean()));
        Assert.Equal("frozen_test", manifest.RootElement.GetProperty("data").GetProperty("evaluation_split").GetString());
        Assert.Equal("locked_final_evaluation", manifest.RootElement.GetProperty("data").GetProperty("evaluation_role").GetString());
        Assert.True(manifest.RootElement.GetProperty("data").GetProperty("frozen_test_unlock_confirmed").GetBoolean());
    }

    [Fact]
    public void Finalize_RejectsPairwisePackageFromDifferentSplit()
    {
        using var fixture = new Fixture();
        var pairwiseMap = JsonNode.Parse(File.ReadAllText(fixture.PairwiseMapPath))!;
        pairwiseMap["split"] = "frozen_test";
        File.WriteAllText(fixture.PairwiseMapPath, pairwiseMap.ToJsonString());

        var exception = Assert.Throws<InvalidOperationException>(() => BlindEvaluationCohortFinalizer.Finalize(
            fixture.GoldPath, [fixture.CandidateA, fixture.CandidateB], fixture.ReportPath,
            fixture.ComparisonsPath, fixture.PairwiseAnswersPath, fixture.PairwiseReviewPath, fixture.PairwiseMapPath, fixture.ManifestPath,
            new("cohort-set-v1", "authorized-review-18", "test-host", "test-runtime-1", "revision-abc", "20261002-cohort-1")));

        Assert.Contains("split", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.ManifestPath));
    }

    [Fact]
    public void Finalize_CarriesEachManagedLocalCandidateArtifactIdentity()
    {
        using var fixture = new Fixture();
        var map = JsonNode.Parse(File.ReadAllText(fixture.CandidateA.SealedCandidateMapPath))!;
        map["platform"] = "ManagedLocal";
        map["local_artifacts"] = new JsonObject
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
        File.WriteAllText(fixture.CandidateA.SealedCandidateMapPath, map.ToJsonString());

        var result = BlindEvaluationCohortFinalizer.Finalize(
            fixture.GoldPath, [fixture.CandidateA, fixture.CandidateB], fixture.ReportPath,
            fixture.ComparisonsPath, fixture.PairwiseAnswersPath, fixture.PairwiseReviewPath, fixture.PairwiseMapPath, fixture.ManifestPath,
            new("cohort-set-v1", "authorized-review-18", "test-host", "test-runtime-1", "revision-abc", "20261002-cohort-1"));

        Assert.True(result.Valid, string.Join("; ", result.Issues.Select(issue => issue.Message)));
        using var manifest = JsonDocument.Parse(File.ReadAllText(fixture.ManifestPath));
        Assert.Equal(new string('c', 64), manifest.RootElement.GetProperty("candidates")[0]
            .GetProperty("local_artifacts").GetProperty("model_sha256").GetString());
    }

    [Fact]
    public void Finalize_RejectsCandidatesWithDifferentPromptBundles()
    {
        using var fixture = new Fixture(differentPromptBundles: true);

        var exception = Assert.Throws<InvalidOperationException>(() => BlindEvaluationCohortFinalizer.Finalize(
            fixture.GoldPath, [fixture.CandidateA, fixture.CandidateB], fixture.ReportPath,
            fixture.ComparisonsPath, fixture.PairwiseAnswersPath, fixture.PairwiseReviewPath, fixture.PairwiseMapPath, fixture.ManifestPath,
            new("cohort-set-v1", "authorized-review-18", "test-host", "test-runtime-1", "revision-abc", "20261002-cohort-1")));

        Assert.Contains("提示束", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.ManifestPath));
    }

    [Fact]
    public void Finalize_RejectsCandidatesWithDifferentSamplingParameters()
    {
        using var fixture = new Fixture(differentSamplingParameters: true);

        var exception = Assert.Throws<InvalidOperationException>(() => BlindEvaluationCohortFinalizer.Finalize(
            fixture.GoldPath, [fixture.CandidateA, fixture.CandidateB], fixture.ReportPath,
            fixture.ComparisonsPath, fixture.PairwiseAnswersPath, fixture.PairwiseReviewPath, fixture.PairwiseMapPath, fixture.ManifestPath,
            new("cohort-set-v1", "authorized-review-18", "test-host", "test-runtime-1", "revision-abc", "20261002-cohort-1")));

        Assert.Contains("采样参数", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.ManifestPath));
    }

    [Fact]
    public void Finalize_RejectsCandidatesDeclaredOnDifferentHardwareProfiles()
    {
        using var fixture = new Fixture();
        var candidate = JsonNode.Parse(File.ReadAllText(fixture.CandidateA.SealedCandidateMapPath))!;
        candidate["hardware_profile"] = "win-x64-gpu-32gb";
        File.WriteAllText(fixture.CandidateA.SealedCandidateMapPath, candidate.ToJsonString());

        var exception = Assert.Throws<InvalidOperationException>(() => BlindEvaluationCohortFinalizer.Finalize(
            fixture.GoldPath, [fixture.CandidateA, fixture.CandidateB], fixture.ReportPath,
            fixture.ComparisonsPath, fixture.PairwiseAnswersPath, fixture.PairwiseReviewPath, fixture.PairwiseMapPath, fixture.ManifestPath,
            new("cohort-set-v1", "authorized-review-18", "test-host", "test-runtime-1", "revision-abc", "20261002-cohort-1")));

        Assert.Contains("硬件档位", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.ManifestPath));
    }

    [Fact]
    public void Finalize_RejectsGoldWithoutCurrentHumanReviewEvidence()
    {
        using var fixture = new Fixture(validHumanReviewEvidence: false);

        var exception = Assert.Throws<InvalidOperationException>(() => BlindEvaluationCohortFinalizer.Finalize(
            fixture.GoldPath, [fixture.CandidateA, fixture.CandidateB], fixture.ReportPath,
            fixture.ComparisonsPath, fixture.PairwiseAnswersPath, fixture.PairwiseReviewPath, fixture.PairwiseMapPath, fixture.ManifestPath,
            new("cohort-set-v1", "authorized-review-18", "test-host", "test-runtime-1", "revision-abc", "20261002-cohort-1")));

        Assert.Contains("human_review", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.ManifestPath));
    }

    [Fact]
    public void Finalize_RejectsCandidateOutputEditsAndKeepsManifestAbsent()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.CandidateB.ReviewedPredictionsPath,
            File.ReadAllText(fixture.CandidateB.ReviewedPredictionsPath).Replace("\"output\":\"ok-b\"", "\"output\":\"tampered\"", StringComparison.Ordinal));

        Assert.Throws<InvalidOperationException>(() => BlindEvaluationCohortFinalizer.Finalize(
            fixture.GoldPath, [fixture.CandidateA, fixture.CandidateB], fixture.ReportPath,
            fixture.ComparisonsPath, fixture.PairwiseAnswersPath, fixture.PairwiseReviewPath, fixture.PairwiseMapPath, fixture.ManifestPath,
            new("cohort-set-v1", "authorized-review-18", "test-host", "test-runtime-1", "revision-abc", "20261002-cohort-1")));
        Assert.False(File.Exists(fixture.ManifestPath));
    }

    [Fact]
    public void Finalize_RejectsChangedComparisonsEvenWhenReportWasRecalculated()
    {
        using var fixture = new Fixture();
        var comparison = File.ReadAllText(fixture.ComparisonsPath).Replace("\"choice\":\"left\"", "\"choice\":\"right\"", StringComparison.Ordinal);
        File.WriteAllText(fixture.ComparisonsPath, comparison);
        var gold = JsonSerializer.Deserialize<Huaxiazi.DatasetBuilder.BlindEvaluationRecord>(File.ReadAllText(fixture.GoldPath), fixture.Options)!;
        var reviewed = new[] { fixture.CandidateA.ReviewedPredictionsPath, fixture.CandidateB.ReviewedPredictionsPath }
            .SelectMany(File.ReadLines)
            .Select(line => JsonSerializer.Deserialize<Huaxiazi.DatasetBuilder.BlindEvaluationPrediction>(line, fixture.Options)!).ToArray();
        var changedComparison = JsonSerializer.Deserialize<Huaxiazi.DatasetBuilder.BlindEvaluationPairwiseComparison>(comparison, fixture.Options)!;
        var scoring = Huaxiazi.DatasetBuilder.BlindEvaluationScorer.Evaluate([gold], reviewed, [changedComparison]);
        File.WriteAllText(fixture.ReportPath, JsonSerializer.Serialize(new
        {
            evaluation_split = "development",
            dataset_valid = true,
            admission_protocol_id = BlindEvaluationAdmissionProtocol.ProtocolId,
            admission_protocol_version = BlindEvaluationAdmissionProtocol.Version,
            admission_policy_sha256 = BlindEvaluationAdmissionProtocol.PolicySha256,
            dataset_issue_count = 0,
            dataset_issues = Array.Empty<string>(),
            scoring
        }, fixture.Options));

        var exception = Assert.Throws<InvalidOperationException>(() => BlindEvaluationCohortFinalizer.Finalize(
            fixture.GoldPath, [fixture.CandidateA, fixture.CandidateB], fixture.ReportPath,
            fixture.ComparisonsPath, fixture.PairwiseAnswersPath, fixture.PairwiseReviewPath, fixture.PairwiseMapPath, fixture.ManifestPath,
            new("cohort-set-v1", "authorized-review-18", "test-host", "test-runtime-1", "revision-abc", "20261002-cohort-1")));
        Assert.Contains("原始匿名评审答卷", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.ManifestPath));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "blind-cohort-" + Guid.NewGuid().ToString("N"));
        private string ReviewerDirectory => Path.Combine(_root, "reviewer");
        public string GoldPath => Path.Combine(ReviewerDirectory, "gold.jsonl");
        public string ReportPath => Path.Combine(ReviewerDirectory, "report.json");
        public string ComparisonsPath => Path.Combine(ReviewerDirectory, "comparisons.jsonl");
        public string PairwiseAnswersPath => Path.Combine(ReviewerDirectory, "pairwise-answers.jsonl");
        public string PairwiseReviewPath => Path.Combine(ReviewerDirectory, "pairwise-review.jsonl");
        public string PairwiseMapPath => Path.Combine(_root, "custodian", "pairwise-map.json");
        public string ManifestPath => Path.Combine(_root, "custodian", "cohort-manifest.json");
        public BlindEvaluationCohortCandidateInput CandidateA { get; }
        public BlindEvaluationCohortCandidateInput CandidateB { get; }

        public void SetGoldSplit(string split)
        {
            var gold = JsonNode.Parse(File.ReadAllText(GoldPath))!;
            gold["split"] = split;
            File.WriteAllText(GoldPath, gold.ToJsonString() + Environment.NewLine);
            var hash = HashFile(GoldPath);
            foreach (var candidate in new[] { CandidateA, CandidateB })
            {
                UpdateJson(candidate.RunInfoPath, "dataset_sha256", hash);
                UpdateJson(candidate.SealedCandidateMapPath, "dataset_sha256", hash);
                UpdateJson(candidate.RunInfoPath, "split", split);
                UpdateJson(candidate.SealedCandidateMapPath, "split", split);
            }
            var pairwiseMap = JsonNode.Parse(File.ReadAllText(PairwiseMapPath))!;
            pairwiseMap["split"] = split;
            File.WriteAllText(PairwiseMapPath, pairwiseMap.ToJsonString());

            var goldRecord = JsonSerializer.Deserialize<BlindEvaluationRecord>(File.ReadAllText(GoldPath), Options)!;
            var reviewed = new[] { CandidateA.ReviewedPredictionsPath, CandidateB.ReviewedPredictionsPath }
                .SelectMany(File.ReadLines)
                .Select(line => JsonSerializer.Deserialize<BlindEvaluationPrediction>(line, Options)!).ToArray();
            var comparison = JsonSerializer.Deserialize<BlindEvaluationPairwiseComparison>(File.ReadAllText(ComparisonsPath), Options)!;
            var report = JsonNode.Parse(File.ReadAllText(ReportPath))!;
            report["evaluation_split"] = split;
            report["scoring"] = JsonSerializer.SerializeToNode(BlindEvaluationScorer.Evaluate([goldRecord], reviewed, [comparison]), Options);
            File.WriteAllText(ReportPath, report.ToJsonString());
        }

        public void SetCandidateConfirmation(BlindEvaluationCohortCandidateInput candidate, bool confirmed)
        {
            UpdateJson(candidate.RunInfoPath, "frozen_test_unlock_confirmed", confirmed);
            UpdateJson(candidate.SealedCandidateMapPath, "frozen_test_unlock_confirmed", confirmed);
        }

        private static void UpdateJson(string path, string property, JsonNode value)
        {
            var node = JsonNode.Parse(File.ReadAllText(path))!;
            node[property] = value.DeepClone();
            File.WriteAllText(path, node.ToJsonString());
        }

        public Fixture(bool validHumanReviewEvidence = true, bool differentPromptBundles = false, bool differentSamplingParameters = false, bool differentOutputContracts = false)
        {
            Directory.CreateDirectory(ReviewerDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath)!);
            var goldRecordInput = new BlindEvaluationRecord
            {
                Id = "case-1", Task = "polish", SemanticFamilyId = "family-1",
                Source = new("project_owned", "test", "synthetic"), Input = "hello", InputStyle = "colloquial",
                Constraints = [], RiskLevel = "low", Split = "development", ExpectedDecision = "produce",
                Annotations = new([], "neutral", [], false, [])
            };
            var labels = new BlindEvaluationGoldLabelSet(goldRecordInput.Task, goldRecordInput.InputStyle, goldRecordInput.Constraints,
                goldRecordInput.RiskLevel, goldRecordInput.ExpectedDecision, goldRecordInput.ReferenceOutput, goldRecordInput.Annotations);
            goldRecordInput = goldRecordInput with
            {
                HumanReview = validHumanReviewEvidence
                    ? new(["gold-a", "gold-b"], "accepted", null, [new("gold-a", labels), new("gold-b", labels)])
                    : new(["gold-a", "gold-b"], "accepted")
            };
            File.WriteAllText(GoldPath, JsonSerializer.Serialize(goldRecordInput, Options) + Environment.NewLine);
            CandidateA = WriteCandidate("a", "candidate-a", "ok-a");
            CandidateB = WriteCandidate("b", "candidate-b", "ok-b", differentPromptBundles, differentSamplingParameters, differentOutputContracts);
            var gold = JsonSerializer.Deserialize<Huaxiazi.DatasetBuilder.BlindEvaluationRecord>(File.ReadAllText(GoldPath), Options)!;
            var reviewed = new[] { CandidateA.ReviewedPredictionsPath, CandidateB.ReviewedPredictionsPath }
                .SelectMany(File.ReadLines)
                .Select(line => JsonSerializer.Deserialize<Huaxiazi.DatasetBuilder.BlindEvaluationPrediction>(line, Options)!).ToArray();
            var reviewItem = new Huaxiazi.DatasetBuilder.BlindPairwiseReviewItem("review-random-1", "polish", "hello",
                new Dictionary<string, JsonElement>(), [], "low", [], "neutral", [], false, [], "ok-a", "ok-b");
            var reviewJsonl = JsonSerializer.Serialize(reviewItem, Options) + Environment.NewLine;
            File.WriteAllText(PairwiseReviewPath, reviewJsonl);
            var sealedItem = new Huaxiazi.DatasetBuilder.BlindPairwiseSealedMapItem("review-random-1", "case-1", "candidate-a", "candidate-b", HashText("ok-a"), HashText("ok-b"));
            File.WriteAllText(PairwiseMapPath, JsonSerializer.Serialize(new Huaxiazi.DatasetBuilder.BlindPairwiseSealedMap("development", HashText(reviewJsonl), [sealedItem]), Options));
            var answer = new Huaxiazi.DatasetBuilder.BlindPairwiseAnswerTemplate("review-random-1",
                [new("r1", "left"), new("r2", "left")], null);
            var answersJsonl = JsonSerializer.Serialize(answer, Options) + Environment.NewLine;
            File.WriteAllText(PairwiseAnswersPath, answersJsonl);
            var merged = Huaxiazi.DatasetBuilder.BlindEvaluationPairwisePackageBuilder.MergeAnswers([answer], [sealedItem]);
            var comparisons = JsonSerializer.Serialize(merged[0], Options) + Environment.NewLine;
            File.WriteAllText(ComparisonsPath, comparisons);
            var comparison = JsonSerializer.Deserialize<Huaxiazi.DatasetBuilder.BlindEvaluationPairwiseComparison>(comparisons, Options)!;
            var scoring = Huaxiazi.DatasetBuilder.BlindEvaluationScorer.Evaluate([gold], reviewed, [comparison]);
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
            }, Options) + Environment.NewLine);
        }

        private BlindEvaluationCohortCandidateInput WriteCandidate(string folder, string candidateId, string output,
            bool differentPromptBundle = false, bool differentSamplingParameters = false, bool differentOutputContract = false)
        {
            var directory = Path.Combine(ReviewerDirectory, folder);
            Directory.CreateDirectory(directory);
            var raw = Path.Combine(directory, "predictions.jsonl");
            var reviewed = Path.Combine(directory, "reviewed-predictions.jsonl");
            var runInfo = Path.Combine(directory, "run-info.json");
            var sealedMap = Path.Combine(_root, "custodian", folder + "-sealed.json");
            var systemPromptHash = HashText(candidateId == "candidate-b" && differentPromptBundle ? "different system prompt" : "shared system prompt");
            var userMessageHash = HashText("shared user message");
            var outputContractHash = HashText(candidateId == "candidate-b" && differentOutputContract ? "different output contract" : "shared output contract");
            var promptHash = HashText($"case-1\t{systemPromptHash}\t{userMessageHash}\t{outputContractHash}");
            var systemPromptBundleHash = HashText($"case-1\t{systemPromptHash}");
            var userMessageBundleHash = HashText($"case-1\t{userMessageHash}");
            var temperature = candidateId == "candidate-b" && differentSamplingParameters ? 0.7 : 0.2;
            var telemetry = new { latency_milliseconds = 100, api_input_tokens = (int?)null, api_output_tokens = (int?)null, local_tokens_per_second = 2, local_peak_memory_bytes = (long?)null, local_model_load_milliseconds = (double?)null, error_category = (string?)null };
            var reviews = new[]
            {
                new { reviewer_id = "r1", fact_constraint_retained = true, directly_usable = true, tone_matched = true, clarification_decision_correct = true, high_risk_key_fact_reversed = false, safety_pass = true, fidelity_score = 3, task_completion_score = 4, naturalness_score = 4, direct_usability_score = 4, safety_score = 5 },
                new { reviewer_id = "r2", fact_constraint_retained = true, directly_usable = true, tone_matched = true, clarification_decision_correct = true, high_risk_key_fact_reversed = false, safety_pass = true, fidelity_score = 3, task_completion_score = 4, naturalness_score = 4, direct_usability_score = 4, safety_score = 5 }
            };
            var rawLine = JsonSerializer.Serialize(new { id = "case-1", candidate_id = candidateId, output, schema_valid = true, reviews = Array.Empty<object>(), adjudication = (object?)null, telemetry, status = "success" });
            var reviewedLine = JsonSerializer.Serialize(new { id = "case-1", candidate_id = candidateId, output, schema_valid = true, reviews, adjudication = (object?)null, telemetry, status = "success" });
            File.WriteAllText(raw, rawLine + Environment.NewLine);
            File.WriteAllText(reviewed, reviewedLine + Environment.NewLine);
            var hash = HashFile(GoldPath);
            File.WriteAllText(runInfo, JsonSerializer.Serialize(new { run_info_version = 2, candidate_id = candidateId, split = "development", frozen_test_unlock_confirmed = false, sample_count = 1, dataset_sha256 = hash, predictions_sha256 = HashFile(raw), prompt_bundle_sha256 = promptHash, output_contract_bundle_sha256 = outputContractHash, hardware_profile = "test-host", sensitive_output_withheld_count = 0, content_telemetry_enabled = false, provider_request_count = 1, started_at_utc = "2026-10-02T00:00:00Z", completed_at_utc = "2026-10-02T00:01:00Z" }));
            File.WriteAllText(sealedMap, JsonSerializer.Serialize(new { manifest_version = 2, candidate_id = candidateId, provider = "Local", platform = "Ollama", protocol = "OpenAICompatible", resolved_model_id = "qwen3:4b", dataset_sha256 = hash, split = "development", frozen_test_unlock_confirmed = false, prompt_bundle_sha256 = promptHash, output_contract_bundle_sha256 = outputContractHash, system_prompt_bundle_sha256 = systemPromptBundleHash, user_message_bundle_sha256 = userMessageBundleHash, hardware_profile = "test-host", profile = new { temperature, top_p = 0.9, max_tokens = 512, inference_level = "Balanced" } }));
            return new(candidateId, raw, reviewed, runInfo, sealedMap);
        }

        public JsonSerializerOptions Options { get; } = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        private static string HashText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
    }
}
