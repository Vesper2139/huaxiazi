using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Huaxiazi.DatasetBuilder;

public sealed record PolishAgentDatasetAuditIssue(string Code, string RecordId, string Message);

public sealed record PolishAgentDatasetAuditReport(
    bool Valid,
    int SampleCount,
    int TrainCount,
    int DevCount,
    int TestCount,
    int TemplateFamilyCount,
    int SourceRecordMatchCount,
    int SingleReviewerSyntheticRecordCount,
    string CanonicalSha256,
    string SftSha256,
    string SourceSha256,
    IReadOnlyList<PolishAgentDatasetAuditIssue> Issues);

/// <summary>Validates the v2 project-owned synthetic polish dataset without treating it as human-reviewed blind-eval evidence.</summary>
public static class PolishAgentDatasetAuditor
{
    private const string ExpectedUserTask = "润色以下内容；保留事实、立场和情绪，不虚构信息；仅在关键事实缺失时澄清。";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed class JsonlRows(byte[] bytes, List<JsonDocument> documents) : IDisposable
    {
        public byte[] Bytes { get; } = bytes;
        public List<JsonDocument> Documents { get; } = documents;
        public void Dispose() { foreach (var document in Documents) document.Dispose(); }
    }

    public static PolishAgentDatasetAuditReport Validate(string canonicalPath, string sftPath, string manifestPath, string sourcePath)
    {
        foreach (var path in new[] { canonicalPath, sftPath, manifestPath, sourcePath }) ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var issues = new List<PolishAgentDatasetAuditIssue>();
        using var canonicalRows = ReadJsonLines(canonicalPath, "canonical", issues);
        using var sftRows = ReadJsonLines(sftPath, "sft", issues);
        using var sourceRows = ReadJsonLines(sourcePath, "source", issues);
        using var manifestDocument = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var manifest = manifestDocument.RootElement;
        if (manifest.ValueKind != JsonValueKind.Object) Add(issues, "manifest-shape", "manifest", "manifest 根节点必须是对象。");

        var canonical = canonicalRows.Documents.Select(doc => doc.RootElement).ToArray();
        var sft = sftRows.Documents.Select(doc => doc.RootElement).ToArray();
        var source = sourceRows.Documents.Select(doc => doc.RootElement).ToArray();
        var counts = canonical.GroupBy(item => GetString(item, "split") ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var familyToSplit = new Dictionary<string, string>(StringComparer.Ordinal);
        var familyCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var canonicalById = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var sourceById = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var sftById = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var singleReviewerCount = 0;
        var sourceMatches = 0;

        foreach (var item in source)
        {
            var id = GetString(item, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (!sourceById.TryAdd(id, item)) Add(issues, "duplicate-source-id", id, "source canonical 中存在重复 id。");
        }
        foreach (var item in canonical)
        {
            var id = GetString(item, "id") ?? "";
            if (id.Length == 0) { Add(issues, "required", "canonical", "缺少记录 id。"); continue; }
            if (!canonicalById.TryAdd(id, item)) Add(issues, "duplicate-id", id, "v2 canonical 中存在重复 id。");
            if (GetString(item, "schema_version") != "2.0") Add(issues, "schema-version", id, "schema_version 必须是 2.0。");
            if (GetString(item, "task_type") != "agent_polish" || GetString(item, "mode") != "polish") Add(issues, "task", id, "v2 训练集仅允许 agent_polish / polish。");
            var split = GetString(item, "split") ?? "";
            if (split is not ("train" or "dev" or "test")) Add(issues, "split", id, "split 必须是 train、dev 或 test。");
            if (string.IsNullOrWhiteSpace(GetString(item, "input"))) Add(issues, "input", id, "input 不能为空。");
            if (!TryObject(item, "context", out _) || !TryObject(item, "output", out var output) ||
                GetString(output, "kind") != "final" || string.IsNullOrWhiteSpace(GetString(output, "content")))
                Add(issues, "output", id, "context 必須是对象，output 必须是带非空 content 的 final 对象。");
            if (!TryObject(item, "provenance", out var provenance))
            {
                Add(issues, "provenance", id, "缺少 provenance 对象。");
                continue;
            }
            var family = GetString(provenance, "source_template_family") ?? "";
            var sourceId = GetString(provenance, "source_id") ?? "";
            if (GetString(provenance, "source_dataset") != "hxz-synthetic-v1" || GetString(provenance, "license") != "project-owned-synthetic" ||
                GetString(provenance, "transform") != "remove_generator_record_suffix_and_normalize_agent_fields" || family.Length == 0 || sourceId.Length == 0)
                Add(issues, "provenance", id, "来源必须标记项目合成数据、原始记录 ID、模板族和确定性转换。");
            else if (sourceById.TryGetValue(sourceId, out var sourceRecord))
            {
                sourceMatches++;
                ValidateSourceAlignment(item, sourceRecord, provenance, id, issues);
            }
            else Add(issues, "source-record-missing", id, "provenance.source_id 在 source canonical 中不存在。");

            if (family.Length > 0)
            {
                if (familyToSplit.TryGetValue(family, out var priorSplit) && priorSplit != split)
                    Add(issues, "family-cross-split", id, $"模板族 {family} 同时出现在 {priorSplit} 与 {split}。");
                else familyToSplit[family] = split;
                familyCounts[family] = familyCounts.GetValueOrDefault(family) + 1;
            }

            if (TryObject(item, "review", out var review))
            {
                if (GetString(review, "review_status") != "accepted" || !TryGetInt(review, "reviewer_count", out var reviewerCount) || reviewerCount < 1 ||
                    !TryGetBool(review, "adjudicated", out _)) Add(issues, "review-metadata", id, "review 元数据字段不完整。");
                else if (reviewerCount == 1) singleReviewerCount++;
            }
            else Add(issues, "review-metadata", id, "缺少 review 元数据对象。");
        }

        foreach (var item in sft)
        {
            var id = GetString(item, "id") ?? "";
            if (id.Length == 0) { Add(issues, "sft-id", "sft", "SFT 记录缺少 id。"); continue; }
            if (!sftById.TryAdd(id, item)) Add(issues, "duplicate-sft-id", id, "SFT 视图存在重复 id。");
        }
        if (canonicalById.Count != sftById.Count || canonicalById.Keys.Any(id => !sftById.ContainsKey(id)))
            Add(issues, "sft-coverage", "sft", "canonical 与 SFT 视图的记录 ID 必须一一对应。");
        foreach (var (id, item) in canonicalById)
            if (sftById.TryGetValue(id, out var sftItem)) ValidateSftAlignment(item, sftItem, id, issues);

        var canonicalHash = Hash(canonicalRows.Bytes);
        var sftHash = Hash(sftRows.Bytes);
        var sourceHash = Hash(sourceRows.Bytes);
        ValidateManifest(manifest, canonicalPath, sftPath, canonical.Length, counts, familyCounts, familyToSplit, canonicalHash, sftHash, sourceHash, issues);
        return new(
            issues.Count == 0,
            canonical.Length,
            counts.GetValueOrDefault("train"), counts.GetValueOrDefault("dev"), counts.GetValueOrDefault("test"),
            familyCounts.Count,
            sourceMatches,
            singleReviewerCount,
            canonicalHash,
            sftHash,
            sourceHash,
            issues);
    }

    private static void ValidateSourceAlignment(JsonElement item, JsonElement source, JsonElement provenance, string id, List<PolishAgentDatasetAuditIssue> issues)
    {
        var sourceId = GetString(source, "id") ?? "";
        if (!sourceId.StartsWith("hxz-v1-polish-", StringComparison.Ordinal) ||
            id != sourceId.Replace("hxz-v1-polish-", "hxz-polish-agent-v2-", StringComparison.Ordinal))
            Add(issues, "source-id-mismatch", id, "v2 id 与来源 id 的确定性映射不一致。");
        if (GetString(source, "mode") != "polish") Add(issues, "source-mode", id, "来源记录不是 polish 模式。");
        foreach (var propertyName in new[] { "category", "scenario", "channel", "risk_level" })
            if (!JsonEqual(GetProperty(item, propertyName), GetProperty(source, propertyName)))
                Add(issues, $"source-{propertyName}-mismatch", id, $"v2 {propertyName} 与来源记录不一致。");
        if (GetString(source, "provenance", "template_family") != GetString(provenance, "source_template_family"))
            Add(issues, "source-family-mismatch", id, "v2 模板族与来源记录不一致。");
        var rawInput = GetString(source, "input") ?? "";
        var normalizedInput = Regex.Replace(rawInput, @"\s*样本\d+。\s*记录\d+。\s*$", "");
        if (GetString(item, "input") != normalizedInput) Add(issues, "source-input-mismatch", id, "v2 输入与来源记录的声明转换不一致。");
        if (!JsonEqual(GetProperty(item, "context"), GetProperty(source, "context"))) Add(issues, "source-context-mismatch", id, "v2 context 与来源记录不一致。");
        if (!JsonEqual(GetProperty(item, "claims"), GetProperty(source, "claims"))) Add(issues, "source-claims-mismatch", id, "v2 claims 与来源记录不一致。");
        var shouldClarify = GetProperty(source, "should_clarify");
        var expectedDecision = shouldClarify.ValueKind == JsonValueKind.True ? "clarify" : "polish";
        if (GetString(item, "expected_decision") != expectedDecision) Add(issues, "source-decision-mismatch", id, "v2 expected_decision 与来源澄清标签不一致。");
        if (!JsonEqual(GetProperty(item, "clarification_questions"), GetProperty(source, "clarification_questions")))
            Add(issues, "source-clarification-mismatch", id, "v2 clarification_questions 与来源记录不一致。");
        if (!JsonEqual(GetProperty(item, "rubric"), GetProperty(source, "rubric"))) Add(issues, "source-rubric-mismatch", id, "v2 rubric 与来源记录不一致。");
        var sourceOutputText = GetString(source, "gold_output");
        if (sourceOutputText is null)
        {
            Add(issues, "source-output-missing", id, "来源记录缺少 gold_output。");
            return;
        }
        using var sourceOutputDocument = JsonDocument.Parse(sourceOutputText);
        var sourceOutput = sourceOutputDocument.RootElement;
        var output = GetProperty(item, "output");
        foreach (var propertyName in new[] { "kind", "scenario", "topic", "content" })
            if (!JsonEqual(GetProperty(output, propertyName), GetProperty(sourceOutput, propertyName)))
                Add(issues, "source-output-mismatch", id, $"v2 output.{propertyName} 与来源 gold_output 不一致。");
    }

    private static void ValidateSftAlignment(JsonElement canonical, JsonElement sft, string id, List<PolishAgentDatasetAuditIssue> issues)
    {
        if (GetString(canonical, "split") != GetString(sft, "split")) Add(issues, "sft-split-mismatch", id, "canonical 与 SFT 的 split 不一致。");
        if (!sft.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() != 2)
        {
            Add(issues, "sft-messages", id, "SFT messages 必须恰好包含 user 和 assistant 两条消息。");
            return;
        }
        var user = messages[0];
        var assistant = messages[1];
        if (GetString(user, "role") != "user" || GetString(assistant, "role") != "assistant") Add(issues, "sft-role", id, "SFT 消息角色顺序必须是 user、assistant。");
        try
        {
            using var userContent = JsonDocument.Parse(GetString(user, "content") ?? "");
            if (GetString(userContent.RootElement, "task") != ExpectedUserTask ||
                GetString(userContent.RootElement, "input") != GetString(canonical, "input") ||
                !JsonEqual(GetProperty(userContent.RootElement, "context"), GetProperty(canonical, "context")))
                Add(issues, "sft-input-mismatch", id, "SFT user 内容与 canonical 输入不一致。");
        }
        catch (JsonException) { Add(issues, "sft-user-json", id, "SFT user content 不是有效 JSON。"); }
        try
        {
            using var assistantContent = JsonDocument.Parse(GetString(assistant, "content") ?? "");
            if (!JsonEqual(assistantContent.RootElement, GetProperty(canonical, "output")))
                Add(issues, "sft-target-mismatch", id, "SFT assistant 目标与 canonical output 不一致。");
        }
        catch (JsonException) { Add(issues, "sft-assistant-json", id, "SFT assistant content 不是有效 JSON。"); }
        if (GetString(sft, "metadata", "expected_decision") != GetString(canonical, "expected_decision") ||
            GetString(sft, "metadata", "source_template_family") != GetString(canonical, "provenance", "source_template_family"))
            Add(issues, "sft-metadata-mismatch", id, "SFT metadata 与 canonical 标签不一致。");
    }

    private static void ValidateManifest(JsonElement manifest, string canonicalPath, string sftPath, int total,
        IReadOnlyDictionary<string, int> counts, IReadOnlyDictionary<string, int> familyCounts, IReadOnlyDictionary<string, string> familyToSplit,
        string canonicalHash, string sftHash, string sourceHash, List<PolishAgentDatasetAuditIssue> issues)
    {
        if (GetString(manifest, "schema_version") != "2.0" || GetString(manifest, "purpose") != "agent_behavior_training" ||
            GetString(manifest, "source_dataset") != "hxz-synthetic-v1")
            Add(issues, "manifest-identity", "manifest", "manifest 必须标记 v2 项目合成润色 Agent 数据集。");
        if (!TryGetInt(manifest, "total", out var manifestTotal) || manifestTotal != total)
            Add(issues, "manifest-total", "manifest", "manifest.total 与 canonical 行数不一致。");
        if (!string.Equals(GetString(manifest, "source_sha256"), sourceHash, StringComparison.OrdinalIgnoreCase))
            Add(issues, "source-hash-mismatch", "manifest", "manifest.source_sha256 与 source canonical 文件不一致。");
        if (manifest.TryGetProperty("counts", out var manifestCounts) && manifestCounts.ValueKind == JsonValueKind.Object)
        {
            foreach (var split in new[] { "train", "dev", "test" })
                if (!TryGetInt(manifestCounts, split, out var expected) || expected != counts.GetValueOrDefault(split))
                    Add(issues, "manifest-split-count", "manifest", $"manifest.counts.{split} 与 canonical 实际数量不一致。");
        }
        else Add(issues, "manifest-counts", "manifest", "manifest.counts 必须是对象。");
        if (!TryGetInt(manifest, "per_template_family", out var perFamily) || perFamily < 1 || familyCounts.Values.Any(count => count != perFamily))
            Add(issues, "family-count", "manifest", "每个模板族的实际记录数必须等于 manifest.per_template_family。");
        if (manifest.TryGetProperty("family_split_policy", out var policy) && policy.ValueKind == JsonValueKind.String)
        {
            var match = Regex.Match(policy.GetString() ?? "", @"^(?<train>\d+) train families / (?<dev>\d+) dev families / (?<test>\d+) test families$");
            if (!match.Success) Add(issues, "family-policy", "manifest", "family_split_policy 格式无效。");
            else
            {
                foreach (var split in new[] { "train", "dev", "test" })
                {
                    var expectedFamilies = int.Parse(match.Groups[split].Value, System.Globalization.CultureInfo.InvariantCulture);
                    var actualFamilies = familyToSplit.Count(pair => pair.Value == split);
                    if (actualFamilies != expectedFamilies) Add(issues, "family-split-count", "manifest", $"{split} 模板族数量与 manifest family_split_policy 不一致。");
                }
            }
        }
        else Add(issues, "family-policy", "manifest", "缺少 family_split_policy 字段。");
        if (manifest.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Object)
        {
            if (GetString(files, "canonical") != Path.GetFileName(canonicalPath) || GetString(files, "sft") != Path.GetFileName(sftPath))
                Add(issues, "manifest-files", "manifest", "manifest.files 声明与传入 canonical/SFT 文件名不一致。");
        }
        else Add(issues, "manifest-files", "manifest", "manifest.files 必须是对象。");
        if (!string.Equals(GetString(manifest, "canonical_sha256"), canonicalHash, StringComparison.OrdinalIgnoreCase))
            Add(issues, "canonical-hash-mismatch", "manifest", "manifest.canonical_sha256 与 canonical 文件不一致。");
        if (!string.Equals(GetString(manifest, "sft_sha256"), sftHash, StringComparison.OrdinalIgnoreCase))
            Add(issues, "sft-hash-mismatch", "manifest", "manifest.sft_sha256 与 SFT 文件不一致。");
        if (!IsSha256(canonicalHash) || !IsSha256(sftHash) || !IsSha256(sourceHash)) Add(issues, "hash", "manifest", "数据文件 SHA-256 计算失败。");
    }

    private static JsonlRows ReadJsonLines(string path, string kind, List<PolishAgentDatasetAuditIssue> issues)
    {
        var bytes = File.ReadAllBytes(path);
        var documents = new List<JsonDocument>();
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) { Add(issues, "blank-line", $"{kind}:{lineNumber}", $"{kind} JSONL 不得包含空行。"); continue; }
            try { documents.Add(JsonDocument.Parse(line)); }
            catch (JsonException) { Add(issues, "invalid-json", $"{kind}:{lineNumber}", $"{kind} JSONL 第 {lineNumber} 行不是有效 JSON。"); }
        }
        return new(bytes, documents);
    }

