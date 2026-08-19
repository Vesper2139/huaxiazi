using System;
using System.Collections.Generic;

namespace PromptFloat.Services;

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
