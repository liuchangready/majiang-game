#requires -Version 5.1
param([string]$RepoRoot = (Join-Path $PSScriptRoot '..\..'))
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $RepoRoot).Path
$runId = [guid]::NewGuid().ToString('N')
$testRoot = Join-Path (Join-Path $env:LOCALAPPDATA 'Temp') ("v5-source-provenance-selftest-{0}" -f $runId)
$sourceDir = Join-Path $root 'tests/v5-golden'
$scriptPath = Join-Path $root 'tools/verify-v5-source-provenance.ps1'
New-Item -ItemType Directory -Path $testRoot | Out-Null

function Invoke-Probe([string]$caseRoot, [switch]$VerifyGitPins) {
    $args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $scriptPath, '-RepoRoot', $caseRoot)
    if ($VerifyGitPins) { $args += '-VerifyGitPins' }
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $output = & powershell.exe @args 2>&1 | Out-String
    $exitCode = $LASTEXITCODE
    $ErrorActionPreference = $previousPreference
    return [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
}

try {
    $cleanRoot = Join-Path $testRoot 'clean-no-git'
    New-Item -ItemType Directory -Path (Join-Path $cleanRoot 'tests/v5-golden') -Force | Out-Null
    Get-ChildItem -LiteralPath $sourceDir -Filter '*.json' -File | Copy-Item -Destination (Join-Path $cleanRoot 'tests/v5-golden')
    $clean = Invoke-Probe $cleanRoot
    if ($clean.ExitCode -ne 0 -or $clean.Output -notmatch 'Git history checked=False') { throw "NO_HISTORY_POSITIVE failed: exit=$($clean.ExitCode) $($clean.Output)" }

    $mutatedManifest = Join-Path $testRoot 'bad-manifest'
    Copy-Item -LiteralPath $cleanRoot -Destination $mutatedManifest -Recurse
    $manifestPath = Join-Path $mutatedManifest 'tests/v5-golden/source-provenance-v1.json'
    [IO.File]::AppendAllText($manifestPath, ' ', [Text.Encoding]::UTF8)
    $badManifest = Invoke-Probe $mutatedManifest
    if ($badManifest.ExitCode -eq 0 -or $badManifest.Output -notmatch 'manifest SHA-256 mismatch') { throw "MANIFEST_TAMPER negative failed: $($badManifest.Output)" }

    $mutatedGolden = Join-Path $testRoot 'bad-golden'
    Copy-Item -LiteralPath $cleanRoot -Destination $mutatedGolden -Recurse
    $goldenPath = Join-Path $mutatedGolden 'tests/v5-golden/movement-v1.json'
    [IO.File]::AppendAllText($goldenPath, ' ', [Text.Encoding]::UTF8)
    $badGolden = Invoke-Probe $mutatedGolden
    if ($badGolden.ExitCode -eq 0 -or $badGolden.Output -notmatch 'golden fixture bytes differ from frozen pin') { throw "GOLDEN_TAMPER negative failed: $($badGolden.Output)" }

    $gitRequired = Invoke-Probe $cleanRoot -VerifyGitPins
    if ($gitRequired.ExitCode -eq 0) { throw 'OPTIONAL_GIT_PIN negative failed: Git verification unexpectedly passed without repository history.' }

    Write-Output 'PASS: provenance verifier no-history positive; manifest, golden and optional-history negatives rejected.'
    Write-Output ("EVIDENCE_ROOT={0}" -f $testRoot)
    exit 0
} catch {
    Write-Output ("FAIL: {0}" -f $_.Exception.Message)
    Write-Output ("EVIDENCE_ROOT={0}" -f $testRoot)
    exit 1
}
