using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Huaxiazi.DatasetBuilder;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class PolishRegressionHumanReviewValidatorTests
{
    [Fact]
    public void Validate_RequiresCompleteReviewCoverageAndPreservesPhase0Boundary()
    {
        using var fixture = new ReviewFixture();
        var report = PolishRegressionHumanReviewValidator.Validate(fixture.Packet, fixture.FirstPass, fixture.Decisions,
            fixture.Labels, fixture.DraftManifest);

        Assert.True(report.Valid, string.Join(Environment.NewLine, report.Issues));
        Assert.Equal(16, report.ExpectedFamilyCount);
        Assert.Equal(16, report.IndependentFirstPassCount);
        Assert.Equal(16, report.CompletedDecisionCount);
        Assert.Equal(16, report.ApprovalCandidateCount);
        Assert.Equal(0, report.Phase0GateContribution);
    }

    [Fact]
    public void Validate_RejectsAiDraftAuthorAsReviewer()
    {
        using var fixture = new ReviewFixture();
        var lines = File.ReadAllLines(fixture.Decisions);
        var decision = JsonNode.Parse(lines[0])!.AsObject();
        decision["reviewer_id"] = "AI assistant";
        lines[0] = decision.ToJsonString();
        File.WriteAllLines(fixture.Decisions, lines);

        var report = PolishRegressionHumanReviewValidator.Validate(fixture.Packet, fixture.FirstPass, fixture.Decisions,
            fixture.Labels, fixture.DraftManifest);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Contains("裁定者与 AI 草稿作者相同", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsMissingFamilyAndSourceTampering()
    {
        using var fixture = new ReviewFixture();
        var firstPassLines = File.ReadAllLines(fixture.FirstPass);
        firstPassLines[0] = "";
        File.WriteAllLines(fixture.FirstPass, firstPassLines);

        var report = PolishRegressionHumanReviewValidator.Validate(fixture.Packet, fixture.FirstPass, fixture.Decisions,
            fixture.Labels, fixture.DraftManifest);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Contains("已完成首轮审阅缺少族", StringComparison.Ordinal));

        firstPassLines = File.ReadAllLines(fixture.FirstPass);
        var firstPass = JsonNode.Parse(firstPassLines[1])!.AsObject();
        firstPass["input"] = "tampered";
        firstPassLines[1] = firstPass.ToJsonString();
        File.WriteAllLines(fixture.FirstPass, firstPassLines);
        report = PolishRegressionHumanReviewValidator.Validate(fixture.Packet, fixture.FirstPass, fixture.Decisions,
            fixture.Labels, fixture.DraftManifest);
        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Contains("改动了受保护的来源字段 input", StringComparison.Ordinal));
    }

    private sealed class ReviewFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "polish-regression-human-review-validator-" + Guid.NewGuid().ToString("N"));
        private readonly string _root = FindRepoRoot();
        public string Packet => Path.Combine(_directory, "packet");
        public string FirstPass => Path.Combine(_directory, "source-first-pass.completed.jsonl");
        public string Decisions => Path.Combine(_directory, "human-decisions.completed.jsonl");
        public string Labels => Path.Combine(_root, "datasets", "polish-regression-v2-draft", "labels.jsonl");
        public string DraftManifest => Path.Combine(_root, "datasets", "polish-regression-v2-draft", "manifest.json");

        public ReviewFixture()
        {
            Directory.CreateDirectory(Packet);
            var packetStatic = Path.Combine(_root, "datasets", "polish-regression-review-packet-v2");
            File.Copy(Path.Combine(packetStatic, "README.md"), Path.Combine(Packet, "README.md"));
            File.Copy(Path.Combine(packetStatic, "decision.schema.json"), Path.Combine(Packet, "decision.schema.json"));
            PolishRegressionHumanReviewPacketBuilder.Build(
                Path.Combine(_root, "datasets", "polish-regression-v1", "cases.jsonl"),
                Path.Combine(_root, "datasets", "polish-agent-v2", "canonical.jsonl"), Labels,
                Path.Combine(_root, "datasets", "polish-regression-v1", "manifest.json"), DraftManifest,
                Path.Combine(_root, "docs", "polish-regression-hierarchy-candidates-2026-10-05-v5.json"), Packet);
            WriteSyntheticCompletedReviews();
        }

        private void WriteSyntheticCompletedReviews()
        {
            var sourceRows = File.ReadAllLines(Path.Combine(Packet, "source-first-pass.jsonl"))
                .Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
            var comparisons = File.ReadAllLines(Path.Combine(Packet, "draft-comparison.jsonl"))
                .Select(line => JsonNode.Parse(line)!.AsObject()).ToDictionary(row => row["family_id"]!.GetValue<string>(), StringComparer.Ordinal);
            var labelRows = File.ReadAllLines(Labels).Select(line => JsonNode.Parse(line)!.AsObject())
                .ToDictionary(row => row["family_id"]!.GetValue<string>(), StringComparer.Ordinal);
            var firstPassRows = new List<string>();
            var decisionRows = new List<string>();
            foreach (var source in sourceRows)
            {
                var family = source["family_id"]!.GetValue<string>();
                var firstPass = source.DeepClone().AsObject();
                var proposal = firstPass["independent_first_pass"]!.AsObject();
                proposal["reviewer_id"] = "human-reviewer-test";
                proposal["reviewed_at_utc"] = "2026-10-05T12:00:00Z";
                proposal["rationale"] = "Synthetic validator fixture only.";
                var proposalFields = proposal["proposed_fields"]!.AsObject();
                foreach (var key in proposalFields.Select(pair => pair.Key).ToArray()) proposalFields[key] = "fixture-value";
                firstPassRows.Add(firstPass.ToJsonString());

                var comparison = comparisons[family];
                var label = labelRows[family];
                var fieldDecisions = new JsonObject();
                foreach (var field in label["fields"]!.AsObject())
                {
                    if (field.Key == "reference_output_reuse") continue;
                    fieldDecisions[field.Key] = new JsonObject
                    {
                        ["outcome"] = "accept",
                        ["final_value"] = comparison["fields"]![field.Key]!["value"]!.DeepClone(),
                        ["rationale"] = "Synthetic validator fixture only."
                    };
                }
                var decision = new JsonObject
                {
                    ["schema_version"] = "1.0", ["review_item_id"] = source["review_item_id"]!.GetValue<string>(),
                    ["family_id"] = family, ["reviewer_id"] = "human-reviewer-test", ["reviewed_at_utc"] = "2026-10-05T12:00:00Z",
                    ["overall_decision"] = "accept", ["backbone_decision"] = new JsonObject
                    {
                        ["outcome"] = "confirm", ["final_backbone_id"] = comparison["provisional_backbone_id"]!.GetValue<string>(),
                        ["rationale"] = "Synthetic validator fixture only."
                    },
                    ["reference_reuse_decision"] = new JsonObject { ["outcome"] = "acceptable", ["rationale"] = "Synthetic validator fixture only." },
                    ["field_decisions"] = fieldDecisions,
                    ["reviewer_is_independent_of_draft_author"] = true,
                    ["rationale"] = "Synthetic validator fixture only."
                };
                decisionRows.Add(decision.ToJsonString());
            }
            File.WriteAllLines(FirstPass, firstPassRows);
            File.WriteAllLines(Decisions, decisionRows);
        }

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
