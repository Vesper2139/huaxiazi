using System.IO.Compression;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class LocalRuntimePackageServiceTests
{
    [Fact]
    public void Catalog_ReportsVerifiedCompilerSourceSeparatelyFromPublisherCommit()
    {
        var packages = LocalRuntimePackageCatalog.Packages;

        Assert.Equal(2, packages.Count);
        Assert.All(packages, package =>
        {
            Assert.Equal("6c59c4007", package.ReportedBuildCommit);
            Assert.Equal("6c59c40076c00eab49754dc955d7652d93f9e125", package.CompilerSourceCommit);
            Assert.Equal("c06f84160a30c66d7b5a2829ae9b3ea15275cbc3", package.AttestationSourceCommit);
            Assert.True(package.IsPrerelease);
            Assert.True(package.CompilerSourceVerified);
        });
    }

    [Fact]
    public async Task InstallAsync_VerifiesExtractsAndActivatesVersionedRuntime()
    {
        var root = CreateTestRoot();
        try
        {
            var zip = CreateZip(malicious: false);
            var descriptor = Descriptor(zip) with
            {
                CompilerSourceCommit = "build-source-full-sha",
                CompilerSourceVerified = true
            };
            var license = Path.Combine(root, "llama-license.txt");
            await File.WriteAllTextAsync(license, "MIT test license");
            using var client = new HttpClient(new StaticHandler(zip));
            using var service = new LocalRuntimePackageService(Path.Combine(root, "runtime"), license, client, [descriptor], AcceptExecutableProbe);

            await service.InstallAsync(descriptor);

            var executable = Path.Combine(root, "runtime", "cpu", "versions", "b-test", "llama-server.exe");
            Assert.True(File.Exists(executable));
            Assert.Equal(executable, LocalRuntimePackageService.ResolveInstalledExecutable(Path.Combine(root, "runtime"), LocalRuntimeFlavor.Cpu));
            Assert.Contains("MIT test license", await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(executable)!, "LICENSE-llama.cpp")));
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(executable)!, "huaxiazi-runtime.json")));
            Assert.True(manifest.RootElement.GetProperty("CompilerSourceVerified").GetBoolean());
            Assert.Equal("build-source-full-sha", manifest.RootElement.GetProperty("CompilerSourceCommit").GetString());
        }
        finally { DeleteTestRoot(root); }
    }

    [Fact]
    public async Task InstallAsync_CopiesAdditionalThirdPartyLicensesBesideRuntime()
    {
        var root = CreateTestRoot();
        try
        {
            var zip = CreateZip(malicious: false);
            var descriptor = Descriptor(zip);
            var primaryLicense = Path.Combine(root, "llama-license.txt");
            var thirdPartyLicense = Path.Combine(root, "vulkan-headers-license.txt");
            var uiNotices = Path.Combine(root, "llama-ui-notices.txt");
            await File.WriteAllTextAsync(primaryLicense, "llama MIT");
            await File.WriteAllTextAsync(thirdPartyLicense, "Khronos Vulkan-Headers license");
            await File.WriteAllTextAsync(uiNotices, "llama.cpp UI third-party notices");
            using var client = new HttpClient(new StaticHandler(zip));
            using var service = new LocalRuntimePackageService(Path.Combine(root, "runtime"), primaryLicense,
                client, [descriptor], AcceptExecutableProbe,
                [
                    (thirdPartyLicense, "LICENSE-Vulkan-Headers.txt"),
                    (uiNotices, "THIRD-PARTY-NOTICES-llama-ui.txt")
                ]);

            await service.InstallAsync(descriptor);

            var installedDirectory = Path.Combine(root, "runtime", "cpu", "versions", "b-test");
            Assert.Equal("llama MIT", await File.ReadAllTextAsync(Path.Combine(installedDirectory, "LICENSE-llama.cpp")));
            Assert.Equal("Khronos Vulkan-Headers license",
                await File.ReadAllTextAsync(Path.Combine(installedDirectory, "LICENSE-Vulkan-Headers.txt")));
            Assert.Equal("llama.cpp UI third-party notices",
                await File.ReadAllTextAsync(Path.Combine(installedDirectory, "THIRD-PARTY-NOTICES-llama-ui.txt")));
        }
        finally { DeleteTestRoot(root); }
    }

    [Fact]
    public async Task InstallAsync_RejectsHashMismatchAndDoesNotActivatePackage()
    {
        var root = CreateTestRoot();
        try
        {
            var zip = CreateZip(malicious: false);
            var descriptor = Descriptor(zip) with { Sha256 = new string('0', 64) };
            var license = Path.Combine(root, "license.txt");
            await File.WriteAllTextAsync(license, "license");
            using var client = new HttpClient(new StaticHandler(zip));
            using var service = new LocalRuntimePackageService(Path.Combine(root, "runtime"), license, client, [descriptor], AcceptExecutableProbe);

            var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallAsync(descriptor));

            Assert.Contains("SHA-256", error.Message);
            Assert.Null(LocalRuntimePackageService.ResolveInstalledExecutable(Path.Combine(root, "runtime"), LocalRuntimeFlavor.Cpu));
        }
        finally { DeleteTestRoot(root); }
    }

    [Fact]
    public async Task InstallAsync_RejectsZipTraversalWithoutWritingOutsideStagingDirectory()
    {
        var root = CreateTestRoot();
        try
        {
            var zip = CreateZip(malicious: true);
            var descriptor = Descriptor(zip);
            var license = Path.Combine(root, "license.txt");
            await File.WriteAllTextAsync(license, "license");
            using var client = new HttpClient(new StaticHandler(zip));
            using var service = new LocalRuntimePackageService(Path.Combine(root, "runtime"), license, client, [descriptor], AcceptExecutableProbe);

            await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallAsync(descriptor));

            Assert.False(File.Exists(Path.Combine(root, "runtime", "cpu", "escape.txt")));
            Assert.Null(LocalRuntimePackageService.ResolveInstalledExecutable(Path.Combine(root, "runtime"), LocalRuntimeFlavor.Cpu));
        }
        finally { DeleteTestRoot(root); }
    }

    [Fact]
    public async Task InstallAsync_ResumesAnInterruptedDownloadWithRangeRequest()
    {
        var root = CreateTestRoot();
        try
        {
            var zip = CreateZip(malicious: false);
            var descriptor = Descriptor(zip);
            var license = Path.Combine(root, "license.txt");
            await File.WriteAllTextAsync(license, "license");
            var handler = new InterruptedThenResumeHandler(zip);
            using var client = new HttpClient(handler);
            using var service = new LocalRuntimePackageService(Path.Combine(root, "runtime"), license, client, [descriptor], AcceptExecutableProbe);

            await Assert.ThrowsAsync<IOException>(() => service.InstallAsync(descriptor));
            await service.InstallAsync(descriptor);

            Assert.True(handler.SawRangeRequest);
            Assert.NotNull(LocalRuntimePackageService.ResolveInstalledExecutable(Path.Combine(root, "runtime"), LocalRuntimeFlavor.Cpu));
        }
        finally { DeleteTestRoot(root); }
    }

    [Fact]
    public async Task InstallAsync_RestartsFromZeroWhenResumeRequestReturns416()
    {
        var root = CreateTestRoot();
        try
        {
            var zip = CreateZip(malicious: false);
            var descriptor = Descriptor(zip);
            var license = Path.Combine(root, "license.txt");
            await File.WriteAllTextAsync(license, "license");
            var handler = new RangeRejectedThenFullResponseHandler(zip);
            using var client = new HttpClient(handler);
            using var service = new LocalRuntimePackageService(Path.Combine(root, "runtime"), license, client, [descriptor], AcceptExecutableProbe);

            await Assert.ThrowsAsync<IOException>(() => service.InstallAsync(descriptor));
            await service.InstallAsync(descriptor);

            Assert.Equal(1, handler.RangeRequests);
            Assert.Equal(2, handler.FullRequests);
            Assert.NotNull(LocalRuntimePackageService.ResolveInstalledExecutable(Path.Combine(root, "runtime"), LocalRuntimeFlavor.Cpu));
        }
        finally { DeleteTestRoot(root); }
    }

    [Fact]
    public async Task InstallAsync_RejectsPartialResponseWithWrongContentRangeStart()
    {
        var root = CreateTestRoot();
        try
        {
            var zip = CreateZip(malicious: false);
            var descriptor = Descriptor(zip);
            var license = Path.Combine(root, "license.txt");
            await File.WriteAllTextAsync(license, "license");
            var handler = new WrongRangeStartHandler(zip);
            using var client = new HttpClient(handler);
            using var service = new LocalRuntimePackageService(Path.Combine(root, "runtime"), license, client, [descriptor], AcceptExecutableProbe);

            await Assert.ThrowsAsync<IOException>(() => service.InstallAsync(descriptor));
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallAsync(descriptor));

            Assert.Contains("续传", error.Message);
            Assert.Null(LocalRuntimePackageService.ResolveInstalledExecutable(Path.Combine(root, "runtime"), LocalRuntimeFlavor.Cpu));
        }
        finally { DeleteTestRoot(root); }
    }

    [Fact]
    public async Task InstallAsync_RetriesOnceAfterTransientSecureConnectionFailure()
    {
        var root = CreateTestRoot();
        try
        {
            var zip = CreateZip(malicious: false);
            var descriptor = Descriptor(zip);
            var license = Path.Combine(root, "license.txt");
            await File.WriteAllTextAsync(license, "license");
            var handler = new TransientSecureConnectionFailureHandler(zip);
            using var client = new HttpClient(handler);
            using var service = new LocalRuntimePackageService(Path.Combine(root, "runtime"), license, client, [descriptor], AcceptExecutableProbe);

            await service.InstallAsync(descriptor);

            Assert.Equal(2, handler.Attempts);
            Assert.NotNull(LocalRuntimePackageService.ResolveInstalledExecutable(Path.Combine(root, "runtime"), LocalRuntimeFlavor.Cpu));
        }
        finally { DeleteTestRoot(root); }
    }

    [Fact]
    public async Task InstallAsync_DoesNotRetryHttpForbiddenResponse()
    {
        var root = CreateTestRoot();
        try
        {
            var zip = CreateZip(malicious: false);
            var descriptor = Descriptor(zip);
            var license = Path.Combine(root, "license.txt");
            await File.WriteAllTextAsync(license, "license");
            var handler = new HttpStatusHandler(HttpStatusCode.Forbidden);
            using var client = new HttpClient(handler);
            using var service = new LocalRuntimePackageService(Path.Combine(root, "runtime"), license, client, [descriptor], AcceptExecutableProbe);

            await Assert.ThrowsAsync<HttpRequestException>(() => service.InstallAsync(descriptor));

            Assert.Equal(1, handler.Attempts);
        }
        finally { DeleteTestRoot(root); }
    }

    [Fact]
    public async Task InstallAsync_StopsAfterOneTransientSendRetry()
    {
        var root = CreateTestRoot();
        try
        {
            var zip = CreateZip(malicious: false);
            var descriptor = Descriptor(zip);
            var license = Path.Combine(root, "license.txt");
            await File.WriteAllTextAsync(license, "license");
            var handler = new AlwaysFailingTransientSendHandler();
            using var client = new HttpClient(handler);
            using var service = new LocalRuntimePackageService(Path.Combine(root, "runtime"), license, client, [descriptor], AcceptExecutableProbe);

            await Assert.ThrowsAsync<HttpRequestException>(() => service.InstallAsync(descriptor));

            Assert.Equal(2, handler.Attempts);
            Assert.Null(LocalRuntimePackageService.ResolveInstalledExecutable(Path.Combine(root, "runtime"), LocalRuntimeFlavor.Cpu));
        }
        finally { DeleteTestRoot(root); }
    }

    [Fact]
    public async Task InstallAsync_DoesNotActivatePackageWhenExecutableProbeFails()
    {
        var root = CreateTestRoot();
        try
        {
            var zip = CreateZip(malicious: false);
            var descriptor = Descriptor(zip);
            var license = Path.Combine(root, "license.txt");
            await File.WriteAllTextAsync(license, "license");
            using var client = new HttpClient(new StaticHandler(zip));
            using var service = new LocalRuntimePackageService(Path.Combine(root, "runtime"), license, client, [descriptor],
                (_, _, _) => throw new InvalidDataException("version mismatch"));

            await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallAsync(descriptor));

            Assert.Null(LocalRuntimePackageService.ResolveInstalledExecutable(Path.Combine(root, "runtime"), LocalRuntimeFlavor.Cpu));
        }
        finally { DeleteTestRoot(root); }
    }

    private static LocalRuntimePackageDescriptor Descriptor(byte[] zip) => new(
        "b-test", LocalRuntimeFlavor.Cpu, new Uri("https://github.com/test/runtime.zip"), zip.Length,
        ExpandedBytes, Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant(), "reported", "attested");

    private static Task AcceptExecutableProbe(string path, string commit, CancellationToken token) => Task.CompletedTask;

    private const int ExpandedBytes = 2 + 3 + 4 + 5 + 7;

    private static byte[] CreateZip(bool malicious)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (malicious) Add(archive, "../escape.txt", "escape");
            Add(archive, "llama-server.exe", "aa");
            Add(archive, "llama-server-impl.dll", "bbb");
            Add(archive, "llama.dll", "cccc");
            Add(archive, "ggml-base.dll", "ddddd");
            Add(archive, "LICENSE-LLVM-OpenMP", "1234567");
        }
        return memory.ToArray();
    }

    private static void Add(ZipArchive archive, string name, string content)
    {
        using var output = archive.CreateEntry(name).Open();
        using var writer = new StreamWriter(output);
        writer.Write(content);
    }

    private static string CreateTestRoot()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "test-data", "LocalRuntimePackage", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTestRoot(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private sealed class StaticHandler(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("github.com", request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(content) });
        }
    }

    private sealed class InterruptedThenResumeHandler(byte[] content) : HttpMessageHandler
    {
        private bool _first = true;
        public bool SawRangeRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("github.com", request.RequestUri!.Host);
            if (_first)
            {
                _first = false;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent(content[..(content.Length / 2)]) });
            }
            Assert.NotNull(request.Headers.Range);
            SawRangeRequest = true;
            var offset = checked((int)request.Headers.Range!.Ranges.Single().From!.Value);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            { Content = new ByteArrayContent(content[offset..]) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, content.Length - 1, content.Length);
            return Task.FromResult(response);
        }
    }

    private sealed class RangeRejectedThenFullResponseHandler(byte[] content) : HttpMessageHandler
    {
        private bool _first = true;
        public int RangeRequests { get; private set; }
        public int FullRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("github.com", request.RequestUri!.Host);
            if (request.Headers.Range is not null)
            {
                RangeRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
            }

            FullRequests++;
            if (_first)
            {
                _first = false;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent(content[..(content.Length / 2)]) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(content) });
        }
    }

    private sealed class WrongRangeStartHandler(byte[] content) : HttpMessageHandler
    {
        private bool _first = true;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("github.com", request.RequestUri!.Host);
            if (_first)
            {
                _first = false;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent(content[..(content.Length / 2)]) });
            }

            var offset = checked((int)request.Headers.Range!.Ranges.Single().From!.Value);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            { Content = new ByteArrayContent(content[offset..]) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, content.Length - offset - 1, content.Length);
            return Task.FromResult(response);
        }
    }

    private sealed class TransientSecureConnectionFailureHandler(byte[] content) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            if (Attempts == 1)
                return Task.FromException<HttpResponseMessage>(new HttpRequestException(
                    HttpRequestError.SecureConnectionError, "TLS connection ended unexpectedly.", new IOException("Unexpected EOF.")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
        }
    }

    private sealed class AlwaysFailingTransientSendHandler : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromException<HttpResponseMessage>(new HttpRequestException(
                HttpRequestError.SecureConnectionError, "TLS connection ended unexpectedly.", new IOException("Unexpected EOF.")));
        }
    }

    private sealed class HttpStatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
