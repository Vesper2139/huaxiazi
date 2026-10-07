using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Huaxiazi.DatasetBuilder;

public sealed record PolishRegressionSupplementSpecificationReviewPacketReport(int SpecificationCount, int Phase0GateContribution,
    bool GenerationAuthorized, string SourceSpecificationsSha256, IReadOnlyDictionary<string, string> FileSha256);

/// <summary>Creates a human review packet for W4.3 without recording approval or authorizing generation.</summary>
public static class PolishRegressionSupplementSpecificationReviewPacketBuilder
{
    private static readonly HashSet<string> Decisions = ["clarify", "polish"];
    private static readonly string[] Headers =
    [
        "spec_id", "target_behavior", "scenario", "facts_json", "input", "constraints_json", "expected_decision_draft",
        "rubric_anchors_json", "prohibited_content_json", "required_schema_extension", "human_decision",
        "human_edited_spec_json", "reviewer_id", "reviewed_at_utc", "rationale"
    ];

    public static PolishRegressionSupplementSpecificationReviewPacketReport Build(string specsDirectory, string coverageDirectory,
        string parentCasesPath, string parentManifestPath, string outputDirectory)
    {
        foreach (var value in new[] { specsDirectory, coverageDirectory, parentCasesPath, parentManifestPath, outputDirectory }) ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var sourceValidation = PolishRegressionSupplementSpecificationValidator.Validate(specsDirectory, coverageDirectory, parentCasesPath, parentManifestPath);
        if (!sourceValidation.Valid) throw new InvalidDataException($"W4.3 源规格包未通过全链校验；问题数：{sourceValidation.Issues.Count}。");
        var specsPath = Path.Combine(Path.GetFullPath(specsDirectory), "sample-specs.jsonl");
        var manifestPath = Path.Combine(Path.GetFullPath(specsDirectory), "manifest.json");
        if (!File.Exists(specsPath)) throw new FileNotFoundException("W4.3 规格文件不存在。", specsPath);
        if (!File.Exists(manifestPath)) throw new FileNotFoundException("W4.3 规格清单不存在。", manifestPath);

        var outputRoot = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(outputRoot) || File.Exists(outputRoot)) throw new IOException("规格审阅包输出路径已存在；请使用新的版本目录。");

        var specsBytes = File.ReadAllBytes(specsPath);
        var manifestBytes = File.ReadAllBytes(manifestPath);
        var specsHash = Hash(specsBytes);
        using var manifestDoc = JsonDocument.Parse(manifestBytes);
        var manifest = manifestDoc.RootElement;
        if (String(manifest, "artifact_version") != "hxz-polish-regression-ai-supplement-specs-v1" ||
            String(manifest, "status") != "ai_draft_pending_human_spec_review" ||
            Integer(manifest, "phase_0_gate_contribution") != 0 || !Boolean(manifest, "not_admissible_as_blind_eval") ||
            Boolean(manifest, "generation_authorized") || String(Property(manifest, "files"), "sample-specs.jsonl") != specsHash)
            throw new InvalidDataException("W4.3 规格来源哈希或内部草稿边界校验失败。");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<string> { string.Join(',', Headers.Select(Csv)) };
        foreach (var (line, lineNumber) in File.ReadLines(specsPath, Encoding.UTF8).Select((line, index) => (line, index + 1)))
        {
            if (string.IsNullOrWhiteSpace(line)) throw new InvalidDataException($"W4.3 规格第 {lineNumber} 行为空。");
            using var document = JsonDocument.Parse(line);
            var spec = document.RootElement;
            var id = String(spec, "spec_id");
            if (id.Length == 0 || !seen.Add(id) || String(spec, "origin") != "ai_assisted_draft" ||
                String(spec, "status") != "ai_spec_draft_pending_human_review" || Integer(spec, "phase_0_gate_contribution") != 0 ||
                String(spec, "schema_version") != "1.0" || string.IsNullOrWhiteSpace(String(spec, "target_behavior")) ||
                string.IsNullOrWhiteSpace(String(spec, "scenario")) || string.IsNullOrWhiteSpace(String(spec, "input")) ||
                !Decisions.Contains(String(spec, "expected_decision")) || !HasNonEmptyStrings(Property(spec, "facts")) ||
                !HasNonEmptyStrings(Property(spec, "constraints")) || !HasNonEmptyStrings(Property(spec, "rubric_anchors")) ||
                !HasNonEmptyStrings(Property(spec, "prohibited_content")))
                throw new InvalidDataException($"W4.3 规格第 {lineNumber} 行的 ID、草稿来源或边界无效。");
            rows.Add(string.Join(',', new[]
            {
                id, String(spec, "target_behavior"), String(spec, "scenario"), Raw(spec, "facts"), String(spec, "input"),
                Raw(spec, "constraints"), String(spec, "expected_decision"), Raw(spec, "rubric_anchors"),
                Raw(spec, "prohibited_content"), String(spec, "required_schema_extension"), "", "", "", "", ""
            }.Select(Csv)));
        }
        if (seen.Count == 0 || Integer(Property(manifest, "counts"), "specifications") != seen.Count)
            throw new InvalidDataException("W4.3 规格数与清单计数不一致或没有规格。");

