using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class StructuredOutputContractFingerprintTests
{
    [Fact]
    public void Compute_IsStableAcrossCollectionInsertionOrder()
    {
        var first = new StructuredOutputContract(
            "result",
            new HashSet<string>(["kind", "answer"], StringComparer.Ordinal),
            new HashSet<string>(["kind", "answer"], StringComparer.Ordinal),
            FieldTypes: new Dictionary<string, StructuredOutputFieldType>(StringComparer.Ordinal)
            {
                ["answer"] = StructuredOutputFieldType.String,
                ["kind"] = StructuredOutputFieldType.String
            });
        var second = new StructuredOutputContract(
            "result",
            new HashSet<string>(["answer", "kind"], StringComparer.Ordinal),
            new HashSet<string>(["answer", "kind"], StringComparer.Ordinal),
            FieldTypes: new Dictionary<string, StructuredOutputFieldType>(StringComparer.Ordinal)
            {
                ["kind"] = StructuredOutputFieldType.String,
                ["answer"] = StructuredOutputFieldType.String
            });

        Assert.Equal(
            StructuredOutputContractFingerprint.Compute(first),
            StructuredOutputContractFingerprint.Compute(second));
    }

    [Fact]
    public void Compute_ChangesWhenLocalValidationBoundsChange()
    {
        var first = new StructuredOutputContract(
            "result",
            new HashSet<string>(["answer"], StringComparer.Ordinal),
            new HashSet<string>(["answer"], StringComparer.Ordinal),
            MaxAnswerCharacters: 100);
        var second = first with { MaxAnswerCharacters = 101 };

        Assert.NotEqual(
            StructuredOutputContractFingerprint.Compute(first),
            StructuredOutputContractFingerprint.Compute(second));
    }

    [Fact]
    public void Compute_IgnoresDescriptiveContractName()
    {
        var first = new StructuredOutputContract(
            "optimized-prompt",
            new HashSet<string>(["answer"], StringComparer.Ordinal),
            new HashSet<string>(["answer"], StringComparer.Ordinal));
        var second = first with { Name = "prompt-answer-v2" };

        Assert.Equal(
            StructuredOutputContractFingerprint.Compute(first),
            StructuredOutputContractFingerprint.Compute(second));
    }
}
