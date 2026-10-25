<#
.SYNOPSIS
  N3 cheat-v1 consumer binding: external single-field mutation matrix.

.DESCRIPTION
  Copies tests/v5-golden/cheat-v1.json into a new exclusive TEMP evidence directory,
  mutates exactly one exported field per run, points the xUnit consumer at the copy via
  V5_CHEAT_GOLDEN_PATH, and requires a real framework failure (native exit 1, Failed > 0,
  zero skipped / Not Run / Errors). The unmodified copy is the positive control.

  The script never writes the repository fixture. Build/tool/test discovery failures are
  propagated as non-zero exits; they are never counted as expected mutation failures.

.EXAMPLE
  pwsh -NoProfile -ExecutionPolicy Bypass -File tools/v5-golden/probe-cheat-binding.ps1
#>
param(
    [string]$Repo = 'D:/majiang-wt/n3-cheat-binding',
    [string]$EvidenceRoot,
    [switch]$SkipBuild,
    [switch]$MatrixOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$dotnet = 'D:/Unity/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    throw "dotnet CLI not found: $dotnet"
}

$Repo = (Resolve-Path -LiteralPath $Repo).Path
$fixture = Join-Path $Repo 'tests/v5-golden/cheat-v1.json'
$project = Join-Path $Repo 'dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj'
$dll = Join-Path $Repo 'dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll'
if (-not (Test-Path -LiteralPath $fixture)) { throw "fixture not found: $fixture" }
if (-not (Test-Path -LiteralPath $project)) { throw "test project not found: $project" }

if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
    $EvidenceRoot = Join-Path $env:TEMP ('n3-cheat-binding-probe-' + [guid]::NewGuid().ToString('N'))
}
else {
    $EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
}
if (Test-Path -LiteralPath $EvidenceRoot) {
    throw "evidence root already exists; refusing to overwrite: $EvidenceRoot"
}

$originalDir = Join-Path $EvidenceRoot 'original'
$mutantDir = Join-Path $EvidenceRoot 'mutants'
$logDir = Join-Path $EvidenceRoot 'logs'
$matrixDir = Join-Path $EvidenceRoot 'matrix'
New-Item -ItemType Directory -Path $originalDir, $mutantDir, $logDir, $matrixDir -Force | Out-Null

$originalCopy = Join-Path $originalDir 'cheat-v1.json'
Copy-Item -LiteralPath $fixture -Destination $originalCopy
$rawSha = (Get-FileHash -LiteralPath $originalCopy -Algorithm SHA256).Hash

$text = [IO.File]::ReadAllText($originalCopy, [Text.Encoding]::UTF8)
$normalized = $text.Replace("`r`n", "`n").Replace("`r", "`n")
$normalizedPath = Join-Path $originalDir 'cheat-v1.lf.json'
[IO.File]::WriteAllText($normalizedPath, $normalized, (New-Object Text.UTF8Encoding($false)))
$normalizedSha = (Get-FileHash -LiteralPath $normalizedPath -Algorithm SHA256).Hash
$gitBlobSha1 = (& git -C $Repo hash-object -- 'tests/v5-golden/cheat-v1.json' 2>$null)
$head = (& git -C $Repo rev-parse HEAD 2>$null)
$dotnetVersion = (& $dotnet --version 2>$null)

$meta = [ordered]@{
    schema = 'v5-cheat-binding-probe-v1'
    repo = $Repo
    head = $head
    dotnet = $dotnet
    dotnetVersion = $dotnetVersion
    fixture = $fixture
    fixtureRawSha256 = $rawSha
    fixtureLfNormalizedSha256 = $normalizedSha
    fixtureGitBlobSha1 = $gitBlobSha1
    script = $PSCommandPath
    scriptSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
    evidenceRoot = $EvidenceRoot
    startedAt = (Get-Date).ToString('o')
}
$meta | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $EvidenceRoot 'meta.json') -Encoding utf8

function Get-TestCounts {
    param([string]$Text)
    $match = [regex]::Match($Text, 'Total:\s*(\d+),\s*Errors:\s*(\d+),\s*Failed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Not Run:\s*(\d+)')
    if (-not $match.Success) {
        return $null
    }

    return [ordered]@{
        total = [int]$match.Groups[1].Value
        errors = [int]$match.Groups[2].Value
        failed = [int]$match.Groups[3].Value
        skipped = [int]$match.Groups[4].Value
        notRun = [int]$match.Groups[5].Value
    }
}

