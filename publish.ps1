<#
.SYNOPSIS
    Self-contained publish for Huaxiazi (win-x64).

.DESCRIPTION
    One command that produces a self-contained installer source and portable package:
        dotnet publish -c Release -r win-x64 --self-contained true -o out/publish/win-x64

    Open-source releases may be produced without a commercial code-signing
    certificate by explicitly passing -AllowUnsigned. If a certificate is
    supplied, Authenticode signing and update trust anchors remain enforced.

    This script is standalone and has no external dependencies beyond the
    .NET 8 SDK and the PowerShell already included with supported Windows.
    PowerShell 7 is recommended for release automation but is not required.

.PARAMETER Configuration
    Build configuration (default: Release).

.PARAMETER Runtime
    Target runtime identifier (default: win-x64).

.PARAMETER OutputDir
    Publish output folder, relative to the repo root (default: out/publish/win-x64).

.PARAMETER CertPath
    Path to a PFX certificate. If set (or -CertSha1 set), code signing runs
    after publish via deploy/sign.ps1.

.PARAMETER CertPassword
    Password for the PFX certificate.

.PARAMETER CertSha1
    Certificate thumbprint to pick from the Windows certificate store.

.PARAMETER SigntoolPath
    Explicit path to signtool.exe (auto-detected from the Windows SDK if omitted).

.PARAMETER UpdateManifestPublicKey
    Base64-encoded RSA SubjectPublicKeyInfo pinned into the release binary.
    Required when producing a signed release with automatic updates enabled.

.PARAMETER AllowUnsigned
    Explicitly allow an unsigned open-source release. Windows may display an
    Unknown Publisher or SmartScreen warning for these artifacts.

.PARAMETER ValidateReleasePolicyOnly
    Validate release signing options and exit without building artifacts.

.PARAMETER Clean
    Clean generated build/publish directories before publishing while retaining reports and evaluation artifacts.

.PARAMETER SkipTests
    Skip the unit-test step before publishing.

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -CertPath .\cert.pfx -CertPassword secret
    .\publish.ps1 -CertSha1 ABC123... -Clean
#>

[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime       = "win-x64",
    [string]$OutputDir     = "out/publish/win-x64",
    [string]$CertPath      = "",
    [string]$CertPassword  = "",
    [string]$CertSha1      = "",
    [string]$SigntoolPath  = "",
    [string]$UpdateManifestPublicKey = "",
    [switch]$AllowUnsigned,
    [switch]$ValidateReleasePolicyOnly,
    [switch]$Clean,
    [switch]$SkipTests,
    [switch]$RequireInstaller,
    [string]$NuGetSource    = ""
)

$ErrorActionPreference = "Stop"
if ($PSVersionTable.PSVersion.Major -lt 5) {
    throw "PowerShell 5.1 or newer is required for release publishing."
}

$ScriptDir = [IO.Path]::GetFullPath((Split-Path -Parent $MyInvocation.MyCommand.Definition))
$MainProj  = Join-Path $ScriptDir "Huaxiazi.csproj"
$TestProj  = Join-Path (Join-Path $ScriptDir "Huaxiazi.Tests") "Huaxiazi.Tests.csproj"
$DistDir   = Join-Path $ScriptDir $OutputDir
$PackagesDir = Join-Path $ScriptDir "release"
$ReportsDir = Join-Path $ScriptDir "out/reports"
$PublishLockFile = Join-Path $ScriptDir "deploy/packages.win-x64.lock.json"

function Write-Step([string]$msg) {
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host "  $msg" -ForegroundColor Cyan
    Write-Host "============================================================" -ForegroundColor Cyan
}

# ---------- 0. Preconditions ----------
try {
    $ver = dotnet --version
    Write-Host ".NET SDK detected: $ver" -ForegroundColor Green
}
catch {
    Write-Error "dotnet not found. Install the .NET 8 SDK (https://dotnet.microsoft.com/download) first."
}
$hasSigningCertificate = -not [string]::IsNullOrWhiteSpace($CertPath) -or -not [string]::IsNullOrWhiteSpace($CertSha1)
if (-not $hasSigningCertificate -and -not $AllowUnsigned) {
    throw "No code-signing certificate was supplied. Pass -AllowUnsigned explicitly for an unsigned open-source release."
}
if ($hasSigningCertificate -and -not $UpdateManifestPublicKey) {
    throw "-UpdateManifestPublicKey is mandatory for signed releases with automatic updates."
}

