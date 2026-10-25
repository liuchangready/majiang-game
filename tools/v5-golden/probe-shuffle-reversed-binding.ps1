<#
.SYNOPSIS
  External single-field mutation probe for the P0.1 shuffle reversed-wall companion.

.DESCRIPTION
  Copies tests/v5-golden/shuffle-reversed-v1.json and the unchanged historical
  tests/v5-golden/shuffle-v1.json into an exclusive TEMP root, mutates exactly one
  field (or one legal-equivalence shape) per run, points MAHJONG_REPO_ROOT at that
  copy, and executes the real Mahjong.Domain.Tests executable filtered to
  ShuffleReversedGoldenTests. The repository fixtures and production sources are
  never modified.

  For every run the script checks the actual native exit code and the xUnit summary:
  exact Total equal to the baseline Total (> 0), Errors=0, Skipped=0, Not Run=0.
  Positive controls must have exit 0 / Failed 0. Every negative must have a non-zero
  native exit and Failed > 0. Missing summaries, discovery drift, tool/runtime
  aborts and unexpected green negatives are violations.

  -SelfNegative injects a legal no-op mutation but labels it as an expected failure.
  The same checker must observe the unexpected green and exit 1. This proves the
  checker is not an unconditional failure and is not a fake proof. It does not touch
  production sources and does not weaken any verifier.

  The script creates a NEW exclusive TEMP directory, restores MAHJONG_REPO_ROOT in
  finally, rejects a pre-existing OutDir, and writes logs, mutated fixture copies,
  CSV, JSON summary and SHA-256 digests under OutDir.

.PARAMETER RepoRoot
  Worktree root containing dotnet/ and tests/v5-golden/.

.PARAMETER Dotnet
  dotnet executable. Default: D:/Unity/dotnet/dotnet.exe

.PARAMETER OutDir
  New evidence directory. Must not already exist.

.PARAMETER SelfNegative
  Run the checker self-negative control; the script must exit 1.

.EXAMPLE
  powershell.exe -NoProfile -File tools/v5-golden/probe-shuffle-reversed-binding.ps1 `
    -RepoRoot D:/majiang-wt/shuffle-reversed-golden `
    -OutDir D:/majiang-wt/shuffle-reversed-golden-evidence/probe
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = 'D:/majiang-wt/shuffle-reversed-golden',
    [string]$Dotnet = 'D:/Unity/dotnet/dotnet.exe',
    [Parameter(Mandatory = $true)][string]$OutDir,
    [switch]$SelfNegative
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Stop-Probe([string]$Message) {
    throw "probe-shuffle-reversed-binding: $Message"
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Set-JsonField($Object, [string]$Name, $Value) {
    $Object.$Name = $Value
}

function Add-JsonField($Object, [string]$Name, $Value) {
    $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
}

function Remove-JsonField($Object, [string]$Name) {
    $Object.PSObject.Properties.Remove($Name)
}

function Swap-ArrayValues($Array, [int]$Left, [int]$Right) {
    $temporary = $Array[$Left]
    $Array[$Left] = $Array[$Right]
    $Array[$Right] = $temporary
}

if (-not (Test-Path -LiteralPath $RepoRoot -PathType Container)) {
    Stop-Probe "RepoRoot does not exist: $RepoRoot"
}
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path

if (-not (Test-Path -LiteralPath $Dotnet -PathType Leaf)) {
    Stop-Probe "dotnet executable does not exist: $Dotnet"
}
$Dotnet = (Resolve-Path -LiteralPath $Dotnet).Path

$fixturePath = Join-Path $RepoRoot 'tests/v5-golden/shuffle-reversed-v1.json'
$oldFixturePath = Join-Path $RepoRoot 'tests/v5-golden/shuffle-v1.json'
$testProject = Join-Path $RepoRoot 'dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj'
$testDll = Join-Path $RepoRoot 'dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll'
$domainDll = Join-Path $RepoRoot 'dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.dll'
$testSource = Join-Path $RepoRoot 'dotnet/tests/Mahjong.Domain.Tests/ShuffleReversedGoldenTests.cs'
$productionSource = Join-Path $RepoRoot 'dotnet/src/Mahjong.Domain/Stacking/ShuffleWallSynthesizer.cs'
$exporterSource = Join-Path $RepoRoot 'engine/src/test/kotlin/com/majiang/v3/V5ShuffleReversedGoldenExporterTest.kt'

foreach ($required in @(
        $fixturePath,
        $oldFixturePath,
        $testProject,
        $testDll,
        $domainDll,
        $testSource,
        $productionSource,
        $exporterSource
    )) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        Stop-Probe "required file missing: $required (build Domain.Tests Release and export the companion first)"
    }
}

