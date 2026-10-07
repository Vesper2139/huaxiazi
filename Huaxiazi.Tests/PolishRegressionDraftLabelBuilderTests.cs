using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Huaxiazi.DatasetBuilder;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class PolishRegressionDraftLabelBuilderTests
{
    [Fact]
    public void Build_FromFrozenWorkspaceCorpusProduces16UnreviewedDraftLabels()
    {
        using var fixture = new Fixture();
        var root = FindRepoRoot();
        File.Copy(Path.Combine(root, "datasets", "polish-regression-v2-draft", "README.md"), Path.Combine(fixture.Output, "README.md"), overwrite: true);
        File.Copy(Path.Combine(root, "datasets", "polish-regression-v2-draft", "label.schema.json"), Path.Combine(fixture.Output, "label.schema.json"), overwrite: true);
        File.Copy(Path.Combine(root, "datasets", "polish-regression-v2-draft", "label-prompt-v1.md"), fixture.Prompt, overwrite: true);

        var report = PolishRegressionDraftLabelBuilder.Build(
            Path.Combine(root, "datasets", "polish-regression-v1", "cases.jsonl"),
            Path.Combine(root, "datasets", "polish-agent-v2", "canonical.jsonl"),
            Path.Combine(root, "docs", "polish-regression-hierarchy-candidates-2026-10-05-v5.json"),
            Path.Combine(root, "datasets", "polish-regression-v1", "manifest.json"),
            fixture.Prompt, fixture.Output, "AI assistant", "GPT-6 (Codex)", "2026-10-05T03:23:24Z");

        Assert.Equal(16, report.LabelCount);
        Assert.Equal(0, report.HumanVerifiedCount);
        var labelLines = File.ReadAllLines(Path.Combine(fixture.Output, "labels.jsonl"));
        Assert.Equal(16, labelLines.Length);
        foreach (var line in labelLines)
        {
            using var labelDocument = JsonDocument.Parse(line);
            var label = labelDocument.RootElement;
            Assert.Equal("unreviewed", label.GetProperty("human_status").GetString());
            Assert.Equal("ai_assisted_draft", label.GetProperty("origin").GetString());
            Assert.Contains("reference_output_reuse_requires_human_review", label.GetProperty("review_flags").EnumerateArray().Select(flag => flag.GetString()));
            Assert.All(label.GetProperty("fields").EnumerateObject(), field =>
                Assert.Equal("unreviewed", field.Value.GetProperty("provenance").GetProperty("human_status").GetString()));
        }
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Output, "manifest.json")));
        Assert.Equal(0, manifest.RootElement.GetProperty("phase_0_gate_contribution").GetInt32());
        Assert.Equal(0, manifest.RootElement.GetProperty("counts").GetProperty("human_verified").GetInt32());
        Assert.True(manifest.RootElement.GetProperty("not_admissible_as_blind_eval").GetBoolean());
    }

    [Fact]
    public void Build_WritesLineagedFieldLevelAiDraftsAndRefusesOverwrite()
    {
        using var fixture = new Fixture();

        var result = PolishRegressionDraftLabelBuilder.Build(fixture.Cases, fixture.Source, fixture.Hierarchy,
            fixture.ParentManifest, fixture.Prompt, fixture.Output, "AI assistant", "GPT-6 (Codex)", "2026-10-05T03:14:30Z");

        Assert.Equal(1, result.LabelCount);
        Assert.Equal(0, result.HumanVerifiedCount);
        var labelsPath = Path.Combine(fixture.Output, "labels.jsonl");
        var originalHash = SHA256.HashData(File.ReadAllBytes(labelsPath));
        using var document = JsonDocument.Parse(File.ReadAllText(labelsPath));
        var root = document.RootElement;
        Assert.Equal("ai_assisted_draft", root.GetProperty("origin").GetString());
        Assert.Equal("unreviewed", root.GetProperty("human_status").GetString());
        Assert.Equal("make_request", root.GetProperty("fields").GetProperty("requested_intent").GetProperty("value").GetString());
        foreach (var field in root.GetProperty("fields").EnumerateObject())
        {
            var provenance = field.Value.GetProperty("provenance");
            Assert.False(string.IsNullOrWhiteSpace(provenance.GetProperty("draft_by").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(provenance.GetProperty("model").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(provenance.GetProperty("prompt_hash").GetString()));
            if (provenance.GetProperty("kind").GetString() == "ai_draft")
                Assert.Equal(result.PromptSha256, provenance.GetProperty("prompt_hash").GetString());
            Assert.Equal("unreviewed", provenance.GetProperty("human_status").GetString());
        }
        Assert.Throws<IOException>(() => PolishRegressionDraftLabelBuilder.Build(fixture.Cases, fixture.Source, fixture.Hierarchy,
            fixture.ParentManifest, fixture.Prompt, fixture.Output, "AI assistant", "GPT-6 (Codex)", "2026-10-05T03:14:30Z"));
        Assert.Equal(originalHash, SHA256.HashData(File.ReadAllBytes(labelsPath)));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "polish-regression-label-draft-" + Guid.NewGuid().ToString("N"));
        public string Cases => Path.Combine(_directory, "cases.jsonl");
        public string Source => Path.Combine(_directory, "source.jsonl");
        public string Hierarchy => Path.Combine(_directory, "hierarchy.json");
        public string ParentManifest => Path.Combine(_directory, "parent-manifest.json");
        public string Output => Path.Combine(_directory, "output");
        public string Prompt => Path.Combine(Output, "label-prompt-v1.md");

        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            Directory.CreateDirectory(Output);
            File.WriteAllText(Prompt, "Draft labels; do not approve source or gold.");
            File.WriteAllText(Path.Combine(Output, "README.md"), "Internal-only draft.");
            File.WriteAllText(Path.Combine(Output, "label.schema.json"), "{}");
            File.WriteAllText(Cases, JsonSerializer.Serialize(new
            {
                family_id = "hxz-polish-reg-family-0123456789abcdef", split = "development",
                input = "请帮我确认计划。 提出请求，语气自然一点，别写得太客套。",
                expected_decision = "polish", reference_output = "请确认计划。", context = new { purpose = "提出请求", recipient = "经理" },
                claims = Array.Empty<object>(), provenance = new { source_record_id = "source-1" }, human_status = "unreviewed"
            }) + "\n");
            File.WriteAllText(Source, JsonSerializer.Serialize(new
            {
                id = "source-1", scenario = "职场沟通", channel = "群聊", risk_level = "low",
                claims = Array.Empty<object>(), rubric = new { fidelity = 5 }
            }) + "\n");
            var casesHash = HashFile(Cases);
            var sourceHash = HashFile(Source);
            File.WriteAllText(ParentManifest, JsonSerializer.Serialize(new
            {
                dataset_version = "hxz-polish-regression-v1", status = "frozen", phase_0_gate_contribution = 0, not_admissible_as_blind_eval = true,
                source = new { source_sha256 = sourceHash }, files = new Dictionary<string, string> { ["cases.jsonl"] = casesHash }
            }));
            File.WriteAllText(Hierarchy, JsonSerializer.Serialize(new
            {
                purpose = "internal_regression_only", phase0_gate_contribution = 0,
                task_family_count = 1,
                backbones = new[] { new { backbone_id = "hxz-polish-backbone-0123456789abcdef", status = "provisional_requires_human_review",
                    task_families = new[] { new { family_id = "hxz-polish-reg-family-0123456789abcdef", intent = "提出请求",
                        reference_output_alignment_status = "potential_reference_reuse_requires_review" } } } }
            }));
        }

        private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "datasets", "polish-regression-v1", "manifest.json"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository dataset root from test output directory.");
    }
}
