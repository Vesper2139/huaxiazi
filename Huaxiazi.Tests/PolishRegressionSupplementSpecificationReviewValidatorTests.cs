using System.IO;
using Huaxiazi.DatasetBuilder;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class PolishRegressionSupplementSpecificationReviewValidatorTests
{
    [Fact]
    public void Validate_RecordsCompleteHumanApprovalAndAllowsInternalGeneration()
    {
        using var fixture = new PolishRegressionSupplementSpecificationReviewFixture();
        fixture.BuildPacket();
        fixture.WriteReview("approve", "该规格事实明确，追问要求与禁止猜测一致。", "");

        var report = PolishRegressionSupplementSpecificationReviewValidator.Validate(fixture.SpecsDirectory, fixture.CoverageDirectory,
            fixture.ParentCasesPath, fixture.ParentManifestPath, fixture.PacketDirectory);

        Assert.True(report.Valid, string.Join(";", report.Issues.Select(issue => issue.Code)));
        Assert.True(report.HumanApprovalRecorded);
        Assert.True(report.GenerationAuthorized);
        Assert.False(report.ReviewerIdentityVerified);
        Assert.Equal(1, report.ApprovedCount);
        Assert.Equal(0, report.Phase0GateContribution);
    }

    [Fact]
    public void Validate_RejectsPlaceholderRationaleAndDoesNotAuthorizeGeneration()
    {
        using var fixture = new PolishRegressionSupplementSpecificationReviewFixture();
        fixture.BuildPacket();
        fixture.WriteReview("approve", "无非空理由", "");

        var report = PolishRegressionSupplementSpecificationReviewValidator.Validate(fixture.SpecsDirectory, fixture.CoverageDirectory,
            fixture.ParentCasesPath, fixture.ParentManifestPath, fixture.PacketDirectory);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "review-rationale");
        Assert.False(report.GenerationAuthorized);
    }

    [Fact]
    public void Validate_RejectsSourceTamperingAndBrokenCoverageChain()
    {
        using var fixture = new PolishRegressionSupplementSpecificationReviewFixture();
        fixture.BuildPacket();
        fixture.WriteReview("approve", "规格符合目标行为和边界。", "");
        File.WriteAllText(fixture.SpecsPath, File.ReadAllText(fixture.SpecsPath).Replace("Missing date", "Changed date", StringComparison.Ordinal));

        var report = PolishRegressionSupplementSpecificationReviewValidator.Validate(fixture.SpecsDirectory, fixture.CoverageDirectory,
            fixture.ParentCasesPath, fixture.ParentManifestPath, fixture.PacketDirectory);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code.StartsWith("source-", StringComparison.Ordinal));
        Assert.False(report.GenerationAuthorized);
    }

    [Fact]
    public void Validate_RejectsCoverageReportTamperingEvenWhenReviewCsvIsComplete()
    {
        using var fixture = new PolishRegressionSupplementSpecificationReviewFixture();
        fixture.BuildPacket();
        fixture.WriteReview("approve", "规格符合目标行为和边界。", "");
        File.AppendAllText(Path.Combine(fixture.CoverageDirectory, "coverage-report.json"), " ");

        var report = PolishRegressionSupplementSpecificationReviewValidator.Validate(fixture.SpecsDirectory, fixture.CoverageDirectory,
            fixture.ParentCasesPath, fixture.ParentManifestPath, fixture.PacketDirectory);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "source-coverage-binding");
        Assert.False(report.GenerationAuthorized);
    }
}
