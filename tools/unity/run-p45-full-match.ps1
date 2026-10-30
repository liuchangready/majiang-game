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
    [ValidateSet('All', 'Build', 'Run', 'Collect', 'Verify')]
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

function Quote-ProcessArgument {
    param([string]$Value)

    if ($null -eq $Value -or $Value.Length -eq 0) {
        return '""'
    }

    if ($Value -notmatch '[\s"]') {
        return $Value
    }

    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append('"')
    $backslashes = 0
    foreach ($ch in $Value.ToCharArray()) {
        if ($ch -eq [char]'\') {
            $backslashes++
        }
        elseif ($ch -eq [char]'"') {
            [void]$builder.Append(('\' * (($backslashes * 2) + 1)))
            [void]$builder.Append('"')
            $backslashes = 0
        }
        else {
            if ($backslashes -gt 0) {
                [void]$builder.Append(('\' * $backslashes))
                $backslashes = 0
            }

            [void]$builder.Append($ch)
        }
    }

    if ($backslashes -gt 0) {
        [void]$builder.Append(('\' * ($backslashes * 2)))
    }

    [void]$builder.Append('"')
    return $builder.ToString()
}

function Join-ProcessArguments {
    param([string[]]$Arguments)

    $quoted = @()
    foreach ($argument in $Arguments) {
        $quoted += Quote-ProcessArgument $argument
    }

    return ($quoted -join ' ')
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

    $argumentLine = Join-ProcessArguments -Arguments $arguments
    $unityHash = (Get-FileHash -LiteralPath $UnityExe -Algorithm SHA256).Hash
    $process = Start-Process -FilePath $UnityExe -ArgumentList $argumentLine -PassThru -WindowStyle Hidden
    $unityRecord = $null
    $disposition = 'ABORTED_AFTER_LAUNCH'
    $nativeExit = $null
    try {
        $unityRecord = [ordered]@{
            schemaVersion = 1
            kind = 'p45-full-match-unity-process'
            pid = $process.Id
            startTimeUtc = $process.StartTime.ToUniversalTime().ToString('o')
            path = $UnityExe
            sha256 = $unityHash
        }
        $unityRecord | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path (Split-Path -Parent $LogPath) 'unity-process.json') -Encoding UTF8
        Write-ToolLog ('UNITY_START pid=' + $process.Id + ' exe=' + $UnityExe + ' sha256=' + $unityRecord.sha256)

        $exited = $process.WaitForExit(3600 * 1000)
        if (-not $exited) {
            Write-ToolLog ('UNITY_TIMEOUT pid=' + $process.Id)
            $stop = Stop-OwnedPlayerProcess -Process $process -Record $unityRecord -Reason 'UNITY_TIMEOUT'
            $disposition = if ($stop -eq 'KILLED') { 'TOOL_TIMEOUT_KILLED' } else { 'TOOL_TIMEOUT_' + $stop }
            return 124
        }

        $process.Refresh()
        $nativeExit = $process.ExitCode
        $disposition = if ($nativeExit -eq 0) { 'EXITED_OK' } else { 'EXITED_NONZERO' }
        if ($null -eq $nativeExit) {
            Write-ToolLog ('UNITY_EXIT_CODE_UNAVAILABLE pid=' + $process.Id)
            return 125
        }

        Write-ToolLog ('UNITY_EXIT pid=' + $process.Id + ' native=' + $nativeExit)
        return [int]$nativeExit
    }
    finally {
        if (-not $process.HasExited) {
            if ($unityRecord -ne $null) {
                $stop = Stop-OwnedPlayerProcess -Process $process -Record $unityRecord -Reason 'UNITY_FINALLY'
                if ($stop -ne 'KILLED') {
                    $disposition = 'FINALIZE_STOP_' + $stop
                }
            }
            else {
                $disposition = 'FINALIZE_IDENTITY_UNAVAILABLE'
            }
        }

        try {
            [ordered]@{
                disposition = $disposition
                nativeExitCode = $nativeExit
                pid = if ($unityRecord) { $unityRecord.pid } else { $process.Id }
                path = $UnityExe
                sha256 = $unityHash
            } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path (Split-Path -Parent $LogPath) 'unity-exit.json') -Encoding UTF8
        }
        catch {
            Write-ToolLog ('UNITY_EXIT_RECORD_FAILED ' + $_.Exception.GetType().Name)
        }
    }
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
    $controlLogs = @('logs/tool.log')
    $entries = New-Object System.Collections.Generic.List[object]
    $totalBytes = [long]0
    Get-ChildItem -LiteralPath $rootFull -Recurse -File | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($rootFull.Length).TrimStart('\', '/').Replace('\', '/')
        if ($relative -eq $manifestName -or $relative -eq $sidecarName -or
            $controlLogs -contains $relative -or
            $relative -like '*.lock' -or $relative -like '*.tmp' -or $relative -like '*.tmp.*') {
            # Manifest/self, mutable control log and transient files are intentionally absent.
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
        controlLogs = $controlLogs
        entries = $entries
    }
    $json = $manifest | ConvertTo-Json -Depth 8
    [System.IO.File]::WriteAllText($OutputPath, $json, (New-Object System.Text.UTF8Encoding($false)))
    return (Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256).Hash
}

function Test-WholePlayerManifest {
    param([string]$ManifestPath, [string]$Root)

    if (-not (Test-Path -LiteralPath $ManifestPath)) {
        throw ('PLAYER_MANIFEST_MISSING ' + $ManifestPath)
    }

    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    if ([int]$manifest.schemaVersion -ne 1 -or [string]$manifest.kind -ne 'p45-full-match-player-files') {
        throw 'PLAYER_MANIFEST_INVALID schema/kind'
    }

    $rootFull = [System.IO.Path]::GetFullPath($Root)
    if ([string]::IsNullOrWhiteSpace([string]$manifest.root) -or
        -not [string]::Equals([System.IO.Path]::GetFullPath([string]$manifest.root), $rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw ('PLAYER_MANIFEST_ROOT_MISMATCH expected=' + $rootFull + ' actual=' + [string]$manifest.root)
    }

    $files = @($manifest.files)
    if ([int]$manifest.fileCount -le 0 -or $files.Count -ne [int]$manifest.fileCount) {
        throw 'PLAYER_MANIFEST_INVALID fileCount'
    }

    $expected = @{}
    [long]$total = 0
    foreach ($entry in $files) {
        $relative = [string]$entry.path
        if ([string]::IsNullOrWhiteSpace($relative) -or $expected.ContainsKey($relative)) {
            throw ('PLAYER_MANIFEST_INVALID entry ' + $relative)
        }

        $expected[$relative] = $true
        $full = [System.IO.Path]::GetFullPath((Join-Path $rootFull $relative))
        if (-not $full.StartsWith($rootFull + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw ('PLAYER_MANIFEST_ESCAPE ' + $relative)
        }

        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
            throw ('PLAYER_MANIFEST_MISSING ' + $relative)
        }

        $item = Get-Item -LiteralPath $full
        $hash = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash
        if ([long]$item.Length -ne [long]$entry.bytes -or $hash -ne [string]$entry.sha256) {
            throw ('PLAYER_MANIFEST_MISMATCH ' + $relative)
        }

        $total += $item.Length
    }

    if ($total -ne [long]$manifest.totalBytes) {
        throw 'PLAYER_MANIFEST_INVALID totalBytes'
    }

    $actual = @{}
    Get-ChildItem -LiteralPath $rootFull -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($rootFull.Length).TrimStart('\', '/').Replace('\', '/')
        $actual[$relative] = $true
    }
    foreach ($path in $actual.Keys) {
        if (-not $expected.ContainsKey($path)) {
            throw ('PLAYER_MANIFEST_EXTRA ' + $path)
        }
    }

    Write-ToolLog ('PLAYER_MANIFEST_VERIFIED files=' + $expected.Count + ' sha256=' + (Get-FileHash -LiteralPath $ManifestPath -Algorithm SHA256).Hash)
}

function Test-ArtifactManifest {
    param([string]$ManifestPath, [string]$ExpectedRoot)

    if (-not (Test-Path -LiteralPath $ManifestPath)) {
        throw "Manifest not found: $ManifestPath"
    }

    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    if ([int]$manifest.schemaVersion -ne 1 -or [string]$manifest.kind -ne 'p45-full-match-evidence') {
        throw 'MANIFEST_INVALID schema/kind'
    }

    $entries = @($manifest.entries)
    if ([int]$manifest.entryCount -le 0 -or $entries.Count -ne [int]$manifest.entryCount) {
        throw 'MANIFEST_INVALID entryCount'
    }

    if ([string]::IsNullOrWhiteSpace([string]$manifest.root)) {
        throw 'MANIFEST_INVALID root'
    }

    $root = [System.IO.Path]::GetFullPath($ExpectedRoot)
    if (-not [string]::Equals([System.IO.Path]::GetFullPath([string]$manifest.root), $root, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw ('MANIFEST_INVALID root mismatch expected=' + $root + ' actual=' + [string]$manifest.root)
    }

    $manifestName = 'evidence/artifacts.json'
    $sidecarName = 'evidence/artifacts.json.sha256.txt'
    $fixedControlLogs = @('logs/tool.log')
    $declaredControlLogs = @($manifest.controlLogs)
    if ($declaredControlLogs.Count -ne $fixedControlLogs.Count -or
        $declaredControlLogs[0] -ne $fixedControlLogs[0]) {
        throw 'MANIFEST_INVALID controlLogs must be exactly logs/tool.log'
    }

    $controlLogs = $fixedControlLogs
    $seen = @{}
    [long]$totalBytes = 0
    foreach ($entry in $entries) {
        $relative = [string]$entry.path
        if ([string]::IsNullOrWhiteSpace($relative) -or $seen.ContainsKey($relative)) {
            throw ('MANIFEST_INVALID ' + $(if ($relative) { 'DUPLICATE ' + $relative } else { 'EMPTY_PATH' }))
        }

        $seen[$relative] = $true
        $full = [System.IO.Path]::GetFullPath((Join-Path $root $relative))
        if (-not $full.StartsWith($root + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw ('MANIFEST_INVALID ESCAPE ' + $relative)
        }

        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
            throw ('MANIFEST_INVALID MISSING ' + $relative)
        }

        $item = Get-Item -LiteralPath $full
        $hash = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash
        if ([long]$item.Length -ne [long]$entry.bytes -or $hash -ne [string]$entry.sha256) {
            throw ('MANIFEST_INVALID MISMATCH ' + $relative)
        }

        $totalBytes += $item.Length
    }

    if ($totalBytes -ne [long]$manifest.totalBytes) {
        throw 'MANIFEST_INVALID totalBytes'
    }

    $actual = @{}
    Get-ChildItem -LiteralPath $root -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($root.Length).TrimStart('\', '/').Replace('\', '/')
        if ($relative -eq $manifestName -or $relative -eq $sidecarName -or
            $controlLogs -contains $relative -or
            $relative -like '*.lock' -or $relative -like '*.tmp' -or $relative -like '*.tmp.*') {
            # Excluded from the sealed evidence set.
        }
        else {
            $actual[$relative] = $true
        }
    }
    foreach ($path in $actual.Keys) {
        if (-not $seen.ContainsKey($path)) {
            throw ('MANIFEST_INVALID EXTRA ' + $path)
        }
    }

    $sidecar = Join-Path (Split-Path -Parent $ManifestPath) 'artifacts.json.sha256.txt'
    if (-not (Test-Path -LiteralPath $sidecar)) {
        throw 'MANIFEST_INVALID sidecar missing'
    }

    $sidecarText = (Get-Content -LiteralPath $sidecar -Raw).Trim()
    $expectedSidecar = (Get-FileHash -LiteralPath $ManifestPath -Algorithm SHA256).Hash
    if (-not $sidecarText.StartsWith($expectedSidecar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'MANIFEST_INVALID sidecar hash'
    }

    $playerManifest = Join-Path $root 'player-files.json'
    if (Test-Path -LiteralPath $playerManifest) {
        Test-WholePlayerManifest -ManifestPath $playerManifest -Root (Join-Path $root 'player')
    }

    Write-ToolLog ('MANIFEST_VERIFIED entries=' + $seen.Count + ' sha256=' + $expectedSidecar)
}

function Write-SourceRecord {
    param([string]$RunRoot)

    $logs = Join-Path $RunRoot 'logs'
    New-Item -ItemType Directory -Force -Path $logs | Out-Null
    $head = (& git -C $script:RepoRoot rev-parse HEAD).Trim()
    $tree = (& git -C $script:RepoRoot rev-parse ('HEAD^{tree}')).Trim()
    $branch = (& git -C $script:RepoRoot rev-parse --abbrev-ref HEAD).Trim()
    $status = (& git -C $script:RepoRoot status --porcelain=v1) -join "`n"
    $dirty = -not [string]::IsNullOrWhiteSpace($status)
    $stat = (& git -C $script:RepoRoot diff --stat) -join "`n"
    & git -C $script:RepoRoot diff --check *> (Join-Path $logs 'diff-check.log')
    $diffCheck = $LASTEXITCODE

    $dirtyPatchPath = Join-Path $logs 'source.dirty.patch'
    $stagedPatchPath = Join-Path $logs 'source.staged.patch'
    $dirtyPatchText = (& git -C $script:RepoRoot diff --binary) -join "`n"
    $stagedPatchText = (& git -C $script:RepoRoot diff --cached --binary) -join "`n"
    [System.IO.File]::WriteAllText($dirtyPatchPath, $dirtyPatchText, (New-Object System.Text.UTF8Encoding($false)))
    [System.IO.File]::WriteAllText($stagedPatchPath, $stagedPatchText, (New-Object System.Text.UTF8Encoding($false)))

    $untracked = New-Object System.Collections.Generic.List[object]
    $untrackedLines = @(& git -C $script:RepoRoot ls-files --others --exclude-standard)
    foreach ($relative in $untrackedLines) {
        if ([string]::IsNullOrWhiteSpace($relative)) { continue }
        $full = Join-Path $script:RepoRoot $relative
        if (Test-Path -LiteralPath $full -PathType Leaf) {
            $untracked.Add([ordered]@{
                path = $relative.Replace('\', '/')
                bytes = [long](Get-Item -LiteralPath $full).Length
                sha256 = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash
            }) | Out-Null
        }
    }
    $untrackedPath = Join-Path $logs 'source.untracked.json'
    if ($untracked.Count -eq 0) {
        [System.IO.File]::WriteAllText($untrackedPath, "[]`n", (New-Object System.Text.UTF8Encoding($false)))
    }
    else {
        $untracked | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $untrackedPath -Encoding UTF8
    }

    $statusPath = Join-Path $logs 'source.status.txt'
    [System.IO.File]::WriteAllText($statusPath, $status, (New-Object System.Text.UTF8Encoding($false)))

    $record = [ordered]@{
        schemaVersion = 1
        kind = 'p45-full-match-source'
        capturedAt = [DateTimeOffset]::Now.ToString('o')
        head = $head
        tree = $tree
        branch = $branch
        dirty = $dirty
        statusPorcelain = $status
        statusSha256 = (Get-FileHash -LiteralPath $statusPath -Algorithm SHA256).Hash
        diffStat = $stat
        diffCheckExit = $diffCheck
        dirtyPatch = 'logs/source.dirty.patch'
        dirtyPatchSha256 = (Get-FileHash -LiteralPath $dirtyPatchPath -Algorithm SHA256).Hash
        stagedPatch = 'logs/source.staged.patch'
        stagedPatchSha256 = (Get-FileHash -LiteralPath $stagedPatchPath -Algorithm SHA256).Hash
        untracked = 'logs/source.untracked.json'
        untrackedCount = $untracked.Count
        untrackedSha256 = (Get-FileHash -LiteralPath $untrackedPath -Algorithm SHA256).Hash
    }
    $record | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $RunRoot 'source.json') -Encoding UTF8
    Write-ToolLog ('SOURCE head=' + $head + ' tree=' + $tree + ' branch=' + $branch + ' dirty=' + $dirty + ' diffCheck=' + $diffCheck)
}

function Write-GeneratedAssetEvidence {
    param([string]$RunRoot)

    $logs = Join-Path $RunRoot 'logs'
    $generatedDirs = @(
        'unity/Mahjong.Unity/Assets/Art',
        'unity/Mahjong.Unity/Assets/Plugins',
        'unity/Mahjong.Unity/Assets/Resources',
        'unity/Mahjong.Unity/Mahjong.Unity.slnx',
        'unity/Mahjong.Unity/ProjectSettings'
    )
    $patchPath = Join-Path $logs 'generated-unity.patch'
    $patchText = (& git -C $script:RepoRoot diff --binary -- @generatedDirs) -join "`n"
    [System.IO.File]::WriteAllText($patchPath, $patchText, (New-Object System.Text.UTF8Encoding($false)))
    $statusText = (& git -C $script:RepoRoot status --porcelain=v1 -- @generatedDirs) -join "`n"
    [System.IO.File]::WriteAllText((Join-Path $logs 'generated-unity-status.txt'), $statusText, (New-Object System.Text.UTF8Encoding($false)))
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($dir in $generatedDirs) {
        Get-ChildItem -LiteralPath (Join-Path $script:RepoRoot $dir) -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
            $relative = $_.FullName.Substring($script:RepoRoot.Length).TrimStart('\', '/').Replace('\', '/')
            $lines.Add(((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash + '  ' + $relative)) | Out-Null
        }
    }
    $lines | Set-Content -LiteralPath (Join-Path $logs 'generated-unity-file-hashes.txt') -Encoding UTF8
    Write-ToolLog ('GENERATED_ASSET_EVIDENCE files=' + $lines.Count + ' patchSha256=' + (Get-FileHash -LiteralPath $patchPath -Algorithm SHA256).Hash)
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
    Write-GeneratedAssetEvidence -RunRoot $RunRoot
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

        $recordPath = [string]$Record.path
        $recordHash = [string]$Record.sha256
        if ([string]::IsNullOrEmpty($recordPath)) { $recordPath = [string]$Record.exe }
        if ([string]::IsNullOrEmpty($recordHash)) { $recordHash = [string]$Record.exeSha256 }
        $current = Get-ProcessRecord -Process $Process
        return ($current.pid -eq [int]$Record.pid -and
                $current.startTimeUtc -eq [string]$Record.startTimeUtc -and
                $current.path -eq $recordPath -and
                $current.sha256 -eq $recordHash)
    }
    catch {
        return $false
    }
}

function Stop-OwnedPlayerProcess {
    param([System.Diagnostics.Process]$Process, $Record, [string]$Reason)

    if (-not (Test-OwnedPlayerProcess -Process $Process -Record $Record)) {
        Write-ToolLog ('STOP_REFUSED reason=' + $Reason + ' pid=' + $(if ($Process) { $Process.Id } else { 'none' }))
        return 'NOT_OWNED_OR_EXITED'
    }

    try {
        $Process.Kill()
        $exited = $Process.WaitForExit(10000)
        $Process.Refresh()
        if (-not $exited -or -not $Process.HasExited) {
            Write-ToolLog ('STOP_TIMEOUT reason=' + $Reason + ' pid=' + $Process.Id)
            return 'KILL_TIMEOUT'
        }

        Write-ToolLog ('STOP_OWNED reason=' + $Reason + ' pid=' + $Process.Id)
        return 'KILLED'
    }
    catch {
        Write-ToolLog ('STOP_FAILED reason=' + $Reason + ' pid=' + $Process.Id + ' error=' + $_.Exception.GetType().Name)
        return 'KILL_FAILED'
    }
}

function Invoke-Run {
    param([string]$RunRoot)

    $playerExe = Join-Path $RunRoot 'player\Mahjong.exe'
    if (-not (Test-Path -LiteralPath $playerExe)) {
        throw "Player not found: $playerExe"
    }

    $playerManifestPath = Join-Path $RunRoot 'player-files.json'
    Test-WholePlayerManifest -ManifestPath $playerManifestPath -Root (Join-Path $RunRoot 'player')

    $runId = 'p45-' + [DateTimeOffset]::Now.ToString('yyyyMMdd-HHmmss') + '-' + ([guid]::NewGuid().ToString('N').Substring(0, 8))
    $executionRoot = Join-Path $RunRoot ('executions\' + $runId)
    if (Test-Path -LiteralPath $executionRoot) {
        throw "Refusing to reuse an existing execution directory: $executionRoot"
    }

    $logs = Join-Path $executionRoot 'logs'
    $driverDir = Join-Path $executionRoot 'driver'
    $dataDir = Join-Path $executionRoot 'data'
    $shotDir = Join-Path $executionRoot 'screenshots'
    $runtimeDir = Join-Path $executionRoot 'runtime'
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
        '-full-match-driver', $driverDir,
        '-full-match-run-id', $runId
    )

    $startedAt = [DateTimeOffset]::UtcNow
    $argumentLine = Join-ProcessArguments -Arguments $arguments
    $playerHash = (Get-FileHash -LiteralPath $playerExe -Algorithm SHA256).Hash
    $process = Start-Process -FilePath $playerExe -ArgumentList $argumentLine -PassThru -WindowStyle Hidden
    $record = $null
    $disposition = 'ABORTED_AFTER_LAUNCH'
    $nativeExit = $null
    try {
        $record = [ordered]@{
            schemaVersion = 1
            kind = 'p45-full-match-player-process'
            runId = $runId
            startedAtUtc = $startedAt.ToString('o')
            commandLine = $arguments
            exe = $playerExe
            exeSha256 = $playerHash
            pid = $process.Id
            startTimeUtc = $process.StartTime.ToUniversalTime().ToString('o')
        }
        $record.path = $record.exe
        $record.sha256 = $record.exeSha256
        $record | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runtimeDir 'process.json') -Encoding UTF8
        Write-ToolLog ('PLAYER_START runId=' + $runId + ' pid=' + $process.Id + ' exe=' + $playerExe + ' sha256=' + $record.sha256)

        $exited = $process.WaitForExit([math]::Max(1, $TimeoutSeconds) * 1000)
        if (-not $exited) {
            Write-ToolLog ('PLAYER_TIMEOUT pid=' + $process.Id + ' timeoutSeconds=' + $TimeoutSeconds)
            $stop = Stop-OwnedPlayerProcess -Process $process -Record $record -Reason 'PLAYER_TIMEOUT'
            if ($stop -eq 'KILLED') {
                $disposition = 'TOOL_TIMEOUT_KILLED'
            }
            elseif ($stop -eq 'KILL_FAILED') {
                $disposition = 'TOOL_TIMEOUT_KILL_FAILED'
            }
            else {
                $disposition = 'TOOL_TIMEOUT_NOT_OWNED'
            }
        }
        else {
            $process.Refresh()
            $nativeExit = $process.ExitCode
            $disposition = if ($nativeExit -eq 0) { 'EXITED_OK' } else { 'EXITED_NONZERO' }
        }
    }
    finally {
        if (-not $process.HasExited) {
            if ($record -ne $null) {
                $stop = Stop-OwnedPlayerProcess -Process $process -Record $record -Reason 'PLAYER_FINALLY'
                if ($stop -ne 'KILLED') {
                    $disposition = 'FINALIZE_STOP_' + $stop
                }
            }
            else {
                # Identity was never captured; do not kill an unproven process.
                $disposition = 'FINALIZE_IDENTITY_UNAVAILABLE'
            }
        }

        try {
            [ordered]@{
                schemaVersion = 1
                kind = 'p45-full-match-player-exit'
                runId = $runId
                finishedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
                disposition = $disposition
                nativeExitCode = $nativeExit
                timeoutSeconds = $TimeoutSeconds
            } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runtimeDir 'exit.json') -Encoding UTF8
            Write-ToolLog ('PLAYER_EXIT runId=' + $runId + ' disposition=' + $disposition + ' native=' + $nativeExit)
        }
        catch {
            Write-ToolLog ('PLAYER_EXIT_RECORD_FAILED ' + $_.Exception.GetType().Name)
        }
    }

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

    $summaryRunIdMatches = $false
    $summaryFresh = $false
    if ($driverSummary) {
        $summaryRunIdMatches = [string]$driverSummary.runId -eq $runId
        $summaryFresh = (Get-Item -LiteralPath $driverSummaryPath).LastWriteTimeUtc -ge $startedAt.UtcDateTime
    }

    $driverPass = $false
    if ($driverSummary -and $summaryRunIdMatches -and $summaryFresh) {
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
        runId = $runId
        runDir = $RunRoot
        executionRoot = $executionRoot
        finishedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        disposition = $disposition
        nativeExitCode = $nativeExit
        driverSummaryPresent = ($null -ne $driverSummary)
        driverSummaryRunIdMatches = $summaryRunIdMatches
        driverSummaryFresh = $summaryFresh
        driverOutcome = if ($driverSummary) { [string]$driverSummary.outcome } else { 'MISSING' }
        driverExitCode = if ($driverSummary) { [int]$driverSummary.exitCode } else { $null }
        driverRoundCount = if ($driverSummary) { @($driverSummary.rounds).Count } else { 0 }
        driverFailureCode = if ($driverSummary) { [string]$driverSummary.failureCode } else { '' }
        driverPass = $driverPass
        playerExeSha256 = (Get-FileHash -LiteralPath $playerExe -Algorithm SHA256).Hash
        playerFilesManifestSha256 = if (Test-Path -LiteralPath $playerManifestPath) { (Get-FileHash -LiteralPath $playerManifestPath -Algorithm SHA256).Hash } else { '' }
        playerManifest = 'player-files.json'
        visualEvidence = 'NOT_RUN (hidden window may produce black frames; not used for PASS)'
        recoveryCompare = 'NOT_RUN (no structured Player exporter; P4.5 Compare remains BLOCKED)'
    }
    $toolSummary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runtimeDir 'tool-summary.json') -Encoding UTF8

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
        Test-ArtifactManifest -ManifestPath $manifest -ExpectedRoot $script:RunRoot
        $script:ToolExit = 0
        exit 0
    }

    if ($Mode -eq 'Collect') {
        if (-not (Test-Path -LiteralPath $script:RunRoot)) {
            throw "Run directory not found: $script:RunRoot"
        }

        $evidenceDir = Join-Path $script:RunRoot 'evidence'
        New-Item -ItemType Directory -Force -Path $evidenceDir | Out-Null
        $manifest = Join-Path $evidenceDir 'artifacts.json'
        $hash = Get-ArtifactManifest -Root $script:RunRoot -OutputPath $manifest
        [System.IO.File]::WriteAllText(
            (Join-Path $evidenceDir 'artifacts.json.sha256.txt'),
            ($hash + '  evidence/artifacts.json' + "`n"),
            (New-Object System.Text.UTF8Encoding($false)))
        Test-ArtifactManifest -ManifestPath $manifest -ExpectedRoot $script:RunRoot
        Write-ToolLog ('ARTIFACT_MANIFEST sha256=' + $hash)
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
