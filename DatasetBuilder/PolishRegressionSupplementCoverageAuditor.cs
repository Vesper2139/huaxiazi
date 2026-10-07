using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Huaxiazi.DatasetBuilder;

public sealed record PolishRegressionSupplementCoverageResult(int FamilyCount, int BehaviorCount, int SpecificationDeficitCount,
    int Phase0GateContribution, IReadOnlyDictionary<string, string> FileSha256);

/// <summary>Counts AI-draft behavior labels and computes gaps to a review target; it does not authorize generation.</summary>
public static class PolishRegressionSupplementCoverageAuditor
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };

    public static PolishRegressionSupplementCoverageResult Build(string casesPath, string manifestPath,
        IReadOnlyList<string> targetBehaviors, int minimumFamiliesPerBehavior, string outputDirectory)
    {
        foreach (var value in new[] { casesPath, manifestPath, outputDirectory }) ArgumentException.ThrowIfNullOrWhiteSpace(value);
        ArgumentNullException.ThrowIfNull(targetBehaviors);
        if (targetBehaviors.Count == 0 || targetBehaviors.Any(string.IsNullOrWhiteSpace) ||
            targetBehaviors.Distinct(StringComparer.Ordinal).Count() != targetBehaviors.Count || minimumFamiliesPerBehavior < 1 || minimumFamiliesPerBehavior > 120)
            throw new ArgumentException("行为类别必须非空且唯一；minimumFamiliesPerBehavior 必须在 1 至 120 之间。");

        var casesHash = HashFile(casesPath);
        var manifestHash = HashFile(manifestPath);
        using var manifestDocument = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var manifest = manifestDocument.RootElement;
        if (String(manifest, "dataset_version") != "hxz-polish-regression-ai-supplement-draft-v1" || String(manifest, "status") != "draft" ||
            String(manifest, "purpose") != "internal_regression_only" || Integer(manifest, "phase_0_gate_contribution") != 0 ||
            !Boolean(manifest, "not_admissible_as_blind_eval") || String(Property(manifest, "files"), "cases.jsonl") != casesHash)
            throw new InvalidDataException("补样必须是哈希匹配的内部 AI 草稿，且阶段 0 贡献为零、不可用于盲评。");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var familyIds = new HashSet<string>(StringComparer.Ordinal);
        var normalizedInputs = new HashSet<string>(StringComparer.Ordinal);
        var behaviorCounts = targetBehaviors.ToDictionary(item => item, _ => 0, StringComparer.Ordinal);
        var decisionCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var splitCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var rowCount = 0;
        foreach (var line in File.ReadLines(casesPath, Encoding.UTF8))
        {
            rowCount++;
            if (string.IsNullOrWhiteSpace(line)) throw new InvalidDataException($"cases.jsonl 第 {rowCount} 行为空。");
            using var document = JsonDocument.Parse(line);
            var item = document.RootElement;
            var id = String(item, "id");
            var family = String(item, "family_id");
            var split = String(item, "split");
            var input = String(item, "input");
            var behavior = String(Property(item, "context"), "gap_behavior");
            if (id.Length == 0 || family.Length == 0 || input.Length == 0 || split is not ("development" or "regression") ||
                String(item, "origin") != "ai_assisted_draft" || String(item, "human_status") != "unreviewed" || behavior.Length == 0)
                throw new InvalidDataException($"cases.jsonl 第 {rowCount} 行缺少身份、split、输入、行为标签或 AI 未审阅状态。");
            if (!ids.Add(id) || !familyIds.Add(family)) throw new InvalidDataException($"cases.jsonl 第 {rowCount} 行包含重复 id 或 family_id。");
            if (!normalizedInputs.Add(Normalize(input))) throw new InvalidDataException($"cases.jsonl 第 {rowCount} 行输入在 NFKC 小写字母数字归一后重复。");
            if (behaviorCounts.ContainsKey(behavior)) behaviorCounts[behavior]++;
            var decision = String(item, "expected_decision");
            if (decision.Length > 0) decisionCounts[decision] = decisionCounts.GetValueOrDefault(decision) + 1;
            splitCounts[split] = splitCounts.GetValueOrDefault(split) + 1;
        }
        if (rowCount == 0 || rowCount != familyIds.Count || Integer(Property(manifest, "counts"), "cases") != rowCount ||
            Integer(Property(manifest, "counts"), "families") != familyIds.Count)
            throw new InvalidDataException("案例/族数为空或与 manifest 计数不一致。");

        var coverage = targetBehaviors.Select(behavior => new
        {
            behavior,
            ai_draft_family_count = behaviorCounts[behavior],
            target_family_count = minimumFamiliesPerBehavior,
            additional_family_specs_needed = Math.Max(0, minimumFamiliesPerBehavior - behaviorCounts[behavior]),
            evidence_status = "ai_draft_unreviewed"
        }).ToArray();
        var deficit = coverage.Sum(item => item.additional_family_specs_needed);
        var report = new
        {
            schema_version = "1.0", report_version = "hxz-polish-regression-ai-supplement-coverage-v1",
            status = "internal_diagnostic_pending_human_spec_review", purpose = "internal_regression_coverage_gap_triage",
            phase_0_gate_contribution = 0, not_admissible_as_blind_eval = true, supplementation_authorized = false,
            evidence_status = "ai_draft_unreviewed", unit = "provisional_ai_labeled_family",
            case_count = rowCount, family_count = familyIds.Count, unique_normalized_input_count = normalizedInputs.Count,
            target_minimum_families_per_behavior = minimumFamiliesPerBehavior, behavior_category_count = targetBehaviors.Count,
            total_additional_families_to_target = deficit,
            projected_family_count_after_target = familyIds.Count + deficit,
            maximum_supplement_family_cap = 120, projected_count_within_cap = familyIds.Count + deficit <= 120,
            decision_counts = decisionCounts, split_counts = splitCounts, coverage,
            source_cases_sha256 = casesHash, source_manifest_sha256 = manifestHash,
            limitations = new[]
            {
                "Behavior labels and references are AI-authored and unreviewed; counts describe assigned categories, not model capability or sample gold quality.",
                "The five-family target is a planning recommendation, not a statistical power guarantee.",
                "Deficits identify how many specifications would be needed if each new family covers exactly one target behavior.",
                "No samples are generated or authorized by this report; W4.3 specifications require human review before W4.4 generation.",
                "This artifact contributes zero to formal phase 0 blind evaluation and cannot promote a model."
            }
        };

        var output = Path.GetFullPath(outputDirectory);
        var reportPath = Path.Combine(output, "coverage-report.json");
        var manifestPathOut = Path.Combine(output, "manifest.json");
        if (Directory.Exists(output) || File.Exists(reportPath) || File.Exists(manifestPathOut))
            throw new IOException("补样覆盖报告目录已存在；请使用新的版本目录。");
        Directory.CreateDirectory(output);
        ImmutableArtifactWriter.WriteNew(reportPath, JsonSerializer.Serialize(report, JsonOptions) + Environment.NewLine);
        var files = new Dictionary<string, string>(StringComparer.Ordinal) { ["coverage-report.json"] = HashFile(reportPath) };
        var outputManifest = new
        {
            schema_version = "1.0", artifact_version = "hxz-polish-reg-ai-supplement-coverage-v1",
            status = "internal_diagnostic_pending_human_spec_review", purpose = "internal_regression_coverage_gap_triage",
            source_cases_sha256 = casesHash, source_manifest_sha256 = manifestHash,
            phase_0_gate_contribution = 0, not_admissible_as_blind_eval = true, supplementation_authorized = false, files
        };
        ImmutableArtifactWriter.WriteNew(manifestPathOut, JsonSerializer.Serialize(outputManifest, JsonOptions) + Environment.NewLine);
        return new(familyIds.Count, targetBehaviors.Count, deficit, 0, files);
    }

    private static string Normalize(string value) => string.Concat(value.Normalize(NormalizationForm.FormKC).ToLowerInvariant().EnumerateRunes()
        .Where(Rune.IsLetterOrDigit).Select(rune => rune.ToString()));
    private static JsonElement Property(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value : default;
    private static string String(JsonElement root, string name) => Property(root, name).ValueKind == JsonValueKind.String ? Property(root, name).GetString() ?? "" : "";
    private static int Integer(JsonElement root, string name) => Property(root, name).TryGetInt32(out var value) ? value : 0;
    private static bool Boolean(JsonElement root, string name) => Property(root, name).ValueKind == JsonValueKind.True;
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