function Get-FailedTestNames {
    param([string]$Text)
    $names = New-Object System.Collections.Generic.List[string]
    foreach ($line in ($Text -split "`r?`n")) {
        $m = [regex]::Match($line, '^\s*Mahjong\.Domain\.Tests\.([A-Za-z0-9_]+)\.([A-Za-z0-9_]+)')
        if ($line -match '\[FAIL\]' -and $m.Success) {
            $names.Add($m.Groups[1].Value + '.' + $m.Groups[2].Value)
        }
    }

    return $names.ToArray()
}

function Invoke-Consumer {
    param([string]$FixturePath, [string]$LogPath)
    $prior = $env:V5_CHEAT_GOLDEN_PATH
    try {
        $env:V5_CHEAT_GOLDEN_PATH = $FixturePath
        $output = & $dotnet $dll -class '*CheatGolden*' -showLiveOutput -noLogo 2>&1
        $nativeExit = $LASTEXITCODE
        $text = ($output | Out-String)
        [IO.File]::WriteAllText($LogPath, $text, (New-Object Text.UTF8Encoding($false)))
        return [ordered]@{
            exit = $nativeExit
            text = $text
            counts = Get-TestCounts -Text $text
            failedTests = Get-FailedTestNames -Text $text
        }
    }
    finally {
        if ($null -eq $prior) {
            Remove-Item Env:V5_CHEAT_GOLDEN_PATH -ErrorAction SilentlyContinue
        }
        else {
            $env:V5_CHEAT_GOLDEN_PATH = $prior
        }
    }
}

function Invoke-Step {
    param([string]$Name, [string]$LogName, [scriptblock]$Action)
    $log = Join-Path $logDir $LogName
    try {
        & $Action 2>&1 | Tee-Object -FilePath $log
        if ($LASTEXITCODE -ne 0) {
            throw "step '$Name' failed with native exit $LASTEXITCODE"
        }
    }
    catch {
        $_ | Out-String | Add-Content -LiteralPath $log
        throw
    }
}

# Build once, non-incrementally, so a stale DLL can never make a mutation "pass".
if (-not $SkipBuild) {
    Invoke-Step -Name 'build' -LogName 'build.log' -Action {
        & $dotnet build $project -c Release --no-incremental
    }
}
if (-not (Test-Path -LiteralPath $dll)) {
    throw "test DLL missing after build: $dll"
}

$positive = Invoke-Consumer -FixturePath $originalCopy -LogPath (Join-Path $logDir 'positive-control.log')
$positiveCounts = $positive.counts
$positiveOk = ($positive.exit -eq 0) -and ($null -ne $positiveCounts) -and
    ($positiveCounts.total -gt 0) -and ($positiveCounts.failed -eq 0) -and
    ($positiveCounts.errors -eq 0) -and ($positiveCounts.skipped -eq 0) -and
    ($positiveCounts.notRun -eq 0)
if (-not $positiveOk) {
    $summary = [ordered]@{
        status = 'FAIL'
        reason = 'positive control did not pass'
        positive = $positive
    }
    $summary | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $EvidenceRoot 'summary.json') -Encoding utf8
    exit 1
}

function Find-Case {
    param($Section, [string]$CaseId)
    $case = $Section | Where-Object { $_.caseId -eq $CaseId }
    if ($null -eq $case) { throw "fixture case not found: $CaseId" }
    return $case
}

