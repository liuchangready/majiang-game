#requires -Version 5.1
<#
.SYNOPSIS
    Isolated P4.5 full-match Windows Player driver runner.

.DESCRIPTION
    Owns one fresh evidence root:
      build/p45-full-match-driver/run-<utc>/
        source.json          source HEAD / dirty / status / diff check
        player/              fresh Unity Windows Player (all files hashed)
        player-files.json    whole-directory SHA-256 file manifest
        logs/                shared build, plugin provenance, Unity build, Player log
        data/                isolated -smoke-data root (save/outbox only here)
        driver/              LocalFullMatchPlayerDriver JSONL + summary
        screenshots/         hidden-window captures (not visual evidence)
        evidence/            final artifact manifest + sidecar

    Every Unity / shared-plugin / Player stage runs while holding the named
    mutex Local\MahjongV5P45UnityValidation. The tool only ever stops the exact
    process it started (PID + start time + full path + exe SHA re-checked).

.PARAMETER Mode
    All   build shared plugins, build a fresh Windows Player, run it, verify
    Build only the shared plugins and Windows Player
    Run   reuse an existing run directory's Player, execute the driver
    Verify re-check an existing artifact manifest

.PARAMETER RunDir
    Absolute or repo-relative run directory. All requires a path that does not
    exist yet. Run/Verify require an existing one.

.PARAMETER TimeoutSeconds
    Bounded Player run time. Default 5400 (90 minutes).

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass `
      -File tools/unity/run-p45-full-match.ps1 -Mode All `
      -RunDir build/p45-full-match-driver/run-20261031-01
#>
[CmdletBinding()]
param(
    [ValidateSet('All', 'Build', 'Run', 'Verify')]
    [string]$Mode = 'All',

    [string]$RunDir,

    [string]$UnityPath = 'D:\Unity\Hub\Editor\6000.3.27f1\Editor\Unity.exe',

    [string]$DotnetPath = 'D:\Unity\dotnet\dotnet.exe',

    [int]$TimeoutSeconds = 5400
)

$ErrorActionPreference = 'Stop'
$script:ToolExit = 0
$script:Mutex = $null
$script:MutexHeld = $false
$script:RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$script:RunRoot = $null
$script:PlayerProcess = $null
$script:PlayerRecord = $null

function Write-ToolLog {
    param([string]$Message)

    $line = '[' + [DateTimeOffset]::Now.ToString('o') + '] ' + $Message
    Write-Host $line
    if ($script:RunRoot -and (Test-Path -LiteralPath (Join-Path $script:RunRoot 'logs'))) {
        Add-Content -LiteralPath (Join-Path $script:RunRoot 'logs\tool.log') -Value $line -Encoding UTF8
    }
}

function Resolve-RunDir {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        $stamp = [DateTimeOffset]::Now.ToString('yyyyMMdd-HHmmss')
        $Value = 'build/p45-full-match-driver/run-' + $stamp
    }

    if ([System.IO.Path]::IsPathRooted($Value)) {
        return [System.IO.Path]::GetFullPath($Value)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $script:RepoRoot $Value))
}

