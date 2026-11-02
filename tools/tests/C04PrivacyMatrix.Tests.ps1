#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$DotnetPath,
    [Parameter(Mandatory=$true)][string]$VerifierDll,
    [Parameter(Mandatory=$true)][string]$RawFramesPath,
    [Parameter(Mandatory=$true)][string]$RunRoot
)
$ErrorActionPreference='Stop'
$RunRoot=[IO.Path]::GetFullPath($RunRoot)
if(Test-Path -LiteralPath $RunRoot){throw 'RunRoot exists; refusing to overwrite verifier evidence.'}
if(-not (Test-Path -LiteralPath $DotnetPath -PathType Leaf) -or -not (Test-Path -LiteralPath $VerifierDll -PathType Leaf) -or -not (Test-Path -LiteralPath $RawFramesPath -PathType Leaf)){throw 'Pinned verifier, dotnet host, or raw capture is missing.'}
New-Item -ItemType Directory -Path $RunRoot|Out-Null
function Quote([string]$value){'"'+$value.Replace('"','\"')+'"'}
function RunVerifier([string]$name,[string[]]$arguments,[bool]$expectSuccess){
    $psi=New-Object Diagnostics.ProcessStartInfo
    $psi.FileName=$DotnetPath;$psi.Arguments=((@($VerifierDll)+$arguments)|ForEach-Object{Quote $_}) -join ' ';$psi.WorkingDirectory=(Split-Path -Parent $VerifierDll)
    $psi.UseShellExecute=$false;$psi.CreateNoWindow=$true;$psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true
    $p=New-Object Diagnostics.Process;$p.StartInfo=$psi;if(-not $p.Start()){throw "Verifier $name did not start."}
    $stdout=$p.StandardOutput.ReadToEnd();$stderr=$p.StandardError.ReadToEnd();$p.WaitForExit();$code=$p.ExitCode
    [IO.File]::WriteAllText((Join-Path $RunRoot "$name.stdout.log"),$stdout,[Text.Encoding]::UTF8)
    [IO.File]::WriteAllText((Join-Path $RunRoot "$name.stderr.log"),$stderr,[Text.Encoding]::UTF8)
    [IO.File]::WriteAllText((Join-Path $RunRoot "$name.exit"),"nativeExit=$code`n",[Text.Encoding]::UTF8)
    if($expectSuccess -and $code -ne 0){throw "Verifier $name expected native 0, got $code. $stderr"}
    if(-not $expectSuccess -and $code -eq 0){throw "Verifier $name accepted a deliberately corrupted raw capture."}
    return [pscustomobject]@{name=$name;nativeExit=$code;stdout=$stdout;stderr=$stderr}
}
$self=RunVerifier 'self-test' @('self-test','unused.json') $true
if($self.stdout -notmatch 'SELF_TEST_PASS positive=1 negative=5'){throw 'Verifier self-test did not report the required positive/negative matrix.'}
$real=RunVerifier 'real-capture' @('verify',$RawFramesPath) $true
if($real.stdout -notmatch 'PRIVACY_MATRIX_PASS frames=([1-9][0-9]*) sha256=([0-9a-f]{64})'){throw 'Real verifier did not report a typed frame count and capture hash.'}
$capture=ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($RawFramesPath,[Text.Encoding]::UTF8))
if($capture.schemaVersion -ne 1 -or @($capture.frames).Count -lt 1){throw 'Raw capture schema is empty or unsupported.'}
$capture.frames[0].wireSha256='0000000000000000000000000000000000000000000000000000000000000000'
$tampered=Join-Path $RunRoot 'tampered-raw-frames.json'
[IO.File]::WriteAllText($tampered,($capture|ConvertTo-Json -Depth 30),(New-Object Text.UTF8Encoding($false)))
$negative=RunVerifier 'tampered-capture-negative' @('verify',$tampered) $false
$summary=[ordered]@{schemaVersion=1;result='PASS';selfTest='positive=1,negative=5';realFrameCount=[int]$Matches[1];realCaptureSha256=$Matches[2];tamperedHashNativeExit=$negative.nativeExit;tamperedHashRejected=$true}
[IO.File]::WriteAllText((Join-Path $RunRoot 'summary.json'),($summary|ConvertTo-Json -Depth 5),(New-Object Text.UTF8Encoding($false)))
Write-Output "C04_PRIVACY_MATRIX_TESTS_PASS frames=$($summary.realFrameCount) rawSha256=$($summary.realCaptureSha256) tamperedExit=$($summary.tamperedHashNativeExit)"
