#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$DotnetPath,
    [Parameter(Mandatory=$true)][string]$VerifierDll,
    [string]$RawFramesPath,
    [Parameter(Mandatory=$true)][string]$RunRoot,
    [string]$LegacyRawFramesPath,
    [ValidateRange(1000,120000)][int]$ProcessTimeoutMs = 20000
)
$ErrorActionPreference='Stop'
$DotnetPath=[IO.Path]::GetFullPath($DotnetPath);$VerifierDll=[IO.Path]::GetFullPath($VerifierDll)
if($RawFramesPath){$RawFramesPath=[IO.Path]::GetFullPath($RawFramesPath)}
if($LegacyRawFramesPath){$LegacyRawFramesPath=[IO.Path]::GetFullPath($LegacyRawFramesPath)}
$RunRoot=[IO.Path]::GetFullPath($RunRoot)
if(Test-Path -LiteralPath $RunRoot){throw 'RunRoot exists; refusing to overwrite verifier evidence.'}
foreach($pinned in @($DotnetPath,$VerifierDll)){if(-not(Test-Path -LiteralPath $pinned -PathType Leaf)){throw "Pinned input is missing: $pinned"}}
if($RawFramesPath -and -not(Test-Path -LiteralPath $RawFramesPath -PathType Leaf)){throw "Pinned raw capture is missing: $RawFramesPath"}
if($LegacyRawFramesPath -and -not(Test-Path -LiteralPath $LegacyRawFramesPath -PathType Leaf)){throw 'Read-only legacy capture path is missing.'}
New-Item -ItemType Directory -Path $RunRoot | Out-Null
$script:Results = [System.Collections.Generic.List[object]]::new()
$script:StartedAt = [DateTime]::UtcNow

