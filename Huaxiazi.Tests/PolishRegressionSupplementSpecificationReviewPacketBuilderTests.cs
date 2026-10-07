using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Huaxiazi.DatasetBuilder;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class PolishRegressionSupplementSpecificationReviewPacketBuilderTests
{
    [Fact]
    public void Build_BindsReviewCsvToSpecificationAndLeavesHumanDecisionsBlank()
    {
        using var fixture = new PolishRegressionSupplementSpecificationReviewFixture();

        var report = PolishRegressionSupplementSpecificationReviewPacketBuilder.Build(fixture.SpecsDirectory, fixture.CoverageDirectory,
            fixture.ParentCasesPath, fixture.ParentManifestPath, fixture.PacketDirectory);

        Assert.Equal(1, report.SpecificationCount);
        Assert.Equal(0, report.Phase0GateContribution);
        Assert.False(report.GenerationAuthorized);
        var templatePath = Path.Combine(fixture.PacketDirectory, "specification-review.template.csv");
        var csv = File.ReadAllText(templatePath);
        Assert.True(File.Exists(Path.Combine(fixture.PacketDirectory, "specification-review.csv")));
        Assert.Contains("spec-1", csv);
        Assert.Contains("clarification_positive_examples", csv);
        Assert.Contains("human_decision", csv);
        Assert.Contains("reviewer_id", csv);
        Assert.Contains("rationale", csv);
        Assert.EndsWith("\"\",\"\",\"\",\"\",\"\"" + Environment.NewLine, csv);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.PacketDirectory, "manifest.json")));
        Assert.Equal(Hash(templatePath), manifest.RootElement.GetProperty("files").GetProperty("specification-review.template.csv").GetString());
    }

    [Fact]
    public void Build_RejectsExistingOutputDirectory()
    {
        using var fixture = new PolishRegressionSupplementSpecificationReviewFixture();
        Directory.CreateDirectory(fixture.PacketDirectory);
        File.WriteAllText(Path.Combine(fixture.PacketDirectory, "keep.txt"), "untouched");

        Assert.Throws<IOException>(() => PolishRegressionSupplementSpecificationReviewPacketBuilder.Build(fixture.SpecsDirectory,
            fixture.CoverageDirectory, fixture.ParentCasesPath, fixture.ParentManifestPath, fixture.PacketDirectory));
        Assert.Equal("untouched", File.ReadAllText(Path.Combine(fixture.PacketDirectory, "keep.txt")));
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
