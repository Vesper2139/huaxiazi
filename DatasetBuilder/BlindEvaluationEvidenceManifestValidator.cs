using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Huaxiazi.DatasetBuilder;

public sealed record BlindEvaluationEvidenceIssue(string Code, string Field, string? RecordId, string Message);

/// <summary>
/// Reports structural consistency of the controlled evidence index. It cannot authenticate
/// vault artifacts, reviewer identities, rights, independence, or trusted timestamps.
/// </summary>
public sealed record BlindEvaluationEvidenceManifestReport(
    bool Valid,
    bool ExternalEvidenceVerified,
    int RecordCount,
    string DatasetSha256,
    string AuthorizationReviewStatus,
    string ReviewerIndependenceReviewStatus,
    IReadOnlyList<BlindEvaluationEvidenceIssue> Issues);

public static class BlindEvaluationEvidenceManifestValidator
{
    private static readonly Regex ArtifactReference = new(@"^vault:[A-Za-z0-9][A-Za-z0-9._:-]{2,159}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RoleReference = new(@"^role:[A-Za-z0-9][A-Za-z0-9._-]{1,119}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ReviewerReference = new(@"^(reviewer:)?[A-Za-z0-9][A-Za-z0-9._-]{0,119}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SourceReference = new(@"^(source:)?[A-Za-z0-9][A-Za-z0-9._:-]{0,119}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ExceptionReference = new(@"^exception:[A-Za-z0-9][A-Za-z0-9._-]{1,119}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> RootFields = Fields("manifest_version", "status", "dataset", "handbook", "authorization_review", "reviewer_independence_review", "record_evidence", "dataset_admission", "exceptions", "custody");
    private static readonly HashSet<string> DatasetFields = Fields("artifact_ref", "sha256", "sample_count", "schema_version", "admission_protocol_id", "admission_protocol_version");
    private static readonly HashSet<string> HandbookFields = Fields("artifact_ref", "version", "sha256");
    private static readonly HashSet<string> ReviewFields = Fields("status", "verified_at_utc", "reviewer_role_ref", "evidence");
    private static readonly HashSet<string> OpaqueEvidenceFields = Fields("artifact_ref", "sha256", "purpose");
    private static readonly HashSet<string> RecordEvidenceFields = Fields("record_id", "source_ref", "authorization_artifact_ref", "authorization_sha256", "blind_package_artifact_ref", "blind_package_version", "blind_package_sha256", "review_submissions", "adjudication");
    private static readonly HashSet<string> SubmissionFields = Fields("reviewer_ref", "artifact_ref", "sha256", "submitted_at_utc", "blind_package_sha256");
    private static readonly HashSet<string> AdjudicationFields = Fields("adjudicator_ref", "artifact_ref", "sha256", "submitted_at_utc", "blind_package_sha256");
    private static readonly HashSet<string> AdmissionFields = Fields("status", "report_artifact_ref", "report_sha256");
    private static readonly HashSet<string> CustodyFields = Fields("evidence_store_ref", "sealed_at_utc", "sealed_by_role_ref", "signature");
    private static readonly HashSet<string> ExceptionFields = Fields("exception_ref", "category", "status", "evidence_ref", "evidence_sha256", "recorded_at_utc", "owner_role_ref");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static BlindEvaluationEvidenceManifestReport Validate(string manifestJson, string datasetPath)
    {
        ArgumentNullException.ThrowIfNull(manifestJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetPath);
        var issues = new List<BlindEvaluationEvidenceIssue>();
        using var manifestDocument = JsonDocument.Parse(manifestJson);
        var root = manifestDocument.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("证据清单根节点必须是 JSON 对象。");

        ValidateNoDuplicateProperties(root, string.Empty, issues);
        ValidateManifestShape(root, issues);
        RequireString(root, "manifest_version", "manifest_version", issues, expected: "huaxiazi-blind-evidence-manifest-v1");
        var manifestStatus = RequireString(root, "status", "status", issues);
        if (manifestStatus is not ("template_not_verified" or "submitted_for_review" or "reviewed" or "rejected"))
            Add(issues, "manifest-status", "status", null, "status 必须是 template_not_verified、submitted_for_review、reviewed 或 rejected。");
        var custody = RequireObject(root, "custody", "custody", issues);
        if (custody is { } custodyObject)
        {
            RequireArtifactReference(custodyObject, "evidence_store_ref", "custody.evidence_store_ref", issues);
            ValidateOptionalUtcTimestamp(custodyObject, "sealed_at_utc", "custody.sealed_at_utc", issues);
            ValidateOptionalRoleReference(custodyObject, "sealed_by_role_ref", "custody.sealed_by_role_ref", issues);
            if (!custodyObject.TryGetProperty("signature", out var signature))
                Add(issues, "required-field", "custody.signature", null, "必须显式提供 signature；未实现受信签名验证时使用 null。");
            else if (signature.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
                Add(issues, "field-type", "custody.signature", null, "signature 必须是对象或 null。");
        }
        var datasetBytes = File.ReadAllBytes(datasetPath);
        var datasetHash = Convert.ToHexString(SHA256.HashData(datasetBytes)).ToLowerInvariant();
        var records = ReadDataset(datasetBytes);
        var dataset = RequireObject(root, "dataset", "dataset", issues);
        if (dataset is { } datasetObject)
        {
            RequireArtifactReference(datasetObject, "artifact_ref", "dataset.artifact_ref", issues);
            RequireHash(datasetObject, "sha256", "dataset.sha256", issues, datasetHash);
            RequireInteger(datasetObject, "sample_count", "dataset.sample_count", issues, records.Length);
            RequireString(datasetObject, "schema_version", "dataset.schema_version", issues, expected: BlindEvaluationAdmissionProtocol.SchemaVersion);
            RequireString(datasetObject, "admission_protocol_id", "dataset.admission_protocol_id", issues, expected: BlindEvaluationAdmissionProtocol.ProtocolId);
            RequireString(datasetObject, "admission_protocol_version", "dataset.admission_protocol_version", issues, expected: BlindEvaluationAdmissionProtocol.Version);
        }

        var handbook = RequireObject(root, "handbook", "handbook", issues);
        if (handbook is { } handbookObject)
        {
            RequireArtifactReference(handbookObject, "artifact_ref", "handbook.artifact_ref", issues);
            RequireString(handbookObject, "version", "handbook.version", issues);
            RequireHash(handbookObject, "sha256", "handbook.sha256", issues);
        }

        var authorizationStatus = ValidateReview(root, "authorization_review", "authorization", issues);
        var independenceStatus = ValidateReview(root, "reviewer_independence_review", "reviewer_independence", issues);
        ValidateRecordEvidence(root, records, issues);
        var admissionStatus = ValidateAdmissionSnapshot(root, issues);
        ValidateExceptions(root, issues);
        if (manifestStatus == "reviewed")
        {
            if (authorizationStatus != "verified" || independenceStatus != "verified" || admissionStatus != "passed" ||
                custody is not { } sealedCustody ||
                !sealedCustody.TryGetProperty("sealed_at_utc", out var sealedAt) || sealedAt.ValueKind != JsonValueKind.String ||
                !sealedCustody.TryGetProperty("sealed_by_role_ref", out var sealedBy) || sealedBy.ValueKind != JsonValueKind.String ||
                !sealedCustody.TryGetProperty("signature", out var signature) || signature.ValueKind != JsonValueKind.Object)
                Add(issues, "reviewed-state-incomplete", "custody", null, "reviewed 状态必须声明授权/独立性复核和数据准入通过，并包含封存时间、角色和签名对象；这些声明仍须由受控审计人核验。");
            if (HasBlockingExceptions(root))
                Add(issues, "reviewed-with-blocking-exception", "exceptions", null, "reviewed 状态不得包含仍为 open 或已 rejected 的例外；先解决例外或将清单标记为 rejected。");
        }

        return new(issues.Count == 0, ExternalEvidenceVerified: false, records.Length, datasetHash,
            authorizationStatus, independenceStatus, issues);
    }

    private static BlindEvaluationRecord[] ReadDataset(byte[] bytes)
    {
        var records = new List<BlindEvaluationRecord>();
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) throw new JsonException($"盲评数据第 {lineNumber} 行为空。");
            records.Add(JsonSerializer.Deserialize<BlindEvaluationRecord>(line, JsonOptions)
                ?? throw new JsonException($"盲评数据第 {lineNumber} 行为空记录。"));
        }
        return records.ToArray();
    }

    private static string ValidateReview(JsonElement root, string name, string field, List<BlindEvaluationEvidenceIssue> issues)
    {
        var review = RequireObject(root, name, field, issues);
        if (review is not { } value) return "not_verified";
        var status = RequireString(value, "status", field + ".status", issues);
        if (status is not ("not_verified" or "verified" or "rejected"))
            Add(issues, "review-status", field + ".status", null, "状态必须是 not_verified、verified 或 rejected。");
        if (status == "verified")
        {
            RequireUtcTimestamp(value, "verified_at_utc", field + ".verified_at_utc", issues);
            RequireOpaqueReference(value, "reviewer_role_ref", field + ".reviewer_role_ref", "role", issues);
        }
        else
        {
            ValidateOptionalUtcTimestamp(value, "verified_at_utc", field + ".verified_at_utc", issues);
            ValidateOptionalRoleReference(value, "reviewer_role_ref", field + ".reviewer_role_ref", issues);
        }

        var evidenceCount = ValidateReviewEvidence(value, field, issues);
        if (status == "verified" && evidenceCount == 0)
            Add(issues, "review-evidence", field + ".evidence", null, "verified 状态必须列出至少一项受控证据引用。");
        return status ?? "not_verified";
    }

    private static int ValidateReviewEvidence(JsonElement review, string field, List<BlindEvaluationEvidenceIssue> issues)
    {
        if (!review.TryGetProperty("evidence", out var evidence) || evidence.ValueKind != JsonValueKind.Array)
        {
            Add(issues, "review-evidence", field + ".evidence", null, "evidence 必须是数组。");
            return 0;
        }

        var index = 0;
        foreach (var item in evidence.EnumerateArray())
        {
            var prefix = $"{field}.evidence[{index++}]";
            if (item.ValueKind != JsonValueKind.Object)
            {
                Add(issues, "review-evidence", prefix, null, "证据条目必须是对象。");
                continue;
            }
            RequireArtifactReference(item, "artifact_ref", prefix + ".artifact_ref", issues);
            RequireHash(item, "sha256", prefix + ".sha256", issues);
            RequireString(item, "purpose", prefix + ".purpose", issues);
        }
        return evidence.GetArrayLength();
    }

    private static void ValidateRecordEvidence(JsonElement root, IReadOnlyList<BlindEvaluationRecord> records, List<BlindEvaluationEvidenceIssue> issues)
    {
        if (!root.TryGetProperty("record_evidence", out var evidence) || evidence.ValueKind != JsonValueKind.Array)
        {
            Add(issues, "record-evidence", "record_evidence", null, "record_evidence 必须是数组，且逐条覆盖冻结数据。");
            return;
        }
        var byId = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var item in evidence.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !TryGetString(item, "record_id", out var id) || string.IsNullOrWhiteSpace(id))
            {
                Add(issues, "record-evidence-id", "record_evidence", null, "每条记录证据必须包含非空 record_id。");
                continue;
            }
            if (!byId.TryAdd(id, item)) Add(issues, "duplicate-record-evidence", "record_evidence", id, "同一 record_id 只能有一条证据索引。");
        }
        var datasetIds = new HashSet<string>(StringComparer.Ordinal);
        var oneTimeReviewerReferences = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (string.IsNullOrWhiteSpace(record.Id))
                Add(issues, "dataset-record-id", "dataset", null, "冻结数据中的每条记录都必须包含非空 id。");
            else if (!datasetIds.Add(record.Id))
                Add(issues, "duplicate-dataset-record-id", "dataset", record.Id, "冻结数据中的 record id 重复，证据无法唯一绑定。");
        }
        foreach (var extraId in byId.Keys.Except(datasetIds, StringComparer.Ordinal))
            Add(issues, "unknown-record-evidence", "record_evidence", extraId, "证据索引引用了不属于该数据快照的记录。");
        foreach (var record in records)
        {
            if (string.IsNullOrWhiteSpace(record.Id)) continue;
            if (!byId.TryGetValue(record.Id, out var item))
            {
                Add(issues, "missing-record-evidence", "record_evidence", record.Id, "冻结数据中的记录缺少授权与评审提交证据索引。");
                continue;
            }
            ValidateSingleRecordEvidence(item, record, issues, oneTimeReviewerReferences);
        }
    }

    private static void ValidateSingleRecordEvidence(
        JsonElement item,
        BlindEvaluationRecord record,
        List<BlindEvaluationEvidenceIssue> issues,
        Dictionary<string, string> oneTimeReviewerReferences)
    {
        var id = record.Id;
        RequireOpaqueReference(item, "source_ref", $"record_evidence.{id}.source_ref", "source", issues);
        if (TryGetString(item, "source_ref", out var sourceRef) &&
            !string.Equals(sourceRef, record.Source?.LicenseOrConsentRef, StringComparison.Ordinal))
            Add(issues, "source-reference-mismatch", $"record_evidence.{id}.source_ref", id, "证据 source_ref 与冻结记录的来源引用不一致。");
        RequireArtifactReference(item, "authorization_artifact_ref", $"record_evidence.{id}.authorization_artifact_ref", issues);
        RequireHash(item, "authorization_sha256", $"record_evidence.{id}.authorization_sha256", issues);
        RequireArtifactReference(item, "blind_package_artifact_ref", $"record_evidence.{id}.blind_package_artifact_ref", issues);
        RequireString(item, "blind_package_version", $"record_evidence.{id}.blind_package_version", issues);
        var packageHash = RequireHash(item, "blind_package_sha256", $"record_evidence.{id}.blind_package_sha256", issues);

        var expectedReviewers = record.HumanReview?.ReviewerIds?.ToHashSet(StringComparer.Ordinal) ?? [];
        DateTimeOffset? latestReviewSubmissionAt = null;
        if (!item.TryGetProperty("review_submissions", out var submissions) || submissions.ValueKind != JsonValueKind.Array || submissions.GetArrayLength() != 2)
        {
            Add(issues, "review-submission-count", $"record_evidence.{id}.review_submissions", id, "每条 gold 必须关联恰好两份独立评审提交。");
        }
        else
        {
            var reviewerRefs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var submission in submissions.EnumerateArray())
            {
                if (submission.ValueKind != JsonValueKind.Object)
                {
                    Add(issues, "review-submission", $"record_evidence.{id}.review_submissions", id, "评审提交必须是对象。");
                    continue;
                }
                RequireOpaqueReference(submission, "reviewer_ref", $"record_evidence.{id}.review_submissions.reviewer_ref", "reviewer", issues, recordId: id);
                if (TryGetString(submission, "reviewer_ref", out var reviewerRef))
                {
                    reviewerRefs.Add(reviewerRef);
                    TrackOneTimeReviewerReference(reviewerRef, id,
                        $"record_evidence.{id}.review_submissions.reviewer_ref", oneTimeReviewerReferences, issues);
                }
                RequireArtifactReference(submission, "artifact_ref", $"record_evidence.{id}.review_submissions.artifact_ref", issues, recordId: id);
                RequireHash(submission, "sha256", $"record_evidence.{id}.review_submissions.sha256", issues, recordId: id);
                RequireUtcTimestamp(submission, "submitted_at_utc", $"record_evidence.{id}.review_submissions.submitted_at_utc", issues, id);
                if (TryGetString(submission, "submitted_at_utc", out var reviewSubmittedAtText) &&
                    StrictUtcTimestamp.TryParse(reviewSubmittedAtText, out var reviewSubmittedAt) &&
                    (latestReviewSubmissionAt is null || reviewSubmittedAt > latestReviewSubmissionAt))
                    latestReviewSubmissionAt = reviewSubmittedAt;
                var submissionPackageHash = RequireHash(submission, "blind_package_sha256", $"record_evidence.{id}.review_submissions.blind_package_sha256", issues, recordId: id);
                if (packageHash is not null && submissionPackageHash is not null && !string.Equals(packageHash, submissionPackageHash, StringComparison.OrdinalIgnoreCase))
                    Add(issues, "blind-package-mismatch", $"record_evidence.{id}.review_submissions.blind_package_sha256", id, "评审提交绑定的盲包哈希与该记录的盲包哈希不一致。");
            }
            if (reviewerRefs.Count != 2 || !reviewerRefs.SetEquals(expectedReviewers))
                Add(issues, "reviewer-evidence-mismatch", $"record_evidence.{id}.review_submissions", id, "证据中的两个 reviewer_ref 必须唯一且与冻结记录评审引用完全对应。");
        }

        var isAdjudicated = string.Equals(record.HumanReview?.ReviewStatus, "adjudicated", StringComparison.Ordinal);
        if (isAdjudicated)
        {
            if (!item.TryGetProperty("adjudication", out var adjudication) || adjudication.ValueKind != JsonValueKind.Object)
            {
                Add(issues, "missing-adjudication-evidence", $"record_evidence.{id}.adjudication", id, "已裁定记录必须关联第三方裁定原始提交。");
                return;
            }
            RequireOpaqueReference(adjudication, "adjudicator_ref", $"record_evidence.{id}.adjudication.adjudicator_ref", "reviewer", issues, recordId: id);
            if (TryGetString(adjudication, "adjudicator_ref", out var adjudicatorRef))
            {
                TrackOneTimeReviewerReference(adjudicatorRef, id,
                    $"record_evidence.{id}.adjudication.adjudicator_ref", oneTimeReviewerReferences, issues);
                if (adjudicatorRef != record.HumanReview?.AdjudicatorId)
                    Add(issues, "adjudicator-evidence-mismatch", $"record_evidence.{id}.adjudication.adjudicator_ref", id, "裁定证据身份引用与冻结记录的 adjudicator_id 不一致。");
            }
            RequireArtifactReference(adjudication, "artifact_ref", $"record_evidence.{id}.adjudication.artifact_ref", issues, recordId: id);
            RequireHash(adjudication, "sha256", $"record_evidence.{id}.adjudication.sha256", issues, recordId: id);
            RequireUtcTimestamp(adjudication, "submitted_at_utc", $"record_evidence.{id}.adjudication.submitted_at_utc", issues, id);
            if (latestReviewSubmissionAt.HasValue && TryGetString(adjudication, "submitted_at_utc", out var adjudicationSubmittedAtText) &&
                StrictUtcTimestamp.TryParse(adjudicationSubmittedAtText, out var adjudicationSubmittedAt))
            {
                if (adjudicationSubmittedAt < latestReviewSubmissionAt.Value)
                    Add(issues, "adjudication-before-reviews", $"record_evidence.{id}.adjudication.submitted_at_utc", id,
                        "第三方裁定提交时间不得早于两份独立评审提交时间；时间真实性仍需对照受控系统审计记录核验。");
            }
            var adjudicationPackageHash = RequireHash(adjudication, "blind_package_sha256", $"record_evidence.{id}.adjudication.blind_package_sha256", issues, recordId: id);
            if (packageHash is not null && adjudicationPackageHash is not null && !string.Equals(packageHash, adjudicationPackageHash, StringComparison.OrdinalIgnoreCase))
                Add(issues, "blind-package-mismatch", $"record_evidence.{id}.adjudication.blind_package_sha256", id, "裁定提交绑定的盲包哈希与该记录的盲包哈希不一致。");
        }
        else if (item.TryGetProperty("adjudication", out var adjudication) && adjudication.ValueKind != JsonValueKind.Null)
        {
            Add(issues, "unexpected-adjudication-evidence", $"record_evidence.{id}.adjudication", id, "无裁定的记录不得附加第三方裁定提交。");
        }
    }

    private static void TrackOneTimeReviewerReference(
        string reviewerReference,
        string recordId,
        string field,
        Dictionary<string, string> references,
        List<BlindEvaluationEvidenceIssue> issues)
    {
        if (!references.TryAdd(reviewerReference, recordId))
            Add(issues, "duplicate-reviewer-reference", field, recordId,
                "一次性评审任务引用（含裁定引用）必须在整个冻结数据集中唯一。");
    }

    private static string? ValidateAdmissionSnapshot(JsonElement root, List<BlindEvaluationEvidenceIssue> issues)
    {
        var admission = RequireObject(root, "dataset_admission", "dataset_admission", issues);
        if (admission is not { } value) return null;
        var status = RequireString(value, "status", "dataset_admission.status", issues);
        if (status is not ("not_run" or "passed" or "failed"))
            Add(issues, "dataset-admission-status", "dataset_admission.status", null, "status 必须是 not_run、passed 或 failed。");
        if (status == "not_run")
        {
            RequireExplicitNull(value, "report_artifact_ref", "dataset_admission.report_artifact_ref", issues);
            RequireExplicitNull(value, "report_sha256", "dataset_admission.report_sha256", issues);
            return status;
        }
        RequireArtifactReference(value, "report_artifact_ref", "dataset_admission.report_artifact_ref", issues);
        RequireHash(value, "report_sha256", "dataset_admission.report_sha256", issues);
        return status;
    }

    private static void ValidateExceptions(JsonElement root, List<BlindEvaluationEvidenceIssue> issues)
    {
        if (!root.TryGetProperty("exceptions", out var exceptions) || exceptions.ValueKind != JsonValueKind.Array)
        {
            Add(issues, "field-type", "exceptions", null, "exceptions 必须是异常对象数组。");
            return;
        }

        var index = 0;
        foreach (var item in exceptions.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                Add(issues, "field-type", $"exceptions[{index}]", null, "每条 exception 必须是对象。");
                index++;
                continue;
            }

            var path = $"exceptions[{index}]";
            RequireOpaqueReference(item, "exception_ref", path + ".exception_ref", "exception", issues);
            var category = RequireString(item, "category", path + ".category", issues);
            if (category is not ("authorization" or "privacy" or "reviewer_independence" or "adjudication" or "retention" or "withdrawal" or "data_integrity" or "other"))
                Add(issues, "exception-category", path + ".category", null, "category 必须使用受支持的受控分类。");
            var status = RequireString(item, "status", path + ".status", issues);
            if (status is not ("open" or "resolved" or "rejected" or "withdrawn"))
                Add(issues, "exception-status", path + ".status", null, "status 必须是 open、resolved、rejected 或 withdrawn。");
            ValidateOptionalArtifactReference(item, "evidence_ref", path + ".evidence_ref", issues);
            ValidateOptionalHash(item, "evidence_sha256", path + ".evidence_sha256", issues);
            var hasEvidenceReference = item.TryGetProperty("evidence_ref", out var evidenceReference) && evidenceReference.ValueKind == JsonValueKind.String;
            var hasEvidenceHash = item.TryGetProperty("evidence_sha256", out var evidenceHash) && evidenceHash.ValueKind == JsonValueKind.String;
            if (hasEvidenceReference != hasEvidenceHash)
                Add(issues, "exception-evidence-pair", path, null, "evidence_ref 与 evidence_sha256 必须同时填写或同时为 null。");
            RequireUtcTimestamp(item, "recorded_at_utc", path + ".recorded_at_utc", issues);
            RequireOpaqueReference(item, "owner_role_ref", path + ".owner_role_ref", "role", issues);
            index++;
        }
    }

    private static bool HasBlockingExceptions(JsonElement root) =>
        root.TryGetProperty("exceptions", out var exceptions) && exceptions.ValueKind == JsonValueKind.Array &&
        exceptions.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Object &&
            TryGetString(item, "status", out var status) && status is "open" or "rejected");

    private static void ValidateOptionalArtifactReference(JsonElement parent, string name, string field, List<BlindEvaluationEvidenceIssue> issues)
    {
        if (!parent.TryGetProperty(name, out var value))
            Add(issues, "required-field", field, null, $"必须显式提供 {field}；没有受控证据时设为 null。");
        else if (value.ValueKind == JsonValueKind.Null) return;
        else if (value.ValueKind != JsonValueKind.String || !ArtifactReference.IsMatch(value.GetString() ?? string.Empty))
            Add(issues, "artifact-reference", field, null, $"{field} 必须是 vault: 不透明引用或 null。");
    }

    private static void ValidateOptionalHash(JsonElement parent, string name, string field, List<BlindEvaluationEvidenceIssue> issues)
    {
        if (!parent.TryGetProperty(name, out var value))
            Add(issues, "required-field", field, null, $"必须显式提供 {field}；没有受控证据时设为 null。");
        else if (value.ValueKind == JsonValueKind.Null) return;
        else if (value.ValueKind != JsonValueKind.String || !IsSha256(value.GetString() ?? string.Empty))
            Add(issues, "invalid-sha256", field, null, $"{field} 必须是 64 位 SHA-256 十六进制摘要或 null。");
    }

    private static void RequireExplicitNull(JsonElement parent, string name, string field, List<BlindEvaluationEvidenceIssue> issues)
    {
        if (!parent.TryGetProperty(name, out var value))
            Add(issues, "required-field", field, null, $"必须显式提供 {field}；准入尚未运行时设为 null。");
        else if (value.ValueKind != JsonValueKind.Null)
            Add(issues, "field-type", field, null, $"准入状态为 not_run 时，{field} 必须为 null。");
    }

    private static void ValidateManifestShape(JsonElement root, List<BlindEvaluationEvidenceIssue> issues)
    {
        CheckAllowedFields(root, string.Empty, RootFields, issues);
        CheckObject(root, "dataset", "dataset", DatasetFields, issues);
        CheckObject(root, "handbook", "handbook", HandbookFields, issues);
        ValidateReviewShape(root, "authorization_review", "authorization_review", issues);
        ValidateReviewShape(root, "reviewer_independence_review", "reviewer_independence_review", issues);
        CheckObject(root, "dataset_admission", "dataset_admission", AdmissionFields, issues);
        CheckObject(root, "custody", "custody", CustodyFields, issues);
        CheckArrayObjects(root, "exceptions", "exceptions", ExceptionFields, issues);

        if (root.TryGetProperty("record_evidence", out var records) && records.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var record in records.EnumerateArray())
            {
                var recordPath = $"record_evidence[{index++}]";
                if (record.ValueKind != JsonValueKind.Object) continue;
                CheckAllowedFields(record, recordPath, RecordEvidenceFields, issues);
                CheckArrayObjects(record, "review_submissions", recordPath + ".review_submissions", SubmissionFields, issues);
                if (record.TryGetProperty("adjudication", out var adjudication) && adjudication.ValueKind == JsonValueKind.Object)
                    CheckAllowedFields(adjudication, recordPath + ".adjudication", AdjudicationFields, issues);
            }
        }
    }

    private static void ValidateReviewShape(JsonElement root, string property, string path, List<BlindEvaluationEvidenceIssue> issues)
    {
        var review = CheckObject(root, property, path, ReviewFields, issues);
        if (review is { } reviewObject)
            CheckArrayObjects(reviewObject, "evidence", path + ".evidence", OpaqueEvidenceFields, issues);
    }

    private static JsonElement? CheckObject(JsonElement parent, string property, string path, HashSet<string> allowed, List<BlindEvaluationEvidenceIssue> issues)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object) return null;
        CheckAllowedFields(value, path, allowed, issues);
        return value;
    }

    private static void CheckArrayObjects(JsonElement parent, string property, string path, HashSet<string> allowed, List<BlindEvaluationEvidenceIssue> issues)
    {
        if (!parent.TryGetProperty(property, out var items) || items.ValueKind != JsonValueKind.Array) return;
        var index = 0;
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object) CheckAllowedFields(item, $"{path}[{index}]", allowed, issues);
            index++;
        }
    }

    private static void CheckAllowedFields(JsonElement value, string path, HashSet<string> allowed, List<BlindEvaluationEvidenceIssue> issues)
    {
        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                var field = string.IsNullOrEmpty(path) ? property.Name : path + "." + property.Name;
                Add(issues, "unknown-field", field, null, "证据清单包含 JSON Schema 未定义字段。");
            }
        }
    }

    private static void ValidateNoDuplicateProperties(JsonElement value, string path, List<BlindEvaluationEvidenceIssue> issues)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                var propertyPath = string.IsNullOrEmpty(path) ? property.Name : path + "." + property.Name;
                if (!seen.Add(property.Name))
                    Add(issues, "duplicate-json-property", propertyPath, null, "JSON 对象中不允许重复字段。");
                ValidateNoDuplicateProperties(property.Value, propertyPath, issues);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray()) ValidateNoDuplicateProperties(item, $"{path}[{index++}]", issues);
        }
    }

    private static HashSet<string> Fields(params string[] names) => new(names, StringComparer.Ordinal);

    private static JsonElement? RequireObject(JsonElement parent, string name, string field, List<BlindEvaluationEvidenceIssue> issues)
    {
        if (parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object) return value;
        Add(issues, "required-object", field, null, $"缺少 JSON 对象 {field}。");
        return null;
    }

    private static string? RequireString(JsonElement parent, string name, string field, List<BlindEvaluationEvidenceIssue> issues, string? expected = null)
    {
        if (!TryGetString(parent, name, out var value) || string.IsNullOrWhiteSpace(value))
        {
            Add(issues, "required-string", field, null, $"{field} 必须是非空字符串。");
            return null;
        }
        if (expected is not null && !string.Equals(value, expected, StringComparison.Ordinal))
            Add(issues, "value-mismatch", field, null, $"{field} 与当前数据快照/准入协议不一致。");
        return value;
    }

    private static string? RequireHash(JsonElement parent, string name, string field, List<BlindEvaluationEvidenceIssue> issues, string? expected = null, string? recordId = null)
    {
        var value = RequireString(parent, name, field, issues);
        if (value is null) return null;
        if (!IsSha256(value))
        {
            Add(issues, "invalid-sha256", field, recordId, $"{field} 必须是 64 位 SHA-256 十六进制摘要。");
            return null;
        }
        if (expected is not null && !string.Equals(value, expected, StringComparison.OrdinalIgnoreCase))
            Add(issues, "dataset-hash-mismatch", field, recordId, $"{field} 与实际数据字节 SHA-256 不一致。");
        return value;
    }

    private static void RequireInteger(JsonElement parent, string name, string field, List<BlindEvaluationEvidenceIssue> issues, int expected)
    {
        if (!parent.TryGetProperty(name, out var value) || !value.TryGetInt32(out var count) || count < 0)
        {
            Add(issues, "sample-count", field, null, $"{field} 必须是非负整数。");
            return;
        }
        if (count != expected) Add(issues, "sample-count-mismatch", field, null, $"{field} 与当前数据文件行数不一致。");
    }

    private static void RequireArtifactReference(JsonElement parent, string name, string field, List<BlindEvaluationEvidenceIssue> issues,
        string? recordId = null, string? valueOverride = null)
    {
        var value = valueOverride ?? (TryGetString(parent, name, out var found) ? found : null);
        if (value is null || !ArtifactReference.IsMatch(value))
            Add(issues, "artifact-reference", field, recordId, $"{field} 必须是非路径、非 URL 的 vault: 不透明引用。");
    }

    private static void RequireOpaqueReference(JsonElement parent, string name, string field, string requiredKind,
        List<BlindEvaluationEvidenceIssue> issues, string? recordId = null)
    {
        if (!TryGetString(parent, name, out var value) || !MatchesReferenceKind(value, requiredKind))
            Add(issues, "opaque-reference", field, recordId, $"{field} 必须符合 {requiredKind} 引用格式，不能包含直接身份或路径。");
    }

    private static bool MatchesReferenceKind(string value, string requiredKind) => requiredKind switch
    {
        "role" => RoleReference.IsMatch(value),
        "reviewer" => ReviewerReference.IsMatch(value),
        "source" => SourceReference.IsMatch(value),
        "exception" => ExceptionReference.IsMatch(value),
        _ => false
    };

    private static void RequireUtcTimestamp(JsonElement parent, string name, string field, List<BlindEvaluationEvidenceIssue> issues, string? recordId = null)
    {
        if (!TryGetString(parent, name, out var value) || !StrictUtcTimestamp.TryParse(value, out _))
            Add(issues, "utc-timestamp", field, recordId, $"{field} 必须是带 Z 或 +00:00 的 UTC 时间。时间真实性仍需对照受控系统审计记录核验。");
    }

    private static void ValidateOptionalUtcTimestamp(JsonElement parent, string name, string field, List<BlindEvaluationEvidenceIssue> issues)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            Add(issues, "required-field", field, null, $"必须显式提供 {field}；未封存时设为 null。");
            return;
        }
        if (value.ValueKind == JsonValueKind.Null) return;
        if (value.ValueKind != JsonValueKind.String || !StrictUtcTimestamp.TryParse(value.GetString(), out _))
            Add(issues, "utc-timestamp", field, null, $"{field} 必须是带 Z 或 +00:00 的 UTC 时间或 null。");
    }

    private static void ValidateOptionalRoleReference(JsonElement parent, string name, string field, List<BlindEvaluationEvidenceIssue> issues)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            Add(issues, "required-field", field, null, $"必须显式提供 {field}；未封存时设为 null。");
            return;
        }
        if (value.ValueKind == JsonValueKind.Null) return;
        if (value.ValueKind != JsonValueKind.String || !RoleReference.IsMatch(value.GetString() ?? string.Empty))
            Add(issues, "opaque-reference", field, null, $"{field} 必须是 role: 前缀的不透明引用或 null。");
    }

    private static bool TryGetString(JsonElement parent, string name, out string value)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString() ?? string.Empty;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static void Add(List<BlindEvaluationEvidenceIssue> issues, string code, string field, string? recordId, string message) =>
        issues.Add(new(code, field, recordId, message));
}
