using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Huaxiazi.DatasetBuilder;
using Huaxiazi.Models;
using Huaxiazi.Services;

namespace Huaxiazi.BlindEvaluationRunner;

public sealed record InternalRegressionDiagnosticSummary(
    int SampleCount,
    int CompletedCount,
    int ClarificationCount,
    int RejectedCount,
    int FailedCount,
    int CancelledCount,
    int ProviderRequestCount,
    string DatasetSha256,
    string RunInfoPath);

public sealed record InternalDiagnosticPredictionContent(
    string Status,
    string Output,
    IReadOnlyList<string> ClarificationQuestions);

public sealed record InternalDiagnosticRequestTrace(
    int RequestIndex,
    string RequestKind,
    string SystemPromptSha256,
    string UserInputSha256,
    string? JsonSchemaSha256,
    string PromptBundleSha256,
    string ParameterBundleSha256);

/// <summary>Captures stable content hashes for internal local diagnostics without retaining prompt text.</summary>
public sealed class InternalDiagnosticRequestTraceClient : ITextGenerationClient, IStructuredTextGenerationClient
{
    private readonly ITextGenerationClient _inner;
    private readonly IStructuredTextGenerationClient _structured;
    private readonly string _parameterBundleSha256;
    private readonly List<InternalDiagnosticRequestTrace> _traces = [];

    public InternalDiagnosticRequestTraceClient(ITextGenerationClient inner, string parameterBundleSha256)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _structured = inner as IStructuredTextGenerationClient
            ?? throw new ArgumentException("诊断请求追踪要求底层客户端支持结构化生成。", nameof(inner));
        if (parameterBundleSha256 is null || parameterBundleSha256.Length != 64 ||
            parameterBundleSha256.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("参数包必须是 SHA-256 十六进制哈希。", nameof(parameterBundleSha256));
        _parameterBundleSha256 = parameterBundleSha256.ToLowerInvariant();
    }

    public IReadOnlyList<InternalDiagnosticRequestTrace> Traces => _traces.ToArray();

    public Task<string> GenerateAsync(string systemPrompt, string userInput, CancellationToken cancellationToken = default)
    {
        Record("text", systemPrompt, userInput, null);
        return _inner.GenerateAsync(systemPrompt, userInput, cancellationToken);
    }

    public Task<string> GenerateStructuredAsync(string systemPrompt, string userInput, string jsonSchema,
        CancellationToken cancellationToken = default)
    {
        Record("structured", systemPrompt, userInput, jsonSchema);
        return _structured.GenerateStructuredAsync(systemPrompt, userInput, jsonSchema, cancellationToken);
    }

    private void Record(string kind, string systemPrompt, string userInput, string? jsonSchema)
    {
        var promptBundle = JsonSerializer.Serialize(new
        {
            request_kind = kind,
            system_prompt = systemPrompt,
            user_input = userInput,
            json_schema = jsonSchema
        });
        _traces.Add(new InternalDiagnosticRequestTrace(
            _traces.Count + 1,
            kind,
            Sha256(systemPrompt),
            Sha256(userInput),
            jsonSchema is null ? null : Sha256(jsonSchema),
            Sha256(promptBundle),
            _parameterBundleSha256));
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

/// <summary>Opt-in local diagnostic modifier that requests Qwen3's documented non-thinking switch.</summary>
public sealed class Qwen3NoThinkDiagnosticClient : ITextGenerationClient, IStructuredTextGenerationClient
{
    private readonly ITextGenerationClient _inner;
    private readonly IStructuredTextGenerationClient _structured;

    public Qwen3NoThinkDiagnosticClient(ITextGenerationClient inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _structured = inner as IStructuredTextGenerationClient
            ?? throw new ArgumentException("Qwen3 no-think 诊断包装器要求底层客户端支持结构化生成。", nameof(inner));
    }

    public Task<string> GenerateAsync(string systemPrompt, string userInput, CancellationToken cancellationToken = default) =>
        _inner.GenerateAsync(AppendSwitch(systemPrompt), userInput, cancellationToken);

    public Task<string> GenerateStructuredAsync(string systemPrompt, string userInput, string jsonSchema, CancellationToken cancellationToken = default) =>
        _structured.GenerateStructuredAsync(AppendSwitch(systemPrompt), userInput, jsonSchema, cancellationToken);

    private static string AppendSwitch(string systemPrompt) =>
        string.IsNullOrWhiteSpace(systemPrompt) ? "/no_think" : systemPrompt.TrimEnd() + "\n/no_think";
}

/// <summary>
/// Runs the real Polish product workflow on the internal synthetic development split.
/// These artifacts are diagnostic only and can never contribute to the phase 0 blind gate.
/// </summary>
public static class InternalRegressionDiagnosticService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static InternalDiagnosticPredictionContent CreatePredictionContent(BlindWorkflowPrediction prediction)
    {
        ArgumentNullException.ThrowIfNull(prediction);
        return new(prediction.Status, prediction.Output, prediction.ClarificationQuestions.ToArray());
    }

    public static string ClassifyDiagnosticOutcome(string status) => status switch
    {
        "completed" => "completed",
        "needs_clarification" => "clarification",
        "failed" => "failed",
        "cancelled" => "cancelled",
        _ => "rejected"
    };

    public static InferenceLevel ResolveDiagnosticInferenceLevel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return InferenceLevel.Medium;
        if (!Enum.GetNames<InferenceLevel>().Contains(value, StringComparer.OrdinalIgnoreCase) ||
            !Enum.TryParse<InferenceLevel>(value, ignoreCase: true, out var level) ||
            level == InferenceLevel.Custom)
            throw new ArgumentException("--inference-level 只接受 Low、Medium 或 High。", nameof(value));
        return level;
    }

