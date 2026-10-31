#Requires -Version 5.1
[CmdletBinding()]
param([string]$UnityPath = 'D:\Unity\Hub\Editor\6000.3.27f1\Editor\Unity.exe', [string]$OutputRoot, [int]$MutexWaitSeconds = 30, [int]$UnityTimeoutSeconds = 7200,
      [string]$AndroidToolchainManifest, [switch]$ProcessFixtureTest,[switch]$VerdictFixtureTest,[switch]$OutputFixtureTest,[switch]$SourceFixtureTest,[switch]$ArgumentFixtureTest)
$ErrorActionPreference = 'Stop'
function Assert-C05FreshOutputDirectory([string]$Path) { $full=[IO.Path]::GetFullPath($Path); if(Test-Path -LiteralPath $full){throw 'Refusing to reuse an existing evidence/output directory; old APKs and failure logs are preserved.'}; return $full }
function ConvertTo-C05WindowsArgument([string]$Value) {
  $builder=New-Object Text.StringBuilder; [void]$builder.Append('"'); $slashes=0
  foreach($ch in $Value.ToCharArray()) {
    if($ch -eq [char]92) { $slashes++; continue }
    if($ch -eq [char]34) { for($i=0;$i -lt (2*$slashes+1);$i++){[void]$builder.Append([char]92)}; [void]$builder.Append([char]34); $slashes=0; continue }
    for($i=0;$i -lt $slashes;$i++){[void]$builder.Append([char]92)}; $slashes=0; [void]$builder.Append($ch)
  }
  for($i=0;$i -lt (2*$slashes);$i++){[void]$builder.Append([char]92)}; [void]$builder.Append('"'); return $builder.ToString()
}
function Start-C05HiddenProcess([string]$FilePath,[string[]]$Arguments,[string]$WorkingDirectory) {
  $line=( @($Arguments | ForEach-Object { ConvertTo-C05WindowsArgument ([string]$_) }) -join ' ' )
  return Start-Process -FilePath $FilePath -ArgumentList $line -WorkingDirectory $WorkingDirectory -WindowStyle Hidden -PassThru
}
function Invoke-C05Git([string]$RepoRoot,[string[]]$GitArgs) {
  $output=@(& git -C $RepoRoot -c core.quotepath=false @GitArgs 2>&1); $code=$LASTEXITCODE
  if($code -ne 0){throw "FAIL: git $($GitArgs -join ' ') exited ${code}: $($output -join ' ')"}
  return ,$output
}
function Get-C05SourceBoundary([string]$RepoRoot,[string[]]$AllowedPaths) {
  $headLines=Invoke-C05Git $RepoRoot @('rev-parse','HEAD'); $head=([string]($headLines|Select-Object -First 1)).Trim()
  $staged=@(Invoke-C05Git $RepoRoot @('diff','--cached','--name-only')) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
  $unstaged=@(Invoke-C05Git $RepoRoot @('diff','--name-only')) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
  $untracked=@(Invoke-C05Git $RepoRoot @('ls-files','--others','--exclude-standard')) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
  $all=@(@($staged)+@($unstaged)+@($untracked) | Select-Object -Unique)
  $unexpected=@($all | Where-Object { $AllowedPaths -notcontains [string]$_ })
  if($unexpected.Count){throw ('FAIL: staged/unstaged/untracked inputs outside C05 expectedFiles: '+($unexpected -join '; '))}
  $hashes=@{}
  foreach($path in $all) {
    $full=Join-Path $RepoRoot ($path.Replace('/',[IO.Path]::DirectorySeparatorChar))
    $worktreeHash=if(Test-Path -LiteralPath $full -PathType Leaf){(Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash}else{$null}
    $indexLines=Invoke-C05Git $RepoRoot @('ls-files','-s','--',$path); $indexLine=[string]($indexLines|Select-Object -First 1); $indexBlob=$null
    if($indexLine -match '^\d+\s+([0-9a-f]+)\s+(\d+)\t'){$indexBlob=$Matches[1]}
    $hashes[$path]=[pscustomobject]@{worktreeSha256=$worktreeHash;indexBlob=$indexBlob;staged=($staged -contains $path);unstaged=($unstaged -contains $path);untracked=($untracked -contains $path)}
  }
  return [pscustomobject]@{head=$head;staged=@($staged);unstaged=@($unstaged);untracked=@($untracked);dirtyPaths=@($all);unexpected=@();inputHashes=$hashes}
}
function Get-C05ProjectContract([string]$ProjectRoot) {
  $sceneFile=Join-Path $ProjectRoot 'ProjectSettings/EditorBuildSettings.asset';$settingsFile=Join-Path $ProjectRoot 'ProjectSettings/ProjectSettings.asset'
  if(-not(Test-Path -LiteralPath $sceneFile -PathType Leaf) -or -not(Test-Path -LiteralPath $settingsFile -PathType Leaf)){throw 'FAIL: Unity scene/Android project settings are missing.'}
  $sceneText=Get-Content -LiteralPath $sceneFile -Raw;$sceneMatches=[regex]::Matches($sceneText,'(?m)^\s*- enabled:\s*1\s*\r?\n\s*path:\s*(?<path>[^\r\n]+)')
  $enabled=@($sceneMatches|ForEach-Object{$_.Groups['path'].Value.Trim()})
  $settings=Get-Content -LiteralPath $settingsFile -Raw;$minMatch=[regex]::Match($settings,'(?m)^\s*AndroidMinSdkVersion:\s*(?<value>\d+)\s*$');$targetMatch=[regex]::Match($settings,'(?m)^\s*AndroidTargetSdkVersion:\s*(?<value>\d+)\s*$')
  if(-not $minMatch.Success -or -not $targetMatch.Success){throw 'FAIL: ProjectSettings lacks explicit Android SDK settings.'}
  return [pscustomobject]@{enabledScenes=$enabled;minSdk=[int]$minMatch.Groups['value'].Value;targetSdkSetting=[int]$targetMatch.Groups['value'].Value}
}
function Get-C05BadgingContract([string]$Metadata) {
  $packageLines=[regex]::Matches($Metadata,"(?m)^package:\s+name='(?<value>[^']+)'(?:\s|$).*$")
  $minLines=[regex]::Matches($Metadata,"(?m)^minSdkVersion:'(?<value>\d+)'\s*$");$targetLines=[regex]::Matches($Metadata,"(?m)^targetSdkVersion:'(?<value>\d+)'\s*$")
  $abiLines=[regex]::Matches($Metadata,'(?m)^native-code:\s*(?<value>[^\r\n]*)\s*$')
  if($packageLines.Count -ne 1 -or $minLines.Count -ne 1 -or $targetLines.Count -ne 1 -or $abiLines.Count -ne 1){throw 'FAIL: APK badging package/SDK/native-code records are missing or duplicated.'}
  $abiText=$abiLines[0].Groups['value'].Value;$abiList=[regex]::Match($abiText,"^'(?<abi>[A-Za-z0-9_.-]+)'(?:\s+'[A-Za-z0-9_.-]+')*$")
  if(-not $abiList.Success){throw 'FAIL: malformed APK native-code ABI record.'}
  $abis=@([regex]::Matches($abiText,"'(?<abi>[A-Za-z0-9_.-]+)'")|ForEach-Object{$_.Groups['abi'].Value})
  return [pscustomobject]@{package=$packageLines[0].Groups['value'].Value;minSdk=[int]$minLines[0].Groups['value'].Value;targetSdk=[int]$targetLines[0].Groups['value'].Value;abis=$abis}
}
function Get-C05ResolvedAutoTargetSdk([object]$Platforms) {
  $levels=@($Platforms|ForEach-Object{if([string]$_ -match '^android-(?<api>\d+)$'){[int]$Matches['api']}else{throw "FAIL: malformed accepted SDK platform entry '$_'."}})
  if(-not $levels.Count){throw 'FAIL: accepted toolchain has no Android platform to resolve automatic targetSdk.'}
  return [int](($levels|Measure-Object -Maximum).Maximum)
}
function Assert-C05BuildOutcome([object]$Report,[string]$ApkPath,[string]$Metadata,[datetime]$StartedAt,[string]$ProjectRoot,[int]$ResolvedAutoTargetSdk) {
  if ($null -eq $Report) { throw 'FAIL: missing Unity BuildReport marker.' }
  if ($Report.result -ne 'Succeeded' -or [int]$Report.errors -ne 0 -or $Report.target -ne 'Android' -or $Report.backend -ne 'IL2CPP' -or $Report.architectures -ne 'ARM64' -or -not $Report.tmpResources -or [int]$Report.minSdkVersion -lt 21 -or -not $Report.landscapeOnly -or -not $Report.fullscreen -or -not $Report.outsideSafeArea) { throw 'FAIL: Unity BuildReport did not satisfy full-game Android settings.' }
  $required=@('Assets/Scenes/Boot.unity','Assets/Scenes/Menu.unity','Assets/Scenes/Lobby.unity','Assets/Scenes/Table.unity','Assets/Scenes/Spectator.unity','Assets/Scenes/Settlement.unity');$contract=Get-C05ProjectContract $ProjectRoot
  foreach($scene in $required){if($contract.enabledScenes -notcontains $scene){throw "FAIL: required production scene is not enabled in project input: $scene"}}
  $reported=@($Report.scenes);if(-not $reported.Count -or $reported.Count -ne $contract.enabledScenes.Count){throw 'FAIL: BuildReport scenes are empty or differ from enabled project scenes.'}
  for($i=0;$i -lt $reported.Count;$i++){if([string]$reported[$i] -cne [string]$contract.enabledScenes[$i]){throw 'FAIL: BuildReport scene order/content differs from actual enabled project scenes.'}}
  if([int]$Report.minSdkVersion -ne $contract.minSdk -or [int]$Report.targetSdkSetting -ne $contract.targetSdkSetting){throw 'FAIL: BuildReport Android SDK settings differ from ProjectSettings.'}
  if (-not (Test-Path -LiteralPath $ApkPath -PathType Leaf)) { throw 'FAIL: fresh APK output missing.' }
  if ((Get-Item -LiteralPath $ApkPath).LastWriteTimeUtc -lt $StartedAt.ToUniversalTime()) { throw 'FAIL: APK is stale and predates this build attempt.' }
  if ($Report.output -ne [IO.Path]::GetFullPath($ApkPath)) { throw 'FAIL: BuildReport output path mismatch.' }
  $badging=Get-C05BadgingContract $Metadata;$expectedTarget=if($contract.targetSdkSetting -gt 0){$contract.targetSdkSetting}else{$ResolvedAutoTargetSdk}
  if($badging.package -cne [string]$Report.packageId -or $badging.abis.Count -ne 1 -or $badging.abis[0] -cne 'arm64-v8a' -or $badging.minSdk -lt 21 -or $badging.minSdk -ne [int]$Report.minSdkVersion -or $badging.targetSdk -ne $expectedTarget -or $badging.targetSdk -lt $badging.minSdk){throw 'FAIL: APK package, ARM64-only ABI or SDK metadata contradicts build/project settings.'}
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
    $projectRoot=Join-Path $testRoot 'project';$settingsDir=Join-Path $projectRoot 'ProjectSettings';New-Item -ItemType Directory -Force -Path $settingsDir|Out-Null
    $scenes=@('Assets/Scenes/Boot.unity','Assets/Scenes/Menu.unity','Assets/Scenes/Lobby.unity','Assets/Scenes/Table.unity','Assets/Scenes/Spectator.unity','Assets/Scenes/Settlement.unity')
    $sceneYaml="EditorBuildSettings:`n  m_Scenes:`n"+(@($scenes|ForEach-Object{"  - enabled: 1`n    path: $_`n"}) -join '')
    [IO.File]::WriteAllText((Join-Path $settingsDir 'EditorBuildSettings.asset'),$sceneYaml);[IO.File]::WriteAllText((Join-Path $settingsDir 'ProjectSettings.asset'),"AndroidMinSdkVersion: 25`nAndroidTargetSdkVersion: 0`n")
    $report=[pscustomobject]@{result='Succeeded';errors=0;target='Android';backend='IL2CPP';architectures='ARM64';tmpResources=$true;minSdkVersion=25;targetSdkSetting=0;landscapeOnly=$true;fullscreen=$true;outsideSafeArea=$true;output=[IO.Path]::GetFullPath($apk);packageId='com.majiang.game';scenes=$scenes}
    $metadata="package: name='com.majiang.game' versionCode='1'`nminSdkVersion:'25'`ntargetSdkVersion:'36'`nnative-code: 'arm64-v8a'"
    $rejected=0;foreach($bad in @($null,($report|Select-Object *))){if($null -ne $bad){$bad.result='Failed';$bad.errors=1};try{Assert-C05BuildOutcome $bad $apk $metadata $now $projectRoot 36|Out-Null}catch{$rejected++}}
    $badSettings=$report|Select-Object *;$badSettings.landscapeOnly=$false;try{Assert-C05BuildOutcome $badSettings $apk $metadata $now $projectRoot 36|Out-Null}catch{$rejected++}
    $oldTime=[DateTime]::UtcNow.AddDays(-1);[IO.File]::SetLastWriteTimeUtc($apk,$oldTime);try{Assert-C05BuildOutcome $report $apk $metadata $now $projectRoot 36|Out-Null}catch{$rejected++};[IO.File]::SetLastWriteTimeUtc($apk,[DateTime]::UtcNow)
    $sceneFile=Join-Path $settingsDir 'EditorBuildSettings.asset';$originalScenes=[IO.File]::ReadAllText($sceneFile)
    foreach($case in @('empty-scenes','missing-settlement')){$badReport=$report|Select-Object *;$badInput=if($case -eq 'empty-scenes'){@()}else{@($scenes|Where-Object{$_ -ne 'Assets/Scenes/Settlement.unity'})};$badReport.scenes=$badInput;$yaml="EditorBuildSettings:`n  m_Scenes:`n"+(@($badInput|ForEach-Object{"  - enabled: 1`n    path: $_`n"}) -join '');[IO.File]::WriteAllText($sceneFile,$yaml);try{Assert-C05BuildOutcome $badReport $apk $metadata $now $projectRoot 36|Out-Null}catch{$rejected++}}
    [IO.File]::WriteAllText($sceneFile,$originalScenes)
    $cases=New-Object System.Collections.Generic.List[string]
    foreach($case in @(@{name='tmp-false';change={param($r)$r.tmpResources=$false}},@{name='tmp-missing';change={param($r)$r.PSObject.Properties.Remove('tmpResources')}},@{name='abi-extra';change={param($r,$m)@{report=$r;metadata=$m.Replace("native-code: 'arm64-v8a'","native-code: 'arm64-v8a' 'x86_64'")}}},@{name='abi-wrong';change={param($r,$m)@{report=$r;metadata=$m.Replace("native-code: 'arm64-v8a'","x86_64'")}}},@{name='abi-malformed';change={param($r,$m)@{report=$r;metadata=$m.Replace("native-code: 'arm64-v8a'","unrelated-native-code: 'arm64-v8a'")}}},@{name='abi-missing';change={param($r,$m)@{report=$r;metadata=$m.Replace("native-code: 'arm64-v8a'",'')}}},@{name='abi-duplicate';change={param($r,$m)@{report=$r;metadata=($m+"`nnative-code: 'arm64-v8a'")}}},@{name='sdk-min-contradiction';change={param($r,$m)@{report=$r;metadata=$m.Replace("minSdkVersion:'25'","minSdkVersion:'21'")}}},@{name='sdk-target-auto-mismatch';change={param($r,$m)@{report=$r;metadata=$m.Replace("targetSdkVersion:'36'","targetSdkVersion:'35'")}}})){
      $badReport=$report|Select-Object *;$badMetadata=$metadata;$changed=& $case.change $badReport $badMetadata;if($changed -is [hashtable]){$badReport=$changed.report;$badMetadata=$changed.metadata};try{Assert-C05BuildOutcome $badReport $apk $badMetadata $now $projectRoot 36|Out-Null}catch{$rejected++;$cases.Add($case.name)}
    }
    $badMinReport=$report|Select-Object *;$badMinReport.minSdkVersion=21;$badMinMetadata=$metadata.Replace("minSdkVersion:'25'","minSdkVersion:'21'");try{Assert-C05BuildOutcome $badMinReport $apk $badMinMetadata $now $projectRoot 36|Out-Null}catch{$rejected++;$cases.Add('sdk-report-project-min-mismatch')}
    $badTargetReport=$report|Select-Object *;$badTargetReport.targetSdkSetting=35;try{Assert-C05BuildOutcome $badTargetReport $apk $metadata $now $projectRoot 36|Out-Null}catch{$rejected++;$cases.Add('sdk-report-project-target-mismatch')}
    $explicitSettings=Join-Path $settingsDir 'ProjectSettings.asset';[IO.File]::WriteAllText($explicitSettings,"AndroidMinSdkVersion: 25`nAndroidTargetSdkVersion: 35`n");$explicitReport=$report|Select-Object *;$explicitReport.targetSdkSetting=35;try{Assert-C05BuildOutcome $explicitReport $apk $metadata $now $projectRoot 36|Out-Null}catch{$rejected++;$cases.Add('sdk-target-explicit-mismatch')};[IO.File]::WriteAllText($explicitSettings,"AndroidMinSdkVersion: 25`nAndroidTargetSdkVersion: 0`n")
    if (-not (Assert-C05BuildOutcome $report $apk $metadata $now $projectRoot 36)) { throw 'valid fixture artifact rejected' }
    if ($rejected -ne 18 -or $cases.Count -ne 12) { throw "build gate negative matrix rejected=$rejected sdkAbiTmpCases=$($cases.Count) expectedRejected=18 expectedSdkAbiTmp=12" }
    Write-Output 'VERDICT_FIXTURE PASS missing-marker failed-report invalid-landscape-settings stale-apk-rejected empty-missing-scene tmp-false-missing extra-wrong-missing-duplicate-malformed-ABI sdk-min-target-contradictions valid-fixture-accepted'; exit 0
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
if ($SourceFixtureTest) {
  $fixtureRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')); $fixture=Join-Path $fixtureRoot ('build\p45-source-fixtures\'+[guid]::NewGuid().ToString('N')); New-Item -ItemType Directory -Force -Path $fixture|Out-Null
  try {
    & git -C $fixture init -q; if($LASTEXITCODE -ne 0){throw 'fixture git init failed'}
    & git -C $fixture config user.email fixture@invalid; if($LASTEXITCODE -ne 0){throw 'fixture git config failed'}
    & git -C $fixture config user.name C05Fixture; if($LASTEXITCODE -ne 0){throw 'fixture git config failed'}
    $sceneDir=Join-Path $fixture 'unity\Mahjong.Unity\Assets\Scenes'; New-Item -ItemType Directory -Force -Path $sceneDir|Out-Null
    $menu=Join-Path $sceneDir 'Menu.unity'; $allowedFile=Join-Path $fixture 'allowed-input.txt'; [IO.File]::WriteAllText($menu,'scene-base'); [IO.File]::WriteAllText($allowedFile,'allowed-base')
    & git -C $fixture add --all; if($LASTEXITCODE -ne 0){throw 'fixture git add failed'}; & git -C $fixture commit -qm fixture; if($LASTEXITCODE -ne 0){throw 'fixture git commit failed'}
    $relativeScene='unity/Mahjong.Unity/Assets/Scenes/Menu.unity'; $allowed=@('allowed-input.txt'); $rejected=0
    [IO.File]::WriteAllText($menu,'scene-staged'); & git -C $fixture add -- $relativeScene; if($LASTEXITCODE -ne 0){throw 'fixture stage failed'}
    try{Get-C05SourceBoundary $fixture $allowed|Out-Null}catch{if($_.Exception.Message -like 'FAIL: staged/unstaged/untracked*'){$rejected++}else{throw}}
    & git -C $fixture reset --hard -q HEAD; if($LASTEXITCODE -ne 0){throw 'fixture reset staged failed'}
    [IO.File]::WriteAllText($menu,'scene-unstaged'); try{Get-C05SourceBoundary $fixture $allowed|Out-Null}catch{if($_.Exception.Message -like 'FAIL: staged/unstaged/untracked*'){$rejected++}else{throw}}
    [IO.File]::WriteAllText($menu,'scene-base'); [IO.File]::WriteAllText((Join-Path $fixture 'unexpected.txt'),'untracked'); try{Get-C05SourceBoundary $fixture $allowed|Out-Null}catch{if($_.Exception.Message -like 'FAIL: staged/unstaged/untracked*'){$rejected++}else{throw}}
    [IO.File]::Delete((Join-Path $fixture 'unexpected.txt'))
    [IO.File]::WriteAllText($allowedFile,'staged-content'); & git -C $fixture add -- allowed-input.txt; if($LASTEXITCODE -ne 0){throw 'fixture allowed stage failed'}; [IO.File]::WriteAllText($allowedFile,'built-worktree-content')
    $result=Get-C05SourceBoundary $fixture $allowed; $expected=(Get-FileHash $allowedFile -Algorithm SHA256).Hash
    if($rejected -ne 3 -or $result.dirtyPaths.Count -ne 1 -or $result.inputHashes['allowed-input.txt'].worktreeSha256 -ne $expected -or -not $result.inputHashes['allowed-input.txt'].indexBlob){throw 'source fixture failed to reject all states/bind permitted dirty bytes'}
    $nonGit=Join-Path $fixture 'missing-repo-path'; $gitFailed=$false; try{Get-C05SourceBoundary $nonGit $allowed|Out-Null}catch{$gitFailed=$true}; if(-not $gitFailed){throw 'git native failure was not rejected'}
    Write-Output 'SOURCE_FIXTURE PASS staged-unstaged-untracked-rejected allowed-bytes-bound git-failure-rejected'; exit 0
  } finally {$full=[IO.Path]::GetFullPath($fixture);$base=[IO.Path]::GetFullPath((Join-Path $fixtureRoot 'build'))+[IO.Path]::DirectorySeparatorChar;if(-not $full.StartsWith($base,[StringComparison]::OrdinalIgnoreCase)){throw 'unsafe source fixture cleanup'};Get-ChildItem -LiteralPath $full -Force -Recurse -ErrorAction SilentlyContinue|ForEach-Object{$_.Attributes=[IO.FileAttributes]::Normal};[IO.Directory]::Delete($full,$true)}
}
if ($ArgumentFixtureTest) {
  $fixtureRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')); $dir=Join-Path $fixtureRoot ('build\p45-argv-fixtures\'+[guid]::NewGuid().ToString('N')); New-Item -ItemType Directory -Force -Path $dir|Out-Null
  try{$exe=Join-Path $dir 'argv recorder.exe';$record=Join-Path $dir 'args.json';$env:C05_ARGV_RECORD=$record
    $source='using System;using System.IO;using System.Linq;using System.Text;public static class R{public static int Main(){var a=Environment.GetCommandLineArgs().Skip(1).Select(x=>Convert.ToBase64String(Encoding.UTF8.GetBytes(x))).ToArray();File.WriteAllLines(Environment.GetEnvironmentVariable("C05_ARGV_RECORD"),a);return 0;}}'
    Add-Type -TypeDefinition $source -OutputAssembly $exe -OutputType ConsoleApplication -ReferencedAssemblies System.Web.Extensions
    $expected=@('ordinary','D:\fixture space\Mahjong.apk','quote"inside','C:\tail\')
    $child=Start-C05HiddenProcess $exe $expected $dir; if(-not $child.WaitForExit(10000)){[void](Stop-C05OwnedProcess $child.Id $child.StartTime.ToUniversalTime().Ticks);throw 'argv fixture timed out'}
    if(-not(Test-Path $record)){throw "argv fixture recorder did not run exit=$($child.ExitCode)"};$actual=@(Get-Content $record|ForEach-Object{[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($_))}); if($child.ExitCode -ne 0 -or $actual.Count -ne $expected.Count){throw "argv fixture count/exit mismatch exit=$($child.ExitCode) count=$($actual.Count)"}; for($i=0;$i -lt $expected.Count;$i++){if($actual[$i] -cne $expected[$i]){throw "argv fixture mismatch at $i actual=[$($actual[$i])] expected=[$($expected[$i])]"}}
    Write-Output 'ARGUMENT_FIXTURE PASS spaces-quotes-trailing-separator-preserved'; exit 0
  }finally{Remove-Item Env:C05_ARGV_RECORD -ErrorAction SilentlyContinue;$full=[IO.Path]::GetFullPath($dir);$base=[IO.Path]::GetFullPath((Join-Path $fixtureRoot 'build'))+[IO.Path]::DirectorySeparatorChar;if(-not $full.StartsWith($base,[StringComparison]::OrdinalIgnoreCase)){throw 'unsafe argv fixture cleanup'};[IO.Directory]::Delete($full,$true)}
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
$docDirs=@(Get-ChildItem -LiteralPath (Join-Path $root 'doc') -Directory | Where-Object { $_.Name -like 'v5-*' })
$deliveryDoc=$null; foreach($dir in $docDirs){$candidate=Join-Path $dir.FullName 'V5-G2-C05-ANDROID-GAME-DELIVERY-20261101.md';if(Test-Path -LiteralPath $candidate){$deliveryDoc=$candidate;break}}
if(-not $deliveryDoc){throw 'C05 delivery report expectedFile missing.'}
$deliveryDocRelative=$deliveryDoc.Substring($root.Length+1).Replace('\','/')
$requiredFiles=@('tools/unity/build-p45-android-game.ps1','tools/unity/run-p45-android-game.ps1','tools/tests/VerifyP45AndroidGame.Tests.ps1','unity/Mahjong.Unity/Assets/Editor/C05FullGameAndroidBuild.cs','unity/Mahjong.Unity/Assets/Editor/C05FullGameAndroidBuild.cs.meta',$deliveryDocRelative)
foreach($relative in $requiredFiles){if(-not (Test-Path -LiteralPath (Join-Path $root $relative) -PathType Leaf)){throw "C05 expectedFile missing: $relative"}}
$sourceState=Get-C05SourceBoundary $root $requiredFiles; $head=$sourceState.head; $dirty=@($sourceState.dirtyPaths)
$inputHashes=[ordered]@{taskInputs=$sourceState.inputHashes;productionInputs=@{}}
foreach($rel in @('unity/Mahjong.Unity/Assets/Editor/C05FullGameAndroidBuild.cs','unity/Mahjong.Unity/Assets/Editor/C05FullGameAndroidBuild.cs.meta','unity/Mahjong.Unity/ProjectSettings/EditorBuildSettings.asset','unity/Mahjong.Unity/ProjectSettings/ProjectSettings.asset','unity/Mahjong.Unity/Packages/manifest.json','unity/Mahjong.Unity/Packages/packages-lock.json')) { $inputHashes.productionInputs[$rel]=(Get-FileHash (Join-Path $root $rel) -Algorithm SHA256).Hash }
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
  $proc = Start-C05HiddenProcess $unity $args $project
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
  $resolvedAutoTarget=Get-C05ResolvedAutoTargetSdk $toolchain.sdkPackages.platforms
  [void](Assert-C05BuildOutcome $report $apk $metadata $start $project $resolvedAutoTarget)
  [pscustomobject]@{ status='PASS';sourceHead=$head;dirtyPaths=@($dirty);inputSha256=$inputHashes;unityVersion=$unityVersion;unityPath='Unity Hub 6000.3.27f1';androidPlayer=(Test-Path $android);toolchainManifestSha256=$manifestSha;androidToolchain=[pscustomobject]@{jdkVersion=$toolchain.jdkVersion;ndkRevision=$toolchain.ndkRevision;cmakeVersion=$toolchain.cmakeVersion;ninjaVersion=$toolchain.ninjaVersion;androidCliMode=$toolchain.androidCliMode;sdkPackages=$toolchain.sdkPackages};apk=(Split-Path $apk -Leaf);apkBytes=$apkInfo.Length;apkSha256=$hash;nativeExit=$native;buildReport=$report;elapsedSeconds=[math]::Round(((Get-Date)-$start).TotalSeconds,1);evidenceRoot='build/p45-android-game/<run>';metadata='apk-metadata.txt';adbPresent=(Test-Path $adb) } | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 (Join-Path $run 'summary.json')
  Write-Host "PASS APK bytes=$($apkInfo.Length) SHA256=$hash evidence=build/p45-android-game/<run>"
} catch {
  $reportLine=$null; if (Test-Path $unityLog) { $reportLine=Select-String -Path $unityLog -Pattern 'C05_BUILD_REPORT: (\{.*\})' | Select-Object -Last 1 }
  $failureStatus=if($_.Exception.Message -like 'BLOCKED:*'){'BLOCKED'}elseif($_.Exception.Message -like 'NOT_RUN:*'){'NOT_RUN'}else{'FAIL'}
  [pscustomobject]@{status=$failureStatus;sourceHead=$head;dirtyPaths=@($dirty);inputSha256=$inputHashes;toolchainManifestSha256=$manifestSha;nativeExit=$native;error=$_.Exception.Message;buildReportLine=$(if($reportLine){$reportLine.Matches[0].Groups[1].Value}else{$null});apkExists=(Test-Path $apk);evidenceRoot='build/p45-android-game/<run>';unityLog='unity.log'} | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 (Join-Path $run 'failure-summary.json')
  throw
} finally { if (Get-Variable oldCli -ErrorAction SilentlyContinue) { $env:ANDROID_CLI_BIN=$oldCli; $env:JAVA_HOME=$oldJava; $env:PATH=$oldPath }; if ($held) { $mutex.ReleaseMutex() }; $mutex.Dispose() }
