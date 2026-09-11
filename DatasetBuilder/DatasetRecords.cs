using System.Text.Json.Serialization;

namespace Huaxiazi.DatasetBuilder;

public sealed record DatasetContext(
    string Recipient,
    string Purpose,
    string Formality,
    string ExplicitRequirements);

public sealed record DatasetClaim(
    string Subject,
    string Relation,
    string Object,
    string Quantity,
    string Time,
    bool Negated,
    string Condition,
    string Modality);

public sealed record DatasetRubric(
    int Fidelity,
    int TaskCompletion,
    int Naturalness,
    int DirectUsability,
    int Safety);

public sealed record DatasetProvenance(
    string Source,
    string Generator,
    string GeneratorPromptVersion,
    string TemplateFamily,
    string License);

public sealed record DatasetReview(
    string ReviewStatus,
    int ReviewerCount,
    bool Adjudicated);

public sealed record DatasetRecord
{
    public string Id { get; init; } = string.Empty;
    public string SchemaVersion { get; init; } = "1.0";
    public string Split { get; init; } = string.Empty;
    public string TaskType { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Depth { get; init; } = string.Empty;
    public string Scenario { get; init; } = string.Empty;
    public string Channel { get; init; } = string.Empty;
    public string RiskLevel { get; init; } = string.Empty;
    public string Input { get; init; } = string.Empty;
    public DatasetContext Context { get; init; } = new("", "", "", "");
    public IReadOnlyList<DatasetClaim> Claims { get; init; } = [];
    public bool ShouldClarify { get; init; }
    public IReadOnlyList<string> ClarificationQuestions { get; init; } = [];
    public string GoldOutput { get; init; } = string.Empty;
    public IReadOnlyList<string> RejectedOutputs { get; init; } = [];
    public DatasetRubric Rubric { get; init; } = new(0, 0, 0, 0, 0);
    public DatasetProvenance Provenance { get; init; } = new("", "", "", "", "");
    public DatasetReview Review { get; init; } = new("pending", 0, false);
}

public sealed class DatasetGenerationOptions
{
    public int TrainCount { get; init; } = 10_000;
    public int DevCount { get; init; } = 1_000;
    public int TestCount { get; init; } = 1_000;
    public int Seed { get; init; } = 20260907;

    public static DatasetGenerationOptions Standard(int seed) => new() { Seed = seed };
}

public sealed record DatasetValidationIssue(string Code, string RecordId, string Message);

public sealed record DatasetExportResult(string CanonicalPath, string SftPath, string PreferencePath);
