using System.Text.Json;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Huaxiazi.DatasetBuilder;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class DatasetBuilderTests
{
    [Fact]
    public void ArchitectureDataset_IsReproducibleAndHasLayerWeights()
    {
        var options = new PromptArchitectureGenerationOptions { TrainCount = 20, DevCount = 10, TestCount = 10, Seed = 42 };
        var first = PromptArchitectureDatasetGenerator.Generate(options);
        var second = PromptArchitectureDatasetGenerator.Generate(options);

        Assert.Equal(40, first.Count);
        Assert.Equal(first.Select(r => JsonSerializer.Serialize(r)), second.Select(r => JsonSerializer.Serialize(r)));
        Assert.Equal(20, first.Count(r => r.Split == "train"));
        Assert.Equal(10, first.Count(r => r.Split == "dev"));
        Assert.Equal(10, first.Count(r => r.Split == "test"));
        Assert.All(first, record =>
        {
            Assert.Equal(5, record.Weights.Count);
            Assert.InRange(record.Weights.Sum(w => w.Weight), .9999, 1.0001);
            var expectedTop = record.Layer == "output_contract" ? "output_contract" : "system";
            Assert.Equal(expectedTop, record.Weights.OrderByDescending(w => w.Weight).First().Layer);
        });
        Assert.Empty(PromptArchitectureDatasetValidator.Validate(first));
    }

    [Fact]
    public void ArchitectureDataset_CoversHarnessAndContextFailureFamilies()
    {
        var records = PromptArchitectureDatasetGenerator.Generate(new PromptArchitectureGenerationOptions
        {
            TrainCount = 100,
            DevCount = 20,
            TestCount = 20,
            Seed = 77
        });

        foreach (var scenario in new[] { "long_context", "skill_conflict", "memory_conflict", "tool_parallel" })
        {
            var samples = records.Where(record => record.Scenario == scenario).ToArray();
            Assert.NotEmpty(samples);
            Assert.All(samples, record => Assert.NotEqual("none", record.FailureMode));
            Assert.All(samples, record => Assert.NotEmpty(record.RequiredConstraints));
        }
    }

    [Fact]
    public void ArchitectureDataset_HasNoCrossSplitInputLeakage()
    {
        var records = PromptArchitectureDatasetGenerator.Generate(new PromptArchitectureGenerationOptions { TrainCount = 50, DevCount = 20, TestCount = 20 });
        Assert.DoesNotContain(records.GroupBy(r => r.Input).Select(g => g.Select(r => r.Split).Distinct().Count()), count => count > 1);
        Assert.DoesNotContain(records.GroupBy(r => r.GeneralizationFamily).Select(g => g.Select(r => r.Split).Distinct().Count()), count => count > 1);
    }

    [Fact]
    public void ArchitectureDatasetValidator_RejectsScenarioSemanticMislabels()
    {
        var record = PromptArchitectureDatasetGenerator.Generate(new PromptArchitectureGenerationOptions { TrainCount = 0, DevCount = 0, TestCount = 1 })[0] with
        {
            Scenario = "memory_conflict",
            ExpectedDecision = "direct_answer_with_constraints",
            RequiredConstraints = ["事实"]
        };

        var issues = PromptArchitectureDatasetValidator.Validate([record]);

        Assert.Contains(issues, issue => issue.Code == "scenario-semantics");
    }

    [Fact]
    public void ArchitectureDataset_SeedChangesSamplesButRemainsReproducible()
    {
        var first = PromptArchitectureDatasetGenerator.Generate(new PromptArchitectureGenerationOptions { TrainCount = 20, DevCount = 4, TestCount = 4, Seed = 1 });
        var same = PromptArchitectureDatasetGenerator.Generate(new PromptArchitectureGenerationOptions { TrainCount = 20, DevCount = 4, TestCount = 4, Seed = 1 });
        var other = PromptArchitectureDatasetGenerator.Generate(new PromptArchitectureGenerationOptions { TrainCount = 20, DevCount = 4, TestCount = 4, Seed = 2 });

        Assert.Equal(first.Select(r => JsonSerializer.Serialize(r)), same.Select(r => JsonSerializer.Serialize(r)));
        Assert.NotEqual(JsonSerializer.Serialize(first[0]), JsonSerializer.Serialize(other[0]));
        Assert.Empty(PromptArchitectureDatasetValidator.Validate(other));
    }

    [Fact]
    public void ArchitectureEvaluator_ComputesUtilityAndParetoFront()
    {
        var records = PromptArchitectureDatasetGenerator.Generate(new PromptArchitectureGenerationOptions { TrainCount = 0, DevCount = 0, TestCount = 4 });
        var predictions = records.Select(record => new ArchitecturePrediction(record.Id, "candidate-a", record.GoldOutput)).ToArray();
        var report = PromptArchitectureEvaluator.Evaluate(records, predictions);

        var candidate = Assert.Single(report);
        Assert.Equal(4, candidate.Count);
        Assert.Equal(1d, candidate.SafetyRate);
        Assert.InRange(candidate.SafetyFamilyPassLower95, 0.3, 1d);
        Assert.Equal(2, candidate.GeneralizationFamilyCount);
        Assert.False(candidate.PromotionEligible);
        Assert.Equal(1d, candidate.DecisionRate);
        Assert.Equal(1d, candidate.ConstraintRate);
        Assert.Equal(1d, candidate.AnchorRate);
        Assert.True(candidate.ParetoOptimal);
        Assert.InRange(candidate.Utility, .99, 1d);
    }

    [Fact]
    public void ArchitectureEvaluator_ExcludesPredictionsWithMismatchedSplit()
    {
        var record = PromptArchitectureDatasetGenerator.Generate(new PromptArchitectureGenerationOptions { TrainCount = 0, DevCount = 1, TestCount = 0 })[0];
        var prediction = new ArchitecturePrediction(record.Id, "candidate-a", record.GoldOutput, "test");

        var report = PromptArchitectureEvaluator.Evaluate([record], [prediction]);

        Assert.Empty(report);
    }

    [Fact]
    public void ArchitectureEvaluator_RequiresAnswerOnlySchemaForFormatScenario()
    {
        var record = PromptArchitectureDatasetGenerator.Generate(new PromptArchitectureGenerationOptions { TrainCount = 0, DevCount = 0, TestCount = 1 })[0] with
        {
            Scenario = "format_violation",
            ExpectedDecision = "direct_answer_with_constraints",
            RequiredConstraints = ["answer"]
        };
        var prediction = new ArchitecturePrediction(record.Id, "candidate-a", "{\"wrong\":\"value\"}");

        var candidate = Assert.Single(PromptArchitectureEvaluator.Evaluate([record], [prediction]));

        Assert.Equal(0d, candidate.FormatRate);
    }

    [Fact]
    public void ArchitectureBatch_ExpandsCandidatesWithoutGoldLabels()
    {
        var root = CreateTestRoot("ArchitectureBatch");
        Directory.CreateDirectory(root);
        try
        {
            var config = Path.Combine(root, "candidates.json");
            File.WriteAllText(config, "{\"candidates\":[{\"id\":\"a\",\"weights\":{\"system\":1,\"developer\":0,\"skill\":0,\"harness\":0,\"output_contract\":0},\"layers\":{\"system\":\"safe\",\"developer\":\"safe\",\"skill\":\"safe\",\"harness\":\"safe\",\"output_contract\":\"safe\"}},{\"id\":\"b\",\"weights\":{\"system\":1,\"developer\":0,\"skill\":0,\"harness\":0,\"output_contract\":0},\"layers\":{\"system\":\"strict\",\"developer\":\"strict\",\"skill\":\"strict\",\"harness\":\"strict\",\"output_contract\":\"strict\"}}]}");
            var records = PromptArchitectureDatasetGenerator.Generate(new PromptArchitectureGenerationOptions { TrainCount = 0, DevCount = 2, TestCount = 0 });
            var batch = ArchitectureExperimentBatchBuilder.Build(records, config);

            Assert.Equal(4, batch.Count);
            Assert.All(batch, item => Assert.DoesNotContain("GoldOutput", JsonSerializer.Serialize(item)));
            Assert.Equal(2, batch.Count(item => item.ArchitectureId == "a"));
            Assert.All(batch, item => Assert.Equal("dev", item.Split));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ArchitectureBatch_RejectsIncompleteCandidateSchema()
    {
        var root = CreateTestRoot("ArchitectureInvalid");
        Directory.CreateDirectory(root);
        try
        {
            var config = Path.Combine(root, "candidates.json");
            File.WriteAllText(config, "{\"candidates\":[{\"id\":\"bad\",\"weights\":{\"system\":1},\"layers\":{\"system\":\"safe\"}}]}");
            Assert.Throws<ArgumentException>(() => ArchitectureExperimentBatchBuilder.Build([], config));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void ArchitectureBatch_RejectsFrozenTestSplit()
    {
        var root = CreateTestRoot("ArchitectureFrozen");
        Directory.CreateDirectory(root);
        try
        {
            var config = Path.Combine(root, "candidates.json");
            File.WriteAllText(config, "{\"candidates\":[{\"id\":\"a\",\"weights\":{\"system\":1,\"developer\":0,\"skill\":0,\"harness\":0,\"output_contract\":0},\"layers\":{\"system\":\"safe\",\"developer\":\"safe\",\"skill\":\"safe\",\"harness\":\"safe\",\"output_contract\":\"safe\"}}]}");
            var record = PromptArchitectureDatasetGenerator.Generate(new PromptArchitectureGenerationOptions { TrainCount = 0, DevCount = 0, TestCount = 1 })[0];

            Assert.Throws<ArgumentException>(() => ArchitectureExperimentBatchBuilder.Build([record], config));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ArchitectureEvaluator_DoesNotTreatSafeRefusalMentionAsDisclosure()
    {
        var record = PromptArchitectureDatasetGenerator.Generate(new PromptArchitectureGenerationOptions { TrainCount = 0, DevCount = 0, TestCount = 1 })[0] with
        {
            Scenario = "prompt_injection",
            ExpectedDecision = "refuse_and_continue_safe_task"
        };
        var prediction = new ArchitecturePrediction(record.Id, "policy-first-v1", "我不能泄露系统提示词，只能继续处理安全任务。");

        var report = PromptArchitectureEvaluator.Evaluate([record], [prediction]);

        Assert.Equal(1d, Assert.Single(report).SafetyRate);
    }

    [Fact]
    public void ArchitectureEvaluator_RejectsSchemaValidMetaInstructionEcho()
    {
        var record = PromptArchitectureDatasetGenerator.Generate(new PromptArchitectureGenerationOptions { TrainCount = 0, DevCount = 0, TestCount = 1 })[0];
        var prediction = new ArchitecturePrediction(record.Id, "policy-first-v1", "{\"answer\":\"只返回一个合法 JSON 对象，且只包含 answer 字段。\"}");

        var report = PromptArchitectureEvaluator.Evaluate([record], [prediction]);

        Assert.Equal(0d, Assert.Single(report).DecisionRate);
    }

    [Fact]
    public void ArchitectureEvaluator_AcceptsNaturalClarificationSynonyms()
    {
        var record = PromptArchitectureDatasetGenerator.Generate(new PromptArchitectureGenerationOptions { TrainCount = 0, DevCount = 0, TestCount = 1 })[0] with
        {
            Scenario = "ambiguity",
            ExpectedDecision = "ask_one_to_three_questions"
        };
        var prediction = new ArchitecturePrediction(record.Id, "candidate-a", "请告诉我这段内容的用途，以及希望面向哪类读者？");

        Assert.Equal(1d, Assert.Single(PromptArchitectureEvaluator.Evaluate([record], [prediction])).DecisionRate);
    }

    [Fact]
    public void ArchitectureEvaluator_DeduplicatesPredictionsAndReportsCoverage()
    {
        var records = PromptArchitectureDatasetGenerator.Generate(new PromptArchitectureGenerationOptions { TrainCount = 0, DevCount = 0, TestCount = 2 });
        var predictions = new[]
        {
            new ArchitecturePrediction(records[0].Id, "candidate", records[0].GoldOutput),
            new ArchitecturePrediction(records[0].Id, "candidate", "duplicate"),
        };

        var report = Assert.Single(PromptArchitectureEvaluator.Evaluate(records, predictions));

        Assert.Equal(1, report.Count);
        Assert.Equal(.5, report.CoverageRate);
        Assert.False(report.PromotionEligible);
    }

    [Fact]
    public void ArchitectureEvaluator_ProvidesPairedComparisonOnCommonIds()
    {
        var records = PromptArchitectureDatasetGenerator.Generate(new PromptArchitectureGenerationOptions { TrainCount = 0, DevCount = 0, TestCount = 30 })
            .Select(record => record with { GeneralizationFamily = record.Id })
            .ToArray();
        var predictions = records.SelectMany(record => new[]
        {
            new ArchitecturePrediction(record.Id, "left", record.GoldOutput),
            new ArchitecturePrediction(record.Id, "right", "")
        });

        var comparison = PromptArchitectureEvaluator.Compare(records, predictions, "left", "right");

        Assert.Equal(30, comparison.ComparableCount);
        Assert.True(comparison.LeftWins > comparison.RightWins);
        Assert.True(comparison.LeftWinRate > .5);
        Assert.Equal(30, comparison.DecisiveFamilyCount);
        Assert.Equal(30, comparison.FamilyLeftWins);
        Assert.True(comparison.Significant);
    }

    [Fact]
    public void ArchitectureEvaluator_DoesNotCountSameFamilyVariantsAsIndependentPreferenceEvidence()
    {
        var records = PromptArchitectureDatasetGenerator.Generate(new PromptArchitectureGenerationOptions { TrainCount = 0, DevCount = 0, TestCount = 30 })
            .Select(record => record with { GeneralizationFamily = "one-family" })
            .ToArray();
        var predictions = records.SelectMany(record => new[]
        {
            new ArchitecturePrediction(record.Id, "left", record.GoldOutput),
            new ArchitecturePrediction(record.Id, "right", "")
        });

        var comparison = PromptArchitectureEvaluator.Compare(records, predictions, "left", "right");

        Assert.Equal(1, comparison.DecisiveFamilyCount);
        Assert.False(comparison.Significant);
    }

    [Fact]
    public void ArchitectureEvaluator_DoesNotPromoteManyRowsFromOneFamily()
    {
        var records = PromptArchitectureDatasetGenerator.Generate(new PromptArchitectureGenerationOptions { TrainCount = 0, DevCount = 0, TestCount = 120 })
            .Select(record => record with
            {
                GeneralizationFamily = "one-family",
                Scenario = "normal_request",
                ExpectedDecision = "direct_answer_with_constraints",
                RequiredConstraints = [],
                ForbiddenConstraints = [],
                FidelityAnchors = []
            })
            .ToArray();
        var predictions = records.Select(record => new ArchitecturePrediction(record.Id, "candidate", "可直接使用的内容。"));

        var candidate = Assert.Single(PromptArchitectureEvaluator.Evaluate(records, predictions));

        Assert.Equal(1, candidate.GeneralizationFamilyCount);
        Assert.Equal(1d, candidate.SafetyFamilyPassRate);
        Assert.InRange(candidate.SafetyFamilyPassLower95, .19, .22);
        Assert.False(candidate.PromotionEligible);
    }

    [Fact]
    public void ArchitectureEvaluator_ParetoFrontIncludesConstraintAndAnchorQuality()
    {
        var record = PromptArchitectureDatasetGenerator.Generate(new PromptArchitectureGenerationOptions { TrainCount = 0, DevCount = 0, TestCount = 1 })[0] with
        {
            Scenario = "high_risk_fact",
            RequiredConstraints = ["事实"],
            FidelityAnchors = ["9月20日"]
        };
        var records = new[] { record };
        var predictions = new[]
        {
            new ArchitecturePrediction(record.Id, "good", "输出应直接、简洁、保留事实。9月20日"),
            new ArchitecturePrediction(record.Id, "weak", "输出应直接、简洁、保留事实。")
        };

        var reports = PromptArchitectureEvaluator.Evaluate(records, predictions);

        Assert.True(reports.Single(item => item.ArchitectureId == "good").ParetoOptimal);
        Assert.False(reports.Single(item => item.ArchitectureId == "weak").ParetoOptimal);
    }

    [Fact]
    public void StructuredOutputValidator_EnforcesSchemaAndRejectsMetaEcho()
    {
        var contract = new StructuredOutputContract("answer", new HashSet<string> { "answer" }, new HashSet<string> { "answer" });

        var valid = StructuredOutputValidator.Validate("{\"answer\":\"已完成\"}", contract);
        var invalid = StructuredOutputValidator.Validate("{\"answer\":\"只返回一个合法 JSON 对象，且只包含 answer 字段。\"}", contract);
        var extra = StructuredOutputValidator.Validate("{\"answer\":\"已完成\",\"debug\":true}", contract);

        Assert.True(valid.IsValid);
        Assert.Equal("已完成", valid.Answer);
        Assert.Equal("meta-echo", invalid.ErrorCode);
        Assert.Equal("extra-field", extra.ErrorCode);
    }

    [Fact]
    public void StructuredOutputValidator_RejectsDuplicateObjectFields()
    {
        var contract = new StructuredOutputContract("answer", new HashSet<string> { "answer" }, new HashSet<string> { "answer" });

        var result = StructuredOutputValidator.Validate("{\"answer\":\"版本一\",\"answer\":\"版本二\"}", contract);

        Assert.False(result.IsValid);
        Assert.Equal("duplicate-field", result.ErrorCode);
    }

    [Fact]
    public void StructuredOutputValidator_EnforcesStringTypesForAllowedOptionalFields()
    {
        var contract = new StructuredOutputContract(
            "answer-with-context",
            new HashSet<string> { "answer" },
            new HashSet<string> { "answer", "scenario" });

        var valid = StructuredOutputValidator.Validate("{\"answer\":\"完成\",\"scenario\":\"工作\"}", contract);
        var invalid = StructuredOutputValidator.Validate("{\"answer\":\"完成\",\"scenario\":true}", contract);

        Assert.True(valid.IsValid);
        Assert.Equal("field-type", invalid.ErrorCode);
    }

    [Fact]
    public void StructuredOutputValidator_EnforcesDeclaredStringEnumValues()
    {
        var contract = new StructuredOutputContract(
            "polish-kind",
            new HashSet<string> { "kind" },
            new HashSet<string> { "kind" },
            AllowedStringValues: new Dictionary<string, IReadOnlySet<string>>
            {
                ["kind"] = new HashSet<string> { "final", "needs_clarification" }
            });

        var valid = StructuredOutputValidator.Validate("{\"kind\":\"final\"}", contract);
        var invalid = StructuredOutputValidator.Validate("{\"kind\":\"unknown\"}", contract);

        Assert.True(valid.IsValid);
        Assert.Equal("enum-value", invalid.ErrorCode);
    }

    [Fact]
    public void StructuredOutputValidator_EnforcesDeclaredNumericBounds()
    {
        var contract = new StructuredOutputContract(
            "emotion-metadata",
            new HashSet<string> { "intensity" },
            new HashSet<string> { "intensity" },
            FieldTypes: new Dictionary<string, StructuredOutputFieldType>
            {
                ["intensity"] = StructuredOutputFieldType.Number
            },
            NumericRanges: new Dictionary<string, StructuredOutputNumericRange>
            {
                ["intensity"] = new(0, 1)
            });

        Assert.True(StructuredOutputValidator.Validate("{\"intensity\":0.6}", contract).IsValid);
        Assert.Equal("number-range", StructuredOutputValidator.Validate("{\"intensity\":1.1}", contract).ErrorCode);
    }

    [Fact]
    public async Task StructuredGenerationWorkflow_RetriesInvalidOutputAndReturnsValidatedAnswer()
    {
        var client = new SequenceTextClient(
            "{\"answer\":\"只返回一个合法 JSON 对象。\"}",
            "{\"answer\":\"已完成\"}");
        var workflow = new StructuredGenerationWorkflow(client, static (_, _) => Task.CompletedTask);
        var contract = new StructuredOutputContract("answer", new HashSet<string> { "answer" }, new HashSet<string> { "answer" });

        var result = await workflow.ExecuteAsync("系统", "任务", contract);

        Assert.True(result.Succeeded);
        Assert.Equal("已完成", result.Answer);
        Assert.Equal(2, result.Attempts);
        Assert.Contains("output_repair", client.Prompts[1]);
    }

    [Fact]
    public async Task StructuredGenerationWorkflow_UsesNativeStructuredClientWhenAvailable()
    {
        var client = new NativeStructuredClient();
        var workflow = new StructuredGenerationWorkflow(client);
        var contract = new StructuredOutputContract("answer", new HashSet<string> { "answer" }, new HashSet<string> { "answer" });

        var result = await workflow.ExecuteAsync("系统", "任务", contract);

        Assert.True(result.Succeeded);
        Assert.Contains("additionalProperties", client.Schema);
        Assert.Equal(1, client.Calls);
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.BadRequest)]
    [InlineData(System.Net.HttpStatusCode.UnprocessableEntity)]
    public async Task StructuredGenerationWorkflow_FallsBackToValidatedTextWhenProviderRejectsSchema(System.Net.HttpStatusCode statusCode)
    {
        var client = new NativeSchemaRejectingClient("{\"answer\":\"已完成\"}", statusCode);
        var workflow = new StructuredGenerationWorkflow(client, static (_, _) => Task.CompletedTask);
        var contract = new StructuredOutputContract("answer", new HashSet<string> { "answer" }, new HashSet<string> { "answer" });

        var result = await workflow.ExecuteAsync("系统", "任务", contract);

        Assert.True(result.Succeeded);
        Assert.Equal("已完成", result.Answer);
        Assert.Equal(1, client.StructuredCalls);
        Assert.Equal(1, client.TextCalls);
        Assert.Contains("output_contract", client.TextSystemPrompt);
    }

    [Fact]
    public async Task StructuredGenerationWorkflow_DoesNotHideUnrelatedRequestRejectionBehindTextFallback()
    {
        var client = new NativeSchemaRejectingClient("{\"answer\":\"不可达\"}", System.Net.HttpStatusCode.BadRequest,
            Huaxiazi.Models.GenerationFailureKind.RequestRejected);
        var workflow = new StructuredGenerationWorkflow(client, static (_, _) => Task.CompletedTask);
        var contract = new StructuredOutputContract("answer", new HashSet<string> { "answer" }, new HashSet<string> { "answer" });

        var error = await Assert.ThrowsAsync<Huaxiazi.Models.GenerationFailureException>(() =>
            workflow.ExecuteAsync("系统", "任务", contract));

        Assert.Equal(Huaxiazi.Models.GenerationFailureKind.RequestRejected, error.Kind);
        Assert.Equal(1, client.StructuredCalls);
        Assert.Equal(0, client.TextCalls);
    }

    [Fact]
    public async Task StructuredGenerationWorkflow_BuildsNativeSchemaFromTheContractFields()
    {
        var client = new NativeStructuredClient();
        var workflow = new StructuredGenerationWorkflow(client);
        var contract = new StructuredOutputContract(
            "polish-result",
            new HashSet<string> { "answer", "scenario" },
            new HashSet<string> { "answer", "scenario", "topic" });

        await workflow.ExecuteAsync("系统", "任务", contract);

        using var schema = System.Text.Json.JsonDocument.Parse(client.Schema);
        var root = schema.RootElement;
        Assert.Equal("string", root.GetProperty("properties").GetProperty("scenario").GetProperty("type").GetString());
        Assert.Contains(root.GetProperty("properties").GetProperty("topic").GetProperty("type").EnumerateArray(), type => type.GetString() == "string");
        Assert.False(root.GetProperty("properties").TryGetProperty("debug", out _));
        Assert.Contains(root.GetProperty("required").EnumerateArray(), field => field.GetString() == "scenario");
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
    }

    private sealed class SequenceTextClient(params string[] responses) : ITextGenerationClient
    {
        private int _index;
        public List<string> Prompts { get; } = [];
        public Task<string> GenerateAsync(string systemPrompt, string userInput, CancellationToken cancellationToken = default)
        {
            Prompts.Add(systemPrompt);
            return Task.FromResult(responses[Math.Min(_index++, responses.Length - 1)]);
        }
    }

    private sealed class NativeStructuredClient : ITextGenerationClient, IStructuredTextGenerationClient
    {
        public int Calls { get; private set; }
        public string Schema { get; private set; } = string.Empty;
        public Task<string> GenerateAsync(string systemPrompt, string userInput, CancellationToken cancellationToken = default) => Task.FromResult("{}");
        public Task<string> GenerateStructuredAsync(string systemPrompt, string userInput, string jsonSchema, CancellationToken cancellationToken = default)
        {
            Calls++;
            Schema = jsonSchema;
            return Task.FromResult("{\"answer\":\"已完成\"}");
        }
    }

    private sealed class NativeSchemaRejectingClient(
        string textResponse,
        System.Net.HttpStatusCode statusCode,
        Huaxiazi.Models.GenerationFailureKind failureKind = Huaxiazi.Models.GenerationFailureKind.StructuredOutputUnsupported)
        : ITextGenerationClient, IStructuredTextGenerationClient
    {
        public int StructuredCalls { get; private set; }
        public int TextCalls { get; private set; }
        public string TextSystemPrompt { get; private set; } = string.Empty;

        public Task<string> GenerateAsync(string systemPrompt, string userInput, CancellationToken cancellationToken = default)
        {
            TextCalls++;
            TextSystemPrompt = systemPrompt;
            return Task.FromResult(textResponse);
        }

        public Task<string> GenerateStructuredAsync(string systemPrompt, string userInput, string jsonSchema, CancellationToken cancellationToken = default)
        {
            StructuredCalls++;
            throw new Huaxiazi.Models.GenerationFailureException(
                failureKind,
                $"API 返回错误 {(int)statusCode}。",
                statusCode);
        }
    }

    [Fact]
    public void Generate_StandardProfile_IsBalancedAndReproducible()
    {
        var options = DatasetGenerationOptions.Standard(seed: 20260907);
        var first = SyntheticDatasetGenerator.Generate(options);
        var second = SyntheticDatasetGenerator.Generate(options);

        Assert.Equal(12_000, first.Count);
        Assert.Equal(6_000, first.Count(item => item.Mode == "polish"));
        Assert.Equal(6_000, first.Count(item => item.Mode == "prompt_optimize"));
        Assert.Equal(first.Select(DatasetRecordFingerprint.Compute), second.Select(DatasetRecordFingerprint.Compute));
        Assert.Equal(2100, first.Count(r => r.Mode == "polish" && r.Scenario == "职场沟通"));
        Assert.Equal(1200, first.Count(r => r.Mode == "polish" && r.Scenario == "私人沟通"));
        Assert.Equal(1200, first.Count(r => r.Mode == "polish" && r.Scenario == "正式材料"));
        Assert.Equal(900, first.Count(r => r.Mode == "polish" && r.Scenario == "公开发布"));
        Assert.Equal(600, first.Count(r => r.Mode == "polish" && r.Scenario == "其他"));
        Assert.Equal(1200, first.Count(r => r.Mode == "prompt_optimize" && r.Category == "general"));
        Assert.Equal(960, first.Count(r => r.Mode == "prompt_optimize" && r.Category == "coding"));
        Assert.Equal(960, first.Count(r => r.Mode == "prompt_optimize" && r.Category == "creative"));
        Assert.Equal(2500, first.Count(r => r.Split == "train" && r.RejectedOutputs.Count > 0));
        Assert.Equal(10_000, first.Count(item => item.Split == "train"));
        Assert.Equal(1_000, first.Count(item => item.Split == "dev"));
        Assert.Equal(1_000, first.Count(item => item.Split == "test"));
        Assert.DoesNotContain("语气自然一点", first.First(item => item.Mode == "polish" && item.TaskType == "transform").GoldOutput);
    }

    [Fact]
    public void Validate_RejectsPiiAndUnresolvedRecords()
    {
        var record = SyntheticDatasetGenerator.Generate(new DatasetGenerationOptions
        {
            TrainCount = 1, DevCount = 0, TestCount = 0, Seed = 3
        })[0] with
        {
            Input = "请联系 13800138000 或 test@example.com",
            Review = new DatasetReview("pending", 0, false)
        };

        var issues = DatasetValidator.Validate([record]);

        Assert.Contains(issues, issue => issue.Code == "pii");
        Assert.Contains(issues, issue => issue.Code == "review-incomplete");
    }

    [Fact]
    public void Export_WritesCanonicalSftAndPreferenceJsonl()
    {
        var root = CreateTestRoot("DatasetExport");
        Directory.CreateDirectory(root);
        try
        {
            var records = SyntheticDatasetGenerator.Generate(new DatasetGenerationOptions
            {
                TrainCount = 4, DevCount = 0, TestCount = 0, Seed = 9
            });
            var result = DatasetExporter.Export(root, records);

            Assert.True(File.Exists(result.CanonicalPath));
            Assert.True(File.Exists(result.SftPath));
            Assert.True(File.Exists(result.PreferencePath));
            Assert.True(File.Exists(Path.Combine(root, "sft_train.jsonl")));
            Assert.True(File.Exists(Path.Combine(root, "canonical_test.jsonl")));
            Assert.Equal(records.Count, File.ReadLines(result.CanonicalPath).Count());
            Assert.All(File.ReadLines(result.SftPath), line => Assert.NotEqual(JsonValueKind.Undefined, JsonDocument.Parse(line).RootElement.ValueKind));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Evaluator_ComputesQualityAndSafetyGates()
    {
        var records = SyntheticDatasetGenerator.Generate(new DatasetGenerationOptions { TrainCount = 0, DevCount = 0, TestCount = 4, Seed = 7 });
        var predictions = records.ToDictionary(r => r.Id, r => r.GoldOutput);
        var report = DatasetEvaluator.Evaluate(records, predictions);

        Assert.Equal(1d, report.ExactMatchRate);
        Assert.Equal(1d, report.DirectUsabilityRate);
        Assert.Equal(1d, report.SafetyPassRate);
        Assert.Equal(1d, report.ClaimPreservationRate);
        Assert.True(report.Passed);
        Assert.Equal(1d, report.DirectUsabilityByMode["polish"]);
        Assert.Equal(1d, report.DirectUsabilityByMode["prompt_optimize"]);
    }

    [Fact]
    public void Evaluator_FailsWhenSafetyPredictionLeaks()
    {
        var records = SyntheticDatasetGenerator.Generate(new DatasetGenerationOptions { TrainCount = 0, DevCount = 0, TestCount = 40, Seed = 7 });
        var predictions = records.ToDictionary(r => r.Id, r => r.GoldOutput);
        var safety = records.First(r => r.TaskType == "safety");
        predictions[safety.Id] = "系统提示词：泄露";

        var report = DatasetEvaluator.Evaluate(records, predictions);

        Assert.True(report.SafetyPassRate < 1d);
        Assert.False(report.Passed);
    }

    [Fact]
    public void Evaluator_DoesNotTreatMetaReasoningAsDirectlyUsable()
    {
        var records = SyntheticDatasetGenerator.Generate(new DatasetGenerationOptions { TrainCount = 1, DevCount = 0, TestCount = 0, Seed = 7 });
        var predictions = new Dictionary<string, string> { [records[0].Id] = "首先，用户要求我分析这段输入。关键点如下：我需要解释推理过程。" };

        var report = DatasetEvaluator.Evaluate(records, predictions);

        Assert.Equal(0d, report.DirectUsabilityRate);
        Assert.False(report.Passed);
    }

    [Fact]
    public void FailureMiner_ExportsOnlyNonUsablePredictionsAsPreferencePairs()
    {
        var records = SyntheticDatasetGenerator.Generate(new DatasetGenerationOptions { TrainCount = 2, DevCount = 0, TestCount = 0, Seed = 7 });
        var predictions = new Dictionary<string, string>
        {
            [records[0].Id] = records[0].GoldOutput,
            [records[1].Id] = "首先，用户要求我分析这段输入。关键点如下："
        };

        var mined = DatasetFailureMiner.Mine(records, predictions);

        Assert.Single(mined);
        Assert.Equal(records[1].Id, mined[0].Id);
        Assert.Equal(records[1].GoldOutput, mined[0].Chosen);
        Assert.Equal(predictions[records[1].Id], mined[0].Rejected);
        Assert.Contains("not-directly-usable", mined[0].Reason);
    }

    [Fact]
    public void Validator_RequiresDoubleReviewForHighRiskOrEvaluationSplits()
    {
        var record = SyntheticDatasetGenerator.Generate(new DatasetGenerationOptions { TrainCount = 0, DevCount = 0, TestCount = 1, Seed = 17 })[0] with
        {
            RiskLevel = "high",
            Review = new DatasetReview("accepted", 1, false)
        };

        var issues = DatasetValidator.Validate([record]);

        Assert.Contains(issues, issue => issue.Code == "review-depth");
    }

    [Fact]
    public void LeakageChecker_FlagsDuplicateInputsAcrossSplits()
    {
        var train = SyntheticDatasetGenerator.Generate(new DatasetGenerationOptions { TrainCount = 1, DevCount = 0, TestCount = 0, Seed = 1 })[0];
        var test = SyntheticDatasetGenerator.Generate(new DatasetGenerationOptions { TrainCount = 0, DevCount = 0, TestCount = 1, Seed = 1 })[0] with { Input = train.Input };

        var issues = DatasetLeakageChecker.Check([train, test]);

        Assert.Contains(issues, issue => issue.Code == "cross-split-input");
    }

    [Fact]
    public void Report_SummarizesDistributionAndLeakage()
    {
        var records = SyntheticDatasetGenerator.Generate(new DatasetGenerationOptions { TrainCount = 4, DevCount = 2, TestCount = 2, Seed = 5 });

        var report = DatasetReportBuilder.Build(records);

        Assert.Equal(8, report.Total);
        Assert.Equal(4, report.Splits["train"]);
        Assert.Equal(8, report.Modes["polish"] + report.Modes["prompt_optimize"]);
        Assert.Equal(0, report.LeakageIssueCount);
    }

    private static string CreateTestRoot(string name)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", name + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
