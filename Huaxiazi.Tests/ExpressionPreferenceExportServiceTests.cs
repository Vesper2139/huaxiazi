using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Huaxiazi.Models;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class ExpressionPreferenceExportServiceTests
{
    [Fact]
    public void Export_WritesOnlyPreferenceProfileAndMetadata()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "PreferenceExport_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "preferences.json");
        var profile = new ExpressionPreferenceProfile
        {
            AcceptedCount = 4,
            TaskPreferences = new Dictionary<string, ExpressionPreferenceSet>
            {
                ["polish|work"] = new() { PreferredTone = "professional", UserConfirmed = true, ForbiddenExpressions = ["套话"] }
            },
            InteractionSignals = new Dictionary<string, ExpressionInteractionSignalSet>
            {
                ["polish|work"] = new()
                {
                    AcceptedCount = 4,
                    EditCount = 2,
                    AcceptedRemovedCannedExpressions = new Dictionary<string, int> { ["首先"] = 3 },
                    RejectedRemovedCannedExpressions = new Dictionary<string, int> { ["其次"] = 1 }
                }
            }
        };

        try
        {
            new ExpressionPreferenceExportService().Export(
                profile,
                destination,
                "正式",
                new Dictionary<string, string>
                {
                    ["Polish|职场沟通"] = "克制",
                    ["Unexpected|bad"] = "不支持风格"
                });

            using var json = JsonDocument.Parse(File.ReadAllText(destination));
            var root = json.RootElement;
            Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
            Assert.True(root.GetProperty("exportedAtUtc").GetDateTimeOffset() <= DateTimeOffset.UtcNow);
            Assert.Equal(4, root.GetProperty("profile").GetProperty("acceptedCount").GetInt32());
            var signals = root.GetProperty("profile").GetProperty("interactionSignals").GetProperty("polish|work");
            Assert.Equal(3, signals.GetProperty("acceptedRemovedCannedExpressions").GetProperty("首先").GetInt32());
            Assert.Equal(1, signals.GetProperty("rejectedRemovedCannedExpressions").GetProperty("其次").GetInt32());
            Assert.Equal("套话", root.GetProperty("profile").GetProperty("taskPreferences").GetProperty("polish|work").GetProperty("forbiddenExpressions")[0].GetString());
            Assert.Equal("正式", root.GetProperty("outputStyles").GetProperty("global").GetString());
            Assert.Equal("克制", root.GetProperty("outputStyles").GetProperty("overrides").GetProperty("Polish|职场沟通").GetString());
            Assert.False(root.GetProperty("outputStyles").GetProperty("overrides").TryGetProperty("Unexpected|bad", out _));
            var exported = File.ReadAllText(destination);
            Assert.DoesNotContain("apiKey", exported, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("history", exported, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("providerProfiles", exported, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("incognitoMode", exported, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("shareConfirmedPreferencesWithCloud", exported, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }
    }
}
