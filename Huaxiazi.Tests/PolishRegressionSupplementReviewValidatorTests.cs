using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Huaxiazi.DatasetBuilder;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class PolishRegressionSupplementReviewValidatorTests
{
    [Fact]
    public void Validate_AcceptsCompleteHumanReviewAndParsesQuotedMultilineCells()
    {
        using var fixture = new ReviewFixture();
        fixture.WriteReview("accept", "polish", "", "specific rationale, with comma\nand second line");

        var report = PolishRegressionSupplementReviewValidator.Validate(fixture.Cases, fixture.ReviewCsv, fixture.Manifest);

        Assert.True(report.Valid, string.Join(Environment.NewLine, report.Issues));
        Assert.Equal(1, report.TotalCases);
        Assert.Equal(1, report.ReviewedCount);
        Assert.Equal(1, report.AcceptedCount);
        Assert.Equal(0, report.EditedCount);
        Assert.Equal(0, report.RejectedCount);
        Assert.False(report.ReviewerIdentityVerified);
        Assert.Equal(0, report.Phase0GateContribution);
    }

    [Fact]
    public void Validate_RejectsPlaceholderRationale()
    {
        using var fixture = new ReviewFixture();
        fixture.WriteReview("accept", "polish", "", "无非空理由");

        var report = PolishRegressionSupplementReviewValidator.Validate(fixture.Cases, fixture.ReviewCsv, fixture.Manifest);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "review-rationale");
    }

    [Fact]
    public void Validate_RejectsEditWithoutEditedReferenceOutput()
    {
        using var fixture = new ReviewFixture();
        fixture.WriteReview("edit", "polish", "", "保留数量并删去新增承诺");

        var report = PolishRegressionSupplementReviewValidator.Validate(fixture.Cases, fixture.ReviewCsv, fixture.Manifest);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "edit-output");
    }

    [Fact]
    public void Validate_RejectsSourceInputTamperingAndStaleManifestHash()
    {
        using var fixture = new ReviewFixture();
        fixture.WriteReview("accept", "polish", "", "输入事实完整且成稿没有添加新信息。", inputOverride: "被替换的案例输入");

        var report = PolishRegressionSupplementReviewValidator.Validate(fixture.Cases, fixture.ReviewCsv, fixture.Manifest);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "source-row-mismatch");
    }

    [Fact]
    public void Validate_RejectsManifestThatAllowsBlindEvaluation()
    {
        using var fixture = new ReviewFixture();
        fixture.SetBlindEvalAdmissible(true);

        var report = PolishRegressionSupplementReviewValidator.Validate(fixture.Cases, fixture.ReviewCsv, fixture.Manifest);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "manifest-boundary");
    }

    [Fact]
    public void Validate_RejectsAcceptWhenAQualityDimensionFails()
    {
        using var fixture = new ReviewFixture();
        fixture.WriteReview("accept", "polish", "", "草稿事实与输入一致，但事实保留项被标为失败。", factPreservation: "fail");

        var report = PolishRegressionSupplementReviewValidator.Validate(fixture.Cases, fixture.ReviewCsv, fixture.Manifest);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "accept-quality");
    }

    [Fact]
    public void Validate_RejectsStaleSourceCasesHash()
    {
        using var fixture = new ReviewFixture();
        fixture.SetSourceHash(new string('0', 64));

        var report = PolishRegressionSupplementReviewValidator.Validate(fixture.Cases, fixture.ReviewCsv, fixture.Manifest);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "source-hash");
    }

    [Fact]
    public void Validate_RejectsPotentialSensitiveContentInDraftCases()
    {
        using var fixture = new ReviewFixture();
        fixture.SetCaseInput("请联系 13800138000 处理订单。");
        fixture.WriteReview("accept", "polish", "", "检查后准备接受。", inputOverride: "请联系 13800138000 处理订单。");

        var report = PolishRegressionSupplementReviewValidator.Validate(fixture.Cases, fixture.ReviewCsv, fixture.Manifest);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "sensitive-content");
    }

    [Fact]
    public void Importer_WritesAcceptedDecisionToCreateOnlyOutput()
    {
        using var fixture = new ReviewFixture();
        fixture.WriteReview("accept", "polish", "", "原稿准确保留数量和禁止新增承诺的约束。 ");
        var output = fixture.ExternalOutputPath;

        var result = PolishRegressionSupplementReviewImporter.Import(fixture.Cases, fixture.ReviewCsv, fixture.Manifest, output);
        using var record = JsonDocument.Parse(File.ReadAllText(output));

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(0, result.EditedCount);
        Assert.Equal(0, result.RejectedCount);
        Assert.Equal(0, result.Phase0GateContribution);
        Assert.False(result.ReviewerIdentityVerified);
        Assert.Equal("accepted", record.RootElement.GetProperty("human_status").GetString());
        Assert.Equal("请保留4个箱子，不增加其他承诺。", record.RootElement.GetProperty("decision").GetProperty("reference_output").GetString());
        Assert.Equal(result.OutputSha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output))).ToLowerInvariant());
        var saved = File.ReadAllBytes(output);
        Assert.Throws<IOException>(() => PolishRegressionSupplementReviewImporter.Import(fixture.Cases, fixture.ReviewCsv,
            fixture.Manifest, output));
        Assert.Equal(saved, File.ReadAllBytes(output));
    }

    [Fact]
    public void Importer_PreservesEditedReferenceOutputAndFinalDecision()
    {
        using var fixture = new ReviewFixture();
        fixture.WriteReview("edit", "clarify", "请补充箱子的具体数量。", "原稿误把数量当成确定值，需先向用户澄清。 ");

        var result = PolishRegressionSupplementReviewImporter.Import(fixture.Cases, fixture.ReviewCsv, fixture.Manifest, fixture.ExternalOutputPath);
        using var record = JsonDocument.Parse(File.ReadAllText(fixture.ExternalOutputPath));

        Assert.Equal(1, result.EditedCount);
        Assert.Equal("edited", record.RootElement.GetProperty("human_status").GetString());
        Assert.Equal("clarify", record.RootElement.GetProperty("decision").GetProperty("expected_decision").GetString());
        Assert.Equal("请补充箱子的具体数量。", record.RootElement.GetProperty("decision").GetProperty("reference_output").GetString());
    }

    [Fact]
    public void Importer_RetainsRejectedDecisionWithoutAddingItAsAcceptedContent()
    {
        using var fixture = new ReviewFixture();
        fixture.WriteReview("reject", "", "", "该案例把原文已知数量改造成未知信息，任务目标不成立。 ");

        var result = PolishRegressionSupplementReviewImporter.Import(fixture.Cases, fixture.ReviewCsv, fixture.Manifest, fixture.ExternalOutputPath);
        using var record = JsonDocument.Parse(File.ReadAllText(fixture.ExternalOutputPath));

        Assert.Equal(1, result.RejectedCount);
        Assert.Equal("rejected", record.RootElement.GetProperty("human_status").GetString());
        Assert.Equal(JsonValueKind.Null, record.RootElement.GetProperty("decision").GetProperty("reference_output").ValueKind);
    }

    [Fact]
    public void Importer_RefusesInvalidReviewWithoutCreatingOutput()
    {
        using var fixture = new ReviewFixture();
        fixture.WriteReview("accept", "polish", "", "无非空理由");
        var output = fixture.ExternalOutputPath;

        Assert.Throws<InvalidDataException>(() => PolishRegressionSupplementReviewImporter.Import(
            fixture.Cases, fixture.ReviewCsv, fixture.Manifest, output));

        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Importer_RefusesOutputInsideReviewInputDirectory()
    {
        using var fixture = new ReviewFixture();

        Assert.Throws<InvalidDataException>(() => PolishRegressionSupplementReviewImporter.Import(
            fixture.Cases, fixture.ReviewCsv, fixture.Manifest, Path.Combine(Path.GetDirectoryName(fixture.ReviewCsv)!, "decisions.jsonl")));
    }

    [Fact]
    public void Program_ExposesSupplementReviewImportCommand()
    {
        using var fixture = new ReviewFixture();
        var originalOutput = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            var exitCode = Program.Main([
                "polish-regression-supplement-review-import",
                "--cases", fixture.Cases,
                "--review-csv", fixture.ReviewCsv,
                "--manifest", fixture.Manifest,
                "--output", fixture.ExternalOutputPath
            ]);

            Assert.Equal(0, exitCode);
            Assert.Contains("\"imported_count\": 1", output.ToString(), StringComparison.Ordinal);
        }
        finally { Console.SetOut(originalOutput); }
    }

    [Fact]
    public void Program_ExposesSupplementReviewValidationCommand()
    {
        using var fixture = new ReviewFixture();
        var originalOutput = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            var exitCode = Program.Main([
                "polish-regression-supplement-review-validate",
                "--cases", fixture.Cases,
                "--review-csv", fixture.ReviewCsv,
                "--manifest", fixture.Manifest
            ]);

            Assert.Equal(0, exitCode);
            Assert.Contains("\"valid\": true", output.ToString(), StringComparison.Ordinal);
        }
        finally { Console.SetOut(originalOutput); }
    }

    private sealed class ReviewFixture : IDisposable
    {
        private static readonly string[] Headers =
        [
            "case_id", "family_id", "split", "gap_behavior", "scenario", "purpose", "formality",
            "expected_decision_draft", "input", "constraints", "claims", "reference_output_draft",
            "human_verdict", "human_expected_decision", "human_edited_reference_output", "fact_preservation",
            "constraint_following", "format_and_tone", "reviewer_id", "reviewed_at_utc", "rationale"
        ];

        private readonly string _directory = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts",
            "polish-regression-supplement-review-" + Guid.NewGuid().ToString("N"));
        public string Cases => Path.Combine(_directory, "cases.jsonl");
        public string ReviewCsv => Path.Combine(_directory, "review.csv");
        public string Manifest => Path.Combine(_directory, "manifest.json");
        public string ExternalOutputPath => Path.Combine(_directory + "-results", "human-decisions.jsonl");
        private string Readme => Path.Combine(_directory, "README.md");

        public ReviewFixture()
        {
            Directory.CreateDirectory(_directory);
            var caseJson = """
                {"schema_version":"1.0","id":"hxz-polish-reg-ai-draft-001","family_id":"hxz-polish-reg-family-0123456789abcdef","split":"development","task":"polish","input":"请润色：保留4个箱子，不要增加承诺。","context":{"gap_behavior":"format_and_schema_requirements","purpose":"仓储通知","formality":"concise"},"constraints":["保留4个箱子","不得增加承诺"],"claims":[{"subject":"箱子","relation":"数量","object":"4个"}],"expected_decision":"polish","reference_output":"请保留4个箱子，不增加其他承诺。","origin":"ai_assisted_draft","provenance":{"source_dataset":"coverage-gap-report.json","source_record_id":"coverage-gap-format_and_schema_requirements","source_sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","transform_version":"ai-authored-supplement-draft-v1"},"human_status":"unreviewed","family_status":"ai_generated_unreviewed","limitations":"AI draft; unreviewed."}
                """;
            File.WriteAllText(Cases, caseJson + Environment.NewLine, new UTF8Encoding(false));
            var sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Cases))).ToLowerInvariant();
            File.WriteAllText(Readme, "Review instructions.", new UTF8Encoding(false));
            var readmeHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Readme))).ToLowerInvariant();
            File.WriteAllText(Manifest, JsonSerializer.Serialize(new
            {
                package = "hxz-polish-regression-ai-supplement-review-2026-10-05",
                source_dataset = "datasets/polish-regression-ai-supplement-draft-v1",
                source_cases_sha256 = sourceHash,
                rows = 1,
                phase_0_gate_contribution = 0,
                not_admissible_as_blind_eval = true,
                files = new Dictionary<string, string> { ["README.md"] = readmeHash }
            }), new UTF8Encoding(false));
            WriteReview("accept", "polish", "", "基于输入中的数量和约束，参考成稿没有改变事实。 ");
        }

        public void WriteReview(string verdict, string expectedDecision, string editedOutput, string rationale,
            string? inputOverride = null, string factPreservation = "pass")
        {
            var values = new[]
            {
                "hxz-polish-reg-ai-draft-001", "hxz-polish-reg-family-0123456789abcdef", "development",
                "format_and_schema_requirements", "", "仓储通知", "concise", "polish",
                inputOverride ?? "请润色：保留4个箱子，不要增加承诺。", "保留4个箱子；不得增加承诺",
                "[{\"subject\":\"箱子\",\"relation\":\"数量\",\"object\":\"4个\"}]",
                "请保留4个箱子，不增加其他承诺。", verdict, expectedDecision, editedOutput,
                factPreservation, "pass", "pass", "reviewer-1", "2026-10-05T08:00:00Z", rationale
            };
            File.WriteAllText(ReviewCsv, string.Join(',', Headers.Select(Csv)) + Environment.NewLine +
                string.Join(',', values.Select(Csv)) + Environment.NewLine, new UTF8Encoding(false));
        }

        public void SetBlindEvalAdmissible(bool value)
        {
            var manifest = JsonNode.Parse(File.ReadAllText(Manifest))!.AsObject();
            manifest["not_admissible_as_blind_eval"] = !value;
            File.WriteAllText(Manifest, manifest.ToJsonString(), new UTF8Encoding(false));
        }

        public void SetSourceHash(string value)
        {
            var manifest = JsonNode.Parse(File.ReadAllText(Manifest))!.AsObject();
            manifest["source_cases_sha256"] = value;
            File.WriteAllText(Manifest, manifest.ToJsonString(), new UTF8Encoding(false));
        }

        public void SetCaseInput(string value)
        {
            var item = JsonNode.Parse(File.ReadAllText(Cases))!.AsObject();
            item["input"] = value;
            File.WriteAllText(Cases, item.ToJsonString() + Environment.NewLine, new UTF8Encoding(false));
            var manifest = JsonNode.Parse(File.ReadAllText(Manifest))!.AsObject();
            manifest["source_cases_sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Cases))).ToLowerInvariant();
            File.WriteAllText(Manifest, manifest.ToJsonString(), new UTF8Encoding(false));
        }

        private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
    }
}
