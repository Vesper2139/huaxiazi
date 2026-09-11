[CmdletBinding()]
param(
    [string]$Model = "qwen3:4b",
    [string]$InputPath = "datasets/architecture-v1/architecture_dev_requests.jsonl",
    [string]$Output = "training/architecture-qwen3-4b-dev-predictions.jsonl",
    [int]$Limit = 0,
    [int]$Skip = 0,
    [string]$ArchitectureId = "",
    [switch]$Append,
    [string]$Endpoint = "http://127.0.0.1:11434/api/chat",
    [int]$NumPredict = 256,
    [switch]$JsonMode,
    [switch]$AllowRemoteEndpoint
)

$ErrorActionPreference = "Stop"
$endpointUri = [Uri]$Endpoint
if (-not $AllowRemoteEndpoint -and $endpointUri.Host -notin @("localhost", "127.0.0.1", "::1")) {
    throw "为避免实验误调用线上服务，Endpoint 默认必须是本机 Ollama 地址；如确需远程模拟服务，请显式指定 -AllowRemoteEndpoint。"
}
$utf8 = [System.Text.UTF8Encoding]::new($false)
$requests = [System.IO.File]::ReadAllLines($InputPath, $utf8) | ForEach-Object { $_ | ConvertFrom-Json }
if (@($requests | Where-Object { [string]$_.split -notin @("train", "dev") }).Count -gt 0) {
    throw "架构 Provider 批量推理只允许 train/dev 请求；缺少或非法 split，拒绝执行。"
}
if ($ArchitectureId) {
    $requests = @($requests | Where-Object { [string]$_.architecture_id -eq $ArchitectureId })
    if ($requests.Count -eq 0) { throw "未找到 architecture_id=$ArchitectureId 的请求。" }
}
if ($Skip -gt 0) { $requests = $requests | Select-Object -Skip $Skip }
if ($Limit -gt 0) { $requests = $requests | Select-Object -First $Limit }
$parent = Split-Path -Parent $Output
if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
if ((Test-Path -LiteralPath $Output) -and -not $Append) { Remove-Item -LiteralPath $Output -Force }

foreach ($request in $requests) {
    $layers = @("system", "developer", "skill", "harness", "output_contract")
    $sections = foreach ($name in $layers) {
        $content = [string]$request.layers.$name
        if ($content) { "<$name>`n$content`n</$name>" }
    }
    $system = ($sections -join "`n`n") + "`n`n<architecture_weights>`n" + (($request.weights.psobject.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ", ") + "`n</architecture_weights>"
    $bodyObject = @{ model = $Model; messages = @(
        @{ role = "system"; content = $system },
        @{ role = "user"; content = if ($JsonMode) { "只返回一个合法 JSON 对象，且只能包含 answer 字段。不要 Markdown、前言、解释或分析。`n`n" + [string]$request.input } else { [string]$request.input } }
    ); stream = $false; think = $false; options = @{ num_predict = $NumPredict; temperature = 0 } }
    if ($JsonMode) {
        $schema = @{ type = "object"; properties = @{ answer = @{ type = "string" } }; required = @("answer"); additionalProperties = $false }
        $bodyObject | Add-Member -NotePropertyName format -NotePropertyValue $schema
    }
    $body = $bodyObject | ConvertTo-Json -Depth 8
    $response = Invoke-RestMethod -Uri $Endpoint -Method Post -ContentType "application/json; charset=utf-8" -Body $body -TimeoutSec 180
    $answerText = [string]$response.message.content
    if ($JsonMode) {
        try {
            $json = $answerText | ConvertFrom-Json
            if ($null -ne $json.answer) { $answerText = (@{ answer = [string]$json.answer } | ConvertTo-Json -Compress) }
        } catch { }
    }
    $line = [pscustomobject]@{ id = $request.sample_id; architecture_id = $request.architecture_id; split = $request.split; output = $answerText } | ConvertTo-Json -Compress
    [System.IO.File]::AppendAllText($Output, $line + [Environment]::NewLine, $utf8)
    Write-Output ([pscustomobject]@{ id = $request.sample_id; architecture_id = $request.architecture_id; done = $response.done } | ConvertTo-Json -Compress)
}
