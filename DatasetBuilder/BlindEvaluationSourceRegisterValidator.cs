using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Huaxiazi.DatasetBuilder;

public sealed record BlindEvaluationSourceRegisterIssue(string Code, string Field, string? RecordId, string Message);

/// <summary>
/// Checks source-register structure and cross-references against blind-evaluation records.
/// It cannot authenticate controlled-vault artifacts, rights, identities, or review independence.
/// </summary>
public sealed record BlindEvaluationSourceRegisterReport(
    bool RecordIntegrityValid,
    bool SourcesCovered,
    bool RequiredUsesClaimed,
    bool RightsReviewClaimComplete,
    bool ExternalEvidenceAuthenticityVerifiedByTool,
    bool Phase0GatePassed,
    int SourceCount,
    int DatasetRecordCount,
    IReadOnlyList<BlindEvaluationSourceRegisterIssue> Issues);

public static class BlindEvaluationSourceRegisterValidator
{
    private static readonly Regex OpaqueReference = new("^[A-Za-z0-9][A-Za-z0-9._:-]{2,127}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Sha256 = new("^[A-Fa-f0-9]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> SourceKinds = new(StringComparer.Ordinal)
    {
        "project_owned", "licensed", "user_authorized_deidentified"
    };
    private static readonly HashSet<string> PermittedUses = new(StringComparer.Ordinal)
    {
        "candidate_inference", "human_review", "aggregate_reporting", "training"
    };
    private static readonly HashSet<string> RootFields = new(StringComparer.Ordinal)
    {
        "register_version", "sources"
    };
    private static readonly HashSet<string> SourceFields = new(StringComparer.Ordinal)
    {
        "source_ref", "source_kind", "rights_basis", "source_record_sha256", "permitted_uses", "valid_from", "valid_until",
        "evidence", "deidentification_review", "retention", "review_status", "rights_review"
    };

    public static BlindEvaluationSourceRegisterReport Validate(string registerJson, string datasetJsonl)
    {
        ArgumentNullException.ThrowIfNull(registerJson);
        ArgumentNullException.ThrowIfNull(datasetJsonl);
        var issues = new List<BlindEvaluationSourceRegisterIssue>();
        using var registerDocument = JsonDocument.Parse(registerJson);
        var root = registerDocument.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("来源登记根节点必须是 JSON 对象。");

        CheckAllowedFields(root, RootFields, "register", issues);
        var version = ReadString(root, "register_version", "register_version", null, issues);
        if (version is not null && version != "1.0")
            Add(issues, "register-version", "register_version", null, "register_version 必须是 1.0。");

        var entries = new Dictionary<string, SourceEntry>(StringComparer.Ordinal);
        if (!root.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Array)
        {
            Add(issues, "sources-required", "sources", null, "sources 必须是来源对象数组。");
        }
        else
        {
            var index = 0;
            foreach (var element in sources.EnumerateArray())
            {
                var field = $"sources[{index++}]";
                if (element.ValueKind != JsonValueKind.Object)
                {
                    Add(issues, "source-entry-type", field, null, "来源条目必须是对象。");
                    continue;
                }
                CheckAllowedFields(element, SourceFields, field, issues);
                var entry = ReadSourceEntry(element, field, issues);
                if (entry is null) continue;
                if (!entries.TryAdd(entry.SourceRef, entry))
                    Add(issues, "duplicate-source-ref", field + ".source_ref", null, "source_ref 必须唯一。");
            }
        }

        var referencedSourceRefs = new HashSet<string>(StringComparer.Ordinal);
        var recordCount = 0;
        var sourceCoverageComplete = true;
        using (var reader = new StringReader(datasetJsonl))
        {
            string? line;
            var lineNumber = 0;
            while ((line = reader.ReadLine()) is not null)
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line))
                {
                    Add(issues, "dataset-empty-line", $"dataset.line[{lineNumber}]", null, "盲评 JSONL 不允许空行。");
                    continue;
                }

                using var recordDocument = JsonDocument.Parse(line);
                var record = recordDocument.RootElement;
                var recordId = record.ValueKind == JsonValueKind.Object && TryGetString(record, "id", out var id) ? id : null;
                if (record.ValueKind != JsonValueKind.Object)
                {
                    Add(issues, "dataset-record-type", $"dataset.line[{lineNumber}]", recordId, "盲评记录必须是 JSON 对象。");
                    continue;
                }
                recordCount++;
                if (string.IsNullOrWhiteSpace(recordId))
                    Add(issues, "dataset-record-id", $"dataset.line[{lineNumber}].id", null, "盲评记录缺少非空 id。");

                if (!record.TryGetProperty("source", out var recordSource) || recordSource.ValueKind != JsonValueKind.Object)
                {
                    Add(issues, "dataset-source-required", "source", recordId, "盲评记录缺少 source 对象。");
                    sourceCoverageComplete = false;
                    continue;
                }
                var sourceRef = ReadString(recordSource, "license_or_consent_ref", "source.license_or_consent_ref", recordId, issues);
                var sourceKind = ReadString(recordSource, "source_kind", "source.source_kind", recordId, issues);
                var sampleSourceHash = ReadOptionalString(recordSource, "source_record_hash", "source.source_record_hash", recordId, issues);
                if (sampleSourceHash is not null && !Sha256.IsMatch(sampleSourceHash))
                    Add(issues, "source-record-hash", "source.source_record_hash", recordId, "source_record_hash 必须是 64 位 SHA-256 十六进制值。");
                if (sourceKind is null) sourceCoverageComplete = false;
                if (sourceRef is null)
                {
                    sourceCoverageComplete = false;
                    continue;
                }
                referencedSourceRefs.Add(sourceRef);
                if (!entries.TryGetValue(sourceRef, out var registered))
                {
                    Add(issues, "source-not-registered", "source.license_or_consent_ref", recordId, "盲评记录引用的 source_ref 不在来源登记中。");
                    sourceCoverageComplete = false;
                    continue;
                }
                if (sourceKind is not null && sourceKind != registered.SourceKind)
                {
                    Add(issues, "source-kind-mismatch", "source.source_kind", recordId, "盲评记录 source_kind 与来源登记不一致。");
                    sourceCoverageComplete = false;
                }
                if (sampleSourceHash is not null && !string.Equals(registered.SourceRecordSha256, sampleSourceHash, StringComparison.OrdinalIgnoreCase))
                    Add(issues, "source-record-hash-mismatch", "source.source_record_hash", recordId, "样本 source_record_hash 与来源登记的 source_record_sha256 不一致。");
            }
        }

        if (recordCount == 0)
            Add(issues, "dataset-empty", "dataset", null, "盲评数据集不能为空。");

        var sourcesCovered = recordCount > 0 && sourceCoverageComplete;
        var usedEntries = referencedSourceRefs.Where(entries.ContainsKey).Select(reference => entries[reference]).ToArray();
        var requiredUsesClaimed = usedEntries.Length == referencedSourceRefs.Count && usedEntries.Length > 0 &&
            usedEntries.All(entry => entry.PermittedUses.Contains("candidate_inference") && entry.PermittedUses.Contains("human_review") && entry.IsWithinValidityWindow);
        var rightsReviewClaimComplete = usedEntries.Length == referencedSourceRefs.Count && usedEntries.Length > 0 &&
            usedEntries.All(entry => entry.ReviewStatus == "verified" && entry.RightsReviewComplete && entry.DeidentificationApproved);

        return new(
            RecordIntegrityValid: issues.Count == 0,
            SourcesCovered: sourcesCovered,
            RequiredUsesClaimed: requiredUsesClaimed,
            RightsReviewClaimComplete: rightsReviewClaimComplete,
            ExternalEvidenceAuthenticityVerifiedByTool: false,
            Phase0GatePassed: false,
            SourceCount: entries.Count,
            DatasetRecordCount: recordCount,
            Issues: issues);
    }

    private static SourceEntry? ReadSourceEntry(JsonElement source, string field, List<BlindEvaluationSourceRegisterIssue> issues)
    {
        var sourceRef = ReadString(source, "source_ref", field + ".source_ref", null, issues);
        if (sourceRef is null || !OpaqueReference.IsMatch(sourceRef))
            Add(issues, "opaque-reference", field + ".source_ref", null, "source_ref 必须是不含个人信息、URL 或路径的不透明引用。");

        var sourceKind = ReadString(source, "source_kind", field + ".source_kind", null, issues);
        if (sourceKind is not null && !SourceKinds.Contains(sourceKind))
            Add(issues, "source-kind", field + ".source_kind", null, "source_kind 不受支持。");
        var rightsBasis = ReadString(source, "rights_basis", field + ".rights_basis", null, issues);
        if (rightsBasis is not null && rightsBasis is not ("project_owned" or "license" or "explicit_consent"))
            Add(issues, "rights-basis", field + ".rights_basis", null, "rights_basis 不受支持。");
        var expectedBasis = sourceKind switch
        {
            "project_owned" => "project_owned",
            "licensed" => "license",
            "user_authorized_deidentified" => "explicit_consent",
            _ => null
        };
        if (expectedBasis is not null && rightsBasis != expectedBasis)
            Add(issues, "rights-basis-mismatch", field + ".rights_basis", null, "rights_basis 与 source_kind 不一致。");
        var sourceRecordSha256 = ReadOptionalString(source, "source_record_sha256", field + ".source_record_sha256", null, issues);
        if (sourceRecordSha256 is not null && !Sha256.IsMatch(sourceRecordSha256))
            Add(issues, "source-record-hash", field + ".source_record_sha256", null, "source_record_sha256 必须是 64 位 SHA-256 十六进制值。");

        var uses = ReadUses(source, field, issues);
        var validFromText = ReadString(source, "valid_from", field + ".valid_from", null, issues);
        var validFrom = ParseDate(validFromText, field + ".valid_from", issues);
        var validUntilText = ReadNullableString(source, "valid_until", field + ".valid_until", issues);
        var validUntil = ParseOptionalDate(validUntilText, field + ".valid_until", issues);
        if (validFrom is { } start && validUntil is { } end && end < start)
            Add(issues, "validity-range", field + ".valid_until", null, "授权到期日期不能早于生效日期。");

        ValidateEvidence(source, field, issues);
        var deidentificationApproved = ValidateDeidentification(source, field, issues);
        ValidateRetention(source, field, issues);
        var reviewStatus = ReadString(source, "review_status", field + ".review_status", null, issues);
        if (reviewStatus is not null && reviewStatus is not ("not_verified" or "verified" or "rejected"))
            Add(issues, "review-status", field + ".review_status", null, "review_status 必须是 not_verified、verified 或 rejected。");
        var rightsReviewComplete = ValidateRightsReview(source, field, reviewStatus, issues);

        if (sourceRef is null || sourceKind is null) return null;
        var isValid = validFrom is { } from && from <= DateOnly.FromDateTime(DateTime.UtcNow) &&
            (validUntil is null || validUntil >= DateOnly.FromDateTime(DateTime.UtcNow));
        return new(sourceRef, sourceKind, sourceRecordSha256, uses, reviewStatus ?? "not_verified", rightsReviewComplete,
            deidentificationApproved, isValid);
    }

    private static HashSet<string> ReadUses(JsonElement source, string field, List<BlindEvaluationSourceRegisterIssue> issues)
    {
        var uses = new HashSet<string>(StringComparer.Ordinal);
        if (!source.TryGetProperty("permitted_uses", out var element) || element.ValueKind != JsonValueKind.Array)
        {
            Add(issues, "permitted-uses", field + ".permitted_uses", null, "permitted_uses 必须是数组。");
            return uses;
        }
        foreach (var use in element.EnumerateArray())
        {
            if (use.ValueKind != JsonValueKind.String || use.GetString() is not { } value || !PermittedUses.Contains(value))
            {
                Add(issues, "permitted-use", field + ".permitted_uses", null, "permitted_uses 含不受支持的用途。");
                continue;
            }
            if (!uses.Add(value)) Add(issues, "duplicate-permitted-use", field + ".permitted_uses", null, "permitted_uses 不能重复。");
        }
        return uses;
    }

    private static void ValidateEvidence(JsonElement source, string field, List<BlindEvaluationSourceRegisterIssue> issues)
    {
        if (!source.TryGetProperty("evidence", out var evidence) || evidence.ValueKind != JsonValueKind.Object)
        {
            Add(issues, "evidence-required", field + ".evidence", null, "evidence 必须是受控证据对象。");
            return;
        }
        var reference = ReadString(evidence, "vault_ref", field + ".evidence.vault_ref", null, issues);
        if (reference is null || !OpaqueReference.IsMatch(reference))
            Add(issues, "opaque-reference", field + ".evidence.vault_ref", null, "vault_ref 必须是不透明引用，不能是 URL 或文件系统路径。");
        var hash = ReadString(evidence, "sha256", field + ".evidence.sha256", null, issues);
        if (hash is null || !Sha256.IsMatch(hash)) Add(issues, "sha256", field + ".evidence.sha256", null, "证据摘要必须是 64 位 SHA-256 十六进制值。");
        CheckAllowedFields(evidence, ["vault_ref", "sha256"], field + ".evidence", issues);
    }

    private static bool ValidateDeidentification(JsonElement source, string field, List<BlindEvaluationSourceRegisterIssue> issues)
    {
        if (!source.TryGetProperty("deidentification_review", out var review) || review.ValueKind != JsonValueKind.Object)
        {
            Add(issues, "deidentification-review-required", field + ".deidentification_review", null, "deidentification_review 必须是对象。");
            return false;
        }
        CheckAllowedFields(review, ["status", "outcome", "reviewer_role_ref", "reviewed_at", "method_summary"], field + ".deidentification_review", issues);
        var status = ReadString(review, "status", field + ".deidentification_review.status", null, issues);
        var outcome = ReadString(review, "outcome", field + ".deidentification_review.outcome", null, issues);
        if (status is not null && status is not ("not_reviewed" or "reviewed"))
            Add(issues, "deidentification-status", field + ".deidentification_review.status", null, "脱敏复核状态无效。");
        if (outcome is not null && outcome is not ("pending" or "approved" or "rejected"))
            Add(issues, "deidentification-outcome", field + ".deidentification_review.outcome", null, "脱敏复核结论无效。");
        ValidateOpaqueField(review, "reviewer_role_ref", field + ".deidentification_review.reviewer_role_ref", issues);
        var reviewedAt = ReadNullableString(review, "reviewed_at", field + ".deidentification_review.reviewed_at", issues);
        if (status == "reviewed" && !IsUtcTimestamp(reviewedAt))
            Add(issues, "deidentification-time", field + ".deidentification_review.reviewed_at", null, "reviewed 状态必须包含合法 UTC 时间。");
        var methodSummary = ReadString(review, "method_summary", field + ".deidentification_review.method_summary", null, issues);
        if (methodSummary?.Length > 500)
            Add(issues, "deidentification-method-length", field + ".deidentification_review.method_summary", null, "脱敏方法摘要不得超过 500 个字符。");
        if (BlindEvaluationAuditor.ContainsPotentialSensitiveData(methodSummary))
            Add(issues, "sensitive-content", field + ".deidentification_review.method_summary", null,
                "脱敏方法摘要中检测到疑似个人信息或密钥；启发式检查不能替代人工复核。");
        if ((outcome == "approved" || outcome == "rejected") && status != "reviewed")
            Add(issues, "deidentification-state", field + ".deidentification_review", null, "已批准或拒绝的脱敏结论必须标记为 reviewed。");
        return status == "reviewed" && outcome == "approved" && IsUtcTimestamp(reviewedAt);
    }

    private static void ValidateRetention(JsonElement source, string field, List<BlindEvaluationSourceRegisterIssue> issues)
    {
        if (!source.TryGetProperty("retention", out var retention) || retention.ValueKind != JsonValueKind.Object)
        {
            Add(issues, "retention-required", field + ".retention", null, "retention 必须明确保留期限和撤回路径。");
            return;
        }
        CheckAllowedFields(retention, ["expires_at", "withdrawal_contact_role_ref", "withdrawal_route_ref"], field + ".retention", issues);
        var expiresAt = ReadNullableString(retention, "expires_at", field + ".retention.expires_at", issues);
        ParseOptionalDate(expiresAt, field + ".retention.expires_at", issues);
        ValidateOpaqueField(retention, "withdrawal_contact_role_ref", field + ".retention.withdrawal_contact_role_ref", issues);
        ValidateOpaqueField(retention, "withdrawal_route_ref", field + ".retention.withdrawal_route_ref", issues);
    }

    private static bool ValidateRightsReview(JsonElement source, string field, string? reviewStatus, List<BlindEvaluationSourceRegisterIssue> issues)
    {
        if (!source.TryGetProperty("rights_review", out var review))
        {
            Add(issues, "rights-review-required", field + ".rights_review", null, "必须显式声明 rights_review；未复核时使用 null。");
            return false;
        }
        if (review.ValueKind == JsonValueKind.Null)
        {
            if (reviewStatus == "verified")
                Add(issues, "verified-review-incomplete", field + ".rights_review", null, "verified 状态必须包含权利复核记录。");
            return false;
        }
        if (review.ValueKind != JsonValueKind.Object)
        {
            Add(issues, "rights-review-type", field + ".rights_review", null, "rights_review 必须是对象或 null。");
            return false;
        }
        CheckAllowedFields(review, ["reviewer_role_ref", "reviewed_at", "evidence_ref", "scope_matches", "exception_ref"], field + ".rights_review", issues);
        if (review.TryGetProperty("exception_ref", out var exceptionRef) &&
            (exceptionRef.ValueKind != JsonValueKind.String || !OpaqueReference.IsMatch(exceptionRef.GetString() ?? string.Empty)))
            Add(issues, "opaque-reference", field + ".rights_review.exception_ref", null, "exception_ref 必须是不透明引用。");
        var hasRequired = true;
        foreach (var property in new[] { "reviewer_role_ref", "reviewed_at", "evidence_ref", "scope_matches" })
            if (!review.TryGetProperty(property, out _)) hasRequired = false;
        ValidateOpaqueField(review, "reviewer_role_ref", field + ".rights_review.reviewer_role_ref", issues);
        var reviewedAt = ReadString(review, "reviewed_at", field + ".rights_review.reviewed_at", null, issues);
        var evidenceRef = ReadString(review, "evidence_ref", field + ".rights_review.evidence_ref", null, issues);
        if (!IsUtcTimestamp(reviewedAt)) Add(issues, "rights-review-time", field + ".rights_review.reviewed_at", null, "权利复核必须包含合法 UTC 时间。");
        if (evidenceRef is null || !OpaqueReference.IsMatch(evidenceRef))
            Add(issues, "opaque-reference", field + ".rights_review.evidence_ref", null, "权利复核证据必须是受控不透明引用。");
        var scopeMatches = review.TryGetProperty("scope_matches", out var scope) && scope.ValueKind == JsonValueKind.True;
        if (review.TryGetProperty("scope_matches", out scope) && scope.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            Add(issues, "rights-review-scope", field + ".rights_review.scope_matches", null, "scope_matches 必须是布尔值。");
        if (reviewStatus == "verified" && (!hasRequired || !scopeMatches || !IsUtcTimestamp(reviewedAt) || evidenceRef is null || !OpaqueReference.IsMatch(evidenceRef)))
            Add(issues, "verified-review-incomplete", field + ".rights_review", null, "verified 状态必须有完整受控复核引用、UTC 时间和 scope_matches=true。");
        return hasRequired && scopeMatches && IsUtcTimestamp(reviewedAt) && evidenceRef is not null && OpaqueReference.IsMatch(evidenceRef);
    }

    private static DateOnly? ParseDate(string? value, string field, List<BlindEvaluationSourceRegisterIssue> issues)
    {
        if (value is not null && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return parsed;
        Add(issues, "date-format", field, null, "日期必须使用 yyyy-MM-dd 格式。");
        return null;
    }

    private static DateOnly? ParseOptionalDate(string? value, string field, List<BlindEvaluationSourceRegisterIssue> issues)
    {
        if (value is null) return null;
        return ParseDate(value, field, issues);
    }

    private static bool IsUtcTimestamp(string? value) => StrictUtcTimestamp.TryParse(value, out _);

    private static string? ReadString(JsonElement parent, string property, string field, string? recordId, List<BlindEvaluationSourceRegisterIssue> issues)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            Add(issues, "required-string", field, recordId, "字段必须是非空字符串。");
            return null;
        }
        return value.GetString();
    }

    private static string? ReadNullableString(JsonElement parent, string property, string field, List<BlindEvaluationSourceRegisterIssue> issues)
    {
        if (!parent.TryGetProperty(property, out var value))
        {
            Add(issues, "required-field", field, null, "必须显式提供该字段；无值时使用 null。");
            return null;
        }
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            Add(issues, "nullable-string", field, null, "字段必须是非空字符串或 null。");
            return null;
        }
        return value.GetString();
    }

    private static string? ReadOptionalString(JsonElement parent, string property, string field, string? recordId, List<BlindEvaluationSourceRegisterIssue> issues)
    {
        if (!parent.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            Add(issues, "optional-string", field, recordId, "字段必须是非空字符串（如提供）或省略。");
            return null;
        }
        return value.GetString();
    }

    private static void ValidateOpaqueField(JsonElement parent, string property, string field, List<BlindEvaluationSourceRegisterIssue> issues)
    {
        var value = ReadString(parent, property, field, null, issues);
        if (value is null || !OpaqueReference.IsMatch(value))
            Add(issues, "opaque-reference", field, null, "字段必须是不含个人信息、URL 或路径的不透明引用。");
    }

    private static void CheckAllowedFields(JsonElement element, IEnumerable<string> allowed, string field, List<BlindEvaluationSourceRegisterIssue> issues)
    {
        var allowlist = allowed.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name)) Add(issues, "duplicate-json-property", field + "." + property.Name, null, "JSON 对象中不允许重复字段。");
            if (!allowlist.Contains(property.Name)) Add(issues, "unknown-field", field + "." + property.Name, null, "来源登记包含未定义字段。");
        }
    }

    private static bool TryGetString(JsonElement element, string property, out string value)
    {
        if (element.TryGetProperty(property, out var propertyValue) && propertyValue.ValueKind == JsonValueKind.String)
        {
            value = propertyValue.GetString()!;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static void Add(List<BlindEvaluationSourceRegisterIssue> issues, string code, string field, string? recordId, string message) =>
        issues.Add(new(code, field, recordId, message));

    private sealed record SourceEntry(
        string SourceRef,
        string SourceKind,
        string? SourceRecordSha256,
        HashSet<string> PermittedUses,
        string ReviewStatus,
        bool RightsReviewComplete,
        bool DeidentificationApproved,
        bool IsWithinValidityWindow);
}
