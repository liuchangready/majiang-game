#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$TestRunRoot = ''
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$runner = Join-Path $repo 'tools/unity/run-p45-c04-privacy-matrix.ps1'
if (-not (Test-Path -LiteralPath $runner -PathType Leaf)) { throw "Runner is missing: $runner" }
$script:TestRoot = $TestRunRoot
if ([string]::IsNullOrWhiteSpace($script:TestRoot)) {
    $script:TestRoot = Join-Path $repo ('build/runner-tests-' + [guid]::NewGuid().ToString('N'))
}
$script:TestRoot = [IO.Path]::GetFullPath($script:TestRoot)
if (Test-Path -LiteralPath $script:TestRoot) { throw 'Runner test root exists; refusing to overwrite evidence.' }
function Assert-TestRootPathBudget([string]$Root) {
    $probe = [IO.Path]::GetFullPath((Join-Path $Root 'c000\control-session-before-stale-submit.json'))
    if ($probe.Length -gt 240) { throw "Runner test root exceeds the portable Windows path budget before writing fixtures: $($probe.Length) > 240" }
    return $probe.Length
}
$script:RootPathBudget = Assert-TestRootPathBudget $script:TestRoot
[IO.Directory]::CreateDirectory($script:TestRoot) | Out-Null
$script:TestTemp = Join-Path $script:TestRoot 'temp'
[IO.Directory]::CreateDirectory($script:TestTemp) | Out-Null
$env:TEMP = $script:TestTemp
$env:TMP = $script:TestTemp

. $runner -LoadFunctionsOnly

$script:Tests = 0
$script:Failures = New-Object Collections.Generic.List[string]
$script:Results = New-Object Collections.Generic.List[object]
$script:CaseDirectoryOrdinal = 0

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Assert-Equal($Expected, $Actual, [string]$Message) {
    if (-not [object]::Equals($Expected, $Actual)) { throw "$Message expected=$Expected actual=$Actual" }
}
function Assert-Throws([scriptblock]$Action, [string]$MessagePart) {
    $caught = $null
    try { & $Action } catch { $caught = $_ }
    if ($null -eq $caught) { throw "Expected failure was not raised: $MessagePart" }
    if ($caught.Exception.Message -notlike ('*' + $MessagePart + '*')) {
        throw "Failure did not contain '$MessagePart': $($caught.Exception.Message)"
    }
}
function New-TestDirectory([string]$Name) {
    $path = Join-Path $script:TestRoot $Name
    [IO.Directory]::CreateDirectory($path) | Out-Null
    return $path
}
function New-CaseFixtureRoot([string]$CaseName) {
    $script:CaseDirectoryOrdinal++
    $leaf = 'c' + $script:CaseDirectoryOrdinal.ToString('D3',[Globalization.CultureInfo]::InvariantCulture)
    return New-TestDirectory $leaf
}
function New-CaseEvidenceFixture([string]$CaseName, [string]$Root) {
    [IO.Directory]::CreateDirectory($Root) | Out-Null
    Write-C04Json (Join-Path $Root 'summary.json') ([ordered]@{ fixtureOnly=$true; schemaVersion=1; case=$CaseName; exitCode=0; evidence='player-case.jsonl' })
    $events = @(
        (@{ fixtureOnly=$true; event='start'; case=$CaseName; pid=12345 } | ConvertTo-Json -Compress),
        (@{ fixtureOnly=$true; event='case_complete'; case=$CaseName } | ConvertTo-Json -Compress)
    )
    Write-C04Utf8 (Join-Path $Root 'player-case.jsonl') (($events -join "`n") + "`n")
    $fixtureRaw = '{"fixtureOnly":true,"wireFrames":[]}'
    switch -CaseSensitive ($CaseName) {
        'RealRoom_FourPlayersSpectatorAndRoleSwitchUseIndependentSessions' {
            Write-C04Utf8 (Join-Path $Root 'role-switch-raw/raw-frames.json') $fixtureRaw
        }
        'RealRoom_DelayedOldRawReceiveIsRejectedByNewBinding' {
            $bytes = [Text.Encoding]::UTF8.GetBytes('FIXTURE ONLY delayed old raw')
            $e = @{ fixtureOnly=$true; schemaVersion=1; oldSessionId='old-session'; newSessionId='new-session'; oldSeat=0; newSeat=1; oldFrameSha256=(Get-C04Sha256Bytes $bytes); oldFrameBase64=[Convert]::ToBase64String($bytes); stateVersion='12'; tilesBefore='1,2,3'; tilesAfter='1,2,3'; accepted=$false }
            Write-C04Json (Join-Path $Root 'delayed-old-raw/delayed-old-raw.json') $e
        }
        'RealRoom_ColdReopenReconcilesOriginalRequestWithoutDuplicate' {
            $e = @{ fixtureOnly=$true; schemaVersion=1; requestId='fixture-request'; scopeId='fixture-scope'; canonicalDigest=('a' * 64); stateVersion='12'; eventSeq='7'; receiptStatus='Applied'; receiptCommandVersion='12'; receiptEventSeq='7'; commandFrames=0; receiptQueryFrames=1; fromReceiptLookup=$true; zeroDuplicateStateDelta=$true }
            Write-C04Json (Join-Path $Root 'recovery-query/recovery-query.json') $e
            Write-C04Utf8 (Join-Path $Root 'recovery-raw/raw-frames.json') $fixtureRaw
        }
        'RealRoom_LegalSleeveAndFeeOwnershipAcrossFourSeatsAndSpectator' {
            $e = @{ fixtureOnly=$true; schemaVersion=1; version='12'; seats=4; targetClosedTiles=13; targetLedgerEntries=1; targetCheatPoints=5; targetOfferId='fixture-offer'; spectatorVersion='12'; spectatorHasPublic=$true; spectatorHasPlayer=$false }
            Write-C04Json (Join-Path $Root 'four-owner/four-owner.json') $e
            Write-C04Utf8 (Join-Path $Root 'four-owner-raw/raw-frames.json') $fixtureRaw
        }
        'RealRoom_ReleasedSeatAiAdvancesAfterPlayerCloseWithSpectatorOnly' {
            $e = @{ fixtureOnly=$true; schemaVersion=1; releaseVersion='10'; afterVersion='11'; advancedWithoutPlayerBinding=$true; claimed=$true; claimEpochBefore=1; claimEpoch=2; claimEpochAdvanced=$true; controlPresent=$true; controlHuman=$true; claimRequestId='fixture-claim'; claimReceiptStatus='Recorded'; claimReceiptReason='LOCAL_CONTROL'; staleRequestId='fixture-stale'; staleDecisionId='fixture-captured-decision'; staleSessionId='fixture-player-session'; staleScopeId='fixture-current-scope'; staleBindingEpoch=5; staleReceiptStatus='Rejected'; expectedStaleReason='DECISION_MISMATCH'; staleReason='DECISION_MISMATCH'; capturedDecisionEpoch=1; staleEpoch=1; staleRound='fixture-round'; staleCommandFrames=1; stalePendingAfter=0; staleNoCommitMetadata=$true; staleDecisionRejected=$true }
            Write-C04Json (Join-Path $Root 'released-control/released-control.json') $e
            $session = @{ fixtureOnly=$true; schemaVersion=1; case=$CaseName; phase='before-stale-submit'; sessionState='Ready'; bindingPresent=$true; sessionId='fixture-player-session'; scopeId='fixture-current-scope'; role=1; seat=0; bindingEpoch=5; snapshotStateVersion=11; snapshotEventSeq=8; snapshotRoundId='fixture-round'; snapshotPhase='Discard'; snapshotCurrentSeat=0; snapshotDecisionId='fixture-current-decision'; capturedStaleDecisionId='fixture-captured-decision'; capturedStaleDecisionEpoch=1; snapshotControlEpoch=2; pendingCommands=0; claimCommandFrames=1 }
            Write-C04Json (Join-Path $Root 'control-session-before-stale-submit.json') $session
            $reconnect = @{ fixtureOnly=$true; schemaVersion=1; case=$CaseName; recoveryBranchTaken=$true; oldState='Reconnecting'; newState='Ready'; oldSessionId='fixture-player-session'; newSessionId='fixture-player-session'; oldRoomId='fixture-room'; newRoomId='fixture-room'; oldRole=1; newRole=1; oldSeat=0; newSeat=0; oldScopeId='fixture-old-scope'; newScopeId='fixture-middle-scope'; oldWelcomeSessionId='fixture-player-session'; newWelcomeSessionId='fixture-player-session'; oldWelcomeRoomId='fixture-room'; newWelcomeRoomId='fixture-room'; oldWelcomeScopeId='fixture-old-scope'; newWelcomeScopeId='fixture-middle-scope'; oldWelcomeRole=1; newWelcomeRole=1; oldWelcomeSeat=0; newWelcomeSeat=0; welcomeFrameCountBefore=1; welcomeFrameCountAfter=2; resumeTicketSha256Before=('a'*64); resumeTicketSha256After=('a'*64); sameSessionIdentity=$true; scopeRotated=$true; oldBindingEpoch=3; newBindingEpoch=4; preRecoveryCachedControlEpoch=1; newSnapshotControlEpoch=2; transportOpen=$true; pendingCommands=0 }
            Write-C04Json (Join-Path $Root 'control-session-reconnect.json') $reconnect
            $ready = @{ fixtureOnly=$true; schemaVersion=1; case=$CaseName; phase='post-recovery-no-change-claim'; sessionState='Ready'; requestId='fixture-nochange'; receiptStatus='Recorded'; receiptReason='LOCAL_CONTROL'; recoveryBranchTaken=$true; oldSessionId='fixture-player-session'; newSessionId='fixture-player-session'; oldRoomId='fixture-room'; newRoomId='fixture-room'; oldScopeId='fixture-middle-scope'; newScopeId='fixture-current-scope'; oldBindingEpoch=4; bindingEpoch=5; welcomeFrameCountBefore=2; welcomeFrameCountAfter=3; oldWelcomeAccountId='fixture-account'; newWelcomeAccountId='fixture-account'; oldWelcomeSessionId='fixture-player-session'; newWelcomeSessionId='fixture-player-session'; oldWelcomeRoomId='fixture-room'; newWelcomeRoomId='fixture-room'; oldWelcomeScopeId='fixture-middle-scope'; newWelcomeScopeId='fixture-current-scope'; oldWelcomeRole=1; newWelcomeRole=1; oldWelcomeSeat=0; newWelcomeSeat=0; resumeTicketSha256Before=('b'*64); resumeTicketSha256After=('b'*64); controlEpochBefore=2; controlEpochAfter=2; controlHuman=$true; sessionId='fixture-player-session'; scopeId='fixture-current-scope'; commandFrames=1; pendingCommands=0 }
            Write-C04Json (Join-Path $Root 'control-session-ready-positive.json') $ready
            Write-C04Utf8 (Join-Path $Root 'released-control-raw/raw-frames.json') $fixtureRaw
        }
        'RealRoom_OpenDecisionDeadlineContinuesAfterPlayerClose' {
            $e = @{ fixtureOnly=$true; schemaVersion=1; case=$CaseName; roomId='fixture-room'; roundId='fixture-round'; decisionId='fixture-decision'; decisionSeat=0; controlEpoch=2; decisionDeadlineRemainingMs=5000; clockDecisionSeat=0; clockServerTimeMs=10000; clockDeadlineRemainingMs=5000; authoritativeDeadlineServerTimeMs=15000; playerSessionId='fixture-player-session'; playerScopeId='fixture-player-scope'; playerRole=1; playerSeat=0; playerBindingEpoch=3; releaseVersion=0; releaseCursorStreamVersion=0; releaseCursorProjectionSeq=0; closeRequestedUtcMs=100; closeCompletedUtcMs=110; playerBindingClosed=$true; spectatorSessionId='fixture-spectator-session'; spectatorRole=2; spectatorSeat=-1; spectatorBaselineVersion=0; spectatorBaselineCursorStreamVersion=0; spectatorBaselineCursorProjectionSeq=0; afterVersion=1; afterCursorStreamVersion=1; afterCursorProjectionSeq=1; afterPhase='Discard'; advancedWithoutPlayerBinding=$true }
            Write-C04Json (Join-Path $Root 'deadline-evidence.json') $e
        }
        'RealRoom_ClosedPlayerToSpectatorToNewPlayerReceivePathRejectsOldRaw' {
            Write-C04Utf8 (Join-Path $Root 'closed-binding-raw/raw-frames.json') $fixtureRaw
        }
        default { throw "Unknown fixture case $CaseName" }
    }
}
function Invoke-Case([string]$Name, [scriptblock]$Body) {
    $script:Tests++
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try {
        & $Body
        $script:Results.Add([pscustomobject]@{ name = $Name; result = 'PASS'; seconds = [Math]::Round($sw.Elapsed.TotalSeconds, 3); error = $null })
        Write-Host "PASS $Name"
    } catch {
        $script:Failures.Add($Name + ': ' + $_.Exception.Message)
        $script:Results.Add([pscustomobject]@{ name = $Name; result = 'FAIL'; seconds = [Math]::Round($sw.Elapsed.TotalSeconds, 3); error = $_.Exception.Message })
        Write-Host "FAIL $Name : $($_.Exception.Message)"
    }
}

