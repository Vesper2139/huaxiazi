Set-StrictMode -Version Latest

function Get-IsolatedTestClassNames {
    [CmdletBinding()]
    param(
        [string[]] $TestListing
    )

    $classes = foreach ($line in $TestListing) {
        if ($line -match '^\s*(?<class>Huaxiazi\.Tests\.[A-Za-z_][A-Za-z0-9_]*)\.[A-Za-z_][A-Za-z0-9_]*') {
            $Matches['class']
        }
    }

    @($classes | Sort-Object -Unique)
}

function New-IsolatedTestClassFilter {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidatePattern('^Huaxiazi\.Tests\.[A-Za-z_][A-Za-z0-9_]*$')]
        [string] $ClassName
    )

    "FullyQualifiedName~$ClassName."
}

function Get-IsolatedTestCaseCount {
    [CmdletBinding()]
    param(
        [AllowEmptyString()]
        [Parameter(Mandatory = $true)]
        [string[]] $TestListing,
        [Parameter(Mandatory = $true)]
        [ValidatePattern('^Huaxiazi\.Tests\.[A-Za-z_][A-Za-z0-9_]*$')]
        [string] $ClassName
    )

    $prefix = [regex]::Escape($ClassName) + '\.[A-Za-z_][A-Za-z0-9_]*(?:\(|$)'
    @($TestListing | Where-Object { $_ -match "^\s*$prefix" }).Count
}

function Get-IsolatedTestMethodGroups {
    [CmdletBinding()]
    param(
        [AllowEmptyString()]
        [Parameter(Mandatory = $true)]
        [string[]] $TestListing,
        [Parameter(Mandatory = $true)]
        [ValidatePattern('^Huaxiazi\.Tests\.[A-Za-z_][A-Za-z0-9_]*$')]
        [string] $ClassName
    )

    $prefix = [regex]::Escape($ClassName) + '\.[A-Za-z_][A-Za-z0-9_]*'
    $methods = foreach ($line in $TestListing) {
        if ($line -match "^\s*(?<method>$prefix)(?:\(.*\))?\s*$") {
            $Matches['method']
        }
    }

    @($methods | Group-Object | Sort-Object Name | ForEach-Object {
        [pscustomobject]@{
            FullyQualifiedName = $_.Name
            ExpectedTotal = $_.Count
        }
    })
}

function ConvertFrom-IsolatedTestTrx {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $Xml,
        [ValidateRange(0, 10000000)]
        [int] $ExpectedTotal = 0
    )

    $document = New-Object System.Xml.XmlDocument
    $document.XmlResolver = $null
    $document.LoadXml($Xml)
    $counters = $document.SelectSingleNode("//*[local-name()='Counters']")
    if ($null -eq $counters) {
        throw 'TRX document does not contain a ResultSummary/Counters element.'
    }

    $required = @('total', 'executed', 'passed', 'failed', 'notExecuted')
    foreach ($name in $required) {
        if ($null -eq $counters.Attributes[$name]) {
            throw "TRX counters are missing the '$name' attribute."
        }
    }

    $values = @{}
    foreach ($name in $required) {
        $number = 0
        if (-not [int]::TryParse($counters.Attributes[$name].Value, [ref] $number)) {
            throw "TRX counter '$name' is not a valid integer."
        }
        $values[$name] = $number
    }

    $aborted = 0
    if ($null -ne $counters.Attributes['aborted'] -and
        -not [int]::TryParse($counters.Attributes['aborted'].Value, [ref] $aborted)) {
        throw "TRX counter 'aborted' is not a valid integer."
    }
    $skippedResults = $document.SelectNodes("//*[local-name()='UnitTestResult' and @outcome='NotExecuted']").Count
    $skipped = [Math]::Max($values['notExecuted'], $skippedResults)

    $total = if ($ExpectedTotal -gt 0) { $ExpectedTotal } else { $values['total'] }
    $unclassified = $total - $values['passed'] - $values['failed'] - $skipped - $aborted
    if ($unclassified -lt 0) {
        throw "TRX result counts exceed the expected total ($total)."
    }

    [pscustomobject]@{
        Total    = $total
        Executed = $values['executed']
        Passed   = $values['passed']
        Failed   = $values['failed']
        Skipped  = $skipped
        Aborted  = $aborted + $unclassified
    }
}

Export-ModuleMember -Function Get-IsolatedTestClassNames, New-IsolatedTestClassFilter, Get-IsolatedTestCaseCount, Get-IsolatedTestMethodGroups, ConvertFrom-IsolatedTestTrx
