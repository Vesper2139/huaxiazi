using System;

namespace Huaxiazi.Services;

public static class ByteSizeFormatter
{
    private static readonly string[] Units = ["KB", "MB", "GB", "TB"];

    public static string FormatModelPackageSize(long bytes)
    {
        var value = Math.Max(0, bytes) / 1024d;
        var unitIndex = 0;

        while (value >= 1024d && unitIndex < Units.Length - 1)
        {
            value /= 1024d;
            unitIndex++;
        }

        return $"{value:0.#} {Units[unitIndex]}";
    }
}
