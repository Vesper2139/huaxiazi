[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ReleaseDirectory,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
$releaseRoot = [IO.Path]::GetFullPath($ReleaseDirectory)
$deliveryNames = @("Huaxiazi-Portable.zip", "Huaxiazi-Setup.exe", "Huaxiazi.exe")
$lines = @(
    Get-ChildItem -LiteralPath $releaseRoot -File |
        Where-Object Name -In $deliveryNames |
        Sort-Object Name |
        ForEach-Object {
            $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
            "$hash  $($_.Name)"
        }
)
if ($lines.Count -eq 0) {
    throw "No Huaxiazi delivery files were found in $releaseRoot."
}

$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $outputFullPath
if ($outputDirectory) { New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null }
[IO.File]::WriteAllLines($outputFullPath, $lines, [Text.UTF8Encoding]::new($false))
Write-Output "checksums=$outputFullPath"
