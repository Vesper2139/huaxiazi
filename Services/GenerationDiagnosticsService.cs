using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

/// <summary>One locally retained, content-free generation request observation.</summary>
public sealed record GenerationDiagnosticEntry(
    DateTimeOffset TimestampUtc,
    string Task,
    string Platform,
    string Protocol,
    string ProviderType,
    string Model,
    double? LatencyMilliseconds,
    int? InputTokens,
    int? OutputTokens,
    int? HttpStatusCode,
    string Outcome,
    string? RequestId,
    int? CacheReadInputTokens,
    int? CacheCreationInputTokens,
    string GenerationId = "",
    string? RuntimeBackend = null,
    double? RuntimeStartupToReadyMilliseconds = null,
    long? RuntimePeakWorkingSetBytes = null,
    bool? IsFirstRequestAfterRuntimeStart = null,
    string? RoutingMode = null,
    string? RouteReason = null,
    string? RouteAttempt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContextPreflightOutcome = null);

public sealed record GenerationQualityObservation(
    string Outcome,
    bool? OutputContractValid,
    bool? StructuredOutputValid,
    bool? QualityGatePassed,
    bool WasRepaired,
    int ValidationIssueCount);

public sealed record GenerationWorkflowDiagnosticEntry(
    DateTimeOffset TimestampUtc,
    string GenerationId,
    string Task,
    string Outcome,
    bool? OutputContractValid,
    bool? StructuredOutputValid,
    bool? QualityGatePassed,
    bool WasRepaired,
    int ValidationIssueCount,
    int RequestCount);

/// <summary>
/// Stores opt-in local request metadata only. It has no API for prompts, generated text,
/// endpoint URLs, profile names, or credentials, and never sends data outside this device.
/// </summary>
public sealed class GenerationDiagnosticsService
{
    private static readonly HashSet<string> AllowedOutcomes = new(StringComparer.Ordinal)
    {
        "success", "refused", "incomplete", "context_limit_exceeded", "provider_error", "invalid_response",
        "structured_output_unsupported", "authentication", "account_prerequisite",
        "permission_denied", "billing_issue", "request_too_large", "rate_limited",
        "timeout", "network_error", "cancelled", "error", "unknown"
    };

    private static readonly HashSet<string> AllowedWorkflowOutcomes = new(StringComparer.Ordinal)
    {
        "final", "needs_clarification", "invalid", "blocked", "failed", "cancelled", "not_generated", "unknown"
    };

    private static readonly HashSet<string> AllowedRouteReasons = new(StringComparer.Ordinal)
    {
        "manual-active-profile", "task-profile", "local-only-task-profile",
        "local-only-selected-local-profile", "prefer-local", "prefer-cloud",
        "configured-fallback-no-preferred-profile", "automatic-fast",
        "automatic-balanced", "automatic-reasoning"
    };

    private static readonly HashSet<string> AllowedContextPreflightOutcomes = new(StringComparer.Ordinal)
    {
        "counted", "unavailable", "exceeded", "cancelled"
    };

