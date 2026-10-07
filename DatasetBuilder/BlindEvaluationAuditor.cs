using System.Text.RegularExpressions;

namespace Huaxiazi.DatasetBuilder;

public sealed record BlindEvaluationSource(string SourceKind, string LicenseOrConsentRef, string Deidentification, string? SourceRecordHash = null);

public sealed record BlindEvaluationAnnotations(
    IReadOnlyList<string> FactsAndConstraints,
    string TargetTone,
    IReadOnlyList<string> FormatRequirements,
    bool ClarificationRequired,
    IReadOnlyList<string> HighRiskKeyFacts);

public sealed record BlindEvaluationGoldLabelSet(
    string Task,
    string InputStyle,
    IReadOnlyList<string> Constraints,
    string RiskLevel,
    string ExpectedDecision,
    string? ReferenceOutput,
    BlindEvaluationAnnotations Annotations);

public sealed record BlindEvaluationAnnotationVote(string ReviewerId, BlindEvaluationGoldLabelSet Labels);

public sealed record BlindEvaluationHumanReview(
    IReadOnlyList<string> ReviewerIds,
    string ReviewStatus,
    string? AdjudicatorId = null,
    IReadOnlyList<BlindEvaluationAnnotationVote>? IndependentAnnotations = null,
    BlindEvaluationAnnotationVote? Adjudication = null);

public sealed record BlindEvaluationKnownRecord(string Split, string Input, string SemanticFamilyId);

public sealed record BlindEvaluationConversationTurn(int TurnIndex, string UserInput);

public sealed record BlindEvaluationRecord
{
    public string Id { get; init; } = string.Empty;
    public string Task { get; init; } = string.Empty;
    public string SemanticFamilyId { get; init; } = string.Empty;
    public BlindEvaluationSource Source { get; init; } = new("", "", "");
    public string Input { get; init; } = string.Empty;
    public string? ConversationId { get; init; }
    public IReadOnlyList<BlindEvaluationConversationTurn>? Turns { get; init; }
    public string InputStyle { get; init; } = string.Empty;
    public Dictionary<string, System.Text.Json.JsonElement> Context { get; init; } = [];
    public IReadOnlyList<string> Constraints { get; init; } = [];
    public string RiskLevel { get; init; } = string.Empty;
    public string Split { get; init; } = string.Empty;
    public string ExpectedDecision { get; init; } = string.Empty;
    public string? ReferenceOutput { get; init; }
    public BlindEvaluationAnnotations Annotations { get; init; } = new([], "", [], false, []);
    public BlindEvaluationHumanReview HumanReview { get; init; } = new([], "pending");
}

public sealed record BlindEvaluationIssue(string Code, string RecordId, string Message);

public sealed record BlindEvaluationCoverageSliceSummary(
    int SampleCount,
    int PolishCount,
    int PromptOptimizeCount,
    int HighRiskCount,
    int RoutineCount,
    int ClarifyCount,
    int FormatRequirementCount,
    int FactOrConstraintAnchorCount,
    int DistinctInputStyleCount,
    IReadOnlyDictionary<string, int> InputStyleCounts);

public sealed record BlindEvaluationTaskSplitCoverageSummary(
    string Task,
    string Split,
    int SampleCount,
    int HighRiskCount,
    int RoutineCount,
    int ClarifyCount,
    int FormatRequirementCount,
    int FactOrConstraintAnchorCount,
    int DistinctInputStyleCount,
    IReadOnlyDictionary<string, int> InputStyleCounts,
    IReadOnlyDictionary<string, int> ProductSliceCounts);

public sealed record BlindEvaluationCoverageSummary(
    int TotalCount,
    int PolishCount,
    int PromptOptimizeCount,
    int DevelopmentCount,
    int FrozenTestCount,
    int HighRiskCount,
    int RoutineCount,
    int ClarifyCount,
    int FormatRequirementCount,
    int FactOrConstraintAnchorCount,
    IReadOnlyDictionary<string, int> InputStyleCounts,
    BlindEvaluationCoverageSliceSummary Development,
    BlindEvaluationCoverageSliceSummary FrozenTest,
    IReadOnlyList<BlindEvaluationTaskSplitCoverageSummary> TaskSplitCoverage);

public sealed record BlindEvaluationAgreementFieldSummary(
    int ComparableCount,
    int ExactAgreementCount,
    double? ExactAgreementRate,
    double? KrippendorffAlphaNominal,
    string ChanceCorrectedStatus,
    double? PositiveSpecificAgreement,
    double? NegativeSpecificAgreement,
    IReadOnlyDictionary<string, int>? PooledCategoryCounts);

public sealed record BlindEvaluationReviewerAgreementSummary(
    int ComparableRecordCount,
    int WholeLabelAgreementCount,
    double? WholeLabelAgreementRate,
    IReadOnlyDictionary<string, BlindEvaluationAgreementFieldSummary> Fields);

