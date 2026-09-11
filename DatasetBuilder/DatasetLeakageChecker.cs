using System.Security.Cryptography;
using System.Text;

namespace Huaxiazi.DatasetBuilder;

public sealed record DatasetLeakageIssue(string Code, string RecordId, string OtherRecordId, string Message);

public static class DatasetLeakageChecker
{
    public static IReadOnlyList<DatasetLeakageIssue> Check(IEnumerable<DatasetRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var issues = new List<DatasetLeakageIssue>();
        var seen = new Dictionary<string, DatasetRecord>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(record.Input.Trim())));
            if (seen.TryGetValue(key, out var prior) && !string.Equals(prior.Split, record.Split, StringComparison.Ordinal))
                issues.Add(new("cross-split-input", record.Id, prior.Id, $"输入与 {prior.Id} 跨 split 重复。"));
            else if (!seen.ContainsKey(key)) seen[key] = record;
        }
        return issues;
    }
}
