using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.VisualBasic.FileIO;

namespace Huaxiazi.DatasetBuilder;

public sealed record LocalModelCandidateReviewIssue(string Code, string CaseId, string Message);

public sealed record LocalModelCandidateReviewReport(
    bool Valid,
    int TotalCount,
    int ReviewedCount,
    int FactIssueCount,
    int RequirementIssueCount,
    int DirectUsableCount,
    int DirectNotUsableCount,
    double? AverageToneFit,
    double? AverageOverall,
    [property: JsonPropertyName("phase_0_gate_contribution")] int Phase0GateContribution,
    IReadOnlyList<LocalModelCandidateReviewIssue> Issues);

public sealed record LocalModelCandidateReviewImportResult(
    int ImportedCount,
    [property: JsonPropertyName("phase_0_gate_contribution")] int Phase0GateContribution,
    string OutputPath,
    string OutputSha256);

/// <summary>Validates human feedback for internal local-model candidates; it never promotes them to gold data.</summary>
public static class LocalModelCandidateReviewImporter
{
    private static readonly string[] Headers =
    [
        "case_id", "family_id", "input", "recipient", "purpose", "formality", "explicit_requirements",
        "candidate_kind", "candidate_output", "fact_preservation", "requirement_preservation", "tone_fit_1_to_5",
        "directly_usable", "overall_1_to_5", "reviewer_id", "reviewed_at_utc", "rationale"
    ];
    private static readonly HashSet<string> Ratings = ["pass", "issue", "not_applicable"];
    private static readonly HashSet<string> AutomatedReviewerIds = ["ai", "assistant", "ai assistant", "automated", "自动生成"];
    private static readonly HashSet<string> ForbiddenPacketFields =
        ["reference_output", "expected_decision", "model", "model_sha256", "quality_gate_passed", "output_contract_valid"];

