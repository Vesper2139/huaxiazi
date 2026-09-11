using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class EmbeddingIndexTests
{
    [Fact]
    public void InMemoryEmbeddingIndex_RanksByCosineAndValidatesDimensions()
    {
        var chunks = new[]
        {
            new KnowledgeChunk("a", "风险", "a"),
            new KnowledgeChunk("b", "日期", "b")
        };
        var index = new InMemoryEmbeddingIndex(
            chunks,
            new Dictionary<string, IReadOnlyList<float>>
            {
                ["a"] = [1, 0],
                ["b"] = [0, 1]
            },
            text => text == "风险" ? [1, 0] : [0, 1]);

        Assert.Equal(1d, index.Similarity("风险", chunks[0]), precision: 6);
        Assert.Equal(0d, index.Similarity("风险", chunks[1]), precision: 6);
        Assert.Throws<ArgumentException>(() => new InMemoryEmbeddingIndex(chunks, new Dictionary<string, IReadOnlyList<float>> { ["a"] = [1], ["b"] = [0, 1] }, _ => [1, 0]));
    }

    [Fact]
    public void InMemoryEmbeddingIndex_ReturnsZeroForUnknownOrInvalidQuery()
    {
        var chunk = new KnowledgeChunk("a", "内容");
        var index = new InMemoryEmbeddingIndex([chunk], new Dictionary<string, IReadOnlyList<float>> { ["a"] = [1, 0] }, _ => [0, 0]);

        Assert.Equal(0d, index.Similarity("unknown", chunk));
        Assert.Equal(0d, index.Similarity("anything", new KnowledgeChunk("missing", "内容")));
    }
}