    public static async Task<InternalRegressionDiagnosticSummary> RunAsync(
        string casesPath,
        string datasetManifestPath,
        string modelId,
        string outputDirectory,
        double temperature = 0.4,
        double topP = 1.0,
        int maxTokens = 2048,
        CancellationToken cancellationToken = default,
        Action<int, int, string>? progress = null,
        bool qwen3NoThink = false,
        int? diagnosticSeed = null,
        string? sampleId = null,
        string? diagnosticPreferenceProfileId = null,
        InferenceLevel diagnosticInferenceLevel = InferenceLevel.Medium)
    {
        var preferenceProfile = InternalDiagnosticPreferenceProfiles.Resolve(diagnosticPreferenceProfileId);
        if (diagnosticInferenceLevel == InferenceLevel.Custom)
            throw new ArgumentException("内部诊断不支持 Custom 推理档。", nameof(diagnosticInferenceLevel));
        var input = LoadDevelopmentRecords(casesPath, datasetManifestPath, sampleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (!double.IsFinite(temperature) || temperature is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(temperature));
        if (!double.IsFinite(topP) || topP is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(topP));
        if (maxTokens is < 128 or > 32768)
            throw new ArgumentOutOfRangeException(nameof(maxTokens));
        if (qwen3NoThink && !IsQwen3Model(modelId))
            throw new InvalidOperationException("--qwen3-no-think 仅适用于 Qwen3 系列 Ollama 型号。未发送任何 Provider 请求。");

        var profile = new ProviderProfile
        {
            Id = "internal-diagnostic-ollama",
            Name = "本机合成回归诊断",
            Type = ProviderType.Local,
            Platform = ProviderPlatform.Ollama,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "http://127.0.0.1:11434/v1",
            Model = modelId.Trim(),
            TimeoutSeconds = 180,
            Temperature = temperature,
            TopP = topP,
            MaxTokens = maxTokens,
            InferenceLevel = diagnosticInferenceLevel
        };
        AIService.ValidateProviderProfile(profile, string.Empty);
        var outputPath = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(outputPath) || File.Exists(outputPath))
            throw new IOException("诊断输出目录已存在；请为每次运行指定一个全新目录。");

        using var localHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var modelMetadata = await ReadOllamaModelMetadataAsync(localHttp, profile.ApiBase, profile.Model, cancellationToken).ConfigureAwait(false);
        var promptSnapshots = input.Records.Select(BlindPromptSnapshotCompiler.Compile).ToArray();
        var promptBundleHash = Sha256(string.Join("\n", promptSnapshots.Select(BlindPromptSnapshotCompiler.BuildBundleHashLine)) +
            (qwen3NoThink ? "\nqwen3_no_think_switch=/no_think" : "\nqwen3_no_think_switch=disabled") +
            $"\ndiagnostic_preference_profile={preferenceProfile?.Id ?? "none"}" +
            $"\ndiagnostic_preference_sha256={preferenceProfile?.Sha256 ?? "none"}");
        var outputContractBundleHash = Sha256(string.Join("\n", promptSnapshots.Select(BlindPromptSnapshotCompiler.BuildOutputContractHashLine)));
        var parameterBundleSha256 = Sha256(JsonSerializer.Serialize(new
        {
            provider = "Ollama",
            protocol = profile.Protocol.ToString(),
            model_id = profile.Model.Trim(),
            temperature = profile.Temperature,
            top_p = profile.TopP,
            max_tokens = profile.MaxTokens,
            inference_level = profile.InferenceLevel.ToString(),
            diagnostic_seed = diagnosticSeed,
            qwen3_no_think_switch_requested = qwen3NoThink
        }, JsonOptions));

        Directory.CreateDirectory(outputPath);
        var predictionsPath = Path.Combine(outputPath, "predictions.jsonl");
        var telemetry = new List<ProviderRequestTelemetry>();
        var allProviderTelemetry = new List<ProviderRequestTelemetry>();
        var durations = new List<double>();
        var completed = 0;
        var clarifications = 0;
        var rejected = 0;
        var failed = 0;
        var cancelled = 0;
        var providerRequestCount = 0;
        var totalInputTokens = 0;
        var totalOutputTokens = 0;
        var allInputTokenCountsKnown = true;
        var allOutputTokenCountsKnown = true;
        var startedAt = DateTimeOffset.UtcNow;
        var service = new AIService(profile, string.Empty, telemetryObserver: telemetry.Add, diagnosticSeed: diagnosticSeed);
        try
        {
            for (var index = 0; index < input.Records.Count; index++)
            {
                var record = input.Records[index];
                telemetry.Clear();
                var requestTraceClient = new InternalDiagnosticRequestTraceClient(service, parameterBundleSha256);
                ITextGenerationClient generationClient = qwen3NoThink
                    ? new Qwen3NoThinkDiagnosticClient(requestTraceClient)
                    : requestTraceClient;
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled += input.Records.Count - index;
                    break;
                }

                var stopwatch = Stopwatch.StartNew();
                BlindWorkflowPrediction prediction;
                try
                {
                    prediction = await BlindWorkflowExecutor.ExecuteAsync(
                        BlindWorkflowRequestFactory.Create(record,
                            diagnosticPreferenceInstructions: preferenceProfile?.Instructions),
                        generationClient, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    stopwatch.Stop();
                    cancelled++;
                    durations.Add(stopwatch.Elapsed.TotalMilliseconds);
                    WritePrediction(predictionsPath, new
                    {
                        id = record.Id,
                        family_id = record.SemanticFamilyId,
                        status = "cancelled",
                        output = string.Empty,
                        clarification_questions = Array.Empty<string>(),
                        quality_issue_codes = Array.Empty<string>(),
                        workflow_latency_ms = stopwatch.Elapsed.TotalMilliseconds,
                        provider_requests = telemetry.Count,
                        provider_request_details = BuildProviderRequestDetails(telemetry),
                        request_traces = requestTraceClient.Traces,
                        input_tokens = SumTokens(telemetry.Select(item => item.InputTokens), out _),
                        output_tokens = SumTokens(telemetry.Select(item => item.OutputTokens), out _)
                    });
                    providerRequestCount += telemetry.Count;
                    foreach (var observation in telemetry)
                    {
                        if (observation.InputTokens is { } inputTokens) totalInputTokens += inputTokens;
                        else allInputTokenCountsKnown = false;
                        if (observation.OutputTokens is { } outputTokens) totalOutputTokens += outputTokens;
                        else allOutputTokenCountsKnown = false;
                    }
                    allProviderTelemetry.AddRange(telemetry);
                    cancelled += input.Records.Count - index - 1;
                    progress?.Invoke(index + 1, input.Records.Count, "cancelled");
                    break;
                }
                catch (Exception exception)
                {
                    stopwatch.Stop();
                    rejected++;
                    durations.Add(stopwatch.Elapsed.TotalMilliseconds);
                    WritePrediction(predictionsPath, new
                    {
                        id = record.Id,
                        family_id = record.SemanticFamilyId,
                        status = "failed",
                        output = string.Empty,
                        quality_issue_codes = new[] { exception is GenerationFailureException failure ? failure.Kind.ToString() : exception.GetType().Name },
                        workflow_latency_ms = stopwatch.Elapsed.TotalMilliseconds,
                        provider_requests = telemetry.Count,
                        provider_request_details = BuildProviderRequestDetails(telemetry),
                        request_traces = requestTraceClient.Traces,
                        input_tokens = SumTokens(telemetry.Select(item => item.InputTokens), out _),
                        output_tokens = SumTokens(telemetry.Select(item => item.OutputTokens), out _)
                    });
                    providerRequestCount += telemetry.Count;
                    foreach (var observation in telemetry)
                    {
                        if (observation.InputTokens is { } inputTokens) totalInputTokens += inputTokens;
                        else allInputTokenCountsKnown = false;
                        if (observation.OutputTokens is { } outputTokens) totalOutputTokens += outputTokens;
                        else allOutputTokenCountsKnown = false;
                    }
                    allProviderTelemetry.AddRange(telemetry);
                    progress?.Invoke(index + 1, input.Records.Count, "failed");
                    continue;
                }

                stopwatch.Stop();
                durations.Add(stopwatch.Elapsed.TotalMilliseconds);
                var output = prediction.Output;
                var status = prediction.Status;
                var qualityCodes = prediction.QualityIssueCodes;
                if ((status is "completed" or "needs_clarification") &&
                    BlindEvaluationAuditor.ContainsPotentialSensitiveData(output))
                {
                    output = string.Empty;
                    status = "output_withheld_sensitive_pattern";
                    qualityCodes = ["potential_sensitive_data"];
                }
                switch (ClassifyDiagnosticOutcome(status))
                {
                    case "completed": completed++; break;
                    case "clarification": clarifications++; break;
                    case "failed": failed++; break;
                    default: rejected++; break;
                }

                var predictionContent = CreatePredictionContent(prediction);

                WritePrediction(predictionsPath, new
                {
                    id = record.Id,
                    family_id = record.SemanticFamilyId,
                    status,
                    output,
                    clarification_questions = predictionContent.ClarificationQuestions,
                    quality_issue_codes = qualityCodes,
                    prediction.WasRepaired,
                    workflow_latency_ms = stopwatch.Elapsed.TotalMilliseconds,
                    provider_requests = telemetry.Count,
                    provider_latencies_ms = telemetry.Select(item => item.LatencyMilliseconds).ToArray(),
                    provider_request_details = BuildProviderRequestDetails(telemetry),
                    request_traces = requestTraceClient.Traces,
                    input_tokens = SumTokens(telemetry.Select(item => item.InputTokens), out _),
                    output_tokens = SumTokens(telemetry.Select(item => item.OutputTokens), out _)
                });
                providerRequestCount += telemetry.Count;
                foreach (var observation in telemetry)
                {
                    if (observation.InputTokens is { } inputTokens) totalInputTokens += inputTokens;
                    else allInputTokenCountsKnown = false;
                    if (observation.OutputTokens is { } outputTokens) totalOutputTokens += outputTokens;
                    else allOutputTokenCountsKnown = false;
                }
                allProviderTelemetry.AddRange(telemetry);
                progress?.Invoke(index + 1, input.Records.Count, status);
            }
        }
        finally
        {
            service.Dispose();
        }

        var predictionsBytes = File.ReadAllBytes(predictionsPath);
        var completedAt = DateTimeOffset.UtcNow;
        var providerLatencySummary = SummarizeProviderLatency(allProviderTelemetry);
        var runInfo = new
        {
            run_info_version = 2,
            request_trace_schema_version = 1,
            run_kind = "internal_synthetic_diagnostic",
            started_at_utc = startedAt.ToString("O", CultureInfo.InvariantCulture),
            completed_at_utc = completedAt.ToString("O", CultureInfo.InvariantCulture),
            dataset_kind = "internal_regression_only",
            dataset_version = input.DatasetVersion,
            phase_0_gate_contribution = 0,
            blind_evaluation = false,
            split = "development",
            sample_selection_mode = sampleId is null ? "full_development" : "single_development_sample",
            selected_sample_id = sampleId,
            diagnostic_preference_profile = preferenceProfile?.Id,
            diagnostic_preference_sha256 = preferenceProfile?.Sha256,
            source_development_sample_count = input.SourceDevelopmentSampleCount,
            request_trace_content_included = false,
            request_parameter_bundle_sha256 = parameterBundleSha256,
            sample_count = input.Records.Count,
            completed_count = completed,
            clarification_count = clarifications,
            rejected_count = rejected,
            failed_count = failed,
            cancelled_count = cancelled,
            workflow_completion_rate = input.Records.Count == 0 ? 0 : (double)(completed + clarifications) / input.Records.Count,
            dataset_sha256 = input.DatasetSha256,
            predictions_sha256 = Sha256(predictionsBytes),
            prompt_bundle_sha256 = promptBundleHash,
            output_contract_bundle_sha256 = outputContractBundleHash,
            provider = "Ollama",
            model_id = profile.Model,
            model_digest = modelMetadata.Digest,
            model_size_bytes = modelMetadata.SizeBytes,
            model_parameter_size = modelMetadata.ParameterSize,
            model_quantization = modelMetadata.Quantization,
            model_context_length = modelMetadata.ContextLength,
            runtime_version = modelMetadata.RuntimeVersion,
            api_base = profile.ApiBase,
            temperature = profile.Temperature,
            top_p = profile.TopP,
            max_tokens = profile.MaxTokens,
            diagnostic_seed = diagnosticSeed,
            inference_level = profile.InferenceLevel.ToString(),
            qwen3_thinking_mode = ResolveQwen3ThinkingModeStatus(qwen3NoThink),
            provider_request_count = providerRequestCount,
            provider_latency_p50_ms = providerLatencySummary.P50Milliseconds,
            provider_latency_p95_ms = providerLatencySummary.P95Milliseconds,
            workflow_latency_p50_ms = Percentile(durations, 0.50),
            workflow_latency_p95_ms = Percentile(durations, 0.95),
            input_tokens = allInputTokenCountsKnown && providerRequestCount > 0 ? totalInputTokens : (int?)null,
            output_tokens = allOutputTokenCountsKnown && providerRequestCount > 0 ? totalOutputTokens : (int?)null,
            peak_model_memory_bytes = (long?)null,
            content_telemetry_enabled = false,
            code_assembly_sha256 = AssemblySha256(typeof(PolishWorkflowService).Assembly.Location),
            runner_assembly_sha256 = AssemblySha256(typeof(InternalRegressionDiagnosticService).Assembly.Location),
            limitations = new[]
            {
                "该结果仅为项目合成开发切片上的本机工作流诊断，不是盲评、真实用户质量证明或模型晋级依据。",
                "Ollama 作为外部本地服务运行；本工具记录客户端延迟与 API 用量（若服务提供），不测量模型进程峰值内存。",
                "未比较候选模型，不从未复核的 expected_decision/reference_output 推导质量分数。",
                "逐请求追踪只保存提示词、Schema 和参数包哈希及白名单终态，不保存这些字段的原文；哈希仍由内容派生，诊断产物应留在受控本机目录。"
            }
        };
        var runInfoPath = Path.Combine(outputPath, "run-info.json");
        ImmutableArtifactWriter.WriteNew(runInfoPath,
            JsonSerializer.Serialize(runInfo, new JsonSerializerOptions(JsonOptions) { WriteIndented = true }) + Environment.NewLine);
        return new(input.Records.Count, completed, clarifications, rejected, failed, cancelled, providerRequestCount,
            input.DatasetSha256, runInfoPath);
    }