function Assert-WithinBuildRoot {
    param([string]$Path)

    $allowed = [System.IO.Path]::GetFullPath((Join-Path $script:RepoRoot 'build'))
    $full = [System.IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($allowed + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "RunDir must stay under the ignored build/ root: $full"
    }
}

function Get-Mutex {
    $name = 'Local\MahjongV5P45UnityValidation'
    $script:Mutex = New-Object System.Threading.Mutex($false, $name)
    $start = [DateTimeOffset]::UtcNow
    Write-ToolLog ('MUTEX_WAIT ' + $name)
    $acquired = $script:Mutex.WaitOne([TimeSpan]::FromSeconds(3600))
    $elapsed = ([DateTimeOffset]::UtcNow - $start).TotalSeconds
    if (-not $acquired) {
        throw ('MUTEX_TIMEOUT ' + $name + ' after ' + [math]::Round($elapsed, 3) + 's')
    }

    $script:MutexHeld = $true
    Write-ToolLog ('MUTEX_ACQUIRED ' + $name + ' waitSeconds=' + [math]::Round($elapsed, 3))
}

function Release-Mutex {
    if ($script:MutexHeld -and $script:Mutex) {
        try {
            $script:Mutex.ReleaseMutex()
        }
        catch {
        }

        $script:MutexHeld = $false
        Write-ToolLog 'MUTEX_RELEASED Local\MahjongV5P45UnityValidation'
    }

    if ($script:Mutex) {
        try {
            $script:Mutex.Dispose()
        }
        catch {
        }

        $script:Mutex = $null
    }
}

function Invoke-CapturedPowerShell {
    param(
        [string]$ScriptPath,
        [string[]]$Arguments,
        [string]$LogPath
    )

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = 'powershell.exe'
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $false
    $psi.RedirectStandardError = $false
    $psi.CreateNoWindow = $true
    $psi.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $ScriptPath + '"'
    foreach ($argument in $Arguments) {
        $psi.Arguments += ' "' + $argument + '"'
    }

    $stdoutPath = $LogPath + '.stdout'
    $stderrPath = $LogPath + '.stderr'
    $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $ScriptPath @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    $text = ($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
    [System.IO.File]::WriteAllText($LogPath, $text, (New-Object System.Text.UTF8Encoding($false)))
    [System.IO.File]::WriteAllText($stdoutPath, $text, (New-Object System.Text.UTF8Encoding($false)))
    [System.IO.File]::WriteAllText($stderrPath, '', (New-Object System.Text.UTF8Encoding($false)))
    Write-ToolLog ('COMMAND powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + $ScriptPath + '" ' + ($Arguments -join ' '))
    Write-ToolLog ('NATIVE_EXIT ' + $exitCode + ' log=' + $LogPath)
    return $exitCode
}

function Invoke-UnityBuild {
    param(
        [string]$UnityExe,
        [string]$ProjectPath,
        [string]$PlayerExe,
        [string]$LogPath,
        [string]$StdOutPath,
        [string]$StdErrPath
    )

    if (-not (Test-Path -LiteralPath $UnityExe)) {
        throw "Unity.exe not found: $UnityExe"
    }

    $playerDir = Split-Path -Parent $PlayerExe
    if (Test-Path -LiteralPath $playerDir) {
        $entries = @(Get-ChildItem -LiteralPath $playerDir -Force)
        if ($entries.Count -gt 0) {
            throw "Refusing to build into a non-empty player directory: $playerDir"
        }
    }
    else {
        New-Item -ItemType Directory -Force -Path $playerDir | Out-Null
    }

    $arguments = @(
        '-batchmode',
        '-quit',
        '-nographics',
        '-projectPath', $ProjectPath,
        '-buildWindows64Player', $PlayerExe,
        '-logFile', $LogPath
    )

    $process = Start-Process -FilePath $UnityExe -ArgumentList $arguments -PassThru -WindowStyle Hidden
    Write-ToolLog ('UNITY_START pid=' + $process.Id + ' exe=' + $UnityExe)
    $exited = $process.WaitForExit(3600 * 1000)
    if (-not $exited) {
        Write-ToolLog ('UNITY_TIMEOUT pid=' + $process.Id)
        try {
            $process.Kill()
        }
        catch {
        }

        return 124
    }

    $process.Refresh()
    $exitCode = $process.ExitCode
    if ($null -eq $exitCode) {
        Write-ToolLog ('UNITY_EXIT_CODE_UNAVAILABLE pid=' + $process.Id)
        return 125
    }

    Write-ToolLog ('UNITY_EXIT pid=' + $process.Id + ' native=' + $exitCode)
    return [int]$exitCode
}

function Get-WholeFileManifest {
    param([string]$Root, [string]$OutputPath)

    $rootFull = [System.IO.Path]::GetFullPath($Root)
    $entries = New-Object System.Collections.Generic.List[object]
    $totalBytes = [long]0
    Get-ChildItem -LiteralPath $rootFull -Recurse -File | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($rootFull.Length).TrimStart('\', '/').Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        $entries.Add([ordered]@{
            path = $relative
            bytes = [long]$_.Length
            sha256 = $hash
        }) | Out-Null
        $totalBytes += $_.Length
    }

    $manifest = [ordered]@{
        schemaVersion = 1
        kind = 'p45-full-match-player-files'
        root = $rootFull
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        fileCount = $entries.Count
        totalBytes = $totalBytes
        files = $entries
    }
    $json = $manifest | ConvertTo-Json -Depth 8
    [System.IO.File]::WriteAllText($OutputPath, $json, (New-Object System.Text.UTF8Encoding($false)))
    return (Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256).Hash
}

function Get-ArtifactManifest {
    param([string]$Root, [string]$OutputPath)

    $rootFull = [System.IO.Path]::GetFullPath($Root)
    $manifestName = 'evidence/artifacts.json'
    $sidecarName = 'evidence/artifacts.json.sha256.txt'
    $entries = New-Object System.Collections.Generic.List[object]
    $totalBytes = [long]0
    Get-ChildItem -LiteralPath $rootFull -Recurse -File | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($rootFull.Length).TrimStart('\', '/').Replace('\', '/')
        if ($relative -eq $manifestName -or $relative -eq $sidecarName -or
            $relative -like '*.lock' -or $relative -like '*.tmp' -or $relative -like '*.tmp.*') {
            # Manifest/self and transient files are intentionally absent.
        }
        else {
            $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            $entries.Add([ordered]@{
                path = $relative
                bytes = [long]$_.Length
                sha256 = $hash
                classification = if ($relative.StartsWith('data/', [System.StringComparison]::OrdinalIgnoreCase)) { 'private-do-not-publish' } else { 'evidence' }
            }) | Out-Null
            $totalBytes += $_.Length
        }
    }

    $manifest = [ordered]@{
        schemaVersion = 1
        kind = 'p45-full-match-evidence'
        root = $rootFull
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        entryCount = $entries.Count
        totalBytes = $totalBytes
        entries = $entries
    }
    $json = $manifest | ConvertTo-Json -Depth 8
    [System.IO.File]::WriteAllText($OutputPath, $json, (New-Object System.Text.UTF8Encoding($false)))
    return (Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256).Hash
}

function Test-ArtifactManifest {
    param([string]$ManifestPath)

    if (-not (Test-Path -LiteralPath $ManifestPath)) {
        throw "Manifest not found: $ManifestPath"
    }

    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    $root = [System.IO.Path]::GetFullPath([string]$manifest.root)
    $seen = @{}
    $failures = New-Object System.Collections.Generic.List[string]
    foreach ($entry in $manifest.entries) {
        $relative = [string]$entry.path
        if ($seen.ContainsKey($relative)) {
            $failures.Add('DUPLICATE ' + $relative) | Out-Null
            continue
        }

        $seen[$relative] = $true
        $full = [System.IO.Path]::GetFullPath((Join-Path $root $relative))
        if (-not $full.StartsWith($root + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
            $failures.Add('ESCAPE ' + $relative) | Out-Null
            continue
        }

        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
            $failures.Add('MISSING ' + $relative) | Out-Null
            continue
        }

        $item = Get-Item -LiteralPath $full
        $hash = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash
        if ([long]$item.Length -ne [long]$entry.bytes -or $hash -ne [string]$entry.sha256) {
            $failures.Add('MISMATCH ' + $relative) | Out-Null
        }
    }

    if ($failures.Count -gt 0) {
        throw ('MANIFEST_INVALID ' + ($failures -join '; '))
    }

    Write-ToolLog ('MANIFEST_VERIFIED entries=' + $seen.Count + ' sha256=' + (Get-FileHash -LiteralPath $ManifestPath -Algorithm SHA256).Hash)
}

function Write-SourceRecord {
    param([string]$RunRoot)

    $head = (& git -C $script:RepoRoot rev-parse HEAD).Trim()
    $branch = (& git -C $script:RepoRoot rev-parse --abbrev-ref HEAD).Trim()
    $status = (& git -C $script:RepoRoot status --porcelain=v1) -join "`n"
    $dirty = -not [string]::IsNullOrWhiteSpace($status)
    $stat = (& git -C $script:RepoRoot diff --stat) -join "`n"
    & git -C $script:RepoRoot diff --check *> (Join-Path $RunRoot 'logs\diff-check.log')
    $diffCheck = $LASTEXITCODE
    $record = [ordered]@{
        schemaVersion = 1
        kind = 'p45-full-match-source'
        capturedAt = [DateTimeOffset]::Now.ToString('o')
        head = $head
        branch = $branch
        dirty = $dirty
        statusPorcelain = $status
        diffStat = $stat
        diffCheckExit = $diffCheck
    }
    $record | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $RunRoot 'source.json') -Encoding UTF8
    Write-ToolLog ('SOURCE head=' + $head + ' branch=' + $branch + ' dirty=' + $dirty + ' diffCheck=' + $diffCheck)
}

