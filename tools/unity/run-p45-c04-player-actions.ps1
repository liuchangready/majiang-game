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
    $stdoutTask = $process.StandardOutput.ReadToEndAsync(); $stderrTask = $process.StandardError.ReadToEndAsync()
    $exited = $process.WaitForExit($Timeout * 1000)
    if (-not $exited) { try { $process.Kill() } catch {}; $process.WaitForExit(); $exit = 124 } else { $exit = $process.ExitCode }
    [IO.File]::WriteAllText($StdoutPath, $stdoutTask.Result, (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText($StderrPath, $stderrTask.Result, (New-Object Text.UTF8Encoding($false)))
    $record = [ordered]@{ name=$Name; file=$FilePath; arguments=$Arguments; workingDirectory=$WorkingDirectory; startedUtc=$started.ToString('o'); endedUtc=[DateTimeOffset]::UtcNow.ToString('o'); exitCode=$exit; timedOut=(-not $exited); stdout=$StdoutPath; stderr=$StderrPath; stdoutSha256=(Get-Sha256 $StdoutPath); stderrSha256=(Get-Sha256 $StderrPath) }
    $script:stageRecords.Add($record) | Out-Null
    Write-RunLog "$Name nativeExit=$exit timeout=$(-not $exited)"
    return $record
}

function Quote-Arg([string]$value) { '"' + $value.Replace('"','\"') + '"' }

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
    param($Summary,[string]$ExpectedCase,[string]$ExpectedPayloadSha256)
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
    if ($Summary.payloadSha256 -notmatch '^[0-9a-f]{64}$' -or $Summary.payloadSha256 -ne $ExpectedPayloadSha256) { throw 'C04 canonical payload hash is missing or mismatched.' }
    if ([string]::IsNullOrWhiteSpace($Summary.canonicalPayloadBase64)) { throw 'C04 canonical payload bytes are missing.' }
    $payloadBytes = [Convert]::FromBase64String($Summary.canonicalPayloadBase64)
    $payloadHash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($payloadBytes)).Replace('-','').ToLowerInvariant()
    if ($payloadHash -ne $Summary.payloadSha256) { throw 'C04 canonical payload bytes do not match the recorded hash.' }
    if ($Summary.receiptStatus -ne 'Applied' -or [UInt64]$Summary.receiptCommandVersion -eq 0) { throw 'C04 Applied receipt binding is missing.' }
    if ([UInt64]$Summary.eventCommandVersion -ne [UInt64]$Summary.receiptCommandVersion) { throw 'C04 event does not match Applied command version.' }
    if ([UInt64]$Summary.stateVersionAfter -le [UInt64]$Summary.stateVersionBefore) { throw 'C04 authoritative state did not advance.' }
    if ([UInt64]$Summary.eventSeqAfter -lt [UInt64]$Summary.eventSeq -or [UInt64]$Summary.eventSeq -le [UInt64]$Summary.eventSeqBefore) { throw 'C04 event cursor/high-water is invalid.' }
    if ([UInt64]$Summary.bindingEpoch -eq 0 -or [UInt64]$Summary.ownerEpoch -eq 0 -or [UInt64]$Summary.controlEpoch -eq 0) { throw 'C04 epoch identity is incomplete.' }
    $eventByCase = @{ 'C04-G2-01'='PonDeclared'; 'C04-G2-02'='ChiDeclared'; 'C04-G2-03'='RiichiDeclared'; 'C04-G2-04'='KanDeclared' }
    if (-not $eventByCase.ContainsKey($ExpectedCase) -or $Summary.eventType -ne $eventByCase[$ExpectedCase]) { throw 'C04 action event type does not match its fixed case.' }
    return $true
}