function New-FixturePlayer {
    param([string]$Root, [string]$SourceHead = ('a' * 40))
    $playerRoot = Join-Path $Root 'player'
    [IO.Directory]::CreateDirectory($playerRoot) | Out-Null
    $exe = Join-Path $playerRoot 'Mahjong.exe'
    [IO.File]::WriteAllBytes($exe, [Text.Encoding]::UTF8.GetBytes('fixture-player-' + [guid]::NewGuid().ToString('N')))
    $managed = Join-Path $playerRoot 'Mahjong_Data/Managed'
    [IO.Directory]::CreateDirectory($managed) | Out-Null
    $plugins = [ordered]@{}
    foreach ($name in $script:C04RequiredPlugins) {
        $file = Join-Path $managed $name
        [IO.File]::WriteAllBytes($file, [Text.Encoding]::UTF8.GetBytes('fixture-' + $name + '-' + [guid]::NewGuid().ToString('N')))
        $plugins[$name] = (Get-C04Sha256File $file)
    }
    $manifestPath = Join-Path $playerRoot 'player-package.sha256'
    $files = @(Get-ChildItem -LiteralPath $playerRoot -Recurse -File | Where-Object { $_.FullName -ne $manifestPath } | Sort-Object FullName)
    $lines = New-Object Collections.Generic.List[string]
    [long]$bytes = 0
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($playerRoot.Length).TrimStart('\', '/') -replace '\\', '/'
        $lines.Add((Get-C04Sha256File $file.FullName) + '  ' + $relative)
        $bytes += $file.Length
    }
    Write-C04Utf8 $manifestPath (($lines -join "`n") + "`n")
    $marker = [ordered]@{
        schemaVersion = 1
        kind = 'c04-privacy-player-build'
        result = 'Succeeded'
        errors = 0
        warnings = 0
        target = 'StandaloneWindows64'
        output = $exe
        playerSha256 = (Get-C04Sha256File $exe)
        playerBytes = (Get-Item -LiteralPath $exe).Length
        packageFileCount = $files.Count
        packageBytes = $bytes
        packageManifest = $manifestPath
        packageManifestSha256 = (Get-C04Sha256File $manifestPath)
        bytes = $bytes
        sourceHead = $SourceHead
        buildCommit = $SourceHead
        errorMessages = @()
        scenes = @($script:C04RequiredScenes)
        plugins = $plugins
    }
    $markerPath = Join-Path $Root 'player-build.json'
    Write-C04Json $markerPath $marker
    return [pscustomobject]@{ root = $playerRoot; markerPath = $markerPath; marker = $marker; exe = $exe; manifest = $manifestPath }
}