    public static LocalModelCandidateReviewReport Validate(string candidatePacketPath, string reviewCsvPath, string metadataPath)
    {
        foreach (var value in new[] { candidatePacketPath, reviewCsvPath, metadataPath })
            ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var issues = new List<LocalModelCandidateReviewIssue>();
        void Add(string code, string caseId, string message) => issues.Add(new(code, caseId, message));
        var packet = ReadPacket(candidatePacketPath, Add);
        var metadata = ReadMetadata(metadataPath, Add);
        ValidateMetadata(candidatePacketPath, metadata, packet.Count, Add);
        var csvRows = ReadCsv(reviewCsvPath, Add);
        var reviewedIds = new HashSet<string>(StringComparer.Ordinal);
        var reviewed = 0;
        var factIssues = 0;
        var requirementIssues = 0;
        var directYes = 0;
        var directNo = 0;
        var toneRatings = new List<int>();
        var overallRatings = new List<int>();

        foreach (var (rowNumber, values) in csvRows)
        {
            string Field(string name) => values.TryGetValue(name, out var value) ? value : string.Empty;
            var caseId = Field("case_id").Trim();
            if (caseId.Length == 0 || !packet.TryGetValue(caseId, out var source))
            {
                Add("unknown-case", caseId, $"CSV 第 {rowNumber} 行 case_id 为空或不属于候选包。");
                continue;
            }
            if (!reviewedIds.Add(caseId))
            {
                Add("duplicate-case", caseId, "同一候选在审查 CSV 中出现多次。");
                continue;
            }

            var rowValid = true;
            foreach (var field in new[] { "family_id", "input", "recipient", "purpose", "formality", "explicit_requirements", "candidate_kind", "candidate_output" })
            {
                if (Field(field) != source[field])
                {
                    Add("source-row-mismatch", caseId, $"CSV 第 {rowNumber} 行 {field} 与原始候选包不一致。");
                    rowValid = false;
                }
            }

            var fact = Field("fact_preservation").Trim().ToLowerInvariant();
            var requirement = Field("requirement_preservation").Trim().ToLowerInvariant();
            if (!Ratings.Contains(fact) || !Ratings.Contains(requirement))
            {
                Add("review-ratings", caseId, "事实保留和要求保留必须填写 pass、issue 或 not_applicable。");
                rowValid = false;
            }
            else
            {
                if (fact == "issue") factIssues++;
                if (requirement == "issue") requirementIssues++;
            }

            var tone = ParseScore(Field("tone_fit_1_to_5"), "tone-fit-score", caseId, rowNumber, Add, out var toneValue);
            var overall = ParseScore(Field("overall_1_to_5"), "overall-score", caseId, rowNumber, Add, out var overallValue);
            if (!tone || !overall) rowValid = false;
            if (toneValue is { } toneScore) toneRatings.Add(toneScore);
            if (overallValue is { } overallScore) overallRatings.Add(overallScore);

            var directUsability = Field("directly_usable").Trim().ToLowerInvariant();
            if (directUsability is not ("yes" or "no" or "not_applicable"))
            {
                Add("direct-usability", caseId, "directly_usable 必须填写 yes、no 或 not_applicable。");
                rowValid = false;
            }
            else
            {
                if (directUsability == "yes") directYes++;
                if (directUsability == "no") directNo++;
            }

            var reviewerId = Field("reviewer_id").Trim();
            if (reviewerId.Length == 0 || AutomatedReviewerIds.Contains(reviewerId.ToLowerInvariant()))
            {
                Add("reviewer-id", caseId, "reviewer_id 必须标识人工审阅者，不能使用 AI/自动化身份。");
                rowValid = false;
            }
            if (!StrictUtcTimestamp.TryParse(Field("reviewed_at_utc").Trim(), out _))
            {
                Add("review-time", caseId, "reviewed_at_utc 必须是带 Z 或 +00:00 的严格 UTC 时间戳。");
                rowValid = false;
            }
            var rationale = Field("rationale");
            if (!ReviewRationaleRules.IsSubstantive(rationale))
            {
                Add("review-rationale", caseId, "rationale 必须填写具体观察，不能留空或使用占位语。");
                rowValid = false;
            }
            if ((fact == "issue" || requirement == "issue" || directUsability == "no") && rationale.Trim().Length < 8)
            {
                Add("issue-rationale", caseId, "存在事实/要求/可用性问题时，rationale 至少需要说明具体问题。");
                rowValid = false;
            }

            if (rowValid) reviewed++;
        }

        foreach (var id in packet.Keys.Order(StringComparer.Ordinal))
            if (!reviewedIds.Contains(id)) Add("missing-review", id, "候选没有对应的人工审查行。");

        return new(issues.Count == 0 && packet.Count > 0, packet.Count, reviewed, factIssues, requirementIssues,
            directYes, directNo, Average(toneRatings), Average(overallRatings), 0, issues);
    }

