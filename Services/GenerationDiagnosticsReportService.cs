using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

public sealed record GenerationRequestGroupReport(
    string Task,
    string Platform,
    string Protocol,
    string ProviderType,
    string Model,
    string? RuntimeBackend,
    int RuntimeStartupObservationCount,
    double? P50RuntimeStartupToReadyMilliseconds,
    double? P95RuntimeStartupToReadyMilliseconds,
    int RuntimeWorkingSetObservationCount,
    long? RuntimePeakWorkingSetBytes,
    int OutputRateObservationCount,
    double? P50OutputTokensPerSecond,
    double? P95OutputTokensPerSecond,
    int FirstRequestAfterRuntimeStartCount,
    int FirstRequestLatencyObservationCount,
    double? P50FirstRequestLatencyMilliseconds,
    double? P95FirstRequestLatencyMilliseconds,
    int SubsequentRuntimeRequestCount,
    int SubsequentRequestLatencyObservationCount,
    double? P50SubsequentRequestLatencyMilliseconds,
    double? P95SubsequentRequestLatencyMilliseconds,
    int UnclassifiedLocalRequestCount,
    DateTimeOffset FirstRequestUtc,
    DateTimeOffset LastRequestUtc,
    int RequestCount,
    int SuccessCount,
    double SuccessRate,
    IReadOnlyDictionary<string, int> OutcomeCounts,
    IReadOnlyDictionary<string, int> ContextPreflightOutcomeCounts,
    int LatencyObservationCount,
    double? P50LatencyMilliseconds,
    double? P95LatencyMilliseconds,
    int InputTokenObservationCount,
    int InputTokenUnknownCount,
    long? InputTokensTotal,
    int OutputTokenObservationCount,
    int OutputTokenUnknownCount,
    long? OutputTokensTotal,
    int CacheReadTokenObservationCount,
    int CacheReadTokenUnknownCount,
    long? CacheReadTokensTotal,
    int CacheCreationTokenObservationCount,
    int CacheCreationTokenUnknownCount,
    long? CacheCreationTokensTotal,
    int AttributedWorkflowCount,
    int QualityGateObservationCount,
    int QualityGatePassedCount,
    double? QualityGatePassRate);

public sealed record GenerationWorkflowGroupReport(
    string Task,
    DateTimeOffset FirstWorkflowUtc,
    DateTimeOffset LastWorkflowUtc,
    int WorkflowCount,
    IReadOnlyDictionary<string, int> OutcomeCounts,
    int OutputContractObservationCount,
    int OutputContractValidCount,
    double? OutputContractValidRate,
    int StructuredOutputObservationCount,
    int StructuredOutputValidCount,
    double? StructuredOutputValidRate,
    int QualityGateObservationCount,
    int QualityGatePassedCount,
    double? QualityGatePassRate,
    int RepairedWorkflowCount,
    int ValidationIssueCountTotal,
    double? AverageValidationIssueCount);

public sealed record GenerationDiagnosticsReport(
    DateTimeOffset GeneratedAtUtc,
    int SourceRequestCount,
    int SourceWorkflowCount,
    int UnattributedWorkflowCount,
    string PercentileMethod,
    string CostStatus,
    IReadOnlyList<GenerationRequestGroupReport> RequestGroups,
    IReadOnlyList<GenerationWorkflowGroupReport> WorkflowGroupsByTask,
    IReadOnlyList<string> MethodNotes,
    IReadOnlyDictionary<string, int> RoutingPolicyCounts,
    IReadOnlyDictionary<string, int> RouteReasonCounts,
    IReadOnlyDictionary<string, int> RouteAttemptCounts);

/// <summary>Builds a content-free local report from request-attempt and workflow-result records.</summary>
public sealed class GenerationDiagnosticsReportService
{
    private sealed record RequestGroupKey(string Task, string Platform, string Protocol, string ProviderType, string Model, string? RuntimeBackend);

    private sealed class RequestGroup(RequestGroupKey key)
    {
        public RequestGroupKey Key { get; } = key;
        public List<GenerationDiagnosticEntry> Requests { get; } = [];
        public List<GenerationWorkflowDiagnosticEntry> Workflows { get; } = [];
    }

