using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Huaxiazi.DatasetBuilder;

public sealed record PolishRegressionCoverageDimension(string Name, string EvidenceBasis,
    IReadOnlyDictionary<string, int>? SourceRowsWithValue, IReadOnlyDictionary<string, int> FamiliesWithValue);
public sealed record PolishRegressionCoverageGap(string Behavior, int? KnownPositiveFamilyCount,
    string EvidenceStatus, string EvidenceBasis, string InternalRegressionNeed, string SupplementationAction);
public sealed record PolishRegressionCoverageGapReport(string SchemaVersion, string Purpose, int Phase0GateContribution,
    bool NotAdmissibleAsBlindEval, int SourceRowCount, int SemanticFamilyCount, int HumanVerifiedFamilyCount,
    string Unit, string ParentManifestSha256, string DraftManifestSha256, string SourceSha256,
    IReadOnlyList<PolishRegressionCoverageDimension> Dimensions, IReadOnlyList<PolishRegressionCoverageGap> Gaps,
    IReadOnlyList<string> Limitations);
public sealed record PolishRegressionCoverageGapBuildReport(string OutputDirectory, int SourceRowCount,
    int SemanticFamilyCount, int Phase0GateContribution, IReadOnlyDictionary<string, string> FileSha256);

/// <summary>Creates evidence-based family coverage and a human-pending gap triage report; it never authorizes supplementation.</summary>
public static class PolishRegressionCoverageGapAuditor
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };
    private sealed record Family(string Id, string Split, List<JsonElement> Rows);

    public static PolishRegressionCoverageGapBuildReport Build(string casesPath, string variantsPath, string sourceCanonicalPath,
        string labelsPath, string parentManifestPath, string draftManifestPath, string hierarchyPath, string outputDirectory)
    {
        foreach (var value in new[] { casesPath, variantsPath, sourceCanonicalPath, labelsPath, parentManifestPath, draftManifestPath, hierarchyPath, outputDirectory })
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        var reportPath = Path.Combine(output, "coverage-gap-report.json");
        var manifestPath = Path.Combine(output, "manifest.json");
        if (File.Exists(reportPath) || File.Exists(manifestPath)) throw new IOException("W4 报告目录已存在产物；请使用新的版本目录。");

        using var parentDocument = JsonDocument.Parse(File.ReadAllText(parentManifestPath));
        using var draftDocument = JsonDocument.Parse(File.ReadAllText(draftManifestPath));
        using var hierarchyDocument = JsonDocument.Parse(File.ReadAllText(hierarchyPath));
        var parent = parentDocument.RootElement;
        var draft = draftDocument.RootElement;
        var hierarchy = hierarchyDocument.RootElement;
        var parentHash = HashFile(parentManifestPath);
        var draftHash = HashFile(draftManifestPath);
        var sourceHash = HashFile(sourceCanonicalPath);
        if (String(parent, "dataset_version") != "hxz-polish-regression-v1" || String(parent, "status") != "frozen" ||
            Integer(parent, "phase_0_gate_contribution") != 0 || !Boolean(parent, "not_admissible_as_blind_eval"))
            throw new InvalidDataException("W4 覆盖报告只接受冻结、阶段 0 零贡献的内部 v1 父版本。");
        if (String(draft, "dataset_version") != "hxz-polish-regression-v2-draft" || String(draft, "parent_manifest_sha256") != parentHash ||
            Integer(draft, "phase_0_gate_contribution") != 0 || !Boolean(draft, "not_admissible_as_blind_eval"))
            throw new InvalidDataException("AI 草稿 manifest 必须绑定父 v1 且保持内部草稿边界。");
        if (String(hierarchy, "purpose") != "internal_regression_only" || Integer(hierarchy, "phase0_gate_contribution") != 0 ||
            HashFile(hierarchyPath) != String(draft, "hierarchy_report_sha256"))
            throw new InvalidDataException("W2 层级报告哈希或内部边界不匹配。");
        var parentFiles = Property(parent, "files");
        if (HashFile(casesPath) != String(parentFiles, "cases.jsonl") || HashFile(variantsPath) != String(parentFiles, "variants.jsonl") ||
            sourceHash != String(Property(parent, "source"), "source_sha256") ||
            HashFile(labelsPath) != String(Property(draft, "files"), "labels.jsonl"))
            throw new InvalidDataException("W4 输入数据哈希与冻结清单不匹配。");

        var families = ReadFamilies(casesPath);
        var sourceRecords = ReadSource(sourceCanonicalPath);
        var variantCount = MapVariants(variantsPath, families, sourceRecords);
        var labels = ReadLabels(labelsPath);
        if (families.Count == 0 || families.Count != labels.Count || families.Count != Integer(Property(draft, "counts"), "labels"))
            throw new InvalidDataException("案例族、草稿标签和清单计数必须一一对应。");
        foreach (var familyId in families.Keys)
            if (!labels.ContainsKey(familyId)) throw new InvalidDataException($"族 {familyId} 缺少草稿标签。");

        var dimensions = new List<PolishRegressionCoverageDimension>
        {
            Dimension("legacy_scenario", "source canonical scenario metadata; source metadata is not human verified", families, row => String(row, "scenario")),
            Dimension("channel", "source canonical channel metadata; source metadata is not human verified", families, row => String(row, "channel")),
            Dimension("risk_level", "source canonical risk metadata; source metadata is not human verified", families, row => String(row, "risk_level")),
            Dimension("expected_decision", "source canonical expected_decision", families, row => String(row, "expected_decision")),
            Dimension("legacy_formality", "source canonical context.formality metadata", families, row => String(Property(row, "context"), "formality")),
            Dimension("output_kind", "source canonical output.kind", families, row => String(Property(row, "output"), "kind")),
            Dimension("explicit_requirements", "source canonical context.explicit_requirements; empty is reported as none_explicit, not missing", families,
                row => { var value = String(Property(row, "context"), "explicit_requirements"); return value.Length == 0 ? "none_explicit" : value; }),
            Dimension("claims_present", "source canonical claims array; family counts once if any member has a nonempty array", families,
                row => Property(row, "claims").ValueKind == JsonValueKind.Array && Property(row, "claims").GetArrayLength() > 0 ? "yes" : "no"),
            Dimension("claim_subject", "claim field nonempty in at least one family member", families, row => ClaimPresence(row, "subject")),
            Dimension("claim_relation", "claim field nonempty in at least one family member", families, row => ClaimPresence(row, "relation")),
            Dimension("claim_object", "claim field nonempty in at least one family member", families, row => ClaimPresence(row, "object")),
            Dimension("claim_quantity", "claim field nonempty in at least one family member", families, row => ClaimPresence(row, "quantity")),
            Dimension("claim_time", "claim field nonempty in at least one family member", families, row => ClaimPresence(row, "time")),
            Dimension("claim_condition", "claim field nonempty in at least one family member", families, row => ClaimPresence(row, "condition")),
            Dimension("claim_modality", "claim field nonempty in at least one family member", families, row => ClaimPresence(row, "modality")),
            Dimension("claim_negated", "claim.negated=true in at least one family member", families,
                row => ClaimBooleanPresence(row, "negated")),
            DraftDimension("draft_semantic_scene", "AI draft labels; all values remain unreviewed", labels, "semantic_scene"),
            DraftDimension("draft_input_style", "AI draft labels; all values remain unreviewed", labels, "input_style"),
            DraftDimension("draft_target_formality", "AI draft labels; all values remain unreviewed", labels, "target_formality")
        };

        var clarificationPositive = CountFamiliesWithSourcePredicate(families, row => String(row, "expected_decision") == "clarify");
        var clarificationOutputs = CountFamiliesWithSourcePredicate(families, row =>
            String(Property(row, "output"), "kind") == "clarification" ||
            Property(row, "clarification_questions").ValueKind == JsonValueKind.Array && Property(row, "clarification_questions").GetArrayLength() > 0);
        var formatPositive = CountFamiliesWithSourcePredicate(families, row =>
            String(Property(row, "context"), "explicit_requirements").Length > 0);
        var highRiskFamilies = CountFamiliesWithSourcePredicate(families, row => String(row, "risk_level") == "high");
        var negationFamilies = CountFamiliesWithClaimPredicate(families, claim => Boolean(claim, "negated"));
        var quantityFamilies = CountFamiliesWithClaimPredicate(families, claim => String(claim, "quantity").Length > 0);
        var timeFamilies = CountFamiliesWithClaimPredicate(families, claim => String(claim, "time").Length > 0);
        var conditionFamilies = CountFamiliesWithClaimPredicate(families, claim => String(claim, "condition").Length > 0);
        var gaps = new[]
        {
            Gap("clarification_positive_examples", clarificationPositive, "measured", "Distinct families with expected_decision=clarify in any source variant."),
            Gap("needs_clarification_outputs", clarificationOutputs, "measured", "Distinct families with output.kind=clarification or a nonempty clarification_questions array in any source variant."),
            Gap("format_and_schema_requirements", formatPositive, "measured", "Distinct families with nonempty context.explicit_requirements; does not infer format contracts from prose."),
            Gap("high_risk_fact_reversal", 0, "not_labeled", $"There are {highRiskFamilies} families carrying high risk metadata, but the source has no reviewed fact_reversal outcome label; 0 is the number of labeled positives, not a measured safety pass."),
            Gap("tone_and_scenario_diversity", null, "needs_human_triage", "See legacy_scenario, channel, legacy_formality, and draft_* distributions; diversity adequacy needs a human task definition."),
            Gap("multi_turn_revision_and_user_negation", 0, "not_represented", "The sample schema contains one input per case and no conversation-turn/revision history; 0 represented examples does not prove absence of negation in single-turn claims."),
            Gap("negation_quantity_time_condition_boundaries", null, "partially_measured", $"Claim families with negation={negationFamilies}, quantity={quantityFamilies}, time={timeFamilies}, condition={conditionFamilies}; boundary adequacy and expected behavior need human triage."),
            Gap("prompt_injection_resistance", 0, "not_labeled", "The legacy source has no reviewed injection challenge/outcome label; 0 labeled positives is not an injection-resistance result."),
            Gap("tool_failure", 0, "not_represented", "The polishing sample schema contains no tool invocation/failure episode."),
            Gap("non_workplace_scenarios", null, "needs_human_triage", "See legacy_scenario distribution; which categories count as non-workplace and whether coverage is adequate require human adjudication."),
            Gap("cross_language_mixing", 0, "not_labeled", "No human-reviewed input_style labels identify cross-language examples; 0 labeled positives does not establish the text-level absence of mixing.")
        };
        var humanVerified = labels.Values.Count(label => String(label, "human_status") is "accepted" or "edited");
        var report = new PolishRegressionCoverageGapReport("1.0", "internal_regression_coverage_and_gap_triage", 0, true,
            variantCount, families.Count, humanVerified, "semantic_family", parentHash, draftHash, sourceHash, dimensions, gaps,
            [
                "Counts are based on 16 provisional semantic families, not the 3,000 repeated source rows.",
                "Source metadata and AI draft dimensions are not human verified; draft_* dimensions are displayed separately.",
                "A zero labeled-positive count is not proof that behavior is absent or that the model passes that behavior.",
                "All supplementation need decisions remain pending human triage; no samples are authorized or generated.",
                "This report contributes zero samples to formal phase 0 blind evaluation and cannot promote a model."
            ]);
        ImmutableArtifactWriter.WriteNew(reportPath, JsonSerializer.Serialize(report, JsonOptions) + Environment.NewLine);
        var files = new Dictionary<string, string>(StringComparer.Ordinal) { ["coverage-gap-report.json"] = HashFile(reportPath) };
        var manifest = new
        {
            schema_version = "1.0", report_version = "hxz-polish-regression-w4-audit-v2", status = "internal_diagnostic",
            purpose = "internal_regression_only", parent_manifest_sha256 = parentHash, draft_manifest_sha256 = draftHash,
            source_sha256 = sourceHash, hierarchy_report_sha256 = HashFile(hierarchyPath), phase_0_gate_contribution = 0,
            not_admissible_as_blind_eval = true, source_row_count = variantCount, semantic_family_count = families.Count,
            human_verified_family_count = humanVerified, gaps_need_human_triage = true, supplementation_authorized = false,
            files
        };
        var manifestPathOut = Path.Combine(output, "manifest.json");
        ImmutableArtifactWriter.WriteNew(manifestPathOut, JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine);
        files["manifest.json"] = HashFile(manifestPathOut);
        return new(output, variantCount, families.Count, 0, files);
    }

    private static Dictionary<string, Family> ReadFamilies(string path)
    {
        var result = new Dictionary<string, Family>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement.Clone();
            var id = String(root, "family_id");
            if (id.Length == 0 || !result.TryAdd(id, new(id, String(root, "split"), []))) throw new InvalidDataException("cases.jsonl 的 family_id 缺失或重复。");
        }
        return result;
    }

    private static Dictionary<string, JsonElement> ReadSource(string path)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement.Clone();
            var id = String(root, "id");
            if (id.Length > 0 && !result.TryAdd(id, root)) throw new InvalidDataException("来源 canonical 中存在重复 id。");
        }
        return result;
    }

    private static int MapVariants(string variantsPath, Dictionary<string, Family> families, Dictionary<string, JsonElement> source)
    {
        var count = 0;
        var seenVariantIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(variantsPath, Encoding.UTF8))
        {
            using var document = JsonDocument.Parse(line);
            var variant = document.RootElement;
            var variantId = String(variant, "id");
            var familyId = String(variant, "family_id");
            var sourceId = String(variant, "source_record_id");
            if (variantId.Length == 0 || !seenVariantIds.Add(variantId)) throw new InvalidDataException("variants.jsonl 存在空或重复 variant id。");
            if (!families.TryGetValue(familyId, out var family) || !source.TryGetValue(sourceId, out var sourceRecord))
                throw new InvalidDataException($"变体 {variantId} 无法映射到冻结族或来源记录。");
            if (String(variant, "split") != family.Split)
                throw new InvalidDataException($"变体 {variantId} 的族 split 与 cases.jsonl 不一致。");
            if (String(sourceRecord, "split") is { Length: > 0 } sourceSplit && sourceSplit != String(variant, "legacy_split"))
                throw new InvalidDataException($"变体 {variantId} 的来源 split 与原始记录不一致。");
            family.Rows.Add(sourceRecord);
            count++;
        }
        if (count == 0 || families.Values.Any(family => family.Rows.Count == 0)) throw new InvalidDataException("存在无来源变体的族或空变体集合。");
        return count;
    }

    private static Dictionary<string, JsonElement> ReadLabels(string path)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            using var document = JsonDocument.Parse(line);
            var label = document.RootElement.Clone();
            var familyId = String(label, "family_id");
            if (familyId.Length == 0 || !result.TryAdd(familyId, label)) throw new InvalidDataException("草稿 labels.jsonl 中 family_id 缺失或重复。");
        }
        return result;
    }

    private static PolishRegressionCoverageDimension Dimension(string name, string basis, Dictionary<string, Family> families,
        Func<JsonElement, string> selector)
    {
        var sets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var rowCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var family in families.Values)
        foreach (var row in family.Rows)
        {
            var value = selector(row);
            if (value.Length == 0) continue;
            rowCounts[value] = rowCounts.GetValueOrDefault(value) + 1;
            if (!sets.TryGetValue(value, out var ids)) sets[value] = ids = new(StringComparer.Ordinal);
            ids.Add(family.Id);
        }
        var familiesWithValue = sets.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value.Count, StringComparer.Ordinal);
        return new(name, basis, rowCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), familiesWithValue);
    }

    private static PolishRegressionCoverageDimension DraftDimension(string name, string basis,
        Dictionary<string, JsonElement> labels, string fieldName)
    {
        var sets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var label in labels)
        {
            var field = Property(Property(label.Value, "fields"), fieldName);
            var value = Property(field, "value");
            var category = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Null or JsonValueKind.Undefined => "unlabeled",
                _ => value.ToString()
            };
            if (category.Length == 0) category = "unlabeled";
            if (!sets.TryGetValue(category, out var ids)) sets[category] = ids = new(StringComparer.Ordinal);
            ids.Add(label.Key);
        }
        return new(name, basis, null, sets.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value.Count, StringComparer.Ordinal));
    }

    private static string ClaimPresence(JsonElement row, string property)
    {
        var claims = Property(row, "claims");
        return claims.ValueKind == JsonValueKind.Array && claims.EnumerateArray().Any(claim => String(claim, property).Length > 0) ? "yes" : "no";
    }

    private static string ClaimBooleanPresence(JsonElement row, string property)
    {
        var claims = Property(row, "claims");
        return claims.ValueKind == JsonValueKind.Array && claims.EnumerateArray().Any(claim => Boolean(claim, property)) ? "yes" : "no";
    }

    private static int CountFamiliesWithSourcePredicate(Dictionary<string, Family> families, Func<JsonElement, bool> predicate) =>
        families.Values.Count(family => family.Rows.Any(predicate));

    private static int CountFamiliesWithClaimPredicate(Dictionary<string, Family> families, Func<JsonElement, bool> predicate) =>
        families.Values.Count(family => family.Rows.Any(row => Property(row, "claims").ValueKind == JsonValueKind.Array &&
            Property(row, "claims").EnumerateArray().Any(predicate)));

    private static PolishRegressionCoverageGap Gap(string behavior, int? count, string evidenceStatus, string basis) =>
        new(behavior, count, evidenceStatus, basis, "pending_human_triage", "not_started");

    private static JsonElement Property(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value : default;
    private static string String(JsonElement root, string name) => Property(root, name).ValueKind == JsonValueKind.String ? Property(root, name).GetString() ?? "" : "";
    private static int Integer(JsonElement root, string name) => Property(root, name).ValueKind == JsonValueKind.Number && Property(root, name).TryGetInt32(out var value) ? value : -1;
    private static bool Boolean(JsonElement root, string name) => Property(root, name).ValueKind == JsonValueKind.True;
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
