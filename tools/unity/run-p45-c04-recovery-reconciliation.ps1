#requires -Version 5.1
<#
.SYNOPSIS
    C04-D01 real Windows Player recovery-request reconciliation runner.

.DESCRIPTION
    Builds (or binds) one Player, commits one real Discard in a fresh local room
    with the C04 probe, applies the frozen save fault, and runs the real recovered
    session through TableSession.ReconcilePendingAsync. Every heavy Unity/Player
    stage holds Local\MahjongV5P45UnityValidation. Builds, Library, TEMP/TMP and
    evidence live under this worktree's ignored build/<GUID> directory.

.PARAMETER Mode
    All   build shared plugins + Player, then prepare and run all eight G6 rows
    Build only build the Player
    Run   run all eight G6 rows using an existing player/ directory
    Verify re-run the verifier over an existing evidence root

.PARAMETER EvidenceRoot
    New evidence root. Required for Build/Run unless a unique root is generated.

.PARAMETER PlayerPath
    Optional already-built Player. When present, Build is skipped and the Player
    SHA256 is bound into every result.
#>
[CmdletBinding()]
param(
    [ValidateSet('All', 'Build', 'Run', 'Verify')]
    [string]$Mode = 'All',

    [string]$EvidenceRoot,

    [string]$PlayerPath,

    [string]$UnityPath = 'D:\Unity\Hub\Editor\6000.3.27f1\Editor\Unity.exe',

    [string]$DotnetPath = 'D:\Unity\dotnet\dotnet.exe',

    [int]$TimeoutSeconds = 240,

    [string[]]$Cases = @(
        'C04-G6-01', 'C04-G6-02', 'C04-G6-03', 'C04-G6-04',
        'C04-G6-05', 'C04-G6-06', 'C04-G6-07', 'C04-G6-08')
)

$ErrorActionPreference = 'Stop'
$script:RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$script:ToolVersion = 'c04-recovery-request/1'
$script:MutexName = 'Local\MahjongV5P45UnityValidation'
$script:Mutex = $null
$script:MutexHeld = $false
$script:Root = $null
$script:Temp = $null
$script:Player = $null
$script:PlayerSha = $null
$script:PackageSha = $null
$script:PackageCount = 0
$script:SourceHead = $null
$script:BuildCommit = $null
$script:OldTemp = $null
$script:OldTmp = $null
$script:CaseResults = New-Object System.Collections.Generic.List[object]

function Write-ToolLog {
    param([string]$Message)
    $line = '[' + [DateTimeOffset]::Now.ToString('o') + '] ' + $Message
    Write-Host $line
    if (-not [string]::IsNullOrWhiteSpace($script:Root)) {
        $logs = Join-Path $script:Root 'logs'
        if (Test-Path -LiteralPath $logs) {
            Add-Content -LiteralPath (Join-Path $logs 'tool.log') -Value $line -Encoding UTF8
        }
    }
}

