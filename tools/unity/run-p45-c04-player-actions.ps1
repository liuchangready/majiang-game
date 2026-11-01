#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$RunRoot,
    [string]$UnityPath = 'D:\Unity\Hub\Editor\6000.3.27f1\Editor\Unity.exe',
    [string]$DotnetPath = 'D:\Unity\dotnet\dotnet.exe',
    [int]$TimeoutSeconds = 1800,
    [switch]$FixtureOnly
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$RunRoot = [IO.Path]::GetFullPath($RunRoot)
$allowed = [IO.Path]::GetFullPath((Join-Path $repo 'build')) + [IO.Path]::DirectorySeparatorChar
if (-not $RunRoot.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'RunRoot must be a fresh direct descendant of repository build/.' }
$leaf = Split-Path -Leaf $RunRoot
if ($leaf -notmatch '^[0-9a-fA-F]{32}$') { throw 'RunRoot leaf must be a GUID-N run id.' }
if (Test-Path -LiteralPath $RunRoot) { throw 'RunRoot already exists; refusing to overwrite evidence.' }
if (-not (Test-Path -LiteralPath $UnityPath -PathType Leaf)) { throw "Pinned Unity executable is missing: $UnityPath" }
if (-not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) { throw "Pinned .NET executable is missing: $DotnetPath" }

New-Item -ItemType Directory -Path $RunRoot | Out-Null
$runId = $leaf.ToLowerInvariant()
$source = Join-Path $RunRoot 'source'
$logs = Join-Path $RunRoot 'logs'
$playerDir = Join-Path $RunRoot 'player'
$caseRoot = Join-Path $RunRoot 'cases'
$dataRoot = Join-Path $RunRoot 'data'
$tempRoot = Join-Path $RunRoot 'temp'
$nugetRoot = Join-Path $RunRoot 'nuget'
$cliRoot = Join-Path $RunRoot 'dotnet-home'
foreach ($path in @($logs,$caseRoot,$dataRoot,$tempRoot,$nugetRoot,$cliRoot)) { New-Item -ItemType Directory -Force -Path $path | Out-Null }
$script:LogPath = Join-Path $logs 'runner.log'
$script:LogOpen = $true
$script:mutex = $null
$script:mutexHeld = $false
$script:stageRecords = New-Object System.Collections.Generic.List[object]
$oldTemp = $env:TEMP; $oldTmp = $env:TMP; $oldNuget = $env:NUGET_PACKAGES; $oldCli = $env:DOTNET_CLI_HOME
$env:TEMP = $tempRoot; $env:TMP = $tempRoot; $env:NUGET_PACKAGES = $nugetRoot; $env:DOTNET_CLI_HOME = $cliRoot
$env:DOTNET_NOLOGO = '1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

function Write-RunLog([string]$message) {
    $line = '[' + [DateTimeOffset]::UtcNow.ToString('o') + '] ' + $message
    Write-Host $line
    if ($script:LogOpen) { Add-Content -LiteralPath $script:LogPath -Value $line -Encoding UTF8 }
}

