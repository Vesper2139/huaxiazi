using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Huaxiazi.DatasetBuilder;

public sealed record PolishRegressionTaskFamilyCandidate(string FamilyId, string Split, string Intent, int RepresentativeCount,
    int DistinctReferenceOutputCount);
public sealed record PolishRegressionBackboneCandidate(string BackboneId, string Status, int TaskFamilyCount,
    int RepresentativeCount, int DistinctReferenceOutputCountAcrossIntents, string ReferenceOutputAlignmentStatus,
    IReadOnlyDictionary<string, int> SplitFamilyCounts,
    IReadOnlyList<PolishRegressionTaskFamilyCandidate> TaskFamilies);
public sealed record PolishRegressionHierarchySplitOverlap(string LeftSplit, string RightSplit, int SharedBackboneCount,
    int LeftBackboneCount, int RightBackboneCount);
public sealed record PolishRegressionIntentBackboneCoverage(string Intent, int TaskFamilyCount, int IndependentBackboneCount,
    int SuggestedMinimumIndependentFamilies, int ShortfallToSuggestedMinimum);
public sealed record PolishRegressionHierarchyReport(string SchemaVersion, string Purpose, int Phase0GateContribution,
    int TaskFamilyCount, int BackboneCount, int MatchedLegacyIntentSuffixFamilyCount, IReadOnlyList<PolishRegressionBackboneCandidate> Backbones,
    IReadOnlyList<PolishRegressionHierarchySplitOverlap> SplitBackboneOverlap,
    IReadOnlyList<PolishRegressionIntentBackboneCoverage> IntentBackboneCoverage,
    string RecommendedTrackMode, string IntentSmokeTrackStatus, string UnseenBackboneTrackStatus,
    IReadOnlyList<string> Limitations);

/// <summary>Maps frozen task-family representatives to provisional scenario backbones for human adjudication.</summary>
public static class PolishRegressionHierarchyAuditor
{
    private sealed record Case(string FamilyId, string Split, string Intent, string BackboneKey, string Output, bool MatchedLegacyIntentSuffix);

    public static PolishRegressionHierarchyReport Audit(string casesJsonlPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(casesJsonlPath);
        var cases = new List<Case>();
        var lineNumber = 0;
        foreach (var line in File.ReadLines(casesJsonlPath, Encoding.UTF8))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) throw new InvalidDataException($"cases.jsonl 第 {lineNumber} 行为空。");
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var familyId = String(root, "family_id");
            var split = String(root, "split");
            var input = String(root, "input");
            var decision = String(root, "expected_decision");
            var output = String(root, "reference_output");
            var context = Property(root, "context");
            var purpose = String(context, "purpose");
            var claims = Property(root, "claims");
            if (familyId.Length == 0 || split.Length == 0 || input.Length == 0 || decision.Length == 0 || output.Length == 0 ||
                purpose.Length == 0 || context.ValueKind != JsonValueKind.Object || claims.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"cases.jsonl 第 {lineNumber} 行缺少层级审计字段。");

