using Huaxiazi.DatasetBuilder;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Huaxiazi.Tests;

[CollectionDefinition("DatasetBuilder CLI", DisableParallelization = true)]
public sealed class DatasetBuilderCliCollection;

[Collection("DatasetBuilder CLI")]
public sealed class BlindEvaluationSourceRegisterValidatorTests
{
    [Fact]
    public void Validate_ReportsUnverifiedButConsistentSourceWithoutPassingPhaseZero()
    {
        var report = BlindEvaluationSourceRegisterValidator.Validate(UnverifiedRegister, DatasetRecord("project_owned", "source:project-1"));

        Assert.True(report.RecordIntegrityValid, string.Join("; ", report.Issues.Select(issue => issue.Message)));
        Assert.True(report.SourcesCovered);
        Assert.True(report.RequiredUsesClaimed);
        Assert.False(report.RightsReviewClaimComplete);
        Assert.False(report.ExternalEvidenceAuthenticityVerifiedByTool);
        Assert.False(report.Phase0GatePassed);
    }

    [Fact]
    public void Validate_RejectsDatasetReferenceMissingFromSourceRegister()
    {
        var report = BlindEvaluationSourceRegisterValidator.Validate(UnverifiedRegister, DatasetRecord("project_owned", "source:missing"));

        Assert.False(report.RecordIntegrityValid);
        Assert.Contains(report.Issues, issue => issue.Code == "source-not-registered" && issue.RecordId == "record-1");
    }

    [Fact]
    public void Validate_MarksSourceCoverageIncompleteWhenDatasetHasNoSourceReference()
    {
        var report = BlindEvaluationSourceRegisterValidator.Validate(UnverifiedRegister, DatasetRecord("project_owned", ""));

        Assert.False(report.RecordIntegrityValid);
        Assert.False(report.SourcesCovered);
    }

    [Fact]
    public void Validate_RejectsDatasetSourceKindThatDisagreesWithRegistry()
    {
        var report = BlindEvaluationSourceRegisterValidator.Validate(UnverifiedRegister, DatasetRecord("licensed", "source:project-1"));

        Assert.False(report.RecordIntegrityValid);
        Assert.Contains(report.Issues, issue => issue.Code == "source-kind-mismatch" && issue.RecordId == "record-1");
    }

    [Fact]
    public void Validate_RejectsRightsBasisThatDoesNotMatchRegisteredSourceKind()
    {
        var register = UnverifiedRegister.Replace("\"rights_basis\": \"project_owned\"", "\"rights_basis\": \"license\"", StringComparison.Ordinal);

        var report = BlindEvaluationSourceRegisterValidator.Validate(register, DatasetRecord("project_owned", "source:project-1"));

        Assert.False(report.RecordIntegrityValid);
        Assert.Contains(report.Issues, issue => issue.Code == "rights-basis-mismatch");
    }

    [Fact]
    public void Validate_DoesNotCallSourceEligibleWhenRequiredPurposesAreMissing()
    {
        var register = UnverifiedRegister.Replace("\"candidate_inference\", \"human_review\", \"aggregate_reporting\"", "\"candidate_inference\", \"aggregate_reporting\"", StringComparison.Ordinal);

        var report = BlindEvaluationSourceRegisterValidator.Validate(register, DatasetRecord("project_owned", "source:project-1"));

        Assert.True(report.RecordIntegrityValid);
        Assert.False(report.RequiredUsesClaimed);
        Assert.False(report.Phase0GatePassed);
    }

    [Fact]
    public void Validate_ReportsExpiredSourceAsIneligibleWithoutConfusingItWithRecordIntegrity()
    {
        var register = UnverifiedRegister.Replace("\"valid_until\": null", "\"valid_until\": \"2025-12-31\"", StringComparison.Ordinal);

        var report = BlindEvaluationSourceRegisterValidator.Validate(register, DatasetRecord("project_owned", "source:project-1"));

        Assert.True(report.RecordIntegrityValid);
        Assert.False(report.RequiredUsesClaimed);
    }

