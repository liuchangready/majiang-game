#requires -Version 5.1
<#
.SYNOPSIS
    Independent verifier for one C04 recovery-request Player result.

.DESCRIPTION
    Reads result.json, expected.txt, input.json and the raw frame files written by
    C04RecoveryReconciliationProbe. It rejects a missing/renamed identity, a
    duplicate command, a receipt not obtained from the real lookup, any delta after
    reconciliation, a non-zero send on FRESH_START/REJECTED, or a frame whose SHA256
    does not match its file. It never writes into the evidence tree except its own
    verification.json.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ResultPath,
    [Parameter(Mandatory = $true)][string]$ExpectedPath,
    [Parameter(Mandatory = $true)][string]$InputPath,
    [Parameter(Mandatory = $true)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$errors = New-Object System.Collections.Generic.List[string]
$checks = [ordered]@{}

function Get-Prop {
    param($Object, [string]$Name, $Default = $null)
    if ($null -eq $Object) { return $Default }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $Default }
    return $property.Value
}

function Assert-Check {
    param([string]$Name, [bool]$Condition, [string]$Message)
    $checks[$Name] = $Condition
    if (-not $Condition) { $errors.Add($Message) | Out-Null }
}

function Same-Text {
    param($Left, $Right)
    return [string]::Equals([string]$Left, [string]$Right, [StringComparison]::OrdinalIgnoreCase)
}

function Same-Ordinal {
    param($Left, $Right)
    return [string]::Equals([string]$Left, [string]$Right, [StringComparison]::Ordinal)
}

function Read-Expected {
    param([string]$Path)
    if (-not [IO.File]::Exists($Path)) { throw "expected.txt is missing: $Path" }
    $map = @{}
    foreach ($line in [IO.File]::ReadAllLines($Path)) {
        $index = $line.IndexOf('=')
        if ($index -gt 0) { $map[$line.Substring(0, $index)] = $line.Substring($index + 1) }
    }
    return $map
}

