using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Huaxiazi.DatasetBuilder;

public sealed record PolishRegressionHumanReviewPacketReport(int FamilyCount, int Phase0GateContribution,
    string DraftManifestSha256, IReadOnlyDictionary<string, string> FileSha256);

/// <summary>Builds a source-and-draft review packet without approving or changing any labels.</summary>
public static class PolishRegressionHumanReviewPacketBuilder
{
    private static readonly JsonSerializerOptions JsonlOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static readonly JsonSerializerOptions ManifestOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };

    private sealed record ReviewCase(JsonElement Root);
    private sealed record DraftLabel(JsonElement Root);
    private sealed record SourceRecord(JsonElement Root, string RawLine);

    public static PolishRegressionHumanReviewPacketReport Build(string casesPath, string sourceCanonicalPath,
        string labelsPath, string parentManifestPath, string draftManifestPath, string hierarchyPath,
        string outputDirectory)
    {
        foreach (var value in new[] { casesPath, sourceCanonicalPath, labelsPath, parentManifestPath, draftManifestPath, hierarchyPath, outputDirectory })
            ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        var outputFiles = new[] { "source-first-pass.jsonl", "draft-comparison.jsonl", "human-decisions.template.jsonl", "manifest.json" };
        if (outputFiles.Any(name => File.Exists(Path.Combine(output, name))))
            throw new IOException("审查包文件已存在；请使用新的 review packet 版本目录。");
        foreach (var required in new[] { "README.md", "decision.schema.json" })
            if (!File.Exists(Path.Combine(output, required))) throw new FileNotFoundException($"审查包缺少 {required}。", required);

        using var parentDoc = JsonDocument.Parse(File.ReadAllText(parentManifestPath));
        using var draftDoc = JsonDocument.Parse(File.ReadAllText(draftManifestPath));
        using var hierarchyDoc = JsonDocument.Parse(File.ReadAllText(hierarchyPath));
        var parent = parentDoc.RootElement;
        var draft = draftDoc.RootElement;
        var hierarchy = hierarchyDoc.RootElement;
        if (String(parent, "dataset_version") != "hxz-polish-regression-v1" || String(parent, "status") != "frozen" ||
            Integer(parent, "phase_0_gate_contribution") != 0 || !Boolean(parent, "not_admissible_as_blind_eval"))
            throw new InvalidDataException("父数据集必须是阶段 0 贡献为零且禁止盲评的冻结 v1。");
        var parentHash = HashFile(parentManifestPath);
        if (String(draft, "dataset_version") != "hxz-polish-regression-v2-draft" || String(draft, "status") != "draft" ||
            String(draft, "parent_manifest_sha256") != parentHash || Integer(draft, "phase_0_gate_contribution") != 0 ||
            !Boolean(draft, "not_admissible_as_blind_eval"))
            throw new InvalidDataException("标注草稿清单与冻结父版本不匹配，或不满足内部草稿边界。");
        if (String(hierarchy, "purpose") != "internal_regression_only" || Integer(hierarchy, "phase0_gate_contribution") != 0 ||
            HashFile(hierarchyPath) != String(draft, "hierarchy_report_sha256"))
            throw new InvalidDataException("层级报告不匹配草稿清单或不属于内部审查轨道。");

        var casesHash = HashFile(casesPath);
        var sourceHash = HashFile(sourceCanonicalPath);
        if (casesHash != String(Property(parent, "files"), "cases.jsonl") ||
            sourceHash != String(Property(parent, "source"), "source_sha256"))
            throw new InvalidDataException("审查包输入与冻结 v1 清单哈希不匹配。");
        if (HashFile(labelsPath) != String(Property(draft, "files"), "labels.jsonl"))
            throw new InvalidDataException("labels.jsonl 与 v2 草稿清单哈希不匹配。");

        var cases = ReadCases(casesPath);
        var labels = ReadLabels(labelsPath);
        var sources = ReadSources(sourceCanonicalPath);
        if (cases.Count == 0 || cases.Count != labels.Count || cases.Count != Integer(Property(draft, "counts"), "labels"))
            throw new InvalidDataException("案例、草稿标签与清单计数必须一一对应且非空。");

        var sourceLines = new List<string>();
        var comparisonLines = new List<string>();
        var decisionLines = new List<string>();
        foreach (var (familyId, caseRecord) in cases.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!labels.TryGetValue(familyId, out var label)) throw new InvalidDataException($"族 {familyId} 缺少 v2 草稿标签。");
            var item = caseRecord.Root;
            var labelRoot = label.Root;
            var sourceId = String(Property(item, "provenance"), "source_record_id");
            if (sourceId != String(labelRoot, "source_record_id") || !sources.TryGetValue(sourceId, out var source))
                throw new InvalidDataException($"族 {familyId} 的源记录 ID 不一致或缺失。");
            if (String(labelRoot, "human_status") != "unreviewed" || Property(Property(labelRoot, "fields"), "requested_intent").ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"族 {familyId} 的草稿状态或字段格式无效。");

            var sourceReview = new
            {
                schema_version = "1.0", review_item_id = "hxz-polish-human-review-" + Hash(familyId)[..16],
                family_id = familyId, split = String(item, "split"), case_id = String(item, "id"),
                source_record_id = sourceId, source_record_sha256 = String(Property(item, "provenance"), "source_sha256"),
                source_dataset_origin = String(item, "origin"), task = String(item, "task"),
                input = String(item, "input"), context = Property(item, "context").Clone(), claims = Property(item, "claims").Clone(),
                expected_decision = String(item, "expected_decision"), reference_output = String(item, "reference_output"),
                legacy_source_metadata = new
                {
                    scenario = String(source.Root, "scenario"), channel = String(source.Root, "channel"),
                    risk_level = String(source.Root, "risk_level"), rubric = Property(source.Root, "rubric").Clone()
                },
                independent_first_pass = new
                {
                    reviewer_id = (string?)null, reviewed_at_utc = (string?)null,
                    proposed_fields = new
                    {
                        requested_intent = (object?)null, semantic_scene = (object?)null, input_style = (object?)null,
                        target_formality = (object?)null, draft_fact_anchors = (object?)null, injection_marker = (object?)null,
                        legacy_scenario = (object?)null, legacy_channel = (object?)null, legacy_risk_level = (object?)null,
                        legacy_task = (object?)null, legacy_expected_decision = (object?)null, legacy_claims = (object?)null,
                        legacy_reference_output = (object?)null, legacy_rubric = (object?)null
                    },
                    rationale = (string?)null, uncertainty_notes = (string?)null
                },
                source_review_status = "pending_human_review"
            };
            sourceLines.Add(JsonSerializer.Serialize(sourceReview, JsonlOptions));

            var fields = Property(labelRoot, "fields");
            var draftAuthors = fields.EnumerateObject()
                .Where(property => String(Property(property.Value, "provenance"), "kind") == "ai_draft")
                .Select(property => String(Property(property.Value, "provenance"), "draft_by"))
                .Where(author => author.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var draftComparison = new
            {
                schema_version = "1.0", review_item_id = "hxz-polish-human-review-" + Hash(familyId)[..16], family_id = familyId,
                provisional_backbone_id = String(labelRoot, "backbone_id"), provisional_backbone_status = String(labelRoot, "backbone_status"),
                draft_authors = draftAuthors,
                fields = fields.EnumerateObject().ToDictionary(property => property.Name, property => new
                {
                    value = Property(property.Value, "value").Clone(), confidence = String(property.Value, "confidence"),
                    basis = String(property.Value, "basis"), provenance_kind = String(Property(property.Value, "provenance"), "kind")
                }, StringComparer.Ordinal),
                review_flags = Property(labelRoot, "review_flags").Clone(), comparison_status = "open_after_source_first_pass"
            };
            comparisonLines.Add(JsonSerializer.Serialize(draftComparison, JsonlOptions));
            var decisions = new
            {
                schema_version = "1.0", review_item_id = "hxz-polish-human-review-" + Hash(familyId)[..16], family_id = familyId,
                reviewer_id = (string?)null, reviewed_at_utc = (string?)null, overall_decision = "pending",
                backbone_decision = new { outcome = "pending", final_backbone_id = (string?)null, rationale = (string?)null },
                reference_reuse_decision = new { outcome = "pending", rationale = (string?)null },
                field_decisions = new { requested_intent = (object?)null, semantic_scene = (object?)null, input_style = (object?)null,
                    target_formality = (object?)null, draft_fact_anchors = (object?)null, injection_marker = (object?)null,
                    legacy_scenario = (object?)null, legacy_channel = (object?)null, legacy_risk_level = (object?)null,
                    legacy_task = (object?)null, legacy_expected_decision = (object?)null, legacy_claims = (object?)null, legacy_reference_output = (object?)null,
                    legacy_rubric = (object?)null },
                reviewer_is_independent_of_draft_author = (bool?)null, rationale = (string?)null
            };
            decisionLines.Add(JsonSerializer.Serialize(decisions, JsonlOptions));
        }
        if (labels.Keys.Except(cases.Keys, StringComparer.Ordinal).Any()) throw new InvalidDataException("草稿包含冻结案例中不存在的额外族。");

        WriteNew(Path.Combine(output, "source-first-pass.jsonl"), sourceLines);
        WriteNew(Path.Combine(output, "draft-comparison.jsonl"), comparisonLines);
        WriteNew(Path.Combine(output, "human-decisions.template.jsonl"), decisionLines);
        var fileNames = new[] { "README.md", "decision.schema.json", "source-first-pass.jsonl", "draft-comparison.jsonl", "human-decisions.template.jsonl" };
        var hashes = fileNames.ToDictionary(name => name, name => HashFile(Path.Combine(output, name)), StringComparer.Ordinal);
        var manifest = new
        {
            schema_version = "1.0", packet_version = "hxz-polish-human-review-v1", status = "awaiting_human_review",
            purpose = "internal_synthetic_label_review_only", source_parent_version = "hxz-polish-regression-v1",
            source_draft_version = "hxz-polish-regression-v2-draft", parent_manifest_sha256 = parentHash,
            draft_manifest_sha256 = HashFile(draftManifestPath), cases_sha256 = casesHash, source_sha256 = sourceHash,
            hierarchy_report_sha256 = HashFile(hierarchyPath), phase_0_gate_contribution = 0,
            not_admissible_as_blind_eval = true,
            workflow = "Complete source-first-pass independently before opening draft-comparison; record decisions in the template. AI cannot approve its own draft.",
            counts = new { review_items = cases.Count, completed = 0, human_verified = 0 }, files = hashes,
            limitations = new[]
            {
                "All cases and references are inherited synthetic project data and are not external or user-representative evidence.",
                "Backbone and reference reuse decisions require human adjudication.",
                "This packet contributes zero samples to phase 0 and cannot be used for model promotion."
            }
        };
        ImmutableArtifactWriter.WriteNew(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(manifest, ManifestOptions) + Environment.NewLine);
        return new(cases.Count, 0, HashFile(draftManifestPath), hashes);
    }

    private static Dictionary<string, ReviewCase> ReadCases(string path)
    {
        var result = new Dictionary<string, ReviewCase>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement.Clone();
            var familyId = String(root, "family_id");
            if (familyId.Length == 0 || !result.TryAdd(familyId, new(root))) throw new InvalidDataException("cases.jsonl 的 family_id 缺失或重复。");
        }
        return result;
    }

    private static Dictionary<string, DraftLabel> ReadLabels(string path)
    {
        var result = new Dictionary<string, DraftLabel>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement.Clone();
            var familyId = String(root, "family_id");
            if (familyId.Length == 0 || !result.TryAdd(familyId, new(root))) throw new InvalidDataException("labels.jsonl 的 family_id 缺失或重复。");
        }
        return result;
    }

    private static Dictionary<string, SourceRecord> ReadSources(string path)
    {
        var result = new Dictionary<string, SourceRecord>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement.Clone();
            var id = String(root, "id");
            if (id.Length > 0 && !result.TryAdd(id, new(root, line))) throw new InvalidDataException("来源 canonical 中存在重复 id。");
        }
        return result;
    }

    private static void WriteNew(string path, IReadOnlyCollection<string> lines) =>
        ImmutableArtifactWriter.WriteNew(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
    private static JsonElement Property(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value : default;
    private static string String(JsonElement root, string name) => Property(root, name).ValueKind == JsonValueKind.String ? Property(root, name).GetString() ?? "" : "";
    private static int Integer(JsonElement root, string name) => Property(root, name).ValueKind == JsonValueKind.Number && Property(root, name).TryGetInt32(out var value) ? value : -1;
    private static bool Boolean(JsonElement root, string name) => Property(root, name).ValueKind == JsonValueKind.True;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
