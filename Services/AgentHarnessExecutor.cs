using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Huaxiazi.Services;

public enum AgentOrchestrationMode { Workflow, Autonomous }
public enum AgentToolSafety { ReadOnly, Mutating }

public sealed record AgentToolRequest(string ToolName, IReadOnlyDictionary<string, string> Arguments, AgentToolSafety Safety = AgentToolSafety.ReadOnly, int TimeoutMs = 15_000, string IdempotencyKey = "");
public sealed record AgentToolResult(string ToolName, bool Succeeded, string Content, string Error = "");
public sealed record AgentExecutionTrace(
    int Step,
    AgentOrchestrationMode Mode,
    IReadOnlyList<string> Tools,
    bool Stopped,
    string StopReason,
    IReadOnlyDictionary<string, int>? AttemptCounts = null,
    IReadOnlyDictionary<string, string>? Errors = null,
    string StablePrefixHash = "",
    IReadOnlyList<string>? OmittedContextLayers = null,
    int ContextCharacters = 0);

public interface IAgentTool
{
    string Name { get; }
    AgentToolSafety Safety { get; }
    Task<AgentToolResult> InvokeAsync(AgentToolRequest request, CancellationToken cancellationToken);
}

/// <summary>Harness boundary for deterministic workflows and bounded autonomous loops.</summary>
public sealed class AgentHarnessExecutor
{
    private readonly IReadOnlyDictionary<string, IAgentTool> _tools;
    private readonly IIdempotencyStore _idempotencyStore;

    public AgentHarnessExecutor(IEnumerable<IAgentTool> tools, TimeSpan? idempotencyTtl = null, IIdempotencyStore? idempotencyStore = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var registered = tools.ToArray();
        if (registered.Any(tool => tool is null || string.IsNullOrWhiteSpace(tool.Name)))
            throw new ArgumentException("工具名称不能为空。", nameof(tools));
        if (registered.GroupBy(tool => tool.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new ArgumentException("工具名称必须唯一，禁止同名工具覆盖。", nameof(tools));
        _tools = registered.ToDictionary(tool => tool.Name, tool => tool, StringComparer.Ordinal);
        _idempotencyStore = idempotencyStore ?? new InMemoryIdempotencyStore(idempotencyTtl);
    }

    public async Task<(IReadOnlyList<AgentToolResult> Results, AgentExecutionTrace Trace)> ExecuteAsync(
        IEnumerable<AgentToolRequest> requests, AgentOrchestrationMode mode, int maxSteps = 4, int maxReadRetries = 2, CancellationToken cancellationToken = default, ComposedPrompt? context = null, int maxToolCalls = 16)
    {
        var pending = requests?.ToArray() ?? throw new ArgumentNullException(nameof(requests));
        if (maxSteps is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(maxSteps));
        if (maxReadRetries is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(maxReadRetries));
        if (maxToolCalls is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(maxToolCalls));
        var results = new List<AgentToolResult>();
        var attempts = new Dictionary<string, int>(StringComparer.Ordinal);
        var stopped = false;
        var reason = "completed";
        var steps = 0;
        var calls = 0;
        for (var step = 1; step <= maxSteps && pending.Length > 0; step++)
        {
            steps = step;
            var remainingCalls = maxToolCalls - calls;
            if (remainingCalls <= 0) { stopped = true; reason = "tool_call_budget_exhausted"; break; }
            var batch = mode == AgentOrchestrationMode.Workflow ? pending[..1] : pending.Where(item => item.Safety == AgentToolSafety.ReadOnly).Take(remainingCalls).ToArray();
            if (batch.Length == 0) batch = pending[..1];
            if (batch.Length > remainingCalls) batch = batch[..remainingCalls];
            var invocations = batch.Select(request => InvokeWithRetryAsync(request, maxReadRetries, cancellationToken));
            var batchResults = await Task.WhenAll(invocations).ConfigureAwait(false);
            calls += batch.Length;
            foreach (var item in batchResults) attempts[item.Request.ToolName] = attempts.TryGetValue(item.Request.ToolName, out var count) ? count + item.Attempts : item.Attempts;
            results.AddRange(batchResults.Select(item => item.Result));
            pending = pending.Where(request => !batch.Contains(request)).ToArray();
            if (results.Any(item => !item.Succeeded)) { stopped = true; reason = "tool_failure"; break; }
        }
        if (pending.Length > 0 && !stopped)
        {
            stopped = true;
            reason = calls >= maxToolCalls ? "tool_call_budget_exhausted" : "step_budget_exhausted";
        }
        var errors = results.Where(item => !item.Succeeded).GroupBy(item => item.ToolName, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Last().Error, StringComparer.Ordinal);
        var trace = new AgentExecutionTrace(steps, mode, results.Select(item => item.ToolName).ToArray(), stopped, reason, attempts, errors,
            context?.StablePrefixHash ?? "", context?.OmittedLayers ?? Array.Empty<string>(), context?.UsedCharacters ?? 0);
        return (results, trace);
    }

    private async Task<(AgentToolRequest Request, AgentToolResult Result, int Attempts)> InvokeWithRetryAsync(AgentToolRequest request, int maxReadRetries, CancellationToken cancellationToken)
    {
        var attempts = request.Safety == AgentToolSafety.ReadOnly ? maxReadRetries + 1 : 1;
        AgentToolResult last = new(request.ToolName, false, string.Empty, "not_attempted");
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            last = await InvokeSafeAsync(request, cancellationToken).ConfigureAwait(false);
            if (last.Succeeded || last.Error is "idempotency_key_required" or "tool_safety_mismatch" or "unknown_tool" or "invalid_timeout") return (request, last, attempt + 1);
            if (attempt + 1 < attempts)
                await Task.Delay(TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt)), cancellationToken).ConfigureAwait(false);
        }
        return (request, last, attempts);
    }

    private async Task<AgentToolResult> InvokeSafeAsync(AgentToolRequest request, CancellationToken cancellationToken)
    {
        if (!_tools.TryGetValue(request.ToolName, out var tool)) return new(request.ToolName, false, string.Empty, "unknown_tool");
        if (tool.Safety != request.Safety) return new(request.ToolName, false, string.Empty, "tool_safety_mismatch");
        if (request.TimeoutMs is < 100 or > 120_000) return new(request.ToolName, false, string.Empty, "invalid_timeout");
        if (request.Safety == AgentToolSafety.Mutating && string.IsNullOrWhiteSpace(request.IdempotencyKey)) return new(request.ToolName, false, string.Empty, "idempotency_key_required");
        var mutationKey = request.Safety == AgentToolSafety.Mutating ? request.ToolName + ":" + request.IdempotencyKey : string.Empty;
        if (mutationKey.Length > 0 && _idempotencyStore.TryGet(mutationKey, out var cached)) return cached;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(request.TimeoutMs);
            var result = await tool.InvokeAsync(request, timeout.Token).ConfigureAwait(false);
            var clean = PromptInjectionSanitizer.RemoveUnsafeLines(result.Content);
            var final = result with { Content = clean };
            if (final.Succeeded && mutationKey.Length > 0) _idempotencyStore.Put(mutationKey, final);
            return final;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(request.ToolName, false, string.Empty, "timeout"); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { return new(request.ToolName, false, string.Empty, exception.GetType().Name); }
    }
}
