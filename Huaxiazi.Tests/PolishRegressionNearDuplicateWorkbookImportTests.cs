using System.IO.Compression;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Huaxiazi.DatasetBuilder;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class PolishRegressionNearDuplicateWorkbookImportTests
{
    [Fact]
    public void ImportCommand_MapsCompletedRowsByPairIdAndPreservesCandidateReferences()
    {
        using var fixture = new Fixture();
        fixture.WriteWorkbook(fixture.WorkbookPath, reverseReviewRows: true);

        var (exitCode, _) = RunCommand(fixture.Command(fixture.OutputPath));

        Assert.Equal(0, exitCode);
        var imported = File.ReadLines(fixture.OutputPath).Select(line => JsonDocument.Parse(line)).ToArray();
        Assert.Equal(3, imported.Length);
        var sourcePairs = File.ReadLines(Path.Combine(fixture.ReportDirectory, "candidate-pairs.jsonl"))
            .Select(line => JsonDocument.Parse(line)).ToDictionary(document => document.RootElement.GetProperty("pair_id").GetString()!);
        foreach (var item in imported)
        {
            using (item)
            {
                var root = item.RootElement;
                var pairId = root.GetProperty("pair_id").GetString()!;
                Assert.Equal("reviewer-" + pairId[^4..], root.GetProperty("reviewer_id").GetString());
                Assert.Equal("distinct_task_intent_same_backbone", root.GetProperty("decision").GetString());
                Assert.Equal("mapped-" + pairId, root.GetProperty("rationale").GetString());
                Assert.Equal("adjudicated", root.GetProperty("status").GetString());
                Assert.Equal(sourcePairs[pairId].RootElement.GetProperty("left_case_id").GetString(), root.GetProperty("left_case_id").GetString());
                Assert.Equal(sourcePairs[pairId].RootElement.GetProperty("right_case_id").GetString(), root.GetProperty("right_case_id").GetString());
            }
        }
        foreach (var document in sourcePairs.Values) document.Dispose();
    }

    [Fact]
    public void ImportCommand_RejectsWorkbookBoundToDifferentSourceManifest()
    {
        using var fixture = new Fixture();
        fixture.WriteWorkbook(fixture.WorkbookPath, sourceBindingOverride: ("parent_manifest_sha256", "stale-hash"));

        var (exitCode, error) = RunCommand(fixture.Command(fixture.OutputPath));

        Assert.NotEqual(0, exitCode);
        Assert.Contains("source-binding", error, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.OutputPath));
    }

    [Fact]
    public void ImportCommand_RejectsIncompleteHumanFieldsWithoutWritingOutput()
    {
        using var fixture = new Fixture();
        fixture.WriteWorkbook(fixture.WorkbookPath, incompletePairId: fixture.PairIds[0]);

        var (exitCode, error) = RunCommand(fixture.Command(fixture.OutputPath));

        Assert.NotEqual(0, exitCode);
        Assert.Contains("incomplete", error, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.OutputPath));
    }

    [Fact]
    public void ImportCommand_ReportsAllPlaceholderRationaleRowsWithoutWritingOutput()
    {
        using var fixture = new Fixture();
        fixture.WriteWorkbook(fixture.WorkbookPath, rationaleOverride: "无非空理由");

        var (exitCode, error) = RunCommand(fixture.Command(fixture.OutputPath));

        Assert.NotEqual(0, exitCode);
        Assert.Contains("rationale", error, StringComparison.OrdinalIgnoreCase);
        foreach (var pairId in fixture.PairIds) Assert.Contains(pairId, error, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.OutputPath));
    }

    [Fact]
    public void ImportCommand_RefusesToOverwriteExistingOutput()
    {
        using var fixture = new Fixture();
        fixture.WriteWorkbook(fixture.WorkbookPath);
        File.WriteAllText(fixture.OutputPath, "keep-existing\n");

        var (exitCode, error) = RunCommand(fixture.Command(fixture.OutputPath));

        Assert.NotEqual(0, exitCode);
        Assert.Contains("exists", error, StringComparison.Ordinal);
        Assert.Equal("keep-existing\n", File.ReadAllText(fixture.OutputPath));
    }

    [Fact]
    public void ImportCommand_RejectsAlteredCaseEvidenceWithoutWritingOutput()
    {
        using var fixture = new Fixture();
        fixture.WriteWorkbook(fixture.WorkbookPath, tamperEvidencePairId: fixture.PairIds[0]);

        var (exitCode, error) = RunCommand(fixture.Command(fixture.OutputPath));

        Assert.NotEqual(0, exitCode);
        Assert.Contains("decision-mismatch", error, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.OutputPath));
    }

    [Fact]
    public void ImportCommand_RejectsDuplicatePairRowsWithoutWritingOutput()
    {
        using var fixture = new Fixture();
        fixture.WriteWorkbook(fixture.WorkbookPath, duplicateReviewPairId: fixture.PairIds[0]);

        var (exitCode, error) = RunCommand(fixture.Command(fixture.OutputPath));

        Assert.NotEqual(0, exitCode);
        Assert.Contains("decision-mismatch", error, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.OutputPath));
    }

    [Fact]
    public void ImportCommand_ReportsMalformedCandidateManifestWithoutThrowing()
    {
        using var fixture = new Fixture();
        fixture.WriteWorkbook(fixture.WorkbookPath);
        var manifestPath = Path.Combine(fixture.ReportDirectory, "manifest.json");
        File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace("\"files\":", "\"broken_files\":", StringComparison.Ordinal));

        var (exitCode, error) = RunCommand(fixture.Command(fixture.OutputPath));

        Assert.Equal(2, exitCode);
        Assert.Contains("必需字段", error, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.OutputPath));
    }

    [Fact]
    public void ImportCommand_RefusesOutputInsideFrozenCasesDirectory()
    {
        using var fixture = new Fixture();
        fixture.WriteWorkbook(fixture.WorkbookPath);

        var (exitCode, error) = RunCommand(fixture.Command(fixture.FrozenOutputPath));

        Assert.NotEqual(0, exitCode);
        Assert.Contains("output-boundary", error, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.FrozenOutputPath));
    }

    [Fact]
    public void ImportCommand_RefusesOutputInsideCandidateReportDirectory()
    {
        using var fixture = new Fixture();
        fixture.WriteWorkbook(fixture.WorkbookPath);

        var (exitCode, error) = RunCommand(fixture.Command(fixture.ReportOutputPath));

        Assert.NotEqual(0, exitCode);
        Assert.Contains("output-boundary", error, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.ReportOutputPath));
    }

    private static (int ExitCode, string Error) RunCommand(string[] args)
    {
        var originalError = Console.Error;
        using var error = new StringWriter();
        try
        {
            Console.SetError(error);
            return (Program.Main(args), error.ToString());
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "neardup-import-" + Guid.NewGuid().ToString("N"));
        private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace OfficeRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace PackageRel = "http://schemas.openxmlformats.org/package/2006/relationships";

        public string CasesDirectory => Path.Combine(_root, "frozen-source");
        public string CasesPath => Path.Combine(CasesDirectory, "cases.jsonl");
        public string ParentManifestPath => Path.Combine(_root, "parent-manifest.json");
        public string RulesPath => Path.Combine(_root, "rules.json");
        public string ReportDirectory => Path.Combine(_root, "candidate-report");
        public string WorkbookPath => Path.Combine(_root, "review.xlsx");
        public string OutputPath => Path.Combine(_root, "results", "human-decisions.completed.jsonl");
        public string FrozenOutputPath => Path.Combine(CasesDirectory, "human-decisions.completed.jsonl");
        public string ReportOutputPath => Path.Combine(ReportDirectory, "human-decisions.completed.jsonl");
        public string[] PairIds => File.ReadLines(Path.Combine(ReportDirectory, "candidate-pairs.jsonl"))
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("pair_id").GetString()!).ToArray();

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(CasesDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath)!);
            File.WriteAllLines(CasesPath,
            [
                "{\"id\":\"case-a\",\"family_id\":\"family-a\",\"split\":\"development\",\"input\":\"项目安排需要确认。\"}",
                "{\"id\":\"case-b\",\"family_id\":\"family-b\",\"split\":\"regression\",\"input\":\"项目安排需要确认！\"}",
                "{\"id\":\"case-c\",\"family_id\":\"family-c\",\"split\":\"development\",\"input\":\"项目安排需要确认？\"}",
            ], new UTF8Encoding(false));
            var casesHash = Hash(File.ReadAllBytes(CasesPath));
            File.WriteAllText(ParentManifestPath,
                "{\"dataset_version\":\"hxz-polish-regression-v1\",\"status\":\"frozen\",\"phase_0_gate_contribution\":0,\"not_admissible_as_blind_eval\":true,\"counts\":{\"families\":3},\"files\":{\"cases.jsonl\":\"" + casesHash + "\"}}\n",
                new UTF8Encoding(false));
            File.WriteAllText(RulesPath,
                "{\"schema_version\":\"1.0\",\"ruleset_id\":\"test-char-trigram-v1\",\"normalization\":\"unicode-formkc-lowercase-alphanumeric-codepoint-trigrams-v1\",\"shingle_size\":3,\"candidate_threshold\":0.8,\"sensitivity_thresholds\":[0.7,0.8,0.9]}\n",
                new UTF8Encoding(false));
            PolishRegressionNearDuplicateAuditor.Build(CasesPath, ParentManifestPath, RulesPath, ReportDirectory);
        }

        public string[] Command(string outputPath) =>
        [
            "polish-regression-neardup-import-workbook",
            "--report-dir", ReportDirectory,
            "--cases", CasesPath,
            "--workbook", WorkbookPath,
            "--output", outputPath,
        ];

        public void WriteWorkbook(string path, bool reverseReviewRows = false,
            (string Key, string Value)? sourceBindingOverride = null, string? incompletePairId = null,
            string? tamperEvidencePairId = null, string? duplicateReviewPairId = null,
            string? rationaleOverride = null)
        {
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            var sheetNames = new[] { "审阅说明", "候选对审阅", "候选元数据", "来源校验" };
            var workbook = new XElement(Main + "workbook",
                new XAttribute(XNamespace.Xmlns + "r", OfficeRel),
                new XElement(Main + "sheets", sheetNames.Select((name, index) =>
                    new XElement(Main + "sheet", new XAttribute("name", name), new XAttribute("sheetId", index + 1),
                        new XAttribute(OfficeRel + "id", "rId" + (index + 1))))));
            AddXml(archive, "xl/workbook.xml", workbook);
            var relationships = new XElement(PackageRel + "Relationships", sheetNames.Select((_, index) =>
                new XElement(PackageRel + "Relationship", new XAttribute("Id", "rId" + (index + 1)),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"),
                    new XAttribute("Target", "worksheets/sheet" + (index + 1) + ".xml"))));
            AddXml(archive, "xl/_rels/workbook.xml.rels", relationships);

            AddSheet(archive, "xl/worksheets/sheet1.xml", new Dictionary<string, string?>(), []);

            var pairs = File.ReadLines(Path.Combine(ReportDirectory, "candidate-pairs.jsonl"))
                .Select(line => JsonDocument.Parse(line)).ToArray();
            var casesById = File.ReadLines(CasesPath).Select(line => JsonDocument.Parse(line))
                .ToDictionary(document => document.RootElement.GetProperty("id").GetString()!, document => document);
            var reviewRows = new List<(string PairId, string?[] Values)>();
            foreach (var pairDoc in pairs)
            {
                var pair = pairDoc.RootElement;
                var id = pair.GetProperty("pair_id").GetString()!;
                var leftEvidence = Evidence(casesById[pair.GetProperty("left_case_id").GetString()!].RootElement);
                if (tamperEvidencePairId == id) leftEvidence += " [evidence edited]";
                reviewRows.Add((id,
                [
                    id,
                    leftEvidence,
                    Evidence(casesById[pair.GetProperty("right_case_id").GetString()!].RootElement),
                    incompletePairId == id ? null : "distinct_task_intent_same_backbone",
                    incompletePairId == id ? null : "reviewer-" + id[^4..],
                    incompletePairId == id ? null : "2026-10-05T12:00:00Z",
                    rationaleOverride ?? (incompletePairId == id ? null : "mapped-" + id),
                ]));
            }
            if (duplicateReviewPairId is { } duplicateId)
            {
                var duplicate = reviewRows.Single(item => item.PairId == duplicateId);
                reviewRows.Add((duplicate.PairId, duplicate.Values.ToArray()));
            }
            if (reverseReviewRows) reviewRows.Reverse();
            AddSheet(archive, "xl/worksheets/sheet2.xml", new Dictionary<string, string?>
            {
                ["A4"] = "pair_id", ["B4"] = "左侧案例证据（未含参考成稿）", ["C4"] = "右侧案例证据（未含参考成稿）",
                ["D4"] = "decision", ["E4"] = "reviewer_id", ["F4"] = "reviewed_at_utc", ["G4"] = "rationale",
            }, reviewRows.Select((row, index) => (rowNumber: index + 5, row.Values)).ToArray());

            var metadata = new Dictionary<string, string?>
            {
                ["A4"] = "pair_id", ["B4"] = "char_trigram_jaccard", ["C4"] = "left_case_id", ["D4"] = "left_split",
                ["E4"] = "right_case_id", ["F4"] = "right_split", ["G4"] = "split_relation", ["H4"] = "adjudication_status",
            };
            var metadataRows = pairs.Select((pairDoc, index) =>
            {
                var pair = pairDoc.RootElement;
                var id = pair.GetProperty("pair_id").GetString()!;
                return (rowNumber: index + 5, new string?[]
                {
                    id,
                    pair.GetProperty("char_trigram_jaccard").GetDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                    pair.GetProperty("left_case_id").GetString(), pair.GetProperty("left_split").GetString(),
                    pair.GetProperty("right_case_id").GetString(), pair.GetProperty("right_split").GetString(),
                    pair.GetProperty("left_split").GetString() == pair.GetProperty("right_split").GetString() ? "same_split" : "cross_split",
                    pair.GetProperty("adjudication_status").GetString(),
                });
            }).ToArray();
            AddSheet(archive, "xl/worksheets/sheet3.xml", metadata, metadataRows);

            using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ReportDirectory, "manifest.json")));
            var manifestRoot = manifest.RootElement;
            var files = manifestRoot.GetProperty("files");
            var source = new Dictionary<string, string?>
            {
                ["A1"] = "workbook_schema_version", ["B1"] = "hxz-polish-neardup-human-review-v1",
                ["A2"] = "artifact_version", ["B2"] = manifestRoot.GetProperty("artifact_version").GetString(),
                ["A3"] = "manifest_sha256", ["B3"] = Hash(File.ReadAllBytes(Path.Combine(ReportDirectory, "manifest.json"))),
                ["A4"] = "candidate_pairs_sha256", ["B4"] = files.GetProperty("candidate-pairs.jsonl").GetString(),
                ["A5"] = "human_template_sha256", ["B5"] = files.GetProperty("human-adjudication.template.jsonl").GetString(),
                ["A6"] = "parent_manifest_sha256", ["B6"] = manifestRoot.GetProperty("parent_manifest_sha256").GetString(),
                ["A7"] = "source_cases_sha256", ["B7"] = manifestRoot.GetProperty("source_cases_sha256").GetString(),
                ["A8"] = "rules_sha256", ["B8"] = manifestRoot.GetProperty("rules_sha256").GetString(),
            };
            if (sourceBindingOverride is { } replacement) source["B" + (replacement.Key switch
                { "artifact_version" => 2, "manifest_sha256" => 3, "candidate_pairs_sha256" => 4, "human_template_sha256" => 5,
                    "parent_manifest_sha256" => 6, "source_cases_sha256" => 7, "rules_sha256" => 8, _ => throw new ArgumentOutOfRangeException() })] = replacement.Value;
            AddSheet(archive, "xl/worksheets/sheet4.xml", source, []);

            foreach (var doc in pairs) doc.Dispose();
            foreach (var doc in casesById.Values) doc.Dispose();
        }

        private static string Evidence(JsonElement item) =>
            $"Input: {item.GetProperty("input").GetString()}\nPurpose: \nFormality: \nExplicit requirements: \nClaims: []";

        private static void AddSheet(ZipArchive archive, string name, Dictionary<string, string?> header,
            IReadOnlyList<(int rowNumber, string?[] values)> data)
        {
            var cells = new Dictionary<string, string?>(header, StringComparer.Ordinal);
            foreach (var (rowNumber, values) in data)
            {
                for (var column = 0; column < values.Length; column++)
                    cells[ColumnName(column + 1) + rowNumber] = values[column];
            }
            var rows = cells.Select(item => (item.Key, item.Value, Row: int.Parse(new string(item.Key.Where(char.IsDigit).ToArray()), System.Globalization.CultureInfo.InvariantCulture)))
                .GroupBy(item => item.Row).OrderBy(group => group.Key)
                .Select(group => new XElement(Main + "row", new XAttribute("r", group.Key), group.OrderBy(item => ColumnIndex(item.Key)).Where(item => item.Value is not null)
                    .Select(item => new XElement(Main + "c", new XAttribute("r", item.Key), new XAttribute("t", "str"), new XElement(Main + "v", item.Value)))));
            AddXml(archive, name, new XElement(Main + "worksheet", new XElement(Main + "sheetData", rows)));
        }

        private static void AddXml(ZipArchive archive, string name, XElement root)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(new XDocument(new XDeclaration("1.0", "utf-8", null), root).ToString(SaveOptions.DisableFormatting));
        }

        private static string ColumnName(int column)
        {
            var result = string.Empty;
            while (column > 0) { column--; result = (char)('A' + column % 26) + result; column /= 26; }
            return result;
        }

        private static int ColumnIndex(string address)
        {
            var value = 0;
            foreach (var character in address.TakeWhile(char.IsLetter)) value = value * 26 + char.ToUpperInvariant(character) - 'A' + 1;
            return value;
        }

        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
