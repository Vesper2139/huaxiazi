using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Huaxiazi.DatasetBuilder;

public sealed record PolishRegressionDraftLabelBuildReport(int LabelCount, int HumanVerifiedCount,
    string ParentManifestSha256, string PromptSha256, IReadOnlyDictionary<string, string> FileSha256);

/// <summary>Creates unreviewed, field-lineaged AI annotations as an immutable child artifact of a frozen internal dataset.</summary>
public static class PolishRegressionDraftLabelBuilder
{
    private static readonly JsonSerializerOptions LineOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static readonly JsonSerializerOptions DocumentOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };
    private static readonly IReadOnlyDictionary<string, string> IntentLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["公开说明"] = "public_explanation", ["婉拒"] = "polite_decline", ["提出请求"] = "make_request",
        ["致歉"] = "apologize", ["致谢"] = "thank", ["说明延期"] = "explain_delay",
        ["进度汇报"] = "progress_report", ["问题分析"] = "problem_analysis"
    };

    private sealed record SourceRecord(JsonElement Root, string RawLine);

    public static PolishRegressionDraftLabelBuildReport Build(string casesJsonlPath, string sourceCanonicalPath,
        string hierarchyReportPath, string parentManifestPath, string promptPath, string outputDirectory,
        string draftBy, string modelId, string draftedAtUtc)
    {
        foreach (var value in new[] { casesJsonlPath, sourceCanonicalPath, hierarchyReportPath, parentManifestPath, promptPath, outputDirectory, draftBy, modelId, draftedAtUtc })
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!DateTimeOffset.TryParse(draftedAtUtc, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var draftedAt))
            throw new ArgumentException("draftedAtUtc 必须是有效的 UTC 时间。", nameof(draftedAtUtc));
        draftedAt = draftedAt.ToUniversalTime();

        var root = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(root);
        var labelsPath = Path.Combine(root, "labels.jsonl");
        var manifestPath = Path.Combine(root, "manifest.json");
        if (File.Exists(labelsPath) || File.Exists(manifestPath))
            throw new IOException("标签草稿或清单已存在；草稿版本不可覆写，请使用新的版本目录。");
        foreach (var required in new[] { "README.md", "label.schema.json" })
            if (!File.Exists(Path.Combine(root, required))) throw new FileNotFoundException($"草稿版本缺少 {required}。", required);

        using var parentManifestDocument = JsonDocument.Parse(File.ReadAllText(parentManifestPath));
        var parentManifest = parentManifestDocument.RootElement;
        if (String(parentManifest, "dataset_version") != "hxz-polish-regression-v1" || String(parentManifest, "status") != "frozen" ||
            Integer(parentManifest, "phase_0_gate_contribution") != 0 || !Boolean(parentManifest, "not_admissible_as_blind_eval"))
            throw new InvalidDataException("父版本必须是阶段 0 贡献为零且禁止盲评的冻结内部数据集。");

        var casesHash = HashFile(casesJsonlPath);
        var casesHashInParent = String(Property(parentManifest, "files"), "cases.jsonl");
        if (!string.Equals(casesHash, casesHashInParent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("cases.jsonl 哈希与父版本冻结清单不一致。");
        var sourceHash = HashFile(sourceCanonicalPath);
        if (!string.Equals(sourceHash, String(Property(parentManifest, "source"), "source_sha256"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("来源 canonical 哈希与父版本冻结清单不一致。");

        var sourceRecords = ReadSourceRecords(sourceCanonicalPath);
        using var hierarchyDocument = JsonDocument.Parse(File.ReadAllText(hierarchyReportPath));
        var hierarchy = hierarchyDocument.RootElement;
        if (String(hierarchy, "purpose") != "internal_regression_only" || Integer(hierarchy, "phase0_gate_contribution") != 0)
            throw new InvalidDataException("层级报告不属于内部回归诊断轨道。");
        var familyMap = ReadFamilyMap(hierarchy);
        var promptHash = HashFile(promptPath);
        var nowText = draftedAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var labels = new List<JsonElement>();
        var seenFamilies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (line, index) in File.ReadLines(casesJsonlPath, Encoding.UTF8).Select((line, index) => (line, index)))
        {
            if (string.IsNullOrWhiteSpace(line)) throw new InvalidDataException($"cases.jsonl 第 {index + 1} 行为空。");
            using var caseDocument = JsonDocument.Parse(line);
            var item = caseDocument.RootElement;
            var familyId = String(item, "family_id");
            if (familyId.Length == 0 || !seenFamilies.Add(familyId)) throw new InvalidDataException($"cases.jsonl 第 {index + 1} 行 family_id 缺失或重复。");
            if (!familyMap.TryGetValue(familyId, out var familyLink)) throw new InvalidDataException($"族 {familyId} 缺少待裁定的 backbone 映射。");
            var sourceId = String(Property(item, "provenance"), "source_record_id");
            if (!sourceRecords.TryGetValue(sourceId, out var sourceRecord)) throw new InvalidDataException($"族 {familyId} 的来源记录 {sourceId} 不存在。");
            var context = Property(item, "context");
            var purpose = String(context, "purpose");
            if (!IntentLabels.TryGetValue(purpose, out var requestedIntent)) requestedIntent = "unmapped_legacy_purpose";
            var input = String(item, "input");
            var inputStyle = "colloquial";
            var scene = input.Contains("王总", StringComparison.Ordinal) || input.Contains("项目版本", StringComparison.Ordinal)
                ? "workplace_project_delivery_update" : "progress_update_domain_unspecified";
            var claims = Property(item, "claims");
            var claimSummary = DraftClaimSummary(input, claims, purpose);
            var sourceRoot = sourceRecord.Root;
            var legacyScenario = String(sourceRoot, "scenario");
            var legacyChannel = String(sourceRoot, "channel");
            var legacyRisk = String(sourceRoot, "risk_level");
            var legacyRubric = Property(sourceRoot, "rubric");
            var allFields = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["requested_intent"] = AiField(requestedIntent, "high", "从 context.purpose 与输入尾缀归一化得到；只是草稿映射。", draftBy, modelId, promptHash, nowText, sourceId),
                ["semantic_scene"] = AiField(scene, "medium", "从族代表输入的事实内容概括；不视为人工确认的真实场景。", draftBy, modelId, promptHash, nowText, sourceId),
                ["input_style"] = AiField(inputStyle, "medium", "输入为自然中文句子并带口语化要求；未见转写或跨语言线索。", draftBy, modelId, promptHash, nowText, sourceId),
                ["target_formality"] = AiField("natural_professional", "medium", "依据“语气自然一点，别写得太客套”及专业克制上下文。", draftBy, modelId, promptHash, nowText, sourceId),
                ["draft_fact_anchors"] = AiField(claimSummary, "medium", "从族代表输入与 claims 抽取的候选事实锚点，需逐项核对。", draftBy, modelId, promptHash, nowText, sourceId),
                ["injection_marker"] = AiField("none_detected_in_representative", "low", "仅对代表输入作启发式人工式阅读；不能外推到所有变体。", draftBy, modelId, promptHash, nowText, sourceId),
                ["reference_output_reuse"] = AiField(familyLink.ReferenceAlignmentStatus, "high", "父级骨架报告发现多意图共享同一参考成稿；只标记复核风险，不判定标签错误。", draftBy, modelId, promptHash, nowText, sourceId),
                ["legacy_scenario"] = ImportedField(legacyScenario, sourceId, nowText),
                ["legacy_channel"] = ImportedField(legacyChannel, sourceId, nowText),
                ["legacy_risk_level"] = ImportedField(legacyRisk, sourceId, nowText),
                ["legacy_task"] = ImportedField(String(item, "task"), sourceId, nowText),
                ["legacy_expected_decision"] = ImportedField(String(item, "expected_decision"), sourceId, nowText),
                ["legacy_claims"] = ImportedField(claims.Clone(), sourceId, nowText),
                ["legacy_reference_output"] = ImportedField(String(item, "reference_output"), sourceId, nowText),
                ["legacy_rubric"] = ImportedField(legacyRubric.Clone(), sourceId, nowText)
            };
            var labelObject = new
            {
                schema_version = "1.0", id = "hxz-polish-reg-label-" + Hash(familyId)[..16],
                dataset_version = "hxz-polish-regression-v2-draft", parent_dataset_version = "hxz-polish-regression-v1",
                family_id = familyId, backbone_id = familyLink.BackboneId, backbone_status = familyLink.BackboneStatus,
                split = String(item, "split"), origin = "ai_assisted_draft", source_record_id = sourceId,
                fields = allFields, human_status = "unreviewed", reviewer_id = (string?)null, reviewed_at_utc = (string?)null,
                review_flags = new[] { "backbone_mapping_requires_human_adjudication", "reference_output_reuse_requires_human_review", "all_labels_are_drafts" }
            };
            var jsonLine = JsonSerializer.Serialize(labelObject, LineOptions);
            using var labelDocument = JsonDocument.Parse(jsonLine);
            labels.Add(labelDocument.RootElement.Clone());
        }
        if (labels.Count == 0 || labels.Count != Integer(hierarchy, "task_family_count"))
            throw new InvalidDataException("case 标签数必须与层级报告的 task-family 数一致。");

        var labelLines = labels.Select(label => JsonSerializer.Serialize(label, LineOptions)).ToArray();
        var labelsText = string.Join(Environment.NewLine, labelLines) + Environment.NewLine;
        ImmutableArtifactWriter.WriteNew(labelsPath, labelsText);
        var artifactPaths = new[] { "README.md", "label.schema.json", Path.GetFileName(promptPath), "labels.jsonl" };
        var hashes = artifactPaths.ToDictionary(path => path.Replace('\\', '/'), path => HashFile(Path.Combine(root, path)), StringComparer.Ordinal);
        var parentManifestHash = HashFile(parentManifestPath);
        var manifest = new
        {
            schema_version = "1.0", dataset_version = "hxz-polish-regression-v2-draft", status = "draft",
            parent_dataset_version = "hxz-polish-regression-v1", parent_manifest_sha256 = parentManifestHash,
            parent_cases_sha256 = casesHash, parent_source_sha256 = sourceHash, hierarchy_report_sha256 = HashFile(hierarchyReportPath),
            purpose = "internal_regression_annotation_draft", phase_0_gate_contribution = 0, not_admissible_as_blind_eval = true,
            origin = "ai_assisted_draft", counts = new { labels = labels.Count, human_verified = 0, unreviewed = labels.Count },
            model = modelId, draft_by = draftBy, prompt_sha256 = promptHash, drafted_at_utc = nowText,
            files = hashes,
            limitations = new[]
            {
                "All annotations are AI drafts and remain unreviewed; they are not gold labels.",
                "Backbone grouping is provisional and its split overlaps across development/regression.",
                "Potential reference-output reuse is a review flag, not an automatic rejection.",
                "Formal phase 0 blind-evaluation contribution is zero."
            }
        };
        ImmutableArtifactWriter.WriteNew(manifestPath, JsonSerializer.Serialize(manifest, DocumentOptions) + Environment.NewLine);
        return new(labels.Count, 0, parentManifestHash, promptHash, hashes);
    }

    private sealed record FamilyLink(string BackboneId, string BackboneStatus, string ReferenceAlignmentStatus);

    private static Dictionary<string, FamilyLink> ReadFamilyMap(JsonElement hierarchy)
    {
        var map = new Dictionary<string, FamilyLink>(StringComparer.Ordinal);
        foreach (var backbone in Property(hierarchy, "backbones").EnumerateArray())
        foreach (var family in Property(backbone, "task_families").EnumerateArray())
        {
            var familyId = String(family, "family_id");
            var link = new FamilyLink(String(backbone, "backbone_id"), String(backbone, "status"), String(backbone, "reference_output_alignment_status"));
            if (familyId.Length == 0 || !map.TryAdd(familyId, link)) throw new InvalidDataException("hierarchy 报告中 family_id 缺失或重复。");
        }
        return map;
    }

    private static object AiField<T>(T value, string confidence, string basis, string draftBy, string model, string promptHash, string timestamp, string sourceId) => new
    {
        value, confidence, basis,
        provenance = new { kind = "ai_draft", draft_by = draftBy, model, prompt_hash = promptHash, drafted_at_utc = timestamp,
            human_status = "unreviewed", reviewer_id = (string?)null, reviewed_at_utc = (string?)null, source_record_id = sourceId }
    };

    private static object ImportedField<T>(T value, string sourceId, string timestamp) => new
    {
        value, confidence = "source_metadata_unverified", basis = "从父版本合成来源记录确定性导入；该字段不是人工核实。",
        provenance = new { kind = "legacy_import", draft_by = "deterministic_parent_import", model = "not_applicable", prompt_hash = "not_applicable",
            drafted_at_utc = timestamp, human_status = "unreviewed", reviewer_id = (string?)null, reviewed_at_utc = (string?)null, source_record_id = sourceId }
    };

    private static string[] DraftClaimSummary(string input, JsonElement claims, string purpose)
    {
        if (claims.ValueKind == JsonValueKind.Array && claims.GetArrayLength() > 0)
            return claims.EnumerateArray().Select(claim => string.Join("; ", claim.EnumerateObject()
                .Where(property => property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                .Select(property => property.Name + "=" + property.Value.ToString()))).ToArray();
        var core = input;
        var suffix = $" {purpose}，语气自然一点，别写得太客套。";
        if (core.EndsWith(suffix, StringComparison.Ordinal)) core = core[..^suffix.Length];
        if (core.Contains("看过", StringComparison.Ordinal) && core.Contains("进展", StringComparison.Ordinal))
            return ["说话者表示已看过该事项", "说话者希望同步进展"];
        return ["未发现结构化 claims；请人工从原文补核事实锚点"];
    }

    private static Dictionary<string, SourceRecord> ReadSourceRecords(string path)
    {
        var records = new Dictionary<string, SourceRecord>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            using var document = JsonDocument.Parse(line);
            var id = String(document.RootElement, "id");
            if (id.Length > 0 && !records.TryAdd(id, new(document.RootElement.Clone(), line)))
                throw new InvalidDataException("来源 canonical 中存在重复 id。");
        }
        return records;
    }

    private static JsonElement Property(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value : default;
    private static string String(JsonElement root, string name) => Property(root, name).ValueKind == JsonValueKind.String ? Property(root, name).GetString() ?? "" : "";
    private static int Integer(JsonElement root, string name) => Property(root, name).TryGetInt32(out var value) ? value : -1;
    private static bool Boolean(JsonElement root, string name) => Property(root, name).ValueKind == JsonValueKind.True;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
