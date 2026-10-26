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
covering:
  - Exact raw byte count and raw SHA-256 drift across accepted Git LF and Windows CRLF profiles
  - Rejection of BOM and non-UTF-8 encodings (including UTF-16LE and UTF-8 with BOM)
  - Rejection of mixed line endings
  - Rejection of corrupt business data in full CLI runs without fake PASS bypass
  - Verification that 136-tile physical conservation violations are caught
  - Missing files, empty files, truncated/invalid JSON
  - Lexical path traversal and NTFS directory junction / reparse point escapes
  - Schema and fixture identity mismatches and duplicate fixture identity collisions
  - Invalid/zero source and provenance commit SHAs (including the D-R2-02 counterexample)
  - Case count mismatches, duplicate case identifiers, and missing required case fields
  - Structural test mode isolation (-InternalStructureTestMode) without golden certification
  - Tooling prechecks: missing git dependency exiting with BLOCKED code 2
  - Safe log path boundary: preventing LogPath from overwriting fixtures or repo files
  - Safe cleanup assertions ensuring recursive deletions remain strictly within isolated GUID roots

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

$canonicalRepo = [System.IO.Path]::GetFullPath($repo)
if (-not $canonicalRepo.EndsWith([System.IO.Path]::DirectorySeparatorChar.ToString())) {
    $canonicalRepo += [System.IO.Path]::DirectorySeparatorChar
}

if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $artifactDir = Join-Path $canonicalRepo 'build\companion-fixtures'
    if (-not (Test-Path -LiteralPath $artifactDir)) {
        New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null
    }
    $LogPath = Join-Path $artifactDir 'selftest.log'
} else {
    $resolvedLog = [System.IO.Path]::GetFullPath($LogPath)
    $goldenDir = [System.IO.Path]::GetFullPath((Join-Path $canonicalRepo 'tests\v5-golden'))
    $toolsDir = [System.IO.Path]::GetFullPath((Join-Path $canonicalRepo 'tools'))
    $allowedBuild = [System.IO.Path]::GetFullPath((Join-Path $canonicalRepo 'build'))
    $allowedArtifacts = [System.IO.Path]::GetFullPath((Join-Path $canonicalRepo 'artifacts'))

    if ($resolvedLog.StartsWith($goldenDir, [System.StringComparison]::OrdinalIgnoreCase) -or
        $resolvedLog.StartsWith($toolsDir, [System.StringComparison]::OrdinalIgnoreCase) -or
        ($resolvedLog.StartsWith($canonicalRepo, [System.StringComparison]::OrdinalIgnoreCase) -and
         -not ($resolvedLog.StartsWith($allowedBuild, [System.StringComparison]::OrdinalIgnoreCase) -or
               $resolvedLog.StartsWith($allowedArtifacts, [System.StringComparison]::OrdinalIgnoreCase)))) {
        Write-Output ("BLOCKED: LogPath '{0}' cannot overwrite golden fixtures, tools, or protected repository files" -f $LogPath)
        exit 2
    }
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
    $text = $Value | ConvertTo-Json -Depth 50
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
    $fixturePath = Join-Path $canonicalRepo ('tests\v5-golden\' + $name)
    if (-not (Test-Path -LiteralPath $fixturePath)) {
        Write-Output ("BLOCKED: required companion fixture missing from repo: '{0}'" -f $fixturePath)
        exit 2
    }
    $beforeHashes[$name] = Get-Sha256 $fixturePath
}

$validatorPathInRepo = Join-Path $canonicalRepo 'tools\verify-v5-companion-fixtures.ps1'
if (Test-Path -LiteralPath $validatorPathInRepo) {
    $beforeHashes['verify-v5-companion-fixtures.ps1'] = Get-Sha256 $validatorPathInRepo
}

$tempRoot = Join-Path $env:TEMP ('v5-companion-fixtures-selftest-' + [guid]::NewGuid().ToString('N'))
$baseRoot = Join-Path $tempRoot 'base'
$caseRoot = Join-Path $tempRoot 'case'

function Assert-WithinGuidRoot {
    param([string]$Path, [string]$GuidRoot)
    if ([string]::IsNullOrWhiteSpace($Path) -or [string]::IsNullOrWhiteSpace($GuidRoot)) {
        throw "Assertion failed: path or guidRoot is empty"
    }
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullRoot = [System.IO.Path]::GetFullPath($GuidRoot)
    if (-not $fullPath.StartsWith($fullRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw ("Safety boundary violation: path '{0}' is outside GuidRoot '{1}'" -f $fullPath, $fullRoot)
    }
}

function Remove-TreeSafely {
    param([string]$Path, [string]$GuidRoot)
    Assert-WithinGuidRoot -Path $Path -GuidRoot $GuidRoot
    if (-not (Test-Path -LiteralPath $Path)) { return }
    Get-ChildItem -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue | Where-Object {
        $null -ne $_.Attributes -and (($_.Attributes.value__ -band [System.IO.FileAttributes]::ReparsePoint.value__) -ne 0)
    } | ForEach-Object {
        $_.Delete()
    }
    Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue
}

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
    Remove-TreeSafely -Path $Case -GuidRoot $tempRoot
    Copy-Item -LiteralPath $Base -Destination $Case -Recurse -Force
}

$psExe = Join-Path $PSHOME 'powershell.exe'

function Invoke-Validator {
    param(
        [string]$Root,
        [string]$FixtureDir = '',
        [switch]$InternalStructureTestMode,
        [switch]$VerifyGitPins,
        [string]$CustomEnvPath = ''
    )
    $validator = Join-Path $Root 'tools\verify-v5-companion-fixtures.ps1'
    $argsList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $validator, '-RepoRoot', $Root)
    if (-not [string]::IsNullOrWhiteSpace($FixtureDir)) {
        $argsList += @('-FixtureDir', $FixtureDir)
    }
    if ($InternalStructureTestMode) {
        $argsList += '-InternalStructureTestMode'
    }
    if ($VerifyGitPins) {
        $argsList += '-VerifyGitPins'
    }

    $savedPath = $null
    if (-not [string]::IsNullOrWhiteSpace($CustomEnvPath)) {
        $savedPath = $env:PATH
        $env:PATH = $CustomEnvPath
    }
    try {
        $output = & $psExe @argsList 2>&1
        $code = $LASTEXITCODE
        return [pscustomobject]@{
            Code = $code
            Output = (@($output) -join "`n")
        }
    }
    finally {
        if ($null -ne $savedPath) {
            $env:PATH = $savedPath
        }
    }
}

