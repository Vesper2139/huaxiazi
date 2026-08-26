using System;
using System.IO;
using System.Linq;

namespace PromptFloat.Services;

internal static class DataDirectoryPolicy
{
    public static bool TryResolve(
        string? configuredPath,
        string defaultRoot,
        bool verifyWritable,
        out string resolvedPath,
        out string errorMessage)
    {
        resolvedPath = Path.GetFullPath(defaultRoot);
        errorMessage = string.Empty;
        if (string.IsNullOrWhiteSpace(configuredPath)) return true;

        try
        {
            if (configuredPath.Any(char.IsControl))
            {
                errorMessage = "数据目录包含无效字符，请重新选择文件夹。";
                return false;
            }
            var expanded = Environment.ExpandEnvironmentVariables(configuredPath.Trim());
            if (expanded.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || expanded.Any(char.IsControl))
            {
                errorMessage = "数据目录包含无效字符，请重新选择文件夹。";
                return false;
            }
            var candidate = Path.GetFullPath(expanded);
            if (File.Exists(candidate))
            {
                errorMessage = "数据目录不能指向文件，请选择文件夹。";
                return false;
            }

            if (verifyWritable)
            {
                Directory.CreateDirectory(candidate);
                var probe = Path.Combine(candidate, ".vesper-write-probe-" + Guid.NewGuid().ToString("N"));
                using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            }

            resolvedPath = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            errorMessage = "数据目录不可用或不可写，请选择本机可访问的文件夹。";
            return false;
        }
    }

    public static string ResolveOrDefault(string? configuredPath, string defaultRoot)
        => TryResolve(configuredPath, defaultRoot, verifyWritable: false, out var resolved, out _)
            ? resolved
            : Path.GetFullPath(defaultRoot);
}