    public static LocalModelCandidateReviewImportResult Import(string candidatePacketPath, string reviewCsvPath,
        string metadataPath, string outputPath)
    {
        foreach (var value in new[] { candidatePacketPath, reviewCsvPath, metadataPath, outputPath })
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var paths = new[] { candidatePacketPath, reviewCsvPath, metadataPath }.Select(Path.GetFullPath).ToArray();
        if (paths.Any(path => !File.Exists(path))) throw new FileNotFoundException("候选 JSONL、人工审查 CSV 或审查元数据不存在。");
        var fullOutputPath = Path.GetFullPath(outputPath);
        if (paths.Any(path => IsWithinDirectory(fullOutputPath, Path.GetDirectoryName(path)!)))
            throw new InvalidDataException("output-boundary: 人审结果必须写到候选包目录之外。");

        var report = Validate(paths[0], paths[1], paths[2]);
        if (!report.Valid)
        {
            var codes = report.Issues.Select(issue => issue.Code).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            throw new InvalidDataException($"review-invalid: 人工审查未通过校验，问题类别：{string.Join(",", codes)}。");
        }

        var packetBytes = File.ReadAllBytes(paths[0]);
        var packetHash = Hash(packetBytes);
        var candidates = ReadPacket(paths[0], (_, _, _) => { });
        var rows = ReadCsv(paths[1], (_, _, _) => { });
        var output = new List<string>(rows.Count);
        foreach (var (_, fields) in rows)
        {
            var caseId = fields["case_id"].Trim();
            var candidate = candidates[caseId];
            var record = new
            {
                schema_version = "local-model-candidate-human-review-v1",
                case_id = caseId,
                family_id = candidate["family_id"],
                candidate_packet_sha256 = packetHash,
                candidate_kind = candidate["candidate_kind"],
                assessment = new
                {
                    fact_preservation = fields["fact_preservation"].Trim().ToLowerInvariant(),
                    requirement_preservation = fields["requirement_preservation"].Trim().ToLowerInvariant(),
                    tone_fit_1_to_5 = ParseNullableScore(fields["tone_fit_1_to_5"]),
                    directly_usable = fields["directly_usable"].Trim().ToLowerInvariant(),
                    overall_1_to_5 = ParseNullableScore(fields["overall_1_to_5"])
                },
                reviewer = new
                {
                    reviewer_id = fields["reviewer_id"].Trim(),
                    reviewed_at_utc = fields["reviewed_at_utc"].Trim(),
                    identity_verified = false
                },
                rationale = fields["rationale"].Trim(),
                human_status = "reviewed_internal_candidate",
                phase_0_gate_contribution = 0
            };
            output.Add(JsonSerializer.Serialize(record));
        }
        var content = string.Join('\n', output) + "\n";
        ImmutableArtifactWriter.WriteNew(fullOutputPath, content);
        return new(output.Count, 0, fullOutputPath, Hash(Encoding.UTF8.GetBytes(content)));
    }