        var csv = string.Join(Environment.NewLine, rows) + Environment.NewLine;
        var readme = "# W4.3 人工规格审阅包\n\n" +
            $"- 规格数：{seen.Count}\n- 状态：待人工逐条审阅；空白决定不代表通过。\n" +
            "- `specification-review.template.csv` 为只读模板；请填写同目录的 `specification-review.csv`。\n" +
            "- 对每行填写 human_decision（approve/edit/reject）、reviewer_id、reviewed_at_utc（UTC）和具体 rationale。\n" +
            "- edit 时在 human_edited_spec_json 提供完整修订规格 JSON。\n" +
            "- 此包不授权样本生成；阶段 0 贡献为 0，禁止用于盲评或模型晋级。\n" +
            "- AI 起草内容与评分锚点都需要独立人工判断。\n";
        var csvHash = Hash(Encoding.UTF8.GetBytes(csv));
        var readmeHash = Hash(Encoding.UTF8.GetBytes(readme));
        var packetManifest = new
        {
            artifact_version = "hxz-polish-regression-ai-supplement-spec-review-v1",
            status = "human_spec_review_pending",
            source_specs_sha256 = specsHash,
            source_manifest_sha256 = Hash(manifestBytes),
            specification_count = seen.Count,
            phase_0_gate_contribution = 0,
            not_admissible_as_blind_eval = true,
            generation_authorized = false,
            files = new Dictionary<string, string> { ["specification-review.template.csv"] = csvHash, ["README.md"] = readmeHash }
        };
        var packetManifestJson = JsonSerializer.Serialize(packetManifest, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true }) + Environment.NewLine;
        Directory.CreateDirectory(outputRoot);
        ImmutableArtifactWriter.WriteNew(Path.Combine(outputRoot, "specification-review.template.csv"), csv);
        ImmutableArtifactWriter.WriteNew(Path.Combine(outputRoot, "specification-review.csv"), csv);
        ImmutableArtifactWriter.WriteNew(Path.Combine(outputRoot, "README.md"), readme);
        ImmutableArtifactWriter.WriteNew(Path.Combine(outputRoot, "manifest.json"), packetManifestJson);
        return new(seen.Count, 0, false, specsHash,
            new Dictionary<string, string> { ["specification-review.template.csv"] = csvHash, ["README.md"] = readmeHash });
    }

    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    private static string Raw(JsonElement element, string name) => Property(element, name).ValueKind is JsonValueKind.Array or JsonValueKind.Object ? Property(element, name).GetRawText() : "null";
    private static string String(JsonElement element, string name) => Property(element, name).ValueKind == JsonValueKind.String ? Property(element, name).GetString() ?? "" : "";
    private static JsonElement Property(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;
    private static int Integer(JsonElement element, string name) => Property(element, name).TryGetInt32(out var value) ? value : 0;
    private static bool Boolean(JsonElement element, string name) => Property(element, name).ValueKind == JsonValueKind.True;
    private static bool HasNonEmptyStrings(JsonElement element) => element.ValueKind == JsonValueKind.Array && element.GetArrayLength() > 0 &&
        element.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()));
    private static string HashFile(string path) => Hash(File.ReadAllBytes(path));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
