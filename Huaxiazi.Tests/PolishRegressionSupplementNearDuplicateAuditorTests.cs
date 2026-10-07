using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Text.Json;
using Huaxiazi.DatasetBuilder;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class PolishRegressionSupplementNearDuplicateAuditorTests
{
    [Fact]
    public void Build_ComparesEveryPairInvolvingSupplementAndEmitsReviewOnlyArtifacts()
    {
        using var fixture = new Fixture();

        var result = PolishRegressionSupplementNearDuplicateAuditor.Build(fixture.BaseCasesPath, fixture.BaseManifestPath,
            fixture.SupplementCasesPath, fixture.SupplementManifestPath, fixture.RulesPath, fixture.OutputA);

        Assert.Equal(4, result.FamilyCount);
        Assert.Equal(5, result.PairCount);
        Assert.Equal(1, result.CandidatePairCount);
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.OutputA, "report.json")));
        Assert.Equal(0, report.RootElement.GetProperty("phase_0_gate_contribution").GetInt32());
        Assert.True(report.RootElement.GetProperty("not_admissible_as_blind_eval").GetBoolean());
        Assert.False(report.RootElement.GetProperty("auto_merge_performed").GetBoolean());
        Assert.Equal(2, report.RootElement.GetProperty("source_dataset_count").GetInt32());
        using var candidate = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.OutputA, "candidate-pairs.jsonl")));
        Assert.Equal("pending_human_adjudication", candidate.RootElement.GetProperty("adjudication_status").GetString());
        Assert.Contains(new[] { candidate.RootElement.GetProperty("left_case_id").GetString(), candidate.RootElement.GetProperty("right_case_id").GetString() },
            id => id is "draft-a" or "draft-b");
    }

    [Fact]
    public void Build_RejectsSupplementCasesNotBoundToItsManifestWithoutCreatingOutput()
    {
        using var fixture = new Fixture();
        File.AppendAllText(fixture.SupplementCasesPath,
            "{\"id\":\"draft-c\",\"family_id\":\"draft-family-c\",\"split\":\"development\",\"input\":\"新的样本输入内容。\"}" + Environment.NewLine,
            new UTF8Encoding(false));

        Assert.Throws<InvalidDataException>(() => PolishRegressionSupplementNearDuplicateAuditor.Build(fixture.BaseCasesPath,
            fixture.BaseManifestPath, fixture.SupplementCasesPath, fixture.SupplementManifestPath, fixture.RulesPath, fixture.OutputA));
        Assert.False(Directory.Exists(fixture.OutputA));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "polish-regression-supplement-neardup-" + Guid.NewGuid().ToString("N"));
        public string BaseCasesPath => Path.Combine(_root, "base-cases.jsonl");
        public string BaseManifestPath => Path.Combine(_root, "base-manifest.json");
        public string SupplementCasesPath => Path.Combine(_root, "supplement-cases.jsonl");
        public string SupplementManifestPath => Path.Combine(_root, "supplement-manifest.json");
        public string RulesPath => Path.Combine(_root, "rules.json");
        public string OutputA => Path.Combine(_root, "output-a");

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
            File.WriteAllLines(BaseCasesPath,
            [
                JsonSerializer.Serialize(new { id = "base-a", family_id = "base-family-a", split = "development", input = "今天讨论项目进度并确认下周安排。" }, options),
                JsonSerializer.Serialize(new { id = "base-b", family_id = "base-family-b", split = "regression", input = "苹果在山上长大，小猫喜欢睡觉。" }, options)
            ], new UTF8Encoding(false));
            var baseHash = Hash(BaseCasesPath);
            File.WriteAllText(BaseManifestPath,
                "{\"dataset_version\":\"hxz-polish-regression-v1\",\"status\":\"frozen\",\"phase_0_gate_contribution\":0,\"not_admissible_as_blind_eval\":true,\"counts\":{\"families\":2},\"files\":{\"cases.jsonl\":\"" + baseHash + "\"}}" + Environment.NewLine,
                new UTF8Encoding(false));
            File.WriteAllLines(SupplementCasesPath,
            [
                JsonSerializer.Serialize(new { id = "draft-a", family_id = "draft-family-a", split = "development", input = "今天讨论项目进度并确认下周安排！" }, options),
                JsonSerializer.Serialize(new { id = "draft-b", family_id = "draft-family-b", split = "regression", input = "供应商将在周四送达两箱材料，请门卫签收。" }, options)
            ], new UTF8Encoding(false));
            var supplementHash = Hash(SupplementCasesPath);
            var baseManifestHash = Hash(BaseManifestPath);
            File.WriteAllText(SupplementManifestPath,
                "{\"dataset_version\":\"hxz-polish-regression-ai-supplement-draft-v1\",\"status\":\"draft\",\"purpose\":\"internal_regression_only\",\"parent_manifest_sha256\":\"" + baseManifestHash + "\",\"phase_0_gate_contribution\":0,\"not_admissible_as_blind_eval\":true,\"counts\":{\"families\":2},\"files\":{\"cases.jsonl\":\"" + supplementHash + "\"}}" + Environment.NewLine,
                new UTF8Encoding(false));
            File.WriteAllText(RulesPath,
                "{\"schema_version\":\"1.0\",\"ruleset_id\":\"test-char-trigram-v1\",\"normalization\":\"unicode-formkc-lowercase-alphanumeric-codepoint-trigrams-v1\",\"shingle_size\":3,\"candidate_threshold\":0.8,\"sensitivity_thresholds\":[0.7,0.8,0.9]}" + Environment.NewLine,
                new UTF8Encoding(false));
        }

        private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
    }
}
