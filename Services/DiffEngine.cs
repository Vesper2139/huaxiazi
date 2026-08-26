using System;
using System.Collections.Generic;
using System.Linq;

namespace Huaxiazi.Services;

/// <summary>
/// 逐行 Diff（LCS）。用于「原文 / 优化稿」的差异视图，区分新增与删除行。
/// </summary>
public enum DiffLineKind
{
    Same,
    Added,
    Removed
}

public readonly record struct DiffLine(DiffLineKind Kind, string Text);

public static class DiffEngine
{
    private const long DetailedComparisonCellLimit = 1_000_000;

    public static IReadOnlyList<DiffLine> Diff(string original, string optimized)
    {
        var a = Split(original);
        var b = Split(optimized);
        if (a.Length == 0 && b.Length == 0) return Array.Empty<DiffLine>();
        return Build(a, b);
    }

    private static string[] Split(string text) =>
        (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static List<DiffLine> Build(string[] a, string[] b)
    {
        var m = a.Length;
        var n = b.Length;
        if ((long)m * n > DetailedComparisonCellLimit)
        {
            var simplified = new List<DiffLine>(m + n + 1)
            {
                new(DiffLineKind.Same, "— 内容较长，已使用简化差异预览 —")
            };
            simplified.AddRange(a.Select(line => new DiffLine(DiffLineKind.Removed, line)));
            simplified.AddRange(b.Select(line => new DiffLine(DiffLineKind.Added, line)));
            return simplified;
        }
        var lcs = new int[m + 1, n + 1];
        for (var i = m - 1; i >= 0; i--)
        {
            for (var j = n - 1; j >= 0; j--)
            {
                lcs[i, j] = a[i] == b[j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var result = new List<DiffLine>(m + n);
        int x = 0, y = 0;
        while (x < m && y < n)
        {
            if (a[x] == b[y])
            {
                result.Add(new DiffLine(DiffLineKind.Same, a[x]));
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                result.Add(new DiffLine(DiffLineKind.Removed, a[x]));
                x++;
            }
            else
            {
                result.Add(new DiffLine(DiffLineKind.Added, b[y]));
                y++;
            }
        }

        while (x < m)
        {
            result.Add(new DiffLine(DiffLineKind.Removed, a[x]));
            x++;
        }

        while (y < n)
        {
            result.Add(new DiffLine(DiffLineKind.Added, b[y]));
            y++;
        }

        return result;
    }
}