    private static Dictionary<string, Dictionary<string, string>> ReadPacket(string path,
        Action<string, string, string> add)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var families = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(path)) { add("packet-missing", "", "候选 JSONL 不存在。"); return result; }
        var lineNumber = 0;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            lineNumber++;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) { add("packet-row", "", $"JSONL 第 {lineNumber} 行不是对象。"); continue; }
                if (ForbiddenPacketFields.Any(field => root.TryGetProperty(field, out _)))
                    add("packet-boundary", String(root, "case_id"), $"JSONL 第 {lineNumber} 行包含禁止进入审查包的参考、标签或模型字段。");
                var id = String(root, "case_id");
                var family = String(root, "family_id");
                var context = Property(root, "context");
                var values = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["family_id"] = family,
                    ["input"] = String(root, "input"),
                    ["recipient"] = String(context, "recipient"),
                    ["purpose"] = String(context, "purpose"),
                    ["formality"] = String(context, "formality"),
                    ["explicit_requirements"] = String(context, "explicit_requirements"),
                    ["candidate_kind"] = String(root, "candidate_kind"),
                    ["candidate_output"] = String(root, "candidate_output")
                };
                if (id.Length == 0 || family.Length == 0 || values["input"].Length == 0 ||
                    values["candidate_kind"] is not ("final" or "needs_clarification") || string.IsNullOrWhiteSpace(values["candidate_output"]))
                    add("packet-row", id, $"JSONL 第 {lineNumber} 行缺少 case、family、输入或候选类型。");
                if (id.Length == 0 || !result.TryAdd(id, values)) add("packet-id", id, $"JSONL 第 {lineNumber} 行 case_id 缺失或重复。");
                if (family.Length == 0 || !families.Add(family)) add("packet-family", id, "候选包中的 family_id 缺失或重复。");
            }
            catch (JsonException) { add("packet-json", "", $"候选 JSONL 第 {lineNumber} 行 JSON 无效。"); }
        }
        return result;
    }

    private static JsonElement ReadMetadata(string path, Action<string, string, string> add)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            return document.RootElement.Clone();
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            add("metadata-json", "", "审查元数据缺失或 JSON 无效。");
            return default;
        }
    }

    private static void ValidateMetadata(string packetPath, JsonElement metadata, int packetCount,
        Action<string, string, string> add)
    {
        if (metadata.ValueKind != JsonValueKind.Object || String(metadata, "schema_version") != "local-model-candidate-review-v1" ||
            Integer(metadata, "phase_0_gate_contribution") != 0 || Property(metadata, "reference_outputs_included").ValueKind != JsonValueKind.False ||
            Property(metadata, "expected_decisions_included").ValueKind != JsonValueKind.False)
            add("metadata-boundary", "", "审查元数据版本或零贡献/无参考答案边界无效。");
        if (Integer(metadata, "cases") != packetCount)
            add("metadata-count", "", "审查元数据计数与候选 JSONL 行数不一致。");
        if (!File.Exists(packetPath) || !Hash(File.ReadAllBytes(packetPath)).Equals(String(metadata, "candidate_packet_sha256"), StringComparison.OrdinalIgnoreCase))
            add("packet-hash", "", "候选 JSONL 哈希与审查元数据绑定值不一致。");
    }

    private static List<(int Row, Dictionary<string, string> Values)> ReadCsv(string path,
        Action<string, string, string> add)
    {
        var rows = new List<(int, Dictionary<string, string>)>();
        if (!File.Exists(path)) { add("csv-missing", "", "人工审查 CSV 不存在。"); return rows; }
        using var parser = new TextFieldParser(path, Encoding.UTF8)
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false,
            Delimiters = [","]
        };
        string[]? header;
        try { header = parser.ReadFields(); }
        catch (MalformedLineException) { add("csv-header", "", "CSV 表头格式无效。"); return rows; }
        if (header is null || !header.SequenceEqual(Headers, StringComparer.Ordinal))
        {
            add("csv-header", "", "CSV 表头必须与本版本审查模板完全一致。");
            return rows;
        }
        var rowNumber = 1;
        while (!parser.EndOfData)
        {
            rowNumber++;
            string[]? values;
            try { values = parser.ReadFields(); }
            catch (MalformedLineException) { add("csv-row", "", $"CSV 第 {rowNumber} 行格式无效。"); break; }
            if (values is null || values.Length != Headers.Length)
            {
                add("csv-row", "", $"CSV 第 {rowNumber} 行字段数错误。");
                continue;
            }
            rows.Add((rowNumber, Headers.Select((name, index) => (name, values[index]))
                .ToDictionary(pair => pair.name, pair => pair.Item2, StringComparer.Ordinal)));
        }
        return rows;
    }

    private static bool ParseScore(string raw, string code, string caseId, int rowNumber,
        Action<string, string, string> add, out int? score)
    {
        score = null;
        var value = raw.Trim();
        if (value.Equals("not_applicable", StringComparison.OrdinalIgnoreCase)) return true;
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed is >= 1 and <= 5)
        {
            score = parsed;
            return true;
        }
        add(code, caseId, $"CSV 第 {rowNumber} 行评分必须是 1–5 或 not_applicable。");
        return false;
    }

    private static int? ParseNullableScore(string raw) => int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var score) ? score : null;
    private static double? Average(IReadOnlyCollection<int> values) => values.Count == 0 ? null : values.Average();
    private static JsonElement Property(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value : default;
    private static string String(JsonElement root, string name) => Property(root, name).ValueKind == JsonValueKind.String ? Property(root, name).GetString() ?? string.Empty : string.Empty;
    private static int Integer(JsonElement root, string name) => Property(root, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var parsed) ? parsed : -1;
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool IsWithinDirectory(string path, string directory)
    {
        var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.Equals(fullDirectory, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(fullDirectory + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