    private sealed class WorkflowGroup(string task)
    {
        public string Task { get; } = task;
        public List<GenerationWorkflowDiagnosticEntry> Workflows { get; } = [];
    }

    private readonly GenerationDiagnosticsService _diagnostics;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public GenerationDiagnosticsReportService(GenerationDiagnosticsService diagnostics) =>
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));

    public GenerationDiagnosticsReport Build()
    {
        var requests = _diagnostics.ReadRecent();
        var workflows = _diagnostics.ReadWorkflowRecent();
        var requestGroups = new Dictionary<RequestGroupKey, RequestGroup>();
        var requestsByGeneration = new Dictionary<string, List<(RequestGroupKey Key, GenerationDiagnosticEntry Entry)>>(StringComparer.Ordinal);

        foreach (var request in requests)
        {
            var key = new RequestGroupKey(request.Task, request.Platform, request.Protocol, request.ProviderType, request.Model, request.RuntimeBackend);
            if (!requestGroups.TryGetValue(key, out var group)) requestGroups[key] = group = new RequestGroup(key);
            group.Requests.Add(request);
            if (string.IsNullOrWhiteSpace(request.GenerationId)) continue;
            if (!requestsByGeneration.TryGetValue(request.GenerationId, out var generationRequests))
                requestsByGeneration[request.GenerationId] = generationRequests = [];
            generationRequests.Add((key, request));
        }

        var taskGroups = new Dictionary<string, WorkflowGroup>(StringComparer.Ordinal);
        var unattributedWorkflowCount = 0;
        foreach (var workflow in workflows)
        {
            if (!taskGroups.TryGetValue(workflow.Task, out var taskGroup)) taskGroups[workflow.Task] = taskGroup = new WorkflowGroup(workflow.Task);
            taskGroup.Workflows.Add(workflow);

            if (string.IsNullOrWhiteSpace(workflow.GenerationId) ||
                !requestsByGeneration.TryGetValue(workflow.GenerationId, out var generationRequests) ||
                generationRequests.Count != workflow.RequestCount)
            {
                unattributedWorkflowCount++;
                continue;
            }

            var keys = generationRequests.Select(item => item.Key).Distinct().ToArray();
            if (keys.Length != 1 || !requestGroups.TryGetValue(keys[0], out var requestGroup))
            {
                unattributedWorkflowCount++;
                continue;
            }
            requestGroup.Workflows.Add(workflow);
        }

        var orderedRequestGroups = requestGroups.Values
            .OrderBy(group => group.Key.Task, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Platform, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Protocol, StringComparer.Ordinal)
            .ThenBy(group => group.Key.ProviderType, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Model, StringComparer.Ordinal)
            .ThenBy(group => group.Key.RuntimeBackend, StringComparer.Ordinal)
            .Select(BuildRequestGroup)
            .ToArray();
        var orderedTaskGroups = taskGroups.Values
            .OrderBy(group => group.Task, StringComparer.Ordinal)
            .Select(BuildWorkflowGroup)
            .ToArray();

        return new GenerationDiagnosticsReport(
            DateTimeOffset.UtcNow,
            requests.Count,
            workflows.Count,
            unattributedWorkflowCount,
            "nearest_rank",
            "not_calculated_no_rate_card",
            orderedRequestGroups,
            orderedTaskGroups,
            [
                "时延分位数按 Provider HTTP 尝试计算，重试单独计数；方法为 nearest-rank。",
                "任一对应观测的 Token 用量未知时，该组 Token 总量记为 null，不报部分合计。",
                "工作流质量只归属到同一 generationId 下唯一且记录数完整的 Provider/模型组；跨模型 fallback 保留在任务级质量统计并计为未归属。",
                "本地运行时后端来自实际启动端点标记 CPU/Vulkan；此报告不采集机器硬件标识。",
                "首个/后续本地请求依据运行时请求序号分类，不按时延阈值猜测；未知分类的 ManagedLocal 请求单独计数。",
                "本地输出速率按输出 Token 数除以 HTTP 请求时延估算，包含 loopback API 开销，不等于纯模型解码速度；启动时延只在每个受管运行时启动后的首个请求记录。",
                "本地峰值工作集为进程累计工作集，不代表显存；在不含硬件型号的情况下不得跨设备比较。",
                "费用未计算；当前没有绑定的费率卡或账单数据。",
                "路由策略、路由原因和主/备用尝试统计按 Provider 请求尝试计数；缺少路由元数据的旧记录不纳入这些计数。",
                "本地上下文预检状态只统计具有白名单状态的 ManagedLocal 请求；旧记录、非托管本地请求和云端请求不计入。"
            ],
            Count(requests.Where(request => request.RoutingMode is not null).Select(request => request.RoutingMode!)),
            Count(requests.Where(request => request.RouteReason is not null).Select(request => request.RouteReason!)),
            Count(requests.Where(request => request.RouteAttempt is not null).Select(request => request.RouteAttempt!)));
    }

    public string WriteSummary()
    {
        var report = Build();
        var path = _diagnostics.SummaryFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(report, _jsonOptions), new UTF8Encoding(false));
        File.Move(temporaryPath, path, overwrite: true);
        return path;
    }

    private static GenerationRequestGroupReport BuildRequestGroup(RequestGroup group)
    {
        var requests = group.Requests;
        var outcomes = Count(requests.Select(request => request.Outcome));
        var latencies = requests.Where(request => request.LatencyMilliseconds.HasValue)
            .Select(request => request.LatencyMilliseconds!.Value)
            .OrderBy(value => value)
            .ToArray();
        var input = SummarizeTokens(requests.Select(request => request.InputTokens));
        var output = SummarizeTokens(requests.Select(request => request.OutputTokens));
        var cacheRead = SummarizeTokens(requests.Select(request => request.CacheReadInputTokens));
        var cacheCreation = SummarizeTokens(requests.Select(request => request.CacheCreationInputTokens));
        var startupTimes = requests.Where(request => request.RuntimeStartupToReadyMilliseconds.HasValue)
            .Select(request => request.RuntimeStartupToReadyMilliseconds!.Value)
            .OrderBy(value => value)
            .ToArray();
        var workingSets = requests.Where(request => request.RuntimePeakWorkingSetBytes.HasValue)
            .Select(request => request.RuntimePeakWorkingSetBytes!.Value)
            .ToArray();
        var outputRates = group.Key.ProviderType == nameof(ProviderType.Local) && group.Key.RuntimeBackend is not null
            ? requests.Where(request => request.OutputTokens is > 0 && request.LatencyMilliseconds is > 0)
                .Select(request => request.OutputTokens!.Value * 1000d / request.LatencyMilliseconds!.Value)
                .OrderBy(value => value)
                .ToArray()
            : [];
        var isManagedLocal = group.Key.ProviderType == nameof(ProviderType.Local) && group.Key.RuntimeBackend is not null;
        var firstRequests = isManagedLocal ? requests.Where(request => request.IsFirstRequestAfterRuntimeStart == true).ToArray() : [];
        var subsequentRequests = isManagedLocal ? requests.Where(request => request.IsFirstRequestAfterRuntimeStart == false).ToArray() : [];
        var firstLatencies = firstRequests.Where(request => request.LatencyMilliseconds.HasValue)
            .Select(request => request.LatencyMilliseconds!.Value).OrderBy(value => value).ToArray();
        var subsequentLatencies = subsequentRequests.Where(request => request.LatencyMilliseconds.HasValue)
            .Select(request => request.LatencyMilliseconds!.Value).OrderBy(value => value).ToArray();
        var unclassifiedLocalRequests = isManagedLocal ? requests.Count(request => !request.IsFirstRequestAfterRuntimeStart.HasValue) : 0;
        var gateKnown = group.Workflows.Where(workflow => workflow.QualityGatePassed.HasValue).ToArray();
        var gatePassed = gateKnown.Count(workflow => workflow.QualityGatePassed == true);
        return new GenerationRequestGroupReport(
            group.Key.Task,
            group.Key.Platform,
            group.Key.Protocol,
            group.Key.ProviderType,
            group.Key.Model,
            group.Key.RuntimeBackend,
            startupTimes.Length,
            Percentile(startupTimes, .50),
            Percentile(startupTimes, .95),
            workingSets.Length,
            workingSets.Length == 0 ? null : workingSets.Max(),
            outputRates.Length,
            Percentile(outputRates, .50),
            Percentile(outputRates, .95),
            firstRequests.Length,
            firstLatencies.Length,
            Percentile(firstLatencies, .50),
            Percentile(firstLatencies, .95),
            subsequentRequests.Length,
            subsequentLatencies.Length,
            Percentile(subsequentLatencies, .50),
            Percentile(subsequentLatencies, .95),
            unclassifiedLocalRequests,
            requests.Min(request => request.TimestampUtc),
            requests.Max(request => request.TimestampUtc),
            requests.Count,
            requests.Count(request => request.Outcome == "success"),
            requests.Count == 0 ? 0 : requests.Count(request => request.Outcome == "success") / (double)requests.Count,
            outcomes,
            Count(requests.Where(request => group.Key.Platform == nameof(ProviderPlatform.ManagedLocal))
                .Select(request => request.ContextPreflightOutcome).Where(outcome => outcome is not null).Select(outcome => outcome!)),
            latencies.Length,
            Percentile(latencies, .50),
            Percentile(latencies, .95),
            input.KnownCount,
            input.UnknownCount,
            input.Total,
            output.KnownCount,
            output.UnknownCount,
            output.Total,
            cacheRead.KnownCount,
            cacheRead.UnknownCount,
            cacheRead.Total,
            cacheCreation.KnownCount,
            cacheCreation.UnknownCount,
            cacheCreation.Total,
            group.Workflows.Count,
            gateKnown.Length,
            gatePassed,
            Ratio(gatePassed, gateKnown.Length));
    }

    private static GenerationWorkflowGroupReport BuildWorkflowGroup(WorkflowGroup group)
    {
        var workflows = group.Workflows;
        var contractKnown = workflows.Where(workflow => workflow.OutputContractValid.HasValue).ToArray();
        var structuredKnown = workflows.Where(workflow => workflow.StructuredOutputValid.HasValue).ToArray();
        var gateKnown = workflows.Where(workflow => workflow.QualityGatePassed.HasValue).ToArray();
        var gatePassed = gateKnown.Count(workflow => workflow.QualityGatePassed == true);
        return new GenerationWorkflowGroupReport(
            group.Task,
            workflows.Min(workflow => workflow.TimestampUtc),
            workflows.Max(workflow => workflow.TimestampUtc),
            workflows.Count,
            Count(workflows.Select(workflow => workflow.Outcome)),
            contractKnown.Length,
            contractKnown.Count(workflow => workflow.OutputContractValid == true),
            Ratio(contractKnown.Count(workflow => workflow.OutputContractValid == true), contractKnown.Length),
            structuredKnown.Length,
            structuredKnown.Count(workflow => workflow.StructuredOutputValid == true),
            Ratio(structuredKnown.Count(workflow => workflow.StructuredOutputValid == true), structuredKnown.Length),
            gateKnown.Length,
            gatePassed,
            Ratio(gatePassed, gateKnown.Length),
            workflows.Count(workflow => workflow.WasRepaired),
            workflows.Sum(workflow => workflow.ValidationIssueCount),
            workflows.Count == 0 ? null : workflows.Average(workflow => workflow.ValidationIssueCount));
    }

    private static IReadOnlyDictionary<string, int> Count(IEnumerable<string> values) =>
        new SortedDictionary<string, int>(values.GroupBy(value => value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal), StringComparer.Ordinal);

    private static (int KnownCount, int UnknownCount, long? Total) SummarizeTokens(IEnumerable<int?> values)
    {
        var counts = values.ToArray();
        var knownCount = counts.Count(value => value.HasValue);
        var unknownCount = counts.Length - knownCount;
        long? total = counts.Length > 0 && unknownCount == 0 ? counts.Sum(value => (long)value!.Value) : null;
        return (knownCount, unknownCount, total);
    }

    private static double? Ratio(int numerator, int denominator) => denominator == 0 ? null : numerator / (double)denominator;

    private static double? Percentile(IReadOnlyList<double> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0) return null;
        var rank = Math.Max(1, (int)Math.Ceiling(percentile * sortedValues.Count));
        return sortedValues[rank - 1];
    }
}
