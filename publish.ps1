<#
.SYNOPSIS
    Self-contained publish for Huaxiazi (win-x64).

.DESCRIPTION
    One command that produces a directly runnable, self-contained artifact:
        dotnet publish -c Release -r win-x64 --self-contained true -o out/publish/win-x64

    Optionally signs the published binaries when a code-signing certificate is
    supplied via -CertPath / -CertPassword (PFX file) or -CertSha1 (cert store
    thumbprint). Signing is OFF unless a certificate is provided, so the script
    is safe to run with no signing infrastructure present.

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
    [switch]$Clean,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$MainProj  = Join-Path $ScriptDir "PromptFloat.csproj"
$TestProj  = Join-Path (Join-Path $ScriptDir "PromptFloat.Tests") "PromptFloat.Tests.csproj"
$DistDir   = Join-Path $ScriptDir $OutputDir
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

    Write-Step "Run unit tests (xUnit)"
    New-Item -ItemType Directory -Path $ReportsDir -Force | Out-Null
    dotnet test $TestProj -c $Configuration --no-build --logger "trx;LogFileName=tests.trx" --results-directory $ReportsDir
    if (-not $?) { throw "Unit tests failed; publish aborted." }
}

# ---------- 3. Self-contained publish ----------
Write-Step "Publish: self-contained $Runtime -> $OutputDir"
dotnet publish $MainProj -c $Configuration -r $Runtime --self-contained true -o $DistDir
if (-not $?) { throw "Self-contained publish failed." }

Write-Host "Publish succeeded: $DistDir\Huaxiazi.exe" -ForegroundColor Green

# ---------- 4. Optional code signing ----------
if ($CertPath -or $CertSha1) {
    Write-Step "Code signing (optional, certificate supplied)"
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
    if (-not $?) { throw "Code signing failed (see deploy/sign.ps1 output)." }
    Write-Host "Code signing complete." -ForegroundColor Green
}
else {
    Write-Host "Skipping code signing (no -CertPath / -CertSha1 provided)." -ForegroundColor Yellow
}

# ---------- 5. Portable ZIP + SHA-256 ----------
Write-Step "Package portable ZIP"
New-Item -ItemType Directory -Path $PackagesDir -Force | Out-Null
$portableZip = Join-Path $PackagesDir "Huaxiazi-portable-$Runtime.zip"
if (Test-Path $portableZip) { Remove-Item -LiteralPath $portableZip -Force }
Compress-Archive -Path (Join-Path $DistDir "*") -DestinationPath $portableZip -CompressionLevel Optimal
$portableHash = (Get-FileHash -LiteralPath $portableZip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($portableZip + ".sha256") -Value "$portableHash  $(Split-Path $portableZip -Leaf)" -Encoding ASCII
Write-Host "Portable ZIP: $portableZip" -ForegroundColor Green
Write-Host "SHA-256    : $portableHash" -ForegroundColor Green

# ---------- 6. Optional Inno Setup installer ----------
$isccCandidates = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
    Write-Step "Build Inno Setup installer"
    & $iscc (Join-Path $ScriptDir "deploy\installer.iss")
    if (-not $?) { throw "Installer build failed." }
    $installer = Join-Path $PackagesDir "HuaxiaziSetup.exe"
    if (Test-Path $installer) {
        $installerHash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
        Set-Content -LiteralPath ($installer + ".sha256") -Value "$installerHash  HuaxiaziSetup.exe" -Encoding ASCII
        Write-Host "Installer: $installer" -ForegroundColor Green
        Write-Host "SHA-256 : $installerHash" -ForegroundColor Green
    }
}
else {
    Write-Host "Inno Setup 6 not found; portable ZIP is ready and deploy/installer.iss remains build-ready." -ForegroundColor Yellow
}

Write-Step "All done"

# Keep the repository clean after a successful release. The portable package is
# delivery artifacts; expanded publish/build/test files are reproducible.
if (Test-Path (Join-Path $ScriptDir "out")) {
    Remove-Item -LiteralPath (Join-Path $ScriptDir "out") -Recurse -Force
}
# Remove an accidentally expanded copy left by older packaging workflows. The
# ZIP/installer packages and their checksums are the supported delivery
# artifacts in release/.
$expandedPackage = Join-Path $PackagesDir "Huaxiazi-portable-$Runtime"
if (Test-Path $expandedPackage -PathType Container) {
    Remove-Item -LiteralPath $expandedPackage -Recurse -Force
}
Write-Host "Cleaned temporary out/ directory; release/ contains only delivery artifacts." -ForegroundColor Green
