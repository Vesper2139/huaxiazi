namespace Huaxiazi.DatasetBuilder;

public sealed record FamilyPreferenceStatistics(
    int DecisiveFamilyCount,
    int LeftWins,
    int RightWins,
    int Ties,
    double LeftWinRate,
    double LeftWinLower95,
    double LeftWinUpper95,
    bool Significant);

/// <summary>Summarizes pairwise outcomes at the semantic-family level.</summary>
public static class FamilyPreferenceStatisticsCalculator
{
    public const int MinimumDecisiveFamilyCountForSignificance = 30;

    /// <param name="outcomes">One tuple per compared sample: family ID and 1 for left, -1 for right, 0 for tie.</param>
    public static FamilyPreferenceStatistics Calculate(IEnumerable<(string FamilyId, int Outcome)> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        var familyOutcomes = outcomes
            .GroupBy(item => item.FamilyId ?? string.Empty, StringComparer.Ordinal)
            .Select(group => group.Sum(item => Math.Sign(item.Outcome)))
            .ToArray();
        var leftWins = familyOutcomes.Count(item => item > 0);
        var rightWins = familyOutcomes.Count(item => item < 0);
        var ties = familyOutcomes.Length - leftWins - rightWins;
        var decisive = leftWins + rightWins;
        var rate = decisive == 0 ? 0d : (double)leftWins / decisive;
        var lower = WilsonLowerBound(leftWins, decisive);
        var upper = WilsonUpperBound(leftWins, decisive);

        return new(decisive, leftWins, rightWins, ties, rate, lower, upper,
            decisive >= MinimumDecisiveFamilyCountForSignificance && lower > .5);
    }

    private static double WilsonLowerBound(int successes, int count)
    {
        if (count == 0) return 0;
        const double z = 1.96;
        var n = (double)count;
        var p = successes / n;
        var denominator = 1 + z * z / n;
        var center = p + z * z / (2 * n);
        var margin = z * Math.Sqrt((p * (1 - p) + z * z / (4 * n)) / n);
        return Math.Max(0, (center - margin) / denominator);
    }

    private static double WilsonUpperBound(int successes, int count)
    {
        if (count == 0) return 1;
        const double z = 1.96;
        var n = (double)count;
        var p = successes / n;
        var denominator = 1 + z * z / n;
        var center = p + z * z / (2 * n);
        var margin = z * Math.Sqrt((p * (1 - p) + z * z / (4 * n)) / n);
        return Math.Min(1, (center + margin) / denominator);
    }
}
