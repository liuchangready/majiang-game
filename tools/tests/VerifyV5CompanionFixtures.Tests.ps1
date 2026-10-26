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
  - Verification that all four fixtures in pure LF profile pass with native exit 0
  - Verification that all four fixtures in pure CRLF profile pass with native exit 0
  - Rejection of BOM and non-UTF-8 encodings (including UTF-16LE and UTF-8 with BOM) across ALL 4 fixtures
  - Rejection of mixed line endings across ALL 4 fixtures
  - Rejection of single-byte drift across ALL 4 fixtures
  - Rejection of corrupt business data in full CLI runs without fake PASS bypass
  - Verification that 136-tile physical conservation violations are caught (duplicate, <0, >135, non-integer)
  - Missing files, empty files, truncated/invalid JSON
  - Lexical path traversal and NTFS directory junction / reparse point escapes
  - Schema and fixture identity mismatches and duplicate fixture identity collisions
  - Invalid/zero source and provenance commit SHAs (including the D-R2-02 counterexample)
  - Case count mismatches, duplicate case identifiers, and missing required case fields
  - Structural test mode isolation (-InternalStructureTestMode) without golden certification
  - Tooling prechecks: missing git dependency exiting with BLOCKED code 2
  - Safe log path boundary: preventing LogPath from overwriting fixtures, repo files, build.gradle, or junction aliases
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

function Test-PathHasReparsePoint {
    param([string]$PathToCheck)
    if ([string]::IsNullOrWhiteSpace($PathToCheck)) { return $false }
    $curr = [System.IO.Path]::GetFullPath($PathToCheck)
    while (-not [string]::IsNullOrWhiteSpace($curr)) {
        if (Test-Path -LiteralPath $curr) {
            $attr = [System.IO.File]::GetAttributes($curr)
            if (($attr.value__ -band [System.IO.FileAttributes]::ReparsePoint.value__) -ne 0) {
                return $true
            }
        }
        $parent = [System.IO.Path]::GetDirectoryName($curr)
        if ($parent -eq $curr -or [string]::IsNullOrWhiteSpace($parent)) { break }
        $curr = $parent
    }
    return $false
}

# -------------------------------------------------------------------------
# LogPath Strict Boundary & Alias Protection
# -------------------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $LogPath = Join-Path $canonicalRepo 'build\companion-fixtures\selftest.log'
}

$resolvedLog = [System.IO.Path]::GetFullPath($LogPath)
$allowedBuildDir = [System.IO.Path]::GetFullPath((Join-Path $canonicalRepo 'build')) + [System.IO.Path]::DirectorySeparatorChar
$allowedArtifactsDir = [System.IO.Path]::GetFullPath((Join-Path $canonicalRepo 'artifacts')) + [System.IO.Path]::DirectorySeparatorChar

# 1. Strictly inside repository allowed build\ or artifacts\ subdirectories
$isAllowedDir = $resolvedLog.StartsWith($allowedBuildDir, [System.StringComparison]::OrdinalIgnoreCase) -or
                $resolvedLog.StartsWith($allowedArtifactsDir, [System.StringComparison]::OrdinalIgnoreCase)
if (-not $isAllowedDir) {
    Write-Output ("BLOCKED: LogPath '{0}' must be located strictly inside 'build\' or 'artifacts\'" -f $LogPath)
    exit 2
}

# 2. Reject reparse points or junction aliases on path or ancestors
if (Test-PathHasReparsePoint $resolvedLog) {
    Write-Output ("BLOCKED: LogPath '{0}' contains reparse points or junction aliases" -f $LogPath)
    exit 2
}

# 3. Reject target if it already exists as a container
if (Test-Path -LiteralPath $resolvedLog -PathType Container) {
    Write-Output ("BLOCKED: LogPath '{0}' cannot be a directory" -f $LogPath)
    exit 2
}

