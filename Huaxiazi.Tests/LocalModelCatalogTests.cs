using System;
using System.Net;
using System.Net.Http;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class LocalModelCatalogTests
{
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public int CallCount { get; private set; }
        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_responder(request));
        }
    }

    [Fact]
    public async Task FetchAsync_RequiresHttpsAndVerifiesSignedEnvelope()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("signed-envelope")
        });
        var service = new LocalModelCatalogService(_ => "{\"schemaVersion\":1,\"catalogVersion\":\"1\",\"models\":[],\"adapters\":[]}");

        var catalog = await service.FetchAsync("https://catalog.example/models.json", new HttpClient(handler));

        Assert.Empty(catalog.Models);
        Assert.Equal(1, handler.CallCount);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.FetchAsync("http://catalog.example/models.json", new HttpClient(handler)));
    }

    [Fact]
    public void DirectDownloadLink_OnlyAllowsHttps()
    {
        Assert.True(LocalModelCatalogService.IsSafeDirectDownloadLink("https://huggingface.co/model/file.gguf"));
        Assert.False(LocalModelCatalogService.IsSafeDirectDownloadLink("http://example.com/file.gguf"));
        Assert.False(LocalModelCatalogService.IsSafeDirectDownloadLink("file:///C:/model.gguf"));
    }

    [Fact]
    public void BuiltInCatalog_ContainsSignedMetadataForDirectDownloads()
    {
        var catalog = LocalModelCatalogService.BuiltInCatalog;

        Assert.Equal(4, catalog.Models.Count);
        Assert.All(catalog.Models, model =>
        {
            Assert.StartsWith("https://huggingface.co/", model.DownloadUrl, StringComparison.Ordinal);
            Assert.Equal(64, model.Sha256.Length);
            Assert.True(model.SizeBytes > 1_000_000_000);
        });
    }

    [Fact]
    public void BuiltInCatalog_ContainsPinnedOfficialQwen3FourBModel()
    {
        var model = Assert.Single(LocalModelCatalogService.BuiltInCatalog.Models, item => item.Id == "qwen3-4b-q4-k-m");

        Assert.Equal("2026.10", model.Version);
        Assert.Equal(2_497_280_256, model.SizeBytes);
        Assert.Equal("7485fe6f11af29433bc51cab58009521f205840f5b4ae3a32fa7f92e8534fdf5", model.Sha256);
        Assert.Equal(
            "https://huggingface.co/Qwen/Qwen3-4B-GGUF/resolve/bc640142c66e1fdd12af0bd68f40445458f3869b/Qwen3-4B-Q4_K_M.gguf?download=true",
            model.DownloadUrl);
        Assert.Equal("Apache-2.0", model.LicenseId);
        Assert.Contains("cas-bridge.xethub.hf.co", model.AllowedRedirectHosts);
    }

    [Fact]
    public void ParseVerifiedCatalog_ValidDescriptorIsAvailableForDownload()
    {
        var payload = """
            {
              "schemaVersion": 1,
              "catalogVersion": "2026.09",
              "models": [
                {
                  "id": "qwen3-4b-instruct-2507-q4",
                  "version": "2507",
                  "displayName": "Qwen3 4B Instruct 2507 Q4_K_M",
                  "tier": "Standard",
                  "fileName": "model.gguf",
                  "downloadUrl": "https://models.example/qwen3-4b.gguf",
                  "sizeBytes": 3000000000,
                  "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                  "contextTokens": 32768,
                  "minimumMemoryBytes": 8589934592,
                  "licenseId": "Apache-2.0",
                  "licenseUrl": "https://www.apache.org/licenses/LICENSE-2.0",
                  "runtimeVersion": "b7000",
                  "allowedRedirectHosts": ["cdn.example"]
                }
              ]
            }
            """;
        var service = new LocalModelCatalogService(_ => payload);

        var catalog = service.ParseVerifiedCatalog("signed-envelope");

        var model = Assert.Single(catalog.Models);
        Assert.Equal("qwen3-4b-instruct-2507-q4", model.Id);
        Assert.Equal(3_000_000_000, model.SizeBytes);
        Assert.Contains("cdn.example", model.AllowedRedirectHosts);
    }

    [Fact]
    public void ParseVerifiedCatalog_InsecureDownloadUrlIsRejected()
    {
        var payload = """
            {
              "schemaVersion": 1,
              "catalogVersion": "bad",
              "models": [{
                "id":"bad","version":"1","displayName":"Bad","tier":"Light","fileName":"model.gguf",
                "downloadUrl":"http://models.example/model.gguf","sizeBytes":100,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "contextTokens":4096,"minimumMemoryBytes":1024,"licenseId":"Apache-2.0","licenseUrl":"https://example/license","runtimeVersion":"b7000"
              }]
            }
            """;
        var service = new LocalModelCatalogService(_ => payload);

        var error = Assert.Throws<InvalidDataException>(() => service.ParseVerifiedCatalog("signed-envelope"));

        Assert.Contains("HTTPS", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseVerifiedCatalog_InvalidSignatureIsRejected()
    {
        var service = new LocalModelCatalogService(_ => null);

        var error = Assert.Throws<InvalidDataException>(() => service.ParseVerifiedCatalog("tampered"));

        Assert.Contains("签名", error.Message);
    }
}