    private static readonly Regex SafeRequestId = new("^[A-Za-z0-9._:-]{1,128}$", RegexOptions.CultureInvariant);
    private readonly object _gate = new();
    private readonly int _maxEntries;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public GenerationDiagnosticsService(string directory, int maxEntries = 500)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (maxEntries < 1) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        FilePath = Path.Combine(Path.GetFullPath(directory), "generation-diagnostics.jsonl");
        WorkflowFilePath = Path.Combine(Path.GetDirectoryName(FilePath)!, "generation-workflow-diagnostics.jsonl");
        SummaryFilePath = Path.Combine(Path.GetDirectoryName(FilePath)!, "generation-diagnostics-summary.json");
        _maxEntries = maxEntries;
    }

    public string FilePath { get; }
    public string WorkflowFilePath { get; }
    public string SummaryFilePath { get; }

    public void Record(ProviderProfile profile, ApplicationMode task, ProviderRequestTelemetry telemetry)
        => Record(Guid.NewGuid().ToString("N"), profile, task, telemetry);

    public void Record(
        string generationId,
        ProviderProfile profile,
        ApplicationMode task,
        ProviderRequestTelemetry telemetry,
        ProviderRoutingMode? routingMode = null,
        string? routeReason = null,
        string? routeAttempt = null)
    {
        ValidateGenerationId(generationId);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(telemetry);
        var runtimeBackend = profile.Type == ProviderType.Local && profile.Platform == ProviderPlatform.ManagedLocal
            ? NormalizeRuntimeBackend(telemetry.RuntimeBackend)
            : null;
        var isManagedLocal = profile.Type == ProviderType.Local && profile.Platform == ProviderPlatform.ManagedLocal;
        var entry = new GenerationDiagnosticEntry(
            DateTimeOffset.UtcNow,
            NormalizeTask(task),
            profile.Platform.ToString(),
            profile.Protocol.ToString(),
            profile.Type.ToString(),
            NormalizeIdentifier(ProviderCapabilityResolver.ResolveModelId(profile), 160),
            double.IsFinite(telemetry.LatencyMilliseconds) && telemetry.LatencyMilliseconds >= 0
                ? Math.Round(Math.Min(telemetry.LatencyMilliseconds, 3_600_000), 1)
                : null,
            NormalizeCount(telemetry.InputTokens),
            NormalizeCount(telemetry.OutputTokens),
            telemetry.HttpStatusCode is >= 100 and <= 599 ? telemetry.HttpStatusCode : null,
            AllowedOutcomes.Contains(telemetry.Outcome) ? telemetry.Outcome : "unknown",
            NormalizeRequestId(telemetry.RequestId),
            NormalizeCount(telemetry.CacheReadInputTokens),
            NormalizeCount(telemetry.CacheCreationInputTokens),
            generationId,
            runtimeBackend,
            runtimeBackend is null ? null : NormalizeMilliseconds(telemetry.RuntimeStartupToReadyMilliseconds),
            runtimeBackend is null ? null : NormalizeBytes(telemetry.RuntimePeakWorkingSetBytes),
            runtimeBackend is null ? null : telemetry.IsFirstRequestAfterRuntimeStart,
            routingMode is { } mode && Enum.IsDefined(mode) ? mode.ToString() : null,
            routeReason is not null && AllowedRouteReasons.Contains(routeReason) ? routeReason : null,
            routeAttempt is "primary" or "fallback" ? routeAttempt : null,
            isManagedLocal && telemetry.ContextPreflightOutcome is { } contextOutcome &&
                AllowedContextPreflightOutcomes.Contains(contextOutcome) ? contextOutcome : null);

        lock (_gate)
        {
            var entries = ReadRecentCore();
            if (entries.Count < _maxEntries)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, JsonSerializer.Serialize(entry, _jsonOptions) + Environment.NewLine, new UTF8Encoding(false));
                return;
            }

            entries.Add(entry);
            // Compact in batches to avoid rewriting the complete file on every request
            // once it reaches its retention limit.
            var compactedCount = Math.Max(1, _maxEntries - Math.Min(50, Math.Max(1, _maxEntries / 10)));
            entries.RemoveRange(0, entries.Count - compactedCount);
            WriteEntries(entries);
        }
    }

    public IReadOnlyList<GenerationDiagnosticEntry> ReadRecent()
    {
        lock (_gate) return ReadRecentCore();
    }

    public void RecordWorkflow(string generationId, ApplicationMode task, GenerationQualityObservation observation, int requestCount)
    {
        ValidateGenerationId(generationId);
        ArgumentNullException.ThrowIfNull(observation);
        var entry = new GenerationWorkflowDiagnosticEntry(
            DateTimeOffset.UtcNow,
            generationId,
            NormalizeTask(task),
            AllowedWorkflowOutcomes.Contains(observation.Outcome) ? observation.Outcome : "unknown",
            observation.OutputContractValid,
            observation.StructuredOutputValid,
            observation.QualityGatePassed,
            observation.WasRepaired,
            Math.Max(0, observation.ValidationIssueCount),
            Math.Max(0, requestCount));
        lock (_gate)
        {
            var entries = ReadWorkflowRecentCore();
            if (entries.Count < _maxEntries)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(WorkflowFilePath)!);
                File.AppendAllText(WorkflowFilePath, JsonSerializer.Serialize(entry, _jsonOptions) + Environment.NewLine, new UTF8Encoding(false));
                return;
            }
            entries.Add(entry);
            var compactedCount = Math.Max(1, _maxEntries - Math.Min(50, Math.Max(1, _maxEntries / 10)));
            entries.RemoveRange(0, entries.Count - compactedCount);
            WriteWorkflowEntries(entries);
        }
    }

    public IReadOnlyList<GenerationWorkflowDiagnosticEntry> ReadWorkflowRecent()
    {
        lock (_gate) return ReadWorkflowRecentCore();
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
            if (File.Exists(WorkflowFilePath)) File.Delete(WorkflowFilePath);
            if (File.Exists(SummaryFilePath)) File.Delete(SummaryFilePath);
            var temporaryPath = FilePath + ".tmp";
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            var workflowTemporaryPath = WorkflowFilePath + ".tmp";
            if (File.Exists(workflowTemporaryPath)) File.Delete(workflowTemporaryPath);
            var summaryTemporaryPath = SummaryFilePath + ".tmp";
            if (File.Exists(summaryTemporaryPath)) File.Delete(summaryTemporaryPath);
        }
    }

    private List<GenerationWorkflowDiagnosticEntry> ReadWorkflowRecentCore()
    {
        if (!File.Exists(WorkflowFilePath)) return [];
        var entries = new List<GenerationWorkflowDiagnosticEntry>();
        foreach (var line in File.ReadLines(WorkflowFilePath, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<GenerationWorkflowDiagnosticEntry>(line, _jsonOptions);
                if (entry is not null) entries.Add(entry);
            }
            catch (JsonException) { }
        }
        return entries.Count > _maxEntries ? entries.TakeLast(_maxEntries).ToList() : entries;
    }

    private List<GenerationDiagnosticEntry> ReadRecentCore()
    {
        if (!File.Exists(FilePath)) return [];
        var entries = new List<GenerationDiagnosticEntry>();
        foreach (var line in File.ReadLines(FilePath, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<GenerationDiagnosticEntry>(line, _jsonOptions);
                if (entry is not null) entries.Add(entry);
            }
            catch (JsonException) { }
        }
        return entries.Count > _maxEntries ? entries.TakeLast(_maxEntries).ToList() : entries;
    }

    private void WriteEntries(IReadOnlyList<GenerationDiagnosticEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporaryPath = FilePath + ".tmp";
        File.WriteAllLines(temporaryPath, entries.Select(entry => JsonSerializer.Serialize(entry, _jsonOptions)), new UTF8Encoding(false));
        File.Move(temporaryPath, FilePath, overwrite: true);
    }

    private void WriteWorkflowEntries(IReadOnlyList<GenerationWorkflowDiagnosticEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(WorkflowFilePath)!);
        var temporaryPath = WorkflowFilePath + ".tmp";
        File.WriteAllLines(temporaryPath, entries.Select(entry => JsonSerializer.Serialize(entry, _jsonOptions)), new UTF8Encoding(false));
        File.Move(temporaryPath, WorkflowFilePath, overwrite: true);
    }

    private static int? NormalizeCount(int? value) => value is >= 0 ? value : null;

    private static double? NormalizeMilliseconds(double? value) => value is { } milliseconds &&
        double.IsFinite(milliseconds) && milliseconds is >= 0 and <= 3_600_000 ? Math.Round(milliseconds, 1) : null;

    private static long? NormalizeBytes(long? value) => value is >= 0 ? value : null;

    private static string? NormalizeRuntimeBackend(string? value) => value?.Trim() switch
    {
        "CPU" => "CPU",
        "Vulkan" => "Vulkan",
        _ => null
    };

    private static void ValidateGenerationId(string generationId)
    {
        if (!Guid.TryParseExact(generationId, "N", out _))
            throw new ArgumentException("Generation ID must be a generated GUID.", nameof(generationId));
    }

    private static string NormalizeTask(ApplicationMode task) => task switch
    {
        ApplicationMode.Polish => nameof(ApplicationMode.Polish),
        ApplicationMode.PromptOptimize => nameof(ApplicationMode.PromptOptimize),
        _ => "unknown"
    };

    private static string NormalizeIdentifier(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = new string(value.Trim().Take(maxLength).Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        return string.Join(' ', normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string? NormalizeRequestId(string? value)
    {
        var normalized = NormalizeIdentifier(value, 128);
        return SafeRequestId.IsMatch(normalized) ? normalized : null;
    }
}

/// <summary>Correlates Provider attempts with one final quality-gate summary for a user generation.</summary>
public sealed class GenerationDiagnosticsScope
{
    private readonly GenerationDiagnosticsService _service;
    private readonly Func<bool> _isEnabled;
    private readonly ProviderRoutingMode? _routingMode;
    private readonly string? _routeReason;
    private readonly string? _primaryProfileId;
    private readonly string? _fallbackProfileId;
    private int _requestCount;
    private int _completed;

    public GenerationDiagnosticsScope(
        GenerationDiagnosticsService service,
        ApplicationMode task,
        Func<bool>? isEnabled = null,
        ProviderRoutingMode? routingMode = null,
        string? routeReason = null,
        string? primaryProfileId = null,
        string? fallbackProfileId = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        Task = task;
        _isEnabled = isEnabled ?? (() => true);
        _routingMode = routingMode;
        _routeReason = routeReason;
        _primaryProfileId = primaryProfileId;
        _fallbackProfileId = fallbackProfileId;
        GenerationId = Guid.NewGuid().ToString("N");
    }

    public string GenerationId { get; }
    public ApplicationMode Task { get; }
    public int RequestCount => System.Threading.Volatile.Read(ref _requestCount);

    public void RecordRequest(ProviderProfile profile, ProviderRequestTelemetry telemetry)
    {
        if (System.Threading.Volatile.Read(ref _completed) != 0 || !IsEnabled()) return;
        System.Threading.Interlocked.Increment(ref _requestCount);
        var attempt = !string.IsNullOrWhiteSpace(_fallbackProfileId) &&
                      string.Equals(profile.Id, _fallbackProfileId, StringComparison.OrdinalIgnoreCase)
            ? "fallback"
            : !string.IsNullOrWhiteSpace(_primaryProfileId) &&
              string.Equals(profile.Id, _primaryProfileId, StringComparison.OrdinalIgnoreCase)
                ? "primary"
                : null;
        try { _service.Record(GenerationId, profile, Task, telemetry, _routingMode, _routeReason, attempt); }
        catch { /* Optional diagnostics must never alter generation behavior. */ }
    }

    public void Complete(GenerationQualityObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (System.Threading.Interlocked.Exchange(ref _completed, 1) != 0) return;
        if (!IsEnabled()) return;
        try { _service.RecordWorkflow(GenerationId, Task, observation, RequestCount); }
        catch { /* Optional diagnostics must never alter generation behavior. */ }
    }

    private bool IsEnabled()
    {
        try { return _isEnabled(); }
        catch { return false; }
    }
}
