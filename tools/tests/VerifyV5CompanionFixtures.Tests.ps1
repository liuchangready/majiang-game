#requires -Version 5.1
<#
.SYNOPSIS
Regression test suite for tools/verify-v5-companion-fixtures.ps1.

.DESCRIPTION
Copies the four accepted companion golden fixtures into an isolated exclusive TEMP tree:
  - tests/v5-golden/movement-v1.json
  - tests/v5-golden/shuffle-reversed-v1.json
  - tests/v5-golden/visibility-v1.json
  - tests/v5-golden/ai-discard-flags-v1.json

Executes positive baseline runs and a comprehensive suite of negative mutation cases
covering single-byte drift, missing files, empty files, truncated/invalid JSON, path escape,
schema/identity mismatches, duplicate fixture identity collision, invalid/zero source commit
SHAs (including the D-R2-02 counterexample), case count mismatches, duplicate case IDs, and
fixture-specific structural violations (such as wall tile counts and required sections).
Verifies that the validator never writes to the real repository and that all real tracked
fixture hashes remain 100% untouched.

Exit codes: 0 = PASS, 1 = FAIL, 2 = BLOCKED.
#>
param(
    [string]$RepoRoot = '',
    [string]$LogPath = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

foreach ($tool in @('Get-FileHash', 'ConvertFrom-Json')) {
    if ($null -eq (Get-Command $tool -ErrorAction SilentlyContinue)) {
        Write-Output ("BLOCKED missing-tool '{0}'" -f $tool)
        exit 2
    }
}

$repo = if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
} else {
    (Resolve-Path $RepoRoot).Path
}

if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $artifactDir = Join-Path $repo 'build\companion-fixtures'
    if (-not (Test-Path -LiteralPath $artifactDir)) {
        New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null
    }
    $LogPath = Join-Path $artifactDir 'selftest.log'
}

$script:Failures = New-Object System.Collections.Generic.List[string]

function Add-Failure {
    param([string]$Message)
    $script:Failures.Add($Message) | Out-Null
}

