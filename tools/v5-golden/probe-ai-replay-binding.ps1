<#
.SYNOPSIS
    Single-field mutation probe for the N3-AI replay/state golden binding.

.DESCRIPTION
    Builds Mahjong.Ai.Tests in Release (non-incremental), copies the real V4-exported
    tests/v5-golden/ai-v1.json into a brand-new TEMP evidence directory, and runs the
    compiled AiReplay golden tests against isolated mutated copies through the
    MAHJONG_AI_GOLDEN override.

    - The repository fixture and the build-output copy are never modified.
    - Every mutation changes exactly one logical field (the two caller-requested scores
      negatives are the documented before+after pair for a rejected case).
    - Negative cases must produce a real failed test: native exit != 0, Total equal to the
      baseline count, Failed >= 1, Errors = 0, Skipped = 0, Not Run = 0, and a real [FAIL]
      line. A crash, an unparsed summary or a "missing fixture" error is a tool failure,
      never an expected FAIL.
    - Positive controls must stay green, proving the test binds behaviour rather than the
      whole file hash.

    Exit codes: 0 = every expectation met; 1 = unexpected pass/fail; 2 = tool/parse failure;
    otherwise the native build failure exit code is propagated.

.PARAMETER RepoRoot
    Repository root. Defaults to two levels above this script.

.PARAMETER EvidenceRoot
    Evidence directory. Must not exist; defaults to a new GUID directory under TEMP.

.PARAMETER DotnetPath
    dotnet executable. Defaults to D:/Unity/dotnet/dotnet.exe.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = '',
    [string]$EvidenceRoot = '',
    [string]$DotnetPath = 'D:/Unity/dotnet/dotnet.exe'
)

$ErrorActionPreference = 'Continue'
$script:ToolFailure = $false

function Write-Step {
    param([string]$Message)
    Write-Host ('[{0}] {1}' -f (Get-Date -Format 'HH:mm:ss'), $Message)
}

function Get-Sha256 {
    param([string]$Path)
    return (Get-FileHash -Path $Path -Algorithm SHA256).Hash
}

function Get-NormalizedSha256 {
    param([string]$Path)
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $text = [System.Text.Encoding]::UTF8.GetString($bytes)
    $normalized = $text.Replace("`r`n", "`n")
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($normalized))
    } finally {
        $sha.Dispose()
    }
    return (($hash | ForEach-Object { $_.ToString('X2') }) -join '')
}

function Get-GitBlobSha1 {
    param([string]$Root, [string]$RelativePath)
    $output = & git -C $Root hash-object -- $RelativePath 2>&1
    if ($LASTEXITCODE -ne 0) {
        return 'GIT-UNAVAILABLE'
    }
    return (($output | Out-String).Trim())
}

function ConvertTo-MutatedFixture {
    param(
        [string]$Template,
        [string]$Destination,
        [scriptblock]$Apply
    )

    $text = [System.IO.File]::ReadAllText($Template, [System.Text.Encoding]::UTF8)
    $root = $text | ConvertFrom-Json
    & $Apply $root
    $json = $root | ConvertTo-Json -Depth 100
    [System.IO.File]::WriteAllText($Destination, $json, (New-Object System.Text.UTF8Encoding($false)))
}

function Invoke-GoldenRun {
    param(
        [string]$FixturePath,
        [string]$LogPath,
        [string]$AssemblyPath,
        [string]$Dotnet
    )

    $previous = $env:MAHJONG_AI_GOLDEN
    $output = @()
    try {
        $env:MAHJONG_AI_GOLDEN = $FixturePath
        $output = & $Dotnet $AssemblyPath -noLogo -class '*AiReplay*' 2>&1
        $exit = $LASTEXITCODE
    } finally {
        if ($null -eq $previous) {
            Remove-Item Env:MAHJONG_AI_GOLDEN -ErrorAction SilentlyContinue
        } else {
            $env:MAHJONG_AI_GOLDEN = $previous
        }
    }

    $output | Out-File -FilePath $LogPath -Encoding utf8
    $logText = ($output | Out-String)

    $summary = [regex]::Matches(
        $logText,
        'Total:\s*(\d+),\s*Errors:\s*(\d+),\s*Failed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Not Run:\s*(\d+)')
    $failedMarkers = [regex]::Matches($logText, '\[FAIL\]').Count
    $parsed = $summary.Count -gt 0
    $total = -1
    $errors = -1
    $failed = -1
    $skipped = -1
    $notRun = -1
    if ($parsed) {
        $match = $summary[$summary.Count - 1]
        $total = [int]$match.Groups[1].Value
        $errors = [int]$match.Groups[2].Value
        $failed = [int]$match.Groups[3].Value
        $skipped = [int]$match.Groups[4].Value
        $notRun = [int]$match.Groups[5].Value
    }

    $missingFixture = $logText -match 'Missing golden fixture|points to a missing fixture'

    return [pscustomobject]@{
        Exit         = $exit
        Parsed       = $parsed
        Total        = $total
        Errors       = $errors
        Failed       = $failed
        Skipped      = $skipped
        NotRun       = $notRun
        FailedMarkers = $failedMarkers
        MissingFixture = $missingFixture
        Log          = $LogPath
    }
}