function Invoke-Build {
    param([string]$RunRoot)

    Write-SourceRecord -RunRoot $RunRoot
    $logs = Join-Path $RunRoot 'logs'
    New-Item -ItemType Directory -Force -Path $logs | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $RunRoot 'player') | Out-Null

    $buildShared = Join-Path $script:RepoRoot 'tools\unity\build-shared.ps1'
    $checkPlugins = Join-Path $script:RepoRoot 'tools\unity\check-shared-plugins.ps1'
    $sharedExit = Invoke-CapturedPowerShell -ScriptPath $buildShared -Arguments @('-Configuration', 'Release') -LogPath (Join-Path $logs 'build-shared.log')
    Get-FileHash -LiteralPath (Join-Path $logs 'build-shared.log') -Algorithm SHA256 |
        ForEach-Object { $_.Hash } | Set-Content -LiteralPath (Join-Path $logs 'build-shared.log.sha256.txt') -Encoding ASCII
    if ($sharedExit -ne 0) {
        Write-ToolLog ('BUILD_FAILED shared native=' + $sharedExit)
        return 10
    }

    $pluginExit = Invoke-CapturedPowerShell -ScriptPath $checkPlugins -Arguments @('-Configuration', 'Release') -LogPath (Join-Path $logs 'check-shared-plugins.log')
    Get-FileHash -LiteralPath (Join-Path $logs 'check-shared-plugins.log') -Algorithm SHA256 |
        ForEach-Object { $_.Hash } | Set-Content -LiteralPath (Join-Path $logs 'check-shared-plugins.log.sha256.txt') -Encoding ASCII
    if ($pluginExit -ne 0) {
        Write-ToolLog ('BUILD_FAILED plugin provenance native=' + $pluginExit)
        return 11
    }

    $playerExe = Join-Path $RunRoot 'player\Mahjong.exe'
    $unityExit = Invoke-UnityBuild `
        -UnityExe $UnityPath `
        -ProjectPath (Join-Path $script:RepoRoot 'unity\Mahjong.Unity') `
        -PlayerExe $playerExe `
        -LogPath (Join-Path $logs 'unity-build.log') `
        -StdOutPath (Join-Path $logs 'unity-build.stdout.log') `
        -StdErrPath (Join-Path $logs 'unity-build.stderr.log')
    if ($unityExit -ne 0) {
        Write-ToolLog ('BUILD_FAILED unity native=' + $unityExit)
        return 12
    }

    $unityLog = Get-Content -LiteralPath (Join-Path $logs 'unity-build.log') -Raw -ErrorAction SilentlyContinue
    if ($unityLog -notmatch 'Build Successful' -or $unityLog -notmatch 'Exiting batchmode successfully now!') {
        Write-ToolLog 'BUILD_FAILED unity log does not contain the success markers'
        return 13
    }

    if (-not (Test-Path -LiteralPath $playerExe)) {
        Write-ToolLog 'BUILD_FAILED player exe missing after Unity exit 0'
        return 14
    }

    $playerManifest = Get-WholeFileManifest -Root (Join-Path $RunRoot 'player') -OutputPath (Join-Path $RunRoot 'player-files.json')
    Write-ToolLog ('PLAYER_MANIFEST sha256=' + $playerManifest)
    return 0
}

