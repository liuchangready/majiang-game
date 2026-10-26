<#
.SYNOPSIS
  External single-field mutation probe for the N3 shuffle-v1 field binding.

.DESCRIPTION
  Copies tests/v5-golden/shuffle-v1.json into an exclusive TEMP root, mutates exactly
  one field (or one legal-equivalence shape) per run, points MAHJONG_REPO_ROOT at that
  copy, and executes the real xUnit-v3 Mahjong.Domain.Tests executable filtered to
  ShuffleGoldenBindingTests. The repository fixture is never modified.

  Positive control (unmodified copy) must have Total>0, Failed=0, Skipped=0.
  Every negative mutation must have Total>0, Failed>0, Skipped=0 and a non-zero native
  exit. Unknown enums, missing fields and wrong JSON types are included and must fail
  rather than fall back to a legal value. Two legal-equivalence mutations (plan order
  and case order) must stay green, with their basis recorded.

  The script creates a NEW exclusive TEMP directory, restores MAHJONG_REPO_ROOT in
  finally, rejects a pre-existing OutDir, and writes logs, mutated fixture copies,
  a CSV matrix and a JSON summary under OutDir.

.PARAMETER RepoRoot
  Worktree root containing dotnet/ and tests/v5-golden/shuffle-v1.json.

.PARAMETER Dotnet
  dotnet executable. Default: D:/Unity/dotnet/dotnet.exe

.PARAMETER OutDir
  New evidence directory. Must not already exist.

.EXAMPLE
  pwsh -NoProfile -File tools/v5-golden/probe-shuffle-binding.ps1 `
    -RepoRoot D:/majiang-wt/n3-shuffle-binding `
    -OutDir D:/majiang-wt/n3-shuffle-binding-evidence-20261025/probe
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = 'D:/majiang-wt/n3-shuffle-binding',
    [string]$Dotnet = 'D:/Unity/dotnet/dotnet.exe',
    [Parameter(Mandatory = $true)][string]$OutDir
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Stop-Probe([string]$Message) {
    throw "probe-shuffle-binding: $Message"
}

if (-not (Test-Path -LiteralPath $RepoRoot -PathType Container)) {
    Stop-Probe "RepoRoot does not exist: $RepoRoot"
}
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path

if (-not (Test-Path -LiteralPath $Dotnet -PathType Leaf)) {
    Stop-Probe "dotnet executable does not exist: $Dotnet"
}
$Dotnet = (Resolve-Path -LiteralPath $Dotnet).Path

$fixturePath = Join-Path $RepoRoot 'tests/v5-golden/shuffle-v1.json'
$testProject = Join-Path $RepoRoot 'dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj'
$testDll = Join-Path $RepoRoot 'dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll'
$domainDll = Join-Path $RepoRoot 'dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.dll'
$testSource = Join-Path $RepoRoot 'dotnet/tests/Mahjong.Domain.Tests/ShuffleGoldenBindingTests.cs'
$productionSource = Join-Path $RepoRoot 'dotnet/src/Mahjong.Domain/Stacking/ShuffleWallSynthesizer.cs'

foreach ($required in @($fixturePath, $testProject, $testDll, $domainDll, $testSource, $productionSource)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        Stop-Probe "required file missing: $required (build Domain.Tests Release first)"
    }
}

# Refuse stale binaries: the binding test and the production entry point must be older
# than the executable that is about to be probed.
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
$original = $fixtureRaw | ConvertFrom-Json
if ($null -eq $original.cases -or $original.cases.Count -ne 32) {
    Stop-Probe 'fixture must contain exactly 32 cases'
}
if ($original.schema -ne 'v5-shuffle-golden-v1') {
    Stop-Probe "unexpected schema: $($original.schema)"
}

