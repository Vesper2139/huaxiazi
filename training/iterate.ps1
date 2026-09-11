[CmdletBinding()]
param(
    [string]$Dataset = "datasets/v1",
    [string]$Model = "qwen3:4b",
    [int]$Limit = 20,
    [string]$RunName = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$builder = Join-Path $root "DatasetBuilder\Huaxiazi.DatasetBuilder.csproj"
if ([string]::IsNullOrWhiteSpace($RunName)) { $RunName = Get-Date -Format "yyyyMMdd-HHmmss" }
$runDir = Join-Path $PSScriptRoot ("runs\" + $RunName)
New-Item -ItemType Directory -Force -Path $runDir | Out-Null
$manifestPath = Join-Path $Dataset "manifest.json"
$datasetHash = if (Test-Path -LiteralPath $manifestPath) { (Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json).dataset_hash } else { "unknown" }

$validationOutput = dotnet run --project $builder -- validate --input (Join-Path $Dataset "canonical_dev.jsonl")
$validationExit = $LASTEXITCODE
$validationOutput | Set-Content (Join-Path $runDir "validation.json") -Encoding UTF8
if ($validationExit -ne 0) { throw "Dataset validation failed with exit code $validationExit" }
$gold = Join-Path $runDir "gold.jsonl"
$utf8 = [System.Text.UTF8Encoding]::new($false)
$devLines = [System.IO.File]::ReadAllLines((Join-Path $Dataset "canonical_dev.jsonl"), $utf8)
if ($Limit -gt 0) { $devLines = $devLines | Select-Object -First $Limit }
[System.IO.File]::WriteAllLines($gold, [string[]]$devLines, $utf8)
$predictions = Join-Path $runDir "predictions.jsonl"
powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "invoke-ollama-eval.ps1") -Model $Model -InputPath $gold -Output $predictions -JsonMode -NumPredict 256 -Instruction "Return JSON only. Put the final Chinese answer in answer, required_output, or output. Do not include analysis."
dotnet run --project $builder -- evaluate --gold $gold --predictions $predictions --output (Join-Path $runDir "evaluation.json")
$evaluationExit = $LASTEXITCODE
dotnet run --project $builder -- mine-failures --gold $gold --predictions $predictions --output (Join-Path $runDir "dpo-failures.jsonl")
Write-Output ([pscustomobject]@{ run = $RunName; model = $Model; dataset_hash = $datasetHash; limit = $devLines.Count; directory = $runDir } | ConvertTo-Json -Compress)
exit $evaluationExit