function Get-Sha256([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }

function ConvertFrom-C04PlayerSummary([string]$path) {
    $raw = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $normalized = @{}
    foreach ($property in $raw.PSObject.Properties) {
        $name = $property.Name
        $key = $name.Substring(0,1).ToLowerInvariant() + $name.Substring(1)
        $normalized[$key] = $property.Value
    }
    return [pscustomobject]$normalized
}

function Invoke-OwnedNative {
    param([string]$Name,[string]$FilePath,[string]$Arguments,[string]$WorkingDirectory,[string]$StdoutPath,[string]$StderrPath,[int]$Timeout)
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath; $psi.Arguments = $Arguments; $psi.WorkingDirectory = $WorkingDirectory
    $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $psi.EnvironmentVariables['TEMP'] = $tempRoot; $psi.EnvironmentVariables['TMP'] = $tempRoot
    $psi.EnvironmentVariables['NUGET_PACKAGES'] = $nugetRoot; $psi.EnvironmentVariables['DOTNET_CLI_HOME'] = $cliRoot
    $psi.EnvironmentVariables['DOTNET_NOLOGO'] = '1'; $psi.EnvironmentVariables['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $process = New-Object System.Diagnostics.Process; $process.StartInfo = $psi
    $started = [DateTimeOffset]::UtcNow
    if (-not $process.Start()) { throw "$Name failed to start." }
    $pidOwned = $process.Id
    $startTimeOwned = $process.StartTime.ToUniversalTime().ToString('o')
    $stdoutTask = $process.StandardOutput.ReadToEndAsync(); $stderrTask = $process.StandardError.ReadToEndAsync()
    $exited = $process.WaitForExit($Timeout * 1000)
    $didTimeout = -not $exited
    $treeKillExit = $null
    if ($didTimeout) {
        # PID came directly from the Process object created above. /T is scoped
        # to that owned process tree; never enumerate/kill unrelated processes.
        $kill = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\taskkill.exe') -ArgumentList @('/PID',"$pidOwned",'/T','/F') -PassThru -WindowStyle Hidden -Wait
        $treeKillExit = $kill.ExitCode
        $exited = $process.WaitForExit(3000)
    }
    $drained = [System.Threading.Tasks.Task]::WaitAll([System.Threading.Tasks.Task[]]@($stdoutTask,$stderrTask),3000)
    $stdoutText = if ($drained) { $stdoutTask.Result } else { '[stdout drain exceeded bounded 3s wait]' }
    $stderrText = if ($drained) { $stderrTask.Result } else { '[stderr drain exceeded bounded 3s wait]' }
    [IO.File]::WriteAllText($StdoutPath, $stdoutText, (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText($StderrPath, $stderrText, (New-Object Text.UTF8Encoding($false)))
    $exit = if ($didTimeout -or -not $exited) { 124 } else { $process.ExitCode }
    $record = [ordered]@{ name=$Name; file=$FilePath; arguments=$Arguments; workingDirectory=$WorkingDirectory; pid=$pidOwned; processStartUtc=$startTimeOwned; startedUtc=$started.ToString('o'); endedUtc=[DateTimeOffset]::UtcNow.ToString('o'); exitCode=$exit; timedOut=$didTimeout; processExitedAfterKill=$exited; treeKillExit=$treeKillExit; outputDrained=$drained; stdout=$StdoutPath; stderr=$StderrPath; stdoutSha256=(Get-Sha256 $StdoutPath); stderrSha256=(Get-Sha256 $StderrPath) }
    $script:stageRecords.Add($record) | Out-Null
    Write-RunLog "$Name nativeExit=$exit timeout=$didTimeout"
    return $record
}

function Quote-Arg([string]$value) { '"' + $value.Replace('"','\"') + '"' }

function ConvertFrom-C04CanonicalPayload([byte[]]$bytes) {
    if ($bytes.Length -lt 8 -or [Text.Encoding]::ASCII.GetString($bytes,0,4) -cne 'MJC1') { throw 'Payload is not an MJC1 canonical command.' }
    if ([BitConverter]::ToUInt16($bytes,4) -ne 1 -or [BitConverter]::ToUInt16($bytes,6) -ne 10) { throw 'Payload is not canonical SubmitAction v1.' }
    $fields = @{}; $offset = 8; $last = 0
    while ($offset -lt $bytes.Length) {
        if ($bytes.Length - $offset -lt 7) { throw 'Truncated canonical payload field header.' }
        $number = [BitConverter]::ToUInt16($bytes,$offset); $type = [int]$bytes[$offset+2]; $length = [BitConverter]::ToUInt32($bytes,$offset+3); $offset += 7
        if ($number -le $last -or $number -eq 0 -or $length -gt ($bytes.Length - $offset)) { throw 'Canonical fields are unordered, duplicate, or truncated.' }
        $last = $number; $value = $null
        if ($number -in 1,2,5) {
            if ($type -ne 5) { throw "Canonical field $number is not String." }
            if ($length -lt 4) { throw "Canonical field $number is missing its inner UTF-8 byte length." }
            $innerLength = [BitConverter]::ToUInt32($bytes,$offset)
            if ($innerLength -ne ($length - 4)) { throw "Canonical field $number inner UTF-8 byte length is inconsistent." }
            $value = (New-Object Text.UTF8Encoding($false,$true)).GetString($bytes,$offset+4,[int]$innerLength)
        } elseif ($number -eq 3) {
            if ($type -ne 3 -or $length -ne 8) { throw 'Canonical controlEpoch is not UInt64.' }
            $value = [BitConverter]::ToUInt64($bytes,$offset)
        } elseif ($number -in 4,7) {
            if ($type -ne 2 -or $length -ne 4) { throw "Canonical field $number is not UInt32." }
            $value = [BitConverter]::ToUInt32($bytes,$offset)
        } elseif ($number -eq 6) {
            if ($type -ne 7 -or $length -lt 4) { throw 'Canonical tileIds is not RepeatedUInt32.' }
            $count = [BitConverter]::ToUInt32($bytes,$offset)
            if ($count -gt 14 -or $length -ne (4 + 4 * $count)) { throw 'Canonical tileIds count/length mismatch.' }
            $items = @(); for ($i=0; $i -lt $count; $i++) { $items += [BitConverter]::ToUInt32($bytes,$offset+4+4*$i) }; $value = $items
        } else { throw "Unexpected SubmitAction canonical field $number." }
        $fields[[int]$number] = $value; $offset += [int]$length
    }
    foreach ($required in 1,2,3,4) { if (-not $fields.ContainsKey($required)) { throw "Missing SubmitAction canonical field $required." } }
    return $fields
}

function Get-ClosureManifest([string]$path,[string]$output) {
    $files = Get-ChildItem -LiteralPath $path -Recurse -File | Sort-Object FullName
    $entries = New-Object System.Collections.Generic.List[object]
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($path.Length).TrimStart('\','/').Replace('\','/')
        $entries.Add([ordered]@{ path=$relative; bytes=[long]$file.Length; sha256=(Get-Sha256 $file.FullName) }) | Out-Null
    }
    $manifest = [ordered]@{ root=$path; fileCount=$entries.Count; entries=$entries }
    $json = $manifest | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($output, $json + "`n", (New-Object Text.UTF8Encoding($false)))
    return Get-Sha256 $output
}

function Test-C04CaseSummary {
    param($Summary,[string]$ExpectedCase)
    if ($null -eq $Summary) { throw 'C04 summary is missing.' }
    if ($Summary.result -ne 'PASS' -or $Summary.status -ne 'GAME') { throw 'C04 result/status is not PASS/GAME.' }
    if ($Summary.eventConfirmed -ne $true) { throw 'C04 event confirmation is absent; Applied receipt alone is insufficient.' }
    if ($Summary.nativeExitCode -ne 0 -or $Summary.nativeTimedOut -ne $false) { throw 'C04 native process failed or timed out.' }
    if ($Summary.caseId -ne $ExpectedCase) { throw 'C04 summary case identity mismatch.' }
    if ($Summary.seat -ne 0 -or $Summary.eventSeat -ne 0) { throw 'C04 result is not bound to seat 0.' }
    if ([string]::IsNullOrWhiteSpace($Summary.roomId) -or [string]::IsNullOrWhiteSpace($Summary.sessionId) -or [string]::IsNullOrWhiteSpace($Summary.roundId)) { throw 'C04 room/session/round binding is incomplete.' }
    if ($Summary.eventRoundId -ne $Summary.roundId) { throw 'C04 visible event round mismatch.' }
    if ([string]::IsNullOrWhiteSpace($Summary.requestId) -or [string]::IsNullOrWhiteSpace($Summary.scopeId)) { throw 'C04 request/scope identity is incomplete.' }
    if ($Summary.receiptRequestId -ne $Summary.requestId -or $Summary.receiptScopeId -ne $Summary.scopeId) { throw 'C04 Applied receipt is not bound to the exact request/scope identity.' }
    if ($Summary.payloadSha256 -notmatch '^[0-9a-f]{64}$') { throw 'C04 canonical payload hash is missing or malformed.' }
    if ([string]::IsNullOrWhiteSpace($Summary.canonicalPayloadBase64)) { throw 'C04 canonical payload bytes are missing.' }
    $payloadBytes = [Convert]::FromBase64String($Summary.canonicalPayloadBase64)
    $payloadHash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($payloadBytes)).Replace('-','').ToLowerInvariant()
    if ($payloadHash -ne $Summary.payloadSha256) { throw 'C04 canonical payload bytes do not match the recorded hash.' }
    $payload = ConvertFrom-C04CanonicalPayload $payloadBytes
    $expectedAction = @{ 'C04-G2-01'=6; 'C04-G2-02'=5; 'C04-G2-03'=8; 'C04-G2-04'=7 }[$ExpectedCase]
    $expectedEventTiles = @{ 'C04-G2-01'='0,1,2'; 'C04-G2-02'='0,4,8'; 'C04-G2-03'=''; 'C04-G2-04'='0,1,2,3' }[$ExpectedCase]
    if ($null -eq $expectedAction -or [UInt32]$payload[4] -ne $expectedAction -or [UInt32]$Summary.payloadActionType -ne $expectedAction) { throw 'Canonical SubmitAction type does not match its fixed case.' }
    if ($payload[1] -cne $Summary.roundId -or $Summary.payloadRoundId -cne $payload[1]) { throw 'Canonical round id is not bound to the case summary.' }
    if ($payload[2] -cne $Summary.decisionId -or $Summary.payloadDecisionId -cne $payload[2] -or [string]::IsNullOrWhiteSpace($Summary.decisionId)) { throw 'Canonical decision id does not match the submitted candidate.' }
    if ([UInt64]$payload[3] -ne [UInt64]$Summary.controlEpoch -or [UInt64]$Summary.payloadControlEpoch -ne [UInt64]$payload[3]) { throw 'Canonical control epoch is not bound to the candidate.' }
    $payloadOption = if ($payload.ContainsKey(5)) { [string]$payload[5] } else { '' }
    $expectedMeldType = if ($ExpectedCase -eq 'C04-G2-04') { 4 } else { 0 }
    $payloadMeldType = if ($payload.ContainsKey(7)) { [UInt32]$payload[7] } else { 0 }
    if ($payloadOption -cne [string]$Summary.candidateOptionId -or $payloadOption -cne [string]$Summary.payloadOptionId -or $payloadMeldType -ne $expectedMeldType -or [UInt32]$Summary.payloadMeldType -ne $expectedMeldType) { throw 'Canonical candidate option/meld type does not match the fixed candidate.' }
    $payloadTiles = if ($payload.ContainsKey(6)) { (@($payload[6]) | Sort-Object) -join ',' } else { '' }
    if (($payloadTiles -ne $expectedEventTiles) -or (($Summary.payloadTileIds -split ',' | Where-Object { $_ } | Sort-Object) -join ',') -ne $expectedEventTiles) { throw 'Canonical physical tile selection does not match its fixed case.' }
    if ($Summary.receiptStatus -ne 'Applied' -or [UInt64]$Summary.receiptCommandVersion -le [UInt64]$Summary.stateVersionBefore) { throw 'Applied receipt command version is not above the subscribed baseline.' }
    if ([UInt64]$Summary.eventCommandVersion -ne [UInt64]$Summary.receiptCommandVersion -or [UInt64]$Summary.eventCommandVersion -le [UInt64]$Summary.stateVersionBefore) { throw 'C04 event command version does not match the post-baseline Applied command.' }
    if ([UInt64]$Summary.stateVersionAfter -le [UInt64]$Summary.stateVersionBefore -or [UInt64]$Summary.stateVersionAfter -ne [UInt64]$Summary.receiptCommandVersion) { throw 'C04 authoritative state version does not equal the Applied command version above baseline.' }
    if ([UInt64]$Summary.eventSeqAfter -lt [UInt64]$Summary.eventSeq -or [UInt64]$Summary.eventSeq -le [UInt64]$Summary.eventSeqBefore -or [UInt64]$Summary.receiptEventSeq -ne [UInt64]$Summary.eventSeqAfter) { throw 'C04 event cursor/high-water is invalid or differs from the receipt.' }
    if ([UInt64]$Summary.bindingEpoch -eq 0 -or [UInt64]$Summary.ownerEpoch -eq 0 -or [UInt64]$Summary.controlEpoch -eq 0 -or [UInt64]$Summary.eventBindingEpoch -ne [UInt64]$Summary.bindingEpoch -or [UInt64]$Summary.eventConnectionEpoch -ne [UInt64]$Summary.connectionEpoch -or [UInt64]$Summary.eventOwnerEpoch -ne [UInt64]$Summary.ownerEpoch) { throw 'C04 epoch identity is incomplete or differs from the accepted batch.' }
    if ($Summary.eventScopeId -cne $Summary.scopeId -or $Summary.eventSessionId -cne $Summary.sessionId -or $Summary.eventRoomId -cne $Summary.roomId -or $Summary.eventTableId -cne $Summary.tableId -or $Summary.eventRole -cne 'Player') { throw 'C04 event batch identity does not match the request context.' }
    $cursorAdvanced = ([UInt64]$Summary.cursorStreamAfter -gt [UInt64]$Summary.cursorStreamBefore) -or (([UInt64]$Summary.cursorStreamAfter -eq [UInt64]$Summary.cursorStreamBefore) -and ([UInt64]$Summary.cursorAfter -gt [UInt64]$Summary.cursorBefore))
    if (-not $cursorAdvanced) { throw 'C04 structured stream cursor did not advance.' }
    $eventByCase = @{ 'C04-G2-01'='PonDeclared'; 'C04-G2-02'='ChiDeclared'; 'C04-G2-03'='RiichiDeclared'; 'C04-G2-04'='KanDeclared' }
    if (-not $eventByCase.ContainsKey($ExpectedCase) -or $Summary.eventType -ne $eventByCase[$ExpectedCase]) { throw 'C04 action event type does not match its fixed case.' }
    if (($Summary.eventTiles -split ',' | Where-Object { $_ } | Sort-Object) -join ',' -ne $expectedEventTiles) { throw 'Visible action event did not carry the fixed physical entities.' }
    switch ($ExpectedCase) {
        'C04-G2-01' { if (($Summary.meldTileIds -split ',' | Sort-Object) -join ',' -ne '0,1,2' -or $Summary.handTileIdsAfter -match '(^|,)(0|1)(,|$)') { throw 'Pon entity transfer to meld/hand is invalid.' } }
        'C04-G2-02' { if (($Summary.meldTileIds -split ',' | Sort-Object) -join ',' -ne '0,4,8' -or $Summary.handTileIdsAfter -match '(^|,)(0|8)(,|$)') { throw 'Chi entity transfer to meld/hand is invalid.' } }
        'C04-G2-03' { if ([UInt64]$Summary.seatScoreBefore -ne 25000 -or [UInt64]$Summary.seatScoreAfter -ne 24000 -or [UInt32]$Summary.riichiSticksAfter -le [UInt32]$Summary.riichiSticksBefore) { throw 'Riichi score/stick transfer is invalid.' } }
        'C04-G2-04' { if (($Summary.meldTileIds -split ',' | Sort-Object) -join ',' -ne '0,1,2,3' -or [string]$Summary.replacementDrawTileId -ne '73') { throw 'Closed Kan/dead-wall replacement entity transfer is invalid.' } }
    }
    return $true
}

if ($FixtureOnly) {
    $ownedChildPath = Join-Path $RunRoot 'owned-timeout-child.ps1'
    $nestedPidPath = Join-Path $RunRoot 'owned-descendant.pid'
    $ownedScript = @'
$nested = Start-Process -FilePath $env:ComSpec -ArgumentList @('/c','ping -n 25 127.0.0.1 > nul') -PassThru -WindowStyle Hidden
[IO.File]::WriteAllText($env:C04_NESTED_PID_FILE,[string]$nested.Id)
Write-Output 'OWNED_TIMEOUT_CHILD_STARTED'
Start-Sleep -Seconds 25
'@
    [IO.File]::WriteAllText($ownedChildPath,$ownedScript,(New-Object Text.UTF8Encoding($false)))
    $env:C04_NESTED_PID_FILE = $nestedPidPath
    $timeoutRecord = Invoke-OwnedNative -Name 'FixtureOwnedTimeoutTree' -FilePath 'powershell.exe' -Arguments ('-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' + (Quote-Arg $ownedChildPath)) -WorkingDirectory $RunRoot -StdoutPath (Join-Path $logs 'owned-timeout.stdout.txt') -StderrPath (Join-Path $logs 'owned-timeout.stderr.txt') -Timeout 1
    Remove-Item Env:C04_NESTED_PID_FILE -ErrorAction SilentlyContinue
    if ($timeoutRecord.exitCode -ne 124 -or -not $timeoutRecord.timedOut -or -not $timeoutRecord.outputDrained -or
        (Get-Content -LiteralPath $timeoutRecord.stdout -Raw) -notmatch 'OWNED_TIMEOUT_CHILD_STARTED' -or
        -not (Test-Path -LiteralPath $nestedPidPath)) { throw 'Owned timeout/tree-kill/output-drain fixture did not retain its own process/output evidence.' }
    $descendantPid = [int](Get-Content -LiteralPath $nestedPidPath -Raw)
    $descendantAlive = $false
    try { $descendant = Get-Process -Id $descendantPid -ErrorAction Stop; $descendantAlive = $descendant.StartTime.ToUniversalTime() -gt [DateTime]::UtcNow.AddMinutes(-2) } catch { }
    if ($descendantAlive) { throw 'Owned timeout fixture descendant survived the scoped process-tree kill.' }
    $stream = New-Object IO.MemoryStream
    $writer = New-Object IO.BinaryWriter($stream)
    $writer.Write([Text.Encoding]::ASCII.GetBytes('MJC1')); $writer.Write([UInt16]1); $writer.Write([UInt16]10)
    function Write-Field([IO.BinaryWriter]$Writer,[UInt16]$Number,[byte]$Type,[byte[]]$Value) { $Writer.Write($Number); $Writer.Write($Type); $Writer.Write([UInt32]$Value.Length); $Writer.Write($Value) }
    function Encode-CanonicalString([string]$Value) { $textBytes=[Text.Encoding]::UTF8.GetBytes($Value); $buffer=New-Object IO.MemoryStream; $stringWriter=New-Object IO.BinaryWriter($buffer); $stringWriter.Write([UInt32]$textBytes.Length); $stringWriter.Write($textBytes); $stringWriter.Flush(); return ,$buffer.ToArray() }
    Write-Field $writer 1 5 (Encode-CanonicalString 'fixture-round')
    Write-Field $writer 2 5 (Encode-CanonicalString 'fixture-decision')
    Write-Field $writer 3 3 ([BitConverter]::GetBytes([UInt64]3))
    Write-Field $writer 4 2 ([BitConverter]::GetBytes([UInt32]6))
    $tileBytes = New-Object IO.MemoryStream; $tileWriter = New-Object IO.BinaryWriter($tileBytes)
    $tileWriter.Write([UInt32]3); $tileWriter.Write([UInt32]0); $tileWriter.Write([UInt32]1); $tileWriter.Write([UInt32]2); $tileWriter.Flush()
    Write-Field $writer 6 7 $tileBytes.ToArray(); $writer.Flush(); $fixtureBytes=$stream.ToArray()
    $fixtureHash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($fixtureBytes)).Replace('-','').ToLowerInvariant()
    $valid = [pscustomobject]@{
        result='PASS'; status='GAME'; eventConfirmed=$true; nativeExitCode=0; nativeTimedOut=$false
        caseId='C04-G2-01'; seat=0; eventSeat=0; roomId='fixture-room'; sessionId='fixture-session'; roundId='fixture-round'
        eventRoundId='fixture-round'; requestId='fixture-request'; scopeId='fixture-scope'; receiptRequestId='fixture-request'; receiptScopeId='fixture-scope'; payloadSha256=$fixtureHash
        canonicalPayloadBase64=[Convert]::ToBase64String($fixtureBytes); receiptStatus='Applied'
        eventCommandVersion=12; receiptCommandVersion=12; receiptEventSeq=21; stateVersionBefore=11; stateVersionAfter=12; eventSeqBefore=20; eventSeqAfter=21; eventSeq=21
        bindingEpoch=1; eventBindingEpoch=1; connectionEpoch=1; eventConnectionEpoch=1; ownerEpoch=2; eventOwnerEpoch=2; controlEpoch=3; eventType='PonDeclared'
        decisionId='fixture-decision'; tableId='fixture-table'; eventScopeId='fixture-scope'; eventSessionId='fixture-session'; eventRoomId='fixture-room'; eventTableId='fixture-table'; eventRole='Player'
        cursorBefore=10; cursorAfter=11; cursorStreamBefore=1; cursorStreamAfter=1
        payloadActionType=6; payloadRoundId='fixture-round'; payloadDecisionId='fixture-decision'; payloadControlEpoch=3; candidateOptionId=''; payloadOptionId=''; payloadMeldType=0; payloadTileIds='0,1,2'
        eventTiles='0,1,2'; meldTileIds='0,1,2'; handTileIdsAfter='20,24'; seatScoreBefore=25000; seatScoreAfter=25000; riichiSticksBefore=0; riichiSticksAfter=0; replacementDrawTileId=''
    }
    Test-C04CaseSummary $valid 'C04-G2-01' | Out-Null
    $negatives = @(
        @{ name='AppliedWithoutEvent'; update=@{ eventConfirmed=$false; eventType='' } },
        @{ name='WrongRound'; update=@{ eventRoundId='other-round' } },
        @{ name='WrongSeat'; update=@{ eventSeat=1 } },
        @{ name='WrongIdentity'; update=@{ receiptRequestId='foreign-request' } },
        @{ name='WrongEpoch'; update=@{ bindingEpoch=999 } },
        @{ name='NativeNonzero'; update=@{ nativeExitCode=17 } },
        @{ name='NativeTimeout'; update=@{ nativeTimedOut=$true } },
        @{ name='MissingCase'; update=@{ caseId='' } },
        @{ name='MissingPayload'; update=@{ canonicalPayloadBase64='' } },
        @{ name='HashMismatch'; update=@{ payloadSha256=('0' * 64) } },
        @{ name='CommandBeforeBaseline'; update=@{ receiptCommandVersion=1; eventCommandVersion=1 } },
        @{ name='CursorRollbackAndMissingDecision'; update=@{ cursorAfter=0; payloadDecisionId='' } },
        @{ name='WrongAction'; update=@{ payloadActionType=1 } },
        @{ name='WrongPhysicalEntities'; update=@{ eventTiles='0,1,3' } },
        @{ name='ForeignBatchIdentity'; update=@{ eventScopeId='foreign-scope' } },
        @{ name='EmptyEvidenceFacts'; update=@{ eventTiles=''; meldTileIds=''; roomId='' } },
        @{ name='WrongControlEpoch'; update=@{ controlEpoch=999; payloadControlEpoch=999 } },
        @{ name='WrongOwnerEpoch'; update=@{ ownerEpoch=999 } },
        @{ name='WrongEventCommandVersion'; update=@{ eventCommandVersion=11 } },
        @{ name='WrongMeldTransfer'; update=@{ meldTileIds='0,1,3' } }
    )
    foreach ($negative in $negatives) {
        $candidate = $valid | Select-Object *
        foreach ($key in $negative.update.Keys) { $candidate.$key = $negative.update[$key] }
        $rejected = $false
        try { Test-C04CaseSummary $candidate 'C04-G2-01' | Out-Null } catch { $rejected = $true }
        if (-not $rejected) { throw "Fixture guard accepted invalid case: $($negative.name)" }
    }
    $arbitrary = $valid | Select-Object *
    $arbitraryBytes = [Text.Encoding]::UTF8.GetBytes('SubmitAction ASCII with a recomputed self-consistent hash')
    $arbitrary.canonicalPayloadBase64 = [Convert]::ToBase64String($arbitraryBytes)
    $arbitrary.payloadSha256 = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($arbitraryBytes)).Replace('-','').ToLowerInvariant()
    $arbitraryRejected = $false
    try { Test-C04CaseSummary $arbitrary 'C04-G2-01' | Out-Null } catch { $arbitraryRejected = $true }
    if (-not $arbitraryRejected) { throw 'Fixture guard accepted arbitrary ASCII after recomputing its hash.' }
    [IO.File]::WriteAllText((Join-Path $RunRoot 'fixture-result.json'), (($valid | ConvertTo-Json -Depth 5) + "`n"), (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText((Join-Path $RunRoot 'foreign-sentinel.txt'), 'foreign-owner-preserved' + "`n", (New-Object Text.UTF8Encoding($false)))
    $fixtureFiles = Get-ChildItem -LiteralPath $RunRoot -Recurse -File | Sort-Object FullName | ForEach-Object { [ordered]@{ path=$_.FullName.Substring($RunRoot.Length).TrimStart('\','/'); sha256=(Get-Sha256 $_.FullName); bytes=$_.Length } }
    [IO.File]::WriteAllText((Join-Path $RunRoot 'fixture-seal.json'), (($fixtureFiles | ConvertTo-Json -Depth 5) + "`n"), (New-Object Text.UTF8Encoding($false)))
    Write-Host "C04_TOOL_FIXTURES_PASS positive=1 negative=$($negatives.Count + 1) timeoutTree=PASS outputDrain=PASS outputFiles=$((Get-ChildItem -LiteralPath $RunRoot -Recurse -File).Count)"
    exit 0
}

try {
    $mutexName = 'Local\MahjongV5P45UnityValidation'
    $script:mutex = New-Object System.Threading.Mutex($false, $mutexName)
    $waitStart = [DateTimeOffset]::UtcNow
    Write-RunLog "WAIT_MUTEX name=$mutexName root=$RunRoot"
    try { $script:mutexHeld = $script:mutex.WaitOne([TimeSpan]::FromMinutes(60)) } catch [System.Threading.AbandonedMutexException] { $script:mutexHeld = $true }
    if (-not $script:mutexHeld) { throw 'Timed out waiting for the shared Unity validation mutex; no Unity stage ran.' }
    Write-RunLog ('MUTEX_ACQUIRED waitedSeconds=' + ([DateTimeOffset]::UtcNow - $waitStart).TotalSeconds.ToString('F3'))

    $gitHead = (& git -C $repo rev-parse HEAD 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $gitHead -notmatch '^[0-9a-f]{40}$') { throw 'Unable to resolve frozen candidate HEAD.' }
    $gitStatusLines = @(& git -C $repo status --short 2>&1)
    if ($LASTEXITCODE -ne 0) { throw 'git status failed before source staging.' }
    $gitStatus = ($gitStatusLines -join "`n").Trim()
    if (-not [string]::IsNullOrWhiteSpace($gitStatus)) { throw "Frozen candidate source must be clean before build: $gitStatus" }
    $gitDiffCheck = & git -C $repo diff --check 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'Source diff --check failed before build.' }
    New-Item -ItemType Directory -Path $source | Out-Null
    $archive = Join-Path $RunRoot 'frozen-source.tar'
    $archiveArgs = @('-C',(Quote-Arg $repo),'archive','--format=tar','--output',(Quote-Arg $archive),$gitHead) -join ' '
    $archiveRecord = Invoke-OwnedNative -Name 'ArchiveFrozenCommit' -FilePath 'git.exe' -Arguments $archiveArgs -WorkingDirectory $repo -StdoutPath (Join-Path $logs 'archive.stdout.txt') -StderrPath (Join-Path $logs 'archive.stderr.txt') -Timeout 180
    if ($archiveRecord.exitCode -ne 0 -or -not (Test-Path -LiteralPath $archive)) { throw 'Frozen commit archive failed.' }
    $extractArgs = @('-xf',(Quote-Arg $archive),'-C',(Quote-Arg $source)) -join ' '
    $extract = Invoke-OwnedNative -Name 'ExtractFrozenCommit' -FilePath 'tar.exe' -Arguments $extractArgs -WorkingDirectory $RunRoot -StdoutPath (Join-Path $logs 'extract.stdout.txt') -StderrPath (Join-Path $logs 'extract.stderr.txt') -Timeout 300
    if ($extract.exitCode -ne 0 -or (Test-Path -LiteralPath (Join-Path $source '.git'))) { throw 'Frozen source archive extraction failed or included git metadata.' }
    $sourceClosureSha = Get-ClosureManifest $source (Join-Path $RunRoot 'source-prebuild-closure.json')

    $dotnetResults = Join-Path $RunRoot 'dotnet-results'
    $dotnetArtifacts = Join-Path $RunRoot 'dotnet-artifacts'
    New-Item -ItemType Directory -Path $dotnetResults,$dotnetArtifacts | Out-Null
    $trx = Join-Path $dotnetResults 'LocalHost.trx'
    $dotnetArgs = @('test',(Quote-Arg (Join-Path $source 'dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj')),
        '--configuration','Release','--artifacts-path',(Quote-Arg $dotnetArtifacts),
        '--results-directory',(Quote-Arg $dotnetResults),'--logger',(Quote-Arg 'trx;LogFileName=LocalHost.trx')) -join ' '
    $localHostTests = Invoke-OwnedNative -Name 'FreshLocalHostFullTests' -FilePath $DotnetPath -Arguments $dotnetArgs -WorkingDirectory $source -StdoutPath (Join-Path $logs 'localhost.stdout.txt') -StderrPath (Join-Path $logs 'localhost.stderr.txt') -Timeout 1200
    if ($localHostTests.exitCode -ne 0 -or -not (Test-Path -LiteralPath $trx)) { throw "Fresh LocalHost tests failed; nativeExit=$($localHostTests.exitCode) or TRX missing." }
    [xml]$trxXml = Get-Content -LiteralPath $trx -Raw
    $trxCounters = $trxXml.SelectSingleNode('//*[local-name()="Counters"]')
    $totalTests = [int]$trxCounters.GetAttribute('total'); $passedTests = [int]$trxCounters.GetAttribute('passed')
    $failedTests = [int]$trxCounters.GetAttribute('failed'); $skippedTests = [int]$trxCounters.GetAttribute('notExecuted')
    if ($totalTests -le 0 -or $passedTests -ne $totalTests -or $failedTests -ne 0 -or $skippedTests -ne 0) { throw "LocalHost test report must be nonempty, all pass, and zero skipped; total=$totalTests passed=$passedTests failed=$failedTests skipped=$skippedTests." }
    $dotnetClosureSha = Get-ClosureManifest $dotnetArtifacts (Join-Path $RunRoot 'dotnet-build-artifacts-closure.json')
    $localHostTestEvidence = [ordered]@{ command=$DotnetPath + ' ' + $dotnetArgs; nativeExitCode=$localHostTests.exitCode; total=$totalTests; passed=$passedTests; failed=$failedTests; skipped=$skippedTests; trx='dotnet-results/LocalHost.trx'; trxSha256=(Get-Sha256 $trx); buildArtifacts='dotnet-build-artifacts-closure.json'; buildArtifactsSha256=$dotnetClosureSha; stdoutSha256=$localHostTests.stdoutSha256; stderrSha256=$localHostTests.stderrSha256 }

    $project = Join-Path $source 'unity/Mahjong.Unity'
    $projectPlugins = Join-Path $project 'Assets/Plugins/Mahjong'
    $sharedLog = Join-Path $logs 'build-shared.log'
    $sharedArgs = @('-NoLogo','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',(Quote-Arg (Join-Path $source 'tools/unity/build-shared.ps1')),'-Configuration','Release','-DotnetPath',(Quote-Arg $DotnetPath),'-PluginOutputDir',(Quote-Arg $projectPlugins)) -join ' '
    $shared = Invoke-OwnedNative -Name 'BuildSharedClosure' -FilePath 'powershell.exe' -Arguments $sharedArgs -WorkingDirectory $source -StdoutPath $sharedLog -StderrPath (Join-Path $logs 'build-shared.stderr.txt') -Timeout 900
    if ($shared.exitCode -ne 0) { throw 'Fresh shared plugin build failed; Unity stages were blocked.' }
    $sharedEvidence = Join-Path $project 'Library/SharedPluginProvenance/provenance.json'
    $guardArgs = @('-NoLogo','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',(Quote-Arg (Join-Path $source 'tools/unity/check-shared-plugins.ps1')),'-ProjectRoot',(Quote-Arg $source),'-PluginDir',(Quote-Arg $projectPlugins),'-EvidencePath',(Quote-Arg $sharedEvidence),'-Quiet') -join ' '
    $guard = Invoke-OwnedNative -Name 'VerifySharedPluginProvenance' -FilePath 'powershell.exe' -Arguments $guardArgs -WorkingDirectory $source -StdoutPath (Join-Path $logs 'shared-provenance.stdout.txt') -StderrPath (Join-Path $logs 'shared-provenance.stderr.txt') -Timeout 300
    if ($guard.exitCode -ne 0) { throw 'Fresh shared plugin provenance gate failed.' }
    $checkCopied = Invoke-OwnedNative -Name 'VerifyCopiedProjectPlugins' -FilePath 'powershell.exe' -Arguments $guardArgs -WorkingDirectory $source -StdoutPath (Join-Path $logs 'copied-plugin.stdout.txt') -StderrPath (Join-Path $logs 'copied-plugin.stderr.txt') -Timeout 300
    if ($checkCopied.exitCode -ne 0) { throw 'Freshly built Unity project plugin closure failed provenance validation.' }
    $pluginClosureSha = Get-ClosureManifest $projectPlugins (Join-Path $RunRoot 'shared-plugin-postbuild-closure.json')
    $provenanceSha = Get-Sha256 $sharedEvidence

    $playXml = Join-Path $logs 'playmode.xml'; $playLog = Join-Path $logs 'playmode.unity.log'
    $unityTestArgs = @('-batchmode','-nographics','-projectPath',(Quote-Arg $project),'-runTests','-testPlatform','PlayMode','-testFilter','Mahjong.Unity.Tests.PlayMode.C04PlayerActionScenarioTests','-testResults',(Quote-Arg $playXml),'-logFile',(Quote-Arg $playLog),'-quit') -join ' '
    $play = Invoke-OwnedNative -Name 'UnityPlayModeC04' -FilePath $UnityPath -Arguments $unityTestArgs -WorkingDirectory $project -StdoutPath (Join-Path $logs 'playmode.stdout.txt') -StderrPath (Join-Path $logs 'playmode.stderr.txt') -Timeout 900
    if ($play.exitCode -ne 0 -or -not (Test-Path -LiteralPath $playXml)) { throw 'Targeted Unity PlayMode run failed or produced no XML.' }
    [xml]$playResults = Get-Content -LiteralPath $playXml -Raw
    $testNodes = @($playResults.SelectNodes('//test-case'))
    $failedNodes = @($testNodes | Where-Object { $_.GetAttribute('result') -ne 'Passed' })
    $caseNames = @('Pon_FixedPlayerActionIsAppliedAndVisible','Chi_FixedPlayerActionIsAppliedAndVisible','Riichi_FixedPlayerActionIsAppliedAndVisible','ClosedKan_FixedPlayerActionAndReplacementDrawAreApplied')
    $actualNames = @($testNodes | ForEach-Object { $_.GetAttribute('name').Split('.')[-1] })
    if ($testNodes.Count -ne 4 -or $failedNodes.Count -ne 0 -or (($actualNames | Sort-Object) -join '|') -cne (($caseNames | Sort-Object) -join '|') -or @($actualNames | Select-Object -Unique).Count -ne 4) { throw "PlayMode must report the exact four unique real C04 cases all Passed; actual=$($testNodes.Count) failed=$($failedNodes.Count) names=$($actualNames -join ',')." }

    New-Item -ItemType Directory -Path $playerDir | Out-Null
    $exe = Join-Path $playerDir 'Mahjong.Unity.exe'
    $buildMarker = Join-Path $logs 'player-build.json'
    $buildArgs = @('-batchmode','-nographics','-projectPath',(Quote-Arg $project),'-executeMethod','C04PlayerActionBuild.Build','-c04-player-output',(Quote-Arg $exe),'-c04-player-marker',(Quote-Arg $buildMarker),'-logFile',(Quote-Arg (Join-Path $logs 'player-build.unity.log')),'-quit') -join ' '
    $build = Invoke-OwnedNative -Name 'BuildFreshWindowsPlayer' -FilePath $UnityPath -Arguments $buildArgs -WorkingDirectory $project -StdoutPath (Join-Path $logs 'player-build.stdout.txt') -StderrPath (Join-Path $logs 'player-build.stderr.txt') -Timeout 1200
    if ($build.exitCode -ne 0 -or -not (Test-Path -LiteralPath $exe) -or -not (Test-Path -LiteralPath $buildMarker)) { throw 'Fresh Windows Player build failed or marker missing.' }
    $markerValue = Get-Content -LiteralPath $buildMarker -Raw | ConvertFrom-Json
    if ($markerValue.result -ne 'Succeeded' -or [int]$markerValue.errors -ne 0 -or $markerValue.target -notmatch 'Windows' -or @($markerValue.scenes | Where-Object { $_ -like '*Boot.unity' }).Count -ne 1) { throw 'Fresh Player build marker does not describe a successful Windows production Boot build.' }
    $postBuildClosureSha = Get-ClosureManifest (Join-Path $project 'Assets') (Join-Path $RunRoot 'source-postbuild-assets-closure.json')
    $scriptAssembliesSha = Get-ClosureManifest (Join-Path $project 'Library/ScriptAssemblies') (Join-Path $RunRoot 'unity-script-assemblies-closure.json')

    $playerFiles = New-Object System.Collections.Generic.List[object]
    foreach ($file in Get-ChildItem -LiteralPath $playerDir -Recurse -File | Sort-Object FullName) {
        $playerFiles.Add([ordered]@{ path=$file.FullName.Substring($playerDir.Length).TrimStart('\','/').Replace('\','/'); bytes=[long]$file.Length; sha256=(Get-Sha256 $file.FullName) }) | Out-Null
    }
    [IO.File]::WriteAllText((Join-Path $RunRoot 'player-package.json'), (($playerFiles | ConvertTo-Json -Depth 5) + "`n"), (New-Object Text.UTF8Encoding($false)))

    $caseSpecs = @(
        @{ id='C04-G2-01'; action='pon'; event='PonDeclared' },
        @{ id='C04-G2-02'; action='chi'; event='ChiDeclared' },
        @{ id='C04-G2-03'; action='riichi'; event='RiichiDeclared' },
        @{ id='C04-G2-04'; action='closed-kan'; event='KanDeclared' }
    )
    $caseResults = New-Object System.Collections.Generic.List[object]
    foreach ($case in $caseSpecs) {
        $caseDir = Join-Path $caseRoot $case.id; $caseData = Join-Path $dataRoot $case.id
        New-Item -ItemType Directory -Path $caseDir,$caseData | Out-Null
        $nativeLog = Join-Path $caseDir 'player.log'
        $playerArgs = @('-batchmode','-nographics','-runInBackground','-smoke-data',(Quote-Arg $caseData),'-logFile',(Quote-Arg $nativeLog),'-c04-player-actions',(Quote-Arg $caseDir),'-c04-player-action-case',$case.id,'-c04-player-action-run-id',$runId)
        $player = Invoke-OwnedNative -Name ('Player-' + $case.id) -FilePath $exe -Arguments ($playerArgs -join ' ') -WorkingDirectory $playerDir -StdoutPath (Join-Path $caseDir 'stdout.txt') -StderrPath (Join-Path $caseDir 'stderr.txt') -Timeout $TimeoutSeconds
        $summaryPath = Join-Path $caseDir 'summary.json'
        if ($player.exitCode -ne 0 -or -not (Test-Path -LiteralPath $summaryPath)) { throw "Player $($case.id) native exit $($player.exitCode), summary missing or nonzero." }
        $summary = ConvertFrom-C04PlayerSummary $summaryPath
        if ($summary.eventType -ne $case.event) { throw "Player $($case.id) visible event mismatch: $($summary.eventType)." }
        $summary | Add-Member -NotePropertyName nativeExitCode -NotePropertyValue $player.exitCode -Force
        $summary | Add-Member -NotePropertyName nativeTimedOut -NotePropertyValue $player.timedOut -Force
        $executionPath = Join-Path $caseDir 'execution.json'
        [IO.File]::WriteAllText($executionPath, (($summary | ConvertTo-Json -Depth 8) + "`n"), (New-Object Text.UTF8Encoding($false)))
        Test-C04CaseSummary $summary $case.id | Out-Null
        if ($summary.runId -ne $runId) { throw "Player $($case.id) run identity mismatch." }
        $caseResults.Add([ordered]@{ caseId=$case.id; status='PASS'; layer='WINDOWS_PLAYER'; nativeExitCode=$player.exitCode; summary=$summaryPath; summarySha256=(Get-Sha256 $summaryPath); executionEvidence=$executionPath; executionEvidenceSha256=(Get-Sha256 $executionPath); playerLog=$nativeLog; playerLogSha256=(Get-Sha256 $nativeLog); payloadSha256=$summary.payloadSha256 }) | Out-Null
    }

    $final = [ordered]@{ schemaVersion=1; task='G2-C04-D03'; status='PASS'; acceptedBase='8fd7c6cb701dfbb41c28938de4186749bdd1d6c7'; frozenCandidate=$gitHead; sourceTreeStatus=$gitStatus; sourceDiffCheck='PASS'; sourceClosureManifest='source-prebuild-closure.json'; sourceClosureManifestSha256=$sourceClosureSha; postBuildAssetsClosure='source-postbuild-assets-closure.json'; postBuildAssetsClosureSha256=$postBuildClosureSha; sharedPluginClosureSha256=$pluginClosureSha; sharedPluginProvenance=$sharedEvidence; sharedPluginProvenanceSha256=$provenanceSha; scriptAssembliesClosureSha256=$scriptAssembliesSha; localHostTests=$localHostTestEvidence; commands=$script:stageRecords; playMode=[ordered]@{ testCount=$testNodes.Count; passed=$testNodes.Count; failed=0; skipped=0; exactNames=$actualNames; xml=$playXml; xmlSha256=(Get-Sha256 $playXml) }; buildMarker=$buildMarker; buildMarkerSha256=(Get-Sha256 $buildMarker); playerPackageManifest='player-package.json'; cases=$caseResults; platforms=[ordered]@{ windowsPlayer='actual four fixed cases'; android='NOT_RUN (C05 owner)'; ios='NOT_RUN'; remoteTransport='NOT_IN_SCOPE' } }
    $summaryOut = Join-Path $RunRoot 'summary.json'
    [IO.File]::WriteAllText($summaryOut, (($final | ConvertTo-Json -Depth 12) + "`n"), (New-Object Text.UTF8Encoding($false)))
    Write-RunLog 'FINAL_SUMMARY_WRITTEN'
    $script:LogOpen = $false
    $artifactFiles = Get-ChildItem -LiteralPath $RunRoot -Recurse -File | Where-Object { $_.Name -ne 'manifest.json' -and $_.Name -ne 'manifest.sha256' } | Sort-Object FullName
    $artifacts = New-Object System.Collections.Generic.List[object]
    foreach ($file in $artifactFiles) {
        $artifacts.Add([ordered]@{ path=$file.FullName.Substring($RunRoot.Length).TrimStart('\','/').Replace('\','/'); bytes=[long]$file.Length; sha256=(Get-Sha256 $file.FullName) }) | Out-Null
    }
    $manifest = [ordered]@{ schemaVersion=1; task='G2-C04-D03'; root=$RunRoot; entryCount=$artifacts.Count; entries=$artifacts }
    $manifestPath = Join-Path $RunRoot 'manifest.json'
    [IO.File]::WriteAllText($manifestPath, (($manifest | ConvertTo-Json -Depth 10) + "`n"), (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText((Join-Path $RunRoot 'manifest.sha256'), ((Get-Sha256 $manifestPath) + "  manifest.json`n"), (New-Object Text.UTF8Encoding($false)))
    Write-Host "C04_PLAYER_ACTIONS_PASS cases=4 manifest=$(Get-Sha256 $manifestPath) root=$RunRoot"
}
catch {
    if ($script:LogOpen) { Write-RunLog ('FAIL ' + $_.Exception.GetType().Name + ': ' + $_.Exception.Message) }
    $failurePath = Join-Path $RunRoot 'failure.json'
    if (-not (Test-Path -LiteralPath $failurePath)) {
        $failure = [ordered]@{ status='FAIL'; task='G2-C04-D03'; error=$_.Exception.Message; stageRecords=$script:stageRecords }
        [IO.File]::WriteAllText($failurePath, (($failure | ConvertTo-Json -Depth 8) + "`n"), (New-Object Text.UTF8Encoding($false)))
    }
    throw
}
finally {
    if ($script:mutexHeld) { try { $script:mutex.ReleaseMutex() } catch {} }
    if ($script:mutex) { $script:mutex.Dispose() }
    $env:TEMP=$oldTemp; $env:TMP=$oldTmp; $env:NUGET_PACKAGES=$oldNuget; $env:DOTNET_CLI_HOME=$oldCli
}