$mutations = @(
    @{ id = 'outcome-tilemark'; category = 'outcome'; field = 'tileMarkHandler[tm-allowed].outcome';
        expect = 'TileMark_BoundCase'; mutate = { param($j) (Find-Case $j.tileMarkHandler 'tm-allowed').outcome = 'Rejected' } },
    @{ id = 'reason-tilemark'; category = 'reason'; field = 'tileMarkHandler[tm-not-your-turn].reason';
        expect = 'TileMark_BoundCase'; mutate = { param($j) (Find-Case $j.tileMarkHandler 'tm-not-your-turn').reason = 'WRONG_PHASE' } },
    @{ id = 'reason-beam'; category = 'reason'; field = 'beamSwapHandler[bm-activate-riichi].reason';
        expect = 'BeamSwap_BoundCase'; mutate = { param($j) (Find-Case $j.beamSwapHandler 'bm-activate-riichi').reason = 'WRONG_PHASE' } },
    @{ id = 'ledger-amount'; category = 'ledger-amount'; field = 'ledger.chargeOnce.amount';
        expect = 'Ledger_Snapshot'; mutate = { param($j) $j.ledger.chargeOnce.amount = -8 } },
    @{ id = 'ledger-balance'; category = 'ledger-balance'; field = 'ledger.chargeOnce.balancesAfter[0]';
        expect = 'Ledger_Snapshot'; mutate = { param($j) $j.ledger.chargeOnce.balancesAfter[0] = 34 } },
    @{ id = 'ledger-flow'; category = 'ledger-flow'; field = 'ledger.flowSummary.seat1NetDelta';
        expect = 'Ledger_Snapshot'; mutate = { param($j) $j.ledger.flowSummary.seat1NetDelta = 6 } },
    @{ id = 'ledger-snapshot-balance'; category = 'ledger-balance'; field = 'ledger.flowSummary.finalBalances[1]';
        expect = 'Ledger_Snapshot'; mutate = { param($j) $j.ledger.flowSummary.finalBalances[1] = 16 } },
    @{ id = 'ledger-reason'; category = 'ledger-reason'; field = 'ledger.chargeOnce.reason';
        expect = 'Ledger_Snapshot'; mutate = { param($j) $j.ledger.chargeOnce.reason = 'ROUND_ACHIEVEMENT' } },
    @{ id = 'mark-version'; category = 'mark-version'; field = 'tileMarkState.addGroup.versionTrail[2]';
        expect = 'TileMarkState_Binds'; mutate = { param($j) $j.tileMarkState.addGroup.versionTrail[2] = 3 } },
    @{ id = 'mark-expiry'; category = 'mark-expiry'; field = 'tileMarkState.expiry.groupsAfterCompletingRound3';
        expect = 'TileMarkState_Binds'; mutate = { param($j) $j.tileMarkState.expiry.groupsAfterCompletingRound3 = 0 } },
    @{ id = 'mark-ordinal'; category = 'mark-ordinal'; field = 'tileMarkState.registerDealtRound.ordinalAfterFirst';
        expect = 'TileMarkState_Binds'; mutate = { param($j) $j.tileMarkState.registerDealtRound.ordinalAfterFirst = 2 } },
    @{ id = 'sleeve-window'; category = 'input-window'; field = 'sleeveBloomHandler[sbo-activate-allowed].goldenInputs.windowId';
        expect = 'SleeveBloom_BoundCase'; mutate = { param($j) (Find-Case $j.sleeveBloomHandler 'sbo-activate-allowed').goldenInputs.windowId = 'bloom-window-2-9' } },
    @{ id = 'sleeve-offer'; category = 'input-offer'; field = 'sleeveBloomHandler[sb-choose-second].goldenInputs.offerId';
        expect = 'SleeveBloom_BoundCase'; mutate = { param($j) (Find-Case $j.sleeveBloomHandler 'sb-choose-second').goldenInputs.offerId = 'bloom-0-other' } },
    @{ id = 'sleeve-tile'; category = 'input-tile'; field = 'sleeveBloomHandler[sb-activate-allowed].goldenInputs.drawnTileId';
        expect = 'SleeveBloom_BoundCase'; mutate = { param($j) (Find-Case $j.sleeveBloomHandler 'sb-activate-allowed').goldenInputs.drawnTileId = 20 } },
    @{ id = 'sleeve-pointer'; category = 'input-pointer'; field = 'sleeveBloomHandler[sb-activate-allowed].goldenInputs.wallPointer';
        expect = 'SleeveBloom_BoundCase'; mutate = { param($j) (Find-Case $j.sleeveBloomHandler 'sb-activate-allowed').goldenInputs.wallPointer = 54 } },
    @{ id = 'movement-container'; category = 'physical-container'; field = 'tileMovement[mv-swap-hand-with-wall-direct].factsAfter[0].container';
        expect = 'Movement_SwapHandWithWall'; mutate = { param($j) (Find-Case $j.tileMovement 'mv-swap-hand-with-wall-direct').factsAfter[0].container = 'hand' } },
    @{ id = 'movement-seat'; category = 'physical-seat'; field = 'tileMovement[mv-swap-hand-with-wall-direct].factsAfter[1].seat';
        expect = 'Movement_SwapHandWithWall'; mutate = { param($j) (Find-Case $j.tileMovement 'mv-swap-hand-with-wall-direct').factsAfter[1].seat = 1 } },
    @{ id = 'movement-index'; category = 'physical-index'; field = 'tileMovement[mv-swap-hand-with-wall-direct].factsAfter[0].index';
        expect = 'Movement_SwapHandWithWall'; mutate = { param($j) (Find-Case $j.tileMovement 'mv-swap-hand-with-wall-direct').factsAfter[0].index = 1 } },
    @{ id = 'movement-hand-tile'; category = 'physical-tile'; field = 'tileMovement[mv-swap-hand-with-wall-direct].handTileId';
        expect = 'Movement_SwapHandWithWall'; mutate = { param($j) (Find-Case $j.tileMovement 'mv-swap-hand-with-wall-direct').handTileId = 12 } },
    @{ id = 'movement-pointer'; category = 'physical-pointer'; field = 'tileMovement[mv-reservation-delivery].wallPointerAfter';
        expect = 'Movement_ReservationDelivery'; mutate = { param($j) (Find-Case $j.tileMovement 'mv-reservation-delivery').wallPointerAfter = 1 } },
    @{ id = 'movement-candidate-slot'; category = 'physical-slot'; field = 'tileMovement[mv-reservation-delivery-then-beam].candidateSlots[0]';
        expect = 'DeliveryThenBeam'; mutate = { param($j) (Find-Case $j.tileMovement 'mv-reservation-delivery-then-beam').candidateSlots[0] = 1 } },
    @{ id = 'beam-outcome'; category = 'outcome'; field = 'beamSwapHandler[bm-choose-keep].outcome';
        expect = 'BeamSwap_BoundCase'; mutate = { param($j) (Find-Case $j.beamSwapHandler 'bm-choose-keep').outcome = 'Rejected' } },
    @{ id = 'ledger-initial-points'; category = 'ledger-balance'; field = 'ledger.initialPoints[1]';
        expect = 'Ledger_Snapshot'; mutate = { param($j) $j.ledger.initialPoints[1] = 41 } },
    @{ id = 'ledger-entry-id'; category = 'ledger-amount'; field = 'ledger.chargeOnce.entryId';
        expect = 'Ledger_Snapshot'; mutate = { param($j) $j.ledger.chargeOnce.entryId = 'skill-1_MUTANT' } },
    @{ id = 'ledger-reject-exception'; category = 'ledger-reason'; field = 'ledger.insufficientFunds.exception';
        expect = 'Ledger_Snapshot'; mutate = { param($j) $j.ledger.insufficientFunds.exception = 'MUTANT' } },
    @{ id = 'mark-register-version'; category = 'mark-version'; field = 'tileMarkState.registerDealtRound.versionAfterFirstRegister';
        expect = 'TileMarkState_Binds'; mutate = { param($j) $j.tileMarkState.registerDealtRound.versionAfterFirstRegister = 2 } },
    @{ id = 'mark-group-exception'; category = 'mark-version'; field = 'tileMarkState.addGroup.sameIdDifferentContentException';
        expect = 'TileMarkState_Binds'; mutate = { param($j) $j.tileMarkState.addGroup.sameIdDifferentContentException = 'MUTANT' } },
    @{ id = 'sleeve-seat'; category = 'input-window'; field = 'sleeveBloomHandler[sb-activate-allowed].goldenInputs.seat';
        expect = 'SleeveBloom_BoundCase'; mutate = { param($j) (Find-Case $j.sleeveBloomHandler 'sb-activate-allowed').goldenInputs.seat = 1 } },
    @{ id = 'sleeve-keep-unknown'; category = 'input-offer'; field = 'sleeveBloomHandler[sb-choose-first].goldenInputs.keep';
        expect = 'SleeveBloom_BoundCase'; mutate = { param($j) (Find-Case $j.sleeveBloomHandler 'sb-choose-first').goldenInputs.keep = 'UNKNOWN' } },
    @{ id = 'sleeve-keep-missing'; category = 'input-offer'; field = 'sleeveBloomHandler[sb-choose-first].goldenInputs.keep';
        expect = 'SleeveBloom_BoundCase'; mutate = { param($j) (Find-Case $j.sleeveBloomHandler 'sb-choose-first').goldenInputs.PSObject.Properties.Remove('keep') } },
    @{ id = 'sleeve-seat-missing'; category = 'input-window'; field = 'sleeveBloomHandler[sb-choose-first].goldenInputs.seat';
        expect = 'SleeveBloom_BoundCase'; mutate = { param($j) (Find-Case $j.sleeveBloomHandler 'sb-choose-first').goldenInputs.PSObject.Properties.Remove('seat') } },
    @{ id = 'sleeve-draw-kind'; category = 'input-tile'; field = 'sleeveBloomHandler[sb-choose-second].goldenInputs.drawKind';
        expect = 'SleeveBloom_BoundCase'; mutate = { param($j) (Find-Case $j.sleeveBloomHandler 'sb-choose-second').goldenInputs.drawKind = 'RINSHAN' } },
    @{ id = 'sleeve-null-typed'; category = 'input-tile'; field = 'sleeveBloomHandler[sb-activate-allowed].goldenInputs.nextDeadWallTileId';
        expect = 'SleeveBloom_BoundCase'; mutate = { param($j) (Find-Case $j.sleeveBloomHandler 'sb-activate-allowed').goldenInputs.nextDeadWallTileId = 'MUTANT' } },
    @{ id = 'sleeve-cost'; category = 'input-pointer'; field = 'sleeveBloomHandler[sb-choose-first].goldenInputs.cost';
        expect = 'SleeveBloom_BoundCase'; mutate = { param($j) (Find-Case $j.sleeveBloomHandler 'sb-choose-first').goldenInputs.cost = 11 } },
    @{ id = 'facts-wall-seat'; category = 'physical-seat'; field = 'tileMovement[mv-swap-hand-with-wall-direct].factsAfter[0].seat';
        expect = 'Movement_SwapHandWithWall'; mutate = { param($j) (Find-Case $j.tileMovement 'mv-swap-hand-with-wall-direct').factsAfter[0].seat = 1 } },
    @{ id = 'facts-hand-index'; category = 'physical-index'; field = 'tileMovement[mv-swap-hand-with-wall-direct].factsAfter[1].index';
        expect = 'Movement_SwapHandWithWall'; mutate = { param($j) (Find-Case $j.tileMovement 'mv-swap-hand-with-wall-direct').factsAfter[1].index = 7 } },
    @{ id = 'm5-delivered'; category = 'physical-tile'; field = 'tileMovement[mv-reservation-delivery-then-beam].deliveredTileId';
        expect = 'DeliveryThenBeam'; mutate = { param($j) (Find-Case $j.tileMovement 'mv-reservation-delivery-then-beam').deliveredTileId = 18 } },
    @{ id = 'm5-delivery-pointer'; category = 'physical-pointer'; field = 'tileMovement[mv-reservation-delivery-then-beam].wallPointerBeforeDelivery';
        expect = 'DeliveryThenBeam'; mutate = { param($j) (Find-Case $j.tileMovement 'mv-reservation-delivery-then-beam').wallPointerBeforeDelivery = 1 } },
    @{ id = 'm3-stale-index'; category = 'physical-index'; field = 'tileMovement[mv-rinshan-and-drawn-swap].factsAfter[0].index';
        expect = 'Movement_Rinshan'; mutate = { param($j) (Find-Case $j.tileMovement 'mv-rinshan-and-drawn-swap').factsAfter[0].index = 1 } },
    @{ id = 'm4-dora-exception'; category = 'metadata'; field = 'tileMovement[mv-swap-hand-with-dead-wall].doraRefusalException';
        expect = 'Movement_DeadWall'; mutate = { param($j) (Find-Case $j.tileMovement 'mv-swap-hand-with-dead-wall').doraRefusalException = 'UNKNOWN' } },
    @{ id = 'm4-wall-exception'; category = 'metadata'; field = 'tileMovement[mv-swap-hand-with-dead-wall].wallSlotRefusalException';
        expect = 'Movement_DeadWall'; mutate = { param($j) (Find-Case $j.tileMovement 'mv-swap-hand-with-dead-wall').wallSlotRefusalException = 'UNKNOWN' } },
    @{ id = 'request-exception'; category = 'metadata'; field = 'beamSwapHandler[bm-keep-original-args-forbidden-at-request].requestException';
        expect = 'BeamSwap_BoundCase'; mutate = { param($j) (Find-Case $j.beamSwapHandler 'bm-keep-original-args-forbidden-at-request').requestException = 'UNKNOWN' } }
)

