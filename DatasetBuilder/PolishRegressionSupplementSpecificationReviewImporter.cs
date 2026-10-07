using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;

namespace Huaxiazi.DatasetBuilder;

public sealed record PolishRegressionSupplementSpecificationReviewImportReport(bool Valid, int SpecificationCount,
    int ApprovedCount, int EditedCount, int RejectedCount, bool GenerationAuthorized, bool ReviewerIdentityVerified,
    int Phase0GateContribution, string OutputDirectory);

/// <summary>Imports validated W4.3 decisions into a new immutable review artifact and internal generation input.</summary>
public static class PolishRegressionSupplementSpecificationReviewImporter
{
    private static readonly string[] Headers =
    [
        "spec_id", "target_behavior", "scenario", "facts_json", "input", "constraints_json", "expected_decision_draft",
        "rubric_anchors_json", "prohibited_content_json", "required_schema_extension", "human_decision",
        "human_edited_spec_json", "reviewer_id", "reviewed_at_utc", "rationale"
    ];
    private static readonly JsonSerializerOptions JsonlOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private sealed record ReviewRow(int RowNumber, IReadOnlyDictionary<string, string> Values);

    public static PolishRegressionSupplementSpecificationReviewImportReport Import(string specsDirectory, string coverageDirectory,
        string parentCasesPath, string parentManifestPath, string packetDirectory, string outputDirectory)
    {
        foreach (var value in new[] { specsDirectory, coverageDirectory, parentCasesPath, parentManifestPath, packetDirectory, outputDirectory }) ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var outputRoot = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(outputRoot) || File.Exists(outputRoot)) throw new IOException("审阅导入输出路径已存在；请使用新的版本目录。");

        var validation = PolishRegressionSupplementSpecificationReviewValidator.Validate(specsDirectory, coverageDirectory, parentCasesPath, parentManifestPath, packetDirectory);
        if (!validation.Valid)
            throw new InvalidDataException($"W4.3 规格审阅无效，拒绝导入；问题数：{validation.Issues.Count}。请先通过 polish-regression-supplement-spec-review-validate。");

        var specsPath = Path.Combine(Path.GetFullPath(specsDirectory), "sample-specs.jsonl");
        var sourceManifestPath = Path.Combine(Path.GetFullPath(specsDirectory), "manifest.json");
        var sourceHash = HashFile(specsPath);
        var packetManifestPath = Path.Combine(Path.GetFullPath(packetDirectory), "manifest.json");
        var packetManifestHash = HashFile(packetManifestPath);
        var specs = ReadSpecs(specsPath);
        var reviews = ReadReviews(Path.Combine(Path.GetFullPath(packetDirectory), "specification-review.csv"));
        var decisionLines = new List<string>();
        var generationLines = new List<string>();
        foreach (var review in reviews)
        {
            var value = review.Values;
            var id = value["spec_id"].Trim();
            var decision = value["human_decision"].Trim();
            var reviewer = value["reviewer_id"].Trim();
            var reviewedAt = value["reviewed_at_utc"].Trim();
            var rationale = value["rationale"].Trim();
            using var editedDocument = decision == "edit" ? JsonDocument.Parse(value["human_edited_spec_json"]) : null;
            var selectedSpec = decision == "edit" ? editedDocument!.RootElement.Clone() : specs[id];
            decisionLines.Add(JsonSerializer.Serialize(new
            {
                schema_version = "1.0", spec_id = id, decision, reviewer_id = reviewer, reviewed_at_utc = reviewedAt,
                rationale, source_spec_sha256 = sourceHash, source_review_packet_manifest_sha256 = packetManifestHash,
                source_spec = specs[id],
                human_edited_spec = decision == "edit" ? selectedSpec : (JsonElement?)null,
                origin = "human_spec_review_record", phase_0_gate_contribution = 0,
                not_admissible_as_blind_eval = true
            }, JsonlOptions));
            if (validation.GenerationAuthorized && (decision is "approve" or "edit"))
                generationLines.Add(JsonSerializer.Serialize(new
                {
                    schema_version = "1.0", spec_id = id, approved_spec = selectedSpec,
                    human_review = new { decision, reviewer_id = reviewer, reviewed_at_utc = reviewedAt, rationale },
                    source_spec_sha256 = sourceHash, origin = "ai_assisted_draft_human_reviewed",
                    purpose = "internal_regression_generation_only", phase_0_gate_contribution = 0,
                    not_admissible_as_blind_eval = true
                }, JsonlOptions));
        }

