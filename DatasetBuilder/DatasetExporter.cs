using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Huaxiazi.DatasetBuilder;

public static class DatasetRecordFingerprint
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General);

    public static string Compute(DatasetRecord record)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, Options));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public static string ComputeDataset(IEnumerable<DatasetRecord> records)
    {
        var joined = string.Join("\n", records.Select(Compute));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined))).ToLowerInvariant();
    }
}

public static class DatasetExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static DatasetExportResult Export(string directory, IReadOnlyList<DatasetRecord> records)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(records);
        Directory.CreateDirectory(directory);
        var canonical = Path.Combine(directory, "canonical.jsonl");
        var sft = Path.Combine(directory, "sft.jsonl");
        var preference = Path.Combine(directory, "preference.jsonl");
        WriteLines(canonical, records.Select(record => JsonSerializer.Serialize(record, JsonOptions)));
        WriteLines(sft, records.Select(ToSft));
        WriteLines(preference, records.Where(record => record.RejectedOutputs.Count > 0).Select(ToPreference));
        foreach (var split in new[] { "train", "dev", "test" })
        {
            var splitRecords = records.Where(record => record.Split == split).ToArray();
            WriteLines(Path.Combine(directory, $"canonical_{split}.jsonl"), splitRecords.Select(record => JsonSerializer.Serialize(record, JsonOptions)));
            WriteLines(Path.Combine(directory, $"sft_{split}.jsonl"), splitRecords.Select(ToSft));
            WriteLines(Path.Combine(directory, $"preference_{split}.jsonl"), splitRecords.Where(record => record.RejectedOutputs.Count > 0).Select(ToPreference));
        }
        return new DatasetExportResult(canonical, sft, preference);
    }

    private static string ToSft(DatasetRecord record) => JsonSerializer.Serialize(new
    {
        id = record.Id,
        messages = new[]
        {
            new { role = "system", content = record.Mode == "polish" ? "你是话匣子表达助手。" : "你是专业 Prompt Engineer。" },
            new { role = "user", content = record.Input },
            new { role = "assistant", content = record.GoldOutput }
        }
    }, JsonOptions);

    private static string ToPreference(DatasetRecord record) => JsonSerializer.Serialize(new
    {
        id = record.Id,
        prompt = record.Input,
        chosen = record.GoldOutput,
        rejected = record.RejectedOutputs[0],
        reason = record.TaskType == "safety" ? "违反安全边界" : "事实或输出质量不达标"
    }, JsonOptions);

    private static void WriteLines(string path, IEnumerable<string> lines) =>
        File.WriteAllLines(path, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
}
