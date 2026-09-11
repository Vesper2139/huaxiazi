using System.Text.RegularExpressions;

namespace Huaxiazi.DatasetBuilder;

public static class DatasetValidator
{
    private static readonly Regex Phone = new(@"(?<!\d)1\d{10}(?!\d)", RegexOptions.Compiled);
    private static readonly Regex Email = new(@"\b[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}\b", RegexOptions.Compiled);
    private static readonly Regex Secret = new(@"(?i)(sk-[a-z0-9]{12,}|api[_ -]?key\s*[:=])", RegexOptions.Compiled);

    public static IReadOnlyList<DatasetValidationIssue> Validate(IEnumerable<DatasetRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var issues = new List<DatasetValidationIssue>();
        foreach (var record in records)
        {
            if (string.IsNullOrWhiteSpace(record.Id)) Add("required", record, "缺少 id", issues);
            if (record.SchemaVersion != "1.0") Add("schema-version", record, "不支持的 schema 版本", issues);
            if (record.Split is not ("train" or "dev" or "test")) Add("split", record, "split 无效", issues);
            if (record.Mode is not ("polish" or "prompt_optimize")) Add("mode", record, "mode 无效", issues);
            if (string.IsNullOrWhiteSpace(record.Input) || string.IsNullOrWhiteSpace(record.GoldOutput)) Add("required", record, "输入或 Gold 为空", issues);
            if (record.Review.ReviewStatus != "accepted" || record.Review.ReviewerCount < 1) Add("review-incomplete", record, "样本尚未完成审核", issues);
            if ((record.Split is "dev" or "test" || record.RiskLevel == "high") && record.Review.ReviewerCount < 2)
                Add("review-depth", record, "dev/test 或高风险样本必须双人复核", issues);
            var allText = record.Input + "\n" + record.GoldOutput + "\n" + string.Join('\n', record.RejectedOutputs);
            if (Phone.IsMatch(allText) || Email.IsMatch(allText) || Secret.IsMatch(allText)) Add("pii", record, "检测到手机号、邮箱或疑似密钥", issues);
            if (record.ShouldClarify != (record.TaskType == "clarify")) Add("clarification", record, "澄清标签与任务类型不一致", issues);
            if (record.ShouldClarify && record.ClarificationQuestions.Count is < 1 or > 3) Add("clarification", record, "澄清问题必须为1至3个", issues);
        }
        return issues;
    }

    private static void Add(string code, DatasetRecord record, string message, List<DatasetValidationIssue> issues) =>
        issues.Add(new DatasetValidationIssue(code, record.Id, message));
}
