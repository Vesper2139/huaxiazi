using System;
using System.IO;
using System.Linq;

namespace Huaxiazi.Services;

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
        var hasConfiguredPath = !string.IsNullOrWhiteSpace(configuredPath);
        if (!hasConfiguredPath && !verifyWritable) return true;

        try
        {
            if (hasConfiguredPath && configuredPath!.Any(char.IsControl))
            {
                errorMessage = "数据目录包含无效字符，请重新选择文件夹。";
                return false;
            }
            var expanded = hasConfiguredPath
                ? Environment.ExpandEnvironmentVariables(configuredPath!.Trim())
                : resolvedPath;
            if (hasConfiguredPath && (expanded.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || expanded.Any(char.IsControl)))
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
                var probe = Path.Combine(candidate, ".huaxiazi-write-probe-" + Guid.NewGuid().ToString("N"));
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

    public static string ResolveOrDefault(string? configuredPath, string defaultRoot, string? fallbackRoot = null)
    {
        if (TryResolve(configuredPath, defaultRoot, verifyWritable: true, out var configured, out _))
            return configured;

        if (TryResolve(null, defaultRoot, verifyWritable: true, out var primary, out _))
            return primary;

        if (!string.IsNullOrWhiteSpace(fallbackRoot) &&
            TryResolve(null, fallbackRoot, verifyWritable: true, out var fallback, out _))
            return fallback;

        // Preserve the historical path as the final deterministic fallback. The
        // health check will still report a useful error if even this path cannot
        // be opened, but a restricted session gets a chance to use fallbackRoot.
        return Path.GetFullPath(defaultRoot);
    }
}
