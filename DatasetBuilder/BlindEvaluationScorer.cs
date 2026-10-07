namespace Huaxiazi.DatasetBuilder;

public sealed record BlindEvaluationReview(
    string ReviewerId,
    bool FactConstraintRetained,
    bool DirectlyUsable,
    bool ToneMatched,
    bool ClarificationDecisionCorrect,
    bool HighRiskKeyFactReversed,
    bool SafetyPass,
    int FidelityScore = 3,
    int TaskCompletionScore = 3,
    int NaturalnessScore = 3,
    int DirectUsabilityScore = 3,
    int SafetyScore = 5);

public sealed record BlindEvaluationAdjudication(
    string AdjudicatorId,
    bool FactConstraintRetained,
    bool DirectlyUsable,
    bool ToneMatched,
    bool ClarificationDecisionCorrect,
    bool HighRiskKeyFactReversed,
    bool SafetyPass,
    int FidelityScore = 3,
    int TaskCompletionScore = 3,
    int NaturalnessScore = 3,
    int DirectUsabilityScore = 3,
    int SafetyScore = 5);

public sealed record BlindEvaluationTelemetry(
    double? LatencyMilliseconds,
    long? ApiInputTokens,
    long? ApiOutputTokens,
    double? LocalTokensPerSecond,
    long? LocalPeakMemoryBytes,
    double? LocalModelLoadMilliseconds,
    string? ErrorCategory = null,
    long? ApiCacheReadInputTokens = null,
    long? ApiCacheCreationInputTokens = null);

public sealed record BlindEvaluationPrediction(
    string Id,
    string CandidateId,
    string Output,
    bool SchemaValid,
    IReadOnlyList<BlindEvaluationReview> Reviews,
    BlindEvaluationAdjudication? Adjudication,
    BlindEvaluationTelemetry? Telemetry,
    string Status = "");

public sealed record BlindEvaluationPairwiseVote(string ReviewerId, string Choice);

public sealed record BlindEvaluationPairwiseAdjudication(string AdjudicatorId, string Choice);

public sealed record BlindEvaluationPairwiseComparison(
    string Id,
    string LeftCandidateId,
    string RightCandidateId,
    IReadOnlyList<BlindEvaluationPairwiseVote> Votes,
    BlindEvaluationPairwiseAdjudication? Adjudication);

public sealed record BlindEvaluationCandidateReport(
    string CandidateId,
    int Total,
    int Predictions,
    int FailedRequests,
    double CoverageRate,
    double SchemaValidRate,
    double FactConstraintRetentionRate,
    double DirectUsabilityRate,
    double ToneMatchRate,
    double ClarificationDecisionAccuracy,
    double MeanFidelityScore,
    double MeanTaskCompletionScore,
    double MeanNaturalnessScore,
    double MeanDirectUsabilityScore,
    double MeanSafetyScore,
    int HighRiskKeyFactReversals,
    double SafetyPassRate,
    double? LatencyP50Milliseconds,
    double? LatencyP95Milliseconds,
    long? ApiInputTokens,
    long? ApiOutputTokens,
    long? ApiCacheReadInputTokens,
    long? ApiCacheCreationInputTokens,
    double? LocalTokensPerSecondP50,
    long? LocalPeakMemoryBytes,
    double? LocalModelLoadP50Milliseconds,
    IReadOnlyDictionary<string, int> ErrorCountsByCategory,
    bool Passed);

public sealed record BlindEvaluationProductSliceReport(
    string CandidateId,
    string Task,
    string Split,
    string ProductSlice,
    int SampleCount,
    int PredictionCount,
    int FailedRequestCount,
    int MissingPredictionCount,
    int InvalidPredictionCount,
    double CoverageRate,
    double SchemaValidRate,
    double FactConstraintRetentionRate,
    double DirectUsabilityRate,
    double ToneMatchRate,
    double ClarificationDecisionAccuracy,
    int HighRiskKeyFactReversals,
    double SafetyPassRate,
    double MeanFidelityScore,
    double MeanTaskCompletionScore,
    double MeanNaturalnessScore,
    double MeanDirectUsabilityScore,
    double MeanSafetyScore)
{
    public int TelemetryPredictionCount { get; init; }
    public int LatencyObservationCount { get; init; }
    public double? LatencyP50Milliseconds { get; init; }
    public double? LatencyP95Milliseconds { get; init; }
    public int ApiInputTokenObservationCount { get; init; }
    public long? ApiInputTokens { get; init; }
    public int ApiOutputTokenObservationCount { get; init; }
    public long? ApiOutputTokens { get; init; }
    public int ApiCacheReadInputTokenObservationCount { get; init; }
    public long? ApiCacheReadInputTokens { get; init; }
    public int ApiCacheCreationInputTokenObservationCount { get; init; }
    public long? ApiCacheCreationInputTokens { get; init; }
    public int LocalTokensPerSecondObservationCount { get; init; }
    public double? LocalTokensPerSecondP50 { get; init; }
    public int LocalPeakMemoryObservationCount { get; init; }
    public long? LocalPeakMemoryBytes { get; init; }
    public int LocalModelLoadObservationCount { get; init; }
    public double? LocalModelLoadP50Milliseconds { get; init; }
    public IReadOnlyDictionary<string, int> ErrorCountsByCategory { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);
}