$rows = New-Object System.Collections.Generic.List[object]
$unexpectedPass = 0
$toolFailures = 0
$wrongFailure = 0
$matrixIndex = 0
foreach ($m in $mutations) {
    $matrixIndex++
    $mutantPath = Join-Path $mutantDir ($m.id + '.json')
    $logPath = Join-Path $logDir ($m.id + '.log')
    $json = Get-Content -LiteralPath $originalCopy -Raw -Encoding UTF8 | ConvertFrom-Json
    & $m.mutate $json
    $jsonText = $json | ConvertTo-Json -Depth 100
    [IO.File]::WriteAllText($mutantPath, $jsonText, (New-Object Text.UTF8Encoding($false)))
    $mutantSha = (Get-FileHash -LiteralPath $mutantPath -Algorithm SHA256).Hash

    $result = Invoke-Consumer -FixturePath $mutantPath -LogPath $logPath
    $counts = $result.counts
    $failedNames = $result.failedTests
    $expectedSeen = ($failedNames -join ';') -match [regex]::Escape($m.expect)
    $behavioralFailure = @($failedNames | Where-Object { $_ -notmatch 'Golden_Header' }).Count -gt 0
    $realFailure = ($result.exit -eq 1) -and ($null -ne $counts) -and
        ($counts.total -eq $positiveCounts.total) -and ($counts.failed -gt 0) -and
        ($counts.errors -eq 0) -and ($counts.skipped -eq 0) -and ($counts.notRun -eq 0) -and
        $behavioralFailure
    $status = if ($realFailure -and $expectedSeen) { 'FAIL_AS_EXPECTED' }
        elseif ($result.exit -eq 0) { 'UNEXPECTED_PASS' }
        elseif (-not $realFailure) { 'TOOL_OR_DISCOVERY_FAILURE' }
        else { 'WRONG_TEST_FAILED' }

    if ($status -eq 'UNEXPECTED_PASS') { $unexpectedPass++ }
    if ($status -eq 'TOOL_OR_DISCOVERY_FAILURE') { $toolFailures++ }
    if ($status -eq 'WRONG_TEST_FAILED') { $wrongFailure++ }

    $rows.Add([pscustomobject]@{
            index = $matrixIndex
            id = $m.id
            category = $m.category
            field = $m.field
            expectedTest = $m.expect
            nativeExit = $result.exit
            total = if ($counts) { $counts.total } else { $null }
            errors = if ($counts) { $counts.errors } else { $null }
            failed = if ($counts) { $counts.failed } else { $null }
            skipped = if ($counts) { $counts.skipped } else { $null }
            notRun = if ($counts) { $counts.notRun } else { $null }
            failedTests = ($failedNames -join ';')
            status = $status
            log = $logPath
            mutantSha256 = $mutantSha
        })
}