# Refuse stale binaries: the real binding test and the Domain entry point must be
# older than the executable that is about to be probed.
$dllTime = (Get-Item -LiteralPath $testDll).LastWriteTimeUtc
foreach ($source in @($testSource, $productionSource)) {
    if ($dllTime -lt (Get-Item -LiteralPath $source).LastWriteTimeUtc) {
        Stop-Probe "stale test binary: $testDll is older than $source; rebuild with 'dotnet build ... -c Release --no-incremental'"
    }
}

if (Test-Path -LiteralPath $OutDir) {
    Stop-Probe "OutDir already exists; choose a new directory: $OutDir"
}
New-Item -ItemType Directory -Path $OutDir | Out-Null

$dotnetVersion = (& $Dotnet --version 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($dotnetVersion)) {
    Stop-Probe "dotnet --version failed with exit $LASTEXITCODE"
}

$git = Get-Command git -ErrorAction SilentlyContinue
if ($null -eq $git) {
    Stop-Probe 'git is required for provenance'
}
$gitHead = (& git -C $RepoRoot rev-parse HEAD 2>$null | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $gitHead.Length -ne 40) {
    Stop-Probe "git rev-parse HEAD failed: $gitHead"
}
$gitBranch = (& git -C $RepoRoot rev-parse --abbrev-ref HEAD 2>$null | Out-String).Trim()
$gitStatus = (& git -C $RepoRoot status --porcelain=v1 --untracked-files=all 2>$null | Out-String).Trim()

$fixtureRaw = Get-Content -LiteralPath $fixturePath -Raw -Encoding UTF8
$oldFixtureRaw = Get-Content -LiteralPath $oldFixturePath -Raw -Encoding UTF8
$original = $fixtureRaw | ConvertFrom-Json
$oldOriginal = $oldFixtureRaw | ConvertFrom-Json
if ($null -eq $original.cases -or $original.cases.Count -ne 32) {
    Stop-Probe 'companion fixture must contain exactly 32 cases'
}
if ($original.schema -ne 'v5-shuffle-reversed-golden-v1') {
    Stop-Probe "unexpected companion schema: $($original.schema)"
}
if ($null -eq $oldOriginal.cases -or $oldOriginal.cases.Count -ne 32) {
    Stop-Probe 'old fixture must contain exactly 32 cases'
}
if ($oldOriginal.schema -ne 'v5-shuffle-golden-v1') {
    Stop-Probe "unexpected old schema: $($oldOriginal.schema)"
}

$fixtureSha = Get-Sha256 $fixturePath
$oldFixtureSha = Get-Sha256 $oldFixturePath
$testDllSha = Get-Sha256 $testDll
$domainDllSha = Get-Sha256 $domainDll
$testSourceSha = Get-Sha256 $testSource
$exporterSourceSha = Get-Sha256 $exporterSource
$scriptSha = Get-Sha256 $PSCommandPath

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('shuffle-reversed-binding-probe-' + [guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $tempRoot) {
    Stop-Probe "exclusive TEMP path already exists: $tempRoot"
}
New-Item -ItemType Directory -Path $tempRoot | Out-Null

$previousRepoRoot = [Environment]::GetEnvironmentVariable('MAHJONG_REPO_ROOT', 'Process')

$environment = [ordered]@{
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    script = $PSCommandPath
    scriptSha256 = $scriptSha
    selfNegative = [bool]$SelfNegative
    repoRoot = $RepoRoot
    gitHead = $gitHead
    gitBranch = $gitBranch
    gitStatus = $gitStatus
    companionFixture = $fixturePath
    companionFixtureSha256 = $fixtureSha
    oldFixture = $oldFixturePath
    oldFixtureSha256 = $oldFixtureSha
    testDll = $testDll
    testDllSha256 = $testDllSha
    domainDllSha256 = $domainDllSha
    testSourceSha256 = $testSourceSha
    exporterSourceSha256 = $exporterSourceSha
    dotnet = $Dotnet
    dotnetVersion = $dotnetVersion
    tempRoot = $tempRoot
    testFilter = '*ShuffleReversedGoldenTests*'
}
$environment | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutDir 'probe-environment.json') -Encoding UTF8

