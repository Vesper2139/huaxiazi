using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Huaxiazi.DatasetBuilder;

public sealed record PolishRegressionHumanReviewValidationReport(bool Valid, int ExpectedFamilyCount,
    int IndependentFirstPassCount, int CompletedDecisionCount, int ApprovalCandidateCount,
    int Phase0GateContribution, IReadOnlyList<string> Issues);

/// <summary>Checks human review coverage and independence. It never promotes labels or creates gold data.</summary>
public static class PolishRegressionHumanReviewValidator
{
    private static readonly HashSet<string> FieldOutcomes = ["accept", "revise", "reject", "uncertain"];
    private static readonly HashSet<string> OverallOutcomes = ["accept", "revise", "reject", "unresolved"];
    private static readonly HashSet<string> BackboneOutcomes = ["confirm", "split", "merge", "unresolved"];
    private static readonly HashSet<string> ReuseOutcomes = ["acceptable", "requires_distinct_reference", "exclude_family_from_scoring", "unresolved"];

    public static PolishRegressionHumanReviewValidationReport Validate(string packetDirectory, string completedFirstPassPath,
        string completedDecisionsPath, string draftLabelsPath, string draftManifestPath)
    {
        foreach (var value in new[] { packetDirectory, completedFirstPassPath, completedDecisionsPath, draftLabelsPath, draftManifestPath })
            ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var issues = new List<string>();
        var packetRoot = Path.GetFullPath(packetDirectory);
        var packetManifestPath = Path.Combine(packetRoot, "manifest.json");
        using var packetDocument = JsonDocument.Parse(File.ReadAllText(packetManifestPath));
        using var draftManifestDocument = JsonDocument.Parse(File.ReadAllText(draftManifestPath));
        var packet = packetDocument.RootElement;
        var draftManifest = draftManifestDocument.RootElement;
        var expectedCount = Integer(Property(packet, "counts"), "review_items");

        if (String(packet, "packet_version") != "hxz-polish-human-review-v1" || String(packet, "status") != "awaiting_human_review")
            issues.Add("审查包版本或状态不正确。");
        if (Integer(packet, "phase_0_gate_contribution") != 0 || !Boolean(packet, "not_admissible_as_blind_eval"))
            issues.Add("审查包未保持阶段 0 零贡献和禁止盲评边界。");
        if (HashFile(draftManifestPath) != String(packet, "draft_manifest_sha256"))
            issues.Add("草稿 manifest 哈希与审查包记录不一致。");
        if (String(draftManifest, "dataset_version") != "hxz-polish-regression-v2-draft" ||
            Integer(draftManifest, "phase_0_gate_contribution") != 0 || !Boolean(draftManifest, "not_admissible_as_blind_eval"))
            issues.Add("草稿 manifest 不是受限的内部 v2 草稿。");
        if (HashFile(draftLabelsPath) != String(Property(draftManifest, "files"), "labels.jsonl"))
            issues.Add("草稿 labels.jsonl 哈希不匹配 draft manifest。");

        VerifyPacketHash(packetRoot, packet, "source-first-pass.jsonl", issues);
        VerifyPacketHash(packetRoot, packet, "draft-comparison.jsonl", issues);
        VerifyPacketHash(packetRoot, packet, "human-decisions.template.jsonl", issues);

        var sourceItems = ReadByFamily(Path.Combine(packetRoot, "source-first-pass.jsonl"), "review_item_id", issues);
        var comparisonItems = ReadByFamily(Path.Combine(packetRoot, "draft-comparison.jsonl"), "review_item_id", issues);
        var draftItems = ReadByFamily(draftLabelsPath, null, issues);
        var firstPassItems = ReadByFamily(completedFirstPassPath, "review_item_id", issues);
        var decisionItems = ReadByFamily(completedDecisionsPath, "review_item_id", issues);

        var expectedFamilies = sourceItems.Keys.ToHashSet(StringComparer.Ordinal);
        if (expectedCount < 0 || expectedFamilies.Count != expectedCount)
            issues.Add("审查包中的族数与清单计数不一致。");
        CheckExactFamilies("草稿对照", expectedFamilies, comparisonItems.Keys, issues);
        CheckExactFamilies("草稿标签", expectedFamilies, draftItems.Keys, issues);
        CheckExactFamilies("已完成首轮审阅", expectedFamilies, firstPassItems.Keys, issues);
        CheckExactFamilies("已完成裁定", expectedFamilies, decisionItems.Keys, issues);

        var firstPassCount = 0;
        var decisionCount = 0;
        var approvalCandidateCount = 0;
        foreach (var familyId in expectedFamilies.Order(StringComparer.Ordinal))
        {
            if (!sourceItems.TryGetValue(familyId, out var expectedSource) ||
                !comparisonItems.TryGetValue(familyId, out var comparison) ||
                !draftItems.TryGetValue(familyId, out var draftLabel) ||
                !firstPassItems.TryGetValue(familyId, out var firstPass) ||
                !decisionItems.TryGetValue(familyId, out var decision)) continue;

            var expectedReviewId = String(expectedSource, "review_item_id");
            if (String(firstPass, "review_item_id") != expectedReviewId || String(decision, "review_item_id") != expectedReviewId)
                issues.Add($"族 {familyId} 的审阅项 ID 与审查包不匹配。");
            foreach (var key in new[] { "case_id", "split", "source_record_id", "source_record_sha256", "source_dataset_origin", "task", "input", "context", "claims", "expected_decision", "reference_output", "legacy_source_metadata" })
                if (!JsonEquals(Property(expectedSource, key), Property(firstPass, key)))
                    issues.Add($"族 {familyId} 的首轮文件改动了受保护的来源字段 {key}。");

            var expectedFirstPassFields = Property(Property(expectedSource, "independent_first_pass"), "proposed_fields");
            var firstPassReview = Property(firstPass, "independent_first_pass");
            var proposedFields = Property(firstPassReview, "proposed_fields");
            var fieldNames = expectedFirstPassFields.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
            if (proposedFields.ValueKind != JsonValueKind.Object ||
                !fieldNames.SetEquals(proposedFields.EnumerateObject().Select(property => property.Name)))
                issues.Add($"族 {familyId} 首轮独立标注字段不完整或包含额外字段。");
            else if (proposedFields.EnumerateObject().Any(property => property.Value.ValueKind == JsonValueKind.Null))
                issues.Add($"族 {familyId} 首轮独立标注仍有空值。");
            var firstReviewer = String(firstPassReview, "reviewer_id");
            if (firstReviewer.Length == 0 || !StrictUtcTimestamp.TryParse(String(firstPassReview, "reviewed_at_utc"), out _)
                || String(firstPassReview, "rationale").Length == 0)
                issues.Add($"族 {familyId} 首轮审阅者、UTC 时间或理由缺失/无效。");
            else firstPassCount++;

            var fields = Property(draftLabel, "fields");
            var draftByValues = fields.EnumerateObject()
                .Where(property => String(Property(property.Value, "provenance"), "kind") == "ai_draft")
                .Select(property => String(Property(property.Value, "provenance"), "draft_by"))
                .Where(author => author.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var comparisonAuthors = Property(comparison, "draft_authors").ValueKind == JsonValueKind.Array
                ? Property(comparison, "draft_authors").EnumerateArray().Select(item => item.GetString() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!draftByValues.SetEquals(comparisonAuthors)) issues.Add($"族 {familyId} 的对照页作者信息与草稿字段血缘不一致。");
            if (firstReviewer.Length > 0 && draftByValues.Contains(firstReviewer))
                issues.Add($"族 {familyId} 的首轮审阅者与 AI 草稿作者相同。");

            var reviewer = String(decision, "reviewer_id");
            var independent = BooleanValue(Property(decision, "reviewer_is_independent_of_draft_author"));
            if (reviewer.Length == 0 || !StrictUtcTimestamp.TryParse(String(decision, "reviewed_at_utc"), out _))
                issues.Add($"族 {familyId} 裁定审阅者或 UTC 时间缺失/无效。");
            else if (draftByValues.Contains(reviewer))
                issues.Add($"族 {familyId} 的裁定者与 AI 草稿作者相同。");
            if (!BooleanValue(Property(decision, "reviewer_is_independent_of_draft_author")))
                issues.Add($"族 {familyId} 未确认审阅者独立于草稿作者。");

            var overall = String(decision, "overall_decision");
            var overallRationale = String(decision, "rationale");
            if (!OverallOutcomes.Contains(overall) || overallRationale.Length == 0)
                issues.Add($"族 {familyId} 整体裁定值或理由无效。");
            var backbone = Property(decision, "backbone_decision");
            var backboneOutcome = String(backbone, "outcome");
            var finalBackboneId = String(backbone, "final_backbone_id");
            if (!BackboneOutcomes.Contains(backboneOutcome) || String(backbone, "rationale").Length == 0)
                issues.Add($"族 {familyId} 骨架裁定值或理由无效。");
            if (backboneOutcome != "unresolved" && finalBackboneId.Length == 0)
                issues.Add($"族 {familyId} 已裁定骨架但缺少最终 backbone_id。");
            if (backboneOutcome == "confirm" && finalBackboneId != String(comparison, "provisional_backbone_id"))
                issues.Add($"族 {familyId} 选择 confirm 但最终 backbone_id 与候选值不同。");
            var reuse = Property(decision, "reference_reuse_decision");
            if (!ReuseOutcomes.Contains(String(reuse, "outcome")) || String(reuse, "rationale").Length == 0)
                issues.Add($"族 {familyId} 参考成稿复用裁定值或理由无效。");

            var fieldDecisions = Property(decision, "field_decisions");
            var expectedDecisionFields = fields.EnumerateObject().Select(property => property.Name)
                .Where(name => name != "reference_output_reuse").ToHashSet(StringComparer.Ordinal);
            if (fieldDecisions.ValueKind != JsonValueKind.Object ||
                !expectedDecisionFields.SetEquals(fieldDecisions.EnumerateObject().Select(property => property.Name)))
                issues.Add($"族 {familyId} 的字段裁定不完整或含多余字段。");
            else
            {
                foreach (var fieldDecision in fieldDecisions.EnumerateObject())
                {
                    var item = fieldDecision.Value;
                    if (!FieldOutcomes.Contains(String(item, "outcome")) ||
                        !item.TryGetProperty("final_value", out _) || String(item, "rationale").Length == 0)
                        issues.Add($"族 {familyId} 字段 {fieldDecision.Name} 缺少有效裁定、最终值或理由。");
                    if (String(item, "outcome") == "accept" &&
                        !JsonEquals(Property(item, "final_value"), Property(Property(fields, fieldDecision.Name), "value")))
                        issues.Add($"族 {familyId} 字段 {fieldDecision.Name} 标记 accept 但最终值与草稿不同。");
                }
            }

            var allAccepted = overall == "accept" && backboneOutcome == "confirm" &&
                String(reuse, "outcome") == "acceptable" && fieldDecisions.ValueKind == JsonValueKind.Object &&
                fieldDecisions.EnumerateObject().All(item => String(item.Value, "outcome") == "accept");
            if (allAccepted && reviewer.Length > 0 && independent && !draftByValues.Contains(reviewer)) approvalCandidateCount++;
            if (reviewer.Length > 0 && StrictUtcTimestamp.TryParse(String(decision, "reviewed_at_utc"), out _)) decisionCount++;
        }

        if (firstPassCount != expectedCount) issues.Add("并非所有族都完成有效的独立首轮审阅。");
        if (decisionCount != expectedCount) issues.Add("并非所有族都完成有效的人工裁定。");
        var valid = issues.Count == 0 && expectedCount > 0;
        return new(valid, Math.Max(0, expectedCount), firstPassCount, decisionCount, approvalCandidateCount, 0, issues);
    }

    private static Dictionary<string, JsonElement> ReadByFamily(string path, string? requiredIdName, List<string> issues)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var lineNumber = 0;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            lineNumber++;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement.Clone();
                var familyId = String(root, "family_id");
                if (familyId.Length == 0 || !result.TryAdd(familyId, root)) issues.Add($"{Path.GetFileName(path)} 第 {lineNumber} 行 family_id 缺失或重复。");
                if (requiredIdName is not null && String(root, requiredIdName).Length == 0)
                    issues.Add($"{Path.GetFileName(path)} 第 {lineNumber} 行 {requiredIdName} 缺失。");
            }
            catch (JsonException exception) { issues.Add($"{Path.GetFileName(path)} 第 {lineNumber} 行 JSON 无效：{exception.Message}"); }
        }
        return result;
    }

    private static void CheckExactFamilies(string label, HashSet<string> expected, IEnumerable<string> actual, List<string> issues)
    {
        var actualSet = actual.ToHashSet(StringComparer.Ordinal);
        foreach (var missing in expected.Except(actualSet, StringComparer.Ordinal)) issues.Add($"{label}缺少族 {missing}。");
        foreach (var extra in actualSet.Except(expected, StringComparer.Ordinal)) issues.Add($"{label}包含未知族 {extra}。");
    }

    private static void VerifyPacketHash(string root, JsonElement manifest, string fileName, List<string> issues)
    {
        var path = Path.Combine(root, fileName);
        var expected = String(Property(manifest, "files"), fileName);
        if (!File.Exists(path) || expected.Length == 0 || HashFile(path) != expected)
            issues.Add($"审查包文件 {fileName} 哈希缺失或不匹配。");
    }

    private static bool JsonEquals(JsonElement left, JsonElement right) => left.ValueKind == right.ValueKind &&
        (left.ValueKind == JsonValueKind.Undefined || string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal));
    private static JsonElement Property(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value : default;
    private static string String(JsonElement root, string name) => Property(root, name).ValueKind == JsonValueKind.String ? Property(root, name).GetString() ?? "" : "";
    private static int Integer(JsonElement root, string name) => Property(root, name).ValueKind == JsonValueKind.Number && Property(root, name).TryGetInt32(out var value) ? value : -1;
    private static bool Boolean(JsonElement root, string name) => BooleanValue(Property(root, name));
    private static bool BooleanValue(JsonElement value) => value.ValueKind == JsonValueKind.True;
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
