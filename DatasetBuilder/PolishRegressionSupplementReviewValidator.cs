using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;

namespace Huaxiazi.DatasetBuilder;

public sealed record PolishRegressionSupplementReviewIssue(string Code, string CaseId, string Message);
public sealed record PolishRegressionSupplementReviewReport(bool Valid, int TotalCases, int ReviewedCount,
    int AcceptedCount, int EditedCount, int RejectedCount, bool ReviewerIdentityVerified, int Phase0GateContribution,
    IReadOnlyList<PolishRegressionSupplementReviewIssue> Issues);

/// <summary>Validates human dispositions for AI-authored internal regression drafts. It never promotes or rewrites cases.</summary>
public static class PolishRegressionSupplementReviewValidator
{
    private static readonly string[] Headers =
    [
        "case_id", "family_id", "split", "gap_behavior", "scenario", "purpose", "formality",
        "expected_decision_draft", "input", "constraints", "claims", "reference_output_draft",
        "human_verdict", "human_expected_decision", "human_edited_reference_output", "fact_preservation",
        "constraint_following", "format_and_tone", "reviewer_id", "reviewed_at_utc", "rationale"
    ];
    private static readonly HashSet<string> Decisions = ["polish", "clarify", "refuse"];
    private static readonly HashSet<string> Verdicts = ["accept", "edit", "reject"];
    private static readonly HashSet<string> Ratings = ["pass", "fail", "na"];
    private static readonly HashSet<string> AutomatedReviewerIds = ["ai", "ai assistant", "assistant", "自动生成"];

