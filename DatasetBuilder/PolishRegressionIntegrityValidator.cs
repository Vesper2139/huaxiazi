using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Huaxiazi.DatasetBuilder;

public sealed record PolishRegressionIntegrityIssue(string Code, string File, string Message);
public sealed record PolishRegressionIntegrityReport(bool Valid, int RowCount, int FamilyCount, int Phase0GateContribution,
    int HumanVerifiedFamilyCount, IReadOnlyList<PolishRegressionIntegrityIssue> Issues);

public static class PolishRegressionManifestWriter
{
    private static readonly string[] ArtifactPaths =
    [
        "README.md", "schema.md", "case.schema.json", "cases.jsonl", "variants.jsonl", "families.json",
        "coverage.json", "gaps.json", "baseline/audit-2026-10-04-corrected.json"
    ];

    /// <summary>Transitions the bootstrap manifest to a frozen manifest once; frozen manifests cannot be overwritten.</summary>
    public static void Finalize(string canonicalPath, string datasetDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetDirectory);
        var root = Path.GetFullPath(datasetDirectory);
        var manifestPath = Path.Combine(root, "manifest.json");
        using (var existing = JsonDocument.Parse(File.ReadAllText(manifestPath)))
        {
            if (String(existing.RootElement, "status") != "initializing")
                throw new IOException("manifest.json 已离开 initializing 状态；冻结清单不可覆写，请创建新版本目录。");
        }

        using var familiesDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "families.json")));
        var familyRoot = familiesDocument.RootElement;
        var files = ArtifactPaths.ToDictionary(path => path.Replace('\\', '/'), path => HashFile(Path.Combine(root, path)), StringComparer.Ordinal);
        var manifest = new
        {
            schema_version = "1.0", dataset_version = "hxz-polish-regression-v1", status = "frozen",
            purpose = "internal_regression_only", phase_0_gate_contribution = 0, not_admissible_as_blind_eval = true,
            boundary = new
            {
                blind_evaluation_allowed = false,
                forbidden_blind_source_kinds = new[] { "project_owned", "licensed", "user_authorized_deidentified" },
                allowed_internal_origins = new[] { "project_synthetic_legacy", "ai_assisted_draft", "human_authored_internal" }
            },
            source = new
            {
                dataset = Path.GetRelativePath(root, Path.GetFullPath(canonicalPath)).Replace('\\', '/'),
                kind = "legacy_project_synthetic", imported_row_count = familyRoot.GetProperty("row_count").GetInt32(),
                source_sha256 = HashFile(canonicalPath)
            },
            counts = new
            {
                rows = familyRoot.GetProperty("row_count").GetInt32(), families = familyRoot.GetProperty("family_count").GetInt32(),
                development_families = SplitCount(familyRoot, "development"), regression_families = SplitCount(familyRoot, "regression"),
                ai_drafts = 0, human_verified = 0
            },
            files,
            limitations = new[]
            {
                "Internal synthetic regression material only; not representative of real users.",
                "Does not establish external quality or safety and cannot activate prompt, routing, or model-weight changes.",
                "Formal phase 0 blind-evaluation contribution is zero; all human review remains pending.",
                "Family assignments use exact normalized keys only; near-duplicate boundary adjudication is not complete."
            }
        };
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true }) + Environment.NewLine;
        var tempPath = manifestPath + ".finalizing-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(tempPath, json, new UTF8Encoding(false));
        try { File.Move(tempPath, manifestPath, overwrite: true); }
        catch { if (File.Exists(tempPath)) File.Delete(tempPath); throw; }
    }

    private static int SplitCount(JsonElement familiesRoot, string split) => familiesRoot.GetProperty("families").EnumerateArray().Count(family => String(family, "split") == split);
    private static string String(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}

