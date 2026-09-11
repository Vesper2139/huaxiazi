using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Huaxiazi.Services;

public enum KnowledgeSensitivity { Public, Internal, Sensitive }

public sealed record KnowledgeChunk(string Id, string Content, string Source = "", IReadOnlyList<string>? Tags = null, KnowledgeSensitivity Sensitivity = KnowledgeSensitivity.Public);

public sealed record RetrievedContext(string Id, string Source, string Content, double LexicalScore, double SemanticScore, double Score);

/// <summary>Small deterministic hybrid retriever; replace the semantic scorer with an embedding service in production.</summary>
public sealed class HybridContextRetriever
{
    private readonly IReadOnlyList<KnowledgeChunk> _chunks;
    private readonly Func<string, KnowledgeChunk, double>? _semanticScorer;

    public HybridContextRetriever(IEnumerable<KnowledgeChunk> chunks, Func<string, KnowledgeChunk, double>? semanticScorer = null)
    {
        _chunks = chunks?.Where(chunk => !string.IsNullOrWhiteSpace(chunk.Id))
            .GroupBy(chunk => chunk.Id, StringComparer.Ordinal)
            .Select(group => group.Last()).ToArray() ?? throw new ArgumentNullException(nameof(chunks));
        _semanticScorer = semanticScorer;
    }

    public HybridContextRetriever(IEnumerable<KnowledgeChunk> chunks, IEmbeddingIndex embeddingIndex)
        : this(chunks, (embeddingIndex ?? throw new ArgumentNullException(nameof(embeddingIndex))).Similarity)
    {
    }

    public IReadOnlyList<RetrievedContext> Retrieve(string query, int topK = 5, int maxCharacters = 6_000, KnowledgeSensitivity maxSensitivity = KnowledgeSensitivity.Internal)
    {
        if (string.IsNullOrWhiteSpace(query) || topK <= 0 || maxCharacters <= 0) return [];
        var queryTerms = Terms(query);
        var ranked = _chunks.Where(chunk => chunk.Sensitivity <= maxSensitivity)
            .Select(chunk =>
            {
                var lexical = LexicalScore(queryTerms, chunk);
                var semantic = SafeSemanticScore(query, chunk);
                return new RetrievedContext(chunk.Id, chunk.Source, PromptInjectionSanitizer.RemoveUnsafeLines(chunk.Content), lexical, semantic, .55 * lexical + .45 * semantic);
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score).ThenBy(item => item.Id, StringComparer.Ordinal)
            .Take(topK);
        var output = new List<RetrievedContext>();
        var used = 0;
        foreach (var item in ranked)
        {
            var remaining = maxCharacters - used;
            if (remaining <= 0) break;
            var content = item.Content.Length <= remaining ? item.Content : SafePrefix(item.Content, Math.Max(0, remaining - 1)).TrimEnd() + "…";
            output.Add(item with { Content = content });
            used += content.Length;
        }
        return output;
    }

    private static double LexicalScore(IReadOnlySet<string> queryTerms, KnowledgeChunk chunk)
    {
        var terms = Terms(chunk.Content);
        if (queryTerms.Count == 0 || terms.Count == 0) return 0;
        return (double)queryTerms.Intersect(terms, StringComparer.OrdinalIgnoreCase).Count() / queryTerms.Count;
    }

    private double SafeSemanticScore(string query, KnowledgeChunk chunk)
    {
        if (_semanticScorer is null) return 0d;
        try { return Math.Clamp(_semanticScorer(query, chunk), 0d, 1d); }
        catch (Exception) { return 0d; }
    }

    private static HashSet<string> Terms(string value)
    {
        var terms = Regex.Matches(value.ToLowerInvariant(), @"[\p{L}\p{N}]{2,}").Select(match => match.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var token in terms.ToArray())
            if (token.Any(ch => ch >= '\u4E00' && ch <= '\u9FFF'))
                for (var i = 0; i < token.Length - 1; i++) terms.Add(token.Substring(i, 2));
        return terms;
    }

    private static string SafePrefix(string value, int length)
    {
        var end = Math.Min(length, value.Length);
        if (end > 0 && end < value.Length && char.IsHighSurrogate(value[end - 1]) && char.IsLowSurrogate(value[end])) end--;
        return value[..Math.Max(0, end)];
    }

}
