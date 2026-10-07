using System.IO;
using System.Text.Json;
using Huaxiazi.DatasetBuilder;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class PolishRegressionCoverageGapAuditorTests
{
    [Fact]
    public void Build_ReportsVariantEvidenceByFamilyAndKeepsAllGapDecisionsPending()
    {
        using var fixture = new Fixture();
        var root = FindRepoRoot();
        var report = PolishRegressionCoverageGapAuditor.Build(
            Path.Combine(root, "datasets", "polish-regression-v1", "cases.jsonl"),
            Path.Combine(root, "datasets", "polish-regression-v1", "variants.jsonl"),
            Path.Combine(root, "datasets", "polish-agent-v2", "canonical.jsonl"),
            Path.Combine(root, "datasets", "polish-regression-v2-draft", "labels.jsonl"),
            Path.Combine(root, "datasets", "polish-regression-v1", "manifest.json"),
            Path.Combine(root, "datasets", "polish-regression-v2-draft", "manifest.json"),
            Path.Combine(root, "docs", "polish-regression-hierarchy-candidates-2026-10-05-v5.json"), fixture.Output);

        Assert.Equal(3000, report.SourceRowCount);
        Assert.Equal(16, report.SemanticFamilyCount);
        Assert.Equal(0, report.Phase0GateContribution);
        using var reportDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Output, "coverage-gap-report.json")));
        var json = reportDocument.RootElement;
        Assert.Equal("internal_regression_coverage_and_gap_triage", json.GetProperty("purpose").GetString());
        Assert.Equal(0, json.GetProperty("human_verified_family_count").GetInt32());
        var dimensions = json.GetProperty("dimensions").EnumerateArray().ToDictionary(
            item => item.GetProperty("name").GetString()!, item => item, StringComparer.Ordinal);
        Assert.Equal(16, dimensions["claims_present"].GetProperty("families_with_value").EnumerateObject().Sum(property => property.Value.GetInt32()));
        Assert.Equal(16, dimensions["claim_quantity"].GetProperty("families_with_value").EnumerateObject().Sum(property => property.Value.GetInt32()));
        Assert.Equal(16, dimensions["draft_input_style"].GetProperty("families_with_value").EnumerateObject().Sum(property => property.Value.GetInt32()));
        Assert.Equal(JsonValueKind.Null, dimensions["draft_input_style"].GetProperty("source_rows_with_value").ValueKind);
        Assert.Equal(3000, dimensions["channel"].GetProperty("source_rows_with_value").EnumerateObject().Sum(property => property.Value.GetInt32()));
        Assert.All(dimensions["channel"].GetProperty("families_with_value").EnumerateObject(), property => Assert.Equal(16, property.Value.GetInt32()));
        Assert.Equal(3000, dimensions["claims_present"].GetProperty("source_rows_with_value").EnumerateObject().Sum(property => property.Value.GetInt32()));
        var gaps = json.GetProperty("gaps").EnumerateArray().ToArray();
        Assert.Equal(11, gaps.Length);
        Assert.All(gaps, gap =>
        {
            Assert.Equal("pending_human_triage", gap.GetProperty("internal_regression_need").GetString());
            Assert.Equal("not_started", gap.GetProperty("supplementation_action").GetString());
        });
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Output, "manifest.json")));
        Assert.Equal("hxz-polish-regression-w4-audit-v2", manifest.RootElement.GetProperty("report_version").GetString());
        Assert.Equal(0, manifest.RootElement.GetProperty("phase_0_gate_contribution").GetInt32());
        Assert.False(manifest.RootElement.GetProperty("supplementation_authorized").GetBoolean());
        Assert.Throws<IOException>(() => PolishRegressionCoverageGapAuditor.Build(
            Path.Combine(root, "datasets", "polish-regression-v1", "cases.jsonl"),
            Path.Combine(root, "datasets", "polish-regression-v1", "variants.jsonl"),
            Path.Combine(root, "datasets", "polish-agent-v2", "canonical.jsonl"),
            Path.Combine(root, "datasets", "polish-regression-v2-draft", "labels.jsonl"),
            Path.Combine(root, "datasets", "polish-regression-v1", "manifest.json"),
            Path.Combine(root, "datasets", "polish-regression-v2-draft", "manifest.json"),
            Path.Combine(root, "docs", "polish-regression-hierarchy-candidates-2026-10-05-v5.json"), fixture.Output));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "polish-regression-coverage-gap-" + Guid.NewGuid().ToString("N"));
        public string Output => Path.Combine(_directory, "output");
        public Fixture() => Directory.CreateDirectory(Output);
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "datasets", "polish-regression-v1", "manifest.json"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository dataset root from test output directory.");
    }
}