public static class PolishRegressionIntegrityValidator
{
    private static readonly HashSet<string> InternalOrigins = new(StringComparer.Ordinal)
        { "project_synthetic_legacy", "ai_assisted_draft", "human_authored_internal" };
    private static readonly string[] BlindSourceKinds = ["project_owned", "licensed", "user_authorized_deidentified"];
    private static readonly string[] RequiredArtifactPaths =
    [
        "README.md", "schema.md", "case.schema.json", "cases.jsonl", "variants.jsonl", "families.json",
        "coverage.json", "gaps.json", "baseline/audit-2026-10-04-corrected.json"
    ];

    public static PolishRegressionIntegrityReport Validate(string datasetDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetDirectory);
        var root = Path.GetFullPath(datasetDirectory);
        var issues = new List<PolishRegressionIntegrityIssue>();
        void Add(string code, string file, string message) => issues.Add(new(code, file, message));
        var manifestPath = Path.Combine(root, "manifest.json");
        using var manifestDocument = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var manifest = manifestDocument.RootElement;
        var purpose = String(manifest, "purpose");
        var contribution = Integer(manifest, "phase_0_gate_contribution");
        var notAdmissible = Boolean(manifest, "not_admissible_as_blind_eval");
        if (purpose != "internal_regression_only" || contribution != 0 || !notAdmissible || String(manifest, "status") != "frozen")
            Add("boundary", "manifest.json", "冻结清单必须标明 internal_regression_only、阶段 0 贡献 0 且不得进入盲评。");
        var boundary = Property(manifest, "boundary");
        if (!IsFalse(boundary, "blind_evaluation_allowed") ||
            !SequenceEquals(boundary, "forbidden_blind_source_kinds", BlindSourceKinds) ||
            !SequenceEquals(boundary, "allowed_internal_origins", InternalOrigins.Order(StringComparer.Ordinal).ToArray()))
            Add("origin-boundary", "manifest.json", "来源隔离枚举与策略不符合内部数据集约定。");

        var files = Property(manifest, "files");
        if (files.ValueKind != JsonValueKind.Object) Add("files", "manifest.json", "manifest.files 必须是文件哈希对象。");
        else
        {
            var listedPaths = files.EnumerateObject().Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var required in RequiredArtifactPaths)
                if (!listedPaths.Contains(required)) Add("missing-hash", required, "冻结清单未包含必需文件的 SHA-256。");
            foreach (var entry in files.EnumerateObject())
            {
                var filePath = Path.GetFullPath(Path.Combine(root, entry.Name.Replace('/', Path.DirectorySeparatorChar)));
                if (!filePath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                { Add("path", "manifest.json", "文件路径超出数据集目录。"); continue; }
                if (!File.Exists(filePath)) { Add("missing-file", entry.Name, "清单引用的文件不存在。"); continue; }
                if (entry.Value.ValueKind != JsonValueKind.String || !string.Equals(entry.Value.GetString(), HashFile(filePath), StringComparison.OrdinalIgnoreCase))
                    Add("hash-mismatch", entry.Name, "文件 SHA-256 与冻结清单不一致。");
            }
        }

