using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

/// <summary>Conservative local-first inference; no extra model call or trainable parameters.</summary>
public sealed class SmartContextAnalyzer
{
    private static readonly Regex AnchorPattern = new(
        @"(?<!\d)(?:\d{4}[-/.年]\d{1,2}(?:[-/.月]\d{1,2}日?)?|\d{1,2}月\d{1,2}日|\d{1,2}[:：]\d{2}|[￥¥$]\s?\d+(?:\.\d+)?|\d+(?:\.\d+)?(?:%|％|天|小时|分钟|元|万元|万|次|个|人|份|页))(?!\d)",
        RegexOptions.Compiled);
    private static readonly string[] UncertaintyTerms = ["可能", "大概", "预计", "或许", "尽量", "争取", "暂定", "不一定"];

    public TextIntelligence Analyze(string? text, SourceApplicationContext? sourceApplication = null)
    {
        var source = text?.Trim() ?? string.Empty;
        var signals = new List<string>();
        var scenario = string.Empty;
        var channel = string.Empty;
        var purpose = string.Empty;
        var formality = string.Empty;

        if (ContainsAny(source, "朋友圈")) { scenario = "公开发布"; channel = "朋友圈"; signals.Add("明确提及朋友圈"); }
        else if (ContainsAny(source, "公众号")) { scenario = "公开发布"; channel = "公众号"; signals.Add("明确提及公众号"); }
        else if (ContainsAny(source, "小红书")) { scenario = "公开发布"; channel = "小红书"; signals.Add("明确提及小红书"); }
        else if (ContainsAny(source, "微博", "公开发布", "公告")) { scenario = "公开发布"; signals.Add("公开发布语境"); }

        if (string.IsNullOrEmpty(channel) && ContainsAny(source, "邮件", "主题：", "收件人：")) { channel = "邮件"; signals.Add("邮件语境"); }
        else if (string.IsNullOrEmpty(channel) && ContainsAny(source, "微信", "群里")) { channel = "微信"; signals.Add("即时通讯语境"); }

        if (string.IsNullOrEmpty(channel) && !string.IsNullOrWhiteSpace(sourceApplication?.Channel))
        {
            channel = sourceApplication.Channel;
            signals.Add(sourceApplication.Evidence);
        }

        if (string.IsNullOrEmpty(scenario) && ContainsAny(source, "王总", "领导", "老板", "客户", "同事", "项目", "需求", "交付", "汇报", "工作", "会议"))
        { scenario = "职场沟通"; formality = "专业、克制"; signals.Add("职场关系或项目语境"); }
        else if (string.IsNullOrEmpty(scenario) && ContainsAny(source, "朋友", "家人", "爸妈", "老同学", "爱人"))
        { scenario = "私人沟通"; formality = "自然、真诚"; signals.Add("私人关系语境"); }

        if (string.IsNullOrEmpty(scenario) && !string.IsNullOrWhiteSpace(sourceApplication?.Scenario))
        {
            scenario = sourceApplication.Scenario;
            if (scenario == "职场沟通") formality = "专业、克制";
            if (!signals.Contains(sourceApplication.Evidence)) signals.Add(sourceApplication.Evidence);
        }

        if (ContainsAny(source, "延期", "晚", "推迟", "顺延", "不能按时", "无法按时")) purpose = "说明延期";
        else if (ContainsAny(source, "抱歉", "对不起", "道歉")) purpose = "致歉";
        else if (ContainsAny(source, "拒绝", "不方便", "无法参加")) purpose = "婉拒";
        else if (ContainsAny(source, "申请", "麻烦", "能否", "希望你", "请你")) purpose = "提出请求";
        else if (ContainsAny(source, "感谢", "谢谢")) purpose = "致谢";
        else if (ContainsAny(source, "汇报", "进展", "同步")) purpose = "进度汇报";

        var anchors = AnchorPattern.Matches(source).Select(match => match.Value).Distinct().ToArray();
        var uncertain = ContainsAny(source, UncertaintyTerms);
        var highRisk = anchors.Length > 0 && (ContainsAny(source, "交付", "完成", "延期", "付款", "报价", "承诺") || uncertain);
        var mediumRisk = anchors.Length > 0 || scenario is "公开发布" or "正式材料" || ContainsAny(source, "客户", "领导", "老板", "道歉", "拒绝");

        return new TextIntelligence
        {
            Channel = channel, Purpose = purpose, Formality = formality, Scenario = scenario,
            Confidence = signals.Count switch { >= 2 => 0.9, 1 => 0.75, _ => 0.35 },
            RiskLevel = highRisk ? TextRiskLevel.High : mediumRisk ? TextRiskLevel.Medium : TextRiskLevel.Low,
            ContainsUncertainty = uncertain, FidelityAnchors = anchors, Signals = signals
        };
    }

    public static ResolvedPolishContext Merge(ParsedInputContext explicitContext, TextIntelligence inferred, string? configuredScenario)
    {
        ArgumentNullException.ThrowIfNull(explicitContext);
        ArgumentNullException.ThrowIfNull(inferred);
        return new ResolvedPolishContext(
            First(explicitContext.Recipient, inferred.Recipient), First(explicitContext.Channel, inferred.Channel),
            First(explicitContext.Purpose, inferred.Purpose), First(explicitContext.Formality, inferred.Formality),
            First(explicitContext.Scenario, inferred.Scenario, string.Equals(configuredScenario, "其他", StringComparison.Ordinal) ? "" : configuredScenario));
    }

    private static string First(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
    private static bool ContainsAny(string source, params string[] values) => values.Any(value => source.Contains(value, StringComparison.OrdinalIgnoreCase));
}