            var suffix = $" {purpose}，语气自然一点，别写得太客套。";
            var suffixMatched = input.EndsWith(suffix, StringComparison.Ordinal);
            var coreInput = suffixMatched ? input[..^suffix.Length] : input;
            coreInput = Regex.Replace(coreInput.Normalize(NormalizationForm.FormKC).Trim(), @"\s+", " ");
            var contextAnchors = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in context.EnumerateObject())
                if (property.Name != "purpose") contextAnchors[property.Name] = property.Value.Clone();
            var key = JsonSerializer.Serialize(new { core_input = coreInput, expected_decision = decision, claims = claims.GetRawText(), context_anchors = contextAnchors });
            cases.Add(new(familyId, split, purpose, key, output, suffixMatched));
        }

        if (cases.Select(item => item.FamilyId).Distinct(StringComparer.Ordinal).Count() != cases.Count)
            throw new InvalidDataException("cases.jsonl 中 family_id 重复；每行必须是一条族代表。");
        var taskFamilies = cases.GroupBy(item => item.FamilyId, StringComparer.Ordinal).ToArray();
        var backbones = cases.GroupBy(item => item.BackboneKey, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new PolishRegressionBackboneCandidate(
                "hxz-polish-backbone-" + Hash(group.Key)[..16], "provisional_requires_human_review",
                group.Select(item => item.FamilyId).Distinct(StringComparer.Ordinal).Count(), group.Count(),
                group.Select(item => item.Output).Distinct(StringComparer.Ordinal).Count(),
                group.Select(item => item.Intent).Distinct(StringComparer.Ordinal).Count() > 1 &&
                group.Select(item => item.Output).Distinct(StringComparer.Ordinal).Count() == 1
                    ? "potential_reference_reuse_requires_review" : "not_flagged_by_reuse_check",
                CountValues(group.GroupBy(item => item.FamilyId, StringComparer.Ordinal).Select(family => family.First().Split)),
                group.GroupBy(item => item.FamilyId, StringComparer.Ordinal).OrderBy(family => family.Key, StringComparer.Ordinal)
                    .Select(family => new PolishRegressionTaskFamilyCandidate(family.Key, family.First().Split, family.First().Intent,
                        family.Count(), family.Select(item => item.Output).Distinct(StringComparer.Ordinal).Count())).ToArray()))
            .ToArray();

        var splitNames = cases.Select(item => item.Split).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var idsBySplit = splitNames.ToDictionary(split => split,
            split => backbones.Where(backbone => backbone.TaskFamilies.Any(family => family.Split == split)).Select(backbone => backbone.BackboneId).ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);
        var overlaps = new List<PolishRegressionHierarchySplitOverlap>();
        for (var left = 0; left < splitNames.Length; left++)
        for (var right = left + 1; right < splitNames.Length; right++)
        {
            var leftSplit = splitNames[left];
            var rightSplit = splitNames[right];
            overlaps.Add(new(leftSplit, rightSplit, idsBySplit[leftSplit].Intersect(idsBySplit[rightSplit], StringComparer.Ordinal).Count(),
                idsBySplit[leftSplit].Count, idsBySplit[rightSplit].Count));
        }

        const int SuggestedMinimumIndependentFamilies = 5;
        var intentCoverage = cases.GroupBy(item => item.Intent, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var familyCount = group.Select(item => item.FamilyId).Distinct(StringComparer.Ordinal).Count();
                var backboneCount = group.Select(item => item.BackboneKey).Distinct(StringComparer.Ordinal).Count();
                return new PolishRegressionIntentBackboneCoverage(group.Key, familyCount, backboneCount,
                    SuggestedMinimumIndependentFamilies, Math.Max(0, SuggestedMinimumIndependentFamilies - backboneCount));
            }).ToArray();
        var sharedOutputAcrossIntentCount = backbones.Count(backbone => backbone.ReferenceOutputAlignmentStatus == "potential_reference_reuse_requires_review");
        var unseenBackboneOverlap = overlaps.Any(item => item.SharedBackboneCount > 0 &&
            new[] { item.LeftSplit, item.RightSplit }.ToHashSet(StringComparer.Ordinal).SetEquals(["development", "regression"]));
        var meetsSuggestedFloor = intentCoverage.Length > 0 && intentCoverage.All(item => item.IndependentBackboneCount >= SuggestedMinimumIndependentFamilies);

        return new("1.0", "internal_regression_only", 0, taskFamilies.Length, backbones.Length,
            cases.Count(item => item.MatchedLegacyIntentSuffix), backbones, overlaps, intentCoverage,
            "separate_tracks",
            sharedOutputAcrossIntentCount > 0 ? "pending_human_review_of_reference_reuse" : "pending_human_review",
            !unseenBackboneOverlap && meetsSuggestedFloor ? "candidate_for_human_review" : "insufficient_independent_backbones_or_split_leakage",
            [
                "Backbone IDs are deterministic candidates, not human-adjudicated semantic gold labels.",
                "A backbone shared across splits cannot support unseen-scenario generalization claims.",
                "The task-family representatives are inherited synthetic records; labels and references remain unreviewed.",
                "This hierarchy report contributes zero samples to formal phase 0 blind evaluation."
            ]);
    }

    private static IReadOnlyDictionary<string, int> CountValues(IEnumerable<string> values) =>
        values.GroupBy(value => value, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
    private static JsonElement Property(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value : default;
    private static string String(JsonElement root, string name) => Property(root, name).ValueKind == JsonValueKind.String ? Property(root, name).GetString() ?? "" : "";
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