        var source = Property(manifest, "source");
        var sourcePath = Path.GetFullPath(Path.Combine(root, String(source, "dataset").Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(sourcePath) || String(source, "source_sha256") != (File.Exists(sourcePath) ? HashFile(sourcePath) : ""))
            Add("source-hash", "manifest.json", "legacy 源文件缺失或哈希不一致。");
        var sourceRecords = File.Exists(sourcePath) ? ReadSourceRecords(sourcePath) : new Dictionary<string, (string RecordHash, string InputHash)>(StringComparer.Ordinal);

        var cases = ReadJsonLines(Path.Combine(root, "cases.jsonl"), "cases.jsonl", Add);
        var variants = ReadJsonLines(Path.Combine(root, "variants.jsonl"), "variants.jsonl", Add);
        var caseByFamily = new Dictionary<string, (string Split, string Origin, string Status)>(StringComparer.Ordinal);
        foreach (var item in cases)
        {
            var id = String(item, "id");
            var familyId = String(item, "family_id");
            if (String(item, "origin") is var origin && !InternalOrigins.Contains(origin)) Add("origin", "cases.jsonl", "case 使用了未允许的内部 origin。");
            if (BlindSourceKinds.Contains(String(item, "origin"))) Add("blind-origin", "cases.jsonl", "case 伪用了盲评授权来源类型。");
            if (String(item, "human_status") is not ("unreviewed" or "accepted" or "edited" or "rejected" or "pending_adjudication")) Add("review-status", "cases.jsonl", "human_status 缺失或无效。");
            if (!caseByFamily.TryAdd(familyId, (String(item, "split"), String(item, "origin"), String(item, "human_status")))) Add("duplicate-family", id, "cases.jsonl 中族代表重复。");
            var contextText = Property(item, "context").ValueKind == JsonValueKind.Undefined ? "" : Property(item, "context").GetRawText();
            if (BlindEvaluationAuditor.ContainsPotentialSensitiveData(String(item, "input")) ||
                BlindEvaluationAuditor.ContainsPotentialSensitiveData(String(item, "reference_output")) ||
                BlindEvaluationAuditor.ContainsPotentialSensitiveData(contextText))
                Add("sensitive-content", "cases.jsonl", "启发式隐私/密钥扫描命中；报告不回显正文。");
        }
        var variantIds = new HashSet<string>(StringComparer.Ordinal);
        var linkedSourceIds = new HashSet<string>(StringComparer.Ordinal);
        var splitsByFamily = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var item in variants)
        {
            var id = String(item, "id");
            var familyId = String(item, "family_id");
            var sourceRecordId = String(item, "source_record_id");
            if (!variantIds.Add(id)) Add("duplicate-variant", "variants.jsonl", "variants id 重复。");
            if (!InternalOrigins.Contains(String(item, "origin"))) Add("origin", "variants.jsonl", "variant 使用了未允许的内部 origin。");
            if (!linkedSourceIds.Add(sourceRecordId)) Add("duplicate-source-record", id, "多个变体指向同一遗留来源记录。");
            if (!sourceRecords.TryGetValue(sourceRecordId, out var sourceRecord)) Add("source-record", id, "变体未映射到遗留 canonical 的记录 ID。");
            else if (String(item, "source_record_sha256") != sourceRecord.RecordHash || String(item, "input_sha256") != sourceRecord.InputHash)
                Add("source-record-hash", id, "变体来源行或输入哈希与遗留 canonical 不一致。");
            if (!caseByFamily.TryGetValue(familyId, out var family)) Add("orphan-variant", id, "variant 未关联到族代表。");
            else if (String(item, "split") != family.Split) Add("family-split", id, "同族变体 split 与族代表不一致。");
            if (!splitsByFamily.TryGetValue(familyId, out var splits)) splitsByFamily[familyId] = splits = new(StringComparer.Ordinal);
            splits.Add(String(item, "split"));
        }
        if (splitsByFamily.Any(pair => pair.Value.Count != 1)) Add("family-cross-split", "variants.jsonl", "同一语义族跨 split。");
        if (sourceRecords.Count != linkedSourceIds.Count || sourceRecords.Keys.Any(id => !linkedSourceIds.Contains(id)))
            Add("source-coverage", "variants.jsonl", "变体必须逐一覆盖遗留 canonical 中的全部记录，且不得遗漏或增加来源行。");