$fixtureSha = (Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256).Hash
$testDllSha = (Get-FileHash -LiteralPath $testDll -Algorithm SHA256).Hash
$domainDllSha = (Get-FileHash -LiteralPath $domainDll -Algorithm SHA256).Hash

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('n3-shuffle-binding-probe-' + [guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $tempRoot) {
    Stop-Probe "exclusive TEMP path already exists: $tempRoot"
}
New-Item -ItemType Directory -Path $tempRoot | Out-Null

$previousRepoRoot = [Environment]::GetEnvironmentVariable('MAHJONG_REPO_ROOT', 'Process')

$environment = [ordered]@{
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    script = $PSCommandPath
    repoRoot = $RepoRoot
    gitHead = $gitHead
    gitBranch = $gitBranch
    gitStatus = $gitStatus
    fixture = $fixturePath
    fixtureSha256 = $fixtureSha
    testDll = $testDll
    testDllSha256 = $testDllSha
    domainDllSha256 = $domainDllSha
    dotnet = $Dotnet
    dotnetVersion = $dotnetVersion
    tempRoot = $tempRoot
    testFilter = '*ShuffleGoldenBindingTests*'
}
$environment | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutDir 'probe-environment.json') -Encoding UTF8

# Legal-equivalence mutations must stay green. Plan order is non-semantic because V4
# ShuffleWallGenerator.kt:68 sorts plans by seat; case order is non-semantic because
# each sample is independent and every binding iterates all 32 cases.
$cases = @(
    [pscustomobject]@{ Name = 'baseline'; Expect = 'Pass'; Why = 'positive control: unmodified external copy'; Mutate = { param($j) } }
    [pscustomobject]@{ Name = 'legal-plan-order'; Expect = 'Pass'; Why = 'V4 ShuffleWallGenerator.kt:68 canonicalises plans by seat; array order is non-semantic'; Mutate = { param($j) $j.cases[0].plans = @($j.cases[0].plans[2], $j.cases[0].plans[1], $j.cases[0].plans[0]) } }
    [pscustomobject]@{ Name = 'legal-case-order'; Expect = 'Pass'; Why = 'cases are independent samples and every binding iterates all 32; array order is non-semantic'; Mutate = { param($j) $j.cases = @($j.cases[31..0]) } }

    [pscustomobject]@{ Name = 'duplicate-all-samples'; Expect = 'Fail'; Why = '32 copies of one seed must not satisfy the exact seed set 0..31'; Mutate = { param($j) $j.cases = @(0..31 | ForEach-Object { $j.cases[0] }) } }
    [pscustomobject]@{ Name = 'duplicate-one-sample'; Expect = 'Fail'; Why = 'replacing one sample with a copy of another leaves only 31 unique seeds'; Mutate = { param($j) $j.cases[1] = $j.cases[0] } }
    [pscustomobject]@{ Name = 'missing-one-sample'; Expect = 'Fail'; Why = '31 cases must fail the required 32-case set'; Mutate = { param($j) $j.cases = @($j.cases[0..30]) } }
    [pscustomobject]@{ Name = 'out-of-range-seed'; Expect = 'Fail'; Why = 'seed 32 with dealer 0 still satisfies seed % 4 but violates the required 0..31 set'; Mutate = { param($j) $j.cases[0].seed = 32; $j.cases[0].dealerSeat = 0 } }
    [pscustomobject]@{ Name = 'generator-wrong-type'; Expect = 'Fail'; Why = 'generator must be a string at the real exporter path, not coerced number'; Mutate = { param($j) $j.generator = 42 } }
    [pscustomobject]@{ Name = 'generator-wrong-path'; Expect = 'Fail'; Why = 'generator path must equal the real V4 exporter source'; Mutate = { param($j) $j.generator = 'not-the-exporter' } }
    [pscustomobject]@{ Name = 'rules-source-wrong-type'; Expect = 'Fail'; Why = 'v4RulesSourceNote must be a string naming the real V4 source tree'; Mutate = { param($j) $j.v4RulesSourceNote = 42 } }
    [pscustomobject]@{ Name = 'rules-source-wrong-path'; Expect = 'Fail'; Why = 'v4RulesSourceNote must contain the real V4 stacking source path and exact-tree declaration'; Mutate = { param($j) $j.v4RulesSourceNote = 'not the V4 source' } }

    [pscustomobject]@{ Name = 'deadwall-flag-false'; Expect = 'Fail'; Why = 'deadWallSegmentEqual must equal the C# TilePositionMap dead-wall relation'; Mutate = { param($j) $j.cases[0].deadWallSegmentEqual = $false } }
    [pscustomobject]@{ Name = 'fourcaster-identity-false'; Expect = 'Fail'; Why = 'fourCasterIdentity must equal base == four and the n=4 Shape output'; Mutate = { param($j) $j.cases[0].fourCasterIdentity = $false } }
    [pscustomobject]@{ Name = 'threecaster-changed-false'; Expect = 'Fail'; Why = 'threeCasterChanged must equal base != three for the exported arrays'; Mutate = { param($j) $j.cases[0].threeCasterChanged = $false } }
    [pscustomobject]@{ Name = 'reversed-equals-three-false'; Expect = 'Fail'; Why = 'SOURCE_ONLY exporter invariant is asserted true; no reversedWall is exported for C# recomputation'; Mutate = { param($j) $j.cases[0].reversedEqualsThree = $false } }
    [pscustomobject]@{ Name = 'plan-target-unknown'; Expect = 'Fail'; Why = 'unknown target enum must fail, never fall back to a legal target'; Mutate = { param($j) $j.cases[0].plans[1].target = 'NOT_A_TARGET' } }
    [pscustomobject]@{ Name = 'plan-seat-out-of-range'; Expect = 'Fail'; Why = 'seat 9 must be rejected by SeatShufflePlan'; Mutate = { param($j) $j.cases[0].plans[1].seat = 9 } }
    [pscustomobject]@{ Name = 'plan-duplicate-seat'; Expect = 'Fail'; Why = 'duplicate seat must be rejected and the exported seat set must stay 0/1/2'; Mutate = { param($j) $j.cases[0].plans[1].seat = 0 } }
    [pscustomobject]@{ Name = 'dealer-seed-relation'; Expect = 'Fail'; Why = 'dealerSeat is bound to seed % 4 and drives Shape'; Mutate = { param($j) $j.cases[0].dealerSeat = 1 } }
    [pscustomobject]@{ Name = 'dealer-out-of-range'; Expect = 'Fail'; Why = 'invalid dealer must be rejected by Shape and the seed relation'; Mutate = { param($j) $j.cases[0].dealerSeat = 4 } }
    [pscustomobject]@{ Name = 'base-wall-field'; Expect = 'Fail'; Why = 'baseWall is the real Shape input; illegal tile id must fail'; Mutate = { param($j) $j.cases[0].baseWall[0] = 999 } }
    [pscustomobject]@{ Name = 'no-target-wall-field'; Expect = 'Fail'; Why = 'noTargetWall must equal the real n=0 Shape output'; Mutate = { param($j) $j.cases[0].noTargetWall[0] = 999 } }
    [pscustomobject]@{ Name = 'threecaster-wall-field'; Expect = 'Fail'; Why = 'threeCasterWall feeds the C# dead-wall mapping and conservation checks'; Mutate = { param($j) $j.cases[0].threeCasterWall[122] = 999 } }
    [pscustomobject]@{ Name = 'zero-budget-wall-field'; Expect = 'Fail'; Why = 'zeroBudgetWall must equal the real zero-budget Shape output'; Mutate = { param($j) $j.cases[0].zeroBudgetWall[0] = 999 } }
    [pscustomobject]@{ Name = 'fourcaster-wall-field'; Expect = 'Fail'; Why = 'fourCasterWall must equal the real n=4 Shape output'; Mutate = { param($j) $j.cases[0].fourCasterWall[0] = 999 } }
    [pscustomobject]@{ Name = 'slot-matrix-initial-slot'; Expect = 'Fail'; Why = 'slotMatrix.initialSlots must equal C# ShuffleSlots.Initial'; Mutate = { param($j) $j.slotMatrix[0].initialSlots[0] = 999 } }
    [pscustomobject]@{ Name = 'demand-vector-value'; Expect = 'Fail'; Why = 'demandVectors must equal C# ShuffleTargets.Demand'; Mutate = { param($j) $j.demandVectors.KOKUSHI[0] = 0.5 } }
    [pscustomobject]@{ Name = 'overlap-matrix-value'; Expect = 'Fail'; Why = 'overlapMatrix must equal C# ShuffleTargets.Overlap'; Mutate = { param($j) $j.overlapMatrix.DAISANGEN.DAISUUSHII = 0.5 } }
    [pscustomobject]@{ Name = 'config-tier-budget'; Expect = 'Fail'; Why = 'configSnapshot tiers must equal C# ShuffleConfig defaults'; Mutate = { param($j) $j.configSnapshot.tiers.'1'.initialBudget = 99 } }
    [pscustomobject]@{ Name = 'missing-deadwall-field'; Expect = 'Fail'; Why = 'missing required field must fail, never default to true'; Mutate = { param($j) $j.cases[0].PSObject.Properties.Remove('deadWallSegmentEqual') } }
    [pscustomobject]@{ Name = 'wrong-type-deadwall-bool'; Expect = 'Fail'; Why = 'string "true" must not be coerced to boolean true'; Mutate = { param($j) $j.cases[0].deadWallSegmentEqual = 'true' } }
    [pscustomobject]@{ Name = 'wrong-type-wall-value'; Expect = 'Fail'; Why = 'string wall value must not be coerced to an int'; Mutate = { param($j) $j.cases[0].baseWall[0] = 'zero' } }
    [pscustomobject]@{ Name = 'red-five-count'; Expect = 'Fail'; Why = 'redFiveCount must match the exported wall physical identity count'; Mutate = { param($j) $j.cases[0].redFiveCount = 2 } }
)

