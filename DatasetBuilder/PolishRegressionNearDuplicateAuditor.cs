using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Huaxiazi.DatasetBuilder;

public sealed record PolishRegressionNearDuplicateSummary(int FamilyCount, int PairCount, int CandidatePairCount,
    int CrossSplitCandidatePairCount, IReadOnlyDictionary<string, string> FileSha256);

/// <summary>Creates a review queue of potentially similar internal regression families; it never merges or labels families.</summary>
public static class PolishRegressionNearDuplicateAuditor
{
    private sealed record Case(string Id, string FamilyId, string Split, string Input);
    private sealed record Pair(string PairId, string LeftCaseId, string RightCaseId, string LeftFamilyId, string RightFamilyId,
        string LeftSplit, string RightSplit, double Similarity, int SharedShingleCount, int UnionShingleCount);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    public static PolishRegressionNearDuplicateSummary Build(string casesPath, string parentManifestPath, string rulesPath, string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(casesPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentManifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(rulesPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var parentManifestHash = HashFile(parentManifestPath);
        using var parent = JsonDocument.Parse(File.ReadAllText(parentManifestPath));
        var parentRoot = parent.RootElement;
        if (String(parentRoot, "status") != "frozen" || Integer(parentRoot, "phase_0_gate_contribution") != 0 ||
            !Boolean(parentRoot, "not_admissible_as_blind_eval"))
            throw new InvalidDataException("父数据集必须是阶段 0 贡献为零且禁止盲评的冻结内部集。");

        var rulesHash = HashFile(rulesPath);
        using var rulesDocument = JsonDocument.Parse(File.ReadAllText(rulesPath));
        var rules = rulesDocument.RootElement;
        var rulesetId = String(rules, "ruleset_id");
        var shingleSize = Integer(rules, "shingle_size");
        var candidateThreshold = Number(rules, "candidate_threshold");
        var normalization = String(rules, "normalization");
        if (String(rules, "schema_version") != "1.0" || string.IsNullOrWhiteSpace(rulesetId) || shingleSize != 3 ||
            candidateThreshold is <= 0 or > 1 || normalization != "unicode-formkc-lowercase-alphanumeric-codepoint-trigrams-v1")
            throw new InvalidDataException("近重复规则文件缺失必需字段或包含不受支持的算法参数。");
        var sensitivityThresholds = Property(rules, "sensitivity_thresholds").EnumerateArray()
            .Select(value => value.GetDouble()).ToArray();
        if (sensitivityThresholds.Length == 0 || sensitivityThresholds.Any(value => value is <= 0 or > 1) ||
            sensitivityThresholds.Distinct().Count() != sensitivityThresholds.Length ||
            !sensitivityThresholds.Contains(candidateThreshold))
            throw new InvalidDataException("sensitivity_thresholds 必须是互异的 (0,1] 阈值且包含 candidate_threshold。");

        var cases = ReadCases(casesPath);
        if (cases.Count == 0) throw new InvalidDataException("近重复审计拒绝空族输入。");
        var casesHash = HashFile(casesPath);
        var parentFiles = Property(parentRoot, "files");
        var parentCounts = Property(parentRoot, "counts");
        if (String(parentRoot, "dataset_version") != "hxz-polish-regression-v1" ||
            String(parentFiles, "cases.jsonl") != casesHash || Integer(parentCounts, "families") != cases.Count)
            throw new InvalidDataException("cases.jsonl 哈希或族数未与指定的冻结 polish-regression-v1 manifest 绑定。");
        var normalizedInputs = cases.ToDictionary(item => item.Id, item => Normalize(item.Input), StringComparer.Ordinal);
        var shingles = normalizedInputs.ToDictionary(pair => pair.Key, pair => BuildShingles(pair.Value, shingleSize), StringComparer.Ordinal);
        var scoredPairs = new List<(Pair Pair, double ExactScore)>();
        for (var leftIndex = 0; leftIndex < cases.Count; leftIndex++)
        for (var rightIndex = leftIndex + 1; rightIndex < cases.Count; rightIndex++)
        {
            var left = cases[leftIndex];
            var right = cases[rightIndex];
            var leftShingles = shingles[left.Id];
            var rightShingles = shingles[right.Id];
            var sharedCount = leftShingles.Count(rightShingles.Contains);
            var unionCount = leftShingles.Count + rightShingles.Count - sharedCount;
            var exactScore = (double)sharedCount / unionCount;
            var pairId = "hxz-polish-reg-near-dup-" + Hash(string.Join("\n", left.Id, right.Id))[..16];
            scoredPairs.Add((new Pair(pairId, left.Id, right.Id, left.FamilyId, right.FamilyId, left.Split, right.Split,
                Math.Round(exactScore, 6, MidpointRounding.AwayFromZero), sharedCount, unionCount), exactScore));
        }

        var candidatePairs = scoredPairs.Where(item => item.ExactScore >= candidateThreshold).Select(item => item.Pair)
            .OrderByDescending(item => item.Similarity).ThenBy(item => item.LeftCaseId, StringComparer.Ordinal)
            .ThenBy(item => item.RightCaseId, StringComparer.Ordinal).ToArray();
        var candidateJsonl = string.Join(Environment.NewLine, candidatePairs.Select(pair => JsonSerializer.Serialize(new
        {
            schema_version = "1.0", pair.PairId, pair.LeftCaseId, pair.RightCaseId, pair.LeftFamilyId, pair.RightFamilyId,
            pair.LeftSplit, pair.RightSplit, char_trigram_jaccard = pair.Similarity, pair.SharedShingleCount,
            pair.UnionShingleCount, ruleset_id = rulesetId, adjudication_status = "pending_human_adjudication"
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })));
        if (candidatePairs.Length > 0) candidateJsonl += Environment.NewLine;
        var reviewTemplateJsonl = string.Join(Environment.NewLine, candidatePairs.Select(pair => JsonSerializer.Serialize(new
        {
            schema_version = "1.0", pair.PairId, pair.LeftCaseId, pair.RightCaseId, pair.LeftFamilyId, pair.RightFamilyId,
            pair.LeftSplit, pair.RightSplit, char_trigram_jaccard = pair.Similarity,
            reviewer_id = (string?)null, reviewed_at_utc = (string?)null, decision = (string?)null,
            rationale = (string?)null, status = "pending_human_adjudication"
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })));
        if (candidatePairs.Length > 0) reviewTemplateJsonl += Environment.NewLine;

        var sensitivity = sensitivityThresholds.Order().Select(threshold => new
        {
            threshold,
            candidate_pair_count = scoredPairs.Count(item => item.ExactScore >= threshold),
            cross_split_pair_count = scoredPairs.Count(item => item.ExactScore >= threshold && item.Pair.LeftSplit != item.Pair.RightSplit)
        }).ToArray();
        var report = new
        {
            schema_version = "1.0",
            report_version = "hxz-polish-reg-near-duplicate-candidates-v1",
            status = "internal_diagnostic_pending_human_adjudication",
            purpose = "internal_near_duplicate_triage",
            phase_0_gate_contribution = 0,
            not_admissible_as_blind_eval = true,
            auto_merge_performed = false,
            threshold_is_calibrated = false,
            threshold_use = "human_review_queue_only",
            ruleset_id = rulesetId,
            candidate_threshold = candidateThreshold,
            normalization,
            shingle_size = shingleSize,
            family_count = cases.Count,
            pair_count = scoredPairs.Count,
            candidate_pair_count = candidatePairs.Length,
            cross_split_candidate_pair_count = candidatePairs.Count(pair => pair.LeftSplit != pair.RightSplit),
            sensitivity,
            source_cases_sha256 = casesHash,
            parent_manifest_sha256 = parentManifestHash,
            rules_sha256 = rulesHash,
            limitations = "Character-trigram Jaccard only queues pairs for human review; the threshold is uncalibrated, candidates are not duplicates by definition, and no family, split, or label is changed."
        };

        var output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        var pairsPath = Path.Combine(output, "candidate-pairs.jsonl");
        var templatePath = Path.Combine(output, "human-adjudication.template.jsonl");
        var reportPath = Path.Combine(output, "report.json");
        var manifestPath = Path.Combine(output, "manifest.json");
        ImmutableArtifactWriter.WriteNew(pairsPath, candidateJsonl);
        ImmutableArtifactWriter.WriteNew(templatePath, reviewTemplateJsonl);
        ImmutableArtifactWriter.WriteNew(reportPath, JsonSerializer.Serialize(report, JsonOptions) + Environment.NewLine);
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["candidate-pairs.jsonl"] = HashFile(pairsPath),
            ["human-adjudication.template.jsonl"] = HashFile(templatePath),
            ["report.json"] = HashFile(reportPath)
        };
        var manifest = new
        {
            schema_version = "1.0", artifact_version = "hxz-polish-reg-near-duplicate-candidates-v1",
            status = "internal_diagnostic_pending_human_adjudication", purpose = "internal_near_duplicate_triage",
            parent_manifest_sha256 = parentManifestHash, source_cases_sha256 = casesHash, rules_sha256 = rulesHash,
            phase_0_gate_contribution = 0, not_admissible_as_blind_eval = true, auto_merge_performed = false, files
        };
        ImmutableArtifactWriter.WriteNew(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine);
        return new(cases.Count, scoredPairs.Count, candidatePairs.Length,
            candidatePairs.Count(pair => pair.LeftSplit != pair.RightSplit), files);
    }

    private static List<Case> ReadCases(string path)
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
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(familyId) || string.IsNullOrWhiteSpace(input) ||
                split is not ("development" or "regression"))
                throw new InvalidDataException($"cases.jsonl 第 {lineNumber} 行缺少有效 id/family_id/input/split。");
            if (!ids.Add(id) || !families.Add(familyId)) throw new InvalidDataException($"cases.jsonl 第 {lineNumber} 行包含重复 case/family id。");
            items.Add(new(id, familyId, split, input));
        }
        items.Sort((left, right) => StringComparer.Ordinal.Compare(left.Id, right.Id));
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
