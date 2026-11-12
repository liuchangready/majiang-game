#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$RunRoot = '',
    [string]$UnityPath = 'D:\game_dev_tools\unity\Editor\Unity.exe',
    [ValidateRange(0,600)][int]$ResourceWaitSeconds = 600,
    [ValidateRange(1,30)][int]$HeartbeatSeconds = 30,
    [ValidateRange(1,3600)][int]$BuildTimeoutSeconds = 1800
)

$ErrorActionPreference = 'Stop'
if (-not ('UiProtoNativeProcess' -as [type])) {
    $nativeCompilerTemp = Join-Path $env:LOCALAPPDATA 'Temp'
    if (Test-Path -LiteralPath $nativeCompilerTemp -PathType Container) {
        $env:TEMP = $nativeCompilerTemp
        $env:TMP = $nativeCompilerTemp
    }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class UiProtoNativeProcess
{
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
}
'@
}
$mutexName = 'Local\MahjongV5P45UnityValidation'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$projectRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'unity\Mahjong.Unity'))
if (-not (Test-Path -LiteralPath $UnityPath -PathType Leaf)) { throw "Unity executable not found: $UnityPath" }
if (-not (Test-Path -LiteralPath $projectRoot -PathType Container)) { throw "Unity project not found: $projectRoot" }

