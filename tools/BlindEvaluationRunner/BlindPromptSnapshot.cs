using Huaxiazi.DatasetBuilder;
using Huaxiazi.Models;
using Huaxiazi.Services;
using System.Security.Cryptography;
using System.Text;

namespace Huaxiazi.BlindEvaluationRunner;

public sealed record BlindPromptSnapshot(string Id, string Task, string Split, string SystemPrompt, string UserMessage)
{
    public string OutputContractSha256 { get; init; } = string.Empty;
    public IReadOnlyList<BlindPromptSnapshotTurn>? ConversationTurns { get; init; }
}

public sealed record BlindPromptSnapshotTurn(int TurnIndex, string SystemPrompt, string UserMessage)
{
    public string OutputContractSha256 { get; init; } = string.Empty;
}

public static class BlindSnapshotSplitSelector
{
    public static IReadOnlyList<BlindEvaluationRecord> Select(
        IReadOnlyList<BlindEvaluationRecord> records,
        string split,
        bool confirmFrozenTestLocked)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (split is not ("development" or "frozen_test" or "all"))
            throw new ArgumentException("split 必须是 development、frozen_test 或 all。", nameof(split));
        if (split == "frozen_test" && !confirmFrozenTestLocked)
            throw new InvalidOperationException("解封 frozen_test 快照前，必须确认候选模型、提示和采样参数均已锁定。");
        var selected = split == "all"
            ? records.ToArray()
            : records.Where(record => string.Equals(record.Split, split, StringComparison.Ordinal)).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException($"盲评集没有 {split} 样本。");
        if (selected.Any(record => record.Split == "frozen_test") && !confirmFrozenTestLocked)
            throw new InvalidOperationException("运行包含 frozen_test 样本前，必须确认候选模型、提示和采样参数均已锁定。");
        return selected;
    }
}

public static class BlindPromptSnapshotCompiler
{
    public static string BuildBundleHashLine(BlindPromptSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var line = snapshot.Id + "\t" + Sha256(snapshot.SystemPrompt) + "\t" + Sha256(snapshot.UserMessage) + "\t" + snapshot.OutputContractSha256;
        if (snapshot.ConversationTurns is not { Count: > 0 }) return line;
        return line + "\tconversation_turns\t" + string.Join("\t", snapshot.ConversationTurns.Select(turn =>
            turn.TurnIndex + ":" + Sha256(turn.SystemPrompt) + ":" + Sha256(turn.UserMessage) + ":" + turn.OutputContractSha256));
    }

    public static string BuildOutputContractHashLine(BlindPromptSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var line = snapshot.Id + "\t" + snapshot.OutputContractSha256;
        if (snapshot.ConversationTurns is not { Count: > 0 }) return line;
        return line + "\tconversation_turns\t" + string.Join("\t", snapshot.ConversationTurns.Select(turn =>
            turn.TurnIndex + ":" + turn.OutputContractSha256));
    }

    public static string BuildSystemPromptHashLine(BlindPromptSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var line = snapshot.Id + "\t" + Sha256(snapshot.SystemPrompt);
        if (snapshot.ConversationTurns is not { Count: > 0 }) return line;
        return line + "\tconversation_turns\t" + string.Join("\t", snapshot.ConversationTurns.Select(turn =>
            turn.TurnIndex + ":" + Sha256(turn.SystemPrompt)));
    }

    public static string BuildUserMessageHashLine(BlindPromptSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var line = snapshot.Id + "\t" + Sha256(snapshot.UserMessage);
        if (snapshot.ConversationTurns is not { Count: > 0 }) return line;
        return line + "\tconversation_turns\t" + string.Join("\t", snapshot.ConversationTurns.Select(turn =>
            turn.TurnIndex + ":" + Sha256(turn.UserMessage)));
    }

    public static BlindPromptSnapshot Compile(BlindEvaluationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Turns is { Count: > 0 })
        {
            var history = new List<PolishConversationMessage>();
            var turns = new List<BlindPromptSnapshotTurn>(record.Turns.Count);
            foreach (var turn in record.Turns)
            {
                var turnRecord = record with { Input = turn.UserInput, ConversationId = null, Turns = null };
                var workflow = BlindWorkflowRequestFactory.Create(turnRecord, history);
                var compiled = CompileSingleTurn(workflow);
                turns.Add(new(turn.TurnIndex, compiled.SystemPrompt, compiled.UserMessage)
                {
                    OutputContractSha256 = compiled.OutputContractSha256
                });
                history.Add(new("user", turn.UserInput));
                if (turn.TurnIndex < record.Turns.Count)
                    history.Add(new("assistant", $"【第 {turn.TurnIndex} 轮上一轮助手回复：运行时由候选模型实际输出替换】"));
            }

            var final = turns[^1];
            return new(record.Id, record.Task, record.Split, final.SystemPrompt, final.UserMessage)
            {
                OutputContractSha256 = final.OutputContractSha256,
                ConversationTurns = turns
            };
        }

        var single = CompileSingleTurn(BlindWorkflowRequestFactory.Create(record));
        return new(record.Id, record.Task, record.Split, single.SystemPrompt, single.UserMessage)
        {
            OutputContractSha256 = single.OutputContractSha256
        };
    }

    private static (string SystemPrompt, string UserMessage, string OutputContractSha256) CompileSingleTurn(BlindWorkflowRequest workflow)
    {
        string systemPrompt;
        string userMessage;
        StructuredOutputContract outputContract;
        if (workflow.PolishRequest is { } polish)
        {
            var builder = new PolishPromptBuilderService();
            systemPrompt = builder.BuildSystemPrompt(polish, workflow.ClarificationEnabled);
            userMessage = builder.BuildUserMessage(polish);
            outputContract = PolishWorkflowService.GetOutputContract(workflow.CompanionMode);
        }
        else if (workflow.PromptRequest is { } prompt)
        {
            var builder = new PromptBuilderService();
            systemPrompt = builder.BuildAgentContext(prompt, workflow.Plan).Text;
            userMessage = builder.BuildUserMessage(prompt);
            outputContract = PromptOptimizationWorkflowService.GetOutputContract(workflow.CompanionMode);
        }
        else
        {
            throw new InvalidOperationException("盲评任务没有可执行的产品请求。");
        }

        return (
            AssistantEmotionProtocol.DecorateSystemPrompt(systemPrompt, workflow.CompanionMode),
            userMessage,
            StructuredOutputContractFingerprint.Compute(outputContract));
    }

    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