    public static (IReadOnlyList<BlindEvaluationRecord> Records, string DatasetVersion, string DatasetSha256, int SourceDevelopmentSampleCount) LoadDevelopmentRecords(
        string casesPath,
        string datasetManifestPath,
        string? sampleId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(casesPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetManifestPath);
        var caseBytes = File.ReadAllBytes(casesPath);
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(datasetManifestPath));
        var root = manifest.RootElement;
        var purpose = ReadString(root, "purpose");
        if (!string.Equals(purpose, "internal_regression_only", StringComparison.Ordinal) ||
            !root.TryGetProperty("phase_0_gate_contribution", out var contribution) ||
            contribution.ValueKind != JsonValueKind.Number || contribution.GetInt32() != 0 ||
            !root.TryGetProperty("not_admissible_as_blind_eval", out var notBlind) || notBlind.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("只允许运行 purpose=internal_regression_only 且 phase_0_gate_contribution=0 的数据集；盲评数据必须走独立候选运行器。");
        if (!root.TryGetProperty("boundary", out var boundary) ||
            !boundary.TryGetProperty("blind_evaluation_allowed", out var blindAllowed) || blindAllowed.ValueKind != JsonValueKind.False)
            throw new InvalidOperationException("internal regression manifest 必须明确禁止 blind evaluation。");
        if (!root.TryGetProperty("files", out var files) || !files.TryGetProperty("cases.jsonl", out var casesHash) ||
            casesHash.ValueKind != JsonValueKind.String || !FixedHashEquals(casesHash.GetString(), Sha256(caseBytes)))
            throw new InvalidOperationException("cases.jsonl 与 internal regression manifest 的 SHA-256 不一致。");

        var allowedOrigins = boundary.TryGetProperty("allowed_internal_origins", out var origins) && origins.ValueKind == JsonValueKind.Array
            ? origins.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var allRecords = new List<BlindEvaluationRecord>();
        foreach (var (line, lineNumber) in File.ReadLines(casesPath).Select((line, index) => (line, index + 1)))
        {
            if (string.IsNullOrWhiteSpace(line)) throw new JsonException($"cases.jsonl 第 {lineNumber} 行为空。");
            using var document = JsonDocument.Parse(line);
            var element = document.RootElement;
            var origin = ReadString(element, "origin");
            if (!allowedOrigins.Contains(origin)) throw new InvalidOperationException($"internal regression 第 {lineNumber} 行 origin 不在 manifest 白名单中。");
            if (ReadString(element, "task") != "polish") throw new InvalidOperationException("internal regression diagnostic 当前仅支持 polish 任务。");
            var record = JsonSerializer.Deserialize<BlindEvaluationRecord>(line, JsonOptions)
                ?? throw new JsonException($"cases.jsonl 第 {lineNumber} 行无法解析。");
            var family = ReadString(element, "family_id");
            if (string.IsNullOrWhiteSpace(record.Id) || string.IsNullOrWhiteSpace(family) || string.IsNullOrWhiteSpace(record.Input))
                throw new InvalidOperationException($"internal regression 第 {lineNumber} 行缺少 id、family_id 或 input。");
            allRecords.Add(record with
            {
                SemanticFamilyId = family,
                Source = new BlindEvaluationSource(origin, "internal synthetic regression", "project-owned synthetic data")
            });
        }

        var selected = allRecords.Where(record => record.Split == "development").ToArray();
        if (selected.Length == 0 || allRecords.Any(record => record.Split is not ("development" or "regression")))
            throw new InvalidOperationException("internal regression 数据必须包含 development 样本，且 split 只能为 development/regression。");
        if (selected.Select(record => record.Id).Distinct(StringComparer.Ordinal).Count() != selected.Length ||
            selected.Select(record => record.SemanticFamilyId).Distinct(StringComparer.Ordinal).Count() != selected.Length)
            throw new InvalidOperationException("development 切片必须按语义族去重，每个 family 只能贡献一条样本。");
        var manifestCount = root.GetProperty("counts").GetProperty("development_families").GetInt32();
        if (selected.Length != manifestCount)
            throw new InvalidOperationException("development 样本数与 manifest 的 development_families 不一致。");
        var fullDevelopmentSampleCount = selected.Length;
        if (sampleId is not null)
        {
            if (string.IsNullOrWhiteSpace(sampleId)) throw new ArgumentException("sampleId 不能为空白。", nameof(sampleId));
            selected = selected.Where(record => string.Equals(record.Id, sampleId, StringComparison.Ordinal)).ToArray();
            if (selected.Length != 1)
                throw new InvalidOperationException("sampleId 必须精确匹配一个已校验的 development 样本；regression 与未知样本不可运行。");
        }
        return (selected, ReadString(root, "dataset_version"), Sha256(caseBytes), fullDevelopmentSampleCount);
    }

