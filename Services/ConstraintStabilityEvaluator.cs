using System;
using System.Collections.Generic;
using System.Linq;

namespace Huaxiazi.Services;

public sealed record ConstraintSpec
{
    public IReadOnlyList<string> Required { get; init; } = [];
    /// <summary>Each group requires at least one observable, approved wording variant.</summary>
    public IReadOnlyList<IReadOnlyList<string>> RequiredAnyOf { get; init; } = [];
    public IReadOnlyList<string> Forbidden { get; init; } = [];
    public IReadOnlyList<string> Anchors { get; init; } = [];
}

public sealed record ConstraintStabilityReport(int Samples, double ConstraintPassRate, double AnchorPreservationRate, double CrossRunConsistency, bool Passed);

public static class ConstraintStabilityEvaluator
{
    public static ConstraintStabilityReport Evaluate(IEnumerable<string> outputs, ConstraintSpec spec, double minimumPassRate = .95)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(spec);
        var samples = outputs.Where(output => output is not null).Select(output => output.Trim()).ToArray();
        if (samples.Length == 0) return new(0, 0, 0, 0, false);
        var statuses = samples.Select(output =>
        (
            Constraints: spec.Required.All(item => output.Contains(item, StringComparison.Ordinal)) &&
                spec.RequiredAnyOf.All(group => group.Count > 0 && group.Any(item => !string.IsNullOrWhiteSpace(item) && output.Contains(item, StringComparison.OrdinalIgnoreCase))) &&
                spec.Forbidden.All(item => !output.Contains(item, StringComparison.OrdinalIgnoreCase)),
            Anchors: spec.Anchors.All(item => output.Contains(item, StringComparison.Ordinal))
        )).ToArray();
        var constraintPass = statuses.Count(status => status.Constraints);
        var anchors = statuses.Count(status => status.Anchors);
        // Wording may legitimately vary across runs. Stability therefore means
        // that the observable constraint/anchor decisions remain consistent.
        var consistency = statuses.GroupBy(status => status).Max(group => (double)group.Count() / samples.Length);
        var passRate = (double)constraintPass / samples.Length;
        var anchorRate = (double)anchors / samples.Length;
        return new(samples.Length, passRate, anchorRate, consistency, passRate >= minimumPassRate && anchorRate >= minimumPassRate);
    }

}