$signerCertificateSha256 = ""
if ($hasSigningCertificate) {
    $releaseCertificate = if ($CertPath) {
        [Security.Cryptography.X509Certificates.X509Certificate2]::new(
            [IO.Path]::GetFullPath($CertPath), $CertPassword,
            [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    }
    else {
        Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My |
            Where-Object Thumbprint -eq ($CertSha1 -replace '\s', '') |
            Select-Object -First 1
    }
    if (-not $releaseCertificate) { throw "Unable to load the release signing certificate." }
    $hashAlgorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $signerCertificateSha256 = -join ($hashAlgorithm.ComputeHash($releaseCertificate.RawData) |
            ForEach-Object { $_.ToString("x2") })
    }
    finally {
        $hashAlgorithm.Dispose()
    }
    $releaseCertificate.Dispose()
}
else {
    Write-Warning "Producing an unsigned open-source release. Windows may show Unknown Publisher or SmartScreen warnings."
}

if ($ValidateReleasePolicyOnly) {
    Write-Output ("release_mode=" + $(if ($hasSigningCertificate) { "signed" } else { "unsigned" }))
    return
}

# ---------- 1. Optional clean ----------
if ($Clean) {
    Write-Step "Cleaning generated output"
    $cleanupScript = Join-Path $ScriptDir "deploy\clean-publish-output.ps1"
    & $cleanupScript -RepoRoot $ScriptDir -Phase BeforePublish -DistDir $DistDir `
        -PortableStageDir (Join-Path $ScriptDir "out/publish/portable-package")
    if (-not $?) { throw "Failed to clean transient build/publish outputs." }
    # release is a generated handoff directory. Remove the whole directory so an
    # older setup/portable/fix artifact can never survive into the next release.
    if (Test-Path $PackagesDir) {
        Get-ChildItem -LiteralPath $PackagesDir -Force | Remove-Item -Recurse -Force
    }
}

# ---------- 2. Restore + Build + Test ----------
# 可选：-NuGetSource 指定还原源。离线/内网发布时可指向本地包目录，例如
#   -NuGetSource "$env:USERPROFILE\.nuget\packages"
# 这样即使 nuget.org 不可达也能完成还原。
$restoreSourceArgs = @()
if (-not [string]::IsNullOrWhiteSpace($NuGetSource)) { $restoreSourceArgs = @("--source", $NuGetSource) }

Write-Step "Restore"
dotnet restore $MainProj -r $Runtime --locked-mode @restoreSourceArgs `
    -p:PublishSingleFile=false `
    -p:NuGetLockFilePath=$PublishLockFile
if (-not $?) { throw "Main project restore failed." }

Write-Step "Build ($Configuration)"
dotnet build $MainProj -c $Configuration --no-restore
if (-not $?) { throw "Main project build failed." }

if (-not $SkipTests) {
    # 只有真的要跑测试时才还原测试工程：离线/受限环境里测试依赖可能不在本地缓存，
    # 而 -SkipTests 的场景根本不需要它们。
    dotnet restore $TestProj --locked-mode @restoreSourceArgs
    if (-not $?) { throw "Test project restore failed." }

    Write-Step "Build test project ($Configuration)"
    dotnet build $TestProj -c $Configuration --no-restore
    if (-not $?) { throw "Test project build failed." }

    Write-Step "Run unit tests (xUnit, isolated WPF groups)"
    New-Item -ItemType Directory -Path $ReportsDir -Force | Out-Null
    # WPF creates native HWND subclasses. Running every UI fixture in one testhost can make the
    # host crash during process teardown even when all assertions pass. Isolate UI-heavy classes
    # in short-lived hosts while keeping the rest in one fast group.
    $testGroups = @(
        @{ Name = "core"; Filter = "FullyQualifiedName!~WpfViewSmokeTests&FullyQualifiedName!~BrandIconTests&FullyQualifiedName!~CompanionUiContractTests&FullyQualifiedName!~ThemeSwapRegressionTests&FullyQualifiedName!~AcceptanceDefectTests&FullyQualifiedName!~InteractionLayoutReverifyTests&FullyQualifiedName!~ResponsiveLayoutRegressionTests&FullyQualifiedName!~ScrollBehaviorTests&FullyQualifiedName!~IntegerInputBehaviorTests&FullyQualifiedName!~DecimalInputBehaviorTests&FullyQualifiedName!~ExceptionPresentationPolicyTests&FullyQualifiedName!~HotkeyParserTests&FullyQualifiedName!~SettingsViewModelTests&FullyQualifiedName!~ThemeServiceTests&FullyQualifiedName!~WindowPlacementServiceTests" },
        @{ Name = "brand"; Filter = "FullyQualifiedName~BrandIconTests" },
        @{ Name = "companion-ui"; Filter = "FullyQualifiedName~CompanionUiContractTests" },
        @{ Name = "theme-swap"; Filter = "FullyQualifiedName~ThemeSwapRegressionTests" },
        @{ Name = "wpf-smoke"; Filter = "FullyQualifiedName~WpfViewSmokeTests" },
        @{ Name = "acceptance"; Filter = "FullyQualifiedName~AcceptanceDefectTests" },
        @{ Name = "layout-reverify"; Filter = "FullyQualifiedName~InteractionLayoutReverifyTests" },
        @{ Name = "responsive-layout"; Filter = "FullyQualifiedName~ResponsiveLayoutRegressionTests" },
        @{ Name = "scroll-behavior"; Filter = "FullyQualifiedName~ScrollBehaviorTests" },
        @{ Name = "integer-input"; Filter = "FullyQualifiedName~IntegerInputBehaviorTests" },
        @{ Name = "decimal-input"; Filter = "FullyQualifiedName~DecimalInputBehaviorTests" },
        @{ Name = "exception-policy"; Filter = "FullyQualifiedName~ExceptionPresentationPolicyTests" },
        @{ Name = "hotkey-parser"; Filter = "FullyQualifiedName~HotkeyParserTests" },
        @{ Name = "settings-view-model"; Filter = "FullyQualifiedName~SettingsViewModelTests" },
        @{ Name = "theme-service"; Filter = "FullyQualifiedName~ThemeServiceTests" },
        @{ Name = "window-placement"; Filter = "FullyQualifiedName~WindowPlacementServiceTests" }
    )
    foreach ($testGroup in $testGroups) {
        dotnet test $TestProj -c $Configuration --no-build --filter $testGroup.Filter `
            --logger "trx;LogFileName=tests-$($testGroup.Name).trx" --results-directory $ReportsDir
        if (-not $?) { throw "Unit test group '$($testGroup.Name)' failed; publish aborted." }
    }
}

