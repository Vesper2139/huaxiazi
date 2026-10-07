using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Huaxiazi.DatasetBuilder;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class PolishAgentDatasetAuditorTests
{
    [Fact]
    public void Validate_AuditsSourceHashFamilyIsolationAndSftAlignment()
    {
        using var fixture = new Fixture();

        var report = PolishAgentDatasetAuditor.Validate(fixture.CanonicalPath, fixture.SftPath, fixture.ManifestPath, fixture.SourcePath);

        Assert.True(report.Valid, string.Join("; ", report.Issues.Select(issue => issue.Message)));
        Assert.Equal(3, report.SampleCount);
        Assert.Equal(1, report.TrainCount);
        Assert.Equal(1, report.DevCount);
        Assert.Equal(1, report.TestCount);
        Assert.Equal(3, report.TemplateFamilyCount);
        Assert.Equal(3, report.SourceRecordMatchCount);
        Assert.Equal(3, report.SingleReviewerSyntheticRecordCount);
    }

    [Fact]
    public void Validate_RejectsManifestSourceHashMismatch()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.ManifestPath, File.ReadAllText(fixture.ManifestPath).Replace(fixture.SourceHash, new string('0', 64), StringComparison.Ordinal));

        var report = PolishAgentDatasetAuditor.Validate(fixture.CanonicalPath, fixture.SftPath, fixture.ManifestPath, fixture.SourcePath);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "source-hash-mismatch");
    }

    [Fact]
    public void Validate_RejectsCanonicalCategoryThatDiffersFromSource()
    {
        using var fixture = new Fixture(tamperCanonicalCategory: true);

        var report = PolishAgentDatasetAuditor.Validate(fixture.CanonicalPath, fixture.SftPath, fixture.ManifestPath, fixture.SourcePath);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "source-category-mismatch");
    }

    [Fact]
    public void Validate_RejectsTemplateFamilyCrossingSplitsAndSftTargetDrift()
    {
        using var fixture = new Fixture(crossSplitFamily: true, tamperSftTarget: true);

        var report = PolishAgentDatasetAuditor.Validate(fixture.CanonicalPath, fixture.SftPath, fixture.ManifestPath, fixture.SourcePath);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "family-cross-split");
        Assert.Contains(report.Issues, issue => issue.Code == "sft-target-mismatch");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "polish-agent-audit-" + Guid.NewGuid().ToString("N"));
        public string CanonicalPath => Path.Combine(_directory, "canonical.jsonl");
        public string SftPath => Path.Combine(_directory, "sft.jsonl");
        public string ManifestPath => Path.Combine(_directory, "manifest.json");
        public string SourcePath => Path.Combine(_directory, "source.jsonl");
        public string SourceHash => HashFile(SourcePath);

        public Fixture(bool crossSplitFamily = false, bool tamperSftTarget = false, bool tamperCanonicalCategory = false)
        {
            Directory.CreateDirectory(_directory);
            var sourceRows = new[]
            {
                SourceRow("source-train", "family-train", "train"),
                SourceRow("source-dev", crossSplitFamily ? "family-train" : "family-dev", "dev"),
                SourceRow("source-test", "family-test", "test")
            };
            File.WriteAllLines(SourcePath, sourceRows.Select(row => JsonSerializer.Serialize(row)), new UTF8Encoding(false));
            var sourceHash = HashFile(SourcePath);
            var canonicalRows = sourceRows.Select((source, index) => BuildCanonical(source, index switch { 0 => "train", 1 => "dev", _ => "test" })).ToArray();
            var canonicalLines = canonicalRows.Select(row => JsonSerializer.Serialize(row)).ToArray();
            if (tamperCanonicalCategory)
                canonicalLines[0] = canonicalLines[0].Replace("\"category\":\"general\"", "\"category\":\"medical\"", StringComparison.Ordinal);
            File.WriteAllLines(CanonicalPath, canonicalLines, new UTF8Encoding(false));
            var sftRows = canonicalRows.Select(BuildSft).ToArray();
            if (tamperSftTarget)
                sftRows[0] = sftRows[0].Replace("rewritten-train", "drifted-target", StringComparison.Ordinal);
            File.WriteAllLines(SftPath, sftRows, new UTF8Encoding(false));
            var counts = new { train = 1, dev = 1, test = 1 };
            var manifest = new
            {
                schema_version = "2.0", dataset_version = "hxz-polish-agent-v2-test", purpose = "agent_behavior_training",
                source_dataset = "hxz-synthetic-v1", source_sha256 = sourceHash,
                canonical_sha256 = HashFile(CanonicalPath), sft_sha256 = HashFile(SftPath), total = 3, counts, per_template_family = 1,
                family_split_policy = "1 train families / 1 dev families / 1 test families",
                files = new { canonical = "canonical.jsonl", sft = "sft.jsonl" },
                limitations = new[] { "合成数据不代表真实用户质量" }
            };
            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest));
        }

        private static object SourceRow(string id, string family, string split) => new
        {
            id = "hxz-v1-polish-" + id,
            mode = "polish",
            category = "general",
            scenario = "职场沟通",
            channel = "群聊",
            risk_level = "low",
            input = "原文内容。 样本1。记录1。",
            context = new { recipient = "同事", purpose = "同步", formality = "自然" },
            claims = new[] { new { subject = "版本", relation = "交付", time = "周五" } },
            should_clarify = false,
            clarification_questions = Array.Empty<string>(),
            gold_output = JsonSerializer.Serialize(new { kind = "final", scenario = "职场沟通", topic = "同步", content = "rewritten-" + split }),
            rubric = new { fidelity = 5, direct_usability = 5 },
            provenance = new { template_family = family },
            review = new { review_status = "accepted", reviewer_count = 1, adjudicated = false }
        };

        private static object BuildCanonical(object sourceObject, string split)
        {
            using var sourceDoc = JsonDocument.Parse(JsonSerializer.Serialize(sourceObject));
            var source = sourceDoc.RootElement;
            using var gold = JsonDocument.Parse(source.GetProperty("gold_output").GetString()!);
            var output = gold.RootElement;
            return new
            {
                id = source.GetProperty("id").GetString()!.Replace("hxz-v1-polish-", "hxz-polish-agent-v2-", StringComparison.Ordinal),
                schema_version = "2.0", split, task_type = "agent_polish", mode = "polish",
                category = source.GetProperty("category").GetString(), scenario = source.GetProperty("scenario").GetString(),
                channel = source.GetProperty("channel").GetString(), risk_level = source.GetProperty("risk_level").GetString(),
                agent_behavior = new { intent = "preserve_meaning_and_improve_expression", preserve_facts = true, preserve_stance = true, preserve_emotion = true, clarify_when = "not_required", output_policy = "directly_usable_final_text_or_structured_clarification" },
                input = "原文内容。", context = source.GetProperty("context").Clone(), claims = source.GetProperty("claims").Clone(),
                expected_decision = "polish", clarification_questions = Array.Empty<string>(),
                output = new { kind = output.GetProperty("kind").GetString(), scenario = output.GetProperty("scenario").GetString(), topic = output.GetProperty("topic").GetString(), content = output.GetProperty("content").GetString() },
                rubric = source.GetProperty("rubric").Clone(),
                provenance = new { source_dataset = "hxz-synthetic-v1", source_id = source.GetProperty("id").GetString(), source_template_family = source.GetProperty("provenance").GetProperty("template_family").GetString(), license = "project-owned-synthetic", transform = "remove_generator_record_suffix_and_normalize_agent_fields" },
                review = new { review_status = "accepted", reviewer_count = 1, adjudicated = false }
            };
        }

        private static string BuildSft(object canonicalObject)
        {
            using var canonicalDoc = JsonDocument.Parse(JsonSerializer.Serialize(canonicalObject));
            var canonical = canonicalDoc.RootElement;
            var user = JsonSerializer.Serialize(new
            {
                task = "润色以下内容；保留事实、立场和情绪，不虚构信息；仅在关键事实缺失时澄清。",
                context = canonical.GetProperty("context"), input = canonical.GetProperty("input")
            });
            var assistant = JsonSerializer.Serialize(canonical.GetProperty("output"));
            return JsonSerializer.Serialize(new
            {
                id = canonical.GetProperty("id").GetString(), split = canonical.GetProperty("split").GetString(),
                messages = new[] { new { role = "user", content = user }, new { role = "assistant", content = assistant } },
                metadata = new
                {
                    expected_decision = canonical.GetProperty("expected_decision").GetString(),
                    scenario = canonical.GetProperty("scenario").GetString(), channel = canonical.GetProperty("channel").GetString(),
                    risk_level = canonical.GetProperty("risk_level").GetString(),
                    source_template_family = canonical.GetProperty("provenance").GetProperty("source_template_family").GetString()
                }
            });
        }

        private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
    }
}
