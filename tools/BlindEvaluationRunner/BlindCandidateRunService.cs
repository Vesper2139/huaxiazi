using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Huaxiazi.DatasetBuilder;
using Huaxiazi.Models;
using Huaxiazi.Services;

namespace Huaxiazi.BlindEvaluationRunner;

public sealed record BlindCandidateRunResult(
    string CandidateId,
    int SampleCount,
    int CancelledCount,
    int ProviderRequestCount,
    string PredictionsSha256);

/// <summary>Runs one candidate through the real product workflows and writes blinded artifacts.</summary>
public static class BlindCandidateRunService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    public static async Task<BlindCandidateRunResult> RunAsync(
        IReadOnlyList<BlindEvaluationRecord> records,
        BlindCandidateRuntimeConfiguration candidate,
        string reviewerOutputDirectory,
        string sealedCandidateMapPath,
        string datasetSha256,
        string split,
        System.Collections.Generic.IList<ProviderRequestTelemetry> requestObservations,
        ITextGenerationClient client,
        CancellationToken cancellationToken = default,
        bool confirmFrozenTestLocked = false,
        string? hardwareProfile = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(requestObservations);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewerOutputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sealedCandidateMapPath);
        if (records.Count == 0) throw new ArgumentException("候选运行至少需要一条样本。", nameof(records));
        if (split is not ("development" or "frozen_test" or "all")) throw new ArgumentException("split 无效。", nameof(split));
        if (!Regex.IsMatch(datasetSha256, "^[a-fA-F0-9]{64}$")) throw new ArgumentException("datasetSha256 必须是 SHA-256。", nameof(datasetSha256));
        if (records.Any(record => record.Split == "frozen_test") && !confirmFrozenTestLocked)
            throw new InvalidOperationException("运行包含 frozen_test 样本前，必须确认候选模型、提示和采样参数均已锁定。");
        if (split != "all" && records.Any(record => !string.Equals(record.Split, split, StringComparison.Ordinal)))
            throw new InvalidOperationException("请求的 split 与候选运行样本的 split 不一致。");
        var recordIssues = BlindEvaluationAuditor.ValidateRecordIntegrity(records);
        if (recordIssues.Count > 0)
            throw new InvalidOperationException("候选运行 gold human_review/record integrity 未通过当前准入：" + string.Join("；", recordIssues.Select(issue => $"{issue.RecordId}/{issue.Code}")));

        var outputPath = Path.GetFullPath(reviewerOutputDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var mapPath = Path.GetFullPath(sealedCandidateMapPath);
        var outputPrefix = outputPath + Path.DirectorySeparatorChar;
        if (mapPath.StartsWith(outputPrefix, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mapPath, outputPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("候选身份封存文件必须存放在评审输出目录之外。");
        if (Directory.Exists(outputPath) || File.Exists(outputPath))
            throw new IOException("评审输出目录已存在；候选运行不可覆盖，请指定一个全新目录。");
        if (File.Exists(mapPath) || Directory.Exists(mapPath))
            throw new IOException("候选身份封存文件已存在；候选映射不可覆盖，请指定一个新文件。");
        ArgumentException.ThrowIfNullOrWhiteSpace(hardwareProfile);
        hardwareProfile = hardwareProfile.Trim();
        if (!Regex.IsMatch(hardwareProfile, "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("hardwareProfile 必须是 1 至 64 位不含空格的非识别性硬件档位标识。", nameof(hardwareProfile));

        var profile = candidate.Profile.Clone();
        if (profile.Platform == ProviderPlatform.ManagedLocal && candidate.LocalArtifacts is null)
            throw new InvalidOperationException("ManagedLocal 候选缺少经 SHA-256 核验的模型与运行时制品信息。");
        if (profile.Platform != ProviderPlatform.ManagedLocal && candidate.LocalArtifacts is not null)
            throw new InvalidOperationException("只有 ManagedLocal 候选可以携带本地模型/运行时制品信息。");
        ValidateIdentitySafeEndpoint(profile);
        var candidateId = "candidate-" + RandomNumberGenerator.GetHexString(16).ToLowerInvariant();
        var startedAt = DateTimeOffset.UtcNow;
        var promptSnapshots = records.Select(BlindPromptSnapshotCompiler.Compile).ToArray();
        var promptBundleHashInput = string.Join("\n", promptSnapshots.Select(BlindPromptSnapshotCompiler.BuildBundleHashLine));
        var promptBundleSha256 = Sha256(promptBundleHashInput);
        var outputContractBundleSha256 = Sha256(string.Join("\n", promptSnapshots.Select(BlindPromptSnapshotCompiler.BuildOutputContractHashLine)));
        var profileJson = JsonSerializer.Serialize(profile, JsonOptions);
        var systemPromptBundleSha256 = Sha256(string.Join("\n", promptSnapshots.Select(BlindPromptSnapshotCompiler.BuildSystemPromptHashLine)));
        var userMessageBundleSha256 = Sha256(string.Join("\n", promptSnapshots.Select(BlindPromptSnapshotCompiler.BuildUserMessageHashLine)));

        var candidateMap = new
        {
            manifest_version = 2,
            candidate_id = candidateId,
            created_at_utc = startedAt.ToString("O", CultureInfo.InvariantCulture),
            authorization_reference = candidate.AuthorizationReference,
            provider = profile.Type.ToString(),
            platform = profile.Platform.ToString(),
            protocol = profile.Protocol.ToString(),
            model_id = profile.Model,
            resolved_model_id = ResolveModelId(profile),
            api_base = profile.ApiBase,
            candidate_profile_sha256 = Sha256(profileJson),
            profile = new
            {
                profile.Id,
                profile.Name,
                type = profile.Type.ToString(),
                platform = profile.Platform.ToString(),
                protocol = profile.Protocol.ToString(),
                profile.TimeoutSeconds,
                profile.Temperature,
                profile.TopP,
                profile.MaxTokens,
                inference_level = profile.InferenceLevel.ToString(),
                profile.EnableModelMapping,
                profile.ModelMapping,
                profile.LocalModelInstallationId,
                profile.LocalAdapterInstallationId,
                profile.LocalRuntimeProfileId,
                profile.LocalRuntimeOptions
            },
            local_artifacts = candidate.LocalArtifacts,
            hardware_profile = hardwareProfile,
            prompt_bundle_sha256 = promptBundleSha256,
            output_contract_bundle_sha256 = outputContractBundleSha256,
            system_prompt_bundle_sha256 = systemPromptBundleSha256,
            user_message_bundle_sha256 = userMessageBundleSha256,
            dataset_sha256 = datasetSha256.ToLowerInvariant(),
            split,
            frozen_test_unlock_confirmed = confirmFrozenTestLocked
        };
        ImmutableArtifactWriter.WriteNew(mapPath, JsonSerializer.Serialize(candidateMap, JsonOptions) + Environment.NewLine);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        Directory.CreateDirectory(outputPath);
        var predictions = new System.Collections.Generic.List<BlindEvaluationPrediction>(records.Count);
        var telemetryLines = new System.Collections.Generic.List<object>(records.Count);
        var sensitiveOutputWithheldCount = 0;
        var providerRequestCount = 0;

        foreach (var record in records)
        {
            requestObservations.Clear();
            if (cancellationToken.IsCancellationRequested)
            {
                predictions.Add(new BlindEvaluationPrediction(record.Id, candidateId, string.Empty, false, [], null,
                    new BlindEvaluationTelemetry(null, null, null, null, null, null, "cancelled"), "cancelled"));
                telemetryLines.Add(TelemetryLine(record.Id, candidateId, requestObservations));
                continue;
            }

            try
            {
                var result = record.Turns is { Count: > 0 }
                    ? await BlindWorkflowExecutor.ExecuteConversationAsync(record, client, cancellationToken).ConfigureAwait(false)
                    : await BlindWorkflowExecutor.ExecuteAsync(BlindWorkflowRequestFactory.Create(record), client, cancellationToken).ConfigureAwait(false);
                var output = result.Status == "needs_clarification"
                    ? string.Join(Environment.NewLine, result.ClarificationQuestions)
                    : result.Output;
                var schemaValid = result.Status is "completed" or "needs_clarification";
                if (BlindEvaluationAuditor.ContainsPotentialSensitiveData(output))
                {
                    sensitiveOutputWithheldCount++;
                    predictions.Add(new BlindEvaluationPrediction(record.Id, candidateId, string.Empty, false, [], null,
                        BuildTelemetry(profile.Type, requestObservations, "other", GetLocalRuntimeMetrics(client)), "error"));
                }
                else if (schemaValid && !string.IsNullOrWhiteSpace(output))
                {
                    var telemetry = BuildTelemetry(profile.Type, requestObservations, null, GetLocalRuntimeMetrics(client));
                    predictions.Add(new BlindEvaluationPrediction(record.Id, candidateId, output, true, [], null,
                        telemetry, "success"));
                }
                else
                {
                    predictions.Add(new BlindEvaluationPrediction(record.Id, candidateId, string.Empty, false, [], null,
                        BuildTelemetry(profile.Type, requestObservations, "workflow_rejected", GetLocalRuntimeMetrics(client)), "error"));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                predictions.Add(new BlindEvaluationPrediction(record.Id, candidateId, string.Empty, false, [], null,
                    BuildTelemetry(profile.Type, requestObservations, "cancelled", GetLocalRuntimeMetrics(client)), "cancelled"));
            }
            catch (Exception exception)
            {
                predictions.Add(new BlindEvaluationPrediction(record.Id, candidateId, string.Empty, false, [], null,
                    BuildTelemetry(profile.Type, requestObservations, CategorizeError(exception), GetLocalRuntimeMetrics(client)), "error"));
            }
            telemetryLines.Add(TelemetryLine(record.Id, candidateId, requestObservations));
            providerRequestCount += requestObservations.Count;
        }

        var predictionsJsonl = string.Join(Environment.NewLine, predictions.Select(prediction =>
            JsonSerializer.Serialize(prediction, JsonOptions))) + Environment.NewLine;
        var telemetryJsonl = string.Join(Environment.NewLine, telemetryLines.Select(line =>
            JsonSerializer.Serialize(line, JsonOptions))) + Environment.NewLine;
        var predictionsPath = Path.Combine(outputPath, "predictions.jsonl");
        var telemetryPath = Path.Combine(outputPath, "request-telemetry.jsonl");
        ImmutableArtifactWriter.WriteNew(predictionsPath, predictionsJsonl);
        ImmutableArtifactWriter.WriteNew(telemetryPath, telemetryJsonl);
        var runInfo = new
        {
            run_info_version = 2,
            run_kind = "candidate",
            started_at_utc = startedAt.ToString("O", CultureInfo.InvariantCulture),
            completed_at_utc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            candidate_id = candidateId,
            split,
            frozen_test_unlock_confirmed = confirmFrozenTestLocked,
            sample_count = records.Count,
            dataset_sha256 = datasetSha256.ToLowerInvariant(),
            prompt_bundle_sha256 = promptBundleSha256,
            output_contract_bundle_sha256 = outputContractBundleSha256,
            predictions_sha256 = Sha256(predictionsJsonl),
            request_telemetry_sha256 = Sha256(telemetryJsonl),
            provider_request_count = providerRequestCount,
            sensitive_output_withheld_count = sensitiveOutputWithheldCount,
            hardware_profile = hardwareProfile,
            content_telemetry_enabled = false,
            reviewer_artifacts_contain_candidate_identity = false,
            product_assembly_version = typeof(PolishWorkflowService).Assembly.GetName().Version?.ToString() ?? "unknown",
            product_assembly_sha256 = AssemblySha256(typeof(PolishWorkflowService).Assembly.Location),
            runner_assembly_version = typeof(BlindCandidateRunService).Assembly.GetName().Version?.ToString() ?? "unknown",
            runner_assembly_sha256 = AssemblySha256(typeof(BlindCandidateRunService).Assembly.Location)
        };
        ImmutableArtifactWriter.WriteNew(Path.Combine(outputPath, "run-info.json"),
            JsonSerializer.Serialize(runInfo, new JsonSerializerOptions(JsonOptions) { WriteIndented = true }) + Environment.NewLine);

        return new(candidateId, predictions.Count,
            predictions.Count(prediction => prediction.Status == "cancelled"), providerRequestCount, Sha256(predictionsJsonl));
    }

    private static BlindEvaluationTelemetry BuildTelemetry(
        ProviderType providerType,
        System.Collections.Generic.IEnumerable<ProviderRequestTelemetry> observations,
        string? errorCategory,
        LocalRuntimeMetrics? localRuntimeMetrics)
    {
        var events = observations.ToArray();
        var latency = events.Length == 0 ? (double?)null : events.Sum(item => item.LatencyMilliseconds);
        if (providerType == ProviderType.Cloud)
        {
            return new BlindEvaluationTelemetry(latency,
                SumIfComplete(events.Select(item => item.Outcome == "success" ? item.InputTokens : null)),
                SumIfComplete(events.Select(item => item.Outcome == "success" ? item.OutputTokens : null)),
                null, null, null, errorCategory,
                SumIfComplete(events.Select(item => item.Outcome == "success" ? item.CacheReadInputTokens : null)),
                SumIfComplete(events.Select(item => item.Outcome == "success" ? item.CacheCreationInputTokens : null)));
        }

        var successfulOutputTokens = events
            .Where(item => item.Outcome == "success")
            .Select(item => item.OutputTokens)
            .ToArray();
        double? tokensPerSecond = null;
        if (successfulOutputTokens.Length > 0 && successfulOutputTokens.All(value => value.HasValue) && latency is > 0)
            tokensPerSecond = successfulOutputTokens.Sum(value => (double)value!.Value) * 1000d / latency.Value;
        return new BlindEvaluationTelemetry(
            latency,
            null,
            null,
            tokensPerSecond,
            localRuntimeMetrics?.PeakWorkingSetBytes,
            localRuntimeMetrics?.StartupToReadyMilliseconds,
            errorCategory);
    }

    private static LocalRuntimeMetrics? GetLocalRuntimeMetrics(ITextGenerationClient client) =>
        client is ILocalRuntimeMetricsClient metricsClient ? metricsClient.GetRuntimeMetrics() : null;

    private static long? SumIfComplete(System.Collections.Generic.IEnumerable<int?> values)
    {
        var materialized = values.ToArray();
        return materialized.Length > 0 && materialized.All(value => value.HasValue)
            ? materialized.Sum(value => (long)value!.Value)
            : null;
    }

    private static object TelemetryLine(
        string recordId,
        string candidateId,
        System.Collections.Generic.IEnumerable<ProviderRequestTelemetry> observations) => new
    {
        id = recordId,
        candidate_id = candidateId,
        requests = observations.Select(item => new
        {
            request_id = item.RequestId,
            latency_milliseconds = item.LatencyMilliseconds,
            input_tokens = item.InputTokens,
            output_tokens = item.OutputTokens,
            cache_read_input_tokens = item.CacheReadInputTokens,
            cache_creation_input_tokens = item.CacheCreationInputTokens,
            http_status_code = item.HttpStatusCode,
            outcome = item.Outcome
        }).ToArray()
    };

    private static string CategorizeError(Exception exception) => exception switch
    {
        OperationCanceledException => "cancelled",
        GenerationFailureException { Kind: GenerationFailureKind.Timeout } => "timeout",
        GenerationFailureException { Kind: GenerationFailureKind.RateLimited } => "rate_limited",
        GenerationFailureException { Kind: GenerationFailureKind.Refused } => "refused",
        GenerationFailureException { Kind: GenerationFailureKind.Incomplete } => "incomplete",
        GenerationFailureException { Kind: GenerationFailureKind.Authentication } => "authentication",
        GenerationFailureException { Kind: GenerationFailureKind.Network } => "network",
        GenerationFailureException { Kind: GenerationFailureKind.InvalidResponse } => "schema_rejected",
        GenerationFailureException { Kind: GenerationFailureKind.RequestRejected } => "schema_rejected",
        InvalidOperationException error when error.Message.Contains("返回为空", StringComparison.Ordinal) => "empty_output",
        UnauthorizedAccessException or IOException or ArgumentException => "runner_error",
        _ => "provider_error"
    };

    private static void ValidateIdentitySafeEndpoint(ProviderProfile profile)
    {
        if (!Uri.TryCreate(profile.ApiBase, UriKind.Absolute, out var endpoint) ||
            endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0)
            throw new InvalidOperationException("候选 API 地址不能在路径中嵌入凭据、查询参数或片段。");
    }

    private static string ResolveModelId(ProviderProfile profile) =>
        profile.EnableModelMapping && profile.ModelMapping.TryGetValue(profile.Model, out var mapped) && !string.IsNullOrWhiteSpace(mapped)
            ? mapped
            : profile.Model;

    private static string AssemblySha256(string path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path)
            ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
            : "unknown";

    private static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
