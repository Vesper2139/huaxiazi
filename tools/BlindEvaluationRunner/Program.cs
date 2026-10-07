using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Huaxiazi.DatasetBuilder;
using Huaxiazi.Models;
using Huaxiazi.Services;

namespace Huaxiazi.BlindEvaluationRunner;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            PrintHelp();
            return 0;
        }
        try
        {
            return args[0] switch
            {
                "snapshot" => CreateSnapshot(args[1..]),
                "run" => RunCandidate(args[1..]),
                "internal-polish-run" => RunInternalPolishDiagnostic(args[1..]),
                "finalize" => FinalizeRun(args[1..]),
                "finalize-cohort" => FinalizeCohort(args[1..]),
                _ => Fail("未知命令。")
            };
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine("运行门禁未通过：" + exception.Message);
            return 3;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or JsonException)
        {
            Console.Error.WriteLine("错误：" + exception.Message);
            return 2;
        }
    }

    private static int FinalizeRun(string[] args)
    {
        var goldPath = GetOption(args, "gold") ?? throw new ArgumentException("finalize 需要 --gold <blind-eval.jsonl>。");
        var rawPredictionsPath = GetOption(args, "raw-predictions") ?? throw new ArgumentException("finalize 需要 --raw-predictions <immutable-candidate-predictions.jsonl>。");
        var reviewedPredictionsPath = GetOption(args, "predictions") ?? throw new ArgumentException("finalize 需要 --predictions <reviewed-predictions.jsonl>。");
        var reportPath = GetOption(args, "report") ?? throw new ArgumentException("finalize 需要 --report <evaluation-report.json>。");
        var runInfoPath = GetOption(args, "run-info") ?? throw new ArgumentException("finalize 需要 --run-info <run-info.json>。");
        var sealedMapPath = GetOption(args, "sealed-manifest") ?? throw new ArgumentException("finalize 需要 --sealed-manifest <outside-reviewer-dir.json>。");
        var outputPath = GetOption(args, "output") ?? throw new ArgumentException("finalize 需要 --output <new-run-manifest.json>。");
        var metadata = new BlindEvaluationManifestFinalizationMetadata(
            GetOption(args, "dataset-id") ?? string.Empty,
            GetOption(args, "authorization-review") ?? string.Empty,
            GetOption(args, "hardware-profile") ?? string.Empty,
            GetOption(args, "runtime-version") ?? string.Empty,
            GetOption(args, "code-revision") ?? string.Empty,
            GetOption(args, "run-id") ?? string.Empty);
        var result = BlindEvaluationManifestFinalizer.Finalize(
            goldPath, rawPredictionsPath, reviewedPredictionsPath, reportPath, GetOption(args, "comparisons"), runInfoPath, sealedMapPath, outputPath, metadata);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            created = true,
            valid = result.Valid,
            sample_count = result.SampleCount,
            dataset_sha256 = result.DatasetSha256,
            semantic_family_split_sha256 = result.SemanticFamilySplitSha256,
            issue_count = result.Issues.Count
        }, JsonOptions));
        return 0;
    }

    private static int FinalizeCohort(string[] args)
    {
        var goldPath = GetOption(args, "gold") ?? throw new ArgumentException("finalize-cohort 需要 --gold <blind-eval.jsonl>。");
        var rawPaths = GetOptions(args, "raw-predictions").ToArray();
        var reviewedPaths = GetOptions(args, "predictions").ToArray();
        var runInfoPaths = GetOptions(args, "run-info").ToArray();
        var sealedPaths = GetOptions(args, "sealed-manifest").ToArray();
        var outputPath = GetOption(args, "output") ?? throw new ArgumentException("finalize-cohort 需要 --output <new-cohort-manifest.json>。");
        var reportPath = GetOption(args, "report") ?? throw new ArgumentException("finalize-cohort 需要 --report <cohort-report.json>。");
        var comparisonsPath = GetOption(args, "comparisons") ?? throw new ArgumentException("finalize-cohort 需要 --comparisons <comparisons.jsonl>。");
        var pairwiseAnswersPath = GetOption(args, "pairwise-answers") ?? throw new ArgumentException("finalize-cohort 需要 --pairwise-answers <completed-answers.jsonl>。");
        var pairwiseReviewPackagePath = GetOption(args, "pairwise-review-package") ?? throw new ArgumentException("finalize-cohort 需要 --pairwise-review-package <pairwise-review.jsonl>。");
        var pairwiseSealedMapPath = GetOption(args, "pairwise-sealed-manifest") ?? throw new ArgumentException("finalize-cohort 需要 --pairwise-sealed-manifest <outside-reviewer-dir.json>。");
        var count = rawPaths.Length;
        if (count < 2 || reviewedPaths.Length != count || runInfoPaths.Length != count || sealedPaths.Length != count)
            throw new ArgumentException("finalize-cohort 需要至少两个候选，并按相同顺序重复提供 --raw-predictions、--predictions、--run-info、--sealed-manifest。");
        var candidateIds = GetOptions(args, "candidate-id").ToArray();
        if (candidateIds.Length != count)
            throw new ArgumentException("finalize-cohort 必须按顺序为每个候选提供一个 --candidate-id alias。");
        var candidates = Enumerable.Range(0, count).Select(index => new BlindEvaluationCohortCandidateInput(
            candidateIds[index], rawPaths[index], reviewedPaths[index], runInfoPaths[index], sealedPaths[index])).ToArray();
        var metadata = new BlindEvaluationManifestFinalizationMetadata(
            GetOption(args, "dataset-id") ?? string.Empty,
            GetOption(args, "authorization-review") ?? string.Empty,
            GetOption(args, "hardware-profile") ?? string.Empty,
            GetOption(args, "runtime-version") ?? string.Empty,
            GetOption(args, "code-revision") ?? string.Empty,
            GetOption(args, "run-id") ?? string.Empty);
        var result = BlindEvaluationCohortFinalizer.Finalize(goldPath, candidates, reportPath, comparisonsPath,
            pairwiseAnswersPath, pairwiseReviewPackagePath, pairwiseSealedMapPath, outputPath, metadata);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            created = true,
            valid = result.Valid,
            sample_count = result.SampleCount,
            dataset_sha256 = result.DatasetSha256,
            semantic_family_split_sha256 = result.SemanticFamilySplitSha256,
            candidate_count = candidates.Length
        }, JsonOptions));
        return 0;
    }

    private static int RunCandidate(string[] args)
    {
        var inputPath = GetOption(args, "input") ?? throw new ArgumentException("run 需要 --input <blind-eval.jsonl>。");
        var candidateConfigPath = GetOption(args, "candidate-config") ?? throw new ArgumentException("run 需要 --candidate-config <candidate.json>。");
        var outputDirectory = GetOption(args, "output") ?? throw new ArgumentException("run 需要 --output <new-reviewer-run-directory>。");
        var sealedMapPath = GetOption(args, "sealed-manifest") ?? throw new ArgumentException("run 需要 --sealed-manifest <outside-reviewer-dir.json>。");
        var split = GetOption(args, "split") ?? throw new ArgumentException("run 需要显式指定 --split development|frozen_test。");
        var hardwareProfile = GetOption(args, "hardware-profile") ?? throw new ArgumentException("run 需要显式指定 --hardware-profile <non-identifying-tier-label>。");
        var authorizationReference = GetOption(args, "authorization-ref") ?? string.Empty;
        var allowCloud = args.Contains("--allow-cloud", StringComparer.Ordinal);
        var frozenTestConfirmed = args.Contains("--confirm-frozen-test-locked", StringComparer.Ordinal);
        var trainingPaths = GetOptions(args, "training").ToArray();
        if (trainingPaths.Length == 0) throw new ArgumentException("run 需要至少一个 --training <canonical.jsonl>。");

        var datasetBytes = File.ReadAllBytes(inputPath);
        var gold = File.ReadLines(inputPath).Select((line, index) =>
            JsonSerializer.Deserialize<BlindEvaluationRecord>(line, JsonOptions)
                ?? throw new JsonException($"盲评样本第 {index + 1} 行为空。")).ToArray();
        var knownRecords = trainingPaths.SelectMany(ReadKnownRecords).ToArray();
        var admissionIssues = BlindEvaluationAuditor.Validate(gold, knownTrainingRecords: knownRecords);
        if (admissionIssues.Count > 0)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { valid = false, issue_count = admissionIssues.Count, issues = admissionIssues }, JsonOptions));
            return 3;
        }

        var selectedGold = BlindSnapshotSplitSelector.Select(gold, split, frozenTestConfirmed);
        var candidateJson = File.ReadAllText(candidateConfigPath);
        var candidate = BlindCandidateConfigurationLoader.Load(
            candidateJson, allowCloud, authorizationReference, Environment.GetEnvironmentVariable);

        var requestObservations = new List<ProviderRequestTelemetry>();
        var runtimeRoot = GetOption(args, "runtime-root");
        var modelRoot = GetOption(args, "model-root");
        if (candidate.Profile.Platform == ProviderPlatform.ManagedLocal)
            Console.Error.WriteLine("正在核验本地模型与推理运行时的 SHA-256；大型模型文件校验可能需要一段时间……");
        using var candidateClient = BlindCandidateClientFactory.Create(
            candidate, requestObservations, runtimeRoot, modelRoot);
        candidate = candidate with { LocalArtifacts = candidateClient.LocalArtifacts };
        using var cancellationSource = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationSource.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        BlindCandidateRunResult run;
        try
        {
            run = BlindCandidateRunService.RunAsync(selectedGold, candidate, outputDirectory, sealedMapPath,
                Convert.ToHexString(SHA256.HashData(datasetBytes)).ToLowerInvariant(), split,
                requestObservations, candidateClient.Client, cancellationSource.Token, frozenTestConfirmed, hardwareProfile).GetAwaiter().GetResult();
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            created = true,
            run_kind = "candidate",
            candidate_id = run.CandidateId,
            sample_count = run.SampleCount,
            cancelled_count = run.CancelledCount,
            provider_request_count = run.ProviderRequestCount,
            predictions_sha256 = run.PredictionsSha256,
            provider_calls_performed = run.ProviderRequestCount > 0,
            content_telemetry_enabled = false
        }, JsonOptions));
        return run.CancelledCount > 0 ? 4 : 0;
    }

    private static int RunInternalPolishDiagnostic(string[] args)
    {
        var inputPath = GetOption(args, "input") ?? throw new ArgumentException("internal-polish-run 需要 --input <internal-cases.jsonl>。");
        var manifestPath = GetOption(args, "manifest") ?? throw new ArgumentException("internal-polish-run 需要 --manifest <internal-regression-manifest.json>。");
        var modelId = GetOption(args, "model") ?? throw new ArgumentException("internal-polish-run 需要 --model <installed-local-ollama-model>。");
        var outputPath = GetOption(args, "output") ?? throw new ArgumentException("internal-polish-run 需要 --output <new-run-directory>。");
        var split = GetOption(args, "split") ?? throw new ArgumentException("internal-polish-run 需要显式指定 --split development。");
        if (!string.Equals(split, "development", StringComparison.Ordinal))
            throw new InvalidOperationException("internal-polish-run 只执行 development；regression 与任何 frozen split 均不得作为开发调参与当前基线输入。");
        var temperature = ReadDoubleOption(args, "temperature", 0.4);
        var topP = ReadDoubleOption(args, "top-p", 1.0);
        var maxTokens = ReadIntOption(args, "max-tokens", 2048);
        var sampleId = GetOption(args, "sample-id");
        var preferenceProfileId = GetOption(args, "preference-profile");
        _ = InternalDiagnosticPreferenceProfiles.Resolve(preferenceProfileId);
        var inferenceLevel = InternalRegressionDiagnosticService.ResolveDiagnosticInferenceLevel(GetOption(args, "inference-level"));
        int? diagnosticSeed = null;
        if (GetOption(args, "seed") is { } seedText)
        {
            if (!int.TryParse(seedText, out var parsedSeed))
                throw new ArgumentException("--seed 必须是 32 位整数。");
            diagnosticSeed = parsedSeed;
        }
        var qwen3NoThink = args.Contains("--qwen3-no-think", StringComparer.Ordinal);
        using var cancellationSource = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationSource.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            var run = InternalRegressionDiagnosticService.RunAsync(
                inputPath,
                manifestPath,
                modelId,
                outputPath,
                temperature,
                topP,
                maxTokens,
                cancellationSource.Token,
                (completed, total, status) => Console.WriteLine($"[{completed}/{total}] {status}"),
                qwen3NoThink,
                diagnosticSeed,
                sampleId,
                preferenceProfileId,
                inferenceLevel)
                .GetAwaiter().GetResult();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                created = true,
                run_kind = "internal_synthetic_diagnostic",
                phase_0_gate_contribution = 0,
                run_info_path = run.RunInfoPath,
                sample_count = run.SampleCount,
                completed_count = run.CompletedCount,
                clarification_count = run.ClarificationCount,
                rejected_count = run.RejectedCount,
                failed_count = run.FailedCount,
                cancelled_count = run.CancelledCount,
                provider_request_count = run.ProviderRequestCount,
                diagnostic_seed = diagnosticSeed,
                diagnostic_preference_profile = preferenceProfileId,
                diagnostic_inference_level = inferenceLevel.ToString(),
                sample_selection_mode = sampleId is null ? "full_development" : "single_development_sample",
                selected_sample_id = sampleId,
                dataset_sha256 = run.DatasetSha256
            }, JsonOptions));
            return run.CancelledCount > 0 ? 4 : 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static double ReadDoubleOption(string[] args, string name, double defaultValue)
    {
        var value = GetOption(args, name);
        if (value is null) return defaultValue;
        if (!double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            throw new ArgumentException($"--{name} 必须是有效数字。");
        return parsed;
    }

    private static int ReadIntOption(string[] args, string name, int defaultValue)
    {
        var value = GetOption(args, name);
        if (value is null) return defaultValue;
        if (!int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            throw new ArgumentException($"--{name} 必须是整数。");
        return parsed;
    }

    private static int CreateSnapshot(string[] args)
    {
        var inputPath = GetOption(args, "input") ?? throw new ArgumentException("snapshot 需要 --input <blind-eval.jsonl>。");
        var outputDirectory = GetOption(args, "output") ?? throw new ArgumentException("snapshot 需要 --output <new-run-directory>。");
        var split = GetOption(args, "split") ?? throw new ArgumentException("snapshot 需要显式指定 --split development|frozen_test。");
        var frozenTestConfirmed = args.Contains("--confirm-frozen-test-locked", StringComparer.Ordinal);
        var trainingPaths = GetOptions(args, "training").ToArray();
        if (trainingPaths.Length == 0) throw new ArgumentException("snapshot 需要至少一个 --training <canonical.jsonl>。");

        var gold = File.ReadLines(inputPath).Select((line, index) =>
            JsonSerializer.Deserialize<BlindEvaluationRecord>(line, JsonOptions)
                ?? throw new JsonException($"盲评样本第 {index + 1} 行为空。")).ToArray();
        var knownRecords = trainingPaths.SelectMany(ReadKnownRecords).ToArray();
        var admissionIssues = BlindEvaluationAuditor.Validate(gold, knownTrainingRecords: knownRecords);
        if (admissionIssues.Count > 0)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { valid = false, issue_count = admissionIssues.Count, issues = admissionIssues }, JsonOptions));
            return 3;
        }

        var selectedGold = BlindSnapshotSplitSelector.Select(gold, split, frozenTestConfirmed);
        var snapshots = selectedGold.Select(BlindPromptSnapshotCompiler.Compile).ToArray();
        var promptLines = snapshots.Select(snapshot => JsonSerializer.Serialize(snapshot, JsonOptions)).ToArray();
        var promptJsonl = string.Join(Environment.NewLine, promptLines) + Environment.NewLine;
        var resolvedDirectory = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(resolvedDirectory) || File.Exists(resolvedDirectory))
            throw new IOException("输出目录已存在；快照运行不可覆盖，请指定一个全新的目录。");
        Directory.CreateDirectory(Path.GetDirectoryName(resolvedDirectory)!);
        Directory.CreateDirectory(resolvedDirectory);

        var promptPath = Path.Combine(resolvedDirectory, "prompts.jsonl");
        var manifestPath = Path.Combine(resolvedDirectory, "snapshot-manifest.json");
        var bundleHashInput = string.Join("\n", snapshots.Select(BlindPromptSnapshotCompiler.BuildBundleHashLine));
        var outputContractBundleHashInput = string.Join("\n", snapshots.Select(BlindPromptSnapshotCompiler.BuildOutputContractHashLine));
        var manifest = new
        {
            manifest_version = 2,
            snapshot_kind = "initial_product_workflow_prompts",
            admission_protocol_id = BlindEvaluationAdmissionProtocol.ProtocolId,
            admission_protocol_version = BlindEvaluationAdmissionProtocol.Version,
            admission_policy_sha256 = BlindEvaluationAdmissionProtocol.PolicySha256,
            created_at_utc = DateTimeOffset.UtcNow.ToString("O"),
            dataset_sha256 = Sha256(File.ReadAllBytes(inputPath)),
            sample_count = snapshots.Length,
            split,
            frozen_test_unlock_confirmed = frozenTestConfirmed,
            semantic_family_split_sha256 = Sha256(string.Join("", selectedGold
                .Select(record => record.SemanticFamilyId + "\t" + record.Split)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .Select(value => value + "\n"))),
            prompts_file = "prompts.jsonl",
            prompts_sha256 = Sha256(Encoding.UTF8.GetBytes(promptJsonl)),
            prompt_bundle_sha256 = Sha256(bundleHashInput),
            output_contract_bundle_sha256 = Sha256(Encoding.UTF8.GetBytes(outputContractBundleHashInput)),
            product_assembly_version = typeof(PolishPromptBuilderService).Assembly.GetName().Version?.ToString() ?? "unknown",
            provider_calls_performed = false,
            content_telemetry_enabled = false,
            contains_prompt_content = true
        };

        ImmutableArtifactWriter.WriteNew(promptPath, promptJsonl);
        ImmutableArtifactWriter.WriteNew(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            created = true,
            sample_count = snapshots.Length,
            prompts_sha256 = manifest.prompts_sha256,
            prompt_bundle_sha256 = manifest.prompt_bundle_sha256,
            output_contract_bundle_sha256 = manifest.output_contract_bundle_sha256,
            split = manifest.split,
            provider_calls_performed = false
        }, JsonOptions));
        return 0;
    }

    private static IEnumerable<BlindEvaluationKnownRecord> ReadKnownRecords(string path)
    {
        foreach (var (line, index) in File.ReadLines(path).Select((line, index) => (line, index + 1)))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new JsonException($"{path} 第 {index} 行必须是 JSON 对象。");
            var split = ReadString(root, "split");
            var input = ReadString(root, "input");
            var family = ReadString(root, "semantic_family_id");
            if (string.IsNullOrWhiteSpace(family)) family = ReadString(root, "generalization_family");
            yield return new BlindEvaluationKnownRecord(split, input, family);
        }
    }

    private static string ReadString(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            return value.GetString() ?? string.Empty;
        return string.Empty;
    }

    private static string? GetOption(string[] args, string name)
    {
        var prefix = "--" + name;
        for (var index = 0; index < args.Length - 1; index++)
            if (args[index] == prefix) return args[index + 1];
        return null;
    }

    private static IEnumerable<string> GetOptions(string[] args, string name)
    {
        var prefix = "--" + name;
        for (var index = 0; index < args.Length - 1; index++)
            if (args[index] == prefix) yield return args[index + 1];
    }

    private static string Sha256(string text) => Sha256(Encoding.UTF8.GetBytes(text));

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    private static void PrintHelp() => Console.WriteLine(
        "用法：\n" +
        "  dotnet run --project .\\tools\\BlindEvaluationRunner -- snapshot --input blind-eval.jsonl --training canonical.jsonl --output <new-dir> --split development|frozen_test [--confirm-frozen-test-locked] [--training canonical-v2.jsonl]\n" +
            "  dotnet run --project .\\tools\\BlindEvaluationRunner -- run --input blind-eval.jsonl --training canonical.jsonl --candidate-config candidate.json --output <new-reviewer-dir> --sealed-manifest <outside-reviewer-dir.json> --split development|frozen_test|all --hardware-profile <non-identifying-tier-label> [--allow-cloud --authorization-ref <approved-ref>] [--confirm-frozen-test-locked] [--runtime-root <runtimes/local> --model-root <installed-models-root>]\n" +
        "  dotnet run --project .\\tools\\BlindEvaluationRunner -- internal-polish-run --input datasets/polish-regression-v1/cases.jsonl --manifest datasets/polish-regression-v1/manifest.json --model qwen3:4b --output <new-local-diagnostic-dir> --split development [--sample-id <development-case-id> --temperature 0.4 --top-p 1.0 --max-tokens 2048 --seed N --inference-level Low|Medium|High --qwen3-no-think --preference-profile natural-concise-preserve-voice]\n" +
        "  dotnet run --project .\\tools\\BlindEvaluationRunner -- finalize --gold blind-eval.jsonl --raw-predictions candidate-predictions.jsonl --predictions reviewed-predictions.jsonl --report evaluation-report.json --run-info run-info.json --sealed-manifest <outside-reviewer-dir.json> --output <new-run-manifest.json> --dataset-id <id> --authorization-review <review-ref> --hardware-profile <host> --runtime-version <version> --code-revision <revision> --run-id <id> [--comparisons comparisons.jsonl]\n" +
        "  dotnet run --project .\\tools\\BlindEvaluationRunner -- finalize-cohort --gold blind-eval.jsonl --raw-predictions a.jsonl --predictions a-reviewed.jsonl --run-info a-run-info.json --sealed-manifest a-sealed.json --candidate-id a --raw-predictions b.jsonl --predictions b-reviewed.jsonl --run-info b-run-info.json --sealed-manifest b-sealed.json --candidate-id b --report cohort-report.json --comparisons comparisons.jsonl --pairwise-answers completed-answers.jsonl --pairwise-review-package pairwise-review.jsonl --pairwise-sealed-manifest <pairwise-map.json> --output <new-cohort-manifest.json> --dataset-id <id> --authorization-review <review-ref> --hardware-profile <host> --runtime-version <version> --code-revision <revision> --run-id <id>\n" +
            "snapshot 只导出产品首轮提示，不调用 Provider。run 会执行单个显式候选及完整产品工作流；云端必须显式传入 --allow-cloud 与 --authorization-ref，密钥只能从候选配置指定的环境变量读取。硬件档位在候选运行时声明，并写入评审目录外的封存映射及 run-info；请使用不含设备序列号、主机名或用户名的稳定标签。该标签是操作员声明，不是硬件真实性证明；云端记录的是候选执行主机档位，不代表云端推理服务器硬件。ManagedLocal 必须显式传入 --runtime-root；模型目录默认使用产品 LocalModelStore 默认目录。评审输出含输入和成稿，必须保存在本机受控目录；封存映射必须单独保管。frozen_test 还需确认候选、提示和参数均已锁定。internal-polish-run 只连接本机 Ollama、只读取 purpose=internal_regression_only 的 development 切片，输出始终写 phase_0_gate_contribution=0 的本地诊断记录，不进行盲评或模型晋级评分。");
}
