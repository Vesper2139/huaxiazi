using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;

namespace Huaxiazi.DatasetBuilder;

public sealed record PolishRegressionSupplementSpecificationReviewIssue(string Code, string SpecificationId, string Message);
public sealed record PolishRegressionSupplementSpecificationReviewReport(bool Valid, int SpecificationCount, int ReviewedCount,
    int ApprovedCount, int EditedCount, int RejectedCount, bool HumanApprovalRecorded, bool GenerationAuthorized,
    bool ReviewerIdentityVerified, int Phase0GateContribution, IReadOnlyList<PolishRegressionSupplementSpecificationReviewIssue> Issues);

/// <summary>Validates human W4.3 specification decisions without editing source specifications or generating samples.</summary>
public static class PolishRegressionSupplementSpecificationReviewValidator
{
    private static readonly string[] Headers =
    [
        "spec_id", "target_behavior", "scenario", "facts_json", "input", "constraints_json", "expected_decision_draft",
        "rubric_anchors_json", "prohibited_content_json", "required_schema_extension", "human_decision",
        "human_edited_spec_json", "reviewer_id", "reviewed_at_utc", "rationale"
    ];
    private static readonly HashSet<string> Decisions = ["approve", "edit", "reject"];
    private static readonly HashSet<string> ExpectedDecisions = ["clarify", "polish"];
    private static readonly HashSet<string> AutomatedReviewers = ["ai", "assistant", "ai assistant", "自动生成"];

