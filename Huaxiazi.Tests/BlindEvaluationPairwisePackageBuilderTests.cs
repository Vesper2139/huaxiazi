using Huaxiazi.DatasetBuilder;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO;
using System.Text;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class BlindEvaluationPairwisePackageBuilderTests
{
    [Fact]
    public void PredictionSchema_DeclaresNullableRuntimeTelemetryFields()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Huaxiazi.csproj")))
            directory = directory.Parent;
        Assert.NotNull(directory);

        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory!.FullName,
            "datasets", "ai-evaluation", "prediction.schema.json")));
        var telemetry = schema.RootElement.GetProperty("$defs").GetProperty("telemetry").GetProperty("properties");
        foreach (var field in new[]
        {
            "latency_milliseconds", "api_input_tokens", "api_output_tokens",
            "api_cache_read_input_tokens", "api_cache_creation_input_tokens",
            "local_tokens_per_second", "local_peak_memory_bytes", "local_model_load_milliseconds"
        })
        {
            Assert.True(telemetry.TryGetProperty(field, out var definition), $"prediction schema 缺少 {field}");
            var acceptedTypes = definition.GetProperty("type").EnumerateArray().Select(item => item.GetString()).ToArray();
            Assert.Contains(field is "latency_milliseconds" or "local_tokens_per_second" or "local_model_load_milliseconds" ? "number" : "integer", acceptedTypes);
            Assert.Contains("null", acceptedTypes);
        }
        Assert.Contains(telemetry.GetProperty("error_category").GetProperty("enum").EnumerateArray(), value => value.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public void Build_HidesCandidateAndGoldIdentityAndRandomizesReviewSide()
    {
        var package = BlindEvaluationPairwisePackageBuilder.Build([Gold()],
        [
            Prediction("candidate-secret-a", "甲方方案"),
            Prediction("candidate-secret-b", "乙方方案")
        ], "development");

        var review = Assert.Single(package.ReviewItems);
        var mapping = Assert.Single(package.SealedMap);
        Assert.NotEqual("gold-secret-id", review.Id);
        Assert.NotEqual("gold-secret-id", mapping.ReviewId);
        Assert.Equal("gold-secret-id", mapping.GoldId);
        Assert.Equal("development", package.Split);
        Assert.Contains(mapping.LeftCandidateId, new[] { "candidate-secret-a", "candidate-secret-b" });
        Assert.Contains(mapping.RightCandidateId, new[] { "candidate-secret-a", "candidate-secret-b" });
        Assert.NotEqual(mapping.LeftCandidateId, mapping.RightCandidateId);
        var reviewerContent = JsonSerializer.Serialize(review);
        Assert.DoesNotContain("gold-secret-id", reviewerContent, StringComparison.Ordinal);
        Assert.DoesNotContain("candidate-secret-a", reviewerContent, StringComparison.Ordinal);
        Assert.DoesNotContain("candidate-secret-b", reviewerContent, StringComparison.Ordinal);
        Assert.Contains("甲方方案", new[] { review.LeftOutput, review.RightOutput });
        Assert.Contains("乙方方案", new[] { review.LeftOutput, review.RightOutput });
        Assert.Contains("核心事实", review.FactsAndConstraints);
        Assert.DoesNotContain("黄金答案不能展示", reviewerContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_IncludesOnlyTheExplicitlySelectedSplit()
    {
        var development = WithMatchingReview(Gold() with { Id = "development-case", SemanticFamilyId = "dev-family" });
        var frozenTest = WithMatchingReview(Gold() with
        {
            Id = "frozen-test-case",
            SemanticFamilyId = "test-family",
            Input = "独立的冻结集输入",
            Split = "frozen_test"
        });
        var predictions = new[]
        {
            Prediction("candidate-a", "dev A") with { Id = development.Id },
            Prediction("candidate-b", "dev B") with { Id = development.Id },
            Prediction("candidate-a", "test A") with { Id = frozenTest.Id },
            Prediction("candidate-b", "test B") with { Id = frozenTest.Id }
        };

        var package = BlindEvaluationPairwisePackageBuilder.Build([development, frozenTest], predictions, "frozen_test");

        Assert.Equal("frozen_test", package.Split);
        Assert.Single(package.ReviewItems);
        Assert.Equal("frozen-test-case", Assert.Single(package.SealedMap).GoldId);
        Assert.DoesNotContain("dev A", JsonSerializer.Serialize(package.ReviewItems));
    }

    [Fact]
    public void Build_RejectsGoldWithoutCurrentHumanReviewEvidence()
    {
        var invalidGold = Gold() with { HumanReview = new(["gold-reviewer-a", "gold-reviewer-b"], "accepted") };

        var exception = Assert.Throws<InvalidOperationException>(() => BlindEvaluationPairwisePackageBuilder.Build([invalidGold],
        [
            Prediction("candidate-a", "甲方方案"),
            Prediction("candidate-b", "乙方方案")
        ], "development"));

        Assert.Contains("human_review", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RejectsPIIInGoldBeforeCreatingReviewItems()
    {
        var sensitiveGold = Gold() with { Input = "请联系 13800138000" };

        var exception = Assert.Throws<InvalidOperationException>(() => BlindEvaluationPairwisePackageBuilder.Build([sensitiveGold],
        [
            Prediction("candidate-a", "甲方方案"),
            Prediction("candidate-b", "乙方方案")
        ], "development"));

        Assert.Contains("pii", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BlindValidate_RejectsUnknownGoldAndReviewFields()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "blind-validate-strict-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var goldPath = Path.Combine(root, "gold.jsonl");
        var trainingPath = Path.Combine(root, "training.jsonl");
        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var gold = JsonNode.Parse(JsonSerializer.Serialize(Gold(), jsonOptions))!.AsObject();
        gold["human_review"]!["unexpected_reviewer_note"] = "must not be silently ignored";
        File.WriteAllText(goldPath, gold.ToJsonString() + Environment.NewLine, new UTF8Encoding(false));
        File.WriteAllText(trainingPath, "{}" + Environment.NewLine, new UTF8Encoding(false));
        var originalError = Console.Error;
        using var error = new StringWriter();
        try
        {
            Console.SetError(error);
            var exitCode = Program.Main(["blind-validate", "--input", goldPath, "--training", trainingPath]);

            Assert.Equal(2, exitCode);
            Assert.Contains("未知字段", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalError);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("project_synthetic_legacy")]
    [InlineData("ai_assisted_draft")]
    [InlineData("human_authored_internal")]
    public void BlindValidateCommand_RejectsInternalRegressionOrigins(string sourceKind)
    {
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "blind-validate-internal-origin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var goldPath = Path.Combine(root, "gold.jsonl");
        var trainingPath = Path.Combine(root, "training.jsonl");
        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var record = Gold() with
        {
            Source = new(sourceKind, "internal-only", "project-internal-synthetic")
        };
        File.WriteAllText(goldPath, JsonSerializer.Serialize(record, jsonOptions) + Environment.NewLine, new UTF8Encoding(false));
        File.WriteAllText(trainingPath, "{\"id\":\"training\",\"split\":\"train\",\"input\":\"unrelated training input\"}" + Environment.NewLine, new UTF8Encoding(false));

        var originalOutput = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            var exitCode = Program.Main(["blind-validate", "--input", goldPath, "--training", trainingPath]);

            Assert.Equal(3, exitCode);
            using var report = JsonDocument.Parse(output.ToString());
            Assert.False(report.RootElement.GetProperty("valid").GetBoolean());
            Assert.Contains(report.RootElement.GetProperty("issues").EnumerateArray(), issue =>
                issue.GetProperty("code").GetString() == "source-authorization");
        }
        finally
        {
            Console.SetOut(originalOutput);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BlindValidateCommand_RejectsFrozenInternalRegressionArtifact()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Huaxiazi.csproj")))
            directory = directory.Parent;
        Assert.NotNull(directory);

        var internalCases = Path.Combine(directory!.FullName, "datasets", "polish-regression-v1", "cases.jsonl");
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "blind-validate-internal-dataset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var trainingPath = Path.Combine(root, "training.jsonl");
        File.WriteAllText(trainingPath, "{\"id\":\"training\",\"split\":\"train\",\"input\":\"unrelated training input\"}" + Environment.NewLine, new UTF8Encoding(false));

        var originalOutput = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exitCode = Program.Main(["blind-validate", "--input", internalCases, "--training", trainingPath]);

            Assert.NotEqual(0, exitCode);
            Assert.Empty(output.ToString());
            Assert.Contains("缺少字段 semantic_family_id", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Build_SkipsCasesWithoutTwoSuccessfulCandidates()
    {
        var package = BlindEvaluationPairwisePackageBuilder.Build([Gold()],
        [
            Prediction("candidate-a", "甲方方案"),
            Prediction("candidate-b", "", status: "error")
        ], "development");

        Assert.Empty(package.ReviewItems);
        Assert.Empty(package.SealedMap);
        Assert.Single(package.SkippedCases);
    }

    [Fact]
    public void Build_RejectsPotentialPersonalDataBeforeCreatingReviewPackage()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => BlindEvaluationPairwisePackageBuilder.Build([Gold()],
        [
            Prediction("candidate-a", "联系 alice@example.com"),
            Prediction("candidate-b", "乙方方案")
        ], "development"));

        Assert.Contains("敏感信息", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MergeAnswers_RestoresSealedCandidateOrderAndGoldId()
    {
        var package = BlindEvaluationPairwisePackageBuilder.Build([Gold()],
        [
            Prediction("candidate-a", "甲方方案"),
            Prediction("candidate-b", "乙方方案")
        ], "development");
        var answer = Assert.Single(package.AnswerTemplates) with
        {
            Votes = [new("reviewer-a", "left"), new("reviewer-b", "left")]
        };

        var comparison = Assert.Single(BlindEvaluationPairwisePackageBuilder.MergeAnswers([answer], package.SealedMap));

        Assert.Equal("gold-secret-id", comparison.Id);
        Assert.Equal(package.SealedMap[0].LeftCandidateId, comparison.LeftCandidateId);
        Assert.Equal(package.SealedMap[0].RightCandidateId, comparison.RightCandidateId);
        Assert.All(comparison.Votes, vote => Assert.Equal("left", vote.Choice));
    }

    [Fact]
    public void PairwiseCli_CreatesIdentityFreeReviewPackageAndMergesAnswers()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "blind-pairwise-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var goldPath = Path.Combine(root, "gold.jsonl");
        var trainingPath = Path.Combine(root, "training.jsonl");
        var firstPredictionPath = Path.Combine(root, "candidate-a.jsonl");
        var secondPredictionPath = Path.Combine(root, "candidate-b.jsonl");
        var reviewDirectory = Path.Combine(root, "reviewer");
        var sealedPath = Path.Combine(root, "custodian", "map.json");
        var answersPath = Path.Combine(reviewDirectory, "answers.jsonl");
        var comparisonPath = Path.Combine(root, "comparisons.jsonl");
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        try
        {
            var records = Enumerable.Range(0, 500).Select(index =>
            {
                var record = Gold() with
                {
                    Id = "gold-case-" + index,
                    Task = index % 5 < 3 ? "polish" : "prompt_optimize",
                    SemanticFamilyId = "family-" + index,
                    Input = "私密原始输入-" + index,
                    InputStyle = (index % 5) switch
                    {
                        0 => "colloquial",
                        1 => "fragmentary",
                        2 => "formal",
                        3 => "speech_transcription",
                        _ => "mixed_language"
                    },
                    Split = index < 400 ? "development" : "frozen_test",
                    RiskLevel = index % 10 == 0 ? "high" : "low",
                    ExpectedDecision = index % 20 == 1 ? "clarify" : "produce",
                    Annotations = new BlindEvaluationAnnotations(
                        ["核心事实"],
                        "自然",
                        index % 20 == 2 ? ["输出为单段"] : [],
                        index % 20 == 1,
                        index % 10 == 0 ? ["关键事实锚点"] : [])
                };
                return WithMatchingReview(record);
            }).ToArray();
            var humanReviews = new[]
            {
                new BlindEvaluationReview("reviewer-a", true, true, true, true, false, true, DirectUsabilityScore: 4),
                new BlindEvaluationReview("reviewer-b", true, true, true, true, false, true, DirectUsabilityScore: 4)
            };
            var predictionsA = records.Select(record => Prediction("candidate-secret-a", "甲方方案") with
            {
                Id = record.Id,
                Reviews = humanReviews,
                Telemetry = new BlindEvaluationTelemetry(100, 100, 10, null, null, null,
                    ApiCacheReadInputTokens: 30, ApiCacheCreationInputTokens: 10)
            }).ToArray();
            var predictionsB = records.Select(record => Prediction("candidate-secret-b", "乙方方案") with
            {
                Id = record.Id,
                Reviews = humanReviews,
                Telemetry = new BlindEvaluationTelemetry(100, 100, 10, null, null, null,
                    ApiCacheReadInputTokens: 30, ApiCacheCreationInputTokens: 10)
            }).ToArray();
            var training = new { id = "train", split = "train", input = "unrelated training input", semantic_family_id = "training-family" };
            File.WriteAllLines(goldPath, records.Select(record => JsonSerializer.Serialize(record, options)), new UTF8Encoding(false));
            File.WriteAllLines(trainingPath, [JsonSerializer.Serialize(training, options)], new UTF8Encoding(false));
            File.WriteAllLines(firstPredictionPath, predictionsA.Select(record => JsonSerializer.Serialize(record, options)), new UTF8Encoding(false));
            File.WriteAllLines(secondPredictionPath, predictionsB.Select(record => JsonSerializer.Serialize(record, options)), new UTF8Encoding(false));
            Assert.Equal(JsonValueKind.Object, JsonDocument.Parse(File.ReadLines(firstPredictionPath).First()).RootElement.ValueKind);

            var validationOutput = new StringWriter();
            var validationOriginalOutput = Console.Out;
            int validationExitCode;
            try
            {
                Console.SetOut(validationOutput);
                validationExitCode = Program.Main(["blind-validate", "--input", goldPath, "--training", trainingPath]);
            }
            finally { Console.SetOut(validationOriginalOutput); }
            Assert.Equal(0, validationExitCode);
            Assert.DoesNotContain("私密原始输入", validationOutput.ToString(), StringComparison.Ordinal);
            using (var validationReport = JsonDocument.Parse(validationOutput.ToString()))
            {
                var externalEvidence = validationReport.RootElement.GetProperty("external_evidence");
                Assert.False(externalEvidence.GetProperty("verified_by_tool").GetBoolean());
                Assert.Equal("not_verified", externalEvidence.GetProperty("authorization_status").GetString());
                Assert.Equal("not_verified", externalEvidence.GetProperty("reviewer_independence_status").GetString());
                var admissionProtocol = validationReport.RootElement.GetProperty("admission_protocol");
                Assert.Equal(BlindEvaluationAdmissionProtocol.ProtocolId, admissionProtocol.GetProperty("id").GetString());
                Assert.Equal(BlindEvaluationAdmissionProtocol.Version, admissionProtocol.GetProperty("version").GetString());
                Assert.Equal(64, admissionProtocol.GetProperty("policy_sha256").GetString()!.Length);
                var coverage = validationReport.RootElement.GetProperty("coverage");
                Assert.Equal(500, coverage.GetProperty("total_count").GetInt32());
                Assert.Equal(300, coverage.GetProperty("polish_count").GetInt32());
                Assert.Equal(200, coverage.GetProperty("prompt_optimize_count").GetInt32());
                Assert.Equal(50, coverage.GetProperty("high_risk_count").GetInt32());
                Assert.Equal(25, coverage.GetProperty("clarify_count").GetInt32());
                Assert.Equal(25, coverage.GetProperty("format_requirement_count").GetInt32());
                var reviewerAgreement = validationReport.RootElement.GetProperty("reviewer_agreement");
                Assert.Equal(500, reviewerAgreement.GetProperty("comparable_record_count").GetInt32());
                Assert.Equal(500, reviewerAgreement.GetProperty("whole_label_agreement_count").GetInt32());
                Assert.Equal(1, reviewerAgreement.GetProperty("whole_label_agreement_rate").GetDouble());
                Assert.Equal(500, reviewerAgreement.GetProperty("fields").GetProperty("expected_decision").GetProperty("comparable_count").GetInt32());
                var clarificationAgreement = reviewerAgreement.GetProperty("fields").GetProperty("clarification_required");
                Assert.Equal(1, clarificationAgreement.GetProperty("krippendorff_alpha_nominal").GetDouble());
                Assert.Equal("computed", clarificationAgreement.GetProperty("chance_corrected_status").GetString());
                var pooledClarificationCounts = clarificationAgreement.GetProperty("pooled_category_counts");
                Assert.Equal(950, pooledClarificationCounts.GetProperty("false").GetInt32());
                Assert.Equal(50, pooledClarificationCounts.GetProperty("true").GetInt32());
                Assert.Equal(1, clarificationAgreement.GetProperty("positive_specific_agreement").GetDouble());
                Assert.Equal(1, clarificationAgreement.GetProperty("negative_specific_agreement").GetDouble());
                var taskSplitCoverage = coverage.GetProperty("task_split_coverage");
                Assert.Equal(4, taskSplitCoverage.GetArrayLength());
                var promptDevelopment = taskSplitCoverage.EnumerateArray().Single(cell =>
                    cell.GetProperty("task").GetString() == "prompt_optimize" &&
                    cell.GetProperty("split").GetString() == "development");
                Assert.Equal(160, promptDevelopment.GetProperty("sample_count").GetInt32());
                Assert.Equal(0, promptDevelopment.GetProperty("high_risk_count").GetInt32());
                var development = coverage.GetProperty("development");
                Assert.Equal(40, development.GetProperty("high_risk_count").GetInt32());
                Assert.Equal(20, development.GetProperty("clarify_count").GetInt32());
                Assert.Equal(20, development.GetProperty("format_requirement_count").GetInt32());
                Assert.Equal(5, development.GetProperty("distinct_input_style_count").GetInt32());
                var frozenTest = coverage.GetProperty("frozen_test");
                Assert.Equal(10, frozenTest.GetProperty("high_risk_count").GetInt32());
                Assert.Equal(5, frozenTest.GetProperty("clarify_count").GetInt32());
                Assert.Equal(5, frozenTest.GetProperty("format_requirement_count").GetInt32());
                Assert.Equal(5, frozenTest.GetProperty("distinct_input_style_count").GetInt32());
            }

            var stderr = new StringWriter();
            var originalErrorForMixedSplit = Console.Error;
            int packageExitCode;
            try
            {
                Console.SetError(stderr);
                packageExitCode = Program.Main([
                "blind-pairwise-package", "--gold", goldPath,
                "--split", "frozen_test",
                "--training", trainingPath,
                "--predictions", firstPredictionPath, "--predictions", secondPredictionPath,
                "--output", reviewDirectory, "--sealed-manifest", sealedPath, "--confirm-frozen-test-locked"
                ]);
            }
            finally { Console.SetError(originalErrorForMixedSplit); }
            Assert.True(packageExitCode == 0, stderr.ToString());
            using (var packageInfo = JsonDocument.Parse(File.ReadAllText(Path.Combine(reviewDirectory, "pairwise-package-info.json"))))
            {
                Assert.Equal("frozen_test", packageInfo.RootElement.GetProperty("split").GetString());
                Assert.Equal(100, packageInfo.RootElement.GetProperty("review_item_count").GetInt32());
            }
            var publicPackagePath = Path.Combine(reviewDirectory, "pairwise-review.jsonl");
            var publicPackage = File.ReadAllText(publicPackagePath);
            Assert.DoesNotContain("gold-case-0", publicPackage, StringComparison.Ordinal);
            Assert.DoesNotContain("candidate-secret-a", publicPackage, StringComparison.Ordinal);
            Assert.DoesNotContain("candidate-secret-b", publicPackage, StringComparison.Ordinal);
            Assert.DoesNotContain("黄金答案不能展示", publicPackage, StringComparison.Ordinal);
            Assert.DoesNotContain("gold-reviewer-a", publicPackage, StringComparison.Ordinal);
            Assert.DoesNotContain("independent_annotations", publicPackage, StringComparison.Ordinal);
            var templates = File.ReadLines(Path.Combine(reviewDirectory, "pairwise-answers.template.jsonl"))
                .Select(line => JsonSerializer.Deserialize<BlindPairwiseAnswerTemplate>(line, options)!)
                .Select(template => template with { Votes = [new("reviewer-a", "left"), new("reviewer-b", "left")] });
            File.WriteAllLines(answersPath, templates.Select(template => JsonSerializer.Serialize(template, options)), new UTF8Encoding(false));

            Assert.Equal(0, Program.Main([
                "blind-pairwise-merge", "--answers", answersPath,
                "--review-package", publicPackagePath, "--sealed-manifest", sealedPath,
                "--output", comparisonPath
            ]));
            var comparison = JsonSerializer.Deserialize<BlindEvaluationPairwiseComparison>(File.ReadLines(comparisonPath).First(), options)!;
            Assert.Contains(comparison.Id, records.Select(record => record.Id));
            Assert.Equal("left", comparison.Votes[0].Choice);

            var reportPath = Path.Combine(root, "evaluation-report.json");
            var originalOutput = Console.Out;
            int evaluationExitCode;
            try
            {
                Console.SetOut(TextWriter.Null);
                evaluationExitCode = Program.Main([
                    "blind-evaluate", "--gold", goldPath,
                    "--split", "frozen_test",
                    "--predictions", firstPredictionPath, "--predictions", secondPredictionPath,
                    "--comparisons", comparisonPath, "--training", trainingPath, "--output", reportPath
                ]);
            }
            finally { Console.SetOut(originalOutput); }
            Assert.Equal(0, evaluationExitCode);
            using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
            Assert.Equal("frozen_test", report.RootElement.GetProperty("evaluation_split").GetString());
            Assert.Equal(2, report.RootElement.GetProperty("scoring").GetProperty("candidates").GetArrayLength());
            var firstCandidateUsage = report.RootElement.GetProperty("scoring").GetProperty("candidates")[0];
            Assert.Equal(10_000, firstCandidateUsage.GetProperty("api_input_tokens").GetInt64());
            Assert.Equal(3_000, firstCandidateUsage.GetProperty("api_cache_read_input_tokens").GetInt64());
            Assert.Equal(1_000, firstCandidateUsage.GetProperty("api_cache_creation_input_tokens").GetInt64());
            Assert.Equal(1, report.RootElement.GetProperty("scoring").GetProperty("pairwise_comparisons").GetArrayLength());
            Assert.Equal(100, report.RootElement.GetProperty("scoring").GetProperty("pairwise_comparisons")[0].GetProperty("comparable_count").GetInt32());

            var mixedSplitReportPath = Path.Combine(root, "mixed-split-report.json");
            var originalError = Console.Error;
            int mixedSplitExitCode;
            try
            {
                Console.SetError(TextWriter.Null);
                mixedSplitExitCode = Program.Main([
                    "blind-evaluate", "--gold", goldPath,
                    "--split", "development",
                    "--predictions", firstPredictionPath, "--predictions", secondPredictionPath,
                    "--comparisons", comparisonPath, "--training", trainingPath,
                    "--output", mixedSplitReportPath
                ]);
            }
            finally { Console.SetError(originalError); }
            Assert.Equal(3, mixedSplitExitCode);
            Assert.False(File.Exists(mixedSplitReportPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static BlindEvaluationRecord Gold() => new()
    {
        Id = "gold-secret-id",
        Task = "polish",
        SemanticFamilyId = "family-secret",
        Source = new("project_owned", "test-authorization-ref", "synthetic-test-data"),
        Input = "私密原始输入",
        InputStyle = "colloquial",
        Constraints = ["保留事实"],
        RiskLevel = "low",
        Split = "development",
        ExpectedDecision = "produce",
        ReferenceOutput = "黄金答案不能展示",
        Annotations = new(["核心事实"], "自然", ["一段话"], false, []),
        HumanReview = new(["gold-reviewer-a", "gold-reviewer-b"], "accepted", null,
            [new("gold-reviewer-a", GoldLabels()), new("gold-reviewer-b", GoldLabels())])
    };

    private static BlindEvaluationGoldLabelSet GoldLabels() => new(
        "polish", "colloquial", ["保留事实"], "low", "produce", "黄金答案不能展示",
        new BlindEvaluationAnnotations(["核心事实"], "自然", ["一段话"], false, []));

    private static BlindEvaluationRecord WithMatchingReview(BlindEvaluationRecord record)
    {
        var labels = new BlindEvaluationGoldLabelSet(record.Task, record.InputStyle, record.Constraints,
            record.RiskLevel, record.ExpectedDecision, record.ReferenceOutput, record.Annotations);
        return record with
        {
            HumanReview = new(["gold-reviewer-a", "gold-reviewer-b"], "accepted", null,
                [new("gold-reviewer-a", labels), new("gold-reviewer-b", labels)])
        };
    }

    private static BlindEvaluationPrediction Prediction(string candidateId, string output, string status = "success") =>
        new("gold-secret-id", candidateId, output, true, [], null, null, status);
}
