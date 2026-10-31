#Requires -Version 5.1
[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][ValidateSet('Devices','Install','Start','Stop','Capture','DeviceInfo')][string]$Action,
  [string]$Serial,[string]$Apk,[string]$EvidenceDir,[string]$PackageId='com.majiang.game',[ValidateRange(1,600)][int]$AdbTimeoutSeconds=30
)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$android=if($env:C05_ADB_PATH){[IO.Path]::GetFullPath($env:C05_ADB_PATH)}else{'D:\Unity\Hub\Editor\6000.3.27f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe'}
if(-not(Test-Path -LiteralPath $android -PathType Leaf)){throw 'BLOCKED: pinned Unity Android SDK adb.exe is unavailable.'}

function ConvertTo-C05WindowsArgument([string]$Value) {
  $builder=New-Object Text.StringBuilder;[void]$builder.Append('"');$slashes=0
  foreach($ch in $Value.ToCharArray()){
    if($ch -eq [char]92){$slashes++;continue}
    if($ch -eq [char]34){for($i=0;$i -lt (2*$slashes+1);$i++){[void]$builder.Append([char]92)};[void]$builder.Append([char]34);$slashes=0;continue}
    for($i=0;$i -lt $slashes;$i++){[void]$builder.Append([char]92)};$slashes=0;[void]$builder.Append($ch)
  }
  for($i=0;$i -lt (2*$slashes);$i++){[void]$builder.Append([char]92)};[void]$builder.Append('"');return $builder.ToString()
}
function Stop-C05AdbProcessIfOwned([int]$ProcessId,[long]$StartTicks){
  $p=Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
  if(-not $p -or $p.StartTime.ToUniversalTime().Ticks -ne $StartTicks){return $false}
  $p.Kill();$p.WaitForExit();return $true
}
function Start-C05AdbProcess([string[]]$AdbArgs){
  $psi=New-Object Diagnostics.ProcessStartInfo;$psi.FileName=$android;$psi.Arguments=(@($AdbArgs|ForEach-Object{ConvertTo-C05WindowsArgument ([string]$_)}) -join ' ')
  $psi.UseShellExecute=$false;$psi.CreateNoWindow=$true;$psi.WindowStyle='Hidden';$psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true
  $proc=New-Object Diagnostics.Process;$proc.StartInfo=$psi;[void]$proc.Start();return $proc
}
function Invoke-C05Adb([string[]]$AdbArgs,[int]$TimeoutSeconds=$AdbTimeoutSeconds){
  $proc=Start-C05AdbProcess $AdbArgs;$pidOwn=$proc.Id;$ticks=$proc.StartTime.ToUniversalTime().Ticks
  $outTask=$proc.StandardOutput.ReadToEndAsync();$errTask=$proc.StandardError.ReadToEndAsync()
  $exited=$proc.WaitForExit($TimeoutSeconds*1000);$killed=$false
  if(-not $exited){$killed=Stop-C05AdbProcessIfOwned $pidOwn $ticks;[void]$proc.WaitForExit(2000)}
  $drained=[Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($outTask,$errTask),[TimeSpan]::FromSeconds(3))
  $stdout=if($outTask.IsCompleted){$outTask.Result}else{''};$stderr=if($errTask.IsCompleted){$errTask.Result}else{''}
  $native=if($exited -and $proc.HasExited){$proc.ExitCode}else{$null}
  [pscustomobject]@{exit=$native;timedOut=(-not $exited);timeoutSeconds=$TimeoutSeconds;pid=$pidOwn;startUtcTicks=$ticks;killedOwnedProcess=$killed;drainComplete=$drained;stdout=$stdout;stderr=$stderr;status=if(-not $exited){'TIMEOUT'}elseif(-not $drained){'OUTPUT_DRAIN_TIMEOUT'}elseif($native -eq 0){'PASS'}else{'FAIL'}}
}
function Invoke-C05AdbBinary([string[]]$AdbArgs,[int]$TimeoutSeconds=$AdbTimeoutSeconds){
  $proc=Start-C05AdbProcess $AdbArgs;$pidOwn=$proc.Id;$ticks=$proc.StartTime.ToUniversalTime().Ticks;$memory=New-Object IO.MemoryStream
  $outTask=$proc.StandardOutput.BaseStream.CopyToAsync($memory);$errTask=$proc.StandardError.ReadToEndAsync()
  $exited=$proc.WaitForExit($TimeoutSeconds*1000);$killed=$false
  if(-not $exited){$killed=Stop-C05AdbProcessIfOwned $pidOwn $ticks;[void]$proc.WaitForExit(2000)}
  $drained=[Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($outTask,$errTask),[TimeSpan]::FromSeconds(3))
  $stderr=if($errTask.IsCompleted){$errTask.Result}else{''};$bytes=$memory.ToArray();$native=if($exited -and $proc.HasExited){$proc.ExitCode}else{$null}
  $memory.Dispose();[pscustomobject]@{exit=$native;timedOut=(-not $exited);timeoutSeconds=$TimeoutSeconds;pid=$pidOwn;startUtcTicks=$ticks;killedOwnedProcess=$killed;drainComplete=$drained;stderr=$stderr;bytes=$bytes;status=if(-not $exited){'TIMEOUT'}elseif(-not $drained){'OUTPUT_DRAIN_TIMEOUT'}elseif($native -eq 0){'PASS'}else{'FAIL'}}
}
function Get-C05ActionEvidenceDir {
  $buildRoot=[IO.Path]::GetFullPath((Join-Path $root 'build'))+[IO.Path]::DirectorySeparatorChar
  if($EvidenceDir){$parent=[IO.Path]::GetFullPath($EvidenceDir);if(-not $parent.StartsWith($buildRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Refusing evidence output outside this worktree build directory.'};if(-not(Test-Path -LiteralPath $parent)){New-Item -ItemType Directory -Force -Path $parent|Out-Null};$dir=Join-Path $parent ((Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8))}
  else{$dir=Join-Path $root ('build\p45-android-game-device\'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8))}
  if(-not $dir.StartsWith($buildRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Refusing evidence output outside this worktree build directory.'}
  if(Test-Path -LiteralPath $dir){throw 'Refusing to reuse an existing device evidence directory.'}
  New-Item -ItemType Directory -Force -Path $dir|Out-Null;return $dir
}
function Save-C05Json([string]$Path,[object]$Value){[IO.File]::WriteAllText($Path,($Value|ConvertTo-Json -Depth 20),[Text.Encoding]::UTF8)}
function Get-C05RunBadgingContract([string]$Metadata){
  $packageLines=[regex]::Matches($Metadata,"(?m)^package:\s+name='(?<value>[^']+)'(?:\s|$).*$")
  $minLines=[regex]::Matches($Metadata,"(?m)^minSdkVersion:'(?<value>\d+)'\s*$");$targetLines=[regex]::Matches($Metadata,"(?m)^targetSdkVersion:'(?<value>\d+)'\s*$")
  $abiLines=[regex]::Matches($Metadata,'(?m)^native-code:\s*(?<value>[^\r\n]*)\s*$')
  if($packageLines.Count -ne 1 -or $minLines.Count -ne 1 -or $targetLines.Count -ne 1 -or $abiLines.Count -ne 1){throw 'FAIL: APK badging package/SDK/native-code records are missing or duplicated.'}
  $abiText=$abiLines[0].Groups['value'].Value;if(-not[regex]::IsMatch($abiText,"^'[A-Za-z0-9_.-]+'(?:\s+'[A-Za-z0-9_.-]+')*$")){throw 'FAIL: malformed APK native-code ABI record.'}
  $abis=@([regex]::Matches($abiText,"'(?<abi>[A-Za-z0-9_.-]+)'")|ForEach-Object{$_.Groups['abi'].Value})
  return [pscustomobject]@{package=$packageLines[0].Groups['value'].Value;minSdk=[int]$minLines[0].Groups['value'].Value;targetSdk=[int]$targetLines[0].Groups['value'].Value;abis=$abis}
}
function Get-C05RunAutoTargetSdk([object]$Platforms){
  $levels=@($Platforms|ForEach-Object{if([string]$_ -match '^android-(?<api>\d+)$'){[int]$Matches['api']}else{throw "FAIL: malformed accepted SDK platform entry '$_'."}})
  if(-not$levels.Count){throw 'FAIL: selected build evidence lacks a resolved automatic targetSdk platform.'}
  return [int](($levels|Measure-Object -Maximum).Maximum)
}
function Get-C05VerifiedArtifact([string]$Path,[string]$ExpectedPackage){
  if([string]::IsNullOrWhiteSpace($Path)){throw 'USAGE: this action requires -Apk bound to successful C05 build evidence.'}
  $apkPath=[IO.Path]::GetFullPath($Path)
  if([IO.Path]::GetExtension($apkPath) -ine '.apk' -or -not(Test-Path -LiteralPath $apkPath -PathType Leaf)){throw 'FAIL: selected artifact must be an existing .apk file.'}
  $runDir=Split-Path $apkPath -Parent;$summaryPath=Join-Path $runDir 'summary.json';$reportPath=Join-Path $runDir 'build-report.json';$metadataPath=Join-Path $runDir 'apk-metadata.txt'
  foreach($required in @($summaryPath,$reportPath,$metadataPath)){if(-not(Test-Path -LiteralPath $required -PathType Leaf)){throw 'FAIL: selected APK lacks successful C05 build evidence.'}}
  $summary=Get-Content $summaryPath -Raw|ConvertFrom-Json;$report=Get-Content $reportPath -Raw|ConvertFrom-Json;$metadata=Get-Content $metadataPath -Raw
  $file=Get-Item $apkPath;$sha=(Get-FileHash $apkPath -Algorithm SHA256).Hash;$reportPackage=[string]$report.packageId
  if($summary.status -ne 'PASS' -or [int]$summary.nativeExit -ne 0 -or [long]$summary.apkBytes -ne [long]$file.Length -or [string]$summary.apkSha256 -ine $sha){throw 'FAIL: selected APK does not match a successful fresh build summary.'}
  $requiredScenes=@('Assets/Scenes/Boot.unity','Assets/Scenes/Menu.unity','Assets/Scenes/Lobby.unity','Assets/Scenes/Table.unity','Assets/Scenes/Spectator.unity','Assets/Scenes/Settlement.unity')
  if($report.result -ne 'Succeeded' -or [int]$report.errors -ne 0 -or $report.target -ne 'Android' -or $report.backend -ne 'IL2CPP' -or $report.architectures -ne 'ARM64' -or $reportPackage -ne $ExpectedPackage -or [int]$report.minSdkVersion -lt 21 -or -not $report.tmpResources -or -not $report.landscapeOnly -or -not $report.fullscreen -or -not $report.outsideSafeArea){throw 'FAIL: selected APK BuildReport/package/settings do not match requested Android game.'}
  $reportScenes=@($report.scenes);$summaryScenes=@($summary.buildReport.scenes)
  foreach($scene in $requiredScenes){if($reportScenes -notcontains $scene -or $summaryScenes -notcontains $scene){throw "FAIL: successful build evidence lacks required production scene '$scene'."}}
  if(-not $reportScenes.Count -or $reportScenes.Count -ne $summaryScenes.Count){throw 'FAIL: build summary/report scene set is empty or inconsistent.'};for($i=0;$i -lt $reportScenes.Count;$i++){if([string]$reportScenes[$i] -cne [string]$summaryScenes[$i]){throw 'FAIL: build summary/report scene identity mismatch.'}}
  if([bool]$summary.buildReport.tmpResources -ne [bool]$report.tmpResources -or [int]$summary.buildReport.minSdkVersion -ne [int]$report.minSdkVersion -or [int]$summary.buildReport.targetSdkSetting -ne [int]$report.targetSdkSetting -or [string]$summary.buildReport.architectures -cne [string]$report.architectures -or [string]$summary.buildReport.backend -cne [string]$report.backend){throw 'FAIL: build summary/report settings identity mismatch.'}
  if([string]$summary.buildReport.packageId -ne $reportPackage -or [string]$summary.sourceHead -notmatch '^[0-9a-fA-F]{40}$' -or [string]$report.output -ne $apkPath){throw 'FAIL: build summary/report identity mismatch.'}
  $expectedInputs=@('unity/Mahjong.Unity/Assets/Editor/C05FullGameAndroidBuild.cs','unity/Mahjong.Unity/Assets/Editor/C05FullGameAndroidBuild.cs.meta','unity/Mahjong.Unity/ProjectSettings/EditorBuildSettings.asset','unity/Mahjong.Unity/ProjectSettings/ProjectSettings.asset')
  foreach($input in $expectedInputs){$inputProperty=$summary.inputSha256.productionInputs.PSObject.Properties[$input];$inputHash=if($inputProperty){[string]$inputProperty.Value}else{''};if($inputHash -notmatch '^[0-9A-Fa-f]{64}$'){throw "FAIL: selected build evidence lacks production input hash for $input."}}
  if([string]$summary.toolchainManifestSha256 -cne 'E4785B1BE9FD4CE158E6730C1F7E01C58A7C974D45A407EF7E9CC215F3BEE934'){throw 'FAIL: selected artifact was not built with accepted P2.2 toolchain manifest.'}
  $badging=Get-C05RunBadgingContract $metadata;$autoTarget=Get-C05RunAutoTargetSdk $summary.androidToolchain.sdkPackages.platforms;$expectedTarget=if([int]$report.targetSdkSetting -gt 0){[int]$report.targetSdkSetting}else{$autoTarget}
  if($badging.package -cne $ExpectedPackage -or $badging.abis.Count -ne 1 -or $badging.abis[0] -cne 'arm64-v8a' -or $badging.minSdk -lt 21 -or $badging.minSdk -ne [int]$report.minSdkVersion -or $badging.targetSdk -ne $expectedTarget -or $badging.targetSdk -lt $badging.minSdk){throw 'FAIL: APK metadata/package ABI/SDK contradicts BuildReport or accepted SDK intent.'}
  [pscustomobject]@{apkPath=$apkPath;apkName=(Split-Path $apkPath -Leaf);sha256=$sha;bytes=$file.Length;sourceHead=[string]$summary.sourceHead;packageId=$reportPackage;buildSummarySha256=(Get-FileHash $summaryPath -Algorithm SHA256).Hash;buildReportSha256=(Get-FileHash $reportPath -Algorithm SHA256).Hash;metadataSha256=(Get-FileHash $metadataPath -Algorithm SHA256).Hash;runDir=$runDir}
}
function Save-C05AdbResult([string]$Dir,[string]$Name,[object]$Result){Save-C05Json (Join-Path $Dir $Name) $Result}

$devices=Invoke-C05Adb @('devices','-l')
if($devices.status -ne 'PASS'){$d=Get-C05ActionEvidenceDir;Save-C05AdbResult $d 'device-list.json' $devices;throw "FAIL: adb devices exit=$($devices.exit) status=$($devices.status) stderr=$($devices.stderr); raw result saved."}
$deviceListing=[string]$devices.stdout
$entries=@($deviceListing -split "`r?`n"|ForEach-Object{if($_ -match '^([^ ]+)\s+(device|unauthorized|offline)\b'){[pscustomobject]@{serial=$Matches[1];state=$Matches[2]}}})
$authorized=@($entries|Where-Object state -eq 'device')
$deviceEvidence=Get-C05ActionEvidenceDir;Save-C05AdbResult $deviceEvidence 'device-list.json' ([pscustomobject]@{status=$devices.status;nativeExit=$devices.exit;stdout=$devices.stdout;stderr=$devices.stderr;devices=$entries})
if($Action -eq 'Devices'){$deviceListing;if($authorized.Count -eq 0){if(@($entries|Where-Object state -eq 'offline').Count){Write-Host 'DEVICE_STATUS=BLOCKED_OFFLINE'}elseif(@($entries|Where-Object state -eq 'unauthorized').Count){Write-Host 'DEVICE_STATUS=BLOCKED_UNAUTHORIZED'}else{Write-Host 'DEVICE_STATUS=NOT_RUN_NO_DEVICE'}}else{Write-Host "DEVICE_STATUS=AVAILABLE authorized=$($authorized.Count)"};Write-Host 'DEVICE_EVIDENCE=build-relative';exit 0}
if(-not $Serial){
  if($authorized.Count -eq 1){$Serial=$authorized[0].serial}
  elseif($authorized.Count -gt 1){throw 'BLOCKED: multiple authorized devices; pass -Serial explicitly.'}
  elseif(@($entries|Where-Object state -eq 'offline').Count){throw "BLOCKED: offline device(s) present; no authorized device available. raw device listing saved; devices=$deviceListing"}
  elseif(@($entries|Where-Object state -eq 'unauthorized').Count){throw "BLOCKED: unauthorized device(s) present; no device action taken. raw device listing saved; devices=$deviceListing"}
  else{throw "NOT_RUN: no device is connected. raw device listing saved under build-relative evidence."}
}
if($authorized.serial -notcontains $Serial){$state=($entries|Where-Object serial -eq $Serial|Select-Object -First 1).state;if($state -eq 'offline'){throw "BLOCKED: serial '$Serial' is offline; no device action taken. raw device listing saved."};if($state -eq 'unauthorized'){throw "BLOCKED: serial '$Serial' is unauthorized; no device action taken. raw device listing saved."};throw "BLOCKED: serial '$Serial' is not an authorized device. raw device listing saved."}
if($PackageId -ne 'com.majiang.game'){throw 'FAIL: package id is fixed to the C05 production game package com.majiang.game.'}

$artifact=$null
if($Action -in @('Install','Start','Stop','Capture')){$artifact=Get-C05VerifiedArtifact $Apk $PackageId}
$dir=Get-C05ActionEvidenceDir
if($Action -eq 'DeviceInfo'){
  $observations=New-Object System.Collections.Generic.List[object]
  foreach($args in @(@('shell','wm','size'),@('shell','wm','density'),@('shell','dumpsys','display'),@('shell','settings','get','system','user_rotation'),@('shell','dumpsys','window','displays'))){$r=Invoke-C05Adb (@('-s',$Serial)+$args);$observations.Add([pscustomobject]@{command=($args -join ' ');result=$r})}
  $failed=@($observations|Where-Object{$_.result.status -ne 'PASS'});$result=[pscustomobject]@{status=if($failed.Count){'FAIL'}else{'PASS'};serial=$Serial;deviceList=$deviceListing;observations=$observations.ToArray()};Save-C05AdbResult $dir 'device-info.json' $result
  if($failed.Count){throw "FAIL: $($failed.Count) required device-info adb command(s) failed; raw exits/stdout/stderr saved under build-relative evidence."};Write-Host 'DEVICE_INFO PASS';exit 0
}
$identity=[pscustomobject]@{apk=$artifact.apkName;apkSha256=$artifact.sha256;apkBytes=$artifact.bytes;buildHead=$artifact.sourceHead;packageId=$artifact.packageId;buildSummarySha256=$artifact.buildSummarySha256;buildReportSha256=$artifact.buildReportSha256;runtimeArtifactLinkage='UNVERIFIED_INSTALLED_PACKAGE'}
switch($Action){
  'Install' {
    $r=Invoke-C05Adb @('-s',$Serial,'install','-r',$artifact.apkPath);$result=[pscustomobject]@{status=$r.status;serial=$Serial;artifact=$identity;adb=$r};Save-C05AdbResult $dir 'install-result.json' $result
    if($r.status -ne 'PASS'){throw "Install failed status=$($r.status) exit=$($r.exit); raw stdout/stderr saved."};Write-Host 'INSTALL PASS; artifact SHA recorded';exit 0
  }
  'Start' {
    $launch=Invoke-C05Adb @('-s',$Serial,'shell','monkey','-p',$PackageId,'-c','android.intent.category.LAUNCHER','1');$pidResult=$null
    if($launch.status -eq 'PASS'){$pidResult=Invoke-C05Adb @('-s',$Serial,'shell','pidof',$PackageId)}
    $status=if($launch.status -ne 'PASS'){$launch.status}elseif($null -eq $pidResult -or $pidResult.status -ne 'PASS' -or [string]::IsNullOrWhiteSpace($pidResult.stdout)){'FAIL'}else{'PASS'}
    $result=[pscustomobject]@{status=$status;serial=$Serial;artifact=$identity;launch=$launch;pidof=$pidResult};Save-C05AdbResult $dir 'launch-result.json' $result
    if($status -ne 'PASS'){throw 'Launch or target pidof failed; native results saved.'};Write-Host 'START PASS; installed-package linkage remains UNVERIFIED';exit 0
  }
  'Stop' {
    $r=Invoke-C05Adb @('-s',$Serial,'shell','am','force-stop',$PackageId);$result=[pscustomobject]@{status=$r.status;serial=$Serial;artifact=$identity;adb=$r};Save-C05AdbResult $dir 'stop-result.json' $result
    if($r.status -ne 'PASS'){throw 'Stop failed; native results saved.'};Write-Host 'STOP PASS';exit 0
  }
  'Capture' {
    $stamp=Get-Date -Format 'yyyyMMdd-HHmmss';$png=Join-Path $dir "$stamp-screen.png";$stderrPath=Join-Path $dir "$stamp-screencap-stderr.txt"
    $screen=Invoke-C05AdbBinary @('-s',$Serial,'exec-out','screencap','-p');[IO.File]::WriteAllBytes($png,[byte[]]$screen.bytes);[IO.File]::WriteAllText($stderrPath,[string]$screen.stderr,[Text.Encoding]::UTF8)
    $pidResult=Invoke-C05Adb @('-s',$Serial,'shell','pidof',$PackageId);$logResult=if($pidResult.status -eq 'PASS' -and -not [string]::IsNullOrWhiteSpace($pidResult.stdout)){Invoke-C05Adb @('-s',$Serial,'logcat','-d','-v','threadtime','--pid',$pidResult.stdout.Trim())}else{$null}
    if($null -ne $logResult){[IO.File]::WriteAllText((Join-Path $dir "$stamp-logcat.txt"),([string]$logResult.stdout+"`n[stderr]`n"+[string]$logResult.stderr),[Text.Encoding]::UTF8)}
    $deviceObservations=New-Object System.Collections.Generic.List[object]
    foreach($args in @(@('shell','wm','size'),@('shell','wm','density'),@('shell','settings','get','system','user_rotation'),@('shell','dumpsys','window','displays'))){$r=Invoke-C05Adb (@('-s',$Serial)+$args);$deviceObservations.Add([pscustomobject]@{command=($args -join ' ');result=$r})}
    $visual='UNASSESSED';try{Add-Type -AssemblyName System.Drawing;$bitmap=[Drawing.Bitmap]::FromFile($png);$nonBlack=$false;for($x=0;$x -lt $bitmap.Width -and -not $nonBlack;$x+=[Math]::Max(1,[int]($bitmap.Width/40))){for($y=0;$y -lt $bitmap.Height;$y+=[Math]::Max(1,[int]($bitmap.Height/40))){$c=$bitmap.GetPixel($x,$y);if($c.R -gt 8 -or $c.G -gt 8 -or $c.B -gt 8){$nonBlack=$true;break}}};$bitmap.Dispose();if(-not $nonBlack){$visual='BLACK_OR_NEAR_BLACK_REQUIRES_INVESTIGATION'}}catch{$visual='DECODE_FAILED_REQUIRES_INVESTIGATION'}
    $requiredFail=($screen.status -ne 'PASS' -or (Get-Item $png).Length -lt 256 -or $pidResult.status -ne 'PASS' -or -not $pidResult.stdout.Trim() -or $deviceObservations.Where({$_.result.status -ne 'PASS'}).Count -gt 0)
    $partial=($null -eq $logResult -or $logResult.status -ne 'PASS')
    $status=if($requiredFail){'FAIL'}elseif($partial){'PARTIAL'}else{'CAPTURED_REQUIRES_HUMAN_VISUAL_REVIEW'}
    $result=[pscustomobject]@{status=$status;serial=$Serial;artifact=$identity;screenshot=(Split-Path $png -Leaf);bytes=(Get-Item $png).Length;sha256=(Get-FileHash $png -Algorithm SHA256).Hash;visualSample=$visual;screenshotIsGamePass=$false;screencap=$screen;pidof=$pidResult;logcat=$logResult;deviceObservations=$deviceObservations.ToArray()}
    Save-C05AdbResult $dir "$stamp-capture-summary.json" $result
    if($requiredFail){throw 'Capture required observation(s) failed; per-command exits/stdout/stderr are saved.'};if($partial){throw 'Capture PARTIAL: package logcat was not obtained; screenshot remains unverified.'}
    Write-Host "CAPTURED bytes=$($result.bytes) SHA256=$($result.sha256); visual semantics require human review.";exit 0
  }
}
