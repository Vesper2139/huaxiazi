using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace Huaxiazi.DatasetBuilder;

public sealed record PolishRegressionNearDuplicateWorkbookImportResult(int CandidateCount, int ImportedCount, string OutputPath);

/// <summary>Copies human-entered workbook fields into the existing JSONL template without deciding labels or changing candidate references.</summary>
public static class PolishRegressionNearDuplicateWorkbookImporter
{
    private const string WorkbookSchemaVersion = "hxz-polish-neardup-human-review-v1";
    private static readonly XNamespace MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace OfficeRelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly HashSet<string> AllowedDecisions = new(StringComparer.Ordinal)
    {
        "same_semantic_family", "distinct_task_intent_same_backbone", "distinct_semantic_families", "uncertain_request_arbitration"
    };

    private sealed record Candidate(string PairId, string LeftCaseId, string RightCaseId, string LeftFamilyId, string RightFamilyId,
        string LeftSplit, string RightSplit, double Similarity, string Status);
    private sealed record WorkbookDecision(string PairId, string Decision, string ReviewerId, string ReviewedAtUtc, string Rationale);
    private sealed record WorkbookMetadata(string PairId, double Similarity, string LeftCaseId, string LeftSplit,
        string RightCaseId, string RightSplit, string SplitRelation, string Status);

