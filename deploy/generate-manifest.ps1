<#
.SYNOPSIS
    Generate release/version.json update manifest with real version / SHA-256 / URL.

.DESCRIPTION
    Renders deploy/update-manifest.template.json and injects the single-source
    values for the auto-update channel:

        version      - the app <Version> from PromptFloat.csproj (passed in)
        sha256       - SHA-256 of the delivered installer / portable package
        url          - HTTPS download URL (pass -DownloadUrl; empty allowed)
        publishedAt  - UTC timestamp (default: now)
        releaseNotes - release notes (default: version-based text)

    Gates (fail loudly instead of shipping a broken manifest):
      1. The template must contain the {{VERSION}} placeholder. A template that
         hardcodes a version (e.g. the historical "1.0.0") is rejected.
      2. After rendering, no {{...}} placeholder may remain.
      3. The emitted "version" must equal the passed -Version (csproj authority).
      4. sha256 must be 64 hex chars; a non-empty url must be HTTPS.

.PARAMETER Version
    The application version, read from the csproj <Version> element.

.PARAMETER Sha256
    Lower/upper hex SHA-256 of the artifact the manifest will point at.

.PARAMETER DownloadUrl
    Full HTTPS URL of the downloadable installer. When empty, the manifest is
    still produced but "url" is left empty and a warning is printed.

.PARAMETER ReleaseNotes
    Optional release notes text. Defaults to a version-based sentence.

.PARAMETER PublishedAt
    Optional ISO-8601 UTC timestamp. Defaults to the current UTC time.

.PARAMETER OutputPath
    Destination file path (default: release/version.json relative to repo root).

.EXAMPLE
    .\deploy\generate-manifest.ps1 -Version 1.2.2 -Sha256 (Get-FileHash release\HuaxiaziSetup.exe -Algorithm SHA256).Hash
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Sha256,
    [string]$DownloadUrl   = "",
    [string]$ReleaseNotes  = "",
    [string]$PublishedAt   = "",
    [string]$OutputPath    = ""
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$TemplatePath = Join-Path $ScriptDir "update-manifest.template.json"
if (-not (Test-Path $TemplatePath)) {
    throw "Template not found: $TemplatePath"
}

if (-not $OutputPath) {
    $OutputPath = Join-Path (Split-Path -Parent $ScriptDir) "release\version.json"
}

# ---------- Gate 1: template must be parameterized, never hardcoded ----------
$template = Get-Content -LiteralPath $TemplatePath -Raw
if ($template -notmatch '\{\{VERSION\}\}') {
    throw "update-manifest 模板缺少 {{VERSION}} 占位符：禁止在模板中写死版本号（历史缺陷：曾写死 1.0.0 静默发布）。"
}

# ---------- Validate inputs ----------
if ($Sha256 -notmatch '^[0-9a-fA-F]{64}$') {
    throw "sha256 必须是 64 位十六进制（当前值：$Sha256）。"
}
if ($DownloadUrl) {
    $urlOk = [Uri]::TryCreate($DownloadUrl, [UriKind]::Absolute, [ref]$null)
    if (-not $urlOk -or -not $DownloadUrl.StartsWith("https://", [StringComparison]::OrdinalIgnoreCase)) {
        throw "下载地址必须为 https:// URL（当前值：$DownloadUrl）。"
    }
}
if (-not $PublishedAt) {
    $PublishedAt = [DateTimeOffset]::UtcNow.ToString("o")
}
if (-not $ReleaseNotes) {
    $ReleaseNotes = "Vesper / PromptFloat $Version 已发布。"
}

# ---------- Render ----------
$rendered = $template
$rendered = $rendered.Replace("{{VERSION}}", $Version)
$rendered = $rendered.Replace("{{URL}}", $DownloadUrl)
$rendered = $rendered.Replace("{{SHA256}}", $Sha256.ToLowerInvariant())
$rendered = $rendered.Replace("{{PUBLISHED_AT}}", $PublishedAt)
$rendered = $rendered.Replace("{{RELEASE_NOTES}}", $ReleaseNotes)

# ---------- Gate 2: no leftover placeholders ----------
if ($rendered -match '\{\{') {
    throw "update manifest 渲染后仍残留占位符（{{...}}），请检查模板与脚本是否同步。"
}

# ---------- Gate 3: emitted version == csproj version ----------
$manifest = $rendered | ConvertFrom-Json
if ($manifest.version -ne $Version) {
    throw "版本门禁失败：清单版本 $($manifest.version) 与 csproj 版本 $Version 不一致。"
}

$targetDir = Split-Path -Parent $OutputPath
if (-not (Test-Path $targetDir)) {
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
}
Set-Content -LiteralPath $OutputPath -Value $rendered -Encoding UTF8
Write-Host "update manifest 已生成: $OutputPath" -ForegroundColor Green
Write-Host "  version      : $($manifest.version)" -ForegroundColor Green
Write-Host "  sha256       : $($manifest.sha256)" -ForegroundColor Green
Write-Host "  url          : $($manifest.url)" -ForegroundColor Green
Write-Host "  publishedAt  : $($manifest.publishedAt)" -ForegroundColor Green
