[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RepoRoot,

    [Parameter(Mandatory = $true)]
    [ValidateSet("BeforePublish", "AfterPublish")]
    [string]$Phase,

    [Parameter(Mandatory = $true)]
    [string]$DistDir,

    [Parameter(Mandatory = $true)]
    [string]$PortableStageDir
)

$ErrorActionPreference = "Stop"
$repoRootFull = [IO.Path]::GetFullPath($RepoRoot).TrimEnd('\', '/')
$publishRoot = [IO.Path]::GetFullPath((Join-Path $repoRootFull "out\publish")).TrimEnd('\', '/')
$publishPrefix = $publishRoot + [IO.Path]::DirectorySeparatorChar

function Get-ValidatedPublishPath([string]$Path, [string]$Name) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($publishPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Name must be inside the repository out\publish directory: $fullPath"
    }

    $protectedPaths = @(
        [IO.Path]::GetFullPath((Join-Path $repoRootFull "out\test-artifacts")),
        [IO.Path]::GetFullPath((Join-Path $repoRootFull "out\reports"))
    )
    foreach ($protectedPath in $protectedPaths) {
        $protectedPrefix = $protectedPath.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if ($fullPath.Equals($protectedPath, [StringComparison]::OrdinalIgnoreCase) -or
            $fullPath.StartsWith($protectedPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            $protectedPath.StartsWith($fullPath.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "$Name overlaps protected evaluation evidence: $fullPath"
        }
    }

    return $fullPath
}

$distPath = Get-ValidatedPublishPath $DistDir "DistDir"
$portableStagePath = Get-ValidatedPublishPath $PortableStageDir "PortableStageDir"
if ($distPath.Equals($portableStagePath, [StringComparison]::OrdinalIgnoreCase) -or
    $distPath.StartsWith($portableStagePath.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    $portableStagePath.StartsWith($distPath.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "DistDir and PortableStageDir must be separate directories."
}

$targets = @($distPath, $portableStagePath)
if ($Phase -eq "BeforePublish") {
    $targets += [IO.Path]::GetFullPath((Join-Path $repoRootFull "out\build"))
}

foreach ($target in $targets) {
    if (Test-Path -LiteralPath $target) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
}
