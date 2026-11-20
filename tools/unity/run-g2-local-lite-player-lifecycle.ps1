#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PlayerDirectory,
    [Parameter(Mandatory=$true)][string]$DataRoot,
    [Parameter(Mandatory=$true)][string]$EvidenceRoot,
    [int]$TimeoutSeconds = 300,
    [int]$MutexTimeoutSeconds = 600,
    [int]$HeartbeatSeconds = 30,
    [string]$MutexCancelFile
)
$ErrorActionPreference='Stop'
$PlayerDirectory=[IO.Path]::GetFullPath($PlayerDirectory)
$DataRoot=[IO.Path]::GetFullPath($DataRoot)
$EvidenceRoot=[IO.Path]::GetFullPath($EvidenceRoot)
$exe=Join-Path $PlayerDirectory 'Mahjong.exe'
if(-not (Test-Path -LiteralPath $exe -PathType Leaf)){ throw "PLAYER_MISSING:$exe" }
if(Test-Path -LiteralPath $EvidenceRoot){ throw 'EVIDENCE_ROOT_EXISTS_REFUSING_OVERWRITE' }
$probeSaveDir=Join-Path (Join-Path $DataRoot 'G2LocalLite/runs/20261120T000000000Z-00000000000000000000000000000000') 'local'
$maxSaveTemp=Join-Path $probeSaveDir 'local-lite-00000000000000000000000000000000.save.tmp.00000000000000000000000000000000'
if($maxSaveTemp.Length -gt 240){ throw "DATAROOT_TOO_LONG_FOR_NATIVE_WINDOWS_SAVE_PATH:$($maxSaveTemp.Length)" }
if($TimeoutSeconds -lt 1 -or $TimeoutSeconds -gt 600){ throw 'TimeoutSeconds must be 1..600' }
if($MutexTimeoutSeconds -lt 1 -or $MutexTimeoutSeconds -gt 600){ throw 'MutexTimeoutSeconds must be 1..600' }
if($HeartbeatSeconds -lt 1 -or $HeartbeatSeconds -gt 30){ throw 'HeartbeatSeconds must be 1..30' }
$null=New-Item -ItemType Directory -Path $EvidenceRoot,$DataRoot
$log=Join-Path $EvidenceRoot 'runner.log'
$mutexName='Local\MahjongV5P45UnityValidation'
$mutex=New-Object System.Threading.Mutex($false,$mutexName)
$held=$false;$proc=$null;$exitCode=1;$stage='WAIT_MUTEX';$started=[DateTimeOffset]::UtcNow
function Write-Stage([string]$text){$line='['+[DateTimeOffset]::UtcNow.ToString('o')+'] '+$text;Write-Host $line;Add-Content -LiteralPath $log -Value $line -Encoding UTF8}
function Get-OwnedTcpRows([int]$pidValue){
    $rows=@();$output=& netstat.exe -ano -p tcp 2>$null
    foreach($line in $output){if($line -match '^\s*TCP\s+(\S+)\s+(\S+)\s+(\S+)\s+(\d+)\s*$' -and [int]$Matches[4] -eq $pidValue){$rows+=,[ordered]@{local=$Matches[1];remote=$Matches[2];state=$Matches[3]}}}
    return ,$rows
}
function Stop-OwnedPlayer([System.Diagnostics.Process]$player,[string]$reason){
    $player.Refresh()
    if($player.HasExited){return}
    Write-Stage ($reason+'_REQUEST_CLOSE_MAIN_WINDOW pid='+$player.Id)
    $accepted=$false
    try{$accepted=$player.CloseMainWindow()}catch{}
    Write-Stage ($reason+'_CLOSE_MAIN_WINDOW_ACCEPTED='+$accepted)
    if($player.WaitForExit(5000)){return}
    Write-Stage ($reason+'_KILL_OWNED_PID pid='+$player.Id)
    try{$player.Kill()}catch{$player.Refresh();if(-not $player.HasExited){throw}}
    if(-not $player.WaitForExit(10000)){throw ($reason+'_OWNED_PLAYER_CLEANUP_FAILED pid='+$player.Id)}
}
try{
    $mutexDeadline=[DateTimeOffset]::UtcNow.AddSeconds($MutexTimeoutSeconds)
    $nextMutexHeartbeat=[DateTimeOffset]::UtcNow.AddSeconds($HeartbeatSeconds)
    while(-not $held){
        if($MutexCancelFile -and (Test-Path -LiteralPath $MutexCancelFile)){throw 'CANCELLED_WHILE_WAITING_FOR_SHARED_MUTEX'}
        $remainingMs=[int][Math]::Max(0,($mutexDeadline-[DateTimeOffset]::UtcNow).TotalMilliseconds)
        if($remainingMs -le 0){throw 'SHARED_MUTEX_TIMEOUT'}
        $waitMs=[Math]::Max(1,[Math]::Min(1000,$remainingMs))
        try{$held=$mutex.WaitOne($waitMs)}catch [System.Threading.AbandonedMutexException]{$held=$true}
        if(-not $held -and ([DateTimeOffset]::UtcNow -ge $mutexDeadline)){throw 'SHARED_MUTEX_TIMEOUT'}
        if(-not $held -and [DateTimeOffset]::UtcNow -ge $nextMutexHeartbeat){Write-Stage 'HEARTBEAT_MUTEX_WAIT';$nextMutexHeartbeat=[DateTimeOffset]::UtcNow.AddSeconds($HeartbeatSeconds)}
    }
    Write-Stage 'MUTEX_ACQUIRED'
    $stage='PLAYER_LIFECYCLE'
    $playerLog=Join-Path $EvidenceRoot 'player.log'
    $playerArgs=@('-smoke-data',('"'+$DataRoot+'"'),'-g2-local-lite-lifecycle','-logFile',('"'+$playerLog+'"')) -join ' '
    $proc=Start-Process -FilePath $exe -ArgumentList $playerArgs -PassThru -WindowStyle Hidden
    Write-Stage ('PLAYER_STARTED pid='+$proc.Id+' exeSha256='+(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant())
    $tcpSamples=0;$tcpRows=@();$lastHeartbeat=[DateTimeOffset]::UtcNow;$deadline=[DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds);$cancelled=$false
    while(-not $proc.HasExited -and [DateTimeOffset]::UtcNow -lt $deadline){
        if(-not $cancelled -and $MutexCancelFile -and (Test-Path -LiteralPath $MutexCancelFile)){$cancelled=$true;Write-Stage 'CANCEL_REQUESTED_CLOSE_MAIN_WINDOW';if(-not $proc.CloseMainWindow()){Write-Stage 'NORMAL_CLOSE_REQUEST_UNAVAILABLE'}}
        if($cancelled){break}
        $tcpSamples++;$tcpRows+=Get-OwnedTcpRows $proc.Id
        if(([DateTimeOffset]::UtcNow-$lastHeartbeat).TotalSeconds -ge $HeartbeatSeconds){Write-Stage ('HEARTBEAT stage='+$stage+' pid='+$proc.Id+' elapsedSeconds='+[int]([DateTimeOffset]::UtcNow-$started).TotalSeconds);$lastHeartbeat=[DateTimeOffset]::UtcNow}
        Start-Sleep -Milliseconds 500;$proc.Refresh()
    }
    if(-not $proc.HasExited -and $cancelled){Stop-OwnedPlayer $proc 'CANCELLED';throw 'PLAYER_LIFECYCLE_CANCELLED'}
    if(-not $proc.HasExited){Stop-OwnedPlayer $proc 'TIMEOUT';throw 'PLAYER_LIFECYCLE_TIMEOUT'}
    $exitCode=$proc.ExitCode
    Write-Stage ('PLAYER_EXIT nativeExit='+$exitCode+' tcpSamples='+$tcpSamples)
    $stage='VERIFY_PLAYER_EVIDENCE'
    $runFiles=@(Get-ChildItem -LiteralPath (Join-Path $DataRoot 'G2LocalLite/runs') -Recurse -File -Filter 'lifecycle-matrix.json' -ErrorAction SilentlyContinue)
    if($runFiles.Count -ne 1){throw "EXPECTED_ONE_LIFECYCLE_RECORD_FOUND_$($runFiles.Count)"}
    $lifecycle=Get-Content -LiteralPath $runFiles[0].FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    if($exitCode -ne 0 -or $lifecycle.status -ne 'PASS' -or $lifecycle.cases.Count -ne 10 -or
       @($lifecycle.rooms|Select-Object -Unique).Count -ne 10 -or @($lifecycle.rounds|Select-Object -Unique).Count -ne 10 -or
       @($lifecycle.scopes|Select-Object -Unique).Count -ne 10 -or @($lifecycle.generations|Select-Object -Unique).Count -ne 10 -or
       -not $lifecycle.roomClosed){throw 'PLAYER_LIFECYCLE_ASSERTION_FAILED'}
    $uniqueTcp=@($tcpRows|Sort-Object local,remote,state -Unique)
    $runRoot=Split-Path -Parent $runFiles[0].FullName
    $dataFiles=@(Get-ChildItem -LiteralPath $runRoot -File -Recurse|Sort-Object FullName|ForEach-Object{[ordered]@{path=$_.FullName.Substring($runRoot.Length+1);bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}})
    $record=[ordered]@{status='PASS';nativeExit=$exitCode;player=$exe;playerSha256=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant();playerLog=$playerLog;runRoot=$runRoot;lifecycleEvidence=$runFiles[0].FullName;dataRoot=$DataRoot;dataFiles=$dataFiles;caseCount=$lifecycle.cases.Count;uniqueRoomCount=@($lifecycle.rooms|Select-Object -Unique).Count;uniqueRoundCount=@($lifecycle.rounds|Select-Object -Unique).Count;uniqueScopeCount=@($lifecycle.scopes|Select-Object -Unique).Count;roomClosed=[bool]$lifecycle.roomClosed;tcpSampleCount=$tcpSamples;tcpUniqueConnections=$uniqueTcp;networkScope='Player process TCP table samples during the complete automated lifecycle process; no UDP or external OS-wide capture'}
    [IO.File]::WriteAllText((Join-Path $EvidenceRoot 'player-lifecycle-result.json'),(($record|ConvertTo-Json -Depth 10)+"`n"),(New-Object Text.UTF8Encoding($false)))
    Write-Stage 'PLAYER_LIFECYCLE_PASS cases=10 roomRoundScopeGeneration_unique=true roomClosed=true'
}
catch{Write-Stage ('FAILED stage='+$stage+' message='+$_.Exception.Message);$exitCode=1}
finally{
    if($proc){$proc.Refresh();if(-not $proc.HasExited){try{Stop-OwnedPlayer $proc 'FINALIZER'}catch{Write-Stage ('OWNED_PROCESS_CLEANUP_FAILED pid='+$proc.Id);try{$proc.Refresh();if(-not $proc.HasExited){$proc.Kill();$null=$proc.WaitForExit(10000)}}catch{}}}}
    if($proc){$proc.Refresh()}
    $writerStopped=(-not $proc -or $proc.HasExited)
    if(-not $writerStopped){Write-Stage 'MANIFEST_NOT_SEALED_WRITER_STILL_RUNNING'}
    if($held -and $writerStopped){$mutex.ReleaseMutex()};$mutex.Dispose()
    $summary=[ordered]@{task='G2-LOCAL-LITE-20261119';stage=$stage;nativeExit=$exitCode;startedUtc=$started.ToString('o');endedUtc=[DateTimeOffset]::UtcNow.ToString('o');mutexName=$mutexName;mutexHeld=$held;evidenceRoot=$EvidenceRoot;playerPid=$(if($proc){$proc.Id}else{$null})}
    [IO.File]::WriteAllText((Join-Path $EvidenceRoot 'summary.json'),(($summary|ConvertTo-Json -Depth 6)+"`n"),(New-Object Text.UTF8Encoding($false)))
    if($writerStopped){
        $files=@(Get-ChildItem -LiteralPath $EvidenceRoot -File -Recurse|Where-Object{$_.Name -notin @('manifest.json','manifest.sha256')}|Sort-Object FullName|ForEach-Object{[ordered]@{path=$_.FullName.Substring($EvidenceRoot.Length+1);bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}})
        if(Test-Path -LiteralPath $DataRoot){$files+=@(Get-ChildItem -LiteralPath $DataRoot -File -Recurse|Sort-Object FullName|ForEach-Object{[ordered]@{path=('data/'+$_.FullName.Substring($DataRoot.Length+1));bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}})}
        [IO.File]::WriteAllText((Join-Path $EvidenceRoot 'manifest.json'),((([ordered]@{task='G2-LOCAL-LITE-20261119';scope='owned Player lifecycle evidence';files=$files})|ConvertTo-Json -Depth 10)+"`n"),(New-Object Text.UTF8Encoding($false)))
        [IO.File]::WriteAllText((Join-Path $EvidenceRoot 'manifest.sha256'),((Get-FileHash -LiteralPath (Join-Path $EvidenceRoot 'manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()+"`n"),(New-Object Text.UTF8Encoding($false)))
    }
}
exit $exitCode