if ([string]::IsNullOrWhiteSpace($RunRoot)) {
    $RunRoot = Join-Path $repoRoot ('build\ui_proto\' + [guid]::NewGuid().ToString('N'))
}
$runRootFull = [IO.Path]::GetFullPath($RunRoot)
$buildDir = Join-Path $runRootFull 'player'
$unityLog = Join-Path $runRootFull 'unity.log'
$stdoutPath = Join-Path $runRootFull 'unity.stdout.log'
$stderrPath = Join-Path $runRootFull 'unity.stderr.log'
$summaryPath = Join-Path $runRootFull 'summary.json'
$buildReportPath = Join-Path $runRootFull 'build-report.json'
$assetPreflightPath = Join-Path $runRootFull 'asset-preflight.log'
$manifestPath = Join-Path $runRootFull 'player.sha256.json'
if (Test-Path -LiteralPath $runRootFull) { throw "Refusing to overwrite existing ui_proto output root: $runRootFull" }
[IO.Directory]::CreateDirectory($buildDir) | Out-Null

$assetCheckScript = Join-Path $PSScriptRoot 'check-ui-proto-assets.ps1'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $assetCheckScript -ProjectRoot $repoRoot *> $assetPreflightPath
$assetPreflightExit = $LASTEXITCODE
if ($assetPreflightExit -ne 0) { throw "FAIL: ui_proto art/reference preflight failed (exit=$assetPreflightExit); inspect $assetPreflightPath" }

function Get-UnityEditors {
    $items = @()
    foreach ($proc in (Get-CimInstance Win32_Process -Filter "Name = 'Unity.exe'" -ErrorAction SilentlyContinue)) {
        $cmd = [string]$proc.CommandLine
        $exe = [string]$proc.ExecutablePath
        if ($exe -notlike '*\Editor\Unity.exe' -and $cmd -notmatch '(?i)(?:^|\s)-projectPath\s+') { continue }
        $project = 'unknown project'
        $m = [regex]::Match($cmd, '-projectPath\s+"?([^" ]+)')
        if ($m.Success) { $project = $m.Groups[1].Value }
        $items += [pscustomobject]@{ Pid = [int]$proc.ProcessId; Project = $project }
    }
    return $items
}

$mutex = $null
$held = $false
$child = $null
$processHandle = [IntPtr]::Zero
$start = [DateTime]::UtcNow
$waited = 0
$unityExit = $null
$unityResult = 'NOT_RUN'
$compileErrors = @()
try {
    while ($waited -lt $ResourceWaitSeconds) {
        $editors = @(Get-UnityEditors)
        if ($editors.Count -eq 0) { break }
        Write-Host ("HEARTBEAT stage=wait-unity-editor waited={0}s/{1}s editors={2}" -f $waited, $ResourceWaitSeconds, (($editors | ForEach-Object { "$($_.Pid):$($_.Project)" }) -join ','))
        $slice = [Math]::Min($HeartbeatSeconds, $ResourceWaitSeconds - $waited)
        Start-Sleep -Seconds $slice
        $waited += $slice
    }
    if (@(Get-UnityEditors).Count -gt 0) { throw "BLOCKED: a Unity Editor is still open after the bounded ${ResourceWaitSeconds}s wait; no second Editor was started." }

    $mutex = New-Object Threading.Mutex($false, $mutexName)
    while (-not $held -and $waited -lt $ResourceWaitSeconds) {
        $slice = [Math]::Min($HeartbeatSeconds, $ResourceWaitSeconds - $waited)
        try { $held = $mutex.WaitOne([TimeSpan]::FromSeconds($slice)) }
        catch [Threading.AbandonedMutexException] { $held = $true }
        if (-not $held) {
            $waited += $slice
            Write-Host ("HEARTBEAT stage=wait-mutex waited={0}s/{1}s mutex={2}" -f $waited, $ResourceWaitSeconds, $mutexName)
        }
    }
    if (-not $held) { throw "BLOCKED: timed out waiting for shared Unity mutex $mutexName after ${ResourceWaitSeconds}s." }
    if (@(Get-UnityEditors).Count -gt 0) { throw 'BLOCKED: a Unity Editor opened while the build mutex was being acquired; refusing to start a second Editor.' }

    $env:UI_PROTO_OUTPUT_PATH = Join-Path $buildDir 'ui_proto.exe'
    $arguments = @('-batchmode','-nographics','-quit','-projectPath',('"' + $projectRoot + '"'),'-executeMethod','Mahjong.Unity.EditorTools.UiProtoWindowsBuild.BuildWindowsDemo','-logFile',('"' + $unityLog + '"')) -join ' '
    $child = Start-Process -FilePath $UnityPath -ArgumentList $arguments -PassThru -NoNewWindow -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
    $processHandle = $child.Handle
    Write-Host ("UI_PROTO_BUILD_STARTED pid={0} runRoot={1}" -f $child.Id, $runRootFull)
    $buildElapsed = 0
    while (-not $child.HasExited -and $buildElapsed -lt $BuildTimeoutSeconds) {
        $slice = [Math]::Min($HeartbeatSeconds, $BuildTimeoutSeconds - $buildElapsed)
        [void]$child.WaitForExit($slice * 1000)
        $buildElapsed += $slice
        if (-not $child.HasExited) { Write-Host ("HEARTBEAT stage=unity-windows-player elapsed={0}s/{1}s pid={2}" -f $buildElapsed, $BuildTimeoutSeconds, $child.Id) }
    }
    if (-not $child.HasExited) {
        [void]$child.CloseMainWindow()
        Write-Host ("CANCEL_REQUESTED stage=unity-windows-player pid={0} reason=timeout" -f $child.Id)
        if (-not $child.WaitForExit(15000)) {
            Write-Warning "Owned Unity process did not exit within 15 seconds after graceful close; retaining the shared mutex until it exits."
            while (-not $child.HasExited) {
                Write-Host ("HEARTBEAT stage=wait-owned-process-exit pid={0}; shared mutex remains held" -f $child.Id)
                [void]$child.WaitForExit($HeartbeatSeconds * 1000)
            }
        }
    }
    $child.WaitForExit()
    $child.Refresh()
    [uint32]$nativeExitCode = 259
    if ($processHandle -eq [IntPtr]::Zero -or -not [UiProtoNativeProcess]::GetExitCodeProcess($processHandle, [ref]$nativeExitCode) -or $nativeExitCode -eq 259) {
        throw "FAIL: could not read a completed native Unity exit code for pid=$($child.Id)."
    }
    $unityExit = [int]$nativeExitCode
    $logText = if (Test-Path -LiteralPath $unityLog) { [IO.File]::ReadAllText($unityLog) } else { '' }
    $compileErrors = @([regex]::Matches($logText, '(?m)^.*(?:error CS\d+|Compilation failed|Scripts have compiler errors).*$') | ForEach-Object { $_.Value.Trim() } | Select-Object -Unique)
    $buildReport = if (Test-Path -LiteralPath $buildReportPath) { Get-Content -LiteralPath $buildReportPath -Raw | ConvertFrom-Json } else { $null }
    $buildReportSucceeded = $buildReport -ne $null -and $buildReport.result -eq 'Succeeded' -and [int]$buildReport.totalErrors -eq 0
    $playerComplete = (Test-Path -LiteralPath $env:UI_PROTO_OUTPUT_PATH -PathType Leaf) `
        -and (Test-Path -LiteralPath (Join-Path $buildDir 'ui_proto_Data') -PathType Container) `
        -and (Test-Path -LiteralPath (Join-Path $buildDir 'UnityPlayer.dll') -PathType Leaf)
    if ($unityExit -ne 0 -or $compileErrors.Count -gt 0 -or -not $buildReportSucceeded -or -not $playerComplete) {
        $unityResult = 'FAIL'
        throw "FAIL: ui_proto Unity build failed (exit=$unityExit, compileErrors=$($compileErrors.Count), buildReportSucceeded=$buildReportSucceeded, playerComplete=$playerComplete); inspect $unityLog"
    }

    $playerFiles = @(Get-ChildItem -LiteralPath $buildDir -Recurse -File)
    $manifest = New-Object Collections.Generic.List[object]
    foreach ($file in $playerFiles) {
        $relative = $file.FullName.Substring($buildDir.Length).TrimStart('\')
        $manifest.Add([pscustomobject]@{ path = $relative; length = [long]$file.Length; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() })
    }
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    $unityResult = 'PASS'
    $summary = [ordered]@{
        status = $unityResult
        product = 'ui_proto Windows visual demo'
        baseline = '58b27b00696c458585a790409931c121f3e51314'
        branch = (git -C $repoRoot branch --show-current)
        head = (git -C $repoRoot rev-parse HEAD)
        workingTree = $repoRoot
        unityPath = $UnityPath
        unityExitCode = $unityExit
        nativeExitStatus = if ($null -eq $unityExit) { 'UNKNOWN' } else { [string]$unityExit }
        assetPreflight = $assetPreflightPath
        assetPreflightExit = $assetPreflightExit
        compilerErrorCount = $compileErrors.Count
        buildReport = $buildReportPath
        buildReportResult = $buildReport.result
        buildReportErrors = $buildReport.totalErrors
        buildReportWarnings = $buildReport.totalWarnings
        output = $env:UI_PROTO_OUTPUT_PATH
        playerDirectory = $buildDir
        playerFileCount = $manifest.Count
        playerSha256Manifest = $manifestPath
        unityLog = $unityLog
        elapsedSeconds = [int]([DateTime]::UtcNow - $start).TotalSeconds
    }
    $summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $summaryPath -Encoding utf8
    Write-Host ("UI_PROTO_BUILD_PASS exe={0} files={1} compilerErrors=0 summary={2}" -f $env:UI_PROTO_OUTPUT_PATH, $manifest.Count, $summaryPath)
}
catch {
    if ($unityResult -ne 'PASS') { $unityResult = 'FAIL' }
    $summary = [ordered]@{
        status = $unityResult
        baseline = '58b27b00696c458585a790409931c121f3e51314'
        branch = (git -C $repoRoot branch --show-current)
        head = (git -C $repoRoot rev-parse HEAD)
        workingTree = $repoRoot
        unityExitCode = $unityExit
        nativeExitStatus = if ($null -eq $unityExit) { 'UNKNOWN' } else { [string]$unityExit }
        assetPreflight = $assetPreflightPath
        assetPreflightExit = $assetPreflightExit
        compilerErrorCount = $compileErrors.Count
        compilerErrors = $compileErrors
        buildReport = $buildReportPath
        buildReportResult = if ($null -ne $buildReport) { $buildReport.result } else { $null }
        buildReportErrors = if ($null -ne $buildReport) { $buildReport.totalErrors } else { $null }
        output = $env:UI_PROTO_OUTPUT_PATH
        unityLog = $unityLog
        error = $_.Exception.Message
        elapsedSeconds = [int]([DateTime]::UtcNow - $start).TotalSeconds
    }
    $summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $summaryPath -Encoding utf8
    Write-Error $_
}
finally {
    Remove-Item Env:UI_PROTO_OUTPUT_PATH -ErrorAction SilentlyContinue
    if ($child -ne $null -and -not $child.HasExited) {
        [void]$child.CloseMainWindow()
        while (-not $child.HasExited) {
            Write-Host ("HEARTBEAT stage=finally-wait-owned-process pid={0}; shared mutex remains held" -f $child.Id)
            [void]$child.WaitForExit($HeartbeatSeconds * 1000)
        }
    }
    if ($child -ne $null) { $child.Dispose() }
    if ($held -and $mutex -ne $null) { try { $mutex.ReleaseMutex() } catch { } }
    if ($mutex -ne $null) { $mutex.Dispose() }
}