# Positive controls must stay green. Case order is non-semantic because each case is
# an independent sample and every binding iterates all 32. LF/CRLF is non-semantic
# for JSON; the line-ending control proves the test is not line-ending brittle.
# Negative mutations cover INPUT, OUTPUT, INTERNAL_RELATION, STRUCTURE, META and
# SOURCE/OLD-FIXTURE provenance. Only companion fixture copies are mutated.
$scenarios = @(
    [pscustomobject]@{ Name = 'baseline'; Expect = 'Pass'; Newline = 'LF'; Why = 'positive control: unmodified external companion copy'; Mutate = { param($j) } }
    [pscustomobject]@{ Name = 'legal-case-order'; Expect = 'Pass'; Newline = 'LF'; Why = 'cases are independent samples and every binding iterates all 32; array order is non-semantic'; Mutate = { param($j) $j.cases = @($j.cases[31..0]) } }
    [pscustomobject]@{ Name = 'legal-crlf'; Expect = 'Pass'; Newline = 'CRLF'; Why = 'JSON line endings are non-semantic; CRLF must remain green and must not be pinned to a repository byte hash'; Mutate = { param($j) } }

    [pscustomobject]@{ Name = 'duplicate-seed'; Expect = 'Fail'; Newline = 'LF'; Why = 'replacing one sample with another seed leaves a duplicate and a missing seed'; Mutate = { param($j) $j.cases[1].seed = $j.cases[0].seed } }
    [pscustomobject]@{ Name = 'duplicate-all-seeds'; Expect = 'Fail'; Newline = 'LF'; Why = '32 copies of seed 0 must not satisfy the exact seed set 0..31'; Mutate = { param($j) $j.cases = @(0..31 | ForEach-Object { $j.cases[0] }) } }
    [pscustomobject]@{ Name = 'missing-one-case'; Expect = 'Fail'; Newline = 'LF'; Why = '31 cases must fail the required exact 32-case set'; Mutate = { param($j) $j.cases = @($j.cases[0..30]) } }
    [pscustomobject]@{ Name = 'case-count-33'; Expect = 'Fail'; Newline = 'LF'; Why = '33 cases must fail the exact 32-case dimension'; Mutate = { param($j) $j.cases = @($j.cases + $j.cases[0]) } }
    [pscustomobject]@{ Name = 'out-of-range-seed'; Expect = 'Fail'; Newline = 'LF'; Why = 'seed 32 with dealer 0 still satisfies seed % 4 but violates the exact 0..31 set'; Mutate = { param($j) $j.cases[0].seed = 32; $j.cases[0].dealerSeat = 0 } }
    [pscustomobject]@{ Name = 'wrong-dealer'; Expect = 'Fail'; Newline = 'LF'; Why = 'dealerSeat is bound to seed % 4 and to the old sample'; Mutate = { param($j) $j.cases[0].dealerSeat = 1 } }
    [pscustomobject]@{ Name = 'wrong-plan-target'; Expect = 'Fail'; Newline = 'LF'; Why = 'unknown target enum must fail, never fall back to a legal target'; Mutate = { param($j) $j.cases[0].plans[1].target = 'NOT_A_TARGET' } }
    [pscustomobject]@{ Name = 'wrong-plan-seat'; Expect = 'Fail'; Newline = 'LF'; Why = 'plan seats must be exactly 0/1/2 in the exported input'; Mutate = { param($j) $j.cases[0].plans[1].seat = 9 } }
    [pscustomobject]@{ Name = 'duplicate-plan-seat'; Expect = 'Fail'; Newline = 'LF'; Why = 'duplicate plan seat must be rejected'; Mutate = { param($j) $j.cases[0].plans[1].seat = 0 } }
    [pscustomobject]@{ Name = 'wrong-plan-target-mismatch'; Expect = 'Fail'; Newline = 'LF'; Why = 'changing a plan target breaks the exact old-case input binding'; Mutate = { param($j) $j.cases[0].plans[0].target = 'CHINITSU_SOU' } }
    [pscustomobject]@{ Name = 'reversed-plans-not-reverse'; Expect = 'Fail'; Newline = 'LF'; Why = 'reversedPlans must be the exact reverse of plans'; Mutate = { param($j) $j.cases[0].reversedPlans = @($j.cases[0].plans[0], $j.cases[0].plans[1], $j.cases[0].plans[2]) } }
    [pscustomobject]@{ Name = 'reversed-plans-missing'; Expect = 'Fail'; Newline = 'LF'; Why = 'missing reversedPlans must fail, never default to forward order'; Mutate = { param($j) Remove-JsonField $j.cases[0] 'reversedPlans' } }
    [pscustomobject]@{ Name = 'config-strength-wrong'; Expect = 'Fail'; Newline = 'LF'; Why = 'config must equal the old sample and C# ShuffleConfig defaults'; Mutate = { param($j) $j.config.strength = 0.1 } }
    [pscustomobject]@{ Name = 'config-missing'; Expect = 'Fail'; Newline = 'LF'; Why = 'missing config must fail, never default to a valid configuration'; Mutate = { param($j) Remove-JsonField $j 'config' } }

    [pscustomobject]@{ Name = 'case-extra-field'; Expect = 'Fail'; Newline = 'LF'; Why = 'extra case field must fail exact field inventory'; Mutate = { param($j) Add-JsonField $j.cases[0] 'extraField' 1 } }
    [pscustomobject]@{ Name = 'top-extra-field'; Expect = 'Fail'; Newline = 'LF'; Why = 'extra top-level field must fail exact field inventory'; Mutate = { param($j) Add-JsonField $j 'extraField' 1 } }
    [pscustomobject]@{ Name = 'case-missing-three-wall'; Expect = 'Fail'; Newline = 'LF'; Why = 'missing threeCasterWall must fail, never default to an empty wall'; Mutate = { param($j) Remove-JsonField $j.cases[0] 'threeCasterWall' } }
    [pscustomobject]@{ Name = 'base-wall-short'; Expect = 'Fail'; Newline = 'LF'; Why = '135-slot baseWall must fail the exact 136 dimension'; Mutate = { param($j) $j.cases[0].baseWall = @($j.cases[0].baseWall[0..134]) } }
    [pscustomobject]@{ Name = 'three-wall-short'; Expect = 'Fail'; Newline = 'LF'; Why = '135-slot threeCasterWall must fail the exact 136 dimension'; Mutate = { param($j) $j.cases[0].threeCasterWall = @($j.cases[0].threeCasterWall[0..134]) } }
    [pscustomobject]@{ Name = 'reversed-wall-missing'; Expect = 'Fail'; Newline = 'LF'; Why = 'the newly supplied actual reversedWall must be present, not optional'; Mutate = { param($j) Remove-JsonField $j.cases[0] 'reversedWall' } }
    [pscustomobject]@{ Name = 'reversed-wall-short'; Expect = 'Fail'; Newline = 'LF'; Why = '135-slot reversedWall must fail the exact 136 dimension'; Mutate = { param($j) $j.cases[0].reversedWall = @($j.cases[0].reversedWall[0..134]) } }
    [pscustomobject]@{ Name = 'reversed-wall-extra'; Expect = 'Fail'; Newline = 'LF'; Why = '137-slot reversedWall must fail the exact 136 dimension'; Mutate = { param($j) $j.cases[0].reversedWall = @($j.cases[0].reversedWall + 0) } }
    [pscustomobject]@{ Name = 'wall-value-out-of-range'; Expect = 'Fail'; Newline = 'LF'; Why = 'tile id 999 is outside 0..135 and must fail physical validity'; Mutate = { param($j) $j.cases[0].reversedWall[0] = 999 } }
    [pscustomobject]@{ Name = 'wall-value-string'; Expect = 'Fail'; Newline = 'LF'; Why = 'string wall value must not be coerced to an int'; Mutate = { param($j) $j.cases[0].reversedWall[0] = 'zero' } }
    [pscustomobject]@{ Name = 'bool-wrong-type'; Expect = 'Fail'; Newline = 'LF'; Why = 'string "true" must not be coerced to boolean true'; Mutate = { param($j) $j.cases[0].reversedEqualsThree = 'true' } }
    [pscustomobject]@{ Name = 'seed-wrong-type'; Expect = 'Fail'; Newline = 'LF'; Why = 'string seed must not be coerced to an int'; Mutate = { param($j) $j.cases[0].seed = 'zero' } }

    [pscustomobject]@{ Name = 'base-wall-single-swap'; Expect = 'Fail'; Newline = 'LF'; Why = 'swapping two baseWall slots breaks the old-sample binding while preserving uniqueness'; Mutate = { param($j) Swap-ArrayValues $j.cases[0].baseWall 0 1 } }
    [pscustomobject]@{ Name = 'three-wall-single-swap'; Expect = 'Fail'; Newline = 'LF'; Why = 'swapping two threeCasterWall slots breaks the old-sample binding and relations'; Mutate = { param($j) Swap-ArrayValues $j.cases[0].threeCasterWall 0 1 } }
    [pscustomobject]@{ Name = 'reversed-wall-single-swap'; Expect = 'Fail'; Newline = 'LF'; Why = 'swapping two reversedWall slots preserves uniqueness but breaks three == reversed recomputation'; Mutate = { param($j) Swap-ArrayValues $j.cases[0].reversedWall 0 1 } }
    [pscustomobject]@{ Name = 'reversed-equals-three-false'; Expect = 'Fail'; Newline = 'LF'; Why = 'the exported boolean must equal the C# recomputation from the two walls'; Mutate = { param($j) $j.cases[0].reversedEqualsThree = $false } }
    [pscustomobject]@{ Name = 'three-caster-changed-false'; Expect = 'Fail'; Newline = 'LF'; Why = 'threeCasterChanged must equal base != three'; Mutate = { param($j) $j.cases[0].threeCasterChanged = $false } }
    [pscustomobject]@{ Name = 'red-five-count-wrong'; Expect = 'Fail'; Newline = 'LF'; Why = 'redFiveCount must match the physical wall identity count'; Mutate = { param($j) $j.cases[0].redFiveCount = 2 } }
    [pscustomobject]@{ Name = 'deadwall-flag-false'; Expect = 'Fail'; Newline = 'LF'; Why = 'deadWallSegmentEqual must equal the actual 122..135 relation'; Mutate = { param($j) $j.cases[0].deadWallSegmentEqual = $false } }

    [pscustomobject]@{ Name = 'schema-wrong'; Expect = 'Fail'; Newline = 'LF'; Why = 'schema must be the new independent companion version'; Mutate = { param($j) $j.schema = 'v5-shuffle-golden-v1' } }
    [pscustomobject]@{ Name = 'generator-wrong-path'; Expect = 'Fail'; Newline = 'LF'; Why = 'generator must name the real companion exporter path'; Mutate = { param($j) $j.generator = 'not-the-exporter' } }
    [pscustomobject]@{ Name = 'generator-wrong-type'; Expect = 'Fail'; Newline = 'LF'; Why = 'generator must be a string, not a number'; Mutate = { param($j) $j.generator = 42 } }
    [pscustomobject]@{ Name = 'base-commit-wrong'; Expect = 'Fail'; Newline = 'LF'; Why = 'v4BaseCommit must equal the frozen accepted base'; Mutate = { param($j) $j.v4BaseCommit = 'not-a-commit' } }
    [pscustomobject]@{ Name = 'source-tree-empty'; Expect = 'Fail'; Newline = 'LF'; Why = 'empty sourceTree must not pass the required source role set'; Mutate = { param($j) $j.sourceTree = @() } }
    [pscustomobject]@{ Name = 'source-tree-missing-role'; Expect = 'Fail'; Newline = 'LF'; Why = 'a missing required source role must fail, not be skipped'; Mutate = { param($j) $j.sourceTree = @($j.sourceTree[0..8]) } }
    [pscustomobject]@{ Name = 'source-tree-duplicate'; Expect = 'Fail'; Newline = 'LF'; Why = 'duplicate source role/path must fail even if the array length stays valid'; Mutate = { param($j) $j.sourceTree[1] = $j.sourceTree[0] } }
    [pscustomobject]@{ Name = 'source-role-wrong'; Expect = 'Fail'; Newline = 'LF'; Why = 'unknown source role must fail the exact required role set'; Mutate = { param($j) $j.sourceTree[0].role = 'v4_production_wall_generator_wrong' } }
    [pscustomobject]@{ Name = 'source-path-wrong'; Expect = 'Fail'; Newline = 'LF'; Why = 'a role must name its exact real source path'; Mutate = { param($j) $j.sourceTree[0].path = 'engine/src/main/kotlin/com/majiang/stacking/NotTheGenerator.kt' } }
    [pscustomobject]@{ Name = 'source-hash-wrong'; Expect = 'Fail'; Newline = 'LF'; Why = 'the LF-normalized source hash must be recomputed from real bytes'; Mutate = { param($j) $h = $j.sourceTree[0].sha256LfNormalized; $j.sourceTree[0].sha256LfNormalized = if ($h[0] -eq '0') { '1' + $h.Substring(1) } else { '0' + $h.Substring(1) } } }
    [pscustomobject]@{ Name = 'source-raw-hash-wrong'; Expect = 'Fail'; Newline = 'LF'; Why = 'the raw source hash must match the recorded line-ending profile'; Mutate = { param($j) $h = $j.sourceTree[0].sha256Raw; $j.sourceTree[0].sha256Raw = if ($h[0] -eq '0') { '1' + $h.Substring(1) } else { '0' + $h.Substring(1) } } }
    [pscustomobject]@{ Name = 'source-line-ending-wrong'; Expect = 'Fail'; Newline = 'LF'; Why = 'recording LF for a CRLF source must fail raw-byte reconstruction'; Mutate = { param($j) $j.sourceTree[0].lineEnding = 'LF' } }
    [pscustomobject]@{ Name = 'old-fixture-hash-wrong'; Expect = 'Fail'; Newline = 'LF'; Why = 'the old fixture hash binding must be the real accepted sample, not a refreshed one'; Mutate = { param($j) $j.bindsOldFixture.sha256LfNormalized = ('0' * 64) } }
    [pscustomobject]@{ Name = 'old-fixture-case-count-wrong'; Expect = 'Fail'; Newline = 'LF'; Why = 'bindsOldFixture.caseCount must be the real 32'; Mutate = { param($j) $j.bindsOldFixture.caseCount = 31 } }
    [pscustomobject]@{ Name = 'old-fixture-commit-wrong'; Expect = 'Fail'; Newline = 'LF'; Why = 'bindsOldFixture.v4Commit must equal the old fixture real commit'; Mutate = { param($j) $j.bindsOldFixture.v4Commit = ('0' * 40) } }
    [pscustomobject]@{ Name = 'generation-count-wrong'; Expect = 'Fail'; Newline = 'LF'; Why = 'generationResult.reversedEqualsThreeSeeds must equal the recomputed 32'; Mutate = { param($j) $j.generationResult.reversedEqualsThreeSeeds = 31 } }
)

