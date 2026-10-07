using System;
using System.IO;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

/// <summary>Exports the user-controlled preference profile without exporting app settings, credentials, or history.</summary>
public sealed class ExpressionPreferenceExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public void Export(
        ExpressionPreferenceProfile profile,
        string destinationPath,
        string? globalOutputStyle = null,
        IReadOnlyDictionary<string, string>? outputStyleOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentException("导出路径不能为空。", nameof(destinationPath));

        var fullPath = Path.GetFullPath(destinationPath);
        var temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var document = new ExportDocument
        {
            ExportedAtUtc = DateTimeOffset.UtcNow,
            Profile = profile,
            OutputStyles = new ExportOutputStyles
            {
                Global = OutputStyleCatalog.Normalize(globalOutputStyle),
                Overrides = OutputStylePreferenceResolver.NormalizeOverrides(outputStyleOverrides)
            }
        };

        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temporaryPath, fullPath, true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }

    private sealed class ExportDocument
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; } = 2;

        [JsonPropertyName("exportedAtUtc")]
        public DateTimeOffset ExportedAtUtc { get; init; }

        [JsonPropertyName("profile")]
        public required ExpressionPreferenceProfile Profile { get; init; }

        [JsonPropertyName("outputStyles")]
        public required ExportOutputStyles OutputStyles { get; init; }
    }

    private sealed class ExportOutputStyles
    {
        [JsonPropertyName("global")]
        public string Global { get; init; } = "自然";

        [JsonPropertyName("overrides")]
        public Dictionary<string, string> Overrides { get; init; } = new(StringComparer.Ordinal);
    }
}
