using System.IO;
using System.Text.Json;
using Huaxiazi.DatasetBuilder;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class PolishRegressionHumanReviewPacketBuilderTests
{
    [Fact]
    public void Build_FromFrozenWorkspaceCorpusCreatesSeparatedSourceAndDraftReviewsForAllFamilies()
    {
        using var fixture = new Fixture();
        var root = FindRepoRoot();
        File.Copy(Path.Combine(root, "datasets", "polish-regression-review-packet-v2", "README.md"), Path.Combine(fixture.Output, "README.md"));
        File.Copy(Path.Combine(root, "datasets", "polish-regression-review-packet-v2", "decision.schema.json"), Path.Combine(fixture.Output, "decision.schema.json"));

        var report = PolishRegressionHumanReviewPacketBuilder.Build(
            Path.Combine(root, "datasets", "polish-regression-v1", "cases.jsonl"),
            Path.Combine(root, "datasets", "polish-agent-v2", "canonical.jsonl"),
            Path.Combine(root, "datasets", "polish-regression-v2-draft", "labels.jsonl"),
            Path.Combine(root, "datasets", "polish-regression-v1", "manifest.json"),
            Path.Combine(root, "datasets", "polish-regression-v2-draft", "manifest.json"),
            Path.Combine(root, "docs", "polish-regression-hierarchy-candidates-2026-10-05-v5.json"),
            fixture.Output);

        Assert.Equal(16, report.FamilyCount);
        Assert.Equal(0, report.Phase0GateContribution);
        var sourceLines = File.ReadAllLines(Path.Combine(fixture.Output, "source-first-pass.jsonl"));
        var comparisonLines = File.ReadAllLines(Path.Combine(fixture.Output, "draft-comparison.jsonl"));
        var decisionLines = File.ReadAllLines(Path.Combine(fixture.Output, "human-decisions.template.jsonl"));
        Assert.Equal(16, sourceLines.Length);
        Assert.Equal(16, comparisonLines.Length);
        Assert.Equal(16, decisionLines.Length);

        foreach (var line in sourceLines)
        {
            using var sourceDocument = JsonDocument.Parse(line);
            var source = sourceDocument.RootElement;
            Assert.False(source.TryGetProperty("provisional_backbone_id", out _));
            Assert.False(source.TryGetProperty("draft_fields", out _));
            Assert.Equal("pending_human_review", source.GetProperty("source_review_status").GetString());
            Assert.Equal(JsonValueKind.Null, source.GetProperty("independent_first_pass").GetProperty("proposed_fields").GetProperty("legacy_task").ValueKind);
        }
        foreach (var line in comparisonLines)
        {
            using var comparisonDocument = JsonDocument.Parse(line);
            Assert.Equal("open_after_source_first_pass", comparisonDocument.RootElement.GetProperty("comparison_status").GetString());
            Assert.Contains("AI assistant", comparisonDocument.RootElement.GetProperty("draft_authors").EnumerateArray().Select(author => author.GetString()));
        }
        foreach (var line in decisionLines)
        {
            using var decisionDocument = JsonDocument.Parse(line);
            var decision = decisionDocument.RootElement;
            Assert.Equal("pending", decision.GetProperty("overall_decision").GetString());
            Assert.Equal(JsonValueKind.Null, decision.GetProperty("reviewer_id").ValueKind);
            Assert.Equal(JsonValueKind.Null, decision.GetProperty("reviewer_is_independent_of_draft_author").ValueKind);
            Assert.True(decision.GetProperty("field_decisions").TryGetProperty("legacy_task", out _));
        }
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Output, "manifest.json")));
        Assert.Equal(0, manifest.RootElement.GetProperty("phase_0_gate_contribution").GetInt32());
        Assert.True(manifest.RootElement.GetProperty("not_admissible_as_blind_eval").GetBoolean());
        Assert.Equal(0, manifest.RootElement.GetProperty("counts").GetProperty("human_verified").GetInt32());
        Assert.Throws<IOException>(() => PolishRegressionHumanReviewPacketBuilder.Build(
            Path.Combine(root, "datasets", "polish-regression-v1", "cases.jsonl"),
            Path.Combine(root, "datasets", "polish-agent-v2", "canonical.jsonl"),
            Path.Combine(root, "datasets", "polish-regression-v2-draft", "labels.jsonl"),
            Path.Combine(root, "datasets", "polish-regression-v1", "manifest.json"),
            Path.Combine(root, "datasets", "polish-regression-v2-draft", "manifest.json"),
            Path.Combine(root, "docs", "polish-regression-hierarchy-candidates-2026-10-05-v5.json"), fixture.Output));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "polish-regression-human-review-packet-" + Guid.NewGuid().ToString("N"));
        public string Output => Path.Combine(_directory, "packet");
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
