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

    [string]$BuildRecordPath,

    [string]$PackageManifestPath,

    [string]$OutputDir,

    [string]$ExpectedSummarySha256,

    [string]$HelperPath,

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
    param([string]$CaseDir, [string]$CaseId, [string]$Fault, [string]$Outcome, $Prepare, [bool]$Force)
    $copy = @('local', 'outbox', 'authority', 'prepare.json', 'expected.txt')
    foreach ($name in $copy) {
        $source = Join-Path $Prepare $name
        if (Test-Path -LiteralPath $source -PathType Container) {
            Copy-Item -LiteralPath $source -Destination (Join-Path $CaseDir $name) -Recurse -Force
        }
        else {
            Copy-Item -LiteralPath $source -Destination (Join-Path $CaseDir $name) -Force
        }
    }
    if (Test-Path -LiteralPath (Join-Path $Prepare 'frames') -PathType Container) {
        Copy-Item -LiteralPath (Join-Path $Prepare 'frames') -Destination (Join-Path $CaseDir 'prepare-frames') -Recurse -Force
    }
    foreach ($pair in @(@('launch.json', 'prepare-launch.json'), @('player.log', 'prepare-player.log'))) {
        $source = Join-Path $Prepare $pair[0]
        if (Test-Path -LiteralPath $source) {
            Copy-Item -LiteralPath $source -Destination (Join-Path $CaseDir $pair[1]) -Force
        }
    }
    $primary = Join-Path $CaseDir 'local\local-main.save'
    $backup = Join-Path $CaseDir 'local\local-main.save.prev'
    $boundary = Join-Path $CaseDir 'boundary'
    New-Item -ItemType Directory -Force -Path $boundary | Out-Null
    if (Test-Path -LiteralPath $primary) { Copy-Item -LiteralPath $primary -Destination (Join-Path $boundary 'primary-before.save') -Force }
    if (Test-Path -LiteralPath $backup) { Copy-Item -LiteralPath $backup -Destination (Join-Path $boundary 'backup-before.save') -Force }
    Apply-Fault -Directory $CaseDir -Fault $Fault
    if (Test-Path -LiteralPath $primary) { Copy-Item -LiteralPath $primary -Destination (Join-Path $boundary 'primary-after.save') -Force }
    if (Test-Path -LiteralPath $backup) { Copy-Item -LiteralPath $backup -Destination (Join-Path $boundary 'backup-after.save') -Force }
    $input = [ordered]@{
        schemaVersion = 1
        kind = 'c04-recovery-case-input'
        caseId = $CaseId
        fault = $Fault
        expectedOutcome = $Outcome
        force = $Force
        playerSha256 = $script:PlayerSha
        packageSha256 = $script:PackageSha
        packageFileCount = $script:PackageCount
        sourceHead = $script:SourceHead
        buildCommit = $script:BuildCommit
        primaryBeforeFaultSha256 = if ([IO.File]::Exists((Join-Path $boundary 'primary-before.save'))) { Get-Sha256 (Join-Path $boundary 'primary-before.save') } else { '' }
        backupBeforeFaultSha256 = if ([IO.File]::Exists((Join-Path $boundary 'backup-before.save'))) { Get-Sha256 (Join-Path $boundary 'backup-before.save') } else { '' }
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

function Invoke-UnityProcess {
    param([string]$UnityExe, [string[]]$Arguments, [int]$TimeoutSeconds)
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $UnityExe
    $psi.Arguments = ($Arguments | ForEach-Object {
        if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
    }) -join ' '
    $psi.WorkingDirectory = Split-Path -Parent $UnityExe
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.EnvironmentVariables['TEMP'] = $script:Temp
    $psi.EnvironmentVariables['TMP'] = $script:Temp
    $process = [System.Diagnostics.Process]::Start($psi)
    $started = $process.StartTime.ToUniversalTime().ToString('o')
    $finished = $process.WaitForExit($TimeoutSeconds * 1000)
    if (-not $finished) {
        try { $process.Kill() } catch { }
        try { [void]$process.WaitForExit(5000) } catch { }
        throw ('UNITY_TIMEOUT pid=' + $process.Id)
    }
    $process.Refresh()
    $exit = [int]$process.ExitCode
    return [pscustomobject]@{
        pid = $process.Id
        startUtc = $started
        path = $UnityExe
        nativeExitCode = $exit
    }
}

function Build-Player {
    $project = Join-Path $script:RepoRoot 'unity\Mahjong.Unity'
    $clean = (& git -C $script:RepoRoot status --porcelain=v1 --untracked-files=no) -join "`n"
    if (-not [string]::IsNullOrWhiteSpace($clean)) {
        throw ('SOURCE_DIRTY: freeze the code commit before building: ' + $clean)
    }
    $head = (& git -C $script:RepoRoot rev-parse HEAD).Trim()
    $script:SourceHead = $head
    $script:BuildCommit = $head
    $tree = (& git -C $script:RepoRoot ls-tree -r --full-tree HEAD) -join "`n"
    $sourceInputs = @(
        'dotnet/tests/Mahjong.LocalHost.Tests/C04RecoveryRequestReconciliationTests.cs',
        'dotnet/tools/C04RecoveryReconciliationVerifier/C04RecoveryReconciliationVerifier.csproj',
        'dotnet/tools/C04RecoveryReconciliationVerifier/Program.cs',
        'tools/unity/run-p45-c04-recovery-reconciliation.ps1',
        'tools/unity/verify-p45-c04-recovery-reconciliation.ps1',
        'tools/tests/VerifyP45C04RecoveryReconciliation.Tests.ps1',
        'unity/Mahjong.Unity/Assets/Scripts/App/C04RecoveryReconciliationProbe.cs',
        'unity/Mahjong.Unity/Assets/Editor/C04RecoveryReconciliationBuild.cs'
    )
    $inputHashes = [ordered]@{}
    foreach ($relative in $sourceInputs) {
        $path = Join-Path $script:RepoRoot $relative
        if (-not (Test-Path -LiteralPath $path)) { throw ('SOURCE_INPUT_MISSING ' + $relative) }
        $inputHashes[$relative] = Get-Sha256 $path
    }
    $snapshot = [ordered]@{
        schemaVersion = 1
        kind = 'c04-recovery-source-snapshot'
        sourceHead = $head
        treeSha256 = Get-StringSha256 $tree
        inputs = $inputHashes
        generatedAt = [DateTimeOffset]::UtcNow.ToString('o')
    }
    Write-JsonFile -Path (Join-Path $script:Root 'source-snapshot.json') -Object $snapshot | Out-Null

    # Import/build in a private staging copy. The source tree is never restored,
    # cleaned or enumerated for deletion; no other user/process bytes are touched.
    $staging = Join-Path $script:Root 'staging-project'
    New-Item -ItemType Directory -Force -Path $staging | Out-Null
    foreach ($name in @('Assets', 'Packages', 'ProjectSettings', 'Mahjong.Unity.slnx')) {
        $source = Join-Path $project $name
        if (Test-Path -LiteralPath $source) {
            Copy-Item -LiteralPath $source -Destination (Join-Path $staging $name) -Recurse -Force
        }
    }
    # Reuse Unity's local Library only inside the isolated staging copy. This keeps
    # package resolution and asset import deterministic without ever touching the
    # source project; plugin/script changes still force the staging recompile.
    $sourceLibrary = Join-Path $project 'Library'
    if (Test-Path -LiteralPath $sourceLibrary) {
        & robocopy $sourceLibrary (Join-Path $staging 'Library') /E /XJ /R:1 /W:1 /NFL /NDL /NJH /NJS | Out-Null
        if ($LASTEXITCODE -ge 8) { throw ('STAGING_LIBRARY_COPY_FAILED ' + $LASTEXITCODE) }
    }
    # The source-tree guard validates the repository's default plugin directory.
    # In an isolated staging project that would validate the wrong directory, so
    # the runner performs the same check-shared-plugins gate explicitly against
    # the staging plugins below, then removes this Editor-only guard from the
    # disposable staging copy (it is not part of a Player build).
    $stagingGuard = Join-Path $staging 'Assets\Editor\SharedPluginProvenanceGuard.cs'
    $stagingPlayProbe = Join-Path $staging 'Assets\Editor\SharedPluginProvenancePlayProbe.cs'
    $guardAdaptation = [ordered]@{
        schemaVersion = 1
        kind = 'c04-recovery-staging-adaptation'
        reason = 'source-tree guard resolves the repository default plugin dir, not the isolated staging copy; the runner runs the same checker against staging first'
        guardRemoved = $false
        playProbeRemoved = $false
        sourceGuardSha256 = if (Test-Path -LiteralPath $stagingGuard) { Get-Sha256 $stagingGuard } else { '' }
        sourcePlayProbeSha256 = if (Test-Path -LiteralPath $stagingPlayProbe) { Get-Sha256 $stagingPlayProbe } else { '' }
    }
    foreach ($stagingEditorFile in @($stagingGuard, $stagingPlayProbe)) {
        if (Test-Path -LiteralPath $stagingEditorFile) {
            Remove-Item -LiteralPath $stagingEditorFile -Force
            $stagingMeta = $stagingEditorFile + '.meta'
            if (Test-Path -LiteralPath $stagingMeta) { Remove-Item -LiteralPath $stagingMeta -Force }
            if ($stagingEditorFile -eq $stagingGuard) { $guardAdaptation.guardRemoved = $true }
            else { $guardAdaptation.playProbeRemoved = $true }
        }
    }
    $adaptationPath = Join-Path $script:Root 'staging-adaptation.json'
    Write-JsonFile -Path $adaptationPath -Object $guardAdaptation | Out-Null
    $stagingEntries = New-Object System.Collections.Generic.List[object]
    foreach ($name in @('Assets', 'Packages', 'ProjectSettings')) {
        $dir = Join-Path $staging $name
        if (Test-Path -LiteralPath $dir -PathType Container) {
            foreach ($file in @(Get-ChildItem -LiteralPath $dir -Recurse -File)) {
                $stagingEntries.Add([ordered]@{
                    path = $file.FullName.Substring($staging.Length).TrimStart('\', '/').Replace('\', '/')
                    bytes = [long]$file.Length
                    sha256 = Get-Sha256 $file.FullName
                }) | Out-Null
            }
        }
    }
    $stagingSlnx = Join-Path $staging 'Mahjong.Unity.slnx'
    if (Test-Path -LiteralPath $stagingSlnx) {
        $stagingEntries.Add([ordered]@{
            path = 'Mahjong.Unity.slnx'
            bytes = [long](Get-Item -LiteralPath $stagingSlnx).Length
            sha256 = Get-Sha256 $stagingSlnx
        }) | Out-Null
    }
    Write-JsonFile -Path (Join-Path $script:Root 'staging-source.json') -Object ([ordered]@{
        schemaVersion = 1
        kind = 'c04-recovery-staging-source'
        sourceHead = $script:SourceHead
        adaptationSha256 = Get-Sha256 $adaptationPath
        files = [object[]]$stagingEntries.ToArray()
    }) | Out-Null

    $stagingProbe = Join-Path $staging 'Assets\Scripts\App\C04RecoveryReconciliationProbe.cs'
    $stagingEditor = Join-Path $staging 'Assets\Editor\C04RecoveryReconciliationBuild.cs'
    if ((Get-Sha256 $stagingProbe) -ne $inputHashes['unity/Mahjong.Unity/Assets/Scripts/App/C04RecoveryReconciliationProbe.cs'] -or
        (Get-Sha256 $stagingEditor) -ne $inputHashes['unity/Mahjong.Unity/Assets/Editor/C04RecoveryReconciliationBuild.cs']) {
        throw 'STAGING_SOURCE_MISMATCH'
    }

    $pluginDir = Join-Path $staging 'Assets\Plugins\Mahjong'
    $sharedLog = Join-Path $script:Root 'logs\build-shared.log'
    $sharedExit = Invoke-Native -FilePath 'powershell.exe' -Arguments @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
        (Join-Path $script:RepoRoot 'tools\unity\build-shared.ps1'), '-Configuration', 'Release',
        '-PluginOutputDir', $pluginDir, '-DotnetPath', $DotnetPath) -LogPath $sharedLog
    if ($sharedExit -ne 0) { throw ('BUILD_SHARED_FAILED exit=' + $sharedExit) }

    $checkLog = Join-Path $script:Root 'logs\check-shared-plugins.log'
    $checkExit = Invoke-Native -FilePath 'powershell.exe' -Arguments @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
        (Join-Path $script:RepoRoot 'tools\unity\check-shared-plugins.ps1'),
        '-ProjectRoot', $script:RepoRoot, '-PluginDir', $pluginDir,
        '-EvidencePath', (Join-Path $pluginDir 'shared-plugin-provenance.json'),
        '-Configuration', 'Release') -LogPath $checkLog
    if ($checkExit -ne 0) { throw ('CHECK_SHARED_PLUGINS_FAILED exit=' + $checkExit) }

    $helperProject = Join-Path $script:RepoRoot 'dotnet\tools\C04RecoveryReconciliationVerifier\C04RecoveryReconciliationVerifier.csproj'
    $helperOut = Join-Path $script:Root 'helper'
    $helperLog = Join-Path $script:Root 'logs\build-helper.log'
    $helperExit = Invoke-Native -FilePath $DotnetPath -Arguments @(
        'build', $helperProject, '-c', 'Release', '-o', $helperOut, '--nologo') -LogPath $helperLog
    if ($helperExit -ne 0) { throw ('HELPER_BUILD_FAILED exit=' + $helperExit) }
    $script:HelperPath = Join-Path $helperOut 'C04RecoveryReconciliationVerifier.dll'
    if (-not (Test-Path -LiteralPath $script:HelperPath)) { throw 'HELPER_MISSING' }

    if (@(Get-UnityProcesses -ProjectPath $staging).Count -ne 0) {
        throw ('UNITY_BUSY: another Editor already owns ' + $staging)
    }
    $playerDir = Join-Path $script:Root 'player'
    New-Item -ItemType Directory -Force -Path $playerDir | Out-Null
    $playerExe = Join-Path $playerDir 'Mahjong.exe'
    $unityLog = Join-Path $script:Root 'logs\unity-build.log'
    $buildMarker = Join-Path $script:Root 'build-marker.json'
    $arguments = @(
        '-batchmode', '-quit', '-nographics',
        '-projectPath', $staging,
        '-executeMethod', 'C04RecoveryReconciliationBuild.Build',
        '-c04Output', $playerExe,
        '-c04Marker', $buildMarker,
        '-c04Log', (Join-Path $script:Root 'logs\editor-build.json'),
        '-c04SourceHead', $script:SourceHead,
        '-c04BuildCommit', $script:BuildCommit,
        '-logFile', $unityLog
    )
    Write-ToolLog ('COMMAND ' + $UnityPath + ' ' + ($arguments -join ' '))
    $unity = Invoke-UnityProcess -UnityExe $UnityPath -Arguments $arguments -TimeoutSeconds 3600
    Write-JsonFile -Path (Join-Path $script:Root 'logs\unity-exit.json') -Object $unity | Out-Null
    if ($unity.nativeExitCode -ne 0) { throw ('UNITY_NATIVE_NONZERO ' + $unity.nativeExitCode) }
    if (-not (Test-Path -LiteralPath $buildMarker)) {
        $tail = if (Test-Path -LiteralPath $unityLog) { (Get-Content -LiteralPath $unityLog -Tail 80) -join "`n" } else { '<no log>' }
        throw ('UNITY_BUILD_NO_MARKER: ' + $tail)
    }
    $marker = [IO.File]::ReadAllText($buildMarker) | ConvertFrom-Json
    if ([string]$marker.result -ne 'Succeeded' -or [int]$marker.errors -ne 0) {
        throw ('BUILD_MARKER_REJECTED result=' + $marker.result + ' errors=' + $marker.errors)
    }
    if (-not (Test-Path -LiteralPath $playerExe)) { throw 'PLAYER_EXE_MISSING' }
    if ([string]$marker.sourceHead -ne $script:SourceHead -or [string]$marker.buildCommit -ne $script:BuildCommit) {
        throw 'BUILD_MARKER_SOURCE_MISMATCH'
    }
    if ([string]$marker.output -ne [IO.Path]::GetFullPath($playerExe)) { throw 'BUILD_MARKER_OUTPUT_MISMATCH' }
    $script:Player = [IO.Path]::GetFullPath($playerExe)
    $script:PlayerSha = Get-Sha256 $playerExe
    if (-not [string]::Equals([string]$marker.playerSha256, $script:PlayerSha, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'BUILD_MARKER_PLAYER_SHA_MISMATCH'
    }
    foreach ($property in $marker.plugins.PSObject.Properties) {
        $pluginPath = Join-Path $pluginDir $property.Name
        if (-not (Test-Path -LiteralPath $pluginPath) -or
            (Get-Sha256 $pluginPath) -ne [string]$property.Value) {
            throw ('BUILD_MARKER_PLUGIN_MISMATCH ' + $property.Name)
        }
    }

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
    $script:BuildRecordPath = $buildMarker
    $script:PackageManifestPath = $manifestPath
    Write-ToolLog ('PLAYER_BOUND sha=' + $script:PlayerSha + ' files=' + $script:PackageCount +
        ' manifest=' + $script:PackageSha + ' unityNative=' + $unity.nativeExitCode)
}

function Bind-ExistingPlayer {
    if (-not (Test-Path -LiteralPath $PlayerPath)) { throw ('PLAYER_NOT_FOUND ' + $PlayerPath) }
    if ([string]::IsNullOrWhiteSpace($BuildRecordPath) -or -not (Test-Path -LiteralPath $BuildRecordPath)) {
        throw 'EXISTING_PLAYER_BUILD_RECORD_REQUIRED'
    }
    if ([string]::IsNullOrWhiteSpace($PackageManifestPath) -or -not (Test-Path -LiteralPath $PackageManifestPath)) {
        throw 'EXISTING_PLAYER_PACKAGE_MANIFEST_REQUIRED'
    }
    $script:Player = [IO.Path]::GetFullPath($PlayerPath)
    $script:PlayerSha = Get-Sha256 $script:Player
    $playerDir = Split-Path -Parent $script:Player
    $marker = [IO.File]::ReadAllText($BuildRecordPath) | ConvertFrom-Json
    if ([string]$marker.result -ne 'Succeeded' -or [int]$marker.errors -ne 0) {
        throw ('EXISTING_PLAYER_BUILD_RECORD_REJECTED result=' + $marker.result + ' errors=' + $marker.errors)
    }
    if ([string]$marker.output -ne $script:Player) { throw 'EXISTING_PLAYER_BUILD_RECORD_OUTPUT_MISMATCH' }
    if (-not [string]::Equals([string]$marker.playerSha256, $script:PlayerSha, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'EXISTING_PLAYER_BUILD_RECORD_SHA_MISMATCH'
    }
    if ([string]::IsNullOrWhiteSpace([string]$marker.sourceHead) -or
        [string]::IsNullOrWhiteSpace([string]$marker.buildCommit)) {
        throw 'EXISTING_PLAYER_BUILD_RECORD_SOURCE_MISSING'
    }
    $snapshotPath = Join-Path (Split-Path -Parent ([IO.Path]::GetFullPath($BuildRecordPath))) 'source-snapshot.json'
    if (-not (Test-Path -LiteralPath $snapshotPath)) {
        throw 'EXISTING_PLAYER_SOURCE_SNAPSHOT_REQUIRED'
    }
    $snapshot = [IO.File]::ReadAllText($snapshotPath) | ConvertFrom-Json
    if ([string]$snapshot.sourceHead -ne [string]$marker.sourceHead) {
        throw 'EXISTING_PLAYER_SOURCE_SNAPSHOT_MISMATCH'
    }
    $probeInSource = & git -C $script:RepoRoot cat-file -e ($marker.sourceHead + ':unity/Mahjong.Unity/Assets/Scripts/App/C04RecoveryReconciliationProbe.cs') 2>$null
    $probeExit = $LASTEXITCODE
    $buildInSource = & git -C $script:RepoRoot cat-file -e ($marker.sourceHead + ':unity/Mahjong.Unity/Assets/Editor/C04RecoveryReconciliationBuild.cs') 2>$null
    $buildExit = $LASTEXITCODE
    if ($probeExit -ne 0 -or $buildExit -ne 0) {
        throw ('EXISTING_PLAYER_SOURCE_LACKS_C04_INPUTS probe=' + $probeExit + ' build=' + $buildExit)
    }

    $manifest = [IO.File]::ReadAllText($PackageManifestPath) | ConvertFrom-Json
    if ([string]$manifest.kind -ne 'c04-recovery-player-files' -or
        [string]$manifest.root -ne $playerDir -or
        [int]$manifest.fileCount -lt 1) {
        throw 'EXISTING_PLAYER_PACKAGE_MANIFEST_INVALID'
    }
    $entries = @($manifest.files)
    if ($entries.Count -ne [int]$manifest.fileCount) {
        throw ('PACKAGE_MANIFEST_COUNT_MISMATCH declared=' + $manifest.fileCount + ' entries=' + $entries.Count)
    }
    $declared = @{}
    $sumBytes = [long]0
    foreach ($entry in $entries) {
        $relative = [string]$entry.path
        if ([string]::IsNullOrWhiteSpace($relative) -or $declared.ContainsKey($relative)) {
            throw ('PACKAGE_MANIFEST_DUPLICATE_OR_EMPTY ' + $relative)
        }
        $declared[$relative] = $true
        $path = Join-Path $playerDir $relative
        if (-not (Test-Path -LiteralPath $path) -or
            (Get-Sha256 $path) -ne [string]$entry.sha256 -or
            (Get-Item -LiteralPath $path).Length -ne [long]$entry.bytes) {
            throw ('PACKAGE_MANIFEST_MISMATCH ' + $relative)
        }
        $sumBytes += [long]$entry.bytes
    }
    if ($sumBytes -ne [long]$manifest.totalBytes) {
        throw ('PACKAGE_MANIFEST_BYTES_MISMATCH declared=' + $manifest.totalBytes + ' actual=' + $sumBytes)
    }
    $actual = @(Get-ChildItem -LiteralPath $playerDir -Recurse -File | ForEach-Object {
        $_.FullName.Substring($playerDir.Length).TrimStart('\', '/').Replace('\', '/')
    })
    if ($actual.Count -ne $entries.Count) {
        throw ('PACKAGE_MANIFEST_FILESET_COUNT_MISMATCH actual=' + $actual.Count + ' declared=' + $entries.Count)
    }
    foreach ($relative in $actual) {
        if (-not $declared.ContainsKey($relative)) {
            throw ('PACKAGE_MANIFEST_UNDECLARED_FILE ' + $relative)
        }
    }
    $script:SourceHead = [string]$marker.sourceHead
    $script:BuildCommit = [string]$marker.buildCommit
    $script:PackageSha = Get-Sha256 $PackageManifestPath
    $script:PackageCount = [int]$manifest.fileCount
    $script:BuildRecordPath = [IO.Path]::GetFullPath($BuildRecordPath)
    $script:PackageManifestPath = [IO.Path]::GetFullPath($PackageManifestPath)
    $helperCandidate = Join-Path (Split-Path -Parent $script:BuildRecordPath) 'helper\C04RecoveryReconciliationVerifier.dll'
    if (-not [string]::IsNullOrWhiteSpace($HelperPath) -and (Test-Path -LiteralPath $HelperPath)) {
        $script:HelperPath = [IO.Path]::GetFullPath($HelperPath)
    }
    elseif (Test-Path -LiteralPath $helperCandidate) {
        $script:HelperPath = $helperCandidate
    }
    if ([string]::IsNullOrWhiteSpace($script:HelperPath) -or
        -not (Test-Path -LiteralPath $script:HelperPath)) {
        throw 'EXISTING_PLAYER_HELPER_REQUIRED'
    }
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
        New-CaseInput -CaseDir $caseDir -CaseId $caseId -Fault $definition.Fault -Outcome $definition.Outcome -Prepare $baseline -Force ([bool]$definition.Force)
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
            '-OutputPath', $verificationPath,
            '-HelperPath', $script:HelperPath,
            '-DotnetPath', $DotnetPath) -LogPath (Join-Path $caseDir 'verify.log')
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
    $fixedCases = @('C04-G6-01', 'C04-G6-02', 'C04-G6-03', 'C04-G6-04',
        'C04-G6-05', 'C04-G6-06', 'C04-G6-07', 'C04-G6-08')
    $actualCases = @($script:CaseResults | ForEach-Object { [string]$_.caseId } | Sort-Object -Unique)
    $complete = ($actualCases.Count -eq 8 -and
        (@($fixedCases | Where-Object { $actualCases -notcontains $_ }).Count -eq 0))
    $allPass = (@($script:CaseResults | Where-Object { $_.status -ne 'PASS' }).Count -eq 0)
    $status = if ($complete -and $allPass) { 'PASS' } elseif (-not $complete) { 'PARTIAL' } else { 'FAIL' }
    $summary = [ordered]@{
        schemaVersion = 1
        kind = 'c04-recovery-run'
        caseSetComplete = $complete
        expectedCaseCount = 8
        uniqueCaseCount = $actualCases.Count
        partial = (-not $complete)
        toolVersion = $script:ToolVersion
        status = $status
        createdAt = [DateTimeOffset]::UtcNow.ToString('o')
        sourceHead = $script:SourceHead
        buildCommit = $script:BuildCommit
        playerPath = $script:Player
        playerSha256 = $script:PlayerSha
        packageManifestSha256 = $script:PackageSha
        packageFileCount = $script:PackageCount
        helperPath = $script:HelperPath
        helperSha256 = if ($script:HelperPath -and (Test-Path -LiteralPath $script:HelperPath)) { Get-Sha256 $script:HelperPath } else { '' }
        buildRecordPath = $script:BuildRecordPath
        packageManifestPath = $script:PackageManifestPath
        mutex = $script:MutexName
        cases = [object[]]$script:CaseResults.ToArray()
    }
    $summaryPath = Join-Path $script:Root 'summary.json'
    $summarySha = Write-JsonFile -Path $summaryPath -Object $summary
    Write-ToolLog ('SUMMARY ' + $status + ' sha=' + $summarySha + ' cases=' + $script:CaseResults.Count)
    $entries = New-Object System.Collections.Generic.List[object]
    $scanFiles = New-Object System.Collections.Generic.List[object]
    foreach ($top in @(Get-ChildItem -LiteralPath $script:Root -File -ErrorAction SilentlyContinue)) {
        $scanFiles.Add($top)
    }
    foreach ($name in @('player', 'helper', 'logs', 'prepare', 'prepare-force', 'cases')) {
        $dir = Join-Path $script:Root $name
        if (Test-Path -LiteralPath $dir -PathType Container) {
            foreach ($file in @(Get-ChildItem -LiteralPath $dir -Recurse -File -ErrorAction SilentlyContinue)) {
                $scanFiles.Add($file)
            }
        }
    }
    foreach ($file in @($scanFiles | Sort-Object FullName)) {
        $relative = $file.FullName.Substring($script:Root.Length).TrimStart('\', '/').Replace('\', '/')
        if ($relative -ne 'evidence-sha256.csv') {
            $entries.Add([pscustomobject]@{
                path = $relative
                bytes = [long]$file.Length
                sha256 = Get-Sha256 $file.FullName
            }) | Out-Null
        }
    }
    $csv = $entries | ForEach-Object { $_.sha256 + ',' + $_.bytes + ',' + $_.path }
    [IO.File]::WriteAllLines((Join-Path $script:Root 'evidence-sha256.csv'), $csv, (New-Object Text.UTF8Encoding($false)))
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
        if ([string]::IsNullOrWhiteSpace($EvidenceRoot) -or
            -not (Test-Path -LiteralPath $EvidenceRoot -PathType Container)) {
            throw 'VERIFY_SOURCE_ROOT_REQUIRED'
        }
        if ([string]::IsNullOrWhiteSpace($OutputDir)) {
            throw 'VERIFY_OUTPUT_DIR_REQUIRED'
        }
        if ([string]::IsNullOrWhiteSpace($HelperPath) -or
            -not (Test-Path -LiteralPath $HelperPath -PathType Leaf)) {
            throw 'VERIFY_HELPER_REQUIRED'
        }
        $sourceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
        $script:Root = [IO.Path]::GetFullPath($OutputDir)
        if (Test-Path -LiteralPath $script:Root) {
            throw ('VERIFY_OUTPUT_REUSE ' + $script:Root)
        }
        New-Item -ItemType Directory -Force -Path $script:Root, (Join-Path $script:Root 'logs') | Out-Null
        $summaryPath = Join-Path $sourceRoot 'summary.json'
        if (-not (Test-Path -LiteralPath $summaryPath)) { throw 'SUMMARY_NOT_FOUND' }
        $summarySha = Get-Sha256 $summaryPath
        if (-not [string]::IsNullOrWhiteSpace($ExpectedSummarySha256) -and
            -not [string]::Equals($summarySha, $ExpectedSummarySha256, [StringComparison]::OrdinalIgnoreCase)) {
            throw ('VERIFY_SUMMARY_PIN_MISMATCH expected=' + $ExpectedSummarySha256 + ' actual=' + $summarySha)
        }
        Copy-Item -LiteralPath $summaryPath -Destination (Join-Path $script:Root 'source-summary.json') -Force
        $summary = [IO.File]::ReadAllText($summaryPath) | ConvertFrom-Json
        $summaryCases = @($summary.cases)
        $summaryIds = @($summaryCases | ForEach-Object { [string]$_.caseId } | Sort-Object -Unique)
        if ([string]$summary.status -ne 'PASS' -or -not [bool]$summary.caseSetComplete -or
            [int]$summary.expectedCaseCount -ne 8 -or $summaryCases.Count -ne 8 -or $summaryIds.Count -ne 8) {
            throw ('VERIFY_INCOMPLETE_CASE_SET status=' + $summary.status +
                ' complete=' + $summary.caseSetComplete + ' cases=' + $summaryCases.Count + ' unique=' + $summaryIds.Count)
        }
        $script:Player = [string]$summary.playerPath
        $script:PlayerSha = [string]$summary.playerSha256
        $script:PackageSha = [string]$summary.packageManifestSha256
        $script:PackageCount = [int]$summary.packageFileCount
        $script:SourceHead = [string]$summary.sourceHead
        $script:BuildCommit = [string]$summary.buildCommit
        $script:HelperPath = [IO.Path]::GetFullPath($HelperPath)
        if (-not (Test-Path -LiteralPath $script:HelperPath)) {
            throw 'VERIFY_HELPER_MISSING'
        }
        $verifyRows = New-Object System.Collections.Generic.List[object]
        foreach ($caseResult in @($summary.cases)) {
            $caseId = [string]$caseResult.caseId
            $caseDir = Join-Path $sourceRoot ('cases\' + $caseId)
            $resultPath = Join-Path $caseDir 'result.json'
            if (-not (Test-Path -LiteralPath $resultPath)) { throw ('RESULT_MISSING ' + $resultPath) }
            $caseOut = Join-Path $script:Root ('cases\' + $caseId)
            New-Item -ItemType Directory -Force -Path $caseOut | Out-Null
            $verifyExit = Invoke-Native -FilePath 'powershell.exe' -Arguments @(
                '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                (Join-Path $script:RepoRoot 'tools\unity\verify-p45-c04-recovery-reconciliation.ps1'),
                '-ResultPath', $resultPath,
                '-ExpectedPath', (Join-Path $caseDir 'expected.txt'),
                '-InputPath', (Join-Path $caseDir 'input.json'),
                '-OutputPath', (Join-Path $caseOut 'verification.json'),
                '-HelperPath', $script:HelperPath,
                '-DotnetPath', $DotnetPath) -LogPath (Join-Path $caseOut 'verify.log')
            $verifyRows.Add([pscustomobject]@{ caseId = $caseId; status = if ($verifyExit -eq 0) { 'PASS' } else { 'FAIL' }; verifyExitCode = $verifyExit }) | Out-Null
            if ($verifyExit -ne 0) { throw ('VERIFY_FAILED ' + $caseId) }
        }
        Write-JsonFile -Path (Join-Path $script:Root 'verification-summary.json') -Object ([ordered]@{
            schemaVersion = 1
            kind = 'c04-recovery-offline-verification'
            sourceRoot = $sourceRoot
            outputDir = $script:Root
            status = 'PASS'
            cases = [object[]]$verifyRows.ToArray()
            generatedAt = [DateTimeOffset]::UtcNow.ToString('o')
        }) | Out-Null
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
            $PlayerPath = Join-Path $script:Root 'player\Mahjong.exe'
            $BuildRecordPath = Join-Path $script:Root 'build-marker.json'
            $PackageManifestPath = Join-Path $script:Root 'player-files.json'
        }
        Bind-ExistingPlayer
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
