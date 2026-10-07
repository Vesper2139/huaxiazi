using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Huaxiazi.DatasetBuilder;

public sealed record PolishRegressionPairwiseInputOverlap(string LeftSplit, string RightSplit, int SharedDistinctInputs, int LeftDistinctInputs, int RightDistinctInputs);
public sealed record PolishRegressionRepeatedInput(string InputSha256, int RowCount);
public sealed record PolishRegressionTemplateFamilySummary(string Family, int RowCount, int DistinctInputCount, string Split);
public sealed record PolishRegressionBaselineReport(
    string SchemaVersion,
    string AuditRuleVersion,
    string Purpose,
    int Phase0GateContribution,
    bool NotAdmissibleAsBlindEval,
    int RowCount,
    int DistinctInputCount,
    int DistinctReferenceOutputCount,
    int TemplateFamilyCount,
    int ClarificationQuestionRowCount,
    int DistinctClaimsObjectCount,
    int NonEmptyClaimsRowCount,
    int DistinctNonEmptyClaimsObjectCount,
    IReadOnlyDictionary<string, int> SplitRowCounts,
    IReadOnlyDictionary<string, int> ExpectedDecisionCounts,
    IReadOnlyDictionary<string, int> OutputKindCounts,
    IReadOnlyDictionary<string, int> ReviewerCountCounts,
    IReadOnlyList<PolishRegressionPairwiseInputOverlap> SplitInputOverlap,
    IReadOnlyList<PolishRegressionRepeatedInput> TopRepeatedInputs,
    IReadOnlyList<PolishRegressionTemplateFamilySummary> TemplateFamilies,
    IReadOnlyDictionary<string, string> InputFileSha256,
    IReadOnlyList<string> Limitations);

/// <summary>Reproducible audit of the legacy synthetic corpus; this report is never blind-evaluation evidence.</summary>
public static class PolishRegressionBaselineAuditor
{
    private sealed record Row(JsonElement Root, string Split, string Input, string Output, string Decision, string OutputKind,
        string Family, string Claims, int ReviewerCount, bool HasClarification);

    public static PolishRegressionBaselineReport Audit(string canonicalPath, string sftPath, string manifestPath, string sourcePath)
    {
        foreach (var path in new[] { canonicalPath, sftPath, manifestPath, sourcePath }) ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var rows = ReadCanonical(canonicalPath);
        var splitNames = new[] { "train", "dev", "test" };
        var splitInputs = splitNames.ToDictionary(name => name,
            name => rows.Where(row => row.Split == name).Select(row => row.Input).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var overlap = new List<PolishRegressionPairwiseInputOverlap>();
        for (var left = 0; left < splitNames.Length; left++)
        for (var right = left + 1; right < splitNames.Length; right++)
        {
            var leftName = splitNames[left];
            var rightName = splitNames[right];
            overlap.Add(new(leftName, rightName, splitInputs[leftName].Intersect(splitInputs[rightName], StringComparer.Ordinal).Count(),
                splitInputs[leftName].Count, splitInputs[rightName].Count));
        }

        var templateFamilies = rows.GroupBy(row => row.Family, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new PolishRegressionTemplateFamilySummary(group.Key, group.Count(), group.Select(row => row.Input).Distinct(StringComparer.Ordinal).Count(),
                group.Select(row => row.Split).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Aggregate((a, b) => a == b ? a : "cross-split")))
            .ToArray();
        var repeated = rows.GroupBy(row => row.Input, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal).Take(10)
            .Select(group => new PolishRegressionRepeatedInput(Hash(group.Key), group.Count())).ToArray();

        return new(
            "1.0", "polish-regression-baseline-v1", "internal_regression_only", 0, true,
            rows.Count,
            rows.Select(row => row.Input).Distinct(StringComparer.Ordinal).Count(),
            rows.Select(row => row.Output).Distinct(StringComparer.Ordinal).Count(),
            templateFamilies.Length,
            rows.Count(row => row.HasClarification),
            rows.Select(row => row.Claims).Distinct(StringComparer.Ordinal).Count(),
            rows.Count(row => row.Claims != "[]"),
            rows.Where(row => row.Claims != "[]").Select(row => row.Claims).Distinct(StringComparer.Ordinal).Count(),
            Count(rows.Select(row => row.Split)),
            Count(rows.Select(row => row.Decision)),
            Count(rows.Select(row => row.OutputKind)),
            Count(rows.Select(row => row.ReviewerCount.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            overlap,
            repeated,
            templateFamilies,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["canonical"] = HashFile(canonicalPath), ["sft"] = HashFile(sftPath),
                ["manifest"] = HashFile(manifestPath), ["source"] = HashFile(sourcePath)
            },
            [
                "Synthetic template-rotation smoke corpus only; not representative of real users.",
                "Rows are not independent samples; coverage decisions must be made at semantic-family level.",
                "This report contributes zero samples to formal phase 0 blind evaluation.",
                "Legacy split overlap means the test split does not measure unseen-input generalization."
            ]);
    }

    private static List<Row> ReadCanonical(string path)
    {
        var rows = new List<Row>();
        var lineNumber = 0;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) throw new InvalidDataException($"canonical JSONL 第 {lineNumber} 行为空。");
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement.Clone();
            var provenance = Property(root, "provenance");
            var output = Property(root, "output");
            var review = Property(root, "review");
            var clarification = Property(root, "clarification_questions");
            var claims = Property(root, "claims");
            if (root.ValueKind != JsonValueKind.Object ||
                !TryString(root, "split", out var split) || !TryString(root, "input", out var input) ||
                !TryString(output, "content", out var outputContent) || !TryString(root, "expected_decision", out var decision) ||
                !TryString(output, "kind", out var outputKind) || !TryString(provenance, "source_template_family", out var family))
                throw new InvalidDataException($"canonical JSONL 第 {lineNumber} 行缺少审计所需字段。");
            var reviewerCount = review.ValueKind == JsonValueKind.Object && review.TryGetProperty("reviewer_count", out var reviewerValue) && reviewerValue.TryGetInt32(out var parsedReviewer) ? parsedReviewer : 0;
            rows.Add(new(root, split, input, outputContent, decision, outputKind, family,
                claims.ValueKind == JsonValueKind.Undefined ? "<missing>" : claims.GetRawText(), reviewerCount,
                clarification.ValueKind == JsonValueKind.Array && clarification.GetArrayLength() > 0));
        }
        return rows;
    }

    private static IReadOnlyDictionary<string, int> Count(IEnumerable<string> values) =>
        values.GroupBy(value => value, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    private static JsonElement Property(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value : default;

    private static bool TryString(JsonElement root, string name, out string value)
    {
        var element = Property(root, name);
        value = element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : string.Empty;
        return value.Length > 0;
    }

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