$csvPath = Join-Path $matrixDir 'matrix.csv'
$rows | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding utf8

$blocked = @(
    'tm-wrong-phase-round-end',
    'tm-capacity-tiles',
    'tm-no-active-decision',
    'tm-deadline-expired',
    'tm-decision-mismatch',
    'sb-activate-wall-empty-drift',
    'bm-activate-deadwall-empty',
    'bm-activate-deadwall-dora-only'
)

function Invoke-ReadinessProbe {
    param([string]$Root)
    $dir = Join-Path $Root 'readiness'
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $domainDll = Join-Path $Repo 'dotnet/src/Mahjong.Domain/bin/Release/netstandard2.1/Mahjong.Domain.dll'
    if (-not (Test-Path -LiteralPath $domainDll)) {
        throw "Domain DLL missing for readiness probe: $domainDll"
    }

    $readinessSource = @'
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Xunit;

namespace Mahjong.Domain.Tests
{
    public class CheatBindingReadinessTests
    {
        [Fact]
        public void SleeveBloom_WallEmptyDrift_MustRejectLikeGolden()
        {
            string path = Environment.GetEnvironmentVariable("V5_CHEAT_GOLDEN_PATH")!;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(path));
            JsonElement c = doc.RootElement.GetProperty("sleeveBloomHandler").EnumerateArray()
                .Single(x => x.GetProperty("caseId").GetString() == "sb-activate-wall-empty-drift");
            Assert.Equal("Rejected", c.GetProperty("outcome").GetString());
            Assert.Equal("SLEEVE_BLOOM_WALL_EMPTY", c.GetProperty("reason").GetString());

            MethodInfo method = typeof(CheatGoldenBindingTests).GetMethod(
                "SleeveBaseMap", BindingFlags.NonPublic | BindingFlags.Static)!;
            TilePositionMap map = (TilePositionMap)method.Invoke(null, new object?[]
            {
                16, new[] { 40, 44, 48 }, 60, DrawSource.Wall, false, new[] { 32 }, false
            })!;
            Assert.True(map.PositionOf(new TileId(32)).Zone != TileZone.Wall);

            CheatTransition actual = CheatSubmission.ActivateSleeveBloom(
                CheatTestBench.InitialState(), map,
                CheatTestBench.Context(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal),
                CheatTestBench.Economy(), CheatTestBench.Sleeve(), 0, "req-1");
            Assert.False(actual.Success,
                "V4 expects Rejected/SLEEVE_BLOOM_WALL_EMPTY; C# returned " +
                (actual.Success ? "Allowed" : actual.Error.ToString()));
            Assert.Equal(CheatErrorCode.NoWallCandidate, actual.Error);
        }
    }
}
'@
    [IO.File]::WriteAllText((Join-Path $dir 'Readiness.cs'), $readinessSource, (New-Object Text.UTF8Encoding($false)))

    $testSource = Join-Path $Repo 'dotnet/tests/Mahjong.Domain.Tests'
    $projectXml = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <LangVersion>9.0</LangVersion>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <AssemblyName>CheatReadinessProbe</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" Version="4.0.2" />
    <Reference Include="Mahjong.Domain">
      <HintPath>$domainDll</HintPath>
    </Reference>
  </ItemGroup>
  <ItemGroup>
    <Compile Include="$testSource/*.cs" />
    <Compile Include="Readiness.cs" />
  </ItemGroup>
