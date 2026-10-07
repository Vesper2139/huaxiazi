using System.Text.Json;

namespace Huaxiazi.DatasetBuilder;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintHelp();
            PrintBlindPairwiseHelp();
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
                "polish-agent-validate" => PolishAgentValidate(args[1..]),
                "polish-regression-report" => PolishRegressionReport(args[1..]),
                "polish-regression-backbone-report" => PolishRegressionBackboneReport(args[1..]),
                "polish-regression-hierarchy-report" => PolishRegressionHierarchyReport(args[1..]),
                "polish-regression-label-draft" => PolishRegressionLabelDraft(args[1..]),
                "polish-regression-review-packet" => PolishRegressionReviewPacket(args[1..]),
                "polish-regression-review-validate" => PolishRegressionReviewValidate(args[1..]),
                "polish-regression-supplement-review-validate" => PolishRegressionSupplementReviewValidate(args[1..]),
                "polish-regression-supplement-review-import" => PolishRegressionSupplementReviewImport(args[1..]),
                "local-model-candidate-review-validate" => LocalModelCandidateReviewValidate(args[1..]),
                "local-model-candidate-review-import" => LocalModelCandidateReviewImport(args[1..]),
                "polish-regression-coverage-gap-report" => PolishRegressionCoverageGapReport(args[1..]),
                "polish-regression-neardup-report" => PolishRegressionNearDuplicateReport(args[1..]),
                "polish-regression-supplement-neardup-report" => PolishRegressionSupplementNearDuplicateReport(args[1..]),
                "polish-regression-supplement-coverage-report" => PolishRegressionSupplementCoverageReport(args[1..]),
                "polish-regression-supplement-spec-validate" => PolishRegressionSupplementSpecificationValidate(args[1..]),
                "polish-regression-supplement-lineage-audit" => PolishRegressionSupplementLineageAudit(args[1..]),
                "polish-regression-supplement-spec-review-packet" => PolishRegressionSupplementSpecificationReviewPacket(args[1..]),
                "polish-regression-supplement-spec-review-validate" => PolishRegressionSupplementSpecificationReviewValidate(args[1..]),
                "polish-regression-supplement-spec-review-import" => PolishRegressionSupplementSpecificationReviewImport(args[1..]),
                "polish-regression-neardup-validate" => PolishRegressionNearDuplicateValidate(args[1..]),
                "polish-regression-neardup-import-workbook" => PolishRegressionNearDuplicateImportWorkbook(args[1..]),
                "polish-regression-build" => PolishRegressionBuild(args[1..]),
                "polish-regression-finalize" => PolishRegressionFinalize(args[1..]),
                "polish-regression-validate" => PolishRegressionValidate(args[1..]),
                "blind-validate" => BlindValidate(args[1..]),
                "blind-evidence-validate" => BlindEvidenceValidate(args[1..]),
                "source-register-validate" => SourceRegisterValidate(args[1..]),
                "blind-evaluate" => BlindEvaluate(args[1..]),
                "blind-manifest-validate" => BlindManifestValidate(args[1..]),
                "blind-pairwise-package" => BlindPairwisePackage(args[1..]),
                "blind-pairwise-merge" => BlindPairwiseMerge(args[1..]),
                "evaluate" => Evaluate(args[1..]),
                "mine-failures" => MineFailures(args[1..]),
                "leakage-check" => LeakageCheck(args[1..]),
                "report" => Report(args[1..]),
                _ => Fail($"未知命令：{args[0]}")
            };
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine($"盲评门禁未通过：{exception.Message}");
            return 3;
        }
        catch (KeyNotFoundException)
        {
            Console.Error.WriteLine("错误：输入工件缺少必需字段。");
            return 2;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or JsonException)
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

    private static int BlindValidate(string[] args)
    {
        var path = GetOption(args, "input") ?? (args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : null);
        if (string.IsNullOrWhiteSpace(path)) return Fail("blind-validate 需要 --input <blind-eval.jsonl>。");
        var trainingPaths = GetOptions(args, "training").ToArray();
        if (trainingPaths.Length == 0) return Fail("blind-validate 需要至少一个 --training <canonical.jsonl> 以检查与既有训练/开发数据的重叠。");
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };
        var records = ReadBlindGold(path, jsonOptions);
        var knownTrainingRecords = trainingPaths.SelectMany(trainingPath => File.ReadLines(trainingPath)
            .Select((line, index) => ReadKnownRecord(line, trainingPath, index + 1)))
            .ToArray();
        var issues = BlindEvaluationAuditor.Validate(records, knownTrainingRecords: knownTrainingRecords).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            valid = issues.Length == 0,
            minimum_sample_count_passed = records.Length >= BlindEvaluationAdmissionProtocol.MinimumSampleCount,
            record_count = records.Length,
            admission_protocol = new
            {
                id = BlindEvaluationAdmissionProtocol.ProtocolId,
                version = BlindEvaluationAdmissionProtocol.Version,
                policy_sha256 = BlindEvaluationAdmissionProtocol.PolicySha256
            },
            external_evidence = new
            {
                verified_by_tool = false,
                authorization_status = "not_verified",
                reviewer_independence_status = "not_verified"
            },
            coverage = BlindEvaluationAuditor.SummarizeCoverage(records),
            reviewer_agreement = BlindEvaluationAuditor.SummarizeReviewerAgreement(records),
            issue_count = issues.Length,
            issues
        }, jsonOptions));
        return issues.Length == 0 ? 0 : 3;
    }

    private static int BlindEvidenceValidate(string[] args)
    {
        var inputPath = GetOption(args, "input") ?? throw new ArgumentException("blind-evidence-validate 需要 --input <blind-eval.jsonl>。", "input");
        var manifestPath = GetOption(args, "manifest") ?? throw new ArgumentException("blind-evidence-validate 需要 --manifest <controlled-evidence-manifest.json>。", "manifest");
        var trainingPaths = GetOptions(args, "training").ToArray();
        if (trainingPaths.Length == 0) throw new ArgumentException("blind-evidence-validate 需要至少一个 --training <canonical.jsonl>。", "training");

        var evidence = BlindEvaluationEvidenceManifestValidator.Validate(File.ReadAllText(manifestPath), inputPath);
        var records = ReadBlindGold(inputPath, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });
        var knownRecords = trainingPaths.SelectMany(path => File.ReadLines(path)
            .Select((line, index) => ReadKnownRecord(line, path, index + 1))).ToArray();
        var integrityIssues = BlindEvaluationAuditor.Validate(records, knownTrainingRecords: knownRecords);
        var report = new
        {
            validation_passed = evidence.Valid && integrityIssues.Count == 0,
            evidence_index_structurally_valid = evidence.Valid,
            record_integrity_valid = integrityIssues.Count == 0,
            record_count = evidence.RecordCount,
            dataset_sha256 = evidence.DatasetSha256,
            rights_reviewed = false,
            reviewer_independence_verified = false,
            rights_review_status_from_manifest_claim = evidence.AuthorizationReviewStatus,
            reviewer_independence_status_from_manifest_claim = evidence.ReviewerIndependenceReviewStatus,
            external_evidence_authenticity_verified_by_tool = evidence.ExternalEvidenceVerified,
            phase_0_gate_passed = false,
            issue_count = evidence.Issues.Count + integrityIssues.Count,
            evidence_issues = evidence.Issues,
            record_integrity_issues = integrityIssues
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true });
        if (GetOption(args, "output") is { } outputPath)
            ImmutableArtifactWriter.WriteNew(outputPath, json + Environment.NewLine);
        Console.WriteLine(json);
        return evidence.Valid && integrityIssues.Count == 0 ? 0 : 3;
    }

    private static int SourceRegisterValidate(string[] args)
    {
        var registerPath = GetOption(args, "register") ?? throw new ArgumentException("source-register-validate 需要 --register <source-register.json>。", "register");
        var datasetPath = GetOption(args, "dataset") ?? throw new ArgumentException("source-register-validate 需要 --dataset <blind-eval.jsonl>。", "dataset");
        var result = BlindEvaluationSourceRegisterValidator.Validate(File.ReadAllText(registerPath), File.ReadAllText(datasetPath));
        var report = new
        {
            validation_passed = result.RecordIntegrityValid,
            record_integrity_valid = result.RecordIntegrityValid,
            sources_covered = result.SourcesCovered,
            required_uses_claimed = result.RequiredUsesClaimed,
            rights_reviewed = false,
            rights_review_claim_complete = result.RightsReviewClaimComplete,
            external_evidence_authenticity_verified_by_tool = result.ExternalEvidenceAuthenticityVerifiedByTool,
            phase_0_gate_passed = result.Phase0GatePassed,
            source_count = result.SourceCount,
            dataset_record_count = result.DatasetRecordCount,
            issue_count = result.Issues.Count,
            issues = result.Issues
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true });
        if (GetOption(args, "output") is { } outputPath)
            ImmutableArtifactWriter.WriteNew(outputPath, json + Environment.NewLine);
        Console.WriteLine(json);
        return result.RecordIntegrityValid ? 0 : 3;
    }

    private static int PolishAgentValidate(string[] args)
    {
        var canonicalPath = GetOption(args, "input") ?? throw new ArgumentException("polish-agent-validate 需要 --input <v2-canonical.jsonl>。");
        var sftPath = GetOption(args, "sft") ?? throw new ArgumentException("polish-agent-validate 需要 --sft <v2-sft.jsonl>。");
        var manifestPath = GetOption(args, "manifest") ?? throw new ArgumentException("polish-agent-validate 需要 --manifest <v2-manifest.json>。");
        var sourcePath = GetOption(args, "source") ?? throw new ArgumentException("polish-agent-validate 需要 --source <source-canonical.jsonl>。");
        var report = PolishAgentDatasetAuditor.Validate(canonicalPath, sftPath, manifestPath, sourcePath);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            valid = report.Valid,
            sample_count = report.SampleCount,
            counts = new { train = report.TrainCount, dev = report.DevCount, test = report.TestCount },
            template_family_count = report.TemplateFamilyCount,
            source_record_match_count = report.SourceRecordMatchCount,
            single_reviewer_synthetic_record_count = report.SingleReviewerSyntheticRecordCount,
            human_blind_review_evidence = false,
            content_telemetry_enabled = false,
            canonical_sha256 = report.CanonicalSha256,
            sft_sha256 = report.SftSha256,
            source_sha256 = report.SourceSha256,
            issue_count = report.Issues.Count,
            issues = report.Issues.Take(50)
        }, options));
        return report.Valid ? 0 : 3;
    }

    private static int PolishRegressionReport(string[] args)
    {
        var input = GetOption(args, "input") ?? throw new ArgumentException("polish-regression-report 需要 --input <canonical.jsonl>。");
        var sft = GetOption(args, "sft") ?? throw new ArgumentException("polish-regression-report 需要 --sft <sft.jsonl>。");
        var manifest = GetOption(args, "manifest") ?? throw new ArgumentException("polish-regression-report 需要 --manifest <manifest.json>。");
        var source = GetOption(args, "source") ?? throw new ArgumentException("polish-regression-report 需要 --source <source-canonical.jsonl>。");
        var output = GetOption(args, "out") ?? throw new ArgumentException("polish-regression-report 需要 --out <new-report.json>。");
        var report = PolishRegressionBaselineAuditor.Audit(input, sft, manifest, source);
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true });
        ImmutableArtifactWriter.WriteNew(output, json + Environment.NewLine);
        Console.WriteLine(JsonSerializer.Serialize(new { output = Path.GetFullPath(output), report.RowCount, report.DistinctInputCount, report.DistinctReferenceOutputCount, report.TemplateFamilyCount }));
        return 0;
    }

    private static int PolishRegressionBackboneReport(string[] args)
    {
        var input = GetOption(args, "input") ?? throw new ArgumentException("polish-regression-backbone-report 需要 --input <canonical.jsonl>。");
        var output = GetOption(args, "out") ?? throw new ArgumentException("polish-regression-backbone-report 需要 --out <new-report.json>。");
        var report = PolishRegressionBackboneAuditor.Audit(input);
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true });
        ImmutableArtifactWriter.WriteNew(output, json + Environment.NewLine);
        Console.WriteLine(JsonSerializer.Serialize(new { output = Path.GetFullPath(output), report.RowCount, report.MatchedLegacyIntentSuffixRowCount, report.DistinctBackboneCount }));
        return 0;
    }

    private static int PolishRegressionHierarchyReport(string[] args)
    {
        var input = GetOption(args, "input") ?? throw new ArgumentException("polish-regression-hierarchy-report 需要 --input <cases.jsonl>。");
        var output = GetOption(args, "out") ?? throw new ArgumentException("polish-regression-hierarchy-report 需要 --out <new-report.json>。");
        var report = PolishRegressionHierarchyAuditor.Audit(input);
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true });
        ImmutableArtifactWriter.WriteNew(output, json + Environment.NewLine);
        Console.WriteLine(JsonSerializer.Serialize(new { output = Path.GetFullPath(output), report.TaskFamilyCount, report.BackboneCount }));
        return 0;
    }

    private static int PolishRegressionLabelDraft(string[] args)
    {
        var cases = GetOption(args, "cases") ?? throw new ArgumentException("polish-regression-label-draft 需要 --cases <cases.jsonl>。");
        var source = GetOption(args, "source") ?? throw new ArgumentException("polish-regression-label-draft 需要 --source <canonical.jsonl>。");
        var hierarchy = GetOption(args, "hierarchy") ?? throw new ArgumentException("polish-regression-label-draft 需要 --hierarchy <hierarchy-report.json>。");
        var parentManifest = GetOption(args, "parent-manifest") ?? throw new ArgumentException("polish-regression-label-draft 需要 --parent-manifest <manifest.json>。");
        var prompt = GetOption(args, "prompt") ?? throw new ArgumentException("polish-regression-label-draft 需要 --prompt <label-prompt.md>。");
        var output = GetOption(args, "output") ?? throw new ArgumentException("polish-regression-label-draft 需要 --output <new-draft-directory>。");
        var draftBy = GetOption(args, "draft-by") ?? throw new ArgumentException("polish-regression-label-draft 需要 --draft-by <author-label>。");
        var model = GetOption(args, "model") ?? throw new ArgumentException("polish-regression-label-draft 需要 --model <model-id>。");
        var draftedAt = GetOption(args, "drafted-at-utc") ?? throw new ArgumentException("polish-regression-label-draft 需要 --drafted-at-utc <ISO-8601-UTC>。");
        var report = PolishRegressionDraftLabelBuilder.Build(cases, source, hierarchy, parentManifest, prompt, output, draftBy, model, draftedAt);
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true }));
        return 0;
    }

    private static int PolishRegressionReviewPacket(string[] args)
    {
        var cases = GetOption(args, "cases") ?? throw new ArgumentException("polish-regression-review-packet 需要 --cases <cases.jsonl>。");
        var source = GetOption(args, "source") ?? throw new ArgumentException("polish-regression-review-packet 需要 --source <canonical.jsonl>。");
        var labels = GetOption(args, "labels") ?? throw new ArgumentException("polish-regression-review-packet 需要 --labels <labels.jsonl>。");
        var parentManifest = GetOption(args, "parent-manifest") ?? throw new ArgumentException("polish-regression-review-packet 需要 --parent-manifest <manifest.json>。");
        var draftManifest = GetOption(args, "draft-manifest") ?? throw new ArgumentException("polish-regression-review-packet 需要 --draft-manifest <manifest.json>。");
        var hierarchy = GetOption(args, "hierarchy") ?? throw new ArgumentException("polish-regression-review-packet 需要 --hierarchy <hierarchy-report.json>。");
        var output = GetOption(args, "output") ?? throw new ArgumentException("polish-regression-review-packet 需要 --output <new-packet-directory>。");
        var report = PolishRegressionHumanReviewPacketBuilder.Build(cases, source, labels, parentManifest, draftManifest, hierarchy, output);
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true }));
        return 0;
    }

    private static int PolishRegressionReviewValidate(string[] args)
    {
        var packet = GetOption(args, "packet-dir") ?? throw new ArgumentException("polish-regression-review-validate 需要 --packet-dir <review-packet-directory>。");
        var firstPass = GetOption(args, "first-pass") ?? throw new ArgumentException("polish-regression-review-validate 需要 --first-pass <completed-source-first-pass.jsonl>。");
        var decisions = GetOption(args, "decisions") ?? throw new ArgumentException("polish-regression-review-validate 需要 --decisions <completed-human-decisions.jsonl>。");
        var labels = GetOption(args, "labels") ?? throw new ArgumentException("polish-regression-review-validate 需要 --labels <draft-labels.jsonl>。");
        var draftManifest = GetOption(args, "draft-manifest") ?? throw new ArgumentException("polish-regression-review-validate 需要 --draft-manifest <draft-manifest.json>。");
        var report = PolishRegressionHumanReviewValidator.Validate(packet, firstPass, decisions, labels, draftManifest);
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true }));
        return report.Valid ? 0 : 3;
    }

    private static int PolishRegressionSupplementReviewValidate(string[] args)
    {
        var cases = GetOption(args, "cases") ?? throw new ArgumentException("polish-regression-supplement-review-validate 需要 --cases <ai-draft-cases.jsonl>。");
        var reviewCsv = GetOption(args, "review-csv") ?? throw new ArgumentException("polish-regression-supplement-review-validate 需要 --review-csv <completed-review.csv>。");
        var manifest = GetOption(args, "manifest") ?? throw new ArgumentException("polish-regression-supplement-review-validate 需要 --manifest <review-package-manifest.json>。");
        var report = PolishRegressionSupplementReviewValidator.Validate(cases, reviewCsv, manifest);
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true }));
        return report.Valid ? 0 : 3;
    }

    private static int PolishRegressionSupplementReviewImport(string[] args)
    {
        var cases = GetOption(args, "cases") ?? throw new ArgumentException("polish-regression-supplement-review-import 需要 --cases <ai-draft-cases.jsonl>。");
        var reviewCsv = GetOption(args, "review-csv") ?? throw new ArgumentException("polish-regression-supplement-review-import 需要 --review-csv <completed-review.csv>。");
        var manifest = GetOption(args, "manifest") ?? throw new ArgumentException("polish-regression-supplement-review-import 需要 --manifest <review-package-manifest.json>。");
        var output = GetOption(args, "output") ?? throw new ArgumentException("polish-regression-supplement-review-import 需要 --output <new-decisions.jsonl>。");
        var result = PolishRegressionSupplementReviewImporter.Import(cases, reviewCsv, manifest, output);
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true }));
        return 0;
    }

    private static int LocalModelCandidateReviewValidate(string[] args)
    {
        var packet = GetOption(args, "candidate-packet") ?? throw new ArgumentException("local-model-candidate-review-validate 需要 --candidate-packet <candidate-review.jsonl>。");
        var reviewCsv = GetOption(args, "review-csv") ?? throw new ArgumentException("local-model-candidate-review-validate 需要 --review-csv <completed-review.csv>。");
        var metadata = GetOption(args, "metadata") ?? throw new ArgumentException("local-model-candidate-review-validate 需要 --metadata <review-metadata.json>。");
        var report = LocalModelCandidateReviewImporter.Validate(packet, reviewCsv, metadata);
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true }));
        return report.Valid ? 0 : 3;
    }

    private static int LocalModelCandidateReviewImport(string[] args)
    {
        var packet = GetOption(args, "candidate-packet") ?? throw new ArgumentException("local-model-candidate-review-import 需要 --candidate-packet <candidate-review.jsonl>。");
        var reviewCsv = GetOption(args, "review-csv") ?? throw new ArgumentException("local-model-candidate-review-import 需要 --review-csv <completed-review.csv>。");
        var metadata = GetOption(args, "metadata") ?? throw new ArgumentException("local-model-candidate-review-import 需要 --metadata <review-metadata.json>。");
        var output = GetOption(args, "output") ?? throw new ArgumentException("local-model-candidate-review-import 需要 --output <new-human-review.jsonl>。");
        var result = LocalModelCandidateReviewImporter.Import(packet, reviewCsv, metadata, output);
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true }));
        return 0;
    }

    private static int PolishRegressionCoverageGapReport(string[] args)
    {
        var cases = GetOption(args, "cases") ?? throw new ArgumentException("polish-regression-coverage-gap-report 需要 --cases <cases.jsonl>。");
        var variants = GetOption(args, "variants") ?? throw new ArgumentException("polish-regression-coverage-gap-report 需要 --variants <variants.jsonl>。");
        var source = GetOption(args, "source") ?? throw new ArgumentException("polish-regression-coverage-gap-report 需要 --source <canonical.jsonl>。");
        var labels = GetOption(args, "labels") ?? throw new ArgumentException("polish-regression-coverage-gap-report 需要 --labels <labels.jsonl>。");
        var parentManifest = GetOption(args, "parent-manifest") ?? throw new ArgumentException("polish-regression-coverage-gap-report 需要 --parent-manifest <parent-manifest.json>。");
        var draftManifest = GetOption(args, "draft-manifest") ?? throw new ArgumentException("polish-regression-coverage-gap-report 需要 --draft-manifest <draft-manifest.json>。");
        var hierarchy = GetOption(args, "hierarchy") ?? throw new ArgumentException("polish-regression-coverage-gap-report 需要 --hierarchy <hierarchy-report.json>。");
        var output = GetOption(args, "output") ?? throw new ArgumentException("polish-regression-coverage-gap-report 需要 --output <new-report-directory>。");
        var report = PolishRegressionCoverageGapAuditor.Build(cases, variants, source, labels, parentManifest, draftManifest, hierarchy, output);
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true }));
        return 0;
    }

    private static int PolishRegressionBuild(string[] args)
    {
        var input = GetOption(args, "input") ?? throw new ArgumentException("polish-regression-build 需要 --input <legacy-canonical.jsonl>。");
        var output = GetOption(args, "output") ?? throw new ArgumentException("polish-regression-build 需要 --output <new-version-directory>。");
        var result = PolishRegressionDatasetBuilder.Build(input, output);
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true }));
        return 0;
    }

    private static int PolishRegressionNearDuplicateReport(string[] args)
    {
        var cases = GetOption(args, "cases") ?? throw new ArgumentException("polish-regression-neardup-report 需要 --cases <cases.jsonl>。");
        var parentManifest = GetOption(args, "parent-manifest") ?? throw new ArgumentException("polish-regression-neardup-report 需要 --parent-manifest <manifest.json>。");
        var rules = GetOption(args, "rules") ?? throw new ArgumentException("polish-regression-neardup-report 需要 --rules <versioned-rules.json>。");
        var output = GetOption(args, "output") ?? throw new ArgumentException("polish-regression-neardup-report 需要 --output <new-report-directory>。");
        var result = PolishRegressionNearDuplicateAuditor.Build(cases, parentManifest, rules, output);
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true }));
        return 0;
    }

    private static int PolishRegressionSupplementNearDuplicateReport(string[] args)
    {
        var baseCases = GetOption(args, "base-cases") ?? throw new ArgumentException("polish-regression-supplement-neardup-report 需要 --base-cases <frozen-cases.jsonl>。");
        var baseManifest = GetOption(args, "base-manifest") ?? throw new ArgumentException("polish-regression-supplement-neardup-report 需要 --base-manifest <frozen-manifest.json>。");
        var supplementCases = GetOption(args, "supplement-cases") ?? throw new ArgumentException("polish-regression-supplement-neardup-report 需要 --supplement-cases <draft-cases.jsonl>。");
        var supplementManifest = GetOption(args, "supplement-manifest") ?? throw new ArgumentException("polish-regression-supplement-neardup-report 需要 --supplement-manifest <draft-manifest.json>。");
        var rules = GetOption(args, "rules") ?? throw new ArgumentException("polish-regression-supplement-neardup-report 需要 --rules <versioned-rules.json>。");
        var output = GetOption(args, "output") ?? throw new ArgumentException("polish-regression-supplement-neardup-report 需要 --output <new-report-directory>。");
        var result = PolishRegressionSupplementNearDuplicateAuditor.Build(baseCases, baseManifest, supplementCases, supplementManifest, rules, output);
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true }));
        return 0;
    }

    private static int PolishRegressionSupplementCoverageReport(string[] args)
    {
        var cases = GetOption(args, "cases") ?? throw new ArgumentException("polish-regression-supplement-coverage-report 需要 --cases <draft-cases.jsonl>。");
        var manifest = GetOption(args, "manifest") ?? throw new ArgumentException("polish-regression-supplement-coverage-report 需要 --manifest <draft-manifest.json>。");
        var behaviorText = GetOption(args, "behaviors") ?? throw new ArgumentException("polish-regression-supplement-coverage-report 需要 --behaviors <comma-separated-category-ids>。");
        var behaviors = behaviorText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!int.TryParse(GetOption(args, "minimum-per-behavior"), out var minimum))
            throw new ArgumentException("polish-regression-supplement-coverage-report 需要整数 --minimum-per-behavior <count>。");
        var output = GetOption(args, "output") ?? throw new ArgumentException("polish-regression-supplement-coverage-report 需要 --output <new-report-directory>。");
        var result = PolishRegressionSupplementCoverageAuditor.Build(cases, manifest, behaviors, minimum, output);
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true }));
        return 0;
    }

    private static int PolishRegressionSupplementSpecificationValidate(string[] args)
    {
        var specsDirectory = GetOption(args, "specs-dir") ?? throw new ArgumentException("polish-regression-supplement-spec-validate 需要 --specs-dir <specification-directory>。");
        var coverageDirectory = GetOption(args, "coverage-dir") ?? throw new ArgumentException("polish-regression-supplement-spec-validate 需要 --coverage-dir <coverage-report-directory>。");
        var parentCases = GetOption(args, "parent-cases") ?? throw new ArgumentException("polish-regression-supplement-spec-validate 需要 --parent-cases <draft-cases.jsonl>。");
        var parentManifest = GetOption(args, "parent-manifest") ?? throw new ArgumentException("polish-regression-supplement-spec-validate 需要 --parent-manifest <draft-manifest.json>。");
        var report = PolishRegressionSupplementSpecificationValidator.Validate(specsDirectory, coverageDirectory, parentCases, parentManifest);
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true }));
        return report.Valid ? 0 : 3;
    }

    private static int PolishRegressionSupplementLineageAudit(string[] args)
    {
        var cases = GetOption(args, "cases") ?? throw new ArgumentException("polish-regression-supplement-lineage-audit 需要 --cases <draft-cases.jsonl>。");
        var manifest = GetOption(args, "manifest") ?? throw new ArgumentException("polish-regression-supplement-lineage-audit 需要 --manifest <draft-manifest.json>。");
        var report = PolishRegressionSupplementLineageAuditor.Validate(cases, manifest);
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true }));
        return report.Valid ? 0 : 3;
    }

    private static int PolishRegressionSupplementSpecificationReviewPacket(string[] args)
    {
        var specs = GetOption(args, "specs-dir") ?? throw new ArgumentException("polish-regression-supplement-spec-review-packet 需要 --specs-dir <specification-directory>。");
        var coverage = GetOption(args, "coverage-dir") ?? throw new ArgumentException("polish-regression-supplement-spec-review-packet 需要 --coverage-dir <coverage-directory>。");
        var parentCases = GetOption(args, "parent-cases") ?? throw new ArgumentException("polish-regression-supplement-spec-review-packet 需要 --parent-cases <parent-cases.jsonl>。");
        var parentManifest = GetOption(args, "parent-manifest") ?? throw new ArgumentException("polish-regression-supplement-spec-review-packet 需要 --parent-manifest <parent-manifest.json>。");
        var output = GetOption(args, "output") ?? throw new ArgumentException("polish-regression-supplement-spec-review-packet 需要 --output <new-review-directory>。");
        var report = PolishRegressionSupplementSpecificationReviewPacketBuilder.Build(specs, coverage, parentCases, parentManifest, output);
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true }));
        return 0;
    }

    private static int PolishRegressionSupplementSpecificationReviewValidate(string[] args)
    {
        var specs = GetOption(args, "specs-dir") ?? throw new ArgumentException("polish-regression-supplement-spec-review-validate 需要 --specs-dir <specification-directory>。");
        var coverage = GetOption(args, "coverage-dir") ?? throw new ArgumentException("polish-regression-supplement-spec-review-validate 需要 --coverage-dir <coverage-directory>。");
        var parentCases = GetOption(args, "parent-cases") ?? throw new ArgumentException("polish-regression-supplement-spec-review-validate 需要 --parent-cases <parent-cases.jsonl>。");
        var parentManifest = GetOption(args, "parent-manifest") ?? throw new ArgumentException("polish-regression-supplement-spec-review-validate 需要 --parent-manifest <parent-manifest.json>。");
        var packet = GetOption(args, "packet-dir") ?? throw new ArgumentException("polish-regression-supplement-spec-review-validate 需要 --packet-dir <review-packet-directory>。");
        var report = PolishRegressionSupplementSpecificationReviewValidator.Validate(specs, coverage, parentCases, parentManifest, packet);
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true }));
        return report.Valid ? 0 : 3;
    }

    private static int PolishRegressionSupplementSpecificationReviewImport(string[] args)
    {
        var specs = GetOption(args, "specs-dir") ?? throw new ArgumentException("polish-regression-supplement-spec-review-import 需要 --specs-dir <specification-directory>。");
        var coverage = GetOption(args, "coverage-dir") ?? throw new ArgumentException("polish-regression-supplement-spec-review-import 需要 --coverage-dir <coverage-directory>。");
        var parentCases = GetOption(args, "parent-cases") ?? throw new ArgumentException("polish-regression-supplement-spec-review-import 需要 --parent-cases <parent-cases.jsonl>。");
        var parentManifest = GetOption(args, "parent-manifest") ?? throw new ArgumentException("polish-regression-supplement-spec-review-import 需要 --parent-manifest <parent-manifest.json>。");
        var packet = GetOption(args, "packet-dir") ?? throw new ArgumentException("polish-regression-supplement-spec-review-import 需要 --packet-dir <review-packet-directory>。");
        var output = GetOption(args, "output") ?? throw new ArgumentException("polish-regression-supplement-spec-review-import 需要 --output <new-reviewed-output-directory>。");
        var report = PolishRegressionSupplementSpecificationReviewImporter.Import(specs, coverage, parentCases, parentManifest, packet, output);
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true }));
        return report.Valid ? 0 : 3;
    }

    private static int PolishRegressionNearDuplicateValidate(string[] args)
    {
        var reportDirectory = GetOption(args, "report-dir") ?? throw new ArgumentException("polish-regression-neardup-validate 需要 --report-dir <candidate-report-directory>。");
        var decisions = GetOption(args, "decisions") ?? throw new ArgumentException("polish-regression-neardup-validate 需要 --decisions <human-decisions.jsonl>。");
        var report = PolishRegressionNearDuplicateAdjudicationValidator.Validate(reportDirectory, decisions);
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true }));
        return report.Valid ? 0 : 3;
    }

    private static int PolishRegressionNearDuplicateImportWorkbook(string[] args)
    {
        var reportDirectory = GetOption(args, "report-dir") ?? throw new ArgumentException("polish-regression-neardup-import-workbook 需要 --report-dir <candidate-report-directory>。");
        var cases = GetOption(args, "cases") ?? throw new ArgumentException("polish-regression-neardup-import-workbook 需要 --cases <frozen-cases.jsonl>。");
        var workbook = GetOption(args, "workbook") ?? throw new ArgumentException("polish-regression-neardup-import-workbook 需要 --workbook <human-review.xlsx>。");
        var output = GetOption(args, "output") ?? throw new ArgumentException("polish-regression-neardup-import-workbook 需要 --output <new-human-decisions.jsonl>。");
        var result = PolishRegressionNearDuplicateWorkbookImporter.Import(reportDirectory, cases, workbook, output);
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true }));
        return 0;
    }

    private static int PolishRegressionFinalize(string[] args)
    {
        var input = GetOption(args, "input") ?? throw new ArgumentException("polish-regression-finalize 需要 --input <legacy-canonical.jsonl>。");
        var dataset = GetOption(args, "dataset-dir") ?? throw new ArgumentException("polish-regression-finalize 需要 --dataset-dir <dataset-directory>。");
        PolishRegressionManifestWriter.Finalize(input, dataset);
        Console.WriteLine(JsonSerializer.Serialize(new { dataset = Path.GetFullPath(dataset), status = "frozen", phase_0_gate_contribution = 0 }));
        return 0;
    }

    private static int PolishRegressionValidate(string[] args)
    {
        var dataset = GetOption(args, "dataset-dir") ?? throw new ArgumentException("polish-regression-validate 需要 --dataset-dir <dataset-directory>。");
        var report = PolishRegressionIntegrityValidator.Validate(dataset);
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true }));
        return report.Valid ? 0 : 3;
    }

    private static int BlindEvaluate(string[] args)
    {
        var goldPath = GetOption(args, "gold") ?? throw new ArgumentException("blind-evaluate 需要 --gold <blind-eval.jsonl>。", "gold");
        var predictionPaths = GetOptions(args, "predictions").ToArray();
        if (predictionPaths.Length == 0) throw new ArgumentException("blind-evaluate 需要至少一个 --predictions <predictions.jsonl>。", "predictions");
        var trainingPaths = GetOptions(args, "training").ToArray();
        if (trainingPaths.Length == 0) throw new ArgumentException("blind-evaluate 需要至少一个 --training <canonical.jsonl>。", "training");
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };
        var gold = ReadBlindGold(goldPath, jsonOptions);
        var predictions = predictionPaths.SelectMany(path => ReadBlindPredictions(path, jsonOptions)).ToArray();
        var comparisonPath = GetOption(args, "comparisons");
        var comparisons = string.IsNullOrWhiteSpace(comparisonPath)
            ? Array.Empty<BlindEvaluationPairwiseComparison>()
            : ReadBlindComparisons(comparisonPath, jsonOptions);
        var knownTrainingRecords = trainingPaths.SelectMany(trainingPath => File.ReadLines(trainingPath)
            .Select((line, index) => ReadKnownRecord(line, trainingPath, index + 1)))
            .ToArray();
        var admissionIssues = BlindEvaluationAuditor.Validate(gold, knownTrainingRecords: knownTrainingRecords).ToArray();
        var evaluationSplit = GetOption(args, "split") ?? "all";
        if (evaluationSplit is not ("all" or "development" or "frozen_test"))
            throw new ArgumentException("blind-evaluate --split 只能是 all、development 或 frozen_test。", "split");
        var scoringGold = evaluationSplit == "all" ? gold : gold.Where(record => record.Split == evaluationSplit).ToArray();
        if (scoringGold.Length == 0) throw new InvalidOperationException($"gold 中没有 split={evaluationSplit} 的样本。");
        var scoringIds = scoringGold.Select(record => record.Id).ToHashSet(StringComparer.Ordinal);
        if (evaluationSplit != "all" && comparisons.Any(item => !scoringIds.Contains(item.Id)))
            throw new InvalidOperationException($"comparisons 含有 evaluation_split={evaluationSplit} 以外的 gold 样本；请为 development 与 frozen_test 分别生成和合并评审记录。");
        var scoringPredictions = evaluationSplit == "all" ? predictions : predictions.Where(item => scoringIds.Contains(item.Id)).ToArray();
        var scoringComparisons = evaluationSplit == "all" ? comparisons : comparisons.Where(item => scoringIds.Contains(item.Id)).ToArray();
        var scoring = BlindEvaluationScorer.Evaluate(scoringGold, scoringPredictions, scoringComparisons);
        var result = new
        {
            evaluation_split = evaluationSplit,
            dataset_valid = admissionIssues.Length == 0,
            admission_protocol_id = BlindEvaluationAdmissionProtocol.ProtocolId,
            admission_protocol_version = BlindEvaluationAdmissionProtocol.Version,
            admission_policy_sha256 = BlindEvaluationAdmissionProtocol.PolicySha256,
            dataset_issue_count = admissionIssues.Length,
            dataset_issues = admissionIssues,
            scoring
        };
        var json = JsonSerializer.Serialize(result, jsonOptions);
        var outputPath = GetOption(args, "output");
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            ImmutableArtifactWriter.WriteNew(outputPath, json + Environment.NewLine);
        }
        Console.WriteLine(json);
        if (admissionIssues.Length > 0 || scoring.Issues.Count > 0) return 3;
        return scoring.Passed ? 0 : 4;
    }

    private static int BlindManifestValidate(string[] args)
    {
        var manifestPath = GetOption(args, "manifest") ?? throw new ArgumentException("blind-manifest-validate 需要 --manifest <run-manifest.json>。", "manifest");
        var datasetPath = GetOption(args, "gold") ?? throw new ArgumentException("blind-manifest-validate 需要 --gold <blind-eval.jsonl>。", "gold");
        var predictionsPath = GetOption(args, "predictions") ?? throw new ArgumentException("blind-manifest-validate 需要 --predictions <predictions.jsonl>。", "predictions");
        var reportPath = GetOption(args, "report") ?? throw new ArgumentException("blind-manifest-validate 需要 --report <evaluation-report.json>。", "report");
        var comparisonsPath = GetOption(args, "comparisons");
        var report = BlindEvaluationManifestValidator.Validate(File.ReadAllText(manifestPath), datasetPath, predictionsPath, reportPath, comparisonsPath);
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        return report.Valid ? 0 : 3;
    }

    private static int BlindPairwisePackage(string[] args)
    {
        var goldPath = GetOption(args, "gold") ?? throw new ArgumentException("blind-pairwise-package 需要 --gold <blind-eval.jsonl>。");
        var predictionPaths = GetOptions(args, "predictions").ToArray();
        if (predictionPaths.Length < 2) throw new ArgumentException("blind-pairwise-package 至少需要两个 --predictions <candidate-predictions.jsonl>。");
        var outputDirectory = GetOption(args, "output") ?? throw new ArgumentException("blind-pairwise-package 需要 --output <new-reviewer-directory>。");
        var sealedMapPath = GetOption(args, "sealed-manifest") ?? throw new ArgumentException("blind-pairwise-package 需要 --sealed-manifest <outside-reviewer-directory.json>。");
        var trainingPaths = GetOptions(args, "training").ToArray();
        if (trainingPaths.Length == 0) throw new ArgumentException("blind-pairwise-package 需要至少一个 --training <canonical.jsonl> 以复核数据授权与泄漏门禁。");
        var frozenTestConfirmed = args.Contains("--confirm-frozen-test-locked", StringComparer.Ordinal);
        var split = GetOption(args, "split") ?? throw new ArgumentException("blind-pairwise-package 需要显式指定 --split development|frozen_test。");
        if (split is not ("development" or "frozen_test"))
            throw new ArgumentException("blind-pairwise-package --split 只能是 development 或 frozen_test。", "split");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var gold = ReadBlindGold(goldPath, options);
        var knownTrainingRecords = trainingPaths.SelectMany(trainingPath => File.ReadLines(trainingPath)
            .Select((line, index) => ReadKnownRecord(line, trainingPath, index + 1))).ToArray();
        var admissionIssues = BlindEvaluationAuditor.Validate(gold, knownTrainingRecords: knownTrainingRecords);
        if (admissionIssues.Count > 0)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { valid = false, issue_count = admissionIssues.Count, issues = admissionIssues }, options));
            return 3;
        }
        if (split == "frozen_test" && !frozenTestConfirmed)
            throw new InvalidOperationException("frozen_test pairwise 评审包必须确认候选、提示和采样参数均已锁定。");
        var predictions = predictionPaths.SelectMany(path => ReadBlindPredictions(path, options)).ToArray();
        var package = BlindEvaluationPairwisePackageBuilder.Build(gold, predictions, split);
        if (package.ReviewItems.Count == 0) throw new InvalidOperationException("没有至少两个成功候选输出的同案例，未创建评审包。");

        var outputFullPath = Path.GetFullPath(outputDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var sealedFullPath = Path.GetFullPath(sealedMapPath);
        if (Directory.Exists(outputFullPath) || File.Exists(outputFullPath))
            throw new IOException("评审包目录已存在；评审包不可覆盖，请指定新目录。");
        if (Directory.Exists(sealedFullPath) || File.Exists(sealedFullPath))
            throw new IOException("比较项封存映射已存在；封存映射不可覆盖，请指定新路径。");
        if (IsPathWithin(sealedFullPath, outputFullPath))
            throw new InvalidOperationException("比较项封存映射必须位于评审包目录之外。");

        var reviewerJsonl = JsonLines(package.ReviewItems, options);
        var answerTemplateJsonl = JsonLines(package.AnswerTemplates, options);
        var reviewerPath = Path.Combine(outputFullPath, "pairwise-review.jsonl");
        Directory.CreateDirectory(outputFullPath);
        ImmutableArtifactWriter.WriteNew(reviewerPath, reviewerJsonl);
        ImmutableArtifactWriter.WriteNew(Path.Combine(outputFullPath, "pairwise-answers.template.jsonl"), answerTemplateJsonl);
        ImmutableArtifactWriter.WriteNew(Path.Combine(outputFullPath, "pairwise-package-info.json"), JsonSerializer.Serialize(new
        {
            package_version = 2,
            split = package.Split,
            created_at_utc = DateTimeOffset.UtcNow,
            review_item_count = package.ReviewItems.Count,
            skipped_case_count = package.SkippedCases.Count,
            review_package_sha256 = HashBytes(System.Text.Encoding.UTF8.GetBytes(reviewerJsonl)),
            answers_template_sha256 = HashBytes(System.Text.Encoding.UTF8.GetBytes(answerTemplateJsonl))
        }, options) + Environment.NewLine);
        var sealedMap = new BlindPairwiseSealedMap(package.Split, HashBytes(System.Text.Encoding.UTF8.GetBytes(reviewerJsonl)), package.SealedMap);
        ImmutableArtifactWriter.WriteNew(sealedFullPath, JsonSerializer.Serialize(sealedMap, options) + Environment.NewLine);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            created = true,
            split = package.Split,
            review_item_count = package.ReviewItems.Count,
            skipped_case_count = package.SkippedCases.Count,
            reviewer_identity_in_package = false,
            candidate_identity_in_package = false
        }, options));
        return 0;
    }

    private static int BlindPairwiseMerge(string[] args)
    {
        var answersPath = GetOption(args, "answers") ?? throw new ArgumentException("blind-pairwise-merge 需要 --answers <completed-answers.jsonl>。");
        var sealedMapPath = GetOption(args, "sealed-manifest") ?? throw new ArgumentException("blind-pairwise-merge 需要 --sealed-manifest <outside-reviewer-directory.json>。");
        var reviewPackagePath = GetOption(args, "review-package") ?? throw new ArgumentException("blind-pairwise-merge 需要 --review-package <pairwise-review.jsonl>。");
        var outputPath = GetOption(args, "output") ?? throw new ArgumentException("blind-pairwise-merge 需要 --output <new-comparisons.jsonl>。");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var sealedMap = JsonSerializer.Deserialize<BlindPairwiseSealedMap>(File.ReadAllText(sealedMapPath), options)
            ?? throw new JsonException("比较项封存映射为空。");
        var reviewHash = HashBytes(File.ReadAllBytes(reviewPackagePath));
        if (!string.Equals(reviewHash, sealedMap.ReviewPackageSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("匿名评审包哈希与封存映射不一致。");
        var answers = File.ReadLines(answersPath).Select((line, index) =>
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            EnsureObjectProperties(root, answersPath, index + 1, ["id", "votes", "adjudication"], ["id", "votes", "adjudication"]);
            var votes = root.GetProperty("votes");
            if (votes.ValueKind != JsonValueKind.Array) throw new JsonException($"{answersPath} 第 {index + 1} 行 votes 必须是数组。");
            foreach (var vote in votes.EnumerateArray())
                EnsureObjectProperties(vote, answersPath, index + 1, ["reviewer_id", "choice"], ["reviewer_id", "choice"]);
            var adjudication = root.GetProperty("adjudication");
            if (adjudication.ValueKind != JsonValueKind.Null)
                EnsureObjectProperties(adjudication, answersPath, index + 1, ["adjudicator_id", "choice"], ["adjudicator_id", "choice"]);
            return JsonSerializer.Deserialize<BlindPairwiseAnswerTemplate>(line, options)
                ?? throw new JsonException($"评审答案第 {index + 1} 行为空。");
        }).ToArray();
        var comparisons = BlindEvaluationPairwisePackageBuilder.MergeAnswers(answers, sealedMap.Items);
        var jsonl = JsonLines(comparisons, options);
        var reviewerDirectory = Path.GetFullPath(Path.GetDirectoryName(Path.GetFullPath(reviewPackagePath))!);
        if (IsPathWithin(Path.GetFullPath(outputPath), reviewerDirectory))
            throw new InvalidOperationException("comparison 含有解盲后的候选 alias，必须保存在匿名评审包目录之外。");
        ImmutableArtifactWriter.WriteNew(outputPath, jsonl);
        Console.WriteLine(JsonSerializer.Serialize(new { created = true, comparison_count = comparisons.Count }, options));
        return 0;
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

    private static IEnumerable<string> GetOptions(string[] args, string name)
    {
        var prefix = "--" + name + "=";
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                yield return args[index][prefix.Length..];
            else if (string.Equals(args[index], "--" + name, StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
                yield return args[++index];
        }
    }

    private static string JsonLines<T>(IEnumerable<T> values, JsonSerializerOptions options) =>
        string.Join(Environment.NewLine, values.Select(value => JsonSerializer.Serialize(value, options))) + Environment.NewLine;

    private static string HashBytes(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsPathWithin(string targetPath, string directoryPath)
    {
        var target = Path.GetFullPath(targetPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var directory = Path.GetFullPath(directoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(target, directory, StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static BlindEvaluationKnownRecord ReadKnownRecord(string line, string path, int lineNumber)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException($"训练数据 {path} 第 {lineNumber} 行不是对象。");
        var split = ReadString(root, "split");
        var input = ReadString(root, "input");
        var family = ReadString(root, "semantic_family_id");
        if (string.IsNullOrWhiteSpace(family) && root.TryGetProperty("provenance", out var provenance) && provenance.ValueKind == JsonValueKind.Object)
        {
            family = ReadString(provenance, "template_family");
            if (string.IsNullOrWhiteSpace(family)) family = ReadString(provenance, "source_template_family");
        }
        if (string.IsNullOrWhiteSpace(family)) family = ReadString(root, "generalization_family");
        return new BlindEvaluationKnownRecord(split, input, family);
    }

    private static BlindEvaluationRecord[] ReadBlindGold(string path, JsonSerializerOptions options) =>
        File.ReadLines(path).Select((line, index) =>
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            EnsureObjectProperties(root, path, index + 1,
                ["id", "task", "semantic_family_id", "source", "input", "input_style", "constraints", "risk_level", "split", "expected_decision", "annotations", "human_review"],
                ["id", "task", "semantic_family_id", "source", "input", "input_style", "context", "constraints", "risk_level", "split", "expected_decision", "reference_output", "annotations", "human_review", "conversation_id", "turns"]);
            EnsureObjectProperties(root.GetProperty("source"), path, index + 1,
                ["source_kind", "license_or_consent_ref", "deidentification"],
                ["source_kind", "license_or_consent_ref", "deidentification", "source_record_hash"]);
            EnsureObjectProperties(root.GetProperty("annotations"), path, index + 1,
                ["facts_and_constraints", "target_tone", "format_requirements", "clarification_required", "high_risk_key_facts"],
                ["facts_and_constraints", "target_tone", "format_requirements", "clarification_required", "high_risk_key_facts"]);
            EnsureObjectProperties(root.GetProperty("human_review"), path, index + 1,
                ["reviewer_ids", "review_status", "independent_annotations"],
                ["reviewer_ids", "review_status", "adjudicator_id", "independent_annotations", "adjudication"]);
            var humanReview = root.GetProperty("human_review");
            var independentAnnotations = humanReview.GetProperty("independent_annotations");
            if (independentAnnotations.ValueKind != JsonValueKind.Array) throw new JsonException($"{path} 第 {index + 1} 行 independent_annotations 必须是数组。");
            foreach (var vote in independentAnnotations.EnumerateArray()) EnsureBlindAnnotationVote(vote, path, index + 1);
            if (humanReview.TryGetProperty("adjudication", out var adjudication) && adjudication.ValueKind is not JsonValueKind.Null)
                EnsureBlindAnnotationVote(adjudication, path, index + 1);
            return JsonSerializer.Deserialize<BlindEvaluationRecord>(line, options)
                ?? throw new JsonException($"Gold 第 {index + 1} 行记录为空。");
        }).ToArray();

    private static void EnsureBlindAnnotationVote(JsonElement vote, string path, int lineNumber)
    {
        EnsureObjectProperties(vote, path, lineNumber, ["reviewer_id", "labels"], ["reviewer_id", "labels"]);
        var labels = vote.GetProperty("labels");
        EnsureObjectProperties(labels, path, lineNumber,
            ["task", "input_style", "constraints", "risk_level", "expected_decision", "reference_output", "annotations"],
            ["task", "input_style", "constraints", "risk_level", "expected_decision", "reference_output", "annotations"]);
        EnsureObjectProperties(labels.GetProperty("annotations"), path, lineNumber,
            ["facts_and_constraints", "target_tone", "format_requirements", "clarification_required", "high_risk_key_facts"],
            ["facts_and_constraints", "target_tone", "format_requirements", "clarification_required", "high_risk_key_facts"]);
    }

    private static BlindEvaluationPrediction[] ReadBlindPredictions(string path, JsonSerializerOptions options) =>
        File.ReadLines(path).Select((line, index) =>
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            EnsureObjectProperties(root, path, index + 1,
                ["id", "candidate_id", "output", "schema_valid", "reviews", "status"],
                ["id", "candidate_id", "output", "schema_valid", "reviews", "adjudication", "telemetry", "status"]);
            var reviews = root.GetProperty("reviews");
            if (reviews.ValueKind != JsonValueKind.Array) throw new JsonException($"{path} 第 {index + 1} 行 reviews 必须是数组。");
            foreach (var review in reviews.EnumerateArray())
                EnsureObjectProperties(review, path, index + 1,
                    ["reviewer_id", "fact_constraint_retained", "directly_usable", "tone_matched", "clarification_decision_correct", "high_risk_key_fact_reversed", "safety_pass", "fidelity_score", "task_completion_score", "naturalness_score", "direct_usability_score", "safety_score"],
                    ["reviewer_id", "fact_constraint_retained", "directly_usable", "tone_matched", "clarification_decision_correct", "high_risk_key_fact_reversed", "safety_pass", "fidelity_score", "task_completion_score", "naturalness_score", "direct_usability_score", "safety_score"]);
            if (root.TryGetProperty("adjudication", out var adjudication) && adjudication.ValueKind != JsonValueKind.Null)
                EnsureObjectProperties(adjudication, path, index + 1,
                    ["adjudicator_id", "fact_constraint_retained", "directly_usable", "tone_matched", "clarification_decision_correct", "high_risk_key_fact_reversed", "safety_pass", "fidelity_score", "task_completion_score", "naturalness_score", "direct_usability_score", "safety_score"],
                    ["adjudicator_id", "fact_constraint_retained", "directly_usable", "tone_matched", "clarification_decision_correct", "high_risk_key_fact_reversed", "safety_pass", "fidelity_score", "task_completion_score", "naturalness_score", "direct_usability_score", "safety_score"]);
            if (root.TryGetProperty("telemetry", out var telemetry) && telemetry.ValueKind != JsonValueKind.Null)
                EnsureObjectProperties(telemetry, path, index + 1, [],
                    ["latency_milliseconds", "api_input_tokens", "api_output_tokens", "api_cache_read_input_tokens", "api_cache_creation_input_tokens", "local_tokens_per_second", "local_peak_memory_bytes", "local_model_load_milliseconds", "error_category"]);
            return JsonSerializer.Deserialize<BlindEvaluationPrediction>(line, options)
                ?? throw new JsonException($"预测文件第 {index + 1} 行记录为空。");
        }).ToArray();

    private static BlindEvaluationPairwiseComparison[] ReadBlindComparisons(string path, JsonSerializerOptions options) =>
        File.ReadLines(path).Select((line, index) =>
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            EnsureObjectProperties(root, path, index + 1,
                ["id", "left_candidate_id", "right_candidate_id", "votes"],
                ["id", "left_candidate_id", "right_candidate_id", "votes", "adjudication"]);
            var votes = root.GetProperty("votes");
            if (votes.ValueKind != JsonValueKind.Array) throw new JsonException($"{path} 第 {index + 1} 行 votes 必须是数组。");
            foreach (var vote in votes.EnumerateArray())
                EnsureObjectProperties(vote, path, index + 1, ["reviewer_id", "choice"], ["reviewer_id", "choice"]);
            if (root.TryGetProperty("adjudication", out var adjudication) && adjudication.ValueKind != JsonValueKind.Null)
                EnsureObjectProperties(adjudication, path, index + 1, ["adjudicator_id", "choice"], ["adjudicator_id", "choice"]);
            return JsonSerializer.Deserialize<BlindEvaluationPairwiseComparison>(line, options)
                ?? throw new JsonException($"成对比较文件第 {index + 1} 行记录为空。");
        }).ToArray();

    private static void EnsureObjectProperties(JsonElement element, string path, int lineNumber, string[] required, string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new JsonException($"{path} 第 {lineNumber} 行对象结构无效。");
        foreach (var property in required)
            if (!element.TryGetProperty(property, out _)) throw new JsonException($"{path} 第 {lineNumber} 行缺少字段 {property}。");
        var allowedSet = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!allowedSet.Contains(property.Name)) throw new JsonException($"{path} 第 {lineNumber} 行包含未知字段 {property.Name}。");
    }

    private static string ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static int ParseInt(string? value, int fallback, string name) =>
        value is null ? fallback : int.TryParse(value, out var parsed) && parsed >= 0 ? parsed : throw new ArgumentException($"--{name} 必须是非负整数。", name);

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"错误：{message}");
        PrintHelp();
        return 2;
    }

    private static void PrintHelp() => Console.WriteLine(
        "用法：dotnet run --project DatasetBuilder -- generate [--output PATH] [--seed N] [--train N] [--dev N] [--test N]\n" +
        "      dotnet run --project DatasetBuilder -- architecture-dataset [--output PATH] [--seed N] [--train N] [--dev N] [--test N]\n" +
        "      dotnet run --project DatasetBuilder -- architecture-validate --input architecture_*.jsonl\n" +
        "      dotnet run --project DatasetBuilder -- architecture-batch --input architecture_dev.jsonl --candidates candidate-configs.json --output requests.jsonl\n" +
        "      dotnet run --project DatasetBuilder -- architecture-evaluate --gold architecture_test.jsonl --predictions predictions.jsonl [--output report.json]\n" +
        "      dotnet run --project DatasetBuilder -- architecture-compare --gold architecture_test.jsonl --predictions predictions.jsonl --left candidate-a --right candidate-b [--output report.json]\n" +
        "      dotnet run --project DatasetBuilder -- validate --input canonical.jsonl\n" +
        "      dotnet run --project DatasetBuilder -- polish-agent-validate --input polish-agent-v2/canonical.jsonl --sft polish-agent-v2/sft.jsonl --manifest polish-agent-v2/manifest.json --source v1/canonical.jsonl\n" +
        "      dotnet run --project DatasetBuilder -- polish-regression-report --input polish-agent-v2/canonical.jsonl --sft polish-agent-v2/sft.jsonl --manifest polish-agent-v2/manifest.json --source v1/canonical.jsonl --out polish-regression-v1/baseline/audit.json\n" +
        "      dotnet run --project DatasetBuilder -- polish-regression-backbone-report --input polish-agent-v2/canonical.jsonl --out test-artifacts/backbone-report.json\n" +
        "      dotnet run --project DatasetBuilder -- polish-regression-hierarchy-report --input polish-regression-v1/cases.jsonl --out test-artifacts/hierarchy-report.json\n" +
        "      dotnet run --project DatasetBuilder -- polish-regression-label-draft --cases polish-regression-v1/cases.jsonl --source polish-agent-v2/canonical.jsonl --hierarchy docs/polish-regression-hierarchy-report.json --parent-manifest polish-regression-v1/manifest.json --prompt polish-regression-v2-draft/label-prompt-v1.md --output polish-regression-v2-draft --draft-by 'AI assistant' --model 'GPT-6 (Codex)' --drafted-at-utc 2026-10-05T03:21:15Z\n" +
        "      dotnet run --project DatasetBuilder -- polish-regression-review-packet --cases polish-regression-v1/cases.jsonl --source polish-agent-v2/canonical.jsonl --labels polish-regression-v2-draft/labels.jsonl --parent-manifest polish-regression-v1/manifest.json --draft-manifest polish-regression-v2-draft/manifest.json --hierarchy docs/polish-regression-hierarchy-candidates-2026-10-05-v5.json --output polish-regression-review-packet-v2\n" +
        "      dotnet run --project DatasetBuilder -- polish-regression-review-validate --packet-dir polish-regression-review-packet-v2 --first-pass source-first-pass.completed.jsonl --decisions human-decisions.completed.jsonl --labels polish-regression-v2-draft/labels.jsonl --draft-manifest polish-regression-v2-draft/manifest.json\n" +
        "      dotnet run --project DatasetBuilder -- polish-regression-supplement-review-validate --cases datasets/polish-regression-ai-supplement-draft-v1/cases.jsonl --review-csv outputs/polish-regression-ai-supplement-review-2026-10-05/human-review-template.csv --manifest outputs/polish-regression-ai-supplement-review-2026-10-05/manifest.json\n" +
        "      dotnet run --project DatasetBuilder -- polish-regression-supplement-review-import --cases datasets/polish-regression-ai-supplement-draft-v1/cases.jsonl --review-csv outputs/polish-regression-ai-supplement-review-2026-10-05/human-review-template.csv --manifest outputs/polish-regression-ai-supplement-review-2026-10-05/manifest.json --output outputs/polish-regression-ai-supplement-review-results-2026-10-05/human-decisions.jsonl\n" +
        "      dotnet run --project DatasetBuilder -- local-model-candidate-review-validate --candidate-packet <candidate-review.jsonl> --review-csv <completed-review.csv> --metadata <review-metadata.json>\n" +
        "      dotnet run --project DatasetBuilder -- local-model-candidate-review-import --candidate-packet <candidate-review.jsonl> --review-csv <completed-review.csv> --metadata <review-metadata.json> --output <new-human-review.jsonl>\n" +
        "      dotnet run --project DatasetBuilder -- polish-regression-coverage-gap-report --cases polish-regression-v1/cases.jsonl --variants polish-regression-v1/variants.jsonl --source polish-agent-v2/canonical.jsonl --labels polish-regression-v2-draft/labels.jsonl --parent-manifest polish-regression-v1/manifest.json --draft-manifest polish-regression-v2-draft/manifest.json --hierarchy docs/polish-regression-hierarchy-candidates-2026-10-05-v5.json --output polish-regression-w4-audit-v3\n" +
        "      dotnet run --project DatasetBuilder -- polish-regression-neardup-report --cases polish-regression-v1/cases.jsonl --parent-manifest polish-regression-v1/manifest.json --rules docs/polish-regression-near-duplicate-rules-v1.json --output datasets/polish-regression-w2-neardup-candidates-v3\n" +
        "      dotnet run --project DatasetBuilder -- polish-regression-supplement-neardup-report --base-cases datasets/polish-regression-v1/cases.jsonl --base-manifest datasets/polish-regression-v1/manifest.json --supplement-cases datasets/polish-regression-ai-supplement-draft-v1/cases.jsonl --supplement-manifest datasets/polish-regression-ai-supplement-draft-v1/manifest.json --rules docs/polish-regression-near-duplicate-rules-v1.json --output datasets/polish-regression-ai-supplement-neardup-v1\n" +
        "      dotnet run --project DatasetBuilder -- polish-regression-supplement-coverage-report --cases datasets/polish-regression-ai-supplement-draft-v1/cases.jsonl --manifest datasets/polish-regression-ai-supplement-draft-v1/manifest.json --behaviors clarification_positive_examples,needs_clarification_outputs,format_and_schema_requirements,high_risk_fact_reversal,tone_and_scenario_diversity,non_workplace_scenarios,multi_turn_revision_and_user_negation,negation_quantity_time_condition_boundaries,prompt_injection_resistance,cross_language_mixing --minimum-per-behavior 5 --output datasets/polish-regression-ai-supplement-coverage-v1\n" +
        "      dotnet run --project DatasetBuilder -- polish-regression-supplement-spec-validate --specs-dir datasets/polish-regression-ai-supplement-specs-v1 --coverage-dir datasets/polish-regression-ai-supplement-coverage-v1 --parent-cases datasets/polish-regression-ai-supplement-draft-v1/cases.jsonl --parent-manifest datasets/polish-regression-ai-supplement-draft-v1/manifest.json\n" +
        "      dotnet run --project DatasetBuilder -- polish-regression-supplement-lineage-audit --cases datasets/polish-regression-ai-supplement-draft-v1/cases.jsonl --manifest datasets/polish-regression-ai-supplement-draft-v1/manifest.json\n" +
        "      dotnet run --project DatasetBuilder -- polish-regression-supplement-spec-review-packet --specs-dir datasets/polish-regression-ai-supplement-specs-v1 --coverage-dir datasets/polish-regression-ai-supplement-coverage-v1 --parent-cases datasets/polish-regression-ai-supplement-draft-v1/cases.jsonl --parent-manifest datasets/polish-regression-ai-supplement-draft-v1/manifest.json --output outputs/polish-regression-ai-supplement-spec-review-2026-10-05-v2\n" +
        "      dotnet run --project DatasetBuilder -- polish-regression-supplement-spec-review-validate --specs-dir datasets/polish-regression-ai-supplement-specs-v1 --coverage-dir datasets/polish-regression-ai-supplement-coverage-v1 --parent-cases datasets/polish-regression-ai-supplement-draft-v1/cases.jsonl --parent-manifest datasets/polish-regression-ai-supplement-draft-v1/manifest.json --packet-dir outputs/polish-regression-ai-supplement-spec-review-2026-10-05-v2\n" +
        "      dotnet run --project DatasetBuilder -- polish-regression-supplement-spec-review-import --specs-dir datasets/polish-regression-ai-supplement-specs-v1 --coverage-dir datasets/polish-regression-ai-supplement-coverage-v1 --parent-cases datasets/polish-regression-ai-supplement-draft-v1/cases.jsonl --parent-manifest datasets/polish-regression-ai-supplement-draft-v1/manifest.json --packet-dir outputs/polish-regression-ai-supplement-spec-review-2026-10-05-v2 --output outputs/polish-regression-ai-supplement-spec-reviewed-v1\n" +
        "      dotnet run --project DatasetBuilder -- polish-regression-neardup-import-workbook --report-dir datasets/polish-regression-w2-neardup-candidates-v3 --cases polish-regression-v1/cases.jsonl --workbook outputs/polish-regression-neardup-review-2026-10-05/near-duplicate-human-review.xlsx --output human-decisions.completed.jsonl\n" +
        "      dotnet run --project DatasetBuilder -- polish-regression-neardup-validate --report-dir datasets/polish-regression-w2-neardup-candidates-v3 --decisions human-decisions.completed.jsonl\n" +
        "      dotnet run --project DatasetBuilder -- polish-regression-build --input polish-agent-v2/canonical.jsonl --output polish-regression-v1/build\n" +
        "      dotnet run --project DatasetBuilder -- polish-regression-finalize --input polish-agent-v2/canonical.jsonl --dataset-dir polish-regression-v1\n" +
        "      dotnet run --project DatasetBuilder -- polish-regression-validate --dataset-dir polish-regression-v1\n" +
        "      dotnet run --project DatasetBuilder -- blind-validate --input blind-eval.jsonl --training canonical.jsonl\n" +
        "      dotnet run --project DatasetBuilder -- blind-evidence-validate --input blind-eval.jsonl --manifest evidence-manifest.json --training canonical.jsonl [--output evidence-validation.json]\n" +
        "      dotnet run --project DatasetBuilder -- source-register-validate --register source-register.json --dataset blind-eval.jsonl [--output source-validation.json]\n" +
        "      dotnet run --project DatasetBuilder -- blind-evaluate --gold blind-eval.jsonl --split development|frozen_test --predictions predictions.jsonl --training canonical.jsonl [--comparisons comparisons.jsonl]\n" +
        "      dotnet run --project DatasetBuilder -- blind-manifest-validate --manifest run-manifest.json --gold blind-eval.jsonl --predictions predictions.jsonl --report report.json [--comparisons comparisons.jsonl]\n" +
        "      dotnet run --project DatasetBuilder -- leakage-check --input canonical.jsonl\n" +
        "      dotnet run --project DatasetBuilder -- report --input canonical.jsonl [--output report.json]\n" +
        "      dotnet run --project DatasetBuilder -- evaluate --gold canonical.jsonl --predictions predictions.jsonl [--output report.json]\n" +
        "      dotnet run --project DatasetBuilder -- mine-failures --gold canonical.jsonl --predictions predictions.jsonl --output dpo-failures.jsonl");

    private static void PrintBlindPairwiseHelp() => Console.WriteLine(
        "      dotnet run --project DatasetBuilder -- blind-pairwise-package --gold blind-eval.jsonl --split frozen_test --training canonical.jsonl --predictions candidate-a.jsonl --predictions candidate-b.jsonl --output <reviewer-dir> --sealed-manifest <outside-reviewer-dir.json> --confirm-frozen-test-locked\n" +
        "      dotnet run --project DatasetBuilder -- blind-pairwise-merge --answers completed-answers.jsonl --review-package pairwise-review.jsonl --sealed-manifest <outside-reviewer-dir.json> --output comparisons.jsonl\n" +
        "      dotnet run --project DatasetBuilder -- blind-evaluate --gold blind-eval.jsonl --split frozen_test --predictions candidate-a-reviewed.jsonl --predictions candidate-b-reviewed.jsonl --training canonical.jsonl --comparisons comparisons.jsonl");
}