# ---------- 3. Self-contained publish ----------
Write-Step "Restore publish dependency graph ($Runtime)"
dotnet restore $MainProj -r $Runtime --locked-mode @restoreSourceArgs `
    -p:PublishSingleFile=false `
    -p:NuGetLockFilePath=$PublishLockFile
if (-not $?) { throw "Locked publish restore failed." }

Write-Step "Publish: self-contained $Runtime -> $OutputDir"
dotnet publish $MainProj -c $Configuration -r $Runtime --self-contained true --no-restore -o $DistDir `
    -p:PublishSingleFile=false `
    -p:NuGetLockFilePath=$PublishLockFile `
    -p:HuaxiaziUpdateManifestPublicKey=$UpdateManifestPublicKey `
    -p:HuaxiaziUpdateSignerCertificateSha256=$signerCertificateSha256
if (-not $?) { throw "Self-contained publish failed." }

Write-Host "Publish succeeded: $DistDir\Huaxiazi.exe" -ForegroundColor Green

$runtimeRoot = Join-Path $ScriptDir "runtimes\local"
if (Test-Path $runtimeRoot -PathType Container) {
    $runtimeExecutables = Get-ChildItem -LiteralPath $runtimeRoot -Filter "llama-server.exe" -File -Recurse
    if (-not $runtimeExecutables) { throw "runtimes/local exists but contains no llama-server.exe." }
    foreach ($runtimeExecutable in $runtimeExecutables) {
        $relativeRuntimePath = $runtimeExecutable.FullName.Substring($runtimeRoot.Length).TrimStart('\\', '/')
        if (-not (Test-Path (Join-Path $DistDir (Join-Path "runtimes\local" $relativeRuntimePath)) -PathType Leaf)) {
            throw "Published artifact is missing runtime file: $relativeRuntimePath"
        }
    }
}

