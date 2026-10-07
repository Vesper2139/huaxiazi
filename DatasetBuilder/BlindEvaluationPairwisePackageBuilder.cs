using System.Security.Cryptography;
using System.Text.Json;

namespace Huaxiazi.DatasetBuilder;

public sealed record BlindPairwiseReviewItem(
    string Id,
    string Task,
    string Input,
    IReadOnlyDictionary<string, JsonElement> Context,
    IReadOnlyList<string> Constraints,
    string RiskLevel,
    IReadOnlyList<string> FactsAndConstraints,
    string TargetTone,
    IReadOnlyList<string> FormatRequirements,
    bool ClarificationRequired,
    IReadOnlyList<string> HighRiskKeyFacts,
    string LeftOutput,
    string RightOutput);

public sealed record BlindPairwiseAnswerTemplate(
    string Id,
    IReadOnlyList<BlindEvaluationPairwiseVote> Votes,
    BlindEvaluationPairwiseAdjudication? Adjudication);

public sealed record BlindPairwiseSealedMapItem(
    string ReviewId,
    string GoldId,
    string LeftCandidateId,
    string RightCandidateId,
    string LeftOutputSha256,
    string RightOutputSha256);

public sealed record BlindPairwiseSealedMap(string Split, string ReviewPackageSha256, IReadOnlyList<BlindPairwiseSealedMapItem> Items);

public sealed record BlindPairwiseSkippedCase(string GoldId, string Reason);

public sealed record BlindEvaluationPairwisePackage(
    string Split,
    IReadOnlyList<BlindPairwiseReviewItem> ReviewItems,
    IReadOnlyList<BlindPairwiseAnswerTemplate> AnswerTemplates,
    IReadOnlyList<BlindPairwiseSealedMapItem> SealedMap,
    IReadOnlyList<BlindPairwiseSkippedCase> SkippedCases);

/// <summary>Creates anonymized, side-randomized pairwise review materials from candidate predictions.</summary>
public static class BlindEvaluationPairwisePackageBuilder
{
    public static BlindEvaluationPairwisePackage Build(
        IReadOnlyList<BlindEvaluationRecord> gold,
        IReadOnlyList<BlindEvaluationPrediction> predictions,
        string split)
    {
        ArgumentNullException.ThrowIfNull(gold);
        ArgumentNullException.ThrowIfNull(predictions);
        if (split is not ("development" or "frozen_test"))
            throw new ArgumentException("成对评审必须显式选择 development 或 frozen_test。", nameof(split));
        if (gold.Count == 0) throw new ArgumentException("成对评审至少需要一条 gold 记录。", nameof(gold));
        var recordIssues = BlindEvaluationAuditor.ValidateRecordIntegrity(gold);
        if (recordIssues.Count > 0)
            throw new InvalidOperationException("成对评审 gold human_review/record integrity 未通过当前准入：" + string.Join("；", recordIssues.Select(issue => $"{issue.RecordId}/{issue.Code}")));
        var goldById = new Dictionary<string, BlindEvaluationRecord>(StringComparer.Ordinal);
        foreach (var record in gold)
        {
            if (string.IsNullOrWhiteSpace(record.Id) || !goldById.TryAdd(record.Id, record))
                throw new InvalidOperationException("gold 存在空或重复 id，不能制作匿名比较包。");
        }
        var selectedGold = gold.Where(record => record.Split == split).ToArray();
        if (selectedGold.Length == 0)
            throw new InvalidOperationException($"gold 中没有 split={split} 的样本，不能制作评审包。");

        var seenPredictions = new HashSet<(string Id, string CandidateId)>();
        foreach (var prediction in predictions)
        {
            if (!goldById.ContainsKey(prediction.Id))
                throw new InvalidOperationException("候选预测包含 gold 中不存在的样本 id。");
            if (string.IsNullOrWhiteSpace(prediction.CandidateId) || !seenPredictions.Add((prediction.Id, prediction.CandidateId)))
                throw new InvalidOperationException("候选预测含空 candidate_id 或重复的样本/候选组合。");
            if (prediction.Status is not ("success" or "error" or "cancelled"))
                throw new InvalidOperationException("候选预测包含未知运行状态。");
            if (prediction.Status == "success" && BlindEvaluationAuditor.ContainsPotentialSensitiveData(prediction.Output))
                throw new InvalidOperationException("候选输出触发敏感信息启发式检查，已拒绝生成评审包；请隔离并复核来源和脱敏边界。");
        }

        var reviewItems = new List<BlindPairwiseReviewItem>();
        var answerTemplates = new List<BlindPairwiseAnswerTemplate>();
        var sealedMap = new List<BlindPairwiseSealedMapItem>();
        var skipped = new List<BlindPairwiseSkippedCase>();
        foreach (var record in selectedGold)
        {
            var successful = predictions.Where(item => item.Id == record.Id && item.Status == "success")
                .OrderBy(item => item.CandidateId, StringComparer.Ordinal).ToArray();
            if (successful.Length < 2)
            {
                skipped.Add(new(record.Id, "fewer_than_two_successful_candidates"));
                continue;
            }

            for (var leftIndex = 0; leftIndex < successful.Length; leftIndex++)
            for (var rightIndex = leftIndex + 1; rightIndex < successful.Length; rightIndex++)
            {
                var first = successful[leftIndex];
                var second = successful[rightIndex];
                var firstOnLeft = RandomNumberGenerator.GetInt32(2) == 0;
                var left = firstOnLeft ? first : second;
                var right = firstOnLeft ? second : first;
                var reviewId = NewReviewId();
                var reviewInput = record.Turns is { Count: > 0 }
                    ? string.Join(Environment.NewLine, record.Turns.Select(turn => $"第 {turn.TurnIndex} 轮用户：{turn.UserInput}"))
                    : record.Input;
                reviewItems.Add(new(reviewId, record.Task, reviewInput, record.Context,
                    record.Constraints, record.RiskLevel,
                    record.Annotations.FactsAndConstraints, record.Annotations.TargetTone,
                    record.Annotations.FormatRequirements, record.Annotations.ClarificationRequired,
                    record.Annotations.HighRiskKeyFacts, left.Output, right.Output));
                answerTemplates.Add(new(reviewId, [], null));
                sealedMap.Add(new(reviewId, record.Id, left.CandidateId, right.CandidateId,
                    Sha256(left.Output), Sha256(right.Output)));
            }
        }

        Shuffle(reviewItems);
        Shuffle(answerTemplates);
        return new(split, reviewItems, answerTemplates, sealedMap, skipped);
    }