function ConvertTo-WindowsArgument([string]$Value){
    if($null -eq $Value){return '""'}
    $builder=[Text.StringBuilder]::new();[void]$builder.Append('"');$slashes=0
    foreach($character in $Value.ToCharArray()){
        if($character -eq '\'){$slashes++;continue}
        if($character -eq '"'){
            if($slashes -gt 0){[void]$builder.Append(('\' * ($slashes*2)))}
            [void]$builder.Append('\"');$slashes=0;continue
        }
        if($slashes -gt 0){[void]$builder.Append(('\' * $slashes));$slashes=0}
        [void]$builder.Append($character)
    }
    if($slashes -gt 0){[void]$builder.Append(('\' * ($slashes*2)))}
    [void]$builder.Append('"');return $builder.ToString()
}

function Write-StableText([string]$Path,[string]$Value){
    $temporary="$Path.tmp"
    [IO.File]::WriteAllText($temporary,$Value,(New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $temporary -Destination $Path
}

function Invoke-BoundedProcess{
    param([string]$Name,[string]$FilePath,[string[]]$Arguments,[int]$TimeoutMs,[string]$CancelFile,[int]$CancelAfterMs=0,[string]$WorkingDirectory=(Get-Location).Path)
    $caseRoot=Join-Path $RunRoot $Name
    New-Item -ItemType Directory -Path $caseRoot | Out-Null
    $psi=[Diagnostics.ProcessStartInfo]::new();$psi.FileName=$FilePath
    $psi.Arguments=(($Arguments|ForEach-Object{ConvertTo-WindowsArgument $_}) -join ' ')
    $psi.WorkingDirectory=$WorkingDirectory;$psi.UseShellExecute=$false;$psi.CreateNoWindow=$true
    $psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true
    $process=[Diagnostics.Process]::new();$process.StartInfo=$psi
    $started=$false;$outTask=$null;$errTask=$null;$state='START_FAILED';$exitCode=$null;$failure=$null;$cancelRequested=$false;$timedOut=$false;$forcedStop=$false
    $identity=$null;$stdout='';$stderr='';$outDrained=$false;$errDrained=$false
    try{
        if(-not $process.Start()){throw "Process $Name did not start."};$started=$true
        $identity=[ordered]@{pid=$process.Id;startUtc=$process.StartTime.ToUniversalTime().ToString('o')}
        $outTask=$process.StandardOutput.ReadToEndAsync();$errTask=$process.StandardError.ReadToEndAsync()
        $watch=[Diagnostics.Stopwatch]::StartNew()
        while(-not $process.WaitForExit(100)){
            if($CancelAfterMs -gt 0 -and -not $cancelRequested -and $watch.ElapsedMilliseconds -ge $CancelAfterMs){
                [IO.File]::WriteAllText($CancelFile,("cancel"+[Environment]::NewLine),(New-Object Text.UTF8Encoding($false)))
            }
            if($CancelFile -and (Test-Path -LiteralPath $CancelFile)){
                $cancelRequested=$true
                if($process.WaitForExit(2500)){break}
                $failure='cooperative cancel grace expired; terminating the task-owned process'
                $process.Kill();$forcedStop=$true
                if(-not $process.WaitForExit(5000)){$state='UNKNOWN';$failure='owned process did not stop after bounded cancellation';break}
                break
            }
            if($watch.ElapsedMilliseconds -ge $TimeoutMs){
                $timedOut=$true;$failure="process exceeded ${TimeoutMs}ms timeout"
                if($process.CloseMainWindow()){$process.WaitForExit(1000)|Out-Null}
                if(-not $process.HasExited){$process.Kill();$forcedStop=$true}
                if(-not $process.WaitForExit(5000)){$state='UNKNOWN';$failure='owned process did not stop after bounded timeout';break}
                break
            }
        }
        if($process.HasExited){$exitCode=$process.ExitCode;if($state -ne 'UNKNOWN'){$state=if($cancelRequested){'CANCELLED'}elseif($timedOut){'TIMED_OUT'}else{'EXITED'}}}
        if($state -ne 'UNKNOWN'){
            $outDrained=$outTask.Wait(5000);$errDrained=$errTask.Wait(5000)
            if(-not($outDrained -and $errDrained)){$state='UNKNOWN';$failure='redirected output streams did not reach EOF after process exit'}
            else{$stdout=$outTask.GetAwaiter().GetResult();$stderr=$errTask.GetAwaiter().GetResult()}
        }
    }catch{$failure=$_.Exception.Message;if($state -ne 'UNKNOWN'){$state='FAILED'}}
    finally{
        if($started -and $process.HasExited -and $state -ne 'UNKNOWN'){
            if($null -ne $outTask -and $outTask.IsCompleted){$stdout=$outTask.GetAwaiter().GetResult();$outDrained=$true}
            if($null -ne $errTask -and $errTask.IsCompleted){$stderr=$errTask.GetAwaiter().GetResult();$errDrained=$true}
        }
        if($state -ne 'UNKNOWN'){
            Write-StableText (Join-Path $caseRoot 'stdout.log') $stdout
            Write-StableText (Join-Path $caseRoot 'stderr.log') $stderr
            Write-StableText (Join-Path $caseRoot 'native-exit.txt') ("nativeExit=$exitCode`n")
        }
        $record=[ordered]@{name=$Name;state=$state;nativeExit=$exitCode;identity=$identity;stopPath=if($forcedStop){'OWNED_PROCESS_TERMINATED_AFTER_GRACE'}elseif($cancelRequested){'COOPERATIVE_CANCEL'}elseif($timedOut){'TIMEOUT_EXITED_NORMALLY'}else{'NATIVE_EXIT'};elapsedMs=if($null -ne $watch){[int]$watch.ElapsedMilliseconds}else{0};
            stdoutChars=$stdout.Length;stderrChars=$stderr.Length;stdoutDrained=$outDrained;stderrDrained=$errDrained;failure=$failure}
        $script:Results.Add($record)
        $process.Dispose()
    }
    if($state -eq 'UNKNOWN'){throw "Process $Name has UNKNOWN stop state; pid=$($identity.pid). $failure"}
    if($state -eq 'FAILED'){throw "Process $Name failed to execute: $failure"}
    return [pscustomobject]@{Name=$Name;State=$state;NativeExit=$exitCode;Stdout=$stdout;Stderr=$stderr;ElapsedMs=$record.elapsedMs}
}

function Invoke-Verifier([string]$Name,[string]$InputPath,[bool]$ExpectSuccess){
    $run=Invoke-BoundedProcess -Name $Name -FilePath $DotnetPath -Arguments @($VerifierDll,'verify',$InputPath) -TimeoutMs $ProcessTimeoutMs -WorkingDirectory (Split-Path -Parent $VerifierDll)
    if($ExpectSuccess -and $run.NativeExit -ne 0){throw "Verifier $Name expected native 0, got $($run.NativeExit). $($run.Stderr)"}
    if(-not $ExpectSuccess -and $run.NativeExit -eq 0){throw "Verifier $Name accepted an invalid raw capture."}
    return $run
}

$outcome='FAIL';$summaryPath=Join-Path $RunRoot 'summary.json'
try{
    $fixtureRoot=Join-Path $RunRoot 'wirecodec-fixtures'
    $self=Invoke-BoundedProcess -Name 'self-test' -FilePath $DotnetPath -Arguments @($VerifierDll,'self-test',$fixtureRoot) -TimeoutMs $ProcessTimeoutMs -WorkingDirectory (Split-Path -Parent $VerifierDll)
    if($self.NativeExit -ne 0 -or $self.Stdout -notmatch 'SELF_TEST_PASS positive=2 negative=29 serializedFrames=5 source=WireCodec'){throw "WireCodec self-test failed or did not run the fixed genuine mutation set. native=$($self.NativeExit) $($self.Stderr)"}

    $validPath=Join-Path $fixtureRoot 'valid.json'
    $valid=Invoke-Verifier 'wirecodec-valid-positive' $validPath $true
    if($valid.Stdout -notmatch 'PRIVACY_MATRIX_PASS frames=5 sha256=[0-9a-f]{64}'){throw 'Valid WireCodec fixture did not produce the verifier pass marker.'}
    [void](Invoke-Verifier 'wirecodec-empty-genesis-push-positive' (Join-Path $fixtureRoot 'valid-empty-genesis-push.json') $true)

    $negativeNames=@('raw-empty','raw-invalid-one-byte','cross-seat-raw-summary-unchanged','hand-count-summary-rewritten','missing-welcome','welcome-record-is-not-projection','truncated-projection-wire',
        'zero-version-without-valid-genesis','public-private-summary','unknown-field-summary-false',
        'wrong-welcome-session','wrong-welcome-role','wrong-welcome-seat','wrong-welcome-envelope-session','wrong-welcome-envelope-binding','wrong-welcome-scope-id','wrong-welcome-scope-role','wrong-welcome-scope-room',
        'wrong-welcome-version','wrong-welcome-owner-epoch','wrong-welcome-binding-epoch','wrong-projection-owner',
        'wrong-projection-audience','wrong-projection-message-type','unknown-projection-message-type','wrong-projection-version','projection-unknown-field','welcome-unknown-field','public-nested-unknown-field')
    foreach($caseName in $negativeNames){$casePath=Join-Path $fixtureRoot "$caseName.json";if(-not(Test-Path -LiteralPath $casePath -PathType Leaf)){throw "Required serialized negative fixture is missing: $caseName"};[void](Invoke-Verifier "negative-$caseName" $casePath $false)}

    $realFrameCount=0;$realCaptureSha256=$null;$tampered=$null
    if($RawFramesPath){
        $real=Invoke-Verifier 'real-capture' $RawFramesPath $true
        if($real.Stdout -notmatch 'PRIVACY_MATRIX_PASS frames=([1-9][0-9]*) sha256=([0-9a-f]{64})'){throw 'Real verifier did not report a typed frame count and capture hash.'}
        $realFrameCount=[int]$Matches[1];$realCaptureSha256=$Matches[2]
        $capture=ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($RawFramesPath,[Text.Encoding]::UTF8))
        if($capture.schemaVersion -ne 2 -or @($capture.frames).Count -lt 1){throw 'Raw capture schema is empty, legacy, or unsupported.'}
        $tamperedCapture=$capture|ConvertTo-Json -Depth 40|ConvertFrom-Json
        $tamperedCapture.frames[0].wireSha256='0000000000000000000000000000000000000000000000000000000000000000'
        $tamperedPath=Join-Path $RunRoot 'tampered-capture.json'
        [IO.File]::WriteAllText($tamperedPath,($tamperedCapture|ConvertTo-Json -Depth 40),(New-Object Text.UTF8Encoding($false)))
        $tampered=Invoke-Verifier 'tampered-real-capture-negative' $tamperedPath $false
    }

    $probePath=Join-Path $RunRoot 'owned-process-probe.ps1'
    @'
param([string]$Mode,[string]$CancelPath)
if($Mode -eq 'large'){for($i=0;$i -lt 6000;$i++){[Console]::Out.WriteLine(('OUT'+('x'*120)));[Console]::Error.WriteLine(('ERR'+('y'*120)))};exit 0}
if($Mode -eq 'cancel'){while(-not(Test-Path -LiteralPath $CancelPath)){Start-Sleep -Milliseconds 40};[Console]::Out.WriteLine('COOPERATIVE_CANCEL_ACK');exit 23}
if($Mode -eq 'timeout' -or $Mode -eq 'ignore-cancel'){while($true){Start-Sleep -Milliseconds 100}}
if($Mode -eq 'native-failure'){[Console]::Error.WriteLine('EXPECTED_NATIVE_FAILURE');exit 37}
exit 90
'@|Set-Content -LiteralPath $probePath -Encoding UTF8
    $powershell=(Get-Process -Id $PID).Path
    $large=Invoke-BoundedProcess -Name 'owned-large-dual-stream' -FilePath $powershell -Arguments @('-NoProfile','-File',$probePath,'large','') -TimeoutMs 15000 -WorkingDirectory $RunRoot
    if($large.NativeExit -ne 0 -or $large.Stdout.Length -lt 500000 -or $large.Stderr.Length -lt 500000){throw 'Large simultaneous stdout/stderr drain was incomplete or returned nonzero.'}
    $nativeFailure=Invoke-BoundedProcess -Name 'owned-native-nonzero' -FilePath $powershell -Arguments @('-NoProfile','-File',$probePath,'native-failure','') -TimeoutMs 5000 -WorkingDirectory $RunRoot
    if($nativeFailure.NativeExit -ne 37 -or $nativeFailure.Stderr -notmatch 'EXPECTED_NATIVE_FAILURE'){throw 'Native nonzero exit or stderr did not propagate from the owned child process.'}
    $cancelPath=Join-Path $RunRoot 'cancel.request'
    $cancel=Invoke-BoundedProcess -Name 'owned-cooperative-cancel' -FilePath $powershell -Arguments @('-NoProfile','-File',$probePath,'cancel',$cancelPath) -TimeoutMs 10000 -CancelFile $cancelPath -CancelAfterMs 500 -WorkingDirectory $RunRoot
    if($cancel.State -ne 'CANCELLED' -or $cancel.NativeExit -ne 23 -or $cancel.Stdout -notmatch 'COOPERATIVE_CANCEL_ACK'){throw 'Cooperative cancellation did not stop and drain the owned process.'}
    $cancelIdentity=$script:Results|Where-Object name -eq 'owned-cooperative-cancel'|Select-Object -First 1
    if(Get-Process -Id $cancelIdentity.identity.pid -ErrorAction SilentlyContinue){throw 'Owned process is still running after cooperative cancellation.'}
    $refuseCancelPath=Join-Path $RunRoot 'cancel.refused.request'
    $refuseCancel=Invoke-BoundedProcess -Name 'owned-cancel-refused' -FilePath $powershell -Arguments @('-NoProfile','-File',$probePath,'ignore-cancel',$refuseCancelPath) -TimeoutMs 10000 -CancelFile $refuseCancelPath -CancelAfterMs 500 -WorkingDirectory $RunRoot
    if($refuseCancel.State -ne 'CANCELLED' -or $null -eq $refuseCancel.NativeExit){throw 'Refused cooperative cancellation did not reach a bounded owned-process stop.'}
    $refuseIdentity=$script:Results|Where-Object name -eq 'owned-cancel-refused'|Select-Object -First 1
    if(Get-Process -Id $refuseIdentity.identity.pid -ErrorAction SilentlyContinue){throw 'Owned process is still running after rejected cancellation cleanup.'}
    $timeout=Invoke-BoundedProcess -Name 'owned-timeout' -FilePath $powershell -Arguments @('-NoProfile','-File',$probePath,'timeout','') -TimeoutMs 1200 -WorkingDirectory $RunRoot
    if($timeout.State -ne 'TIMED_OUT'){throw "Owned process timeout was misclassified: $($timeout.State)"}
    $timeoutIdentity=$script:Results|Where-Object name -eq 'owned-timeout'|Select-Object -First 1
    if(Get-Process -Id $timeoutIdentity.identity.pid -ErrorAction SilentlyContinue){throw 'Owned process is still running after timeout cleanup.'}
    foreach($requiredLog in @('stdout.log','stderr.log','native-exit.txt')){if(-not(Test-Path -LiteralPath (Join-Path $RunRoot "owned-timeout/$requiredLog") -PathType Leaf)){throw 'Timed out process did not receive stable output and exit finalization.'}}

    $legacyResult=$null
    if($LegacyRawFramesPath){$legacy=Invoke-Verifier 'legacy-no-welcome-sample-negative' $LegacyRawFramesPath $false;$legacyResult=[ordered]@{state='REJECTED_AS_EXPECTED';nativeExit=$legacy.NativeExit;sha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $LegacyRawFramesPath).Hash}}

    $outcome='PASS'
    $summary=[ordered]@{schemaVersion=2;result=$outcome;startedUtc=$script:StartedAt.ToString('o');finishedUtc=[DateTime]::UtcNow.ToString('o');
        positive='WireCodec typed Welcome + 4 player snapshots + 1 public snapshot';negativeCount=$negativeNames.Count;negativeNames=$negativeNames;
        realCapture=if($RawFramesPath){'PASS'}else{'NOT_RUN_NO_CURRENT_APPLICATION_CAPTURE'};
        realFrameCount=$realFrameCount;realCaptureSha256=$realCaptureSha256;tamperedCaptureNativeExit=$tampered.NativeExit;
        largeOutput=@{nativeExit=$large.NativeExit;stdoutChars=$large.Stdout.Length;stderrChars=$large.Stderr.Length};
        cancellation=@{nativeExit=$cancel.NativeExit;state=$cancel.State;processStopped=$true};cancelRefused=@{nativeExit=$refuseCancel.NativeExit;state=$refuseCancel.State;processStopped=$true};
        nativeFailure=@{nativeExit=$nativeFailure.NativeExit;stderr=$nativeFailure.Stderr};timeout=@{nativeExit=$timeout.NativeExit;state=$timeout.State;processStopped=$true};
        legacySample=$legacyResult;processes=@($script:Results)}
    Write-StableText $summaryPath ($summary|ConvertTo-Json -Depth 12)
    $manifest=[ordered]@{}
    Get-ChildItem -LiteralPath $RunRoot -File -Recurse|Where-Object {$_.FullName -ne (Join-Path $RunRoot 'stable-manifest.json')}|ForEach-Object{
        $relative=$_.FullName.Substring($RunRoot.Length).TrimStart('\');$manifest[$relative]=(Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash
    }
    Write-StableText (Join-Path $RunRoot 'stable-manifest.json') ($manifest|ConvertTo-Json -Depth 8)
    Write-Output "C04_PRIVACY_MATRIX_TESTS_PASS frames=$realFrameCount rawSha256=$realCaptureSha256 negatives=$($negativeNames.Count) largeOut=$($large.Stdout.Length) largeErr=$($large.Stderr.Length) cancelExit=$($cancel.NativeExit) cancelRefusedExit=$($refuseCancel.NativeExit) nativeFailure=$($nativeFailure.NativeExit) timeoutState=$($timeout.State)"
}catch{
    $outcome='FAIL';$failureSummary=[ordered]@{schemaVersion=2;result=$outcome;startedUtc=$script:StartedAt.ToString('o');finishedUtc=[DateTime]::UtcNow.ToString('o');error=$_.Exception.Message;processes=@($script:Results)}
    try{Write-StableText $summaryPath ($failureSummary|ConvertTo-Json -Depth 12)}catch{}
    try{
        $failureManifest=[ordered]@{}
        Get-ChildItem -LiteralPath $RunRoot -File -Recurse|Where-Object {$_.FullName -ne (Join-Path $RunRoot 'stable-manifest.json')}|ForEach-Object{
            $relative=$_.FullName.Substring($RunRoot.Length).TrimStart('\');$failureManifest[$relative]=(Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash
        }
        Write-StableText (Join-Path $RunRoot 'stable-manifest.json') ($failureManifest|ConvertTo-Json -Depth 8)
    }catch{}
    throw
}
