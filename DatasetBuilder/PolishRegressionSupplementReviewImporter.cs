using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Huaxiazi.DatasetBuilder;

public sealed record PolishRegressionSupplementReviewImportResult(int ImportedCount, int AcceptedCount, int EditedCount,
    int RejectedCount, bool ReviewerIdentityVerified, int Phase0GateContribution, string OutputPath, string OutputSha256);

/// <summary>Writes an immutable decision log after structural validation; it does not merge or promote the reviewed cases.</summary>
public static class PolishRegressionSupplementReviewImporter
{
    public static PolishRegressionSupplementReviewImportResult Import(string casesPath, string reviewCsvPath,
        string reviewManifestPath, string outputPath)
    {
        foreach (var value in new[] { casesPath, reviewCsvPath, reviewManifestPath, outputPath })
            ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var fullCasesPath = Path.GetFullPath(casesPath);
        var fullCsvPath = Path.GetFullPath(reviewCsvPath);
        var fullManifestPath = Path.GetFullPath(reviewManifestPath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        if (!File.Exists(fullCasesPath) || !File.Exists(fullCsvPath) || !File.Exists(fullManifestPath))
            throw new FileNotFoundException("cases.jsonl、审阅 CSV 或审阅 manifest 不存在。");

        var protectedDirectories = new[]
        {
            Path.GetDirectoryName(fullCasesPath)!,
            Path.GetDirectoryName(fullCsvPath)!,
            Path.GetDirectoryName(fullManifestPath)!
        };
        if (protectedDirectories.Any(directory => IsWithinDirectory(fullOutputPath, directory)))
            throw new InvalidDataException("output-boundary: 决策日志必须写到源数据集和审阅包目录之外。");

        var validation = PolishRegressionSupplementReviewValidator.Validate(fullCasesPath, fullCsvPath, fullManifestPath);
        if (!validation.Valid)
        {
            var codes = validation.Issues.Select(issue => issue.Code).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            throw new InvalidDataException($"review-invalid: 人工审阅尚未通过结构校验，问题类别：{string.Join(",", codes)}。");
        }

        var casesBytes = File.ReadAllBytes(fullCasesPath);
        var sourceHash = Hash(casesBytes);
        var cases = PolishRegressionSupplementReviewValidator.ReadCases(casesBytes, (_, _, _) => { });
        var rows = PolishRegressionSupplementReviewValidator.ReadCsv(fullCsvPath, (_, _, _) => { });
        var decisions = new List<string>(rows.Count);
        var accepted = 0;
        var edited = 0;
        var rejected = 0;
        foreach (var (_, fields) in rows)
        {
            var caseId = fields["case_id"].Trim();
            var source = cases[caseId];
            var verdict = fields["human_verdict"].Trim();
            var finalDecision = verdict == "reject" ? null : fields["human_expected_decision"].Trim();
            var finalOutput = verdict switch
            {
                "accept" => NullableString(source, "reference_output"),
                "edit" => fields["human_edited_reference_output"],
                _ => null
            };
            if (verdict == "accept") accepted++;
            else if (verdict == "edit") edited++;
            else rejected++;

            var record = new
            {
                schema_version = "hxz-polish-regression-supplement-review-v1",
                case_id = caseId,
                family_id = String(source, "family_id"),
                source_cases_sha256 = sourceHash,
                decision = new { verdict, expected_decision = finalDecision, reference_output = finalOutput },
                quality = new
                {
                    fact_preservation = fields["fact_preservation"].Trim(),
                    constraint_following = fields["constraint_following"].Trim(),
                    format_and_tone = fields["format_and_tone"].Trim()
                },
                reviewer = new
                {
                    reviewer_id = fields["reviewer_id"].Trim(),
                    reviewed_at_utc = fields["reviewed_at_utc"].Trim(),
                    identity_verified = false
                },
                rationale = fields["rationale"].Trim(),
                human_status = verdict switch { "accept" => "accepted", "edit" => "edited", _ => "rejected" },
                phase_0_gate_contribution = 0
            };
            decisions.Add(JsonSerializer.Serialize(record, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        }

        var content = string.Join('\n', decisions) + "\n";
        ImmutableArtifactWriter.WriteNew(fullOutputPath, content);
        return new(decisions.Count, accepted, edited, rejected, false, 0, fullOutputPath,
            Hash(Encoding.UTF8.GetBytes(content)));
    }

    private static bool IsWithinDirectory(string path, string directory)
    {
        var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.Equals(fullDirectory, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(fullDirectory + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NullableString(JsonElement root, string name)
    {
        var value = Property(root, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static JsonElement Property(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value : default;
    private static string String(JsonElement root, string name) =>
        Property(root, name).ValueKind == JsonValueKind.String ? Property(root, name).GetString() ?? "" : "";
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
