using System;
using System.Linq;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

internal static class MissingInformationSummary
{
    public static string For(ProfessionalizationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return plan.Purpose switch
        {
            "说明延期" => "延期原因",
            "问题分析" => "具体问题或异常表现",
            _ when plan.ClarificationQuestions.Any(question => question.Contains("回复谁", StringComparison.Ordinal)) => "回复对象",
            _ when plan.ClarificationQuestions.Any(question => question.Contains("对方说了什么", StringComparison.Ordinal)) => "对方内容或要表达的关键事实",
            _ when plan.ClarificationQuestions.Any(question => question.Contains("具体任务", StringComparison.Ordinal)) => "具体任务",
            _ when plan.ClarificationQuestions.Any(question => question.Contains("输入、限制或交付格式", StringComparison.Ordinal)) => "输入、限制或交付格式",
            _ when plan.ClarificationQuestions.Any(question => question.Contains("原始内容", StringComparison.Ordinal)) => "待处理原文",
            _ => "完成当前任务所需的关键信息"
        };
    }
}