function Write-MarkerVariant($Fixture, [string]$Name, [scriptblock]$Mutate) {
    $copy = $Fixture.marker | ConvertTo-Json -Depth 12 | ConvertFrom-Json
    & $Mutate $copy
    $path = Join-Path $Fixture.root ($Name + '.json')
    Write-C04Json $path $copy
    return $path
}

function Write-TestScript([string]$Path, [string]$Body) {
    Write-C04Utf8 $Path ($Body + "`n")
}

Invoke-Case 'source identity accepts clean frozen head and rejects dirty/mismatch' {
    $head = '0123456789abcdef0123456789abcdef01234567'
    $identity = Assert-C04SourceIdentity -Head $head -DirtyEntries @() -DiffCheckExit 0 -ExpectedHead $head
    Assert-Equal $head $identity.head 'clean head'
    Assert-Throws { Assert-C04SourceIdentity -Head $head -DirtyEntries @(' M file.cs') -DiffCheckExit 0 } 'dirty'
    Assert-Throws { Assert-C04SourceIdentity -Head $head -DirtyEntries @() -DiffCheckExit 1 } 'diff --check'
    Assert-Throws { Assert-C04SourceIdentity -Head $head -DirtyEntries @() -ExpectedHead ('b' * 40) } 'does not match'
}

Invoke-Case 'copy input closure is byte-identical and detects destination mutation' {
    $fake = New-TestDirectory 'copy-repo'
    $unity = Join-Path $fake 'unity/Mahjong.Unity'
    foreach ($name in @('Assets', 'Packages', 'ProjectSettings')) {
        $dir = Join-Path $unity $name
        [IO.Directory]::CreateDirectory($dir) | Out-Null
        Write-C04Utf8 (Join-Path $dir ($name + '.txt')) ('source-' + $name)
    }
    $destination = Join-Path (New-TestDirectory 'copy-out') 'staging'
    $copied = Copy-C04UnityInputs -Repo $fake -Destination $destination
    Assert-Equal $copied.source.sha256 $copied.staged.sha256 'copy hash'
    Assert-Equal $copied.source.count $copied.staged.count 'copy count'
    $stagedManifest = Get-C04SelectedManifest -Base $destination -RelativeRoots @('Assets', 'Packages', 'ProjectSettings')
    Assert-C04ManifestEqual $copied.source $stagedManifest 'staged'
    Write-C04Utf8 (Join-Path $destination 'Assets/Assets.txt') 'mutated'
    $mutated = Get-C04SelectedManifest -Base $destination -RelativeRoots @('Assets', 'Packages', 'ProjectSettings')
    Assert-Throws { Assert-C04ManifestEqual $copied.source $mutated 'staged' } 'input closure mismatch'
}

Invoke-Case 'Unity XML accepts only the exact passed case and denominator' {
    $dir = New-TestDirectory 'xml'
    $expected = 'Mahjong.Unity.Tests.PlayMode.C04PrivacyRoleSwitchPlayModeTests.RealRoom_FourPlayersSpectatorAndRoleSwitchUseIndependentSessions'
    $valid = @"
<?xml version="1.0" encoding="utf-8"?>
<test-run result="Passed" total="1" passed="1" failed="0" skipped="0" inconclusive="0" errors="0" not-run="0">
  <test-suite result="Passed" total="1" passed="1" failed="0" skipped="0">
    <test-case fullname="$expected" name="RealRoom" result="Passed" />
  </test-suite>
</test-run>
"@
    $validPath = Join-Path $dir 'valid.xml'
    Write-C04Utf8 $validPath $valid
    $parsed = Assert-C04UnityXml -XmlPath $validPath -ExpectedTests @($expected)
    Assert-Equal 1 $parsed.total 'valid total'
    Assert-Equal 1 $parsed.passed 'valid passed'
    $variants = @{
        skipped = ($valid -replace 'skipped="0"', 'skipped="7"' -replace 'total="1" passed="1" failed="0" skipped="7"', 'total="8" passed="1" failed="0" skipped="7"')
        errors = ($valid -replace 'errors="0"', 'errors="3"')
        failed = ($valid -replace 'failed="0"', 'failed="1"')
        wrongCase = ($valid -replace 'RealRoom_FourPlayersSpectatorAndRoleSwitchUseIndependentSessions', 'SomeOtherTest')
        wrongResult = ($valid -replace 'result="Passed"', 'result="Failed"')
        notrun = ($valid -replace 'not-run="0"', 'not-run="1"')
    }
    foreach ($pair in $variants.GetEnumerator()) {
        $path = Join-Path $dir ($pair.Key + '.xml')
        Write-C04Utf8 $path $pair.Value
        Assert-Throws { Assert-C04UnityXml -XmlPath $path -ExpectedTests @($expected) } 'Unity XML'
    }
    Assert-Throws { Assert-C04UnityXml -XmlPath (Join-Path $dir 'missing.xml') -ExpectedTests @($expected) } 'missing'
}