    public static IReadOnlyList<BlindEvaluationPairwiseComparison> MergeAnswers(
        IReadOnlyList<BlindPairwiseAnswerTemplate> answers,
        IReadOnlyList<BlindPairwiseSealedMapItem> sealedMap)
    {
        ArgumentNullException.ThrowIfNull(answers);
        ArgumentNullException.ThrowIfNull(sealedMap);
        var mapById = sealedMap.ToDictionary(item => item.ReviewId, StringComparer.Ordinal);
        var answersById = answers.ToDictionary(item => item.Id, StringComparer.Ordinal);
        if (mapById.Count == 0 || mapById.Count != sealedMap.Count || answersById.Count != answers.Count ||
            answersById.Count != mapById.Count || mapById.Keys.Any(id => !answersById.ContainsKey(id)))
            throw new InvalidOperationException("评审答案 ID 必须与封存映射中的所有比较项一一对应。");

        var result = new List<BlindEvaluationPairwiseComparison>(sealedMap.Count);
        foreach (var mapping in sealedMap)
        {
            var answer = answersById[mapping.ReviewId];
            if (answer.Votes is null || answer.Votes.Count != 2 ||
                answer.Votes.Any(vote => string.IsNullOrWhiteSpace(vote.ReviewerId) || vote.Choice is not ("left" or "right" or "tie")) ||
                answer.Votes.Select(vote => vote.ReviewerId.Trim()).Distinct(StringComparer.Ordinal).Count() != 2)
                throw new InvalidOperationException($"评审项 {mapping.ReviewId} 必须有两名不同评审者的有效投票。");
            if (answer.Votes[0].Choice != answer.Votes[1].Choice &&
                (answer.Adjudication is null || string.IsNullOrWhiteSpace(answer.Adjudication.AdjudicatorId) ||
                 answer.Votes.Any(vote => string.Equals(vote.ReviewerId.Trim(), answer.Adjudication.AdjudicatorId.Trim(), StringComparison.Ordinal)) ||
                 answer.Adjudication.Choice is not ("left" or "right" or "tie")))
                throw new InvalidOperationException($"评审项 {mapping.ReviewId} 的投票有分歧，必须由第三人裁定。");
            result.Add(new(mapping.GoldId, mapping.LeftCandidateId, mapping.RightCandidateId, answer.Votes, answer.Adjudication));
        }
        return result;
    }

    private static string NewReviewId() => "pair-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static void Shuffle<T>(IList<T> items)
    {
        for (var index = items.Count - 1; index > 0; index--)
        {
            var swapIndex = RandomNumberGenerator.GetInt32(index + 1);
            (items[index], items[swapIndex]) = (items[swapIndex], items[index]);
        }
    }

    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
