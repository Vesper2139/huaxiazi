using System.IO;
using System.Text.Json;
using Huaxiazi.DatasetBuilder;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class PolishRegressionBaselineAuditorTests
{
    [Fact]
    public void Audit_ReportsDistinctInputsOutputsAndCrossSplitInputLeakage()
    {
        using var fixture = new Fixture();

        var report = PolishRegressionBaselineAuditor.Audit(fixture.Canonical, fixture.Sft, fixture.Manifest, fixture.Source);

        Assert.Equal(4, report.RowCount);
        Assert.Equal(3, report.DistinctInputCount);
        Assert.Equal(2, report.DistinctReferenceOutputCount);
        Assert.Equal(0, report.Phase0GateContribution);
        Assert.True(report.NotAdmissibleAsBlindEval);
        var trainTest = Assert.Single(report.SplitInputOverlap, item => item.LeftSplit == "train" && item.RightSplit == "test");
        Assert.Equal(1, trainTest.SharedDistinctInputs);
        Assert.Equal(2, trainTest.LeftDistinctInputs);
        Assert.Equal(1, trainTest.RightDistinctInputs);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "polish-regression-audit-" + Guid.NewGuid().ToString("N"));
        public string Canonical => Path.Combine(_directory, "canonical.jsonl");
        public string Sft => Path.Combine(_directory, "sft.jsonl");
        public string Manifest => Path.Combine(_directory, "manifest.json");
        public string Source => Path.Combine(_directory, "source.jsonl");

        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            var rows = new[]
            {
                Row("train", "same input", "output-a", "family-a"),
                Row("train", "train only", "output-a", "family-a"),
                Row("dev", "dev only", "output-b", "family-b"),
                Row("test", "same input", "output-b", "family-c")
            };
            File.WriteAllLines(Canonical, rows.Select(row => JsonSerializer.Serialize(row)));
            File.WriteAllText(Sft, "{}\n");
            File.WriteAllText(Manifest, "{}\n");
            File.WriteAllText(Source, "{}\n");
        }

        private static object Row(string split, string input, string output, string family) => new
        {
            split, input, expected_decision = "polish", clarification_questions = Array.Empty<string>(),
            output = new { kind = "final", content = output },
            provenance = new { source_template_family = family },
            claims = new[] { new { anchor = "claim" } },
            review = new { reviewer_count = 1 }
        };

        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
    }
}
