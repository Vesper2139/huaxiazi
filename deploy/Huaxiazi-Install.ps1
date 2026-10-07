<#
    Launch the embedded Inno installer from an ASCII staging path.
    Inno Setup's self-extracting loader can fail when the source EXE itself is
    launched from a non-ASCII directory on some Windows environments.
#>
[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$InstallerArguments
)

$ErrorActionPreference = "Stop"
$source = Join-Path $PSScriptRoot "Huaxiazi-Setup.exe"
if (-not (Test-Path $source -PathType Leaf)) {
    throw "Huaxiazi-Setup.exe was not found beside this installer launcher."
}

$stagingRoot = Join-Path $env:PUBLIC "Huaxiazi-Setup-Staging"
New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
$staged = Join-Path $stagingRoot ("Huaxiazi-Setup-" + [guid]::NewGuid().ToString("N") + ".exe")
Copy-Item -LiteralPath $source -Destination $staged -Force

try {
    $argumentList = @($InstallerArguments | Where-Object { $_ })
    $process = Start-Process -FilePath $staged -ArgumentList $argumentList -PassThru -Wait
    exit $process.ExitCode
}
finally {
    Remove-Item -LiteralPath $staged -Force -ErrorAction SilentlyContinue
}
