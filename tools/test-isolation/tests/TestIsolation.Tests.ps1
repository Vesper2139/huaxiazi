$modulePath = Join-Path $PSScriptRoot '..\TestIsolation.psm1'

Describe 'Test isolation helpers' {
    BeforeEach {
        Import-Module $modulePath -Force
    }

    It 'extracts unique test classes from xUnit list output' {
        $listing = @(
            'The following Tests are available:'
            '    Huaxiazi.Tests.AIServiceTests.GenerateAsync'
            '    Huaxiazi.Tests.AIServiceTests.GenerateAsync_WhenEmpty'
            '    Huaxiazi.Tests.AIServiceTests+NestedTests.Case'
            '    Huaxiazi.Tests.ConfigServiceTests.LoadAsync'
            '    Build succeeded.'
        )

        @(Get-IsolatedTestClassNames -TestListing $listing) | Should Be @(
            'Huaxiazi.Tests.AIServiceTests'
            'Huaxiazi.Tests.ConfigServiceTests'
        )
    }

    It 'builds an exact class-prefix filter that excludes similarly named classes' {
        New-IsolatedTestClassFilter -ClassName 'Huaxiazi.Tests.AIServiceTests' |
            Should Be 'FullyQualifiedName~Huaxiazi.Tests.AIServiceTests.'
    }

    It 'counts expected cases for one test class from the discovered listing' {
        $listing = @(
            ''
            '    Huaxiazi.Tests.SampleTests.First'
            '    Huaxiazi.Tests.SampleTests.Second(value: 1)'
            '    Huaxiazi.Tests.SampleTestsExtra.First'
            '    Huaxiazi.Tests.OtherTests.First'
            ''
        )

        Get-IsolatedTestCaseCount -TestListing $listing -ClassName 'Huaxiazi.Tests.SampleTests' |
            Should Be 2
    }

    It 'groups parameterized test display names by exact fully qualified method' {
        $listing = @(
            ''
            '    Huaxiazi.Tests.SampleTests.First'
            '    Huaxiazi.Tests.SampleTests.Transform(value: 1)'
            '    Huaxiazi.Tests.SampleTests.Transform(value: 2)'
            '    Huaxiazi.Tests.SampleTests.TransformAgain'
            '    Huaxiazi.Tests.SampleTestsExtra.First'
        )

        $groups = @(Get-IsolatedTestMethodGroups -TestListing $listing -ClassName 'Huaxiazi.Tests.SampleTests')
        $groups.Count | Should Be 3
        $groups[0].FullyQualifiedName | Should Be 'Huaxiazi.Tests.SampleTests.First'
        $groups[0].ExpectedTotal | Should Be 1
        $groups[1].FullyQualifiedName | Should Be 'Huaxiazi.Tests.SampleTests.Transform'
        $groups[1].ExpectedTotal | Should Be 2
        $groups[2].FullyQualifiedName | Should Be 'Huaxiazi.Tests.SampleTests.TransformAgain'
        $groups[2].ExpectedTotal | Should Be 1
    }

    It 'reads the executed and failed counts from a TRX summary' {
        $trx = @'
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <ResultSummary>
    <Counters total="5" executed="4" passed="3" failed="1" error="0" timeout="0" aborted="0" inconclusive="0" notRunnable="0" notExecuted="1" />
  </ResultSummary>
</TestRun>
'@

        $summary = ConvertFrom-IsolatedTestTrx -Xml $trx
        $summary.Total | Should Be 5
        $summary.Executed | Should Be 4
        $summary.Passed | Should Be 3
        $summary.Failed | Should Be 1
        $summary.Skipped | Should Be 1
        $summary.Aborted | Should Be 0
    }

    It 'counts xUnit skipped results when the TRX notExecuted counter is zero' {
        $trx = @'
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testName="SkippedOne" outcome="NotExecuted" />
    <UnitTestResult testName="SkippedTwo" outcome="NotExecuted" />
  </Results>
  <ResultSummary>
    <Counters total="4" executed="2" passed="2" failed="0" error="0" timeout="0" aborted="0" inconclusive="0" notRunnable="0" notExecuted="0" />
  </ResultSummary>
</TestRun>
'@

        (ConvertFrom-IsolatedTestTrx -Xml $trx).Skipped | Should Be 2
    }

    It 'classifies listed tests missing from a partial TRX as aborted' {
        $trx = @'
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <ResultSummary>
    <Counters total="22" executed="22" passed="22" failed="0" error="0" timeout="0" aborted="0" inconclusive="0" notRunnable="0" notExecuted="0" />
  </ResultSummary>
</TestRun>
'@

        $summary = ConvertFrom-IsolatedTestTrx -Xml $trx -ExpectedTotal 55
        $summary.Total | Should Be 55
        $summary.Passed | Should Be 22
        $summary.Aborted | Should Be 33
    }

    It 'rejects TRX documents that do not contain result counters' {
        $threw = $false
        try {
            ConvertFrom-IsolatedTestTrx -Xml '<TestRun />' | Out-Null
        } catch {
            $threw = $true
        }
        $threw | Should Be $true
    }
}