# ---------------------------------------------------------------------------
# Paths and evidence root
# ---------------------------------------------------------------------------

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
}
$RepoRoot = (Resolve-Path $RepoRoot).Path
$FixtureRelative = 'tests/v5-golden/ai-v1.json'
$FixturePath = Join-Path $RepoRoot ($FixtureRelative -replace '/', '\')
$ProjectPath = Join-Path $RepoRoot 'dotnet/tests/Mahjong.Ai.Tests/Mahjong.Ai.Tests.csproj'
$AssemblyPath = Join-Path $RepoRoot 'dotnet/tests/Mahjong.Ai.Tests/bin/Release/net8.0/Mahjong.Ai.Tests.dll'

foreach ($required in @($FixturePath, $ProjectPath, $DotnetPath)) {
    if (-not (Test-Path -LiteralPath $required)) {
        Write-Host ('PROBE-FAIL: required path is missing: {0}' -f $required)
        exit 2
    }
}

if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
    $EvidenceRoot = Join-Path $env:TEMP ('ai-replay-binding-probe-' + [guid]::NewGuid().ToString('N'))
}
if (Test-Path -LiteralPath $EvidenceRoot) {
    Write-Host ('PROBE-FAIL: evidence root already exists; every run needs a new directory: {0}' -f $EvidenceRoot)
    exit 2
}
New-Item -ItemType Directory -Path $EvidenceRoot | Out-Null

$sourceRawSha = Get-Sha256 $FixturePath
$sourceNormalizedSha = Get-NormalizedSha256 $FixturePath
$sourceGitSha1 = Get-GitBlobSha1 $RepoRoot $FixtureRelative

$meta = [ordered]@{
    evidenceRoot      = $EvidenceRoot
    repoRoot          = $RepoRoot
    fixture           = $FixtureRelative
    fixtureRawSha256  = $sourceRawSha
    fixtureLfSha256   = $sourceNormalizedSha
    fixtureGitSha1    = $sourceGitSha1
    dotnet            = $DotnetPath
    dotnetVersion     = ((& $DotnetPath --version 2>&1 | Out-String).Trim())
    startedAtUtc      = (Get-Date).ToUniversalTime().ToString('o')
}
$meta | ConvertTo-Json | Out-File -FilePath (Join-Path $EvidenceRoot 'meta.json') -Encoding utf8

Write-Step ('evidence root: ' + $EvidenceRoot)
Write-Step ('fixture raw SHA256: ' + $sourceRawSha)

# ---------------------------------------------------------------------------
# Build once; any build failure propagates its numeric exit code.
# ---------------------------------------------------------------------------

$buildLog = Join-Path $EvidenceRoot 'build.log'
$buildOutput = & $DotnetPath build $ProjectPath -c Release --no-incremental -v:minimal 2>&1
$buildExit = $LASTEXITCODE
$buildOutput | Out-File -FilePath $buildLog -Encoding utf8
Write-Step ('build exit: ' + $buildExit)
if ($buildExit -ne 0) {
    Write-Host ('PROBE-FAIL: build failed with native exit {0}; see {1}' -f $buildExit, $buildLog)
    exit $buildExit
}
if (-not (Test-Path -LiteralPath $AssemblyPath)) {
    Write-Host ('PROBE-FAIL: test assembly missing after a successful build: {0}' -f $AssemblyPath)
    exit 2
}

# ---------------------------------------------------------------------------
# Case matrix
# ---------------------------------------------------------------------------

$ai001 = 'AI-001-legal-intent-before-deadline'
$ai002 = 'AI-002-old-round'

