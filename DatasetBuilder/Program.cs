using System.Text.Json;

namespace Huaxiazi.DatasetBuilder;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintHelp();
            return 0;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "generate" => Generate(args[1..]),
                "architecture-dataset" => ArchitectureDataset(args[1..]),
                "architecture-validate" => ArchitectureValidate(args[1..]),
                "architecture-batch" => ArchitectureBatch(args[1..]),
                "architecture-evaluate" => ArchitectureEvaluate(args[1..]),
                "architecture-compare" => ArchitectureCompare(args[1..]),
                "validate" => Validate(args[1..]),
                "evaluate" => Evaluate(args[1..]),
                "mine-failures" => MineFailures(args[1..]),
                "leakage-check" => LeakageCheck(args[1..]),
                "report" => Report(args[1..]),
                _ => Fail($"未知命令：{args[0]}")
            };
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or JsonException)
        {
            Console.Error.WriteLine($"错误：{exception.Message}");
            return 2;
        }
    }

    private static int Generate(string[] args)
    {
        var output = GetOption(args, "output") ?? Path.Combine(Environment.CurrentDirectory, "datasets", "v1");
        var seed = ParseInt(GetOption(args, "seed"), 20260907, "seed");
        var options = new DatasetGenerationOptions
        {
            Seed = seed,
            TrainCount = ParseInt(GetOption(args, "train"), 10_000, "train"),
            DevCount = ParseInt(GetOption(args, "dev"), 1_000, "dev"),
            TestCount = ParseInt(GetOption(args, "test"), 1_000, "test")
        };
        var records = SyntheticDatasetGenerator.Generate(options);
        var issues = DatasetValidator.Validate(records);
        if (issues.Count > 0)
        {
            Console.Error.WriteLine($"生成结果未通过校验：{issues.Count} 个问题。首个问题：{issues[0].Message}");
            return 3;
        }
        var leakageIssues = DatasetLeakageChecker.Check(records);
        if (leakageIssues.Count > 0)
        {
            Console.Error.WriteLine($"生成结果存在跨 split 泄漏：{leakageIssues.Count} 个问题。");
            return 3;
        }
        var export = DatasetExporter.Export(output, records);
        var manifest = new
        {
            schema_version = "1.0",
            dataset_version = "hxz-synthetic-v1",
            generated_at_utc = DateTimeOffset.UtcNow,
            seed,
            counts = new { total = records.Count, train = options.TrainCount, dev = options.DevCount, test = options.TestCount },
            modes = new { polish = records.Count(item => item.Mode == "polish"), prompt_optimize = records.Count(item => item.Mode == "prompt_optimize") },
            files = new { canonical = Path.GetFileName(export.CanonicalPath), sft = Path.GetFileName(export.SftPath), preference = Path.GetFileName(export.PreferencePath), report = "report.json" },
            dataset_hash = DatasetRecordFingerprint.ComputeDataset(records),
            fingerprints = records.Take(100).Select(DatasetRecordFingerprint.Compute).ToArray()
        };
        File.WriteAllText(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        var distribution = DatasetReportBuilder.Build(records);
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(distribution, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { output, records = records.Count, export.CanonicalPath, export.SftPath, export.PreferencePath }));
        return 0;
    }

    private static int ArchitectureDataset(string[] args)
    {
        var output = GetOption(args, "output") ?? Path.Combine(Environment.CurrentDirectory, "datasets", "architecture-v1");
        var options = new PromptArchitectureGenerationOptions
        {
            Seed = ParseInt(GetOption(args, "seed"), 20260907, "seed"),
            TrainCount = ParseInt(GetOption(args, "train"), 10_000, "train"),
            DevCount = ParseInt(GetOption(args, "dev"), 1_000, "dev"),
            TestCount = ParseInt(GetOption(args, "test"), 1_000, "test")
        };
        var records = PromptArchitectureDatasetGenerator.Generate(options);
        var issues = PromptArchitectureDatasetValidator.Validate(records);
        if (issues.Count > 0) return Fail($"架构数据集校验失败：{issues.Count} 个问题。");
        if (records.GroupBy(r => r.Input, StringComparer.Ordinal).Any(g => g.Select(r => r.Split).Distinct().Count() > 1))
            return Fail("架构数据集存在跨 split 输入重复。");
        PromptArchitectureDatasetGenerator.Export(output, records);
        var manifest = new
        {
            schema_version = "1.1",
            dataset_version = "hxz-prompt-architecture-v1",
            purpose = "prompt_architecture_search",
            generated_at_utc = DateTimeOffset.UtcNow,
            seed = options.Seed,
            counts = new { total = records.Count, train = options.TrainCount, dev = options.DevCount, test = options.TestCount },
            layers = new[] { "system", "developer", "skill", "harness", "output_contract" },
            variants = new[] { "minimal", "explicit", "policy_first", "examples_first" },
            scenarios = new[] { "normal_request", "ambiguity", "prompt_injection", "high_risk_fact", "tool_failure", "format_violation", "long_context", "skill_conflict", "memory_conflict", "tool_parallel" },
            constraint_fields = new[] { "required_constraints", "forbidden_constraints", "fidelity_anchors" },
            split_policy = new { train = "family-00..07", dev = "family-08..09", test = "family-10..11", input_and_family_disjoint = true },
            files = new { train = "architecture_train.jsonl", dev = "architecture_dev.jsonl", test = "architecture_test.jsonl" }
        };
        File.WriteAllText(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { output, records = records.Count }));
        return 0;
    }

    private static int ArchitectureEvaluate(string[] args)
    {
        var goldPath = GetOption(args, "gold") ?? throw new ArgumentException("architecture-evaluate 需要 --gold。", "gold");
        var predictionPath = GetOption(args, "predictions") ?? throw new ArgumentException("architecture-evaluate 需要 --predictions。", "predictions");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var gold = File.ReadLines(goldPath).Select(line => JsonSerializer.Deserialize<PromptArchitectureRecord>(line, options) ?? throw new JsonException("架构 gold 记录为空。")).ToArray();
        var predictions = File.ReadLines(predictionPath).Select(line => JsonSerializer.Deserialize<ArchitecturePrediction>(line, options) ?? throw new JsonException("架构预测记录为空。")).ToArray();
        var report = PromptArchitectureEvaluator.Evaluate(gold, predictions);
        var json = JsonSerializer.Serialize(report, options);
        var output = GetOption(args, "output");
        if (!string.IsNullOrWhiteSpace(output)) File.WriteAllText(output, json + Environment.NewLine);
        Console.WriteLine(json);
        return report.Count > 0 ? 0 : 4;
    }

    private static int ArchitectureValidate(string[] args)
    {
        var path = GetOption(args, "input") ?? (args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : null);
        if (string.IsNullOrWhiteSpace(path)) return Fail("architecture-validate 需要 --input <architecture_*.jsonl>。");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var records = File.ReadLines(path).Select(line => JsonSerializer.Deserialize<PromptArchitectureRecord>(line, options) ?? throw new JsonException("架构记录为空。")).ToArray();
        var issues = PromptArchitectureDatasetValidator.Validate(records).ToList();
        var leakage = records.GroupBy(r => r.Input, StringComparer.Ordinal).Where(g => g.Select(r => r.Split).Distinct().Count() > 1).ToArray();
        if (leakage.Length > 0) issues.Add(new PromptArchitectureValidationIssue("cross-split-leakage", "dataset", "相同输入出现在多个 split。"));
        Console.WriteLine(JsonSerializer.Serialize(new { valid = issues.Count == 0, issue_count = issues.Count, issues }, options));
        return issues.Count == 0 ? 0 : 3;
    }

    private static int ArchitectureCompare(string[] args)
    {
        var goldPath = GetOption(args, "gold") ?? throw new ArgumentException("architecture-compare 需要 --gold。", "gold");
        var predictionPath = GetOption(args, "predictions") ?? throw new ArgumentException("architecture-compare 需要 --predictions。", "predictions");
        var left = GetOption(args, "left") ?? throw new ArgumentException("architecture-compare 需要 --left。", "left");
        var right = GetOption(args, "right") ?? throw new ArgumentException("architecture-compare 需要 --right。", "right");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var gold = File.ReadLines(goldPath).Select(line => JsonSerializer.Deserialize<PromptArchitectureRecord>(line, options) ?? throw new JsonException("架构 gold 记录为空。")).ToArray();
        var predictions = File.ReadLines(predictionPath).Select(line => JsonSerializer.Deserialize<ArchitecturePrediction>(line, options) ?? throw new JsonException("架构预测记录为空。")).ToArray();
        var report = PromptArchitectureEvaluator.Compare(gold, predictions, left, right);
        var json = JsonSerializer.Serialize(report, options);
        var output = GetOption(args, "output");
        if (!string.IsNullOrWhiteSpace(output)) File.WriteAllText(output, json + Environment.NewLine);
        Console.WriteLine(json);
        return report.ComparableCount > 0 ? 0 : 4;
    }

    private static int ArchitectureBatch(string[] args)
    {
        var input = GetOption(args, "input") ?? throw new ArgumentException("architecture-batch 需要 --input。", "input");
        var candidates = GetOption(args, "candidates") ?? throw new ArgumentException("architecture-batch 需要 --candidates。", "candidates");
        var output = GetOption(args, "output") ?? throw new ArgumentException("architecture-batch 需要 --output。", "output");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var records = File.ReadLines(input).Select(line => JsonSerializer.Deserialize<PromptArchitectureRecord>(line, options) ?? throw new JsonException("架构记录为空。"));
        var requests = ArchitectureExperimentBatchBuilder.Build(records, candidates);
        ArchitectureExperimentBatchBuilder.Export(output, requests);
        Console.WriteLine(JsonSerializer.Serialize(new { output, requests = requests.Count, gold_included = false }));
        return 0;
    }

    private static int Validate(string[] args)
    {
        var path = GetOption(args, "input") ?? (args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : null);
        if (string.IsNullOrWhiteSpace(path)) return Fail("validate 需要 --input <canonical.jsonl>。");
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };
        var records = File.ReadLines(path).Select(line => JsonSerializer.Deserialize<DatasetRecord>(line, jsonOptions) ?? throw new JsonException("记录为空。"));
        var issues = DatasetValidator.Validate(records).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new { valid = issues.Length == 0, issue_count = issues.Length, issues }));
        return issues.Length == 0 ? 0 : 3;
    }

    private static int Evaluate(string[] args)
    {
        var goldPath = GetOption(args, "gold") ?? throw new ArgumentException("evaluate 需要 --gold <canonical.jsonl>。", "gold");
        var predictionPath = GetOption(args, "predictions") ?? throw new ArgumentException("evaluate 需要 --predictions <predictions.jsonl>。", "predictions");
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var records = File.ReadLines(goldPath).Select(line => JsonSerializer.Deserialize<DatasetRecord>(line, jsonOptions) ?? throw new JsonException("Gold 记录为空。")).ToArray();
        var predictions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(predictionPath))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var id = root.GetProperty("id").GetString() ?? throw new JsonException("预测缺少 id。");
            var output = root.GetProperty("output").GetString() ?? string.Empty;
            predictions[id] = output;
        }
        var report = DatasetEvaluator.Evaluate(records, predictions);
        var outputPath = GetOption(args, "output");
        var json = JsonSerializer.Serialize(report, jsonOptions);
        if (!string.IsNullOrWhiteSpace(outputPath)) File.WriteAllText(outputPath, json + Environment.NewLine);
        Console.WriteLine(json);
        return report.Passed ? 0 : 4;
    }

    private static int MineFailures(string[] args)
    {
        var goldPath = GetOption(args, "gold") ?? throw new ArgumentException("mine-failures 需要 --gold。", "gold");
        var predictionPath = GetOption(args, "predictions") ?? throw new ArgumentException("mine-failures 需要 --predictions。", "predictions");
        var outputPath = GetOption(args, "output") ?? throw new ArgumentException("mine-failures 需要 --output。", "output");
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var records = File.ReadLines(goldPath).Select(line => JsonSerializer.Deserialize<DatasetRecord>(line, jsonOptions) ?? throw new JsonException("Gold 记录为空。")).ToArray();
        var predictions = File.ReadLines(predictionPath).Select(line => JsonDocument.Parse(line).RootElement).ToDictionary(root => root.GetProperty("id").GetString()!, root => root.GetProperty("output").GetString() ?? string.Empty, StringComparer.Ordinal);
        var mined = DatasetFailureMiner.Mine(records, predictions);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.WriteAllLines(outputPath, mined.Select(pair => JsonSerializer.Serialize(pair, jsonOptions)));
        Console.WriteLine(JsonSerializer.Serialize(new { output = outputPath, mined = mined.Count }));
        return 0;
    }

    private static int LeakageCheck(string[] args)
    {
        var path = GetOption(args, "input") ?? (args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : null);
        if (string.IsNullOrWhiteSpace(path)) return Fail("leakage-check 需要 --input <canonical.jsonl>。");
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var records = File.ReadLines(path).Select(line => JsonSerializer.Deserialize<DatasetRecord>(line, jsonOptions) ?? throw new JsonException("记录为空。"));
        var issues = DatasetLeakageChecker.Check(records).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new { clean = issues.Length == 0, issue_count = issues.Length, issues }, jsonOptions));
        return issues.Length == 0 ? 0 : 3;
    }

    private static int Report(string[] args)
    {
        var path = GetOption(args, "input") ?? (args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : null);
        if (string.IsNullOrWhiteSpace(path)) return Fail("report 需要 --input <canonical.jsonl>。");
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var records = File.ReadLines(path).Select(line => JsonSerializer.Deserialize<DatasetRecord>(line, jsonOptions) ?? throw new JsonException("记录为空.")).ToArray();
        var report = DatasetReportBuilder.Build(records);
        var outputPath = GetOption(args, "output");
        var json = JsonSerializer.Serialize(report, jsonOptions);
        if (!string.IsNullOrWhiteSpace(outputPath)) File.WriteAllText(outputPath, json + Environment.NewLine);
        Console.WriteLine(json);
        return report.LeakageIssueCount == 0 ? 0 : 3;
    }

    private static string? GetOption(string[] args, string name)
    {
        var prefix = "--" + name + "=";
        var inline = args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (inline is not null) return inline[prefix.Length..];
        var index = Array.FindIndex(args, arg => string.Equals(arg, "--" + name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int ParseInt(string? value, int fallback, string name) =>
        value is null ? fallback : int.TryParse(value, out var parsed) && parsed >= 0 ? parsed : throw new ArgumentException($"--{name} 必须是非负整数。", name);

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"错误：{message}");
        PrintHelp();
        return 2;
    }

    private static void PrintHelp() => Console.WriteLine("用法：dotnet run --project DatasetBuilder -- generate [--output PATH] [--seed N] [--train N] [--dev N] [--test N]\n      dotnet run --project DatasetBuilder -- architecture-dataset [--output PATH] [--seed N] [--train N] [--dev N] [--test N]\n      dotnet run --project DatasetBuilder -- architecture-validate --input architecture_*.jsonl\n      dotnet run --project DatasetBuilder -- architecture-batch --input architecture_dev.jsonl --candidates candidate-configs.json --output requests.jsonl\n      dotnet run --project DatasetBuilder -- architecture-evaluate --gold architecture_test.jsonl --predictions predictions.jsonl [--output report.json]\n      dotnet run --project DatasetBuilder -- architecture-compare --gold architecture_test.jsonl --predictions predictions.jsonl --left candidate-a --right candidate-b [--output report.json]\n      dotnet run --project DatasetBuilder -- validate --input canonical.jsonl\n      dotnet run --project DatasetBuilder -- leakage-check --input canonical.jsonl\n      dotnet run --project DatasetBuilder -- report --input canonical.jsonl [--output report.json]\n      dotnet run --project DatasetBuilder -- evaluate --gold canonical.jsonl --predictions predictions.jsonl [--output report.json]\n      dotnet run --project DatasetBuilder -- mine-failures --gold canonical.jsonl --predictions predictions.jsonl --output dpo-failures.jsonl");
}