try {
    $result = [IO.File]::ReadAllText($ResultPath) | ConvertFrom-Json
    $input = [IO.File]::ReadAllText($InputPath) | ConvertFrom-Json
    $expected = Read-Expected -Path $ExpectedPath
    $resultRoot = Split-Path -Parent ([IO.Path]::GetFullPath($ResultPath))
    $caseId = [string](Get-Prop $input 'caseId')
    $fault = [string](Get-Prop $input 'fault')
    $outcome = [string](Get-Prop $input 'expectedOutcome')
    $playerSha = [string](Get-Prop $input 'playerSha256')
    $packageSha = [string](Get-Prop $input 'packageSha256')
    $sourceHead = [string](Get-Prop $input 'sourceHead')
    $buildCommit = [string](Get-Prop $input 'buildCommit')

    Assert-Check 'schema' ([int](Get-Prop $result 'schemaVersion') -eq 1) 'result schemaVersion is not 1'
    Assert-Check 'kind' ([string](Get-Prop $result 'kind') -eq 'c04-recovery-result') 'result kind is not c04-recovery-result'
    Assert-Check 'case' (Same-Ordinal (Get-Prop $result 'caseId') $caseId) 'result caseId differs from input'
    Assert-Check 'fault' (Same-Ordinal (Get-Prop $result 'fault') $fault) 'result fault differs from input'
    Assert-Check 'expectedOutcome' (Same-Ordinal (Get-Prop $result 'expectedOutcome') $outcome) 'result expectedOutcome differs from input'
    Assert-Check 'player' (Same-Text (Get-Prop (Get-Prop $result 'build') 'playerSha256') $playerSha) 'result player SHA256 differs from input'
    Assert-Check 'package' (Same-Text (Get-Prop (Get-Prop $result 'build') 'packageSha256') $packageSha) 'result package SHA256 differs from input'
    Assert-Check 'source' (Same-Text (Get-Prop (Get-Prop $result 'build') 'sourceHead') $sourceHead) 'result sourceHead differs from input'
    Assert-Check 'commit' (Same-Text (Get-Prop (Get-Prop $result 'build') 'buildCommit') $buildCommit) 'result buildCommit differs from input'
    Assert-Check 'process' ([string]::IsNullOrWhiteSpace([string](Get-Prop (Get-Prop $result 'process') 'path')) -eq $false) 'result has no process path'
    Assert-Check 'requestId' (Same-Ordinal (Get-Prop (Get-Prop $result 'request') 'requestId') $expected['requestId']) 'result requestId differs from the prepared canonical request'
    Assert-Check 'scopeId' (Same-Ordinal (Get-Prop (Get-Prop $result 'request') 'scopeId') $expected['scopeId']) 'result scopeId differs from the prepared canonical request'
    Assert-Check 'accountId' (Same-Ordinal (Get-Prop (Get-Prop $result 'request') 'accountId') $expected['accountId']) 'result accountId differs from the prepared canonical request'
    Assert-Check 'payloadSha256' (Same-Text (Get-Prop (Get-Prop $result 'request') 'payloadSha256') $expected['payloadSha256']) 'result payload SHA256 differs from the prepared canonical request'
    Assert-Check 'canonicalDigest' (Same-Text (Get-Prop $result 'canonicalDigest') $expected['canonicalDigest']) 'result canonical digest differs from the prepared canonical request'

    $frames = @(Get-Prop $result 'frames' @())
    $frameErrors = 0
    foreach ($frame in $frames) {
        $relative = [string](Get-Prop $frame 'file')
        if ([string]::IsNullOrWhiteSpace($relative)) { $frameErrors++; continue }
        $path = Join-Path $resultRoot $relative
        if (-not [IO.File]::Exists($path) -and
            -not $relative.StartsWith('frames/', [StringComparison]::OrdinalIgnoreCase)) {
            $path = Join-Path (Join-Path $resultRoot 'frames') $relative
        }
        if (-not [IO.File]::Exists($path)) { $frameErrors++; continue }
        $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if (-not (Same-Text $actual (Get-Prop $frame 'sha256'))) { $frameErrors++ }
    }
    Assert-Check 'frameFiles' ($frameErrors -eq 0) ('frame files missing or SHA256 mismatched: ' + $frameErrors)

    $oldFrames = @($frames | Where-Object {
        (Same-Ordinal (Get-Prop $_ 'requestId') $expected['requestId']) -or
        (Same-Ordinal (Get-Prop $_ 'scopeId') $expected['scopeId'])
    })
    $oldCommands = @($oldFrames | Where-Object { [string](Get-Prop $_ 'type') -eq 'Command' })
    $oldQueries = @($oldFrames | Where-Object { [string](Get-Prop $_ 'type') -eq 'ReceiptQuery' })

    if ($outcome -eq 'RECOVERED' -or $outcome -eq 'RECOVERED_FROM_BACKUP') {
        Assert-Check 'actualOutcome' (Same-Ordinal (Get-Prop $result 'actualOutcome') $outcome) ('actual outcome is ' + (Get-Prop $result 'actualOutcome'))
        $state = Get-Prop $result 'state'
        $resolution = Get-Prop $result 'resolution'
        Assert-Check 'appliedReceipt' ((Same-Ordinal (Get-Prop (Get-Prop $result 'request') 'status') 'APPLIED') -and
            (Same-Ordinal (Get-Prop $resolution 'receiptStatus') 'Applied')) 'the recovered request is not APPLIED'
        Assert-Check 'fromLookup' ([bool](Get-Prop $resolution 'fromLookup')) 'the resolution did not come from the real receipt lookup'
        Assert-Check 'resolutionDigest' (Same-Text (Get-Prop $resolution 'digest') $expected['canonicalDigest']) 'resolution digest differs'
        Assert-Check 'resolutionRequest' (Same-Ordinal (Get-Prop $resolution 'requestId') $expected['requestId']) 'resolution request id differs'
        Assert-Check 'commandVersion' (Same-Ordinal (Get-Prop $state 'stateVersion') $expected['commandVersion']) 'stateVersion is not commandVersion'
        Assert-Check 'eventSeq' (Same-Ordinal (Get-Prop $state 'eventSeq') $expected['eventSeq']) 'eventSeq differs from the original Applied receipt'
        Assert-Check 'hand' ([int](Get-Prop $state 'handCount') -eq ([int]$expected['preHandCount'] - 1)) 'hand count did not decrease by exactly one'
        Assert-Check 'river' ([int](Get-Prop $state 'riverCount') -eq ([int]$expected['preRiverCount'] + 1)) 'river count did not increase by exactly one'
        Assert-Check 'tileInRiver' ([bool](Get-Prop $state 'tileInRiver')) 'the original tile is not in the river'
        Assert-Check 'discardEvent' ([int](Get-Prop $state 'discardEvents') -eq 1) 'there is not exactly one TileDiscarded event'
        Assert-Check 'ledger' (Same-Text (Get-Prop $state 'ledgerSha256') $expected['ledgerSha256']) 'ledger digest changed'
        Assert-Check 'physical136' (([int](Get-Prop $state 'physicalCount') -eq 136) -and ([int](Get-Prop $state 'physicalUnique') -eq 136)) 'physical entities are not 136/136 unique'
        Assert-Check 'zeroDelta' ([bool](Get-Prop (Get-Prop $result 'checks') 'zeroDeltaAfterReconcile')) 'reconciliation changed state/event/ledger/entity'
        Assert-Check 'repeatNoSend' ([bool](Get-Prop (Get-Prop $result 'checks') 'repeatQuerySentNothing')) 'a repeated query sent another frame'
        Assert-Check 'noCommandResend' ($oldCommands.Count -eq 0) 'the original command was resent after recovery'
        Assert-Check 'oneQuery' ($oldQueries.Count -eq 1) ('expected exactly one original receipt query, got ' + $oldQueries.Count)
        Assert-Check 'oldOutbox' ([int](Get-Prop $result 'oldOutboxUnresolved') -eq 1) 'the original outbox entry was not unresolved before reconciliation'
        foreach ($property in (Get-Prop $result 'checks').PSObject.Properties) {
            if ($property.Value -is [bool]) { Assert-Check ('check:' + $property.Name) ([bool]$property.Value) ('result check failed: ' + $property.Name) }
        }
    }
    elseif ($outcome -eq 'FRESH_START') {
        Assert-Check 'actualOutcome' (Same-Ordinal (Get-Prop $result 'actualOutcome') 'FRESH_START') 'actual outcome is not FRESH_START'
        $state = Get-Prop $result 'state'
        Assert-Check 'freshVersion' (Same-Ordinal (Get-Prop $state 'stateVersion') '0') 'fresh room stateVersion is not 0'
        Assert-Check 'oldReceiptAbsent' (-not [bool](Get-Prop $state 'oldReceiptPresent')) 'the old receipt is present in the fresh room'
        Assert-Check 'oldOutbox' ([int](Get-Prop $state 'oldOutboxUnresolved') -eq 1) 'the old outbox entry was not kept'
        Assert-Check 'noOldCommand' ($oldCommands.Count -eq 0) 'an old-scope command was sent after FRESH_START'
        Assert-Check 'noOldQuery' ($oldQueries.Count -eq 0) 'an old-scope receipt query was sent after FRESH_START'
        foreach ($property in (Get-Prop $result 'checks').PSObject.Properties) {
            if ($property.Value -is [bool]) { Assert-Check ('check:' + $property.Name) ([bool]$property.Value) ('result check failed: ' + $property.Name) }
        }
    }
    elseif ($outcome -eq 'REJECTED') {
        Assert-Check 'actualOutcome' (Same-Ordinal (Get-Prop $result 'actualOutcome') 'REJECTED') 'actual outcome is not REJECTED'
        Assert-Check 'zeroFrames' ($frames.Count -eq 0) 'a rejected recovery sent a wire frame'
        Assert-Check 'oldOutbox' ([int](Get-Prop (Get-Prop $result 'state') 'oldOutboxUnresolved') -eq 1) 'the old outbox entry was not kept'
        Assert-Check 'unchangedPrimary' (Same-Text (Get-Prop (Get-Prop $result 'fileHashes') 'primary') $input.primaryBeforeFaultSha256) 'the rejected recovery changed the primary save'
        Assert-Check 'unchangedBackup' (Same-Text (Get-Prop (Get-Prop $result 'fileHashes') 'backup') $input.backupBeforeFaultSha256) 'the rejected recovery changed the backup save'
        foreach ($property in (Get-Prop $result 'checks').PSObject.Properties) {
            if ($property.Value -is [bool]) { Assert-Check ('check:' + $property.Name) ([bool]$property.Value) ('result check failed: ' + $property.Name) }
        }
    }
    else {
        Assert-Check 'outcome' $false ('unknown expected outcome: ' + $outcome)
    }
}
catch {
    $errors.Add(('verifier exception: ' + $_.Exception.GetType().Name + ': ' + $_.Exception.Message)) | Out-Null
}

$status = if ($errors.Count -eq 0) { 'PASS' } else { 'FAIL' }
$verification = [ordered]@{
    schemaVersion = 1
    kind = 'c04-recovery-verification'
    status = $status
    resultPath = [IO.Path]::GetFullPath($ResultPath)
    expectedPath = [IO.Path]::GetFullPath($ExpectedPath)
    inputPath = [IO.Path]::GetFullPath($InputPath)
    checks = $checks
    errors = @($errors)
    verifiedAt = [DateTimeOffset]::UtcNow.ToString('o')
}
$json = $verification | ConvertTo-Json -Depth 10
[IO.File]::WriteAllText($OutputPath, $json, (New-Object System.Text.UTF8Encoding($false)))
Write-Output ('C04_VERIFY ' + $status + ' errors=' + $errors.Count + ' output=' + $OutputPath)
if ($errors.Count -ne 0) {
    foreach ($errorText in $errors) { Write-Output ('C04_VERIFY_ERROR ' + $errorText) }
    exit 1
}

exit 0
