using System.Text.Json;

namespace Huaxiazi.DatasetBuilder;

public sealed record ArchitectureBatchRequest(
    string RequestId,
    string ArchitectureId,
    string SampleId,
    string Input,
    IReadOnlyDictionary<string, string> Layers,
    IReadOnlyDictionary<string, double> Weights,
    string Scenario,
    string RiskLevel,
    string Split = "");

public static class ArchitectureExperimentBatchBuilder
{
    private static readonly string[] RequiredLayers = ["system", "developer", "skill", "harness", "output_contract"];
    public static IReadOnlyList<ArchitectureBatchRequest> Build(
        IEnumerable<PromptArchitectureRecord> records,
        string candidateConfigPath)
    {
        ArgumentNullException.ThrowIfNull(records);
        var sourceRecords = records.ToArray();
        if (sourceRecords.Any(record => string.Equals(record.Split, "test", StringComparison.Ordinal)))
            throw new ArgumentException("冻结 test split 不能用于候选架构搜索；请先在 train/dev 上完成选择。", nameof(records));
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        using var document = JsonDocument.Parse(File.ReadAllText(candidateConfigPath));
        var candidates = document.RootElement.GetProperty("candidates").EnumerateArray().ToArray();
        var result = new List<ArchitectureBatchRequest>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var id = candidate.GetProperty("id").GetString() ?? throw new JsonException("候选架构缺少 id。");
            if (string.IsNullOrWhiteSpace(id) || !ids.Add(id)) throw new ArgumentException($"候选架构 id 重复或为空：{id}");
            var layers = candidate.GetProperty("layers").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty, StringComparer.Ordinal);
            var weights = candidate.GetProperty("weights").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble(), StringComparer.Ordinal);
            if (!RequiredLayers.All(layers.ContainsKey) || layers.Any(item => string.IsNullOrWhiteSpace(item.Value)))
                throw new ArgumentException($"候选架构 {id} 必须提供五层非空措辞。");
            if (!RequiredLayers.All(weights.ContainsKey) || weights.Any(item => item.Value < 0 || !double.IsFinite(item.Value)) || Math.Abs(RequiredLayers.Sum(layer => weights[layer]) - 1d) > .0001)
                throw new ArgumentException($"候选架构 {id} 的五层权重必须为非负有限值且总和为 1。");
            foreach (var record in sourceRecords)
            {
                var requestId = $"{id}::{record.Id}";
                result.Add(new ArchitectureBatchRequest(requestId, id, record.Id, record.Input, layers, weights, record.Scenario, record.RiskLevel, record.Split));
            }
        }
        return result;
    }

    public static void Export(string outputPath, IEnumerable<ArchitectureBatchRequest> requests)
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.WriteAllLines(outputPath, requests.Select(item => JsonSerializer.Serialize(item, options)));
    }
}
