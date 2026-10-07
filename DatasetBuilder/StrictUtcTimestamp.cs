using System.Globalization;
using System.Text.RegularExpressions;

namespace Huaxiazi.DatasetBuilder;

internal static class StrictUtcTimestamp
{
    private static readonly Regex Rfc3339UtcShape = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool TryParse(string? value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return value is not null &&
            Rfc3339UtcShape.IsMatch(value) &&
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp) &&
            timestamp.Offset == TimeSpan.Zero;
    }
}