public sealed record BlindEvaluationPairwiseReport(
    string LeftCandidateId,
    string RightCandidateId,
    int ComparableCount,
    int LeftWins,
    int RightWins,
    int Ties,
    double LeftWinRate,
    int DecisiveFamilyCount,
    int FamilyLeftWins,
    int FamilyRightWins,
    int FamilyTies,
    double FamilyLeftWinRate,
    double FamilyLeftWinLower95,
    double FamilyLeftWinUpper95,
    bool Significant);

public sealed record BlindEvaluationScoringIssue(string Code, string RecordId, string Message);

public sealed record BlindEvaluationScoreReport(
    int GoldCount,
    IReadOnlyList<BlindEvaluationCandidateReport> Candidates,
    IReadOnlyList<BlindEvaluationPairwiseReport> PairwiseComparisons,
    IReadOnlyList<BlindEvaluationScoringIssue> Issues,
    bool Passed)
{
    public IReadOnlyList<BlindEvaluationProductSliceReport> ProductSliceReports { get; init; } = [];
}

/// <summary>Summarizes blinded human judgements and content-free provider telemetry.</summary>
public static class BlindEvaluationScorer
{
    private static readonly HashSet<string> ErrorCategories = new(StringComparer.Ordinal)
    {
        "timeout", "rate_limited", "authentication", "provider_error", "network", "empty_output", "schema_rejected", "workflow_rejected", "runner_error", "cancelled", "other"
    };

    private sealed class PairwiseBucket(string leftCandidateId, string rightCandidateId)
    {
        public string LeftCandidateId { get; } = leftCandidateId;
        public string RightCandidateId { get; } = rightCandidateId;
        public List<(string SemanticFamilyId, string Choice)> Choices { get; } = [];
    }

