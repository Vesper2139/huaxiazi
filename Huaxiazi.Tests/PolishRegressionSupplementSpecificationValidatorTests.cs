using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Text.Json;
using Huaxiazi.DatasetBuilder;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class PolishRegressionSupplementSpecificationValidatorTests
{
    [Fact]
    public void Validate_ConfirmsCoverageAndProvenanceButLeavesHumanApprovalPending()
    {
        using var fixture = new Fixture();

        var report = PolishRegressionSupplementSpecificationValidator.Validate(fixture.SpecsDirectory, fixture.CoverageDirectory,
            fixture.ParentCasesPath, fixture.ParentManifestPath);

        Assert.True(report.Valid, string.Join(";", report.Issues.Select(issue => issue.Code)));
        Assert.Equal(3, report.SpecificationCount);
        Assert.Equal(2, report.TargetBehaviorCount);
        Assert.False(report.HumanApprovalRecorded);
        Assert.False(report.GenerationAuthorized);
        Assert.Equal(0, report.Phase0GateContribution);
    }

    [Fact]
    public void Validate_RejectsHashTamperingAndDuplicateSpecifications()
    {
        using var fixture = new Fixture();
        File.AppendAllText(Path.Combine(fixture.SpecsDirectory, "sample-specs.jsonl"), File.ReadAllText(Path.Combine(fixture.SpecsDirectory, "sample-specs.jsonl")));

        var report = PolishRegressionSupplementSpecificationValidator.Validate(fixture.SpecsDirectory, fixture.CoverageDirectory,
            fixture.ParentCasesPath, fixture.ParentManifestPath);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "spec-file-hash");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "polish-regression-spec-validation-" + Guid.NewGuid().ToString("N"));
        public string ParentCasesPath => Path.Combine(_root, "parent-cases.jsonl");
        public string ParentManifestPath => Path.Combine(_root, "parent-manifest.json");
        public string CoverageDirectory => Path.Combine(_root, "coverage");
        public string SpecsDirectory => Path.Combine(_root, "specs");

        public Fixture()
        {
            Directory.CreateDirectory(CoverageDirectory);
            Directory.CreateDirectory(SpecsDirectory);
            File.WriteAllText(ParentCasesPath, "{\"id\":\"parent-case\"}" + Environment.NewLine, new UTF8Encoding(false));
            var casesHash = Hash(ParentCasesPath);
            File.WriteAllText(ParentManifestPath,
                "{\"dataset_version\":\"hxz-polish-regression-ai-supplement-draft-v1\",\"status\":\"draft\",\"purpose\":\"internal_regression_only\",\"phase_0_gate_contribution\":0,\"not_admissible_as_blind_eval\":true,\"files\":{\"cases.jsonl\":\"" + casesHash + "\"}}" + Environment.NewLine,
                new UTF8Encoding(false));
            var parentManifestHash = Hash(ParentManifestPath);
            var coverageReport = new
            {
                source_cases_sha256 = casesHash,
                source_manifest_sha256 = parentManifestHash,
                supplementation_authorized = false,
                phase_0_gate_contribution = 0,
                not_admissible_as_blind_eval = true,
                coverage = new[]
                {
                    new { behavior = "clarify-a", ai_draft_family_count = 1, target_family_count = 2, additional_family_specs_needed = 1 },
                    new { behavior = "format-b", ai_draft_family_count = 0, target_family_count = 2, additional_family_specs_needed = 2 }
                }
            };
            var coveragePath = Path.Combine(CoverageDirectory, "coverage-report.json");
            WriteJson(coveragePath, coverageReport);
            WriteJson(Path.Combine(CoverageDirectory, "manifest.json"), new
            {
                artifact_version = "hxz-polish-reg-ai-supplement-coverage-v1", source_cases_sha256 = casesHash,
                source_manifest_sha256 = parentManifestHash, phase_0_gate_contribution = 0, not_admissible_as_blind_eval = true,
                supplementation_authorized = false, files = new Dictionary<string, string> { ["coverage-report.json"] = Hash(coveragePath) }
            });
            var specs = new[]
            {
                new { schema_version = "1.0", spec_id = "spec-1", target_behavior = "clarify-a", scenario = "A", facts = new[] { "fact" }, input = "input a", constraints = new[] { "constraint" }, expected_decision = "clarify", rubric_anchors = new[] { "anchor" }, prohibited_content = new[] { "forbidden" }, required_schema_extension = (string?)null, status = "ai_spec_draft_pending_human_review", origin = "ai_assisted_draft", phase_0_gate_contribution = 0 },
                new { schema_version = "1.0", spec_id = "spec-2", target_behavior = "format-b", scenario = "B", facts = new[] { "fact" }, input = "input b", constraints = new[] { "constraint" }, expected_decision = "polish", rubric_anchors = new[] { "anchor" }, prohibited_content = new[] { "forbidden" }, required_schema_extension = (string?)null, status = "ai_spec_draft_pending_human_review", origin = "ai_assisted_draft", phase_0_gate_contribution = 0 },
                new { schema_version = "1.0", spec_id = "spec-3", target_behavior = "format-b", scenario = "C", facts = new[] { "fact" }, input = "input c", constraints = new[] { "constraint" }, expected_decision = "polish", rubric_anchors = new[] { "anchor" }, prohibited_content = new[] { "forbidden" }, required_schema_extension = (string?)null, status = "ai_spec_draft_pending_human_review", origin = "ai_assisted_draft", phase_0_gate_contribution = 0 }
            };
            var specPath = Path.Combine(SpecsDirectory, "sample-specs.jsonl");
            File.WriteAllLines(specPath, specs.Select(spec => JsonSerializer.Serialize(spec, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })), new UTF8Encoding(false));
            WriteJson(Path.Combine(SpecsDirectory, "manifest.json"), new
            {
                artifact_version = "hxz-polish-regression-ai-supplement-specs-v1", status = "ai_draft_pending_human_spec_review",
                parent_cases_sha256 = casesHash, parent_manifest_sha256 = parentManifestHash, coverage_report_sha256 = Hash(coveragePath),
                phase_0_gate_contribution = 0, not_admissible_as_blind_eval = true, generation_authorized = false,
                counts = new { specifications = 3 }, files = new Dictionary<string, string> { ["sample-specs.jsonl"] = Hash(specPath) }
            });
        }

        private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        private static void WriteJson(string path, object value) => File.WriteAllText(path,
            JsonSerializer.Serialize(value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }) + Environment.NewLine,
            new UTF8Encoding(false));
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
    }
}
