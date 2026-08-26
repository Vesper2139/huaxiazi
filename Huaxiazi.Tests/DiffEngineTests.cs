using System.Linq;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class DiffEngineTests
{
    [Fact]
    public void LargeCompletelyDifferentDocuments_UseTruthfulLinearMemoryPreview()
    {
        var original = string.Join('\n', Enumerable.Range(0, 1200).Select(index => $"旧-{index}"));
        var optimized = string.Join('\n', Enumerable.Range(0, 1200).Select(index => $"新-{index}"));

        var result = DiffEngine.Diff(original, optimized);

        Assert.Contains("简化", result[0].Text);
        Assert.Equal(1200, result.Count(line => line.Kind == DiffLineKind.Removed));
        Assert.Equal(1200, result.Count(line => line.Kind == DiffLineKind.Added));
    }
}