if ($FixtureOnly) {
    $fixtureBytes = [Text.Encoding]::UTF8.GetBytes('C04 canonical payload fixture')
    $fixtureHash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($fixtureBytes)).Replace('-','').ToLowerInvariant()
    $valid = [pscustomobject]@{
        result='PASS'; status='GAME'; eventConfirmed=$true; nativeExitCode=0; nativeTimedOut=$false
        caseId='C04-G2-01'; seat=0; eventSeat=0; roomId='fixture-room'; sessionId='fixture-session'; roundId='fixture-round'
        eventRoundId='fixture-round'; requestId='fixture-request'; scopeId='fixture-scope'; receiptRequestId='fixture-request'; receiptScopeId='fixture-scope'; payloadSha256=$fixtureHash
        canonicalPayloadBase64=[Convert]::ToBase64String($fixtureBytes); receiptStatus='Applied'; receiptCommandVersion=9
        eventCommandVersion=9; stateVersionBefore=11; stateVersionAfter=12; eventSeqBefore=20; eventSeqAfter=21; eventSeq=21
        bindingEpoch=1; ownerEpoch=2; controlEpoch=3; eventType='PonDeclared'
    }
    Test-C04CaseSummary $valid 'C04-G2-01' $fixtureHash | Out-Null
    $negatives = @(
        @{ name='AppliedWithoutEvent'; update=@{ eventConfirmed=$false; eventType='' } },
        @{ name='WrongRound'; update=@{ eventRoundId='other-round' } },
        @{ name='WrongSeat'; update=@{ eventSeat=1 } },
        @{ name='WrongIdentity'; update=@{ receiptRequestId='foreign-request' } },
        @{ name='WrongEpoch'; update=@{ bindingEpoch=0 } },
        @{ name='NativeNonzero'; update=@{ nativeExitCode=17 } },
        @{ name='NativeTimeout'; update=@{ nativeTimedOut=$true } },
        @{ name='MissingCase'; update=@{ caseId='' } },
        @{ name='MissingPayload'; update=@{ canonicalPayloadBase64='' } },
        @{ name='HashMismatch'; update=@{ payloadSha256=('0' * 64) } }
    )
    foreach ($negative in $negatives) {
        $candidate = $valid | Select-Object *
        foreach ($key in $negative.update.Keys) { $candidate.$key = $negative.update[$key] }
        $rejected = $false
        try { Test-C04CaseSummary $candidate 'C04-G2-01' $fixtureHash | Out-Null } catch { $rejected = $true }
        if (-not $rejected) { throw "Fixture guard accepted invalid case: $($negative.name)" }
    }
    Write-Host "C04_TOOL_FIXTURES_PASS positive=1 negative=$($negatives.Count)"
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

    $gitHead = (& git -C $repo rev-parse HEAD).Trim()
    $gitStatus = (& git -C $repo status --short).Trim()
    $gitDiffCheck = & git -C $repo diff --check 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'Source diff --check failed before build.' }
    $sourceCopyArgs = @('"' + $repo + '"','"' + $source + '"','/E','/COPY:DAT','/DCOPY:DAT','/R:1','/W:1','/NFL','/NDL','/NJH','/NJS','/NP','/XD','.git','build','Library','Temp','Logs','obj','bin','.vs','UserSettings','Builds') -join ' '
    $copy = Invoke-OwnedNative -Name 'CopyFrozenSource' -FilePath (Join-Path $env:SystemRoot 'System32\robocopy.exe') -Arguments $sourceCopyArgs -WorkingDirectory $repo -StdoutPath (Join-Path $logs 'source-copy.stdout.txt') -StderrPath (Join-Path $logs 'source-copy.stderr.txt') -Timeout 600
    if ($copy.exitCode -gt 7) { throw "Frozen source copy failed with native exit $($copy.exitCode)." }
    $sourceClosureSha = Get-ClosureManifest $source (Join-Path $RunRoot 'source-closure.json')

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

    $playXml = Join-Path $logs 'playmode.xml'; $playLog = Join-Path $logs 'playmode.unity.log'
    $unityTestArgs = @('-batchmode','-nographics','-projectPath',(Quote-Arg $project),'-runTests','-testPlatform','PlayMode','-testFilter','Mahjong.Unity.Tests.PlayMode.C04PlayerActionScenarioTests','-testResults',(Quote-Arg $playXml),'-logFile',(Quote-Arg $playLog),'-quit') -join ' '
    $play = Invoke-OwnedNative -Name 'UnityPlayModeC04' -FilePath $UnityPath -Arguments $unityTestArgs -WorkingDirectory $project -StdoutPath (Join-Path $logs 'playmode.stdout.txt') -StderrPath (Join-Path $logs 'playmode.stderr.txt') -Timeout 900
    if ($play.exitCode -ne 0 -or -not (Test-Path -LiteralPath $playXml)) { throw 'Targeted Unity PlayMode run failed or produced no XML.' }
    [xml]$playResults = Get-Content -LiteralPath $playXml -Raw
    $testNodes = @($playResults.SelectNodes('//test-case'))
    $failedNodes = @($testNodes | Where-Object { $_.GetAttribute('result') -ne 'Passed' })
    if ($testNodes.Count -ne 4 -or $failedNodes.Count -ne 0) { throw "PlayMode must report four real cases, all Passed; actual=$($testNodes.Count) failed=$($failedNodes.Count)." }

    New-Item -ItemType Directory -Path $playerDir | Out-Null
    $exe = Join-Path $playerDir 'Mahjong.Unity.exe'
    $buildMarker = Join-Path $logs 'player-build.json'
    $buildArgs = @('-batchmode','-nographics','-projectPath',(Quote-Arg $project),'-executeMethod','C04PlayerActionBuild.Build','-c04-player-output',(Quote-Arg $exe),'-c04-player-marker',(Quote-Arg $buildMarker),'-logFile',(Quote-Arg (Join-Path $logs 'player-build.unity.log')),'-quit') -join ' '
    $build = Invoke-OwnedNative -Name 'BuildFreshWindowsPlayer' -FilePath $UnityPath -Arguments $buildArgs -WorkingDirectory $project -StdoutPath (Join-Path $logs 'player-build.stdout.txt') -StderrPath (Join-Path $logs 'player-build.stderr.txt') -Timeout 1200
    if ($build.exitCode -ne 0 -or -not (Test-Path -LiteralPath $exe)) { throw 'Fresh Windows Player build failed.' }

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
        $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
        if ($summary.eventType -ne $case.event) { throw "Player $($case.id) visible event mismatch: $($summary.eventType)." }
        $summary | Add-Member -NotePropertyName nativeExitCode -NotePropertyValue $player.exitCode -Force
        $summary | Add-Member -NotePropertyName nativeTimedOut -NotePropertyValue $player.timedOut -Force
        $executionPath = Join-Path $caseDir 'execution.json'
        [IO.File]::WriteAllText($executionPath, (($summary | ConvertTo-Json -Depth 8) + "`n"), (New-Object Text.UTF8Encoding($false)))
        Test-C04CaseSummary $summary $case.id $summary.payloadSha256 | Out-Null
        if ($summary.runId -ne $runId) { throw "Player $($case.id) run identity mismatch." }
        $caseResults.Add([ordered]@{ caseId=$case.id; status='PASS'; layer='WINDOWS_PLAYER'; nativeExitCode=$player.exitCode; summary=$summaryPath; summarySha256=(Get-Sha256 $summaryPath); executionEvidence=$executionPath; executionEvidenceSha256=(Get-Sha256 $executionPath); playerLog=$nativeLog; playerLogSha256=(Get-Sha256 $nativeLog); payloadSha256=$summary.payloadSha256 }) | Out-Null
    }

    $final = [ordered]@{ schemaVersion=1; task='G2-C04-D03'; status='PASS'; acceptedBase='8fd7c6cb701dfbb41c28938de4186749bdd1d6c7'; frozenCandidate=$gitHead; sourceTreeStatus=$gitStatus; sourceDiffCheck='PASS'; sourceClosureManifest='source-closure.json'; sourceClosureManifestSha256=$sourceClosureSha; commands=$script:stageRecords; dotnetLocalHost='see native stages'; playMode=[ordered]@{ testCount=$testNodes.Count; passed=$testNodes.Count; failed=0; skipped=0; xml=$playXml; xmlSha256=(Get-Sha256 $playXml) }; buildMarker=$buildMarker; buildMarkerSha256=(Get-Sha256 $buildMarker); playerPackageManifest='player-package.json'; cases=$caseResults; platforms=[ordered]@{ windowsPlayer='actual four fixed cases'; android='NOT_RUN (C05 owner)'; ios='NOT_RUN'; remoteTransport='NOT_IN_SCOPE' } }
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