function Get-Sha256 {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Read-JsonFile {
    param([string]$Path)
    $text = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
    return ($text | ConvertFrom-Json)
}

function Write-JsonFile {
    param([string]$Path, [object]$Value)
    $text = $Value | ConvertTo-Json -Depth 30
    [System.IO.File]::WriteAllText($Path, $text, (New-Object System.Text.UTF8Encoding($false)))
}

$realCompanionFixtures = @(
    'movement-v1.json',
    'shuffle-reversed-v1.json',
    'visibility-v1.json',
    'ai-discard-flags-v1.json'
)

$beforeHashes = @{}
foreach ($name in $realCompanionFixtures) {
    $fixturePath = Join-Path $repo ('tests\v5-golden\' + $name)
    if (-not (Test-Path -LiteralPath $fixturePath)) {
        Write-Output ("BLOCKED: required companion fixture missing from repo: '{0}'" -f $fixturePath)
        exit 2
    }
    $beforeHashes[$name] = Get-Sha256 $fixturePath
}

$tempRoot = Join-Path $env:TEMP ('v5-companion-fixtures-selftest-' + [guid]::NewGuid().ToString('N'))
$baseRoot = Join-Path $tempRoot 'base'
$caseRoot = Join-Path $tempRoot 'case'

function New-BaseTree {
    param([string]$Root, [string]$SourceRepo)
    $fixtureTargetDir = Join-Path $Root 'tests\v5-golden'
    $toolsTargetDir = Join-Path $Root 'tools'
    New-Item -ItemType Directory -Force -Path $fixtureTargetDir | Out-Null
    New-Item -ItemType Directory -Force -Path $toolsTargetDir | Out-Null

    foreach ($name in $realCompanionFixtures) {
        $src = Join-Path $SourceRepo ('tests\v5-golden\' + $name)
        $dst = Join-Path $fixtureTargetDir $name
        Copy-Item -LiteralPath $src -Destination $dst -Force
    }

    $validatorSrc = Join-Path $SourceRepo 'tools\verify-v5-companion-fixtures.ps1'
    $validatorDst = Join-Path $toolsTargetDir 'verify-v5-companion-fixtures.ps1'
    Copy-Item -LiteralPath $validatorSrc -Destination $validatorDst -Force
}

function New-CaseTree {
    param([string]$Base, [string]$Case)
    if (Test-Path -LiteralPath $Case) {
        Remove-Item -LiteralPath $Case -Recurse -Force
    }
    Copy-Item -LiteralPath $Base -Destination $Case -Recurse -Force
}

function Invoke-Validator {
    param(
        [string]$Root,
        [string]$FixtureDir = '',
        [switch]$SkipHashCheck,
        [switch]$VerifyGitPins
    )
    $validator = Join-Path $Root 'tools\verify-v5-companion-fixtures.ps1'
    $argsList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $validator, '-RepoRoot', $Root)
    if (-not [string]::IsNullOrWhiteSpace($FixtureDir)) {
        $argsList += @('-FixtureDir', $FixtureDir)
    }
    if ($SkipHashCheck) {
        $argsList += '-SkipHashCheck'
    }
    if ($VerifyGitPins) {
        $argsList += '-VerifyGitPins'
    }
    $output = & powershell.exe @argsList 2>&1
    $code = $LASTEXITCODE
    return [pscustomobject]@{
        Code = $code
        Output = (@($output) -join "`n")
    }
}

$script:CaseResults = @()

function Invoke-NegativeCase {
    param(
        [string]$Name,
        [string]$ExpectedCode,
        [scriptblock]$Mutator,
        [string]$FixtureDir = '',
        [switch]$SkipHashCheck,
        [switch]$VerifyGitPins
    )
    New-CaseTree -Base $baseRoot -Case $caseRoot
    & $Mutator $caseRoot
    $result = Invoke-Validator -Root $caseRoot -FixtureDir $FixtureDir -SkipHashCheck:$SkipHashCheck -VerifyGitPins:$VerifyGitPins
    $ok = ($result.Code -eq 1) -and $result.Output.Contains($ExpectedCode)
    $script:CaseResults += [pscustomobject]@{
        Name = $Name
        Expected = $ExpectedCode
        ExitCode = $result.Code
        Ok = $ok
        Output = $result.Output
    }
    if (-not $ok) {
        Add-Failure ('{0}: expected exit1 containing {1}, got exit{2}; output={3}' -f $Name, $ExpectedCode, $result.Code, $result.Output)
    }
}

try {
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
    New-BaseTree -Root $baseRoot -SourceRepo $repo

    # -------------------------------------------------------------------------
    # Positive Baselines
    # -------------------------------------------------------------------------
    # Baseline 1: In isolated TEMP sandbox
    $pos1 = Invoke-Validator -Root $baseRoot
    $pos1Ok = ($pos1.Code -eq 0) -and $pos1.Output.Contains('PASS: 4/4 companion golden fixtures verified successfully')
    $script:CaseResults += [pscustomobject]@{
        Name = 'positive-temp-baseline'
        Expected = 'PASS'
        ExitCode = $pos1.Code
        Ok = $pos1Ok
        Output = $pos1.Output
    }
    if (-not $pos1Ok) {
        Add-Failure ('positive-temp-baseline: expected exit 0 PASS, got exit {0}; output: {1}' -f $pos1.Code, $pos1.Output)
    }

    # Baseline 2: On real repository with git pins verified
    $pos2 = Invoke-Validator -Root $repo -VerifyGitPins
    $pos2Ok = ($pos2.Code -eq 0) -and $pos2.Output.Contains('PASS: 4/4 companion golden fixtures verified successfully') -and $pos2.Output.Contains('git blobs verified equal')
    $script:CaseResults += [pscustomobject]@{
        Name = 'positive-real-repo-git-pins'
        Expected = 'PASS'
        ExitCode = $pos2.Code
        Ok = $pos2Ok
        Output = $pos2.Output
    }
    if (-not $pos2Ok) {
        Add-Failure ('positive-real-repo-git-pins: expected exit 0 PASS with git pins, got exit {0}; output: {1}' -f $pos2.Code, $pos2.Output)
    }

    # -------------------------------------------------------------------------
    # Group A: Missing File & Empty File Negatives
    # -------------------------------------------------------------------------
    Invoke-NegativeCase -Name 'neg-missing-movement' -ExpectedCode 'FIXTURE_MISSING' -Mutator {
        param($root)
        Remove-Item -LiteralPath (Join-Path $root 'tests\v5-golden\movement-v1.json') -Force
    }

    Invoke-NegativeCase -Name 'neg-missing-shuffle-reversed' -ExpectedCode 'FIXTURE_MISSING' -Mutator {
        param($root)
        Remove-Item -LiteralPath (Join-Path $root 'tests\v5-golden\shuffle-reversed-v1.json') -Force
    }

    Invoke-NegativeCase -Name 'neg-missing-visibility' -ExpectedCode 'FIXTURE_MISSING' -Mutator {
        param($root)
        Remove-Item -LiteralPath (Join-Path $root 'tests\v5-golden\visibility-v1.json') -Force
    }

    Invoke-NegativeCase -Name 'neg-missing-ai-discard-flags' -ExpectedCode 'FIXTURE_MISSING' -Mutator {
        param($root)
        Remove-Item -LiteralPath (Join-Path $root 'tests\v5-golden\ai-discard-flags-v1.json') -Force
    }

    Invoke-NegativeCase -Name 'neg-empty-fixture' -ExpectedCode 'FIXTURE_EMPTY' -Mutator {
        param($root)
        [System.IO.File]::WriteAllText((Join-Path $root 'tests\v5-golden\movement-v1.json'), '')
    }

    # -------------------------------------------------------------------------
    # Group B: Path Escape Negative
    # -------------------------------------------------------------------------
    Invoke-NegativeCase -Name 'neg-path-escape' -ExpectedCode 'PATH_ESCAPE_DETECTED' -FixtureDir (Join-Path $baseRoot '..\..\escaped') -Mutator {
        param($root)
    }

    # -------------------------------------------------------------------------
    # Group C: Single-Byte Drift Negatives (Default Full Run with Hash Check)
    # -------------------------------------------------------------------------
    Invoke-NegativeCase -Name 'neg-single-byte-drift-movement' -ExpectedCode 'HASH_MISMATCH' -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\movement-v1.json'
        $txt = [System.IO.File]::ReadAllText($p, [System.Text.Encoding]::UTF8)
        $txt = $txt.Substring(0, $txt.Length - 10) + 'X' + $txt.Substring($txt.Length - 9)
        [System.IO.File]::WriteAllText($p, $txt, [System.Text.Encoding]::UTF8)
    }

    Invoke-NegativeCase -Name 'neg-single-byte-drift-shuffle-reversed' -ExpectedCode 'HASH_MISMATCH' -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\shuffle-reversed-v1.json'
        $txt = [System.IO.File]::ReadAllText($p, [System.Text.Encoding]::UTF8)
        $txt = $txt.Substring(0, $txt.Length - 10) + 'X' + $txt.Substring($txt.Length - 9)
        [System.IO.File]::WriteAllText($p, $txt, [System.Text.Encoding]::UTF8)
    }

    Invoke-NegativeCase -Name 'neg-single-byte-drift-visibility' -ExpectedCode 'HASH_MISMATCH' -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\visibility-v1.json'
        $txt = [System.IO.File]::ReadAllText($p, [System.Text.Encoding]::UTF8)
        $txt = $txt.Substring(0, $txt.Length - 10) + 'X' + $txt.Substring($txt.Length - 9)
        [System.IO.File]::WriteAllText($p, $txt, [System.Text.Encoding]::UTF8)
    }

    Invoke-NegativeCase -Name 'neg-single-byte-drift-ai-discard-flags' -ExpectedCode 'HASH_MISMATCH' -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\ai-discard-flags-v1.json'
        $txt = [System.IO.File]::ReadAllText($p, [System.Text.Encoding]::UTF8)
        $txt = $txt.Substring(0, $txt.Length - 10) + 'X' + $txt.Substring($txt.Length - 9)
        [System.IO.File]::WriteAllText($p, $txt, [System.Text.Encoding]::UTF8)
    }

    # -------------------------------------------------------------------------
    # Group D: Truncated & Invalid JSON Syntax Negatives (Tested with -SkipHashCheck)
    # -------------------------------------------------------------------------
    Invoke-NegativeCase -Name 'neg-truncated-json' -ExpectedCode 'INVALID_JSON' -SkipHashCheck -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\movement-v1.json'
        $txt = [System.IO.File]::ReadAllText($p, [System.Text.Encoding]::UTF8)
        [System.IO.File]::WriteAllText($p, $txt.Substring(0, 150), [System.Text.Encoding]::UTF8)
    }

    Invoke-NegativeCase -Name 'neg-corrupted-json-syntax' -ExpectedCode 'INVALID_JSON' -SkipHashCheck -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\ai-discard-flags-v1.json'
        $txt = "{\`"fixture\`": UNQUOTED_VALUE"
        [System.IO.File]::WriteAllText($p, $txt, [System.Text.Encoding]::UTF8)
    }

    # -------------------------------------------------------------------------
    # Group E: Movement Fixture Semantic & Structural Negatives (Tested with -SkipHashCheck)
    # -------------------------------------------------------------------------
    Invoke-NegativeCase -Name 'neg-movement-schema-mismatch' -ExpectedCode 'SCHEMA_MISMATCH' -SkipHashCheck -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\movement-v1.json'
        $j = Read-JsonFile $p
        $j.schema = 'corrupted-movement-schema'
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-movement-root-case-count-mismatch' -ExpectedCode 'CASE_COUNT_MISMATCH' -SkipHashCheck -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\movement-v1.json'
        $j = Read-JsonFile $p
        $j.caseCount = 9
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-movement-cases-array-mismatch' -ExpectedCode 'CASE_COUNT_MISMATCH' -SkipHashCheck -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\movement-v1.json'
        $j = Read-JsonFile $p
        $j.cases = @($j.cases)[0..6]
        $j.caseCount = 7
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-movement-duplicate-case-id' -ExpectedCode 'DUPLICATE_CASE_ID' -SkipHashCheck -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\movement-v1.json'
        $j = Read-JsonFile $p
        $j.cases[1].caseId = $j.cases[0].caseId
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-movement-source-commit-zero' -ExpectedCode 'SOURCE_COMMIT_INVALID' -SkipHashCheck -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\movement-v1.json'
        $j = Read-JsonFile $p
        $j.v4Commit = '0000000000000000000000000000000000000000'
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-movement-generator-mismatch' -ExpectedCode 'GENERATOR_MISMATCH' -SkipHashCheck -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\movement-v1.json'
        $j = Read-JsonFile $p
        $j.generator = 'engine/src/test/kotlin/com/majiang/model/FakeExporter.kt'
        Write-JsonFile $p $j
    }

    # -------------------------------------------------------------------------
    # Group F: Shuffle Reversed Fixture Semantic & Structural Negatives (Tested with -SkipHashCheck)
    # -------------------------------------------------------------------------
    Invoke-NegativeCase -Name 'neg-shuffle-schema-mismatch' -ExpectedCode 'SCHEMA_MISMATCH' -SkipHashCheck -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\shuffle-reversed-v1.json'
        $j = Read-JsonFile $p
        $j.schema = 'corrupted-shuffle-schema'
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-shuffle-source-commit-zero' -ExpectedCode 'SOURCE_COMMIT_INVALID' -SkipHashCheck -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\shuffle-reversed-v1.json'
        $j = Read-JsonFile $p
        $j.v4BaseCommit = '0000000000000000000000000000000000000000'
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-shuffle-case-count-mismatch' -ExpectedCode 'CASE_COUNT_MISMATCH' -SkipHashCheck -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\shuffle-reversed-v1.json'
        $j = Read-JsonFile $p
        $j.cases = @($j.cases)[0..30]
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-shuffle-duplicate-seed' -ExpectedCode 'DUPLICATE_CASE_ID' -SkipHashCheck -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\shuffle-reversed-v1.json'
        $j = Read-JsonFile $p
        $j.cases[1].seed = $j.cases[0].seed
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-shuffle-wall-size-invalid' -ExpectedCode 'WALL_SIZE_INVALID' -SkipHashCheck -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\shuffle-reversed-v1.json'
        $j = Read-JsonFile $p
        $j.cases[0].threeCasterWall = @($j.cases[0].threeCasterWall)[0..134]
        Write-JsonFile $p $j
    }

    # -------------------------------------------------------------------------
    # Group G: Visibility Fixture Semantic & Structural Negatives (Tested with -SkipHashCheck)
    # -------------------------------------------------------------------------
    Invoke-NegativeCase -Name 'neg-visibility-schema-mismatch' -ExpectedCode 'SCHEMA_MISMATCH' -SkipHashCheck -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\visibility-v1.json'
        $j = Read-JsonFile $p
        $j.schema = 'corrupted-vis-schema'
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-visibility-source-commit-zero' -ExpectedCode 'SOURCE_COMMIT_INVALID' -SkipHashCheck -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\visibility-v1.json'
        $j = Read-JsonFile $p
        $j.v4SourceCommit = '0000000000000000000000000000000000000000'
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-visibility-render-sha-mismatch' -ExpectedCode 'RENDER_SHA_MISMATCH' -SkipHashCheck -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\visibility-v1.json'
        $j = Read-JsonFile $p
        $j.renderSha256LfNormalized = '0000000000000000000000000000000000000000000000000000000000000000'
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-visibility-case-count-mismatch' -ExpectedCode 'CASE_COUNT_MISMATCH' -SkipHashCheck -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\visibility-v1.json'
        $j = Read-JsonFile $p
        $j.cases = @($j.cases)[0..2]
        $j.caseCount = 3
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-visibility-duplicate-case-id' -ExpectedCode 'DUPLICATE_CASE_ID' -SkipHashCheck -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\visibility-v1.json'
        $j = Read-JsonFile $p
        $j.cases[1].id = $j.cases[0].id
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-visibility-missing-input' -ExpectedCode 'CASE_STRUCTURE_INVALID' -SkipHashCheck -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\visibility-v1.json'
        $j = Read-JsonFile $p
        $j.cases[0].input = $null
        Write-JsonFile $p $j
    }

    # -------------------------------------------------------------------------
    # Group H: AI Discard Flags Semantic & Structural Negatives (Tested with -SkipHashCheck)
    # -------------------------------------------------------------------------
    Invoke-NegativeCase -Name 'neg-ai-discard-fixture-identity-mismatch' -ExpectedCode 'FIXTURE_IDENTITY_MISMATCH' -SkipHashCheck -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\ai-discard-flags-v1.json'
        $j = Read-JsonFile $p
        $j.fixture = 'corrupted-ai-fixture'
        Write-JsonFile $p $j
    }

    # D-R2-02 counterexample reproduction: provenance.generatedAtHead set to 40 zeroes
    Invoke-NegativeCase -Name 'neg-ai-discard-provenance-sha-zero' -ExpectedCode 'PROVENANCE_SHA_INVALID' -SkipHashCheck -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\ai-discard-flags-v1.json'
        $j = Read-JsonFile $p
        $j.provenance.generatedAtHead = '0000000000000000000000000000000000000000'
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-ai-discard-v4-engine-commit-zero' -ExpectedCode 'PROVENANCE_SHA_INVALID' -SkipHashCheck -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\ai-discard-flags-v1.json'
        $j = Read-JsonFile $p
        $j.provenance.v4EngineLastCommit = '0000000000000000000000000000000000000000'
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-ai-discard-render-sha-mismatch' -ExpectedCode 'RENDER_SHA_MISMATCH' -SkipHashCheck -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\ai-discard-flags-v1.json'
        $j = Read-JsonFile $p
        $j.provenance.renderSha256 = '0000000000000000000000000000000000000000000000000000000000000000'
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-ai-discard-case-count-mismatch' -ExpectedCode 'CASE_COUNT_MISMATCH' -SkipHashCheck -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\ai-discard-flags-v1.json'
        $j = Read-JsonFile $p
        $j.cases = @($j.cases)[0..1]
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-ai-discard-duplicate-case-id' -ExpectedCode 'DUPLICATE_CASE_ID' -SkipHashCheck -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\ai-discard-flags-v1.json'
        $j = Read-JsonFile $p
        $j.cases[1].caseId = $j.cases[0].caseId
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-ai-discard-missing-flag-semantics' -ExpectedCode 'STRUCTURE_INVALID' -SkipHashCheck -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\ai-discard-flags-v1.json'
        $j = Read-JsonFile $p
        $j.flagSemantics = $null
        Write-JsonFile $p $j
    }

    # -------------------------------------------------------------------------
    # Group I: Duplicate Fixture Identity Collision
    # -------------------------------------------------------------------------
    Invoke-NegativeCase -Name 'neg-duplicate-fixture-identity-collision' -ExpectedCode 'DUPLICATE_FIXTURE_IDENTITY' -SkipHashCheck -Mutator {
        param($root)
        $src = Join-Path $root 'tests\v5-golden\movement-v1.json'
        $dst = Join-Path $root 'tests\v5-golden\shuffle-reversed-v1.json'
        Copy-Item -LiteralPath $src -Destination $dst -Force
    }
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}

# -------------------------------------------------------------------------
# Real Repository Fixture Immutability Verification
# -------------------------------------------------------------------------
$afterHashes = @{}
foreach ($name in $realCompanionFixtures) {
    $fixturePath = Join-Path $repo ('tests\v5-golden\' + $name)
    $afterHashes[$name] = Get-Sha256 $fixturePath
    if ($beforeHashes[$name] -ne $afterHashes[$name]) {
        Add-Failure ('REAL_FIXTURE_MUTATED: {0} hash changed from {1} to {2}' -f $name, $beforeHashes[$name], $afterHashes[$name])
    }
}

# -------------------------------------------------------------------------
# Output and Log Persistence
# -------------------------------------------------------------------------
$logLines = @()
$logLines += ('# V5 Companion Fixtures Validator Regression Test Run - {0}' -f (Get-Date -Format 'o'))
$logLines += ('RepoRoot: {0}' -f $repo)
$logLines += ('LogPath: {0}' -f $LogPath)
$logLines += ('Real fixtures immutable: {0}' -f ($script:Failures.Count -eq 0))
$logLines += ''

foreach ($c in $script:CaseResults) {
    $statusStr = if ($c.Ok) { 'PASS' } else { 'FAIL' }
    $logLines += ('[{0}] {1} (Expected: {2}, Exit: {3})' -f $statusStr, $c.Name, $c.Expected, $c.ExitCode)
    $logLines += $c.Output
    $logLines += ''
}

if ($script:Failures.Count -gt 0) {
    $logLines += '=========================================================='
    $logLines += ('FAILURES DETECTED ({0}):' -f $script:Failures.Count)
    foreach ($f in $script:Failures) {
        $logLines += ('  - {0}' -f $f)
    }
    $logText = $logLines -join "`n"
    [System.IO.File]::WriteAllText($LogPath, $logText, (New-Object System.Text.UTF8Encoding($false)))
    Write-Output $logText
    exit 1
}

$positiveCount = @($script:CaseResults | Where-Object { $_.Name -like 'positive*' }).Count
$negativeCount = @($script:CaseResults | Where-Object { $_.Name -like 'neg*' }).Count
$totalCount = $script:CaseResults.Count

$summaryLine = ('SELFTEST PASS: total={0} positives={1} negatives={2} failures=0 real_fixtures_unchanged=true log={3}' -f $totalCount, $positiveCount, $negativeCount, $LogPath)
$logLines += $summaryLine
$logText = $logLines -join "`n"
[System.IO.File]::WriteAllText($LogPath, $logText, (New-Object System.Text.UTF8Encoding($false)))

Write-Output $summaryLine
exit 0