$cases = @(
    # Positive controls: must stay green. They prove the probe reads the TEMP copy and that
    # the test binds behaviour, not a whole-file hash.
    @{ Id = 'P01-baseline'; Expect = 'pass'; Path = 'unmodified copy of the real fixture'; Apply = { param($r) } }
    @{ Id = 'P02-note-only'; Expect = 'pass'; Path = 'cases[AI-001].note'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-001-legal-intent-before-deadline' }; $c.note = 'probe control note; not a behavioural field' } }
    @{ Id = 'P03-time-still-before-deadline'; Expect = 'pass'; Path = 'cases[AI-001].v4Call.nowMillis + v5.submittedAtMillis = deadline-2'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-001-legal-intent-before-deadline' }; $c.v4Call.nowMillis = 23998; $c.v5.submittedAtMillis = 23998 } }
    @{ Id = 'P04-provenance-only'; Expect = 'pass'; Path = 'generatedAtHead'; Apply = { param($r) $r.generatedAtHead = 'ffffffffffffffffffffffffffffffffffffffff' } }
    @{ Id = 'P05-session-identity-only'; Expect = 'pass'; Path = 'v5Observation.sessionId'; Apply = { param($r) $r.v5Observation.sessionId = 'probe-session-control' } }

    # Caller-requested scores negatives.
    @{ Id = 'N01-accepted-after-scores0'; Expect = 'fail'; Path = 'cases[AI-001].v4.afterState.scores[0]'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-001-legal-intent-before-deadline' }; $c.v4.afterState.scores[0] = 12345 } }
    @{ Id = 'N02-rejected-before-and-after-scores0'; Expect = 'fail'; Path = 'cases[AI-002].v4.beforeState.scores[0] + afterState.scores[0]'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-002-old-round' }; $c.v4.beforeState.scores[0] = 12345; $c.v4.afterState.scores[0] = 12345 } }

    # Four-seat state fields (before and after).
    @{ Id = 'N03-before-seat0-closed'; Expect = 'fail'; Path = 'cases[AI-001].v4.beforeState.seats[0].closedCount'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-001-legal-intent-before-deadline' }; $c.v4.beforeState.seats[0].closedCount = 12 } }
    @{ Id = 'N04-after-seat1-closed'; Expect = 'fail'; Path = 'cases[AI-001].v4.afterState.seats[1].closedCount'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-001-legal-intent-before-deadline' }; $c.v4.afterState.seats[1].closedCount = 14 } }
    @{ Id = 'N05-before-seat1-drawn'; Expect = 'fail'; Path = 'cases[AI-001].v4.beforeState.seats[1].drawnTileId'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-001-legal-intent-before-deadline' }; $c.v4.beforeState.seats[1].drawnTileId = 4 } }
    @{ Id = 'N06-after-seat1-drawn'; Expect = 'fail'; Path = 'cases[AI-001].v4.afterState.seats[1].drawnTileId'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-001-legal-intent-before-deadline' }; $c.v4.afterState.seats[1].drawnTileId = 4 } }
    @{ Id = 'N07-after-seat2-meld'; Expect = 'fail'; Path = 'cases[AI-001].v4.afterState.seats[2].meldCount'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-001-legal-intent-before-deadline' }; $c.v4.afterState.seats[2].meldCount = 1 } }
    @{ Id = 'N08-after-seat1-discard'; Expect = 'fail'; Path = 'cases[AI-001].v4.afterState.seats[1].discardCount'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-001-legal-intent-before-deadline' }; $c.v4.afterState.seats[1].discardCount = 0 } }
    @{ Id = 'N09-after-seat3-sleeve'; Expect = 'fail'; Path = 'cases[AI-001].v4.afterState.seats[3].sleeveCount'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-001-legal-intent-before-deadline' }; $c.v4.afterState.seats[3].sleeveCount = 1 } }
    @{ Id = 'N10-rejected-after-seat1-discard'; Expect = 'fail'; Path = 'cases[AI-002].v4.afterState.seats[1].discardCount'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-002-old-round' }; $c.v4.afterState.seats[1].discardCount = 1 } }

    # Wall pointer / end / remaining boundaries.
    @{ Id = 'N11-before-wall-pointer'; Expect = 'fail'; Path = 'cases[AI-001].v4.beforeState.wallPointer'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-001-legal-intent-before-deadline' }; $c.v4.beforeState.wallPointer = 1 } }
    @{ Id = 'N12-before-wall-end'; Expect = 'fail'; Path = 'cases[AI-001].v4.beforeState.wallEndExclusive'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-001-legal-intent-before-deadline' }; $c.v4.beforeState.wallEndExclusive = 68 } }
    @{ Id = 'N13-after-wall-remaining'; Expect = 'fail'; Path = 'cases[AI-001].v4.afterState.wallRemaining'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-001-legal-intent-before-deadline' }; $c.v4.afterState.wallRemaining = 68 } }
    @{ Id = 'N14-rejected-before-wall-pointer'; Expect = 'fail'; Path = 'cases[AI-003].v4.beforeState.wallPointer'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-003-old-decision' }; $c.v4.beforeState.wallPointer = 2 } }

    # Event payload and order/identity.
    @{ Id = 'N15-event-tile'; Expect = 'fail'; Path = 'cases[AI-001].v4.events[0].tileId'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-001-legal-intent-before-deadline' }; $c.v4.events[0].tileId = 135 } }
    @{ Id = 'N16-event-player'; Expect = 'fail'; Path = 'cases[AI-001].v4.events[0].player'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-001-legal-intent-before-deadline' }; $c.v4.events[0].player = 2 } }
    @{ Id = 'N17-event-type'; Expect = 'fail'; Path = 'cases[AI-001].v4.events[0].type'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-001-legal-intent-before-deadline' }; $c.v4.events[0].type = 'TileDrawn' } }
    @{ Id = 'N18-event-structured'; Expect = 'fail'; Path = 'cases[AI-001].v4.events[0].structured'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-001-legal-intent-before-deadline' }; $c.v4.events[0].structured = $false } }
    @{ Id = 'N19-event-count'; Expect = 'fail'; Path = 'cases[AI-001].v4.eventCount'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-001-legal-intent-before-deadline' }; $c.v4.eventCount = 2 } }

    # Last discard, phase, version, seats, sticks, cheat points.
    @{ Id = 'N20-last-discard-tile'; Expect = 'fail'; Path = 'cases[AI-001].v4.afterState.lastDiscardTileId'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-001-legal-intent-before-deadline' }; $c.v4.afterState.lastDiscardTileId = 4 } }
    @{ Id = 'N21-last-discard-player'; Expect = 'fail'; Path = 'cases[AI-001].v4.afterState.lastDiscardPlayer'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-001-legal-intent-before-deadline' }; $c.v4.afterState.lastDiscardPlayer = 2 } }
    @{ Id = 'N22-after-phase'; Expect = 'fail'; Path = 'cases[AI-001].v4.afterState.phase'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-001-legal-intent-before-deadline' }; $c.v4.afterState.phase = 'DISCARD' } }
    @{ Id = 'N23-after-round-version'; Expect = 'fail'; Path = 'cases[AI-001].v4.afterState.roundVersion'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-001-legal-intent-before-deadline' }; $c.v4.afterState.roundVersion = 2 } }
    @{ Id = 'N24-before-dealer'; Expect = 'fail'; Path = 'cases[AI-001].v4.beforeState.dealerSeat'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-001-legal-intent-before-deadline' }; $c.v4.beforeState.dealerSeat = 0 } }
    @{ Id = 'N25-after-current-player'; Expect = 'fail'; Path = 'cases[AI-001].v4.afterState.currentPlayer'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-001-legal-intent-before-deadline' }; $c.v4.afterState.currentPlayer = 2 } }
    @{ Id = 'N26-after-riichi-sticks'; Expect = 'fail'; Path = 'cases[AI-001].v4.afterState.riichiSticks'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-001-legal-intent-before-deadline' }; $c.v4.afterState.riichiSticks = 1 } }
    @{ Id = 'N27-before-honba-sticks'; Expect = 'fail'; Path = 'cases[AI-002].v4.beforeState.honbaSticks'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-002-old-round' }; $c.v4.beforeState.honbaSticks = 1 } }
    @{ Id = 'N28-after-cheat-points'; Expect = 'fail'; Path = 'cases[AI-001].v4.afterState.cheatPoints[2]'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-001-legal-intent-before-deadline' }; $c.v4.afterState.cheatPoints[2] = 10 } }
    @{ Id = 'N29-after-turn-number'; Expect = 'fail'; Path = 'cases[AI-001].v4.afterState.turnNumber'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-001-legal-intent-before-deadline' }; $c.v4.afterState.turnNumber = 13 } }

    # Receipt semantics and rejected zero-side-effect snapshots.
    @{ Id = 'N30-accepted-receipt-status'; Expect = 'fail'; Path = 'cases[AI-001].v4.receiptStatus'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-001-legal-intent-before-deadline' }; $c.v4.receiptStatus = 'REJECTED' } }
    @{ Id = 'N31-rejected-reason'; Expect = 'fail'; Path = 'cases[AI-002].v4.reason'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-002-old-round' }; $c.v4.reason = 'WRONG_PHASE' } }
    @{ Id = 'N32-rejected-after-version'; Expect = 'fail'; Path = 'cases[AI-002].v4.afterState.roundVersion'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-002-old-round' }; $c.v4.afterState.roundVersion = 1 } }
    @{ Id = 'N33-accepted-intent-ref'; Expect = 'fail'; Path = 'cases[AI-001].v5.intentActionRef'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-001-legal-intent-before-deadline' }; $c.v5.intentActionRef = 'L1' } }

    # Strict schema handling: unknown enum, missing field, wrong type, extra field.
    @{ Id = 'N34-unknown-phase-enum'; Expect = 'fail'; Path = 'cases[AI-001].v4.afterState.phase'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-001-legal-intent-before-deadline' }; $c.v4.afterState.phase = 'BOGUS_PHASE' } }
    @{ Id = 'N35-missing-state-field'; Expect = 'fail'; Path = 'cases[AI-001].v4.afterState.wallRemaining (removed)'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-001-legal-intent-before-deadline' }; $c.v4.afterState.PSObject.Properties.Remove('wallRemaining') } }
    @{ Id = 'N36-wrong-type-score'; Expect = 'fail'; Path = 'cases[AI-001].v4.afterState.scores[1] = string'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-001-legal-intent-before-deadline' }; $c.v4.afterState.scores[1] = '25000' } }
    @{ Id = 'N37-extra-state-field'; Expect = 'fail'; Path = 'cases[AI-001].v4.afterState.unexpectedField (added)'; Apply = { param($r) $c = $r.cases | Where-Object { $_.id -eq 'AI-001-legal-intent-before-deadline' }; $c.v4.afterState | Add-Member -NotePropertyName unexpectedField -NotePropertyValue 1 } }
    @{ Id = 'N38-fixture-identity-sentinel'; Expect = 'fail'; Path = 'v4Commit (proves the TEMP copy is the file under test)'; Apply = { param($r) $r.v4Commit = '0000000000000000000000000000000000000000' } }
)

$baselineTemplate = Join-Path $EvidenceRoot 'ai-v1.baseline.json'
Copy-Item -LiteralPath $FixturePath -Destination $baselineTemplate -Force

$rows = New-Object System.Collections.ArrayList
$unexpected = 0
$expectedTotal = -1

foreach ($case in $cases) {
    $fixtureCopy = Join-Path $EvidenceRoot ($case.Id + '.json')
    ConvertTo-MutatedFixture -Template $baselineTemplate -Destination $fixtureCopy -Apply $case.Apply
    $mutatedSha = Get-Sha256 $fixtureCopy
    $logPath = Join-Path $EvidenceRoot ($case.Id + '.log')
    $result = Invoke-GoldenRun -FixturePath $fixtureCopy -LogPath $logPath -AssemblyPath $AssemblyPath -Dotnet $DotnetPath

    if (-not $result.Parsed) {
        $script:ToolFailure = $true
        Write-Host ('TOOL-FAILURE: no test summary parsed for {0}; see {1}' -f $case.Id, $logPath)
        $unexpected++
        $status = 'TOOL-FAILURE'
    } elseif ($result.MissingFixture) {
        $script:ToolFailure = $true
        Write-Host ('TOOL-FAILURE: fixture-not-found path taken for {0}; see {1}' -f $case.Id, $logPath)
        $unexpected++
        $status = 'TOOL-FAILURE'
    } else {
        if ($expectedTotal -lt 0) {
            $expectedTotal = $result.Total
        }

        $countsOk = ($result.Total -eq $expectedTotal) -and
                    ($result.Errors -eq 0) -and
                    ($result.Skipped -eq 0) -and
                    ($result.NotRun -eq 0) -and
                    ($result.Total -gt 0)

        if ($case.Expect -eq 'pass') {
            $ok = $countsOk -and ($result.Exit -eq 0) -and ($result.Failed -eq 0) -and ($result.FailedMarkers -eq 0)
            $status = if ($ok) { 'PASS' } else { 'UNEXPECTED-FAIL' }
        } else {
            $ok = $countsOk -and ($result.Exit -ne 0) -and ($result.Failed -ge 1) -and ($result.FailedMarkers -ge 1)
            $status = if ($ok) { 'PASS' } else { 'UNEXPECTED-PASS' }
        }

        if (-not $ok) {
            $unexpected++
            Write-Host ('UNEXPECTED: {0} expected {1} but exit={2} total={3} failed={4} errors={5} skipped={6} notRun={7}; {8}' -f `
                $case.Id, $case.Expect, $result.Exit, $result.Total, $result.Failed, $result.Errors, $result.Skipped, $result.NotRun, $logPath)
        }
    }

    $null = $rows.Add([pscustomobject]@{
        id          = $case.Id
        expectation = $case.Expect
        jsonPath    = $case.Path
        status      = $status
        exit        = $result.Exit
        total       = $result.Total
        passed      = if ($result.Total -ge 0) { $result.Total - $result.Failed - $result.Errors } else { -1 }
        failed      = $result.Failed
        errors      = $result.Errors
        skipped     = $result.Skipped
        notRun      = $result.NotRun
        failMarkers = $result.FailedMarkers
        fixtureSha  = $mutatedSha
        log         = $result.Log
    })

    Write-Step ('{0,-40} {1,-10} exit={2} total={3} failed={4} {5}' -f $case.Id, $case.Expect, $result.Exit, $result.Total, $result.Failed, $status)
}

$rows | Export-Csv -Path (Join-Path $EvidenceRoot 'matrix.csv') -NoTypeInformation -Encoding UTF8

$sourceRawAfter = Get-Sha256 $FixturePath
$sourcePreserved = ($sourceRawAfter -eq $sourceRawSha)
if (-not $sourcePreserved) {
    Write-Host ('PROBE-FAIL: repository fixture changed during the probe: {0} -> {1}' -f $sourceRawSha, $sourceRawAfter)
}

$summary = [ordered]@{
    evidenceRoot       = $EvidenceRoot
    cases              = $rows.Count
    positiveControls   = ($rows | Where-Object { $_.expectation -eq 'pass' }).Count
    negativeControls   = ($rows | Where-Object { $_.expectation -eq 'fail' }).Count
    expectedTotal      = $expectedTotal
    unexpectedResults  = $unexpected
    toolFailure        = $script:ToolFailure
    sourceFixtureRawSha256 = $sourceRawSha
    sourceFixtureLfSha256  = $sourceNormalizedSha
    sourceFixtureGitSha1   = $sourceGitSha1
    sourceFixturePreserved = $sourcePreserved
    sourceFixtureRawSha256After = $sourceRawAfter
    finishedAtUtc      = (Get-Date).ToUniversalTime().ToString('o')
}
$summary | ConvertTo-Json | Out-File -FilePath (Join-Path $EvidenceRoot 'summary.json') -Encoding utf8

Write-Host ''
Write-Host '=== PROBE SUMMARY ==='
$rows | Format-Table id, expectation, status, exit, total, failed, errors, skipped, notRun -AutoSize | Out-String -Width 200 | Write-Host
Write-Host ('cases={0} positive={1} negative={2} expectedTotal={3} unexpected={4} toolFailure={5}' -f `
    $rows.Count, $summary.positiveControls, $summary.negativeControls, $expectedTotal, $unexpected, $script:ToolFailure)
Write-Host ('source raw SHA256: {0}' -f $sourceRawSha)
Write-Host ('source LF  SHA256: {0}' -f $sourceNormalizedSha)
Write-Host ('source git  SHA1  : {0}' -f $sourceGitSha1)
Write-Host ('evidence: {0}' -f $EvidenceRoot)

if ($unexpected -gt 0) {
    Write-Host 'PROBE-FAIL: unexpected result(s) in the mutation matrix.'
    if ($script:ToolFailure) { exit 2 }
    exit 1
}

if (-not $sourcePreserved) {
    Write-Host 'PROBE-FAIL: source fixture was not preserved.'
    exit 1
}

Write-Host 'PROBE-PASS: every positive control stayed green and every negative produced a real failed test.'
exit 0