# ---------- 4. Optional code signing ----------
if ($CertPath -or $CertSha1) {
    Write-Step "Code signing"
    $signScript = Join-Path (Join-Path $ScriptDir "deploy") "sign.ps1"
    if (-not (Test-Path $signScript)) {
        throw "deploy/sign.ps1 not found; cannot sign."
    }

    $signParams = @{ DistDir = $DistDir }
    if ($CertPath)     { $signParams['CertPath'] = $CertPath }
    if ($CertPassword) { $signParams['CertPassword'] = $CertPassword }
    if ($CertSha1)     { $signParams['CertSha1'] = $CertSha1 }
    if ($SigntoolPath) { $signParams['SigntoolPath'] = $SigntoolPath }

    & $signScript @signParams
    if (-not $?) { throw "Code signing failed for $DistDir (see deploy/sign.ps1 output)." }
    Write-Host "Code signing complete." -ForegroundColor Green
}

# ---------- 5. Stable client delivery files ----------
# Installer and portable package intentionally use the same folder publish.
# A single-file bundle is not shipped: .NET must self-extract all WPF/native
# files before managed startup, which is fragile under endpoint security or
# restricted TEMP policies. The folder publish runs directly from any path.
Write-Step "Package portable ZIP"
New-Item -ItemType Directory -Path $PackagesDir -Force | Out-Null
$portableZip = Join-Path $PackagesDir "Huaxiazi-Portable.zip"
if (Test-Path $portableZip) { Remove-Item -LiteralPath $portableZip -Force }
$portableStage = Join-Path $ScriptDir "out/publish/portable-package"
if (Test-Path $portableStage) { Remove-Item -LiteralPath $portableStage -Recurse -Force }
New-Item -ItemType Directory -Path $portableStage -Force | Out-Null
Copy-Item -Path (Join-Path $DistDir "*") -Destination $portableStage -Recurse -Force
# 便携包必须自带这些随包文件；解压后缺任何一个都会让程序启动异常或功能降级。
$requiredPortableFiles = @(
    "Config\default-config.json",
    "Resources\Brand\Huaxiazi.ico",
    "Prompts\SystemPrompt.txt"
)
foreach ($relative in $requiredPortableFiles) {
    if (-not (Test-Path (Join-Path $portableStage $relative) -PathType Leaf)) {
        throw "Portable package is missing required file: $relative"
    }
}
if (-not (Test-Path (Join-Path $portableStage "Presets\Skills") -PathType Container)) {
    throw "Portable package is missing the Presets\Skills directory."
}
Compress-Archive -Path (Join-Path $portableStage "*") -DestinationPath $portableZip -CompressionLevel Optimal
Write-Host "Portable ZIP: $portableZip" -ForegroundColor Green

# ---------- 6. Inno Setup installer（默认交付物） ----------
# 早期可正常安装的版本使用 Inno Setup 完整安装包：它负责目录选择、
# 当前用户安装、开始菜单/桌面快捷方式、升级覆盖和卸载注册。
$installerScript = Join-Path $ScriptDir "deploy\installer.iss"
$installer = Join-Path $PackagesDir "Huaxiazi-Setup.exe"
if (-not (Test-Path $installerScript -PathType Leaf)) {
    throw "Inno Setup 脚本不存在：$installerScript"
}
if (Test-Path $installer -PathType Leaf) { Remove-Item -LiteralPath $installer -Force }

