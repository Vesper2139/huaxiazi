using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Huaxiazi.DatasetBuilder;

/// <summary>Versioned, machine-identifiable admission rules for blind evaluation datasets.</summary>
public static class BlindEvaluationAdmissionProtocol
{
    public const string ProtocolId = "huaxiazi-blind-evaluation-admission";
    public const string Version = "3.1";
    public const int RunManifestVersion = 4;
    public const string SchemaVersion = "3.1";
    public const int MinimumSampleCount = 500;
    public const double TaskMinimumRatio = 0.20;
    public const double SplitMinimumRatio = 0.20;
    public const double HighRiskMinimumRatio = 0.10;
    public const double RoutineMinimumRatio = 0.50;
    public const double ClarificationMinimumRatio = 0.05;
    public const double FormatMinimumRatio = 0.05;
    public const double FactOrConstraintAnchorMinimumRatio = 0.50;
    public const int MinimumInputStylesPerSplit = 3;

    private static readonly (string Name, string Definition)[] InputStyles =
    [
        ("colloquial", "complete everyday conversational phrasing"),
        ("fragmentary", "predominantly incomplete or elliptical fragments"),
        ("formal", "predominantly formal written phrasing"),
        ("speech_transcription", "clear spoken disfluencies or transcription artifacts"),
        ("mixed_language", "substantive meaning-bearing code switching")
    ];

    public static IReadOnlyList<string> SupportedInputStyles { get; } =
        Array.AsReadOnly(InputStyles.Select(style => style.Name).ToArray());

    public static string PolicySha256 { get; } = ComputePolicySha256();

    private static string ComputePolicySha256()
    {
        var canonical = new StringBuilder()
            .Append("protocol_id=").Append(ProtocolId).Append('\n')
            .Append("version=").Append(Version).Append('\n')
            .Append("schema_version=").Append(SchemaVersion).Append('\n')
            .Append("minimum_sample_count=").Append(MinimumSampleCount.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append("task_minimum_ratio=").Append(TaskMinimumRatio.ToString("0.00", CultureInfo.InvariantCulture)).Append('\n')
            .Append("split_minimum_ratio=").Append(SplitMinimumRatio.ToString("0.00", CultureInfo.InvariantCulture)).Append('\n')
            .Append("high_risk_minimum_ratio=").Append(HighRiskMinimumRatio.ToString("0.00", CultureInfo.InvariantCulture)).Append('\n')
            .Append("routine_minimum_ratio=").Append(RoutineMinimumRatio.ToString("0.00", CultureInfo.InvariantCulture)).Append('\n')
            .Append("clarification_minimum_ratio=").Append(ClarificationMinimumRatio.ToString("0.00", CultureInfo.InvariantCulture)).Append('\n')
            .Append("format_minimum_ratio=").Append(FormatMinimumRatio.ToString("0.00", CultureInfo.InvariantCulture)).Append('\n')
            .Append("fact_or_constraint_anchor_minimum_ratio=").Append(FactOrConstraintAnchorMinimumRatio.ToString("0.00", CultureInfo.InvariantCulture)).Append('\n')
            .Append("minimum_input_styles_per_split=").Append(MinimumInputStylesPerSplit.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append("rounding=ceiling_with_minimum_one\n")
            .Append("ratio_scope=total_and_each_split\n")
            .Append("routine_definition=risk_level_low_or_medium\n")
            .Append("anchor_definition=constraints_or_annotations_facts_and_constraints\n")
            .Append("gold_review=two_independent_complete_label_sets\n")
            .Append("accepted=reviewer_labels_equal_final_gold_no_adjudication\n")
            .Append("adjudicated=reviewer_labels_disagree_third_party_labels_equal_final_gold\n")
            .Append("gold_review_fields=task,input_style,constraints,risk_level,expected_decision,reference_output,annotations\n")
            .Append("conversation_fields=optional_paired_conversation_id_and_turns\n")
            .Append("conversation_task=polish\n")
            .Append("conversation_minimum_turns=2\n")
            .Append("conversation_turn_indexes=contiguous_one_based\n")
            .Append("conversation_final_input=exact_last_user_input\n")
            .Append("conversation_privacy_and_leakage_scope=all_user_turns\n")
            .Append("split_names=development,frozen_test\n");
        foreach (var style in InputStyles)
            canonical.Append("input_style.").Append(style.Name).Append('=').Append(style.Definition).Append('\n');

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }
}
