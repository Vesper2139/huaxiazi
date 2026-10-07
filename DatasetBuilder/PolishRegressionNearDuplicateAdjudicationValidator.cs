using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Huaxiazi.DatasetBuilder;

public sealed record PolishRegressionNearDuplicateAdjudicationIssue(string Code, string PairId, string Message);
public sealed record PolishRegressionNearDuplicateAdjudicationReport(bool Valid, int CandidateCount, int AdjudicatedCount,
    bool ReviewerIdentityVerified, IReadOnlyList<PolishRegressionNearDuplicateAdjudicationIssue> Issues);

/// <summary>Checks completeness and integrity of human near-duplicate decisions without judging their semantic correctness or identity.</summary>
public static class PolishRegressionNearDuplicateAdjudicationValidator
{
    private sealed record Candidate(string PairId, string LeftCaseId, string RightCaseId, string LeftFamilyId, string RightFamilyId);
    private static readonly HashSet<string> Decisions = new(StringComparer.Ordinal)
    {
        "same_semantic_family", "distinct_task_intent_same_backbone", "distinct_semantic_families", "uncertain_request_arbitration"
    };

    public static PolishRegressionNearDuplicateAdjudicationReport Validate(string reportDirectory, string decisionsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(decisionsPath);
        var root = Path.GetFullPath(reportDirectory);
        var issues = new List<PolishRegressionNearDuplicateAdjudicationIssue>();
        void Add(string code, string pairId, string message) => issues.Add(new(code, pairId, message));

        var manifestPath = Path.Combine(root, "manifest.json");
        using var manifestDocument = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var manifest = manifestDocument.RootElement;
        if (String(manifest, "purpose") != "internal_near_duplicate_triage" || Integer(manifest, "phase_0_gate_contribution") != 0 ||
            !Boolean(manifest, "not_admissible_as_blind_eval") || Boolean(manifest, "auto_merge_performed"))
            Add("boundary", "", "候选清单必须保持内部诊断、阶段 0 零贡献、禁止盲评和不自动合族边界。");

        var fileHashes = Property(manifest, "files");
        foreach (var name in new[] { "candidate-pairs.jsonl", "human-adjudication.template.jsonl", "report.json" })
        {
            var path = Path.Combine(root, name);
            if (!File.Exists(path) || String(fileHashes, name) != HashFile(path))
                Add("artifact-hash", "", $"候选工件 {name} 缺失或哈希与 manifest 不一致。");
        }

        var candidates = ReadCandidates(Path.Combine(root, "candidate-pairs.jsonl"));
        var candidateById = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.PairId) || !candidateById.TryAdd(candidate.PairId, candidate))
                Add("candidate-id", candidate.PairId, "候选 pair_id 缺失或重复。");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var adjudicatedCount = 0;
        var lineNumber = 0;
        foreach (var line in File.ReadLines(decisionsPath, Encoding.UTF8))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) { Add("decision-fields", "", $"裁定文件第 {lineNumber} 行为空。"); continue; }
            using var document = JsonDocument.Parse(line);
            var item = document.RootElement;
            var pairId = String(item, "pair_id");
            if (!candidateById.TryGetValue(pairId, out var candidate)) { Add("unknown-pair", pairId, "裁定引用了候选清单中不存在的 pair_id。"); continue; }
            if (!seen.Add(pairId)) { Add("duplicate-decision", pairId, "同一候选 pair_id 被裁定多次。"); continue; }

            var fieldsValid = true;
            var rationale = String(item, "rationale");
            if (String(item, "status") != "adjudicated" || string.IsNullOrWhiteSpace(String(item, "reviewer_id")) ||
                string.IsNullOrWhiteSpace(rationale))
            {
                Add("decision-fields", pairId, "完成裁定必须填写 adjudicated 状态、reviewer_id 和 rationale。");
                fieldsValid = false;
            }
            else if (!ReviewRationaleRules.IsSubstantive(rationale))
            {
                Add("decision-rationale", pairId, "rationale 不能是占位语，必须提供基于候选证据的具体理由。");
                fieldsValid = false;
            }
            if (!Decisions.Contains(String(item, "decision")))
            {
                Add("decision-value", pairId, "decision 必须属于受支持的人工语义裁定枚举。");
                fieldsValid = false;
            }
            if (!IsUtcTimestamp(String(item, "reviewed_at_utc")))
            {
                Add("review-time", pairId, "reviewed_at_utc 必须是以 Z 结尾的 UTC 时间戳。");
                fieldsValid = false;
            }
            if (String(item, "left_case_id") != candidate.LeftCaseId || String(item, "right_case_id") != candidate.RightCaseId ||
                String(item, "left_family_id") != candidate.LeftFamilyId || String(item, "right_family_id") != candidate.RightFamilyId)
                {
                    Add("pair-mismatch", pairId, "裁定行的 case/family 引用与候选工件不匹配。");
                    fieldsValid = false;
                }
            if (fieldsValid) adjudicatedCount++;
        }

        foreach (var candidate in candidates)
            if (!seen.Contains(candidate.PairId)) Add("missing-decision", candidate.PairId, "候选 pair 尚无人工裁定记录。");

        using var reportDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "report.json")));
        if (Integer(reportDocument.RootElement, "candidate_pair_count") != candidates.Count)
            Add("candidate-count", "", "候选报告数量与 candidate-pairs.jsonl 不一致。");
        return new(issues.Count == 0, candidates.Count, adjudicatedCount, false, issues);
    }

    private static List<Candidate> ReadCandidates(string path)
    {
        var result = new List<Candidate>();
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) throw new InvalidDataException("candidate-pairs.jsonl 含空行。");
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            result.Add(new(String(root, "pair_id"), String(root, "left_case_id"), String(root, "right_case_id"),
                String(root, "left_family_id"), String(root, "right_family_id")));
        }
        return result;
    }

    private static bool IsUtcTimestamp(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) &&
        parsed.Offset == TimeSpan.Zero && value.EndsWith('Z');

    private static JsonElement Property(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value : default;
    private static string String(JsonElement root, string name) => Property(root, name).ValueKind == JsonValueKind.String ? Property(root, name).GetString() ?? "" : "";
    private static int Integer(JsonElement root, string name) => Property(root, name).TryGetInt32(out var value) ? value : 0;
    private static bool Boolean(JsonElement root, string name) => Property(root, name).ValueKind == JsonValueKind.True;
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