Import-Module (Join-Path $ScriptDir "deploy\InnoSetupVersion.psm1") -Force
$isccCandidates = @(
    (Get-Command iscc.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
    (Join-Path ${env:LOCALAPPDATA} "Programs\Inno Setup 6\ISCC.exe"),
    (Join-Path ${env:ProgramFiles} "Inno Setup 6\ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe")
) | Where-Object { $_ -and (Test-Path $_ -PathType Leaf) } | Select-Object -Unique
if (-not $isccCandidates) {
    if ($RequireInstaller) { throw "未找到 Inno Setup 6.7.3+ 的 ISCC.exe，无法生成完整安装包。" }
    Write-Warning "未找到 ISCC.exe，跳过 Huaxiazi-Setup.exe；正式发布请使用 -RequireInstaller。"
}
else {
    $iscc = [string](@($isccCandidates)[0])
    $registeredInnoVersion = Get-InnoSetupRegisteredVersion
    if (-not $registeredInnoVersion) {
        throw "找到了 ISCC.exe，但未在卸载注册表中找到 Inno Setup DisplayVersion；无法验证最低版本 6.7.3。"
    }
    $verifiedInnoVersion = Assert-InnoSetupVersion -DisplayVersion ([string]$registeredInnoVersion)
    Write-Host "Inno Setup $verifiedInnoVersion detected from uninstall registry." -ForegroundColor Green
    Write-Step "Build Inno Setup installer"
    $isccArgs = @(
        "/Qp",
        "/O$PackagesDir",
        "/DMySourceDir=$DistDir",
        $installerScript
    )
    $compiler = Start-Process -FilePath $iscc -ArgumentList $isccArgs -Wait -PassThru -NoNewWindow
    if ($compiler.ExitCode -ne 0) { throw "Inno Setup 编译失败，退出码：$($compiler.ExitCode)" }
    if (-not (Test-Path $installer -PathType Leaf)) {
        throw "Inno Setup 没有生成 Huaxiazi-Setup.exe。"
    }
    # UseSetupLdr=no splits the installer payload into companion .bin files.
    # Require them here so the setup cannot be handed off as an unusable EXE.
    $installerParts = @(Get-ChildItem -LiteralPath $PackagesDir -File -Filter "Huaxiazi-Setup-*.bin")
    if ($installerParts.Count -eq 0) {
        throw "Inno Setup did not generate the required Huaxiazi-Setup-*.bin payload files."
    }
    if ($hasSigningCertificate) {
        $installerSignParams = @{ DistDir = $PackagesDir }
        if ($CertPath)     { $installerSignParams['CertPath'] = $CertPath }
        if ($CertPassword) { $installerSignParams['CertPassword'] = $CertPassword }
        if ($CertSha1)     { $installerSignParams['CertSha1'] = $CertSha1 }
        if ($SigntoolPath) { $installerSignParams['SigntoolPath'] = $SigntoolPath }
        & (Join-Path $ScriptDir "deploy\sign.ps1") @installerSignParams
        if (-not $?) { throw "Installer signing failed." }
        Write-Host "Installer: $installer" -ForegroundColor Green
    }
    else {
        Write-Host "Installer: $installer (unsigned)" -ForegroundColor Yellow
    }
}

# release/ is a client handoff folder, not an artifact archive.
$checksumFile = Join-Path $PackagesDir "SHA256SUMS.txt"
& (Join-Path $ScriptDir "deploy\write-checksums.ps1") -ReleaseDirectory $PackagesDir -OutputPath $checksumFile
if (-not $?) { throw "Failed to generate SHA-256 checksums." }

$deliveryNames = @("Huaxiazi-Portable.zip", "Huaxiazi-Setup.exe", "SHA256SUMS.txt") +
    @(Get-ChildItem -LiteralPath $PackagesDir -File -Filter "Huaxiazi-Setup-*.bin" | Select-Object -ExpandProperty Name)
# 必须连目录一起清理：旧版本把 out/publish/win-x64 展开发布副本留在 release/Huaxiazi-final，
# 用户很容易双击那个过期目录里的 EXE，跑到的是上几次构建的旧程序。
Get-ChildItem -LiteralPath $PackagesDir -Force |
    Where-Object { $_.Name -notin $deliveryNames } |
    Remove-Item -Recurse -Force

Write-Step "All done"

# Remove only the expanded publish copy and portable staging directory. Keep
# build outputs, test reports, and evaluation/model evidence under out/.
$cleanupScript = Join-Path $ScriptDir "deploy\clean-publish-output.ps1"
& $cleanupScript -RepoRoot $ScriptDir -Phase AfterPublish -DistDir $DistDir `
    -PortableStageDir (Join-Path $ScriptDir "out/publish/portable-package")
if (-not $?) { throw "Failed to remove transient publish staging directories." }
# Remove an accidentally expanded copy left by older packaging workflows. The
# ZIP/installer packages are the supported delivery artifacts in release/.
$expandedPackage = Join-Path $PackagesDir "Huaxiazi-Portable"
if (Test-Path $expandedPackage -PathType Container) {
    Remove-Item -LiteralPath $expandedPackage -Recurse -Force
}
Write-Host "Removed transient publish staging directories; preserved out/ reports and evaluation evidence." -ForegroundColor Green
