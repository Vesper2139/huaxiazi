using System;
using System.Collections.Generic;
using System.Linq;

namespace Huaxiazi.Services;

/// <summary>Provider-agnostic dense-vector index used by the hybrid retriever.</summary>
public interface IEmbeddingIndex
{
    double Similarity(string query, KnowledgeChunk chunk);
}

/// <summary>
/// Validates precomputed chunk vectors and computes bounded cosine similarity.
/// A production provider can replace the query encoder without changing RAG ranking code.
/// </summary>
public sealed class InMemoryEmbeddingIndex : IEmbeddingIndex
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<float>> _vectors;
    private readonly Func<string, IReadOnlyList<float>> _queryEncoder;
    private readonly int _dimensions;

    public InMemoryEmbeddingIndex(
        IEnumerable<KnowledgeChunk> chunks,
        IReadOnlyDictionary<string, IReadOnlyList<float>> vectors,
        Func<string, IReadOnlyList<float>> queryEncoder)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(vectors);
        ArgumentNullException.ThrowIfNull(queryEncoder);
        var ids = chunks.Select(chunk => chunk.Id).Where(id => !string.IsNullOrWhiteSpace(id)).ToArray();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new ArgumentException("知识块 ID 必须唯一。", nameof(chunks));
        if (vectors.Count == 0) throw new ArgumentException("至少需要一个向量。", nameof(vectors));
        if (vectors.Keys.Any(key => !ids.Contains(key, StringComparer.Ordinal)))
            throw new ArgumentException("向量不能引用未知知识块。", nameof(vectors));
        _dimensions = ValidateDimensions(vectors.Values, nameof(vectors));
        _vectors = vectors.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        _queryEncoder = queryEncoder;
    }

    public double Similarity(string query, KnowledgeChunk chunk)
    {
        if (string.IsNullOrWhiteSpace(query) || chunk is null || !_vectors.TryGetValue(chunk.Id, out var document)) return 0d;
        try
        {
            var queryVector = _queryEncoder(query);
            if (queryVector is null || queryVector.Count != _dimensions || !IsFinite(queryVector)) return 0d;
            return Cosine(queryVector, document);
        }
        catch (Exception)
        {
            return 0d;
        }
    }

    private static int ValidateDimensions(IEnumerable<IReadOnlyList<float>> vectors, string parameterName)
    {
        var first = vectors.FirstOrDefault();
        if (first is null || first.Count == 0 || !IsFinite(first)) throw new ArgumentException("向量维度和值必须有效。", parameterName);
        var dimensions = first.Count;
        if (vectors.Any(vector => vector is null || vector.Count != dimensions || !IsFinite(vector)))
            throw new ArgumentException("所有向量必须具有相同的有限维度。", parameterName);
        return dimensions;
    }

    private static bool IsFinite(IReadOnlyList<float> vector) => vector.All(float.IsFinite);

    private static double Cosine(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        double dot = 0, leftNorm = 0, rightNorm = 0;
        for (var i = 0; i < left.Count; i++)
        {
            dot += left[i] * right[i];
            leftNorm += left[i] * left[i];
            rightNorm += right[i] * right[i];
        }
        if (leftNorm <= double.Epsilon || rightNorm <= double.Epsilon) return 0d;
        return Math.Clamp(dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm)), 0d, 1d);
    }
}
