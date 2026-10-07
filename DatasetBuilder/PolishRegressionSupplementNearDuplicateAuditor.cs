using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Huaxiazi.DatasetBuilder;

/// <summary>Queues potential cross-dataset or supplement-internal duplicates for review; it never merges families.</summary>
public static class PolishRegressionSupplementNearDuplicateAuditor
{
    private sealed record Case(string Id, string FamilyId, string Split, string Input, bool IsSupplement);
    private sealed record Pair(string PairId, string LeftCaseId, string RightCaseId, string LeftFamilyId, string RightFamilyId,
        string LeftSplit, string RightSplit, double Similarity, int SharedShingleCount, int UnionShingleCount);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };

    public static PolishRegressionNearDuplicateSummary Build(string baseCasesPath, string baseManifestPath, string supplementCasesPath,
        string supplementManifestPath, string rulesPath, string outputDirectory)
    {
        foreach (var path in new[] { baseCasesPath, baseManifestPath, supplementCasesPath, supplementManifestPath, rulesPath, outputDirectory })
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var baseCasesHash = HashFile(baseCasesPath);
        var baseManifestHash = HashFile(baseManifestPath);
        using var baseManifestDoc = JsonDocument.Parse(File.ReadAllText(baseManifestPath));
        var baseManifest = baseManifestDoc.RootElement;
        if (String(baseManifest, "dataset_version") != "hxz-polish-regression-v1" || String(baseManifest, "status") != "frozen" ||
            Integer(baseManifest, "phase_0_gate_contribution") != 0 || !Boolean(baseManifest, "not_admissible_as_blind_eval") ||
            String(Property(baseManifest, "files"), "cases.jsonl") != baseCasesHash)
            throw new InvalidDataException("基线必须是哈希绑定且阶段 0 零贡献的冻结 polish-regression-v1。 ");

        var supplementCasesHash = HashFile(supplementCasesPath);
        var supplementManifestHash = HashFile(supplementManifestPath);
        using var supplementManifestDoc = JsonDocument.Parse(File.ReadAllText(supplementManifestPath));
        var supplementManifest = supplementManifestDoc.RootElement;
        if (String(supplementManifest, "dataset_version") != "hxz-polish-regression-ai-supplement-draft-v1" ||
            String(supplementManifest, "status") != "draft" || String(supplementManifest, "purpose") != "internal_regression_only" ||
            String(supplementManifest, "parent_manifest_sha256") != baseManifestHash || Integer(supplementManifest, "phase_0_gate_contribution") != 0 ||
            !Boolean(supplementManifest, "not_admissible_as_blind_eval") ||
            String(Property(supplementManifest, "files"), "cases.jsonl") != supplementCasesHash)
            throw new InvalidDataException("AI 补充集必须是哈希绑定到当前冻结基线的内部草稿，且阶段 0 贡献为零。 ");

        var baseCases = ReadCases(baseCasesPath, isSupplement: false);
        var supplementCases = ReadCases(supplementCasesPath, isSupplement: true);
        if (baseCases.Count == 0 || supplementCases.Count == 0 || Integer(Property(baseManifest, "counts"), "families") != baseCases.Count ||
            Integer(Property(supplementManifest, "counts"), "families") != supplementCases.Count)
            throw new InvalidDataException("基线或补充集为空，或 cases 数量与 manifest 的族数不匹配。");
        var baseIds = baseCases.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var baseFamilyIds = baseCases.Select(item => item.FamilyId).ToHashSet(StringComparer.Ordinal);
        if (supplementCases.Any(item => baseIds.Contains(item.Id) || baseFamilyIds.Contains(item.FamilyId)))
            throw new InvalidDataException("基线与补充集存在重复 case_id 或 family_id，无法安全交叉比较。");

        var rulesHash = HashFile(rulesPath);
        using var rulesDoc = JsonDocument.Parse(File.ReadAllText(rulesPath));
        var rules = rulesDoc.RootElement;
        var rulesetId = String(rules, "ruleset_id");
        var shingleSize = Integer(rules, "shingle_size");
        var threshold = Number(rules, "candidate_threshold");
        var normalization = String(rules, "normalization");
        var sensitivityThresholds = Property(rules, "sensitivity_thresholds").EnumerateArray().Select(item => item.GetDouble()).ToArray();
        if (String(rules, "schema_version") != "1.0" || string.IsNullOrWhiteSpace(rulesetId) || shingleSize != 3 || threshold is <= 0 or > 1 ||
            normalization != "unicode-formkc-lowercase-alphanumeric-codepoint-trigrams-v1" || sensitivityThresholds.Length == 0 ||
            sensitivityThresholds.Any(value => value is <= 0 or > 1) || sensitivityThresholds.Distinct().Count() != sensitivityThresholds.Length ||
            !sensitivityThresholds.Contains(threshold))
            throw new InvalidDataException("近重复规则文件缺失必需字段或包含不受支持的算法参数。");

        var cases = baseCases.Concat(supplementCases).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var shingles = cases.ToDictionary(item => item.Id, item => BuildShingles(Normalize(item.Input), shingleSize), StringComparer.Ordinal);
        var scored = new List<(Pair Pair, double ExactScore)>();
        for (var i = 0; i < cases.Length; i++)
        for (var j = i + 1; j < cases.Length; j++)
        {
            var left = cases[i];
            var right = cases[j];
            if (!left.IsSupplement && !right.IsSupplement) continue;
            var leftShingles = shingles[left.Id];
            var rightShingles = shingles[right.Id];
            var shared = leftShingles.Count(rightShingles.Contains);
            var union = leftShingles.Count + rightShingles.Count - shared;
            var score = (double)shared / union;
            var pairId = "hxz-polish-reg-supplement-near-dup-" + Hash(string.Join("\n", left.Id, right.Id))[..16];
            scored.Add((new(pairId, left.Id, right.Id, left.FamilyId, right.FamilyId, left.Split, right.Split,
                Math.Round(score, 6, MidpointRounding.AwayFromZero), shared, union), score));
        }

        var candidates = scored.Where(item => item.ExactScore >= threshold).Select(item => item.Pair)
            .OrderByDescending(item => item.Similarity).ThenBy(item => item.LeftCaseId, StringComparer.Ordinal)
            .ThenBy(item => item.RightCaseId, StringComparer.Ordinal).ToArray();
        var candidateJsonl = string.Join(Environment.NewLine, candidates.Select(pair => JsonSerializer.Serialize(new
        {
            schema_version = "1.0", pair.PairId, pair.LeftCaseId, pair.RightCaseId, pair.LeftFamilyId, pair.RightFamilyId,
            pair.LeftSplit, pair.RightSplit, char_trigram_jaccard = pair.Similarity, pair.SharedShingleCount, pair.UnionShingleCount,
            ruleset_id = rulesetId, adjudication_status = "pending_human_adjudication"
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })));
        if (candidates.Length > 0) candidateJsonl += Environment.NewLine;
        var reviewJsonl = string.Join(Environment.NewLine, candidates.Select(pair => JsonSerializer.Serialize(new
        {
            schema_version = "1.0", pair.PairId, pair.LeftCaseId, pair.RightCaseId, pair.LeftFamilyId, pair.RightFamilyId,
            pair.LeftSplit, pair.RightSplit, char_trigram_jaccard = pair.Similarity, reviewer_id = (string?)null,
            reviewed_at_utc = (string?)null, decision = (string?)null, rationale = (string?)null, status = "pending_human_adjudication"
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })));
        if (candidates.Length > 0) reviewJsonl += Environment.NewLine;

        var report = new
        {
            schema_version = "1.0", report_version = "hxz-polish-reg-supplement-near-duplicate-candidates-v1",
            status = "internal_diagnostic_pending_human_adjudication", purpose = "internal_near_duplicate_triage",
            phase_0_gate_contribution = 0, not_admissible_as_blind_eval = true, auto_merge_performed = false,
            threshold_is_calibrated = false, threshold_use = "human_review_queue_only", ruleset_id = rulesetId,
            candidate_threshold = threshold, normalization, shingle_size = shingleSize, source_dataset_count = 2,
            base_family_count = baseCases.Count, supplement_family_count = supplementCases.Count, family_count = cases.Length,
            pair_count = scored.Count, candidate_pair_count = candidates.Length,
            cross_split_candidate_pair_count = candidates.Count(pair => pair.LeftSplit != pair.RightSplit),
            sensitivity = sensitivityThresholds.Order().Select(value => new
            {
                threshold = value, candidate_pair_count = scored.Count(item => item.ExactScore >= value),
                cross_split_pair_count = scored.Count(item => item.ExactScore >= value && item.Pair.LeftSplit != item.Pair.RightSplit)
            }).ToArray(),
            base_cases_sha256 = baseCasesHash, base_manifest_sha256 = baseManifestHash,
            supplement_cases_sha256 = supplementCasesHash, supplement_manifest_sha256 = supplementManifestHash, rules_sha256 = rulesHash,
            limitations = "Character-trigram Jaccard only queues pairs for human review; the threshold is uncalibrated, candidates are not duplicates by definition, and no family, split, or label is changed."
        };

        var output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        var pairsPath = Path.Combine(output, "candidate-pairs.jsonl");
        var templatePath = Path.Combine(output, "human-adjudication.template.jsonl");
        var reportPath = Path.Combine(output, "report.json");
        ImmutableArtifactWriter.WriteNew(pairsPath, candidateJsonl);
        ImmutableArtifactWriter.WriteNew(templatePath, reviewJsonl);
        ImmutableArtifactWriter.WriteNew(reportPath, JsonSerializer.Serialize(report, JsonOptions) + Environment.NewLine);
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["candidate-pairs.jsonl"] = HashFile(pairsPath), ["human-adjudication.template.jsonl"] = HashFile(templatePath),
            ["report.json"] = HashFile(reportPath)
        };
        var manifest = new
        {
            schema_version = "1.0", artifact_version = "hxz-polish-reg-near-duplicate-candidates-v1",
            status = "internal_diagnostic_pending_human_adjudication", purpose = "internal_near_duplicate_triage",
            base_manifest_sha256 = baseManifestHash, supplement_manifest_sha256 = supplementManifestHash,
            base_cases_sha256 = baseCasesHash, supplement_cases_sha256 = supplementCasesHash, rules_sha256 = rulesHash,
            phase_0_gate_contribution = 0, not_admissible_as_blind_eval = true, auto_merge_performed = false, files
        };
        ImmutableArtifactWriter.WriteNew(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine);
        return new(cases.Length, scored.Count, candidates.Length, candidates.Count(pair => pair.LeftSplit != pair.RightSplit), files);
    }

    private static List<Case> ReadCases(string path, bool isSupplement)
    {
        var items = new List<Case>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var families = new HashSet<string>(StringComparer.Ordinal);
        var lineNumber = 0;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) throw new InvalidDataException($"cases.jsonl 第 {lineNumber} 行为空。");
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var id = String(root, "id");
            var familyId = String(root, "family_id");
            var split = String(root, "split");
            var input = String(root, "input");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(familyId) || string.IsNullOrWhiteSpace(input) || split is not ("development" or "regression"))
                throw new InvalidDataException($"cases.jsonl 第 {lineNumber} 行缺少有效 id/family_id/input/split。");
            if (!ids.Add(id) || !families.Add(familyId)) throw new InvalidDataException($"cases.jsonl 第 {lineNumber} 行包含重复 case/family id。");
            items.Add(new(id, familyId, split, input, isSupplement));
        }
        return items;
    }

    private static string Normalize(string input)
    {
        var result = new StringBuilder();
        foreach (var rune in input.Normalize(NormalizationForm.FormKC).ToLowerInvariant().EnumerateRunes())
            if (Rune.IsLetterOrDigit(rune)) result.Append(rune.ToString());
        if (result.Length == 0) throw new InvalidDataException("输入归一化后为空，无法构建字符 shingle。");
        return result.ToString();
    }
    private static HashSet<string> BuildShingles(string normalized, int size)
    {
        var runes = normalized.EnumerateRunes().Select(rune => rune.ToString()).ToArray();
        if (runes.Length < size) throw new InvalidDataException($"输入归一化后短于 shingle_size={size}。");
        var result = new HashSet<string>(StringComparer.Ordinal);
        for (var start = 0; start <= runes.Length - size; start++) result.Add(string.Concat(runes.Skip(start).Take(size)));
        return result;
    }
    private static JsonElement Property(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value : default;
    private static string String(JsonElement root, string name) => Property(root, name).ValueKind == JsonValueKind.String ? Property(root, name).GetString() ?? "" : "";
    private static int Integer(JsonElement root, string name) => Property(root, name).TryGetInt32(out var value) ? value : 0;
    private static double Number(JsonElement root, string name) => Property(root, name).TryGetDouble(out var value) ? value : double.NaN;
    private static bool Boolean(JsonElement root, string name) => Property(root, name).ValueKind == JsonValueKind.True;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
