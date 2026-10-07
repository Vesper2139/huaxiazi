using System.Text.Json;
using Huaxiazi.DatasetBuilder;
using Huaxiazi.BlindEvaluationRunner;
using Huaxiazi.Models;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class BlindEvaluationCoverageMatrixTests
{
    [Fact]
    public void SummarizeCoverage_ReportsEveryTaskSplitCellWithoutChangingAdmissionRules()
    {
        var records = Enumerable.Range(0, 500).Select(index =>
        {
            var split = index < 250 ? "development" : "frozen_test";
            var withinSplit = index % 250;
            var task = withinSplit < 125 ? "polish" : "prompt_optimize";
            var highRisk = task == "prompt_optimize" && withinSplit is >= 125 and < 150;
            var clarification = index is < 13 or >= 375 and < 388;
            var record = Record(index.ToString("D4"), task, split, highRisk ? "high" : "low") with
            {
                InputStyle = InputStyleFor(index),
                ExpectedDecision = clarification ? "clarify" : "produce",
                Annotations = new BlindEvaluationAnnotations(
                    ["deadline"], "formal", clarification ? ["bullet list"] : [], clarification,
                    highRisk ? ["deadline"] : [])
            };
            return WithMatchingReview(record);
        }).ToArray();

        var summary = BlindEvaluationAuditor.SummarizeCoverage(records);
        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        using var document = JsonDocument.Parse(json);
        var coverage = document.RootElement;

        Assert.True(coverage.TryGetProperty("task_split_coverage", out var cells));
        Assert.Equal(4, cells.GetArrayLength());
        Assert.Equal(500, cells.EnumerateArray().Sum(cell => cell.GetProperty("sample_count").GetInt32()));

        var polishDevelopment = FindCell(cells, "polish", "development");
        Assert.Equal(125, polishDevelopment.GetProperty("sample_count").GetInt32());
        Assert.Equal(0, polishDevelopment.GetProperty("high_risk_count").GetInt32());
        Assert.Equal(13, polishDevelopment.GetProperty("clarify_count").GetInt32());
        Assert.Equal(13, polishDevelopment.GetProperty("format_requirement_count").GetInt32());
        Assert.Equal(125, polishDevelopment.GetProperty("fact_or_constraint_anchor_count").GetInt32());

        var promptFrozenTest = FindCell(cells, "prompt_optimize", "frozen_test");
        Assert.Equal(125, promptFrozenTest.GetProperty("sample_count").GetInt32());
        Assert.Equal(25, promptFrozenTest.GetProperty("high_risk_count").GetInt32());
        Assert.Equal(13, promptFrozenTest.GetProperty("clarify_count").GetInt32());
        Assert.Equal(5, promptFrozenTest.GetProperty("distinct_input_style_count").GetInt32());
        Assert.Empty(BlindEvaluationAuditor.Validate(records, minimumSampleCount: 1));
    }

    [Fact]
    public void SummarizeCoverage_ReportsKnownScenarioAndCategoryBucketsWithoutEchoingUnknownContext()
    {
        var records = new[]
        {
            Record("polish-formal", "polish", "development", "low") with
            {
                Context = Context("scenario", "\"正式材料\"")
            },
            Record("polish-unknown", "polish", "development", "low") with
            {
                Context = Context("scenario", "\"请联系13800138000\"")
            },
            Record("prompt-coding", "prompt_optimize", "development", "low") with
            {
                Context = Context("category", "\"Coding\"")
            },
            Record("prompt-data", "prompt_optimize", "development", "low") with
            {
                Context = Context("category", "\"数据分析\"")
            },
            Record("prompt-default", "prompt_optimize", "development", "low"),
            Record("prompt-invalid", "prompt_optimize", "development", "low") with
            {
                Context = Context("category", "{\"untrusted\":true}")
            }
        };

        var summary = BlindEvaluationAuditor.SummarizeCoverage(records);
        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        using var document = JsonDocument.Parse(json);
        var cells = document.RootElement.GetProperty("task_split_coverage");

        var polish = FindCell(cells, "polish", "development").GetProperty("product_slice_counts");
        Assert.Equal(1, polish.GetProperty("正式材料").GetInt32());
        Assert.Equal(1, polish.GetProperty("未指定或自定义").GetInt32());

        var prompt = FindCell(cells, "prompt_optimize", "development").GetProperty("product_slice_counts");
        Assert.Equal(1, prompt.GetProperty("通用任务").GetInt32());
        Assert.Equal(1, prompt.GetProperty("编程开发").GetInt32());
        Assert.Equal(1, prompt.GetProperty("数据分析").GetInt32());
        Assert.Equal(1, prompt.GetProperty("未指定或自定义").GetInt32());
        Assert.DoesNotContain("13800138000", json, StringComparison.Ordinal);
        Assert.DoesNotContain("untrusted", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductCategorySlices_MatchWorkflowFactoryForEveryEnumAndDisplayName()
    {
        var records = PromptCategoryMetadata.AllCategories
            .SelectMany(category => new[] { category.ToString(), category.GetDisplayName() }
                .Select((alias, aliasIndex) => Record($"prompt-{category}-{aliasIndex}", "prompt_optimize", "development", "low") with
                {
                    Context = Context("category", JsonSerializer.Serialize(alias))
                }))
            .ToArray();

        foreach (var record in records)
        {
            var expectedCategory = Enum.Parse<PromptCategory>(record.Id.Split('-')[1]);
            Assert.Equal(expectedCategory, BlindWorkflowRequestFactory.Create(record).PromptRequest!.Category);
        }

        var cell = BlindEvaluationAuditor.SummarizeCoverage(records).TaskSplitCoverage
            .Single(item => item.Task == "prompt_optimize" && item.Split == "development");
        foreach (var category in PromptCategoryMetadata.AllCategories)
            Assert.Equal(2, cell.ProductSliceCounts[category.GetDisplayName()]);
        Assert.Equal(0, cell.ProductSliceCounts["未指定或自定义"]);
    }

    private static JsonElement FindCell(JsonElement cells, string task, string split) =>
        cells.EnumerateArray().Single(cell =>
            cell.GetProperty("task").GetString() == task &&
            cell.GetProperty("split").GetString() == split);

    private static BlindEvaluationRecord Record(string id, string task, string split, string risk) => new()
    {
        Id = id,
        Task = task,
        SemanticFamilyId = "family-" + id,
        Source = new BlindEvaluationSource("project_owned", "source-" + id, "no personal data"),
        Input = "test input " + id,
        InputStyle = "colloquial",
        RiskLevel = risk,
        Split = split,
        ExpectedDecision = "produce",
        Constraints = [],
        Annotations = new BlindEvaluationAnnotations([], "neutral", [], false, [])
    };

    private static Dictionary<string, JsonElement> Context(string name, string jsonValue) =>
        new(StringComparer.Ordinal) { [name] = JsonDocument.Parse(jsonValue).RootElement.Clone() };

    private static string InputStyleFor(int index) => (index % 5) switch
    {
        0 => "colloquial",
        1 => "fragmentary",
        2 => "formal",
        3 => "speech_transcription",
        _ => "mixed_language"
    };

    private static BlindEvaluationRecord WithMatchingReview(BlindEvaluationRecord record)
    {
        var labels = new BlindEvaluationGoldLabelSet(record.Task, record.InputStyle, record.Constraints, record.RiskLevel,
            record.ExpectedDecision, record.ReferenceOutput, record.Annotations);
        return record with
        {
            HumanReview = new BlindEvaluationHumanReview(["reviewer-a", "reviewer-b"], "accepted", null,
                [new("reviewer-a", labels), new("reviewer-b", labels)])
        };
    }
}