    [Fact]
    public void Validate_RejectsPublicUrlAsControlledVaultReference()
    {
        var register = UnverifiedRegister.Replace("vault:auth-1", "https://example.invalid/auth.json", StringComparison.Ordinal);

        var report = BlindEvaluationSourceRegisterValidator.Validate(register, DatasetRecord("project_owned", "source:project-1"));

        Assert.False(report.RecordIntegrityValid);
        Assert.Contains(report.Issues, issue => issue.Code == "opaque-reference" && issue.Field == "sources[0].evidence.vault_ref");
    }

    [Fact]
    public void Validate_RejectsSensitiveContentInDeidentificationMethodSummary()
    {
        var register = JsonNode.Parse(UnverifiedRegister)!;
        register["sources"]![0]!["deidentification_review"]!["method_summary"] = "reviewed contact reviewer@example.invalid";

        var report = BlindEvaluationSourceRegisterValidator.Validate(register.ToJsonString(), DatasetRecord("project_owned", "source:project-1"));

        Assert.False(report.RecordIntegrityValid);
        Assert.Contains(report.Issues, issue => issue.Code == "sensitive-content" &&
            issue.Field == "sources[0].deidentification_review.method_summary");
    }

    [Fact]
    public void Validate_RejectsSampleSourceRecordHashThatDiffersFromRegistry()
    {
        const string registeredHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string sampleHash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
        var register = UnverifiedRegister.Replace("\"source_kind\": \"project_owned\",", "\"source_kind\": \"project_owned\",\n              \"source_record_sha256\": \"" + registeredHash + "\",", StringComparison.Ordinal);

        var report = BlindEvaluationSourceRegisterValidator.Validate(register, DatasetRecord("project_owned", "source:project-1", sampleHash));

        Assert.False(report.RecordIntegrityValid);
        Assert.Contains(report.Issues, issue => issue.Code == "source-record-hash-mismatch" && issue.RecordId == "record-1");
    }

    [Fact]
    public void Validate_RequiresSupportingReviewFieldsForVerifiedRightsClaim()
    {
        var register = UnverifiedRegister.Replace("\"review_status\": \"not_verified\"", "\"review_status\": \"verified\"", StringComparison.Ordinal);

        var report = BlindEvaluationSourceRegisterValidator.Validate(register, DatasetRecord("project_owned", "source:project-1"));

        Assert.False(report.RecordIntegrityValid);
        Assert.Contains(report.Issues, issue => issue.Code == "verified-review-incomplete");
        Assert.False(report.RightsReviewClaimComplete);
    }

    [Fact]
    public void Validate_SeparatesCompleteReviewClaimsFromAuthenticityAndPhaseZeroGate()
    {
        var register = JsonNode.Parse(UnverifiedRegister)!;
        var source = register["sources"]![0]!;
        source["review_status"] = "verified";
        source["rights_review"] = new JsonObject
        {
            ["reviewer_role_ref"] = "role:independent-reviewer",
            ["reviewed_at"] = "2026-10-03T12:00:00Z",
            ["evidence_ref"] = "vault:rights-review-1",
            ["scope_matches"] = true
        };
        source["deidentification_review"] = new JsonObject
        {
            ["status"] = "reviewed",
            ["outcome"] = "approved",
            ["reviewer_role_ref"] = "role:privacy-reviewer",
            ["reviewed_at"] = "2026-10-03T12:00:00Z",
            ["method_summary"] = "reviewed"
        };

        var report = BlindEvaluationSourceRegisterValidator.Validate(register.ToJsonString(), DatasetRecord("project_owned", "source:project-1"));

        Assert.True(report.RecordIntegrityValid, string.Join("; ", report.Issues.Select(issue => issue.Message)));
        Assert.True(report.RequiredUsesClaimed);
        Assert.True(report.RightsReviewClaimComplete);
        Assert.False(report.ExternalEvidenceAuthenticityVerifiedByTool);
        Assert.False(report.Phase0GatePassed);
    }

