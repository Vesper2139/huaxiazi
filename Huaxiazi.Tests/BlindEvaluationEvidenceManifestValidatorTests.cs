using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Huaxiazi.DatasetBuilder;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class BlindEvaluationEvidenceManifestValidatorTests
{
    [Fact]
    public void Validate_AcceptsConsistentEvidenceIndexWithoutClaimingEvidenceAuthenticity()
    {
        using var fixture = new EvidenceFixture();

        var report = BlindEvaluationEvidenceManifestValidator.Validate(fixture.ManifestJson, fixture.DatasetPath);

        Assert.True(report.Valid, string.Join("; ", report.Issues.Select(issue => issue.Message)));
        Assert.False(report.ExternalEvidenceVerified);
        Assert.Equal("verified", report.AuthorizationReviewStatus);
        Assert.Equal("verified", report.ReviewerIndependenceReviewStatus);
        Assert.Equal(1, report.RecordCount);
    }

    [Fact]
    public void Validate_RejectsEvidenceIndexBoundToDifferentDatasetBytes()
    {
        using var fixture = new EvidenceFixture();
        var manifest = JsonNode.Parse(fixture.ManifestJson)!;
        manifest["dataset"]!["sha256"] = new string('0', 64);

        var report = BlindEvaluationEvidenceManifestValidator.Validate(manifest.ToJsonString(), fixture.DatasetPath);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "dataset-hash-mismatch");
    }

    [Fact]
    public void Validate_RejectsUnknownRootFields()
    {
        using var fixture = new EvidenceFixture();
        var manifest = JsonNode.Parse(fixture.ManifestJson)!;
        manifest["reviewer_email"] = "reviewer@example.invalid";

        var report = BlindEvaluationEvidenceManifestValidator.Validate(manifest.ToJsonString(), fixture.DatasetPath);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "unknown-field" && issue.Field == "reviewer_email");
    }

    [Fact]
    public void Validate_RejectsUnknownFieldsInNestedSchemaObjects()
    {
        using var fixture = new EvidenceFixture();
        var manifest = JsonNode.Parse(fixture.ManifestJson)!;
        manifest["dataset"]!["private_note"] = "must not be stored here";

        var report = BlindEvaluationEvidenceManifestValidator.Validate(manifest.ToJsonString(), fixture.DatasetPath);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "unknown-field" && issue.Field == "dataset.private_note");
    }

    [Fact]
    public void Validate_RejectsDuplicateJsonProperties()
    {
        using var fixture = new EvidenceFixture();
        var duplicateStatus = fixture.ManifestJson[..^1] + ",\"status\":\"submitted_for_review\"}";

        var report = BlindEvaluationEvidenceManifestValidator.Validate(duplicateStatus, fixture.DatasetPath);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "duplicate-json-property" && issue.Field == "status");
    }

    [Fact]
    public void Validate_RejectsNonArrayExceptionCollection()
    {
        using var fixture = new EvidenceFixture();
        var manifest = JsonNode.Parse(fixture.ManifestJson)!;
        manifest["exceptions"] = "none";

        var report = BlindEvaluationEvidenceManifestValidator.Validate(manifest.ToJsonString(), fixture.DatasetPath);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "field-type" && issue.Field == "exceptions");
    }

    [Fact]
    public void Validate_RejectsFreeFormExceptionContent()
    {
        using var fixture = new EvidenceFixture();
        var manifest = JsonNode.Parse(fixture.ManifestJson)!;
        manifest["exceptions"] = new JsonArray(new JsonObject
        {
            ["description"] = "reviewer@example.invalid"
        });

        var report = BlindEvaluationEvidenceManifestValidator.Validate(manifest.ToJsonString(), fixture.DatasetPath);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "unknown-field" && issue.Field == "exceptions[0].description");
    }

    [Fact]
    public void Validate_AcceptsContentFreeControlledExceptionReference()
    {
        using var fixture = new EvidenceFixture();
        var manifest = JsonNode.Parse(fixture.ManifestJson)!;
        manifest["exceptions"] = new JsonArray(new JsonObject
        {
            ["exception_ref"] = "exception:review-01",
            ["category"] = "privacy",
            ["status"] = "open",
            ["evidence_ref"] = null,
            ["evidence_sha256"] = null,
            ["recorded_at_utc"] = "2026-10-03T03:00:00Z",
            ["owner_role_ref"] = "role:eval-custodian"
        });

        var report = BlindEvaluationEvidenceManifestValidator.Validate(manifest.ToJsonString(), fixture.DatasetPath);

        Assert.True(report.Valid, string.Join("; ", report.Issues.Select(issue => issue.Message)));
    }

    [Fact]
    public void Validate_RejectsRoleReferencesThatDoNotMatchSchemaPattern()
    {
        using var fixture = new EvidenceFixture();
        var manifest = JsonNode.Parse(fixture.ManifestJson)!;
        manifest["authorization_review"]!["reviewer_role_ref"] = "role:a";

        var report = BlindEvaluationEvidenceManifestValidator.Validate(manifest.ToJsonString(), fixture.DatasetPath);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "opaque-reference" && issue.Field == "authorization.reviewer_role_ref");
    }

    [Fact]
    public void Validate_RejectsInvalidSignatureValueType()
    {
        using var fixture = new EvidenceFixture();
        var manifest = JsonNode.Parse(fixture.ManifestJson)!;
        manifest["custody"]!["signature"] = "not-a-signature-object";

        var report = BlindEvaluationEvidenceManifestValidator.Validate(manifest.ToJsonString(), fixture.DatasetPath);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "field-type" && issue.Field == "custody.signature");
    }

    [Fact]
    public void Validate_RequiresAdmissionReportFieldsWhenAdmissionHasNotRun()
    {
        using var fixture = new EvidenceFixture();
        var manifest = JsonNode.Parse(fixture.ManifestJson)!;
        manifest["dataset_admission"] = new JsonObject { ["status"] = "not_run" };

        var report = BlindEvaluationEvidenceManifestValidator.Validate(manifest.ToJsonString(), fixture.DatasetPath);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "required-field" && issue.Field == "dataset_admission.report_artifact_ref");
        Assert.Contains(report.Issues, issue => issue.Code == "required-field" && issue.Field == "dataset_admission.report_sha256");
    }

    [Fact]
    public void Validate_RequiresAllReviewFieldsForNotVerifiedStatus()
    {
        using var fixture = new EvidenceFixture();
        var manifest = JsonNode.Parse(fixture.ManifestJson)!;
        manifest["authorization_review"]!["status"] = "not_verified";
        manifest["authorization_review"]!.AsObject().Remove("evidence");

        var report = BlindEvaluationEvidenceManifestValidator.Validate(manifest.ToJsonString(), fixture.DatasetPath);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "review-evidence" && issue.Field == "authorization.evidence");
    }

    [Fact]
    public void Validate_RejectsMissingIndependentSubmissionForDatasetReviewer()
    {
        using var fixture = new EvidenceFixture();
        var manifest = JsonNode.Parse(fixture.ManifestJson)!;
        manifest["record_evidence"]![0]!["review_submissions"]![1]!["reviewer_ref"] = "reviewer:unknown";

        var report = BlindEvaluationEvidenceManifestValidator.Validate(manifest.ToJsonString(), fixture.DatasetPath);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "reviewer-evidence-mismatch" && issue.RecordId == "record-1");
    }

    [Fact]
    public void Validate_RejectsReusedOneTimeReviewerReferenceAcrossRecords()
    {
        using var fixture = new EvidenceFixture(recordCount: 2, reuseReviewerReferences: true);

        var report = BlindEvaluationEvidenceManifestValidator.Validate(fixture.ManifestJson, fixture.DatasetPath);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "duplicate-reviewer-reference" && issue.RecordId == "record-2");
    }

    [Fact]
    public void Validate_AcceptsDistinctOneTimeReviewerReferencesAcrossRecords()
    {
        using var fixture = new EvidenceFixture(recordCount: 2);

        var report = BlindEvaluationEvidenceManifestValidator.Validate(fixture.ManifestJson, fixture.DatasetPath);

        Assert.True(report.Valid, string.Join("; ", report.Issues.Select(issue => issue.Message)));
        Assert.Equal(2, report.RecordCount);
    }

    [Fact]
    public void Validate_RejectsReusedOneTimeAdjudicatorReferenceAcrossRecords()
    {
        using var fixture = new EvidenceFixture(adjudicated: true, recordCount: 2, reuseReviewerReferences: true);

        var report = BlindEvaluationEvidenceManifestValidator.Validate(fixture.ManifestJson, fixture.DatasetPath);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "duplicate-reviewer-reference" && issue.RecordId == "record-2");
    }

    [Fact]
    public void Validate_RejectsFilesystemPathsAsControlledArtifactReferences()
    {
        using var fixture = new EvidenceFixture();
        var manifest = JsonNode.Parse(fixture.ManifestJson)!;
        manifest["dataset"]!["artifact_ref"] = "C:\\private\\dataset.jsonl";

        var report = BlindEvaluationEvidenceManifestValidator.Validate(manifest.ToJsonString(), fixture.DatasetPath);

        Assert.Contains(report.Issues, issue => issue.Code == "artifact-reference" && issue.Field == "dataset.artifact_ref");
        Assert.False(report.ExternalEvidenceVerified);
    }

    [Fact]
    public void Validate_RejectsAdjudicationSubmissionBoundToDifferentBlindPackage()
    {
        using var fixture = new EvidenceFixture(adjudicated: true);
        var manifest = JsonNode.Parse(fixture.ManifestJson)!;
        manifest["record_evidence"]![0]!["adjudication"]!["blind_package_sha256"] = new string('b', 64);

        var report = BlindEvaluationEvidenceManifestValidator.Validate(manifest.ToJsonString(), fixture.DatasetPath);

        Assert.Contains(report.Issues, issue => issue.Code == "blind-package-mismatch" && issue.RecordId == "record-1");
    }

    [Fact]
    public void Validate_RejectsAdjudicationTimestampEarlierThanIndependentReviews()
    {
        using var fixture = new EvidenceFixture(adjudicated: true);
        var manifest = JsonNode.Parse(fixture.ManifestJson)!;
        manifest["record_evidence"]![0]!["adjudication"]!["submitted_at_utc"] = "2026-09-30T23:59:59Z";

        var report = BlindEvaluationEvidenceManifestValidator.Validate(manifest.ToJsonString(), fixture.DatasetPath);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "adjudication-before-reviews" && issue.RecordId == "record-1");
    }

    [Fact]
    public void Validate_RejectsReviewedManifestWithoutSealedCustodyRecord()
    {
        using var fixture = new EvidenceFixture();
        var manifest = JsonNode.Parse(fixture.ManifestJson)!;
        manifest["status"] = "reviewed";

        var report = BlindEvaluationEvidenceManifestValidator.Validate(manifest.ToJsonString(), fixture.DatasetPath);

        Assert.Contains(report.Issues, issue => issue.Code == "reviewed-state-incomplete" && issue.Field == "custody");
    }

    [Theory]
    [InlineData("open")]
    [InlineData("rejected")]
    public void Validate_RejectsReviewedManifestWithBlockingException(string exceptionStatus)
    {
        using var fixture = new EvidenceFixture();
        var manifest = JsonNode.Parse(fixture.ManifestJson)!;
        manifest["status"] = "reviewed";
        manifest["dataset_admission"] = new JsonObject
        {
            ["status"] = "passed",
            ["report_artifact_ref"] = "vault:admission-report-1",
            ["report_sha256"] = new string('3', 64)
        };
        manifest["custody"] = new JsonObject
        {
            ["evidence_store_ref"] = "vault:eval-store-1",
            ["sealed_at_utc"] = "2026-10-03T04:00:00Z",
            ["sealed_by_role_ref"] = "role:eval-custodian-1",
            ["signature"] = new JsonObject { ["scheme"] = "externally-managed" }
        };
        manifest["exceptions"] = new JsonArray(new JsonObject
        {
            ["exception_ref"] = "exception:privacy-01",
            ["category"] = "privacy",
            ["status"] = exceptionStatus,
            ["evidence_ref"] = null,
            ["evidence_sha256"] = null,
            ["recorded_at_utc"] = "2026-10-03T03:00:00Z",
            ["owner_role_ref"] = "role:eval-custodian-1"
        });

        var report = BlindEvaluationEvidenceManifestValidator.Validate(manifest.ToJsonString(), fixture.DatasetPath);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "reviewed-with-blocking-exception");
    }

    [Fact]
    public void Validate_RejectsNonRfc3339ReviewSubmissionTimestamp()
    {
        using var fixture = new EvidenceFixture();
        var manifest = JsonNode.Parse(fixture.ManifestJson)!;
        manifest["record_evidence"]![0]!["review_submissions"]![0]!["submitted_at_utc"] = "10/01/2026 01:00:00 +00:00";

        var report = BlindEvaluationEvidenceManifestValidator.Validate(manifest.ToJsonString(), fixture.DatasetPath);

        Assert.Contains(report.Issues, issue => issue.Code == "utc-timestamp" &&
            issue.Field == "record_evidence.record-1.review_submissions.submitted_at_utc");
    }

    private sealed class EvidenceFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "evidence-manifest-" + Guid.NewGuid().ToString("N"));
        public string DatasetPath { get; }
        public string ManifestJson { get; }

        public EvidenceFixture(bool adjudicated = false, int recordCount = 1, bool reuseReviewerReferences = false)
        {
            Directory.CreateDirectory(_root);
            DatasetPath = Path.Combine(_root, "blind-eval.jsonl");
            var labels = new BlindEvaluationGoldLabelSet("polish", "colloquial", [], "low", "produce", "hello", new([], "neutral", [], false, []));
            var records = Enumerable.Range(1, recordCount).Select(index =>
            {
                var suffix = index == 1 || reuseReviewerReferences ? string.Empty : "-" + index;
                var reviewerA = "reviewer:a" + suffix;
                var reviewerB = "reviewer:b" + suffix;
                var adjudicator = "reviewer:c" + suffix;
                return new BlindEvaluationRecord
                {
                    Id = "record-" + index,
                    Task = "polish",
                    SemanticFamilyId = "family-" + index,
                    Source = new("project_owned", "source:project-1", "reviewed"),
                    Input = "hello",
                    InputStyle = "colloquial",
                    RiskLevel = "low",
                    Split = "development",
                    ExpectedDecision = "produce",
                    Annotations = labels.Annotations,
                    HumanReview = new([reviewerA, reviewerB], adjudicated ? "adjudicated" : "accepted",
                        adjudicated ? adjudicator : null,
                        Adjudication: adjudicated ? new BlindEvaluationAnnotationVote(adjudicator, labels) : null)
                };
            }).ToArray();
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
            var datasetBytes = Encoding.UTF8.GetBytes(string.Join("\n", records.Select(record => JsonSerializer.Serialize(record, jsonOptions))) + "\n");
            File.WriteAllBytes(DatasetPath, datasetBytes);
            var hash = Convert.ToHexString(SHA256.HashData(datasetBytes)).ToLowerInvariant();
            var manifest = new
            {
                manifest_version = "huaxiazi-blind-evidence-manifest-v1",
                status = "submitted_for_review",
                dataset = new
                {
                    artifact_ref = "vault:dataset-1",
                    sha256 = hash,
                    sample_count = records.Length,
                    schema_version = "3.1",
                    admission_protocol_id = BlindEvaluationAdmissionProtocol.ProtocolId,
                    admission_protocol_version = BlindEvaluationAdmissionProtocol.Version
                },
                handbook = new { artifact_ref = "vault:handbook-1", version = "1.0", sha256 = new string('b', 64) },
                authorization_review = new
                {
                    status = "verified",
                    verified_at_utc = "2026-10-03T01:00:00Z",
                    reviewer_role_ref = "role:data-reviewer-1",
                    evidence = new[] { new { artifact_ref = "vault:rights-1", sha256 = new string('c', 64), purpose = "rights" } }
                },
                reviewer_independence_review = new
                {
                    status = "verified",
                    verified_at_utc = "2026-10-03T02:00:00Z",
                    reviewer_role_ref = "role:eval-custodian-1",
                    evidence = new[] { new { artifact_ref = "vault:identity-review-1", sha256 = new string('d', 64), purpose = "independence" } }
                },
                record_evidence = Enumerable.Range(1, recordCount).Select(index =>
                    {
                        var suffix = index == 1 || reuseReviewerReferences ? string.Empty : "-" + index;
                        var reviewerA = "reviewer:a" + suffix;
                        var reviewerB = "reviewer:b" + suffix;
                        var adjudicator = "reviewer:c" + suffix;
                        var packageHash = new string((char)('a' + index - 1), 64);
                        return new
                    {
                        record_id = "record-" + index,
                        source_ref = "source:project-1",
                        authorization_artifact_ref = "vault:record-rights-" + index,
                        authorization_sha256 = new string('e', 64),
                        blind_package_artifact_ref = "vault:blind-package-" + index,
                        blind_package_version = "blind-package-v1",
                        blind_package_sha256 = packageHash,
                        review_submissions = new[]
                        {
                            new { reviewer_ref = reviewerA, artifact_ref = "vault:submission-a" + suffix, sha256 = new string('f', 64), submitted_at_utc = "2026-10-01T01:00:00Z", blind_package_sha256 = packageHash },
                            new { reviewer_ref = reviewerB, artifact_ref = "vault:submission-b" + suffix, sha256 = new string('1', 64), submitted_at_utc = "2026-10-01T02:00:00Z", blind_package_sha256 = packageHash }
                        },
                        adjudication = adjudicated
                            ? new { adjudicator_ref = adjudicator, artifact_ref = "vault:adjudication-" + index, sha256 = new string('2', 64), submitted_at_utc = "2026-10-01T03:00:00Z", blind_package_sha256 = packageHash }
                            : null
                        };
                    }).ToArray(),
                dataset_admission = new { status = "not_run", report_artifact_ref = (string?)null, report_sha256 = (string?)null },
                exceptions = Array.Empty<object>(),
                custody = new { evidence_store_ref = "vault:eval-store-1", sealed_at_utc = (string?)null, sealed_by_role_ref = (string?)null, signature = (object?)null }
            };
            ManifestJson = JsonSerializer.Serialize(manifest, jsonOptions);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