    private static bool TryObject(JsonElement parent, string name, out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object) return true;
        value = default;
        return false;
    }
    private static JsonElement GetProperty(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) ? value : default;
    private static string? GetString(JsonElement parent, string name) => GetProperty(parent, name).ValueKind == JsonValueKind.String ? GetProperty(parent, name).GetString() : null;
    private static string? GetString(JsonElement parent, string objectName, string propertyName) => GetString(GetProperty(parent, objectName), propertyName);
    private static bool TryGetInt(JsonElement parent, string name, out int value) => GetProperty(parent, name).TryGetInt32(out value);
    private static bool TryGetBool(JsonElement parent, string name, out bool value)
    {
        var element = GetProperty(parent, name);
        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False) { value = element.GetBoolean(); return true; }
        value = false;
        return false;
    }
    private static bool JsonEqual(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind) return false;
        return left.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => true,
            JsonValueKind.Object => JsonObjectEqual(left, right),
            JsonValueKind.Array => JsonArrayEqual(left, right),
            JsonValueKind.String => string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal),
            JsonValueKind.Number => left.TryGetDecimal(out var leftNumber) && right.TryGetDecimal(out var rightNumber)
                ? leftNumber == rightNumber
                : string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal),
            JsonValueKind.True or JsonValueKind.False => left.GetBoolean() == right.GetBoolean(),
            _ => false
        };
    }

    private static bool JsonObjectEqual(JsonElement left, JsonElement right)
    {
        var leftProperties = left.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
        var rightProperties = right.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
        return leftProperties.Count == rightProperties.Count && leftProperties.All(pair => rightProperties.TryGetValue(pair.Key, out var value) && JsonEqual(pair.Value, value));
    }

    private static bool JsonArrayEqual(JsonElement left, JsonElement right)
    {
        var leftItems = left.EnumerateArray().ToArray();
        var rightItems = right.EnumerateArray().ToArray();
        return leftItems.Length == rightItems.Length && leftItems.Zip(rightItems).All(pair => JsonEqual(pair.First, pair.Second));
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static void Add(List<PolishAgentDatasetAuditIssue> issues, string code, string id, string message) => issues.Add(new(code, id, message));
}
