using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Huaxiazi.DatasetBuilder;

namespace Huaxiazi.Tests;

internal sealed class PolishRegressionSupplementSpecificationReviewFixture : IDisposable
{
    private readonly string _root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "spec-review-flow-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public string SpecsDirectory => Path.Combine(_root, "specs");
    public string SpecsPath => Path.Combine(SpecsDirectory, "sample-specs.jsonl");
    public string SpecsManifestPath => Path.Combine(SpecsDirectory, "manifest.json");
    public string ManifestPath => SpecsManifestPath;
    public string CoverageDirectory => Path.Combine(_root, "coverage");
    public string ParentCasesPath => Path.Combine(_root, "parent-cases.jsonl");
    public string ParentManifestPath => Path.Combine(_root, "parent-manifest.json");
    public string PacketDirectory => Path.Combine(_root, "packet");
    public string OutputDirectory => Path.Combine(_root, "imported");

    public PolishRegressionSupplementSpecificationReviewFixture()
    {
        Directory.CreateDirectory(SpecsDirectory);
        Directory.CreateDirectory(CoverageDirectory);
        File.WriteAllText(ParentCasesPath, "{\"id\":\"parent-case\"}" + Environment.NewLine, new UTF8Encoding(false));
        var parentCasesHash = Hash(ParentCasesPath);
        WriteJson(ParentManifestPath, new
        {
            dataset_version = "hxz-polish-regression-ai-supplement-draft-v1", status = "draft", purpose = "internal_regression_only",
            phase_0_gate_contribution = 0, not_admissible_as_blind_eval = true,
            files = new Dictionary<string, string> { ["cases.jsonl"] = parentCasesHash }
        });
        var parentManifestHash = Hash(ParentManifestPath);
        var coverageReportPath = Path.Combine(CoverageDirectory, "coverage-report.json");
        WriteJson(coverageReportPath, new
        {
            source_cases_sha256 = parentCasesHash, source_manifest_sha256 = parentManifestHash,
            supplementation_authorized = false, phase_0_gate_contribution = 0, not_admissible_as_blind_eval = true,
            coverage = new[] { new { behavior = "clarification_positive_examples", ai_draft_family_count = 0, target_family_count = 1, additional_family_specs_needed = 1 } }
        });
        WriteJson(Path.Combine(CoverageDirectory, "manifest.json"), new
        {
            artifact_version = "hxz-polish-reg-ai-supplement-coverage-v1", source_cases_sha256 = parentCasesHash,
            source_manifest_sha256 = parentManifestHash, phase_0_gate_contribution = 0, not_admissible_as_blind_eval = true,
            supplementation_authorized = false,
            files = new Dictionary<string, string> { ["coverage-report.json"] = Hash(coverageReportPath) }
        });
        var spec = new
        {
            schema_version = "1.0", spec_id = "spec-1", target_behavior = "clarification_positive_examples", scenario = "Missing date",
            facts = new[] { "date absent" }, input = "Please invite them", constraints = new[] { "Do not guess the date" },
            expected_decision = "clarify", rubric_anchors = new[] { "Ask for date" }, prohibited_content = new[] { "Invented date" },
            required_schema_extension = (string?)null, status = "ai_spec_draft_pending_human_review", origin = "ai_assisted_draft", phase_0_gate_contribution = 0
        };
        File.WriteAllText(SpecsPath, JsonSerializer.Serialize(spec, JsonOptions) + Environment.NewLine, new UTF8Encoding(false));
        WriteJson(SpecsManifestPath, new
        {
            artifact_version = "hxz-polish-regression-ai-supplement-specs-v1", status = "ai_draft_pending_human_spec_review",
            parent_cases_sha256 = parentCasesHash, parent_manifest_sha256 = parentManifestHash,
            coverage_report_sha256 = Hash(coverageReportPath), phase_0_gate_contribution = 0,
            not_admissible_as_blind_eval = true, generation_authorized = false, counts = new { specifications = 1 },
            files = new Dictionary<string, string> { ["sample-specs.jsonl"] = Hash(SpecsPath) }
        });
    }

    public void BuildPacket() => PolishRegressionSupplementSpecificationReviewPacketBuilder.Build(
        SpecsDirectory, CoverageDirectory, ParentCasesPath, ParentManifestPath, PacketDirectory);

    public void WriteReview(string decision, string rationale, string editedSpec)
    {
        var headers = new[] { "spec_id", "target_behavior", "scenario", "facts_json", "input", "constraints_json", "expected_decision_draft", "rubric_anchors_json", "prohibited_content_json", "required_schema_extension", "human_decision", "human_edited_spec_json", "reviewer_id", "reviewed_at_utc", "rationale" };
        var fields = new[] { "spec-1", "clarification_positive_examples", "Missing date", "[\"date absent\"]", "Please invite them", "[\"Do not guess the date\"]", "clarify", "[\"Ask for date\"]", "[\"Invented date\"]", "", decision, editedSpec, "reviewer-a", "2026-10-05T10:00:00Z", rationale };
        File.WriteAllText(Path.Combine(PacketDirectory, "specification-review.csv"), string.Join(',', headers.Select(Csv)) + Environment.NewLine + string.Join(',', fields.Select(Csv)) + Environment.NewLine, new UTF8Encoding(false));
    }

    private static void WriteJson(string path, object value) => File.WriteAllText(path,
        JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, new UTF8Encoding(false));
    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
