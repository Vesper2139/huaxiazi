using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Huaxiazi.DatasetBuilder;

namespace Huaxiazi.BlindEvaluationRunner;

public sealed record BlindEvaluationManifestFinalizationMetadata(
    string DatasetId,
    string SourceAuthorizationReview,
    string HardwareProfile,
    string RuntimeVersion,
    string CodeRevision,
    string RunId);

/// <summary>Creates the final, de-anonymized experiment manifest after human scoring is complete.</summary>
public static class BlindEvaluationManifestFinalizer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    public static BlindEvaluationManifestReport Finalize(
        string goldPath,
        string rawPredictionsPath,
        string reviewedPredictionsPath,
        string reportPath,
        string? comparisonsPath,
        string runInfoPath,
        string sealedCandidateMapPath,
        string outputManifestPath,
        BlindEvaluationManifestFinalizationMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        foreach (var value in new[] { goldPath, rawPredictionsPath, reviewedPredictionsPath, reportPath, runInfoPath, sealedCandidateMapPath, outputManifestPath })
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (Directory.Exists(outputManifestPath) || File.Exists(outputManifestPath))
            throw new IOException("最终运行清单目标已存在；清单不可覆盖，请指定新路径。");

        var outputFullPath = Path.GetFullPath(outputManifestPath);
        var sealedFullPath = Path.GetFullPath(sealedCandidateMapPath);
        var reviewerDirectory = Path.GetFullPath(Path.GetDirectoryName(Path.GetFullPath(reviewedPredictionsPath))!);
        if (IsPathWithin(outputFullPath, reviewerDirectory))
            throw new InvalidOperationException("最终清单包含解盲身份信息，必须保存在评审产物目录之外。");
        if (string.Equals(outputFullPath, sealedFullPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("最终清单不得覆盖封存候选映射。");

        using var goldDocument = JsonDocument.Parse("[" + string.Join(",", File.ReadLines(goldPath)) + "]");
        using var runDocument = JsonDocument.Parse(File.ReadAllText(runInfoPath));
        using var sealedDocument = JsonDocument.Parse(File.ReadAllText(sealedCandidateMapPath));
        using var reportDocument = JsonDocument.Parse(File.ReadAllText(reportPath));
        using var rawPredictionsDocument = ReadJsonLinesAsArray(rawPredictionsPath);
        using var reviewedPredictionsDocument = ReadJsonLinesAsArray(reviewedPredictionsPath);
        using var comparisonsDocument = string.IsNullOrWhiteSpace(comparisonsPath) ? null : ReadJsonLinesAsArray(comparisonsPath);

        var gold = goldDocument.RootElement.EnumerateArray().ToArray();
        var run = runDocument.RootElement;
        var sealedMap = sealedDocument.RootElement;
        var reportRoot = reportDocument.RootElement;
        var scoring = GetRequiredObject(reportRoot, "scoring");
        var evaluationSplit = RequiredString(reportRoot, "evaluation_split");
        if (evaluationSplit is not ("development" or "frozen_test"))
            throw new InvalidOperationException("单候选最终清单必须绑定 development 或 frozen_test 评分报告；all 只可用于诊断。");
        if (GetOptionalInt(run, "run_info_version") != 2 || GetOptionalInt(sealedMap, "manifest_version") != 2)
            throw new InvalidOperationException("候选 run-info 与封存映射必须使用包含输出契约指纹的版本 2 格式。");
        var candidateId = RequiredString(sealedMap, "candidate_id");
        if (RequiredString(run, "candidate_id") != candidateId)
            throw new InvalidOperationException("run-info 与封存候选映射的 candidate_id 不一致。");
        if (RequiredString(run, "split") != "all" || RequiredString(sealedMap, "split") != "all")
            throw new InvalidOperationException("只有覆盖完整 gold 快照的 split=all 运行才能生成最终质量清单；单 split 运行仅用于开发调试。");
        if (RequiredString(run, "dataset_sha256") != HashFile(goldPath))
            throw new InvalidOperationException("run-info 的数据哈希与当前 gold 文件不一致。");
        if (RequiredString(sealedMap, "dataset_sha256") != HashFile(goldPath))
            throw new InvalidOperationException("封存候选映射的数据哈希与当前 gold 文件不一致。");
        if (RequiredString(run, "predictions_sha256") != HashFile(rawPredictionsPath))
            throw new InvalidOperationException("run-info 的预测哈希与原始候选预测文件不一致。");
        if (RequiredString(sealedMap, "prompt_bundle_sha256") != RequiredString(run, "prompt_bundle_sha256"))
            throw new InvalidOperationException("run-info 与封存候选映射的提示束哈希不一致。");
        var outputContractBundleSha256 = RequiredHash(sealedMap, "output_contract_bundle_sha256");
        if (!string.Equals(outputContractBundleSha256, RequiredHash(run, "output_contract_bundle_sha256"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("run-info 与封存候选映射的输出契约束哈希不一致。");
        if (GetOptionalInt(run, "sensitive_output_withheld_count") is > 0)
            throw new InvalidOperationException("运行曾因敏感信息启发式剔除候选输出，不得生成最终质量清单；请隔离并重新运行。");
        if (!run.TryGetProperty("content_telemetry_enabled", out var contentTelemetry) || contentTelemetry.ValueKind != JsonValueKind.False)
            throw new InvalidOperationException("run-info 未确认内容遥测关闭，不能生成最终清单。");
        if (GetOptionalInt(run, "sample_count") != gold.Length || rawPredictionsDocument.RootElement.GetArrayLength() != gold.Length ||
            reviewedPredictionsDocument.RootElement.GetArrayLength() != gold.Length)
            throw new InvalidOperationException("gold、run-info、原始预测与已评审预测的样本数不一致。");
        if (!reportRoot.TryGetProperty("dataset_valid", out var datasetValid) || datasetValid.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("评分报告未确认数据集 admission 校验通过。");
        VerifyAdmissionProtocol(reportRoot);
        if (scoring.TryGetProperty("issues", out var scoreIssues) && scoreIssues.ValueKind == JsonValueKind.Array && scoreIssues.GetArrayLength() > 0)
            throw new InvalidOperationException("评分报告包含评分问题，不能封存为完成的评测运行。");

        var goldRecords = ReadJsonLines<BlindEvaluationRecord>(goldPath);
        var recordIssues = BlindEvaluationAuditor.ValidateRecordIntegrity(goldRecords);
        if (recordIssues.Count > 0)
            throw new InvalidOperationException("gold human_review/record integrity 未通过当前准入：" + string.Join("；", recordIssues.Select(issue => $"{issue.RecordId}/{issue.Code}")));
        var runConfirmedFrozenTest = RequiredBoolean(run, "frozen_test_unlock_confirmed");
        var mapConfirmedFrozenTest = RequiredBoolean(sealedMap, "frozen_test_unlock_confirmed");
        if (runConfirmedFrozenTest != mapConfirmedFrozenTest)
            throw new InvalidOperationException("run-info 与封存候选映射的 frozen_test 解封确认状态不一致。");
        var hardwareProfile = RequiredMetadata(metadata.HardwareProfile, nameof(metadata.HardwareProfile));
        if (RequiredString(run, "hardware_profile") != hardwareProfile || RequiredString(sealedMap, "hardware_profile") != hardwareProfile)
            throw new InvalidOperationException("最终清单的硬件档位必须与候选运行时的 run-info 和封存映射声明一致。");
        if (goldRecords.Any(record => record.Split == "frozen_test") && !runConfirmedFrozenTest)
            throw new InvalidOperationException("gold 含 frozen_test 样本，但运行记录未绑定显式解封确认。");
        var rawPredictionRecords = ReadJsonLines<BlindEvaluationPrediction>(rawPredictionsPath);
        var predictionRecords = ReadJsonLines<BlindEvaluationPrediction>(reviewedPredictionsPath);
        VerifyReviewerAnnotationsOnly(rawPredictionRecords, predictionRecords);
        var evaluationGold = goldRecords.Where(item => item.Split == evaluationSplit).ToArray();
        if (evaluationGold.Length == 0)
            throw new InvalidOperationException($"gold 中没有 evaluation_split={evaluationSplit} 的样本。");
        var evaluationIds = evaluationGold.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var evaluationPredictions = predictionRecords.Where(item => evaluationIds.Contains(item.Id)).ToArray();
        var comparisonRecords = string.IsNullOrWhiteSpace(comparisonsPath)
            ? Array.Empty<BlindEvaluationPairwiseComparison>()
            : ReadJsonLines<BlindEvaluationPairwiseComparison>(comparisonsPath);
        if (comparisonRecords.Any(item => !evaluationIds.Contains(item.Id)))
            throw new InvalidOperationException($"comparisons 含有 evaluation_split={evaluationSplit} 以外的 gold 样本；development 与 frozen_test 必须分别评审和收口。");
        var recalculated = BlindEvaluationScorer.Evaluate(evaluationGold, evaluationPredictions, comparisonRecords);
        var recorded = JsonSerializer.Deserialize<BlindEvaluationScoreReport>(scoring.GetRawText(), JsonOptions)
            ?? throw new JsonException("评分报告 scoring 节点为空。");
        if (!string.Equals(JsonSerializer.Serialize(recorded, JsonOptions), JsonSerializer.Serialize(recalculated, JsonOptions), StringComparison.Ordinal))
            throw new InvalidOperationException("评分报告与当前 gold、预测和比较记录不一致；请重新运行 blind-evaluate。");

        foreach (var prediction in reviewedPredictionsDocument.RootElement.EnumerateArray())
            if (RequiredString(prediction, "candidate_id") != candidateId)
                throw new InvalidOperationException("预测记录混入了不同候选 alias，无法绑定到单一封存候选。");
        var scoredCandidate = GetRequiredArray(scoring, "candidates").EnumerateArray()
            .SingleOrDefault(item => TryGetString(item, "candidate_id") == candidateId);
        if (scoredCandidate.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("评分报告中找不到唯一匹配的候选 alias。");

        var taskNames = evaluationGold.Select(item => item.Task).Distinct(StringComparer.Ordinal).ToArray();
        var workflow = taskNames.Length == 1 ? taskNames[0] : "mixed";
        var profile = GetRequiredObject(sealedMap, "profile");
        var providerType = RequiredString(sealedMap, "provider");
        var platform = RequiredString(sealedMap, "platform");
        JsonElement? localArtifacts = null;
        if (platform.Equals("ManagedLocal", StringComparison.OrdinalIgnoreCase))
        {
            if (!sealedMap.TryGetProperty("local_artifacts", out var sealedLocalArtifacts) || sealedLocalArtifacts.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("ManagedLocal 封存候选缺少已核验的模型与运行时制品信息。");
            localArtifacts = sealedLocalArtifacts.Clone();
        }
        var metrics = BuildMetrics(scoredCandidate, scoring, candidateId, providerType.Equals("Cloud", StringComparison.OrdinalIgnoreCase));
        var manifest = new
        {
            manifest_version = BlindEvaluationAdmissionProtocol.RunManifestVersion,
            run_id = RequiredMetadata(metadata.RunId, nameof(metadata.RunId)),
            run_kind = "candidate",
            created_at_utc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            code_revision = RequiredMetadata(metadata.CodeRevision, nameof(metadata.CodeRevision)),
            data = new
            {
                dataset_id = RequiredMetadata(metadata.DatasetId, nameof(metadata.DatasetId)),
                admission_protocol_id = BlindEvaluationAdmissionProtocol.ProtocolId,
                admission_protocol_version = BlindEvaluationAdmissionProtocol.Version,
                admission_policy_sha256 = BlindEvaluationAdmissionProtocol.PolicySha256,
                dataset_sha256 = HashFile(goldPath),
                sample_count = gold.Length,
                evaluation_split = evaluationSplit,
                evaluation_role = evaluationSplit == "frozen_test" ? "locked_final_evaluation" : "development_diagnostic",
                evaluation_sample_count = evaluationGold.Length,
                semantic_family_split_sha256 = ComputeFamilySplitHash(gold),
                source_authorization_review = RequiredMetadata(metadata.SourceAuthorizationReview, nameof(metadata.SourceAuthorizationReview))
            },
            artifacts = new
            {
                predictions_sha256 = HashFile(reviewedPredictionsPath),
                evaluation_report_sha256 = HashFile(reportPath),
                comparisons_sha256 = string.IsNullOrWhiteSpace(comparisonsPath) ? null : HashFile(comparisonsPath)
            },
            candidate = new
            {
                workflow,
                provider = providerType + "/" + platform + "/" + RequiredString(sealedMap, "protocol"),
                model_id_and_version = RequiredString(sealedMap, "resolved_model_id"),
                runtime_version = RequiredMetadata(metadata.RuntimeVersion, nameof(metadata.RuntimeVersion)),
                local_artifacts = localArtifacts,
                system_prompt_bundle_sha256 = RequiredHash(sealedMap, "system_prompt_bundle_sha256"),
                user_message_bundle_sha256 = RequiredHash(sealedMap, "user_message_bundle_sha256"),
                output_contract_bundle_sha256 = outputContractBundleSha256,
                sampling = new
                {
                    temperature = GetOptionalDouble(profile, "temperature"),
                    top_p = GetOptionalDouble(profile, "top_p"),
                    max_output_tokens = GetOptionalInt(profile, "max_tokens"),
                    reasoning_level = TryGetString(profile, "inference_level") ?? "not_supported"
                }
            },
            execution = new
            {
                hardware_profile = hardwareProfile,
                hardware_profile_source = "operator_declared_unverified",
                privacy_mode = providerType.Equals("Cloud", StringComparison.OrdinalIgnoreCase) ? "cloud-explicitly-authorized" : "local-only",
                content_telemetry_enabled = false,
                frozen_test_unlock_confirmed = runConfirmedFrozenTest,
                request_count = GetOptionalInt(run, "provider_request_count") ?? 0,
                started_at_utc = RequiredString(run, "started_at_utc"),
                completed_at_utc = RequiredString(run, "completed_at_utc")
            },
            metrics
        };

        var json = JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine;
        var validation = BlindEvaluationManifestValidator.Validate(json, goldPath, reviewedPredictionsPath, reportPath, comparisonsPath);
        if (!validation.Valid)
            throw new InvalidOperationException("生成的运行清单未通过一致性校验：" + string.Join("；", validation.Issues.Select(issue => issue.Field + " " + issue.Message)));
        ImmutableArtifactWriter.WriteNew(outputFullPath, json);
        return validation;
    }

    private static object BuildMetrics(JsonElement candidate, JsonElement scoring, string candidateId, bool isCloud)
    {
        var pairwise = GetRequiredArray(scoring, "pairwise_comparisons").EnumerateArray()
            .Where(item => TryGetString(item, "left_candidate_id") == candidateId || TryGetString(item, "right_candidate_id") == candidateId)
            .ToArray();
        var comparable = pairwise.Sum(item => GetOptionalInt(item, "comparable_count") ?? 0);
        double? preference = null;
        double[]? interval = null;
        int? decisiveFamilyCount = null;
        if (pairwise.Length == 1)
        {
            var comparison = pairwise[0];
            var candidateIsLeft = TryGetString(comparison, "left_candidate_id") == candidateId;
            var familyRate = GetOptionalDouble(comparison, "family_left_win_rate");
            var lower = GetOptionalDouble(comparison, "family_left_win_lower95");
            var upper = GetOptionalDouble(comparison, "family_left_win_upper95");
            decisiveFamilyCount = GetOptionalInt(comparison, "decisive_family_count");
            if (familyRate.HasValue && lower.HasValue && upper.HasValue && decisiveFamilyCount.HasValue)
            {
                preference = candidateIsLeft ? familyRate.Value : 1 - familyRate.Value;
                interval = candidateIsLeft
                    ? [lower.Value, upper.Value]
                    : [1 - upper.Value, 1 - lower.Value];
            }
        }
        var inputTokens = GetOptionalLong(candidate, "api_input_tokens");
        var outputTokens = GetOptionalLong(candidate, "api_output_tokens");
        var cacheReadInputTokens = GetOptionalLong(candidate, "api_cache_read_input_tokens");
        var cacheCreationInputTokens = GetOptionalLong(candidate, "api_cache_creation_input_tokens");
        return new
        {
            schema_valid_rate = GetOptionalDouble(candidate, "schema_valid_rate"),
            fact_constraint_retention_rate = GetOptionalDouble(candidate, "fact_constraint_retention_rate"),
            high_risk_key_fact_reversals = GetOptionalInt(candidate, "high_risk_key_fact_reversals"),
            direct_usability_rate = GetOptionalDouble(candidate, "direct_usability_rate"),
            tone_match_rate = GetOptionalDouble(candidate, "tone_match_rate"),
            clarification_decision_accuracy = GetOptionalDouble(candidate, "clarification_decision_accuracy"),
            pairwise_preference_rate = preference,
            pairwise_confidence_interval_95 = interval,
            pairwise_comparable_count = comparable,
            pairwise_decisive_family_count = decisiveFamilyCount,
            api_latency_p50_ms = isCloud ? GetOptionalDouble(candidate, "latency_p50_milliseconds") : null,
            api_latency_p95_ms = isCloud ? GetOptionalDouble(candidate, "latency_p95_milliseconds") : null,
            local_latency_p50_ms = isCloud ? null : GetOptionalDouble(candidate, "latency_p50_milliseconds"),
            local_latency_p95_ms = isCloud ? null : GetOptionalDouble(candidate, "latency_p95_milliseconds"),
            api_token_usage = inputTokens.HasValue && outputTokens.HasValue ? new
            {
                input_tokens = inputTokens,
                output_tokens = outputTokens,
                cache_read_input_tokens = cacheReadInputTokens,
                cache_creation_input_tokens = cacheCreationInputTokens
            } : null,
            api_cost_accounting = new
            {
                status = isCloud ? "not_reported" : "not_applicable",
                amount = (double?)null,
                currency = (string?)null,
                basis_kind = (string?)null,
                basis_ref = (string?)null,
                basis_sha256 = (string?)null,
                assessed_at_utc = (string?)null
            },
            local_model_load_ms = GetOptionalDouble(candidate, "local_model_load_p50_milliseconds"),
            local_tokens_per_second = GetOptionalDouble(candidate, "local_tokens_per_second_p50"),
            local_peak_memory_bytes = GetOptionalLong(candidate, "local_peak_memory_bytes")
        };
    }

    private static string ComputeFamilySplitHash(IEnumerable<JsonElement> records) =>
        BlindEvaluationManifestValidator.ComputeSemanticFamilySplitSha256(records.Select(record =>
            (RequiredString(record, "semantic_family_id"), RequiredString(record, "split"))));

    private static JsonDocument ReadJsonLinesAsArray(string path) =>
        JsonDocument.Parse("[" + string.Join(",", File.ReadLines(path)) + "]");

    private static T[] ReadJsonLines<T>(string path) => File.ReadLines(path)
        .Select(line => JsonSerializer.Deserialize<T>(line, JsonOptions) ?? throw new JsonException($"JSONL 文件 {Path.GetFileName(path)} 包含空记录。"))
        .ToArray();

    private static void VerifyAdmissionProtocol(JsonElement reportRoot)
    {
        var protocolId = RequiredString(reportRoot, "admission_protocol_id");
        var protocolVersion = RequiredString(reportRoot, "admission_protocol_version");
        var policyHash = RequiredString(reportRoot, "admission_policy_sha256");
        if (!string.Equals(protocolId, BlindEvaluationAdmissionProtocol.ProtocolId, StringComparison.Ordinal) ||
            !string.Equals(protocolVersion, BlindEvaluationAdmissionProtocol.Version, StringComparison.Ordinal) ||
            !string.Equals(policyHash, BlindEvaluationAdmissionProtocol.PolicySha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("评分报告未绑定当前盲评准入协议；请使用当前版本重新执行 blind-evaluate。");
    }

    private static void VerifyReviewerAnnotationsOnly(
        IReadOnlyList<BlindEvaluationPrediction> rawPredictions,
        IReadOnlyList<BlindEvaluationPrediction> reviewedPredictions)
    {
        var rawById = rawPredictions.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var reviewedById = reviewedPredictions.ToDictionary(item => item.Id, StringComparer.Ordinal);
        if (rawById.Count != reviewedById.Count || rawById.Keys.Any(id => !reviewedById.ContainsKey(id)))
            throw new InvalidOperationException("已评审预测的样本 ID 与原始候选预测不一致。");
        foreach (var (id, raw) in rawById)
        {
            var reviewed = reviewedById[id];
            var rawCandidateOutput = JsonSerializer.Serialize(raw with { Reviews = [], Adjudication = null }, JsonOptions);
            var reviewedCandidateOutput = JsonSerializer.Serialize(reviewed with { Reviews = [], Adjudication = null }, JsonOptions);
            if (!string.Equals(rawCandidateOutput, reviewedCandidateOutput, StringComparison.Ordinal))
                throw new InvalidOperationException($"样本 {id} 的候选输出或运行遥测在人工评审副本中被修改。");
        }
    }

    private static JsonElement GetRequiredObject(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw new JsonException($"缺少 JSON 对象 {name}。");

    private static JsonElement GetRequiredArray(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value
            : throw new JsonException($"缺少 JSON 数组 {name}。");

    private static string RequiredString(JsonElement parent, string name) =>
        TryGetString(parent, name) is { Length: > 0 } value ? value : throw new JsonException($"缺少非空字符串 {name}。");

    private static bool RequiredBoolean(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new JsonException($"缺少布尔字段 {name}。");

    private static string? TryGetString(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string RequiredHash(JsonElement parent, string name)
    {
        var value = RequiredString(parent, name);
        return value.Length == 64 && value.All(Uri.IsHexDigit) ? value.ToLowerInvariant() : throw new JsonException($"{name} 不是 SHA-256。");
    }

    private static int? GetOptionalInt(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static long? GetOptionalLong(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    private static double? GetOptionalDouble(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;

    private static string RequiredMetadata(string value, string field) =>
        !string.IsNullOrWhiteSpace(value) ? value.Trim() : throw new ArgumentException($"最终清单元数据 {field} 不能为空。", field);

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static bool IsPathWithin(string targetPath, string directoryPath)
    {
        var target = Path.GetFullPath(targetPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var directory = Path.GetFullPath(directoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(target, directory, StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
