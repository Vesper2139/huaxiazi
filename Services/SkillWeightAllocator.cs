using System;
using System.Collections.Generic;
using System.Linq;

namespace Huaxiazi.Services;

/// <summary>Converts heterogeneous routing scores into bounded mixture weights.</summary>
public static class SkillWeightAllocator
{
    public static IReadOnlyDictionary<string, double> Allocate(
        IEnumerable<(string Id, double Score)> candidates,
        double temperature = 20,
        double maximumWeight = .75)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (temperature <= 0 || maximumWeight is <= 0 or > 1) throw new ArgumentOutOfRangeException();
        var values = candidates.Where(item => !string.IsNullOrWhiteSpace(item.Id) && double.IsFinite(item.Score))
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Id: group.Key, Score: group.Max(item => item.Score)))
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (values.Length == 0) return new Dictionary<string, double>(StringComparer.Ordinal);
        if (values.Length == 1) return new Dictionary<string, double>(StringComparer.Ordinal) { [values[0].Id] = 1d };

        var max = values.Max(item => item.Score);
        var raw = values.Select(item => Math.Exp(Math.Clamp((item.Score - max) / temperature, -40, 0))).ToArray();
        var total = raw.Sum();
        var weights = raw.Select(value => value / total).ToArray();
        // Cap a dominant skill and redistribute its excess among the others.
        var dominant = Array.IndexOf(weights, weights.Max());
        if (weights[dominant] > maximumWeight)
        {
            var excess = weights[dominant] - maximumWeight;
            weights[dominant] = maximumWeight;
            var remainder = 1 - weights[dominant];
            var otherTotal = weights.Where((_, index) => index != dominant).Sum();
            for (var i = 0; i < weights.Length; i++)
                if (i != dominant) weights[i] += excess * (otherTotal <= 0 ? 1d / (weights.Length - 1) : weights[i] / otherTotal);
        }
        var rounded = weights.Select(value => Math.Round(value, 4)).ToArray();
        var residual = Math.Round(1d - rounded.Sum(), 4);
        // Preserve the dominant cap when rounding leaves a positive residual;
        // putting that residual on the dominant entry would violate the hard
        // maximum by a tiny but observable amount.
        var adjustIndex = residual > 0 && values.Length > 1
            ? Enumerable.Range(0, rounded.Length).First(index => index != dominant)
            : Array.IndexOf(rounded, rounded.Max());
        rounded[adjustIndex] = Math.Round(rounded[adjustIndex] + residual, 4);
        return values.Select((item, index) => (item.Id, Weight: rounded[index]))
            .ToDictionary(item => item.Id, item => item.Weight, StringComparer.Ordinal);
    }
}
