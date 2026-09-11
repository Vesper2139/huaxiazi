[CmdletBinding()]
param(
    [string]$Model = "huaxiazi-qwen3-strict:latest",
    [string]$InputPath = "datasets/architecture-v1/architecture_dev_requests.jsonl",
    [string]$OutputRoot = "training/runs/ollama-matrix",
    [int]$Limit = 0,
    [switch]$JsonMode
)

$ErrorActionPreference = "Stop"
$requests = [System.IO.File]::ReadAllLines($InputPath, [System.Text.UTF8Encoding]::new($false)) |
    ForEach-Object { $_ | ConvertFrom-Json }
if (@($requests | Where-Object { [string]$_.split -notin @("train", "dev") }).Count -gt 0) {
    throw "矩阵实验只允许 train/dev split。"
}
$ids = @($requests | Select-Object -ExpandProperty architecture_id -Unique | Sort-Object)
if ($ids.Count -eq 0) { throw "输入中没有 architecture_id。" }
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
foreach ($id in $ids) {
    $safe = $id -replace '[^A-Za-z0-9_.-]', '_'
    $output = Join-Path $OutputRoot "$safe-predictions.jsonl"
    $invoke = @{ Model = $Model; InputPath = $InputPath; Output = $output; ArchitectureId = $id }
    if ($Limit -gt 0) { $invoke.Limit = $Limit }
    if ($JsonMode) { $invoke.JsonMode = $true }
    & (Join-Path $PSScriptRoot 'invoke-ollama-architecture-eval.ps1') @invoke | Write-Output
}
