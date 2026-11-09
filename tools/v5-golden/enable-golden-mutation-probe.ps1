param([Parameter(Mandatory = $true)][string]$RepoRoot)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $RepoRoot).Path
$verifier = Join-Path $root 'tools/verify-v5-source-provenance.ps1'
$output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $verifier -RepoRoot $root 2>&1
if ($LASTEXITCODE -ne 0) { throw "pristine frozen provenance check failed before mutation probe: $($output | Out-String)" }
$env:MAHJONG_GOLDEN_PROBE = '1'