</Project>
"@
    [IO.File]::WriteAllText((Join-Path $dir 'CheatReadinessProbe.csproj'), $projectXml, (New-Object Text.UTF8Encoding($false)))

    $buildLog = Join-Path $dir 'build.log'
    $buildOutput = & $dotnet build (Join-Path $dir 'CheatReadinessProbe.csproj') -c Release 2>&1
    $buildExit = $LASTEXITCODE
    [IO.File]::WriteAllText($buildLog, ($buildOutput | Out-String), (New-Object Text.UTF8Encoding($false)))
    if ($buildExit -ne 0) {
        return [ordered]@{
            status = 'TOOL_FAILURE'
            reason = 'readiness probe build failed'
            buildExit = $buildExit
            buildLog = $buildLog
        }
    }

    $readyDll = Join-Path $dir 'bin/Release/net8.0/CheatReadinessProbe.dll'
    $readyLog = Join-Path $dir 'readiness.log'
    $prior = $env:V5_CHEAT_GOLDEN_PATH
    try {
        $env:V5_CHEAT_GOLDEN_PATH = $originalCopy
        $output = & $dotnet $readyDll -class '*CheatBindingReadinessTests*' -showLiveOutput -noLogo 2>&1
        $exit = $LASTEXITCODE
        $text = $output | Out-String
        [IO.File]::WriteAllText($readyLog, $text, (New-Object Text.UTF8Encoding($false)))
    }
    finally {
        if ($null -eq $prior) {
            Remove-Item Env:V5_CHEAT_GOLDEN_PATH -ErrorAction SilentlyContinue
        }
        else {
            $env:V5_CHEAT_GOLDEN_PATH = $prior
        }
    }

    $counts = Get-TestCounts -Text $text
    $failedNames = @(Get-FailedTestNames -Text $text)
    $conformanceFailure = ($exit -eq 1) -and ($null -ne $counts) -and
        ($counts.total -eq 1) -and ($counts.failed -gt 0) -and ($counts.errors -eq 0) -and
        ($counts.skipped -eq 0) -and ($counts.notRun -eq 0) -and
        ($text -match 'V4 expects Rejected/SLEEVE_BLOOM_WALL_EMPTY')
    $status = if ($exit -eq 0 -and $null -ne $counts -and $counts.failed -eq 0) { 'PASS' }
        elseif ($conformanceFailure) { 'FAIL_CONFORMANCE' }
        else { 'TOOL_OR_UNEXPECTED_FAILURE' }

    return [ordered]@{
        status = $status
        nativeExit = $exit
        counts = $counts
        failedTests = ($failedNames -join ';')
        log = $readyLog
        buildLog = $buildLog
    }
}