    public static BlindEvaluationScoreReport Evaluate(
        IReadOnlyList<BlindEvaluationRecord> gold,
        IReadOnlyList<BlindEvaluationPrediction> predictions,
        IReadOnlyList<BlindEvaluationPairwiseComparison>? comparisons = null)
    {
        ArgumentNullException.ThrowIfNull(gold);
        ArgumentNullException.ThrowIfNull(predictions);
        comparisons ??= [];
        var issues = new List<BlindEvaluationScoringIssue>();
        if (gold.Count == 0) return new(0, [], [], [new("empty-gold", "dataset", "盲评 gold 数据集为空。")], false);

        var goldGroups = gold.GroupBy(item => item.Id, StringComparer.Ordinal).ToArray();
        foreach (var group in goldGroups.Where(group => group.Count() > 1))
            issues.Add(new("duplicate-gold-id", BlindEvaluationAuditor.ToSafeDiagnosticRecordId(group.Key), "gold 数据中存在重复 id。"));
        var goldById = goldGroups.ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var prediction in predictions)
        {
            if (!goldById.ContainsKey(prediction.Id))
                issues.Add(new("unknown-prediction-id", BlindEvaluationAuditor.ToSafeDiagnosticRecordId(prediction.Id), "预测 id 不存在于 gold 数据中。"));
            if (string.IsNullOrWhiteSpace(prediction.CandidateId))
                issues.Add(new("candidate-id", BlindEvaluationAuditor.ToSafeDiagnosticRecordId(prediction.Id), "预测缺少盲化 candidate_id。"));
            if (prediction.Status is not ("success" or "error" or "cancelled"))
                issues.Add(new("prediction-status", BlindEvaluationAuditor.ToSafeDiagnosticRecordId(prediction.Id), "预测状态必须为 success、error 或 cancelled。"));
            if (prediction.Status == "success" && string.IsNullOrWhiteSpace(prediction.Output))
                issues.Add(new("empty-output", BlindEvaluationAuditor.ToSafeDiagnosticRecordId(prediction.Id), "预测输出不能为空。"));
            if (prediction.Status == "success" && BlindEvaluationAuditor.ContainsPotentialSensitiveData(prediction.Output))
                issues.Add(new("pii-output", BlindEvaluationAuditor.ToSafeDiagnosticRecordId(prediction.Id), "候选输出中检测到疑似手机号、身份证号、邮箱、QQ/微信联系标识或密钥；请隔离并人工脱敏复核后重新生成评测产物。"));
            if (prediction.Status is "error" or "cancelled")
            {
                if (prediction.SchemaValid || !string.IsNullOrWhiteSpace(prediction.Output) || prediction.Reviews is { Count: > 0 })
                    issues.Add(new("failed-prediction-content", BlindEvaluationAuditor.ToSafeDiagnosticRecordId(prediction.Id), "失败请求不应包含候选输出或人工评分。"));
                if (prediction.Telemetry is null || string.IsNullOrWhiteSpace(prediction.Telemetry.ErrorCategory) ||
                    !ErrorCategories.Contains(prediction.Telemetry.ErrorCategory) ||
                    (prediction.Status == "cancelled" && prediction.Telemetry.ErrorCategory != "cancelled"))
                    issues.Add(new("missing-error-category", BlindEvaluationAuditor.ToSafeDiagnosticRecordId(prediction.Id), "失败请求必须记录规范化错误类别。"));
            }
            if (prediction.Telemetry is { } telemetry &&
                ((telemetry.LatencyMilliseconds.HasValue && (!double.IsFinite(telemetry.LatencyMilliseconds.Value) || telemetry.LatencyMilliseconds.Value < 0)) ||
                 (telemetry.ApiInputTokens.HasValue && telemetry.ApiInputTokens.Value < 0) ||
                 (telemetry.ApiOutputTokens.HasValue && telemetry.ApiOutputTokens.Value < 0) ||
                 (telemetry.ApiCacheReadInputTokens.HasValue && telemetry.ApiCacheReadInputTokens.Value < 0) ||
                 (telemetry.ApiCacheCreationInputTokens.HasValue && telemetry.ApiCacheCreationInputTokens.Value < 0) ||
                 (telemetry.LocalTokensPerSecond.HasValue && (!double.IsFinite(telemetry.LocalTokensPerSecond.Value) || telemetry.LocalTokensPerSecond.Value < 0)) ||
                 (telemetry.LocalPeakMemoryBytes.HasValue && telemetry.LocalPeakMemoryBytes.Value < 0) ||
                 (telemetry.LocalModelLoadMilliseconds.HasValue && (!double.IsFinite(telemetry.LocalModelLoadMilliseconds.Value) || telemetry.LocalModelLoadMilliseconds.Value < 0))))
                issues.Add(new("invalid-telemetry", BlindEvaluationAuditor.ToSafeDiagnosticRecordId(prediction.Id), "推理遥测值必须是有限的非负数。"));
            if (prediction.Telemetry?.ErrorCategory is { Length: > 0 } category && !ErrorCategories.Contains(category))
                issues.Add(new("error-category", BlindEvaluationAuditor.ToSafeDiagnosticRecordId(prediction.Id), "遥测包含未知的规范化错误类别。"));
        }

