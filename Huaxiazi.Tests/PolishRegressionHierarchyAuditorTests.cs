using System.IO;
using System.Text.Json;
using Huaxiazi.DatasetBuilder;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class PolishRegressionHierarchyAuditorTests
{
    [Fact]
    public void Audit_MapsTaskFamiliesToProvisionalBackbonesWithoutTreatingThemAsReviewed()
    {
        using var fixture = new Fixture();

        var report = PolishRegressionHierarchyAuditor.Audit(fixture.Cases);

        Assert.Equal(3, report.TaskFamilyCount);
        Assert.Equal(2, report.BackboneCount);
        Assert.Equal(3, report.MatchedLegacyIntentSuffixFamilyCount);
        Assert.Equal("separate_tracks", report.RecommendedTrackMode);
        Assert.Equal("pending_human_review_of_reference_reuse", report.IntentSmokeTrackStatus);
        Assert.Equal("insufficient_independent_backbones_or_split_leakage", report.UnseenBackboneTrackStatus);
        Assert.All(report.Backbones, backbone => Assert.Equal("provisional_requires_human_review", backbone.Status));
        Assert.Contains(report.SplitBackboneOverlap, item => item.SharedBackboneCount == 1 &&
            new[] { item.LeftSplit, item.RightSplit }.ToHashSet(StringComparer.Ordinal).SetEquals(["development", "regression"]));
        var progress = Assert.Single(report.IntentBackboneCoverage, item => item.Intent == "进度汇报");
        Assert.Equal(2, progress.IndependentBackboneCount);
        Assert.Equal(3, progress.ShortfallToSuggestedMinimum);
        var multiIntentBackbone = Assert.Single(report.Backbones, backbone => backbone.TaskFamilyCount == 2);
        Assert.Equal(1, multiIntentBackbone.DistinctReferenceOutputCountAcrossIntents);
        Assert.Equal("potential_reference_reuse_requires_review", multiIntentBackbone.ReferenceOutputAlignmentStatus);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "polish-regression-hierarchy-" + Guid.NewGuid().ToString("N"));
        public string Cases => Path.Combine(_directory, "cases.jsonl");

        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            var rows = new[]
            {
                Row("family-a", "development", "项目下周交付。", "进度汇报", "same-output"),
                Row("family-b", "regression", "项目下周交付。", "婉拒", "same-output"),
                Row("family-c", "development", "已收到材料。", "进度汇报", "another-output")
            };
            File.WriteAllLines(Cases, rows.Select(row => JsonSerializer.Serialize(row)));
        }

        private static object Row(string family, string split, string core, string purpose, string output) => new
        {
            id = "hxz-polish-reg-" + family, family_id = "hxz-polish-reg-family-" + family,
            split, input = core + " " + purpose + "，语气自然一点，别写得太客套。",
            expected_decision = "polish", context = new { purpose, recipient = "manager" },
            claims = new[] { new { fact = "date" } }, reference_output = output
        };

        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
    }
}