$matrixOk = $unexpectedPass -eq 0 -and $toolFailures -eq 0 -and $wrongFailure -eq 0
$readiness = if ($MatrixOnly) {
    [ordered]@{ status = 'NOT_RUN'; reason = 'MatrixOnly' }
}
else {
    Invoke-ReadinessProbe -Root $EvidenceRoot
}
$readinessCauses = @($blocked)
if (-not $MatrixOnly -and $readiness.status -eq 'FAIL_CONFORMANCE') {
    $readinessCauses += 'F-01:sleeve-wall-empty-drift-conformance'
}
$readinessStatus = if (-not $matrixOk) { 'FAIL' }
    elseif ($MatrixOnly) { 'NOT_RUN' }
    elseif ($blocked.Count -gt 0 -or $readiness.status -eq 'FAIL_CONFORMANCE') { 'BLOCKED' }
    elseif ($readiness.status -eq 'PASS') { 'PASS' }
    else { 'FAIL' }

$summary = [ordered]@{
    schema = 'v5-cheat-binding-probe-v2'
    matrixStatus = if ($matrixOk) { 'PASS' } else { 'FAIL' }
    readinessStatus = $readinessStatus
    status = if ($matrixOk -and $readinessStatus -eq 'PASS') { 'PASS' } elseif ($matrixOk) { 'MATRIX_PASS_READINESS_BLOCKED' } else { 'FAIL' }
    positive = [ordered]@{
        exit = $positive.exit
        counts = $positiveCounts
        log = (Join-Path $logDir 'positive-control.log')
    }
    mutations = $rows.Count
    unexpectedPass = $unexpectedPass
    toolFailures = $toolFailures
    wrongFailure = $wrongFailure
    categories = @($mutations | ForEach-Object { $_.category } | Sort-Object -Unique)
    blockedCases = $blocked
    blockedCount = $blocked.Count
    readinessProbe = $readiness
    readinessCauses = $readinessCauses
    fixtureRawSha256 = $rawSha
    fixtureLfNormalizedSha256 = $normalizedSha
    fixtureGitBlobSha1 = $gitBlobSha1
    matrixCsv = $csvPath
    finishedAt = (Get-Date).ToString('o')
}
$summaryPath = Join-Path $EvidenceRoot 'summary.json'
$summary | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $summaryPath -Encoding utf8