function Get-Sha256 {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Get-StringSha256 {
    param([string]$Text)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
        return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToUpperInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Write-JsonFile {
    param([string]$Path, $Object)
    $json = $Object | ConvertTo-Json -Depth 12
    [IO.File]::WriteAllText($Path, $json, (New-Object Text.UTF8Encoding($false)))
    return (Get-Sha256 $Path)
}

function Acquire-Mutex {
    $script:Mutex = New-Object System.Threading.Mutex($false, $script:MutexName)
    $script:MutexHeld = $script:Mutex.WaitOne([Math]::Max(1000, 300000))
    if (-not $script:MutexHeld) {
        throw 'UNITY_MUTEX_TIMEOUT: another Unity/Player validation holds ' + $script:MutexName
    }
}

function Release-Mutex {
    if ($script:MutexHeld -and $null -ne $script:Mutex) {
        try { $script:Mutex.ReleaseMutex() } catch { }
        $script:MutexHeld = $false
    }
    if ($null -ne $script:Mutex) {
        try { $script:Mutex.Dispose() } catch { }
        $script:Mutex = $null
    }
}

function Initialize-Root {
    if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
        $stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
        $EvidenceRoot = Join-Path $script:RepoRoot ('build/c04-recovery-request/' + $stamp + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    }
    $script:Root = [IO.Path]::GetFullPath($EvidenceRoot)
    if (Test-Path -LiteralPath $script:Root) {
        throw ('EVIDENCE_REUSE: refusing to reuse existing evidence root ' + $script:Root)
    }
    New-Item -ItemType Directory -Force -Path $script:Root | Out-Null
    $script:Temp = Join-Path $script:Root 'temp'
    New-Item -ItemType Directory -Force -Path $script:Temp, (Join-Path $script:Root 'logs') | Out-Null
    $script:OldTemp = $env:TEMP
    $script:OldTmp = $env:TMP
    $env:TEMP = $script:Temp
    $env:TMP = $script:Temp
}

function Initialize-ExistingRoot {
    if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
        throw 'EVIDENCE_ROOT_REQUIRED'
    }
    $script:Root = [IO.Path]::GetFullPath($EvidenceRoot)
    if (-not (Test-Path -LiteralPath $script:Root -PathType Container)) {
        throw ('EVIDENCE_ROOT_NOT_FOUND ' + $script:Root)
    }
    $script:Temp = Join-Path $script:Root 'temp'
    New-Item -ItemType Directory -Force -Path $script:Temp | Out-Null
    $script:OldTemp = $env:TEMP
    $script:OldTmp = $env:TMP
    $env:TEMP = $script:Temp
    $env:TMP = $script:Temp
}

function Initialize-RunRoot {
    if (-not [string]::IsNullOrWhiteSpace($EvidenceRoot) -and
        (Test-Path -LiteralPath ([IO.Path]::GetFullPath($EvidenceRoot)) -PathType Container)) {
        Initialize-ExistingRoot
    }
    else {
        Initialize-Root
    }
}

function Restore-Environment {
    if ($null -ne $script:OldTemp) { $env:TEMP = $script:OldTemp }
    if ($null -ne $script:OldTmp) { $env:TMP = $script:OldTmp }
}

function Invoke-Native {
    param(
        [string]$FilePath,
        [string[]]$Arguments,
        [string]$LogPath
    )
    Write-ToolLog ('COMMAND ' + $FilePath + ' ' + ($Arguments -join ' '))
    $output = & $FilePath @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    $text = ($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
    [IO.File]::WriteAllText($LogPath, $text, (New-Object Text.UTF8Encoding($false)))
    Write-ToolLog ('NATIVE_EXIT ' + $exitCode + ' log=' + $LogPath)
    return [int]$exitCode
}

function Get-UnityProcesses {
    param([string]$ProjectPath)
    $full = [IO.Path]::GetFullPath($ProjectPath)
    return @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" -ErrorAction SilentlyContinue |
        Where-Object {
            $_.CommandLine -and
            $_.CommandLine.IndexOf($full, [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $_.CommandLine.IndexOf('Unity Hub', [StringComparison]::OrdinalIgnoreCase) -lt 0
        })
}

function Wait-UnityExit {
    param([string]$ProjectPath, [int]$Seconds)
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $processes = @(Get-UnityProcesses -ProjectPath $ProjectPath)
        if ($processes.Count -eq 0) { return $true }
        Start-Sleep -Seconds 2
    }
    return $false
}

function Invoke-Player {
    param(
        [string]$Directory,
        [string]$ProbeMode,
        [string]$CaseId,
        [string]$Fault,
        [string]$Outcome,
        [bool]$Force
    )
    New-Item -ItemType Directory -Force -Path $Directory, (Join-Path $Directory 'appdata') | Out-Null
    $log = Join-Path $Directory 'player.log'
    $stdout = Join-Path $Directory 'stdout.log'
    $stderr = Join-Path $Directory 'stderr.log'
    $arguments = @(
        '-screen-width', '1280',
        '-screen-height', '720',
        '-screen-fullscreen', '0',
        '-logFile', $log,
        '-smoke-data', (Join-Path $Directory 'appdata'),
        '-c04-recovery', $Directory,
        '-c04-recovery-mode', $ProbeMode,
        '-c04-recovery-case', $CaseId,
        '-c04-recovery-fault', $Fault,
        '-c04-recovery-outcome', $Outcome,
        '-c04-recovery-player-sha', $script:PlayerSha,
        '-c04-recovery-package-sha', $script:PackageSha,
        '-c04-recovery-package-count', [string]$script:PackageCount,
        '-c04-recovery-source-head', $script:SourceHead,
        '-c04-recovery-build-commit', $script:BuildCommit,
        '-c04-recovery-ruleset', '1'
    )
    if ($Force) { $arguments += '-c04-recovery-force' }
    $argumentText = ($arguments | ForEach-Object {
        if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
    }) -join ' '
    Write-ToolLog ('PLAYER ' + $ProbeMode + ' case=' + $CaseId + ' dir=' + $Directory)
    [IO.File]::WriteAllText($stdout, '', (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText($stderr, '', (New-Object Text.UTF8Encoding($false)))
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $script:Player
    $psi.Arguments = $argumentText
    $psi.WorkingDirectory = Split-Path -Parent $script:Player
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.EnvironmentVariables['TEMP'] = $script:Temp
    $psi.EnvironmentVariables['TMP'] = $script:Temp
    $process = [System.Diagnostics.Process]::Start($psi)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline -and -not $process.HasExited) {
        Start-Sleep -Milliseconds 100
    }
    $timedOut = -not $process.HasExited
    if ($timedOut) {
        try { $process.Kill() } catch { }
        try { [void]$process.WaitForExit(5000) } catch { }
    }
    $native = $null
    try {
        $process.WaitForExit()
        $process.Refresh()
        $native = [int]$process.ExitCode
    }
    catch {
        try { $native = [int]$process.ExitCode } catch { }
    }
    $start = $null
    try { $start = $process.StartTime.ToUniversalTime().ToString('o') } catch { }
    $record = [ordered]@{
        probeMode = $ProbeMode
        caseId = $CaseId
        fault = $Fault
        expectedOutcome = $Outcome
        pid = $process.Id
        startTimeUtc = $start
        path = $script:Player
        sha256 = $script:PlayerSha
        arguments = $arguments
        timedOut = $timedOut
        nativeExitCode = $native
        log = $log
        stdout = $stdout
        stderr = $stderr
    }
    Write-JsonFile -Path (Join-Path $Directory 'launch.json') -Object $record | Out-Null
    Write-ToolLog ('PLAYER_EXIT case=' + $CaseId + ' native=' + $native + ' timeout=' + $timedOut)
    return [pscustomobject]$record
}

function Apply-Fault {
    param([string]$Directory, [string]$Fault)
    $primary = Join-Path $Directory 'local\local-main.save'
    $backup = Join-Path $Directory 'local\local-main.save.prev'
    if (-not [IO.File]::Exists($primary)) { throw ('SAVE_MISSING ' + $primary) }
    if (-not [IO.File]::Exists($backup)) { throw ('BACKUP_MISSING ' + $backup) }
    switch ($Fault) {
        'None' { }
        'TruncatePrimary' {
            $bytes = [IO.File]::ReadAllBytes($primary)
            $keep = [Math]::Max(1, [int]($bytes.Length / 2))
            [IO.File]::WriteAllBytes($primary, $bytes[0..($keep - 1)])
        }
        'BadChecksumPrimary' {
            $bytes = [IO.File]::ReadAllBytes($primary)
            if ($bytes.Length -le 44) { throw 'SAVE_FORMAT: primary too short' }
            $bytes[44] = [byte]($bytes[44] -bxor 0x5A)
            [IO.File]::WriteAllBytes($primary, $bytes)
        }
        'IncompatiblePrimary' {
            $bytes = [IO.File]::ReadAllBytes($primary)
            if ($bytes.Length -lt 44) { throw 'SAVE_FORMAT: primary too short' }
            $bytes[4] = 2; $bytes[5] = 0; $bytes[6] = 0; $bytes[7] = 0
            [IO.File]::WriteAllBytes($primary, $bytes)
        }
        'MissingPrimaryWithBackup' { Remove-Item -LiteralPath $primary -Force }
        'BothMissing' {
            Remove-Item -LiteralPath $primary -Force
            Remove-Item -LiteralPath $backup -Force
        }
        'BothBad' {
            $bytes = [IO.File]::ReadAllBytes($primary)
            $keep = [Math]::Max(1, [int]($bytes.Length / 3))
            [IO.File]::WriteAllBytes($primary, $bytes[0..($keep - 1)])
            $backupBytes = [IO.File]::ReadAllBytes($backup)
            if ($backupBytes.Length -le 44) { throw 'SAVE_FORMAT: backup too short' }
            $backupBytes[44] = [byte]($backupBytes[44] -bxor 0xA5)
            [IO.File]::WriteAllBytes($backup, $backupBytes)
        }
        default { throw ('UNSUPPORTED_FAULT ' + $Fault) }
    }
}

function New-CaseInput {
    param([string]$CaseDir, [string]$CaseId, [string]$Fault, [string]$Outcome, $Prepare)
    $copy = @('local', 'outbox', 'expected.txt')
    foreach ($name in $copy) {
        $source = Join-Path $Prepare $name
        if (Test-Path -LiteralPath $source -PathType Container) {
            Copy-Item -LiteralPath $source -Destination (Join-Path $CaseDir $name) -Recurse -Force
        }
        else {
            Copy-Item -LiteralPath $source -Destination (Join-Path $CaseDir $name) -Force
        }
    }
    Apply-Fault -Directory $CaseDir -Fault $Fault
    $primary = Join-Path $CaseDir 'local\local-main.save'
    $backup = Join-Path $CaseDir 'local\local-main.save.prev'
    $input = [ordered]@{
        schemaVersion = 1
        kind = 'c04-recovery-case-input'
        caseId = $CaseId
        fault = $Fault
        expectedOutcome = $Outcome
        playerSha256 = $script:PlayerSha
        packageSha256 = $script:PackageSha
        packageFileCount = $script:PackageCount
        sourceHead = $script:SourceHead
        buildCommit = $script:BuildCommit
        primaryBeforeFaultSha256 = if ([IO.File]::Exists($primary)) { Get-Sha256 $primary } else { '' }
        backupBeforeFaultSha256 = if ([IO.File]::Exists($backup)) { Get-Sha256 $backup } else { '' }
        prepare = $Prepare
    }
    Write-JsonFile -Path (Join-Path $CaseDir 'input.json') -Object $input | Out-Null
}

function Get-CaseDefinition {
    param([string]$CaseId)
    switch ($CaseId) {
        'C04-G6-01' { return @{ Fault = 'None'; Outcome = 'RECOVERED'; Force = $false } }
        'C04-G6-02' { return @{ Fault = 'None'; Outcome = 'RECOVERED'; Force = $true } }
        'C04-G6-03' { return @{ Fault = 'TruncatePrimary'; Outcome = 'RECOVERED_FROM_BACKUP'; Force = $false } }
        'C04-G6-04' { return @{ Fault = 'BadChecksumPrimary'; Outcome = 'RECOVERED_FROM_BACKUP'; Force = $false } }
        'C04-G6-05' { return @{ Fault = 'MissingPrimaryWithBackup'; Outcome = 'RECOVERED_FROM_BACKUP'; Force = $false } }
        'C04-G6-06' { return @{ Fault = 'BothMissing'; Outcome = 'FRESH_START'; Force = $false } }
        'C04-G6-07' { return @{ Fault = 'BothBad'; Outcome = 'REJECTED'; Force = $false } }
        'C04-G6-08' { return @{ Fault = 'IncompatiblePrimary'; Outcome = 'REJECTED'; Force = $false } }
        default { throw ('UNKNOWN_CASE ' + $CaseId) }
    }
}

function Build-Player {
    $project = Join-Path $script:RepoRoot 'unity\Mahjong.Unity'
    $pluginDir = Join-Path $project 'Assets\Plugins\Mahjong'
    $dirty = (& git -C $script:RepoRoot status --porcelain=v1 -- unity/Mahjong.Unity/Assets/Plugins/Mahjong) -join "`n"
    if (-not [string]::IsNullOrWhiteSpace($dirty)) {
        throw ('PLUGIN_DIR_DIRTY: refusing to build over existing plugin changes: ' + $dirty)
    }
    $before = @{}
    Get-ChildItem -LiteralPath $pluginDir -File | ForEach-Object { $before[$_.Name] = Get-Sha256 $_.FullName }
    Write-JsonFile -Path (Join-Path $script:Root 'plugin-before.json') -Object $before | Out-Null

    $sharedLog = Join-Path $script:Root 'logs\build-shared.log'
    $sharedExit = Invoke-Native -FilePath 'powershell.exe' -Arguments @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
        (Join-Path $script:RepoRoot 'tools\unity\build-shared.ps1'), '-Configuration', 'Release') -LogPath $sharedLog
    if ($sharedExit -ne 0) { throw ('BUILD_SHARED_FAILED exit=' + $sharedExit) }

    $checkLog = Join-Path $script:Root 'logs\check-shared-plugins.log'
    $checkExit = Invoke-Native -FilePath 'powershell.exe' -Arguments @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
        (Join-Path $script:RepoRoot 'tools\unity\check-shared-plugins.ps1'), '-Configuration', 'Release') -LogPath $checkLog
    if ($checkExit -ne 0) { throw ('CHECK_SHARED_PLUGINS_FAILED exit=' + $checkExit) }

    $after = @{}
    Get-ChildItem -LiteralPath $pluginDir -File | ForEach-Object { $after[$_.Name] = Get-Sha256 $_.FullName }
    Write-JsonFile -Path (Join-Path $script:Root 'plugin-after.json') -Object $after | Out-Null

    if (@(Get-UnityProcesses -ProjectPath $project).Count -ne 0) {
        throw ('UNITY_BUSY: another Editor already owns ' + $project)
    }
    $playerDir = Join-Path $script:Root 'player'
    New-Item -ItemType Directory -Force -Path $playerDir | Out-Null
    $playerExe = Join-Path $playerDir 'Mahjong.exe'
    $unityLog = Join-Path $script:Root 'logs\unity-build.log'
    $buildMarker = Join-Path $script:Root 'build-marker.json'
    $arguments = @(
        '-batchmode', '-quit', '-nographics',
        '-projectPath', $project,
        '-executeMethod', 'C04RecoveryReconciliationBuild.Build',
        '-c04Output', $playerExe,
        '-c04Marker', $buildMarker,
        '-c04Log', (Join-Path $script:Root 'logs\editor-build.json'),
        '-c04SourceHead', $script:SourceHead,
        '-c04BuildCommit', $script:BuildCommit,
        '-logFile', $unityLog
    )
    Write-ToolLog ('COMMAND ' + $UnityPath + ' ' + ($arguments -join ' '))
    $unityProcess = Start-Process -FilePath $UnityPath -ArgumentList ($arguments | ForEach-Object {
        if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
    }) -PassThru -WindowStyle Hidden
    $finished = Wait-UnityExit -ProjectPath $project -Seconds 3600
    if (-not $finished) {
        try { if (-not $unityProcess.HasExited) { $unityProcess.Kill() } } catch { }
        throw 'UNITY_BUILD_TIMEOUT'
    }
    Start-Sleep -Seconds 2
    if (-not (Test-Path -LiteralPath $buildMarker)) {
        $tail = if (Test-Path -LiteralPath $unityLog) { (Get-Content -LiteralPath $unityLog -Tail 80) -join "`n" } else { '<no log>' }
        throw ('UNITY_BUILD_NO_MARKER: ' + $tail)
    }
    if (-not (Test-Path -LiteralPath $playerExe)) { throw 'PLAYER_EXE_MISSING' }
    $script:Player = [IO.Path]::GetFullPath($playerExe)
    $script:PlayerSha = Get-Sha256 $playerExe
    $files = @(Get-ChildItem -LiteralPath $playerDir -Recurse -File | Sort-Object FullName)
    $total = ($files | Measure-Object Length -Sum).Sum
    $manifest = [ordered]@{
        schemaVersion = 1
        kind = 'c04-recovery-player-files'
        root = $playerDir
        fileCount = $files.Count
        totalBytes = [long]$total
        files = @($files | ForEach-Object {
            [ordered]@{
                path = $_.FullName.Substring($playerDir.Length).TrimStart('\', '/').Replace('\', '/')
                bytes = [long]$_.Length
                sha256 = Get-Sha256 $_.FullName
            }
        })
    }
    $manifestPath = Join-Path $script:Root 'player-files.json'
    $script:PackageSha = Write-JsonFile -Path $manifestPath -Object $manifest
    $script:PackageCount = $files.Count
    Write-ToolLog ('PLAYER_BOUND sha=' + $script:PlayerSha + ' files=' + $script:PackageCount + ' manifest=' + $script:PackageSha)

    # Restore the tracked plugin bytes after the Player has copied them into its
    # package. The build evidence keeps both the pre/post hashes and the manifest.
    # Unity may rewrite tracked generated font/performance resources and the
    # solution while importing. They are build by-products, not deliverables, so
    # restore every tracked Unity asset and remove only untracked plugin copies.
    & git -C $script:RepoRoot restore --source=HEAD -- unity/Mahjong.Unity/Assets unity/Mahjong.Unity/ProjectSettings unity/Mahjong.Unity/Mahjong.Unity.slnx | Out-Null
    & git -C $script:RepoRoot clean -fd -- unity/Mahjong.Unity/Assets/Plugins/Mahjong | Out-Null
    $restored = (& git -C $script:RepoRoot status --porcelain=v1 --untracked-files=no) -join "`n"
    if (-not [string]::IsNullOrWhiteSpace($restored)) {
        throw ('GENERATED_ASSET_RESTORE_FAILED: ' + $restored)
    }
}

function Bind-ExistingPlayer {
    if (-not (Test-Path -LiteralPath $PlayerPath)) { throw ('PLAYER_NOT_FOUND ' + $PlayerPath) }
    $script:Player = [IO.Path]::GetFullPath($PlayerPath)
    $script:PlayerSha = Get-Sha256 $script:Player
    $playerDir = Split-Path -Parent $script:Player
    $files = @(Get-ChildItem -LiteralPath $playerDir -Recurse -File | Sort-Object FullName)
    $total = ($files | Measure-Object Length -Sum).Sum
    $manifest = [ordered]@{
        schemaVersion = 1
        kind = 'c04-recovery-player-files'
        root = $playerDir
        fileCount = $files.Count
        totalBytes = [long]$total
        files = @($files | ForEach-Object {
            [ordered]@{
                path = $_.FullName.Substring($playerDir.Length).TrimStart('\', '/').Replace('\', '/')
                bytes = [long]$_.Length
                sha256 = Get-Sha256 $_.FullName
            }
        })
    }
    $script:PackageSha = Write-JsonFile -Path (Join-Path $script:Root 'player-files.json') -Object $manifest
    $script:PackageCount = $files.Count
    Write-ToolLog ('PLAYER_BOUND_REUSE sha=' + $script:PlayerSha + ' files=' + $script:PackageCount)
}

function Run-Cases {
    $normalPrepare = Join-Path $script:Root 'prepare'
    $forcePrepare = Join-Path $script:Root 'prepare-force'
    New-Item -ItemType Directory -Force -Path $normalPrepare, $forcePrepare | Out-Null

    $normalLaunch = Invoke-Player -Directory $normalPrepare -ProbeMode 'Prepare' -CaseId 'prepare' -Fault 'None' -Outcome 'RECOVERED' -Force $false
    if ($normalLaunch.nativeExitCode -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $normalPrepare 'prepare.json'))) {
        throw ('PREPARE_FAILED native=' + $normalLaunch.nativeExitCode)
    }

    $forceLaunch = Invoke-Player -Directory $forcePrepare -ProbeMode 'Prepare' -CaseId 'prepare-force' -Fault 'None' -Outcome 'RECOVERED' -Force $true
    if ($forceLaunch.nativeExitCode -eq 0 -or -not (Test-Path -LiteralPath (Join-Path $forcePrepare 'prepare.json'))) {
        throw ('FORCE_PREPARE_FAILED native=' + $forceLaunch.nativeExitCode)
    }

    foreach ($caseId in $Cases) {
        $definition = Get-CaseDefinition -CaseId $caseId
        $caseDir = Join-Path $script:Root ('cases\' + $caseId)
        New-Item -ItemType Directory -Force -Path $caseDir | Out-Null
        $baseline = if ($definition.Force) { $forcePrepare } else { $normalPrepare }
        New-CaseInput -CaseDir $caseDir -CaseId $caseId -Fault $definition.Fault -Outcome $definition.Outcome -Prepare $baseline
        $launch = Invoke-Player -Directory $caseDir -ProbeMode 'Recover' -CaseId $caseId -Fault $definition.Fault -Outcome $definition.Outcome -Force $false
        $resultPath = Join-Path $caseDir 'result.json'
        $verificationPath = Join-Path $caseDir 'verification.json'
        if (-not (Test-Path -LiteralPath $resultPath)) {
            $caseResult = [ordered]@{
                caseId = $caseId
                fault = $definition.Fault
                expectedOutcome = $definition.Outcome
                status = 'FAIL'
                nativeExitCode = $launch.nativeExitCode
                reason = 'RESULT_MISSING'
            }
            Write-JsonFile -Path (Join-Path $caseDir 'case.json') -Object $caseResult | Out-Null
            $script:CaseResults.Add([pscustomobject]$caseResult)
            continue
        }
        $verifyExit = Invoke-Native -FilePath 'powershell.exe' -Arguments @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
            (Join-Path $script:RepoRoot 'tools\unity\verify-p45-c04-recovery-reconciliation.ps1'),
            '-ResultPath', $resultPath,
            '-ExpectedPath', (Join-Path $caseDir 'expected.txt'),
            '-InputPath', (Join-Path $caseDir 'input.json'),
            '-OutputPath', $verificationPath) -LogPath (Join-Path $caseDir 'verify.log')
        $caseResult = [ordered]@{
            caseId = $caseId
            fault = $definition.Fault
            expectedOutcome = $definition.Outcome
            status = if ($verifyExit -eq 0 -and $launch.nativeExitCode -eq 0) { 'PASS' } else { 'FAIL' }
            nativeExitCode = $launch.nativeExitCode
            verifyExitCode = $verifyExit
            resultSha256 = Get-Sha256 $resultPath
            verificationSha256 = if (Test-Path -LiteralPath $verificationPath) { Get-Sha256 $verificationPath } else { '' }
        }
        Write-JsonFile -Path (Join-Path $caseDir 'case.json') -Object $caseResult | Out-Null
        $script:CaseResults.Add([pscustomobject]$caseResult)
        Write-ToolLog ('CASE ' + $caseId + ' status=' + $caseResult.status + ' native=' + $launch.nativeExitCode + ' verify=' + $verifyExit)
    }
}

function Write-Summary {
    $status = if (@($script:CaseResults | Where-Object { $_.status -ne 'PASS' }).Count -eq 0) { 'PASS' } else { 'FAIL' }
    $summary = [ordered]@{
        schemaVersion = 1
        kind = 'c04-recovery-run'
        toolVersion = $script:ToolVersion
        status = $status
        createdAt = [DateTimeOffset]::UtcNow.ToString('o')
        sourceHead = $script:SourceHead
        buildCommit = $script:BuildCommit
        playerPath = $script:Player
        playerSha256 = $script:PlayerSha
        packageManifestSha256 = $script:PackageSha
        packageFileCount = $script:PackageCount
        mutex = $script:MutexName
        cases = [object[]]$script:CaseResults.ToArray()
    }
    $summaryPath = Join-Path $script:Root 'summary.json'
    $summarySha = Write-JsonFile -Path $summaryPath -Object $summary
    $entries = New-Object System.Collections.Generic.List[object]
    Get-ChildItem -LiteralPath $script:Root -Recurse -File | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($script:Root.Length).TrimStart('\', '/').Replace('\', '/')
        if ($relative -ne 'evidence-sha256.csv' -and
            -not $relative.StartsWith('temp/', [StringComparison]::OrdinalIgnoreCase)) {
            $entries.Add([pscustomobject]@{
                path = $relative
                bytes = [long]$_.Length
                sha256 = Get-Sha256 $_.FullName
            }) | Out-Null
        }
    }
    $csv = $entries | ForEach-Object { $_.sha256 + ',' + $_.bytes + ',' + $_.path }
    [IO.File]::WriteAllLines((Join-Path $script:Root 'evidence-sha256.csv'), $csv, (New-Object Text.UTF8Encoding($false)))
    Write-ToolLog ('SUMMARY ' + $status + ' sha=' + $summarySha + ' cases=' + $script:CaseResults.Count)
    return $status
}

try {
    $script:SourceHead = (& git -C $script:RepoRoot rev-parse HEAD).Trim()
    $script:BuildCommit = $script:SourceHead
    $dirty = (& git -C $script:RepoRoot status --porcelain=v1 --untracked-files=no) -join "`n"
    if (-not [string]::IsNullOrWhiteSpace($dirty)) {
        throw ('WORKTREE_DIRTY: refusing a build/run with uncommitted tracked changes: ' + $dirty)
    }

    if ($Mode -eq 'Verify') {
        Initialize-ExistingRoot
        $summaryPath = Join-Path $script:Root 'summary.json'
        if (-not (Test-Path -LiteralPath $summaryPath)) { throw 'SUMMARY_NOT_FOUND' }
        $summary = [IO.File]::ReadAllText($summaryPath) | ConvertFrom-Json
        $script:Player = [string]$summary.playerPath
        $script:PlayerSha = [string]$summary.playerSha256
        $script:PackageSha = [string]$summary.packageManifestSha256
        $script:PackageCount = [int]$summary.packageFileCount
        $script:SourceHead = [string]$summary.sourceHead
        $script:BuildCommit = [string]$summary.buildCommit
        foreach ($caseResult in @($summary.cases)) {
            $caseDir = Join-Path $script:Root ('cases\' + [string]$caseResult.caseId)
            $resultPath = Join-Path $caseDir 'result.json'
            if (-not (Test-Path -LiteralPath $resultPath)) { throw ('RESULT_MISSING ' + $resultPath) }
            $verifyExit = Invoke-Native -FilePath 'powershell.exe' -Arguments @(
                '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                (Join-Path $script:RepoRoot 'tools\unity\verify-p45-c04-recovery-reconciliation.ps1'),
                '-ResultPath', $resultPath,
                '-ExpectedPath', (Join-Path $caseDir 'expected.txt'),
                '-InputPath', (Join-Path $caseDir 'input.json'),
                '-OutputPath', (Join-Path $caseDir 'verification.json')) -LogPath (Join-Path $caseDir 'verify.log')
            if ($verifyExit -ne 0) { throw ('VERIFY_FAILED ' + $caseResult.caseId) }
        }
        Write-Output 'C04_VERIFY_PASS'
        exit 0
    }

    if ($Mode -eq 'Run') {
        Initialize-RunRoot
    }
    else {
        Initialize-Root
    }

    Acquire-Mutex

    if ($Mode -eq 'All' -or $Mode -eq 'Build') {
        if (-not [string]::IsNullOrWhiteSpace($PlayerPath)) {
            throw 'PLAYER_PATH_CONFLICTS_WITH_BUILD'
        }
        Build-Player
    }
    else {
        if ([string]::IsNullOrWhiteSpace($PlayerPath)) {
            $script:Player = Join-Path $script:Root 'player\Mahjong.exe'
            if (-not (Test-Path -LiteralPath $script:Player)) { throw 'PLAYER_NOT_FOUND_IN_RUN_ROOT' }
            $script:PlayerSha = Get-Sha256 $script:Player
            $manifestPath = Join-Path $script:Root 'player-files.json'
            if (-not (Test-Path -LiteralPath $manifestPath)) { throw 'PLAYER_MANIFEST_MISSING' }
            $script:PackageSha = Get-Sha256 $manifestPath
            $script:PackageCount = @((Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json).files).Count
        }
        else {
            Bind-ExistingPlayer
        }
    }

    if ($Mode -eq 'All' -or $Mode -eq 'Run') {
        Run-Cases
        $status = Write-Summary
        if ($status -ne 'PASS') { exit 1 }
    }
    exit 0
}
catch {
    $message = $_.Exception.GetType().Name + ': ' + $_.Exception.Message
    Write-ToolLog ('TOOL_FAIL ' + $message + ' stack=' + $_.ScriptStackTrace)
    if ($script:Root) {
        try {
            $failure = [ordered]@{
                schemaVersion = 1
                kind = 'c04-recovery-run-failure'
                status = 'BLOCKED'
                detail = $message
                atUtc = [DateTimeOffset]::UtcNow.ToString('o')
                cases = [object[]]$script:CaseResults.ToArray()
            }
            Write-JsonFile -Path (Join-Path $script:Root 'failure.json') -Object $failure | Out-Null
        }
        catch { }
    }
    Write-Error $message
    exit 1
}
finally {
    Restore-Environment
    Release-Mutex
}
