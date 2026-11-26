#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$UnityPath = 'D:/game_dev_tools/unity/Editor/Unity.exe',
    [string]$DotnetPath = 'D:/game_dev_tools/dotnet/dotnet.exe',
    [string]$RunRoot,
    [int]$TimeoutSeconds = 1500,
    [int]$MutexTimeoutSeconds = 600,
    [int]$MutexHeartbeatSeconds = 30,
    [string]$MutexCancelFile
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $RunRoot) { $RunRoot = Join-Path $repo ('build/' + [guid]::NewGuid().ToString('N')) }
$RunRoot = [IO.Path]::GetFullPath($RunRoot)
$buildRoot = [IO.Path]::GetFullPath((Join-Path $repo 'build')) + [IO.Path]::DirectorySeparatorChar
if (-not $RunRoot.StartsWith($buildRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'RunRoot must be a new descendant of this worktree build/.' }
if (Test-Path -LiteralPath $RunRoot) { throw 'RunRoot exists; refusing to overwrite evidence.' }
if (-not (Test-Path -LiteralPath $UnityPath -PathType Leaf)) { throw "Unity executable missing: $UnityPath" }
if (-not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) { throw ".NET executable missing: $DotnetPath" }
if ($TimeoutSeconds -lt 60 -or $TimeoutSeconds -gt 1800) { throw 'TimeoutSeconds must be 60..1800.' }
if ($MutexTimeoutSeconds -lt 1 -or $MutexTimeoutSeconds -gt 600) { throw 'MutexTimeoutSeconds must be 1..600.' }
if ($MutexHeartbeatSeconds -lt 1 -or $MutexHeartbeatSeconds -gt 30) { throw 'MutexHeartbeatSeconds must be 1..30.' }

$null = New-Item -ItemType Directory -Path $RunRoot
$logs = Join-Path $RunRoot 'logs'; $player = Join-Path $RunRoot 'player'
$null = New-Item -ItemType Directory -Path $logs,$player
$mutexName = 'Local\MahjongV5P45UnityValidation'
$mutex = New-Object System.Threading.Mutex($false, $mutexName)
$held = $false; $unityProcess = $null; $exitCode = 1; $stage = 'WAIT_MUTEX'
$startedUtc = [DateTimeOffset]::UtcNow
function Write-Stage([string]$message) {
    $line = '[' + [DateTimeOffset]::UtcNow.ToString('o') + '] ' + $message
    Write-Host $line; Add-Content -LiteralPath (Join-Path $logs 'runner.log') -Value $line -Encoding UTF8
}
function Restore-UnityRunNoise {
    # These files are Unity/test-runner side effects in this clean task worktree,
    # not candidate inputs. The source files and shared plugin DLL/PDBs are untouched.
    $paths = @(
        'unity/Mahjong.Unity/Assets/Art/UI/Fonts/NotoSansSC-Regular SDF.asset',
        'unity/Mahjong.Unity/Assets/Resources/PerformanceTestRunInfo.json',
        'unity/Mahjong.Unity/Assets/Resources/PerformanceTestRunInfo.json.meta',
        'unity/Mahjong.Unity/Assets/Resources/PerformanceTestRunSettings.json',
        'unity/Mahjong.Unity/Assets/Resources/PerformanceTestRunSettings.json.meta',
        'unity/Mahjong.Unity/Assets/Settings/UniversalRP.asset',
        'unity/Mahjong.Unity/ProjectSettings/ProjectSettings.asset'
    )
    & git -C $repo restore --worktree -- $paths
    if ($LASTEXITCODE -ne 0) { throw 'Could not restore known Unity run-generated tracked files.' }
    $assetRoot = Join-Path $repo 'unity/Mahjong.Unity/Assets'
    foreach ($item in @(Get-ChildItem -LiteralPath $assetRoot -File -Filter 'InitTestScene*.unity' -ErrorAction SilentlyContinue)) {
        $relative = $item.FullName.Substring($repo.Length + 1).Replace('\','/')
        $tracked = @(& git -C $repo ls-files -- $relative)
        if ($tracked.Count -eq 0) {
            Remove-Item -LiteralPath $item.FullName
            $meta = $item.FullName + '.meta'
            if (Test-Path -LiteralPath $meta) { Remove-Item -LiteralPath $meta }
        }
    }
    $sceneTemplate = Join-Path $repo 'unity/Mahjong.Unity/ProjectSettings/SceneTemplateSettings.json'
    if (Test-Path -LiteralPath $sceneTemplate) {
        $relative = 'unity/Mahjong.Unity/ProjectSettings/SceneTemplateSettings.json'
        $tracked = @(& git -C $repo ls-files -- $relative)
        if ($tracked.Count -eq 0) { Remove-Item -LiteralPath $sceneTemplate }
    }
}
function Get-HashWithRetry([string]$path) {
    for ($attempt = 0; $attempt -lt 8; $attempt++) {
        try { return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
        catch { if ($attempt -eq 7) { throw }; Start-Sleep -Seconds 1 }
    }
}
function Quote-Arg([string]$value) { '"' + $value.Replace('"','\"') + '"' }
function Invoke-Bounded([string]$file,[string]$arguments,[string]$name,[int]$timeout) {
    $stdout = Join-Path $logs ($name + '.stdout.txt'); $stderr = Join-Path $logs ($name + '.stderr.txt')
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $file; $startInfo.Arguments = $arguments; $startInfo.WorkingDirectory = $repo
    $startInfo.UseShellExecute = $false; $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true; $startInfo.RedirectStandardError = $true
    $p = New-Object System.Diagnostics.Process; $p.StartInfo = $startInfo
    if (-not $p.Start()) { throw "Could not start owned process: $name" }
    $stdoutTask = $p.StandardOutput.ReadToEndAsync(); $stderrTask = $p.StandardError.ReadToEndAsync()
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($timeout); $next = [DateTimeOffset]::UtcNow.AddSeconds($MutexHeartbeatSeconds)
    while (-not $p.WaitForExit(1000) -and [DateTimeOffset]::UtcNow -lt $deadline) {
        if ([DateTimeOffset]::UtcNow -ge $next) { Write-Stage "HEARTBEAT stage=$name pid=$($p.Id) elapsedSeconds=$([int]([DateTimeOffset]::UtcNow-$startedUtc).TotalSeconds) timeoutSeconds=$timeout"; $next = [DateTimeOffset]::UtcNow.AddSeconds($MutexHeartbeatSeconds) }
        if ($MutexCancelFile -and (Test-Path -LiteralPath $MutexCancelFile)) {
            $p.CloseMainWindow() | Out-Null
            if (-not $p.WaitForExit(10000)) { $p.Kill(); $p.WaitForExit() }
            [IO.File]::WriteAllText($stdout,$stdoutTask.Result); [IO.File]::WriteAllText($stderr,$stderrTask.Result)
            throw "$name cancelled by request."
        }
    }
    if (-not $p.HasExited) {
        $p.CloseMainWindow() | Out-Null
        if (-not $p.WaitForExit(10000)) { $p.Kill(); $p.WaitForExit() }
        [IO.File]::WriteAllText($stdout,$stdoutTask.Result); [IO.File]::WriteAllText($stderr,$stderrTask.Result)
        throw "$name exceeded its $timeout second bound."
    }
    $p.WaitForExit()
    [IO.File]::WriteAllText($stdout,$stdoutTask.Result)
    [IO.File]::WriteAllText($stderr,$stderrTask.Result)
    return [pscustomobject]@{ exitCode=$p.ExitCode; stdout=$stdout; stderr=$stderr; pid=$p.Id }
}

try {
    Write-Stage "WAIT_MUTEX name=$mutexName timeoutSeconds=$MutexTimeoutSeconds"
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($MutexTimeoutSeconds); $next = [DateTimeOffset]::UtcNow.AddSeconds($MutexHeartbeatSeconds)
    while (-not $held -and [DateTimeOffset]::UtcNow -lt $deadline) {
        if ($MutexCancelFile -and (Test-Path -LiteralPath $MutexCancelFile)) { throw 'Cancelled while waiting for Unity mutex.' }
        try { $held = $mutex.WaitOne(1000) } catch [System.Threading.AbandonedMutexException] { $held = $true }
        if (-not $held -and [DateTimeOffset]::UtcNow -ge $next) { Write-Stage "HEARTBEAT stage=WAIT_MUTEX waitedSeconds=$([int]([DateTimeOffset]::UtcNow-$startedUtc).TotalSeconds)"; $next = [DateTimeOffset]::UtcNow.AddSeconds($MutexHeartbeatSeconds) }
    }
    if (-not $held) { throw "Timed out waiting for shared Unity mutex after $MutexTimeoutSeconds seconds." }
    Write-Stage 'MUTEX_ACQUIRED'

    $stage = 'BUILD_SHARED'
    $sharedArgs = @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',(Join-Path $repo 'tools/unity/build-shared.ps1'),'-Configuration','Release','-DotnetPath',$DotnetPath) | ForEach-Object { Quote-Arg ([string]$_) }
    $shared = Invoke-Bounded 'powershell.exe' ($sharedArgs -join ' ') 'build-shared' 1200
    if ($shared.exitCode -ne 0) { throw "build-shared.ps1 failed with native exit $($shared.exitCode)." }

    $stage = 'CHECK_SHARED'
    $checkArgs = @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',(Join-Path $repo 'tools/unity/check-shared-plugins.ps1'),'-Configuration','Release','-Quiet') | ForEach-Object { Quote-Arg ([string]$_) }
    $check = Invoke-Bounded 'powershell.exe' ($checkArgs -join ' ') 'check-shared-plugins' 300
    if ($check.exitCode -ne 0) { throw "check-shared-plugins.ps1 failed with native exit $($check.exitCode)." }

    $stage = 'EDITMODE_REGRESSION'
    $unityProject = Join-Path $repo 'unity/Mahjong.Unity'
    $editXml = Join-Path $RunRoot 'editmode-results.xml'
    $editArgs = @('-batchmode','-projectPath',(Quote-Arg $unityProject),'-runTests','-testPlatform','EditMode',
        '-testFilter','Mahjong.Unity.Tests.EditMode.AppCompositionTests','-testResults',(Quote-Arg $editXml),
        '-logFile',(Quote-Arg (Join-Path $logs 'unity-editmode.log'))) -join ' '
    $edit = Invoke-Bounded $UnityPath $editArgs 'unity-editmode' 900
    if ($edit.exitCode -ne 0 -or -not (Test-Path -LiteralPath $editXml -PathType Leaf))
        { throw "Unity EditMode regression failed or produced no XML (native exit $($edit.exitCode))." }
    [xml]$editResult = Get-Content -LiteralPath $editXml -Raw -Encoding UTF8
    $editCases = @($editResult.SelectNodes('//test-case'))
    $editFailures = @($editResult.SelectNodes('//test-case[@result != "Passed"]'))
    $liteCase = @($editCases | Where-Object { $_.fullname -like '*LocalLiteGamePreparation_RotatesRoomIdWithoutOpeningAHost' -and $_.result -eq 'Passed' })
    if ($editCases.Count -eq 0 -or $editFailures.Count -ne 0 -or $liteCase.Count -ne 1)
        { throw "EditMode XML did not prove a nonzero clean AppCompositionTests run including the Lite room rotation case. cases=$($editCases.Count) failures=$($editFailures.Count) lite=$($liteCase.Count)" }
    Write-Stage "EDITMODE_PASS tests=$($editCases.Count) failures=0 xml=$editXml"
    Restore-UnityRunNoise

    $fontXml = Join-Path $RunRoot 'editmode-lite-font.xml'
    $fontArgs = @('-batchmode','-projectPath',(Quote-Arg $unityProject),'-runTests','-testPlatform','EditMode',
        '-testFilter','Mahjong.Unity.Tests.EditMode.LocalLiteFontFallbackTests','-testResults',(Quote-Arg $fontXml),
        '-logFile',(Quote-Arg (Join-Path $logs 'unity-editmode-lite-font.log'))) -join ' '
    $fontTest = Invoke-Bounded $UnityPath $fontArgs 'unity-editmode-lite-font' 600
    if ($fontTest.exitCode -ne 0 -or -not (Test-Path -LiteralPath $fontXml -PathType Leaf))
        { throw "Unity Lite font fallback regression failed or produced no XML (native exit $($fontTest.exitCode))." }
    [xml]$fontResult = Get-Content -LiteralPath $fontXml -Raw -Encoding UTF8
    $fontCases = @($fontResult.SelectNodes('//test-case'))
    $fontFailures = @($fontResult.SelectNodes('//test-case[@result != "Passed"]'))
    if ($fontCases.Count -lt 3 -or $fontFailures.Count -ne 0)
        { throw "Lite menu/settlement/font regression XML did not prove three nonzero passing tests: cases=$($fontCases.Count) failures=$($fontFailures.Count)" }
    Write-Stage "EDITMODE_LITE_UI_PASS tests=$($fontCases.Count) failures=0 xml=$fontXml"
    Restore-UnityRunNoise

    $stage = 'PLAYMODE_COMPONENT_REGRESSION'
    $playXml = Join-Path $RunRoot 'playmode-c04-actions.xml'
    $playArgs = @('-batchmode','-projectPath',(Quote-Arg $unityProject),'-runTests','-testPlatform','PlayMode',
        '-testFilter','Mahjong.Unity.Tests.PlayMode.C04PlayerActionScenarioTests','-testResults',(Quote-Arg $playXml),
        '-logFile',(Quote-Arg (Join-Path $logs 'unity-playmode-c04.log'))) -join ' '
    $play = Invoke-Bounded $UnityPath $playArgs 'unity-playmode-c04' 900
    if ($play.exitCode -ne 0 -or -not (Test-Path -LiteralPath $playXml -PathType Leaf))
        { throw "Unity PlayMode component regression failed or produced no XML (native exit $($play.exitCode))." }
    [xml]$playResult = Get-Content -LiteralPath $playXml -Raw -Encoding UTF8
    $playCases = @($playResult.SelectNodes('//test-case'))
    $playFailures = @($playResult.SelectNodes('//test-case[@result != "Passed"]'))
    if ($playCases.Count -lt 5 -or $playFailures.Count -ne 0)
        { throw "PlayMode C04 action XML was not a nonzero clean run: cases=$($playCases.Count) failures=$($playFailures.Count)" }
    Write-Stage "PLAYMODE_COMPONENT_PASS tests=$($playCases.Count) failures=0 xml=$playXml; automated Client host regression only, not physical mouse evidence"
    Restore-UnityRunNoise

    $stage = 'PLAYMODE_CHARACTER_REGRESSION'
    $requiredCharacterTests = @(
        'Mahjong.Unity.Tests.PlayMode.TablePlayModeTests.LiteMenu_CharacterButtonsAndPlayButton_AreIndependentRaycastTargets_AndOctopusStartsLocalGame'
        'Mahjong.Unity.Tests.PlayMode.TablePlayModeTests.OctopusThirdSleeveCandidate_IsShownAndClickedThroughClientOutbox'
        'Mahjong.Unity.Tests.PlayMode.TablePlayModeTests.SleeveBloomThirdCandidate_IsHiddenAndRejectedWhenProjectionOmitsIt'
        'Mahjong.Unity.Tests.PlayMode.TablePlayModeTests.LiteSleevePreselectionCanBeCancelledBeforeAnOrdinaryDraw'
        'Mahjong.Unity.Tests.PlayMode.TablePlayModeTests.LiteArmedKanDrawSubmitsSleeveBloomOnlyOnDrawConfirmation'
        'Mahjong.Unity.Tests.PlayMode.TablePlayModeTests.LiteRiichiPreselectionWaitsForTheOwnersDraw'
        'Mahjong.Unity.Tests.PlayMode.LocalHostCompositionPlayModeTests.BusyRejectedStart_DoesNotOverwriteLastCharacterForRestart'
    )
    $charFilter = $requiredCharacterTests -join ';'
    $charXml = Join-Path $RunRoot 'playmode-character.xml'
    $charArgs = @('-batchmode','-projectPath',(Quote-Arg $unityProject),'-runTests','-testPlatform','PlayMode',
        '-testFilter',(Quote-Arg $charFilter),'-testResults',(Quote-Arg $charXml),
        '-logFile',(Quote-Arg (Join-Path $logs 'unity-playmode-character.log'))) -join ' '
    $charPlay = Invoke-Bounded $UnityPath $charArgs 'unity-playmode-character' 900
    if ($charPlay.exitCode -ne 0 -or -not (Test-Path -LiteralPath $charXml -PathType Leaf))
        { throw "Unity PlayMode character regression failed or produced no XML (native exit $($charPlay.exitCode))." }
    [xml]$charResult = Get-Content -LiteralPath $charXml -Raw -Encoding UTF8
    $charCases = @($charResult.SelectNodes('//test-case'))
    $charFailures = @($charResult.SelectNodes('//test-case[@result != "Passed"]'))
    $charNames = @($charCases | ForEach-Object { [string]$_.fullname })
    $missingCharTests = @($requiredCharacterTests | Where-Object { $charNames -notcontains $_ })
    $unexpectedCharTests = @($charNames | Where-Object { $requiredCharacterTests -notcontains $_ })
    $duplicateCharTests = @($charNames | Group-Object | Where-Object { $_.Count -ne 1 } | ForEach-Object { $_.Name })
    if ($charCases.Count -ne $requiredCharacterTests.Count -or
        $charFailures.Count -ne 0 -or
        $missingCharTests.Count -ne 0 -or
        $unexpectedCharTests.Count -ne 0 -or
        $duplicateCharTests.Count -ne 0)
    {
        throw ("PlayMode character XML did not prove unique pass of all $($requiredCharacterTests.Count) required tests: cases=$($charCases.Count) failures=$($charFailures.Count) missing=$($missingCharTests -join ',') unexpected=$($unexpectedCharTests -join ',') duplicates=$($duplicateCharTests -join ',')")
    }
    Write-Stage "PLAYMODE_CHARACTER_PASS tests=$($charCases.Count) failures=0 xml=$charXml"
    Restore-UnityRunNoise

    $stage = 'FREEZE_SOURCE'
    $head = (& git -C $repo rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $head -notmatch '^[0-9a-f]{40}$') { throw 'Cannot resolve source HEAD.' }
    # The delivery report is written after the candidate has been frozen and
    # records that candidate's identity. Exclude it to avoid a self-referential
    # hash; all product source, tests, task-card and build scripts remain bound.
    $diff = (& git -C $repo diff HEAD --binary -- . ':(exclude)doc/G2_LOCAL_LITE_DELIVERY_20261119.md' | Out-String)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot capture tracked worktree diff.' }
    $hashInput = New-Object System.Collections.Generic.List[string]
    $hashInput.Add('HEAD=' + $head); $hashInput.Add('DIFF=' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($diff)))
    $untracked = @(& git -C $repo ls-files --others --exclude-standard -- . ':(exclude)doc/G2_LOCAL_LITE_DELIVERY_20261119.md')
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate untracked source files.' }
    foreach ($relative in ($untracked | Sort-Object)) {
        $path = Join-Path $repo $relative
        if (Test-Path -LiteralPath $path -PathType Leaf) { $hashInput.Add('UNTRACKED=' + $relative + ':' + (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash) }
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $diffSha = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($hashInput -join "`n")))).Replace('-','').ToLowerInvariant() }
    finally { $sha.Dispose() }
    $sourceStatus = (& git -C $repo status --short --branch | Out-String)
    [IO.File]::WriteAllText((Join-Path $RunRoot 'source-identity.txt'),"HEAD=$head`nworktreeDiffSha256=$diffSha`n$sourceStatus",(New-Object Text.UTF8Encoding($false)))

    $stage = 'BUILD_PLAYER'
    $exe = Join-Path $player 'Mahjong.exe'; $marker = Join-Path $logs 'player-build.json'
    $unityArgs = @('-batchmode','-projectPath',(Quote-Arg $unityProject),'-executeMethod','G2LocalLitePlayerBuild.Build',
        '-g2-local-lite-output',(Quote-Arg $exe),'-g2-local-lite-marker',(Quote-Arg $marker),'-g2-local-lite-source',$head,
        '-g2-local-lite-diff-sha256',$diffSha,'-logFile',(Quote-Arg (Join-Path $logs 'player-build.unity.log')),'-quit') -join ' '
    $build = Invoke-Bounded $UnityPath $unityArgs 'unity-player-build' $TimeoutSeconds
    $unityProcess = [pscustomobject]@{ pid=$build.pid; exitCode=$build.exitCode }
    if ($build.exitCode -ne 0) { throw "Unity Player build failed with native exit $($build.exitCode)." }
    Restore-UnityRunNoise
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Unity reported success but Player executable is missing.' }
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) { throw 'Player build marker is missing.' }

    $stage = 'PACKAGE_MANIFEST'
    $files = @(Get-ChildItem -LiteralPath $player -File -Recurse | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path=$_.FullName.Substring($player.Length + 1); bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    $buildRecord = Get-Content -LiteralPath $marker -Raw -Encoding UTF8 | ConvertFrom-Json
    $package = [ordered]@{ schemaVersion=1; product='G2-Local-Lite'; sourceHead=$head; worktreeDiffSha256=$diffSha;
        playerDirectory=$player; executable=$exe; executableSha256=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant();
        build=$buildRecord; sharedPluginProvenance=(Join-Path $unityProject 'Library/SharedPluginProvenance/provenance.json'); files=$files }
    [IO.File]::WriteAllText((Join-Path $RunRoot 'player-package.json'),(($package | ConvertTo-Json -Depth 10) + "`n"),(New-Object Text.UTF8Encoding($false)))
    $exitCode = 0; $stage = 'COMPLETE'
    Write-Stage "PLAYER_BUILT exe=$exe sourceHead=$head fileCount=$($files.Count)"
}
catch {
    Write-Stage "FAILED stage=$stage type=$($_.Exception.GetType().Name) message=$($_.Exception.Message)"
    $exitCode = 1
}
finally {
    $record = [ordered]@{ task='G2-LOCAL-LITE-20261119'; status=$(if ($exitCode -eq 0) {'PASS_BUILD_ONLY'} else {'FAIL'});
        stage=$stage; nativeExit=$exitCode; startedUtc=$startedUtc.ToString('o'); endedUtc=[DateTimeOffset]::UtcNow.ToString('o');
        mutexName=$mutexName; mutexHeld=$held; unityProcess=$unityProcess; runRoot=$RunRoot }
    [IO.File]::WriteAllText((Join-Path $RunRoot 'summary.json'),(($record | ConvertTo-Json -Depth 6) + "`n"),(New-Object Text.UTF8Encoding($false)))
    if ($held) { $mutex.ReleaseMutex() }; $mutex.Dispose()
    $files = @(Get-ChildItem -LiteralPath $RunRoot -File -Recurse | Where-Object { $_.Name -notin @('manifest.json','manifest.sha256') } | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path=$_.FullName.Substring($RunRoot.Length + 1); bytes=$_.Length; sha256=(Get-HashWithRetry $_.FullName) }
    })
    $manifest = [ordered]@{ task='G2-LOCAL-LITE-20261119'; scope='owned build runner output'; files=$files }
    $manifestText = ($manifest | ConvertTo-Json -Depth 8) + "`n"
    [IO.File]::WriteAllText((Join-Path $RunRoot 'manifest.json'),$manifestText,(New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText((Join-Path $RunRoot 'manifest.sha256'),((Get-FileHash -LiteralPath (Join-Path $RunRoot 'manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant() + "`n"),(New-Object Text.UTF8Encoding($false)))
}
Write-Host "G2-LOCAL-LITE build status=$($record.status) nativeExit=$exitCode output=$RunRoot"
exit $exitCode
