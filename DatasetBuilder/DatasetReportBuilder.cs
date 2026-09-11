namespace Huaxiazi.DatasetBuilder;

public sealed record DatasetDistributionReport(
    int Total,
    IReadOnlyDictionary<string, int> Splits,
    IReadOnlyDictionary<string, int> Modes,
    IReadOnlyDictionary<string, int> TaskTypes,
    IReadOnlyDictionary<string, int> Risks,
    IReadOnlyDictionary<string, int> Reviewers,
    int LeakageIssueCount);

public static class DatasetReportBuilder
{
    public static DatasetDistributionReport Build(IReadOnlyList<DatasetRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        static Dictionary<string, int> CountBy<T>(IEnumerable<DatasetRecord> source, Func<DatasetRecord, T> selector) =>
            source.GroupBy(selector).ToDictionary(group => Convert.ToString(group.Key) ?? "", group => group.Count(), StringComparer.Ordinal);
        return new(
            records.Count,
            CountBy(records, record => record.Split),
            CountBy(records, record => record.Mode),
            CountBy(records, record => record.TaskType),
            CountBy(records, record => record.RiskLevel),
            CountBy(records, record => record.Review.ReviewerCount),
            DatasetLeakageChecker.Check(records).Count);
    }
}