        var candidates = predictions.Where(item => !string.IsNullOrWhiteSpace(item.CandidateId))
            .Select(item => item.CandidateId).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var reports = new List<BlindEvaluationCandidateReport>();
        var productSliceReports = new List<BlindEvaluationProductSliceReport>();
        var candidatePredictionMaps = new Dictionary<string, Dictionary<string, BlindEvaluationPrediction>>(StringComparer.Ordinal);
        foreach (var candidateId in candidates)
        {
            var groups = predictions.Where(item => item.CandidateId == candidateId && goldById.ContainsKey(item.Id))
                .GroupBy(item => item.Id, StringComparer.Ordinal).ToArray();
            foreach (var group in groups.Where(group => group.Count() > 1))
                issues.Add(new("duplicate-prediction", BlindEvaluationAuditor.ToSafeDiagnosticRecordId(group.Key), "某候选对同一样本有重复预测。"));
            var attempts = groups.Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
            var attemptedIds = groups.Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
            var unique = attempts.Where(pair => pair.Value.Status == "success").ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            candidatePredictionMaps[candidateId] = unique;

            var factCount = 0; var usableCount = 0; var toneCount = 0; var clarificationCount = 0; var safetyCount = 0;
            double fidelityScore = 0; double taskCompletionScore = 0; double naturalnessScore = 0; double directUsabilityScore = 0; double safetyScore = 0;
            var highRiskReversals = 0; var schemaCount = 0;
            var effectiveReviews = new Dictionary<string, BlindEvaluationReview?>(StringComparer.Ordinal);
            foreach (var (id, prediction) in unique)
            {
                var effective = ResolveReviews(prediction, issues);
                effectiveReviews[id] = effective;
                if (effective is null) continue;
                factCount += effective.FactConstraintRetained ? 1 : 0;
                usableCount += effective.DirectlyUsable ? 1 : 0;
                toneCount += effective.ToneMatched ? 1 : 0;
                clarificationCount += effective.ClarificationDecisionCorrect ? 1 : 0;
                safetyCount += effective.SafetyPass ? 1 : 0;
                fidelityScore += effective.FidelityScore;
                taskCompletionScore += effective.TaskCompletionScore;
                naturalnessScore += effective.NaturalnessScore;
                directUsabilityScore += effective.DirectUsabilityScore;
                safetyScore += effective.SafetyScore;
                if (goldById[id].RiskLevel == "high" && effective.HighRiskKeyFactReversed) highRiskReversals++;
                if (prediction.SchemaValid) schemaCount++;
            }

            var total = goldById.Count;
            var telemetry = attempts.Values.Select(item => item.Telemetry).Where(item => item is not null).Cast<BlindEvaluationTelemetry>().ToArray();
            var errorCounts = telemetry.Where(item => !string.IsNullOrWhiteSpace(item.ErrorCategory))
                .GroupBy(item => item.ErrorCategory!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            reports.Add(new BlindEvaluationCandidateReport(
                candidateId,
                total,
                unique.Count,
                total - unique.Count,
                (double)unique.Count / total,
                (double)schemaCount / total,
                (double)factCount / total,
                (double)usableCount / total,
                (double)toneCount / total,
                (double)clarificationCount / total,
                fidelityScore / total,
                taskCompletionScore / total,
                naturalnessScore / total,
                directUsabilityScore / total,
                safetyScore / total,
                highRiskReversals,
                (double)safetyCount / total,
                Percentile(telemetry.Where(item => item.LatencyMilliseconds.HasValue).Select(item => item.LatencyMilliseconds!.Value), .50),
                Percentile(telemetry.Where(item => item.LatencyMilliseconds.HasValue).Select(item => item.LatencyMilliseconds!.Value), .95),
                SumCompleteOrNull(telemetry.Select(item => item.ApiInputTokens)),
                SumCompleteOrNull(telemetry.Select(item => item.ApiOutputTokens)),
                SumCompleteOrNull(telemetry.Select(item => item.ApiCacheReadInputTokens)),
                SumCompleteOrNull(telemetry.Select(item => item.ApiCacheCreationInputTokens)),
                Percentile(telemetry.Where(item => item.LocalTokensPerSecond.HasValue).Select(item => item.LocalTokensPerSecond!.Value), .50),
                MaxOrNull(telemetry.Select(item => item.LocalPeakMemoryBytes)),
                Percentile(telemetry.Where(item => item.LocalModelLoadMilliseconds.HasValue).Select(item => item.LocalModelLoadMilliseconds!.Value), .50),
                errorCounts,
                unique.Count == total && (double)schemaCount / total >= .99 && (double)factCount / total >= .98 &&
                (double)usableCount / total >= .90 && highRiskReversals == 0 && issues.Count == 0));

            var sliceGroups = goldById.Values
                .GroupBy(record => (
                    record.Task,
                    record.Split,
                    ProductSlice: BlindEvaluationAuditor.ClassifyProductSlice(record.Task, record.Context)))
                .OrderBy(group => group.Key.Task, StringComparer.Ordinal)
                .ThenBy(group => group.Key.Split, StringComparer.Ordinal)
                .ThenBy(group => group.Key.ProductSlice, StringComparer.Ordinal);
            foreach (var group in sliceGroups)
            {
                var records = group.ToArray();
                var successfulPredictions = records.Where(record => unique.ContainsKey(record.Id)).ToArray();
                var failedRequestCount = records.Count(record =>
                    attempts.TryGetValue(record.Id, out var attempt) && attempt.Status is "error" or "cancelled");
                var missingPredictionCount = records.Count(record => !attemptedIds.Contains(record.Id));
                var invalidPredictionCount = records.Count(record => attemptedIds.Contains(record.Id) &&
                    !unique.ContainsKey(record.Id) &&
                    (!attempts.TryGetValue(record.Id, out var attempt) || attempt.Status is not ("error" or "cancelled")));
                var rated = records
                    .Select(record => (Record: record, Review: effectiveReviews.GetValueOrDefault(record.Id)))
                    .Where(item => item.Review is not null)
                    .Select(item => (item.Record, Review: item.Review!))
                    .ToArray();
                var sliceFactCount = rated.Count(item => item.Review.FactConstraintRetained);
                var sliceUsableCount = rated.Count(item => item.Review.DirectlyUsable);
                var sliceToneCount = rated.Count(item => item.Review.ToneMatched);
                var sliceClarificationCount = rated.Count(item => item.Review.ClarificationDecisionCorrect);
                var sliceSafetyCount = rated.Count(item => item.Review.SafetyPass);
                var sliceHighRiskReversals = rated.Count(item => item.Record.RiskLevel == "high" && item.Review.HighRiskKeyFactReversed);
                var sliceSchemaCount = successfulPredictions.Count(record => unique[record.Id].SchemaValid);
                var sliceFidelityScore = rated.Sum(item => item.Review.FidelityScore);
                var sliceTaskCompletionScore = rated.Sum(item => item.Review.TaskCompletionScore);
                var sliceNaturalnessScore = rated.Sum(item => item.Review.NaturalnessScore);
                var sliceDirectUsabilityScore = rated.Sum(item => item.Review.DirectUsabilityScore);
                var sliceSafetyScore = rated.Sum(item => item.Review.SafetyScore);
                var sampleCount = records.Length;
                var sliceAttempts = records
                    .Where(record => attempts.ContainsKey(record.Id))
                    .Select(record => attempts[record.Id])
                    .ToArray();
                var sliceTelemetry = sliceAttempts
                    .Where(item => item.Telemetry is not null)
                    .Select(item => item.Telemetry!)
                    .ToArray();
                productSliceReports.Add(new(
                    candidateId,
                    group.Key.Task,
                    group.Key.Split,
                    group.Key.ProductSlice,
                    sampleCount,
                    successfulPredictions.Length,
                    failedRequestCount,
                    missingPredictionCount,
                    invalidPredictionCount,
                    (double)successfulPredictions.Length / sampleCount,
                    (double)sliceSchemaCount / sampleCount,
                    (double)sliceFactCount / sampleCount,
                    (double)sliceUsableCount / sampleCount,
                    (double)sliceToneCount / sampleCount,
                    (double)sliceClarificationCount / sampleCount,
                    sliceHighRiskReversals,
                    (double)sliceSafetyCount / sampleCount,
                    sliceFidelityScore / sampleCount,
                    sliceTaskCompletionScore / sampleCount,
                    sliceNaturalnessScore / sampleCount,
                    sliceDirectUsabilityScore / sampleCount,
                    sliceSafetyScore / sampleCount)
                {
                    TelemetryPredictionCount = sliceTelemetry.Length,
                    LatencyObservationCount = sliceTelemetry.Count(item => item.LatencyMilliseconds.HasValue),
                    LatencyP50Milliseconds = Percentile(sliceTelemetry.Where(item => item.LatencyMilliseconds.HasValue).Select(item => item.LatencyMilliseconds!.Value), .50),
                    LatencyP95Milliseconds = Percentile(sliceTelemetry.Where(item => item.LatencyMilliseconds.HasValue).Select(item => item.LatencyMilliseconds!.Value), .95),
                    ApiInputTokenObservationCount = sliceTelemetry.Count(item => item.ApiInputTokens.HasValue),
                    ApiInputTokens = SumCompleteOrNull(sliceAttempts.Select(item => item.Telemetry?.ApiInputTokens)),
                    ApiOutputTokenObservationCount = sliceTelemetry.Count(item => item.ApiOutputTokens.HasValue),
                    ApiOutputTokens = SumCompleteOrNull(sliceAttempts.Select(item => item.Telemetry?.ApiOutputTokens)),
                    ApiCacheReadInputTokenObservationCount = sliceTelemetry.Count(item => item.ApiCacheReadInputTokens.HasValue),
                    ApiCacheReadInputTokens = SumCompleteOrNull(sliceAttempts.Select(item => item.Telemetry?.ApiCacheReadInputTokens)),
                    ApiCacheCreationInputTokenObservationCount = sliceTelemetry.Count(item => item.ApiCacheCreationInputTokens.HasValue),
                    ApiCacheCreationInputTokens = SumCompleteOrNull(sliceAttempts.Select(item => item.Telemetry?.ApiCacheCreationInputTokens)),
                    LocalTokensPerSecondObservationCount = sliceTelemetry.Count(item => item.LocalTokensPerSecond.HasValue),
                    LocalTokensPerSecondP50 = Percentile(sliceTelemetry.Where(item => item.LocalTokensPerSecond.HasValue).Select(item => item.LocalTokensPerSecond!.Value), .50),
                    LocalPeakMemoryObservationCount = sliceTelemetry.Count(item => item.LocalPeakMemoryBytes.HasValue),
                    LocalPeakMemoryBytes = MaxOrNull(sliceTelemetry.Select(item => item.LocalPeakMemoryBytes)),
                    LocalModelLoadObservationCount = sliceTelemetry.Count(item => item.LocalModelLoadMilliseconds.HasValue),
                    LocalModelLoadP50Milliseconds = Percentile(sliceTelemetry.Where(item => item.LocalModelLoadMilliseconds.HasValue).Select(item => item.LocalModelLoadMilliseconds!.Value), .50),
                    ErrorCountsByCategory = sliceTelemetry.Where(item => !string.IsNullOrWhiteSpace(item.ErrorCategory))
                        .GroupBy(item => item.ErrorCategory!, StringComparer.Ordinal)
                        .ToDictionary(error => error.Key, error => error.Count(), StringComparer.Ordinal)
                });
            }
        }

        var pairwiseBuckets = new Dictionary<(string First, string Second), PairwiseBucket>();
        var pairKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var comparison in comparisons)
        {
            if (!goldById.ContainsKey(comparison.Id))
            {
                issues.Add(new("unknown-comparison-id", BlindEvaluationAuditor.ToSafeDiagnosticRecordId(comparison.Id), "成对比较 id 不存在于 gold 数据中。"));
                continue;
            }
            if (string.IsNullOrWhiteSpace(comparison.LeftCandidateId) || string.IsNullOrWhiteSpace(comparison.RightCandidateId) ||
                comparison.LeftCandidateId == comparison.RightCandidateId)
            {
                issues.Add(new("comparison-candidates", BlindEvaluationAuditor.ToSafeDiagnosticRecordId(comparison.Id), "成对比较必须包含两个不同候选。"));
                continue;
            }
            var firstCandidate = string.CompareOrdinal(comparison.LeftCandidateId, comparison.RightCandidateId) < 0
                ? comparison.LeftCandidateId : comparison.RightCandidateId;
            var secondCandidate = firstCandidate == comparison.LeftCandidateId ? comparison.RightCandidateId : comparison.LeftCandidateId;
            var pairKey = comparison.Id + "\0" + firstCandidate + "\0" + secondCandidate;
            if (!pairKeys.Add(pairKey))
            {
                issues.Add(new("duplicate-comparison", BlindEvaluationAuditor.ToSafeDiagnosticRecordId(comparison.Id), "同一样本和候选组合被重复比较。"));
                continue;
            }
            if (!candidatePredictionMaps.TryGetValue(comparison.LeftCandidateId, out var leftPredictions) || !leftPredictions.ContainsKey(comparison.Id) ||
                !candidatePredictionMaps.TryGetValue(comparison.RightCandidateId, out var rightPredictions) || !rightPredictions.ContainsKey(comparison.Id))
            {
                issues.Add(new("comparison-missing-output", BlindEvaluationAuditor.ToSafeDiagnosticRecordId(comparison.Id), "成对比较缺少同一案例的候选输出。"));
                continue;
            }
            var semanticFamilyId = goldById[comparison.Id].SemanticFamilyId;
            if (string.IsNullOrWhiteSpace(semanticFamilyId))
            {
                issues.Add(new("comparison-family", BlindEvaluationAuditor.ToSafeDiagnosticRecordId(comparison.Id), "成对比较缺少语义族标识，无法按独立任务族判断置信区间。"));
                continue;
            }
            var choice = ResolvePairwise(comparison, issues);
            if (choice is null) continue;
            var key = (firstCandidate, secondCandidate);
            if (!pairwiseBuckets.TryGetValue(key, out var bucket))
                pairwiseBuckets[key] = bucket = new PairwiseBucket(comparison.LeftCandidateId, comparison.RightCandidateId);
            var normalizedChoice = comparison.LeftCandidateId == bucket.LeftCandidateId
                ? choice
                : choice switch { "left" => "right", "right" => "left", _ => "tie" };
            bucket.Choices.Add((semanticFamilyId, normalizedChoice));
        }
        var pairwiseReports = pairwiseBuckets.Values
            .Select(bucket => BuildPairwise(bucket.LeftCandidateId, bucket.RightCandidateId, bucket.Choices))
            .ToArray();

