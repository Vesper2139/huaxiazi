using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class UpdateDownloadServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HuaxiaziUpdate_" + Guid.NewGuid().ToString("N"));

    private sealed class Handler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });
        }
    }

    [Fact]
    public async Task DownloadAsync_HttpUrl_ReturnsError()
    {
        using var client = new HttpClient();
        var service = new UpdateDownloadService(client);
        var target = Path.Combine(_root, "HuaxiaziSetup.exe");

        var result = await service.DownloadAsync(
            "http://example.com/HuaxiaziSetup.exe",
            new string('0', 64),
            target);

        Assert.Equal(UpdateDownloadStatus.Error, result.Status);
        Assert.Contains("HTTPS", result.Message);
    }

    [Fact]
    public async Task DownloadAsync_InvalidUrl_ReturnsError()
    {
        using var client = new HttpClient();
        var service = new UpdateDownloadService(client);
        var target = Path.Combine(_root, "HuaxiaziSetup.exe");

        var result = await service.DownloadAsync(
            "not-a-url",
            new string('0', 64),
            target);

        Assert.Equal(UpdateDownloadStatus.Error, result.Status);
    }

    [Fact]
    public async Task DownloadAsync_ValidHash_AtomicallyWritesInstaller()
    {
        var payload = Encoding.UTF8.GetBytes("signed installer bytes");
        using var client = new HttpClient(new Handler(payload));
        var service = new UpdateDownloadService(client, new StubAuthenticodeVerifier(true));
        var target = Path.Combine(_root, "HuaxiaziSetup.exe");

        var result = await service.DownloadAsync(
            "https://example.com/HuaxiaziSetup.exe",
            Convert.ToHexString(SHA256.HashData(payload)),
            target);

        Assert.Equal(UpdateDownloadStatus.Success, result.Status);
        Assert.Equal(payload, File.ReadAllBytes(target));
        Assert.False(File.Exists(target + ".download"));
    }

    [Fact]
    public async Task DownloadAsync_RejectsUnsignedInstallerEvenWhenHashMatches()
    {
        var payload = Encoding.UTF8.GetBytes("unsigned installer bytes");
        using var client = new HttpClient(new Handler(payload));
        var service = new UpdateDownloadService(client, new StubAuthenticodeVerifier(false));
        var target = Path.Combine(_root, "HuaxiaziSetup.exe");

        var result = await service.DownloadAsync(
            "https://example.com/HuaxiaziSetup.exe",
            Convert.ToHexString(SHA256.HashData(payload)),
            target);

        Assert.Equal(UpdateDownloadStatus.SignatureInvalid, result.Status);
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".download"));
    }

    private sealed class StubAuthenticodeVerifier(bool isValid) : IAuthenticodeVerifier
    {
        public bool Verify(string filePath, out string? publisher)
        {
            publisher = isValid ? "Huaxiazi" : null;
            return isValid;
        }
    }

    [Fact]
    public async Task DownloadAsync_HashMismatch_RemovesTemporaryFileAndDoesNotReplaceTarget()
    {
        var payload = Encoding.UTF8.GetBytes("tampered bytes");
        using var client = new HttpClient(new Handler(payload));
        var service = new UpdateDownloadService(client);
        var target = Path.Combine(_root, "HuaxiaziSetup.exe");

        var result = await service.DownloadAsync(
            "https://example.com/HuaxiaziSetup.exe",
            new string('0', 64),
            target);

        Assert.Equal(UpdateDownloadStatus.HashMismatch, result.Status);
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".download"));
    }

    [Fact]
    public async Task DownloadAsync_Cancelled_DoesNotLeavePartialFile()
    {
        using var client = new HttpClient(new Handler([1, 2, 3]));
        var service = new UpdateDownloadService(client);
        var target = Path.Combine(_root, "HuaxiaziSetup.exe");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await service.DownloadAsync(
            "https://example.com/HuaxiaziSetup.exe",
            new string('0', 64),
            target,
            cancellationToken: cts.Token);

        Assert.Equal(UpdateDownloadStatus.Cancelled, result.Status);
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".download"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