$rows = New-Object System.Collections.Generic.List[object]
$violations = New-Object System.Collections.Generic.List[string]
$fixtureCopyDir = Join-Path $OutDir 'fixtures'
New-Item -ItemType Directory -Path $fixtureCopyDir | Out-Null

$baselineTotal = $null

try {
    foreach ($scenario in $cases) {
        $caseRoot = Join-Path $tempRoot $scenario.Name
        $fixtureDir = Join-Path $caseRoot 'tests/v5-golden'
        New-Item -ItemType Directory -Path $fixtureDir -Force | Out-Null

        $json = $fixtureRaw | ConvertFrom-Json
        & $scenario.Mutate $json

        $dest = Join-Path $fixtureDir 'shuffle-v1.json'
        [IO.File]::WriteAllText($dest, ($json | ConvertTo-Json -Depth 100), [Text.UTF8Encoding]::new($false))
        $copyPath = Join-Path $fixtureCopyDir ($scenario.Name + '.shuffle-v1.json')
        Copy-Item -LiteralPath $dest -Destination $copyPath -Force
        $copySha = (Get-FileHash -LiteralPath $copyPath -Algorithm SHA256).Hash

        $env:MAHJONG_REPO_ROOT = $caseRoot
        $logPath = Join-Path $OutDir ($scenario.Name + '.log')
        & $Dotnet $testDll -noLogo -class '*ShuffleGoldenBindingTests*' *> $logPath
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
                    reason = 'missing summary'; exit = $nativeExit; total = $null; errors = $null
                    failed = $null; skipped = $null; notRun = $null; sha256 = $copySha
                    basis = $scenario.Why; log = $logPath; fixtureCopy = $copyPath
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

        if ($scenario.Expect -eq 'Pass') {
            if ($nativeExit -ne 0) { $scenarioViolations.Add("expected native exit 0, got $nativeExit") | Out-Null }
            if ($failed -ne 0) { $scenarioViolations.Add("expected Failed 0, got $failed") | Out-Null }
        }
        else {
            if ($nativeExit -eq 0) { $scenarioViolations.Add('expected non-zero native exit') | Out-Null }
            if ($failed -le 0) { $scenarioViolations.Add('expected Failed > 0') | Out-Null }
        }

        if ($scenario.Name -eq 'baseline') {
            if ($null -eq $baselineTotal) { $baselineTotal = $total }
        }
        elseif ($null -ne $baselineTotal -and $total -ne $baselineTotal) {
            $scenarioViolations.Add("discovery drifted: Total $total != baseline $baselineTotal") | Out-Null
        }

        $result = if ($scenarioViolations.Count -eq 0) { 'OK' } else { 'VIOLATION:' + ($scenarioViolations -join '; ') }
        foreach ($violation in $scenarioViolations) {
            $violations.Add("$($scenario.Name): $violation") | Out-Null
        }

        $rows.Add([pscustomobject]@{
                name = $scenario.Name; expected = $scenario.Expect; result = $result
                reason = $scenario.Why; exit = $nativeExit; total = $total; errors = $errors
                failed = $failed; skipped = $skipped; notRun = $notRun; sha256 = $copySha
                basis = $scenario.Why; log = $logPath; fixtureCopy = $copyPath
            }) | Out-Null
    }
}
finally {
    [Environment]::SetEnvironmentVariable('MAHJONG_REPO_ROOT', $previousRepoRoot, 'Process')
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}