        var decisionJsonl = decisionLines.Count == 0 ? "" : string.Join(Environment.NewLine, decisionLines) + Environment.NewLine;
        var generationJsonl = generationLines.Count == 0 ? "" : string.Join(Environment.NewLine, generationLines) + Environment.NewLine;
        var readme = "# W4.3 人工规格审阅导入结果\n\n" +
            $"- 规格：{validation.SpecificationCount}\n- approve：{validation.ApprovedCount}\n- edit：{validation.EditedCount}\n- reject：{validation.RejectedCount}\n" +
            $"- 内部生成授权：{validation.GenerationAuthorized}\n- 审阅者身份已验证：{validation.ReviewerIdentityVerified}\n" +
            "- 阶段 0 贡献：0；禁止用于盲评、模型晋级或微调。\n- 被拒绝决定始终保存在 review-decisions.jsonl。\n";
        var decisionHash = Hash(Encoding.UTF8.GetBytes(decisionJsonl));
        var generationHash = Hash(Encoding.UTF8.GetBytes(generationJsonl));
        var readmeHash = Hash(Encoding.UTF8.GetBytes(readme));
        var manifest = new
        {
            artifact_version = "hxz-polish-regression-ai-supplement-spec-reviewed-v1",
            status = validation.GenerationAuthorized ? "human_reviewed_generation_authorized" : "human_reviewed_replanning_required",
            source_specs_sha256 = sourceHash, source_manifest_sha256 = HashFile(sourceManifestPath),
            source_review_packet_manifest_sha256 = packetManifestHash,
            counts = new { specifications = validation.SpecificationCount, approved = validation.ApprovedCount, edited = validation.EditedCount,
                rejected = validation.RejectedCount, generation_inputs = generationLines.Count },
            generation_authorized = validation.GenerationAuthorized,
            reviewer_identity_verified = validation.ReviewerIdentityVerified,
            phase_0_gate_contribution = 0, not_admissible_as_blind_eval = true, not_usable_for_model_promotion = true,
            files = new Dictionary<string, string>
            {
                ["review-decisions.jsonl"] = decisionHash, ["generation-input.jsonl"] = generationHash, ["README.md"] = readmeHash
            }
        };
        var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true }) + Environment.NewLine;

        Directory.CreateDirectory(outputRoot);
        ImmutableArtifactWriter.WriteNew(Path.Combine(outputRoot, "review-decisions.jsonl"), decisionJsonl);
        ImmutableArtifactWriter.WriteNew(Path.Combine(outputRoot, "generation-input.jsonl"), generationJsonl);
        ImmutableArtifactWriter.WriteNew(Path.Combine(outputRoot, "README.md"), readme);
        ImmutableArtifactWriter.WriteNew(Path.Combine(outputRoot, "manifest.json"), manifestJson);
        return new(true, validation.SpecificationCount, validation.ApprovedCount, validation.EditedCount,
            validation.RejectedCount, validation.GenerationAuthorized, validation.ReviewerIdentityVerified, 0, outputRoot);
    }

    private static Dictionary<string, JsonElement> ReadSpecs(string path)
    {
        var specs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            using var document = JsonDocument.Parse(line);
            specs.Add(document.RootElement.GetProperty("spec_id").GetString()!, document.RootElement.Clone());
        }
        return specs;
    }

    private static List<ReviewRow> ReadReviews(string path)
    {
        using var parser = new TextFieldParser(path, Encoding.UTF8)
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false
        };
        parser.SetDelimiters(",");
        _ = parser.ReadFields();
        var rows = new List<ReviewRow>();
        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields()!;
            rows.Add(new(rows.Count + 2, Headers.Zip(fields).ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal)));
        }
        return rows;
    }

    private static string HashFile(string path) => Hash(File.ReadAllBytes(path));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
