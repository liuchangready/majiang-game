#Requires -Version 5.1
$ErrorActionPreference='Continue'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$runner=Join-Path $root 'tools\unity\run-p45-android-game.ps1'
$build=Join-Path $root 'tools\unity\build-p45-android-game.ps1'
$buildRoot=[IO.Path]::GetFullPath((Join-Path $root 'build'))+[IO.Path]::DirectorySeparatorChar
$runRoot=Join-Path $root ('build\p45-tool-tests\'+[guid]::NewGuid().ToString('N'))
if(Test-Path -LiteralPath $runRoot){throw 'Refusing to reuse test output directory.'}
New-Item -ItemType Directory -Path $runRoot|Out-Null
$ownsRunRoot=$true
$suitePassed=$false
$legacySentinelRoot=[IO.Path]::GetFullPath((Join-Path $root 'build\p45-android-game-device-tool-fixture'))
$ownsLegacySentinel=$false
if(-not $env:C05_TEST_SECOND_RUN){
  if(Test-Path -LiteralPath $legacySentinelRoot){throw 'Refusing existing legacy-output fixture; preserving caller-owned contents.'}
  New-Item -ItemType Directory -Path $legacySentinelRoot|Out-Null;$ownsLegacySentinel=$true
  [IO.File]::WriteAllText((Join-Path $legacySentinelRoot 'sentinel.txt'),'caller-owned sentinel')
}
$temp=Join-Path $runRoot 'fixtures';New-Item -ItemType Directory -Path $temp|Out-Null
$script:passed=New-Object System.Collections.Generic.List[string]
$script:foreignFixtureIdentity=$null
function Mark-C05Pass([string]$Name){$script:passed.Add($Name)}
function New-C05TestArtifact([string]$Dir,[string]$Package='com.majiang.game'){
  New-Item -ItemType Directory -Force -Path $Dir|Out-Null;$apk=Join-Path $Dir 'Mahjong.apk';$bytes=New-Object byte[] 512;for($i=0;$i -lt $bytes.Length;$i++){$bytes[$i]=[byte](($i*17)%251)};[IO.File]::WriteAllBytes($apk,$bytes)
  $report=[pscustomobject]@{result='Succeeded';errors=0;warnings=0;target='Android';backend='IL2CPP';architectures='ARM64';tmpResources=$true;minSdkVersion=25;targetSdkSetting=0;landscapeOnly=$true;fullscreen=$true;outsideSafeArea=$true;output=[IO.Path]::GetFullPath($apk);packageId=$Package;scenes=@('Assets/Scenes/Boot.unity','Assets/Scenes/Menu.unity','Assets/Scenes/Lobby.unity','Assets/Scenes/Table.unity','Assets/Scenes/Spectator.unity','Assets/Scenes/Settlement.unity')}
  $summary=[pscustomobject]@{status='PASS';nativeExit=0;apkBytes=$bytes.Length;apkSha256=(Get-FileHash $apk -Algorithm SHA256).Hash;sourceHead='fixture-source-head';buildReport=$report}
  $summary|ConvertTo-Json -Depth 8|Set-Content -Encoding UTF8 (Join-Path $Dir 'summary.json');$report|ConvertTo-Json -Depth 8|Set-Content -Encoding UTF8 (Join-Path $Dir 'build-report.json')
  "package: name='$Package' versionCode='1' minSdkVersion:'25' targetSdkVersion:'36'`nnative-code: 'arm64-v8a'"|Set-Content -Encoding ASCII (Join-Path $Dir 'apk-metadata.txt');return $apk
}
try {
  $fixture=Join-Path $temp 'adb-fixture.exe'
  $fixtureApk=Join-Path $temp 'ignored.apk'; [IO.File]::WriteAllBytes($fixtureApk,[byte[]]@(1,2,3))
  $fixtureSource=@'
using System; using System.IO; using System.Linq; using System.Diagnostics;
public static class AdbFixture { public static int Main(string[] a) {
 if(a.Length>0 && a[0]=="foreign-child") { System.Threading.Thread.Sleep(90000); return 0; }
 var state=Environment.GetEnvironmentVariable("C05_FIXTURE_STATE")??"none";
 var calls=Environment.GetEnvironmentVariable("C05_FIXTURE_CALLS");if(!String.IsNullOrEmpty(calls))File.AppendAllText(calls,String.Join(" ",a)+Environment.NewLine);
 var mode=Environment.GetEnvironmentVariable("C05_FIXTURE_MODE")??"";
 if(a.Length>0 && a[0]=="devices") { if(mode=="hang-devices"||mode=="hang-devices-foreign")System.Threading.Thread.Sleep(90000);Console.WriteLine("List of devices attached"); if(state=="unauthorized") Console.WriteLine("emulator-5554 unauthorized usb:1-1"); if(state=="offline") Console.WriteLine("emulator-5554 offline usb:1-1"); if(state=="single"||state=="multiple") Console.WriteLine("fixture-01 device product:test model:test transport_id:1"); if(state=="multiple") Console.WriteLine("fixture-02 device product:test model:test transport_id:2"); return 0; }
 if(a.Contains("exec-out")) { if(mode=="hang-capture")System.Threading.Thread.Sleep(90000);if(mode=="stderr-heavy")Console.Error.Write(new String('E',262144));if(Environment.GetEnvironmentVariable("C05_FIXTURE_SCREEN")=="empty")return 0;var file=Environment.GetEnvironmentVariable("C05_FIXTURE_PNG");if(!String.IsNullOrEmpty(file))using(var f=File.OpenRead(file))f.CopyTo(Console.OpenStandardOutput()); return 0; }
 if(a.Contains("install")) { if(mode=="install-success"){Console.WriteLine("Success");return 0;}Console.Error.WriteLine("INSTALL_FAILED_USER_RESTRICTED"); return 12; }
 if(a.Contains("logcat")) { if(mode=="logfail"){Console.Error.WriteLine("REVIEWER_FIXTURE_PERMISSION_DENIED");return 17;}if(mode=="stderr-heavy")Console.Error.Write(new String('E',262144));Console.WriteLine("fixture logcat"); return 0; }
 if(a.Contains("pidof")) { if(mode=="pidfail"){Console.Error.WriteLine("REVIEWER_FIXTURE_PIDOF_DENIED");return 17;}Console.WriteLine("24680"); return 0; }
 if(mode=="deny"){Console.Error.WriteLine("REVIEWER_FIXTURE_PERMISSION_DENIED");return 17;}Console.WriteLine("fixture device info"); return 0;
} }
'@
  Add-Type -TypeDefinition $fixtureSource -OutputAssembly $fixture -OutputType ConsoleApplication
  $env:C05_ADB_PATH=$fixture;$env:C05_FIXTURE_CALLS=Join-Path $runRoot 'adb-calls.log'
  $env:C05_FIXTURE_STATE='unauthorized'
  $r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action Install -Serial emulator-5554 -Apk ignored.apk 2>&1; $code=$LASTEXITCODE
  if ($code -eq 0 -or ($r -join "`n") -notmatch 'unauthorized') { throw 'unauthorized device was not fail-closed' }
  Mark-C05Pass 'unauthorized serial mutation blocked'
  $env:C05_FIXTURE_STATE='none'
  $r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action Install 2>&1; $code=$LASTEXITCODE
  if ($code -eq 0 -or ($r -join "`n") -notmatch 'NOT_RUN: no device') { throw 'no-device case was not reported' }
  Mark-C05Pass 'empty device list is NOT_RUN'
  $env:C05_FIXTURE_STATE='offline'
  $r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action Install 2>&1; $code=$LASTEXITCODE
  if ($code -eq 0 -or ($r -join "`n") -notmatch 'BLOCKED: offline device') { throw 'auto-selected offline device was not BLOCKED' }
  Mark-C05Pass 'offline auto-selection blocked'
  $mutationCount=@(Get-Content $env:C05_FIXTURE_CALLS|Where-Object{$_ -match '\binstall\b'}).Count
  $r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action Install -Serial emulator-5554 2>&1; $code=$LASTEXITCODE
  if($code -eq 0 -or ($r -join "`n") -notmatch 'BLOCKED: serial .* offline' -or @(Get-Content $env:C05_FIXTURE_CALLS|Where-Object{$_ -match '\binstall\b'}).Count -ne $mutationCount){throw 'explicit offline serial was not blocked before mutation'}
  Mark-C05Pass 'explicit offline serial blocked before mutation'
  $env:C05_FIXTURE_STATE='offline'
  $r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action Devices 2>&1; $code=$LASTEXITCODE
  if ($code -ne 0 -or ($r -join "`n") -notmatch 'DEVICE_STATUS=BLOCKED_OFFLINE') { throw 'offline enumeration status was flattened' }
  Mark-C05Pass 'offline enumeration classified BLOCKED'
  $env:C05_FIXTURE_STATE='none'
  $env:C05_FIXTURE_STATE='multiple'
  $mutationCount=@(Get-Content $env:C05_FIXTURE_CALLS|Where-Object{$_ -match '\binstall\b'}).Count
  $r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action Install -Apk ignored.apk 2>&1; $code=$LASTEXITCODE
  if ($code -eq 0 -or ($r -join "`n") -notmatch 'multiple authorized devices' -or @(Get-Content $env:C05_FIXTURE_CALLS|Where-Object{$_ -match '\binstall\b'}).Count -ne $mutationCount) { throw 'ambiguous serial was not rejected before mutation' }
  Mark-C05Pass 'multiple-device serial ambiguity blocked'
  $env:C05_FIXTURE_STATE='single'
  $outRoot=Join-Path $runRoot 'preexisting-capture-parent'; $blackDir=Join-Path $outRoot 'black'; $emptyDir=Join-Path $outRoot 'empty'
  New-Item -ItemType Directory -Path $blackDir -Force | Out-Null;New-Item -ItemType Directory -Path $emptyDir -Force | Out-Null
  $sentinel=Join-Path $blackDir 'caller-owned-history.txt';[IO.File]::WriteAllText($sentinel,'preserve this fixture history')
  Add-Type -AssemblyName System.Drawing; $bmp=New-Object Drawing.Bitmap(512,512); try { $bmp.Save((Join-Path $temp 'black.png'),[Drawing.Imaging.ImageFormat]::Png) } finally { $bmp.Dispose() }
  $env:C05_FIXTURE_PNG=Join-Path $temp 'black.png'; $env:C05_FIXTURE_SCREEN='black'
  $artifactDir=Join-Path $runRoot 'valid-artifact';$validApk=New-C05TestArtifact $artifactDir
  $callsBefore=if(Test-Path $env:C05_FIXTURE_CALLS){@(Get-Content $env:C05_FIXTURE_CALLS|Where-Object{$_ -match '\binstall\b'}).Count}else{0}
  $invalidPath=Join-Path $temp 'not-an-apk.txt';[IO.File]::WriteAllBytes($invalidPath,[byte[]]@(1..21));$r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action Install -Serial fixture-01 -Apk $invalidPath 2>&1;$code=$LASTEXITCODE
  $callsAfter=@(Get-Content $env:C05_FIXTURE_CALLS|Where-Object{$_ -match '\binstall\b'}).Count;if($code -eq 0 -or ($r -join "`n") -notmatch 'existing .apk' -or $callsAfter -ne $callsBefore){throw 'invalid APK caused an adb install call'}
  Mark-C05Pass 'malformed artifact rejected before adb mutation'
  $wrongDir=Join-Path $runRoot 'wrong-package';$wrongApk=New-C05TestArtifact $wrongDir 'com.reviewer.wrong';$callsBefore=$callsAfter
  $r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action Install -Serial fixture-01 -Apk $wrongApk 2>&1;$code=$LASTEXITCODE;$callsAfter=@(Get-Content $env:C05_FIXTURE_CALLS|Where-Object{$_ -match '\binstall\b'}).Count
  if($code -eq 0 -or $callsAfter -ne $callsBefore){throw 'wrong package artifact reached adb'};Mark-C05Pass 'wrong package rejected before adb mutation'
  $settingsDir=Join-Path $runRoot 'invalid-build-settings';$settingsApk=New-C05TestArtifact $settingsDir;$reportPath=Join-Path $settingsDir 'build-report.json';$badSettings=Get-Content $reportPath -Raw|ConvertFrom-Json;$badSettings.landscapeOnly=$false;$badSettings|ConvertTo-Json -Depth 8|Set-Content -Encoding UTF8 $reportPath;$callsBefore=$callsAfter
  $r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action Install -Serial fixture-01 -Apk $settingsApk 2>&1;$code=$LASTEXITCODE;$callsAfter=@(Get-Content $env:C05_FIXTURE_CALLS|Where-Object{$_ -match '\binstall\b'}).Count
  if($code -eq 0 -or $callsAfter -ne $callsBefore){throw 'landscape-invalid build reached adb'};Mark-C05Pass 'landscape-invalid BuildReport rejected before adb mutation'
  $sdkDir=Join-Path $runRoot 'invalid-sdk-metadata';$sdkApk=New-C05TestArtifact $sdkDir;$metadataPath=Join-Path $sdkDir 'apk-metadata.txt';$badMetadata=(Get-Content $metadataPath -Raw) -replace "targetSdkVersion:'36'","targetSdkVersion:'24'";[IO.File]::WriteAllText($metadataPath,$badMetadata);$callsBefore=$callsAfter
  $r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action Install -Serial fixture-01 -Apk $sdkApk 2>&1;$code=$LASTEXITCODE;$callsAfter=@(Get-Content $env:C05_FIXTURE_CALLS|Where-Object{$_ -match '\binstall\b'}).Count
  if($code -eq 0 -or $callsAfter -ne $callsBefore){throw 'target SDK below min SDK reached adb'};Mark-C05Pass 'invalid APK SDK metadata rejected before adb mutation'
  $hashDir=Join-Path $runRoot 'hash-mismatch';$hashApk=New-C05TestArtifact $hashDir;[IO.File]::AppendAllText($hashApk,'altered');$callsBefore=$callsAfter
  $r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action Install -Serial fixture-01 -Apk $hashApk 2>&1;$code=$LASTEXITCODE;$callsAfter=@(Get-Content $env:C05_FIXTURE_CALLS|Where-Object{$_ -match '\binstall\b'}).Count
  if($code -eq 0 -or $callsAfter -ne $callsBefore){throw 'hash/report mismatch reached adb'};Mark-C05Pass 'APK hash/report mismatch rejected before adb mutation'
  $env:C05_FIXTURE_MODE='';$callsBefore=$callsAfter
  $r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action Install -Serial fixture-01 -Apk $validApk -EvidenceDir (Join-Path $runRoot 'install') 2>&1;$code=$LASTEXITCODE
  if($code -eq 0 -or ($r -join "`n") -notmatch 'Install failed status=FAIL exit=12'){throw 'adb install native failure was lost'}
  $install=Get-ChildItem (Join-Path $runRoot 'install') -Filter 'install-result.json' -Recurse|Select-Object -First 1;$installJson=Get-Content $install.FullName -Raw|ConvertFrom-Json
  if($installJson.artifact.apkSha256 -ne (Get-FileHash $validApk -Algorithm SHA256).Hash){throw 'install evidence omitted selected APK identity'};Mark-C05Pass 'adb install failure and selected artifact identity retained'
  $env:C05_PRIVATE_TOKEN='fixture-secret-must-not-be-recorded';$r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action Start -Serial fixture-01 -Apk $validApk -EvidenceDir (Join-Path $runRoot 'private-info-check') 2>&1;$code=$LASTEXITCODE;Remove-Item Env:C05_PRIVATE_TOKEN -ErrorAction SilentlyContinue
  $privateEvidence=Get-ChildItem (Join-Path $runRoot 'private-info-check') -Filter launch-result.json -Recurse|Select-Object -First 1
  if($code -ne 0 -or ($r -join "`n") -match 'fixture-secret-must-not-be-recorded' -or (Get-Content $privateEvidence.FullName -Raw) -match 'fixture-secret-must-not-be-recorded'){throw 'private environment value leaked into tool output/evidence'}
  Mark-C05Pass 'environment token sentinel absent from command output and evidence'
  $r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action Start -Serial fixture-01 -Apk $validApk -EvidenceDir (Join-Path $runRoot 'start') 2>&1;$code=$LASTEXITCODE
  if($code -ne 0 -or ($r -join "`n") -notmatch 'UNVERIFIED'){throw 'start artifact linkage was not explicitly unverified'}
  $startRecord=Get-ChildItem (Join-Path $runRoot 'start') -Filter 'launch-result.json' -Recurse|Select-Object -First 1|Get-Content -Raw|ConvertFrom-Json
  if($startRecord.artifact.apkSha256 -ne (Get-FileHash $validApk -Algorithm SHA256).Hash -or $startRecord.artifact.runtimeArtifactLinkage -ne 'UNVERIFIED_INSTALLED_PACKAGE'){throw 'start evidence lacks selected artifact identity/linkage status'};Mark-C05Pass 'start links selected artifact and marks installed linkage UNVERIFIED'
  $r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action Devices 2>&1;$code=$LASTEXITCODE;if($code -ne 0){throw 'device enumeration incorrectly required an APK'};Mark-C05Pass 'Devices works without APK'
  $env:C05_FIXTURE_MODE='deny';$infoRoot=Join-Path $runRoot 'info-denied';$r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action DeviceInfo -Serial fixture-01 -EvidenceDir $infoRoot 2>&1;$code=$LASTEXITCODE
  $infoRecord=Get-ChildItem $infoRoot -Filter device-info.json -Recurse|Select-Object -First 1|Get-Content -Raw|ConvertFrom-Json
  if($code -eq 0 -or $infoRecord.status -ne 'FAIL' -or @($infoRecord.observations|Where-Object{$_.result.exit -ne 17 -or $_.result.stderr -notmatch 'PERMISSION_DENIED'}).Count){throw 'DeviceInfo permission failures were flattened'};Mark-C05Pass 'all DeviceInfo native permission failures retained'
  $env:C05_FIXTURE_MODE='logfail';$partialRoot=Join-Path $runRoot 'logcat-failure';$r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action Capture -Serial fixture-01 -Apk $validApk -EvidenceDir $partialRoot 2>&1;$code=$LASTEXITCODE
  $partial=Get-ChildItem $partialRoot -Filter '*capture-summary.json' -Recurse|Select-Object -First 1|Get-Content -Raw|ConvertFrom-Json
  if($code -eq 0 -or $partial.status -ne 'PARTIAL' -or $partial.logcat.exit -ne 17 -or $partial.screenshotIsGamePass){throw 'logcat failure was represented as complete/pass'};Mark-C05Pass 'logcat native failure represented as PARTIAL'
  $env:C05_FIXTURE_MODE='pidfail';$pidRoot=Join-Path $runRoot 'pidof-failure';$r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action Capture -Serial fixture-01 -Apk $validApk -EvidenceDir $pidRoot 2>&1;$code=$LASTEXITCODE
  $pidRecord=Get-ChildItem $pidRoot -Filter '*capture-summary.json' -Recurse|Select-Object -First 1|Get-Content -Raw|ConvertFrom-Json
  if($code -eq 0 -or $pidRecord.status -ne 'FAIL' -or $pidRecord.pidof.exit -ne 17){throw 'pidof failure was not propagated'};Mark-C05Pass 'pidof native failure propagated and retained'
  $env:C05_FIXTURE_MODE='stderr-heavy';$heavyRoot=Join-Path $runRoot 'stderr-heavy';$r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action Capture -Serial fixture-01 -Apk $validApk -EvidenceDir $heavyRoot 2>&1;$code=$LASTEXITCODE
  $heavy=Get-ChildItem $heavyRoot -Filter '*capture-summary.json' -Recurse|Select-Object -First 1|Get-Content -Raw|ConvertFrom-Json
  if($code -ne 0 -or $heavy.screencap.stderr.Length -lt 200000 -or -not $heavy.screencap.drainComplete){throw 'stderr-heavy capture deadlocked or lost stderr'};Mark-C05Pass 'stderr-heavy stdout/stderr drained concurrently'
  $env:C05_FIXTURE_MODE='hang-devices';$hangDevices=Join-Path $runRoot 'hang-devices';$clock=[Diagnostics.Stopwatch]::StartNew();$r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action Devices -AdbTimeoutSeconds 1 -EvidenceDir $hangDevices 2>&1;$code=$LASTEXITCODE;$clock.Stop()
  $hangRecord=Get-ChildItem $hangDevices -Filter device-list.json -Recurse|Select-Object -First 1|Get-Content -Raw|ConvertFrom-Json
  if($code -eq 0 -or $clock.Elapsed.TotalSeconds -gt 15 -or -not $hangRecord.timedOut -or -not $hangRecord.killedOwnedProcess){throw 'hung adb devices was not bounded/owned'};Mark-C05Pass 'hung devices bounded and exact owned process stopped'
  $foreign=Start-Process -FilePath $fixture -ArgumentList 'foreign-child' -WindowStyle Hidden -PassThru;$script:foreignFixtureIdentity=[pscustomobject]@{pid=$foreign.Id;ticks=$foreign.StartTime.ToUniversalTime().Ticks};$env:C05_FIXTURE_MODE='hang-devices-foreign';$clock=[Diagnostics.Stopwatch]::StartNew();$r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action Devices -AdbTimeoutSeconds 1 -EvidenceDir (Join-Path $runRoot 'hang-devices-foreign') 2>&1;$code=$LASTEXITCODE;$clock.Stop()
  $foreign=Get-Process -Id $script:foreignFixtureIdentity.pid -ErrorAction SilentlyContinue
  if($code -eq 0 -or $clock.Elapsed.TotalSeconds -gt 15 -or -not $foreign -or $foreign.StartTime.ToUniversalTime().Ticks -ne $script:foreignFixtureIdentity.ticks){throw 'hung adb handling killed or lost a foreign process'}
  $foreign.Kill();[void]$foreign.WaitForExit(5000);$script:foreignFixtureIdentity=$null;Mark-C05Pass 'hung adb leaves unrelated child process alive'
  $env:C05_FIXTURE_MODE='hang-capture';$hangCap=Join-Path $runRoot 'hang-capture';$clock=[Diagnostics.Stopwatch]::StartNew();$r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action Capture -Serial fixture-01 -Apk $validApk -AdbTimeoutSeconds 1 -EvidenceDir $hangCap 2>&1;$code=$LASTEXITCODE;$clock.Stop()
  $hangCapture=Get-ChildItem $hangCap -Filter '*capture-summary.json' -Recurse|Select-Object -First 1|Get-Content -Raw|ConvertFrom-Json
  if($code -eq 0 -or $clock.Elapsed.TotalSeconds -gt 25 -or -not $hangCapture.screencap.timedOut -or -not $hangCapture.screencap.killedOwnedProcess){throw 'hung capture was not bounded/owned'};Mark-C05Pass 'hung binary capture bounded and exact owned process stopped'
  $env:C05_FIXTURE_MODE=''
  $r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action Capture -Serial fixture-01 -Apk $validApk -EvidenceDir $blackDir 2>&1; $code=$LASTEXITCODE
  $blackSummary=Get-ChildItem $blackDir -Filter '*capture-summary.json' -Recurse | Select-Object -First 1
  if ($code -ne 0 -or -not $blackSummary -or (Get-Content $blackSummary.FullName -Raw) -notmatch 'BLACK_OR_NEAR_BLACK_REQUIRES_INVESTIGATION' -or (Get-Content $blackSummary.FullName -Raw) -match '"screenshotIsGamePass"\s*:\s*true' -or -not(Test-Path $sentinel) -or (Get-Content $sentinel -Raw) -ne 'preserve this fixture history') { throw 'black capture did not preserve pre-existing history or remained non-passing' }
  $r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action Capture -Serial fixture-01 -Apk $validApk -EvidenceDir $blackDir 2>&1; $code=$LASTEXITCODE
  if ($code -ne 0 -or @(Get-ChildItem $blackDir -Filter '*capture-summary.json' -Recurse).Count -ne 2) { throw 'repeat capture overwrote or rejected existing evidence parent' }
  $env:C05_FIXTURE_SCREEN='empty'
  $r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action Capture -Serial fixture-01 -Apk $validApk -EvidenceDir $emptyDir 2>&1; $code=$LASTEXITCODE
  $emptySummary=Get-ChildItem $emptyDir -Filter '*capture-summary.json' -Recurse|Select-Object -First 1|Get-Content -Raw|ConvertFrom-Json
  if ($code -eq 0 -or $emptySummary.bytes -ne 0 -or $emptySummary.screenshotIsGamePass -or $emptySummary.status -ne 'FAIL') { throw 'empty screenshot was accepted or not recorded as non-pass' }
  $r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $build -UnityPath (Join-Path $temp 'missing\Unity.exe') 2>&1; $code=$LASTEXITCODE
  if ($code -eq 0) { throw 'missing Unity executable was accepted' }
  $r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $build -OutputFixtureTest 2>&1; $code=$LASTEXITCODE
  if ($code -ne 0 -or ($r -join "`n") -notmatch 'OUTPUT_FIXTURE PASS existing-apk-output-rejected') { throw 'old APK output directory was reused' }
  $r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $build -ProcessFixtureTest 2>&1; $code=$LASTEXITCODE
  if ($code -ne 0 -or ($r -join "`n") -notmatch 'PROCESS_FIXTURE PASS stale-identity-preserved exact-owned-instance-stopped') { throw 'process timeout ownership fixture failed' }
  $r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $build -VerdictFixtureTest 2>&1; $code=$LASTEXITCODE
  if ($code -ne 0 -or ($r -join "`n") -notmatch 'VERDICT_FIXTURE PASS missing-marker failed-report invalid-landscape-settings stale-apk-rejected valid-fixture-accepted') { throw 'BuildReport/APK verdict fixture failed' }
  $r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $build -SourceFixtureTest 2>&1; $code=$LASTEXITCODE
  if ($code -ne 0 -or ($r -join "`n") -notmatch 'SOURCE_FIXTURE PASS staged-unstaged-untracked-rejected allowed-bytes-bound git-failure-rejected') { throw 'staged/unstaged/untracked source boundary fixture failed' }
  Mark-C05Pass 'source preflight rejects staged, unstaged and untracked inputs; binds allowed bytes'
  $r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $build -ArgumentFixtureTest 2>&1; $code=$LASTEXITCODE
  if ($code -ne 0 -or ($r -join "`n") -notmatch 'ARGUMENT_FIXTURE PASS spaces-quotes-trailing-separator-preserved') { throw 'Windows argv fidelity fixture failed' }
  Mark-C05Pass 'production launcher preserves spaces, quotes and trailing separator'
  if(-not $env:C05_TEST_SECOND_RUN){$env:C05_TEST_SECOND_RUN='1';try{$repeat=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath 2>&1;$repeatExit=$LASTEXITCODE}finally{Remove-Item Env:C05_TEST_SECOND_RUN -ErrorAction SilentlyContinue};if($repeatExit -ne 0 -or ($repeat -join "`n") -notmatch 'TOOL_FIXTURE PASS assertions='){throw 'second consecutive full TOOL_FIXTURE suite failed'};if(-not(Test-Path -LiteralPath (Join-Path $legacySentinelRoot 'sentinel.txt'))){throw 'pre-existing sentinel was removed during repeated run'};Mark-C05Pass 'pre-existing output sentinel survives two consecutive complete runs'}
  $env:C05_FIXTURE_STATE='single';$env:C05_FIXTURE_MODE='';$env:C05_FIXTURE_CALLS=Join-Path $runRoot 'second-run-adb-calls.log'
  $r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action Devices 2>&1; $code=$LASTEXITCODE
  if($code -ne 0 -or -not(Test-Path $env:C05_FIXTURE_CALLS)){throw 'second consecutive device-tool invocation failed'}
  Mark-C05Pass 'two consecutive invocations use isolated evidence outputs'
  $suitePassed=$true
  "TOOL_FIXTURE PASS assertions=$($script:passed.Count) names=$($script:passed -join '; ') evidence=$($runRoot.Substring($root.Length+1).Replace('\','/'))"
}
finally {
  Remove-Item Env:C05_ADB_PATH -ErrorAction SilentlyContinue; Remove-Item Env:C05_FIXTURE_STATE -ErrorAction SilentlyContinue; Remove-Item Env:C05_FIXTURE_PNG -ErrorAction SilentlyContinue; Remove-Item Env:C05_FIXTURE_SCREEN -ErrorAction SilentlyContinue; Remove-Item Env:C05_FIXTURE_CALLS -ErrorAction SilentlyContinue; Remove-Item Env:C05_FIXTURE_MODE -ErrorAction SilentlyContinue;Remove-Item Env:C05_PRIVATE_TOKEN -ErrorAction SilentlyContinue
  Remove-Item Env:C05_FIXTURE_FOREIGN -ErrorAction SilentlyContinue
  if($script:foreignFixtureIdentity){$owned=Get-Process -Id $script:foreignFixtureIdentity.pid -ErrorAction SilentlyContinue;if($owned -and $owned.StartTime.ToUniversalTime().Ticks -eq $script:foreignFixtureIdentity.ticks){$owned.Kill();[void]$owned.WaitForExit(5000)}}
  $runFull=[IO.Path]::GetFullPath($runRoot); $allowedBuild=[IO.Path]::GetFullPath((Join-Path $root 'build'))+[IO.Path]::DirectorySeparatorChar
  if(-not $ownsRunRoot -or -not $runFull.StartsWith($allowedBuild,[StringComparison]::OrdinalIgnoreCase)){throw 'Refusing to abandon or clean unowned test evidence root.'}
  if($suitePassed -and $ownsLegacySentinel -and $legacySentinelRoot.StartsWith($allowedBuild,[StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $legacySentinelRoot)) {
    $sentinelPath=[IO.Path]::GetFullPath((Join-Path $legacySentinelRoot 'sentinel.txt'))
    if($sentinelPath.StartsWith($legacySentinelRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $sentinelPath -PathType Leaf) -and (Get-Content -LiteralPath $sentinelPath -Raw) -eq 'caller-owned sentinel') {
      [IO.File]::Delete($sentinelPath)
      try { [IO.Directory]::Delete($legacySentinelRoot,$false) } catch [IO.IOException] { }
    }
  }
}
