using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Huaxiazi.DatasetBuilder;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class LocalModelCandidateReviewImporterTests
{
    [Fact]
    public void Validate_AcceptsCompleteTraceableHumanReview()
    {
        using var fixture = new Fixture();

        var report = LocalModelCandidateReviewImporter.Validate(fixture.PacketPath, fixture.CsvPath, fixture.MetadataPath);

        Assert.True(report.Valid, string.Join("; ", report.Issues.Select(issue => issue.Code + ":" + issue.Message)));
        Assert.Equal(2, report.TotalCount);
        Assert.Equal(2, report.ReviewedCount);
        Assert.Equal(0, report.Phase0GateContribution);
        Assert.Equal(1, report.FactIssueCount);
        Assert.Equal(1, report.DirectUsableCount);
    }

    [Fact]
    public void Validate_RejectsCandidateTextDriftAndReferenceLeakage()
    {
        using var fixture = new Fixture();
        fixture.WriteCsv(fixture.Reviews[0] with { CandidateOutput = "tampered" }, fixture.Reviews[1]);

        var driftReport = LocalModelCandidateReviewImporter.Validate(fixture.PacketPath, fixture.CsvPath, fixture.MetadataPath);
        Assert.Contains(driftReport.Issues, issue => issue.Code == "source-row-mismatch");

        fixture.WriteReviewPacketWithReferenceLeak();
        var leakageReport = LocalModelCandidateReviewImporter.Validate(fixture.PacketPath, fixture.CsvPath, fixture.MetadataPath);
        Assert.Contains(leakageReport.Issues, issue => issue.Code == "packet-boundary");
    }

    [Fact]
    public void Validate_RejectsIncompleteInvalidAndStaleReviewData()
    {
        using var fixture = new Fixture();
        var invalid = fixture.Reviews[0] with { ToneFit = "6", ReviewerId = "AI", ReviewedAt = "2026-10-06T08:00:00+08:00", Rationale = "暂无" };
        fixture.WriteCsv(invalid);

        var report = LocalModelCandidateReviewImporter.Validate(fixture.PacketPath, fixture.CsvPath, fixture.MetadataPath);

        Assert.Contains(report.Issues, issue => issue.Code == "tone-fit-score");
        Assert.Contains(report.Issues, issue => issue.Code == "reviewer-id");
        Assert.Contains(report.Issues, issue => issue.Code == "review-time");
        Assert.Contains(report.Issues, issue => issue.Code == "review-rationale");
        Assert.Contains(report.Issues, issue => issue.Code == "missing-review");

        File.AppendAllText(fixture.PacketPath, " ");
        var staleReport = LocalModelCandidateReviewImporter.Validate(fixture.PacketPath, fixture.CsvPath, fixture.MetadataPath);
        Assert.Contains(staleReport.Issues, issue => issue.Code == "packet-hash");
    }

    [Fact]
    public void Import_WritesImmutableNonPromotingReviewRecordsOutsidePacket()
    {
        using var fixture = new Fixture();
        var outputPath = Path.Combine(fixture.Root, "imported", "human-reviews.jsonl");

        var result = LocalModelCandidateReviewImporter.Import(fixture.PacketPath, fixture.CsvPath, fixture.MetadataPath, outputPath);

        Assert.Equal(2, result.ImportedCount);
        Assert.Equal(0, result.Phase0GateContribution);
        Assert.True(File.Exists(outputPath));
        Assert.Throws<IOException>(() => LocalModelCandidateReviewImporter.Import(
            fixture.PacketPath, fixture.CsvPath, fixture.MetadataPath, outputPath));
        Assert.Throws<InvalidDataException>(() => LocalModelCandidateReviewImporter.Import(
            fixture.PacketPath, fixture.CsvPath, fixture.MetadataPath, Path.Combine(fixture.Root, "packet", "decisions.jsonl")));
        var imported = File.ReadLines(outputPath).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            Assert.Equal(2, imported.Length);
            Assert.All(imported, document => Assert.Equal(0, document.RootElement.GetProperty("phase_0_gate_contribution").GetInt32()));
            Assert.All(imported, document => Assert.False(document.RootElement.TryGetProperty("reference_output", out _)));
            Assert.Equal("reviewer-a", imported[0].RootElement.GetProperty("reviewer").GetProperty("reviewer_id").GetString());
        }
        finally
        {
            foreach (var document in imported) document.Dispose();
        }
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly string[] Headers =
        [
            "case_id", "family_id", "input", "recipient", "purpose", "formality", "explicit_requirements",
            "candidate_kind", "candidate_output", "fact_preservation", "requirement_preservation", "tone_fit_1_to_5",
            "directly_usable", "overall_1_to_5", "reviewer_id", "reviewed_at_utc", "rationale"
        ];

        public string Root { get; } = Path.Combine(AppContext.BaseDirectory, "test-data", "LocalModelCandidateReview_" + Guid.NewGuid().ToString("N"));
        public string PacketPath => Path.Combine(Root, "packet", "candidate-review.jsonl");
        public string CsvPath => Path.Combine(Root, "packet", "candidate-review.csv");
        public string MetadataPath => Path.Combine(Root, "packet", "review-metadata.json");
        public ReviewRow[] Reviews { get; } =
        [
            new("review-1", "family-0000000000000001", "Original, with a comma", "manager", "request", "formal", "retain date",
                "final", "Candidate\nwith a line break", "issue", "pass", "3", "yes", "3", "reviewer-a", "2026-10-06T00:00:00Z", "日期表达需要核对。"),
            new("review-2", "family-0000000000000002", "请澄清用途", "", "", "", "", "needs_clarification", "需要补充具体用途吗？",
                "not_applicable", "not_applicable", "not_applicable", "not_applicable", "4", "reviewer-a", "2026-10-06T00:00:00Z", "澄清方向明确。")
        ];

        public Fixture()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PacketPath)!);
            WriteReviewPacket();
            WriteCsv(Reviews);
            WriteMetadata();
        }

        public void WriteCsv(params ReviewRow[] rows)
        {
            var lines = new List<string> { string.Join(",", Headers.Select(Csv)) };
            lines.AddRange(rows.Select(row => string.Join(",", row.Values.Select(Csv))));
            File.WriteAllText(CsvPath, string.Join(Environment.NewLine, lines) + Environment.NewLine, new UTF8Encoding(false));
        }

        public void WriteReviewPacketWithReferenceLeak()
        {
            var records = Reviews.Select(row => new
            {
                case_id = row.CaseId,
                family_id = row.FamilyId,
                input = row.Input,
                context = new { recipient = row.Recipient, purpose = row.Purpose, formality = row.Formality, explicit_requirements = row.ExplicitRequirements },
                candidate_kind = row.CandidateKind,
                candidate_output = row.CandidateOutput,
                reference_output = "must never be in the candidate review packet"
            });
            File.WriteAllText(PacketPath, string.Join('\n', records.Select(record => JsonSerializer.Serialize(record))) + "\n");
            WriteMetadata();
        }

        private void WriteReviewPacket()
        {
            var records = Reviews.Select(row => new
            {
                case_id = row.CaseId,
                family_id = row.FamilyId,
                input = row.Input,
                context = new { recipient = row.Recipient, purpose = row.Purpose, formality = row.Formality, explicit_requirements = row.ExplicitRequirements },
                candidate_kind = row.CandidateKind,
                candidate_output = row.CandidateOutput,
                reviewer = new { fact_preservation = (string?)null }
            });
            File.WriteAllText(PacketPath, string.Join('\n', records.Select(record => JsonSerializer.Serialize(record))) + "\n");
        }

        private void WriteMetadata()
        {
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(PacketPath))).ToLowerInvariant();
            File.WriteAllText(MetadataPath, JsonSerializer.Serialize(new
            {
                schema_version = "local-model-candidate-review-v1",
                candidate_packet_sha256 = hash,
                cases = Reviews.Length,
                phase_0_gate_contribution = 0,
                reference_outputs_included = false,
                expected_decisions_included = false
            }));
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }

        private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private sealed record ReviewRow(string CaseId, string FamilyId, string Input, string Recipient, string Purpose,
        string Formality, string ExplicitRequirements, string CandidateKind, string CandidateOutput,
        string FactPreservation, string RequirementPreservation, string ToneFit, string DirectUsable, string Overall,
        string ReviewerId, string ReviewedAt, string Rationale)
    {
        public IEnumerable<string> Values =>
        [
            CaseId, FamilyId, Input, Recipient, Purpose, Formality, ExplicitRequirements, CandidateKind, CandidateOutput,
            FactPreservation, RequirementPreservation, ToneFit, DirectUsable, Overall, ReviewerId, ReviewedAt, Rationale
        ];
    }
}
