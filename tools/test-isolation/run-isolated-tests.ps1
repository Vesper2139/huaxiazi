[CmdletBinding()]
param(
    [string] $ProjectPath = 'Huaxiazi.Tests\Huaxiazi.Tests.csproj',
    [string] $OutputDirectory,
    [string[]] $ClassName,
    [switch] $ListOnly,
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
    $OutputDirectory = Join-Path $repoRoot ('out\test-artifacts\class-isolated-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
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
    if ($listingExitCode -ne 0 -or $discovered.Count -eq 0) {
        $listingOutput | Set-Content -LiteralPath (Join-Path $OutputDirectory 'list-tests.log') -Encoding UTF8
        throw "Could not discover test classes (dotnet exit code $listingExitCode). See list-tests.log in $OutputDirectory."
    }

    if ($ListOnly) {
        $discovered
        return
    }

    $selected = if ($ClassName -and $ClassName.Count -gt 0) { @($ClassName | Sort-Object -Unique) } else { $discovered }
    $unknown = @($selected | Where-Object { $_ -notin $discovered })
    if ($unknown.Count -gt 0) {
        throw "Requested test class(es) were not discovered: $($unknown -join ', ')"
    }

    $expectedCasesByClass = @{}
    foreach ($class in $selected) {
        $expectedCasesByClass[$class] = Get-IsolatedTestCaseCount -TestListing $listingOutput -ClassName $class
        if ($expectedCasesByClass[$class] -eq 0) {
            throw "No test cases were listed for discovered class '$class'."
        }
    }

    $rows = [Collections.Generic.List[object]]::new()
    foreach ($class in $selected) {
        $expectedCases = $expectedCasesByClass[$class]
        $safeName = $class -replace '[^A-Za-z0-9_.-]', '_'
        $classDirectory = Join-Path $OutputDirectory $safeName
        New-Item -ItemType Directory -Path $classDirectory -Force | Out-Null
        $trxPath = Join-Path $classDirectory 'results.trx'
        $logPath = Join-Path $classDirectory 'dotnet.log'
        Remove-Item -LiteralPath $trxPath -Force -ErrorAction SilentlyContinue

        $runArgs = @('test', $project, '--no-build', '--no-restore', '--filter', (New-IsolatedTestClassFilter -ClassName $class), '--logger', 'trx;LogFileName=results.trx', '--results-directory', $classDirectory)
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
                $counts = ConvertFrom-IsolatedTestTrx -Xml (Get-Content -LiteralPath $trxPath -Raw) -ExpectedTotal $expectedCases
            } catch {
                $parseError = $_.Exception.Message
            }
        } else {
            $parseError = 'dotnet test did not produce results.trx.'
        }

        if ($null -eq $counts) {
            $rows.Add([pscustomobject]@{ Class = $class; Total = $expectedCases; Executed = 0; Passed = 0; Failed = 0; Skipped = 0; Aborted = $expectedCases; ExitCode = $runExitCode; Error = $parseError })
            Write-Warning ('{0}: {1}' -f $class, $parseError)
            continue
        }

        $errorText = if ($counts.Aborted -gt 0) { "TRX accounted for only $($expectedCases - $counts.Aborted) of $expectedCases listed cases; remaining cases were not reported." } elseif ($runExitCode -ne 0) { "dotnet exit code $runExitCode" } elseif ($counts.Failed -gt 0) { 'test failures' } else { '' }
        $rows.Add([pscustomobject]@{ Class = $class; Total = $counts.Total; Executed = $counts.Executed; Passed = $counts.Passed; Failed = $counts.Failed; Skipped = $counts.Skipped; Aborted = $counts.Aborted; ExitCode = $runExitCode; Error = $errorText })
        Write-Host ("{0}: {1}/{2} passed, {3} failed, {4} skipped, {5} aborted" -f $class, $counts.Passed, $counts.Executed, $counts.Failed, $counts.Skipped, $counts.Aborted)
    }

    $summaryPath = Join-Path $OutputDirectory 'summary.tsv'
    $rows | Export-Csv -LiteralPath $summaryPath -NoTypeInformation -Delimiter "`t" -Encoding UTF8
    $totals = [pscustomobject]@{
        Classes = $rows.Count
        Total = ($rows | Measure-Object -Property Total -Sum).Sum
        Executed = ($rows | Measure-Object -Property Executed -Sum).Sum
        Passed = ($rows | Measure-Object -Property Passed -Sum).Sum
        Failed = ($rows | Measure-Object -Property Failed -Sum).Sum
        Skipped = ($rows | Measure-Object -Property Skipped -Sum).Sum
        Aborted = ($rows | Measure-Object -Property Aborted -Sum).Sum
    }
    Write-Host ("Summary: {0} classes, {1} total, {2} passed, {3} failed, {4} skipped, {5} aborted" -f $totals.Classes, $totals.Total, $totals.Passed, $totals.Failed, $totals.Skipped, $totals.Aborted)
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