$script:CaseResults = @()

function Invoke-NegativeCase {
    param(
        [string]$Name,
        [string]$ExpectedCode,
        [scriptblock]$Mutator,
        [string]$FixtureDir = '',
        [switch]$InternalStructureTestMode,
        [switch]$VerifyGitPins,
        [int]$ExpectedExitCode = 1,
        [string]$CustomEnvPath = ''
    )
    New-CaseTree -Base $baseRoot -Case $caseRoot
    & $Mutator $caseRoot
    $result = Invoke-Validator -Root $caseRoot -FixtureDir $FixtureDir -InternalStructureTestMode:$InternalStructureTestMode -VerifyGitPins:$VerifyGitPins -CustomEnvPath $CustomEnvPath
    $ok = ($result.Code -eq $ExpectedExitCode) -and $result.Output.Contains($ExpectedCode)
    $script:CaseResults += [pscustomobject]@{
        Name = $Name
        Expected = $ExpectedCode
        ExitCode = $result.Code
        Ok = $ok
        Output = $result.Output
    }
    if (-not $ok) {
        Add-Failure ('{0}: expected exit{1} containing {2}, got exit{3}; output={4}' -f $Name, $ExpectedExitCode, $ExpectedCode, $result.Code, $result.Output)
    }
}

try {
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
    New-BaseTree -Root $baseRoot -SourceRepo $canonicalRepo

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
    $pos2 = Invoke-Validator -Root $canonicalRepo -VerifyGitPins
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
    # Group B: Path Escape & Reparse Point Negatives
    # -------------------------------------------------------------------------
    Invoke-NegativeCase -Name 'neg-path-escape-lexical' -ExpectedCode 'PATH_ESCAPE_DETECTED' -FixtureDir (Join-Path $baseRoot '..\..\escaped') -Mutator {
        param($root)
    }

    Invoke-NegativeCase -Name 'neg-path-escape-junction' -ExpectedCode 'PATH_ESCAPE_DETECTED' -Mutator {
        param($root)
        $externalDir = Join-Path $tempRoot 'outside-junction-target'
        New-Item -ItemType Directory -Force -Path $externalDir | Out-Null
        $juncTarget = Join-Path $root 'tests\escaped-junc'
        New-Item -ItemType Junction -Path $juncTarget -Target $externalDir | Out-Null
    } -FixtureDir (Join-Path $caseRoot 'tests\escaped-junc')

    # -------------------------------------------------------------------------
    # Group C: Encoding & Single-Byte Drift Negatives (Default Full Run with Hash Check)
    # -------------------------------------------------------------------------
    Invoke-NegativeCase -Name 'neg-encoding-utf16-le' -ExpectedCode 'INVALID_ENCODING' -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\movement-v1.json'
        $txt = [System.IO.File]::ReadAllText($p)
        [System.IO.File]::WriteAllText($p, $txt, [System.Text.Encoding]::Unicode)
    }

    Invoke-NegativeCase -Name 'neg-encoding-utf8-bom' -ExpectedCode 'INVALID_ENCODING' -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\movement-v1.json'
        $raw = [System.IO.File]::ReadAllBytes($p)
        $bomBytes = [byte[]](0xEF, 0xBB, 0xBF)
        $combined = New-Object byte[] ($bomBytes.Length + $raw.Length)
        [System.Buffer]::BlockCopy($bomBytes, 0, $combined, 0, $bomBytes.Length)
        [System.Buffer]::BlockCopy($raw, 0, $combined, $bomBytes.Length, $raw.Length)
        [System.IO.File]::WriteAllBytes($p, $combined)
    }

    Invoke-NegativeCase -Name 'neg-encoding-mixed-eol' -ExpectedCode 'HASH_MISMATCH' -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\movement-v1.json'
        $txt = [System.IO.File]::ReadAllText($p).Replace("`r`n", "`n")
        $firstNl = $txt.IndexOf("`n")
        $txt = $txt.Substring(0, $firstNl) + "`r`n" + $txt.Substring($firstNl + 1)
        [System.IO.File]::WriteAllText($p, $txt, (New-Object System.Text.UTF8Encoding($false)))
    }

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
    # Group D: Conservation Violation & Anti-Fraud (Full CLI Run vs Internal Structure Test Mode)
    # -------------------------------------------------------------------------
    Invoke-NegativeCase -Name 'neg-duplicate-tile-cli-full' -ExpectedCode 'HASH_MISMATCH' -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\shuffle-reversed-v1.json'
        $j = Read-JsonFile $p
        $j.cases[0].threeCasterWall[0] = $j.cases[0].threeCasterWall[1]
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-duplicate-tile-structure-mode' -ExpectedCode 'WALL_CONSERVATION_INVALID' -InternalStructureTestMode -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\shuffle-reversed-v1.json'
        $j = Read-JsonFile $p
        $j.cases[0].threeCasterWall[0] = $j.cases[0].threeCasterWall[1]
        Write-JsonFile $p $j
    }

    # -------------------------------------------------------------------------
    # Group E: Truncated & Invalid JSON Syntax Negatives (Tested with -InternalStructureTestMode)
    # -------------------------------------------------------------------------
    Invoke-NegativeCase -Name 'neg-truncated-json' -ExpectedCode 'INVALID_JSON' -InternalStructureTestMode -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\movement-v1.json'
        $txt = [System.IO.File]::ReadAllText($p, [System.Text.Encoding]::UTF8)
        [System.IO.File]::WriteAllText($p, $txt.Substring(0, 150), [System.Text.Encoding]::UTF8)
    }

    Invoke-NegativeCase -Name 'neg-corrupted-json-syntax' -ExpectedCode 'INVALID_JSON' -InternalStructureTestMode -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\ai-discard-flags-v1.json'
        $txt = "{\`"fixture\`": UNQUOTED_VALUE"
        [System.IO.File]::WriteAllText($p, $txt, [System.Text.Encoding]::UTF8)
    }

    # -------------------------------------------------------------------------
    # Group F: Movement Fixture Semantic & Structural Negatives (Tested with -InternalStructureTestMode)
    # -------------------------------------------------------------------------
    Invoke-NegativeCase -Name 'neg-movement-schema-mismatch' -ExpectedCode 'SCHEMA_MISMATCH' -InternalStructureTestMode -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\movement-v1.json'
        $j = Read-JsonFile $p
        $j.schema = 'corrupted-movement-schema'
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-movement-root-case-count-mismatch' -ExpectedCode 'CASE_COUNT_MISMATCH' -InternalStructureTestMode -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\movement-v1.json'
        $j = Read-JsonFile $p
        $j.caseCount = 9
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-movement-cases-array-mismatch' -ExpectedCode 'CASE_COUNT_MISMATCH' -InternalStructureTestMode -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\movement-v1.json'
        $j = Read-JsonFile $p
        $j.cases = @($j.cases)[0..6]
        $j.caseCount = 7
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-movement-duplicate-case-id' -ExpectedCode 'DUPLICATE_CASE_ID' -InternalStructureTestMode -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\movement-v1.json'
        $j = Read-JsonFile $p
        $j.cases[1].caseId = $j.cases[0].caseId
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-movement-source-commit-zero' -ExpectedCode 'SOURCE_COMMIT_INVALID' -InternalStructureTestMode -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\movement-v1.json'
        $j = Read-JsonFile $p
        $j.v4Commit = '0000000000000000000000000000000000000000'
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-movement-generator-mismatch' -ExpectedCode 'GENERATOR_MISMATCH' -InternalStructureTestMode -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\movement-v1.json'
        $j = Read-JsonFile $p
        $j.generator = 'engine/src/test/kotlin/com/majiang/model/FakeExporter.kt'
        Write-JsonFile $p $j
    }

    # -------------------------------------------------------------------------
    # Group G: Shuffle Reversed Fixture Semantic & Structural Negatives (Tested with -InternalStructureTestMode)
    # -------------------------------------------------------------------------
    Invoke-NegativeCase -Name 'neg-shuffle-schema-mismatch' -ExpectedCode 'SCHEMA_MISMATCH' -InternalStructureTestMode -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\shuffle-reversed-v1.json'
        $j = Read-JsonFile $p
        $j.schema = 'corrupted-shuffle-schema'
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-shuffle-source-commit-zero' -ExpectedCode 'SOURCE_COMMIT_INVALID' -InternalStructureTestMode -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\shuffle-reversed-v1.json'
        $j = Read-JsonFile $p
        $j.v4BaseCommit = '0000000000000000000000000000000000000000'
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-shuffle-case-count-mismatch' -ExpectedCode 'CASE_COUNT_MISMATCH' -InternalStructureTestMode -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\shuffle-reversed-v1.json'
        $j = Read-JsonFile $p
        $j.cases = @($j.cases)[0..30]
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-shuffle-duplicate-seed' -ExpectedCode 'DUPLICATE_CASE_ID' -InternalStructureTestMode -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\shuffle-reversed-v1.json'
        $j = Read-JsonFile $p
        $j.cases[1].seed = $j.cases[0].seed
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-shuffle-wall-size-invalid' -ExpectedCode 'WALL_SIZE_INVALID' -InternalStructureTestMode -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\shuffle-reversed-v1.json'
        $j = Read-JsonFile $p
        $j.cases[0].threeCasterWall = @($j.cases[0].threeCasterWall)[0..134]
        Write-JsonFile $p $j
    }

    # -------------------------------------------------------------------------
    # Group H: Visibility Fixture Semantic & Structural Negatives (Tested with -InternalStructureTestMode)
    # -------------------------------------------------------------------------
    Invoke-NegativeCase -Name 'neg-visibility-schema-mismatch' -ExpectedCode 'SCHEMA_MISMATCH' -InternalStructureTestMode -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\visibility-v1.json'
        $j = Read-JsonFile $p
        $j.schema = 'corrupted-vis-schema'
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-visibility-source-commit-zero' -ExpectedCode 'SOURCE_COMMIT_INVALID' -InternalStructureTestMode -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\visibility-v1.json'
        $j = Read-JsonFile $p
        $j.v4SourceCommit = '0000000000000000000000000000000000000000'
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-visibility-render-sha-mismatch' -ExpectedCode 'RENDER_SHA_MISMATCH' -InternalStructureTestMode -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\visibility-v1.json'
        $j = Read-JsonFile $p
        $j.renderSha256LfNormalized = '0000000000000000000000000000000000000000000000000000000000000000'
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-visibility-case-count-mismatch' -ExpectedCode 'CASE_COUNT_MISMATCH' -InternalStructureTestMode -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\visibility-v1.json'
        $j = Read-JsonFile $p
        $j.cases = @($j.cases)[0..2]
        $j.caseCount = 3
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-visibility-duplicate-case-id' -ExpectedCode 'DUPLICATE_CASE_ID' -InternalStructureTestMode -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\visibility-v1.json'
        $j = Read-JsonFile $p
        $j.cases[1].id = $j.cases[0].id
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-visibility-missing-input' -ExpectedCode 'CASE_STRUCTURE_INVALID' -InternalStructureTestMode -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\visibility-v1.json'
        $j = Read-JsonFile $p
        $j.cases[0].input = $null
        Write-JsonFile $p $j
    }

    # -------------------------------------------------------------------------
    # Group I: AI Discard Flags Semantic & Structural Negatives (Tested with -InternalStructureTestMode)
    # -------------------------------------------------------------------------
    Invoke-NegativeCase -Name 'neg-ai-discard-fixture-identity-mismatch' -ExpectedCode 'FIXTURE_IDENTITY_MISMATCH' -InternalStructureTestMode -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\ai-discard-flags-v1.json'
        $j = Read-JsonFile $p
        $j.fixture = 'corrupted-ai-fixture'
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-ai-discard-provenance-sha-zero' -ExpectedCode 'PROVENANCE_SHA_INVALID' -InternalStructureTestMode -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\ai-discard-flags-v1.json'
        $j = Read-JsonFile $p
        $j.provenance.generatedAtHead = '0000000000000000000000000000000000000000'
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-ai-discard-v4-engine-commit-zero' -ExpectedCode 'PROVENANCE_SHA_INVALID' -InternalStructureTestMode -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\ai-discard-flags-v1.json'
        $j = Read-JsonFile $p
        $j.provenance.v4EngineLastCommit = '0000000000000000000000000000000000000000'
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-ai-discard-render-sha-mismatch' -ExpectedCode 'RENDER_SHA_MISMATCH' -InternalStructureTestMode -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\ai-discard-flags-v1.json'
        $j = Read-JsonFile $p
        $j.provenance.renderSha256 = '0000000000000000000000000000000000000000000000000000000000000000'
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-ai-discard-case-count-mismatch' -ExpectedCode 'CASE_COUNT_MISMATCH' -InternalStructureTestMode -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\ai-discard-flags-v1.json'
        $j = Read-JsonFile $p
        $j.cases = @($j.cases)[0..1]
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-ai-discard-duplicate-case-id' -ExpectedCode 'DUPLICATE_CASE_ID' -InternalStructureTestMode -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\ai-discard-flags-v1.json'
        $j = Read-JsonFile $p
        $j.cases[1].caseId = $j.cases[0].caseId
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-ai-discard-missing-flag-semantics' -ExpectedCode 'STRUCTURE_INVALID' -InternalStructureTestMode -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\ai-discard-flags-v1.json'
        $j = Read-JsonFile $p
        $j.flagSemantics = $null
        Write-JsonFile $p $j
    }

    # -------------------------------------------------------------------------
    # Group J: Duplicate Fixture Identity Collision (Tested with -InternalStructureTestMode)
    # -------------------------------------------------------------------------
    Invoke-NegativeCase -Name 'neg-duplicate-fixture-identity-collision' -ExpectedCode 'DUPLICATE_FIXTURE_IDENTITY' -InternalStructureTestMode -Mutator {
        param($root)
        $src = Join-Path $root 'tests\v5-golden\movement-v1.json'
        $dst = Join-Path $root 'tests\v5-golden\shuffle-reversed-v1.json'
        Copy-Item -LiteralPath $src -Destination $dst -Force
    }

    # -------------------------------------------------------------------------
    # Group K: Environment & Tooling Prechecks (Exit code 2 BLOCKED)
    # -------------------------------------------------------------------------
    Invoke-NegativeCase -Name 'neg-missing-git-dependency' -ExpectedCode 'BLOCKED: missing required tool ''git'' for -VerifyGitPins' -VerifyGitPins -ExpectedExitCode 2 -CustomEnvPath "$env:SystemRoot\System32" -Mutator {
        param($root)
    }

    # -------------------------------------------------------------------------
    # Group L: Test Script LogPath Protection Precheck (Exit code 2 BLOCKED)
    # -------------------------------------------------------------------------
    $testScriptPath = $PSCommandPath
    $protectedLogTarget = Join-Path $canonicalRepo 'tests\v5-golden\movement-v1.json'
    $logTestOut = & $psExe -NoProfile -ExecutionPolicy Bypass -File $testScriptPath -RepoRoot $canonicalRepo -LogPath $protectedLogTarget 2>&1
    $logTestCode = $LASTEXITCODE
    $logTestText = (@($logTestOut) -join "`n")
    $logTestOk = ($logTestCode -eq 2) -and $logTestText.Contains('cannot overwrite golden fixtures')
    $script:CaseResults += [pscustomobject]@{
        Name = 'neg-test-logpath-target-protected'
        Expected = 'BLOCKED'
        ExitCode = $logTestCode
        Ok = $logTestOk
        Output = $logTestText
    }
    if (-not $logTestOk) {
        Add-Failure ('neg-test-logpath-target-protected: expected exit 2 containing BLOCKED, got exit {0}; output={1}' -f $logTestCode, $logTestText)
    }
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-TreeSafely -Path $tempRoot -GuidRoot $tempRoot
    }
}

