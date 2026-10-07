using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class LocalRuntimePrerequisiteServiceTests
{
    [Fact]
    public async Task EnsureReadyAsync_SkipsDownloadAndElevationWhenInstalledVersionIsCurrent()
    {
        var handler = new CountingHandler([1, 2, 3]);
        var launched = false;
        using var client = new HttpClient(handler);
        using var service = Create(client, () => LocalRuntimePrerequisiteService.RequiredVersion,
            _ => true, (_, _) => { launched = true; return Task.FromResult(0); });

        await service.EnsureReadyAsync();

        Assert.Equal(0, handler.Requests);
        Assert.False(launched);
    }

    [Fact]
    public async Task EnsureReadyAsync_VerifiesPinnedDownloadElevatesAndRechecksInstalledVersion()
    {
        var payload = new byte[] { 4, 5, 6, 7 };
        var handler = new CountingHandler(payload);
        var installedVersion = new Version(14, 40, 0, 0);
        ProcessStartInfoSnapshot? invocation = null;
        using var client = new HttpClient(handler);
        using var service = Create(client, () => installedVersion,
            path => File.ReadAllBytes(path).AsSpan().SequenceEqual(payload), (info, _) =>
            {
                invocation = new ProcessStartInfoSnapshot(info.FileName, info.Arguments, info.Verb, info.UseShellExecute);
                installedVersion = LocalRuntimePrerequisiteService.RequiredVersion;
                return Task.FromResult(0);
            }, Convert.ToHexString(SHA256.HashData(payload)));

        await service.EnsureReadyAsync();

        Assert.Equal(1, handler.Requests);
        Assert.NotNull(invocation);
        Assert.Equal("runas", invocation.Verb);
        Assert.True(invocation.UseShellExecute);
        Assert.Contains("/install /passive /norestart", invocation.Arguments);
    }

    [Fact]
    public async Task EnsureReadyAsync_RejectsHashOrSignatureFailureWithoutLaunchingInstaller()
    {
        foreach (var mode in new[] { "hash", "signature" })
        {
            var launched = false;
            using var client = new HttpClient(new CountingHandler([9, 8, 7]));
            using var service = Create(client, () => new Version(14, 0, 0, 0),
                _ => mode != "signature", (_, _) => { launched = true; return Task.FromResult(0); },
                mode == "hash" ? new string('0', 64) : Convert.ToHexString(SHA256.HashData([9, 8, 7])));

            await Assert.ThrowsAsync<InvalidDataException>(() => service.EnsureReadyAsync());
            Assert.False(launched);
        }
    }

    [Fact]
    public async Task EnsureReadyAsync_ExplainsUacDeclineAndNeverReportsSuccess()
    {
        using var client = new HttpClient(new CountingHandler([1]));
        using var service = Create(client, () => new Version(14, 0, 0, 0), _ => true,
            (_, _) => throw new System.ComponentModel.Win32Exception(1223), Convert.ToHexString(SHA256.HashData([1])));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureReadyAsync());

        Assert.Contains("UAC", error.Message);
        Assert.Contains(LocalRuntimePrerequisiteService.ManualInstallUri, error.Message);
    }

    [Fact]
    public async Task EnsureReadyAsync_InstallerFailureRequiresManualRecoveryAndRecheck()
    {
        var installerLaunched = false;
        using var client = new HttpClient(new CountingHandler([2]));
        using var service = Create(client, () => new Version(14, 0, 0, 0), _ => true,
            (_, _) => { installerLaunched = true; return Task.FromResult(5100); }, Convert.ToHexString(SHA256.HashData([2])));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureReadyAsync());

        Assert.True(installerLaunched);
        Assert.Contains("退出代码为 5100", error.Message);
        Assert.Contains("没有切换到云端", error.Message);
        Assert.Contains(LocalRuntimePrerequisiteService.ManualInstallUri, error.Message);
    }

    [Fact]
    public async Task EnsureReadyAsync_OfflineFailureShowsManualInstallLinkWithoutElevation()
    {
        var launched = false;
        using var client = new HttpClient(new OfflineHandler());
        using var service = Create(client, () => null, _ => true,
            (_, _) => { launched = true; return Task.FromResult(0); });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureReadyAsync());

        Assert.False(launched);
        Assert.Contains("检查网络后重试", error.Message);
        Assert.Contains(LocalRuntimePrerequisiteService.ManualInstallUri, error.Message);
    }

    [Fact]
    public async Task EnsureReadyAsync_PropagatesUserCancellationWithoutLaunchingInstaller()
    {
        var launched = false;
        using var client = new HttpClient(new CountingHandler([1]));
        using var service = Create(client, () => new Version(14, 0, 0, 0), _ => true,
            (_, _) => { launched = true; return Task.FromResult(0); });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.EnsureReadyAsync(cancellation.Token));

        Assert.False(launched);
    }

    private static LocalRuntimePrerequisiteService Create(HttpClient client, Func<Version?> installedVersion,
        Func<string, bool> signatureVerifier, Func<System.Diagnostics.ProcessStartInfo, CancellationToken, Task<int>> runner,
        string? expectedHash = null) =>
        new(client, installedVersion, signatureVerifier, runner,
            Path.Combine(AppContext.BaseDirectory, "test-data", "LocalRuntimePrerequisiteService", Guid.NewGuid().ToString("N")), expectedHash);

    private sealed record ProcessStartInfoSnapshot(string FileName, string Arguments, string Verb, bool UseShellExecute);

    private sealed class CountingHandler(byte[] payload) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("aka.ms", request.RequestUri!.Host);
            Assert.Equal("/vs/18/release/14.51.36247/VC_Redist.x64.exe", request.RequestUri.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        }
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("offline"));
    }
}