        // A late comparison issue must invalidate all promotion decisions too.
        if (issues.Count > 0)
        {
            reports = reports.Select(item => item with { Passed = false }).ToList();
        }
        var passed = issues.Count == 0 && reports.Any(item => item.Passed);
        return new(goldById.Count, reports, pairwiseReports, issues, passed)
        {
            ProductSliceReports = productSliceReports
        };
    }

    private static BlindEvaluationReview? ResolveReviews(BlindEvaluationPrediction prediction, List<BlindEvaluationScoringIssue> issues)
    {
        if (prediction.Reviews is null || prediction.Reviews.Count != 2 ||
            prediction.Reviews.Any(item => string.IsNullOrWhiteSpace(item.ReviewerId)) ||
            prediction.Reviews.Select(item => item.ReviewerId.Trim()).Distinct(StringComparer.Ordinal).Count() != 2)
        {
            issues.Add(new("review-count", BlindEvaluationAuditor.ToSafeDiagnosticRecordId(prediction.Id), "每条候选输出必须有两名不同盲评员的独立评分。"));
            return null;
        }
        var left = prediction.Reviews[0];
        var right = prediction.Reviews[1];
        if (prediction.Reviews.Any(review => !IsValidRating(review.FidelityScore) || !IsValidRating(review.TaskCompletionScore) ||
            !IsValidRating(review.NaturalnessScore) || !IsValidRating(review.DirectUsabilityScore) || !IsValidRating(review.SafetyScore)) ||
            (prediction.Adjudication is { } scoreAdjudication && (!IsValidRating(scoreAdjudication.FidelityScore) || !IsValidRating(scoreAdjudication.TaskCompletionScore) ||
                !IsValidRating(scoreAdjudication.NaturalnessScore) || !IsValidRating(scoreAdjudication.DirectUsabilityScore) || !IsValidRating(scoreAdjudication.SafetyScore))))
        {
            issues.Add(new("rating-range", BlindEvaluationAuditor.ToSafeDiagnosticRecordId(prediction.Id), "人工评分必须在 1 至 5 分之间。"));
            return null;
        }
        if (prediction.Reviews.Any(review => !IsRubricConsistent(review.DirectlyUsable, review.DirectUsabilityScore,
                review.HighRiskKeyFactReversed, review.FidelityScore)) ||
            (prediction.Adjudication is { } rubricAdjudication && !IsRubricConsistent(rubricAdjudication.DirectlyUsable,
                rubricAdjudication.DirectUsabilityScore, rubricAdjudication.HighRiskKeyFactReversed, rubricAdjudication.FidelityScore)))
        {
            issues.Add(new("review-rubric-conflict", BlindEvaluationAuditor.ToSafeDiagnosticRecordId(prediction.Id),
                "人工评分的直接可用性标签/分数或高风险事实反转/保真分违反标注手册的一致性规则。"));
            return null;
        }
        if (!SameScores(left, right))
        {
            if (prediction.Adjudication is null || string.IsNullOrWhiteSpace(prediction.Adjudication.AdjudicatorId) ||
                string.Equals(prediction.Adjudication.AdjudicatorId.Trim(), left.ReviewerId.Trim(), StringComparison.Ordinal) ||
                string.Equals(prediction.Adjudication.AdjudicatorId.Trim(), right.ReviewerId.Trim(), StringComparison.Ordinal))
            {
                issues.Add(new("unresolved-review", BlindEvaluationAuditor.ToSafeDiagnosticRecordId(prediction.Id), "两名盲评员存在分歧，必须由第三人裁定。"));
                return null;
            }
            return new BlindEvaluationReview(prediction.Adjudication.AdjudicatorId,
                prediction.Adjudication.FactConstraintRetained,
                prediction.Adjudication.DirectlyUsable,
                prediction.Adjudication.ToneMatched,
                prediction.Adjudication.ClarificationDecisionCorrect,
                prediction.Adjudication.HighRiskKeyFactReversed,
                prediction.Adjudication.SafetyPass,
                prediction.Adjudication.FidelityScore,
                prediction.Adjudication.TaskCompletionScore,
                prediction.Adjudication.NaturalnessScore,
                prediction.Adjudication.DirectUsabilityScore,
                prediction.Adjudication.SafetyScore);
        }
        return left;
    }

    private static bool IsValidRating(int value) => value is >= 1 and <= 5;

    private static bool IsRubricConsistent(bool directlyUsable, int directUsabilityScore, bool highRiskKeyFactReversed, int fidelityScore) =>
        directlyUsable == (directUsabilityScore >= 4) &&
        (!highRiskKeyFactReversed || fidelityScore == 1);

    private static bool SameScores(BlindEvaluationReview left, BlindEvaluationReview right) =>
        left.FactConstraintRetained == right.FactConstraintRetained &&
        left.DirectlyUsable == right.DirectlyUsable && left.ToneMatched == right.ToneMatched &&
        left.ClarificationDecisionCorrect == right.ClarificationDecisionCorrect &&
        left.HighRiskKeyFactReversed == right.HighRiskKeyFactReversed && left.SafetyPass == right.SafetyPass &&
        left.FidelityScore == right.FidelityScore && left.TaskCompletionScore == right.TaskCompletionScore &&
        left.NaturalnessScore == right.NaturalnessScore && left.DirectUsabilityScore == right.DirectUsabilityScore &&
        left.SafetyScore == right.SafetyScore;

    private static string? ResolvePairwise(BlindEvaluationPairwiseComparison comparison, List<BlindEvaluationScoringIssue> issues)
    {
        if (comparison.Votes is null || comparison.Votes.Count != 2 ||
            comparison.Votes.Any(vote => string.IsNullOrWhiteSpace(vote.ReviewerId) || vote.Choice is not ("left" or "right" or "tie")) ||
            comparison.Votes.Select(vote => vote.ReviewerId.Trim()).Distinct(StringComparer.Ordinal).Count() != 2)
        {
            issues.Add(new("pairwise-review-count", BlindEvaluationAuditor.ToSafeDiagnosticRecordId(comparison.Id), "每组成对比较必须有两名不同评审者的有效投票。"));
            return null;
        }
        var first = comparison.Votes[0].Choice;
        var second = comparison.Votes[1].Choice;
        if (first != second)
        {
            var adjudicatorId = comparison.Adjudication?.AdjudicatorId?.Trim();
            if (comparison.Adjudication is null || string.IsNullOrWhiteSpace(comparison.Adjudication.AdjudicatorId) ||
                comparison.Votes.Any(vote => string.Equals(vote.ReviewerId.Trim(), adjudicatorId, StringComparison.Ordinal)) ||
                comparison.Adjudication.Choice is not ("left" or "right" or "tie"))
            {
                issues.Add(new("unresolved-pairwise", BlindEvaluationAuditor.ToSafeDiagnosticRecordId(comparison.Id), "成对偏好意见不一致，必须由第三人裁定。"));
                return null;
            }
            return comparison.Adjudication.Choice;
        }
        return first;
    }

    private static BlindEvaluationPairwiseReport BuildPairwise(
        string left,
        string right,
        IReadOnlyList<(string SemanticFamilyId, string Choice)> choices)
    {
        var leftWins = choices.Count(item => item.Choice == "left");
        var rightWins = choices.Count(item => item.Choice == "right");
        var ties = choices.Count - leftWins - rightWins;
        var decisive = leftWins + rightWins;
        var rate = decisive == 0 ? 0d : (double)leftWins / decisive;
        var familyStatistics = FamilyPreferenceStatisticsCalculator.Calculate(choices.Select(item =>
            (item.SemanticFamilyId, item.Choice switch { "left" => 1, "right" => -1, _ => 0 })));
        return new(left, right, choices.Count, leftWins, rightWins, ties, rate,
            familyStatistics.DecisiveFamilyCount,
            familyStatistics.LeftWins,
            familyStatistics.RightWins,
            familyStatistics.Ties,
            familyStatistics.LeftWinRate,
            familyStatistics.LeftWinLower95,
            familyStatistics.LeftWinUpper95,
            familyStatistics.Significant);
    }

    private static double? Percentile(IEnumerable<double> source, double percentile)
    {
        var values = source.Where(double.IsFinite).OrderBy(value => value).ToArray();
        if (values.Length == 0) return null;
        var position = (values.Length - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper) return values[lower];
        return values[lower] + ((values[upper] - values[lower]) * (position - lower));
    }

    private static long? SumOrNull(IEnumerable<long?> values)
    {
        var present = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return present.Length == 0 ? null : present.Sum();
    }

    private static long? SumCompleteOrNull(IEnumerable<long?> values)
    {
        var materialized = values.ToArray();
        return materialized.Length == 0 || materialized.Any(value => !value.HasValue)
            ? null
            : materialized.Sum(value => value!.Value);
    }

    private static long? MaxOrNull(IEnumerable<long?> values)
    {
        var present = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return present.Length == 0 ? null : present.Max();
    }

}
