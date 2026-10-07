[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^Huaxiazi\.Tests\.[A-Za-z_][A-Za-z0-9_]*$')]
    [string] $ClassName,
    [string] $ProjectPath = 'Huaxiazi.Tests\Huaxiazi.Tests.csproj',
    [string] $OutputDirectory,
    [switch] $NoRestore
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$project = if ([IO.Path]::IsPathRooted($ProjectPath)) { $ProjectPath } else { Join-Path $repoRoot $ProjectPath }
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw "Test project not found: $project"
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot ('out\test-artifacts\method-isolated-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
} elseif (-not [IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot $OutputDirectory
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

Import-Module (Join-Path $PSScriptRoot 'TestIsolation.psm1') -Force

$oldTemp = $env:TEMP
$oldTmp = $env:TMP
$oldTmpDir = $env:TMPDIR
$testTemp = Join-Path $repoRoot 'out\test-temp'
New-Item -ItemType Directory -Path $testTemp -Force | Out-Null
$env:TEMP = $testTemp
$env:TMP = $testTemp
$env:TMPDIR = $testTemp

try {
    $listingArgs = @('test', $project, '--list-tests', '--logger', 'console;verbosity=quiet')
    if ($NoRestore) { $listingArgs += '--no-restore' }
    $listingOutput = @(& dotnet @listingArgs 2>&1 | ForEach-Object { $_.ToString() })
    $listingExitCode = $LASTEXITCODE
    $discovered = @(Get-IsolatedTestClassNames -TestListing $listingOutput)
    if ($listingExitCode -ne 0 -or $discovered -notcontains $ClassName) {
        $listingOutput | Set-Content -LiteralPath (Join-Path $OutputDirectory 'list-tests.log') -Encoding UTF8
        throw "Test class '$ClassName' was not discovered (dotnet exit code $listingExitCode). See list-tests.log in $OutputDirectory."
    }

    $groups = @(Get-IsolatedTestMethodGroups -TestListing $listingOutput -ClassName $ClassName)
    if ($groups.Count -eq 0) {
        throw "No test methods were listed for class '$ClassName'."
    }

    $rows = [Collections.Generic.List[object]]::new()
    foreach ($group in $groups) {
        $shortName = $group.FullyQualifiedName.Substring($ClassName.Length + 1)
        $safeName = $shortName -replace '[^A-Za-z0-9_.-]', '_'
        $methodDirectory = Join-Path (Join-Path $OutputDirectory $ClassName) $safeName
        New-Item -ItemType Directory -Path $methodDirectory -Force | Out-Null
        $trxPath = Join-Path $methodDirectory 'results.trx'
        $logPath = Join-Path $methodDirectory 'dotnet.log'
        Remove-Item -LiteralPath $trxPath -Force -ErrorAction SilentlyContinue

        $filter = 'FullyQualifiedName=' + $group.FullyQualifiedName
        $runArgs = @('test', $project, '--no-build', '--no-restore', '--filter', $filter, '--logger', 'trx;LogFileName=results.trx', '--results-directory', $methodDirectory)
        $previousErrorActionPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            & dotnet @runArgs *> $logPath
            $runExitCode = $LASTEXITCODE
        } finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }

        $counts = $null
        $parseError = $null
        if (Test-Path -LiteralPath $trxPath -PathType Leaf) {
            try {
                $counts = ConvertFrom-IsolatedTestTrx -Xml (Get-Content -LiteralPath $trxPath -Raw) -ExpectedTotal $group.ExpectedTotal
            } catch {
                $parseError = $_.Exception.Message
            }
        } else {
            $parseError = 'dotnet test did not produce results.trx.'
        }

        if ($null -eq $counts) {
            $rows.Add([pscustomobject]@{ TestMethod = $group.FullyQualifiedName; Total = $group.ExpectedTotal; Executed = 0; Passed = 0; Failed = 0; Skipped = 0; Aborted = $group.ExpectedTotal; ExitCode = $runExitCode; Error = $parseError })
            Write-Warning ('{0}: {1}' -f $group.FullyQualifiedName, $parseError)
            continue
        }

        $errorText = if ($counts.Aborted -gt 0) { "TRX accounted for only $($group.ExpectedTotal - $counts.Aborted) of $($group.ExpectedTotal) listed cases." } elseif ($runExitCode -ne 0) { "dotnet exit code $runExitCode" } elseif ($counts.Failed -gt 0) { 'test failures' } else { '' }
        $rows.Add([pscustomobject]@{ TestMethod = $group.FullyQualifiedName; Total = $counts.Total; Executed = $counts.Executed; Passed = $counts.Passed; Failed = $counts.Failed; Skipped = $counts.Skipped; Aborted = $counts.Aborted; ExitCode = $runExitCode; Error = $errorText })
        Write-Host ("{0}: {1}/{2} passed, {3} failed, {4} skipped, {5} aborted" -f $group.FullyQualifiedName, $counts.Passed, $counts.Executed, $counts.Failed, $counts.Skipped, $counts.Aborted)
    }

    $summaryPath = Join-Path $OutputDirectory 'summary.tsv'
    $rows | Export-Csv -LiteralPath $summaryPath -NoTypeInformation -Delimiter "`t" -Encoding UTF8
    $totals = [pscustomobject]@{
        Methods = $rows.Count
        Total = ($rows | Measure-Object -Property Total -Sum).Sum
        Executed = ($rows | Measure-Object -Property Executed -Sum).Sum
        Passed = ($rows | Measure-Object -Property Passed -Sum).Sum
        Failed = ($rows | Measure-Object -Property Failed -Sum).Sum
        Skipped = ($rows | Measure-Object -Property Skipped -Sum).Sum
        Aborted = ($rows | Measure-Object -Property Aborted -Sum).Sum
    }
    Write-Host ("Summary: {0} methods, {1} total, {2} passed, {3} failed, {4} skipped, {5} aborted" -f $totals.Methods, $totals.Total, $totals.Passed, $totals.Failed, $totals.Skipped, $totals.Aborted)
    Write-Host "Summary file: $summaryPath"
    if (@($rows | Where-Object { $_.Failed -gt 0 -or $_.Aborted -gt 0 -or $_.ExitCode -ne 0 -or $_.Error }).Count -gt 0) {
        exit 1
    }
    exit 0
} finally {
    $env:TEMP = $oldTemp
    $env:TMP = $oldTmp
    $env:TMPDIR = $oldTmpDir
}
