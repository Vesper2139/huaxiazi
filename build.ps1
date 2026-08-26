<#
.SYNOPSIS
    Huaxiazi one-shot build / test / publish script.

.DESCRIPTION
    Goal: produce a directly runnable artifact without Visual Studio.
    Requires .NET 8 SDK installed on Windows.

    Output layout:
      - out/publish/win-x64/ self-contained publish.
      - out/publish/win-x64-framework-dependent/ framework-dependent publish.

.PARAMETER Configuration
    Build configuration (default: Release).

.PARAMETER Clean
    Clean all generated directories before building.

.PARAMETER SkipTests
    Skip unit tests (restore + build + publish only).

.PARAMETER NoPublish
    Restore + build + test only, no publish.

.EXAMPLE
    .\build.ps1                 # Release + tests + both publishes
    .\build.ps1 -Clean          # clean full rebuild
    .\build.ps1 -Configuration Debug -SkipTests
#>

[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [switch]$Clean,
    [switch]$SkipTests,
    [switch]$NoPublish
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$MainProj  = Join-Path $ScriptDir "Huaxiazi.csproj"
$TestProj  = Join-Path (Join-Path $ScriptDir "Huaxiazi.Tests") "Huaxiazi.Tests.csproj"
$DistSc    = Join-Path $ScriptDir "out\publish\win-x64"
$DistFd    = Join-Path $ScriptDir "out\publish\win-x64-framework-dependent"
$ReportsDir = Join-Path $ScriptDir "out\reports"

function Write-Step([string]$msg) {
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host "  $msg" -ForegroundColor Cyan
    Write-Host "============================================================" -ForegroundColor Cyan
}

function Test-DotNet {
    try {
        $ver = dotnet --version
        Write-Host ".NET SDK detected: $ver" -ForegroundColor Green
    }
    catch {
        Write-Error "dotnet not found. Install .NET 8 SDK (https://dotnet.microsoft.com/download) first."
    }
}

# ---------- 0. Preconditions ----------
Test-DotNet

# ---------- 1. Clean ----------
if ($Clean) {
    Write-Step "Cleaning generated output"
    @("out") | ForEach-Object {
        $p = Join-Path $ScriptDir $_
        if (Test-Path $p) { Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

# ---------- 2. Restore ----------
Write-Step "Restore dependencies"
dotnet restore $MainProj
dotnet restore $TestProj

# ---------- 3. Build ----------
Write-Step "Build main project ($Configuration)"
dotnet build $MainProj -c $Configuration --no-restore
if (-not $?) { throw "Main project build failed." }

Write-Step "Build test project ($Configuration)"
dotnet build $TestProj -c $Configuration --no-restore
if (-not $?) { throw "Test project build failed." }

# ---------- 4. Test ----------
if (-not $SkipTests) {
    Write-Step "Run unit tests (xUnit)"
    New-Item -ItemType Directory -Path $ReportsDir -Force | Out-Null
    dotnet test $TestProj -c $Configuration --no-build --logger "trx;LogFileName=tests.trx" --results-directory $ReportsDir
    if (-not $?) { throw "Unit tests failed; publish aborted." }
}

# ---------- 5. Publish ----------
if (-not $NoPublish) {
    Write-Step "Publish: self-contained win-x64 -> out/publish/win-x64/"
    dotnet publish $MainProj -c $Configuration -r win-x64 --self-contained true -o $DistSc
    if (-not $?) { throw "Self-contained publish failed." }

    Write-Step "Publish: framework-dependent win-x64 -> out/publish/win-x64-framework-dependent/"
    dotnet publish $MainProj -c $Configuration -r win-x64 --self-contained false -o $DistFd
    if (-not $?) { throw "Framework-dependent publish failed." }

    Write-Host ""
    Write-Host "Publish complete:" -ForegroundColor Green
    Write-Host "  self-contained   : $DistSc\Huaxiazi.exe  (no .NET runtime needed)" -ForegroundColor Green
    Write-Host "  framework-dependent: $DistFd\Huaxiazi.exe  (needs .NET 8 runtime)" -ForegroundColor Green
}

Write-Step "All done"