$csvPath = Join-Path $OutDir 'binding-probes.csv'
$rows | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding UTF8
$summary = [ordered]@{
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    repoRoot = $RepoRoot
    gitHead = $gitHead
    fixtureSha256 = $fixtureSha
    testDllSha256 = $testDllSha
    baselineTotal = $baselineTotal
    scenarios = $rows.Count
    violations = $violations.Count
    outDir = $OutDir
    csv = $csvPath
    # This matrix only proves the bound fixture fields. It is not a G1 decision and
    # does not turn any unclosed input gap into PASS.
    readiness = [ordered]@{
        g1 = 'NOT_RUN'
        threeCasterShapeBinding = 'BLOCKED_RANDOM_INPUT'
        threeCasterBlockedReason = 'fixture has no recordedDoubles for the V4 Random(seed) decisions; Shape(empty trace) is TraceExhausted'
        threeCasterMinimalExtension = 'cases[].threeCasterRandomEvents: ordered typed events after base wall formation: {kind:"nextInt", bound:n, result:r} for every Fisher-Yates bounded choice and {kind:"nextDouble", value:v} for last-chance/proficiency/donor; C# ShuffleWallSynthesizer currently consumes one [0,1) double per decision, so an adapter must encode each bounded integer result as a double whose floor(pick*(i+1)) equals r and preserve the original event order; recording only nextDouble is not sufficient; seam and per-wall verification still require authorization'
        reversedEqualsThreeIndependentRecompute = 'BLOCKED_MISSING_REVERSED_WALL'
        reversedMinimalExtension = 'cases[].reversedWall: the exporter already computes reversed at engine/src/test/kotlin/com/majiang/v3/V5ShuffleGoldenExporterTest.kt:65'
        zeroBudgetEmptyTrace = 'NOT_APPLICABLE: V4 consumes the seat/slot shuffle draws before observing budget 0; empty trace is TraceExhausted'
        pg = 'NOT_RUN'
        wss = 'NOT_RUN'
        unity = 'NOT_RUN'
        gradle = 'NOT_RUN'
        remoteCi = 'NOT_RUN'
    }
    rows = $rows
}
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutDir 'probe-summary.json') -Encoding UTF8

$rows | Format-Table name, expected, result, exit, total, errors, failed, skipped, notRun -AutoSize

if ($violations.Count -gt 0) {
    Stop-Probe ("matrix violations:`n" + ($violations -join "`n"))
}

Write-Host "probe-shuffle-binding: PASS ($($rows.Count) scenarios, baseline Total=$baselineTotal, zero skips)"
