using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

/// <summary>
/// 提示词组装服务。
/// 读取 Prompts/SystemPrompt.txt（含 {{Category}} {{Depth}} {{UserInput}} 占位符），
/// 在调用 AIService 前把占位符替换为实际内容。
///
/// 设计取舍：
/// 单一 SystemPrompt 已能覆盖全部 6 个方向的差异化要求（因为 SystemPrompt 明确要求
/// "根据任务类别调整提示词结构" 并且注入了类别优化重点）。因此本服务的实现中，
/// CodingPrompt.txt / WritingPrompt.txt 等分类文件**作为可选的类别增强指令**——
/// 当对应文件存在时，将其内容拼接在 SystemPrompt 之后，以强化该类别的结构约束；
/// 文件缺失时则完全回退到 SystemPrompt 自身，逻辑自洽、不影响可用性。
/// 这样既能利用分类文件的细节，又不会因为文件丢失导致功能不可用。
/// </summary>
public sealed class PromptBuilderService
{
    private readonly string _promptsDirectory;

    public PromptBuilderService()
    {
        // 优先从程序运行目录的 Prompts 子目录读取（发布后随程序分发）。
        var baseDir = AppContext.BaseDirectory;
        _promptsDirectory = Path.Combine(baseDir, "Prompts");

        // 开发期（从源码 bin 之外运行时）回退到项目目录。
        if (!Directory.Exists(_promptsDirectory))
        {
            var candidate = Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? baseDir,
                "Prompts");
            if (Directory.Exists(candidate))
            {
                _promptsDirectory = candidate;
            }
        }
    }

    /// <summary>
    /// 组装最终发送给模型的 System Prompt。
    /// </summary>
    public string Build(PromptRequest request)
        => Build(request, null);

    public string Build(PromptRequest request, ProfessionalizationPlan? plan)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var result = BuildCorePrompt(request);

        // 可选：注入类别增强指令文件（如 CodingPrompt.txt），缺失则跳过。
        var categoryFile = GetCategoryFileName(request.Category);
        var categoryExtra = ReadPromptFile(categoryFile);
        if (!string.IsNullOrWhiteSpace(categoryExtra))
        {
            var sb = new StringBuilder();
            sb.AppendLine(result);
            sb.AppendLine();
            sb.AppendLine("---- 类别增强指令 ----");
            sb.AppendLine(categoryExtra);
            result = sb.ToString();
        }

        var personalized = new StringBuilder(result);
        PromptContextComposer.AppendPersonalization(personalized, request.Persona, request.PreferenceInstructions);
        result = personalized.ToString();

        if (plan is not null && !string.IsNullOrWhiteSpace(plan.StrategyInstructions))
        {
            result += $"\n\n---\n专业化执行计划（优先遵守事实保真与用户本次明确要求）：\n{plan.StrategyInstructions}\n";
            if (plan.SelectedSkillIds.Count > 0)
                result += $"选用 Skill：{string.Join("、", plan.SelectedSkillIds)}；权重：{string.Join("、", plan.SkillWeights.Select(item => item.Key + "=" + item.Value.ToString("0.000")))}。\n";
            if (!string.IsNullOrWhiteSpace(plan.RecommendedModelTier))
                result += $"建议模型档位：{plan.RecommendedModelTier}（{plan.ModelSelectionReason}）。\n";
        }

        var secured = new StringBuilder(result);
        PromptSecurityPolicy.AppendTrustBoundary(secured);
        result = PromptContextBudget.Enforce(secured.ToString());

        return result;
    }

    /// <summary>
    /// 生产工作流使用的分层上下文入口。保留 Build 的兼容输出，同时为新调用方提供
    /// XML 层边界、稳定前缀哈希和可观测的省略层列表。
    /// </summary>
    public ComposedPrompt BuildAgentContext(PromptRequest request, ProfessionalizationPlan? plan = null, int maxCharacters = 24_000)
    {
        ArgumentNullException.ThrowIfNull(request);
        var personalization = PromptContextComposer.AppendPersonalization(new StringBuilder(), request.Persona, request.PreferenceInstructions).ToString();
        var strategy = plan?.StrategyInstructions ?? string.Empty;
        if (plan is not null && plan.SelectedSkillIds.Count > 0)
            strategy += "\n选用 Skill：" + string.Join("、", plan.SelectedSkillIds) + "；权重：" + string.Join("、", plan.SkillWeights.Select(item => item.Key + "=" + item.Value.ToString("0.000")));
        var developer = "类别：" + request.Category.GetDisplayName() + "；深度：" + request.Depth.GetDisplayName();
        // A user-editable "custom system prompt" is not allowed to replace the
        // product system layer. Compile it as untrusted, lower-priority developer
        // guidance so safety rules and the protocol remain authoritative.
        var customGuidance = PersonalizationConstraintCompiler.Compile(null, request.CustomSystemPrompt).Preferences;
        if (!string.IsNullOrWhiteSpace(customGuidance))
            developer += "\n用户自定义表达指导（低于系统安全规则）：\n" + customGuidance;
        var categoryExtra = ReadPromptFile(GetCategoryFileName(request.Category));
        if (!string.IsNullOrWhiteSpace(categoryExtra)) developer += "\n类别增强指令（低于系统安全规则）：\n" + ExpressionSkillRouter.ProjectInstructions(categoryExtra, Array.Empty<string>());
        var systemLayer = new StringBuilder(BuildCorePrompt(new PromptRequest
        {
            UserInput = request.UserInput,
            Category = request.Category,
            Depth = request.Depth,
            // Never allow user text to replace the bundled system prompt in
            // the layered production entry point.
            CustomSystemPrompt = string.Empty
        }));
        PromptSecurityPolicy.AppendTrustBoundary(systemLayer);

        return AgentContextPipeline.Compose(new AgentContextInput
        {
            SystemPrompt = systemLayer.ToString(),
            DeveloperPrompt = developer,
            DeveloperStable = false,
            FidelityAnchors = plan?.FidelityAnchors ?? Array.Empty<string>(),
            SkillInstructions = strategy,
            Personalization = personalization,
            Task = "当前用户输入通过独立 user message 提供；只处理该输入，不从系统上下文臆造业务事实。"
        }, maxCharacters);
    }

    private string BuildCorePrompt(PromptRequest request)
    {
        var systemPrompt = string.IsNullOrWhiteSpace(request.CustomSystemPrompt) ? ReadPromptFile("SystemPrompt.txt") : request.CustomSystemPrompt.Trim();
        if (string.IsNullOrWhiteSpace(systemPrompt)) systemPrompt = "你是一个专业的 Prompt Engineer，请把用户的模糊需求重构为结构化提示词。";
        var categoryText = $"{request.Category.GetDisplayName()}（优化重点：{request.Category.GetOptimizationFocus()}）";
        var depthText = $"{request.Depth.GetDisplayName()}（{request.Depth.GetStructureTemplate()}）";
        return systemPrompt.Replace("{{Category}}", categoryText, StringComparison.Ordinal)
            .Replace("{{Depth}}", depthText, StringComparison.Ordinal)
            .Replace("{{UserInput}}", "[用户输入通过 user message 单独提供]", StringComparison.Ordinal);
    }

    public string BuildRepairPrompt(PromptRequest request, ProfessionalizationPlan plan, System.Collections.Generic.IReadOnlyList<QualityIssue> issues)
    {
        var prompt = Build(request, plan);
        var details = string.Join("\n", issues.Select(issue => "- " + issue.Message));
        return prompt + $"\n\n---\n上一份结果未通过本地质量门禁。只修复下列问题并重新输出完整最终提示词，不解释修改过程：\n{details}\n不得遗漏原文事实锚点，不得新增业务事实。";
    }

    public string BuildUserMessage(PromptRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.UserInput ?? string.Empty;
    }

    /// <summary>
    /// 读取 Prompts 目录下的某个 txt 文件，文件缺失返回空字符串。
    /// </summary>
    private string ReadPromptFile(string fileName)
    {
        try
        {
            var path = Path.Combine(_promptsDirectory, fileName);
            if (File.Exists(path))
            {
                return File.ReadAllText(path, Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // 忽略读取异常，回退到空。
        }
        return string.Empty;
    }

    /// <summary>
    /// 类别 -> 对应增强指令文件名。
    /// </summary>
    private static string GetCategoryFileName(PromptCategory category) => category switch
    {
        PromptCategory.General => "GeneralPrompt.txt",
        PromptCategory.Coding => "CodingPrompt.txt",
        PromptCategory.Writing => "WritingPrompt.txt",
        PromptCategory.Analysis => "AnalysisPrompt.txt",
        PromptCategory.Research => "ResearchPrompt.txt",
        PromptCategory.Creative => "CreativePrompt.txt",
        _ => "GeneralPrompt.txt"
    };
}