function Get-ProcessRecord {
    param([System.Diagnostics.Process]$Process)

    $item = Get-Item -LiteralPath $Process.MainModule.FileName
    return [ordered]@{
        pid = $Process.Id
        startTimeUtc = $Process.StartTime.ToUniversalTime().ToString('o')
        path = $item.FullName
        sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
    }
}

function Test-OwnedPlayerProcess {
    param([System.Diagnostics.Process]$Process, $Record)

    if ($null -eq $Process -or $null -eq $Record) {
        return $false
    }

    try {
        $Process.Refresh()
        if ($Process.HasExited) {
            return $false
        }

        $current = Get-ProcessRecord -Process $Process
        return ($current.pid -eq [int]$Record.pid -and
                $current.startTimeUtc -eq [string]$Record.startTimeUtc -and
                $current.path -eq [string]$Record.path -and
                $current.sha256 -eq [string]$Record.sha256)
    }
    catch {
        return $false
    }
}

function Invoke-Run {
    param([string]$RunRoot)

    $playerExe = Join-Path $RunRoot 'player\Mahjong.exe'
    if (-not (Test-Path -LiteralPath $playerExe)) {
        throw "Player not found: $playerExe"
    }

    $logs = Join-Path $RunRoot 'logs'
    $driverDir = Join-Path $RunRoot 'driver'
    $dataDir = Join-Path $RunRoot 'data'
    $shotDir = Join-Path $RunRoot 'screenshots'
    $runtimeDir = Join-Path $RunRoot 'runtime'
    foreach ($dir in @($logs, $driverDir, $dataDir, $shotDir, $runtimeDir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }

    $playerLog = Join-Path $logs 'player.log'
    $arguments = @(
        '-screen-width', '1280',
        '-screen-height', '720',
        '-screen-fullscreen', '0',
        '-logfile', $playerLog,
        '-smoke-data', $dataDir,
        '-full-match-driver', $driverDir
    )

    $process = Start-Process -FilePath $playerExe -ArgumentList $arguments -PassThru -WindowStyle Hidden
    $record = [ordered]@{
        schemaVersion = 1
        kind = 'p45-full-match-player-process'
        startedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        commandLine = $arguments
        exe = $playerExe
        exeSha256 = (Get-FileHash -LiteralPath $playerExe -Algorithm SHA256).Hash
        pid = $process.Id
        startTimeUtc = $process.StartTime.ToUniversalTime().ToString('o')
    }
    $record | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runtimeDir 'process.json') -Encoding UTF8
    Write-ToolLog ('PLAYER_START pid=' + $process.Id + ' exe=' + $playerExe + ' sha256=' + $record.exeSha256)

    $exited = $process.WaitForExit([math]::Max(1, $TimeoutSeconds) * 1000)
    $disposition = ''
    $nativeExit = $null
    if (-not $exited) {
        Write-ToolLog ('PLAYER_TIMEOUT pid=' + $process.Id + ' timeoutSeconds=' + $TimeoutSeconds)
        if (Test-OwnedPlayerProcess -Process $process -Record $record) {
            try {
                $process.Kill()
                $process.WaitForExit(10000) | Out-Null
                $disposition = 'TOOL_TIMEOUT_KILLED'
            }
            catch {
                $disposition = 'TOOL_TIMEOUT_KILL_FAILED'
            }
        }
        else {
            $disposition = 'TOOL_TIMEOUT_NOT_OWNED'
        }
    }
    else {
        $nativeExit = $process.ExitCode
        $disposition = if ($nativeExit -eq 0) { 'EXITED_OK' } else { 'EXITED_NONZERO' }
    }

    $exitRecord = [ordered]@{
        schemaVersion = 1
        kind = 'p45-full-match-player-exit'
        finishedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        disposition = $disposition
        nativeExitCode = $nativeExit
        timeoutSeconds = $TimeoutSeconds
    }
    $exitRecord | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runtimeDir 'exit.json') -Encoding UTF8
    Write-ToolLog ('PLAYER_EXIT disposition=' + $disposition + ' native=' + $nativeExit)

    $driverSummaryPath = Join-Path $driverDir 'summary.json'
    $driverSummary = $null
    if (Test-Path -LiteralPath $driverSummaryPath) {
        try {
            $driverSummary = Get-Content -LiteralPath $driverSummaryPath -Raw | ConvertFrom-Json
        }
        catch {
            Write-ToolLog ('DRIVER_SUMMARY_PARSE_FAILED ' + $_.Exception.Message)
        }
    }

    $driverPass = $false
    if ($driverSummary) {
        $rounds = @($driverSummary.rounds)
        $finalScores = @($driverSummary.finalScores)
        $rankings = @($driverSummary.rankings)
        $driverPass = (
            [string]$driverSummary.outcome -eq 'PASS' -and
            [bool]$driverSummary.matchFinished -eq $true -and
            [int]$driverSummary.exitCode -eq 0 -and
            $rounds.Count -ge 1 -and
            $finalScores.Count -eq 4 -and
            $rankings.Count -eq 4)
    }

    $toolSummary = [ordered]@{
        schemaVersion = 1
        kind = 'p45-full-match-tool-summary'
        runDir = $RunRoot
        finishedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        disposition = $disposition
        nativeExitCode = $nativeExit
        driverSummaryPresent = ($null -ne $driverSummary)
        driverOutcome = if ($driverSummary) { [string]$driverSummary.outcome } else { 'MISSING' }
        driverExitCode = if ($driverSummary) { [int]$driverSummary.exitCode } else { $null }
        driverRoundCount = if ($driverSummary) { @($driverSummary.rounds).Count } else { 0 }
        driverFailureCode = if ($driverSummary) { [string]$driverSummary.failureCode } else { '' }
        driverPass = $driverPass
        playerExeSha256 = (Get-FileHash -LiteralPath $playerExe -Algorithm SHA256).Hash
        playerFilesManifestSha256 = if (Test-Path -LiteralPath (Join-Path $RunRoot 'player-files.json')) { (Get-FileHash -LiteralPath (Join-Path $RunRoot 'player-files.json') -Algorithm SHA256).Hash } else { '' }
        playerManifest = 'player-files.json'
        visualEvidence = 'NOT_RUN (hidden window may produce black frames; not used for PASS)'
        recoveryCompare = 'NOT_RUN (no structured Player exporter; P4.5 Compare remains BLOCKED)'
    }
    $toolSummary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $RunRoot 'runtime\tool-summary.json') -Encoding UTF8

    if ($disposition -eq 'TOOL_TIMEOUT_KILLED' -or $disposition -eq 'TOOL_TIMEOUT_NOT_OWNED' -or $disposition -eq 'TOOL_TIMEOUT_KILL_FAILED') {
        return 11
    }

    if ($nativeExit -ne 0) {
        return 10
    }

    if (-not $driverPass) {
        return 20
    }

    return 0
}

