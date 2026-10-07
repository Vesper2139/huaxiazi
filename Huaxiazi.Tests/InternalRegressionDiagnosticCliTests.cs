using System.IO;
using System.Linq;
using System.Reflection;
using Huaxiazi.BlindEvaluationRunner;
using Huaxiazi.Models;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class InternalRegressionDiagnosticCliTests
{
    [Fact]
    public void DiagnosticPreferenceProfiles_ResolveOnlyNamedProfileAndExposeStableHash()
    {
        var profile = InternalDiagnosticPreferenceProfiles.Resolve("natural-concise-preserve-voice")
            ?? throw new InvalidOperationException("Expected named preference profile.");

        Assert.Equal("natural-concise-preserve-voice", profile.Id);
        Assert.Contains("自然、简洁", profile.Instructions, StringComparison.Ordinal);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(profile.Instructions))).ToLowerInvariant(), profile.Sha256);
        Assert.Null(InternalDiagnosticPreferenceProfiles.Resolve(null));
        Assert.Throws<ArgumentException>(() => InternalDiagnosticPreferenceProfiles.Resolve("arbitrary-prompt"));
    }

    [Fact]
    public void DiagnosticPredictionContent_PreservesClarificationQuestionsForHumanReview()
    {
        var prediction = new BlindWorkflowPrediction("case-a", "needs_clarification", string.Empty,
            ["需要确认交付日期", "需要确认收件人"], [], false);

        var content = InternalRegressionDiagnosticService.CreatePredictionContent(prediction);

        Assert.Equal(prediction.ClarificationQuestions, content.ClarificationQuestions);
    }

    [Theory]
    [InlineData("completed", "completed")]
    [InlineData("needs_clarification", "clarification")]
    [InlineData("failed", "failed")]
    [InlineData("invalid", "rejected")]
    [InlineData("cancelled", "cancelled")]
    public void DiagnosticOutcomeClassification_KeepsInferenceFailuresOutOfRejectionCounts(string status, string expected)
    {
        Assert.Equal(expected, InternalRegressionDiagnosticService.ClassifyDiagnosticOutcome(status));
    }

    [Theory]
    [InlineData("Low", InferenceLevel.Low)]
    [InlineData("medium", InferenceLevel.Medium)]
    [InlineData("HIGH", InferenceLevel.High)]
    public void DiagnosticInferenceLevel_ParsesSupportedLevels(string value, InferenceLevel expected)
    {
        Assert.Equal(expected, InternalRegressionDiagnosticService.ResolveDiagnosticInferenceLevel(value));
    }

    [Fact]
    public void DiagnosticInferenceLevel_DefaultsToMediumAndRejectsCustomOrUnknown()
    {
        Assert.Equal(InferenceLevel.Medium, InternalRegressionDiagnosticService.ResolveDiagnosticInferenceLevel(null));
        Assert.Throws<ArgumentException>(() => InternalRegressionDiagnosticService.ResolveDiagnosticInferenceLevel("Custom"));
        Assert.Throws<ArgumentException>(() => InternalRegressionDiagnosticService.ResolveDiagnosticInferenceLevel("thinking"));
    }

    [Theory]
    [InlineData(false, "provider_default")]
    [InlineData(true, "no_think_prompt_switch_requested_effect_unverified")]
    public void Qwen3ThinkingModeStatus_DistinguishesRequestFromVerifiedRuntimeState(bool requested, string expected)
    {
        var resolver = typeof(InternalRegressionDiagnosticService).GetMethod("ResolveQwen3ThinkingModeStatus",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(resolver);
        Assert.Equal(expected, resolver!.Invoke(null, [requested]));
    }

    [Fact]
    public async Task InternalDiagnosticRequestTraceClient_StoresStableHashesWithoutPromptContent()
    {
        var traceType = typeof(InternalRegressionDiagnosticService).Assembly.GetType(
            "Huaxiazi.BlindEvaluationRunner.InternalDiagnosticRequestTraceClient");
        Assert.NotNull(traceType);

        const string parameterHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var inner = new CapturingStructuredClient();
        var traced = Activator.CreateInstance(traceType!, inner, parameterHash)!;
        var generate = traceType!.GetMethod("GenerateStructuredAsync", [typeof(string), typeof(string), typeof(string), typeof(CancellationToken)]);
        Assert.NotNull(generate);

        var result = await (Task<string>)generate!.Invoke(traced,
            ["private-system-prompt", "private-user-input", "private-schema", CancellationToken.None])!;

        Assert.Equal("response", result);
        Assert.Equal("private-system-prompt", inner.SystemPrompt);
        Assert.Equal("private-user-input", inner.UserInput);
        Assert.Equal("private-schema", inner.Schema);
        var traces = (System.Collections.IEnumerable)traceType.GetProperty("Traces")!.GetValue(traced)!;
        var trace = traces.Cast<object>().Single();
        var serialized = System.Text.Json.JsonSerializer.Serialize(trace);
        Assert.DoesNotContain("private-", serialized, StringComparison.Ordinal);
        Assert.Contains(parameterHash, serialized, StringComparison.Ordinal);
        foreach (var propertyName in new[] { "SystemPromptSha256", "UserInputSha256", "JsonSchemaSha256", "PromptBundleSha256" })
        {
            var hash = (string)traceType.Assembly.GetType("Huaxiazi.BlindEvaluationRunner.InternalDiagnosticRequestTrace")!
                .GetProperty(propertyName)!.GetValue(trace)!;
            Assert.Matches("^[a-f0-9]{64}$", hash);
        }
    }

    [Fact]
    public void LoadDevelopmentRecords_CanSelectOneSampleOnlyAfterValidatingWholeDataset()
    {
        var loader = typeof(InternalRegressionDiagnosticService).GetMethod("LoadDevelopmentRecords",
            [typeof(string), typeof(string), typeof(string)]);
        Assert.NotNull(loader);

        var datasetDirectory = Path.Combine(FindRepoRoot(), "datasets", "polish-regression-v1");
        var casesPath = Path.Combine(datasetDirectory, "cases.jsonl");
        var manifestPath = Path.Combine(datasetDirectory, "manifest.json");
        var sampleId = File.ReadLines(casesPath)
            .Select(line => System.Text.Json.JsonDocument.Parse(line))
            .Select(document =>
            {
                using (document)
                    return document.RootElement.GetProperty("split").GetString() == "development"
                        ? document.RootElement.GetProperty("id").GetString()
                        : null;
            })
            .First(value => value is not null)!;

        var result = loader!.Invoke(null, [casesPath, manifestPath, sampleId]);
        var tuple = result!.GetType();
        var records = ((System.Collections.IEnumerable)tuple.GetField("Item1")!.GetValue(result)!).Cast<object>().ToArray();

        var selected = Assert.Single(records);
        Assert.Equal(sampleId, selected.GetType().GetProperty("Id")!.GetValue(selected));
        Assert.Equal(13, tuple.GetField("Item4")!.GetValue(result));
    }

    [Fact]
    public void SummarizeProviderLatency_UsesAllRequestObservations()
    {
        var observations = new[]
        {
            new ProviderRequestTelemetry("1", 10, 1, 1, 200, "completed"),
            new ProviderRequestTelemetry("2", 20, 1, 1, 200, "completed"),
            new ProviderRequestTelemetry("3", 30, 1, 1, 200, "completed")
        };

        var summary = InternalRegressionDiagnosticService.SummarizeProviderLatency(observations);

        Assert.Equal(20, summary.P50Milliseconds);
        Assert.Equal(30, summary.P95Milliseconds);
    }

    [Fact]
    public async Task Qwen3NoThinkDiagnosticClient_AppendsSwitchToSystemPromptAndPreservesRequest()
    {
        var inner = new CapturingStructuredClient();
        var client = new Qwen3NoThinkDiagnosticClient(inner);

        var result = await client.GenerateStructuredAsync("system prompt", "user input", "{\"type\":\"object\"}");

        Assert.Equal("response", result);
        Assert.Equal("system prompt\n/no_think", inner.SystemPrompt);
        Assert.Equal("user input", inner.UserInput);
        Assert.Equal("{\"type\":\"object\"}", inner.Schema);
    }

    [Fact]
    public void InternalPolishRun_RejectsBlindEvaluationDataBeforeCreatingArtifacts()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "internal-diagnostic-cli", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var casesPath = Path.Combine(root, "cases.jsonl");
        var manifestPath = Path.Combine(root, "manifest.json");
        var outputPath = Path.Combine(root, "run");
        File.WriteAllText(casesPath, "not-read-before-purpose-check");
        File.WriteAllText(manifestPath, """
            {"purpose":"external_blind_evaluation","phase_0_gate_contribution":500,"not_admissible_as_blind_eval":false}
            """);

        var priorError = Console.Error;
        using var capturedError = new StringWriter();
        try
        {
            Console.SetError(capturedError);
            var exitCode = InvokeProgram("internal-polish-run", "--input", casesPath, "--manifest", manifestPath,
                "--model", "qwen3:4b", "--output", outputPath, "--split", "development");

            Assert.Equal(3, exitCode);
            Assert.Contains("internal_regression_only", capturedError.ToString());
            Assert.False(Directory.Exists(outputPath));
        }
        finally
        {
            Console.SetError(priorError);
            Directory.Delete(root, recursive: true);
        }
    }

    private static int InvokeProgram(params string[] args)
    {
        var main = typeof(BlindWorkflowExecutor).Assembly.GetType("Huaxiazi.BlindEvaluationRunner.Program")!
            .GetMethod("Main", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (int)main.Invoke(null, [args])!;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "datasets", "polish-regression-v1", "manifest.json")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("无法从测试程序集定位项目根目录。");
    }

    private sealed class CapturingStructuredClient : ITextGenerationClient, IStructuredTextGenerationClient
    {
        public string? SystemPrompt { get; private set; }
        public string? UserInput { get; private set; }
        public string? Schema { get; private set; }

        public Task<string> GenerateAsync(string systemPrompt, string userInput, CancellationToken cancellationToken = default)
        {
            SystemPrompt = systemPrompt;
            UserInput = userInput;
            return Task.FromResult("response");
        }

        public Task<string> GenerateStructuredAsync(string systemPrompt, string userInput, string jsonSchema, CancellationToken cancellationToken = default)
        {
            SystemPrompt = systemPrompt;
            UserInput = userInput;
            Schema = jsonSchema;
            return Task.FromResult("response");
        }
    }
}