    public static PolishRegressionSupplementSpecificationReviewReport Validate(string specsDirectory, string coverageDirectory,
        string parentCasesPath, string parentManifestPath, string packetDirectory)
    {
        foreach (var value in new[] { specsDirectory, coverageDirectory, parentCasesPath, parentManifestPath, packetDirectory }) ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var sourceValidation = PolishRegressionSupplementSpecificationValidator.Validate(specsDirectory, coverageDirectory, parentCasesPath, parentManifestPath);
        var specsPath = Path.Combine(Path.GetFullPath(specsDirectory), "sample-specs.jsonl");
        var sourceManifestPath = Path.Combine(Path.GetFullPath(specsDirectory), "manifest.json");
        var packetRoot = Path.GetFullPath(packetDirectory);
        var packetManifestPath = Path.Combine(packetRoot, "manifest.json");
        var csvPath = Path.Combine(packetRoot, "specification-review.csv");
        var templatePath = Path.Combine(packetRoot, "specification-review.template.csv");
        var issues = new List<PolishRegressionSupplementSpecificationReviewIssue>();
        void Add(string code, string id, string message) => issues.Add(new(code, id, message));
        foreach (var issue in sourceValidation.Issues) Add("source-" + issue.Code, issue.RecordId, issue.Message);
        foreach (var path in new[] { specsPath, sourceManifestPath, packetManifestPath, csvPath, templatePath })
            if (!File.Exists(path)) throw new FileNotFoundException("W4.3 规格审阅来源或审阅包文件缺失。", path);

        using var sourceManifestDoc = JsonDocument.Parse(File.ReadAllBytes(sourceManifestPath));
        using var packetManifestDoc = JsonDocument.Parse(File.ReadAllBytes(packetManifestPath));
        var sourceManifest = sourceManifestDoc.RootElement;
        var packetManifest = packetManifestDoc.RootElement;
        var specsHash = HashFile(specsPath);
        var phase0Contribution = Integer(packetManifest, "phase_0_gate_contribution");
        if (String(sourceManifest, "artifact_version") != "hxz-polish-regression-ai-supplement-specs-v1" ||
            String(sourceManifest, "status") != "ai_draft_pending_human_spec_review" || Integer(sourceManifest, "phase_0_gate_contribution") != 0 ||
            !Boolean(sourceManifest, "not_admissible_as_blind_eval") || Boolean(sourceManifest, "generation_authorized"))
            Add("source-spec-manifest", "", "源规格清单状态、哈希或内部草稿边界无效。");
        if (String(Property(sourceManifest, "files"), "sample-specs.jsonl") != specsHash)
            Add("source-specs-hash", "", "当前源规格文件哈希与源 manifest 不匹配。");
        if (String(packetManifest, "artifact_version") != "hxz-polish-regression-ai-supplement-spec-review-v1" ||
            String(packetManifest, "status") != "human_spec_review_pending" || String(packetManifest, "source_specs_sha256") != specsHash ||
            String(packetManifest, "source_manifest_sha256") != HashFile(sourceManifestPath) || phase0Contribution != 0 ||
            !Boolean(packetManifest, "not_admissible_as_blind_eval") || Boolean(packetManifest, "generation_authorized"))
            Add("packet-manifest", "", "审阅包清单来源哈希或内部数据边界无效。");
        if (String(Property(packetManifest, "files"), "specification-review.template.csv") != HashFile(templatePath))
            Add("review-template-hash", "", "只读审阅模板与 packet manifest 哈希不一致。");
        var readmePath = Path.Combine(packetRoot, "README.md");
        if (!File.Exists(readmePath) || String(Property(packetManifest, "files"), "README.md") != (File.Exists(readmePath) ? HashFile(readmePath) : ""))
            Add("packet-readme-hash", "", "审阅包说明缺失或哈希不匹配。");

        var sourceSpecs = ReadSpecs(specsPath, Add);
        if (Integer(packetManifest, "specification_count") != sourceSpecs.Count)
            Add("manifest-count", "", "packet manifest 规格数量与源规格数量不一致。");
        var csvRows = ReadCsv(csvPath, Add);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var reviewed = 0;
        var approved = 0;
        var edited = 0;
        var rejected = 0;
        foreach (var (rowNumber, values) in csvRows)
        {
            string Field(string name) => values.TryGetValue(name, out var value) ? value : "";
            var id = Field("spec_id").Trim();
            if (id.Length == 0 || !sourceSpecs.TryGetValue(id, out var source))
            {
                Add("unknown-specification", id, $"CSV 第 {rowNumber} 行 spec_id 为空或不属于源规格包。");
                continue;
            }
            if (!seen.Add(id))
            {
                Add("duplicate-specification", id, "同一规格在审阅 CSV 中重复出现。");
                continue;
            }
            ValidateSourceSnapshot(values, source, id, rowNumber, Add);
            var decision = Field("human_decision").Trim();
            var editedSpecJson = Field("human_edited_spec_json");
            var reviewer = Field("reviewer_id").Trim();
            var rowValid = true;
            if (!Decisions.Contains(decision))
            {
                Add("review-decision", id, $"CSV 第 {rowNumber} 行 human_decision 必须为 approve、edit 或 reject。");
                rowValid = false;
            }
            else if (decision == "approve") approved++;
            else if (decision == "edit") edited++;
            else rejected++;

            if (decision == "approve" && editedSpecJson.Length > 0)
            {
                Add("approve-has-edit", id, "approve 不能携带修订规格 JSON；有内容改动应选择 edit。");
                rowValid = false;
            }
            else if (decision == "edit" && !ValidEditedSpec(editedSpecJson, id))
            {
                Add("edited-spec-invalid", id, "edit 必须提供完整、有效且 spec_id 不变的修订规格 JSON。");
                rowValid = false;
            }
            else if (decision == "reject" && editedSpecJson.Length > 0)
            {
                Add("reject-has-edit", id, "reject 不应携带用于生成的修订规格 JSON。");
                rowValid = false;
            }
            if (reviewer.Length == 0 || AutomatedReviewers.Contains(reviewer.ToLowerInvariant()))
            {
                Add("reviewer", id, "必须填写人工审阅者标识，不能使用 AI/自动生成身份。");
                rowValid = false;
            }
            if (!StrictUtcTimestamp.TryParse(Field("reviewed_at_utc").Trim(), out _))
            {
                Add("review-time", id, "reviewed_at_utc 必须为带 Z 或 +00:00 的严格 UTC 时间戳。");
                rowValid = false;
            }
            if (!ReviewRationaleRules.IsSubstantive(Field("rationale")))
            {
                Add("review-rationale", id, "必须填写针对该规格的具体判断理由，不能留空或使用占位语。");
                rowValid = false;
            }
            if (rowValid) reviewed++;
        }
        foreach (var missing in sourceSpecs.Keys.Except(seen, StringComparer.Ordinal)) Add("specification-missing", missing, "源规格在审阅 CSV 中没有对应行。");
        if (Integer(packetManifest, "specification_count") != sourceSpecs.Count || sourceSpecs.Count == 0)
            Add("specification-count", "", "源规格为空或数量与审阅包不一致。");

        var valid = issues.Count == 0;
        var allEligible = approved + edited == sourceSpecs.Count && rejected == 0;
        var humanApproval = valid && reviewed == sourceSpecs.Count;
        return new(valid, sourceSpecs.Count, reviewed, approved, edited, rejected, humanApproval,
            valid && humanApproval && allEligible && sourceSpecs.Count > 0, false, phase0Contribution, issues);
    }

