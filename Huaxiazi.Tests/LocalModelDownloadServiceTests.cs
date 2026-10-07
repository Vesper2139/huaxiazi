using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Huaxiazi.Models;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class LocalModelDownloadServiceTests
{
    [Fact]
    public async Task DownloadAsync_InterruptedTransferResumesWithRangeAndRegistersVerifiedModel()
    {
        var root = CreateTempDirectory();
        try
        {
            var bytes = Encoding.UTF8.GetBytes("GGUF-this-is-a-model-payload");
            const int prefixLength = 9;
            var attempts = 0;
            var handler = new DelegateHandler(request =>
            {
                attempts++;
                if (attempts == 1)
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StreamContent(new PrefixThenThrowStream(bytes[..prefixLength]))
                    };
                    response.Content.Headers.ContentLength = bytes.Length;
                    response.Headers.ETag = new EntityTagHeaderValue("\"model-v1\"");
                    return response;
                }

                Assert.Equal(prefixLength, request.Headers.Range?.Ranges.Single().From);
                Assert.Equal("\"model-v1\"", request.Headers.IfRange?.EntityTag?.Tag);
                var resumed = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(bytes[prefixLength..])
                };
                resumed.Content.Headers.ContentRange = new ContentRangeHeaderValue(prefixLength, bytes.Length - 1, bytes.Length);
                resumed.Headers.ETag = new EntityTagHeaderValue("\"model-v1\"");
                return resumed;
            });
            var store = new LocalModelStore(Path.Combine(root, "managed"));
            var service = new LocalModelDownloadService(store, new HttpClient(handler));
            var descriptor = Descriptor(bytes);

            var interrupted = await service.DownloadAsync(descriptor);
            var completed = await service.DownloadAsync(descriptor);

            Assert.Equal(LocalModelDownloadStatus.Error, interrupted.Status);
            Assert.Equal(LocalModelDownloadStatus.Success, completed.Status);
            var installed = Assert.Single(store.GetInstalledModels());
            Assert.Equal(descriptor.Sha256, installed.Sha256);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(installed.FilePath));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DownloadAsync_HashMismatchDeletesUntrustedArtifactAndDoesNotRegisterModel()
    {
        var root = CreateTempDirectory();
        try
        {
            var bytes = Encoding.UTF8.GetBytes("GGUF-corrupted");
            var handler = new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            });
            var store = new LocalModelStore(Path.Combine(root, "managed"));
            var service = new LocalModelDownloadService(store, new HttpClient(handler));
            var descriptor = Descriptor(bytes);
            descriptor.Sha256 = new string('a', 64);

            var result = await service.DownloadAsync(descriptor);

            Assert.Equal(LocalModelDownloadStatus.HashMismatch, result.Status);
            Assert.Empty(store.GetInstalledModels());
            Assert.Empty(Directory.Exists(Path.Combine(store.Root, "base"))
                ? Directory.EnumerateFiles(Path.Combine(store.Root, "base"), "*.gguf", SearchOption.AllDirectories)
                : []);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DownloadAsync_EnforcesConfiguredDeadlineWhenServerStalls()
    {
        var root = CreateTempDirectory();
        try
        {
            var handler = new StallingHandler(async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable");
            });
            var service = new LocalModelDownloadService(
                new LocalModelStore(Path.Combine(root, "managed")),
                new HttpClient(handler),
                TimeSpan.FromMilliseconds(50));

            var result = await service.DownloadAsync(Descriptor(Encoding.UTF8.GetBytes("GGUF-stalled")));

            Assert.Equal(LocalModelDownloadStatus.Cancelled, result.Status);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task HasExpectedSha256Async_DetectsPostHashArtifactTampering()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "model.gguf");
            var original = Encoding.UTF8.GetBytes("GGUF-original");
            await File.WriteAllBytesAsync(path, original);
            var expected = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();
            await File.WriteAllTextAsync(path, "GGUF-tampered");

            Assert.False(await LocalModelDownloadService.HasExpectedSha256Async(path, expected));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static LocalModelDescriptor Descriptor(byte[] bytes) => new()
    {
        Id = "qwen-test",
        Version = "1",
        DisplayName = "Qwen Test",
        Tier = "Light",
        FileName = "model.gguf",
        DownloadUrl = "https://models.example/model.gguf",
        SizeBytes = bytes.Length,
        Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
        ContextTokens = 4096,
        MinimumMemoryBytes = 1024,
        LicenseId = "Apache-2.0",
        LicenseUrl = "https://www.apache.org/licenses/LICENSE-2.0",
        RuntimeVersion = "test"
    };

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "HuaxiaziDownload_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }

    private sealed class StallingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class PrefixThenThrowStream(byte[] prefix) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => prefix.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (_position < prefix.Length)
            {
                var length = Math.Min(buffer.Length, prefix.Length - _position);
                prefix.AsMemory(_position, length).CopyTo(buffer);
                _position += length;
                return length;
            }
            throw new IOException("simulated interruption");
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
