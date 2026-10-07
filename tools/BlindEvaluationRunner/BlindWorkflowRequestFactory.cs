using System.Text.Json;
using Huaxiazi.DatasetBuilder;
using Huaxiazi.Models;
using Huaxiazi.Services;

namespace Huaxiazi.BlindEvaluationRunner;

/// <summary>
/// A blind-evaluation input normalized into the same typed request objects that
/// the product workflows consume. Gold answers and reviewer data are never copied.
/// </summary>
public sealed record BlindWorkflowRequest(
    string RecordId,
    string Task,
    string Split,
    ProfessionalizationPlan Plan,
    CompanionDriverMode CompanionMode,
    PolishRequest? PolishRequest,
    PromptRequest? PromptRequest,
    bool ClarificationEnabled);

public static class BlindWorkflowRequestFactory
{
    private static readonly ProfessionalizationPlanner Planner = new();

    public static BlindWorkflowRequest Create(
        BlindEvaluationRecord record,
        IReadOnlyList<PolishConversationMessage>? conversationHistory = null,
        string? diagnosticPreferenceInstructions = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (conversationHistory is { Count: > 0 } && record.Task != "polish")
            throw new ArgumentException("当前顺序对话回放仅支持 polish 任务。", nameof(conversationHistory));
        if (string.IsNullOrWhiteSpace(record.Id)) throw new ArgumentException("评测记录缺少 id。", nameof(record));
        if (string.IsNullOrWhiteSpace(record.Input)) throw new ArgumentException("评测记录缺少 input。", nameof(record));
        if (record.Task is not ("polish" or "prompt_optimize")) throw new ArgumentException("评测 task 无效。", nameof(record));

        IReadOnlyDictionary<string, JsonElement> context = record.Context ?? [];
        var constraints = string.Join("\n", record.Constraints ?? []);
        var preferences = ReadString(context, "preference_instructions");
        if (!string.IsNullOrWhiteSpace(diagnosticPreferenceInstructions))
        {
            if (!string.IsNullOrWhiteSpace(preferences))
                throw new ArgumentException("偏好对照诊断要求开发样本不含既有 preference_instructions，以保证只改变一个实验变量。", nameof(record));
            preferences = diagnosticPreferenceInstructions.Trim();
        }
        var categoryDepth = ReadPromptShape(context);
        var plan = Planner.Create(new ProfessionalizationRequest
        {
            Input = record.Input,
            Mode = record.Task == "polish" ? ApplicationMode.Polish : ApplicationMode.PromptOptimize,
            Category = categoryDepth.Category,
            Depth = categoryDepth.Depth,
            Recipient = ReadString(context, "recipient"),
            Scenario = ReadString(context, "scenario"),
            Purpose = ReadString(context, "purpose"),
            Formality = ReadString(context, "formality"),
            ExplicitRequirements = constraints,
            PreferenceInstructions = preferences
        });

        var companionMode = ReadCompanionMode(context);
        var clarificationEnabled = ReadBoolean(context, "clarification_enabled", defaultValue: true);
        if (record.Task == "polish")
        {
            var polish = new PolishRequest
            {
                OriginalText = record.Input,
                Recipient = ReadString(context, "recipient"),
                Channel = ReadString(context, "channel"),
                Purpose = ReadString(context, "purpose"),
                Formality = ReadString(context, "formality"),
                Scenario = ReadString(context, "scenario"),
                OutputStyle = ReadString(context, "output_style", "自然"),
                CustomStyleInstructions = ReadString(context, "custom_style_instructions"),
                Persona = ReadString(context, "persona"),
                CustomSystemPrompt = ReadString(context, "custom_system_prompt"),
                PreferenceInstructions = preferences,
                ConversationHistory = conversationHistory ?? [],
                Professionalization = plan
            };
            return new(record.Id, record.Task, record.Split, plan, companionMode, polish, null, clarificationEnabled);
        }

        var prompt = new PromptRequest
        {
            UserInput = record.Input,
            Category = categoryDepth.Category,
            Depth = categoryDepth.Depth,
            Persona = ReadString(context, "persona"),
            CustomSystemPrompt = ReadString(context, "custom_system_prompt"),
            PreferenceInstructions = preferences,
            Professionalization = plan
        };
        return new(record.Id, record.Task, record.Split, plan, companionMode, null, prompt, clarificationEnabled);
    }

    private static CompanionDriverMode ReadCompanionMode(IReadOnlyDictionary<string, JsonElement> context)
    {
        var value = ReadString(context, "companion_driver_mode", nameof(CompanionDriverMode.Local));
        if (Enum.TryParse<CompanionDriverMode>(value, ignoreCase: true, out var mode)) return mode;
        throw new ArgumentException("context.companion_driver_mode 必须是受支持的 CompanionDriverMode。", nameof(context));
    }

    private static (PromptCategory Category, PromptDepth Depth) ReadPromptShape(IReadOnlyDictionary<string, JsonElement> context)
    {
        var categoryText = ReadString(context, "category", "General");
        if (!Enum.TryParse<PromptCategory>(categoryText, ignoreCase: true, out var category))
        {
            category = categoryText switch
            {
                "通用任务" => PromptCategory.General,
                "编程开发" => PromptCategory.Coding,
                "文案写作" => PromptCategory.Writing,
                "数据分析" => PromptCategory.Analysis,
                "学术研究" => PromptCategory.Research,
                "创意设计" => PromptCategory.Creative,
                _ => throw new ArgumentException("context.category 必须是受支持的 PromptCategory。", nameof(context))
            };
        }

        var depthText = ReadString(context, "depth", "Standard");
        if (!Enum.TryParse<PromptDepth>(depthText, ignoreCase: true, out var depth))
        {
            depth = depthText switch
            {
                "简洁" => PromptDepth.Concise,
                "标准" => PromptDepth.Standard,
                "详细" => PromptDepth.Detailed,
                _ => throw new ArgumentException("context.depth 必须是受支持的 PromptDepth。", nameof(context))
            };
        }
        return (category, depth);
    }

    private static string ReadString(IReadOnlyDictionary<string, JsonElement> context, string name, string defaultValue = "")
    {
        if (!context.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null) return defaultValue;
        if (value.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"context.{name} 必须是字符串。", nameof(context));
        return value.GetString() ?? defaultValue;
    }

    private static bool ReadBoolean(IReadOnlyDictionary<string, JsonElement> context, string name, bool defaultValue)
    {
        if (!context.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null) return defaultValue;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
        throw new ArgumentException($"context.{name} 必须是布尔值。", nameof(context));
    }
}
