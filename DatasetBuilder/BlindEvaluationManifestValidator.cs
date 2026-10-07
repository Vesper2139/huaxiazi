using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Huaxiazi.DatasetBuilder;

public sealed record BlindEvaluationManifestIssue(string Code, string Field, string Message);

public sealed record BlindEvaluationManifestReport(bool Valid, int SampleCount, string DatasetSha256, string SemanticFamilySplitSha256, IReadOnlyList<BlindEvaluationManifestIssue> Issues);

/// <summary>Checks that a run manifest is bound to the exact blind dataset snapshot and privacy contract.</summary>
public static class BlindEvaluationManifestValidator
{
    private static readonly Regex CostEvidenceReference = new("^[A-Za-z0-9][A-Za-z0-9._:-]{2,127}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    public static BlindEvaluationManifestReport Validate(
        string manifestJson,
        string datasetPath,
        string predictionsPath,
        string reportPath,
        string? comparisonsPath = null)
    {
        ArgumentNullException.ThrowIfNull(manifestJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(predictionsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportPath);
        var issues = new List<BlindEvaluationManifestIssue>();
        using var manifestDocument = JsonDocument.Parse(manifestJson);
        var root = manifestDocument.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("运行清单根节点必须是 JSON 对象。");

        if (!TryObject(root, "data", issues, out var data) ||
            !TryObject(root, "candidate", issues, out var candidate) ||
            !TryObject(root, "artifacts", issues, out var artifacts) ||
            !TryObject(root, "metrics", issues, out var metrics) ||
            !TryObject(root, "execution", issues, out var execution))
            return new(false, 0, string.Empty, string.Empty, issues);

        if (!root.TryGetProperty("manifest_version", out var version) || !version.TryGetInt32(out var versionNumber) || versionNumber != BlindEvaluationAdmissionProtocol.RunManifestVersion)
            Add(issues, "manifest-version", "manifest_version", $"manifest_version 必须为 {BlindEvaluationAdmissionProtocol.RunManifestVersion}。");
        RequireString(root, "run_id", issues);
        var runKind = RequireString(root, "run_kind", issues);
        if (runKind is not null && runKind is not ("baseline" or "candidate"))
            Add(issues, "run-kind", "run_kind", "run_kind 必须是 baseline 或 candidate。");
        var manifestCreatedAt = RequireUtcTimestamp(root, "created_at_utc", issues);
        RequireString(root, "code_revision", issues);

        RequireString(data, "dataset_id", issues);
        var admissionProtocolId = RequireString(data, "admission_protocol_id", issues);
        var admissionProtocolVersion = RequireString(data, "admission_protocol_version", issues);
        var admissionPolicySha256 = RequireHash(data, "admission_policy_sha256", issues);
        if (!string.Equals(admissionProtocolId, BlindEvaluationAdmissionProtocol.ProtocolId, StringComparison.Ordinal) ||
            !string.Equals(admissionProtocolVersion, BlindEvaluationAdmissionProtocol.Version, StringComparison.Ordinal) ||
            !string.Equals(admissionPolicySha256, BlindEvaluationAdmissionProtocol.PolicySha256, StringComparison.OrdinalIgnoreCase))
            Add(issues, "admission-policy-mismatch", "data.admission_policy_sha256", "运行清单必须绑定当前盲评准入协议 ID、版本和规则哈希；旧版或不同规则的运行不可作为当前协议结果。");
        var expectedDatasetHash = RequireHash(data, "dataset_sha256", issues);
        var expectedCount = ReadNonNegativeInt(data, "sample_count", issues);
        RequireHash(data, "semantic_family_split_sha256", issues);
        RequireString(data, "source_authorization_review", issues);
        string? evaluationSplit = null;
        if (data.TryGetProperty("evaluation_split", out _))
        {
            evaluationSplit = RequireString(data, "evaluation_split", issues);
            if (evaluationSplit is not ("development" or "frozen_test"))
                Add(issues, "evaluation-split", "data.evaluation_split", "evaluation_split 只能是 development 或 frozen_test；all 仅用于诊断，不能写入最终清单。");
            var expectedRole = evaluationSplit == "frozen_test" ? "locked_final_evaluation" : "development_diagnostic";
            if (!string.Equals(RequireString(data, "evaluation_role", issues), expectedRole, StringComparison.Ordinal))
                Add(issues, "evaluation-role-mismatch", "data.evaluation_role", "evaluation_role 必须与声明的 evaluation_split 一致。");
            ReadNonNegativeInt(data, "evaluation_sample_count", issues);
        }

        var workflow = RequireString(candidate, "workflow", issues);
        if (workflow is not null && workflow is not ("polish" or "prompt_optimize" or "mixed"))
            Add(issues, "workflow", "candidate.workflow", "workflow 必须是 polish、prompt_optimize 或 mixed。");
        var provider = RequireString(candidate, "provider", issues);
        ValidateLocalArtifactProvenance(candidate, issues);
        RequireString(candidate, "model_id_and_version", issues);
        RequireString(candidate, "runtime_version", issues);
        RequireHash(candidate, "system_prompt_bundle_sha256", issues);
        RequireHash(candidate, "user_message_bundle_sha256", issues);
        RequireHash(candidate, "output_contract_bundle_sha256", issues);
        var predictionsHash = RequireHash(artifacts, "predictions_sha256", issues);
        var reportHash = RequireHash(artifacts, "evaluation_report_sha256", issues);
        var comparisonsHash = ReadOptionalHash(artifacts, "comparisons_sha256", issues);
        VerifyArtifactHash(predictionsPath, predictionsHash, "artifacts.predictions_sha256", issues);
        VerifyArtifactHash(reportPath, reportHash, "artifacts.evaluation_report_sha256", issues);
        ValidateAdmissionReport(reportPath, issues, evaluationSplit);
        if (comparisonsHash is null && comparisonsPath is not null)
            Add(issues, "artifact-hash-missing", "artifacts.comparisons_sha256", "提供了成对比较文件，但清单没有记录其哈希。");
        else if (comparisonsHash is not null && comparisonsPath is null)
            Add(issues, "artifact-file-missing", "artifacts.comparisons_sha256", "清单记录了成对比较哈希，但没有提供对应文件。");
        else if (comparisonsHash is not null && comparisonsPath is not null)
            VerifyArtifactHash(comparisonsPath, comparisonsHash, "artifacts.comparisons_sha256", issues);
        if (TryObject(candidate, "sampling", issues, out var sampling))
        {
            RequireString(sampling, "reasoning_level", issues);
            ValidateOptionalNonNegativeNumber(sampling, "temperature", issues);
            ValidateOptionalProbability(sampling, "top_p", issues);
            ValidateOptionalPositiveInt(sampling, "max_output_tokens", issues);
        }
        ValidateMetrics(metrics, issues, provider);

        RequireString(execution, "hardware_profile", issues);
        if (execution.TryGetProperty("hardware_profile_source", out var hardwareProfileSource) &&
            (hardwareProfileSource.ValueKind != JsonValueKind.String || hardwareProfileSource.GetString() != "operator_declared_unverified"))
            Add(issues, "hardware-profile-source", "execution.hardware_profile_source", "hardware_profile_source 当前只允许 operator_declared_unverified；该值不构成硬件真实性证明。");
        var privacyMode = RequireString(execution, "privacy_mode", issues);
        if (privacyMode is not null && privacyMode is not ("local-only" or "cloud-explicitly-authorized"))
            Add(issues, "privacy-mode", "execution.privacy_mode", "privacy_mode 必须明确为 local-only 或 cloud-explicitly-authorized。");
        if (!execution.TryGetProperty("content_telemetry_enabled", out var telemetry) || telemetry.ValueKind != JsonValueKind.False)
            Add(issues, "content-telemetry-enabled", "execution.content_telemetry_enabled", "阶段 0 运行不得启用内容遥测。");
        var frozenTestUnlockConfirmed = RequireBoolean(execution, "frozen_test_unlock_confirmed", issues);
        var requestCount = ReadNonNegativeInt(execution, "request_count", issues);
        var startedAt = RequireUtcTimestamp(execution, "started_at_utc", issues);
        var completedAt = RequireUtcTimestamp(execution, "completed_at_utc", issues);
        if (startedAt.HasValue && completedAt.HasValue && completedAt < startedAt)
            Add(issues, "execution-time-order", "execution.completed_at_utc", "完成时间不得早于开始时间。");
        if (manifestCreatedAt.HasValue && completedAt.HasValue && manifestCreatedAt < completedAt)
            Add(issues, "manifest-time-order", "created_at_utc", "最终清单创建时间不得早于运行完成时间。");

        var datasetBytes = File.ReadAllBytes(datasetPath);
        var actualHash = Convert.ToHexString(SHA256.HashData(datasetBytes));
        if (expectedDatasetHash is not null && !string.Equals(expectedDatasetHash, actualHash, StringComparison.OrdinalIgnoreCase))
            Add(issues, "dataset-hash-mismatch", "data.dataset_sha256", "运行清单的数据哈希与实际盲评文件不一致。");

        var (sampleCount, familySplitHash, containsFrozenTest, developmentCount, frozenTestCount) = ReadDatasetSnapshot(datasetBytes, issues);
        if (containsFrozenTest && frozenTestUnlockConfirmed is not true)
            Add(issues, "frozen-test-unlock-unconfirmed", "execution.frozen_test_unlock_confirmed", "数据集包含 frozen_test 样本，运行清单必须记录显式解封确认。");
        if (expectedCount.HasValue && expectedCount.Value != sampleCount)
            Add(issues, "sample-count-mismatch", "data.sample_count", "运行清单样本数与实际盲评文件不一致。");
        if (evaluationSplit is not null)
        {
            var expectedEvaluationCount = evaluationSplit == "development" ? developmentCount : frozenTestCount;
            var declaredEvaluationCount = ReadNonNegativeInt(data, "evaluation_sample_count", issues);
            if (declaredEvaluationCount.HasValue && declaredEvaluationCount.Value != expectedEvaluationCount)
                Add(issues, "evaluation-sample-count-mismatch", "data.evaluation_sample_count", "evaluation_sample_count 与所选 split 的实际样本数不一致。");
            if (!string.IsNullOrWhiteSpace(comparisonsPath))
                ValidateComparisonSplit(datasetBytes, comparisonsPath, evaluationSplit, issues);
        }
        if (sampleCount > 0 && requestCount.HasValue && requestCount.Value < sampleCount)
            Add(issues, "request-count-incomplete", "execution.request_count", "request_count 少于盲评样本数，不能作为完整冻结运行记录。");
        if (TryGetString(data, "semantic_family_split_sha256", out var expectedFamilySplitHash) &&
            !string.Equals(expectedFamilySplitHash, familySplitHash, StringComparison.OrdinalIgnoreCase))
            Add(issues, "family-split-hash-mismatch", "data.semantic_family_split_sha256", "语义族与 split 摘要与实际盲评文件不一致。");

        return new(issues.Count == 0, sampleCount, actualHash, familySplitHash, issues);
    }

    private static void ValidateLocalArtifactProvenance(JsonElement candidate, List<BlindEvaluationManifestIssue> issues)
    {
        var provider = TryGetString(candidate, "provider", out var providerName) ? providerName : string.Empty;
        var isManagedLocal = provider.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(part => string.Equals(part, "ManagedLocal", StringComparison.OrdinalIgnoreCase));
        if (!candidate.TryGetProperty("local_artifacts", out var artifacts) || artifacts.ValueKind == JsonValueKind.Null)
        {
            if (isManagedLocal)
                Add(issues, "local-artifact-provenance", "candidate.local_artifacts", "ManagedLocal 候选必须绑定已核验的模型与运行时制品信息。");
            return;
        }
        if (artifacts.ValueKind != JsonValueKind.Object)
        {
            Add(issues, "local-artifact-provenance", "candidate.local_artifacts", "local_artifacts 必须是对象或 null。");
            return;
        }

        RequireString(artifacts, "model_installation_id", issues);
        RequireString(artifacts, "model_version", issues);
        RequireNonNegativeLong(artifacts, "model_size_bytes", issues, "candidate.local_artifacts");
        RequireLocalProvenanceHash(artifacts, "model_sha256", issues);
        ValidateOptionalProvenanceString(artifacts, "adapter_installation_id", issues);
        ValidateOptionalHash(artifacts, "adapter_sha256", issues);
        var hasAdapterId = artifacts.TryGetProperty("adapter_installation_id", out var adapterId) && adapterId.ValueKind == JsonValueKind.String;
        var hasAdapterHash = artifacts.TryGetProperty("adapter_sha256", out var adapterHash) && adapterHash.ValueKind == JsonValueKind.String;
        if (hasAdapterId != hasAdapterHash)
            Add(issues, "local-adapter-provenance", "candidate.local_artifacts", "LoRA installation ID 与 SHA-256 必须同时提供或同时为 null。");

        if (!TryGetString(artifacts, "runtime_flavor", out var runtimeFlavor) || runtimeFlavor is not ("cpu" or "vulkan"))
            Add(issues, "local-runtime-flavor", "candidate.local_artifacts.runtime_flavor", "runtime_flavor 必须是 cpu 或 vulkan。");
        ValidateOptionalProvenanceString(artifacts, "runtime_file_version", issues);
        RequireLocalProvenanceHash(artifacts, "runtime_sha256", issues);
    }

    private static void RequireLocalProvenanceHash(JsonElement parent, string propertyName, List<BlindEvaluationManifestIssue> issues)
    {
        if (!TryGetString(parent, propertyName, out var value) || string.IsNullOrWhiteSpace(value))
        {
            Add(issues, "required-string", $"candidate.local_artifacts.{propertyName}", $"字段 {propertyName} 必须是非空字符串。");
            return;
        }
        if (!IsSha256(value))
            Add(issues, "invalid-sha256", $"candidate.local_artifacts.{propertyName}", $"字段 {propertyName} 必须是 64 位 SHA-256 十六进制摘要。");
    }

    private static void ValidateOptionalProvenanceString(JsonElement parent, string propertyName, List<BlindEvaluationManifestIssue> issues)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
        {
            Add(issues, "required-field", $"candidate.local_artifacts.{propertyName}", $"必须显式提供 {propertyName} 字段；不适用时设为 null。");
            return;
        }
        if (value.ValueKind != JsonValueKind.Null && (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())))
            Add(issues, "local-artifact-value", $"candidate.local_artifacts.{propertyName}", $"{propertyName} 必须是非空字符串或 null。");
    }

    private static void ValidateOptionalHash(JsonElement parent, string propertyName, List<BlindEvaluationManifestIssue> issues)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
        {
            Add(issues, "required-field", $"candidate.local_artifacts.{propertyName}", $"必须显式提供 {propertyName} 字段；不适用时设为 null。");
            return;
        }
        if (value.ValueKind != JsonValueKind.Null &&
            (value.ValueKind != JsonValueKind.String || !IsSha256(value.GetString() ?? string.Empty)))
            Add(issues, "invalid-sha256", $"candidate.local_artifacts.{propertyName}", $"{propertyName} 必须是 64 位 SHA-256 摘要或 null。");
    }

    private static long? RequireNonNegativeLong(JsonElement parent, string propertyName, List<BlindEvaluationManifestIssue> issues, string pathPrefix)
    {
        if (parent.TryGetProperty(propertyName, out var value) && value.TryGetInt64(out var number) && number >= 0) return number;
        Add(issues, "non-negative-integer", $"{pathPrefix}.{propertyName}", $"字段 {propertyName} 必须是非负整数。");
        return null;
    }

    public static string ComputeSemanticFamilySplitSha256(IEnumerable<(string FamilyId, string Split)> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var canonical = string.Concat(pairs.Distinct()
            .OrderBy(pair => pair.FamilyId, StringComparer.Ordinal)
            .ThenBy(pair => pair.Split, StringComparer.Ordinal)
            .Select(pair => $"{pair.FamilyId}\t{pair.Split}\n"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static (int SampleCount, string FamilySplitHash, bool ContainsFrozenTest, int DevelopmentCount, int FrozenTestCount) ReadDatasetSnapshot(byte[] datasetBytes, List<BlindEvaluationManifestIssue> issues)
    {
        var count = 0;
        var containsFrozenTest = false;
        var developmentCount = 0;
        var frozenTestCount = 0;
        var pairs = new List<(string FamilyId, string Split)>();
        var records = new List<BlindEvaluationRecord>();
        var lineNumber = 0;
        using var snapshotStream = new MemoryStream(datasetBytes, writable: false);
        using var reader = new StreamReader(snapshotStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                Add(issues, "blank-dataset-line", $"dataset.line.{lineNumber}", "盲评 JSONL 不得包含空行。");
                continue;
            }
            count++;
            using var recordDocument = JsonDocument.Parse(line);
            var record = recordDocument.RootElement;
            records.Add(JsonSerializer.Deserialize<BlindEvaluationRecord>(line, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            })!);
            if (record.ValueKind != JsonValueKind.Object ||
                !TryGetString(record, "semantic_family_id", out var family) || string.IsNullOrWhiteSpace(family) ||
                !TryGetString(record, "split", out var split) || split is not ("development" or "frozen_test"))
            {
                Add(issues, "dataset-family-split", $"dataset.line.{lineNumber}", "每条盲评记录必须包含有效 semantic_family_id 和 split。");
                continue;
            }
            pairs.Add((family, split));
            containsFrozenTest |= split == "frozen_test";
            if (split == "development") developmentCount++;
            else if (split == "frozen_test") frozenTestCount++;
        }
        foreach (var issue in BlindEvaluationAuditor.ValidateRecordIntegrity(records))
            Add(issues, issue.Code, $"dataset.{issue.RecordId}", issue.Message);
        return (count, ComputeSemanticFamilySplitSha256(pairs), containsFrozenTest, developmentCount, frozenTestCount);
    }

    private static void ValidateComparisonSplit(byte[] datasetBytes, string comparisonsPath, string evaluationSplit, List<BlindEvaluationManifestIssue> issues)
    {
        var splitsById = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var stream = new MemoryStream(datasetBytes, writable: false))
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var item = JsonDocument.Parse(line);
                if (TryGetString(item.RootElement, "id", out var id) && TryGetString(item.RootElement, "split", out var split))
                    splitsById[id] = split;
            }
        }

        var lineNumber = 0;
        foreach (var line in File.ReadLines(comparisonsPath))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var comparison = JsonDocument.Parse(line);
            if (!TryGetString(comparison.RootElement, "id", out var id) ||
                !splitsById.TryGetValue(id, out var comparisonSplit) || comparisonSplit != evaluationSplit)
                Add(issues, "comparison-split-mismatch", $"comparisons.line.{lineNumber}", "成对比较引用了所选 evaluation_split 以外或不存在的 gold 样本。");
        }
    }

    private static bool TryObject(JsonElement parent, string propertyName, List<BlindEvaluationManifestIssue> issues, out JsonElement value)
    {
        if (parent.TryGetProperty(propertyName, out value) && value.ValueKind == JsonValueKind.Object) return true;
        Add(issues, "required-object", propertyName, $"缺少对象字段 {propertyName}。");
        return false;
    }

    private static string? RequireString(JsonElement parent, string propertyName, List<BlindEvaluationManifestIssue> issues)
    {
        if (TryGetString(parent, propertyName, out var value) && !string.IsNullOrWhiteSpace(value)) return value.Trim();
        Add(issues, "required-string", propertyName, $"字段 {propertyName} 必须是非空字符串。");
        return null;
    }

    private static string? RequireHash(JsonElement parent, string propertyName, List<BlindEvaluationManifestIssue> issues)
    {
        var value = RequireString(parent, propertyName, issues);
        if (value is not null && !IsSha256(value))
        {
            Add(issues, "invalid-sha256", propertyName, $"字段 {propertyName} 必须是 64 位 SHA-256 十六进制摘要。");
            return null;
        }
        return value;
    }

    private static string? ReadOptionalHash(JsonElement parent, string propertyName, List<BlindEvaluationManifestIssue> issues)
    {
        if (!parent.TryGetProperty(propertyName, out var element))
        {
            Add(issues, "required-field", propertyName, $"缺少字段 {propertyName}；无成对比较时应显式设为 null。");
            return null;
        }
        if (element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind != JsonValueKind.String || !IsSha256(element.GetString() ?? string.Empty))
        {
            Add(issues, "invalid-sha256", propertyName, $"字段 {propertyName} 必须是 64 位 SHA-256 摘要或 null。");
            return null;
        }
        return element.GetString();
    }

    private static void VerifyArtifactHash(string path, string? expectedHash, string field, List<BlindEvaluationManifestIssue> issues)
    {
        if (expectedHash is null) return;
        var actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            Add(issues, "artifact-hash-mismatch", field, $"文件 {field} 的 SHA-256 与清单不一致。");
    }

    private static void ValidateAdmissionReport(string reportPath, List<BlindEvaluationManifestIssue> issues, string? expectedEvaluationSplit)
    {
        using var reportDocument = JsonDocument.Parse(File.ReadAllText(reportPath));
        var report = reportDocument.RootElement;
        if (report.ValueKind != JsonValueKind.Object)
        {
            Add(issues, "report-invalid", "report", "评分报告根节点必须是对象。");
            return;
        }
        if (!report.TryGetProperty("dataset_valid", out var datasetValid) || datasetValid.ValueKind != JsonValueKind.True)
            Add(issues, "dataset-not-valid", "report.dataset_valid", "评分报告必须明确确认盲评数据通过准入审计。");

        TryGetString(report, "admission_protocol_id", out var protocolId);
        TryGetString(report, "admission_protocol_version", out var protocolVersion);
        TryGetString(report, "admission_policy_sha256", out var policyHash);
        if (!string.Equals(protocolId, BlindEvaluationAdmissionProtocol.ProtocolId, StringComparison.Ordinal) ||
            !string.Equals(protocolVersion, BlindEvaluationAdmissionProtocol.Version, StringComparison.Ordinal) ||
            !string.Equals(policyHash, BlindEvaluationAdmissionProtocol.PolicySha256, StringComparison.OrdinalIgnoreCase))
            Add(issues, "admission-policy-mismatch", "report.admission_policy_sha256", "评分报告必须绑定当前盲评准入协议版本和规则哈希。");
        if (expectedEvaluationSplit is not null &&
            (!TryGetString(report, "evaluation_split", out var actualSplit) || !string.Equals(actualSplit, expectedEvaluationSplit, StringComparison.Ordinal)))
            Add(issues, "evaluation-split-mismatch", "report.evaluation_split", "评分报告的 evaluation_split 必须与最终清单一致。");
    }

    private static DateTimeOffset? RequireUtcTimestamp(JsonElement parent, string propertyName, List<BlindEvaluationManifestIssue> issues)
    {
        if (TryGetString(parent, propertyName, out var value) && StrictUtcTimestamp.TryParse(value, out var timestamp)) return timestamp;
        Add(issues, "utc-timestamp", propertyName, $"字段 {propertyName} 必须是带 Z 或 +00:00 的有效 UTC 时间。");
        return null;
    }

    private static int? ReadNonNegativeInt(JsonElement parent, string propertyName, List<BlindEvaluationManifestIssue> issues)
    {
        if (parent.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var number) && number >= 0) return number;
        Add(issues, "non-negative-integer", propertyName, $"字段 {propertyName} 必须是非负整数。");
        return null;
    }

    private static bool? RequireBoolean(JsonElement parent, string propertyName, List<BlindEvaluationManifestIssue> issues)
    {
        if (parent.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return value.GetBoolean();
        Add(issues, "required-boolean", $"execution.{propertyName}", $"字段 {propertyName} 必须是布尔值。");
        return null;
    }

    private static void ValidateOptionalNonNegativeNumber(JsonElement parent, string propertyName, List<BlindEvaluationManifestIssue> issues, string pathPrefix = "candidate.sampling")
    {
        if (!parent.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null) return;
        if (!value.TryGetDouble(out var number) || !double.IsFinite(number) || number < 0)
            Add(issues, "invalid-number", $"{pathPrefix}.{propertyName}", $"{propertyName} 必须为有限非负数或 null。");
    }

    private static void ValidateOptionalProbability(JsonElement parent, string propertyName, List<BlindEvaluationManifestIssue> issues)
    {
        if (!parent.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null) return;
        if (!value.TryGetDouble(out var number) || !double.IsFinite(number) || number is < 0 or > 1)
            Add(issues, "invalid-sampling-value", $"candidate.sampling.{propertyName}", $"{propertyName} 必须在 0 到 1 之间或为 null。");
    }

    private static void ValidateOptionalPositiveInt(JsonElement parent, string propertyName, List<BlindEvaluationManifestIssue> issues)
    {
        if (!parent.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null) return;
        if (!value.TryGetInt32(out var number) || number <= 0)
            Add(issues, "invalid-sampling-value", $"candidate.sampling.{propertyName}", $"{propertyName} 必须为正整数或 null。");
    }

    private static void ValidateMetrics(JsonElement metrics, List<BlindEvaluationManifestIssue> issues, string? provider)
    {
        foreach (var name in new[]
        {
            "schema_valid_rate", "fact_constraint_retention_rate", "direct_usability_rate", "tone_match_rate",
            "clarification_decision_accuracy", "pairwise_preference_rate", "pairwise_confidence_interval_95",
            "pairwise_comparable_count", "pairwise_decisive_family_count", "high_risk_key_fact_reversals", "api_latency_p50_ms", "api_latency_p95_ms",
            "api_token_usage", "local_model_load_ms", "local_tokens_per_second", "local_peak_memory_bytes"
        })
        {
            if (!metrics.TryGetProperty(name, out _))
                Add(issues, "required-metric", $"metrics.{name}", $"metrics 必须显式包含 {name}；不适用的指标可设为 null。");
        }

        foreach (var name in new[]
        {
            "schema_valid_rate", "fact_constraint_retention_rate", "direct_usability_rate", "tone_match_rate",
            "clarification_decision_accuracy", "pairwise_preference_rate"
        })
            ValidateMetricProbability(metrics, name, issues);

        RequireNonNegativeInt(metrics, "high_risk_key_fact_reversals", issues);
        RequireNonNegativeInt(metrics, "pairwise_comparable_count", issues);
        ValidateOptionalNonNegativeInt(metrics, "pairwise_decisive_family_count", issues);
        foreach (var name in new[] { "api_latency_p50_ms", "api_latency_p95_ms", "local_latency_p50_ms", "local_latency_p95_ms", "local_model_load_ms", "local_tokens_per_second" })
            ValidateOptionalNonNegativeNumber(metrics, name, issues, "metrics");
        ValidateOptionalNonNegativeLong(metrics, "local_peak_memory_bytes", issues);
        ValidateConfidenceInterval(metrics, issues);
        ValidateApiTokenUsage(metrics, issues);
        ValidateApiCostAccounting(metrics, issues, provider);
    }

    private static void ValidateApiCostAccounting(JsonElement metrics, List<BlindEvaluationManifestIssue> issues, string? provider)
    {
        if (!metrics.TryGetProperty("api_cost_accounting", out var accounting)) return;
        const string field = "metrics.api_cost_accounting";
        if (accounting.ValueKind != JsonValueKind.Object)
        {
            Add(issues, "api-cost-accounting", field, "api_cost_accounting 必须是对象；没有可报告金额时仍应记录明确状态。");
            return;
        }

        var status = RequireString(accounting, "status", issues);
        var providerKind = provider?.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if ((string.Equals(providerKind, "Local", StringComparison.OrdinalIgnoreCase) && status is not "not_applicable") ||
            (string.Equals(providerKind, "Cloud", StringComparison.OrdinalIgnoreCase) && status is "not_applicable"))
            Add(issues, "api-cost-provider", field + ".status", "费用状态必须与候选 Provider 一致：本地推理为 not_applicable，云 Provider 不得标记 not_applicable。");
        var amount = accounting.TryGetProperty("amount", out var amountElement) && amountElement.ValueKind == JsonValueKind.Number &&
            amountElement.TryGetDouble(out var parsedAmount) && double.IsFinite(parsedAmount) && parsedAmount >= 0
            ? parsedAmount
            : (double?)null;
        if (amount is null && (!accounting.TryGetProperty("amount", out amountElement) || amountElement.ValueKind != JsonValueKind.Null))
            Add(issues, "api-cost-amount", field + ".amount", "amount 必须是非负有限数或 null。");

        var currency = ReadNullableText(accounting, "currency", field + ".currency", issues);
        if (currency is not null && (currency.Length != 3 || currency.Any(character => character is < 'A' or > 'Z')))
            Add(issues, "api-cost-currency", field + ".currency", "currency 必须是 3 位大写货币代码或 null。");
        var basisKind = ReadNullableText(accounting, "basis_kind", field + ".basis_kind", issues);
        var basisReference = ReadNullableText(accounting, "basis_ref", field + ".basis_ref", issues);
        var basisHash = ReadNullableText(accounting, "basis_sha256", field + ".basis_sha256", issues);
        var assessedAt = ReadNullableText(accounting, "assessed_at_utc", field + ".assessed_at_utc", issues);

        var noCost = status is "not_reported" or "not_applicable";
        if (noCost)
        {
            if (amount is not null || currency is not null || basisKind is not null || basisReference is not null || basisHash is not null || assessedAt is not null)
                Add(issues, "api-cost-state", field, "not_reported/not_applicable 状态的金额与依据字段必须为 null。");
            return;
        }

        var expectedBasisKind = status switch
        {
            "estimated" => "provider_rate_card",
            "provider_billed" => "provider_billing_record",
            _ => null
        };
        if (expectedBasisKind is null)
        {
            Add(issues, "api-cost-status", field + ".status", "status 必须是 not_reported、not_applicable、estimated 或 provider_billed。");
            return;
        }
        if (amount is null || currency is null || basisKind != expectedBasisKind || basisReference is null ||
            !CostEvidenceReference.IsMatch(basisReference) || basisHash is null || !IsSha256(basisHash) || !IsUtcTimestamp(assessedAt))
            Add(issues, "api-cost-evidence", field, "估算或账单金额必须绑定货币、匹配的定价/账单依据类型、不透明依据引用、SHA-256 和 UTC 核对时间。");
    }

    private static string? ReadNullableText(JsonElement parent, string property, string field, List<BlindEvaluationManifestIssue> issues)
    {
        if (!parent.TryGetProperty(property, out var value))
        {
            Add(issues, "api-cost-field", field, "api_cost_accounting 必须显式包含该字段；无值时使用 null。");
            return null;
        }
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            Add(issues, "api-cost-field", field, "字段必须是非空字符串或 null。");
            return null;
        }
        return value.GetString();
    }

    private static bool IsUtcTimestamp(string? value) => StrictUtcTimestamp.TryParse(value, out _);

    private static void ValidateMetricProbability(JsonElement parent, string propertyName, List<BlindEvaluationManifestIssue> issues)
    {
        if (!parent.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null) return;
        if (!value.TryGetDouble(out var number) || !double.IsFinite(number) || number is < 0 or > 1)
            Add(issues, "invalid-metric", $"metrics.{propertyName}", $"{propertyName} 必须在 0 到 1 之间或为 null。");
    }

    private static void ValidateConfidenceInterval(JsonElement metrics, List<BlindEvaluationManifestIssue> issues)
    {
        if (!metrics.TryGetProperty("pairwise_confidence_interval_95", out var interval) || interval.ValueKind == JsonValueKind.Null) return;
        if (interval.ValueKind != JsonValueKind.Array || interval.GetArrayLength() != 2)
        {
            Add(issues, "invalid-confidence-interval", "metrics.pairwise_confidence_interval_95", "成对比较置信区间必须是 [下界, 上界] 或 null。");
            return;
        }
        var values = interval.EnumerateArray().ToArray();
        if (!values[0].TryGetDouble(out var lower) || !values[1].TryGetDouble(out var upper) ||
            !double.IsFinite(lower) || !double.IsFinite(upper) || lower is < 0 or > 1 || upper is < 0 or > 1 || lower > upper)
            Add(issues, "invalid-confidence-interval", "metrics.pairwise_confidence_interval_95", "置信区间必须满足 0 ≤ 下界 ≤ 上界 ≤ 1。");
    }

    private static void ValidateApiTokenUsage(JsonElement metrics, List<BlindEvaluationManifestIssue> issues)
    {
        if (!metrics.TryGetProperty("api_token_usage", out var usage) || usage.ValueKind == JsonValueKind.Null) return;
        if (!TryApiTokenUsage(usage, issues, out var tokenUsage)) return;
        RequireNonNegativeLong(tokenUsage, "input_tokens", issues, "metrics.api_token_usage");
        RequireNonNegativeLong(tokenUsage, "output_tokens", issues, "metrics.api_token_usage");
        ValidateOptionalNonNegativeLong(tokenUsage, "cache_read_input_tokens", issues, "metrics.api_token_usage");
        ValidateOptionalNonNegativeLong(tokenUsage, "cache_creation_input_tokens", issues, "metrics.api_token_usage");
    }

    private static bool TryApiTokenUsage(JsonElement value, List<BlindEvaluationManifestIssue> issues, out JsonElement tokenUsage)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (!value.TryGetProperty("input_tokens", out _) || !value.TryGetProperty("output_tokens", out _))
            {
                Add(issues, "api-token-usage", "metrics.api_token_usage", "api_token_usage 必须包含 input_tokens 和 output_tokens。");
                tokenUsage = default;
                return false;
            }
            tokenUsage = value;
            return true;
        }
        tokenUsage = default;
        Add(issues, "api-token-usage", "metrics.api_token_usage", "api_token_usage 必须是包含 input_tokens/output_tokens 的对象或 null。");
        return false;
    }

    private static int? RequireNonNegativeInt(JsonElement parent, string propertyName, List<BlindEvaluationManifestIssue> issues)
    {
        if (parent.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var number) && number >= 0) return number;
        Add(issues, "non-negative-integer", $"metrics.{propertyName}", $"{propertyName} 必须是非负整数。");
        return null;
    }

    private static void ValidateOptionalNonNegativeLong(JsonElement parent, string propertyName, List<BlindEvaluationManifestIssue> issues, string pathPrefix = "metrics")
    {
        if (!parent.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null) return;
        if (!value.TryGetInt64(out var number) || number < 0)
            Add(issues, "invalid-metric", $"{pathPrefix}.{propertyName}", $"{propertyName} 必须是非负整数或 null。");
    }

    private static void ValidateOptionalNonNegativeInt(JsonElement parent, string propertyName, List<BlindEvaluationManifestIssue> issues)
    {
        if (!parent.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null) return;
        if (!value.TryGetInt32(out var number) || number < 0)
            Add(issues, "invalid-metric", $"metrics.{propertyName}", $"{propertyName} 必须是非负整数或 null。");
    }

    private static bool TryGetString(JsonElement parent, string propertyName, out string value)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString() ?? string.Empty;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(character => Uri.IsHexDigit(character));

    private static void Add(List<BlindEvaluationManifestIssue> issues, string code, string field, string message) =>
        issues.Add(new(code, field, message));
}
