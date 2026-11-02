#requires -Version 5.1
[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$RunRoot,
  [string]$UnityPath='D:\Unity\Hub\Editor\6000.3.27f1\Editor\Unity.exe',
  [string]$DotnetPath='D:\Unity\dotnet\dotnet.exe',
  [int]$TimeoutSeconds=900,
  [int]$MutexTimeoutSeconds=600,
  [int]$MutexHeartbeatSeconds=30,
  [string]$MutexCancelFile,
  [switch]$PreflightOnly,
  [switch]$BuildPlayer,
  [switch]$WindowsPlayerTests
)
$ErrorActionPreference='Stop'
if($BuildPlayer -and $WindowsPlayerTests){throw 'Choose either the standalone Player build or Windows Player test run.'}
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$RunRoot=[IO.Path]::GetFullPath($RunRoot)
$allowed=[IO.Path]::GetFullPath((Join-Path $repo 'build'))+[IO.Path]::DirectorySeparatorChar
if(-not $RunRoot.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'RunRoot must be a fresh child of repository build/.'}
if((Split-Path -Leaf $RunRoot) -notmatch '^[0-9a-fA-F]{32}$'){throw 'RunRoot must end in a GUID-N run id.'}
if(Test-Path -LiteralPath $RunRoot){throw 'RunRoot exists; refusing to overwrite evidence.'}
if(-not (Test-Path -LiteralPath $UnityPath -PathType Leaf) -or -not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)){throw 'Pinned Unity or dotnet executable is missing.'}
New-Item -ItemType Directory -Path $RunRoot | Out-Null
$script:RunRoot=$RunRoot
$script:LogPath=Join-Path $RunRoot 'runner.log'
$script:Mutex=$null
$script:OwnedProcess=$null
$script:StageDrive=$null
$source=Join-Path $RunRoot 'staging-project'
$temp=Join-Path $RunRoot 'temp'
$upm=Join-Path $RunRoot 'upm-cache'
$nuget=Join-Path $RunRoot 'nuget'
$cli=Join-Path $RunRoot 'dotnet-home'
foreach($p in @($temp,$upm,(Join-Path $upm 'npm'),(Join-Path $upm 'packages'),$nuget,$cli)){New-Item -ItemType Directory -Force -Path $p|Out-Null}
$old=@{TEMP=$env:TEMP;TMP=$env:TMP;NUGET_PACKAGES=$env:NUGET_PACKAGES;DOTNET_CLI_HOME=$env:DOTNET_CLI_HOME;UPM_CACHE_ROOT=$env:UPM_CACHE_ROOT;UPM_NPM_CACHE_PATH=$env:UPM_NPM_CACHE_PATH;UPM_CACHE_PATH=$env:UPM_CACHE_PATH;MAHJONG_C04_PRIVACY_EVIDENCE=$env:MAHJONG_C04_PRIVACY_EVIDENCE}
$env:TEMP=$temp;$env:TMP=$temp;$env:NUGET_PACKAGES=$nuget;$env:DOTNET_CLI_HOME=$cli
$env:UPM_CACHE_ROOT=$upm;$env:UPM_NPM_CACHE_PATH=Join-Path $upm 'npm';$env:UPM_CACHE_PATH=Join-Path $upm 'packages'
$env:DOTNET_NOLOGO='1';$env:DOTNET_CLI_TELEMETRY_OPTOUT='1';$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
function Log([string]$s){$line='['+[DateTimeOffset]::UtcNow.ToString('o')+'] '+$s;Write-Host $line;Add-Content -LiteralPath $script:LogPath -Value $line -Encoding UTF8}
function Q([string]$s){'"'+$s.Replace('"','\"')+'"'}
function Invoke-Owned([string]$name,[string]$exe,[string[]]$commandArgs,[string]$cwd,[int]$timeout){
  $psi=New-Object Diagnostics.ProcessStartInfo;$psi.FileName=$exe;$psi.Arguments=($commandArgs|ForEach-Object{Q $_}) -join ' ';$psi.WorkingDirectory=$cwd;$psi.UseShellExecute=$false;$psi.CreateNoWindow=$true;$psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true
  Log "START name=$name exe=$exe argv=$($psi.Arguments) cwd=$cwd timeout=$timeout"
  foreach($k in @('TEMP','TMP','NUGET_PACKAGES','DOTNET_CLI_HOME','UPM_CACHE_ROOT','UPM_NPM_CACHE_PATH','UPM_CACHE_PATH','MAHJONG_C04_PRIVACY_EVIDENCE')){$psi.EnvironmentVariables[$k]=[Environment]::GetEnvironmentVariable($k)}
  $p=New-Object Diagnostics.Process;$p.StartInfo=$psi;$out=Join-Path $RunRoot "$name.stdout.log";$err=Join-Path $RunRoot "$name.stderr.log";$sw=[Diagnostics.Stopwatch]::StartNew();if(-not $p.Start()){throw "$name did not start"};$script:OwnedProcess=$p
  $o=$p.StandardOutput.ReadToEndAsync();$e=$p.StandardError.ReadToEndAsync();$next=[DateTime]::UtcNow.AddSeconds($MutexHeartbeatSeconds)
  while(-not $p.WaitForExit(1000)){
    if($sw.Elapsed.TotalSeconds -ge $timeout){[IO.File]::WriteAllText((Join-Path $RunRoot "$name.timeout"),"timeout=$timeout`n",[Text.Encoding]::UTF8);try{$p.CloseMainWindow()}catch{};if(-not $p.WaitForExit(15000)){throw "$name timeout: process did not honor normal close"};break}
    if($MutexCancelFile -and (Test-Path -LiteralPath $MutexCancelFile)){Log "$name cancel requested; sending normal close";[IO.File]::WriteAllText((Join-Path $RunRoot "$name.cancelled"),'cancel-file observed',[Text.Encoding]::UTF8);try{$p.CloseMainWindow()}catch{};if(-not $p.WaitForExit(15000)){throw "$name cancellation: process did not honor normal close"};break}
    if([DateTime]::UtcNow -ge $next){Log "HEARTBEAT phase=$name pid=$($p.Id) elapsed=$([int]$sw.Elapsed.TotalSeconds)s";$next=[DateTime]::UtcNow.AddSeconds($MutexHeartbeatSeconds)}
  }
  $p.WaitForExit();[IO.File]::WriteAllText($out,$o.GetAwaiter().GetResult(),[Text.Encoding]::UTF8);[IO.File]::WriteAllText($err,$e.GetAwaiter().GetResult(),[Text.Encoding]::UTF8);$code=$p.ExitCode;$script:OwnedProcess=$null
  [IO.File]::WriteAllText((Join-Path $RunRoot "$name.exit"),"nativeExit=$code`nelapsedSeconds=$([Math]::Round($sw.Elapsed.TotalSeconds,3))`npid=$($p.Id)`n",[Text.Encoding]::UTF8);Log "$name exit=$code elapsed=$([int]$sw.Elapsed.TotalSeconds)s";if($code -ne 0){throw "$name failed with native exit $code"};return $code
}
function Acquire-Mutex{
  $sw=[Diagnostics.Stopwatch]::StartNew();do{if($MutexCancelFile -and (Test-Path -LiteralPath $MutexCancelFile)){throw 'Cancelled while waiting for Unity validation mutex.'};$created=$false;$m=New-Object Threading.Mutex($false,'Local\MahjongV5P45UnityValidation',[ref]$created);try{$held=$m.WaitOne(1000)}catch [Threading.AbandonedMutexException]{$held=$true};if($held){$script:Mutex=$m;Log "MUTEX_ACQUIRED name=Local\MahjongV5P45UnityValidation waited=$([int]$sw.Elapsed.TotalSeconds)s";return};$m.Dispose();if([int]$sw.Elapsed.TotalSeconds -ge $MutexTimeoutSeconds){throw 'Unity mutex wait exceeded configured bound.'};if([int]$sw.Elapsed.TotalSeconds -gt 0 -and [int]$sw.Elapsed.TotalSeconds % $MutexHeartbeatSeconds -eq 0){Log "HEARTBEAT mutex_wait=$([int]$sw.Elapsed.TotalSeconds)s"}}while($true)
}
try{
  $head=(git -C $repo rev-parse HEAD).Trim();$status=git -C $repo status --porcelain
  Log "PRECHECK head=$head unity=$((Get-Item $UnityPath).VersionInfo.FileVersion) dotnet=$((Get-Item $DotnetPath).VersionInfo.FileVersion) dirtyEntries=$(@($status).Count) runRoot=$RunRoot"
  if($PreflightOnly){[IO.File]::WriteAllText((Join-Path $RunRoot 'preflight.json'),(@{head=$head;dirty=@($status);unityPath=$UnityPath;dotnetPath=$DotnetPath;timeout=$TimeoutSeconds;mutex='Local\MahjongV5P45UnityValidation'}|ConvertTo-Json -Depth 4),[Text.Encoding]::UTF8);return}
  $unitySource=Join-Path $repo 'unity/Mahjong.Unity'
  New-Item -ItemType Directory -Path $source | Out-Null
  foreach($name in @('Assets','Packages','ProjectSettings')){
    $sourcePath=Join-Path $unitySource $name;$destinationPath=Join-Path $source $name
    if(-not (Test-Path -LiteralPath $sourcePath -PathType Container)){throw "Unity source project folder is missing: $sourcePath"}
    New-Item -ItemType Directory -Path $destinationPath | Out-Null
    Get-ChildItem -LiteralPath $sourcePath -Force | Copy-Item -Destination $destinationPath -Recurse -Force
  }
  $occupied=@(Get-PSDrive -PSProvider FileSystem | ForEach-Object { $_.Name.ToUpperInvariant() })
  foreach($letter in @('R','S','T','U','V','W','X','Y','Z')){
    if($occupied -notcontains $letter){
      $drive="${letter}:"
      & subst.exe $drive $RunRoot
      if($LASTEXITCODE -ne 0){throw "Could not map the isolated run root to a short Unity staging drive (subst exit $LASTEXITCODE)."}
      $script:StageDrive=$drive
      break
    }
  }
  if(-not $script:StageDrive){throw 'No unused drive letter is available for the short Unity staging path.'}
  $unitySourceRoot=$script:StageDrive+'\'
  $unityProject=Join-Path $unitySourceRoot 'staging-project'
  if(-not (Test-Path -LiteralPath (Join-Path $source 'Assets/Scripts/App/AppComposition.cs') -PathType Leaf) -or
     -not (Test-Path -LiteralPath (Join-Path $source 'Assets/Tests/PlayMode/C04PrivacyRoleSwitchPlayModeTests.cs') -PathType Leaf)){
    throw 'Staged Unity project layout lacks AppComposition or the C04 PlayMode test under Assets/.'
  }
  $pluginDir=Join-Path $unityProject 'Assets/Plugins/Mahjong';New-Item -ItemType Directory -Force -Path $pluginDir|Out-Null
  $wrapperDir=Join-Path $source 'tools/unity';New-Item -ItemType Directory -Force -Path $wrapperDir|Out-Null
  $checker=Join-Path $repo 'tools/unity/check-shared-plugins.ps1';$manifest=Join-Path $pluginDir 'shared-plugin-provenance.json'
  $wrapper=@"
param([string]`$ProjectRoot,[switch]`$Quiet)
`$ErrorActionPreference='Stop'
& '$checker' -ProjectRoot '$repo' -PluginDir '$pluginDir' -EvidencePath '$manifest' -Configuration Release -Quiet:`$Quiet
exit `$LASTEXITCODE
"@
  [IO.File]::WriteAllText((Join-Path $wrapperDir 'check-shared-plugins.ps1'),$wrapper,[Text.UTF8Encoding]::new($false))
  $verifierDll=Join-Path $repo 'dotnet/tools/C04PrivacyMatrixVerifier/bin/Release/net8.0/C04PrivacyMatrixVerifier.dll'
  Invoke-Owned 'privacy-verifier-build' $DotnetPath @('build',(Join-Path $repo 'dotnet/tools/C04PrivacyMatrixVerifier/C04PrivacyMatrixVerifier.csproj'),'-c','Release','--nologo') $repo 300|Out-Null
  $verifierDll=Join-Path $repo 'dotnet/tools/C04PrivacyMatrixVerifier/bin/Release/net8.0/C04PrivacyMatrixVerifier.dll'
  Invoke-Owned 'privacy-verifier-selftest' $DotnetPath @($verifierDll,'self-test','unused.json') $repo 30|Out-Null
  Acquire-Mutex
  $sharedBuilder=Join-Path $repo 'tools/unity/build-shared.ps1'
  Invoke-Owned 'shared-plugin-build-and-provenance' 'powershell.exe' @('-NoProfile','-ExecutionPolicy','Bypass','-File',$sharedBuilder,'-Configuration','Release','-DotnetPath',$DotnetPath,'-PluginOutputDir',$pluginDir) $repo 1200|Out-Null
  if(-not (Test-Path -LiteralPath $manifest -PathType Leaf)){throw 'Fresh shared plugin provenance manifest is missing.'}
  Copy-Item -LiteralPath $manifest -Destination (Join-Path $RunRoot 'shared-plugin-provenance.json')
  $unityLog=Join-Path $RunRoot 'unity-editor.log';$testXml=Join-Path $RunRoot 'playmode-results.xml';$privacyEvidence=Join-Path $RunRoot 'privacy-evidence';$marker=Join-Path $RunRoot 'player-build.json';$player=Join-Path $RunRoot 'player/Mahjong.exe';$buildLog=Join-Path $RunRoot 'player-build.log'
  New-Item -ItemType Directory -Path (Split-Path -Parent $player) -Force|Out-Null
  if($BuildPlayer){$method='C04PrivacyBuild.Build';$unityArgs=@('-batchmode','-quit','-projectPath',$unityProject,'-executeMethod',$method,'-logFile',$unityLog,'-c04PrivacyOutput',$player,'-c04PrivacyMarker',$marker,'-c04PrivacyBuildLog',$buildLog,'-c04PrivacySourceHead',$head,'-c04PrivacyBuildCommit',(git -C $repo rev-parse HEAD).Trim(),'-upmCachePath',$upm)}
  elseif($WindowsPlayerTests){$testXml=Join-Path $RunRoot 'windows-player-results.xml';$env:MAHJONG_C04_PRIVACY_EVIDENCE=$privacyEvidence;$unityArgs=@('-batchmode','-projectPath',$unityProject,'-runTests','-testPlatform','StandaloneWindows64','-testFilter','Mahjong.Unity.Tests.PlayMode.C04PrivacyRoleSwitchPlayModeTests.RealRoom_FourPlayersSpectatorAndRoleSwitchUseIndependentSessions','-testResults',$testXml,'-logFile',$unityLog,'-upmCachePath',$upm)}
  else{$unityArgs=@('-batchmode','-projectPath',$unityProject,'-runTests','-testPlatform','PlayMode','-testFilter','Mahjong.Unity.Tests.PlayMode.C04PrivacyRoleSwitchPlayModeTests.RealRoom_FourPlayersSpectatorAndRoleSwitchUseIndependentSessions','-testResults',$testXml,'-logFile',$unityLog,'-c04PrivacyEvidence',$privacyEvidence,'-upmCachePath',$upm)}
  Invoke-Owned $(if($BuildPlayer){'unity-player-build'}elseif($WindowsPlayerTests){'unity-windows-player-tests'}else{'unity-playmode'}) $UnityPath $unityArgs $unityProject $TimeoutSeconds|Out-Null
  if(-not $BuildPlayer){
    if(-not (Test-Path -LiteralPath $testXml)){throw 'Unity PlayMode run did not write test XML.'}
    [xml]$xml=[IO.File]::ReadAllText($testXml);$testSummary=$xml.'test-run';if([int]$testSummary.failed -ne 0 -or [int]$testSummary.passed -ne 1){throw "PlayMode result mismatch: passed=$($testSummary.passed) failed=$($testSummary.failed)."}
    if($WindowsPlayerTests -and -not (Test-Path (Join-Path $privacyEvidence 'raw-frames.json'))){throw 'Windows test Player did not seal real raw channel frames under its isolated evidence root.'}
    $rawFrames=Join-Path $privacyEvidence 'raw-frames.json';if(-not (Test-Path $rawFrames)){throw 'PlayMode did not seal raw frames.'}
    $matrixTests=Join-Path $repo 'tools/tests/C04PrivacyMatrix.Tests.ps1';$verifierEvidence=Join-Path $RunRoot 'verifier-tests'
    Invoke-Owned 'privacy-matrix-tests' 'powershell.exe' @('-NoProfile','-ExecutionPolicy','Bypass','-File',$matrixTests,'-DotnetPath',$DotnetPath,'-VerifierDll',$verifierDll,'-RawFramesPath',$rawFrames,'-RunRoot',$verifierEvidence) $repo 180|Out-Null
  }else{if(-not (Test-Path $marker)){throw 'Windows Player build marker missing.'}}
  $ended=[ordered]@{schemaVersion=1;head=$head;mode=$(if($BuildPlayer){'player-build'}elseif($WindowsPlayerTests){'windows-player-tests'}else{'playmode'});unityLogSha256=(Get-FileHash $unityLog -Algorithm SHA256).Hash.ToLowerInvariant();testXmlSha256=$(if(Test-Path $testXml){(Get-FileHash $testXml -Algorithm SHA256).Hash.ToLowerInvariant()}else{$null});playerBuildMarker=$(Test-Path $marker);finishedUtc=[DateTimeOffset]::UtcNow.ToString('o')}
  [IO.File]::WriteAllText((Join-Path $RunRoot 'stage-result.json'),($ended|ConvertTo-Json -Depth 4),[Text.Encoding]::UTF8)
}catch{Log "FAIL $($_.Exception.Message)";[IO.File]::WriteAllText((Join-Path $RunRoot 'failure.txt'),$_.Exception.ToString(),[Text.Encoding]::UTF8);throw}
finally{
  if($script:OwnedProcess -and -not $script:OwnedProcess.HasExited){try{$script:OwnedProcess.CloseMainWindow()}catch{}}
  if($script:Mutex){try{$script:Mutex.ReleaseMutex()}finally{$script:Mutex.Dispose();$script:Mutex=$null;Log 'MUTEX_RELEASED'}}
  if($script:StageDrive){& subst.exe $script:StageDrive /D;Log "UNITY_STAGING_DRIVE_RELEASED drive=$($script:StageDrive)";$script:StageDrive=$null}
  foreach($k in $old.Keys){[Environment]::SetEnvironmentVariable($k,$old[$k])}
  if($script:LogPath -and (Test-Path $script:LogPath)){Log 'RUNNER_FINALLY_COMPLETE'}
}
