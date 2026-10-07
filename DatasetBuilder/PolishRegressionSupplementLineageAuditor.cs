using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Huaxiazi.DatasetBuilder;

public sealed record PolishRegressionSupplementLineageIssue(string Code, string RecordId, string Message);
public sealed record PolishRegressionSupplementLineageReport(bool Valid, int CaseCount, int MissingLineageCaseCount,
    int Phase0GateContribution, bool NotAdmissibleAsBlindEval, IReadOnlyList<PolishRegressionSupplementLineageIssue> Issues);

/// <summary>Checks W4.4 per-case generation metadata while preserving the internal-only boundary.</summary>
public static class PolishRegressionSupplementLineageAuditor
{
    private static readonly Regex Sha256 = new("^[0-9a-f]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static PolishRegressionSupplementLineageReport Validate(string casesPath, string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(casesPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        if (!File.Exists(casesPath)) throw new FileNotFoundException("补样案例文件不存在。", casesPath);
        if (!File.Exists(manifestPath)) throw new FileNotFoundException("补样 manifest 不存在。", manifestPath);

        var issues = new List<PolishRegressionSupplementLineageIssue>();
        void Add(string code, string id, string message) => issues.Add(new(code, id, message));
        using var manifestDocument = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var manifest = manifestDocument.RootElement;
        var contribution = Integer(manifest, "phase_0_gate_contribution");
        var blindForbidden = Boolean(manifest, "not_admissible_as_blind_eval");
        if (String(manifest, "dataset_version") != "hxz-polish-regression-ai-supplement-draft-v1" ||
            String(manifest, "status") != "draft" || String(manifest, "purpose") != "internal_regression_only" ||
            contribution != 0 || !blindForbidden)
            Add("manifest-boundary", "", "补样必须保持内部草稿状态、阶段 0 零贡献并禁止作为盲评来源。");
        if (String(Property(manifest, "files"), "cases.jsonl") != HashFile(casesPath))
            Add("cases-hash-mismatch", "", "cases.jsonl 哈希与 manifest 不匹配。");

        var count = 0;
        var missing = 0;
        foreach (var line in File.ReadLines(casesPath, Encoding.UTF8))
        {
            count++;
            string id = "";
            try
            {
                using var document = JsonDocument.Parse(line);
                var row = document.RootElement;
                id = String(row, "id");
                if (String(row, "origin") != "ai_assisted_draft")
                {
                    Add("origin-invalid", id, "补样来源必须标记为 ai_assisted_draft。");
                    continue;
                }
                var lineage = Property(row, "generation_lineage");
                var model = String(lineage, "model_id");
                var promptHash = String(lineage, "prompt_sha256");
                var seed = Property(lineage, "random_seed");
                var generatedAt = String(lineage, "generated_at_utc");
                var operatorId = String(lineage, "operator");
                var absent = lineage.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(model) ||
                    !Sha256.IsMatch(promptHash) || seed.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ||
                    (seed.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(seed.GetString())) ||
                    !StrictUtcTimestamp.TryParse(generatedAt, out _) || string.IsNullOrWhiteSpace(operatorId);
                if (absent)
                {
                    missing++;
                    Add("generation-lineage-missing", id, "缺少有效的模型 ID、提示 SHA-256、随机种子、UTC 生成时间或操作者。");
                }
            }
            catch (JsonException)
            {
                Add("case-json-invalid", id, $"案例第 {count} 行不是有效 JSON。");
            }
        }
        if (count == 0) Add("cases-empty", "", "补样案例文件为空。");
        return new(issues.Count == 0, count, missing, contribution, blindForbidden, issues);
    }

    private static string HashFile(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static JsonElement Property(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;
    private static string String(JsonElement element, string name)
    {
        var value = Property(element, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    }
    private static int Integer(JsonElement element, string name) => Property(element, name).TryGetInt32(out var value) ? value : -1;
    private static bool Boolean(JsonElement element, string name) => Property(element, name).ValueKind == JsonValueKind.True;
}
