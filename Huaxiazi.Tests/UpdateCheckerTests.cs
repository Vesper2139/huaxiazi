using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

/// <summary>
/// 验证 UpdateChecker 的安全降级与版本比较逻辑：
///  - 禁用（URL 为空）安静返回 Disabled；
///  - 坏 JSON / 缺字段 / 非法版本号 安静返回 Error；
///  - 版本比较（更新 / 持平 / 更旧）正确；
///  - 离线 / HTTP 错误 安静返回 Error，绝不抛未处理异常。
/// 网络层通过注入 HttpClient + 桩 HttpMessageHandler 替换，避免任何真实网络。
/// </summary>
public class UpdateCheckerTests
{
    /// <summary>桩处理器：用给定委托构造响应，或抛出以模拟离线。</summary>
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }

    private static UpdateChecker CreateWithHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new UpdateChecker(new HttpClient(new StubHttpMessageHandler(responder)));

    private static UpdateChecker CreateWithTrustedHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var client = new HttpClient(new StubHttpMessageHandler(responder));
        var constructor = typeof(UpdateChecker).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(HttpClient), typeof(Func<string, string?>)],
            modifiers: null);
        Assert.NotNull(constructor);
        return (UpdateChecker)constructor!.Invoke([client, (Func<string, string?>)(raw => raw)]);
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode code = HttpStatusCode.OK)
        => new HttpResponseMessage(code)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };

    [Fact]
    public void GetCurrentVersion_ReturnsValidVersion()
    {
        var v = UpdateChecker.GetCurrentVersion();
        Assert.NotNull(v);
        Assert.True(v.Major >= 0);
    }

    [Fact]
    public async Task CheckAsync_EmptyUrl_ReturnsDisabled()
    {
        var checker = new UpdateChecker();
        var result = await checker.CheckAsync("");

        Assert.Equal(UpdateStatus.Disabled, result.Status);
        Assert.Equal("未配置更新源", result.Message);
    }

    [Fact]
    public async Task CheckAsync_NullUrl_ReturnsDisabled()
    {
        var checker = new UpdateChecker();
        var result = await checker.CheckAsync(null);

        Assert.Equal(UpdateStatus.Disabled, result.Status);
    }

    [Fact]
    public void Evaluate_BadJson_ReturnsError()
    {
        var checker = new UpdateChecker();
        var result = checker.Evaluate("{ this is not valid json", new Version(1, 0, 0, 0));

        Assert.Equal(UpdateStatus.Error, result.Status);
    }

    [Fact]
    public void Evaluate_MissingVersionField_ReturnsError()
    {
        var checker = new UpdateChecker();
        var result = checker.Evaluate("{ \"url\": \"https://example.com/release\" }", new Version(1, 0, 0, 0));

        Assert.Equal(UpdateStatus.Error, result.Status);
    }

    [Fact]
    public void Evaluate_InvalidVersionValue_ReturnsError()
    {
        var checker = new UpdateChecker();
        var result = checker.Evaluate("{ \"version\": \"not-a-version\" }", new Version(1, 0, 0, 0));

        Assert.Equal(UpdateStatus.Error, result.Status);
    }

    [Fact]
    public void Evaluate_NewerVersion_ReturnsUpdateAvailable()
    {
        var checker = new UpdateChecker();
        var result = checker.Evaluate(
            "{ \"version\": \"2.0.0\", \"url\": \"https://example.com/release\" }",
            new Version(1, 0, 0, 0));

        Assert.Equal(UpdateStatus.UpdateAvailable, result.Status);
        Assert.Equal("2.0.0", result.LatestVersion?.ToString());
        Assert.Equal("https://example.com/release", result.DownloadUrl);
    }

    [Fact]
    public void Evaluate_ManifestMetadata_IsReturnedForVerifiedDownload()
    {
        var checker = new UpdateChecker();
        var result = checker.Evaluate(
            """
            {
              "version": "2.0.0",
              "url": "https://example.com/HuaxiaziSetup.exe",
              "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
              "publishedAt": "2026-08-11T12:00:00Z",
              "releaseNotes": "新增表达润色模式"
            }
            """,
            new Version(1, 0, 0));

        Assert.Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", result.Sha256);
        Assert.Equal(new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero), result.PublishedAt);
        Assert.Equal("新增表达润色模式", result.ReleaseNotes);
    }

    [Fact]
    public void Evaluate_SameVersion_ReturnsUpToDate()
    {
        var checker = new UpdateChecker();
        var result = checker.Evaluate("{ \"version\": \"1.0.0.0\" }", new Version(1, 0, 0, 0));

        Assert.Equal(UpdateStatus.UpToDate, result.Status);
    }

    [Fact]
    public void Evaluate_OlderVersion_ReturnsUpToDate()
    {
        var checker = new UpdateChecker();
        var result = checker.Evaluate("{ \"version\": \"0.9.0\" }", new Version(1, 0, 0, 0));

        Assert.Equal(UpdateStatus.UpToDate, result.Status);
    }

    [Fact]
    public async Task CheckAsync_HttpErrorStatus_ReturnsError()
    {
        var checker = CreateWithHandler(_ => JsonResponse("{ }", HttpStatusCode.InternalServerError));
        var result = await checker.CheckAsync("https://example.com/version.json");

        Assert.Equal(UpdateStatus.Error, result.Status);
    }

    [Fact]
    public async Task CheckAsync_NetworkFailure_ReturnsError()
    {
        var checker = new UpdateChecker(new HttpClient(new StubHttpMessageHandler(_ => throw new HttpRequestException("offline"))));
        var result = await checker.CheckAsync("https://example.com/version.json");

        Assert.Equal(UpdateStatus.Error, result.Status);
    }

    [Fact]
    public async Task CheckAsync_HttpUrl_ReturnsError()
    {
        var checker = new UpdateChecker();
        var result = await checker.CheckAsync("http://example.com/version.json");

        Assert.Equal(UpdateStatus.Error, result.Status);
        Assert.Contains("HTTPS", result.Message);
    }

    [Fact]
    public async Task CheckAsync_InvalidUrl_ReturnsError()
    {
        var checker = new UpdateChecker();
        var result = await checker.CheckAsync("not-a-url");

        Assert.Equal(UpdateStatus.Error, result.Status);
    }

    [Fact]
    public async Task CheckAsync_SuccessfulResponse_ParsesCorrectly()
    {
        var checker = CreateWithTrustedHandler(_ => JsonResponse("{ \"version\": \"1.0.0.0\" }"));
        var result = await checker.CheckAsync("https://example.com/version.json");

        Assert.Equal(UpdateStatus.UpToDate, result.Status);
        Assert.Equal("1.0.0.0", result.LatestVersion?.ToString());
    }

    [Fact]
    public async Task CheckAsync_UnsignedManifest_IsRejectedByDefault()
    {
        var checker = CreateWithHandler(_ => JsonResponse("{ \"version\": \"999.0.0\" }"));

        var result = await checker.CheckAsync("https://example.com/version.json");

        Assert.Equal(UpdateStatus.Error, result.Status);
        Assert.Contains("签名", result.Message);
    }

    [Fact]
    public void ManifestSignature_ValidSignaturePassesAndPayloadTamperingFails()
    {
        using var rsa = RSA.Create(2048);
        var publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        var unsigned = """
            {"version":"2.0.0","url":"https://example.com/app.exe","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","publishedAt":"2026-08-30T00:00:00Z","releaseNotes":"safe"}
            """;
        var utility = typeof(UpdateChecker).Assembly.GetType("Huaxiazi.Services.UpdateManifestSignature");
        Assert.NotNull(utility);
        var verifyMethod = utility!.GetMethod("TryVerifyAndExtract", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(verifyMethod);
        var payload = System.Text.Encoding.UTF8.GetBytes(unsigned);
        var signature = Convert.ToBase64String(rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var signed = new JsonObject
        {
            ["payload"] = Convert.ToBase64String(payload),
            ["signature"] = signature
        };
        var arguments = new object?[] { signed.ToJsonString(), publicKey, null };

        Assert.True(Assert.IsType<bool>(verifyMethod.Invoke(null, arguments)));
        Assert.Equal(unsigned, arguments[2]);
        signed["payload"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(unsigned.Replace("2.0.0", "99.0.0")));
        arguments = [signed.ToJsonString(), publicKey, null];
        Assert.False(Assert.IsType<bool>(verifyMethod.Invoke(null, arguments)));
    }

    [Fact]
    public async Task CheckAsync_RejectsCrossHostDownloadManifest()
    {
        var checker = CreateWithTrustedHandler(_ => JsonResponse(
            "{ \"version\": \"999.0.0\", \"url\": \"https://attacker.example/payload.exe\", \"sha256\": \"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\" }"));
        var result = await checker.CheckAsync("https://updates.example/version.json");

        Assert.Equal(UpdateStatus.Error, result.Status);
        Assert.Contains("相同的 HTTPS 主机", result.Message);
    }
}