Invoke-Case 'build marker binds source, player, package and plugin closure' {
    $fixture = New-FixturePlayer -Root (New-TestDirectory 'marker-valid')
    $valid = Assert-C04BuildMarker -MarkerPath $fixture.markerPath -ExpectedSourceHead $fixture.marker.sourceHead -ExpectedBuildCommit $fixture.marker.buildCommit
    Assert-Equal $fixture.marker.playerSha256 $valid.playerSha256 'player sha'
    Assert-Equal $fixture.marker.packageFileCount $valid.packageFileCount 'package count'
    Assert-Equal $fixture.marker.packageBytes $valid.packageBytes 'package bytes'

    $wrongSource = Write-MarkerVariant $fixture 'wrong-source' { param($m) $m.sourceHead = ('b' * 40) }
    Assert-Throws { Assert-C04BuildMarker -MarkerPath $wrongSource -ExpectedSourceHead $fixture.marker.sourceHead -ExpectedBuildCommit $fixture.marker.buildCommit } 'sourceHead'
    $errors = Write-MarkerVariant $fixture 'marker-errors' { param($m) $m.errors = 3 }
    Assert-Throws { Assert-C04BuildMarker -MarkerPath $errors -ExpectedSourceHead $fixture.marker.sourceHead -ExpectedBuildCommit $fixture.marker.buildCommit } 'errors'
    $wrongBytes = Write-MarkerVariant $fixture 'marker-bytes' { param($m) $m.playerBytes = 1 }
    Assert-Throws { Assert-C04BuildMarker -MarkerPath $wrongBytes -ExpectedSourceHead $fixture.marker.sourceHead -ExpectedBuildCommit $fixture.marker.buildCommit } 'playerBytes'
    $wrongPlugin = Write-MarkerVariant $fixture 'marker-plugin' { param($m) $m.plugins.'Mahjong.LocalHost.dll' = ('0' * 64) }
    Assert-Throws { Assert-C04BuildMarker -MarkerPath $wrongPlugin -ExpectedSourceHead $fixture.marker.sourceHead -ExpectedBuildCommit $fixture.marker.buildCommit } 'plugin hash'
    $missingScene = Write-MarkerVariant $fixture 'marker-scene' { param($m) $m.scenes = @($m.scenes | Where-Object { $_ -ne 'Assets/Scenes/Boot.unity' }) }
    Assert-Throws { Assert-C04BuildMarker -MarkerPath $missingScene -ExpectedSourceHead $fixture.marker.sourceHead -ExpectedBuildCommit $fixture.marker.buildCommit } 'required scene'

    $fake = New-FixturePlayer -Root (New-TestDirectory 'marker-missing-file')
    Remove-Item -LiteralPath (Join-Path $fake.root 'Mahjong_Data/Managed/Mahjong.LocalHost.dll') -Force
    Assert-Throws { Assert-C04BuildMarker -MarkerPath $fake.markerPath -ExpectedSourceHead $fake.marker.sourceHead -ExpectedBuildCommit $fake.marker.buildCommit } 'plugin is missing'

    $unlisted = New-FixturePlayer -Root (New-TestDirectory 'marker-unlisted')
    Write-C04Utf8 (Join-Path $unlisted.root 'UNLISTED-RUNTIME.dll') 'unlisted'
    Assert-Throws { Assert-C04BuildMarker -MarkerPath $unlisted.markerPath -ExpectedSourceHead $unlisted.marker.sourceHead -ExpectedBuildCommit $unlisted.marker.buildCommit } 'unlisted file'

    $escape = New-FixturePlayer -Root (New-TestDirectory 'marker-escape')
    $escapeDir = Join-Path (Split-Path -Parent $escape.root) 'player-escape'
    [IO.Directory]::CreateDirectory($escapeDir) | Out-Null
    $outside = Join-Path $escapeDir 'outside.bin'
    Write-C04Utf8 $outside 'outside'
    $escapeMarker = $escape.marker | ConvertTo-Json -Depth 12 | ConvertFrom-Json
    [IO.File]::AppendAllText($escape.marker.packageManifest, (Get-C04Sha256File $outside) + '  ../player-escape/outside.bin' + "`n", (New-Object Text.UTF8Encoding($false)))
    $escapeMarker.packageManifestSha256 = Get-C04Sha256File $escape.marker.packageManifest
    $escapeMarker.packageFileCount = [int]$escapeMarker.packageFileCount + 1
    $escapeMarker.packageBytes = [long]$escapeMarker.packageBytes + (Get-Item -LiteralPath $outside).Length
    $escapeMarkerPath = Join-Path $escape.root 'escape-marker.json'
    Write-C04Json $escapeMarkerPath $escapeMarker
    Assert-Throws { Assert-C04BuildMarker -MarkerPath $escapeMarkerPath -ExpectedSourceHead $escape.marker.sourceHead -ExpectedBuildCommit $escape.marker.buildCommit } 'parent traversal'

    $nullField = Write-MarkerVariant $fixture 'marker-null-field' { param($m) $m.packageFileCount = $null }
    Assert-Throws { Assert-C04BuildMarker -MarkerPath $nullField -ExpectedSourceHead $fixture.marker.sourceHead -ExpectedBuildCommit $fixture.marker.buildCommit } 'required field packageFileCount'
}

Invoke-Case 'owned process drains large stdout/stderr and propagates native nonzero' {
    $dir = New-TestDirectory 'owned-process'
    $scriptPath = Join-Path $dir 'write-large.ps1'
    Write-TestScript $scriptPath '[Console]::Out.Write(("o" * 1048576)); [Console]::Error.Write(("e" * 1048576)); exit 7'
    $result = Invoke-C04OwnedProcess -Name 'large-output' -FileName 'powershell.exe' -Arguments @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $scriptPath) -WorkingDirectory $dir -TimeoutSeconds 30 -CloseGraceSeconds 5 -EvidenceDirectory $dir -HeartbeatSeconds 5
    Assert-Equal 1 @($result).Count 'large output return cardinality'
    Assert-Equal 7 $result.exitCode 'native exit'
    Assert-True (-not $result.timedOut) 'large output timed out'
    Assert-True (-not $result.aliveAfter) 'large output writer survived'
    Assert-True ((Get-Item -LiteralPath $result.stdoutPath).Length -ge 1048576) 'stdout was not fully drained'
    Assert-True ((Get-Item -LiteralPath $result.stderrPath).Length -ge 1048576) 'stderr was not fully drained'
    Assert-Throws { Assert-C04NativeSuccess $result 'large-output' } 'native exit 7'
}

Invoke-Case 'bounded timeout stops only the owned writer and leaves no residual process' {
    $dir = New-TestDirectory 'owned-timeout'
    $scriptPath = Join-Path $dir 'sleep.ps1'
    Write-TestScript $scriptPath 'Start-Sleep -Seconds 60'
    $result = Invoke-C04OwnedProcess -Name 'timeout-helper' -FileName 'powershell.exe' -Arguments @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $scriptPath) -WorkingDirectory $dir -TimeoutSeconds 1 -CloseGraceSeconds 2 -EvidenceDirectory $dir -HeartbeatSeconds 1
    Assert-Equal 1 @($result).Count 'timeout return cardinality'
    Assert-True $result.timedOut 'timeout was not recorded'
    Assert-True (-not $result.aliveAfter) 'timed-out writer is still alive'
    Assert-True ($result.elapsedSeconds -lt 20) 'timeout exceeded its bound'
    Assert-Throws { Assert-C04NativeSuccess $result 'timeout-helper' } 'timed out'
    $stillThere = $true
    try { $null = Get-Process -Id $result.pid -ErrorAction Stop } catch { $stillThere = $false }
    Assert-True (-not $stillThere) 'timed-out owned PID still exists'
}

Invoke-Case 'normal cancellation file is bounded and leaves no residual writer' {
    $dir = New-TestDirectory 'owned-cancel'
    $scriptPath = Join-Path $dir 'sleep.ps1'
    Write-TestScript $scriptPath 'Start-Sleep -Seconds 60'
    $cancel = Join-Path $dir 'cancel.flag'
    $job = Start-Job -ScriptBlock {
        param($path) Start-Sleep -Seconds 1; [IO.File]::WriteAllText($path, 'cancel')
    } -ArgumentList $cancel
    $result = Invoke-C04OwnedProcess -Name 'cancel-helper' -FileName 'powershell.exe' -Arguments @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $scriptPath) -WorkingDirectory $dir -TimeoutSeconds 20 -CancelFile $cancel -CloseGraceSeconds 2 -EvidenceDirectory $dir -HeartbeatSeconds 1
    Wait-Job $job -Timeout 10 | Out-Null
    Remove-Job $job -Force
    Assert-Equal 1 @($result).Count 'cancel return cardinality'
    Assert-True $result.cancelled 'cancel was not recorded'
    Assert-True (-not $result.aliveAfter) 'cancelled writer is still alive'
    Assert-Throws { Assert-C04NativeSuccess $result 'cancel-helper' } 'cancelled'
}

