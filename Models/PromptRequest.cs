using PromptFloat.Models;

namespace PromptFloat.Models;

/// <summary>
/// 一次提示词优化请求的输入模型。
/// 聚合了用户输入、所选方向与深度，供 ViewModel / Services 传递使用。
/// </summary>
public sealed class PromptRequest
{
    /// <summary>用户原始需求文本。</summary>
    public string UserInput { get; init; } = string.Empty;

    /// <summary>所选任务类别（方向）。</summary>
    public PromptCategory Category { get; init; } = PromptCategory.General;

    /// <summary>所选优化深度。</summary>
    public PromptDepth Depth { get; init; } = PromptDepth.Standard;

    /// <summary>
    /// 用户画像 / 角色设定（可选）。非空时由 PromptBuilderService 追加到 System Prompt，
    /// 让优化结果贴合用户身份与偏好。
    /// </summary>
    public string Persona { get; init; } = string.Empty;
    public string CustomSystemPrompt { get; init; } = string.Empty;
    public string PreferenceInstructions { get; init; } = string.Empty;
    public ProfessionalizationPlan? Professionalization { get; init; }

    /// <summary>
    /// 基于当前输入构造一个请求。提供默认值以保证非空。
    /// </summary>
    /// <param name="userInput">用户原始需求。</param>
    /// <param name="category">所选任务类别（方向）。</param>
    /// <param name="depth">所选优化深度。</param>
    /// <param name="persona">用户画像 / 角色设定（可选，默认空）。</param>
    public static PromptRequest Create(string userInput, PromptCategory category, PromptDepth depth, string persona = "")
    {
        return new PromptRequest
        {
            UserInput = userInput ?? string.Empty,
            Category = category,
            Depth = depth,
            Persona = persona ?? string.Empty
        };
    }
}
