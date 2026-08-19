using System;
using System.IO;
using System.Reflection;
using System.Text;
using PromptFloat.Models;

namespace PromptFloat.Services;

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
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var systemPrompt = string.IsNullOrWhiteSpace(request.CustomSystemPrompt)
            ? ReadPromptFile("SystemPrompt.txt")
            : request.CustomSystemPrompt.Trim();
        if (string.IsNullOrWhiteSpace(systemPrompt))
        {
            // 极端兜底：文件缺失时给出最小可用提示。
            systemPrompt = "你是一个专业的 Prompt Engineer，请把用户的模糊需求重构为结构化提示词。";
        }

        var categoryText = $"{request.Category.GetDisplayName()}（优化重点：{request.Category.GetOptimizationFocus()}）";
        var depthText = $"{request.Depth.GetDisplayName()}（{request.Depth.GetStructureTemplate()}）";

        var result = systemPrompt
            .Replace("{{Category}}", categoryText, StringComparison.Ordinal)
            .Replace("{{Depth}}", depthText, StringComparison.Ordinal)
            .Replace("{{UserInput}}", "[用户输入通过 user message 单独提供]", StringComparison.Ordinal);

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

        // 可选：追加用户画像 / 角色设定（见 AppSettings.UserPersona）。
        // 仅当 Persona 非空时拼接，保证空画像时输出与旧行为完全一致（向后兼容）。
        if (!string.IsNullOrWhiteSpace(request.Persona))
        {
            result += $"\n\n---\n用户画像 / 角色设定（请在优化时贴合该用户的身份与偏好）：\n{request.Persona}\n";
        }

        if (!string.IsNullOrWhiteSpace(request.PreferenceInstructions))
        {
            result += $"\n\n---\n用户改写偏好（必须遵守）：\n{request.PreferenceInstructions.Trim()}\n";
        }

        var secured = new StringBuilder(result);
        PromptSecurityPolicy.AppendTrustBoundary(secured);
        result = secured.ToString();

        return result;
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
