using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Huaxiazi.Models;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class SecurityHardeningTests
{
    private sealed class RedirectHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://other.example/package.exe") }
            });
        }
    }

    [Theory]
    [InlineData("https://127.0.0.1:8443/v1")]
    [InlineData("https://10.0.0.8/v1")]
    [InlineData("https://192.168.1.8/v1")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    public void CustomCloudEndpoint_RejectsPrivateOrMetadataAddress(string endpoint)
    {
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.CustomOpenAICompatible,
            Protocol = ProviderProtocol.OpenAICompatible,
            Type = ProviderType.Cloud,
            ApiBase = endpoint,
            Model = "model"
        };

        var accepted = ProviderEndpointPolicy.TryValidate(profile, "secret", out _, out _);

        Assert.False(accepted);
    }

    [Theory]
    [InlineData("https://user:password@example.com/v1")]
    [InlineData("https://example.com/v1?next=https://attacker.example")]
    [InlineData("https://example.com/v1#fragment")]
    public void CustomCloudEndpoint_RejectsUserInfoQueryAndFragment(string endpoint)
    {
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.CustomOpenAICompatible,
            Protocol = ProviderProtocol.OpenAICompatible,
            Type = ProviderType.Cloud,
            ApiBase = endpoint,
            Model = "model"
        };

        var accepted = ProviderEndpointPolicy.TryValidate(profile, "secret", out _, out _);

        Assert.False(accepted);
    }

    [Fact]
    public void LocalProvider_RejectsNonDefaultPort()
    {
        var profile = new ProviderProfile
        {
            Platform = ProviderPlatform.Ollama,
            Protocol = ProviderProtocol.OpenAICompatible,
            Type = ProviderType.Local,
            ApiBase = "http://127.0.0.1:18923/v1",
            Model = "llama3"
        };

        Assert.False(ProviderEndpointPolicy.TryValidate(profile, null, out _, out _));
    }

    [Fact]
    public void ProviderNormalization_DerivesMissingSecretBinding()
    {
        var settings = new AppSettings
        {
            ProviderProfiles = [new ProviderProfile { Id = "main", SecretId = string.Empty }],
            ActiveProviderProfileId = "main"
        };

        settings.NormalizeProviderProfiles();

        Assert.Equal("provider-main", settings.ProviderProfiles[0].SecretId);
    }

    [Fact]
    public void LoadedConfig_CannotChooseAnExistingCredentialSlot()
    {
        var settings = new AppSettings
        {
            ProviderProfiles = [new ProviderProfile { Id = "attacker", SecretId = "provider-default" }]
        };

        settings.NormalizeProviderProfiles();
        Assert.True(settings.NormalizeLoadedCredentialBindings());

        Assert.Equal("provider-attacker", settings.ProviderProfiles[0].SecretId);
    }

    [Fact]
    public void LoadedCustomEndpoint_DoesNotReuseLegacyCredentialSlot()
    {
        var settings = new AppSettings
        {
            ProviderProfiles =
            [
                new ProviderProfile
                {
                    Id = "default",
                    Platform = ProviderPlatform.CustomOpenAICompatible,
                    Protocol = ProviderProtocol.OpenAICompatible,
                    Type = ProviderType.Cloud,
                    ApiBase = "https://evil.example/v1",
                    SecretId = "provider-default"
                }
            ],
            ActiveProviderProfileId = "default"
        };

        settings.NormalizeProviderProfiles();
        settings.NormalizeLoadedCredentialBindings();

        Assert.NotEqual("provider-default", settings.ProviderProfiles[0].SecretId);
    }

    [Fact]
    public void RestoreBackup_DoesNotOverwriteExistingConfig()
    {
        var root = Path.Combine(Path.GetTempPath(), "HuaxiaziSecurity_" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = Path.Combine(root, "source");
            var destination = Path.Combine(root, "destination");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(source, "config.json"), "{\"apiBase\":\"https://attacker.example\"}");
            File.WriteAllText(Path.Combine(destination, "config.json"), "{\"apiBase\":\"https://trusted.example\"}");
            var backup = Path.Combine(root, "backup.zip");
            new DataManagementService().CreateBackup(source, backup);

            new DataManagementService().RestoreBackup(backup, destination);

            Assert.Equal("{\"apiBase\":\"https://trusted.example\"}", File.ReadAllText(Path.Combine(destination, "config.json")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task UpdateDownload_DoesNotFollowRedirects()
    {
        var handler = new RedirectHandler();
        using var client = new HttpClient(handler);
        var root = Path.Combine(Path.GetTempPath(), "HuaxiaziUpdateSecurity_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var result = await new UpdateDownloadService(client).DownloadAsync(
                "https://example.com/package.exe", new string('0', 64), Path.Combine(root, "package.exe"));
            Assert.Equal(UpdateDownloadStatus.Error, result.Status);
            Assert.Equal(1, handler.Calls);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void ErrorLog_RedactsProviderHeadersAndEscapesNewlines()
    {
        var value = ErrorLogService.RedactSensitiveData("x-api-key: secret\r\nforged-entry\n\"key\":\"json-secret\"");
        Assert.DoesNotContain("secret", value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("json-secret", value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\r", value);
        Assert.DoesNotContain("\n", value);
    }

    [Fact]
    public void SecurityEventLog_DetectsTamperingInTheAppendOnlyChain()
    {
        var root = Path.Combine(Path.GetTempPath(), "HuaxiaziAudit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var log = new SecurityEventLogService(root);
            log.Append("EndpointChanged", "profile-1", "oldHostHash=old;newHost=api.example.com");
            log.Append("BackupRestored", "backup.zip", "sha256=abc");
            Assert.True(log.Validate(out _));
            File.AppendAllText(Path.Combine(root, "security.log"), "tampered\n");
            Assert.False(log.Validate(out _));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void SafeArchiveExtraction_EnforcesActualBytesWritten()
    {
        var root = Path.Combine(Path.GetTempPath(), "HuaxiaziZip_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var zip = Path.Combine(root, "payload.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("payload.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write(new string('x', 1024 * 1024));
            }
            using var read = ZipFile.OpenRead(zip);
            var output = Path.Combine(root, "out", "payload.txt");
            Assert.Throws<InvalidDataException>(() => SafeArchiveExtraction.ExtractToFile(
                read.GetEntry("payload.txt")!, output, 0, 128 * 1024));
            Assert.False(File.Exists(output));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void CorruptedDatabase_IsReportedWithoutThrowingDuringConstruction()
    {
        var root = Path.Combine(Path.GetTempPath(), "HuaxiaziCorruptDb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "data"));
        try
        {
            File.WriteAllText(Path.Combine(root, "data", "huaxiazi.db"), "not a sqlite database");

            var archive = new ArchiveService(root);

            var integrity = archive.CheckIntegrity();
            Assert.False(integrity.IsHealthy);
            Assert.Throws<DatabaseCorruptedException>(() => archive.Search(string.Empty));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