    private static Dictionary<string, JsonElement> ReadSpecs(string path, Action<string, string, string> add)
    {
        var specs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var lineNumber = 0;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            lineNumber++;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var spec = doc.RootElement.Clone();
                var id = String(spec, "spec_id");
                if (id.Length == 0 || !specs.TryAdd(id, spec)) add("source-spec-invalid", id, $"源规格第 {lineNumber} 行 ID 缺失或重复。");
            }
            catch (JsonException) { add("source-spec-invalid", "", $"源规格第 {lineNumber} 行不是有效 JSON。"); }
        }
        return specs;
    }

    private static List<(int Row, Dictionary<string, string> Values)> ReadCsv(string path, Action<string, string, string> add)
    {
        var rows = new List<(int, Dictionary<string, string>)>();
        using var parser = new TextFieldParser(path, Encoding.UTF8)
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false
        };
        parser.SetDelimiters(",");
        if (parser.EndOfData) { add("review-csv-empty", "", "审阅 CSV 为空。"); return rows; }
        var headers = parser.ReadFields() ?? [];
        if (!headers.SequenceEqual(Headers, StringComparer.Ordinal)) add("review-csv-header", "", "审阅 CSV 列名或顺序与模板不匹配。");
        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields() ?? [];
            if (fields.Length != Headers.Length) { add("review-csv-columns", "", $"CSV 第 {rows.Count + 2} 行列数不正确。"); continue; }
            rows.Add((rows.Count + 2, Headers.Zip(fields).ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal)));
        }
        return rows;
    }

    private static void ValidateSourceSnapshot(Dictionary<string, string> values, JsonElement source, string id, int row,
        Action<string, string, string> add)
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["target_behavior"] = String(source, "target_behavior"), ["scenario"] = String(source, "scenario"),
            ["facts_json"] = Raw(source, "facts"), ["input"] = String(source, "input"), ["constraints_json"] = Raw(source, "constraints"),
            ["expected_decision_draft"] = String(source, "expected_decision"), ["rubric_anchors_json"] = Raw(source, "rubric_anchors"),
            ["prohibited_content_json"] = Raw(source, "prohibited_content"), ["required_schema_extension"] = String(source, "required_schema_extension")
        };
        foreach (var (field, value) in expected)
            if (!values.TryGetValue(field, out var actual) || actual != value)
                add("source-snapshot-mismatch", id, $"CSV 第 {row} 行 {field} 与哈希绑定源规格不一致。");
    }

    private static bool ValidEditedSpec(string json, string expectedId)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var spec = doc.RootElement;
            return spec.ValueKind == JsonValueKind.Object && String(spec, "spec_id") == expectedId &&
                String(spec, "schema_version") == "1.0" && String(spec, "origin") == "ai_assisted_draft" &&
                String(spec, "status") == "ai_spec_draft_pending_human_review" && Integer(spec, "phase_0_gate_contribution") == 0 &&
                !string.IsNullOrWhiteSpace(String(spec, "target_behavior")) && !string.IsNullOrWhiteSpace(String(spec, "scenario")) &&
                !string.IsNullOrWhiteSpace(String(spec, "input")) && ExpectedDecisions.Contains(String(spec, "expected_decision")) &&
                HasNonEmptyStrings(Property(spec, "facts")) && HasNonEmptyStrings(Property(spec, "constraints")) &&
                HasNonEmptyStrings(Property(spec, "rubric_anchors")) && HasNonEmptyStrings(Property(spec, "prohibited_content"));
        }
        catch (JsonException) { return false; }
    }

    private static bool HasNonEmptyStrings(JsonElement element) => element.ValueKind == JsonValueKind.Array && element.GetArrayLength() > 0 &&
        element.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()));
    private static string Raw(JsonElement element, string name) => Property(element, name).ValueKind is JsonValueKind.Array or JsonValueKind.Object ? Property(element, name).GetRawText() : "null";
    private static JsonElement Property(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;
    private static string String(JsonElement element, string name) => Property(element, name).ValueKind == JsonValueKind.String ? Property(element, name).GetString() ?? "" : "";
    private static int Integer(JsonElement element, string name) => Property(element, name).TryGetInt32(out var value) ? value : 0;
    private static bool Boolean(JsonElement element, string name) => Property(element, name).ValueKind == JsonValueKind.True;
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
