using System.IO;
using System.Text.Json;
using Huaxiazi.DatasetBuilder;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class PolishRegressionBackboneAuditorTests
{
    [Fact]
    public void Audit_StripsLegacyIntentSuffixAndFindsBackboneOverlapAcrossSplits()
    {
        using var fixture = new Fixture();

        var report = PolishRegressionBackboneAuditor.Audit(fixture.Canonical);

        Assert.Equal(3, report.RowCount);
        Assert.Equal(3, report.MatchedLegacyIntentSuffixRowCount);
        Assert.Equal(2, report.DistinctBackboneCount);
        var trainTest = Assert.Single(report.SplitBackboneOverlap, item =>
            new[] { item.LeftSplit, item.RightSplit }.ToHashSet(StringComparer.Ordinal).SetEquals(["train", "test"]));
        Assert.Equal(1, trainTest.SharedBackboneCount);
        var firstBackbone = Assert.Single(report.Backbones, item => item.IntentCount == 2);
        Assert.Equal(1, firstBackbone.DistinctReferenceOutputCount);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "polish-regression-backbone-" + Guid.NewGuid().ToString("N"));
        public string Canonical => Path.Combine(_directory, "canonical.jsonl");

        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            var rows = new[]
            {
                Row("one-a", "项目版本预计9月20日交付。", "进度汇报", "train", "same-output"),
                Row("one-b", "项目版本预计9月20日交付。", "婉拒", "test", "same-output"),
                Row("two-a", "这件事已经处理。", "进度汇报", "dev", "another-output")
            };
            File.WriteAllLines(Canonical, rows.Select(row => JsonSerializer.Serialize(row)));
        }

        private static object Row(string id, string core, string purpose, string split, string output) => new
        {
            id, input = core + " " + purpose + "，语气自然一点，别写得太客套。", split,
            expected_decision = "polish", context = new { purpose, recipient = "manager" },
            claims = new[] { new { subject = "delivery", time = "2026-09-20" } },
            output = new { kind = "final", content = output }
        };

        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
    }
}
