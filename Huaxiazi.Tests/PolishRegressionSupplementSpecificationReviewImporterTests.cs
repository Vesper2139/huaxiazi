using System.IO;
using System.Text.Json;
using Huaxiazi.DatasetBuilder;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class PolishRegressionSupplementSpecificationReviewImporterTests
{
    [Fact]
    public void Import_EmitsReviewedGenerationInputOnlyAfterCompleteApproval()
    {
        using var fixture = new PolishRegressionSupplementSpecificationReviewFixture();
        fixture.BuildPacket();
        fixture.WriteReview("approve", "事实和澄清目标明确，输入不允许推测缺失日期。", "");

        var report = PolishRegressionSupplementSpecificationReviewImporter.Import(fixture.SpecsDirectory, fixture.CoverageDirectory,
            fixture.ParentCasesPath, fixture.ParentManifestPath, fixture.PacketDirectory, fixture.OutputDirectory);

        Assert.True(report.GenerationAuthorized);
        Assert.Equal(1, report.ApprovedCount);
        Assert.Single(File.ReadLines(Path.Combine(fixture.OutputDirectory, "generation-input.jsonl")));
        Assert.Single(File.ReadLines(Path.Combine(fixture.OutputDirectory, "review-decisions.jsonl")));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.OutputDirectory, "manifest.json")));
        Assert.True(manifest.RootElement.GetProperty("generation_authorized").GetBoolean());
        Assert.False(manifest.RootElement.GetProperty("reviewer_identity_verified").GetBoolean());
        Assert.Equal(0, manifest.RootElement.GetProperty("phase_0_gate_contribution").GetInt32());
    }

    [Fact]
    public void Import_PreservesRejectionAndEmitsNoGenerationInput()
    {
        using var fixture = new PolishRegressionSupplementSpecificationReviewFixture();
        fixture.BuildPacket();
        fixture.WriteReview("reject", "该规格的输入场景不符合计划中的澄清任务，应重新定义。", "");

        var report = PolishRegressionSupplementSpecificationReviewImporter.Import(fixture.SpecsDirectory, fixture.CoverageDirectory,
            fixture.ParentCasesPath, fixture.ParentManifestPath, fixture.PacketDirectory, fixture.OutputDirectory);

        Assert.False(report.GenerationAuthorized);
        Assert.Equal(1, report.RejectedCount);
        Assert.Single(File.ReadLines(Path.Combine(fixture.OutputDirectory, "review-decisions.jsonl")));
        Assert.Empty(File.ReadLines(Path.Combine(fixture.OutputDirectory, "generation-input.jsonl")));
    }

    [Fact]
    public void Import_RejectsInvalidReviewBeforeCreatingOutput()
    {
        using var fixture = new PolishRegressionSupplementSpecificationReviewFixture();
        fixture.BuildPacket();
        fixture.WriteReview("approve", "待补充", "");

        Assert.Throws<InvalidDataException>(() => PolishRegressionSupplementSpecificationReviewImporter.Import(fixture.SpecsDirectory,
            fixture.CoverageDirectory, fixture.ParentCasesPath, fixture.ParentManifestPath, fixture.PacketDirectory, fixture.OutputDirectory));
        Assert.False(Directory.Exists(fixture.OutputDirectory));
    }
}
