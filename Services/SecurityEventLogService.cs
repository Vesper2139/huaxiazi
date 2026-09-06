using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Huaxiazi.Services;

/// <summary>Minimal local security audit trail with a tamper-evident hash chain.</summary>
public sealed class SecurityEventLogService
{
    private readonly string _path;
    private readonly object _gate = new();

    public SecurityEventLogService(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _path = Path.Combine(Path.GetFullPath(dataRoot), "security.log");
    }

    public void Append(string eventType, string subjectId, string detail)
    {
        eventType = Sanitize(eventType, 64);
        subjectId = Sanitize(subjectId, 128);
        detail = Sanitize(detail, 512);
        var timestamp = DateTimeOffset.UtcNow.ToString("O");
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var previous = ReadLastHash();
            var canonical = string.Join('|', timestamp, eventType, subjectId, detail, previous);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
            var record = new AuditRecord(timestamp, eventType, subjectId, detail, previous, hash,
                OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().User?.Value ?? "unknown" : "local-user");
            File.AppendAllText(_path, JsonSerializer.Serialize(record) + Environment.NewLine, new UTF8Encoding(false));
        }
    }

    public bool Validate(out string error)
    {
        error = string.Empty;
        if (!File.Exists(_path)) return true;
        lock (_gate)
        {
            var previous = string.Empty;
            try
            {
                foreach (var line in File.ReadLines(_path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var record = JsonSerializer.Deserialize<AuditRecord>(line)
                        ?? throw new InvalidDataException("安全审计记录为空。");
                    var canonical = string.Join('|', record.Timestamp, record.EventType, record.SubjectId, record.Detail, record.PreviousHash);
                    var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
                    if (!string.Equals(record.PreviousHash, previous, StringComparison.Ordinal) ||
                        !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(record.Hash), Encoding.UTF8.GetBytes(expected)))
                        throw new InvalidDataException("安全审计日志完整性校验失败。");
                    previous = record.Hash;
                }
                return true;
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or FormatException)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    private string ReadLastHash()
    {
        if (!File.Exists(_path)) return string.Empty;
        var line = File.ReadLines(_path).LastOrDefault(item => !string.IsNullOrWhiteSpace(item));
        if (string.IsNullOrWhiteSpace(line)) return string.Empty;
        try { return JsonSerializer.Deserialize<AuditRecord>(line)?.Hash ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static string Sanitize(string value, int maximum)
    {
        value = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= maximum ? value : value[..maximum];
    }

    private sealed record AuditRecord(string Timestamp, string EventType, string SubjectId, string Detail,
        string PreviousHash, string Hash, string Actor);
}
