using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Huaxiazi.DatasetBuilder;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class PolishRegressionNearDuplicateAuditorTests
{
    [Fact]
    public void Build_QueuesOnlyThresholdPairsAndProducesByteStableArtifacts()
    {
        using var fixture = new Fixture();

        var first = PolishRegressionNearDuplicateAuditor.Build(fixture.CasesPath, fixture.ParentManifestPath, fixture.RulesPath, fixture.OutputA);
        var second = PolishRegressionNearDuplicateAuditor.Build(fixture.CasesPath, fixture.ParentManifestPath, fixture.RulesPath, fixture.OutputB);

        Assert.Equal(3, first.FamilyCount);
        Assert.Equal(3, first.PairCount);
        Assert.Equal(1, first.CandidatePairCount);
        Assert.Equal(1, first.CrossSplitCandidatePairCount);
        Assert.Equal(second.CandidatePairCount, first.CandidatePairCount);
        var firstPairs = File.ReadAllBytes(Path.Combine(fixture.OutputA, "candidate-pairs.jsonl"));
        var secondPairs = File.ReadAllBytes(Path.Combine(fixture.OutputB, "candidate-pairs.jsonl"));
        Assert.Equal(SHA256.HashData(firstPairs), SHA256.HashData(secondPairs));
        var firstTemplate = File.ReadAllBytes(Path.Combine(fixture.OutputA, "human-adjudication.template.jsonl"));
        var secondTemplate = File.ReadAllBytes(Path.Combine(fixture.OutputB, "human-adjudication.template.jsonl"));
        Assert.Equal(SHA256.HashData(firstTemplate), SHA256.HashData(secondTemplate));
        using var template = JsonDocument.Parse(Encoding.UTF8.GetString(firstTemplate));
        Assert.Equal("pending_human_adjudication", template.RootElement.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, template.RootElement.GetProperty("reviewer_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, template.RootElement.GetProperty("decision").ValueKind);
        Assert.Equal(JsonValueKind.Null, template.RootElement.GetProperty("rationale").ValueKind);

        using var pairDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.OutputA, "candidate-pairs.jsonl")));
        var pair = pairDocument.RootElement;
        Assert.Equal("case-a", pair.GetProperty("left_case_id").GetString());
        Assert.Equal("case-b", pair.GetProperty("right_case_id").GetString());
        Assert.Equal(1.0, pair.GetProperty("char_trigram_jaccard").GetDouble());
        Assert.Equal("pending_human_adjudication", pair.GetProperty("adjudication_status").GetString());

        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.OutputA, "report.json")));
        Assert.Equal(0, report.RootElement.GetProperty("phase_0_gate_contribution").GetInt32());
        Assert.True(report.RootElement.GetProperty("not_admissible_as_blind_eval").GetBoolean());
        Assert.False(report.RootElement.GetProperty("auto_merge_performed").GetBoolean());
    }

    [Fact]
    public void ProgramCommand_WritesCandidateReportToCreateOnlyDirectory()
    {
        using var fixture = new Fixture();
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exitCode = Program.Main([
                "polish-regression-neardup-report",
                "--cases", fixture.CasesPath,
                "--parent-manifest", fixture.ParentManifestPath,
                "--rules", fixture.RulesPath,
                "--output", fixture.OutputA
            ]);

            Assert.Equal(0, exitCode);
            using var summary = JsonDocument.Parse(output.ToString());
            Assert.Equal(1, summary.RootElement.GetProperty("candidate_pair_count").GetInt32());
            Assert.True(File.Exists(Path.Combine(fixture.OutputA, "candidate-pairs.jsonl")));
            Assert.True(File.Exists(Path.Combine(fixture.OutputA, "manifest.json")));

            var decision = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.OutputA, "human-adjudication.template.jsonl")))!.AsObject();
            decision["reviewer_id"] = "reviewer-001";
            decision["reviewed_at_utc"] = "2026-10-05T12:00:00Z";
            decision["decision"] = "distinct_task_intent_same_backbone";
            decision["rationale"] = "同一事实骨架，但任务意图不同。";
            decision["status"] = "adjudicated";
            var decisionsPath = Path.Combine(fixture.Root, "human-decisions.completed.jsonl");
            File.WriteAllText(decisionsPath, decision.ToJsonString() + Environment.NewLine, new UTF8Encoding(false));
            using var validationOutput = new StringWriter();
            Console.SetOut(validationOutput);
            Assert.Equal(0, Program.Main([
                "polish-regression-neardup-validate",
                "--report-dir", fixture.OutputA,
                "--decisions", decisionsPath
            ]));
            using (var validation = JsonDocument.Parse(validationOutput.ToString()))
                Assert.True(validation.RootElement.GetProperty("valid").GetBoolean());

            Console.SetOut(output);
            var pairsBefore = File.ReadAllBytes(Path.Combine(fixture.OutputA, "candidate-pairs.jsonl"));
            Assert.Equal(2, Program.Main([
                "polish-regression-neardup-report",
                "--cases", fixture.CasesPath,
                "--parent-manifest", fixture.ParentManifestPath,
                "--rules", fixture.RulesPath,
                "--output", fixture.OutputA
            ]));
            Assert.Equal(pairsBefore, File.ReadAllBytes(Path.Combine(fixture.OutputA, "candidate-pairs.jsonl")));
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
    }

    [Fact]
    public void Build_RejectsCasesFileNotBoundToFrozenParentManifest()
    {
        using var fixture = new Fixture();
        File.AppendAllText(fixture.CasesPath,
            "{\"id\":\"case-d\",\"family_id\":\"family-d\",\"split\":\"regression\",\"input\":\"另一个输入内容。\"}" + Environment.NewLine,
            new UTF8Encoding(false));

        Assert.Throws<InvalidDataException>(() => PolishRegressionNearDuplicateAuditor.Build(
            fixture.CasesPath, fixture.ParentManifestPath, fixture.RulesPath, fixture.OutputA));
        Assert.False(Directory.Exists(fixture.OutputA));
    }

    [Fact]
    public void AdjudicationValidator_AcceptsCompleteDecisionBoundToCandidate()
    {
        using var fixture = new Fixture();
        PolishRegressionNearDuplicateAuditor.Build(fixture.CasesPath, fixture.ParentManifestPath, fixture.RulesPath, fixture.OutputA);
        var decision = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.OutputA, "human-adjudication.template.jsonl")))!.AsObject();
        decision["reviewer_id"] = "reviewer-001";
        decision["reviewed_at_utc"] = "2026-10-05T12:00:00Z";
        decision["decision"] = "distinct_task_intent_same_backbone";
        decision["rationale"] = "相同事实骨架，但用户请求的表达意图不同。";
        decision["status"] = "adjudicated";
        var decisionsPath = Path.Combine(fixture.Root, "human-decisions.completed.jsonl");
        File.WriteAllText(decisionsPath, decision.ToJsonString() + Environment.NewLine, new UTF8Encoding(false));

        var report = PolishRegressionNearDuplicateAdjudicationValidator.Validate(fixture.OutputA, decisionsPath);

        Assert.True(report.Valid, string.Join(";", report.Issues.Select(issue => issue.Code)));
        Assert.Equal(1, report.CandidateCount);
        Assert.Equal(1, report.AdjudicatedCount);
        Assert.False(report.ReviewerIdentityVerified);
    }

    [Fact]
    public void AdjudicationValidator_RejectsPlaceholderRationale()
    {
        using var fixture = new Fixture();
        PolishRegressionNearDuplicateAuditor.Build(fixture.CasesPath, fixture.ParentManifestPath, fixture.RulesPath, fixture.OutputA);
        var decision = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.OutputA, "human-adjudication.template.jsonl")))!.AsObject();
        decision["reviewer_id"] = "reviewer-001";
        decision["reviewed_at_utc"] = "2026-10-05T12:00:00Z";
        decision["decision"] = "distinct_task_intent_same_backbone";
        decision["rationale"] = "无非空理由";
        decision["status"] = "adjudicated";
        var decisionsPath = Path.Combine(fixture.Root, "human-decisions.completed.jsonl");
        File.WriteAllText(decisionsPath, decision.ToJsonString() + Environment.NewLine, new UTF8Encoding(false));

        var report = PolishRegressionNearDuplicateAdjudicationValidator.Validate(fixture.OutputA, decisionsPath);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "decision-rationale");
        Assert.Equal(0, report.AdjudicatedCount);
    }

    [Fact]
    public void AdjudicationValidator_RejectsBlankTemplateAsCompletedReview()
    {
        using var fixture = new Fixture();
        PolishRegressionNearDuplicateAuditor.Build(fixture.CasesPath, fixture.ParentManifestPath, fixture.RulesPath, fixture.OutputA);
        var template = File.ReadAllText(Path.Combine(fixture.OutputA, "human-adjudication.template.jsonl"));
        var decisionsPath = Path.Combine(fixture.Root, "human-decisions.completed.jsonl");
        File.WriteAllText(decisionsPath, template, new UTF8Encoding(false));

        var report = PolishRegressionNearDuplicateAdjudicationValidator.Validate(fixture.OutputA, decisionsPath);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "decision-fields");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "polish-regression-neardup-" + Guid.NewGuid().ToString("N"));
        public string Root => _root;
        public string CasesPath => Path.Combine(_root, "cases.jsonl");
        public string ParentManifestPath => Path.Combine(_root, "parent-manifest.json");
        public string RulesPath => Path.Combine(_root, "rules.json");
        public string OutputA => Path.Combine(_root, "output-a");
        public string OutputB => Path.Combine(_root, "output-b");

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
            File.WriteAllLines(CasesPath,
            [
                JsonSerializer.Serialize(new { id = "case-a", family_id = "family-a", split = "development", input = "今天我们讨论项目进度并确认下周安排。" }, options),
                JsonSerializer.Serialize(new { id = "case-b", family_id = "family-b", split = "regression", input = "今天我们讨论项目进度并确认下周安排！" }, options),
                JsonSerializer.Serialize(new { id = "case-c", family_id = "family-c", split = "development", input = "苹果在山上长大，小猫喜欢睡觉。" }, options)
            ], new UTF8Encoding(false));
            var casesHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(CasesPath))).ToLowerInvariant();
            File.WriteAllText(ParentManifestPath,
                "{\"dataset_version\":\"hxz-polish-regression-v1\",\"status\":\"frozen\",\"phase_0_gate_contribution\":0,\"not_admissible_as_blind_eval\":true,\"counts\":{\"families\":3},\"files\":{\"cases.jsonl\":\"" + casesHash + "\"}}" + Environment.NewLine,
                new UTF8Encoding(false));
            File.WriteAllText(RulesPath, "{\"schema_version\":\"1.0\",\"ruleset_id\":\"test-char-trigram-v1\",\"normalization\":\"unicode-formkc-lowercase-alphanumeric-codepoint-trigrams-v1\",\"shingle_size\":3,\"candidate_threshold\":0.8,\"sensitivity_thresholds\":[0.7,0.8,0.9]}" + Environment.NewLine, new UTF8Encoding(false));
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
