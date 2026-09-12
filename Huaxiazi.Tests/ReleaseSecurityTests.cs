using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class ReleaseSecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HuaxiaziReleaseSecurity_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SignScript_WithoutCertificate_FailsClosed()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "Huaxiazi.exe"), [0x4d, 0x5a]);

        var result = RunPowerShell($"& '{Path.Combine(RepoRoot(), "deploy", "sign.ps1")}' -DistDir '{_root}'");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("certificate", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GenerateManifest_WithoutSigningKey_FailsClosed()
    {
        Directory.CreateDirectory(_root);
        var output = Path.Combine(_root, "version.json");
        var script = Path.Combine(RepoRoot(), "deploy", "generate-manifest.ps1");

        var result = RunPowerShell(
            $"& '{script}' -Version '2.0.0' -Sha256 '{new string('a', 64)}' " +
            $"-DownloadUrl 'https://example.com/app.exe' -OutputPath '{output}'");

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void GenerateManifest_WithRsaKey_ProducesVerifiableSignedEnvelope()
    {
        Directory.CreateDirectory(_root);
        using var rsa = RSA.Create(2048);
        var privateKeyPath = Path.Combine(_root, "manifest-private.pem");
        File.WriteAllText(privateKeyPath, rsa.ExportRSAPrivateKeyPem(), new UTF8Encoding(false));
        var publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        var output = Path.Combine(_root, "version.json");
        var script = Path.Combine(RepoRoot(), "deploy", "generate-manifest.ps1");

        var result = RunPowerShell(
            $"& '{script}' -Version '2.0.0' -Sha256 '{new string('a', 64)}' " +
            $"-DownloadUrl 'https://example.com/app.exe' -SigningKeyPath '{privateKeyPath}' -OutputPath '{output}'");

        Assert.Equal(0, result.ExitCode);
        var envelope = File.ReadAllText(output);
        Assert.True(UpdateManifestSignature.TryVerifyAndExtract(envelope, publicKey, out var payload));
        using var document = JsonDocument.Parse(payload!);
        Assert.Equal("2.0.0", document.RootElement.GetProperty("version").GetString());
        Assert.DoesNotContain("PRIVATE KEY", envelope, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptInstaller_UsesProtectedMachineDirectoryAndRequiresElevation()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "install.ps1"));

        Assert.Contains("$env:ProgramFiles", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$env:LOCALAPPDATA\\Programs\\Huaxiazi", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IsInRole", script, StringComparison.Ordinal);
        Assert.Contains("Administrator", script, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeConfig_DisablesUnsafeBinaryFormatterSerialization()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Huaxiazi.runtimeconfig.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var properties = document.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties");

        Assert.True(properties.TryGetProperty("System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization", out var value));
        Assert.False(value.GetBoolean());
    }

    [Fact]
    public void SigningScript_UsesHttpsTimestampByDefault()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "sign.ps1"));

        Assert.Contains("$TimestampUrl = \"https://", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PublishScript_ExplicitUnsignedModePassesReleasePolicyValidation()
    {
        var script = Path.Combine(RepoRoot(), "publish.ps1");

        var result = RunPowerShell($"& '{script}' -AllowUnsigned -ValidateReleasePolicyOnly");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("release_mode=unsigned", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WriteChecksums_ProducesSortedSha256ManifestForDeliveryFiles()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "Huaxiazi.exe"), "standalone", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(_root, "Huaxiazi-Portable.zip"), "portable", new UTF8Encoding(false));
        var output = Path.Combine(_root, "SHA256SUMS.txt");
        var script = Path.Combine(RepoRoot(), "deploy", "write-checksums.ps1");

        var result = RunPowerShell($"& '{script}' -ReleaseDirectory '{_root}' -OutputPath '{output}'");

        Assert.Equal(0, result.ExitCode);
        var lines = File.ReadAllLines(output);
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("  Huaxiazi-Portable.zip", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("  Huaxiazi.exe", lines[1], StringComparison.Ordinal);
        Assert.All(lines, line => Assert.Matches("^[0-9A-F]{64}  Huaxiazi", line));
    }

    [Fact]
    public void Installer_EmbedsCurrentReleaseFileVersion()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "installer.iss"));

        Assert.Contains("#define MyAppVersion   \"2.0.1\"", script, StringComparison.Ordinal);
        Assert.Contains("VersionInfoVersion={#MyAppVersion}.0", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PublishScript_UsesDedicatedLockedRuntimeGraph()
    {
        var root = RepoRoot();
        var script = File.ReadAllText(Path.Combine(root, "publish.ps1"));

        Assert.True(File.Exists(Path.Combine(root, "deploy", "packages.win-x64.lock.json")));
        Assert.Contains("deploy/packages.win-x64.lock.json", script, StringComparison.Ordinal);
        Assert.Contains("-r $Runtime --locked-mode", script, StringComparison.Ordinal);
        Assert.Contains("--no-restore", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseBuild_PinsDotNetMajorAndRuntimePatch()
    {
        var root = RepoRoot();
        var project = File.ReadAllText(Path.Combine(root, "Huaxiazi.csproj"));
        var sdkPolicy = File.ReadAllText(Path.Combine(root, "global.json"));

        Assert.Contains("<RuntimeFrameworkVersion>8.0.30</RuntimeFrameworkVersion>", project, StringComparison.Ordinal);
        Assert.Contains("\"version\": \"8.0.100\"", sdkPolicy, StringComparison.Ordinal);
        Assert.Contains("\"rollForward\": \"latestFeature\"", sdkPolicy, StringComparison.Ordinal);
    }

    [Fact]
    public void WinTrust_ReleasesNestedMarshaledStructures()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "Services", "WinTrust.cs"));

        Assert.Contains("Marshal.DestroyStructure", source, StringComparison.Ordinal);
    }

    private static (int ExitCode, string Output) RunPowerShell(string command)
    {
        using var process = Process.Start(new ProcessStartInfo("pwsh.exe", $"-NoProfile -NonInteractive -Command \"{command}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        })!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout + stderr);
    }

    private static string RepoRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "Huaxiazi.sln")))
            directory = Path.GetDirectoryName(directory);
        return directory ?? throw new InvalidOperationException("未找到解决方案根目录。");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