# -------------------------------------------------------------------------
# Real Repository Fixture and Tool Immutability Verification
# -------------------------------------------------------------------------
$afterHashes = @{}
foreach ($name in $realCompanionFixtures) {
    $fixturePath = Join-Path $canonicalRepo ('tests\v5-golden\' + $name)
    $afterHashes[$name] = Get-Sha256 $fixturePath
    if ($beforeHashes[$name] -ne $afterHashes[$name]) {
        Add-Failure ('REAL_FIXTURE_MUTATED: {0} hash changed from {1} to {2}' -f $name, $beforeHashes[$name], $afterHashes[$name])
    }
}

if ($beforeHashes.ContainsKey('verify-v5-companion-fixtures.ps1')) {
    $currentValidatorHash = Get-Sha256 $validatorPathInRepo
    if ($beforeHashes['verify-v5-companion-fixtures.ps1'] -ne $currentValidatorHash) {
        Add-Failure ('REAL_VALIDATOR_MUTATED: verify-v5-companion-fixtures.ps1 hash changed from {0} to {1}' -f $beforeHashes['verify-v5-companion-fixtures.ps1'], $currentValidatorHash)
    }
}

# -------------------------------------------------------------------------
# Output and Log Persistence
# -------------------------------------------------------------------------
$logLines = @()
$logLines += ('# V5 Companion Fixtures Validator Regression Test Run - {0}' -f (Get-Date -Format 'o'))
$logLines += ('RepoRoot: {0}' -f $canonicalRepo)
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