    public static PolishRegressionSupplementReviewReport Validate(string casesPath, string reviewCsvPath, string reviewManifestPath)
    {
        foreach (var value in new[] { casesPath, reviewCsvPath, reviewManifestPath })
            ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var issues = new List<PolishRegressionSupplementReviewIssue>();
        void Add(string code, string caseId, string message) => issues.Add(new(code, caseId, message));

        var caseBytes = File.ReadAllBytes(casesPath);
        using var manifestDocument = JsonDocument.Parse(File.ReadAllBytes(reviewManifestPath));
        var manifest = manifestDocument.RootElement;
        var phase0Contribution = Integer(manifest, "phase_0_gate_contribution");
        if (String(manifest, "package") != "hxz-polish-regression-ai-supplement-review-2026-10-05" ||
            String(manifest, "source_dataset") != "datasets/polish-regression-ai-supplement-draft-v1" ||
            phase0Contribution != 0 || !Boolean(manifest, "not_admissible_as_blind_eval"))
            Add("manifest-boundary", "", "审阅包来源或阶段 0 零贡献边界不匹配。");
        if (String(manifest, "source_cases_sha256") != Hash(caseBytes))
            Add("source-hash", "", "审阅包绑定的源样本哈希与当前 cases.jsonl 不一致。");
        var readmePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reviewManifestPath))!, "README.md");
        var files = Property(manifest, "files");
        if (!File.Exists(readmePath) || String(files, "README.md") != Hash(File.Exists(readmePath) ? File.ReadAllBytes(readmePath) : []))
            Add("manifest-artifact-hash", "", "审阅包 README 缺失或哈希与 manifest 不一致。");

        var cases = ReadCases(caseBytes, Add);
        if (Integer(manifest, "rows") != cases.Count)
            Add("manifest-count", "", "审阅包 rows 计数与源样本数不一致。");

        var csvRows = ReadCsv(reviewCsvPath, Add);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var reviewed = 0;
        var accepted = 0;
        var edited = 0;
        var rejected = 0;
        foreach (var (rowNumber, values) in csvRows)
        {
            string Field(string name) => values.TryGetValue(name, out var value) ? value : "";
            var caseId = Field("case_id").Trim();
            if (caseId.Length == 0 || !cases.TryGetValue(caseId, out var source))
            {
                Add("unknown-case", caseId, $"CSV 第 {rowNumber} 行 case_id 为空或不属于源样本集。");
                continue;
            }
            if (!seen.Add(caseId))
            {
                Add("duplicate-case", caseId, "同一案例在审阅 CSV 中出现多次。");
                continue;
            }

            ValidateSourceBinding(values, source, caseId, rowNumber, Add);
            var verdict = Field("human_verdict").Trim();
            var expectedDecision = Field("human_expected_decision").Trim();
            var editedOutput = Field("human_edited_reference_output");
            var reviewer = Field("reviewer_id").Trim();
            var rationale = Field("rationale");
            var rowValid = true;
            if (!Verdicts.Contains(verdict))
            {
                Add("review-verdict", caseId, $"CSV 第 {rowNumber} 行 human_verdict 必须为 accept、edit 或 reject。");
                rowValid = false;
            }
            else if (verdict == "accept") accepted++;
            else if (verdict == "edit") edited++;
            else rejected++;

            if (verdict is "accept" or "edit")
            {
                if (!Decisions.Contains(expectedDecision))
                {
                    Add("review-decision", caseId, "接受或修改的案例必须提供有效的最终 expected_decision。");
                    rowValid = false;
                }
                if (verdict == "accept" && (expectedDecision != String(source, "expected_decision") || editedOutput.Length > 0))
                {
                    Add("accept-mismatch", caseId, "accept 必须保留草稿决策且不得填写修订成稿；有改动时请标为 edit。");
                    rowValid = false;
                }
                if (verdict == "edit" && string.IsNullOrWhiteSpace(editedOutput))
                {
                    Add("edit-output", caseId, "edit 必须填写完整的 human_edited_reference_output。");
                    rowValid = false;
                }
            }
            else if (expectedDecision.Length > 0 || editedOutput.Length > 0)
            {
                Add("reject-content", caseId, "reject 不应携带待合并的最终决策或成稿；请将具体建议写入理由。");
                rowValid = false;
            }

            var factRating = Field("fact_preservation").Trim();
            var constraintRating = Field("constraint_following").Trim();
            var styleRating = Field("format_and_tone").Trim();
            if (!Ratings.Contains(factRating) || !Ratings.Contains(constraintRating) || !Ratings.Contains(styleRating))
            {
                Add("review-ratings", caseId, "事实保留、约束遵循、格式与语气必须分别填写 pass、fail 或 na。");
                rowValid = false;
            }
            else if (verdict == "accept" && new[] { factRating, constraintRating, styleRating }.Contains("fail", StringComparer.Ordinal))
            {
                Add("accept-quality", caseId, "accept 与至少一个 fail 评价冲突；请修改样本或拒绝该样本。");
                rowValid = false;
            }
            if (reviewer.Length == 0 || AutomatedReviewerIds.Contains(reviewer))
            {
                Add("reviewer", caseId, "必须填写人工审阅者标识，不能使用 AI/自动生成身份。");
                rowValid = false;
            }
            if (!StrictUtcTimestamp.TryParse(Field("reviewed_at_utc").Trim(), out _))
            {
                Add("review-time", caseId, "reviewed_at_utc 必须为带 Z 或 +00:00 的严格 UTC 时间戳。");
                rowValid = false;
            }
            if (!ReviewRationaleRules.IsSubstantive(rationale))
            {
                Add("review-rationale", caseId, "rationale 必须是具体理由，不能留空或使用占位语。");
                rowValid = false;
            }

            var outputForPrivacyScan = verdict == "edit" ? editedOutput : String(source, "reference_output");
            if (BlindEvaluationAuditor.ContainsPotentialSensitiveData(String(source, "input")) ||
                BlindEvaluationAuditor.ContainsPotentialSensitiveData(outputForPrivacyScan) ||
                BlindEvaluationAuditor.ContainsPotentialSensitiveData(JsonText(Property(source, "context"))))
            {
                Add("sensitive-content", caseId, "样本或审阅后的成稿命中隐私/密钥启发式扫描。");
                rowValid = false;
            }
            if (rowValid) reviewed++;
        }

        foreach (var id in cases.Keys.Order(StringComparer.Ordinal))
            if (!seen.Contains(id)) Add("missing-review", id, "源样本没有对应的人工审阅行。");

        return new(issues.Count == 0 && cases.Count > 0, cases.Count, reviewed, accepted, edited, rejected, false, 0, issues);
    }

    internal static Dictionary<string, JsonElement> ReadCases(byte[] bytes, Action<string, string, string> add)
    {
        var cases = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var families = new HashSet<string>(StringComparer.Ordinal);
        using var reader = new MemoryStream(bytes);
        using var text = new StreamReader(reader, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lineNumber = 0;
        while (text.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) { add("case-jsonl", "", $"cases.jsonl 第 {lineNumber} 行为空。"); continue; }
            try
            {
                using var document = JsonDocument.Parse(line);
                var item = document.RootElement.Clone();
                var id = String(item, "id");
                var familyId = String(item, "family_id");
                if (String(item, "schema_version") != "1.0" || String(item, "task") != "polish" ||
                    String(item, "origin") != "ai_assisted_draft" || String(item, "human_status") != "unreviewed" ||
                    !Decisions.Contains(String(item, "expected_decision")) || string.IsNullOrWhiteSpace(String(item, "input")) ||
                    Property(item, "reference_output").ValueKind is not (JsonValueKind.String or JsonValueKind.Null) ||
                    !StrictUtcShapeSha256(String(Property(item, "provenance"), "source_sha256")))
                    add("case-contract", id, "样本不符合 v1 AI 草稿的任务、来源、未审状态或必要字段约定。");
                if (!Regex.IsMatch(id, "^hxz-polish-reg-[a-z0-9-]+$", RegexOptions.CultureInvariant) ||
                    !Regex.IsMatch(familyId, "^hxz-polish-reg-family-[a-f0-9]{16}$", RegexOptions.CultureInvariant))
                    add("case-id", id, "case id 或 family_id 不符合 v1 标识格式。");
                if (id.Length == 0 || !cases.TryAdd(id, item)) add("case-id", id, $"cases.jsonl 第 {lineNumber} 行 id 缺失或重复。");
                if (familyId.Length == 0 || !families.Add(familyId)) add("family-id", id, "family_id 缺失或重复；回归 split 必须按族隔离。");
                if (String(item, "split") is not ("development" or "regression")) add("case-split", id, "split 必须为 development 或 regression。");
            }
            catch (JsonException) { add("case-jsonl", "", $"cases.jsonl 第 {lineNumber} 行 JSON 无效。"); }
        }
        return cases;
    }

    internal static List<(int Row, Dictionary<string, string> Values)> ReadCsv(string path,
        Action<string, string, string> add)
    {
        var result = new List<(int, Dictionary<string, string>)>();
        using var parser = new TextFieldParser(path, Encoding.UTF8)
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false,
            Delimiters = [","]
        };
        string[]? header;
        try { header = parser.ReadFields(); }
        catch (MalformedLineException) { add("csv-header", "", "CSV 表头格式无效。"); return result; }
        if (header is null || !header.SequenceEqual(Headers, StringComparer.Ordinal))
        {
            add("csv-header", "", "CSV 表头必须与当前版本的审阅模板完全一致。");
            return result;
        }

        var row = 1;
        while (!parser.EndOfData)
        {
            row++;
            string[]? fields;
            try { fields = parser.ReadFields(); }
            catch (MalformedLineException) { add("csv-row", "", $"CSV 第 {row} 条记录格式无效。"); break; }
            if (fields is null || fields.Length != Headers.Length)
            {
                add("csv-row", "", $"CSV 第 {row} 条记录的字段数不正确。");
                continue;
            }
            result.Add((row, Headers.Select((name, index) => (name, fields[index]))
                .ToDictionary(pair => pair.name, pair => pair.Item2, StringComparer.Ordinal)));
        }
        return result;
    }

    private static void ValidateSourceBinding(Dictionary<string, string> fields, JsonElement source, string caseId,
        int row, Action<string, string, string> add)
    {
        var context = Property(source, "context");
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["family_id"] = String(source, "family_id"), ["split"] = String(source, "split"),
            ["gap_behavior"] = String(context, "gap_behavior"), ["scenario"] = String(context, "scenario"),
            ["purpose"] = String(context, "purpose"), ["formality"] = String(context, "formality"),
            ["expected_decision_draft"] = String(source, "expected_decision"), ["input"] = String(source, "input"),
            ["constraints"] = string.Join('；', Array(Property(source, "constraints")).Select(item => item.GetString() ?? "")),
            ["reference_output_draft"] = String(source, "reference_output")
        };
        foreach (var (field, value) in expected)
            if (fields[field] != value) add("source-row-mismatch", caseId, $"CSV 第 {row} 行 {field} 与源样本不一致。");

        try
        {
            var csvClaims = JsonNode.Parse(string.IsNullOrWhiteSpace(fields["claims"]) ? "[]" : fields["claims"]);
            var sourceClaims = JsonNode.Parse(JsonText(Property(source, "claims"), "[]"));
            if (!JsonNode.DeepEquals(csvClaims, sourceClaims)) add("source-row-mismatch", caseId, $"CSV 第 {row} 行 claims 与源样本不一致。");
        }
        catch (JsonException) { add("source-row-mismatch", caseId, $"CSV 第 {row} 行 claims 不是合法 JSON。"); }
    }

    private static IEnumerable<JsonElement> Array(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [];
    private static string JsonText(JsonElement value, string fallback = "") =>
        value.ValueKind == JsonValueKind.Undefined ? fallback : value.GetRawText();
    private static bool StrictUtcShapeSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static JsonElement Property(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value : default;
    private static string String(JsonElement root, string name) =>
        Property(root, name).ValueKind == JsonValueKind.String ? Property(root, name).GetString() ?? "" : "";
    private static int Integer(JsonElement root, string name) =>
        Property(root, name).ValueKind == JsonValueKind.Number && Property(root, name).TryGetInt32(out var value) ? value : -1;
    private static bool Boolean(JsonElement root, string name) => Property(root, name).ValueKind == JsonValueKind.True;
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
