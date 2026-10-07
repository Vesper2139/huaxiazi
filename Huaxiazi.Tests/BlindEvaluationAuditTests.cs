using System.IO;
using System.Text.Json;
using Huaxiazi.DatasetBuilder;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class BlindEvaluationAuditTests
{
    [Fact]
    public void Validate_Ready500SampleDatasetPasses()
    {
        var records = Enumerable.Range(0, 500)
            .Select(index =>
            {
                var record = ValidRecord(index.ToString("D4"), "family-" + index, index < 400 ? "development" : "frozen_test") with
                {
                    Task = index % 5 < 3 ? "polish" : "prompt_optimize",
                    InputStyle = InputStyleFor(index),
                    RiskLevel = index % 10 == 0 ? "high" : "low",
                    ExpectedDecision = index % 20 == 1 ? "clarify" : "produce",
                    Annotations = new BlindEvaluationAnnotations(
                        ["本周进度"],
                        "专业、克制",
                        index % 20 == 2 ? ["保留项目符号格式"] : [],
                        index % 20 == 1,
                        index % 10 == 0 ? ["交付时间与条件"] : [])
                };
                return WithMatchingReview(record);
            })
            .ToArray();

        var issues = BlindEvaluationAuditor.Validate(records);

        Assert.Empty(issues);
        var coverage = BlindEvaluationAuditor.SummarizeCoverage(records);
        Assert.Equal(500, coverage.TotalCount);
        Assert.Equal(300, coverage.PolishCount);
        Assert.Equal(200, coverage.PromptOptimizeCount);
        Assert.Equal(400, coverage.DevelopmentCount);
        Assert.Equal(100, coverage.FrozenTestCount);
        Assert.Equal(50, coverage.HighRiskCount);
        Assert.Equal(450, coverage.RoutineCount);
        Assert.Equal(25, coverage.ClarifyCount);
        Assert.Equal(25, coverage.FormatRequirementCount);
        Assert.Equal(500, coverage.FactOrConstraintAnchorCount);
        Assert.Equal(40, coverage.Development.HighRiskCount);
        Assert.Equal(20, coverage.Development.ClarifyCount);
        Assert.Equal(20, coverage.Development.FormatRequirementCount);
        Assert.Equal(10, coverage.FrozenTest.HighRiskCount);
        Assert.Equal(5, coverage.FrozenTest.ClarifyCount);
        Assert.Equal(5, coverage.FrozenTest.FormatRequirementCount);
        Assert.Equal(5, coverage.Development.DistinctInputStyleCount);
        Assert.Equal(5, coverage.FrozenTest.DistinctInputStyleCount);
        Assert.Equal(100, coverage.FrozenTest.InputStyleCounts.Values.Sum());
    }

    [Fact]
    public void Validate_AcceptsSequentialConversationTurnsAndRequiresFinalTurnInputToMatch()
    {
        var valid = ValidRecord("conversation-valid", "conversation-family-valid", "development") with
        {
            ConversationId = "conversation-1",
            Turns = [new(1, "通知大家周三开放。"), new(2, "改为周五开放，周三不要再写。")],
            Input = "改为周五开放，周三不要再写。"
        };

        var validIssues = BlindEvaluationAuditor.Validate([valid], minimumSampleCount: 1);
        var invalid = valid with { Input = "通知大家周三开放。" };
        var invalidIssues = BlindEvaluationAuditor.Validate([invalid], minimumSampleCount: 1);

        Assert.DoesNotContain(validIssues, issue => issue.Code.StartsWith("conversation-", StringComparison.Ordinal));
        Assert.Contains(invalidIssues, issue => issue.Code == "conversation-final-input");
    }

    [Fact]
    public void BlindEvaluationSchema_DeclaresOptionalPairedPolishConversationFields()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Huaxiazi.csproj")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory!.FullName,
            "datasets", "ai-evaluation", "blind-eval.schema.json")));

        var root = schema.RootElement;
        Assert.Equal("3.1", BlindEvaluationAdmissionProtocol.Version);
        Assert.Equal("3.1", BlindEvaluationAdmissionProtocol.SchemaVersion);
        Assert.EndsWith("v3.1.json", root.GetProperty("$id").GetString(), StringComparison.Ordinal);
        var properties = root.GetProperty("properties");
        Assert.True(properties.TryGetProperty("conversation_id", out _));
        var turns = properties.GetProperty("turns");
        Assert.Equal(2, turns.GetProperty("minItems").GetInt32());
        Assert.Equal("turn_index", turns.GetProperty("items").GetProperty("required")[0].GetString());
        Assert.Equal("user_input", turns.GetProperty("items").GetProperty("required")[1].GetString());
        Assert.Equal("polish", root.GetProperty("allOf")[0].GetProperty("then")
            .GetProperty("properties").GetProperty("task").GetProperty("const").GetString());
        Assert.Contains("conversation_id", root.GetProperty("dependentRequired").GetProperty("turns")
            .EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public void Validate_DetectsTrainingOverlapInAnEarlierConversationTurn()
    {
        var record = ValidRecord("conversation-overlap", "conversation-family-overlap", "development") with
        {
            ConversationId = "conversation-overlap",
            Turns = [new(1, "训练集中的早先请求文本。"), new(2, "我改主意，请按最新要求写。")],
            Input = "我改主意，请按最新要求写。"
        };

        var issues = BlindEvaluationAuditor.Validate([record], minimumSampleCount: 1,
            knownTrainingRecords: [new("train", "训练集中的早先请求文本。", "training-family")]);

        Assert.Contains(issues, issue => issue.Code == "input-overlap-training" && issue.RecordId == record.Id);
    }

    [Fact]
    public void SummarizeReviewerAgreement_ReportsExactLabelAndFieldAgreementWithoutContent()
    {
        var first = ValidRecord("agreement-a", "agreement-family-a", "development");
        var second = ValidRecord("agreement-b", "agreement-family-b", "development");
        var secondVotes = second.HumanReview.IndependentAnnotations!;
        var changedSecondReview = second with
        {
            HumanReview = second.HumanReview with
            {
                IndependentAnnotations = [secondVotes[0], secondVotes[1] with { Labels = secondVotes[1].Labels with { ExpectedDecision = "clarify" } }]
            }
        };

        var summary = BlindEvaluationAuditor.SummarizeReviewerAgreement([first, changedSecondReview]);

        Assert.Equal(2, summary.ComparableRecordCount);
        Assert.Equal(1, summary.WholeLabelAgreementCount);
        Assert.Equal(0.5, summary.WholeLabelAgreementRate);
        Assert.Equal(2, summary.Fields["task"].ComparableCount);
        Assert.Equal(2, summary.Fields["task"].ExactAgreementCount);
        Assert.Equal(1, summary.Fields["expected_decision"].ExactAgreementCount);
        Assert.Equal(0.5, summary.Fields["expected_decision"].ExactAgreementRate);
        Assert.True(Math.Abs(summary.Fields["expected_decision"].KrippendorffAlphaNominal!.Value) < 1e-12);
        Assert.Equal(3, summary.Fields["expected_decision"].PooledCategoryCounts!["produce"]);
        Assert.Equal(1, summary.Fields["expected_decision"].PooledCategoryCounts!["clarify"]);
        Assert.Null(summary.Fields["task"].KrippendorffAlphaNominal);
        Assert.Equal("computed", summary.Fields["expected_decision"].ChanceCorrectedStatus);
        Assert.Equal("undefined_perfect_expected_agreement", summary.Fields["task"].ChanceCorrectedStatus);
        Assert.Equal("not_applicable_scale", summary.Fields["risk_level"].ChanceCorrectedStatus);
        Assert.Equal("not_applicable_scale", summary.Fields["reference_output"].ChanceCorrectedStatus);
        Assert.Null(summary.Fields["clarification_required"].PositiveSpecificAgreement);
        Assert.Equal(1, summary.Fields["clarification_required"].NegativeSpecificAgreement);
        Assert.DoesNotContain("本周进度", JsonSerializer.Serialize(summary), StringComparison.Ordinal);
    }

    [Fact]
    public void SummarizeReviewerAgreement_ExcludesDuplicateOrMismatchedReviewerReferences()
    {
        var duplicateReviewer = ValidRecord("same-reviewer", "agreement-family-same", "development");
        var duplicateVotes = duplicateReviewer.HumanReview.IndependentAnnotations!;
        duplicateReviewer = duplicateReviewer with
        {
            HumanReview = duplicateReviewer.HumanReview with
            {
                ReviewerIds = ["reviewer-a", "reviewer-a"],
                IndependentAnnotations = [duplicateVotes[0], duplicateVotes[1] with { ReviewerId = "reviewer-a" }]
            }
        };
        var mismatchedReferences = ValidRecord("mismatched-reviewer", "agreement-family-mismatch", "development") with
        {
            HumanReview = ValidRecord("mismatched-inner", "inner-family", "development").HumanReview with
            {
                ReviewerIds = ["reviewer-a", "reviewer-c"]
            }
        };

        var summary = BlindEvaluationAuditor.SummarizeReviewerAgreement([duplicateReviewer, mismatchedReferences]);

        Assert.Equal(0, summary.ComparableRecordCount);
        Assert.Null(summary.WholeLabelAgreementRate);
        Assert.All(summary.Fields.Values, field => Assert.Null(field.ExactAgreementRate));
    }

    [Fact]
    public void SummarizeReviewerAgreement_ShowsWhyRawAgreementNeedsChanceCorrection()
    {
        var records = Enumerable.Range(0, 10).Select(index =>
        {
            var record = ValidRecord("prevalence-" + index, "prevalence-family-" + index, "development");
            if (index != 9) return record;
            var votes = record.HumanReview.IndependentAnnotations!;
            return record with
            {
                HumanReview = record.HumanReview with
                {
                    IndependentAnnotations = [votes[0], votes[1] with { Labels = votes[1].Labels with { ExpectedDecision = "clarify" } }]
                }
            };
        }).ToArray();

        var field = BlindEvaluationAuditor.SummarizeReviewerAgreement(records).Fields["expected_decision"];

        Assert.Equal(0.9, field.ExactAgreementRate);
        Assert.True(Math.Abs(field.KrippendorffAlphaNominal!.Value) < 1e-12);
        Assert.Equal(19, field.PooledCategoryCounts!["produce"]);
        Assert.Equal(1, field.PooledCategoryCounts["clarify"]);
    }

    [Fact]
    public void SummarizeReviewerAgreement_UsesNominalAlphaWithOneTimePerRecordReviewerReferences()
    {
        var records = Enumerable.Range(0, 10).Select(index =>
        {
            var record = ValidRecord("rotating-reviewers-" + index, "rotating-family-" + index, "development");
            var labels = GoldLabels(record) with { ExpectedDecision = index % 2 == 0 ? "produce" : "clarify" };
            return record with
            {
                HumanReview = new BlindEvaluationHumanReview(
                    ["reviewer-ref-a-" + index, "reviewer-ref-b-" + index], "accepted", null,
                    [new("reviewer-ref-a-" + index, labels), new("reviewer-ref-b-" + index, labels)])
            };
        }).ToArray();

        var field = BlindEvaluationAuditor.SummarizeReviewerAgreement(records).Fields["expected_decision"];

        Assert.Equal(10, field.ComparableCount);
        Assert.Equal(1, field.KrippendorffAlphaNominal);
        Assert.Equal("computed", field.ChanceCorrectedStatus);
        Assert.Equal(20, field.PooledCategoryCounts!.Values.Sum());
    }

    [Fact]
    public void SummarizeReviewerAgreement_ReportsPositiveAndNegativeAgreementForBinaryClarification()
    {
        var pairs = new[] { (First: true, Second: true), (First: true, Second: false), (First: false, Second: false) };
        var records = pairs.Select((pair, index) =>
        {
            var record = ValidRecord("binary-agreement-" + index, "binary-family-" + index, "development");
            var votes = record.HumanReview.IndependentAnnotations!;
            var firstLabels = votes[0].Labels with
            {
                Annotations = votes[0].Labels.Annotations with { ClarificationRequired = pair.First }
            };
            var secondLabels = votes[1].Labels with
            {
                Annotations = votes[1].Labels.Annotations with { ClarificationRequired = pair.Second }
            };
            return record with
            {
                HumanReview = record.HumanReview with
                {
                    IndependentAnnotations = [votes[0] with { Labels = firstLabels }, votes[1] with { Labels = secondLabels }]
                }
            };
        }).ToArray();

        var field = BlindEvaluationAuditor.SummarizeReviewerAgreement(records).Fields["clarification_required"];

        Assert.Equal(2d / 3d, field.PositiveSpecificAgreement);
        Assert.Equal(2d / 3d, field.NegativeSpecificAgreement);
    }

    [Fact]
    public void Validate_RejectsTaskCoverageThatExistsOnlyAsOneTokenSample()
    {
        var records = Enumerable.Range(0, 500)
            .Select(index => ValidRecord(index.ToString("D4"), "task-family-" + index, index < 400 ? "development" : "frozen_test") with
            {
                Task = index == 499 ? "prompt_optimize" : "polish"
            })
            .ToArray();

        var issues = BlindEvaluationAuditor.Validate(records);

        Assert.Contains(issues, issue => issue.Code == "task-coverage");
    }

    [Fact]
    public void Validate_RejectsBlindSetWithoutHighRiskClarificationOrFormatCoverage()
    {
        var records = Enumerable.Range(0, 500)
            .Select(index => ValidRecord(index.ToString("D4"), "coverage-family-" + index, index < 400 ? "development" : "frozen_test") with
            {
                Task = index % 2 == 0 ? "polish" : "prompt_optimize"
            })
            .ToArray();

        var issues = BlindEvaluationAuditor.Validate(records);

        Assert.Contains(issues, issue => issue.Code == "coverage-high-risk");
        Assert.Contains(issues, issue => issue.Code == "coverage-clarify");
        Assert.Contains(issues, issue => issue.Code == "coverage-format");
    }

    [Fact]
    public void Validate_RejectsFrozenSplitThatOmitsCoveredDimensions()
    {
        var records = Enumerable.Range(0, 500)
            .Select(index => ValidRecord(index.ToString("D4"), "split-coverage-family-" + index, index < 400 ? "development" : "frozen_test") with
            {
                Task = index % 5 < 3 ? "polish" : "prompt_optimize",
                RiskLevel = index < 50 ? "high" : "low",
                ExpectedDecision = index is >= 50 and < 75 ? "clarify" : "produce",
                Annotations = new BlindEvaluationAnnotations(
                    ["本周进度"],
                    "专业、克制",
                    index is >= 75 and < 100 ? ["保留项目符号格式"] : [],
                    index is >= 50 and < 75,
                    index < 50 ? ["交付时间与条件"] : [])
            })
            .ToArray();

        var issues = BlindEvaluationAuditor.Validate(records);

        Assert.Contains(issues, issue => issue.Code == "coverage-frozen-test-high-risk");
        Assert.Contains(issues, issue => issue.Code == "coverage-frozen-test-clarify");
        Assert.Contains(issues, issue => issue.Code == "coverage-frozen-test-format");
    }

    [Fact]
    public void Validate_RejectsFrozenSplitWithInsufficientInputStyleDiversity()
    {
        var records = Enumerable.Range(0, 500)
            .Select(index => ValidRecord(index.ToString("D4"), "style-family-" + index, index < 400 ? "development" : "frozen_test") with
            {
                Task = index % 2 == 0 ? "polish" : "prompt_optimize",
                InputStyle = index < 400 ? InputStyleFor(index) : "colloquial",
                RiskLevel = index % 10 == 0 ? "high" : "low",
                ExpectedDecision = index % 20 == 1 ? "clarify" : "produce",
                Annotations = new BlindEvaluationAnnotations(
                    ["本周进度"],
                    "专业、克制",
                    index % 20 == 2 ? ["保留项目符号格式"] : [],
                    index % 20 == 1,
                    index % 10 == 0 ? ["交付时间与条件"] : [])
            })
            .ToArray();

        var issues = BlindEvaluationAuditor.Validate(records);

        Assert.Contains(issues, issue => issue.Code == "coverage-frozen-test-input-style-diversity");
    }

    [Fact]
    public void Validate_RejectsAcceptedGoldWhoseIndependentReviewersDisagree()
    {
        var record = ValidRecord("review-disagreement", "review-family", "development");
        var firstLabels = GoldLabels(record);
        var secondLabels = firstLabels with
        {
            Annotations = firstLabels.Annotations with { TargetTone = "轻松随意" }
        };
        record = record with
        {
            HumanReview = new BlindEvaluationHumanReview(
                ["reviewer-a", "reviewer-b"], "accepted", null,
                [new("reviewer-a", firstLabels), new("reviewer-b", secondLabels)])
        };

        var issues = BlindEvaluationAuditor.Validate([record], minimumSampleCount: 1);

        Assert.Contains(issues, issue => issue.Code == "review-disagreement");
    }

    [Fact]
    public void Validate_AcceptsThirdPartyAdjudicationAndRequiresFinalGoldToMatchIt()
    {
        var record = ValidRecord("adjudicated-review", "adjudicated-family", "development");
        var first = GoldLabels(record);
        var dissent = first with { ExpectedDecision = "clarify" };
        var final = first with { Annotations = first.Annotations with { TargetTone = "严谨、简洁" } };
        record = record with
        {
            Annotations = final.Annotations,
            HumanReview = new BlindEvaluationHumanReview(
                ["reviewer-a", "reviewer-b"], "adjudicated", "reviewer-c",
                [new("reviewer-a", first), new("reviewer-b", dissent)],
                new("reviewer-c", final))
        };

        var issues = BlindEvaluationAuditor.Validate([record], minimumSampleCount: 1);

        Assert.DoesNotContain(issues, issue => issue.Code is "review-evidence" or "adjudication-evidence" or "adjudication-label-mismatch");
    }

    [Fact]
    public void Validate_RejectsFinalGoldThatDiffersFromUnanimousAcceptedLabels()
    {
        var record = ValidRecord("accepted-label-mismatch", "accepted-family", "development");
        var votes = GoldLabels(record);
        record = record with
        {
            Annotations = record.Annotations with { TargetTone = "过度热情" },
            HumanReview = new BlindEvaluationHumanReview(
                ["reviewer-a", "reviewer-b"], "accepted", null,
                [new("reviewer-a", votes), new("reviewer-b", votes)])
        };

        var issues = BlindEvaluationAuditor.Validate([record], minimumSampleCount: 1);

        Assert.Contains(issues, issue => issue.Code == "review-label-mismatch");
    }

    [Fact]
    public void Validate_ScansHiddenReviewerEvidenceForSensitiveData()
    {
        var record = ValidRecord("review-pii", "review-pii-family", "development");
        var labels = GoldLabels(record) with { ReferenceOutput = "联系邮箱 reviewer@example.com" };
        record = record with
        {
            HumanReview = new BlindEvaluationHumanReview(
                ["reviewer-a", "reviewer-b"], "accepted", null,
                [new("reviewer-a", labels), new("reviewer-b", labels)])
        };

        var issues = BlindEvaluationAuditor.Validate([record], minimumSampleCount: 1);
        var reviewOnlyIssues = BlindEvaluationAuditor.ValidateHumanReviewEvidence([record]);

        Assert.Contains(issues, issue => issue.Code == "pii");
        Assert.Contains(reviewOnlyIssues, issue => issue.Code == "pii");
    }

    [Fact]
    public void Validate_ScansChineseSensitiveLabelsInReviewerEvidence()
    {
        var record = ValidRecord("reviewer-label-pii", "reviewer-label-pii-family", "development");
        var labels = GoldLabels(record);
        var reviewer = "微信号：alice_123456";
        record = record with
        {
            HumanReview = new BlindEvaluationHumanReview(
                [reviewer, "reviewer-b"], "accepted", null,
                [new(reviewer, labels), new("reviewer-b", labels)])
        };

        var datasetIssues = BlindEvaluationAuditor.Validate([record], minimumSampleCount: 1);
        var reviewOnlyIssues = BlindEvaluationAuditor.ValidateHumanReviewEvidence([record]);

        Assert.Contains(datasetIssues, issue => issue.Code == "pii");
        Assert.Contains(reviewOnlyIssues, issue => issue.Code == "pii");
        Assert.DoesNotContain(reviewer, string.Join("\n", datasetIssues.Select(issue => issue.Message)), StringComparison.Ordinal);
        Assert.DoesNotContain(reviewer, string.Join("\n", reviewOnlyIssues.Select(issue => issue.Message)), StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsSmallUnreviewedUnauthorizedAndLeakingDataset()
    {
        var development = ValidRecord("a", "shared-family", "development");
        var frozen = ValidRecord("a", "shared-family", "frozen_test") with
        {
            Source = new BlindEvaluationSource("unknown", "", ""),
            Input = "请联系 13800138000",
            HumanReview = new BlindEvaluationHumanReview(["reviewer-1", "reviewer-1"], "pending")
        };

        var issues = BlindEvaluationAuditor.Validate([development, frozen]);

        Assert.Contains(issues, issue => issue.Code == "dataset-too-small");
        Assert.Contains(issues, issue => issue.Code == "duplicate-id");
        Assert.Contains(issues, issue => issue.Code == "family-cross-split");
        Assert.Contains(issues, issue => issue.Code == "source-authorization");
        Assert.Contains(issues, issue => issue.Code == "pii");
        Assert.Contains(issues, issue => issue.Code == "double-review");
    }

    [Theory]
    [InlineData("project_synthetic_legacy")]
    [InlineData("ai_assisted_draft")]
    [InlineData("human_authored_internal")]
    public void Validate_RejectsInternalRegressionOrigins(string origin)
    {
        var record = ValidRecord("internal-origin", "internal-origin-family", "frozen_test") with
        {
            Source = new BlindEvaluationSource(origin, "internal-only", "not applicable")
        };

        var issues = BlindEvaluationAuditor.Validate([record], minimumSampleCount: 1);

        Assert.Contains(issues, issue => issue.Code == "source-authorization");
    }

    [Fact]
    public void Validate_RequiresHighRiskFactsAndClarificationAlignment()
    {
        var record = ValidRecord("high-risk", "family-high", "frozen_test") with
        {
            RiskLevel = "high",
            ExpectedDecision = "produce",
            Annotations = new BlindEvaluationAnnotations([], "正式", [], true, [])
        };

        var issues = BlindEvaluationAuditor.Validate([record], minimumSampleCount: 1);

        Assert.Contains(issues, issue => issue.Code == "high-risk-facts");
        Assert.Contains(issues, issue => issue.Code == "clarification-mismatch");
    }

    [Fact]
    public void Validate_RejectsSemanticFamilyAndExactInputFromTrainingOrDevelopmentData()
    {
        var record = ValidRecord("overlap", "known-family", "frozen_test") with
        {
            Input = "known training input"
        };
        var known = new BlindEvaluationKnownRecord("train", "known training input", "known-family");

        var issues = BlindEvaluationAuditor.Validate([record], minimumSampleCount: 1, knownTrainingRecords: [known]);

        Assert.Contains(issues, issue => issue.Code == "family-overlap-training");
        Assert.Contains(issues, issue => issue.Code == "input-overlap-training");
    }

    [Fact]
    public void Validate_RequiresDistinctAdjudicatorForAdjudicatedSourceLabels()
    {
        var record = ValidRecord("adjudicated", "family-adjudicated", "frozen_test") with
        {
            HumanReview = new BlindEvaluationHumanReview(["reviewer-a", "reviewer-b"], "adjudicated", "reviewer-b")
        };

        var issues = BlindEvaluationAuditor.Validate([record], minimumSampleCount: 1);

        Assert.Contains(issues, issue => issue.Code == "double-review");
    }

    [Fact]
    public void Validate_DetectsChineseNationalIdNumbers()
    {
        var record = ValidRecord("national-id", "family-national-id", "frozen_test") with
        {
            Input = "证件号码：11010519900101123X"
        };

        var issues = BlindEvaluationAuditor.Validate([record], minimumSampleCount: 1);

        Assert.Contains(issues, issue => issue.Code == "pii");
    }

    [Fact]
    public void Validate_DetectsFormattedMobilePhonesInOutputRequirements()
    {
        var record = ValidRecord("phone", "family-phone", "frozen_test") with
        {
            Annotations = new BlindEvaluationAnnotations(
                ["请保留联系方式 +86 138-0013-8000"],
                "正式",
                [],
                false,
                [])
        };

        var issues = BlindEvaluationAuditor.Validate([record], minimumSampleCount: 1);

        Assert.Contains(issues, issue => issue.Code == "pii");
    }

    [Theory]
    [InlineData("recipient", "请联系 138-0013-8000 确认安排")]
    [InlineData("preference_instructions", "需要时发到 alice@example.com")]
    [InlineData("custom_system_prompt", "内部令牌 api_key=abcdefghijklmno")]
    [InlineData("api_password", "APIPassword: 123456")]
    [InlineData("request_headers", "x-goog-api-key: AQ_auth_key_example_2026")]
    [InlineData("persona", "微信号：alice_123456")]
    public void Validate_DetectsSensitiveValuesInContextWithoutEchoingThem(string key, string value)
    {
        using var valueDocument = JsonDocument.Parse(JsonSerializer.Serialize(value));
        var record = ValidRecord("context-pii", "context-pii-family", "frozen_test") with
        {
            Context = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                [key] = valueDocument.RootElement.Clone()
            }
        };
        var piiIssue = Assert.Single(BlindEvaluationAuditor.Validate([record], minimumSampleCount: 1), issue => issue.Code == "pii");

        Assert.DoesNotContain(value, piiIssue.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Authorization: Bearer abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("AWS access key: AKIAIOSFODNN7EXAMPLE")]
    [InlineData("AWS temporary access key: ASIAIOSFODNN7EXAMPLE")]
    [InlineData("Slack token: xox" + "b-1234567890-1234567890-abcdefghijklmnopqrstuvwxyz")]
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
    [InlineData("-----BEGIN PRIVATE KEY-----")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----")]
    public void Validate_DetectsCommonCredentialFormatsWithoutEchoingThem(string value)
    {
        var record = ValidRecord("credential", "family-credential", "development") with
        {
            Input = "请检查这个配置：" + value
        };

        var piiIssue = Assert.Single(BlindEvaluationAuditor.Validate([record], minimumSampleCount: 1), issue => issue.Code == "pii");

        Assert.DoesNotContain(value, piiIssue.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Bearer credentials should only be sent over TLS.")]
    [InlineData("AKIA is a prefix used in AWS documentation.")]
    [InlineData("akiaiosfodnn7example is not a canonical AWS access key ID.")]
    [InlineData("Slack token formats begin with xoxb-.")]
    [InlineData("-----BEGIN PRIVATE KEY--- is an incomplete header.")]
    [InlineData("Authorization may use a Bearer scheme.")]
    [InlineData("AIza is a documented prefix but this value is incomplete.")]
    [InlineData("AIza0123456789012345678901234567890123 is one character short.")]
    [InlineData("gsk is part of Groq's variable name GROQ_API_KEY.")]
    [InlineData("The documented OpenRouter key prefix is sk-or-v1-.")]
    [InlineData("The documented Anthropic key example starts with sk-ant-api03-.")]
    [InlineData("The xAI example only mentions the xai- prefix.")]
    [InlineData("OpenAI project-key examples use the sk-proj- prefix.")]
    [InlineData("Volcengine Ark uses the documented shape ark-<uuid>-<suffix>.")]
    [InlineData("Alibaba Coding Plan keys use the sk-sp- prefix.")]
    [InlineData("MiMo Token Plan keys use the tp- prefix.")]
    [InlineData("Spark API documentation calls the credential APIPassword.")]
    [InlineData("APIPassword:")]
    public void ContainsPotentialSensitiveData_DoesNotMatchCredentialVocabularyAlone(string text)
    {
        Assert.False(BlindEvaluationAuditor.ContainsPotentialSensitiveData(text));
    }

    [Fact]
    public void Validate_DetectsSensitiveDataInNestedContextObjects()
    {
        using var contextDocument = JsonDocument.Parse("""{"profile":{"contact":"微信号：alice_123456"},"api_key":"abcdefghijklmno"}""");
        var record = ValidRecord("nested-context-pii", "nested-context-pii-family", "frozen_test") with
        {
            Context = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["custom"] = contextDocument.RootElement.Clone()
            }
        };

        var issues = BlindEvaluationAuditor.Validate([record], minimumSampleCount: 1);

        Assert.Contains(issues, issue => issue.Code == "pii");
    }

    [Fact]
    public void Validate_DetectsExplicitQQGroupIdentifiers()
    {
        var record = ValidRecord("qq-group", "family-qq-group", "frozen_test") with
        {
            Input = "请加入讨论QQ群：123456789"
        };

        var issues = BlindEvaluationAuditor.Validate([record], minimumSampleCount: 1);

        Assert.Contains(issues, issue => issue.Code == "pii");
    }

    [Fact]
    public void Validate_DetectsExplicitQQAccountNumbers()
    {
        var record = ValidRecord("qq-account", "family-qq-account", "frozen_test") with
        {
            Input = "通过 QQ：123456789 联系我"
        };

        var issues = BlindEvaluationAuditor.Validate([record], minimumSampleCount: 1);

        Assert.Contains(issues, issue => issue.Code == "pii");
    }

    [Theory]
    [InlineData("请添加微信号：alice_123456 联系。")]
    [InlineData("请联系 wxid_abcd123456 完成确认。")]
    public void Validate_DetectsExplicitWeChatAccountIdentifiers(string input)
    {
        var record = ValidRecord("wechat-account", "family-wechat-account", "frozen_test") with
        {
            Input = input
        };

        var issues = BlindEvaluationAuditor.Validate([record], minimumSampleCount: 1);

        Assert.Contains(issues, issue => issue.Code == "pii");
    }

    [Fact]
    public void Validate_ScansIdentifiersAndSourceReferencesWithoutEchoingSensitiveRecordId()
    {
        var sensitiveId = ValidRecord("metadata-id", "family-metadata-id", "development") with
        {
            Id = "reviewer@example.com"
        };
        var sensitiveFamily = ValidRecord("metadata-family", "family-metadata-family", "development") with
        {
            SemanticFamilyId = "family-13800138000"
        };
        var sensitiveSourceReference = ValidRecord("metadata-source", "family-metadata-source", "development") with
        {
            Source = new BlindEvaluationSource("licensed", "source-contact-test@example.com", "direct identifiers removed")
        };

        var issues = BlindEvaluationAuditor.Validate([sensitiveId, sensitiveFamily, sensitiveSourceReference], minimumSampleCount: 1);
        var piiIssues = issues.Where(issue => issue.Code == "pii").ToArray();

        Assert.Equal(3, piiIssues.Length);
        Assert.DoesNotContain(piiIssues, issue => issue.RecordId.Contains("reviewer@example.com", StringComparison.Ordinal));
        Assert.Contains(piiIssues, issue => issue.RecordId == "[redacted-record-id]");
    }

    private static BlindEvaluationRecord ValidRecord(string id, string family, string split)
    {
        var record = new BlindEvaluationRecord
        {
            Id = "blind-" + id,
            Task = "polish",
            SemanticFamilyId = family,
            Source = new BlindEvaluationSource("licensed", "license-record-" + id, "direct identifiers removed"),
            Input = "请说明本周进度。" + id,
            InputStyle = "colloquial",
            Constraints = ["不得虚构完成情况"],
            RiskLevel = "low",
            Split = split,
            ExpectedDecision = "produce",
            Annotations = new BlindEvaluationAnnotations(["本周进度"], "专业、克制", [], false, [])
        };
        return WithMatchingReview(record);
    }

    private static string InputStyleFor(int index) => (index % 5) switch
    {
        0 => "colloquial",
        1 => "fragmentary",
        2 => "formal",
        3 => "speech_transcription",
        _ => "mixed_language"
    };

    private static BlindEvaluationGoldLabelSet GoldLabels(BlindEvaluationRecord record) => new(
        record.Task, record.InputStyle, record.Constraints, record.RiskLevel,
        record.ExpectedDecision, record.ReferenceOutput, record.Annotations);

    private static BlindEvaluationRecord WithMatchingReview(BlindEvaluationRecord record)
    {
        var labels = GoldLabels(record);
        return record with
        {
            HumanReview = new BlindEvaluationHumanReview(["reviewer-a", "reviewer-b"], "accepted", null,
                [new("reviewer-a", labels), new("reviewer-b", labels)])
        };
    }
}
