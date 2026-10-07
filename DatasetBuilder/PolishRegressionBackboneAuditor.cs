using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Huaxiazi.DatasetBuilder;

public sealed record PolishRegressionBackboneSummary(string BackboneId, int RowCount, int IntentCount,
    int DistinctReferenceOutputCount, IReadOnlyDictionary<string, int> SplitRowCounts);
public sealed record PolishRegressionBackboneOverlap(string LeftSplit, string RightSplit, int SharedBackboneCount,
    int LeftBackboneCount, int RightBackboneCount);
public sealed record PolishRegressionBackboneAuditReport(string SchemaVersion, string Purpose, int Phase0GateContribution,
    int RowCount, int MatchedLegacyIntentSuffixRowCount, int DistinctBackboneCount,
    IReadOnlyList<PolishRegressionBackboneSummary> Backbones,
    IReadOnlyList<PolishRegressionBackboneOverlap> SplitBackboneOverlap,
    string SourceCanonicalSha256, IReadOnlyList<string> Limitations);

/// <summary>Audits repeated factual content after removing the legacy generator's appended purpose/style suffix.</summary>
public static class PolishRegressionBackboneAuditor
{
    private sealed record Row(string Split, string Intent, string BackboneKey, string Output);
    private sealed record Backbone(string Key, IReadOnlyList<Row> Rows);

    public static PolishRegressionBackboneAuditReport Audit(string canonicalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);
        var rows = new List<Row>();
        var matchedSuffixCount = 0;
        foreach (var (line, index) in File.ReadLines(canonicalPath, Encoding.UTF8).Select((line, index) => (line, index)))
        {
            if (string.IsNullOrWhiteSpace(line)) throw new InvalidDataException($"canonical JSONL 第 {index + 1} 行为空。");
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var input = String(root, "input");
            var split = String(root, "split");
            var decision = String(root, "expected_decision");
            var context = Property(root, "context");
            var purpose = String(context, "purpose");
            var claims = Property(root, "claims");
            var output = String(Property(root, "output"), "content");
            if (input.Length == 0 || purpose.Length == 0 || split.Length == 0 || decision.Length == 0 || output.Length == 0 ||
                context.ValueKind != JsonValueKind.Object || claims.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"canonical JSONL 第 {index + 1} 行缺少骨架审计字段。");

            var suffix = $" {purpose}，语气自然一点，别写得太客套。";
            var coreInput = input;
            if (input.EndsWith(suffix, StringComparison.Ordinal))
            {
                coreInput = input[..^suffix.Length];
                matchedSuffixCount++;
            }
            coreInput = Regex.Replace(coreInput.Normalize(NormalizationForm.FormKC).Trim(), @"\s+", " ");
            var contextAnchors = ContextWithoutPurpose(context);
            var backboneKey = JsonSerializer.Serialize(new
            {
                core_input = coreInput,
                expected_decision = decision,
                claims = claims.GetRawText(),
                context_anchors = contextAnchors
            });
            rows.Add(new(split, purpose, backboneKey, output));
        }

        var backbones = rows.GroupBy(row => row.BackboneKey, StringComparer.Ordinal)
            .Select(group => new Backbone(group.Key, group.ToArray())).OrderBy(group => group.Key, StringComparer.Ordinal).ToArray();
        var summaries = backbones.Select(backbone => new PolishRegressionBackboneSummary(
            "hxz-polish-backbone-" + Hash(backbone.Key)[..16], backbone.Rows.Count,
            backbone.Rows.Select(row => row.Intent).Distinct(StringComparer.Ordinal).Count(),
            backbone.Rows.Select(row => row.Output).Distinct(StringComparer.Ordinal).Count(),
            Count(backbone.Rows.Select(row => row.Split)))).ToArray();

        var splits = rows.Select(row => row.Split).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var backboneIdsBySplit = splits.ToDictionary(split => split,
            split => backbones.Where(backbone => backbone.Rows.Any(row => row.Split == split)).Select(backbone => Hash(backbone.Key)).ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);
        var overlap = new List<PolishRegressionBackboneOverlap>();
        for (var left = 0; left < splits.Length; left++)
        for (var right = left + 1; right < splits.Length; right++)
        {
            var leftSplit = splits[left];
            var rightSplit = splits[right];
            overlap.Add(new(leftSplit, rightSplit, backboneIdsBySplit[leftSplit].Intersect(backboneIdsBySplit[rightSplit], StringComparer.Ordinal).Count(),
                backboneIdsBySplit[leftSplit].Count, backboneIdsBySplit[rightSplit].Count));
        }

        return new("1.0", "internal_regression_only", 0, rows.Count, matchedSuffixCount, backbones.Length, summaries,
            overlap, HashFile(canonicalPath),
            [
                "Backbones are diagnostic groupings formed by stripping only the known legacy purpose/style suffix; they are not reviewed semantic gold labels.",
                "The legacy corpus contains repeated factual backbones across its development and test splits; its test split cannot measure unseen-scenario generalization.",
                "This diagnostic contributes zero samples to formal phase 0 blind evaluation."
            ]);
    }

    private static IReadOnlyDictionary<string, int> Count(IEnumerable<string> values) =>
        values.GroupBy(value => value, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
    private static JsonElement Property(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value : default;
    private static string String(JsonElement root, string name) => Property(root, name).ValueKind == JsonValueKind.String ? Property(root, name).GetString() ?? "" : "";
    private static SortedDictionary<string, JsonElement> ContextWithoutPurpose(JsonElement context)
    {
        var values = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        if (context.ValueKind == JsonValueKind.Object)
            foreach (var property in context.EnumerateObject())
                if (property.Name != "purpose") values[property.Name] = property.Value.Clone();
        return values;
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