    public static PolishRegressionNearDuplicateWorkbookImportResult Import(string reportDirectory, string casesPath,
        string workbookPath, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(casesPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var reportRoot = Path.GetFullPath(reportDirectory);
        var fullCasesPath = Path.GetFullPath(casesPath);
        var fullWorkbookPath = Path.GetFullPath(workbookPath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        if (!Directory.Exists(reportRoot) || !File.Exists(fullCasesPath) || !File.Exists(fullWorkbookPath))
            throw new FileNotFoundException("report-dir、cases.jsonl 或 workbook 文件不存在。");
        var protectedDirectories = new[] { reportRoot, Path.GetDirectoryName(fullCasesPath)! };
        if (protectedDirectories.Any(directory => IsWithinDirectory(fullOutputPath, directory)))
            throw new InvalidDataException("output-boundary: 导入结果必须写入候选报告目录和冻结源数据目录之外。");
        if (File.Exists(fullOutputPath) || Directory.Exists(fullOutputPath))
            throw new IOException("output-exists: 导入输出采用 create-only，目标已存在。");
        var outputDirectory = Path.GetDirectoryName(fullOutputPath)!;
        if (!Directory.Exists(outputDirectory))
            throw new DirectoryNotFoundException("输出目录必须已存在；导入器不会创建目录或修改输入工件。");

        var manifestPath = Path.Combine(reportRoot, "manifest.json");
        var pairsPath = Path.Combine(reportRoot, "candidate-pairs.jsonl");
        var templatePath = Path.Combine(reportRoot, "human-adjudication.template.jsonl");
        var reportPath = Path.Combine(reportRoot, "report.json");
        using var manifestDocument = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var manifest = manifestDocument.RootElement;
        EnsureBoundary(manifest);
        var files = manifest.GetProperty("files");
        EnsureFileHash(pairsPath, String(files, "candidate-pairs.jsonl"));
        EnsureFileHash(templatePath, String(files, "human-adjudication.template.jsonl"));
        EnsureFileHash(reportPath, String(files, "report.json"));
        var caseHash = HashFile(fullCasesPath);
        if (String(manifest, "source_cases_sha256") != caseHash)
            throw new InvalidDataException("source-binding: cases.jsonl 与候选报告绑定的哈希不一致。");

        var candidates = ReadCandidates(pairsPath);
        var templateLines = File.ReadLines(templatePath, Encoding.UTF8).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        var templateById = ReadTemplate(templateLines, candidates);
        var casesById = ReadCases(fullCasesPath);
        using var workbook = new ReviewWorkbook(fullWorkbookPath);
        ValidateSourceBinding(workbook, manifest, manifestPath, files);
        var metadata = ReadMetadata(workbook, candidates);
        var decisions = ReadDecisions(workbook, candidates, metadata, casesById);

        var output = new StringBuilder();
        foreach (var candidate in candidates)
        {
            var decision = decisions[candidate.PairId];
            var item = JsonNode.Parse(templateById[candidate.PairId])!.AsObject();
            item["decision"] = decision.Decision;
            item["reviewer_id"] = decision.ReviewerId;
            item["reviewed_at_utc"] = decision.ReviewedAtUtc;
            item["rationale"] = decision.Rationale;
            item["status"] = "adjudicated";
            output.Append(item.ToJsonString()).Append('\n');
        }

        using (var stream = new FileStream(fullOutputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            writer.Write(output.ToString());
        return new(candidates.Count, decisions.Count, fullOutputPath);
    }

    private static void EnsureBoundary(JsonElement manifest)
    {
        if (String(manifest, "artifact_version") != "hxz-polish-reg-near-duplicate-candidates-v1" ||
            String(manifest, "purpose") != "internal_near_duplicate_triage" ||
            Integer(manifest, "phase_0_gate_contribution") != 0 ||
            !Boolean(manifest, "not_admissible_as_blind_eval") || Boolean(manifest, "auto_merge_performed"))
            throw new InvalidDataException("source-binding: 报告缺少支持的版本或内部数据边界。");
    }

    private static void ValidateSourceBinding(ReviewWorkbook workbook, JsonElement manifest, string manifestPath, JsonElement files)
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["workbook_schema_version"] = WorkbookSchemaVersion,
            ["artifact_version"] = String(manifest, "artifact_version"),
            ["manifest_sha256"] = HashFile(manifestPath),
            ["candidate_pairs_sha256"] = String(files, "candidate-pairs.jsonl"),
            ["human_template_sha256"] = String(files, "human-adjudication.template.jsonl"),
            ["parent_manifest_sha256"] = String(manifest, "parent_manifest_sha256"),
            ["source_cases_sha256"] = String(manifest, "source_cases_sha256"),
            ["rules_sha256"] = String(manifest, "rules_sha256"),
        };
        var actual = new Dictionary<string, string>(StringComparer.Ordinal);
        var lastRow = workbook.GetLastRow("来源校验");
        for (var row = 1; row <= lastRow; row++)
        {
            var key = workbook.GetCell("来源校验", $"A{row}");
            var value = workbook.GetCell("来源校验", $"B{row}");
            if (string.IsNullOrWhiteSpace(key) || !actual.TryAdd(key, value))
                throw new InvalidDataException("source-binding: 来源校验工作表存在空 key 或重复 key。");
        }
        var mismatched = expected.Where(pair => !actual.TryGetValue(pair.Key, out var value) || value != pair.Value)
            .Select(pair => pair.Key).Concat(actual.Keys.Except(expected.Keys, StringComparer.Ordinal)).ToArray();
        if (expected.Count != actual.Count || mismatched.Length > 0)
            throw new InvalidDataException($"source-binding: 工作簿与当前来源不一致，字段：{string.Join(",", mismatched)}。");
    }

    private static List<Candidate> ReadCandidates(string path)
    {
        var result = new List<Candidate>();
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) throw new InvalidDataException("candidate-pairs.jsonl 含空行。");
            using var document = JsonDocument.Parse(line);
            var item = document.RootElement;
            result.Add(new(String(item, "pair_id"), String(item, "left_case_id"), String(item, "right_case_id"),
                String(item, "left_family_id"), String(item, "right_family_id"), String(item, "left_split"), String(item, "right_split"),
                Number(item, "char_trigram_jaccard"), String(item, "adjudication_status")));
        }
        if (result.Count == 0 || result.Any(item => string.IsNullOrWhiteSpace(item.PairId)) ||
            result.Select(item => item.PairId).Distinct(StringComparer.Ordinal).Count() != result.Count)
            throw new InvalidDataException("candidate-pairs.jsonl 为空，或 pair_id 缺失/重复。");
        return result;
    }