try {
    $script:RunRoot = Resolve-RunDir -Value $RunDir
    Assert-WithinBuildRoot -Path $script:RunRoot

    if ($Mode -eq 'Verify') {
        if (-not (Test-Path -LiteralPath $script:RunRoot)) {
            throw "Run directory not found: $script:RunRoot"
        }

        $manifest = Join-Path $script:RunRoot 'evidence\artifacts.json'
        if (-not (Test-Path -LiteralPath $manifest)) {
            $manifest = Join-Path $script:RunRoot 'artifacts.json'
        }

        Test-ArtifactManifest -ManifestPath $manifest
        $script:ToolExit = 0
        exit 0
    }

    if ($Mode -eq 'All' -or $Mode -eq 'Build') {
        if (Test-Path -LiteralPath $script:RunRoot) {
            throw "Refusing to reuse an existing run directory: $script:RunRoot"
        }
    }
    elseif (-not (Test-Path -LiteralPath $script:RunRoot)) {
        throw "Run directory not found: $script:RunRoot"
    }

    New-Item -ItemType Directory -Force -Path $script:RunRoot | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $script:RunRoot 'logs') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $script:RunRoot 'runtime') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $script:RunRoot 'evidence') | Out-Null

    Get-Mutex
    try {
        if ($Mode -eq 'All' -or $Mode -eq 'Build') {
            $script:ToolExit = Invoke-Build -RunRoot $script:RunRoot
        }

        if ($script:ToolExit -eq 0 -and ($Mode -eq 'All' -or $Mode -eq 'Run')) {
            $script:ToolExit = Invoke-Run -RunRoot $script:RunRoot
        }
    }
    finally {
        Release-Mutex
    }

    if (Test-Path -LiteralPath (Join-Path $script:RunRoot 'logs\player.log')) {
        $playerHash = (Get-FileHash -LiteralPath (Join-Path $script:RunRoot 'logs\player.log') -Algorithm SHA256).Hash
        Write-ToolLog ('PLAYER_LOG_SHA256 ' + $playerHash)
    }

    $manifestPath = Join-Path $script:RunRoot 'evidence\artifacts.json'
    $manifestHash = Get-ArtifactManifest -Root $script:RunRoot -OutputPath $manifestPath
    [System.IO.File]::WriteAllText(
        (Join-Path $script:RunRoot 'evidence\artifacts.json.sha256.txt'),
        ($manifestHash + '  evidence/artifacts.json' + "`n"),
        (New-Object System.Text.UTF8Encoding($false)))
    Write-ToolLog ('ARTIFACT_MANIFEST sha256=' + $manifestHash)

    Write-ToolLog ('TOOL_EXIT ' + $script:ToolExit)
    exit $script:ToolExit
}
catch {
    Write-ToolLog ('TOOL_FAILED ' + $_.Exception.GetType().Name + ': ' + $_.Exception.Message)
    Release-Mutex
    exit 99
}