Invoke-Case 'inherited output writer is bounded and owned descendant is stopped' {
    $dir = New-TestDirectory 'owned-descendant'
    $helper = Join-Path $dir 'InheritedOutputTest.exe'
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
public class InheritedOutputTest {
  public static void Main(string[] args) {
    string exe = Assembly.GetExecutingAssembly().Location;
    if (args.Length > 0 && args[0] == "writer") {
      File.WriteAllText(args[1], Process.GetCurrentProcess().Id.ToString());
      Console.WriteLine("WRITER");
      Thread.Sleep(4000);
      return;
    }
    if (args.Length > 0 && args[0] == "middle") {
      var writer = Process.Start(new ProcessStartInfo(exe, "writer \"" + args[1] + "\"") {
        UseShellExecute = false, CreateNoWindow = true
      });
      writer.WaitForExit();
      return;
    }
    var middle = Process.Start(new ProcessStartInfo(exe, "middle \"" + args[1] + "\"") {
      UseShellExecute = false, CreateNoWindow = true
    });
    middle.WaitForExit();
  }
}
'@ -OutputAssembly $helper -OutputType ConsoleApplication
    $pidFile = Join-Path $dir 'writer.pid'
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $result = Invoke-C04OwnedProcess -Name 'descendant-output' -FileName $helper -Arguments @('root', $pidFile) -WorkingDirectory $dir -TimeoutSeconds 1 -CloseGraceSeconds 1 -EvidenceDirectory $dir -HeartbeatSeconds 1
    $sw.Stop()
    Assert-Equal 1 @($result).Count 'descendant return cardinality'
    Assert-True ($sw.Elapsed.TotalSeconds -lt 12) 'descendant call exceeded its bound'
    Assert-True (Test-Path -LiteralPath $pidFile) 'writer pid was not recorded'
    $childAlive = $true
    try { $null = Get-Process -Id ([int](Get-Content $pidFile)) -ErrorAction Stop } catch { $childAlive = $false }
    Assert-True (-not $childAlive) 'owned descendant is still alive'
}

Invoke-Case 'post-start failure registration is cleaned and returned as failure' {
    $dir = New-TestDirectory 'owned-post-start-failure'
    $registry = New-Object Collections.Generic.List[object]
    Assert-Throws {
        Invoke-C04OwnedProcess -Name 'post-start-failure' -FileName 'powershell.exe' -Arguments @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command', 'Start-Sleep -Seconds 60') -WorkingDirectory $dir -TimeoutSeconds 5 -CloseGraceSeconds 2 -EvidenceDirectory $dir -HeartbeatSeconds 1 -OwnedRegistry $registry -InjectPostStartFailure
    } 'INJECTED_POST_START_FAILURE'
    Assert-Equal 1 $registry.Count 'post-start registry count'
    Assert-True (-not $registry[0].aliveAfter) 'post-start registered process was not stopped'
    $stillThere = $true
    try { $null = Get-Process -Id $registry[0].pid -ErrorAction Stop } catch { $stillThere = $false }
    Assert-True (-not $stillThere) 'post-start process still exists'
}

Invoke-Case 'stop identity mismatch is refused without terminating the process' {
    $current = Get-Process -Id $PID
    Assert-Throws {
        Stop-C04OwnedProcess -Process $current -OwnedPid $PID -StartTicks 1 -GraceSeconds 1
    } 'start identity changed'
    Assert-True (-not $current.HasExited) 'identity mismatch terminated the current process'
}

Invoke-Case 'unresolved record without captured identity is not cleared' {
    $record = [pscustomobject]@{
        pid = 999999
        startTicks = 1L
        aliveAfter = $true
        drainUnresolved = $true
        capturedDescendants = @()
        cleanupError = $null
        descendantsStopped = 0
    }
    Stop-C04OwnedRecord $record 1
    Assert-True $record.aliveAfter 'unresolved record was cleared'
    Assert-True ($record.cleanupError -like '*no captured descendant identity*') 'unresolved record did not preserve UNKNOWN reason'
}

Invoke-Case 'test player package identity is bound to the same executable/plugin closure' {
    $dir = New-TestDirectory 'test-player-package'
    $staging = Join-Path $dir 'staging'
    $player = Join-Path $staging 'Temp/UnityTempFile-fixture/PlayerWithTests'
    $managed = Join-Path $player 'PlayerWithTests_Data/Managed'
    [IO.Directory]::CreateDirectory($managed) | Out-Null
    Write-C04Utf8 (Join-Path $player 'PlayerWithTests.exe') 'test-player'
    foreach ($name in $script:C04RequiredPlugins) { Write-C04Utf8 (Join-Path $managed $name) ('plugin-' + $name) }
    $identity = Get-C04TestPlayerPackage -StagingProject $staging -RunRoot $dir -SourceHead ('a' * 40)
    Assert-True (Test-Path -LiteralPath $identity.packageManifest) 'test player manifest missing'
    Assert-Equal 9 $identity.packageFileCount 'test player package count'
    Assert-True ($identity.packageBytes -gt 0) 'test player package bytes'
    Remove-Item -LiteralPath (Join-Path $managed 'Mahjong.LocalHost.dll') -Force
    Assert-Throws { Get-C04TestPlayerPackage -StagingProject $staging -RunRoot (New-TestDirectory 'test-player-missing') -SourceHead ('a' * 40) } 'missing required managed plugin'
}

Invoke-Case 'required raw scenes are all present and any missing scene is rejected' {
    $dir = New-TestDirectory 'raw-scenes'
    $roleSwitch = Join-Path $dir 'role-switch-raw'
    [IO.Directory]::CreateDirectory($roleSwitch) | Out-Null
    Write-C04Utf8 (Join-Path $roleSwitch 'raw-frames.json') '{"schemaVersion":2,"frames":[{}]}'
    foreach ($name in @('four-owner-raw', 'closed-binding-raw', 'recovery-raw')) {
        $sub = Join-Path $dir $name
        [IO.Directory]::CreateDirectory($sub) | Out-Null
        Write-C04Utf8 (Join-Path $sub 'raw-frames.json') '{"schemaVersion":2,"frames":[{}]}'
    }
    $scenes = @(Get-C04ValidatedRawScenes -PrivacyEvidence $dir)
    Assert-Equal 4 $scenes.Count 'required raw scene count'
    $rolePath = Join-Path $roleSwitch 'raw-frames.json'
    Write-C04Utf8 $rolePath '{"schemaVersion":1,"frames":[{}]}'
    Assert-Throws { Get-C04ValidatedRawScenes -PrivacyEvidence $dir } 'schema is empty or unsupported'
    Write-C04Utf8 $rolePath '{"schemaVersion":2,"frames":[{}]}'
    Remove-Item -LiteralPath (Join-Path $dir 'closed-binding-raw/raw-frames.json') -Force
    Assert-Throws { Get-C04ValidatedRawScenes -PrivacyEvidence $dir } "Required raw scene 'closed-binding'"
}