# Manifest of every evidence file, computed after all writes except itself.
$manifest = Get-ChildItem -LiteralPath $EvidenceRoot -Recurse -File |
    Where-Object { $_.Name -ne 'evidence-sha256.csv' } |
    Sort-Object FullName |
    ForEach-Object {
        [pscustomobject]@{
            path = $_.FullName
            size = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    }
$manifest | Export-Csv -LiteralPath (Join-Path $EvidenceRoot 'evidence-sha256.csv') -NoTypeInformation -Encoding utf8

Write-Host ("EVIDENCE={0}" -f $EvidenceRoot)
Write-Host ("POSITIVE total={0} failed={1} exit={2}" -f $positiveCounts.total, $positiveCounts.failed, $positive.exit)
foreach ($row in $rows) {
    Write-Host ("{0} {1} {2} exit={3} total={4} failed={5} skipped={6} notRun={7} -> {8}" -f
        $row.index, $row.status, $row.id, $row.nativeExit, $row.total, $row.failed, $row.skipped, $row.notRun, $row.category)
}
Write-Host ("BLOCKED={0} ({1})" -f $blocked.Count, ($blocked -join ','))
Write-Host ("MATRIX={0}" -f $summary.matrixStatus)
Write-Host ("READINESS={0} probe={1} causes={2}" -f $readinessStatus, $readiness.status, ($readinessCauses -join ','))

if (-not $matrixOk) {
    exit 1
}
if (-not $MatrixOnly -and $readinessStatus -ne 'PASS') {
    exit 1
}
exit 0
