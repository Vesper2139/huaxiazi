param(
    [Parameter(Mandatory = $true)]
    [string]$Source,
    [string]$Root = (Join-Path $PSScriptRoot '..\Resources\Skins')
)

dotnet run --project (Join-Path $PSScriptRoot 'ExtractFloatingBallSprites.csproj') --configuration Release -- "$Source" (Join-Path $PSScriptRoot '..\Resources\Skins')
if ($LASTEXITCODE -ne 0) { throw "悬浮球素材生成失败，退出码：$LASTEXITCODE" }