/// <summary>
/// Validates admission and split hygiene for the user-facing blind evaluation set.
/// This is deliberately separate from the existing synthetic training dataset validator.
/// </summary>
public static class BlindEvaluationAuditor
{
    private static readonly Regex Phone = new(@"(?<!\d)(?:(?:\+|00)86[\s-]?)?1[3-9](?:[\s-]?\d){9}(?!\d)", RegexOptions.Compiled);
    private static readonly Regex ChineseNationalId = new(@"(?<!\d)(?:\d{15}|\d{17}[\dXx])(?![\dXx])", RegexOptions.Compiled);
    private static readonly Regex Email = new(@"\b[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}\b", RegexOptions.Compiled);
    private static readonly Regex Secret = new(@"(?i)(sk-[a-z0-9]{12,}|api[_ -]?(?:key|password)\s*[:=]\s*\S+)", RegexOptions.Compiled);
    private static readonly Regex OpenRouterKey = new(@"(?i)(?<![A-Za-z0-9])sk-or-v1-[A-Za-z0-9_-]+(?![A-Za-z0-9_-])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AnthropicKey = new(@"(?i)(?<![A-Za-z0-9])sk-ant-api03-[A-Za-z0-9_-]+(?![A-Za-z0-9_-])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex XaiKey = new(@"(?i)(?<![A-Za-z0-9])xai-[A-Za-z0-9_-]+(?![A-Za-z0-9_-])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OpenAiProjectKey = new(@"(?i)(?<![A-Za-z0-9])sk-proj-[A-Za-z0-9_-]+(?![A-Za-z0-9_-])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ArkKey = new(@"(?i)(?<![A-Za-z0-9])ark-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}-[A-Za-z0-9_-]+(?![A-Za-z0-9_-])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AlibabaPlanKey = new(@"(?i)(?<![A-Za-z0-9])sk-sp-[A-Za-z0-9_-]+(?![A-Za-z0-9_-])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MiMoTokenPlanKey = new(@"(?i)(?<![A-Za-z0-9])tp-[A-Za-z0-9_-]+(?![A-Za-z0-9_-])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CommonCredentialFormat = new(
        @"(?x)(?<![A-Za-z0-9])(?:AKIA|ASIA)[A-Z0-9]{16}(?![A-Z0-9])|(?<![A-Za-z0-9])AIza[A-Za-z0-9_-]{35}(?![A-Za-z0-9_-])|(?<![A-Za-z0-9])gsk_[A-Za-z0-9_-]{20,255}(?![A-Za-z0-9_-])|(?i:(?<![A-Za-z0-9])xox[baprs]-[A-Za-z0-9-]{16,255}(?![A-Za-z0-9-])|-----BEGIN\s+(?:(?:RSA|EC|DSA|OPENSSH|PGP|ENCRYPTED)\s+)?PRIVATE\s+KEY-----|\bAuthorization\s*:\s*(?:Bearer\s+[A-Za-z0-9._~+/=-]{20,}|Basic\s+[A-Za-z0-9+/]{20,}={0,2}))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex QqContact = new(@"(?:QQ群|Q群|QQ号|QQ)\s*(?:群号|号码|号)?\s*[：:#(（]?\s*\d{5,12}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex WeChatContact = new(@"(?:(?:微信(?:号|账号|\s*ID)|wechat\s*(?:id|account))\s*[：:=]?\s*[A-Za-z][A-Za-z0-9_-]{5,31}(?![A-Za-z0-9_-])|(?<![A-Za-z0-9_-])wxid_[A-Za-z0-9_-]{6,32}(?![A-Za-z0-9_-]))", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> SourceKinds = new(StringComparer.Ordinal)
    {
        "project_owned", "licensed", "user_authorized_deidentified"
    };
    private static readonly HashSet<string> InputStyles = new(BlindEvaluationAdmissionProtocol.SupportedInputStyles, StringComparer.Ordinal);
    private static readonly string[] PolishScenarios = ["私人沟通", "职场沟通", "公开发布", "正式材料", "其他"];
    private static readonly (string Name, string DisplayName)[] PromptCategories =
    [
        ("General", "通用任务"), ("Coding", "编程开发"), ("Writing", "文案写作"),
        ("Analysis", "数据分析"), ("Research", "学术研究"), ("Creative", "创意设计")
    ];
    private const string UnspecifiedOrCustomSlice = "未指定或自定义";

    public static BlindEvaluationCoverageSummary SummarizeCoverage(IEnumerable<BlindEvaluationRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var items = records.ToArray();
        return new(
            items.Length,
            items.Count(item => item.Task == "polish"),
            items.Count(item => item.Task == "prompt_optimize"),
            items.Count(item => item.Split == "development"),
            items.Count(item => item.Split == "frozen_test"),
            items.Count(item => item.RiskLevel == "high"),
            items.Count(item => item.RiskLevel is "low" or "medium"),
            items.Count(item => item.ExpectedDecision == "clarify"),
            items.Count(item => item.Annotations?.FormatRequirements?.Any(value => !string.IsNullOrWhiteSpace(value)) == true),
            items.Count(item =>
                item.Constraints?.Any(value => !string.IsNullOrWhiteSpace(value)) == true ||
                item.Annotations?.FactsAndConstraints?.Any(value => !string.IsNullOrWhiteSpace(value)) == true),
            SummarizeInputStyles(items),
            SummarizeCoverageSlice(items.Where(item => item.Split == "development")),
            SummarizeCoverageSlice(items.Where(item => item.Split == "frozen_test")),
            SummarizeTaskSplitCoverage(items));
    }

/// <summary>Summarizes exact and nominal chance-corrected agreement between the two initial label sets. These are descriptive calibration diagnostics, not admission gates.</summary>
    public static BlindEvaluationReviewerAgreementSummary SummarizeReviewerAgreement(IEnumerable<BlindEvaluationRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var pairs = records
            .Select(record => record.HumanReview)
            .Where(HasStructurallyIndependentVotes)
            .Select(review => review!.IndependentAnnotations!)
            .Select(votes => (Left: votes[0].Labels, Right: votes[1].Labels))
            .ToArray();

        var fields = new Dictionary<string, BlindEvaluationAgreementFieldSummary>(StringComparer.Ordinal)
        {
            ["task"] = SummarizeCategoricalAgreement(pairs, labels => labels.Task, ["polish", "prompt_optimize"]),
            ["input_style"] = SummarizeCategoricalAgreement(pairs, labels => labels.InputStyle, InputStyles.Order(StringComparer.Ordinal).ToArray()),
            ["constraints"] = SummarizeAgreementField(pairs, (left, right) => EqualLabelList(left.Constraints, right.Constraints)),
            ["risk_level"] = SummarizeCategoricalAgreement(pairs, labels => labels.RiskLevel, ["low", "medium", "high"], includeChanceCorrection: false),
            ["expected_decision"] = SummarizeCategoricalAgreement(pairs, labels => labels.ExpectedDecision, ["produce", "clarify", "refuse"]),
            ["reference_output"] = SummarizeAgreementField(pairs, (left, right) => string.Equals(left.ReferenceOutput?.Trim(), right.ReferenceOutput?.Trim(), StringComparison.Ordinal)),
            ["facts_and_constraints"] = SummarizeAgreementField(pairs, (left, right) => EqualLabelList(left.Annotations.FactsAndConstraints, right.Annotations.FactsAndConstraints)),
            ["target_tone"] = SummarizeAgreementField(pairs, (left, right) => string.Equals(left.Annotations.TargetTone?.Trim(), right.Annotations.TargetTone?.Trim(), StringComparison.Ordinal)),
            ["format_requirements"] = SummarizeAgreementField(pairs, (left, right) => EqualLabelList(left.Annotations.FormatRequirements, right.Annotations.FormatRequirements)),
            ["clarification_required"] = SummarizeCategoricalAgreement(pairs, labels => labels.Annotations.ClarificationRequired ? "true" : "false", ["false", "true"], includeBinarySpecificAgreement: true),
            ["high_risk_key_facts"] = SummarizeAgreementField(pairs, (left, right) => EqualLabelList(left.Annotations.HighRiskKeyFacts, right.Annotations.HighRiskKeyFacts))
        };
        var wholeLabelAgreementCount = pairs.Count(pair => GoldLabelsEqual(pair.Left, pair.Right));
        return new(pairs.Length, wholeLabelAgreementCount, ExactAgreementRate(pairs.Length, wholeLabelAgreementCount), fields);
    }

    private static BlindEvaluationAgreementFieldSummary SummarizeAgreementField(
        IReadOnlyList<(BlindEvaluationGoldLabelSet Left, BlindEvaluationGoldLabelSet Right)> pairs,
        Func<BlindEvaluationGoldLabelSet, BlindEvaluationGoldLabelSet, bool> agrees)
    {
        var agreementCount = pairs.Count(pair => agrees(pair.Left, pair.Right));
        return new(pairs.Count, agreementCount, ExactAgreementRate(pairs.Count, agreementCount), null, "not_applicable_scale", null, null, null);
    }

    private static BlindEvaluationAgreementFieldSummary SummarizeCategoricalAgreement(
        IReadOnlyList<(BlindEvaluationGoldLabelSet Left, BlindEvaluationGoldLabelSet Right)> pairs,
        Func<BlindEvaluationGoldLabelSet, string> category,
        IReadOnlyList<string> knownCategories,
        bool includeChanceCorrection = true,
        bool includeBinarySpecificAgreement = false)
    {
        const string otherCategory = "other_or_invalid";
        var first = pairs.Select(pair => NormalizeCategory(category(pair.Left), knownCategories, otherCategory)).ToArray();
        var second = pairs.Select(pair => NormalizeCategory(category(pair.Right), knownCategories, otherCategory)).ToArray();
        var agreementCount = first.Zip(second).Count(pair => string.Equals(pair.First, pair.Second, StringComparison.Ordinal));
        var categories = knownCategories.Append(otherCategory).ToArray();
        var pooledCounts = CountCategories(first.Concat(second), categories);
        var (alpha, alphaStatus) = includeChanceCorrection
            ? ComputeKrippendorffAlphaNominal(first, second, pooledCounts)
            : ((double?)null, "not_applicable_scale");
        var (positiveSpecificAgreement, negativeSpecificAgreement) = includeBinarySpecificAgreement
            ? ComputeBinarySpecificAgreement(first, second, "true", "false")
            : ((double?)null, (double?)null);
        return new(pairs.Count, agreementCount, ExactAgreementRate(pairs.Count, agreementCount), alpha, alphaStatus,
            positiveSpecificAgreement, negativeSpecificAgreement, pooledCounts);
    }

    private static string NormalizeCategory(string value, IReadOnlyList<string> knownCategories, string otherCategory) =>
        knownCategories.Contains(value, StringComparer.Ordinal) ? value : otherCategory;

    private static IReadOnlyDictionary<string, int> CountCategories(IEnumerable<string> values, IReadOnlyList<string> categories) =>
        categories.ToDictionary(category => category, category => values.Count(value => value == category), StringComparer.Ordinal);

    private static (double? Value, string Status) ComputeKrippendorffAlphaNominal(
        string[] first,
        string[] second,
        IReadOnlyDictionary<string, int> pooledCounts)
    {
        if (first.Length == 0) return (null, "no_comparable_records");
        var ratingCount = first.Length + second.Length;
        var observedDisagreement = first.Zip(second).Count(pair => pair.First != pair.Second) / (double)first.Length;
        var expectedDisagreement = ratingCount == 0
            ? 0d
            : (ratingCount / (double)(ratingCount - 1)) *
                (1d - pooledCounts.Values.Sum(count => Math.Pow(count / (double)ratingCount, 2)));
        return expectedDisagreement <= 1e-12
            ? (null, "undefined_perfect_expected_agreement")
            : (1d - (observedDisagreement / expectedDisagreement), "computed");
    }

    private static (double? Positive, double? Negative) ComputeBinarySpecificAgreement(
        string[] first,
        string[] second,
        string positiveCategory,
        string negativeCategory)
    {
        var bothPositive = first.Zip(second).Count(pair => pair.First == positiveCategory && pair.Second == positiveCategory);
        var bothNegative = first.Zip(second).Count(pair => pair.First == negativeCategory && pair.Second == negativeCategory);
        var disagreements = first.Zip(second).Count(pair => pair.First != pair.Second);
        var positiveDenominator = (2 * bothPositive) + disagreements;
        var negativeDenominator = (2 * bothNegative) + disagreements;
        return (
            positiveDenominator == 0 ? null : (2 * bothPositive) / (double)positiveDenominator,
            negativeDenominator == 0 ? null : (2 * bothNegative) / (double)negativeDenominator);
    }

    private static double? ExactAgreementRate(int comparableCount, int agreementCount) =>
        comparableCount == 0 ? null : agreementCount / (double)comparableCount;

    private static bool HasStructurallyIndependentVotes(BlindEvaluationHumanReview? review)
    {
        var votes = review?.IndependentAnnotations;
        if (votes is not { Count: 2 } || votes.Any(vote => vote is null || string.IsNullOrWhiteSpace(vote.ReviewerId) || vote.Labels?.Annotations is null))
            return false;
        if (review!.ReviewerIds is not { Count: 2 } || review.ReviewerIds.Any(string.IsNullOrWhiteSpace))
            return false;

        var voteIds = votes.Select(vote => vote.ReviewerId.Trim()).ToArray();
        var declaredIds = review.ReviewerIds.Select(id => id.Trim()).ToArray();
        return voteIds.Distinct(StringComparer.Ordinal).Count() == 2 &&
            declaredIds.Distinct(StringComparer.Ordinal).Count() == 2 &&
            voteIds.ToHashSet(StringComparer.Ordinal).SetEquals(declaredIds);
    }

    public static IReadOnlyList<BlindEvaluationIssue> Validate(
        IEnumerable<BlindEvaluationRecord> records,
        int minimumSampleCount = BlindEvaluationAdmissionProtocol.MinimumSampleCount,
        IEnumerable<BlindEvaluationKnownRecord>? knownTrainingRecords = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (minimumSampleCount < 1) throw new ArgumentOutOfRangeException(nameof(minimumSampleCount));
        var items = records.ToArray();
        var issues = new List<BlindEvaluationIssue>();
        if (items.Length < minimumSampleCount)
            issues.Add(new("dataset-too-small", "dataset", $"盲评集共 {items.Length} 条，少于要求的 {minimumSampleCount} 条。"));
        var minimumBalanced = MinimumCoverage(items.Length, BlindEvaluationAdmissionProtocol.TaskMinimumRatio);
        var coverage = SummarizeCoverage(items);
        var polishCount = coverage.PolishCount;
        var promptOptimizeCount = coverage.PromptOptimizeCount;
        if (polishCount < minimumBalanced || promptOptimizeCount < minimumBalanced)
            AddDatasetCoverage("task-coverage", $"polish 与 prompt_optimize 各至少需要 {minimumBalanced} 条，当前分别为 {polishCount} 与 {promptOptimizeCount} 条。", issues);
        var developmentCount = coverage.DevelopmentCount;
        var frozenTestCount = coverage.FrozenTestCount;
        var minimumSplit = MinimumCoverage(items.Length, BlindEvaluationAdmissionProtocol.SplitMinimumRatio);
        if (developmentCount < minimumSplit || frozenTestCount < minimumSplit)
            AddDatasetCoverage("split-coverage", $"development 与 frozen_test 各至少需要 {minimumSplit} 条，当前分别为 {developmentCount} 与 {frozenTestCount} 条。", issues);
        var minimumHighRisk = MinimumCoverage(items.Length, BlindEvaluationAdmissionProtocol.HighRiskMinimumRatio);
        var highRiskCount = coverage.HighRiskCount;
        if (highRiskCount < minimumHighRisk)
            AddDatasetCoverage("coverage-high-risk", $"高风险样本至少需要 {minimumHighRisk} 条（总量 10%），当前 {highRiskCount} 条。", issues);
        var minimumRoutine = MinimumCoverage(items.Length, BlindEvaluationAdmissionProtocol.RoutineMinimumRatio);
        var routineCount = coverage.RoutineCount;
        if (routineCount < minimumRoutine)
            AddDatasetCoverage("coverage-standard", $"常规样本至少需要 {minimumRoutine} 条（总量 50%），当前 {routineCount} 条。", issues);
        var minimumClarification = MinimumCoverage(items.Length, BlindEvaluationAdmissionProtocol.ClarificationMinimumRatio);
        var clarifyCount = coverage.ClarifyCount;
        if (clarifyCount < minimumClarification)
            AddDatasetCoverage("coverage-clarify", $"需澄清样本至少需要 {minimumClarification} 条（总量 5%），当前 {clarifyCount} 条。", issues);
        var minimumFormat = MinimumCoverage(items.Length, BlindEvaluationAdmissionProtocol.FormatMinimumRatio);
        var formatCount = coverage.FormatRequirementCount;
        if (formatCount < minimumFormat)
            AddDatasetCoverage("coverage-format", $"明确格式要求样本至少需要 {minimumFormat} 条（总量 5%），当前 {formatCount} 条。", issues);
        var anchoredCount = coverage.FactOrConstraintAnchorCount;
        var minimumAnchored = MinimumCoverage(items.Length, BlindEvaluationAdmissionProtocol.FactOrConstraintAnchorMinimumRatio);
        if (anchoredCount < minimumAnchored)
            AddDatasetCoverage("coverage-facts-constraints", $"含事实或约束锚点的样本至少需要 {minimumAnchored} 条（总量 50%），当前 {anchoredCount} 条。", issues);
        ValidateCoverageSlice(coverage.Development, "development", issues);
        ValidateCoverageSlice(coverage.FrozenTest, "frozen-test", issues);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var familySplits = new Dictionary<string, string>(StringComparer.Ordinal);
        var inputSplits = new Dictionary<string, string>(StringComparer.Ordinal);
        var knownFamilies = new HashSet<string>(StringComparer.Ordinal);
        var knownInputs = new HashSet<string>(StringComparer.Ordinal);
        if (knownTrainingRecords is not null)
        {
            foreach (var known in knownTrainingRecords.Where(item => item.Split is "train" or "dev"))
            {
                if (!string.IsNullOrWhiteSpace(known.SemanticFamilyId)) knownFamilies.Add(known.SemanticFamilyId);
                if (!string.IsNullOrWhiteSpace(known.Input)) knownInputs.Add(known.Input.Trim());
            }
        }
        foreach (var record in items)
        {
            if (string.IsNullOrWhiteSpace(record.Id)) Add("required", record, "缺少 id。", issues);
            else if (!ids.Add(record.Id)) Add("duplicate-id", record, "id 重复。", issues);
            if (record.Task is not ("polish" or "prompt_optimize")) Add("task", record, "task 无效。", issues);
            if (string.IsNullOrWhiteSpace(record.SemanticFamilyId)) Add("required", record, "缺少 semantic_family_id。", issues);
            if (record.Split is not ("development" or "frozen_test")) Add("split", record, "split 无效。", issues);
            if (record.RiskLevel is not ("low" or "medium" or "high")) Add("risk-level", record, "risk_level 无效。", issues);
            if (record.ExpectedDecision is not ("produce" or "clarify" or "refuse")) Add("decision", record, "expected_decision 无效。", issues);
            if (string.IsNullOrWhiteSpace(record.Input)) Add("required", record, "input 不能为空。", issues);
            ValidateConversation(record, issues);
            if (!InputStyles.Contains(record.InputStyle)) Add("input-style", record, "input_style 必须是受支持的输入表达风格标签。", issues);
            if (record.Annotations is null) Add("required", record, "缺少 annotations。", issues);
            if (record.Source is null || !SourceKinds.Contains(record.Source.SourceKind) ||
                string.IsNullOrWhiteSpace(record.Source.LicenseOrConsentRef) ||
                string.IsNullOrWhiteSpace(record.Source.Deidentification) ||
                (record.Source.SourceRecordHash is not null && !Regex.IsMatch(record.Source.SourceRecordHash, "^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant)))
                Add("source-authorization", record, "来源类型、许可/授权凭据和脱敏说明必须齐全。", issues);
            ValidateHumanReviewRecord(record, issues);

            if (record.Annotations is not null)
            {
                if (record.Annotations.FactsAndConstraints is null || string.IsNullOrWhiteSpace(record.Annotations.TargetTone) ||
                    record.Annotations.FormatRequirements is null || record.Annotations.HighRiskKeyFacts is null)
                    Add("annotation-fields", record, "事实/约束、目标语气、格式要求及高风险事实标注必须完整。", issues);
                if ((record.ExpectedDecision == "clarify") != record.Annotations.ClarificationRequired)
                    Add("clarification-mismatch", record, "澄清决策与标注不一致。", issues);
                if (record.RiskLevel == "high" && (record.Annotations.HighRiskKeyFacts is null || record.Annotations.HighRiskKeyFacts.Count == 0))
                    Add("high-risk-facts", record, "高风险样本必须列明关键事实锚点。", issues);
            }

            if (!string.IsNullOrWhiteSpace(record.SemanticFamilyId) && record.Split is "development" or "frozen_test")
            {
                if (knownFamilies.Contains(record.SemanticFamilyId))
                    Add("family-overlap-training", record, "语义族与训练/开发数据重叠。", issues);
                if (familySplits.TryGetValue(record.SemanticFamilyId, out var priorSplit) && priorSplit != record.Split)
                    Add("family-cross-split", record, $"语义族同时出现在 {priorSplit} 与 {record.Split}。", issues);
                else familySplits[record.SemanticFamilyId] = record.Split;
            }

            IEnumerable<string> recordInputs = record.Turns is { Count: > 0 }
                ? record.Turns.Where(turn => turn is not null).Select(turn => turn.UserInput)
                : [record.Input];
            if (record.Split is "development" or "frozen_test")
            {
                foreach (var input in recordInputs.Where(input => !string.IsNullOrWhiteSpace(input)))
                {
                    var inputKey = input.Trim();
                    if (knownInputs.Contains(inputKey)) Add("input-overlap-training", record, "输入或对话轮次与训练/开发数据重复。", issues);
                    if (inputSplits.TryGetValue(inputKey, out var priorSplit) && priorSplit != record.Split)
                        Add("input-cross-split", record, $"相同输入或对话轮次同时出现在 {priorSplit} 与 {record.Split}。", issues);
                    else if (inputSplits.ContainsKey(inputKey))
                        Add("duplicate-input", record, "同一输入或对话轮次重复出现。", issues);
                    else inputSplits[inputKey] = record.Split;
                }
            }

            var checkedText = string.Join("\n", record.Input, record.ReferenceOutput ?? string.Empty,
                record.Id,
                record.SemanticFamilyId,
                record.Source?.SourceKind ?? string.Empty,
                record.Source?.LicenseOrConsentRef ?? string.Empty,
                record.Source?.Deidentification ?? string.Empty,
                GetSensitiveScanContextText(record.Context),
                string.Join("\n", record.Constraints ?? []),
                string.Join("\n", record.Annotations?.FactsAndConstraints ?? []),
                record.Annotations?.TargetTone ?? string.Empty,
                string.Join("\n", record.Annotations?.FormatRequirements ?? []),
                string.Join("\n", record.Annotations?.HighRiskKeyFacts ?? []),
                record.ConversationId ?? string.Empty,
                string.Join("\n", record.Turns?.Select(turn => turn is null ? string.Empty : $"{turn.TurnIndex}:{turn.UserInput}") ?? []));
            checkedText += "\n" + GetSensitiveScanJsonText(System.Text.Json.JsonSerializer.SerializeToElement(record.HumanReview));
            if (ContainsPotentialSensitiveData(checkedText))
                Add("pii", record, "内容或评测标注中检测到疑似手机号、身份证号、邮箱、QQ/微信联系标识或密钥；该启发式检查不能替代人工脱敏审查。", issues);
        }
        return issues;
    }

    public static IReadOnlyList<BlindEvaluationIssue> ValidateHumanReviewEvidence(IEnumerable<BlindEvaluationRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var issues = new List<BlindEvaluationIssue>();
        foreach (var record in records)
        {
            if (record is null)
            {
                issues.Add(new("review-evidence", "dataset", "gold 中包含空记录，无法校验 human_review。"));
                continue;
            }
            ValidateHumanReviewRecord(record, issues);
            var reviewText = GetSensitiveScanJsonText(System.Text.Json.JsonSerializer.SerializeToElement(record.HumanReview));
            if (ContainsPotentialSensitiveData(reviewText))
                Add("pii", record, "human_review 证据中检测到疑似个人信息或密钥；该启发式检查不能替代人工脱敏审查。", issues);
        }
        return issues;
    }

    public static IReadOnlyList<BlindEvaluationIssue> ValidateRecordIntegrity(
        IEnumerable<BlindEvaluationRecord> records,
        IEnumerable<BlindEvaluationKnownRecord>? knownTrainingRecords = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        var items = records.ToArray();
        if (items.Length == 0)
            return [new("dataset-empty", "dataset", "盲评 gold 不得为空。")];

        var issues = Validate(items, items.Length, knownTrainingRecords);
        return issues.Where(issue => !IsAggregateCoverageIssue(issue.Code)).ToArray();
    }

    private static bool IsAggregateCoverageIssue(string code) =>
        code is "dataset-too-small" or "task-coverage" or "split-coverage" || code.StartsWith("coverage-", StringComparison.Ordinal);

    private static void ValidateConversation(BlindEvaluationRecord record, List<BlindEvaluationIssue> issues)
    {
        var hasConversationId = !string.IsNullOrWhiteSpace(record.ConversationId);
        var hasTurns = record.Turns is { Count: > 0 };
        if (!hasConversationId && !hasTurns) return;
        if (!hasConversationId || !hasTurns || record.Turns!.Count < 2)
        {
            Add("conversation-shape", record, "多轮评测必须同时提供 conversation_id 和至少两个 turns。", issues);
            return;
        }
        if (record.Task != "polish") Add("conversation-task", record, "当前多轮回放契约仅适用于 polish 任务。", issues);
        for (var index = 0; index < record.Turns.Count; index++)
        {
            var turn = record.Turns[index];
            if (turn is null || turn.TurnIndex != index + 1 || string.IsNullOrWhiteSpace(turn.UserInput))
                Add("conversation-turn-sequence", record, "turns 必须从 1 开始连续编号，且每轮 user_input 非空。", issues);
        }
        if (record.Turns[^1] is { } finalTurn && !string.Equals(record.Input, finalTurn.UserInput, StringComparison.Ordinal))
            Add("conversation-final-input", record, "record.input 必须与最后一轮 user_input 完全一致，确保单轮消费者保留最后指令。", issues);
    }

    private static void ValidateHumanReviewRecord(BlindEvaluationRecord record, List<BlindEvaluationIssue> issues)
    {
        var review = record.HumanReview;
        if (review is null || review.ReviewStatus is not ("accepted" or "adjudicated") ||
            review.ReviewerIds is null || review.ReviewerIds.Count != 2 || review.ReviewerIds.Any(string.IsNullOrWhiteSpace) ||
            review.ReviewerIds.Select(id => id.Trim()).Distinct(StringComparer.Ordinal).Count() != 2 ||
            (review.ReviewStatus == "adjudicated" &&
                (string.IsNullOrWhiteSpace(review.AdjudicatorId) ||
                 review.ReviewerIds.Any(id => string.Equals(id.Trim(), review.AdjudicatorId.Trim(), StringComparison.Ordinal)))) ||
            (review.ReviewStatus == "accepted" && !string.IsNullOrWhiteSpace(review.AdjudicatorId)))
            Add("double-review", record, "样本须有两名不同评审者；若标为 adjudicated，还须记录不同的第三方裁定者。", issues);
        ValidateGoldReviewEvidence(record, issues);
    }

    public static bool ContainsPotentialSensitiveData(string? text) =>
        !string.IsNullOrWhiteSpace(text) &&
        (Phone.IsMatch(text) || ChineseNationalId.IsMatch(text) || Email.IsMatch(text) || Secret.IsMatch(text) || OpenRouterKey.IsMatch(text) || AnthropicKey.IsMatch(text) || XaiKey.IsMatch(text) || OpenAiProjectKey.IsMatch(text) || ArkKey.IsMatch(text) || AlibabaPlanKey.IsMatch(text) || MiMoTokenPlanKey.IsMatch(text) ||
         CommonCredentialFormat.IsMatch(text) || QqContact.IsMatch(text) || WeChatContact.IsMatch(text));

    public static string ClassifyProductSlice(string task, IReadOnlyDictionary<string, System.Text.Json.JsonElement>? context) => task switch
    {
        "polish" => ClassifyPolishScenario(context),
        "prompt_optimize" => ClassifyPromptCategory(context),
        _ => UnspecifiedOrCustomSlice
    };

    private static string GetSensitiveScanContextText(IReadOnlyDictionary<string, System.Text.Json.JsonElement>? context) =>
        context is null
            ? string.Empty
            : string.Join("\n", context.Select(pair => $"{pair.Key}: {GetSensitiveScanJsonText(pair.Value)}"));

    private static string GetSensitiveScanJsonText(System.Text.Json.JsonElement value) => value.ValueKind switch
    {
        System.Text.Json.JsonValueKind.Object => string.Join("\n", value.EnumerateObject()
            .Select(property => $"{property.Name}: {GetSensitiveScanJsonText(property.Value)}")),
        System.Text.Json.JsonValueKind.Array => string.Join("\n", value.EnumerateArray().Select(GetSensitiveScanJsonText)),
        System.Text.Json.JsonValueKind.String => value.GetString() ?? string.Empty,
        System.Text.Json.JsonValueKind.Number => value.GetRawText(),
        System.Text.Json.JsonValueKind.True => "true",
        System.Text.Json.JsonValueKind.False => "false",
        _ => string.Empty
    };

    public static string ToSafeDiagnosticRecordId(string? recordId) =>
        ContainsPotentialSensitiveData(recordId) ? "[redacted-record-id]" : recordId ?? string.Empty;

    private static void Add(string code, BlindEvaluationRecord record, string message, List<BlindEvaluationIssue> issues)
    {
        issues.Add(new(code, ToSafeDiagnosticRecordId(record.Id), message));
    }

    private static int MinimumCoverage(int total, double ratio) => Math.Max(1, (int)Math.Ceiling(total * ratio));

    private static BlindEvaluationCoverageSliceSummary SummarizeCoverageSlice(IEnumerable<BlindEvaluationRecord> records)
    {
        var items = records.ToArray();
        return new(
            items.Length,
            items.Count(item => item.Task == "polish"),
            items.Count(item => item.Task == "prompt_optimize"),
            items.Count(item => item.RiskLevel == "high"),
            items.Count(item => item.RiskLevel is "low" or "medium"),
            items.Count(item => item.ExpectedDecision == "clarify"),
            items.Count(item => item.Annotations?.FormatRequirements?.Any(value => !string.IsNullOrWhiteSpace(value)) == true),
            items.Count(item =>
                item.Constraints?.Any(value => !string.IsNullOrWhiteSpace(value)) == true ||
                item.Annotations?.FactsAndConstraints?.Any(value => !string.IsNullOrWhiteSpace(value)) == true),
            SummarizeInputStyles(items).Count,
            SummarizeInputStyles(items));
    }

    private static IReadOnlyList<BlindEvaluationTaskSplitCoverageSummary> SummarizeTaskSplitCoverage(IReadOnlyList<BlindEvaluationRecord> records)
    {
        var summaries = new List<BlindEvaluationTaskSplitCoverageSummary>(4);
        foreach (var task in new[] { "polish", "prompt_optimize" })
        foreach (var split in new[] { "development", "frozen_test" })
        {
            var items = records.Where(item => item.Task == task && item.Split == split).ToArray();
            var styleCounts = BlindEvaluationAdmissionProtocol.SupportedInputStyles.ToDictionary(
                style => style,
                style => items.Count(item => string.Equals(item.InputStyle, style, StringComparison.Ordinal)),
                StringComparer.Ordinal);
            var productSliceCounts = task == "polish"
                ? PolishScenarios.Append(UnspecifiedOrCustomSlice).ToDictionary(
                    scenario => scenario,
                    scenario => items.Count(item => ClassifyProductSlice(task, item.Context) == scenario),
                    StringComparer.Ordinal)
                : PromptCategories.Select(category => category.DisplayName).Append(UnspecifiedOrCustomSlice).ToDictionary(
                    category => category,
                    category => items.Count(item => ClassifyProductSlice(task, item.Context) == category),
                    StringComparer.Ordinal);
            summaries.Add(new(
                task,
                split,
                items.Length,
                items.Count(item => item.RiskLevel == "high"),
                items.Count(item => item.RiskLevel is "low" or "medium"),
                items.Count(item => item.ExpectedDecision == "clarify"),
                items.Count(item => item.Annotations?.FormatRequirements?.Any(value => !string.IsNullOrWhiteSpace(value)) == true),
                items.Count(item =>
                    item.Constraints?.Any(value => !string.IsNullOrWhiteSpace(value)) == true ||
                    item.Annotations?.FactsAndConstraints?.Any(value => !string.IsNullOrWhiteSpace(value)) == true),
                styleCounts.Count(pair => pair.Value > 0),
                styleCounts,
                productSliceCounts));
        }

        return summaries;
    }

    private static string ClassifyPolishScenario(IReadOnlyDictionary<string, System.Text.Json.JsonElement>? context)
    {
        if (context is not null && context.TryGetValue("scenario", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var scenario = value.GetString();
            if (PolishScenarios.Contains(scenario, StringComparer.Ordinal)) return scenario!;
        }

        return UnspecifiedOrCustomSlice;
    }

    private static string ClassifyPromptCategory(IReadOnlyDictionary<string, System.Text.Json.JsonElement>? context)
    {
        // The product workflow defaults a missing/null category to General.
        var categoryText = "General";
        if (context is not null && context.TryGetValue("category", out var value))
        {
            if (value.ValueKind == System.Text.Json.JsonValueKind.Null) categoryText = "General";
            else if (value.ValueKind == System.Text.Json.JsonValueKind.String) categoryText = value.GetString() ?? "General";
            else return UnspecifiedOrCustomSlice;
        }

        var category = PromptCategories.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, categoryText, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.DisplayName, categoryText, StringComparison.Ordinal));
        return category.DisplayName ?? UnspecifiedOrCustomSlice;
    }

    private static IReadOnlyDictionary<string, int> SummarizeInputStyles(IEnumerable<BlindEvaluationRecord> records) =>
        records.Where(item => InputStyles.Contains(item.InputStyle))
            .GroupBy(item => item.InputStyle, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    private static void ValidateCoverageSlice(BlindEvaluationCoverageSliceSummary coverage, string splitName, List<BlindEvaluationIssue> issues)
    {
        var balancedMinimum = MinimumCoverage(coverage.SampleCount, BlindEvaluationAdmissionProtocol.TaskMinimumRatio);
        if (coverage.PolishCount < balancedMinimum || coverage.PromptOptimizeCount < balancedMinimum)
            AddDatasetCoverage($"coverage-{splitName}-task", $"{splitName} 中 polish 与 prompt_optimize 各至少需要 {balancedMinimum} 条，当前分别为 {coverage.PolishCount} 与 {coverage.PromptOptimizeCount} 条。", issues);
        var highRiskMinimum = MinimumCoverage(coverage.SampleCount, BlindEvaluationAdmissionProtocol.HighRiskMinimumRatio);
        if (coverage.HighRiskCount < highRiskMinimum)
            AddDatasetCoverage($"coverage-{splitName}-high-risk", $"{splitName} 中高风险样本至少需要 {highRiskMinimum} 条（split 内 10%），当前 {coverage.HighRiskCount} 条。", issues);
        var routineMinimum = MinimumCoverage(coverage.SampleCount, BlindEvaluationAdmissionProtocol.RoutineMinimumRatio);
        if (coverage.RoutineCount < routineMinimum)
            AddDatasetCoverage($"coverage-{splitName}-standard", $"{splitName} 中常规样本至少需要 {routineMinimum} 条（split 内 50%），当前 {coverage.RoutineCount} 条。", issues);
        var clarificationMinimum = MinimumCoverage(coverage.SampleCount, BlindEvaluationAdmissionProtocol.ClarificationMinimumRatio);
        if (coverage.ClarifyCount < clarificationMinimum)
            AddDatasetCoverage($"coverage-{splitName}-clarify", $"{splitName} 中需澄清样本至少需要 {clarificationMinimum} 条（split 内 5%），当前 {coverage.ClarifyCount} 条。", issues);
        var formatMinimum = MinimumCoverage(coverage.SampleCount, BlindEvaluationAdmissionProtocol.FormatMinimumRatio);
        if (coverage.FormatRequirementCount < formatMinimum)
            AddDatasetCoverage($"coverage-{splitName}-format", $"{splitName} 中明确格式要求样本至少需要 {formatMinimum} 条（split 内 5%），当前 {coverage.FormatRequirementCount} 条。", issues);
        var anchorMinimum = MinimumCoverage(coverage.SampleCount, BlindEvaluationAdmissionProtocol.FactOrConstraintAnchorMinimumRatio);
        if (coverage.FactOrConstraintAnchorCount < anchorMinimum)
            AddDatasetCoverage($"coverage-{splitName}-facts-constraints", $"{splitName} 中事实/约束锚点样本至少需要 {anchorMinimum} 条（split 内 50%），当前 {coverage.FactOrConstraintAnchorCount} 条。", issues);
        if (coverage.DistinctInputStyleCount < BlindEvaluationAdmissionProtocol.MinimumInputStylesPerSplit)
            AddDatasetCoverage($"coverage-{splitName}-input-style-diversity", $"{splitName} 至少需要覆盖 {BlindEvaluationAdmissionProtocol.MinimumInputStylesPerSplit} 种受支持的输入表达风格，当前 {coverage.DistinctInputStyleCount} 种。", issues);
    }

    private static void ValidateGoldReviewEvidence(BlindEvaluationRecord record, List<BlindEvaluationIssue> issues)
    {
        var review = record.HumanReview;
        var votes = review?.IndependentAnnotations;
        if (votes is null || votes.Count != 2 || votes.Any(vote => vote is null || string.IsNullOrWhiteSpace(vote.ReviewerId) || vote.Labels is null))
        {
            Add("review-evidence", record, "必须保留两名评审者各自独立提交的完整 gold 标注证据。", issues);
            return;
        }

        if (review!.ReviewerIds is null || review.ReviewerIds.Count != 2 || review.ReviewerIds.Any(string.IsNullOrWhiteSpace) ||
            votes.Select(vote => vote.ReviewerId.Trim()).Distinct(StringComparer.Ordinal).Count() != 2 ||
            !votes.Select(vote => vote.ReviewerId.Trim()).ToHashSet(StringComparer.Ordinal)
                .SetEquals(review.ReviewerIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim())))
        {
            Add("review-evidence", record, "独立标注证据中的评审者必须与 human_review.reviewer_ids 一一对应。", issues);
            return;
        }

        var reviewersAgree = GoldLabelsEqual(votes[0].Labels, votes[1].Labels);
        if (review.ReviewStatus == "accepted")
        {
            if (!reviewersAgree)
            {
                Add("review-disagreement", record, "评审标为 accepted，但两份独立 gold 标注存在分歧；必须改为第三方裁定。", issues);
                return;
            }
            if (review.Adjudication is not null || !string.IsNullOrWhiteSpace(review.AdjudicatorId))
                Add("double-review", record, "两名评审者一致时不得附加 adjudication 记录。", issues);
            if (!GoldLabelsMatchRecord(votes[0].Labels, record))
                Add("review-label-mismatch", record, "最终 gold 标签必须与两名一致评审者的独立标注完全一致。", issues);
            return;
        }

        if (reviewersAgree)
            Add("review-status", record, "两名评审者一致时 review_status 应为 accepted，而非 adjudicated。", issues);
        var adjudication = review.Adjudication;
        if (adjudication is null || string.IsNullOrWhiteSpace(review.AdjudicatorId) ||
            !string.Equals(adjudication.ReviewerId?.Trim(), review.AdjudicatorId.Trim(), StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(adjudication.ReviewerId) ||
            review.ReviewerIds.Any(id => string.Equals(id?.Trim(), adjudication.ReviewerId.Trim(), StringComparison.Ordinal)) ||
            adjudication.Labels is null)
        {
            Add("adjudication-evidence", record, "分歧必须有不同于两名评审者的第三人及其完整裁定标签。", issues);
            return;
        }
        if (!GoldLabelsMatchRecord(adjudication.Labels, record))
            Add("adjudication-label-mismatch", record, "最终 gold 标签必须与第三方裁定标签完全一致。", issues);
    }

    private static bool GoldLabelsMatchRecord(BlindEvaluationGoldLabelSet labels, BlindEvaluationRecord record) =>
        string.Equals(labels.Task, record.Task, StringComparison.Ordinal) &&
        string.Equals(labels.InputStyle, record.InputStyle, StringComparison.Ordinal) &&
        EqualLabelList(labels.Constraints, record.Constraints) &&
        string.Equals(labels.RiskLevel, record.RiskLevel, StringComparison.Ordinal) &&
        string.Equals(labels.ExpectedDecision, record.ExpectedDecision, StringComparison.Ordinal) &&
        string.Equals(labels.ReferenceOutput?.Trim(), record.ReferenceOutput?.Trim(), StringComparison.Ordinal) &&
        AnnotationsEqual(labels.Annotations, record.Annotations);

    private static bool GoldLabelsEqual(BlindEvaluationGoldLabelSet left, BlindEvaluationGoldLabelSet right) =>
        string.Equals(left.Task, right.Task, StringComparison.Ordinal) &&
        string.Equals(left.InputStyle, right.InputStyle, StringComparison.Ordinal) &&
        EqualLabelList(left.Constraints, right.Constraints) &&
        string.Equals(left.RiskLevel, right.RiskLevel, StringComparison.Ordinal) &&
        string.Equals(left.ExpectedDecision, right.ExpectedDecision, StringComparison.Ordinal) &&
        string.Equals(left.ReferenceOutput?.Trim(), right.ReferenceOutput?.Trim(), StringComparison.Ordinal) &&
        AnnotationsEqual(left.Annotations, right.Annotations);

    private static bool AnnotationsEqual(BlindEvaluationAnnotations? left, BlindEvaluationAnnotations? right) =>
        left is not null && right is not null &&
        EqualLabelList(left.FactsAndConstraints, right.FactsAndConstraints) &&
        string.Equals(left.TargetTone?.Trim(), right.TargetTone?.Trim(), StringComparison.Ordinal) &&
        EqualLabelList(left.FormatRequirements, right.FormatRequirements) &&
        left.ClarificationRequired == right.ClarificationRequired &&
        EqualLabelList(left.HighRiskKeyFacts, right.HighRiskKeyFacts);

    private static bool EqualLabelList(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
    {
        if (left is null || right is null || left.Count != right.Count) return false;
        return left.Select(value => value?.Trim() ?? string.Empty).OrderBy(value => value, StringComparer.Ordinal)
            .SequenceEqual(right.Select(value => value?.Trim() ?? string.Empty).OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal);
    }

    private static void AddDatasetCoverage(string code, string message, List<BlindEvaluationIssue> issues) =>
        issues.Add(new(code, "dataset", message));
}
