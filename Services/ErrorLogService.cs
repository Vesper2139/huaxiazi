using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Huaxiazi.Services;

/// <summary>有界、节流的错误日志。避免同一 UI 故障在消息循环中无限放大日志。</summary>
public sealed class ErrorLogService
{
    private const int MaximumEntryCharacters = 64 * 1024;
    private static readonly TimeSpan RedactionTimeout = TimeSpan.FromMilliseconds(250);
    private readonly string _directory;
    private readonly long _maxBytes;
    private readonly TimeSpan _throttleWindow;
    private readonly int _maxArchiveFiles;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Dictionary<string, DateTimeOffset> _lastWritten = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public ErrorLogService(
        string directory,
        long maxBytes = 5 * 1024 * 1024,
        int maxArchiveFiles = 5,
        TimeSpan? throttleWindow = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (maxArchiveFiles < 1) throw new ArgumentOutOfRangeException(nameof(maxArchiveFiles));
        _directory = Path.GetFullPath(directory);
        _maxBytes = maxBytes;
        _maxArchiveFiles = maxArchiveFiles;
        _throttleWindow = throttleWindow ?? TimeSpan.FromSeconds(60);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public bool Write(string source, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        source = string.IsNullOrWhiteSpace(source) ? "Unknown" : source.Trim();
        var now = _utcNow();
        var signature = $"{source}|{exception.GetType().FullName}|{RedactSensitiveData(exception.Message)}";
        lock (_gate)
        {
            if (_lastWritten.TryGetValue(signature, out var previous) && now - previous < _throttleWindow) return false;
            _lastWritten[signature] = now;
            Directory.CreateDirectory(_directory);
            var activePath = Path.Combine(_directory, "errors.log");
            RotateIfNeeded(activePath, now);
            PruneArchives();
            var details = RedactSensitiveData(exception.ToString());
            if (details.Length > MaximumEntryCharacters) details = details[..MaximumEntryCharacters] + "…[truncated]";
            File.AppendAllText(activePath, $"{now:O} [{source}] {details}{Environment.NewLine}", new UTF8Encoding(false));
            return true;
        }
    }

    internal static string RedactSensitiveData(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var redacted = Regex.Replace(value,
            @"(?i)\bauthorization\s*:\s*bearer\s+[^\s&;,]+",
            "Authorization: Bearer [REDACTED]", RegexOptions.CultureInvariant, RedactionTimeout);
        redacted = Regex.Replace(redacted,
            @"(?i)\b(api[_-]?key|access[_-]?token|refresh[_-]?token|token|secret|password)\s*[:=]\s*([^\s&;,]+)",
            "$1=[REDACTED]", RegexOptions.CultureInvariant, RedactionTimeout);
        redacted = Regex.Replace(redacted,
            @"(?i)([?&](?:api[_-]?key|access[_-]?token|refresh[_-]?token|token|secret|password)=)[^&#\s]+",
            "$1[REDACTED]", RegexOptions.CultureInvariant, RedactionTimeout);
        redacted = Regex.Replace(redacted,
            @"(?i)\b(?:x-api-key|x-goog-api-key|api-key)\s*:\s*[^\s&;,]+",
            "[REDACTED-API-KEY]", RegexOptions.CultureInvariant, RedactionTimeout);
        redacted = Regex.Replace(redacted,
            @"(?i)([""](?:key|client_secret)[""]\s*:\s*[""])[^""]*([""])" ,
            "$1[REDACTED]$2", RegexOptions.CultureInvariant, RedactionTimeout);
        return redacted.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    public void Prepare()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            RotateIfNeeded(Path.Combine(_directory, "errors.log"), _utcNow());
            PruneArchives();
        }
    }

    private void RotateIfNeeded(string activePath, DateTimeOffset now)
    {
        if (!File.Exists(activePath) || new FileInfo(activePath).Length < _maxBytes) return;
        var stem = $"errors-{now:yyyyMMddHHmmss}";
        var rotatedPath = Path.Combine(_directory, stem + ".log");
        for (var suffix = 2; File.Exists(rotatedPath); suffix++)
            rotatedPath = Path.Combine(_directory, $"{stem}-{suffix}.log");
        File.Move(activePath, rotatedPath);
    }

    private void PruneArchives()
    {
        var archives = new DirectoryInfo(_directory).GetFiles("errors-*.log")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.Ordinal)
            .Skip(_maxArchiveFiles);
        foreach (var archive in archives)
        {
            try { archive.Delete(); }
            catch { }
        }
    }
}