    private static Dictionary<string, string> ReadTemplate(string[] lines, IReadOnlyList<Candidate> candidates)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            var item = document.RootElement;
            var pairId = String(item, "pair_id");
            if (!result.TryAdd(pairId, line)) throw new InvalidDataException($"模板 pair_id 重复：{pairId}");
        }
        if (result.Count != candidates.Count || candidates.Any(candidate => !result.ContainsKey(candidate.PairId)))
            throw new InvalidDataException("模板与候选清单的 pair_id 集合不一致。");
        foreach (var candidate in candidates)
        {
            using var document = JsonDocument.Parse(result[candidate.PairId]);
            var item = document.RootElement;
            if (String(item, "left_case_id") != candidate.LeftCaseId || String(item, "right_case_id") != candidate.RightCaseId ||
                String(item, "left_family_id") != candidate.LeftFamilyId || String(item, "right_family_id") != candidate.RightFamilyId ||
                String(item, "status") != "pending_human_adjudication")
                throw new InvalidDataException($"模板候选引用不匹配或已被预填：{candidate.PairId}");
        }
        return result;
    }

    private static Dictionary<string, JsonElement> ReadCases(string path)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            using var document = JsonDocument.Parse(line);
            var item = document.RootElement.Clone();
            var caseId = String(item, "id");
            if (!result.TryAdd(caseId, item)) throw new InvalidDataException($"cases.jsonl 中 id 重复：{caseId}");
        }
        return result;
    }

    private static Dictionary<string, WorkbookMetadata> ReadMetadata(ReviewWorkbook workbook, IReadOnlyList<Candidate> candidates)
    {
        var expectedHeaders = new[] { "pair_id", "char_trigram_jaccard", "left_case_id", "left_split", "right_case_id", "right_split", "split_relation", "adjudication_status" };
        EnsureHeaders(workbook, "候选元数据", expectedHeaders);
        var result = new Dictionary<string, WorkbookMetadata>(StringComparer.Ordinal);
        foreach (var row in ReadRows(workbook, "候选元数据", 8))
        {
            var id = row[0];
            if (string.IsNullOrWhiteSpace(id)) throw new InvalidDataException("metadata-mismatch: 候选元数据行缺少 pair_id。");
            if (!double.TryParse(row[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var score))
                throw new InvalidDataException($"metadata-mismatch: 分数无效：{id}");
            var item = new WorkbookMetadata(id, score, row[2], row[3], row[4], row[5], row[6], row[7]);
            if (!result.TryAdd(id, item)) throw new InvalidDataException($"metadata-mismatch: pair_id 重复：{id}");
        }
        if (result.Count != candidates.Count) throw new InvalidDataException("metadata-mismatch: 候选元数据行数与候选清单不同。");
        foreach (var candidate in candidates)
        {
            if (!result.TryGetValue(candidate.PairId, out var item) ||
                item.LeftCaseId != candidate.LeftCaseId || item.RightCaseId != candidate.RightCaseId ||
                item.LeftSplit != candidate.LeftSplit || item.RightSplit != candidate.RightSplit ||
                item.SplitRelation != (candidate.LeftSplit == candidate.RightSplit ? "same_split" : "cross_split") ||
                item.Status != candidate.Status || Math.Abs(item.Similarity - candidate.Similarity) > 0.0000005)
                throw new InvalidDataException($"metadata-mismatch: 候选元数据与 JSONL 不一致：{candidate.PairId}");
        }
        return result;
    }

    private static Dictionary<string, WorkbookDecision> ReadDecisions(ReviewWorkbook workbook, IReadOnlyList<Candidate> candidates,
        IReadOnlyDictionary<string, WorkbookMetadata> metadata, IReadOnlyDictionary<string, JsonElement> cases)
    {
        var expectedHeaders = new[] { "pair_id", "左侧案例证据（未含参考成稿）", "右侧案例证据（未含参考成稿）", "decision", "reviewer_id", "reviewed_at_utc", "rationale" };
        EnsureHeaders(workbook, "候选对审阅", expectedHeaders);
        var result = new Dictionary<string, WorkbookDecision>(StringComparer.Ordinal);
        foreach (var row in ReadRows(workbook, "候选对审阅", 7))
        {
            var pairId = row[0];
            if (string.IsNullOrWhiteSpace(pairId)) throw new InvalidDataException("decision-mismatch: 审阅行缺少 pair_id。");
            var candidate = candidates.SingleOrDefault(item => item.PairId == pairId);
            if (candidate is null) throw new InvalidDataException($"decision-mismatch: 未知 pair_id：{pairId}");
            if (!cases.TryGetValue(candidate.LeftCaseId, out var left) || !cases.TryGetValue(candidate.RightCaseId, out var right))
                throw new InvalidDataException($"decision-mismatch: 候选引用的 case 不在绑定的 cases.jsonl 中：{pairId}");
            if (row[1] != BuildEvidence(left) || row[2] != BuildEvidence(right))
                throw new InvalidDataException($"decision-mismatch: 审阅证据与当前冻结 cases.jsonl 不一致：{pairId}");
            var decision = new WorkbookDecision(pairId, row[3], row[4], row[5], row[6]);
            if (!result.TryAdd(pairId, decision)) throw new InvalidDataException($"decision-mismatch: pair_id 重复：{pairId}");
        }
        if (result.Count != candidates.Count) throw new InvalidDataException("decision-incomplete: 审阅行数与候选数不同。");
        foreach (var item in result.Values)
        {
            if (!AllowedDecisions.Contains(item.Decision) || string.IsNullOrWhiteSpace(item.ReviewerId) || !IsUtcTimestamp(item.ReviewedAtUtc))
                throw new InvalidDataException($"decision-incomplete: 决定、reviewer_id 和 UTC 时间必须完整且合法：{item.PairId}");
        }
        var invalidRationales = result.Values.Where(item => !ReviewRationaleRules.IsSubstantive(item.Rationale))
            .Select(item => item.PairId).Order(StringComparer.Ordinal).ToArray();
        if (invalidRationales.Length > 0)
            throw new InvalidDataException($"decision-incomplete: rationale 必须是具体理由，不能留空或使用占位语；需修订 pair_id：{string.Join(", ", invalidRationales)}。");
        return result;
    }

    private static string BuildEvidence(JsonElement item)
    {
        var context = item.TryGetProperty("context", out var value) && value.ValueKind == JsonValueKind.Object ? value : default;
        var purpose = String(context, "purpose");
        var formality = String(context, "formality");
        var explicitRequirements = String(context, "explicit_requirements");
        var claims = item.TryGetProperty("claims", out var claimValue)
            ? JsonSerializer.Serialize(claimValue, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
            : "[]";
        return $"Input: {String(item, "input")}\nPurpose: {purpose}\nFormality: {formality}\nExplicit requirements: {explicitRequirements}\nClaims: {claims}";
    }

    private static IEnumerable<string[]> ReadRows(ReviewWorkbook workbook, string sheetName, int columnCount)
    {
        var lastRow = workbook.GetLastRow(sheetName);
        for (var row = 5; row <= lastRow; row++)
        {
            var values = new string[columnCount];
            for (var column = 0; column < columnCount; column++) values[column] = workbook.GetCell(sheetName, ColumnName(column + 1) + row);
            if (values.All(string.IsNullOrWhiteSpace)) continue;
            yield return values;
        }
    }

    private static void EnsureHeaders(ReviewWorkbook workbook, string sheetName, IReadOnlyList<string> expected)
    {
        for (var column = 0; column < expected.Count; column++)
            if (workbook.GetCell(sheetName, ColumnName(column + 1) + "4") != expected[column])
                throw new InvalidDataException($"workbook-schema: {sheetName} 表头不符合受支持的审阅工作簿版本。");
    }

    private static string ColumnName(int column)
    {
        var result = string.Empty;
        while (column > 0) { column--; result = (char)('A' + column % 26) + result; column /= 26; }
        return result;
    }

    private static bool IsUtcTimestamp(string value) =>
        value.EndsWith('Z') && DateTimeOffset.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _);

    private static void EnsureFileHash(string path, string expected)
    {
        if (!File.Exists(path) || HashFile(path) != expected)
            throw new InvalidDataException($"source-binding: 候选包工件缺失或哈希不匹配：{Path.GetFileName(path)}");
    }

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static bool IsWithinDirectory(string path, string directory)
    {
        var relative = Path.GetRelativePath(directory, path);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private static string String(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static int Integer(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : -1;
    private static double Number(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? number : double.NaN;
    private static bool Boolean(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private sealed class ReviewWorkbook : IDisposable
    {
        private readonly ZipArchive _archive;
        private readonly Dictionary<string, Sheet> _sheets = new(StringComparer.Ordinal);
        private readonly List<string> _sharedStrings;

        private sealed class Sheet(XDocument document)
        {
            public Dictionary<string, string> Cells { get; } = document.Descendants(MainNs + "c")
                .Where(cell => cell.Attribute("r") is not null)
                .ToDictionary(cell => cell.Attribute("r")!.Value, cell => cell.ToString(SaveOptions.DisableFormatting), StringComparer.Ordinal);
            public int LastRow => document.Descendants(MainNs + "row").Select(row => (int?)row.Attribute("r") ?? 0).DefaultIfEmpty(0).Max();
        }

        public ReviewWorkbook(string path)
        {
            _archive = ZipFile.OpenRead(path);
            try
            {
                _sharedStrings = ReadSharedStrings();
                var workbook = ReadXml("xl/workbook.xml");
                var relationships = ReadXml("xl/_rels/workbook.xml.rels");
                var targets = relationships.Root!.Elements(PackageRelNs + "Relationship").ToDictionary(
                    item => (string)item.Attribute("Id")!, item => NormalizeTarget((string)item.Attribute("Target")!), StringComparer.Ordinal);
                foreach (var sheet in workbook.Descendants(MainNs + "sheet"))
                {
                    var name = (string?)sheet.Attribute("name") ?? "";
                    var relationshipId = (string?)sheet.Attribute(OfficeRelNs + "id") ?? "";
                    if (string.IsNullOrWhiteSpace(name) || !targets.TryGetValue(relationshipId, out var target) || !_sheets.TryAdd(name, new(ReadXml(target))))
                        throw new InvalidDataException("workbook-schema: 工作簿工作表名称或关系无效。");
                }
            }
            catch
            {
                _archive.Dispose();
                throw;
            }
        }

        public string GetCell(string sheetName, string address)
        {
            if (!_sheets.TryGetValue(sheetName, out var sheet)) throw new InvalidDataException($"workbook-schema: 缺少工作表 {sheetName}。");
            if (!sheet.Cells.TryGetValue(address, out var xml)) return "";
            var cell = XElement.Parse(xml);
            var type = (string?)cell.Attribute("t") ?? "n";
            if (type == "inlineStr") return string.Concat(cell.Descendants(MainNs + "t").Select(value => value.Value));
            var value = cell.Element(MainNs + "v")?.Value ?? "";
            if (type == "s")
            {
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0 || index >= _sharedStrings.Count)
                    throw new InvalidDataException("workbook-schema: shared string 索引无效。");
                return _sharedStrings[index];
            }
            return value;
        }

        public int GetLastRow(string sheetName) => _sheets.TryGetValue(sheetName, out var sheet)
            ? sheet.LastRow : throw new InvalidDataException($"workbook-schema: 缺少工作表 {sheetName}。");

        private List<string> ReadSharedStrings()
        {
            var entry = _archive.GetEntry("xl/sharedStrings.xml");
            if (entry is null) return [];
            using var stream = entry.Open();
            var document = LoadXml(stream);
            return document.Root?.Elements(MainNs + "si").Select(item => string.Concat(item.Descendants(MainNs + "t").Select(text => text.Value))).ToList() ?? [];
        }

        private XDocument ReadXml(string name)
        {
            var entry = _archive.GetEntry(name) ?? throw new InvalidDataException($"workbook-schema: XLSX 缺少 {name}。");
            using var stream = entry.Open();
            return LoadXml(stream);
        }

        private static XDocument LoadXml(Stream stream)
        {
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return XDocument.Load(reader, LoadOptions.None);
        }

        private static string NormalizeTarget(string target)
        {
            var segments = (target.StartsWith('/') ? target[1..] : "xl/" + target).Split('/');
            var normalized = new List<string>();
            foreach (var segment in segments)
            {
                if (segment is "" or ".") continue;
                if (segment == "..") { if (normalized.Count > 0) normalized.RemoveAt(normalized.Count - 1); continue; }
                normalized.Add(segment);
            }
            return string.Join('/', normalized);
        }

        public void Dispose() => _archive.Dispose();
    }
}
