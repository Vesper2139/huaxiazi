<#
.SYNOPSIS
    Self-contained publish for Huaxiazi (win-x64).

.DESCRIPTION
    One command that produces a directly runnable, self-contained artifact:
        dotnet publish -c Release -r win-x64 --self-contained true -o out/publish/win-x64

    Release publishing is fail-closed: a code-signing certificate must be supplied
    via -CertPath / -CertPassword (PFX file) or -CertSha1 (certificate store).

    This script is standalone and has no external dependencies beyond the
    .NET 8 SDK and (optionally) signtool.exe.

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
    Required for every release publish.

.PARAMETER Clean
    Clean bin/obj/<OutputDir> before publishing.

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
    [switch]$Clean,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "PowerShell 7 or newer is required for secure release publishing. Run this script with pwsh."
}

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$MainProj  = Join-Path $ScriptDir "Huaxiazi.csproj"
$TestProj  = Join-Path (Join-Path $ScriptDir "Huaxiazi.Tests") "Huaxiazi.Tests.csproj"
$DistDir   = Join-Path $ScriptDir $OutputDir
$SingleFileDir = Join-Path $ScriptDir "out/publish/single-file-$Runtime"
$PackagesDir = Join-Path $ScriptDir "release"
$ReportsDir = Join-Path $ScriptDir "out/reports"

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
if (-not $CertPath -and -not $CertSha1) {
    throw "Release signing is mandatory. Supply -CertPath or -CertSha1; use build.ps1 for unsigned local development builds."
}
if (-not $UpdateManifestPublicKey) {
    throw "-UpdateManifestPublicKey is mandatory so release builds can verify signed update manifests."
}
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
$signerCertificateSha256 = [Convert]::ToHexString(
    [Security.Cryptography.SHA256]::HashData($releaseCertificate.RawData))
$releaseCertificate.Dispose()

