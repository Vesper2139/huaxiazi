using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Huaxiazi.DatasetBuilder;

public sealed record PolishRegressionBuildSummary(int RowCount, int FamilyCount, int DevelopmentFamilyCount, int RegressionFamilyCount,
    IReadOnlyDictionary<string, string> FileSha256);

/// <summary>Creates provisional, internal-only exact semantic-family artifacts from the legacy synthetic corpus.</summary>
public static class PolishRegressionDatasetBuilder
{
    private const string Origin = "project_synthetic_legacy";
    private const string NormalizationVersion = "exact-whitespace-punctuation-v1";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };
    private static readonly JsonSerializerOptions JsonLineOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private sealed record SourceRow(JsonElement Root, string Id, string LegacySplit, string Input, string Output,
        string TaskIntent, string Decision, string Claims, string Context, string Family, string Risk, string Scenario, string Channel, string Formality);
    private sealed record BuiltFamily(string FamilyId, string SemanticKeySha256, string Split, string FamilyStatus,
        string GroupingMethod, IReadOnlyList<string> MemberLegacyTemplateFamilies, IReadOnlyDictionary<string, int> LegacySplitCounts,
        IReadOnlyList<string> MemberIds, IReadOnlyList<object> Members, SourceRow Representative, int RowCount);

    public static PolishRegressionBuildSummary Build(string canonicalPath, string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var canonicalFullPath = Path.GetFullPath(canonicalPath);
        var outputFullPath = Path.GetFullPath(outputDirectory);
        var rows = ReadRows(canonicalFullPath);
        if (rows.Count == 0) throw new InvalidDataException("输入 canonical 为空，拒绝生成空回归集。");

        var groups = rows.GroupBy(FamilyKey, StringComparer.Ordinal)
            .Select(group => new { Key = group.Key, Rows = group.OrderBy(row => row.Id, StringComparer.Ordinal).ToArray() })
            .OrderBy(group => group.Key, StringComparer.Ordinal).ToArray();
        var families = groups.Select(group =>
        {
            var keyHash = Hash(group.Key);
            var regression = Convert.ToUInt32(keyHash[..8], 16) % 10 < 2;
            return new BuiltFamily(
                "hxz-polish-reg-family-" + keyHash[..16], keyHash, regression ? "regression" : "development",
                "provisional_exact_key_pending_human_review", NormalizationVersion,
                group.Rows.Select(row => row.Family).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                Count(group.Rows.Select(row => row.LegacySplit)),
                group.Rows.Select(row => row.Id).ToArray(),
                group.Rows.Select(row => (object)new { id = row.Id, input_sha256 = Hash(row.Input), record_sha256 = Hash(row.Root.GetRawText()) }).ToArray(),
                group.Rows[0], group.Rows.Length);
        }).ToArray();

        Directory.CreateDirectory(outputFullPath);
        var casesPath = Path.Combine(outputFullPath, "cases.jsonl");
        var variantsPath = Path.Combine(outputFullPath, "variants.jsonl");
        var familiesPath = Path.Combine(outputFullPath, "families.json");
        var coveragePath = Path.Combine(outputFullPath, "coverage.json");
        var gapsPath = Path.Combine(outputFullPath, "gaps.json");

        var familyByLegacyId = families.SelectMany(family => family.MemberIds.Select(id => (id, family))).ToDictionary(pair => pair.id, pair => pair.family, StringComparer.Ordinal);
        var caseLines = families.Select(family => JsonSerializer.Serialize(new
        {
            schema_version = "1.0", id = "hxz-polish-reg-" + family.Representative.Id.Replace("hxz-polish-agent-v2-", "", StringComparison.Ordinal),
            family_id = family.FamilyId, split = family.Split, task = "polish", input = family.Representative.Input,
            context = JsonDocument.Parse(family.Representative.Context).RootElement.Clone(),
            claims = JsonDocument.Parse(family.Representative.Claims).RootElement.Clone(),
            expected_decision = family.Representative.Decision, reference_output = family.Representative.Output,
            origin = Origin,
            provenance = new { source_dataset = "datasets/polish-agent-v2/canonical.jsonl", source_record_id = family.Representative.Id, source_sha256 = Hash(family.Representative.Root.GetRawText()), transform_version = NormalizationVersion },
            human_status = "unreviewed", family_status = family.FamilyStatus,
            limitations = "Synthetic internal case; not a human-verified gold label."
        }, JsonLineOptions)).ToArray();
        var variantLines = rows.OrderBy(row => row.Id, StringComparer.Ordinal).Select(row =>
        {
            var family = familyByLegacyId[row.Id];
            return JsonSerializer.Serialize(new
            {
                schema_version = "1.0", id = row.Id.Replace("hxz-polish-agent-v2-", "hxz-polish-reg-variant-", StringComparison.Ordinal),
                family_id = family.FamilyId, split = family.Split, legacy_split = row.LegacySplit,
                source_record_id = row.Id, source_record_sha256 = Hash(row.Root.GetRawText()), input_sha256 = Hash(row.Input),
                origin = Origin, human_status = "unreviewed"
            }, JsonLineOptions);
        }).ToArray();
        var familiesDocument = new
        {
            schema_version = "1.0", dataset_version = "hxz-polish-regression-v1", normalization_version = NormalizationVersion,
            family_count = families.Length, row_count = rows.Count,
            exact_key_only = true, near_duplicate_adjudication_required = true,
            families = families.Select(family => new
            {
                family_id = family.FamilyId, semantic_key_sha256 = family.SemanticKeySha256, split = family.Split,
                family_status = family.FamilyStatus, grouping_method = family.GroupingMethod,
                near_duplicate_adjudication = "not_performed", member_legacy_template_families = family.MemberLegacyTemplateFamilies,
                legacy_split_counts = family.LegacySplitCounts, member_ids = family.MemberIds, members = family.Members
            }).ToArray()
        };
        var coverage = BuildCoverage(families);
        var gaps = BuildGaps();

        // Each output is created once. A partial build leaves files in place and must be retried into a fresh version directory.
        ImmutableArtifactWriter.WriteNew(casesPath, string.Join(Environment.NewLine, caseLines) + Environment.NewLine);
        ImmutableArtifactWriter.WriteNew(variantsPath, string.Join(Environment.NewLine, variantLines) + Environment.NewLine);
        ImmutableArtifactWriter.WriteNew(familiesPath, JsonSerializer.Serialize(familiesDocument, JsonOptions) + Environment.NewLine);
        ImmutableArtifactWriter.WriteNew(coveragePath, JsonSerializer.Serialize(coverage, JsonOptions) + Environment.NewLine);
        ImmutableArtifactWriter.WriteNew(gapsPath, JsonSerializer.Serialize(gaps, JsonOptions) + Environment.NewLine);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cases.jsonl"] = HashFile(casesPath), ["variants.jsonl"] = HashFile(variantsPath),
            ["families.json"] = HashFile(familiesPath), ["coverage.json"] = HashFile(coveragePath), ["gaps.json"] = HashFile(gapsPath)
        };
        return new(rows.Count, families.Length, families.Count(family => family.Split == "development"), families.Count(family => family.Split == "regression"), hashes);
    }

    private static List<SourceRow> ReadRows(string path)
    {
        var rows = new List<SourceRow>();
        var lineNumber = 0;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) throw new InvalidDataException($"输入第 {lineNumber} 行为空。");
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement.Clone();
            var provenance = Prop(root, "provenance");
            var behavior = Prop(root, "agent_behavior");
            var output = Prop(root, "output");
            var context = Prop(root, "context");
            var claims = Prop(root, "claims");
            if (!String(root, "id", out var id) || !String(root, "split", out var split) || !String(root, "input", out var input) ||
                !String(root, "expected_decision", out var decision) || !String(provenance, "source_template_family", out var family) ||
                !String(behavior, "intent", out var intent) || !String(output, "content", out var outputContent) ||
                context.ValueKind != JsonValueKind.Object || claims.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"输入第 {lineNumber} 行缺少归族所需字段。");
            rows.Add(new(root, id, split, input, outputContent, intent, decision, claims.GetRawText(), context.GetRawText(), family,
                Value(root, "risk_level"), Value(root, "scenario"), Value(root, "channel"), Value(context, "formality")));
        }
        return rows;
    }

    private static string FamilyKey(SourceRow row)
    {
        var normalizedInput = Regex.Replace(row.Input.Normalize(NormalizationForm.FormKC).Trim(), @"\s+", " ");
        return JsonSerializer.Serialize(new
        {
            normalized_input = normalizedInput,
            task_intent = row.TaskIntent,
            expected_decision = row.Decision,
            claims = row.Claims,
            context = row.Context
        });
    }

    private static object BuildCoverage(BuiltFamily[] families)
    {
        var reps = families.Select(family => family.Representative).ToArray();
        return new
        {
            schema_version = "1.0", unit = "semantic_family", family_count = families.Length,
            source_row_count = families.Sum(family => family.RowCount),
            dimensions = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["decision"] = Count(reps.Select(row => row.Decision)), ["scenario"] = Count(reps.Select(row => row.Scenario)),
                ["channel"] = Count(reps.Select(row => row.Channel)), ["risk_level"] = Count(reps.Select(row => row.Risk)),
                ["formality"] = Count(reps.Select(row => EmptyAsUnknown(row.Formality))),
                ["claims_present"] = Count(reps.Select(row => row.Claims == "[]" ? "no" : "yes")),
                ["input_style"] = new Dictionary<string, int> { ["unlabeled"] = families.Length },
                ["format_requirement"] = new Dictionary<string, int> { ["unlabeled"] = families.Length },
                ["multi_turn"] = new Dictionary<string, int> { ["present_in_source"] = 0, ["unlabeled"] = families.Length }
            },
            limitations = "Coverage counts exact-key provisional families; it is not a human-reviewed distribution."
        };
    }

    private static object BuildGaps() => new
    {
        schema_version = "1.0", unit = "semantic_family", status = "requires_human_triage_before_supplementation",
        gaps = new[]
        {
            Gap("clarification_positive_examples", 0), Gap("needs_clarification_outputs", 0), Gap("format_and_schema_requirements", 0),
            Gap("high_risk_fact_reversal", 0), Gap("tone_and_scenario_diversity", null), Gap("multi_turn_revision_and_user_negation", 0),
            Gap("negation_quantity_time_condition_boundaries", null), Gap("prompt_injection_resistance", 0), Gap("tool_failure", 0),
            Gap("non_workplace_scenarios", null), Gap("cross_language_mixing", null)
        },
        supplementation = new { authorized = false, maximum_new_families_if_later_approved = 120, reason = "No supplementation before human triage, case specifications, and review. Phase 0 contribution remains zero." }
    };

    private static object Gap(string name, int? knownPositiveFamilyCount) => new
    {
        behavior = name, known_positive_family_count = knownPositiveFamilyCount,
        internal_regression_need = "pending_human_triage", supplementation_action = "not_started"
    };

    private static string EmptyAsUnknown(string value) => string.IsNullOrWhiteSpace(value) ? "unlabeled" : value;
    private static JsonElement Prop(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value : default;
    private static string Value(JsonElement root, string name) => Prop(root, name).ValueKind == JsonValueKind.String ? Prop(root, name).GetString() ?? "" : "";
    private static bool String(JsonElement root, string name, out string value) { value = Value(root, name); return value.Length > 0; }
    private static IReadOnlyDictionary<string, int> Count(IEnumerable<string> values) => values.GroupBy(value => value, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