        var familiesPath = Path.Combine(root, "families.json");
        using var familyRegistryDocument = JsonDocument.Parse(File.ReadAllText(familiesPath));
        var familyRegistry = familyRegistryDocument.RootElement;
        if (Integer(familyRegistry, "row_count") != variants.Count || Integer(familyRegistry, "family_count") != cases.Count)
            Add("family-registry-count", "families.json", "族注册表行数/族数与 cases/variants 不一致。");
        var registeredFamilyIds = new HashSet<string>(StringComparer.Ordinal);
        if (Property(familyRegistry, "families").ValueKind != JsonValueKind.Array) Add("family-registry-shape", "families.json", "families 必须为数组。");
        else foreach (var family in Property(familyRegistry, "families").EnumerateArray())
        {
            var id = String(family, "family_id");
            if (!registeredFamilyIds.Add(id)) Add("family-registry-duplicate", "families.json", "族 ID 重复。");
            if (!caseByFamily.ContainsKey(id)) Add("family-registry-orphan", "families.json", "族注册表条目没有 cases 代表。");
            var memberIds = Property(family, "member_ids");
            if (memberIds.ValueKind != JsonValueKind.Array || memberIds.EnumerateArray().Any(member =>
                    !variants.Any(variant => String(variant, "source_record_id") == (member.GetString() ?? "") && String(variant, "family_id") == id)))
                Add("family-members", id, "族注册表成员与 variants 映射不一致。");
        }
        if (registeredFamilyIds.Count != caseByFamily.Count || caseByFamily.Keys.Any(id => !registeredFamilyIds.Contains(id)))
            Add("family-registry-coverage", "families.json", "族注册表与 cases 族代表不一一对应。");

        using var coverageDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "coverage.json")));
        if (Integer(coverageDocument.RootElement, "family_count") != cases.Count || String(coverageDocument.RootElement, "unit") != "semantic_family")
            Add("coverage-unit", "coverage.json", "覆盖报告必须按语义族计数且族数与 cases 一致。");

        var counts = Property(manifest, "counts");
        var rowCount = Integer(counts, "rows");
        var familyCount = Integer(counts, "families");
        var humanVerified = Integer(counts, "human_verified");
        if (rowCount != variants.Count || rowCount != Integer(source, "imported_row_count")) Add("row-count", "manifest.json", "rows 与变体/来源记录数不一致。");
        if (familyCount != cases.Count || familyCount != caseByFamily.Count) Add("family-count", "manifest.json", "families 与 cases 数量不一致。");
        var actualReviewed = cases.Count(item => String(item, "human_status") is "accepted" or "edited");
        if (humanVerified != actualReviewed) Add("human-review-count", "manifest.json", "human_verified 与族代表审核状态不一致。");
        return new(issues.Count == 0, rowCount, familyCount, contribution, humanVerified, issues);
    }

    private static List<JsonElement> ReadJsonLines(string path, string file, Action<string, string, string> add)
    {
        var result = new List<JsonElement>();
        if (!File.Exists(path)) { add("missing-file", file, "数据文件缺失。"); return result; }
        var lineNumber = 0;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            lineNumber++;
            try { using var document = JsonDocument.Parse(line); result.Add(document.RootElement.Clone()); }
            catch (JsonException) { add("jsonl", file, $"第 {lineNumber} 行 JSON 无效。"); }
        }
        return result;
    }

    private static Dictionary<string, (string RecordHash, string InputHash)> ReadSourceRecords(string path)
    {
        var result = new Dictionary<string, (string RecordHash, string InputHash)>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            using var document = JsonDocument.Parse(line);
            var id = String(document.RootElement, "id");
            var input = String(document.RootElement, "input");
            if (id.Length > 0) result.TryAdd(id, (Hash(document.RootElement.GetRawText()), Hash(input)));
        }
        return result;
    }

    private static JsonElement Property(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value : default;
    private static string String(JsonElement root, string name) => Property(root, name).ValueKind == JsonValueKind.String ? Property(root, name).GetString() ?? "" : "";
    private static int Integer(JsonElement root, string name) => Property(root, name).TryGetInt32(out var value) ? value : -1;
    private static bool Boolean(JsonElement root, string name) => Property(root, name).ValueKind == JsonValueKind.True;
    private static bool IsFalse(JsonElement root, string name) => Property(root, name).ValueKind == JsonValueKind.False;
    private static bool SequenceEquals(JsonElement root, string name, IReadOnlyList<string> expected) =>
        Property(root, name).ValueKind == JsonValueKind.Array &&
        Property(root, name).EnumerateArray().Select(value => value.GetString() ?? "").Order(StringComparer.Ordinal)
            .SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal);
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