if ($SelfNegative) {
    # Deliberately inject a legal positive control but label it as an expected failure.
    # The same checker must report VIOLATION and exit 1; no unconditional exit 1 is used.
    $scenarios += [pscustomobject]@{
        Name = 'self-negative-legal-control'
        Expect = 'Fail'
        Newline = 'LF'
        Why = 'self-negative: a legal no-op copy labelled Fail must be detected as unexpected green'
        Mutate = { param($j) }
    }
}

$rows = New-Object System.Collections.Generic.List[object]
$violations = New-Object System.Collections.Generic.List[string]
$fixtureCopyDir = Join-Path $OutDir 'fixtures'
New-Item -ItemType Directory -Path $fixtureCopyDir | Out-Null
$baselineTotal = $null
$selfNegativeDetected = $false

try {
    foreach ($scenario in $scenarios) {
        $caseRoot = Join-Path $tempRoot $scenario.Name
        $fixtureDir = Join-Path $caseRoot 'tests/v5-golden'
        New-Item -ItemType Directory -Path $fixtureDir -Force | Out-Null

        $json = $fixtureRaw | ConvertFrom-Json
        & $scenario.Mutate $json

        $jsonText = ($json | ConvertTo-Json -Depth 100).Replace("`r`n", "`n")
        if ($scenario.Newline -eq 'CRLF') {
            $jsonText = $jsonText.Replace("`n", "`r`n")
        }

        $dest = Join-Path $fixtureDir 'shuffle-reversed-v1.json'
        $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
        [IO.File]::WriteAllText($dest, $jsonText, $utf8NoBom)
        Copy-Item -LiteralPath $oldFixturePath -Destination (Join-Path $fixtureDir 'shuffle-v1.json') -Force

        $copyPath = Join-Path $fixtureCopyDir ($scenario.Name + '.shuffle-reversed-v1.json')
        Copy-Item -LiteralPath $dest -Destination $copyPath -Force
        $copySha = Get-Sha256 $copyPath

        $env:MAHJONG_REPO_ROOT = $caseRoot
        $logPath = Join-Path $OutDir ($scenario.Name + '.log')
        & $Dotnet $testDll -noLogo -class '*ShuffleReversedGoldenTests*' *> $logPath
        $nativeExit = $LASTEXITCODE

        $logText = Get-Content -LiteralPath $logPath -Raw
        if ($logText -match 'Test Run Aborted|No test matches|Unhandled exception|Stack overflow|OutOfMemoryException|MSBUILD : error|was not found') {
            $violations.Add("$($scenario.Name): tool/runtime error found in log") | Out-Null
        }

        $summaryMatch = [regex]::Matches(
            $logText,
            'Total:\s*(\d+),\s*Errors:\s*(\d+),\s*Failed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Not Run:\s*(\d+)') |
            Select-Object -Last 1
        if ($null -eq $summaryMatch) {
            $violations.Add("$($scenario.Name): missing TEST EXECUTION SUMMARY") | Out-Null
            $rows.Add([pscustomobject]@{
                    name = $scenario.Name; expected = $scenario.Expect; result = 'VIOLATION'
                    reason = 'missing summary'; exit = $nativeExit; expectedTotal = $baselineTotal
                    total = $null; errors = $null; failed = $null; skipped = $null; notRun = $null
                    sha256 = $copySha; basis = $scenario.Why; log = $logPath; fixtureCopy = $copyPath
                    logSha256 = Get-Sha256 $logPath; fixtureSha256 = $copySha
                }) | Out-Null
            continue
        }

        $total = [int]$summaryMatch.Groups[1].Value
        $errors = [int]$summaryMatch.Groups[2].Value
        $failed = [int]$summaryMatch.Groups[3].Value
        $skipped = [int]$summaryMatch.Groups[4].Value
        $notRun = [int]$summaryMatch.Groups[5].Value

        $scenarioViolations = New-Object System.Collections.Generic.List[string]
        if ($total -le 0) { $scenarioViolations.Add('Total must be > 0') | Out-Null }
        if ($errors -ne 0) { $scenarioViolations.Add('Errors must be 0') | Out-Null }
        if ($skipped -ne 0) { $scenarioViolations.Add('Skipped must be 0') | Out-Null }
        if ($notRun -ne 0) { $scenarioViolations.Add('Not Run must be 0') | Out-Null }

        if ($scenario.Name -eq 'baseline') {
            if ($null -eq $baselineTotal) { $baselineTotal = $total }
        }
        elseif ($null -ne $baselineTotal -and $total -ne $baselineTotal) {
            $scenarioViolations.Add("discovery drifted: Total $total != baseline $baselineTotal") | Out-Null
        }

        if ($scenario.Expect -eq 'Pass') {
            if ($nativeExit -ne 0) { $scenarioViolations.Add("expected native exit 0, got $nativeExit") | Out-Null }
            if ($failed -ne 0) { $scenarioViolations.Add("expected Failed 0, got $failed") | Out-Null }
        }
        else {
            if ($nativeExit -eq 0) { $scenarioViolations.Add('expected non-zero native exit') | Out-Null }
            if ($failed -le 0) { $scenarioViolations.Add('expected Failed > 0') | Out-Null }
        }

        $result = if ($scenarioViolations.Count -eq 0) { 'OK' } else { 'VIOLATION:' + ($scenarioViolations -join '; ') }
        foreach ($violation in $scenarioViolations) {
            $violations.Add("$($scenario.Name): $violation") | Out-Null
        }

        if ($scenario.Name -eq 'self-negative-legal-control' -and $result -like 'VIOLATION*') {
            $selfNegativeDetected = $true
        }

        $rows.Add([pscustomobject]@{
                name = $scenario.Name; expected = $scenario.Expect; result = $result
                reason = $scenario.Why; exit = $nativeExit; expectedTotal = $baselineTotal
                total = $total; errors = $errors; failed = $failed; skipped = $skipped
                notRun = $notRun; sha256 = $copySha; basis = $scenario.Why; log = $logPath
                fixtureCopy = $copyPath; logSha256 = Get-Sha256 $logPath; fixtureSha256 = $copySha
            }) | Out-Null
    }
}
finally {
    [Environment]::SetEnvironmentVariable('MAHJONG_REPO_ROOT', $previousRepoRoot, 'Process')
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}

