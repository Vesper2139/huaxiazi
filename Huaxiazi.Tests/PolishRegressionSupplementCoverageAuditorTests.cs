using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Text.Json;
using Huaxiazi.DatasetBuilder;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class PolishRegressionSupplementCoverageAuditorTests
{
    [Fact]
    public void Build_ReportsUnreviewedFamilyCoverageAndDeficitsWithoutAuthorizingGeneration()
    {
        using var fixture = new Fixture();

        var result = PolishRegressionSupplementCoverageAuditor.Build(fixture.CasesPath, fixture.ManifestPath,
            ["clarification_positive_examples", "format_and_schema_requirements", "prompt_injection_resistance"], 2, fixture.OutputPath);

        Assert.Equal(2, result.FamilyCount);
        Assert.Equal(4, result.SpecificationDeficitCount);
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.OutputPath, "coverage-report.json")));
        var root = report.RootElement;
        Assert.Equal(0, root.GetProperty("phase_0_gate_contribution").GetInt32());
        Assert.True(root.GetProperty("not_admissible_as_blind_eval").GetBoolean());
        Assert.False(root.GetProperty("supplementation_authorized").GetBoolean());
        Assert.Equal(4, root.GetProperty("total_additional_families_to_target").GetInt32());
        Assert.Equal("ai_draft_unreviewed", root.GetProperty("evidence_status").GetString());
        Assert.True(File.Exists(Path.Combine(fixture.OutputPath, "manifest.json")));
    }

    [Fact]
    public void Build_RejectsTamperedCasesBeforeCreatingOutput()
    {
        using var fixture = new Fixture();
        File.AppendAllText(fixture.CasesPath,
            "{\"id\":\"case-c\",\"family_id\":\"family-c\",\"split\":\"development\",\"input\":\"另一条样本\",\"origin\":\"ai_assisted_draft\",\"human_status\":\"unreviewed\",\"context\":{\"gap_behavior\":\"format_and_schema_requirements\"}}" + Environment.NewLine,
            new UTF8Encoding(false));

        Assert.Throws<InvalidDataException>(() => PolishRegressionSupplementCoverageAuditor.Build(fixture.CasesPath,
            fixture.ManifestPath, ["format_and_schema_requirements"], 2, fixture.OutputPath));
        Assert.False(Directory.Exists(fixture.OutputPath));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "polish-regression-supplement-coverage-" + Guid.NewGuid().ToString("N"));
        public string CasesPath => Path.Combine(_root, "cases.jsonl");
        public string ManifestPath => Path.Combine(_root, "manifest.json");
        public string OutputPath => Path.Combine(_root, "coverage-report");

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
            File.WriteAllLines(CasesPath,
            [
                JsonSerializer.Serialize(new { id = "case-a", family_id = "family-a", split = "development", input = "请问具体日期是什么？", origin = "ai_assisted_draft", human_status = "unreviewed", context = new { gap_behavior = "clarification_positive_examples" } }, options),
                JsonSerializer.Serialize(new { id = "case-b", family_id = "family-b", split = "regression", input = "请输出 JSON。", origin = "ai_assisted_draft", human_status = "unreviewed", context = new { gap_behavior = "format_and_schema_requirements" } }, options)
            ], new UTF8Encoding(false));
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(CasesPath))).ToLowerInvariant();
            File.WriteAllText(ManifestPath,
                "{\"dataset_version\":\"hxz-polish-regression-ai-supplement-draft-v1\",\"status\":\"draft\",\"purpose\":\"internal_regression_only\",\"phase_0_gate_contribution\":0,\"not_admissible_as_blind_eval\":true,\"counts\":{\"cases\":2,\"families\":2},\"files\":{\"cases.jsonl\":\"" + hash + "\"}}" + Environment.NewLine,
                new UTF8Encoding(false));
        }
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
    }
}
