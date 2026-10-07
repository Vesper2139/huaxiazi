using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Text.Json;
using Huaxiazi.DatasetBuilder;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class PolishRegressionSupplementLineageAuditorTests
{
    [Fact]
    public void Validate_AcceptsCompletePerCaseGenerationLineage()
    {
        using var fixture = new Fixture(includeLineage: true);

        var report = PolishRegressionSupplementLineageAuditor.Validate(fixture.CasesPath, fixture.ManifestPath);

        Assert.True(report.Valid, string.Join(";", report.Issues.Select(issue => issue.Code)));
        Assert.Equal(1, report.CaseCount);
        Assert.Equal(0, report.MissingLineageCaseCount);
        Assert.Equal(0, report.Phase0GateContribution);
    }

    [Fact]
    public void Validate_ReportsMissingLineageWithoutPromotingInternalDraft()
    {
        using var fixture = new Fixture(includeLineage: false);

        var report = PolishRegressionSupplementLineageAuditor.Validate(fixture.CasesPath, fixture.ManifestPath);

        Assert.False(report.Valid);
        Assert.Equal(1, report.MissingLineageCaseCount);
        Assert.Contains(report.Issues, issue => issue.Code == "generation-lineage-missing");
        Assert.Equal(0, report.Phase0GateContribution);
        Assert.True(report.NotAdmissibleAsBlindEval);
    }

    [Fact]
    public void Validate_RejectsCaseHashMismatch()
    {
        using var fixture = new Fixture(includeLineage: true);
        File.AppendAllText(fixture.CasesPath, "\n");

        var report = PolishRegressionSupplementLineageAuditor.Validate(fixture.CasesPath, fixture.ManifestPath);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "cases-hash-mismatch");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "supplement-lineage-" + Guid.NewGuid().ToString("N"));
        public string CasesPath => Path.Combine(_root, "cases.jsonl");
        public string ManifestPath => Path.Combine(_root, "manifest.json");

        public Fixture(bool includeLineage)
        {
            Directory.CreateDirectory(_root);
            var generation = includeLineage
                ? "\"generation_lineage\":{\"model_id\":\"provider/model-version\",\"prompt_sha256\":\"" + new string('a', 64) + "\",\"random_seed\":42,\"generated_at_utc\":\"2026-10-05T10:00:00Z\",\"operator\":\"codex-session\"}"
                : "\"generation_lineage\":null";
            var row = "{\"id\":\"case-1\",\"origin\":\"ai_assisted_draft\"," + generation + "}";
            File.WriteAllText(CasesPath, row + Environment.NewLine, new UTF8Encoding(false));
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(CasesPath))).ToLowerInvariant();
            var manifest = new
            {
                dataset_version = "hxz-polish-regression-ai-supplement-draft-v1",
                status = "draft",
                purpose = "internal_regression_only",
                phase_0_gate_contribution = 0,
                not_admissible_as_blind_eval = true,
                files = new Dictionary<string, string> { ["cases.jsonl"] = hash }
            };
            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest) + Environment.NewLine, new UTF8Encoding(false));
        }

        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
    }
}