if ($SelfNegative -and -not $selfNegativeDetected) {
    Stop-Probe 'self-negative control did not produce an unexpected-green violation; refusing a fake exit 1'
}

$csvPath = Join-Path $OutDir 'binding-probes.csv'
$rows | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding UTF8
$summary = [ordered]@{
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    selfNegative = [bool]$SelfNegative
    selfNegativeDetected = $selfNegativeDetected
    repoRoot = $RepoRoot
    gitHead = $gitHead
    gitBranch = $gitBranch
    companionFixtureSha256 = $fixtureSha
    oldFixtureSha256 = $oldFixtureSha
    testDllSha256 = $testDllSha
    domainDllSha256 = $domainDllSha
    testSourceSha256 = $testSourceSha
    exporterSourceSha256 = $exporterSourceSha
    scriptSha256 = $scriptSha
    baselineTotal = $baselineTotal
    scenarios = $rows.Count
    violations = $violations.Count
    outDir = $OutDir
    csv = $csvPath
    csvSha256 = Get-Sha256 $csvPath
    # This matrix only proves the companion binding and probe behaviour. It is not a
    # G1/G5 decision and does not turn any unclosed random-input gap into PASS.
    readiness = [ordered]@{
        scope = 'P0.1 actual reversed-wall companion only'
        reversedWallCompanion = 'PASS (component)'
        threeCasterShapeBinding = 'BLOCKED_RANDOM_INPUT'
        threeCasterBlockedReason = 'no real V4 ordered random events were extracted; this task does not implement RNG/trace instrumentation'
        orderedRandomInput = 'NOT_IMPLEMENTED'
        g1 = 'NOT_RUN'
        g5 = 'NOT_APPROVED'
        pg = 'NOT_RUN'
        wss = 'NOT_RUN'
        unity = 'NOT_RUN'
        il2cpp = 'NOT_RUN'
        gradleDevice = 'NOT_RUN'
        remoteCi = 'NOT_RUN'
    }
    rows = $rows
}
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutDir 'probe-summary.json') -Encoding UTF8

$rows | Format-Table name, expected, result, exit, expectedTotal, total, errors, failed, skipped, notRun -AutoSize

if ($violations.Count -gt 0) {
    Stop-Probe ("matrix violations:`n" + ($violations -join "`n"))
}

Write-Host "probe-shuffle-reversed-binding: PASS ($($rows.Count) scenarios, baseline Total=$baselineTotal, zero skips)"
