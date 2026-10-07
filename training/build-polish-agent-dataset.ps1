param(
  [string]$InputPath = (Join-Path $PSScriptRoot '..\datasets\v1\canonical.jsonl'),
  [string]$OutputDir = (Join-Path $PSScriptRoot '..\datasets\polish-agent-v2'),
  [int]$PerFamily = 125
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutputDir | Out-Null
$rows = Get-Content $InputPath | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.mode -eq 'polish' }
$families = @($rows | ForEach-Object { $_.provenance.template_family } | Sort-Object -Unique)
if ($families.Count -ne 24) { throw "Expected 24 polish template families, found $($families.Count)" }
$splitByFamily = @{}
for ($i=0; $i -lt $families.Count; $i++) {
  if ($i -lt 18) { $splitByFamily[$families[$i]] = 'train' }
  elseif ($i -lt 21) { $splitByFamily[$families[$i]] = 'dev' }
  else { $splitByFamily[$families[$i]] = 'test' }
}
$out = @()
foreach ($family in $families) {
  $familyRows = @($rows | Where-Object {
    if ($_.provenance.template_family -ne $family) { return $false }
    try { $g = $_.gold_output | ConvertFrom-Json } catch { return $false }
    $g.kind -eq 'final' -and -not [string]::IsNullOrWhiteSpace([string]$g.content)
  } | Sort-Object id | Select-Object -First $PerFamily)
  if ($familyRows.Count -ne $PerFamily) { throw "$family has $($familyRows.Count) rows, expected $PerFamily" }
  foreach ($row in $familyRows) {
    $gold = $row.gold_output | ConvertFrom-Json
    $cleanInput = [regex]::Replace([string]$row.input, '\s*样本\d+。\s*记录\d+。\s*$', '')
    $out += [ordered]@{
      id = $row.id.Replace('hxz-v1-polish-', 'hxz-polish-agent-v2-')
      schema_version = '2.0'
      split = $splitByFamily[$family]
      task_type = 'agent_polish'
      mode = 'polish'
      category = $row.category
      scenario = $row.scenario
      channel = $row.channel
      risk_level = $row.risk_level
      agent_behavior = [ordered]@{
        intent = 'preserve_meaning_and_improve_expression'
        preserve_facts = $true
        preserve_stance = $true
        preserve_emotion = $true
        clarify_when = if ($row.should_clarify) { 'missing_or_conflicting_key_facts' } else { 'not_required' }
        output_policy = 'directly_usable_final_text_or_structured_clarification'
      }
      input = $cleanInput
      context = $row.context
      claims = $row.claims
      expected_decision = if ($row.should_clarify) { 'clarify' } else { 'polish' }
      clarification_questions = $row.clarification_questions
      output = [ordered]@{ kind = $gold.kind; scenario = $gold.scenario; topic = $gold.topic; content = $gold.content }
      rubric = $row.rubric
      provenance = [ordered]@{ source_dataset = 'hxz-synthetic-v1'; source_id = $row.id; source_template_family = $family; license = 'project-owned-synthetic'; transform = 'remove_generator_record_suffix_and_normalize_agent_fields' }
      review = $row.review
    }
  }
}
$jsonLines = $out | ForEach-Object { $_ | ConvertTo-Json -Depth 20 -Compress }
$jsonLines | Where-Object { $_ } | Set-Content -Encoding UTF8 (Join-Path $OutputDir 'canonical.jsonl')
$sftLines = $out | ForEach-Object {
  $user = [ordered]@{ task = '润色以下内容；保留事实、立场和情绪，不虚构信息；仅在关键事实缺失时澄清。'; context = $_.context; input = $_.input }
  [ordered]@{ id=$_.id; split=$_.split; messages=@(@{role='user';content=($user | ConvertTo-Json -Depth 10 -Compress)}, @{role='assistant';content=($_.output | ConvertTo-Json -Depth 10 -Compress)}); metadata=@{scenario=$_.scenario;channel=$_.channel;risk_level=$_.risk_level;expected_decision=$_.expected_decision;source_template_family=$_.provenance.source_template_family} } | ConvertTo-Json -Depth 20 -Compress
}
$sftLines | Where-Object { $_ } | Set-Content -Encoding UTF8 (Join-Path $OutputDir 'sft.jsonl')
$canonicalHash = (Get-FileHash (Join-Path $OutputDir 'canonical.jsonl') -Algorithm SHA256).Hash.ToLowerInvariant()
$sftHash = (Get-FileHash (Join-Path $OutputDir 'sft.jsonl') -Algorithm SHA256).Hash.ToLowerInvariant()
$hash = (Get-FileHash $InputPath -Algorithm SHA256).Hash.ToLowerInvariant()
$counts = @{}
foreach ($s in @('train','dev','test')) { $counts[$s] = @($out | Where-Object split -eq $s).Count }
$manifest = [ordered]@{
  schema_version='2.0'; dataset_version='hxz-polish-agent-v2-3000'; purpose='agent_behavior_training'; source_dataset='hxz-synthetic-v1'; source_sha256=$hash; canonical_sha256=$canonicalHash; sft_sha256=$sftHash; generated_at=(Get-Date).ToUniversalTime().ToString('o'); total=$out.Count; counts=$counts; per_template_family=$PerFamily; family_split_policy='18 train families / 3 dev families / 3 test families'; files=@{canonical='canonical.jsonl';sft='sft.jsonl'}; limitations=@('源数据为项目自有合成样本，不等同于真实用户语料','需要人工抽检自然度、事实保真和场景覆盖后再用于权重训练','test 按模板族隔离，但源模板仍可能存在语义近似')
}
$manifest | ConvertTo-Json -Depth 10 | Set-Content -Encoding UTF8 (Join-Path $OutputDir 'manifest.json')
Write-Output "generated=$($out.Count) train=$($counts['train']) dev=$($counts['dev']) test=$($counts['test'])"