    private static string ResolveQwen3ThinkingModeStatus(bool noThinkSwitchRequested) =>
        noThinkSwitchRequested ? "no_think_prompt_switch_requested_effect_unverified" : "provider_default";

    private static object[] BuildProviderRequestDetails(IEnumerable<ProviderRequestTelemetry> observations) => observations
        .Select(item => (object)new
        {
            request_id = item.RequestId,
            outcome = item.Outcome,
            finish_reason = item.FinishReason,
            http_status_code = item.HttpStatusCode,
            latency_ms = item.LatencyMilliseconds,
            input_tokens = item.InputTokens,
            output_tokens = item.OutputTokens
        }).ToArray();

    private static async Task<(string Digest, long? SizeBytes, string? ParameterSize, string? Quantization, int? ContextLength, string? RuntimeVersion)> ReadOllamaModelMetadataAsync(
        HttpClient httpClient,
        string apiBase,
        string modelId,
        CancellationToken cancellationToken)
    {
        var baseUri = new Uri(apiBase, UriKind.Absolute);
        var tagsUri = new Uri(baseUri, "../api/tags");
        using var tags = await httpClient.GetAsync(tagsUri, cancellationToken).ConfigureAwait(false);
        tags.EnsureSuccessStatusCode();
        using var tagsDocument = JsonDocument.Parse(await tags.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        if (!tagsDocument.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("本机 Ollama 未返回模型清单。");
        foreach (var model in models.EnumerateArray())
        {
            if (!string.Equals(ReadString(model, "name"), modelId, StringComparison.Ordinal) &&
                !string.Equals(ReadString(model, "model"), modelId, StringComparison.Ordinal)) continue;
            var details = model.TryGetProperty("details", out var value) && value.ValueKind == JsonValueKind.Object ? value : default;
            string? detail(string name) => details.ValueKind == JsonValueKind.Object ? ReadString(details, name) : null;
            int? context = details.ValueKind == JsonValueKind.Object && details.TryGetProperty("context_length", out var contextValue) && contextValue.TryGetInt32(out var parsedContext)
                ? parsedContext : null;
            long? size = model.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out var parsedSize) ? parsedSize : null;
            var runtimeVersion = await TryReadOllamaVersionAsync(httpClient, new Uri(baseUri, "../api/version"), cancellationToken).ConfigureAwait(false);
            return (ReadString(model, "digest"), size, detail("parameter_size"), detail("quantization_level"), context, runtimeVersion);
        }
        throw new InvalidOperationException($"Ollama 本机未安装精确型号 {modelId}；为避免运行错误候选，本次停止。");
    }

    private static async Task<string?> TryReadOllamaVersionAsync(HttpClient httpClient, Uri versionUri, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(versionUri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return ReadString(document.RootElement, "version");
        }
        catch (HttpRequestException) { return null; }
        catch (JsonException) { return null; }
    }

    private static void WritePrediction(string path, object prediction) =>
        File.AppendAllText(path, JsonSerializer.Serialize(prediction, JsonOptions) + Environment.NewLine, new UTF8Encoding(false));

    private static int? SumTokens(IEnumerable<int?> tokens, out bool complete)
    {
        var values = tokens.ToArray();
        complete = values.Length > 0 && values.All(value => value.HasValue);
        if (!complete) return null;
        try { return checked(values.Sum(value => value!.Value)); }
        catch (OverflowException) { complete = false; return null; }
    }

    private static double? Percentile(IEnumerable<double> values, double percentile)
    {
        var sorted = values.Where(double.IsFinite).Order().ToArray();
        if (sorted.Length == 0) return null;
        var index = Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1);
        return Math.Round(sorted[index], 2);
    }

    public static (double? P50Milliseconds, double? P95Milliseconds) SummarizeProviderLatency(
        IEnumerable<ProviderRequestTelemetry> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var latencies = observations.Select(item => item.LatencyMilliseconds).Where(double.IsFinite).ToArray();
        return (Percentile(latencies, 0.50), Percentile(latencies, 0.95));
    }

    private static string ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool IsQwen3Model(string modelId)
    {
        var modelName = modelId.Trim().Split(':', 2)[0];
        return modelName.StartsWith("qwen3", StringComparison.OrdinalIgnoreCase) ||
            modelName.StartsWith("huaxiazi-qwen3", StringComparison.OrdinalIgnoreCase);
    }

    private static bool FixedHashEquals(string? expected, string actual) =>
        expected is not null && string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Sha256(string value) => Sha256(Encoding.UTF8.GetBytes(value));

    private static string AssemblySha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