# 4. Safe to ensure parent directory exists
$logParent = [System.IO.Path]::GetDirectoryName($resolvedLog)
if (-not [string]::IsNullOrWhiteSpace($logParent) -and -not (Test-Path -LiteralPath $logParent)) {
    New-Item -ItemType Directory -Force -Path $logParent | Out-Null
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
$buildGradleInRepo = Join-Path $canonicalRepo 'build.gradle'
if (Test-Path -LiteralPath $buildGradleInRepo) {
    $beforeHashes['build.gradle'] = Get-Sha256 $buildGradleInRepo
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
    $rootWithSlash = if ($fullRoot.EndsWith([System.IO.Path]::DirectorySeparatorChar.ToString())) {
        $fullRoot
    } else {
        $fullRoot + [System.IO.Path]::DirectorySeparatorChar
    }
    $isInside = ($fullPath -eq $fullRoot) -or $fullPath.StartsWith($rootWithSlash, [System.StringComparison]::OrdinalIgnoreCase)
    if (-not $isInside) {
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
    # Positive Baselines (LF profile, CRLF profile, Real repo git pins)
    # -------------------------------------------------------------------------
    # Baseline 1: In isolated TEMP sandbox with pure LF encoding on all 4 fixtures
    $lfRoot = Join-Path $tempRoot 'baseline-lf'
    New-BaseTree -Root $lfRoot -SourceRepo $canonicalRepo
    foreach ($n in $realCompanionFixtures) {
        $fp = Join-Path $lfRoot ('tests\v5-golden\' + $n)
        $t = [System.IO.File]::ReadAllText($fp).Replace("`r`n", "`n")
        [System.IO.File]::WriteAllText($fp, $t, (New-Object System.Text.UTF8Encoding($false)))
    }
    $posLf = Invoke-Validator -Root $lfRoot
    $posLfOk = ($posLf.Code -eq 0) -and $posLf.Output.Contains('PASS: 4/4 companion golden fixtures verified successfully')
    $script:CaseResults += [pscustomobject]@{
        Name = 'positive-all-lf-profile'
        Expected = 'PASS'
        ExitCode = $posLf.Code
        Ok = $posLfOk
        Output = $posLf.Output
    }
    if (-not $posLfOk) {
        Add-Failure ('positive-all-lf-profile: expected exit 0 PASS, got exit {0}; output: {1}' -f $posLf.Code, $posLf.Output)
    }

    # Baseline 2: In isolated TEMP sandbox with pure CRLF encoding on all 4 fixtures
    $crlfRoot = Join-Path $tempRoot 'baseline-crlf'
    New-BaseTree -Root $crlfRoot -SourceRepo $canonicalRepo
    foreach ($n in $realCompanionFixtures) {
        $fp = Join-Path $crlfRoot ('tests\v5-golden\' + $n)
        $t = [System.IO.File]::ReadAllText($fp).Replace("`r`n", "`n").Replace("`n", "`r`n")
        [System.IO.File]::WriteAllText($fp, $t, (New-Object System.Text.UTF8Encoding($false)))
    }
    $posCrlf = Invoke-Validator -Root $crlfRoot
    $posCrlfOk = ($posCrlf.Code -eq 0) -and $posCrlf.Output.Contains('PASS: 4/4 companion golden fixtures verified successfully')
    $script:CaseResults += [pscustomobject]@{
        Name = 'positive-all-crlf-profile'
        Expected = 'PASS'
        ExitCode = $posCrlf.Code
        Ok = $posCrlfOk
        Output = $posCrlf.Output
    }
    if (-not $posCrlfOk) {
        Add-Failure ('positive-all-crlf-profile: expected exit 0 PASS, got exit {0}; output: {1}' -f $posCrlf.Code, $posCrlf.Output)
    }

    # Baseline 3: On real repository with git pins verified
    $posReal = Invoke-Validator -Root $canonicalRepo -VerifyGitPins
    $posRealOk = ($posReal.Code -eq 0) -and $posReal.Output.Contains('PASS: 4/4 companion golden fixtures verified successfully') -and $posReal.Output.Contains('git blobs verified equal')
    $script:CaseResults += [pscustomobject]@{
        Name = 'positive-real-repo-git-pins'
        Expected = 'PASS'
        ExitCode = $posReal.Code
        Ok = $posRealOk
        Output = $posReal.Output
    }
    if (-not $posRealOk) {
        Add-Failure ('positive-real-repo-git-pins: expected exit 0 PASS with git pins, got exit {0}; output: {1}' -f $posReal.Code, $posReal.Output)
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
    # Group C: Encoding Negatives across ALL FOUR Companion Fixtures
    # -------------------------------------------------------------------------
    $companionNames = @('movement-v1', 'shuffle-reversed-v1', 'visibility-v1', 'ai-discard-flags-v1')

    # C1: UTF-16LE with BOM across all 4 fixtures
    foreach ($fn in $companionNames) {
        $cname = "neg-encoding-utf16-$fn"
        $targetFile = "tests\v5-golden\$fn.json"
        Invoke-NegativeCase -Name $cname -ExpectedCode 'INVALID_ENCODING' -Mutator {
            param($root)
            $p = Join-Path $root $targetFile
            $txt = [System.IO.File]::ReadAllText($p)
            [System.IO.File]::WriteAllText($p, $txt, [System.Text.Encoding]::Unicode)
        }
    }

    # C2: UTF-8 with BOM across all 4 fixtures
    foreach ($fn in $companionNames) {
        $cname = "neg-encoding-utf8-bom-$fn"
        $targetFile = "tests\v5-golden\$fn.json"
        Invoke-NegativeCase -Name $cname -ExpectedCode 'INVALID_ENCODING' -Mutator {
            param($root)
            $p = Join-Path $root $targetFile
            $raw = [System.IO.File]::ReadAllBytes($p)
            $bomBytes = [byte[]](0xEF, 0xBB, 0xBF)
            $combined = New-Object byte[] ($bomBytes.Length + $raw.Length)
            [System.Buffer]::BlockCopy($bomBytes, 0, $combined, 0, $bomBytes.Length)
            [System.Buffer]::BlockCopy($raw, 0, $combined, $bomBytes.Length, $raw.Length)
            [System.IO.File]::WriteAllBytes($p, $combined)
        }
    }

    # C3: Mixed line endings across all 4 fixtures
    foreach ($fn in $companionNames) {
        $cname = "neg-encoding-mixed-eol-$fn"
        $targetFile = "tests\v5-golden\$fn.json"
        Invoke-NegativeCase -Name $cname -ExpectedCode 'HASH_MISMATCH' -Mutator {
            param($root)
            $p = Join-Path $root $targetFile
            $txt = [System.IO.File]::ReadAllText($p).Replace("`r`n", "`n")
            $firstNl = $txt.IndexOf("`n")
            $txt = $txt.Substring(0, $firstNl) + "`r`n" + $txt.Substring($firstNl + 1)
            [System.IO.File]::WriteAllText($p, $txt, (New-Object System.Text.UTF8Encoding($false)))
        }
    }

    # -------------------------------------------------------------------------
    # Group D: Single-Byte Drift across ALL FOUR Companion Fixtures
    # -------------------------------------------------------------------------
    foreach ($fn in $companionNames) {
        $cname = "neg-single-byte-drift-$fn"
        $targetFile = "tests\v5-golden\$fn.json"
        Invoke-NegativeCase -Name $cname -ExpectedCode 'HASH_MISMATCH' -Mutator {
            param($root)
            $p = Join-Path $root $targetFile
            $txt = [System.IO.File]::ReadAllText($p, [System.Text.Encoding]::UTF8)
            $txt = $txt.Substring(0, $txt.Length - 10) + 'X' + $txt.Substring($txt.Length - 9)
            [System.IO.File]::WriteAllText($p, $txt, [System.Text.Encoding]::UTF8)
        }
    }

    # -------------------------------------------------------------------------
    # Group E: Physical Conservation & Range Invariants (CLI Fraud & Structure Mode)
    # -------------------------------------------------------------------------
    Invoke-NegativeCase -Name 'neg-shuffle-duplicate-tile-cli-full' -ExpectedCode 'HASH_MISMATCH' -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\shuffle-reversed-v1.json'
        $j = Read-JsonFile $p
        $j.cases[0].threeCasterWall[0] = $j.cases[0].threeCasterWall[1]
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-shuffle-duplicate-tile-structure-mode' -ExpectedCode 'WALL_CONSERVATION_INVALID' -InternalStructureTestMode -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\shuffle-reversed-v1.json'
        $j = Read-JsonFile $p
        $j.cases[0].threeCasterWall[0] = $j.cases[0].threeCasterWall[1]
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-shuffle-out-of-range-tile-structure-mode' -ExpectedCode 'WALL_CONSERVATION_INVALID' -InternalStructureTestMode -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\shuffle-reversed-v1.json'
        $j = Read-JsonFile $p
        $j.cases[0].threeCasterWall = @(136..271)
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-shuffle-negative-tile-structure-mode' -ExpectedCode 'WALL_CONSERVATION_INVALID' -InternalStructureTestMode -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\shuffle-reversed-v1.json'
        $j = Read-JsonFile $p
        $j.cases[0].reversedWall[0] = -1
        Write-JsonFile $p $j
    }

    Invoke-NegativeCase -Name 'neg-shuffle-non-integer-tile-structure-mode' -ExpectedCode 'WALL_CONSERVATION_INVALID' -InternalStructureTestMode -Mutator {
        param($root)
        $p = Join-Path $root 'tests\v5-golden\shuffle-reversed-v1.json'
        $j = Read-JsonFile $p
        $j.cases[0].reversedWall[0] = 'not-a-tile-number'
        Write-JsonFile $p $j
    }

    # -------------------------------------------------------------------------
    # Group F: Truncated & Invalid JSON Syntax Negatives (Tested with -InternalStructureTestMode)
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
    # Group G: Movement Fixture Semantic & Structural Negatives (Tested with -InternalStructureTestMode)
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
    # Group H: Shuffle Reversed Fixture Semantic & Structural Negatives (Tested with -InternalStructureTestMode)
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
    # Group I: Visibility Fixture Semantic & Structural Negatives (Tested with -InternalStructureTestMode)
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
    # Group J: AI Discard Flags Semantic & Structural Negatives (Tested with -InternalStructureTestMode)
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
    # Group K: Duplicate Fixture Identity Collision (Tested with -InternalStructureTestMode)
    # -------------------------------------------------------------------------
    Invoke-NegativeCase -Name 'neg-duplicate-fixture-identity-collision' -ExpectedCode 'DUPLICATE_FIXTURE_IDENTITY' -InternalStructureTestMode -Mutator {
        param($root)
        $src = Join-Path $root 'tests\v5-golden\movement-v1.json'
        $dst = Join-Path $root 'tests\v5-golden\shuffle-reversed-v1.json'
        Copy-Item -LiteralPath $src -Destination $dst -Force
    }

    # -------------------------------------------------------------------------
    # Group L: Environment & Tooling Prechecks (Exit code 2 BLOCKED)
    # -------------------------------------------------------------------------
    Invoke-NegativeCase -Name 'neg-missing-git-dependency' -ExpectedCode 'BLOCKED: missing required tool ''git'' for -VerifyGitPins' -VerifyGitPins -ExpectedExitCode 2 -CustomEnvPath "$env:SystemRoot\System32" -Mutator {
        param($root)
    }

    # -------------------------------------------------------------------------
    # Group M: Test Script LogPath Protection Prechecks (Exit code 2 BLOCKED)
    # -------------------------------------------------------------------------
    $testScriptPath = $PSCommandPath

    # M1: Targeting fixture inside tests/v5-golden
    $protectedFixtureTarget = Join-Path $canonicalRepo 'tests\v5-golden\movement-v1.json'
    $logTestOut1 = & $psExe -NoProfile -ExecutionPolicy Bypass -File $testScriptPath -RepoRoot $canonicalRepo -LogPath $protectedFixtureTarget 2>&1
    $logTestCode1 = $LASTEXITCODE
    $logTestText1 = (@($logTestOut1) -join "`n")
    $logTestOk1 = ($logTestCode1 -eq 2) -and $logTestText1.Contains("strictly inside 'build\' or 'artifacts\'")
    $script:CaseResults += [pscustomobject]@{
        Name = 'neg-test-logpath-target-protected-fixture'
        Expected = 'BLOCKED'
        ExitCode = $logTestCode1
        Ok = $logTestOk1
        Output = $logTestText1
    }
    if (-not $logTestOk1) {
        Add-Failure ('neg-test-logpath-target-protected-fixture: expected exit 2 containing BLOCKED, got exit {0}; output={1}' -f $logTestCode1, $logTestText1)
    }

    # M2: Prefix similarity exploit targeting build.gradle
    $buildGradleTarget = Join-Path $canonicalRepo 'build.gradle'
    $logTestOut2 = & $psExe -NoProfile -ExecutionPolicy Bypass -File $testScriptPath -RepoRoot $canonicalRepo -LogPath $buildGradleTarget 2>&1
    $logTestCode2 = $LASTEXITCODE
    $logTestText2 = (@($logTestOut2) -join "`n")
    $logTestOk2 = ($logTestCode2 -eq 2) -and $logTestText2.Contains("strictly inside 'build\' or 'artifacts\'")
    $script:CaseResults += [pscustomobject]@{
        Name = 'neg-test-logpath-target-build-gradle'
        Expected = 'BLOCKED'
        ExitCode = $logTestCode2
        Ok = $logTestOk2
        Output = $logTestText2
    }
    if (-not $logTestOk2) {
        Add-Failure ('neg-test-logpath-target-build-gradle: expected exit 2 containing BLOCKED, got exit {0}; output={1}' -f $logTestCode2, $logTestText2)
    }

    # M3: Junction alias under build/ pointing to tests/v5-golden
    $probeBuildAliasRoot = Join-Path $tempRoot 'alias-probe-repo'
    New-BaseTree -Root $probeBuildAliasRoot -SourceRepo $canonicalRepo
    $aliasDir = Join-Path $probeBuildAliasRoot 'build\alias'
    New-Item -ItemType Directory -Force -Path (Join-Path $probeBuildAliasRoot 'build') | Out-Null
    New-Item -ItemType Junction -Path $aliasDir -Target (Join-Path $probeBuildAliasRoot 'tests\v5-golden') | Out-Null
    $aliasLogTarget = Join-Path $aliasDir 'movement-v1.json'
    $logTestOut3 = & $psExe -NoProfile -ExecutionPolicy Bypass -File $testScriptPath -RepoRoot $probeBuildAliasRoot -LogPath $aliasLogTarget 2>&1
    $logTestCode3 = $LASTEXITCODE
    $logTestText3 = (@($logTestOut3) -join "`n")
    $logTestOk3 = ($logTestCode3 -eq 2) -and $logTestText3.Contains('contains reparse points or junction aliases')
    $script:CaseResults += [pscustomobject]@{
        Name = 'neg-test-logpath-target-junction-alias'
        Expected = 'BLOCKED'
        ExitCode = $logTestCode3
        Ok = $logTestOk3
        Output = $logTestText3
    }
    if (-not $logTestOk3) {
        Add-Failure ('neg-test-logpath-target-junction-alias: expected exit 2 containing BLOCKED, got exit {0}; output={1}' -f $logTestCode3, $logTestText3)
    }

    # M4: Arbitrary external log location outside repository
    $externalLogTarget = Join-Path $tempRoot 'external-log\selftest.log'
    $logTestOut4 = & $psExe -NoProfile -ExecutionPolicy Bypass -File $testScriptPath -RepoRoot $canonicalRepo -LogPath $externalLogTarget 2>&1
    $logTestCode4 = $LASTEXITCODE
    $logTestText4 = (@($logTestOut4) -join "`n")
    $logTestOk4 = ($logTestCode4 -eq 2) -and $logTestText4.Contains("strictly inside 'build\' or 'artifacts\'")
    $script:CaseResults += [pscustomobject]@{
        Name = 'neg-test-logpath-outside-repo'
        Expected = 'BLOCKED'
        ExitCode = $logTestCode4
        Ok = $logTestOk4
        Output = $logTestText4
    }
    if (-not $logTestOk4) {
        Add-Failure ('neg-test-logpath-outside-repo: expected exit 2 containing BLOCKED, got exit {0}; output={1}' -f $logTestCode4, $logTestText4)
    }

    # -------------------------------------------------------------------------
    # Group N: Cleanup Safety Assertion Boundary Verification
    # -------------------------------------------------------------------------
    $cleanupProbeOk = $false
    try {
        Assert-WithinGuidRoot -Path ($tempRoot + '-sibling') -GuidRoot $tempRoot
        $cleanupProbeOk = $false
    } catch {
        $cleanupProbeOk = $_.Exception.Message.Contains('outside GuidRoot')
    }
    $script:CaseResults += [pscustomobject]@{
        Name = 'neg-cleanup-prefix-boundary'
        Expected = 'Safety boundary violation'
        ExitCode = 1
        Ok = $cleanupProbeOk
        Output = 'Rejected sibling directory outside GuidRoot'
    }
    if (-not $cleanupProbeOk) {
        Add-Failure 'neg-cleanup-prefix-boundary: Assert-WithinGuidRoot incorrectly accepted sibling path with matching prefix'
    }
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-TreeSafely -Path $tempRoot -GuidRoot $tempRoot
    }
}

# -------------------------------------------------------------------------
# Output and Log Persistence (Executed FIRST)
# -------------------------------------------------------------------------
$logLines = @()
$logLines += ('# V5 Companion Fixtures Validator Regression Test Run - {0}' -f (Get-Date -Format 'o'))
$logLines += ('RepoRoot: {0}' -f $canonicalRepo)
$logLines += ('LogPath: {0}' -f $LogPath)
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

$summaryLine = ('SELFTEST PASS: total={0} positives={1} negatives={2} failures=0 log={3}' -f $totalCount, $positiveCount, $negativeCount, $LogPath)
$logLines += $summaryLine
$logText = $logLines -join "`n"

# Write log to disk
[System.IO.File]::WriteAllText($LogPath, $logText, (New-Object System.Text.UTF8Encoding($false)))

# -------------------------------------------------------------------------
# Real Repository Fixture and Tool Immutability Verification (Executed AFTER Log Write)
# -------------------------------------------------------------------------
$immutabilityFailures = @()
foreach ($name in $realCompanionFixtures) {
    $fixturePath = Join-Path $canonicalRepo ('tests\v5-golden\' + $name)
    $currentHash = Get-Sha256 $fixturePath
    if ($beforeHashes[$name] -ne $currentHash) {
        $immutabilityFailures += ('REAL_FIXTURE_MUTATED: {0} hash changed from {1} to {2}' -f $name, $beforeHashes[$name], $currentHash)
    }
}

if ($beforeHashes.ContainsKey('verify-v5-companion-fixtures.ps1')) {
    $currentValidatorHash = Get-Sha256 $validatorPathInRepo
    if ($beforeHashes['verify-v5-companion-fixtures.ps1'] -ne $currentValidatorHash) {
        $immutabilityFailures += ('REAL_VALIDATOR_MUTATED: verify-v5-companion-fixtures.ps1 hash changed from {0} to {1}' -f $beforeHashes['verify-v5-companion-fixtures.ps1'], $currentValidatorHash)
    }
}

if ($beforeHashes.ContainsKey('build.gradle')) {
    $currentGradleHash = Get-Sha256 $buildGradleInRepo
    if ($beforeHashes['build.gradle'] -ne $currentGradleHash) {
        $immutabilityFailures += ('REAL_BUILD_GRADLE_MUTATED: build.gradle hash changed from {0} to {1}' -f $beforeHashes['build.gradle'], $currentGradleHash)
    }
}

if ($immutabilityFailures.Count -gt 0) {
    foreach ($err in $immutabilityFailures) {
        Write-Output ("CRITICAL_SECURITY_FAILURE: {0}" -f $err)
    }
    exit 1
}

Write-Output ("{0} real_fixtures_unchanged=true" -f $summaryLine)
exit 0