Invoke-Case 'mutex wait timeout does not acquire or release another process lock' {
    $name = 'Local\C04RunnerTests-' + [guid]::NewGuid().ToString('N')
    $dir = New-TestDirectory 'mutex'
    $ready = Join-Path $dir 'ready.flag'
    $holderScript = Join-Path $dir 'holder.ps1'
    $holderBody = '$m = New-Object Threading.Mutex($false, ''' + $name + '''); $null = $m.WaitOne(); [IO.File]::WriteAllText(''' + $ready + ''', ''ready''); Start-Sleep -Seconds 8'
    Write-TestScript $holderScript $holderBody
    $holder = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $holderScript) -PassThru -WindowStyle Hidden
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(5)
        while (-not (Test-Path -LiteralPath $ready) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 50 }
        Assert-True (Test-Path -LiteralPath $ready) 'mutex holder did not become ready'
        Assert-Throws { Acquire-C04Mutex -Name $name -TimeoutSeconds 1 -HeartbeatSeconds 1 } 'mutex wait exceeded'
    } finally {
        if (-not $holder.HasExited) { $holder.Kill(); $null = $holder.WaitForExit(5000) }
        $holder.Dispose()
    }
}

Invoke-Case 'evidence seal is stable and refuses a second seal' {
    $dir = New-TestDirectory 'seal'
    Write-C04Utf8 (Join-Path $dir 'raw.txt') 'stable'
    $summary = [ordered]@{ schemaVersion = 1; status = 'PASS'; runRoot = $dir; finishedUtc = [DateTimeOffset]::UtcNow.ToString('o') }
    $first = Write-C04SealedEvidence -RunRoot $dir -Summary $summary
    Assert-True (Test-Path -LiteralPath (Join-Path $dir 'summary.json')) 'summary missing'
    Assert-True (Test-Path -LiteralPath (Join-Path $dir 'manifest.sha256')) 'manifest missing'
    Assert-True (Test-Path -LiteralPath (Join-Path $dir 'seal.json')) 'seal missing'
    $hashBefore = Get-C04Sha256File (Join-Path $dir 'manifest.sha256')
    Start-Sleep -Milliseconds 200
    $hashAfter = Get-C04Sha256File (Join-Path $dir 'manifest.sha256')
    Assert-Equal $hashBefore $hashAfter 'manifest changed after seal'
    Assert-Throws { Write-C04SealedEvidence -RunRoot $dir -Summary $summary } 'already sealed'
}

Invoke-Case 'actual Main seal failure is native non-zero and not a PASS' {
    $dir = New-TestDirectory 'main-seal-failure'
    $probe = Join-Path $dir 'main-seal-failure.ps1'
    $runRoot = Join-Path $dir ([guid]::NewGuid().ToString('N'))
    $body = @'
param([string]$Repo, [string]$RunRoot)
$ErrorActionPreference = 'Stop'
Set-Location $Repo
$ProbeRunRoot = $RunRoot
. (Join-Path $Repo 'tools/unity/run-p45-c04-privacy-matrix.ps1') -LoadFunctionsOnly
$RunRoot = $ProbeRunRoot
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $Repo 'tools/unity/run-p45-c04-privacy-matrix.ps1'), [ref]$tokens, [ref]$errors)
foreach ($name in @('Log', 'Invoke-C04RunnerMain')) {
  $fn = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true)
  $functionText = $fn.Extent.Text.Replace('$PSScriptRoot', ("'" + (Join-Path $Repo 'tools/unity') + "'"))
  Invoke-Expression $functionText
}
function Write-C04SealedEvidence { param([string]$RunRoot, $Summary) throw 'INJECTED_MAIN_SEAL_FAILURE' }
$PreflightOnly = $true
$ExpectedSourceHead = (& git rev-parse HEAD).Trim()
$script:LogPath = ''; $script:OwnedProcesses = New-Object Collections.Generic.List[object]
$script:MutexHandle = $null; $script:StageDrive = $null; $script:OldEnvironment = @{}
try {
  Invoke-C04RunnerMain
  Write-Output 'ACTUAL_MAIN_RETURNED_SUCCESS'
  exit 0
} catch {
  Write-Output ('MAIN_THREW=' + $_.Exception.Message)
  exit 1
}
'@
    Write-C04Utf8 $probe $body
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $probe, '-Repo', $repo, '-RunRoot', $runRoot)
    $result = Invoke-C04OwnedProcess -Name 'main-seal-probe' -FileName 'powershell.exe' -Arguments $arguments -WorkingDirectory $repo -TimeoutSeconds 60 -CloseGraceSeconds 5 -EvidenceDirectory $dir -HeartbeatSeconds 5
    Assert-Equal 1 @($result).Count 'actual Main probe cardinality'
    Assert-True (-not $result.timedOut) 'actual Main probe timed out unexpectedly'
    $stdout = if (Test-Path -LiteralPath $result.stdoutPath) { [IO.File]::ReadAllText($result.stdoutPath) } else { '' }
    $stderr = if (Test-Path -LiteralPath $result.stderrPath) { [IO.File]::ReadAllText($result.stderrPath) } else { '' }
    Assert-True ($result.exitCode -ne 0) 'actual Main seal failure returned native 0'
    Assert-True ($stdout -notmatch 'ACTUAL_MAIN_RETURNED_SUCCESS') 'actual Main returned success after seal failure'
    Assert-True (($stdout + $stderr) -match 'SEAL_FAILED|INJECTED_MAIN_SEAL_FAILURE|Runner finalization failed') 'actual Main did not report the seal failure'
}

Invoke-Case 'ordinary Windows app matrix uses the built Mahjong.exe and exact seven names' {
    $text = [IO.File]::ReadAllText($runner,[Text.Encoding]::UTF8)
    Assert-True ($text.Contains('[switch]$WindowsAppMatrix') -and $text.Contains('Invoke-C04WindowsAppMatrix')) 'ordinary app mode is missing'
    foreach ($testName in $script:C04ExpectedTests) { Assert-True ($text.Contains($testName)) "ordinary app mode lost fixed case $testName" }
    $start = $text.IndexOf('function Invoke-C04WindowsAppMatrix', [StringComparison]::Ordinal)
    $end = $text.IndexOf('function Invoke-C04RunnerMain', $start, [StringComparison]::Ordinal)
    Assert-True ($start -ge 0 -and $end -gt $start) 'ordinary app matrix function boundary is missing'
    $body = $text.Substring($start,$end-$start)
    Assert-True ($body.Contains('$PlayerPath') -and $body.Contains('Start-C04Owned') -and $body.Contains('windows-app-package-before.sha256') -and $body.Contains('windows-app-package-after.sha256')) 'matrix is not bound to one built application/package manifest'
    Assert-True (-not $body.Contains('-testPlatform') -and -not $body.Contains('test-player')) 'ordinary app path uses the Test Player route'
}

Invoke-Case 'ordinary Windows app P2 launches separate processes and reconciles original receipt' {
    $text = [IO.File]::ReadAllText($runner,[Text.Encoding]::UTF8)
    foreach ($required in @('windows-app-p2-prepare','windows-app-p2-recover','-c04-recovery-mode','Prepare','Recover',
        'exactOriginalIdentity','foundFromRealLookup','zeroDeltaAfterReconcile','noOriginalCommandResend',
        'oldScopeQueryCount','prepareDoc.process.pid','recoverDoc.process.pid','canonicalDigest')) {
        Assert-True ($text.Contains($required)) "P2 runner contract is missing $required"
    }
}

Invoke-Case 'semantic Windows Player evidence accepts all seven fixture-only producer schemas' {
    foreach ($testName in $script:C04ExpectedTests) {
        $caseName = $testName.Substring($testName.LastIndexOf('.') + 1)
        $caseRoot = New-CaseFixtureRoot $caseName
        New-CaseEvidenceFixture $caseName $caseRoot
        $accepted = Assert-C04PlayerCaseEvidence -CaseName $caseName -CaseRoot $caseRoot
        Assert-Equal $caseName $accepted.case "semantic evidence case $caseName"
        Assert-True ($accepted.artifacts.Count -gt 0) "semantic evidence did not return required case artifacts for $caseName"
    }
}

Invoke-Case 'no-change Ready evidence rejects identity, WELCOME, epoch, and terminal-state drift' {
    $caseName = 'RealRoom_ReleasedSeatAiAdvancesAfterPlayerCloseWithSpectatorOnly'
    foreach ($mutation in @(
        @{ key='recoveryBranchTaken'; value=$false },
        @{ key='newSessionId'; value='foreign-session' },
        @{ key='newRoomId'; value='foreign-room' },
        @{ key='newScopeId'; value='fixture-old-scope' },
        @{ key='bindingEpoch'; value=3 },
        @{ key='welcomeFrameCountAfter'; value=2 },
        @{ key='newWelcomeAccountId'; value='foreign-account' },
        @{ key='newWelcomeScopeId'; value='unissued-scope' },
        @{ key='newWelcomeRole'; value=2 },
        @{ key='newWelcomeSeat'; value=1 },
        @{ key='resumeTicketSha256After'; value=('c'*64) },
        @{ key='controlEpochAfter'; value=3 },
        @{ key='receiptReason'; value='OTHER_REASON' },
        @{ key='sessionState'; value='Reconnecting' },
        @{ key='commandFrames'; value=2 },
        @{ key='pendingCommands'; value=1 }
    )) {
        $caseRoot = New-CaseFixtureRoot $caseName
        New-CaseEvidenceFixture $caseName $caseRoot
        $path = Join-Path $caseRoot 'control-session-ready-positive.json'
        $evidence = [IO.File]::ReadAllText($path,[Text.Encoding]::UTF8) | ConvertFrom-Json
        $evidence.($mutation.key) = $mutation.value
        Write-C04Json $path $evidence
        Assert-Throws { Assert-C04PlayerCaseEvidence -CaseName $caseName -CaseRoot $caseRoot } 'Released-control no-regression Ready positive control'
    }
}

Invoke-Case 'recovery receipt evidence rejects status or original commit metadata drift' {
    $caseName = 'RealRoom_ColdReopenReconcilesOriginalRequestWithoutDuplicate'
    foreach ($mutation in @(
        @{ key='receiptStatus'; value='Rejected' },
        @{ key='receiptCommandVersion'; value='13' },
        @{ key='receiptEventSeq'; value='8' },
        @{ key='commandFrames'; value=1 },
        @{ key='receiptQueryFrames'; value=0 },
        @{ key='fromReceiptLookup'; value=$false },
        @{ key='zeroDuplicateStateDelta'; value=$false }
    )) {
        $caseRoot = New-CaseFixtureRoot $caseName
        New-CaseEvidenceFixture $caseName $caseRoot
        $path = Join-Path $caseRoot 'recovery-query/recovery-query.json'
        $evidence = [IO.File]::ReadAllText($path,[Text.Encoding]::UTF8) | ConvertFrom-Json
        $evidence.($mutation.key) = $mutation.value
        Write-C04Json $path $evidence
        Assert-Throws { Assert-C04PlayerCaseEvidence -CaseName $caseName -CaseRoot $caseRoot } 'Original-request recovery evidence failed'
    }
}

Invoke-Case 'semantic Windows Player evidence rejects every missing required artifact' {
    foreach ($testName in $script:C04ExpectedTests) {
        $caseName = $testName.Substring($testName.LastIndexOf('.') + 1)
        $caseRoot = New-CaseFixtureRoot $caseName
        New-CaseEvidenceFixture $caseName $caseRoot
        $required = @(Get-C04StableFiles -Root $caseRoot | Where-Object { $_.Name -notin @('summary.json','player-case.jsonl') })
        foreach ($artifact in $required) {
            Remove-Item -LiteralPath $artifact.FullName
            Assert-Throws { Assert-C04PlayerCaseEvidence -CaseName $caseName -CaseRoot $caseRoot } 'Player case evidence is incomplete'
            New-CaseEvidenceFixture $caseName $caseRoot
        }
    }
}

Invoke-Case 'deadline evidence rejects wrong identity, missing decision, epoch, deadline, or progress' {
    $caseName = 'RealRoom_OpenDecisionDeadlineContinuesAfterPlayerClose'
    $mutations = @(
        @{ name='wrong-case'; key='case'; value='RealRoom_ReleasedSeatAiAdvancesAfterPlayerCloseWithSpectatorOnly' },
        @{ name='empty-decision'; key='decisionId'; value='' },
        @{ name='wrong-epoch'; key='controlEpoch'; value=0 },
        @{ name='wrong-clock-seat'; key='clockDecisionSeat'; value=1 },
        @{ name='deadline-mismatch'; key='authoritativeDeadlineServerTimeMs'; value=15001 },
        @{ name='baseline-version-mismatch'; key='spectatorBaselineVersion'; value=1 },
        @{ name='no-version-progress'; key='afterVersion'; value=0 },
        @{ name='no-cursor-stream-progress'; key='afterCursorStreamVersion'; value=0 },
        @{ name='no-cursor-projection-progress'; key='afterCursorProjectionSeq'; value=0 },
        @{ name='player-still-bound'; key='playerBindingClosed'; value=$false }
    )
    foreach ($mutation in $mutations) {
        $caseRoot = New-CaseFixtureRoot $caseName
        New-CaseEvidenceFixture $caseName $caseRoot
        $path = Join-Path $caseRoot 'deadline-evidence.json'
        $e = [IO.File]::ReadAllText($path) | ConvertFrom-Json
        $e.($mutation.key) = $mutation.value
        Write-C04Json $path $e
        Assert-Throws { Assert-C04PlayerCaseEvidence -CaseName $caseName -CaseRoot $caseRoot } 'Open-deadline evidence failed'
    }
}

Invoke-Case 'deadline floor numeric fields must be present even when the real initial floor is zero' {
    $caseName = 'RealRoom_OpenDecisionDeadlineContinuesAfterPlayerClose'
    $caseRoot = New-CaseFixtureRoot $caseName
    New-CaseEvidenceFixture $caseName $caseRoot
    $path = Join-Path $caseRoot 'deadline-evidence.json'
    $e = [IO.File]::ReadAllText($path) | ConvertFrom-Json
    $e.PSObject.Properties.Remove('releaseVersion')
    Write-C04Json $path $e
    Assert-Throws { Assert-C04PlayerCaseEvidence -CaseName $caseName -CaseRoot $caseRoot } 'Open-deadline evidence failed'
}

Invoke-Case 'released-control stale-command evidence requires a live ready bound player session' {
    $caseName = 'RealRoom_ReleasedSeatAiAdvancesAfterPlayerCloseWithSpectatorOnly'
    foreach ($mutation in @(
        @{ file='control-session-before-stale-submit.json'; key='sessionState'; value='Closed' },
        @{ file='control-session-before-stale-submit.json'; key='bindingPresent'; value=$false },
        @{ file='control-session-before-stale-submit.json'; key='scopeId'; value='' },
        @{ file='control-session-before-stale-submit.json'; key='bindingEpoch'; value=0 },
        @{ file='control-session-before-stale-submit.json'; key='seat'; value=1 },
        @{ file='released-control/released-control.json'; key='claimReceiptStatus'; value='Rejected' },
        @{ file='released-control/released-control.json'; key='claimReceiptReason'; value='DECISION_MISMATCH' },
        @{ file='released-control/released-control.json'; key='staleReceiptStatus'; value='Recorded' },
        @{ file='released-control/released-control.json'; key='staleReason'; value='SAME_WRONG_REASON' },
        @{ file='released-control/released-control.json'; key='expectedStaleReason'; value='SAME_WRONG_REASON' },
        @{ file='released-control/released-control.json'; key='capturedDecisionEpoch'; value=0 },
        @{ file='released-control/released-control.json'; key='claimEpoch'; value=1 },
        @{ file='released-control/released-control.json'; key='staleNoCommitMetadata'; value=$false },
        @{ file='released-control/released-control.json'; key='staleCommandFrames'; value=2 },
        @{ file='released-control/released-control.json'; key='stalePendingAfter'; value=1 },
        @{ file='released-control/released-control.json'; key='staleRequestId'; value='fixture-claim' },
        @{ file='released-control/released-control.json'; key='staleDecisionId'; value='different-decision' },
        @{ file='released-control/released-control.json'; key='staleScopeId'; value='different-scope' },
        @{ file='released-control/released-control.json'; key='staleRound'; value='different-round' },
        @{ file='control-session-before-stale-submit.json'; key='snapshotControlEpoch'; value=1 },
        @{ file='control-session-reconnect.json'; key='newSessionId'; value='different-session' },
        @{ file='control-session-reconnect.json'; key='newWelcomeScopeId'; value='not-issued-by-welcome' },
        @{ file='control-session-reconnect.json'; key='newWelcomeSessionId'; value='different-account-session' },
        @{ file='control-session-reconnect.json'; key='newWelcomeRoomId'; value='different-room' },
        @{ file='control-session-reconnect.json'; key='resumeTicketSha256After'; value=('b'*64) },
        @{ file='control-session-reconnect.json'; key='newWelcomeRole'; value=2 },
        @{ file='control-session-reconnect.json'; key='newWelcomeSeat'; value=1 },
        @{ file='control-session-reconnect.json'; key='welcomeFrameCountAfter'; value=1 },
        @{ file='control-session-reconnect.json'; key='newBindingEpoch'; value=3 },
        @{ file='control-session-reconnect.json'; key='recoveryBranchTaken'; value=$false },
        @{ file='control-session-reconnect.json'; key='newState'; value='Reconnecting' },
        @{ file='control-session-before-stale-submit.json'; key='capturedStaleDecisionId'; value='different-original-decision' },
        @{ file='control-session-reconnect.json'; key='newScopeId'; value='fixture-old-scope' },
        @{ file='control-session-reconnect.json'; key='newScopeId'; value='fixture-unrelated-scope' },
        @{ file='control-session-reconnect.json'; key='newBindingEpoch'; value=5 },
        @{ file='control-session-ready-positive.json'; key='sessionState'; value='Reconnecting' },
        @{ file='control-session-ready-positive.json'; key='controlEpochAfter'; value=3 },
        @{ file='control-session-ready-positive.json'; key='receiptReason'; value='DECISION_MISMATCH' },
        @{ file='control-session-ready-positive.json'; key='pendingCommands'; value=1 }
    )) {
        $caseRoot = New-CaseFixtureRoot $caseName
        New-CaseEvidenceFixture $caseName $caseRoot
        $path = Join-Path $caseRoot $mutation.file
        $e = [IO.File]::ReadAllText($path) | ConvertFrom-Json
        $e.($mutation.key) = $mutation.value
        Write-C04Json $path $e
        Assert-Throws { Assert-C04PlayerCaseEvidence -CaseName $caseName -CaseRoot $caseRoot } 'Released-control'
    }
}

Invoke-Case 'runner test root rejects over-budget paths before fixture creation' {
    $longRoot = Join-Path $script:TestRoot (('x' * 220))
    Assert-Throws { Assert-TestRootPathBudget $longRoot } 'portable Windows path budget'
}

Invoke-Case 'a meaningless third file cannot substitute for deadline evidence' {
    $caseName = 'RealRoom_OpenDecisionDeadlineContinuesAfterPlayerClose'
    $caseRoot = New-CaseFixtureRoot $caseName
    New-CaseEvidenceFixture $caseName $caseRoot
    Remove-Item -LiteralPath (Join-Path $caseRoot 'deadline-evidence.json')
    Write-C04Utf8 (Join-Path $caseRoot 'meaningless-third-file.txt') 'FIXTURE ONLY'
    Assert-Throws { Assert-C04PlayerCaseEvidence -CaseName $caseName -CaseRoot $caseRoot } 'Player case evidence is incomplete'
}

Invoke-Case 'case summary and JSONL event identity are required and nonempty' {
    $caseName = 'RealRoom_OpenDecisionDeadlineContinuesAfterPlayerClose'
    $caseRoot = New-CaseFixtureRoot $caseName
    New-CaseEvidenceFixture $caseName $caseRoot
    Write-C04Utf8 (Join-Path $caseRoot 'player-case.jsonl') "`n"
    Assert-Throws { Assert-C04PlayerCaseEvidence -CaseName $caseName -CaseRoot $caseRoot } 'Player case terminal log'
}

Invoke-Case 'Unity compile-only mode imports and compiles without claiming a Player build' {
    $text = [IO.File]::ReadAllText($runner,[Text.Encoding]::UTF8)
    Assert-True ($text.Contains('[switch]$CompileOnly') -and $text.Contains("'unity-compile-only'")) 'compile-only runner mode is missing'
    Assert-True ($text.Contains('unity-project-compile-only') -and $text.Contains('Mahjong.Unity.App.dll') -and $text.Contains('Script compilation failed')) 'compile-only mode does not require the compiled app assembly and fail on compiler diagnostics'
    Assert-True ($text.Contains('ReuseLibraryCacheFrom requires a Unity execution mode') -and -not $text.Contains('ReuseLibraryCacheFrom is permitted only for CompileOnly.')) 'sealed Library reuse is incorrectly restricted to compile-only runs'
}

Invoke-Case 'reused Unity Library is an excluded sealed cache and fresh source is restaged' {
    $text = [IO.File]::ReadAllText($runner,[Text.Encoding]::UTF8)
    Assert-True ($text.Contains('[string]$ReuseLibraryCacheFrom') -and $text.Contains('excludedFromPriorSeal = $true')) 'cache reuse is not recorded as an excluded cache'
    Assert-True ($text.Contains('Reusable Library is not a sealed, excluded cache') -and $text.Contains('Move-Item -LiteralPath $sourceResolved -Destination $destinationLibrary')) 'cache reuse lacks prior-seal/path guards'
    Assert-True ($text.Contains('$copied = Copy-C04UnityInputs -Repo $repo -Destination $staging')) 'fresh source closure is not copied before cache reuse'
}

$summary = [ordered]@{
    schemaVersion = 1
    kind = 'c04-privacy-runner-tests'
    result = $(if ($script:Failures.Count -eq 0) { 'PASS' } else { 'FAIL' })
    tests = $script:Tests
    failures = $script:Failures.Count
    cases = $script:Results
    runRoot = $script:TestRoot
    finishedUtc = [DateTimeOffset]::UtcNow.ToString('o')
}
Write-C04Json (Join-Path $script:TestRoot 'runner-tests.json') $summary
if ($script:Failures.Count -ne 0) {
    $script:Failures | ForEach-Object { Write-Host "FAILURE: $_" }
    Write-Host "C04_PRIVACY_RUNNER_TESTS_FAIL tests=$($script:Tests) failures=$($script:Failures.Count)"
    exit 1
}
Write-Host "C04_PRIVACY_RUNNER_TESTS_PASS tests=$($script:Tests) root=$script:TestRoot"
exit 0
