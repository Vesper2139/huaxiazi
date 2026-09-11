[CmdletBinding()]
param(
    [string]$Model = "",
    [string]$Train = "datasets/v1/sft_train.jsonl"
)

$ErrorActionPreference = "Stop"
$python = Join-Path $PSScriptRoot ".venv\Scripts\python.exe"
if (-not (Test-Path -LiteralPath $python)) { $python = "python" }
$script = @'
import json, importlib.util, os, sys
result = {"python": sys.executable, "python_version": sys.version.split()[0], "packages": {name: bool(importlib.util.find_spec(name)) for name in ["torch", "transformers", "peft", "datasets", "accelerate"]}}
try:
 import torch
 result["torch"] = torch.__version__; result["cuda"] = bool(torch.cuda.is_available()); result["cuda_version"] = torch.version.cuda
except Exception as exc: result["torch_error"] = str(exc)
print(json.dumps(result, ensure_ascii=False))
'@
$probe = [System.IO.Path]::GetTempFileName() + ".py"
[System.IO.File]::WriteAllText($probe, $script, [System.Text.UTF8Encoding]::new($false))
try { $runtime = & $python $probe | ConvertFrom-Json }
finally { Remove-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue }
$gpu = nvidia-smi --query-gpu=name,memory.total,memory.free --format=csv,noheader,nounits 2>$null
$drive = Get-PSDrive -Name ((Split-Path -Qualifier (Get-Location)).TrimEnd(':'))
$output = [ordered]@{ runtime = $runtime; gpu = @($gpu); free_disk_gb = [math]::Round($drive.Free / 1GB, 2); train_exists = Test-Path -LiteralPath $Train; train_file = $Train }
if ($Model) {
    $output.model = $Model
    $output.model_directory_exists = Test-Path -LiteralPath $Model
    $output.model_config_exists = Test-Path -LiteralPath (Join-Path $Model "config.json")
    $output.model_weights_found = if ($output.model_directory_exists) { @(Get-ChildItem -LiteralPath $Model -File -Recurse -ErrorAction SilentlyContinue | Where-Object Extension -in @(".safetensors", ".bin")).Count -gt 0 } else { $false }
    $output.model_ready = $output.model_directory_exists -and $output.model_config_exists -and $output.model_weights_found
}
$output | ConvertTo-Json -Depth 6
