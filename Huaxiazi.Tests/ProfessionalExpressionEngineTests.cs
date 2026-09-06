using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Huaxiazi.Models;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class ProfessionalExpressionEngineTests
{
    [Fact]
    public void Plan_PolishRequest_PreservesEntitiesFactsAndSelectsWorkplaceStrategy()
    {
        var plan = new ProfessionalizationPlanner().Create(new ProfessionalizationRequest
        {
            Input = "王总，原定8月20日交付，目前可能晚2天。",
            Mode = ApplicationMode.Polish
        });

        Assert.Equal("text-polisher", plan.StrategyId);
        Assert.Equal("职场沟通", plan.Scenario);
        Assert.Contains("王总", plan.FidelityAnchors);
        Assert.Contains("8月20日", plan.FidelityAnchors);
        Assert.Contains("2天", plan.FidelityAnchors);
        Assert.False(plan.NeedsClarification);
    }

    [Fact]
    public void Plan_OnlyAsksForMateriallyAmbiguousShortRequest()
    {
        var plan = new ProfessionalizationPlanner().Create(new ProfessionalizationRequest
        {
            Input = "帮我回复一下",
            Mode = ApplicationMode.Polish
        });

        Assert.True(plan.NeedsClarification);
        Assert.InRange(plan.ClarificationQuestions.Count, 1, 3);
    }

    [Fact]
    public void Plan_PromptOptimize_UsesPromptCoreAndRequestedShape()
    {
        var plan = new ProfessionalizationPlanner().Create(new ProfessionalizationRequest
        {
            Input = "做一个库存系统",
            Mode = ApplicationMode.PromptOptimize,
            Category = PromptCategory.Coding,
            Depth = PromptDepth.Detailed
        });

        Assert.Equal("prompt-optimizer", plan.StrategyId);
        Assert.Contains("编程开发", plan.StrategyInstructions);
        Assert.Contains("详细", plan.StrategyInstructions);
    }

    [Fact]
    public void QualityValidator_FlagsLostNegationAndStrengthenedPromiseAsUnsafe()
    {
        var source = "王总，我目前不能保证8月20日完成，只能争取晚2天内交付。";
        var plan = new ProfessionalizationPlanner().Create(new ProfessionalizationRequest
        {
            Input = source,
            Mode = ApplicationMode.Polish
        });

        var report = ProfessionalQualityValidator.Validate(
            plan,
            "王总，我保证8月20日完成，并一定在2天内交付。");

        Assert.False(report.IsSafe);
        Assert.Contains(report.Issues, issue => issue.Code == "negation-lost");
        Assert.Contains(report.Issues, issue => issue.Code == "commitment-strengthened");
    }

    [Fact]
    public void QualityValidator_TreatsCannedClosingAsRepairableQualityIssue()
    {
        var plan = new ProfessionalizationPlanner().Create(new ProfessionalizationRequest
        {
            Input = "请把项目进度发给客户。",
            Mode = ApplicationMode.Polish
        });

        var report = ProfessionalQualityValidator.Validate(
            plan,
            "请向客户同步项目进度。希望以上内容对您有所帮助。");

        Assert.True(report.IsSafe);
        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, issue => issue.Code == "canned-expression");
    }

    [Fact]
    public void QualityValidator_BlocksLostConditionAndResponsibility()
    {
        var plan = new ProfessionalizationPlanner().Create(new ProfessionalizationRequest
        {
            Input = "如果客户确认方案，由王总负责8月20日交付。",
            Mode = ApplicationMode.Polish
        });
        var report = ProfessionalQualityValidator.Validate(plan, "王总将在8月20日交付。");
        Assert.False(report.IsSafe);
        Assert.Contains(report.Issues, issue => issue.Code == "condition-lost");
        Assert.Contains(report.Issues, issue => issue.Code == "responsibility-lost");
    }

    [Fact]
    public async Task PromptWorkflow_RepairsOnceWhenInitialOutputDropsFactAnchor()
    {
        var client = new SequenceClient(
            "请设计库存系统。",
            "请设计库存系统，必须支持3个仓库，并给出验收标准。输出完整实施方案。");
        var request = new PromptRequest
        {
            UserInput = "设计一个支持3个仓库的库存系统，要有验收标准",
            Category = PromptCategory.Coding,
            Depth = PromptDepth.Standard
        };
        var plan = new ProfessionalizationPlanner().Create(new ProfessionalizationRequest
        {
            Input = request.UserInput,
            Mode = ApplicationMode.PromptOptimize,
            Category = request.Category,
            Depth = request.Depth
        });

        var result = await new PromptOptimizationWorkflowService(client, new PromptBuilderService())
            .ExecuteAsync(request, plan);

        Assert.Equal(2, client.Calls);
        Assert.True(result.WasRepaired);
        Assert.False(result.IsBlocked);
        Assert.Contains("3个仓库", result.Content);
    }

    [Fact]
    public async Task PromptWorkflow_EmptyInitialAndRepairResponsesNeverReturnEmptySuccess()
    {
        var client = new SequenceClient("", "");
        var request = new PromptRequest { UserInput = "写一个项目说明", Category = PromptCategory.Writing, Depth = PromptDepth.Standard };
        var plan = new ProfessionalizationPlanner().Create(new ProfessionalizationRequest
        {
            Input = request.UserInput,
            Mode = ApplicationMode.PromptOptimize,
            Category = request.Category,
            Depth = request.Depth
        });

        var result = await new PromptOptimizationWorkflowService(client, new PromptBuilderService())
            .ExecuteAsync(request, plan);

        Assert.True(result.IsBlocked);
        Assert.True(string.IsNullOrWhiteSpace(result.Content));
    }

    [Fact]
    public async Task PromptWorkflow_ClientEmptyResponse_IsRetriedAndCanComplete()
    {
        var client = new ThrowOnceEmptyClient("结构化后的提示词");
        var request = new PromptRequest { UserInput = "写一个项目说明", Category = PromptCategory.Writing, Depth = PromptDepth.Standard };
        var plan = new ProfessionalizationPlanner().Create(new ProfessionalizationRequest
        {
            Input = request.UserInput,
            Mode = ApplicationMode.PromptOptimize,
            Category = request.Category,
            Depth = request.Depth
        });

        var result = await new PromptOptimizationWorkflowService(client, new PromptBuilderService())
            .ExecuteAsync(request, plan);

        Assert.False(result.IsBlocked);
        Assert.Equal("结构化后的提示词", result.Content);
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public void StructuredPreferenceLearning_DoesNotPersistGeneratedOrEditedText()
    {
        var profile = new ExpressionPreferenceProfile();
        var service = new StructuredPreferenceService();

        service.RecordEdit(profile,
            "首先感谢您的理解与支持。项目将在明天完成，后续我会及时同步。",
            "项目预计明天完成，我会同步进展。",
            "职场沟通");

        var json = JsonSerializer.Serialize(profile);
        Assert.DoesNotContain("项目将在明天完成", json);
        Assert.DoesNotContain("项目预计明天完成", json);
        Assert.True(profile.EditCount > 0);
        Assert.Contains("简洁", service.BuildInstructions(profile));
    }

    private sealed class SequenceClient(params string[] responses) : ITextGenerationClient
    {
        private int _index;
        public int Calls => _index;
        public Task<string> GenerateAsync(string systemPrompt, string userInput, CancellationToken cancellationToken = default)
        {
            var value = responses[Math.Min(_index, responses.Length - 1)];
            _index++;
            return Task.FromResult(value);
        }
    }

    private sealed class ThrowOnceEmptyClient(string successfulResponse) : ITextGenerationClient
    {
        public int Calls { get; private set; }

        public Task<string> GenerateAsync(string systemPrompt, string userInput, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Calls == 1) throw new InvalidOperationException("模型返回为空，请重试。");
            return Task.FromResult(successfulResponse);
        }
    }
}
