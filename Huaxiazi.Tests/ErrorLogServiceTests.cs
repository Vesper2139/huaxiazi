using System;
using System.IO;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class ErrorLogServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HuaxiaziErrors_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Write_RepeatedSignatureWithinThrottleWindow_AppendsOnlyOnce()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var service = new ErrorLogService(_root, maxBytes: 1024, throttleWindow: TimeSpan.FromSeconds(60), utcNow: () => now);

        service.Write("UI", new InvalidOperationException("同一故障"));
        service.Write("UI", new InvalidOperationException("同一故障"));

        var text = File.ReadAllText(Path.Combine(_root, "errors.log"));
        Assert.Equal(1, CountOccurrences(text, "同一故障"));
    }

    [Fact]
    public void Write_WhenActiveLogExceedsLimit_RotatesBeforeAppending()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "errors.log"), new string('x', 101));
        var service = new ErrorLogService(
            _root,
            maxBytes: 100,
            throttleWindow: TimeSpan.FromSeconds(60),
            utcNow: () => new DateTimeOffset(2026, 8, 14, 12, 34, 56, TimeSpan.Zero));

        service.Write("UI", new InvalidOperationException("轮转后的新错误"));

        Assert.Contains("轮转后的新错误", File.ReadAllText(Path.Combine(_root, "errors.log")));
        var rotated = Assert.Single(Directory.GetFiles(_root, "errors-20260814123456*.log"));
        Assert.Equal(101, new FileInfo(rotated).Length);
    }

    [Fact]
    public void Prepare_WhenPreviousRunLeftOversizedLog_RotatesWithoutWaitingForAnotherFailure()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "errors.log"), new string('x', 101));
        var service = new ErrorLogService(
            _root,
            maxBytes: 100,
            utcNow: () => new DateTimeOffset(2026, 8, 14, 13, 0, 0, TimeSpan.Zero));

        service.Prepare();

        Assert.False(File.Exists(Path.Combine(_root, "errors.log")));
        Assert.Single(Directory.GetFiles(_root, "errors-20260814130000*.log"));
    }

    [Fact]
    public void Prepare_PrunesOldRotatedLogsToConfiguredRetentionLimit()
    {
        Directory.CreateDirectory(_root);
        for (var index = 1; index <= 4; index++)
        {
            var path = Path.Combine(_root, $"errors-2026080{index}120000.log");
            File.WriteAllText(path, index.ToString());
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 8, index, 12, 0, 0, DateTimeKind.Utc));
        }
        var service = new ErrorLogService(_root, maxArchiveFiles: 2);

        service.Prepare();

        var retained = Directory.GetFiles(_root, "errors-*.log").OrderBy(Path.GetFileName).ToArray();
        Assert.Equal(2, retained.Length);
        Assert.EndsWith("errors-20260803120000.log", retained[0]);
        Assert.EndsWith("errors-20260804120000.log", retained[1]);
    }

    [Fact]
    public void Write_ExceptionContainingCredentials_RedactsAllSensitiveValues()
    {
        var service = new ErrorLogService(_root);
        var exception = new InvalidOperationException(
            "api_key=sk-api-secret token: token-secret Authorization: Bearer bearer-secret " +
            "https://example.test/callback?access_token=query-secret&safe=value");

        service.Write("Provider", exception);

        var text = File.ReadAllText(Path.Combine(_root, "errors.log"));
        Assert.DoesNotContain("sk-api-secret", text);
        Assert.DoesNotContain("token-secret", text);
        Assert.DoesNotContain("bearer-secret", text);
        Assert.DoesNotContain("query-secret", text);
        Assert.Contains("[REDACTED]", text);
        Assert.Contains("safe=value", text);
    }

    private static int CountOccurrences(string text, string value) =>
        (text.Length - text.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }
}
