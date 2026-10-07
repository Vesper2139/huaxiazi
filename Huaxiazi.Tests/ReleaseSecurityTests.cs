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
    public void ReleaseDoesNotShipScriptInstaller()
    {
        var publish = File.ReadAllText(Path.Combine(RepoRoot(), "publish.ps1"));
        var checksums = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "write-checksums.ps1"));

        Assert.DoesNotContain("Copy-Item -LiteralPath (Join-Path $ScriptDir \"deploy/install.ps1\")", publish, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Copy-Item -LiteralPath (Join-Path $ScriptDir \"deploy/install.cmd\")", publish, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Huaxiazi-Install.ps1", checksums, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Huaxiazi-Install.cmd", checksums, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PortableDelivery_ContainsNoPrerequisiteInstallerLauncher()
    {
        var root = RepoRoot();
        var publish = File.ReadAllText(Path.Combine(root, "publish.ps1"));
        var standards = File.ReadAllText(Path.Combine(root, "docs", "development-standards.md"));

        Assert.Contains("Huaxiazi-Portable.zip", publish, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Huaxiazi-Setup.exe", publish, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Copy-Item -LiteralPath (Join-Path $ScriptDir \"deploy/install.ps1\")", publish, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".NET Runtime 或 .NET SDK", standards, StringComparison.Ordinal);
        Assert.Contains("PowerShell 7 可以作为 CI 推荐环境，但不得成为用户安装条件", standards, StringComparison.Ordinal);
        Assert.Contains("Inno Setup", standards, StringComparison.Ordinal);
    }

    [Fact]
    public void PublishPolicyValidation_WorksOnBuiltInWindowsPowerShell()
    {
        var script = Path.Combine(RepoRoot(), "publish.ps1");
        var result = RunShell("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\" -AllowUnsigned -ValidateReleasePolicyOnly");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("release_mode=unsigned", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RunPowerShell_FallsBackToWindowsPowerShellWhenPowerShell7IsUnavailable()
    {
        Directory.CreateDirectory(_root);
        var emptyPath = Path.Combine(_root, "empty-path");
        Directory.CreateDirectory(emptyPath);

        var resolver = typeof(ReleaseSecurityTests).GetMethod(
            "ResolvePowerShellExecutable",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(resolver);
        var executable = (string)resolver.Invoke(null, [emptyPath])!;
        var result = RunShell(executable, "-NoProfile -NonInteractive -Command \"Write-Output fallback-ok\"");

        Assert.Equal("powershell.exe", executable);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("fallback-ok", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerVersionGate_AcceptsMinimumAndRejectsLowerVersions()
    {
        var module = Path.Combine(RepoRoot(), "deploy", "InnoSetupVersion.psm1");
        var accepted = RunPowerShell($"Import-Module '{module}' -Force; Assert-InnoSetupVersion -DisplayVersion '6.7.3'");
        var rejected = RunPowerShell($"Import-Module '{module}' -Force; Assert-InnoSetupVersion -DisplayVersion '6.7.2'");

        Assert.Equal(0, accepted.ExitCode);
        Assert.Contains("6.7.3", accepted.Output, StringComparison.Ordinal);
        Assert.NotEqual(0, rejected.ExitCode);
        Assert.Contains("6.7.3", rejected.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void PublishScript_UsesRegisteredInnoSetupVersionForInstallerGate()
    {
        var publish = File.ReadAllText(Path.Combine(RepoRoot(), "publish.ps1"));
        var versionModule = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "InnoSetupVersion.psm1"));

        Assert.Contains("Get-InnoSetupRegisteredVersion", publish, StringComparison.Ordinal);
        Assert.Contains("Assert-InnoSetupVersion", publish, StringComparison.Ordinal);
        Assert.Contains("Get-ItemProperty", versionModule, StringComparison.Ordinal);
        Assert.Contains("DisplayVersion", versionModule, StringComparison.Ordinal);
        Assert.Contains("Inno Setup 6_is1", versionModule, StringComparison.Ordinal);
        Assert.Contains("Inno Setup_is1", versionModule, StringComparison.Ordinal);
    }

    [Fact]
    public void AppVersion_IsDefinedOnceAndMatchesReadme()
    {
        var root = RepoRoot();
        var sharedProps = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));
        var project = File.ReadAllText(Path.Combine(root, "Huaxiazi.csproj"));
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));

        Assert.Contains("<Version>2.0.4</Version>", sharedProps, StringComparison.Ordinal);
        Assert.Contains("<AssemblyVersion>$(Version).0</AssemblyVersion>", sharedProps, StringComparison.Ordinal);
        Assert.Contains("<FileVersion>$(Version).0</FileVersion>", sharedProps, StringComparison.Ordinal);
        Assert.DoesNotContain("<Version>2.0.3</Version>", project, StringComparison.Ordinal);
        Assert.Contains("当前版本：2.0.4", readme, StringComparison.Ordinal);
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
        File.WriteAllText(Path.Combine(_root, "Huaxiazi.exe"), "legacy standalone", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(_root, "Huaxiazi-Portable.zip"), "portable", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(_root, "Huaxiazi-Setup.exe"), "setup", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(_root, "Huaxiazi-Setup-0.bin"), "setup payload", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(_root, "Huaxiazi-Install.ps1"), "legacy launcher", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(_root, "Huaxiazi-Install.cmd"), "legacy launcher", new UTF8Encoding(false));
        var output = Path.Combine(_root, "SHA256SUMS.txt");
        var script = Path.Combine(RepoRoot(), "deploy", "write-checksums.ps1");

        var result = RunPowerShell($"& '{script}' -ReleaseDirectory '{_root}' -OutputPath '{output}'");

        Assert.Equal(0, result.ExitCode);
        var lines = File.ReadAllLines(output);
        Assert.Equal(3, lines.Length);
        Assert.EndsWith("  Huaxiazi-Portable.zip", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("  Huaxiazi-Setup-0.bin", lines[1], StringComparison.Ordinal);
        Assert.EndsWith("  Huaxiazi-Setup.exe", lines[2], StringComparison.Ordinal);
        Assert.All(lines, line => Assert.Matches("^[0-9A-F]{64}  Huaxiazi", line));
    }

    [Fact]
    public void Installer_UsesInnoSetupAndPerUserDefault()
    {
        var installer = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "installer.iss"));

        Assert.Contains("PrivilegesRequired=lowest", installer, StringComparison.Ordinal);
        Assert.Contains("DefaultDirName={localappdata}\\Programs\\{#MyAppName}", installer, StringComparison.Ordinal);
        Assert.Contains("DisableDirPage=no", installer, StringComparison.Ordinal);
        Assert.Contains("UseSetupLdr=no", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void Installer_DefaultsToWritablePerUserLocationWithoutElevation()
    {
        var publish = File.ReadAllText(Path.Combine(RepoRoot(), "publish.ps1"));

        Assert.Contains("ISCC.exe", publish, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("installer.iss", publish, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RequireInstaller", publish, StringComparison.Ordinal);
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
    public void PublishCleanup_BeforePublishRemovesOnlyBuildAndPublishOutputs()
    {
        var evidenceFile = CreateFixtureFile("out/test-artifacts/review-packet/ratings.csv");
        var reportFile = CreateFixtureFile("out/reports/previous-run.trx");
        var buildFile = CreateFixtureFile("out/build/old-build.marker");
        var distFile = CreateFixtureFile("out/publish/win-x64/old-publish.marker");
        var portableStageFile = CreateFixtureFile("out/publish/portable-package/old-stage.marker");
        var script = Path.Combine(RepoRoot(), "deploy", "clean-publish-output.ps1");
        var distDir = Path.Combine(_root, "out", "publish", "win-x64");
        var portableStage = Path.Combine(_root, "out", "publish", "portable-package");

        var result = RunPowerShell(
            $"& '{script}' -RepoRoot '{_root}' -Phase BeforePublish -DistDir '{distDir}' -PortableStageDir '{portableStage}'");

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(evidenceFile));
        Assert.True(File.Exists(reportFile));
        Assert.False(File.Exists(buildFile));
        Assert.False(File.Exists(distFile));
        Assert.False(File.Exists(portableStageFile));
    }

    [Fact]
    public void PublishCleanup_AfterPublishPreservesBuildReportsAndEvaluationEvidence()
    {
        var evidenceFile = CreateFixtureFile("out/test-artifacts/review-packet/ratings.csv");
        var reportFile = CreateFixtureFile("out/reports/current-run.trx");
        var buildFile = CreateFixtureFile("out/build/test-artifact/trace.json");
        var distFile = CreateFixtureFile("out/publish/win-x64/Huaxiazi.exe");
        var portableStageFile = CreateFixtureFile("out/publish/portable-package/stage.marker");
        var script = Path.Combine(RepoRoot(), "deploy", "clean-publish-output.ps1");
        var distDir = Path.Combine(_root, "out", "publish", "win-x64");
        var portableStage = Path.Combine(_root, "out", "publish", "portable-package");

        var result = RunPowerShell(
            $"& '{script}' -RepoRoot '{_root}' -Phase AfterPublish -DistDir '{distDir}' -PortableStageDir '{portableStage}'");

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(evidenceFile));
        Assert.True(File.Exists(reportFile));
        Assert.True(File.Exists(buildFile));
        Assert.False(File.Exists(distFile));
        Assert.False(File.Exists(portableStageFile));
    }

    [Fact]
    public void PublishCleanup_RejectsTargetsOutsidePublishRoot()
    {
        var evidenceFile = CreateFixtureFile("out/test-artifacts/review-packet/ratings.csv");
        var script = Path.Combine(RepoRoot(), "deploy", "clean-publish-output.ps1");
        var distDir = Path.Combine(_root, "out", "test-artifacts");
        var portableStage = Path.Combine(_root, "out", "publish", "portable-package");
        Directory.CreateDirectory(portableStage);

        var result = RunPowerShell(
            $"& '{script}' -RepoRoot '{_root}' -Phase BeforePublish -DistDir '{distDir}' -PortableStageDir '{portableStage}'");

        Assert.NotEqual(0, result.ExitCode);
        Assert.True(File.Exists(evidenceFile));
    }

    [Fact]
    public void PublishScript_DelegatesCleanupAndDoesNotDeleteOutRoot()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "publish.ps1"));

        Assert.Contains("clean-publish-output.ps1", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-Item -LiteralPath (Join-Path $ScriptDir \"out\") -Recurse -Force", script, StringComparison.Ordinal);
        Assert.DoesNotContain("@(\"out\")", script, StringComparison.Ordinal);
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
        => RunShell(ResolvePowerShellExecutable(Environment.GetEnvironmentVariable("PATH")), $"-NoProfile -NonInteractive -Command \"{command}\"");

    private static string ResolvePowerShellExecutable(string? path)
    {
        foreach (var directory in (path ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), "pwsh.exe");
            if (File.Exists(candidate)) return candidate;
        }

        return "powershell.exe";
    }

    private static (int ExitCode, string Output) RunShell(string executable, string arguments)
    {
        var executablePath = executable.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")
            : executable;
        using var process = Process.Start(new ProcessStartInfo(executablePath, arguments)
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

    private string CreateFixtureFile(string relativePath)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fixture", new UTF8Encoding(false));
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
