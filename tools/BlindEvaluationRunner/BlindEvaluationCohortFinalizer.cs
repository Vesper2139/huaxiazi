using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Huaxiazi.DatasetBuilder;

namespace Huaxiazi.BlindEvaluationRunner;

public sealed record BlindEvaluationCohortCandidateInput(
    string CandidateId,
    string RawPredictionsPath,
    string ReviewedPredictionsPath,
    string RunInfoPath,
    string SealedCandidateMapPath);

/// <summary>Creates a sealed manifest for one jointly scored, multi-candidate blind evaluation cohort.</summary>
public static class BlindEvaluationCohortFinalizer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    public static BlindEvaluationManifestReport Finalize(
        string goldPath,
        IReadOnlyList<BlindEvaluationCohortCandidateInput> candidates,
        string reportPath,
        string comparisonsPath,
        string pairwiseAnswersPath,
        string pairwiseReviewPackagePath,
        string pairwiseSealedMapPath,
        string outputManifestPath,
        BlindEvaluationManifestFinalizationMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(metadata);
        foreach (var path in new[] { goldPath, reportPath, comparisonsPath, pairwiseAnswersPath, pairwiseReviewPackagePath, pairwiseSealedMapPath, outputManifestPath })
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (candidates.Count < 2) throw new ArgumentException("cohort 至少需要两个候选。", nameof(candidates));
        if (candidates.Any(candidate => candidate is null || string.IsNullOrWhiteSpace(candidate.CandidateId) ||
            string.IsNullOrWhiteSpace(candidate.RawPredictionsPath) || string.IsNullOrWhiteSpace(candidate.ReviewedPredictionsPath) ||
            string.IsNullOrWhiteSpace(candidate.RunInfoPath) || string.IsNullOrWhiteSpace(candidate.SealedCandidateMapPath)))
            throw new ArgumentException("每个 cohort 候选都必须提供 alias、原始/评审预测、run-info 和封存映射路径。", nameof(candidates));
        if (candidates.Select(item => item.CandidateId).Distinct(StringComparer.Ordinal).Count() != candidates.Count)
            throw new InvalidOperationException("cohort candidate_id 必须唯一。");
        if (File.Exists(outputManifestPath) || Directory.Exists(outputManifestPath))
            throw new IOException("cohort 清单目标已存在；清单不可覆盖，请指定新路径。");

        var outputFullPath = Path.GetFullPath(outputManifestPath);
        var reviewerDirectories = candidates.Select(item => Path.GetFullPath(Path.GetDirectoryName(Path.GetFullPath(item.ReviewedPredictionsPath))!))
            .Append(Path.GetFullPath(Path.GetDirectoryName(Path.GetFullPath(pairwiseReviewPackagePath))!)).ToArray();
        if (reviewerDirectories.Any(directory => IsPathWithin(outputFullPath, directory)))
            throw new InvalidOperationException("cohort 清单包含解盲身份信息，必须保存在所有评审产物目录之外。");
        var protectedPaths = candidates.Select(item => Path.GetFullPath(item.SealedCandidateMapPath))
            .Append(Path.GetFullPath(comparisonsPath)).Append(Path.GetFullPath(reportPath))
            .Append(Path.GetFullPath(pairwiseAnswersPath)).Append(Path.GetFullPath(pairwiseReviewPackagePath)).Append(Path.GetFullPath(pairwiseSealedMapPath));
        if (protectedPaths.Any(path => string.Equals(path, outputFullPath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("cohort 清单不得覆盖封存映射、评分报告或成对比较文件。");

        var gold = ReadJsonLines<BlindEvaluationRecord>(goldPath);
        if (gold.Length == 0) throw new InvalidOperationException("gold 数据集为空，不能封存 cohort。");
        var recordIssues = BlindEvaluationAuditor.ValidateRecordIntegrity(gold);
        if (recordIssues.Count > 0)
            throw new InvalidOperationException("gold human_review/record integrity 未通过当前准入：" + string.Join("；", recordIssues.Select(issue => $"{issue.RecordId}/{issue.Code}")));
        var goldIds = gold.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        if (goldIds.Count != gold.Length) throw new InvalidOperationException("gold 中存在重复样本 ID。");
        var candidateDetails = new List<object>();
        var allReviewed = new List<BlindEvaluationPrediction>();
        string? cohortSplit = null;
        BlindEvaluationRecord[]? selectedGold = null;
        var runDocuments = new List<JsonDocument>();
        var sealedDocuments = new List<JsonDocument>();
        string? cohortPromptBundleSha256 = null;
        string? cohortSystemPromptBundleSha256 = null;
        string? cohortUserMessageBundleSha256 = null;
        string? cohortOutputContractBundleSha256 = null;
        (double? Temperature, double? TopP, int? MaxOutputTokens, string ReasoningLevel)? cohortSampling = null;
        var hardwareProfile = RequiredMetadata(metadata.HardwareProfile, nameof(metadata.HardwareProfile));
        try
        {
            foreach (var input in candidates)
            {
                var run = JsonDocument.Parse(File.ReadAllText(input.RunInfoPath));
                var sealedMap = JsonDocument.Parse(File.ReadAllText(input.SealedCandidateMapPath));
                runDocuments.Add(run);
                sealedDocuments.Add(sealedMap);
                var runRoot = run.RootElement;
                var mapRoot = sealedMap.RootElement;
                if (GetOptionalInt(runRoot, "run_info_version") != 2 || GetOptionalInt(mapRoot, "manifest_version") != 2)
                    throw new InvalidOperationException($"候选 {input.CandidateId} 的 run-info 与封存映射必须使用包含输出契约指纹的版本 2 格式。");
                if (RequiredString(runRoot, "candidate_id") != input.CandidateId || RequiredString(mapRoot, "candidate_id") != input.CandidateId)
                    throw new InvalidOperationException($"候选 {input.CandidateId} 的 run-info、封存映射与参数 alias 不一致。");
                if (RequiredString(runRoot, "hardware_profile") != hardwareProfile || RequiredString(mapRoot, "hardware_profile") != hardwareProfile)
                    throw new InvalidOperationException($"候选 {input.CandidateId} 的硬件档位与 cohort 声明不一致；同一 cohort 必须在同一已声明硬件档位上运行。");
                var runSplit = RequiredString(runRoot, "split");
                var mapSplit = RequiredString(mapRoot, "split");
                if (runSplit is not ("development" or "frozen_test") || mapSplit != runSplit)
                    throw new InvalidOperationException($"候选 {input.CandidateId} 必须使用一致的 development 或 frozen_test split。");
                if (cohortSplit is null)
                {
                    cohortSplit = runSplit;
                    selectedGold = gold.Where(item => item.Split == cohortSplit).ToArray();
                    if (selectedGold.Length == 0)
                        throw new InvalidOperationException($"gold 中没有 cohort split={cohortSplit} 的样本。");
                }
                else if (cohortSplit != runSplit)
                    throw new InvalidOperationException("同一 cohort 的候选必须使用相同 split；development 与 frozen_test 不能混合比较。");
                var runConfirmedFrozenTest = RequiredBoolean(runRoot, "frozen_test_unlock_confirmed");
                var mapConfirmedFrozenTest = RequiredBoolean(mapRoot, "frozen_test_unlock_confirmed");
                if (runConfirmedFrozenTest != mapConfirmedFrozenTest)
                    throw new InvalidOperationException($"候选 {input.CandidateId} 的 run-info 与封存映射解封确认状态不一致。");
                if (runSplit == "frozen_test" && !runConfirmedFrozenTest)
                    throw new InvalidOperationException($"候选 {input.CandidateId} 的冻结集运行缺少显式解封确认。");
                if (RequiredString(runRoot, "dataset_sha256") != HashFile(goldPath) || RequiredString(mapRoot, "dataset_sha256") != HashFile(goldPath))
                    throw new InvalidOperationException($"候选 {input.CandidateId} 的数据集哈希与当前 gold 不一致。");
                if (RequiredString(runRoot, "predictions_sha256") != HashFile(input.RawPredictionsPath))
                    throw new InvalidOperationException($"候选 {input.CandidateId} 的原始预测哈希与 run-info 不一致。");
                if (RequiredString(runRoot, "prompt_bundle_sha256") != RequiredString(mapRoot, "prompt_bundle_sha256"))
                    throw new InvalidOperationException($"候选 {input.CandidateId} 的提示束哈希与 run-info/封存映射不一致。");
                var outputContractBundleSha256 = RequiredHash(mapRoot, "output_contract_bundle_sha256");
                if (!string.Equals(outputContractBundleSha256, RequiredHash(runRoot, "output_contract_bundle_sha256"), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"候选 {input.CandidateId} 的输出契约束哈希与 run-info/封存映射不一致。");
                if (GetOptionalInt(runRoot, "sensitive_output_withheld_count") is > 0)
                    throw new InvalidOperationException($"候选 {input.CandidateId} 曾剔除疑似敏感输出，必须隔离并重新运行。");
                if (!runRoot.TryGetProperty("content_telemetry_enabled", out var contentTelemetry) || contentTelemetry.ValueKind != JsonValueKind.False)
                    throw new InvalidOperationException($"候选 {input.CandidateId} 未确认内容遥测关闭。");
                if (GetOptionalInt(runRoot, "sample_count") != selectedGold!.Length)
                    throw new InvalidOperationException($"候选 {input.CandidateId} 的样本数与 split={cohortSplit} gold 不一致。");

                var raw = ReadJsonLines<BlindEvaluationPrediction>(input.RawPredictionsPath);
                var reviewed = ReadJsonLines<BlindEvaluationPrediction>(input.ReviewedPredictionsPath);
                if (raw.Length != selectedGold!.Length || reviewed.Length != selectedGold.Length)
                    throw new InvalidOperationException($"候选 {input.CandidateId} 的原始/评审预测覆盖数与 split={cohortSplit} gold 不一致。");
                VerifyReviewerAnnotationsOnly(raw, reviewed, input.CandidateId);
                if (reviewed.Any(item => item.CandidateId != input.CandidateId))
                    throw new InvalidOperationException($"评审预测混入其他候选 alias：{input.CandidateId}。");
                if (!reviewed.Select(item => item.Id).ToHashSet(StringComparer.Ordinal).SetEquals(selectedGold.Select(item => item.Id)))
                    throw new InvalidOperationException($"候选 {input.CandidateId} 的预测 ID 与 split={cohortSplit} gold 不完全一致。");
                allReviewed.AddRange(reviewed);

                var profile = GetRequiredObject(mapRoot, "profile");
                var platform = RequiredString(mapRoot, "platform");
                JsonElement? localArtifacts = null;
                if (platform.Equals("ManagedLocal", StringComparison.OrdinalIgnoreCase))
                {
                    if (!mapRoot.TryGetProperty("local_artifacts", out var artifacts) || artifacts.ValueKind != JsonValueKind.Object)
                        throw new InvalidOperationException($"候选 {input.CandidateId} 的 ManagedLocal 封存映射缺少模型与运行时制品信息。");
                    ValidateLocalArtifacts(artifacts, input.CandidateId);
                    localArtifacts = artifacts.Clone();
                }
                var promptBundleSha256 = RequiredHash(mapRoot, "prompt_bundle_sha256");
                var systemPromptBundleSha256 = RequiredHash(mapRoot, "system_prompt_bundle_sha256");
                var userMessageBundleSha256 = RequiredHash(mapRoot, "user_message_bundle_sha256");
                var sampling = (
                    Temperature: GetOptionalDouble(profile, "temperature"),
                    TopP: GetOptionalDouble(profile, "top_p"),
                    MaxOutputTokens: GetOptionalInt(profile, "max_tokens"),
                    ReasoningLevel: TryGetString(profile, "inference_level") ?? "not_supported");

                if (cohortPromptBundleSha256 is null)
                {
                    cohortPromptBundleSha256 = promptBundleSha256;
                    cohortSystemPromptBundleSha256 = systemPromptBundleSha256;
                    cohortUserMessageBundleSha256 = userMessageBundleSha256;
                    cohortOutputContractBundleSha256 = outputContractBundleSha256;
                    cohortSampling = sampling;
                }
                else
                {
                    if (!string.Equals(cohortOutputContractBundleSha256, outputContractBundleSha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("同一 cohort 的候选必须使用相同的输出契约束；输出 Schema 或本地校验规则不同的候选需分开评估。");
                    if (!string.Equals(cohortPromptBundleSha256, promptBundleSha256, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(cohortSystemPromptBundleSha256, systemPromptBundleSha256, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(cohortUserMessageBundleSha256, userMessageBundleSha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("同一 cohort 的候选必须使用相同的数据提示束、系统提示束和用户消息束；提示不同的候选需分开评估。");

                    if (cohortSampling is not { } baselineSampling ||
                        !Nullable.Equals(baselineSampling.Temperature, sampling.Temperature) ||
                        !Nullable.Equals(baselineSampling.TopP, sampling.TopP) ||
                        !Nullable.Equals(baselineSampling.MaxOutputTokens, sampling.MaxOutputTokens) ||
                        !string.Equals(baselineSampling.ReasoningLevel, sampling.ReasoningLevel, StringComparison.Ordinal))
                        throw new InvalidOperationException("同一 cohort 的候选必须使用相同的采样参数（温度、top-p、最大输出和推理级别）。");
                }

                candidateDetails.Add(new
                {
                    candidate_id = input.CandidateId,
                    provider = RequiredString(mapRoot, "provider") + "/" + RequiredString(mapRoot, "platform") + "/" + RequiredString(mapRoot, "protocol"),
                    model_id_and_version = RequiredString(mapRoot, "resolved_model_id"),
                    runtime_version = RequiredMetadata(metadata.RuntimeVersion, nameof(metadata.RuntimeVersion)),
                    local_artifacts = localArtifacts,
                    prompt_bundle_sha256 = promptBundleSha256,
                    system_prompt_bundle_sha256 = systemPromptBundleSha256,
                    user_message_bundle_sha256 = userMessageBundleSha256,
                    output_contract_bundle_sha256 = outputContractBundleSha256,
                    sampling = new
                    {
                        temperature = sampling.Temperature, top_p = sampling.TopP,
                        max_output_tokens = sampling.MaxOutputTokens, reasoning_level = sampling.ReasoningLevel
                    },
                    artifacts = new
                    {
                        raw_predictions_sha256 = HashFile(input.RawPredictionsPath),
                        reviewed_predictions_sha256 = HashFile(input.ReviewedPredictionsPath),
                        run_info_sha256 = HashFile(input.RunInfoPath),
                        sealed_candidate_map_sha256 = HashFile(input.SealedCandidateMapPath)
                    },
                    execution = new
                    {
                        hardware_profile = hardwareProfile,
                        hardware_profile_source = "operator_declared_unverified",
                        privacy_mode = RequiredString(mapRoot, "provider").Equals("Cloud", StringComparison.OrdinalIgnoreCase) ? "cloud-explicitly-authorized" : "local-only",
                        content_telemetry_enabled = false,
                        frozen_test_unlock_confirmed = runConfirmedFrozenTest,
                        request_count = GetOptionalInt(runRoot, "provider_request_count") ?? 0,
                        api_cost_accounting = new
                        {
                            status = RequiredString(mapRoot, "provider").Equals("Cloud", StringComparison.OrdinalIgnoreCase) ? "not_reported" : "not_applicable",
                            amount = (double?)null,
                            currency = (string?)null,
                            basis_kind = (string?)null,
                            basis_ref = (string?)null,
                            basis_sha256 = (string?)null,
                            assessed_at_utc = (string?)null
                        },
                        started_at_utc = RequiredString(runRoot, "started_at_utc"),
                        completed_at_utc = RequiredString(runRoot, "completed_at_utc")
                    }
                });
            }

            using var reportDocument = JsonDocument.Parse(File.ReadAllText(reportPath));
            var reportRoot = reportDocument.RootElement;
            if (!reportRoot.TryGetProperty("dataset_valid", out var datasetValid) || datasetValid.ValueKind != JsonValueKind.True)
                throw new InvalidOperationException("cohort 评分报告未确认数据集准入校验通过。");
            VerifyAdmissionProtocol(reportRoot);
            var scoring = GetRequiredObject(reportRoot, "scoring");
            if (RequiredString(reportRoot, "evaluation_split") != cohortSplit)
                throw new InvalidOperationException("cohort 评分报告的 evaluation_split 与候选运行 split 不一致；请按单一 split 重新执行 blind-evaluate。");
            if (scoring.TryGetProperty("issues", out var scoreIssues) && scoreIssues.ValueKind == JsonValueKind.Array && scoreIssues.GetArrayLength() > 0)
                throw new InvalidOperationException("cohort 评分报告包含评分问题，不能封存。");
            var comparisons = ReadJsonLines<BlindEvaluationPairwiseComparison>(comparisonsPath);
            if (comparisons.Length == 0) throw new InvalidOperationException("cohort 必须绑定非空双盲比较记录。");
            var reviewItems = ReadJsonLines<BlindPairwiseReviewItem>(pairwiseReviewPackagePath);
            var sealedPairwiseMap = JsonSerializer.Deserialize<BlindPairwiseSealedMap>(File.ReadAllText(pairwiseSealedMapPath), JsonOptions)
                ?? throw new JsonException("pairwise 封存映射为空。");
            if (!string.Equals(sealedPairwiseMap.Split, cohortSplit, StringComparison.Ordinal))
                throw new InvalidOperationException("pairwise 评审包 split 与 cohort 候选 split 不一致；开发集与冻结集不得合并。");
            var selectedIds = selectedGold!.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            if (sealedPairwiseMap.Items.Any(item => !selectedIds.Contains(item.GoldId)) || comparisons.Any(item => !selectedIds.Contains(item.Id)))
                throw new InvalidOperationException("pairwise 比较包含 cohort split 以外的 gold 样本。");
            if (!string.Equals(sealedPairwiseMap.ReviewPackageSha256, HashFile(pairwiseReviewPackagePath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("匿名 pairwise 评审包与封存映射的哈希不一致。");
            VerifyPairwiseArtifacts(reviewItems, sealedPairwiseMap.Items, comparisons, allReviewed);
            var answers = ReadJsonLines<BlindPairwiseAnswerTemplate>(pairwiseAnswersPath);
            var reconstructedComparisons = BlindEvaluationPairwisePackageBuilder.MergeAnswers(answers, sealedPairwiseMap.Items);
            if (!string.Equals(JsonSerializer.Serialize(reconstructedComparisons, JsonOptions), JsonSerializer.Serialize(comparisons, JsonOptions), StringComparison.Ordinal))
                throw new InvalidOperationException("解盲 comparisons 与原始匿名评审答卷/封存映射重建结果不一致。");
            var recalculated = BlindEvaluationScorer.Evaluate(selectedGold!, allReviewed, comparisons);
            var recorded = JsonSerializer.Deserialize<BlindEvaluationScoreReport>(scoring.GetRawText(), JsonOptions)
                ?? throw new JsonException("cohort 评分报告 scoring 节点为空。");
            if (!string.Equals(JsonSerializer.Serialize(recorded, JsonOptions), JsonSerializer.Serialize(recalculated, JsonOptions), StringComparison.Ordinal))
                throw new InvalidOperationException("cohort 评分报告与当前 gold、候选预测和比较记录不一致；请重新运行 blind-evaluate。");
            if (recorded.Candidates.Count != candidates.Count || recorded.Candidates.Any(item => !candidates.Any(candidate => candidate.CandidateId == item.CandidateId)))
                throw new InvalidOperationException("评分报告中的候选集合与 cohort 候选集合不一致。");

            var taskNames = selectedGold!.Select(item => item.Task).Distinct(StringComparer.Ordinal).ToArray();
            var manifest = new
            {
                manifest_version = BlindEvaluationAdmissionProtocol.RunManifestVersion,
                run_kind = "cohort",
                run_id = RequiredMetadata(metadata.RunId, nameof(metadata.RunId)),
                created_at_utc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                code_revision = RequiredMetadata(metadata.CodeRevision, nameof(metadata.CodeRevision)),
                data = new
                {
                    dataset_id = RequiredMetadata(metadata.DatasetId, nameof(metadata.DatasetId)), dataset_sha256 = HashFile(goldPath),
                    admission_protocol_id = BlindEvaluationAdmissionProtocol.ProtocolId,
                    admission_protocol_version = BlindEvaluationAdmissionProtocol.Version,
                    admission_policy_sha256 = BlindEvaluationAdmissionProtocol.PolicySha256,
                    evaluation_split = cohortSplit,
                    evaluation_role = cohortSplit == "frozen_test" ? "locked_final_evaluation" : "development_diagnostic",
                    frozen_test_unlock_confirmed = cohortSplit == "frozen_test",
                    sample_count = selectedGold!.Length,
                    dataset_sample_count = gold.Length,
                    semantic_family_split_sha256 = BlindEvaluationManifestValidator.ComputeSemanticFamilySplitSha256(gold.Select(item => (item.SemanticFamilyId, item.Split))),
                    source_authorization_review = RequiredMetadata(metadata.SourceAuthorizationReview, nameof(metadata.SourceAuthorizationReview))
                },
                artifacts = new
                {
                    evaluation_report_sha256 = HashFile(reportPath), comparisons_sha256 = HashFile(comparisonsPath),
                    pairwise_answers_sha256 = HashFile(pairwiseAnswersPath),
                    pairwise_review_package_sha256 = HashFile(pairwiseReviewPackagePath), pairwise_sealed_map_sha256 = HashFile(pairwiseSealedMapPath)
                },
                workflow = taskNames.Length == 1 ? taskNames[0] : "mixed",
                candidates = candidateDetails,
                scoring = recorded
            };
            var json = JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine;
            var finalDirectory = Path.GetDirectoryName(outputFullPath)!;
            Directory.CreateDirectory(finalDirectory);
            ImmutableArtifactWriter.WriteNew(outputFullPath, json);
            return new(true, selectedGold!.Length, HashFile(goldPath), BlindEvaluationManifestValidator.ComputeSemanticFamilySplitSha256(gold.Select(item => (item.SemanticFamilyId, item.Split))), []);
        }
        finally
        {
            foreach (var document in runDocuments) document.Dispose();
            foreach (var document in sealedDocuments) document.Dispose();
        }
    }

    private static void VerifyAdmissionProtocol(JsonElement reportRoot)
    {
        var protocolId = RequiredString(reportRoot, "admission_protocol_id");
        var protocolVersion = RequiredString(reportRoot, "admission_protocol_version");
        var policyHash = RequiredString(reportRoot, "admission_policy_sha256");
        if (!string.Equals(protocolId, BlindEvaluationAdmissionProtocol.ProtocolId, StringComparison.Ordinal) ||
            !string.Equals(protocolVersion, BlindEvaluationAdmissionProtocol.Version, StringComparison.Ordinal) ||
            !string.Equals(policyHash, BlindEvaluationAdmissionProtocol.PolicySha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("cohort 评分报告未绑定当前盲评准入协议；请使用当前版本重新执行 blind-evaluate。");
    }

    private static void VerifyReviewerAnnotationsOnly(IReadOnlyList<BlindEvaluationPrediction> raw, IReadOnlyList<BlindEvaluationPrediction> reviewed, string candidateId)
    {
        var rawById = raw.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var reviewedById = reviewed.ToDictionary(item => item.Id, StringComparer.Ordinal);
        if (rawById.Count != reviewedById.Count || rawById.Keys.Any(id => !reviewedById.ContainsKey(id)))
            throw new InvalidOperationException($"候选 {candidateId} 的评审预测 ID 与原始预测不一致。");
        foreach (var (id, rawItem) in rawById)
        {
            var reviewedItem = reviewedById[id];
            if (!string.Equals(JsonSerializer.Serialize(rawItem with { Reviews = [], Adjudication = null }, JsonOptions),
                JsonSerializer.Serialize(reviewedItem with { Reviews = [], Adjudication = null }, JsonOptions), StringComparison.Ordinal))
                throw new InvalidOperationException($"候选 {candidateId} 样本 {id} 的生成内容或运行遥测在评审副本中被修改。");
        }
    }

    private static void ValidateLocalArtifacts(JsonElement artifacts, string candidateId)
    {
        RequiredString(artifacts, "model_installation_id");
        RequiredString(artifacts, "model_version");
        if (!artifacts.TryGetProperty("model_size_bytes", out var size) || !size.TryGetInt64(out var sizeBytes) || sizeBytes < 0)
            throw new InvalidOperationException($"候选 {candidateId} 的 ManagedLocal 模型大小无效。");
        RequiredHash(artifacts, "model_sha256");
        if (!artifacts.TryGetProperty("adapter_installation_id", out var adapterId) ||
            !artifacts.TryGetProperty("adapter_sha256", out var adapterHash))
            throw new InvalidOperationException($"候选 {candidateId} 的 ManagedLocal 适配器字段不完整。");
        var hasAdapterId = adapterId.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(adapterId.GetString());
        var hasAdapterHash = adapterHash.ValueKind == JsonValueKind.String;
        if (adapterId.ValueKind != JsonValueKind.Null && !hasAdapterId || adapterHash.ValueKind != JsonValueKind.Null && !hasAdapterHash || hasAdapterId != hasAdapterHash)
            throw new InvalidOperationException($"候选 {candidateId} 的 ManagedLocal 适配器身份无效。");
        if (hasAdapterHash) RequiredHash(artifacts, "adapter_sha256");
        var flavor = RequiredString(artifacts, "runtime_flavor");
        if (flavor is not ("cpu" or "vulkan"))
            throw new InvalidOperationException($"候选 {candidateId} 的 ManagedLocal runtime flavor 无效。");
        if (!artifacts.TryGetProperty("runtime_file_version", out var runtimeVersion) ||
            runtimeVersion.ValueKind != JsonValueKind.Null && (runtimeVersion.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(runtimeVersion.GetString())))
            throw new InvalidOperationException($"候选 {candidateId} 的 ManagedLocal runtime 文件版本字段无效。");
        RequiredHash(artifacts, "runtime_sha256");
    }

    private static void VerifyPairwiseArtifacts(
        IReadOnlyList<BlindPairwiseReviewItem> reviewItems,
        IReadOnlyList<BlindPairwiseSealedMapItem> sealedItems,
        IReadOnlyList<BlindEvaluationPairwiseComparison> comparisons,
        IReadOnlyList<BlindEvaluationPrediction> predictions)
    {
        if (reviewItems.Count != sealedItems.Count || sealedItems.Count != comparisons.Count)
            throw new InvalidOperationException("匿名评审包、pairwise 封存映射与解盲 comparisons 的记录数不一致。");
        var reviewById = reviewItems.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var comparisonKeys = comparisons.Select(item => (item.Id, item.LeftCandidateId, item.RightCandidateId)).ToHashSet();
        var predictionsByKey = predictions.ToDictionary(item => (item.Id, item.CandidateId));
        var seenGoldPairs = new HashSet<(string Id, string First, string Second)>();
        foreach (var sealedItem in sealedItems)
        {
            if (!reviewById.TryGetValue(sealedItem.ReviewId, out var review))
                throw new InvalidOperationException("pairwise 封存映射引用了评审包中不存在的随机评审 ID。");
            if (!comparisonKeys.Contains((sealedItem.GoldId, sealedItem.LeftCandidateId, sealedItem.RightCandidateId)))
                throw new InvalidOperationException("解盲 comparisons 与 pairwise 封存映射中的样本/候选顺序不一致。");
            var pairKey = string.CompareOrdinal(sealedItem.LeftCandidateId, sealedItem.RightCandidateId) < 0
                ? (sealedItem.GoldId, sealedItem.LeftCandidateId, sealedItem.RightCandidateId)
                : (sealedItem.GoldId, sealedItem.RightCandidateId, sealedItem.LeftCandidateId);
            if (!seenGoldPairs.Add(pairKey)) throw new InvalidOperationException("pairwise 封存映射重复包含同一样本与候选组合。");
            if (!predictionsByKey.TryGetValue((sealedItem.GoldId, sealedItem.LeftCandidateId), out var left) ||
                !predictionsByKey.TryGetValue((sealedItem.GoldId, sealedItem.RightCandidateId), out var right) ||
                left.Status != "success" || right.Status != "success")
                throw new InvalidOperationException("pairwise 封存映射找不到对应的成功候选输出。");
            if (!string.Equals(HashText(review.LeftOutput), sealedItem.LeftOutputSha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(HashText(review.RightOutput), sealedItem.RightOutputSha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(HashText(left.Output), sealedItem.LeftOutputSha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(HashText(right.Output), sealedItem.RightOutputSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("pairwise 评审包或候选预测输出与封存输出摘要不一致。");
        }
    }

    private static T[] ReadJsonLines<T>(string path) => File.ReadLines(path)
        .Where(line => !string.IsNullOrWhiteSpace(line))
        .Select(line => JsonSerializer.Deserialize<T>(line, JsonOptions) ?? throw new JsonException($"{Path.GetFileName(path)} 包含空记录。"))
        .ToArray();

    private static JsonElement GetRequiredObject(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : throw new JsonException($"缺少 JSON 对象 {name}。");
    private static string RequiredString(JsonElement parent, string name) => TryGetString(parent, name) is { Length: > 0 } value ? value : throw new JsonException($"缺少非空字符串 {name}。");
    private static bool RequiredBoolean(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw new JsonException($"缺少布尔字段 {name}。");
    private static string? TryGetString(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string RequiredHash(JsonElement parent, string name) { var value = RequiredString(parent, name); return value.Length == 64 && value.All(Uri.IsHexDigit) ? value.ToLowerInvariant() : throw new JsonException($"{name} 不是 SHA-256。"); }
    private static int? GetOptionalInt(JsonElement parent, string name) => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
    private static double? GetOptionalDouble(JsonElement parent, string name) => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;
    private static string RequiredMetadata(string value, string field) => !string.IsNullOrWhiteSpace(value) ? value.Trim() : throw new ArgumentException($"最终清单元数据 {field} 不能为空。", field);
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string HashText(string value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static bool IsPathWithin(string targetPath, string directoryPath)
    {
        var target = Path.GetFullPath(targetPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var directory = Path.GetFullPath(directoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(target, directory, StringComparison.OrdinalIgnoreCase) || target.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
