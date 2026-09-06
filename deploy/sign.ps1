<#
.SYNOPSIS
    Authenticode-sign the published Huaxiazi binaries (SHA256 + RFC3161 timestamp).

.DESCRIPTION
    Signs out/publish/win-x64/Huaxiazi.exe and Huaxiazi.dll using signtool.exe.
    signtool is auto-detected from the installed Windows SDK; pass -SigntoolPath
    to override. A certificate is selected either from a PFX file (-CertPath /
    -CertPassword) or from the Windows certificate store by thumbprint (-CertSha1).

    SAFETY GUARANTEES (never crash with an unhandled exception):
      * If no certificate is supplied (-CertPath / -CertSha1), the script prints a
        clear message and exits 0 without signing.
      * If signtool.exe cannot be located, the script prints a clear warning and
        exits 0 (the publish artifact is still usable / unsigned).
      * Any other error during signing is caught, reported clearly, and the script
        exits with a non-zero code instead of throwing an unhandled exception.

.PARAMETER DistDir
    Folder containing the binaries to sign (default: out/publish/win-x64, resolved next to repo root).

.PARAMETER CertPath
    Path to a PFX certificate file.

.PARAMETER CertPassword
    Password for the PFX certificate.

.PARAMETER CertSha1
    Certificate thumbprint (SHA1) to select from the Windows certificate store.

.PARAMETER SigntoolPath
    Explicit path to signtool.exe.

.PARAMETER TimestampUrl
    RFC3161 timestamp server URL (default: DigiCert).

.EXAMPLE
    .\sign.ps1 -DistDir out/publish/win-x64 -CertPath .\cert.pfx -CertPassword secret
    .\sign.ps1 -DistDir out/publish/win-x64 -CertSha1 ABC123DEF456 -SigntoolPath C:\sdk\signtool.exe
#>

[CmdletBinding()]
param(
    [string]$DistDir      = "out/publish/win-x64",
    [string]$CertPath     = "",
    [string]$CertPassword = "",
    [string]$CertSha1     = "",
    [string]$SigntoolPath = "",
    [string]$TimestampUrl = "https://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"

function Write-Step([string]$msg) {
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host "  $msg" -ForegroundColor Cyan
    Write-Host "============================================================" -ForegroundColor Cyan
}

# Resolve DistDir relative to the repository root.
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$RepoRoot  = Split-Path -Parent $ScriptDir
if (-not [System.IO.Path]::IsPathRooted($DistDir)) {
    $DistDir = Join-Path $RepoRoot $DistDir
}
$DistDir = [System.IO.Path]::GetFullPath($DistDir)

try {
    # ---------- 0. A release without a certificate is a failed release ----------
    if ([string]::IsNullOrWhiteSpace($CertPath) -and [string]::IsNullOrWhiteSpace($CertSha1)) {
        Write-Error "A signing certificate is required (-CertPath or -CertSha1); refusing to produce an unsigned release."
        exit 1
    }

    # ---------- 1. Locate signtool ----------
    $signtool = $null
    if (-not [string]::IsNullOrWhiteSpace($SigntoolPath) -and (Test-Path $SigntoolPath)) {
        $signtool = $SigntoolPath
    }
    else {
        $kitRoots = @(
            Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin",
            Join-Path $env:ProgramFiles "Windows Kits\10\bin"
        )
        foreach ($root in $kitRoots) {
            if (Test-Path $root) {
                $found = Get-ChildItem -Path $root -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
                         Where-Object { $_.FullName -match '[\\/]x64[\\/]' } |
                         Select-Object -First 1 -ExpandProperty FullName
                if ($found) { $signtool = $found; break }
            }
        }
        if (-not $signtool) {
            $onPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
            if ($onPath) { $signtool = $onPath.Source }
        }
    }

    if (-not $signtool) {
        Write-Error "signtool.exe not found (Windows SDK is not installed); refusing to skip signing."
        exit 1
    }
    Write-Host "Using signtool: $signtool" -ForegroundColor Green

    # ---------- 2. Collect targets ----------
    if (-not (Test-Path $DistDir)) {
        Write-Error "DistDir not found: $DistDir."
        exit 1
    }

    $targetNames = @("Huaxiazi.exe", "Huaxiazi.dll", "Huaxiazi-Setup.exe")
    $targets = @()
    foreach ($name in $targetNames) {
        $fp = Join-Path $DistDir $name
        if (Test-Path $fp) { $targets += $fp }
    }

    if ($targets.Count -eq 0) {
        Write-Error "No signable files found in $DistDir."
        exit 1
    }

    # ---------- 3. Build base sign arguments ----------
    $baseArgs = @("/fd", "SHA256", "/tr", $TimestampUrl, "/td", "SHA256")
    if (-not [string]::IsNullOrWhiteSpace($CertSha1)) {
        $baseArgs += "/sha1"
        $baseArgs += $CertSha1
    }
    else {
        $baseArgs += "/f"
        $baseArgs += $CertPath
        if (-not [string]::IsNullOrWhiteSpace($CertPassword)) {
            $baseArgs += "/p"
            $baseArgs += $CertPassword
        }
    }

    # ---------- 4. Sign each target ----------
    Write-Step "Signing $($targets.Count) file(s)"
    $failed = @()
    foreach ($file in $targets) {
        Write-Host "Signing $file ..." -ForegroundColor White
        & $signtool @baseArgs "$file"
        if (-not $?) {
            Write-Warning "Failed to sign: $file"
            $failed += $file
        }
        else {
            Write-Host "  OK" -ForegroundColor Green
        }
    }

    if ($failed.Count -gt 0) {
        Write-Error "Signing failed for $($failed.Count) file(s). See output above."
        exit 1
    }

    Write-Host "All files signed successfully." -ForegroundColor Green
    exit 0
}
catch {
    # Never let an unhandled exception bubble up; report clearly and exit non-zero.
    Write-Error "Signing aborted due to an unexpected error: $($_.Exception.Message)"
    exit 1
}