    [Fact]
    public void Validate_RejectsNonRfc3339RightsReviewTimestamp()
    {
        var register = JsonNode.Parse(UnverifiedRegister)!;
        var source = register["sources"]![0]!;
        source["review_status"] = "verified";
        source["rights_review"] = new JsonObject
        {
            ["reviewer_role_ref"] = "role:independent-reviewer",
            ["reviewed_at"] = "10/03/2026 12:00:00 +00:00",
            ["evidence_ref"] = "vault:rights-review-1",
            ["scope_matches"] = true
        };
        source["deidentification_review"] = new JsonObject
        {
            ["status"] = "reviewed",
            ["outcome"] = "approved",
            ["reviewer_role_ref"] = "role:privacy-reviewer",
            ["reviewed_at"] = "2026-10-03T12:00:00Z",
            ["method_summary"] = "reviewed"
        };

        var report = BlindEvaluationSourceRegisterValidator.Validate(register.ToJsonString(), DatasetRecord("project_owned", "source:project-1"));

        Assert.False(report.RecordIntegrityValid);
        Assert.Contains(report.Issues, issue => issue.Code == "rights-review-time");
    }

    [Fact]
    public void Cli_WritesCreateOnlySourceRegisterReportAndKeepsPhaseZeroClosed()
    {
        using var fixture = new CliFixture();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = Program.Main(["source-register-validate", "--register", fixture.RegisterPath, "--dataset", fixture.DatasetPath, "--output", fixture.ReportPath]);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(fixture.ReportPath));
            using var report = JsonDocument.Parse(File.ReadAllText(fixture.ReportPath));
            Assert.True(report.RootElement.GetProperty("record_integrity_valid").GetBoolean());
            Assert.True(report.RootElement.GetProperty("sources_covered").GetBoolean());
            Assert.False(report.RootElement.GetProperty("rights_reviewed").GetBoolean());
            Assert.False(report.RootElement.GetProperty("external_evidence_authenticity_verified_by_tool").GetBoolean());
            Assert.False(report.RootElement.GetProperty("phase_0_gate_passed").GetBoolean());
            var original = File.ReadAllText(fixture.ReportPath);
            Assert.Equal(2, Program.Main(["source-register-validate", "--register", fixture.RegisterPath, "--dataset", fixture.DatasetPath, "--output", fixture.ReportPath]));
            Assert.Equal(original, File.ReadAllText(fixture.ReportPath));
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static string DatasetRecord(string sourceKind, string sourceRef, string? sourceRecordHash = null) =>
        "{\"id\":\"record-1\",\"source\":{\"source_kind\":\"" + sourceKind + "\",\"license_or_consent_ref\":\"" + sourceRef + "\"" +
        (sourceRecordHash is null ? string.Empty : ",\"source_record_hash\":\"" + sourceRecordHash + "\"") + "}}" + Environment.NewLine;

    private const string UnverifiedRegister = """
        {
          "register_version": "1.0",
          "sources": [
            {
              "source_ref": "source:project-1",
              "source_kind": "project_owned",
              "rights_basis": "project_owned",
              "permitted_uses": ["candidate_inference", "human_review", "aggregate_reporting"],
              "valid_from": "2025-01-01",
              "valid_until": null,
              "evidence": {"vault_ref": "vault:auth-1", "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},
              "deidentification_review": {
                "status": "not_reviewed",
                "outcome": "pending",
                "reviewer_role_ref": "role:privacy-reviewer",
                "reviewed_at": null,
                "method_summary": "pending"
              },
              "retention": {
                "expires_at": null,
                "withdrawal_contact_role_ref": "role:data-owner",
                "withdrawal_route_ref": "route:withdrawal"
              },
              "review_status": "not_verified",
              "rights_review": null
            }
          ]
        }
        """;

    private sealed class CliFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "source-register-" + Guid.NewGuid().ToString("N"));
        public string RegisterPath { get; }
        public string DatasetPath { get; }
        public string ReportPath { get; }

        public CliFixture()
        {
            Directory.CreateDirectory(_root);
            RegisterPath = Path.Combine(_root, "source-register.json");
            DatasetPath = Path.Combine(_root, "blind-eval.jsonl");
            ReportPath = Path.Combine(_root, "report.json");
            File.WriteAllText(RegisterPath, UnverifiedRegister);
            File.WriteAllText(DatasetPath, DatasetRecord("project_owned", "source:project-1"));
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
