using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Huaxiazi.DatasetBuilder;

public sealed record PolishRegressionSupplementSpecificationIssue(string Code, string RecordId, string Message);
public sealed record PolishRegressionSupplementSpecificationValidationReport(bool Valid, int SpecificationCount,
    int TargetBehaviorCount, bool HumanApprovalRecorded, bool GenerationAuthorized, int Phase0GateContribution,
    IReadOnlyList<PolishRegressionSupplementSpecificationIssue> Issues);

/// <summary>Validates the AI-drafted W4.3 specification package and its coverage/provenance; it never approves generation.</summary>
public static class PolishRegressionSupplementSpecificationValidator
{
    private static readonly HashSet<string> Decisions = new(StringComparer.Ordinal) { "clarify", "polish" };

    public static PolishRegressionSupplementSpecificationValidationReport Validate(string specsDirectory, string coverageDirectory,
        string parentCasesPath, string parentManifestPath)
    {
        foreach (var value in new[] { specsDirectory, coverageDirectory, parentCasesPath, parentManifestPath })
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var specsRoot = Path.GetFullPath(specsDirectory);
        var coverageRoot = Path.GetFullPath(coverageDirectory);
        var specsManifestPath = Path.Combine(specsRoot, "manifest.json");
        var specsPath = Path.Combine(specsRoot, "sample-specs.jsonl");
        var coverageManifestPath = Path.Combine(coverageRoot, "manifest.json");
        var coveragePath = Path.Combine(coverageRoot, "coverage-report.json");
        foreach (var path in new[] { specsManifestPath, specsPath, coverageManifestPath, coveragePath, parentCasesPath, parentManifestPath })
            if (!File.Exists(path)) throw new FileNotFoundException("W4.3 规格包、覆盖包或父草稿文件缺失。", path);

        var issues = new List<PolishRegressionSupplementSpecificationIssue>();
        void Add(string code, string id, string message) => issues.Add(new(code, id, message));
        var parentCasesHash = HashFile(parentCasesPath);
        var parentManifestHash = HashFile(parentManifestPath);
        var coverageHash = HashFile(coveragePath);
        using var parentDoc = JsonDocument.Parse(File.ReadAllBytes(parentManifestPath));
        using var coverageManifestDoc = JsonDocument.Parse(File.ReadAllBytes(coverageManifestPath));
        using var coverageDoc = JsonDocument.Parse(File.ReadAllBytes(coveragePath));
        using var specsManifestDoc = JsonDocument.Parse(File.ReadAllBytes(specsManifestPath));
        var parent = parentDoc.RootElement;
        var coverageManifest = coverageManifestDoc.RootElement;
        var coverage = coverageDoc.RootElement;
        var manifest = specsManifestDoc.RootElement;

        if (String(parent, "dataset_version") != "hxz-polish-regression-ai-supplement-draft-v1" ||
            String(parent, "status") != "draft" || Integer(parent, "phase_0_gate_contribution") != 0 ||
            !Boolean(parent, "not_admissible_as_blind_eval") || String(Property(parent, "files"), "cases.jsonl") != parentCasesHash)
            Add("parent-boundary", "", "父补样必须是哈希绑定的内部草稿且阶段 0 贡献为零。");

        var coverageFiles = Property(coverageManifest, "files");
        if (String(coverageManifest, "artifact_version") != "hxz-polish-reg-ai-supplement-coverage-v1" ||
            String(coverageManifest, "source_cases_sha256") != parentCasesHash ||
            String(coverageManifest, "source_manifest_sha256") != parentManifestHash ||
            String(coverageFiles, "coverage-report.json") != coverageHash ||
            Integer(coverageManifest, "phase_0_gate_contribution") != 0 || !Boolean(coverageManifest, "not_admissible_as_blind_eval") ||
            Boolean(coverageManifest, "supplementation_authorized") ||
            String(coverage, "source_cases_sha256") != parentCasesHash || String(coverage, "source_manifest_sha256") != parentManifestHash ||
            Integer(coverage, "phase_0_gate_contribution") != 0 || !Boolean(coverage, "not_admissible_as_blind_eval") ||
            Boolean(coverage, "supplementation_authorized"))
            Add("coverage-binding", "", "覆盖报告、其清单和父草稿哈希/内部边界未完整匹配。");

        var specFiles = Property(manifest, "files");
        if (String(manifest, "artifact_version") != "hxz-polish-regression-ai-supplement-specs-v1" ||
            String(manifest, "status") != "ai_draft_pending_human_spec_review" ||
            String(manifest, "parent_cases_sha256") != parentCasesHash || String(manifest, "parent_manifest_sha256") != parentManifestHash ||
            String(manifest, "coverage_report_sha256") != coverageHash ||
            Integer(manifest, "phase_0_gate_contribution") != 0 || !Boolean(manifest, "not_admissible_as_blind_eval") ||
            Boolean(manifest, "generation_authorized"))
            Add("spec-manifest", "", "规格清单哈希、来源绑定、草稿状态或禁止生成边界无效。");
        if (String(specFiles, "sample-specs.jsonl") != HashFile(specsPath))
            Add("spec-file-hash", "", "sample-specs.jsonl 哈希与规格 manifest 不匹配。");

        var targetCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var coverageRows = Property(coverage, "coverage");
        if (coverageRows.ValueKind != JsonValueKind.Array) Add("coverage-schema", "", "覆盖报告缺少 coverage 数组。");
        else foreach (var row in coverageRows.EnumerateArray())
        {
            var behavior = String(row, "behavior");
            var deficit = Integer(row, "additional_family_specs_needed");
            if (behavior.Length == 0 || deficit < 0 || !targetCounts.TryAdd(behavior, deficit))
                Add("coverage-category", behavior, "覆盖类别缺失、缺口无效或重复。");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var actualCounts = targetCounts.Keys.ToDictionary(key => key, _ => 0, StringComparer.Ordinal);
        var specificationCount = 0;
        foreach (var line in File.ReadLines(specsPath, Encoding.UTF8))
        {
            specificationCount++;
            if (string.IsNullOrWhiteSpace(line)) { Add("spec-json", "", $"规格第 {specificationCount} 行为空。"); continue; }
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { Add("spec-json", "", $"规格第 {specificationCount} 行不是合法 JSON。"); continue; }
            using (document)
            {
                var spec = document.RootElement;
                var id = String(spec, "spec_id");
                var behavior = String(spec, "target_behavior");
                if (id.Length == 0 || !ids.Add(id)) Add("spec-id", id, "spec_id 缺失或重复。");
                if (!targetCounts.ContainsKey(behavior)) Add("spec-category", id, "target_behavior 不在绑定的覆盖报告类别中。");
                else actualCounts[behavior]++;
                if (String(spec, "schema_version") != "1.0" || String(spec, "origin") != "ai_assisted_draft" ||
                    String(spec, "status") != "ai_spec_draft_pending_human_review" || Integer(spec, "phase_0_gate_contribution") != 0 ||
                    string.IsNullOrWhiteSpace(String(spec, "scenario")) || string.IsNullOrWhiteSpace(String(spec, "input")) ||
                    !Decisions.Contains(String(spec, "expected_decision")) || !HasNonEmptyStrings(Property(spec, "facts")) ||
                    !HasNonEmptyStrings(Property(spec, "constraints")) || !HasNonEmptyStrings(Property(spec, "rubric_anchors")) ||
                    !HasNonEmptyStrings(Property(spec, "prohibited_content")))
                    Add("spec-fields", id, "规格缺少有效来源、草稿状态、事实、输入、约束、决策、评分锚点或禁止内容。");
            }
        }

        foreach (var (behavior, expected) in targetCounts)
            if (actualCounts[behavior] != expected)
                Add("coverage-deficit-mismatch", behavior, $"规格数 {actualCounts[behavior]} 与覆盖缺口 {expected} 不一致。");
        if (Integer(Property(manifest, "counts"), "specifications") != specificationCount)
            Add("spec-count", "", "规格总数与规格 manifest 不一致。");

        return new(issues.Count == 0, specificationCount, targetCounts.Count, false, false, 0, issues);
    }

    private static bool HasNonEmptyStrings(JsonElement element) => element.ValueKind == JsonValueKind.Array &&
        element.GetArrayLength() > 0 && element.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()));
    private static JsonElement Property(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value : default;
    private static string String(JsonElement root, string name) => Property(root, name).ValueKind == JsonValueKind.String ? Property(root, name).GetString() ?? "" : "";
    private static int Integer(JsonElement root, string name) => Property(root, name).TryGetInt32(out var value) ? value : 0;
    private static bool Boolean(JsonElement root, string name) => Property(root, name).ValueKind == JsonValueKind.True;
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
