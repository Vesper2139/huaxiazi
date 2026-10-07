using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Huaxiazi.DatasetBuilder;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class PolishRegressionDatasetBuilderTests
{
    [Fact]
    public void Build_GroupsExactNormalizedFamiliesAndRefusesToOverwriteArtifacts()
    {
        using var fixture = new Fixture();
        var first = PolishRegressionDatasetBuilder.Build(fixture.Canonical, fixture.Output);
        var casesPath = Path.Combine(fixture.Output, "cases.jsonl");
        var before = SHA256.HashData(File.ReadAllBytes(casesPath));

        Assert.Equal(3, first.RowCount);
        Assert.Equal(2, first.FamilyCount);
        Assert.Equal(3, File.ReadAllLines(Path.Combine(fixture.Output, "variants.jsonl")).Length);
        Assert.Throws<IOException>(() => PolishRegressionDatasetBuilder.Build(fixture.Canonical, fixture.Output));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(casesPath)));
    }

    [Fact]
    public void FinalizeAndValidate_EnforceBoundaryCountsHashesAndCreateOnlyFreeze()
    {
        using var fixture = new Fixture();
        PolishRegressionDatasetBuilder.Build(fixture.Canonical, fixture.Output);
        foreach (var name in new[] { "README.md", "schema.md", "case.schema.json" }) File.WriteAllText(Path.Combine(fixture.Output, name), "test artifact");
        Directory.CreateDirectory(Path.Combine(fixture.Output, "baseline"));
        File.WriteAllText(Path.Combine(fixture.Output, "baseline", "audit-2026-10-04-corrected.json"), "{}");
        File.WriteAllText(Path.Combine(fixture.Output, "manifest.json"), "{\"status\":\"initializing\"}");

        PolishRegressionManifestWriter.Finalize(fixture.Canonical, fixture.Output);
        var report = PolishRegressionIntegrityValidator.Validate(fixture.Output);

        Assert.True(report.Valid, string.Join("; ", report.Issues.Select(issue => issue.Message)));
        Assert.Equal(3, report.RowCount);
        Assert.Equal(2, report.FamilyCount);
        Assert.Equal(0, report.Phase0GateContribution);
        Assert.Throws<IOException>(() => PolishRegressionManifestWriter.Finalize(fixture.Canonical, fixture.Output));
        File.AppendAllText(Path.Combine(fixture.Output, "cases.jsonl"), " ");
        var tampered = PolishRegressionIntegrityValidator.Validate(fixture.Output);
        Assert.False(tampered.Valid);
        Assert.Contains(tampered.Issues, issue => issue.Code == "hash-mismatch" && issue.File == "cases.jsonl");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "polish-regression-build-" + Guid.NewGuid().ToString("N"));
        public string Canonical => Path.Combine(_directory, "canonical.jsonl");
        public string Output => Path.Combine(_directory, "result");

        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            var rows = new[]
            {
                Row("hxz-polish-agent-v2-000001", "same input", "train"),
                Row("hxz-polish-agent-v2-000002", " same   input ", "dev"),
                Row("hxz-polish-agent-v2-000003", "other input", "test")
            };
            File.WriteAllLines(Canonical, rows.Select(row => JsonSerializer.Serialize(row)));
        }

        private static object Row(string id, string input, string split) => new
        {
            id, split, input, expected_decision = "polish", risk_level = "low", scenario = "work", channel = "chat",
            agent_behavior = new { intent = "preserve_meaning" },
            context = new { formality = "natural" }, claims = new[] { new { subject = "date", time = "tomorrow" } },
            output = new { kind = "final", content = "rewritten" },
            provenance = new { source_template_family = "legacy-family" }
        };

        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
    }
}
