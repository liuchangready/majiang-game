#Requires -Version 5.1
[CmdletBinding()]
param([string]$UnityPath = 'D:\Unity\Hub\Editor\6000.3.27f1\Editor\Unity.exe', [string]$OutputRoot, [int]$MutexWaitSeconds = 30, [int]$UnityTimeoutSeconds = 7200,
      [string]$AndroidToolchainManifest, [switch]$ProcessFixtureTest,[switch]$VerdictFixtureTest,[switch]$OutputFixtureTest)
$ErrorActionPreference = 'Stop'
function Assert-C05FreshOutputDirectory([string]$Path) { $full=[IO.Path]::GetFullPath($Path); if(Test-Path -LiteralPath $full){throw 'Refusing to reuse an existing evidence/output directory; old APKs and failure logs are preserved.'}; return $full }
function Assert-C05BuildOutcome([object]$Report,[string]$ApkPath,[string]$Metadata,[datetime]$StartedAt,[string[]]$RequiredScenes) {
  if ($null -eq $Report) { throw 'FAIL: missing Unity BuildReport marker.' }
  if ($Report.result -ne 'Succeeded' -or [int]$Report.errors -ne 0 -or $Report.target -ne 'Android' -or $Report.backend -ne 'IL2CPP' -or $Report.architectures -ne 'ARM64' -or -not $Report.tmpResources) { throw 'FAIL: Unity BuildReport did not satisfy full-game Android settings.' }
  foreach($scene in $RequiredScenes){ if ($Report.scenes -notcontains $scene) { throw "FAIL: enabled production scene missing from report: $scene" } }
  if (-not (Test-Path -LiteralPath $ApkPath -PathType Leaf)) { throw 'FAIL: fresh APK output missing.' }
  if ((Get-Item -LiteralPath $ApkPath).LastWriteTimeUtc -lt $StartedAt.ToUniversalTime()) { throw 'FAIL: APK is stale and predates this build attempt.' }
  if ($Report.output -ne [IO.Path]::GetFullPath($ApkPath)) { throw 'FAIL: BuildReport output path mismatch.' }
  if ($Metadata -notmatch ("package: name='"+[regex]::Escape([string]$Report.packageId)+"'") -or $Metadata -notmatch "native-code: 'arm64-v8a'") { throw 'FAIL: APK package metadata does not match package id and ARM64 ABI.' }
  return $true
}
function Stop-C05OwnedProcess([int]$ProcessId,[long]$ExpectedStartTicks) {
  $owned=Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
  if (-not $owned) { return $false }
  if ($owned.StartTime.ToUniversalTime().Ticks -ne $ExpectedStartTicks) { return $false }
  $owned.Kill(); $owned.WaitForExit(); return $true
}
if ($VerdictFixtureTest) {
  $fixtureRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')); $testRoot=Join-Path $fixtureRoot ('build\p45-verdict-fixtures\'+[guid]::NewGuid().ToString('N')); New-Item -ItemType Directory -Force -Path $testRoot|Out-Null
  try {
    $apk=Join-Path $testRoot 'fixture.apk'; [IO.File]::WriteAllBytes($apk,[byte[]]@(1,2,3)); $now=[DateTime]::UtcNow.AddSeconds(-1)
    $report=[pscustomobject]@{result='Succeeded';errors=0;target='Android';backend='IL2CPP';architectures='ARM64';tmpResources=$true;output=[IO.Path]::GetFullPath($apk);packageId='com.majiang.game';scenes=@('Assets/Scenes/Boot.unity','Assets/Scenes/Menu.unity','Assets/Scenes/Lobby.unity','Assets/Scenes/Table.unity','Assets/Scenes/Spectator.unity','Assets/Scenes/Settlement.unity')}
    $metadata="package: name='com.majiang.game' versionCode='1'`nnative-code: 'arm64-v8a'"
    $rejected=0; foreach($bad in @($null,($report|Select-Object *))){if($null -ne $bad){$bad.result='Failed';$bad.errors=1};try{Assert-C05BuildOutcome $bad $apk $metadata $now $report.scenes|Out-Null}catch{$rejected++}}
    $oldTime=[DateTime]::UtcNow.AddDays(-1); [IO.File]::SetLastWriteTimeUtc($apk,$oldTime); try{Assert-C05BuildOutcome $report $apk $metadata $now $report.scenes|Out-Null}catch{$rejected++}; [IO.File]::SetLastWriteTimeUtc($apk,[DateTime]::UtcNow)
    if (-not (Assert-C05BuildOutcome $report $apk $metadata $now $report.scenes)) { throw 'valid fixture artifact rejected' }
    if ($rejected -ne 3) { throw "failed/missing/stale report cases rejected=$rejected expected=3" }
    Write-Output 'VERDICT_FIXTURE PASS missing-marker failed-report stale-apk-rejected valid-fixture-accepted'; exit 0
  } finally { $full=[IO.Path]::GetFullPath($testRoot); $base=[IO.Path]::GetFullPath((Join-Path $fixtureRoot 'build'))+[IO.Path]::DirectorySeparatorChar; if(-not $full.StartsWith($base,[StringComparison]::OrdinalIgnoreCase)){throw 'unsafe verdict fixture cleanup'}; [IO.Directory]::Delete($full,$true) }
}
if ($ProcessFixtureTest) {
  $child=Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-Command','Start-Sleep -Seconds 90') -WindowStyle Hidden -PassThru
  try {
    $ticks=$child.StartTime.ToUniversalTime().Ticks
    $rejected=Stop-C05OwnedProcess $child.Id ($ticks+1)
    $stillAlive=Get-Process -Id $child.Id -ErrorAction SilentlyContinue
    if ($rejected -or -not $stillAlive) { throw 'foreign/stale start-time identity was not preserved' }
    $stopped=Stop-C05OwnedProcess $child.Id $ticks
    if (-not $stopped) { throw 'owned process exact PID+start-time did not stop' }
    Write-Output 'PROCESS_FIXTURE PASS stale-identity-preserved exact-owned-instance-stopped'; exit 0
  } finally { if (Get-Process -Id $child.Id -ErrorAction SilentlyContinue) { [void](Stop-C05OwnedProcess $child.Id $child.StartTime.ToUniversalTime().Ticks) } }
}
if ($OutputFixtureTest) {
  $fixtureRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')); $base=Join-Path $fixtureRoot ('build\p45-output-fixtures\'+[guid]::NewGuid().ToString('N')); New-Item -ItemType Directory -Force -Path $base|Out-Null
  try { [IO.File]::WriteAllBytes((Join-Path $base 'Mahjong.apk'),[byte[]]@(1,2,3)); try{[void](Assert-C05FreshOutputDirectory $base)}catch{if($_.Exception.Message -like 'Refusing to reuse*'){Write-Output 'OUTPUT_FIXTURE PASS existing-apk-output-rejected';exit 0};throw}; throw 'existing output was accepted' }
  finally { $full=[IO.Path]::GetFullPath($base); $allowed=[IO.Path]::GetFullPath((Join-Path $fixtureRoot 'build'))+[IO.Path]::DirectorySeparatorChar; if(-not $full.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'unsafe output fixture cleanup'}; [IO.Directory]::Delete($full,$true) }
}
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$project = Join-Path $root 'unity\Mahjong.Unity'
$unity = [IO.Path]::GetFullPath($UnityPath)
if (-not (Test-Path -LiteralPath $unity -PathType Leaf)) { throw "Unity Editor missing: $unity" }
$versionFile = Join-Path (Split-Path (Split-Path $unity -Parent) -Parent) 'Editor\Data\Resources\UnityVersion.txt'
if (-not (Test-Path $versionFile)) { $versionFile = Join-Path (Split-Path $unity -Parent) 'Data\Resources\UnityVersion.txt' }
$unityVersion = if (Test-Path $versionFile) { (Get-Content $versionFile -Raw).Trim() } else { 'UNKNOWN' }
$android = Join-Path (Split-Path $unity -Parent) 'Data\PlaybackEngines\AndroidPlayer'
foreach ($required in @('OpenJDK\bin\java.exe','SDK\platform-tools\adb.exe','NDK')) { if (-not (Test-Path (Join-Path $android $required))) { throw "Android toolchain incomplete: $required" } }
if (-not $AndroidToolchainManifest) { $AndroidToolchainManifest=Join-Path $root 'build\android-toolchain\private\toolchain.json' }
$manifestPath=[IO.Path]::GetFullPath($AndroidToolchainManifest)
if (-not (Test-Path $manifestPath -PathType Leaf)) { throw "BLOCKED: accepted private Android toolchain manifest missing: $manifestPath" }
$manifestSha=(Get-FileHash $manifestPath -Algorithm SHA256).Hash
if ($manifestSha -ne 'E4785B1BE9FD4CE158E6730C1F7E01C58A7C974D45A407EF7E9CC215F3BEE934') { throw 'FAIL: Android toolchain manifest digest differs from accepted P2.2 manifest.' }
$toolchain=Get-Content $manifestPath -Raw | ConvertFrom-Json
if ($toolchain.schemaVersion -ne 1 -or $toolchain.ninjaVersion -ne '1.13.2' -or $toolchain.ninjaSha256 -ne 'E52A7AD9538D9618C67A0BD777964E2EEC8A30F68B810A2F6ADCE1F2DAF847B8') { throw 'FAIL: accepted Android toolchain manifest identity mismatch.' }
foreach($entry in @(@($toolchain.ninjaExe,$toolchain.ninjaSha256),@($toolchain.androidCliBin,'F66B015EF9F2054D8E4A9877185A6D922109AA68A4AF366F5E96C2629F7BD210'))) {
  if (-not (Test-Path -LiteralPath $entry[0] -PathType Leaf) -or (Get-FileHash -LiteralPath $entry[0] -Algorithm SHA256).Hash -ne $entry[1]) { throw 'FAIL: private Android helper missing or digest mismatch.' }
}
if (-not $OutputRoot) { $OutputRoot = Join-Path $root ('build\p45-android-game\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8)) }
$run = Assert-C05FreshOutputDirectory $OutputRoot; New-Item -ItemType Directory -Force -Path $run | Out-Null
$apk = Join-Path $run 'Mahjong.apk'; $marker = Join-Path $run 'build-report.json'; $editorLog = Join-Path $run 'build-report-detail.json'; $unityLog = Join-Path $run 'unity.log'
$head = (& git -C $root rev-parse HEAD).Trim(); $dirty = @(& git -C $root -c core.quotepath=false diff --name-only) + @(& git -C $root -c core.quotepath=false ls-files --others --exclude-standard)
$dirtyAllowed = @('tools/unity/build-p45-android-game.ps1','tools/unity/run-p45-android-game.ps1','tools/tests/VerifyP45AndroidGame.Tests.ps1','unity/Mahjong.Unity/Assets/Editor/C05FullGameAndroidBuild.cs','unity/Mahjong.Unity/Assets/Editor/C05FullGameAndroidBuild.cs.meta')
$unexpected = @($dirty | Where-Object { $dirtyAllowed -notcontains $_ -and -not ($_ -like 'doc/*' -and (Split-Path $_ -Leaf) -eq 'V5-G2-C05-ANDROID-GAME-DELIVERY-20261101.md') })
if ($unexpected.Count) { throw 'Unexpected dirty paths prevent C05 build: ' + ($unexpected -join ';') }
$inputHashes=@{}; foreach($rel in @('unity/Mahjong.Unity/Assets/Editor/C05FullGameAndroidBuild.cs','unity/Mahjong.Unity/Assets/Editor/C05FullGameAndroidBuild.cs.meta','unity/Mahjong.Unity/ProjectSettings/EditorBuildSettings.asset','unity/Mahjong.Unity/ProjectSettings/ProjectSettings.asset','unity/Mahjong.Unity/Packages/manifest.json','unity/Mahjong.Unity/Packages/packages-lock.json')) { $inputHashes[$rel]=(Get-FileHash (Join-Path $root $rel) -Algorithm SHA256).Hash }
$mutex = New-Object Threading.Mutex($false, 'Local\MahjongV5P45UnityValidation'); $held = $false
try {
  $held = $mutex.WaitOne([TimeSpan]::FromSeconds($MutexWaitSeconds))
  if (-not $held) { throw 'BLOCKED: Unity validation mutex busy; no Unity process started.' }
  $sharedLog = Join-Path $run 'shared-build.log'
  & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build-shared.ps1') -Configuration Release *> $sharedLog
  $sharedExit = $LASTEXITCODE
  if ($sharedExit -ne 0) { throw "Shared plugin forced build failed exit=$sharedExit log=$sharedLog" }
  & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'check-shared-plugins.ps1') -Configuration Release *> (Join-Path $run 'plugin-provenance.log')
  $provenanceExit = $LASTEXITCODE
  if ($provenanceExit -ne 0) { throw "Shared plugin provenance verification failed exit=$provenanceExit" }
  $oldCli=$env:ANDROID_CLI_BIN; $oldJava=$env:JAVA_HOME; $oldPath=$env:PATH
  $env:ANDROID_CLI_BIN=$toolchain.androidCliBin; $env:JAVA_HOME=$toolchain.jdkHome; $env:PATH=($toolchain.ninjaDir + ';' + $oldPath)
  $args = @('-batchmode','-quit','-projectPath',$project,'-buildTarget','Android','-executeMethod','C05FullGameAndroidBuild.Build','-c05Output',$apk,'-c05Marker',$marker,'-c05EditorLog',$editorLog,'-logFile',$unityLog)
  $start = Get-Date
  $proc = Start-Process -FilePath $unity -ArgumentList $args -WorkingDirectory $project -WindowStyle Hidden -PassThru
  $ownedPid=$proc.Id; $ownedStart=$proc.StartTime.ToUniversalTime().Ticks
  if (-not $proc.WaitForExit($UnityTimeoutSeconds * 1000)) {
    $killAttempted=Stop-C05OwnedProcess $ownedPid $ownedStart
    [pscustomobject]@{status='TIMEOUT';pid=$ownedPid;startUtcTicks=$ownedStart;timeoutSeconds=$UnityTimeoutSeconds;killAttempted=$killAttempted} | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $run 'timeout.json')
    throw "Unity timeout after $UnityTimeoutSeconds seconds; only the exact launched PID/start-time instance was eligible for termination."
  }
  $native = $proc.ExitCode
  if ($native -ne 0) { throw "Unity exited $native; see $unityLog" }
  if (-not (Test-Path $marker)) { throw 'Unity returned success without a fresh BuildReport marker.' }
  $report = Get-Content $marker -Raw | ConvertFrom-Json
  if ($report.result -ne 'Succeeded' -or [int]$report.errors -ne 0) { throw "Unity BuildReport rejected result=$($report.result) errors=$($report.errors)." }
  if (-not (Test-Path $apk)) { throw 'Unity BuildReport succeeded but fresh APK output is missing.' }
  $hash = (Get-FileHash $apk -Algorithm SHA256).Hash; $apkInfo = Get-Item $apk
  $env:ANDROID_CLI_BIN=$oldCli; $env:JAVA_HOME=$oldJava; $env:PATH=$oldPath
  $adb = Join-Path $android 'SDK\platform-tools\adb.exe'; $aapt = Join-Path $toolchain.sdkRoot 'build-tools'
  $aapt2 = Get-ChildItem $aapt -Filter aapt2.exe -Recurse | Sort-Object FullName -Descending | Select-Object -First 1
  if (-not $aapt2) { throw 'FAIL: aapt2 not found in accepted Android SDK build-tools.' }
  $metadataOutput=& $aapt2.FullName dump badging $apk 2>&1; $aaptExit=$LASTEXITCODE; $metadata=$metadataOutput|Out-String
  $metaPath = Join-Path $run 'apk-metadata.txt'; [IO.File]::WriteAllText($metaPath,$metadata)
  if ($aaptExit -ne 0) { throw "FAIL: aapt2 dump badging exited $aaptExit." }
  [void](Assert-C05BuildOutcome $report $apk $metadata $start $report.scenes)
  [pscustomobject]@{ status='PASS';sourceHead=$head;dirtyPaths=@($dirty);inputSha256=$inputHashes;unityVersion=$unityVersion;unityPath='Unity Hub 6000.3.27f1';androidPlayer=(Test-Path $android);toolchainManifestSha256=$manifestSha;androidToolchain=[pscustomobject]@{jdkVersion=$toolchain.jdkVersion;ndkRevision=$toolchain.ndkRevision;cmakeVersion=$toolchain.cmakeVersion;ninjaVersion=$toolchain.ninjaVersion;androidCliMode=$toolchain.androidCliMode;sdkPackages=$toolchain.sdkPackages};apk=(Split-Path $apk -Leaf);apkBytes=$apkInfo.Length;apkSha256=$hash;nativeExit=$native;buildReport=$report;elapsedSeconds=[math]::Round(((Get-Date)-$start).TotalSeconds,1);evidenceRoot='build/p45-android-game/<run>';metadata='apk-metadata.txt';adbPresent=(Test-Path $adb) } | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 (Join-Path $run 'summary.json')
  Write-Host "PASS APK bytes=$($apkInfo.Length) SHA256=$hash evidence=build/p45-android-game/<run>"
} catch {
  $reportLine=$null; if (Test-Path $unityLog) { $reportLine=Select-String -Path $unityLog -Pattern 'C05_BUILD_REPORT: (\{.*\})' | Select-Object -Last 1 }
  $failureStatus=if($_.Exception.Message -like 'BLOCKED:*'){'BLOCKED'}elseif($_.Exception.Message -like 'NOT_RUN:*'){'NOT_RUN'}else{'FAIL'}
  [pscustomobject]@{status=$failureStatus;sourceHead=$head;dirtyPaths=@($dirty);inputSha256=$inputHashes;toolchainManifestSha256=$manifestSha;nativeExit=$native;error=$_.Exception.Message;buildReportLine=$(if($reportLine){$reportLine.Matches[0].Groups[1].Value}else{$null});apkExists=(Test-Path $apk);evidenceRoot='build/p45-android-game/<run>';unityLog='unity.log'} | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 (Join-Path $run 'failure-summary.json')
  throw
} finally { if (Get-Variable oldCli -ErrorAction SilentlyContinue) { $env:ANDROID_CLI_BIN=$oldCli; $env:JAVA_HOME=$oldJava; $env:PATH=$oldPath }; if ($held) { $mutex.ReleaseMutex() }; $mutex.Dispose() }