# ---------- 1. Optional clean ----------
if ($Clean) {
    Write-Step "Cleaning generated output"
    @("out") | ForEach-Object {
        $p = Join-Path $ScriptDir $_
        if (Test-Path $p) { Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

# ---------- 2. Restore + Build + Test ----------
Write-Step "Restore"
dotnet restore $MainProj
dotnet restore $TestProj

Write-Step "Build ($Configuration)"
dotnet build $MainProj -c $Configuration --no-restore
if (-not $?) { throw "Main project build failed." }

if (-not $SkipTests) {
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
        @{ Name = "integer-input"; Filter = "FullyQualifiedName~IntegerInputBehaviorTests" }
    )
    foreach ($testGroup in $testGroups) {
        dotnet test $TestProj -c $Configuration --no-build --filter $testGroup.Filter `
            --logger "trx;LogFileName=tests-$($testGroup.Name).trx" --results-directory $ReportsDir
        if (-not $?) { throw "Unit test group '$($testGroup.Name)' failed; publish aborted." }
    }
}

# ---------- 3. Self-contained publish ----------
Write-Step "Publish: self-contained $Runtime -> $OutputDir"
dotnet publish $MainProj -c $Configuration -r $Runtime --self-contained true -o $DistDir `
    -p:HuaxiaziUpdateManifestPublicKey=$UpdateManifestPublicKey `
    -p:HuaxiaziUpdateSignerCertificateSha256=$signerCertificateSha256
if (-not $?) { throw "Self-contained publish failed." }

Write-Host "Publish succeeded: $DistDir\Huaxiazi.exe" -ForegroundColor Green

# A separate single-file build is required for a truly direct-download EXE.
# IncludeAllContentForSelfExtract bundles Prompts/default config together with
# WPF, SQLite and native runtime files instead of shipping a misleading apphost.
Write-Step "Publish: standalone single-file $Runtime"
dotnet publish $MainProj -c $Configuration -r $Runtime --self-contained true -o $SingleFileDir `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:HuaxiaziUpdateManifestPublicKey=$UpdateManifestPublicKey `
    -p:HuaxiaziUpdateSignerCertificateSha256=$signerCertificateSha256
if (-not $?) { throw "Standalone single-file publish failed." }
$singleFileSource = Join-Path $SingleFileDir "Huaxiazi.exe"
if (-not (Test-Path $singleFileSource -PathType Leaf)) {
    throw "Standalone publish did not produce Huaxiazi.exe."
}
Write-Host "Standalone publish succeeded: $singleFileSource" -ForegroundColor Green

# ---------- 4. Mandatory code signing ----------
if ($CertPath -or $CertSha1) {
    Write-Step "Code signing (mandatory release gate)"
    $signScript = Join-Path (Join-Path $ScriptDir "deploy") "sign.ps1"
    if (-not (Test-Path $signScript)) {
        throw "deploy/sign.ps1 not found; cannot sign."
    }

    foreach ($signDirectory in @($DistDir, $SingleFileDir)) {
        $signParams = @{ DistDir = $signDirectory }
        if ($CertPath)     { $signParams['CertPath'] = $CertPath }
        if ($CertPassword) { $signParams['CertPassword'] = $CertPassword }
        if ($CertSha1)     { $signParams['CertSha1'] = $CertSha1 }
        if ($SigntoolPath) { $signParams['SigntoolPath'] = $SigntoolPath }

        & $signScript @signParams
        if (-not $?) { throw "Code signing failed for $signDirectory (see deploy/sign.ps1 output)." }
    }
    Write-Host "Code signing complete." -ForegroundColor Green
}

# ---------- 5. Stable client delivery files ----------
Write-Step "Package standalone EXE"
New-Item -ItemType Directory -Path $PackagesDir -Force | Out-Null
$standaloneExe = Join-Path $PackagesDir "Huaxiazi.exe"
Copy-Item -LiteralPath $singleFileSource -Destination $standaloneExe -Force
Write-Host "Standalone EXE: $standaloneExe" -ForegroundColor Green

Write-Step "Package portable ZIP"
$portableZip = Join-Path $PackagesDir "Huaxiazi-Portable.zip"
if (Test-Path $portableZip) { Remove-Item -LiteralPath $portableZip -Force }
Compress-Archive -Path (Join-Path $DistDir "*") -DestinationPath $portableZip -CompressionLevel Optimal
Write-Host "Portable ZIP: $portableZip" -ForegroundColor Green

# ---------- 6. Optional Inno Setup installer ----------
$isccCandidates = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 7\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 7\ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 7\ISCC.exe"),
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
    Write-Step "Build Inno Setup installer"
    & $iscc (Join-Path $ScriptDir "deploy\installer.iss")
    if (-not $?) { throw "Installer build failed." }
    $installer = Join-Path $PackagesDir "Huaxiazi-Setup.exe"
    if (Test-Path $installer) {
        $installerSignParams = @{ DistDir = $PackagesDir }
        if ($CertPath)     { $installerSignParams['CertPath'] = $CertPath }
        if ($CertPassword) { $installerSignParams['CertPassword'] = $CertPassword }
        if ($CertSha1)     { $installerSignParams['CertSha1'] = $CertSha1 }
        if ($SigntoolPath) { $installerSignParams['SigntoolPath'] = $SigntoolPath }
        & (Join-Path $ScriptDir "deploy\sign.ps1") @installerSignParams
        if (-not $?) { throw "Installer signing failed." }
        Write-Host "Installer: $installer" -ForegroundColor Green
    }
}
else {
    Write-Host "Inno Setup 6 not found; portable ZIP is ready and deploy/installer.iss remains build-ready." -ForegroundColor Yellow
}

# release/ is a client handoff folder, not an artifact archive.
$deliveryNames = @("Huaxiazi.exe", "Huaxiazi-Portable.zip", "Huaxiazi-Setup.exe")
Get-ChildItem -LiteralPath $PackagesDir -File |
    Where-Object { $_.Name -notin $deliveryNames } |
    Remove-Item -Force

Write-Step "All done"

# Keep the repository clean after a successful release. The portable package is
# delivery artifacts; expanded publish/build/test files are reproducible.
if (Test-Path (Join-Path $ScriptDir "out")) {
    Remove-Item -LiteralPath (Join-Path $ScriptDir "out") -Recurse -Force
}
# Remove an accidentally expanded copy left by older packaging workflows. The
# ZIP/installer packages are the supported delivery artifacts in release/.
$expandedPackage = Join-Path $PackagesDir "Huaxiazi-Portable"
if (Test-Path $expandedPackage -PathType Container) {
    Remove-Item -LiteralPath $expandedPackage -Recurse -Force
}
Write-Host "Cleaned temporary out/ directory; release/ contains only delivery artifacts." -ForegroundColor Green
