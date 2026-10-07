using System;
using System.IO;
using System.Text.Json;
using Huaxiazi.DatasetBuilder;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class BlindEvaluationScorerTests
{
    [Theory]
    [InlineData(true, 3)]
    [InlineData(false, 4)]
    public void Evaluate_RejectsDirectUsabilityBooleanThatConflictsWithItsScore(bool directlyUsable, int score)
    {
        var review = Review(true, directlyUsable, true, true, false, true) with { DirectUsabilityScore = score };
        var report = BlindEvaluationScorer.Evaluate(
            [Gold("direct-usability-conflict", "low")],
            [Prediction("direct-usability-conflict", "candidate-a", true, review, null)]);

        Assert.Contains(report.Issues, issue => issue.Code == "review-rubric-conflict");
        Assert.False(Assert.Single(report.Candidates).Passed);
    }

    [Fact]
    public void Evaluate_RejectsHighRiskReversalWithFidelityScoreAboveOne()
    {
        var review = Review(true, true, true, true, true, false) with { FidelityScore = 3 };
        var report = BlindEvaluationScorer.Evaluate(
            [Gold("reversal-score-conflict", "high")],
            [Prediction("reversal-score-conflict", "candidate-a", true, review, null)]);

        Assert.Contains(report.Issues, issue => issue.Code == "review-rubric-conflict");
        Assert.False(Assert.Single(report.Candidates).Passed);
    }

    [Fact]
    public void BlindEvaluateCommand_EmitsProductSliceReportsInSnakeCaseJson()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "blind-score-slices-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
            var gold = Gold("cli-sample", "low") with { Context = Context("scenario", "私人沟通") };
            var labels = new BlindEvaluationGoldLabelSet(gold.Task, gold.InputStyle, gold.Constraints,
                gold.RiskLevel, gold.ExpectedDecision, gold.ReferenceOutput, gold.Annotations);
            gold = gold with
            {
                HumanReview = new BlindEvaluationHumanReview(["gold-a", "gold-b"], "accepted", null,
                    [new("gold-a", labels), new("gold-b", labels)])
            };
            var goldPath = Path.Combine(root, "gold.jsonl");
            var trainingPath = Path.Combine(root, "training.jsonl");
            var predictionsPath = Path.Combine(root, "predictions.jsonl");
            File.WriteAllText(goldPath, JsonSerializer.Serialize(gold, options) + Environment.NewLine);
            File.WriteAllText(trainingPath, "{\"id\":\"train\",\"split\":\"train\",\"input\":\"unrelated\",\"semantic_family_id\":\"train-family\"}" + Environment.NewLine);
            File.WriteAllText(predictionsPath, JsonSerializer.Serialize(
                Prediction("cli-sample", "candidate-a", true, Review(true, true, true, true, false, true), new(125, 50, 25, null, null, null)), options) + Environment.NewLine);

            var output = new StringWriter();
            var originalOutput = Console.Out;
            int exitCode;
            try
            {
                Console.SetOut(output);
                exitCode = Huaxiazi.DatasetBuilder.Program.Main(new[]
                    { "blind-evaluate", "--gold", goldPath, "--predictions", predictionsPath, "--training", trainingPath });
            }
            finally { Console.SetOut(originalOutput); }

            Assert.Equal(3, exitCode); // A one-row fixture must fail dataset admission.
            using var document = JsonDocument.Parse(output.ToString());
            var slice = Assert.Single(document.RootElement.GetProperty("scoring").GetProperty("product_slice_reports").EnumerateArray());
            Assert.Equal("私人沟通", slice.GetProperty("product_slice").GetString());
            Assert.Equal("polish", slice.GetProperty("task").GetString());
            Assert.Equal(1, slice.GetProperty("sample_count").GetInt32());
            Assert.Equal(1, slice.GetProperty("telemetry_prediction_count").GetInt32());
            Assert.Equal(125d, slice.GetProperty("latency_p50_milliseconds").GetDouble());
            Assert.Equal(50, slice.GetProperty("api_input_tokens").GetInt64());
            Assert.True(slice.GetProperty("direct_usability_rate").GetDouble() > 0);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Evaluate_ReportsDescriptiveQualityByTaskSplitAndProductSlice()
    {
        var gold = new[]
        {
            Gold("private", "low") with
            {
                Context = Context("scenario", "私人沟通")
            },
            Gold("public", "high") with
            {
                Context = Context("scenario", "公开发布")
            },
            Gold("coding", "low") with
            {
                Task = "prompt_optimize",
                Split = "development",
                Context = Context("category", "Coding")
            },
            Gold("missing", "low") with
            {
                Context = Context("scenario", "自定义沟通")
            },
            Gold("duplicate", "low") with
            {
                Context = Context("scenario", "正式材料")
            }
        };
        var predictions = new[]
        {
            Prediction("private", "candidate-a", true, Review(true, true, true, true, false, true), null),
            Prediction("public", "candidate-a", true, Review(false, false, false, true, true, true), null),
            new BlindEvaluationPrediction("coding", "candidate-a", string.Empty, false, [], null,
                new BlindEvaluationTelemetry(80, 10, null, null, null, null, "network"), "error"),
            Prediction("duplicate", "candidate-a", true, Review(true, true, true, true, false, true), null),
            Prediction("duplicate", "candidate-a", true, Review(true, true, true, true, false, true), null)
        };

        var report = BlindEvaluationScorer.Evaluate(gold, predictions);

        Assert.False(Assert.Single(report.Candidates).Passed);
        var privateSlice = Assert.Single(report.ProductSliceReports, item => item.ProductSlice == "私人沟通");
        Assert.Equal("polish", privateSlice.Task);
        Assert.Equal("frozen_test", privateSlice.Split);
        Assert.Equal(1, privateSlice.SampleCount);
        Assert.Equal(1, privateSlice.PredictionCount);
        Assert.Equal(1d, privateSlice.DirectUsabilityRate);

        var publicSlice = Assert.Single(report.ProductSliceReports, item => item.ProductSlice == "公开发布");
        Assert.Equal(1, publicSlice.HighRiskKeyFactReversals);
        Assert.Equal(0d, publicSlice.FactConstraintRetentionRate);
        Assert.Equal(0d, publicSlice.DirectUsabilityRate);

        var codingSlice = Assert.Single(report.ProductSliceReports, item => item.ProductSlice == "编程开发");
        Assert.Equal("prompt_optimize", codingSlice.Task);
        Assert.Equal("development", codingSlice.Split);
        Assert.Equal(1, codingSlice.FailedRequestCount);
        Assert.Equal(0d, codingSlice.CoverageRate);
        Assert.Equal(0d, codingSlice.SchemaValidRate);

        var missingSlice = Assert.Single(report.ProductSliceReports, item => item.ProductSlice == "未指定或自定义");
        Assert.Equal(1, missingSlice.MissingPredictionCount);
        Assert.Equal(0, missingSlice.FailedRequestCount);
        Assert.Equal(0, missingSlice.InvalidPredictionCount);

        var duplicateSlice = Assert.Single(report.ProductSliceReports, item => item.ProductSlice == "正式材料");
        Assert.Equal(0, duplicateSlice.MissingPredictionCount);
        Assert.Equal(0, duplicateSlice.FailedRequestCount);
        Assert.Equal(1, duplicateSlice.InvalidPredictionCount);
        Assert.Contains(report.Issues, issue => issue.Code == "duplicate-prediction");
    }

    [Fact]
    public void Evaluate_ReportsOperationalMetricsAndObservationCountsByProductSlice()
    {
        var gold = new[]
        {
            Gold("private-1", "low") with { Context = Context("scenario", "私人沟通") },
            Gold("private-2", "low") with { Context = Context("scenario", "私人沟通") },
            Gold("public-1", "low") with { Context = Context("scenario", "公开发布") }
        };
        var predictions = new[]
        {
            Prediction("private-1", "candidate-a", true, Review(true, true, true, true, false, true), new(100, 40, 20, 10, 1_000, 500)),
            new BlindEvaluationPrediction("private-2", "candidate-a", string.Empty, false, [], null,
                new(300, null, null, null, null, null, "timeout"), "error"),
            Prediction("public-1", "candidate-a", true, Review(true, true, true, true, false, true), new(200, null, null, 20, 2_000, 800))
        };

        var report = BlindEvaluationScorer.Evaluate(gold, predictions);

        var privateSlice = Assert.Single(report.ProductSliceReports, item => item.ProductSlice == "私人沟通");
        Assert.Equal(2, privateSlice.TelemetryPredictionCount);
        Assert.Equal(2, privateSlice.LatencyObservationCount);
        Assert.Equal(200d, privateSlice.LatencyP50Milliseconds);
        Assert.Equal(290d, privateSlice.LatencyP95Milliseconds);
        Assert.Null(privateSlice.ApiInputTokens); // Partial telemetry must not look like a complete total.
        Assert.Equal(1, privateSlice.ApiInputTokenObservationCount);
        Assert.Null(privateSlice.ApiOutputTokens);
        Assert.Equal(1, privateSlice.ApiOutputTokenObservationCount);
        Assert.Equal(1, privateSlice.LocalTokensPerSecondObservationCount);
        Assert.Equal(10d, privateSlice.LocalTokensPerSecondP50);
        Assert.Equal(1_000, privateSlice.LocalPeakMemoryBytes);
        Assert.Equal(1, privateSlice.LocalPeakMemoryObservationCount);
        Assert.Equal(500d, privateSlice.LocalModelLoadP50Milliseconds);
        Assert.Equal(1, privateSlice.LocalModelLoadObservationCount);
        Assert.Equal(1, privateSlice.ErrorCountsByCategory["timeout"]);

        var publicSlice = Assert.Single(report.ProductSliceReports, item => item.ProductSlice == "公开发布");
        Assert.Equal(1, publicSlice.LatencyObservationCount);
        Assert.Equal(200d, publicSlice.LatencyP50Milliseconds);
        Assert.Null(publicSlice.ApiInputTokens);
        Assert.Equal(0, publicSlice.ApiInputTokenObservationCount);
        Assert.Equal(20d, publicSlice.LocalTokensPerSecondP50);
        Assert.Equal(2_000, publicSlice.LocalPeakMemoryBytes);
    }

    [Fact]
    public void Evaluate_ReportsHumanQualitySchemaAndProviderTelemetry()
    {
        var gold = new[]
        {
            Gold("sample-1", "low"),
            Gold("sample-2", "high")
        };
        var predictions = new[]
        {
            Prediction("sample-1", "candidate-a", true, Review(true, true, true, true, false, true), new(100, 40, 20, 15, 1_000, 800, ApiCacheReadInputTokens: 5, ApiCacheCreationInputTokens: 2)),
            Prediction("sample-2", "candidate-a", false, Review(false, true, false, true, false, true), new(300, 60, 30, 12, 2_000, 900, ApiCacheReadInputTokens: 10, ApiCacheCreationInputTokens: 3))
        };

        var report = BlindEvaluationScorer.Evaluate(gold, predictions);

        var candidate = Assert.Single(report.Candidates);
        Assert.Equal(1d, candidate.CoverageRate);
        Assert.Equal(.5d, candidate.SchemaValidRate);
        Assert.Equal(.5d, candidate.FactConstraintRetentionRate);
        Assert.Equal(1d, candidate.DirectUsabilityRate);
        Assert.Equal(.5d, candidate.ToneMatchRate);
        Assert.Equal(1d, candidate.ClarificationDecisionAccuracy);
        Assert.Equal(3d, candidate.MeanFidelityScore);
        Assert.Equal(3d, candidate.MeanTaskCompletionScore);
        Assert.Equal(3d, candidate.MeanNaturalnessScore);
        Assert.Equal(4d, candidate.MeanDirectUsabilityScore);
        Assert.Equal(5d, candidate.MeanSafetyScore);
        Assert.Equal(0, candidate.HighRiskKeyFactReversals);
        Assert.Equal(200d, candidate.LatencyP50Milliseconds);
        Assert.Equal(290d, candidate.LatencyP95Milliseconds);
        Assert.Equal(100, candidate.ApiInputTokens);
        Assert.Equal(50, candidate.ApiOutputTokens);
        Assert.Equal(15, candidate.ApiCacheReadInputTokens);
        Assert.Equal(5, candidate.ApiCacheCreationInputTokens);
        Assert.Equal(13.5d, candidate.LocalTokensPerSecondP50);
        Assert.Equal(2_000, candidate.LocalPeakMemoryBytes);
        Assert.Equal(850d, candidate.LocalModelLoadP50Milliseconds);
        Assert.False(candidate.Passed);
        Assert.Empty(report.Issues);
    }

    [Fact]
    public void Evaluate_RequiresAdjudicationWhenReviewersDisagree()
    {
        var prediction = new BlindEvaluationPrediction(
            "sample-1",
            "candidate-a",
            "输出",
            true,
            [Review(true, true, true, true, false, true), Review(false, true, true, true, false, true, "reviewer-b")],
            null,
            null,
            "success");

        var report = BlindEvaluationScorer.Evaluate([Gold("sample-1", "low")], [prediction]);

        Assert.Contains(report.Issues, issue => issue.Code == "unresolved-review");
        Assert.False(Assert.Single(report.Candidates).Passed);
    }

    [Fact]
    public void Compare_RejectsAdjudicatorWhoseReviewerIdMatchesAfterTrimming()
    {
        var gold = Gold("sample-1", "low");
        var predictions = new[]
        {
            Prediction("sample-1", "candidate-a", true, Review(true, true, true, true, false, true), null),
            Prediction("sample-1", "baseline", true, Review(true, true, true, true, false, true), null)
        };
        var comparison = new BlindEvaluationPairwiseComparison(
            "sample-1",
            "candidate-a",
            "baseline",
            [new("reviewer-a", "left"), new("reviewer-b", "right")],
            new(" reviewer-a ", "left"));

        var report = BlindEvaluationScorer.Evaluate([gold], predictions, [comparison]);

        Assert.Contains(report.Issues, issue => issue.Code == "unresolved-pairwise");
        Assert.Empty(report.PairwiseComparisons);
    }

    [Fact]
    public void Compare_UsesSameCasesAndWilsonConfidenceForPairedPreferences()
    {
        var gold = Enumerable.Range(0, 30).Select(index => Gold("sample-" + index, "low")).ToArray();
        var predictions = gold.SelectMany(item => new[]
        {
            Prediction(item.Id, "candidate-a", true, Review(true, true, true, true, false, true), null),
            Prediction(item.Id, "baseline", true, Review(true, true, true, true, false, true), null)
        }).ToArray();
        var comparisons = gold.Select((item, index) => index % 2 == 0
            ? new BlindEvaluationPairwiseComparison(
                item.Id,
                "candidate-a",
                "baseline",
                [new("reviewer-a", "left"), new("reviewer-b", "left")],
                null)
            : new BlindEvaluationPairwiseComparison(
                item.Id,
                "baseline",
                "candidate-a",
                [new("reviewer-a", "right"), new("reviewer-b", "right")],
                null)).ToArray();

        var report = BlindEvaluationScorer.Evaluate(gold, predictions, comparisons);

        var pair = Assert.Single(report.PairwiseComparisons);
        Assert.Equal(30, pair.ComparableCount);
        Assert.Equal(1d, pair.LeftWinRate);
        Assert.Equal(30, pair.DecisiveFamilyCount);
        Assert.Equal(30, pair.FamilyLeftWins);
        Assert.Equal(0, pair.FamilyRightWins);
        Assert.InRange(pair.FamilyLeftWinLower95, .88, .94);
        Assert.InRange(pair.FamilyLeftWinUpper95, .99, 1);
        Assert.True(pair.Significant);
        Assert.Empty(report.Issues);
    }

    [Fact]
    public void Compare_DoesNotTreatSamplesFromOneSemanticFamilyAsIndependentEvidence()
    {
        var gold = Enumerable.Range(0, 20)
            .Select(index => Gold("sample-" + index, "low") with { SemanticFamilyId = "same-family" })
            .ToArray();
        var predictions = gold.SelectMany(item => new[]
        {
            Prediction(item.Id, "candidate-a", true, Review(true, true, true, true, false, true), null),
            Prediction(item.Id, "baseline", true, Review(true, true, true, true, false, true), null)
        }).ToArray();
        var comparisons = gold.Select(item => new BlindEvaluationPairwiseComparison(
            item.Id,
            "candidate-a",
            "baseline",
            [new("reviewer-a", "left"), new("reviewer-b", "left")],
            null)).ToArray();

        var report = BlindEvaluationScorer.Evaluate(gold, predictions, comparisons);

        var pair = Assert.Single(report.PairwiseComparisons);
        Assert.Equal(20, pair.ComparableCount);
        Assert.Equal(1, pair.DecisiveFamilyCount);
        Assert.Equal(1, pair.FamilyLeftWins);
        Assert.False(pair.Significant);
    }

    [Fact]
    public void Compare_RequiresAtLeastThirtyDecisiveSemanticFamilies()
    {
        var gold = Enumerable.Range(0, 20).Select(index => Gold("sample-" + index, "low")).ToArray();
        var predictions = gold.SelectMany(item => new[]
        {
            Prediction(item.Id, "candidate-a", true, Review(true, true, true, true, false, true), null),
            Prediction(item.Id, "baseline", true, Review(true, true, true, true, false, true), null)
        }).ToArray();
        var comparisons = gold.Select(item => new BlindEvaluationPairwiseComparison(
            item.Id,
            "candidate-a",
            "baseline",
            [new("reviewer-a", "left"), new("reviewer-b", "left")],
            null)).ToArray();

        var pair = Assert.Single(BlindEvaluationScorer.Evaluate(gold, predictions, comparisons).PairwiseComparisons);

        Assert.Equal(20, pair.DecisiveFamilyCount);
        Assert.True(pair.FamilyLeftWinLower95 > .5);
        Assert.False(pair.Significant);
    }

    [Fact]
    public void Evaluate_CountsProviderFailuresAsMissingAndGroupsNormalizedErrorCategories()
    {
        var gold = new[] { Gold("sample-1", "low"), Gold("sample-2", "low") };
        var predictions = new[]
        {
            Prediction("sample-1", "candidate-a", true, Review(true, true, true, true, false, true), new(100, 10, 5, null, null, null)),
            new BlindEvaluationPrediction("sample-2", "candidate-a", "", false, [], null,
                new BlindEvaluationTelemetry(500, 10, null, null, null, null, "network"), "error")
        };

        var report = BlindEvaluationScorer.Evaluate(gold, predictions);

        var candidate = Assert.Single(report.Candidates);
        Assert.Equal(1, candidate.Predictions);
        Assert.Equal(1, candidate.FailedRequests);
        Assert.Equal(.5d, candidate.CoverageRate);
        Assert.Equal(.5d, candidate.SchemaValidRate);
        Assert.Equal(20, candidate.ApiInputTokens);
        Assert.Null(candidate.ApiOutputTokens);
        Assert.Equal(1, candidate.ErrorCountsByCategory["network"]);
        Assert.Empty(report.Issues);
    }

    [Fact]
    public void Evaluate_RejectsCandidateOutputWithPotentialPersonalDataWithoutEchoingIt()
    {
        var sensitiveOutput = "请联系 test@example.com，手机号 138 0013 8000。";
        var prediction = Prediction("sample-1", "candidate-a", true,
            Review(true, true, true, true, false, true), null) with { Output = sensitiveOutput };

        var report = BlindEvaluationScorer.Evaluate([Gold("sample-1", "low")], [prediction]);

        var issue = Assert.Single(report.Issues);
        Assert.Equal("pii-output", issue.Code);
        Assert.Equal("sample-1", issue.RecordId);
        Assert.DoesNotContain("test@example.com", issue.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("138 0013 8000", issue.Message, StringComparison.Ordinal);
        Assert.False(Assert.Single(report.Candidates).Passed);
    }

    [Fact]
    public void Evaluate_RejectsCandidateOutputWithExplicitWeChatIdWithoutEchoingIt()
    {
        const string sensitiveOutput = "请添加微信号：alice_123456 联系。";
        var prediction = Prediction("sample-1", "candidate-a", true,
            Review(true, true, true, true, false, true), null) with { Output = sensitiveOutput };

        var report = BlindEvaluationScorer.Evaluate([Gold("sample-1", "low")], [prediction]);

        var issue = Assert.Single(report.Issues);
        Assert.Equal("pii-output", issue.Code);
        Assert.DoesNotContain("alice_123456", issue.Message, StringComparison.Ordinal);
        Assert.False(Assert.Single(report.Candidates).Passed);
    }

    [Theory]
    [InlineData("AIza01234567890123456789012345678901234")]
    [InlineData("gsk_0123456789abcdefghijklmnopqrstuvwxyz")]
    [InlineData("sk-or-v1-0123456789abcdef0123456789abcdef")]
    [InlineData("sk-or-v1-a")]
    [InlineData("sk-ant-api03-0123456789abcdef0123456789abcdef")]
    [InlineData("sk-ant-api03-a")]
    [InlineData("xai-0123456789abcdef0123456789abcdef")]
    [InlineData("xai-a")]
    [InlineData("sk-proj-0123456789abcdef0123456789abcdef")]
    [InlineData("sk-proj-a")]
    [InlineData("ark-123e4567-e89b-12d3-a456-426614174000-suffix012345")]
    [InlineData("ark-123e4567-e89b-12d3-a456-426614174000-a")]
    [InlineData("sk-sp-0123456789abcdef0123456789abcdef")]
    [InlineData("sk-sp-a")]
    [InlineData("tp-0123456789abcdef0123456789abcdef")]
    [InlineData("tp-a")]
    public void Evaluate_RejectsCandidateOutputWithKnownProviderKeysWithoutEchoingThem(string key)
    {
        var sensitiveOutput = "配置示例：" + key;
        var prediction = Prediction("sample-1", "candidate-a", true,
            Review(true, true, true, true, false, true), null) with { Output = sensitiveOutput };

        var report = BlindEvaluationScorer.Evaluate([Gold("sample-1", "low")], [prediction]);

        var issue = Assert.Single(report.Issues);
        Assert.Equal("pii-output", issue.Code);
        Assert.DoesNotContain(key, issue.Message, StringComparison.Ordinal);
        Assert.False(Assert.Single(report.Candidates).Passed);
    }

    [Fact]
    public void Evaluate_RejectsCandidateOutputWithLabeledOpaqueGeminiAuthKeyWithoutEchoingIt()
    {
        const string sensitiveKey = "AQ_auth_key_example_2026";
        var prediction = Prediction("sample-1", "candidate-a", true,
            Review(true, true, true, true, false, true), null) with
        {
            Output = "x-goog-api-key: " + sensitiveKey
        };

        var report = BlindEvaluationScorer.Evaluate([Gold("sample-1", "low")], [prediction]);

        var issue = Assert.Single(report.Issues);
        Assert.Equal("pii-output", issue.Code);
        Assert.DoesNotContain(sensitiveKey, issue.Message, StringComparison.Ordinal);
        Assert.False(Assert.Single(report.Candidates).Passed);
    }

    [Fact]
    public void Evaluate_RejectsCandidateOutputWithLabeledSparkApiPasswordWithoutEchoingIt()
    {
        const string sensitiveValue = "APIPassword: 123456";
        var prediction = Prediction("sample-1", "candidate-a", true,
            Review(true, true, true, true, false, true), null) with { Output = sensitiveValue };

        var report = BlindEvaluationScorer.Evaluate([Gold("sample-1", "low")], [prediction]);

        var issue = Assert.Single(report.Issues);
        Assert.Equal("pii-output", issue.Code);
        Assert.DoesNotContain(sensitiveValue, issue.Message, StringComparison.Ordinal);
        Assert.False(Assert.Single(report.Candidates).Passed);
    }

    [Fact]
    public void Evaluate_RedactsSensitiveSampleIdFromDiagnosticReferences()
    {
        const string sensitiveId = "reviewer@example.com";
        var prediction = Prediction(sensitiveId, "candidate-a", true,
            Review(true, true, true, true, false, true), null) with { Output = "请添加微信号：alice_123456 联系。" };

        var report = BlindEvaluationScorer.Evaluate([Gold(sensitiveId, "low")], [prediction]);

        Assert.Contains(report.Issues, issue => issue.Code == "pii-output");
        Assert.DoesNotContain(report.Issues,
            issue => issue.RecordId.Contains(sensitiveId, StringComparison.Ordinal) || issue.Message.Contains(sensitiveId, StringComparison.Ordinal));
        Assert.Contains(report.Issues, issue => issue.Code == "pii-output" && issue.RecordId == "[redacted-record-id]");
    }

    [Fact]
    public void Evaluate_RejectsNegativeCacheTokenTelemetry()
    {
        var prediction = Prediction("sample-1", "candidate-a", true,
            Review(true, true, true, true, false, true),
            new BlindEvaluationTelemetry(100, 10, 5, null, null, null, ApiCacheReadInputTokens: -1));

        var report = BlindEvaluationScorer.Evaluate([Gold("sample-1", "low")], [prediction]);

        Assert.Contains(report.Issues, issue => issue.Code == "invalid-telemetry" && issue.RecordId == "sample-1");
    }

    [Fact]
    public void Evaluate_LeavesCacheUsageUnknownWhenAnyPredictionOmitsTheBreakdown()
    {
        var gold = new[] { Gold("sample-1", "low"), Gold("sample-2", "low") };
        var predictions = new[]
        {
            Prediction("sample-1", "candidate-a", true, Review(true, true, true, true, false, true),
                new BlindEvaluationTelemetry(100, 100, 20, null, null, null, ApiCacheReadInputTokens: 30, ApiCacheCreationInputTokens: 10)),
            Prediction("sample-2", "candidate-a", true, Review(true, true, true, true, false, true),
                new BlindEvaluationTelemetry(100, 100, 20, null, null, null))
        };

        var report = BlindEvaluationScorer.Evaluate(gold, predictions);

        var candidate = Assert.Single(report.Candidates);
        Assert.Null(candidate.ApiCacheReadInputTokens);
        Assert.Null(candidate.ApiCacheCreationInputTokens);
    }

    private static BlindEvaluationRecord Gold(string id, string risk) => new()
    {
        Id = id,
        Task = "polish",
        SemanticFamilyId = id,
        Source = new("licensed", "licence-ref", "direct identifiers removed"),
        Input = "输入 " + id,
        Constraints = ["保留事实"],
        RiskLevel = risk,
        Split = "frozen_test",
        ExpectedDecision = "produce",
        Annotations = new(["事实锚点"], "正式", [], false, risk == "high" ? ["关键事实"] : []),
        HumanReview = new(["curator-a", "curator-b"], "accepted")
    };

    private static BlindEvaluationReview Review(bool facts, bool usable, bool tone, bool clarify, bool reversed, bool safe, string reviewer = "reviewer-a") =>
        new(reviewer, facts, usable, tone, clarify, reversed, safe,
            FidelityScore: reversed ? 1 : 3,
            DirectUsabilityScore: usable ? 4 : 3);

    private static Dictionary<string, JsonElement> Context(string key, string value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return new(StringComparer.Ordinal) { [key] = document.RootElement.Clone() };
    }

    private static BlindEvaluationPrediction Prediction(
        string id,
        string candidate,
        bool schemaValid,
        BlindEvaluationReview review,
        BlindEvaluationTelemetry? telemetry) =>
        new(id, candidate, "这是最终输出。", schemaValid, [review, review with { ReviewerId = "reviewer-b" }], null, telemetry, "success");
}
