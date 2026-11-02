<#
.SYNOPSIS
    C04-G3 fixed Windows Player cheat probe runner (D04-C / R1-06).

.DESCRIPTION
    Windows PowerShell 5.1 compatible bounded build/package/process runner.
    It stages a private Unity project copy, builds the shared assembly closure
    into an exclusive staging directory, attributes BuildReport step failures,
    seals a complete Player package manifest, and only then runs the Player.
    Final case semantics belong to the separate D04-A C04PlayerCheatVerifier2
    CLI; this runner never substitutes the old local Strict verifier for it.

.PARAMETER Case
    authorized, negative, all, or build (build performs the build/package
    mechanism only and records GAME semantics as NOT_RUN).
#>
[CmdletBinding()]
param(
    [ValidateSet('authorized', 'negative', 'all', 'build')]
    [string]$Case = 'authorized',
    [string]$EvidenceRoot,
    [string]$PlayerPath,
    [string]$SourceHead,
    [string]$BuildCommit = 'unknown',
    [string]$UnityExe = 'D:\Unity\Hub\Editor\6000.3.27f1\Editor\Unity.exe',
    [string]$ProjectPath,
    [string]$BuildReport,
    [switch]$SkipBuild,
    [switch]$SelfTest,
    [string]$VerifierPath,
    [string]$DotnetPath,
    [string]$CancelFile,
    [int]$MutexWaitSeconds = 600,
    [int]$SharedBuildTimeoutSeconds = 600,
    [int]$UnityImportTimeoutSeconds = 900,
    [int]$UnityBuildTimeoutSeconds = 1500,
    [int]$PlayerTimeoutSeconds = 300,
    [int]$VerifierTimeoutSeconds = 180,
    [int]$DrainGraceSeconds = 10,
    [int]$HeartbeatSeconds = 30
)

$ErrorActionPreference = 'Stop'
$script:C04RunnerVersion = 'c04-d04-c-runner-v2'
$script:C04KnownNonFatalBuildErrors = @(
    'Host type is not matching any asset type at Path Packages/com.unity.render-pipelines.universal/Shaders/AutodeskInteractive/AutodeskInteractive.shadergraph.',
    'Host type is not matching any asset type at Path Packages/com.unity.render-pipelines.core/Editor/Lighting/ProbeVolume/VirtualOffset/TraceVirtualOffset.urtshader.'
)
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $ProjectPath) { $ProjectPath = Join-Path $root 'unity\Mahjong.Unity' }
$mutexName = 'Local\MahjongV5P45UnityValidation'
$script:C04LogPath = $null
$script:C04CancelFile = $CancelFile
$script:C04Cancelled = $false
$script:C04ObservedChildren = @{}
$script:C04Summary = $null

# ---------------------------------------------------------------------------
# Basic utilities
# ---------------------------------------------------------------------------

function Write-C04Log {
    param([string]$Message)
    $line = '{0} {1}' -f ([DateTime]::UtcNow.ToString('O')), $Message
    Write-Host $Message
    if ($script:C04LogPath) {
        try { [System.IO.File]::AppendAllText($script:C04LogPath, $line + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false))) }
        catch { }
    }
}

function Get-C04Sha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try {
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try {
            $bytes = $sha.ComputeHash($stream)
            return ([System.BitConverter]::ToString($bytes)).Replace('-', '').ToLowerInvariant()
        }
        finally { $sha.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Get-C04TextSha256 {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
        return ([System.BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally { $sha.Dispose() }
}

function Write-C04Text {
    param([string]$Path, [AllowEmptyString()][string]$Text)
    $directory = Split-Path -Parent $Path
    if ($directory) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
    [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($false)))
}

function Write-C04Json {
    param([string]$Path, $Object)
    Write-C04Text -Path $Path -Text ($Object | ConvertTo-Json -Depth 12)
}

function Read-C04Json {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json)
}

function Get-C04FullPath {
    param([string]$Path, [string]$Base = (Get-Location).Path)
    if ([System.IO.Path]::IsPathRooted($Path)) { return [System.IO.Path]::GetFullPath($Path) }
    return [System.IO.Path]::GetFullPath((Join-Path $Base $Path))
}

function Test-C04PathWithin {
    param([string]$Path, [string]$Parent)
    $p = (Get-C04FullPath $Path).TrimEnd('\', '/')
    $q = (Get-C04FullPath $Parent).TrimEnd('\', '/')
    if ($p.Equals($q, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    return $p.StartsWith($q + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

function Test-C04NoReparsePoint {
    param([Parameter(Mandatory = $true)][string]$Path)
    $bad = New-Object System.Collections.Generic.List[string]
    if (-not (Test-Path -LiteralPath $Path)) { return @() }
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { $bad.Add($item.FullName) | Out-Null }
    if ($item.PSIsContainer) {
        foreach ($child in Get-ChildItem -LiteralPath $Path -Recurse -Force -ErrorAction Stop) {
            if (($child.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { $bad.Add($child.FullName) | Out-Null }
        }
    }
    return @($bad)
}

function Assert-C04NoReparsePoint {
    param([string]$Path, [string]$Label)
    $bad = @(Test-C04NoReparsePoint -Path $Path)
    if ($bad.Count -gt 0) {
        throw ('C04FAIL:REPARSE_POINT:{0} contains reparse points: {1}' -f $Label, ($bad -join ';'))
    }
}

function Copy-C04DirectoryContents {
    param([string]$Source, [string]$Destination)
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination (Join-Path $Destination $item.Name) -Recurse -Force
    }
}

function Get-C04FilesUnder {
    param(
        [string]$Directory,
        [string]$Relative,
        [string[]]$ExcludeDirectoryNames,
        [string[]]$ExcludeRelativeDirectories
    )
    foreach ($item in Get-ChildItem -LiteralPath $Directory -Force -ErrorAction Stop) {
        $childRelative = if ([string]::IsNullOrEmpty($Relative)) { $item.Name } else { $Relative + '/' + $item.Name }
        if ($item.PSIsContainer) {
            if ($ExcludeDirectoryNames -contains $item.Name) { continue }
            $skip = $false
            foreach ($ex in $ExcludeRelativeDirectories) {
                $prefix = $ex.TrimEnd('/') + '/'
                if (($childRelative + '/').StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
                    $childRelative.Equals($ex.TrimEnd('/'), [StringComparison]::OrdinalIgnoreCase)) { $skip = $true; break }
            }
            if ($skip) { continue }
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "C04FAIL:REPARSE_POINT:staging contains a reparse point: $($item.FullName)"
            }
            Get-C04FilesUnder -Directory $item.FullName -Relative $childRelative -ExcludeDirectoryNames $ExcludeDirectoryNames -ExcludeRelativeDirectories $ExcludeRelativeDirectories
        }
        else {
            [pscustomobject]@{ File = $item; Relative = $childRelative }
        }
    }
}

function Get-C04FileTable {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [string[]]$ExcludeRelativeDirectories = @(),
        [string[]]$ExcludeDirectoryNames = @(),
        [string[]]$ExcludeRelativeFiles = @(),
        [string[]]$ExcludeFileSuffixes = @()
    )
    $rootFull = (Get-C04FullPath $Root).TrimEnd('\', '/')
    if (-not (Test-Path -LiteralPath $rootFull)) { return @() }
    $rows = New-Object System.Collections.Generic.List[object]
    foreach ($entry in @(Get-C04FilesUnder -Directory $rootFull -Relative '' -ExcludeDirectoryNames $ExcludeDirectoryNames -ExcludeRelativeDirectories $ExcludeRelativeDirectories)) {
        $relative = [string]$entry.Relative
        $excluded = $false
        if ($ExcludeFileSuffixes.Count -gt 0) {
            foreach ($suffix in $ExcludeFileSuffixes) {
                if ($relative.EndsWith($suffix, [StringComparison]::OrdinalIgnoreCase)) { $excluded = $true; break }
            }
        }
        if ($excluded) { continue }
        foreach ($ex in $ExcludeRelativeFiles) {
            if ($relative.Equals($ex, [StringComparison]::OrdinalIgnoreCase)) { $excluded = $true; break }
        }
        if ($excluded) { continue }
        $file = $entry.File
        $rows.Add([pscustomobject]@{ path = $relative; bytes = [long]$file.Length; sha256 = (Get-C04Sha256 $file.FullName) }) | Out-Null
    }
    return $rows.ToArray()
}

function Write-C04ManifestFile {
    param(
        [string]$Path,
        [object[]]$Rows,
        [string]$Kind = 'file-manifest'
    )
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('"path","bytes","sha256"') | Out-Null
    foreach ($row in $Rows) {
        $lines.Add(('"{0}","{1}","{2}"' -f ([string]$row.path).Replace('"', '""'), [long]$row.bytes, [string]$row.sha256)) | Out-Null
    }
    Write-C04Text -Path $Path -Text (($lines -join [Environment]::NewLine) + [Environment]::NewLine)
    return [pscustomobject]@{
        kind = $Kind
        path = $Path
        fileCount = @($Rows).Count
        bytes = [long](@($Rows) | Measure-Object -Property bytes -Sum).Sum
        sha256 = (Get-C04Sha256 $Path)
    }
}

# ---------------------------------------------------------------------------
# Windows command-line quoting and bounded owned process execution
# ---------------------------------------------------------------------------

function ConvertTo-C04QuotedArgument {
    param([AllowEmptyString()][string]$Argument)
    if ($null -eq $Argument) { return '""' }
    if ($Argument.Length -eq 0) { return '""' }
    if ($Argument -notmatch '[\s"]') { return $Argument }

    $builder = New-Object System.Text.StringBuilder
    $builder.Append('"') | Out-Null
    $backslashes = 0
    foreach ($character in $Argument.ToCharArray()) {
        if ($character -eq '\') {
            $backslashes++
        }
        elseif ($character -eq '"') {
            $builder.Append('\' * (($backslashes * 2) + 1)) | Out-Null
            $builder.Append('"') | Out-Null
            $backslashes = 0
        }
        else {
            if ($backslashes -gt 0) { $builder.Append('\' * $backslashes) | Out-Null; $backslashes = 0 }
            $builder.Append($character) | Out-Null
        }
    }
    if ($backslashes -gt 0) { $builder.Append('\' * ($backslashes * 2)) | Out-Null }
    $builder.Append('"') | Out-Null
    return $builder.ToString()
}

function Get-C04AllProcesses {
    return @(Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, CreationDate, Name -ErrorAction Stop)
}

function Get-C04DescendantProcesses {
    param([int]$RootProcessId)
    $all = @(Get-C04AllProcesses)
    $byParent = @{}
    foreach ($process in $all) {
        $parent = [int]$process.ParentProcessId
        if (-not $byParent.ContainsKey($parent)) { $byParent[$parent] = New-Object System.Collections.Generic.List[object] }
        $byParent[$parent].Add($process) | Out-Null
    }
    $result = New-Object System.Collections.Generic.List[object]
    $seen = @{}
    $queue = New-Object System.Collections.Queue
    $queue.Enqueue($RootProcessId)
    $seen[$RootProcessId] = $true
    while ($queue.Count -gt 0) {
        $current = [int]$queue.Dequeue()
        if (-not $byParent.ContainsKey($current)) { continue }
        foreach ($child in $byParent[$current]) {
            $pidValue = [int]$child.ProcessId
            if ($seen.ContainsKey($pidValue)) { continue }
            $seen[$pidValue] = $true
            if ($pidValue -ne $PID) {
                $result.Add($child) | Out-Null
                $queue.Enqueue($pidValue)
            }
        }
    }
    return $result.ToArray()
}

function Get-C04ProcessCreationUtc {
    param($Process)
    try { return ([datetime]$Process.CreationDate).ToUniversalTime() }
    catch { return [datetime]::MinValue }
}

function Stop-C04ExactProcess {
    param(
        [int]$ProcessId,
        [datetime]$CreationTimeUtc,
        [string]$Reason
    )
    if ($ProcessId -le 0 -or $ProcessId -eq $PID) { return $false }
    try {
        $current = @(Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessId" -ErrorAction Stop)
        if ($current.Count -ne 1) { return $false }
        $actual = Get-C04ProcessCreationUtc $current[0]
        if ($CreationTimeUtc -ne [datetime]::MinValue -and
            [math]::Abs(($actual - $CreationTimeUtc).TotalSeconds) -gt 2) {
            return $false
        }
        Stop-Process -Id $ProcessId -Force -ErrorAction Stop
        Write-C04Log ("STOP_OWNED pid={0} created={1} reason={2}" -f $ProcessId, $CreationTimeUtc.ToString('O'), $Reason)
        return $true
    }
    catch {
        return $false
    }
}

function Stop-C04OwnedDescendants {
    param([int]$RootProcessId, [string]$Reason)
    $stopped = 0
    $remaining = @(Get-C04DescendantProcesses -RootProcessId $RootProcessId)
    foreach ($process in ($remaining | Sort-Object { [int]$_.ProcessId } -Descending)) {
        $created = Get-C04ProcessCreationUtc $process
        if (Stop-C04ExactProcess -ProcessId ([int]$process.ProcessId) -CreationTimeUtc $created -Reason $Reason) { $stopped++ }
    }
    return $stopped
}

function Wait-C04StreamTasks {
    param([System.Threading.Tasks.Task[]]$Tasks, [int]$Milliseconds)
    try {
        return [System.Threading.Tasks.Task]::WaitAll($Tasks, [int]$Milliseconds)
    }
    catch {
        return $false
    }
}

function Start-C04BoundedProcess {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$Arguments = @(),
        [string]$WorkingDirectory,
        [int]$TimeoutSeconds = 180,
        [string]$StdOutPath,
        [string]$StdErrPath,
        [string]$Label = 'process',
        [scriptblock]$CancelProvider,
        [string]$CancelFilePath,
        [int]$Heartbeat = 30,
        [int]$DrainGrace = 10
    )

    if (-not [System.IO.Path]::IsPathRooted($FilePath)) {
        $command = Get-Command $FilePath -ErrorAction SilentlyContinue
        if ($command) { $FilePath = $command.Source }
    }
    if (-not (Test-Path -LiteralPath $FilePath -PathType Leaf)) {
        throw "C04FAIL:PROCESS_NOT_FOUND:$Label executable not found: $FilePath"
    }
    if (-not $StdOutPath) { $StdOutPath = Join-Path ([System.IO.Path]::GetTempPath()) ("c04-$Label-$([guid]::NewGuid().ToString('N')).out") }
    if (-not $StdErrPath) { $StdErrPath = Join-Path ([System.IO.Path]::GetTempPath()) ("c04-$Label-$([guid]::NewGuid().ToString('N')).err") }
    foreach ($path in @($StdOutPath, $StdErrPath)) {
        $directory = Split-Path -Parent $path
        if ($directory) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
    }

    $argumentString = (@($Arguments) | ForEach-Object { ConvertTo-C04QuotedArgument ([string]$_) }) -join ' '
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    $psi.Arguments = $argumentString
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    if ($WorkingDirectory) { $psi.WorkingDirectory = $WorkingDirectory }

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $psi
    $outStream = $null
    $errStream = $null
    $outTask = $null
    $errTask = $null
    $startedAt = Get-Date
    $startTimeUtc = [datetime]::MinValue
    $pidValue = 0
    $timedOut = $false
    $cancelled = $false
    $drained = $false
    $descendantWriter = $false
    $ownedStopped = 0
    try {
        $outStream = [System.IO.File]::Open($StdOutPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)
        $errStream = [System.IO.File]::Open($StdErrPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)
        if (-not $process.Start()) { throw "C04FAIL:PROCESS_START:$Label" }
        $pidValue = $process.Id
        try { $startTimeUtc = $process.StartTime.ToUniversalTime() } catch { $startTimeUtc = [datetime]::MinValue }
        $outTask = $process.StandardOutput.BaseStream.CopyToAsync($outStream)
        $errTask = $process.StandardError.BaseStream.CopyToAsync($errStream)
        Write-C04Log ("START {0} pid={1} start={2} timeout={3}s file={4}" -f $Label, $pidValue, $startTimeUtc.ToString('O'), $TimeoutSeconds, $FilePath)

        $lastHeartbeat = Get-Date
        $nextDescendantScan = (Get-Date).AddSeconds(2)
        while (-not $process.HasExited) {
            $elapsed = ((Get-Date) - $startedAt).TotalSeconds
            if ($TimeoutSeconds -gt 0 -and $elapsed -ge $TimeoutSeconds) { $timedOut = $true; break }
            if ($CancelProvider) {
                try { if (& $CancelProvider) { $cancelled = $true; break } } catch { }
            }
            if ($script:C04Cancelled) { $cancelled = $true; break }
            if ($CancelFilePath -and (Test-Path -LiteralPath $CancelFilePath)) { $cancelled = $true; break }
            if ($script:C04CancelFile -and (Test-Path -LiteralPath $script:C04CancelFile)) { $cancelled = $true; break }
            if ((Get-Date) -ge $nextDescendantScan) {
                foreach ($child in @(Get-C04DescendantProcesses -RootProcessId $pidValue)) {
                    $script:C04ObservedChildren["$([int]$child.ProcessId)"] = Get-C04ProcessCreationUtc $child
                }
                $nextDescendantScan = (Get-Date).AddSeconds(2)
            }
            if ($Heartbeat -gt 0 -and ((Get-Date) - $lastHeartbeat).TotalSeconds -ge $Heartbeat) {
                Write-C04Log ("HEARTBEAT {0} pid={1} elapsed={2:N1}s" -f $Label, $pidValue, $elapsed)
                $lastHeartbeat = Get-Date
            }
            Start-Sleep -Milliseconds 100
        }

        if ($timedOut -or $cancelled) {
            $reason = if ($timedOut) { 'TIMEOUT' } else { 'CANCEL' }
            [void](Stop-C04ExactProcess -ProcessId $pidValue -CreationTimeUtc $startTimeUtc -Reason $reason)
            try { [void]$process.WaitForExit(5000) } catch { }
            $ownedStopped += Stop-C04OwnedDescendants -RootProcessId $pidValue -Reason $reason
        }

        $waitMs = [Math]::Max(1000, $DrainGrace * 1000)
        $drained = Wait-C04StreamTasks -Tasks ([System.Threading.Tasks.Task[]]@($outTask, $errTask)) -Milliseconds $waitMs
        if (-not $drained) {
            # The root exited but inherited pipe handles remain open. Only the
            # exact observed descendants of this root are eligible for cleanup.
            $descendantWriter = $true
            $ownedStopped += Stop-C04OwnedDescendants -RootProcessId $pidValue -Reason 'UNRESOLVED_WRITER'
            $drained = Wait-C04StreamTasks -Tasks ([System.Threading.Tasks.Task[]]@($outTask, $errTask)) -Milliseconds $waitMs
        }
    }
    finally {
        try { if ($outTask) { [void]$outTask.Wait(2000) } } catch { }
        try { if ($errTask) { [void]$errTask.Wait(2000) } } catch { }
        try { if ($outStream) { $outStream.Flush(); $outStream.Dispose() } } catch { }
        try { if ($errStream) { $errStream.Flush(); $errStream.Dispose() } } catch { }
    }

    $rawExit = $null
    $exitKnown = $false
    try {
        if ($process.HasExited) {
            $rawExit = $process.ExitCode
            $exitKnown = -not ($timedOut -or $cancelled)
        }
    }
    catch { }
    try { $process.Dispose() } catch { }

    $result = [pscustomobject]@{
        label = $Label
        file = $FilePath
        arguments = @($Arguments)
        commandLine = $argumentString
        pid = $pidValue
        startTimeUtc = $startTimeUtc.ToString('O')
        exitCode = if ($exitKnown) { $rawExit } else { $null }
        exitCodeKnown = $exitKnown
        rawExitCode = $rawExit
        timedOut = $timedOut
        cancelled = $cancelled
        drainComplete = [bool]$drained
        descendantWriter = $descendantWriter
        ownedDescendantsStopped = $ownedStopped
        durationSeconds = [math]::Round(((Get-Date) - $startedAt).TotalSeconds, 3)
        stdoutPath = $StdOutPath
        stderrPath = $StdErrPath
        stdoutBytes = if (Test-Path -LiteralPath $StdOutPath) { (Get-Item -LiteralPath $StdOutPath).Length } else { 0 }
        stderrBytes = if (Test-Path -LiteralPath $StdErrPath) { (Get-Item -LiteralPath $StdErrPath).Length } else { 0 }
    }
    Write-C04Log ("END {0} pid={1} exit={2} known={3} timeout={4} cancel={5} drained={6} ownedStopped={7} duration={8:N2}s" -f $Label, $pidValue, $result.exitCode, $exitKnown, $timedOut, $cancelled, $drained, $ownedStopped, $result.durationSeconds)
    return $result
}

function Invoke-C04CheckedProcess {
    param(
        [string]$FilePath,
        [string[]]$Arguments,
        [string]$WorkingDirectory,
        [int]$TimeoutSeconds,
        [string]$StdOutPath,
        [string]$StdErrPath,
        [string]$Label,
        [string]$TimeoutCode = 'TIMEOUT',
        [string]$NativeCode = 'NATIVE_EXIT'
    )
    $result = Start-C04BoundedProcess -FilePath $FilePath -Arguments $Arguments -WorkingDirectory $WorkingDirectory -TimeoutSeconds $TimeoutSeconds -StdOutPath $StdOutPath -StdErrPath $StdErrPath -Label $Label -DrainGrace $DrainGraceSeconds -Heartbeat $HeartbeatSeconds
    if ($result.timedOut) { throw "C04FAIL:${TimeoutCode}:$Label timed out after $TimeoutSeconds seconds (pid=$($result.pid))" }
    if ($result.cancelled) { throw "C04FAIL:CANCELLED:$Label was cancelled (pid=$($result.pid))" }
    if (-not $result.exitCodeKnown) { throw "C04FAIL:UNKNOWN_EXIT:$Label native result unknown (pid=$($result.pid))" }
    if ([int]$result.exitCode -ne 0) { throw "C04FAIL:${NativeCode}:$Label exited $($result.exitCode)" }
    return $result
}

# ---------------------------------------------------------------------------
# Source identity, pinned staging, plugin closure
# ---------------------------------------------------------------------------

function Get-C04GitOutput {
    param([string[]]$GitArguments)
    $output = @(& git -C $root @GitArguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw ('C04FAIL:GIT:{0} exited {1}: {2}' -f ($GitArguments -join ' '), $LASTEXITCODE, ($output -join ' '))
    }
    return $output
}

function Get-C04SourceIdentity {
    param([string]$RepoRoot = $root)
    # The identity is bound to build-relevant tracked content, not to the commit
    # hash, so a docs-only commit cannot invalidate a package built from the same
    # code. Paths outside dotnet/unity/tools and the root build configuration are
    # deliberately not part of the Player build input closure.
    $head = ((& git -C $RepoRoot rev-parse HEAD) -join '').Trim()
    if ($LASTEXITCODE -ne 0) { throw 'C04FAIL:GIT:HEAD unreadable' }
    $pathspecs = @('dotnet', 'unity', 'tools')
    $rootFiles = @(
        'Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props',
        'Directory.Build.rsp', 'NuGet.config', 'global.json'
    )
    foreach ($rootFile in $rootFiles) {
        if (Test-Path -LiteralPath (Join-Path $RepoRoot $rootFile) -PathType Leaf) { $pathspecs += $rootFile }
    }

    $treeLines = @(& git -C $RepoRoot ls-tree -r HEAD -- @pathspecs 2>&1)
    if ($LASTEXITCODE -ne 0) { throw 'C04FAIL:GIT:tree unreadable' }
    $status = @(& git -C $RepoRoot status --porcelain=v1 --untracked-files=all -- @pathspecs 2>&1)
    if ($LASTEXITCODE -ne 0) { throw 'C04FAIL:GIT:status unreadable' }

    $diffFile = Join-Path ([System.IO.Path]::GetTempPath()) ("c04-diff-$([guid]::NewGuid().ToString('N')).bin")
    try {
        & git -C $RepoRoot diff --binary HEAD --output=$diffFile -- @pathspecs | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'C04FAIL:GIT:diff unreadable' }
        $diffSha = if (Test-Path -LiteralPath $diffFile) { Get-C04Sha256 $diffFile } else { Get-C04TextSha256 '' }
    }
    finally {
        if (Test-Path -LiteralPath $diffFile) { Remove-Item -LiteralPath $diffFile -Force -ErrorAction SilentlyContinue }
    }

    $untracked = New-Object System.Collections.Generic.List[object]
    $raw = @(& git -C $RepoRoot ls-files --others --exclude-standard -z -- @pathspecs 2>&1)
    if ($LASTEXITCODE -ne 0) { throw 'C04FAIL:GIT:untracked unreadable' }
    $joined = ($raw -join '')
    foreach ($relative in ($joined -split [char]0)) {
        if ([string]::IsNullOrWhiteSpace($relative)) { continue }
        $path = Join-Path $RepoRoot ($relative.Replace('/', '\'))
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $info = Get-Item -LiteralPath $path
            $untracked.Add([pscustomobject]@{ path = $relative; bytes = [long]$info.Length; sha256 = (Get-C04Sha256 $path) }) | Out-Null
        }
        else {
            $untracked.Add([pscustomobject]@{ path = $relative; bytes = -1; sha256 = 'missing' }) | Out-Null
        }
    }

    $canonical = New-Object System.Text.StringBuilder
    $canonical.Append('tree`n') | Out-Null
    foreach ($line in ($treeLines | Sort-Object)) { $canonical.Append('tree=' + $line + "`n") | Out-Null }
    $canonical.Append('diff=' + $diffSha + "`n") | Out-Null
    foreach ($line in ($status | Sort-Object)) { $canonical.Append('status=' + $line + "`n") | Out-Null }
    foreach ($row in ($untracked | Sort-Object -Property path)) {
        $canonical.Append(('untracked={0}|{1}|{2}' -f $row.path, $row.bytes, $row.sha256) + "`n") | Out-Null
    }

    return [pscustomobject]@{
        head = $head
        pathspecs = @($pathspecs)
        treeSha256 = Get-C04TextSha256 (($treeLines | Sort-Object) -join "`n")
        diffSha256 = $diffSha
        statusLines = @($status)
        untracked = $untracked.ToArray()
        identitySha256 = Get-C04TextSha256 $canonical.ToString()
        capturedAtUtc = [DateTime]::UtcNow.ToString('O')
    }
}

function Copy-C04TreeFiltered {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [string[]]$ExcludeDirectoryNames = @()
    )
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        if ($item.PSIsContainer -and ($ExcludeDirectoryNames -contains $item.Name)) { continue }
        $target = Join-Path $Destination $item.Name
        if ($item.PSIsContainer) { Copy-C04TreeFiltered -Source $item.FullName -Destination $target -ExcludeDirectoryNames $ExcludeDirectoryNames }
        else { Copy-Item -LiteralPath $item.FullName -Destination $target -Force }
    }
}

function New-C04StagedRepository {
    param(
        [string]$SourceRoot,
        [string]$StagingRepo
    )
    if (Test-Path -LiteralPath $StagingRepo) {
        throw "C04FAIL:STAGING_EXISTS:pinned staging already exists: $StagingRepo"
    }
    New-Item -ItemType Directory -Force -Path $StagingRepo | Out-Null
    Write-C04Log "STAGE copy pinned source snapshot -> $StagingRepo"
    Copy-C04TreeFiltered -Source (Join-Path $SourceRoot 'dotnet') -Destination (Join-Path $StagingRepo 'dotnet') -ExcludeDirectoryNames @('bin', 'obj')
    Copy-C04TreeFiltered -Source (Join-Path $SourceRoot 'unity\Mahjong.Unity') -Destination (Join-Path $StagingRepo 'unity\Mahjong.Unity') -ExcludeDirectoryNames @('Library', 'Temp', 'Logs', 'obj', 'UserSettings')
    if (Test-Path -LiteralPath (Join-Path $SourceRoot 'tests')) {
        Copy-C04TreeFiltered -Source (Join-Path $SourceRoot 'tests') -Destination (Join-Path $StagingRepo 'tests') -ExcludeDirectoryNames @('bin', 'obj')
    }
    $toolsTarget = Join-Path $StagingRepo 'tools\unity'
    New-Item -ItemType Directory -Force -Path $toolsTarget | Out-Null
    foreach ($tool in @('build-shared.ps1', 'check-shared-plugins.ps1', 'shared-plugin-provenance-lib.ps1')) {
        Copy-Item -LiteralPath (Join-Path $SourceRoot ('tools\unity\' + $tool)) -Destination (Join-Path $toolsTarget $tool) -Force
    }
    foreach ($rootFile in @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'Directory.Build.rsp', 'NuGet.config', 'global.json')) {
        $sourceFile = Join-Path $SourceRoot $rootFile
        if (Test-Path -LiteralPath $sourceFile -PathType Leaf) { Copy-Item -LiteralPath $sourceFile -Destination (Join-Path $StagingRepo $rootFile) -Force }
    }
    Assert-C04NoReparsePoint -Path $StagingRepo -Label 'staging-repository'
    $manifestPath = Join-Path (Split-Path -Parent $StagingRepo) 'staged-source-manifest.csv'
    $rows = Get-C04FileTable -Root $StagingRepo -ExcludeDirectoryNames @('bin', 'obj', 'Library', 'Temp', 'Logs') -ExcludeRelativeDirectories @('unity/Mahjong.Unity/Assets/Plugins/Mahjong')
    $manifest = Write-C04ManifestFile -Path $manifestPath -Rows $rows -Kind 'c04-pinned-source-snapshot'
    return [pscustomobject]@{ repo = $StagingRepo; manifest = $manifest; rows = $rows; project = (Join-Path $StagingRepo 'unity\Mahjong.Unity') }
}

function Test-C04ManifestAgainstRoot {
    param(
        [string]$Root,
        [string]$ManifestPath,
        [string[]]$ExcludeDirectoryNames = @(),
        [string[]]$ExcludeRelativeDirectories = @(),
        [string[]]$ExcludeFileSuffixes = @()
    )
    $errors = New-Object System.Collections.Generic.List[string]
    if (-not (Test-Path -LiteralPath $ManifestPath)) { $errors.Add('STAGING_MANIFEST_MISSING') | Out-Null; return @($errors) }
    $expected = @(Import-Csv -LiteralPath $ManifestPath)
    $actual = @(Get-C04FileTable -Root $Root -ExcludeDirectoryNames $ExcludeDirectoryNames -ExcludeRelativeDirectories $ExcludeRelativeDirectories -ExcludeFileSuffixes $ExcludeFileSuffixes)
    $expectedMap = @{}
    foreach ($row in $expected) { $expectedMap[[string]$row.path] = $row }
    $actualMap = @{}
    foreach ($row in $actual) { $actualMap[[string]$row.path] = $row }
    foreach ($key in $expectedMap.Keys) {
        if (-not $actualMap.ContainsKey($key)) { $errors.Add('STAGING_MISSING:' + $key) | Out-Null; continue }
        if ([long]$expectedMap[$key].bytes -ne [long]$actualMap[$key].bytes -or
            [string]$expectedMap[$key].sha256 -ne [string]$actualMap[$key].sha256) {
            $errors.Add('STAGING_CHANGED:' + $key) | Out-Null
        }
    }
    foreach ($key in $actualMap.Keys) {
        if (-not $expectedMap.ContainsKey($key)) { $errors.Add('STAGING_EXTRA:' + $key) | Out-Null }
    }
    return @($errors)
}

function New-C04StagedUnityProject {
    param(
        [string]$SourceProject,
        [string]$StagingProject,
        [string]$PluginSourceDir,
        [object[]]$PluginFiles
    )
    if (Test-Path -LiteralPath $StagingProject) {
        throw "C04FAIL:STAGING_EXISTS:pinned staging already exists: $StagingProject"
    }
    New-Item -ItemType Directory -Force -Path $StagingProject | Out-Null
    Copy-C04DirectoryContents -Source $SourceProject -Destination $StagingProject
    foreach ($generated in @('Library', 'Temp', 'Logs', 'obj', 'UserSettings')) {
        $path = Join-Path $StagingProject $generated
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }
    Assert-C04NoReparsePoint -Path $StagingProject -Label 'staging-project'

    $pluginTarget = Join-Path $StagingProject 'Assets\Plugins\Mahjong'
    if (-not (Test-Path -LiteralPath $pluginTarget)) { New-Item -ItemType Directory -Force -Path $pluginTarget | Out-Null }
    $keepNames = @{}
    foreach ($file in $PluginFiles) {
        $keepNames[[string]$file.name] = $true
        Copy-Item -LiteralPath (Join-Path $PluginSourceDir ([string]$file.name)) -Destination (Join-Path $pluginTarget ([string]$file.name)) -Force
    }
    foreach ($existing in @(Get-ChildItem -LiteralPath $pluginTarget -File -Force)) {
        if ($existing.Extension -in @('.dll', '.pdb') -and -not $keepNames.ContainsKey($existing.Name)) {
            Remove-Item -LiteralPath $existing.FullName -Force
        }
    }

    $manifestPath = Join-Path (Split-Path -Parent $StagingProject) 'staging-project-manifest.csv'
    $rows = Get-C04FileTable -Root $StagingProject -ExcludeRelativeDirectories @('Library', 'Temp', 'Logs', 'obj', 'UserSettings')
    $manifest = Write-C04ManifestFile -Path $manifestPath -Rows $rows -Kind 'c04-staged-unity-project'
    Write-C04Json -Path (Join-Path (Split-Path -Parent $StagingProject) 'staging-project-seal.json') -Object ([ordered]@{
        schemaVersion = 1
        kind = 'c04-staged-unity-project'
        sourceProject = (Get-C04FullPath $SourceProject)
        projectFileCount = $manifest.fileCount
        projectBytes = $manifest.bytes
        manifestSha256 = $manifest.sha256
        pluginSetSha256 = (Get-C04PluginSetSha $PluginFiles)
        createdUtc = [DateTime]::UtcNow.ToString('O')
    })
    return [pscustomobject]@{ project = $StagingProject; manifest = $manifest; rows = $rows }
}

function Test-C04StagedUnityProject {
    param([string]$StagingProject, [string]$ManifestPath)
    $errors = New-Object System.Collections.Generic.List[string]
    if (-not (Test-Path -LiteralPath $ManifestPath)) { $errors.Add('STAGING_MANIFEST_MISSING') | Out-Null; return @($errors) }
    $expected = @(Import-Csv -LiteralPath $ManifestPath)
    $actual = @(Get-C04FileTable -Root $StagingProject -ExcludeRelativeDirectories @('Library', 'Temp', 'Logs', 'obj', 'UserSettings'))
    $expectedMap = @{}
    foreach ($row in $expected) { $expectedMap[[string]$row.path] = $row }
    $actualMap = @{}
    foreach ($row in $actual) { $actualMap[[string]$row.path] = $row }
    foreach ($key in $expectedMap.Keys) {
        if (-not $actualMap.ContainsKey($key)) { $errors.Add('STAGING_MISSING:' + $key) | Out-Null; continue }
        if ([long]$expectedMap[$key].bytes -ne [long]$actualMap[$key].bytes -or
            [string]$expectedMap[$key].sha256 -ne [string]$actualMap[$key].sha256) {
            $errors.Add('STAGING_CHANGED:' + $key) | Out-Null
        }
    }
    foreach ($key in $actualMap.Keys) {
        if (-not $expectedMap.ContainsKey($key)) { $errors.Add('STAGING_EXTRA:' + $key) | Out-Null }
    }
    return @($errors)
}

function Get-C04PluginSetSha {
    param([object[]]$Files)
    $canonical = (@($Files) | Sort-Object -Property name | ForEach-Object {
        '{0}|{1}|{2}' -f ([string]$_.name).ToLowerInvariant(), [long]$_.bytes, ([string]$_.sha256).ToLowerInvariant()
    }) -join "`n"
    return Get-C04TextSha256 $canonical
}

function Get-C04PluginClosure {
    param(
        [Parameter(Mandatory = $true)][string]$ProvenancePath,
        [Parameter(Mandatory = $true)][string]$PluginDir
    )
    $errors = New-Object System.Collections.Generic.List[string]
    $files = New-Object System.Collections.Generic.List[object]
    if (-not (Test-Path -LiteralPath $ProvenancePath)) {
        $errors.Add('PLUGIN_PROVENANCE_MISSING') | Out-Null
        return [pscustomobject]@{ ok = $false; errors = @($errors); files = @(); pluginSetSha256 = ''; provenanceSha256 = '' }
    }
    try { $data = Get-Content -LiteralPath $ProvenancePath -Raw | ConvertFrom-Json }
    catch { $errors.Add('PLUGIN_PROVENANCE_MALFORMED') | Out-Null; return [pscustomobject]@{ ok = $false; errors = @($errors); files = @(); pluginSetSha256 = ''; provenanceSha256 = '' } }

    if ([int]$data.schemaVersion -ne 2) { $errors.Add('PLUGIN_PROVENANCE_SCHEMA') | Out-Null }
    if ([string]$data.kind -ne 'mahjong-unity-shared-plugin-provenance') { $errors.Add('PLUGIN_PROVENANCE_KIND') | Out-Null }
    if ([string]$data.evidenceScope -notin @('scratch', 'default-synced')) { $errors.Add('PLUGIN_PROVENANCE_SCOPE') | Out-Null }
    if (-not [string]::IsNullOrWhiteSpace([string]$data.pluginDir) -and
        (Get-C04FullPath ([string]$data.pluginDir)) -ne (Get-C04FullPath $PluginDir)) {
        $errors.Add('PLUGIN_PROVENANCE_PLUGIN_DIR') | Out-Null
    }
    if ($null -eq $data.build -or [int]$data.build.exitCode -ne 0) { $errors.Add('PLUGIN_PROVENANCE_BUILD_EXIT') | Out-Null }

    $names = @{}
    foreach ($output in @($data.outputs)) {
        $name = [string]$output.Name
        $path = Join-Path $PluginDir $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { $errors.Add('PLUGIN_MISSING:' + $name) | Out-Null; continue }
        $bytes = (Get-Item -LiteralPath $path).Length
        $sha = Get-C04Sha256 $path
        if ([long]$output.Length -ne [long]$bytes) { $errors.Add('PLUGIN_SIZE_MISMATCH:' + $name) | Out-Null }
        if ([string]$output.Sha256 -ne $sha) { $errors.Add('PLUGIN_HASH_MISMATCH:' + $name) | Out-Null }
        $names[$name] = $true
        $files.Add([pscustomobject]@{ name = $name; bytes = [long]$bytes; sha256 = $sha }) | Out-Null
    }

    foreach ($required in @('Mahjong.Domain.dll', 'Mahjong.Contracts.dll', 'Mahjong.Contracts.Wire.dll', 'Mahjong.TableRuntime.dll', 'Mahjong.Client.dll', 'Mahjong.LocalHost.dll', 'Mahjong.Ai.dll', 'Google.Protobuf.dll')) {
        if (-not $names.ContainsKey($required)) { $errors.Add('PLUGIN_REQUIRED_MISSING:' + $required) | Out-Null }
    }
    foreach ($file in @(Get-ChildItem -LiteralPath $PluginDir -File -Force)) {
        if ($file.Name -eq 'shared-plugin-provenance.json') { continue }
        if ($file.Extension -eq '.meta') { continue }
        if (-not $names.ContainsKey($file.Name)) { $errors.Add('PLUGIN_UNEXPECTED:' + $file.Name) | Out-Null }
    }

    return [pscustomobject]@{
        ok = ($errors.Count -eq 0)
        errors = $errors.ToArray()
        files = $files.ToArray()
        pluginSetSha256 = Get-C04PluginSetSha $files.ToArray()
        provenanceSha256 = Get-C04Sha256 $ProvenancePath
        evidenceScope = [string]$data.evidenceScope
        inputCount = @($data.inputs).Count
        buildExitCode = [int]$data.build.exitCode
    }
}

# ---------------------------------------------------------------------------
# BuildReport attribution
# ---------------------------------------------------------------------------

function Test-C04BuildReport {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedOutput,
        [object[]]$ExpectedPluginFiles = @(),
        [string[]]$AllowedNonFatalStepErrors = @()
    )
    $errors = New-Object System.Collections.Generic.List[string]
    $warnings = New-Object System.Collections.Generic.List[string]
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        $errors.Add('BUILD_REPORT_MISSING') | Out-Null
        return [pscustomobject]@{ ok = $false; errors = @($errors); warnings = @(); report = $null; stepErrors = @() }
    }
    try { $report = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
    catch {
        $errors.Add('BUILD_REPORT_MALFORMED') | Out-Null
        return [pscustomobject]@{ ok = $false; errors = @($errors); warnings = @(); report = $null; stepErrors = @() }
    }

    if ([int]$report.schema -ne 1) { $errors.Add('BUILD_REPORT_SCHEMA') | Out-Null }
    if ([string]$report.result -ne 'Succeeded') { $errors.Add('BUILD_REPORT_RESULT:' + [string]$report.result) | Out-Null }
    if ($null -eq $report.errors -or $report.errors -is [bool] -or $report.errors -is [string]) {
        $errors.Add('BUILD_REPORT_ERRORS_NOT_NUMERIC') | Out-Null
    }
    $knownNonFatal = New-Object System.Collections.Generic.List[string]
    $stepErrors = New-Object System.Collections.Generic.List[string]
    $stepCount = 0
    foreach ($step in @($report.steps)) {
        if ($null -eq $step) { continue }
        $stepCount++
        foreach ($message in @($step.messages)) {
            if ($null -eq $message) { continue }
            $type = [string]$message.type
            if ($type -in @('Error', 'Exception', 'Assert')) {
                $detail = ('step={0} type={1} message={2}' -f [string]$step.name, $type, [string]$message.content)
                $stepErrors.Add($detail) | Out-Null
                $content = [string]$message.content
                $isAllowed = ($AllowedNonFatalStepErrors -contains $content)
                if (-not $isAllowed -and $content -match '^Error building Player: [0-9]+ errors$' -and $AllowedNonFatalStepErrors.Count -gt 0) {
                    $isAllowed = $true
                }
                if ($isAllowed) { $knownNonFatal.Add($detail) | Out-Null }
                else { $errors.Add('BUILD_REPORT_STEP_ERROR:' + $detail) | Out-Null }
            }
        }
    }
    if (($null -ne $report.errors) -and [int]$report.errors -gt 0) {
        $errorCount = [int]$report.errors
        if ($stepErrors.Count -eq 0) {
            $errors.Add('BUILD_REPORT_UNATTRIBUTED_ERRORS') | Out-Null
        }
        if ($errors.Count -gt 0 -or $AllowedNonFatalStepErrors.Count -eq 0) {
            $errors.Add('BUILD_REPORT_ERRORS:' + $errorCount) | Out-Null
        }
    }
    if ($stepCount -eq 0) { $errors.Add('BUILD_REPORT_NO_STEPS') | Out-Null }

    $expectedFull = Get-C04FullPath $ExpectedOutput
    $reportedFull = if ([string]::IsNullOrWhiteSpace([string]$report.output)) { '' } else { Get-C04FullPath ([string]$report.output) }
    if (-not $reportedFull.Equals($expectedFull, [StringComparison]::OrdinalIgnoreCase)) {
        $errors.Add('BUILD_REPORT_OUTPUT_MISMATCH') | Out-Null
    }
    if (-not (Test-Path -LiteralPath $expectedFull -PathType Leaf)) {
        $errors.Add('BUILD_OUTPUT_MISSING') | Out-Null
    }
    else {
        $bytes = (Get-Item -LiteralPath $expectedFull).Length
        $sha = Get-C04Sha256 $expectedFull
        if ($report.bytes -and [long]$report.bytes -ne [long]$bytes) { $errors.Add('BUILD_OUTPUT_SIZE_MISMATCH') | Out-Null }
        if ([string]$report.outputSha256 -and [string]$report.outputSha256 -ne $sha) { $errors.Add('BUILD_OUTPUT_HASH_MISMATCH') | Out-Null }
    }

    foreach ($scene in @('Assets/Scenes/Boot.unity', 'Assets/Scenes/Menu.unity', 'Assets/Scenes/Lobby.unity', 'Assets/Scenes/Table.unity', 'Assets/Scenes/Spectator.unity', 'Assets/Scenes/Settlement.unity')) {
        if (@($report.scenes) -notcontains $scene) { $errors.Add('BUILD_SCENE_MISSING:' + $scene) | Out-Null }
    }

    if ($ExpectedPluginFiles.Count -gt 0) {
        $reported = @{}
        foreach ($plugin in @($report.plugins)) { $reported[[string]$plugin.name] = $plugin }
        foreach ($file in @($ExpectedPluginFiles | Where-Object { ([string]$_.name).EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase) })) {
            if (-not $reported.ContainsKey([string]$file.name)) { $errors.Add('BUILD_REPORT_PLUGIN_MISSING:' + [string]$file.name) | Out-Null }
            elseif ([string]$reported[[string]$file.name].sha256 -ne [string]$file.sha256) { $errors.Add('BUILD_REPORT_PLUGIN_HASH:' + [string]$file.name) | Out-Null }
        }
    }

    try {
        $started = [datetime]::Parse([string]$report.startedUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind)
        $finished = [datetime]::Parse([string]$report.finishedUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind)
        if ($finished -le $started) { $errors.Add('BUILD_REPORT_TIME_ORDER') | Out-Null }
    }
    catch { $errors.Add('BUILD_REPORT_TIME_INVALID') | Out-Null }

    return [pscustomobject]@{
        ok = ($errors.Count -eq 0)
        errors = @($errors)
        warnings = @($warnings)
        report = $report
        stepErrors = @($stepErrors)
        knownNonFatalStepErrors = @($knownNonFatal)
        unknownStepErrors = @($errors | Where-Object { $_ -like 'BUILD_REPORT_STEP_ERROR:*' })
        stepCount = $stepCount
    }
}

# ---------------------------------------------------------------------------
# Package manifest, package seal, evidence seal
# ---------------------------------------------------------------------------

function Get-C04PackageRows {
    param([string]$PackageRoot)
    return @(Get-C04FileTable -Root $PackageRoot -ExcludeRelativeFiles @('c04-package-manifest.csv', 'c04-package-seal.json'))
}

function Write-C04PackageSeal {
    param(
        [string]$PackageRoot,
        [string]$PlayerPath,
        [string]$ReportPath,
        [string]$ProvenancePath,
        [string]$SourceHead,
        [string]$SourceIdentitySha256,
        [string]$BuildCommit,
        [object]$PluginClosure
    )
    $packageRootFull = (Get-C04FullPath $PackageRoot).TrimEnd('\', '/')
    $manifestPath = Join-Path $packageRootFull 'c04-package-manifest.csv'
    $sealPath = Join-Path $packageRootFull 'c04-package-seal.json'
    $rows = Get-C04PackageRows -PackageRoot $packageRootFull
    $manifest = Write-C04ManifestFile -Path $manifestPath -Rows $rows -Kind 'c04-player-package'
    $playerFull = Get-C04FullPath $PlayerPath
    if (-not (Test-C04PathWithin $playerFull $packageRootFull)) { throw 'C04FAIL:PACKAGE_PLAYER_OUTSIDE' }
    $playerRelative = $playerFull.Substring($packageRootFull.Length).TrimStart('\', '/').Replace('\', '/')
    $reportFull = Get-C04FullPath $ReportPath
    $provenanceFull = Get-C04FullPath $ProvenancePath
    foreach ($required in @($playerFull, $reportFull, $provenanceFull)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "C04FAIL:PACKAGE_REQUIRED_MISSING:$required" }
    }
    $report = Read-C04Json -Path $reportFull
    $seal = [ordered]@{
        schemaVersion = 1
        kind = 'c04-player-package-seal'
        runnerVersion = $script:C04RunnerVersion
        sealedAtUtc = [DateTime]::UtcNow.ToString('O')
        sourceHead = $SourceHead
        sourceIdentitySha256 = $SourceIdentitySha256
        buildCommit = $BuildCommit
        packageRoot = $packageRootFull
        playerRelativePath = $playerRelative
        playerBytes = (Get-Item -LiteralPath $playerFull).Length
        playerSha256 = Get-C04Sha256 $playerFull
        packageManifestRelativePath = 'c04-package-manifest.csv'
        packageManifestBytes = (Get-Item -LiteralPath $manifestPath).Length
        packageManifestSha256 = (Get-C04Sha256 $manifestPath)
        packageFileCount = $manifest.fileCount
        packageBytes = $manifest.bytes
        buildReportRelativePath = $reportFull.Substring($packageRootFull.Length).TrimStart('\', '/').Replace('\', '/')
        buildReportSha256 = (Get-C04Sha256 $reportFull)
        buildResult = [string]$report.result
        buildErrors = $report.errors
        buildWarnings = $report.warnings
        buildStepErrorCount = [int]$report.stepErrorCount
        buildStepExceptionCount = [int]$report.stepExceptionCount
        buildKnownNonFatalErrors = [int]$report.stepErrorCount
        buildErrorClassification = if ([int]$report.stepErrorCount -gt 0) { 'UNITY_PACKAGE_ASSET_HOST_TYPE_NONFATAL' } else { 'NONE' }
        pluginProvenanceRelativePath = $provenanceFull.Substring($packageRootFull.Length).TrimStart('\', '/').Replace('\', '/')
        pluginProvenanceSha256 = (Get-C04Sha256 $provenanceFull)
        pluginSetSha256 = $PluginClosure.pluginSetSha256
        pluginFileCount = @($PluginClosure.files).Count
    }
    Write-C04Json -Path $sealPath -Object $seal
    return [pscustomobject]@{ sealPath = $sealPath; seal = $seal; manifestPath = $manifestPath; manifest = $manifest }
}

function Test-C04PackageSeal {
    param(
        [Parameter(Mandatory = $true)][string]$PlayerPath,
        [string]$ExpectedSourceHead,
        [string]$ExpectedBuildCommit,
        [string]$CurrentSourceIdentitySha256
    )
    $errors = New-Object System.Collections.Generic.List[string]
    $playerFull = Get-C04FullPath $PlayerPath
    $packageRoot = Split-Path -Parent $playerFull
    $sealPath = Join-Path $packageRoot 'c04-package-seal.json'
    $manifestPath = Join-Path $packageRoot 'c04-package-manifest.csv'
    if (-not (Test-Path -LiteralPath $playerFull)) { $errors.Add('PLAYER_NOT_FOUND') | Out-Null }
    if (-not (Test-Path -LiteralPath $sealPath)) { $errors.Add('PACKAGE_SEAL_MISSING') | Out-Null; return [pscustomobject]@{ ok = $false; errors = @($errors); seal = $null; manifest = $null } }
    if (-not (Test-Path -LiteralPath $manifestPath)) { $errors.Add('PACKAGE_MANIFEST_MISSING') | Out-Null; return [pscustomobject]@{ ok = $false; errors = @($errors); seal = $null; manifest = $null } }
    try { $seal = Get-Content -LiteralPath $sealPath -Raw | ConvertFrom-Json }
    catch { $errors.Add('PACKAGE_SEAL_MALFORMED') | Out-Null; return [pscustomobject]@{ ok = $false; errors = @($errors); seal = $null; manifest = $null } }

    if ([int]$seal.schemaVersion -ne 1) { $errors.Add('PACKAGE_SEAL_SCHEMA') | Out-Null }
    if ([string]$seal.kind -ne 'c04-player-package-seal') { $errors.Add('PACKAGE_SEAL_KIND') | Out-Null }
    if ((Get-C04FullPath ([string]$seal.packageRoot)) -ne $packageRoot) { $errors.Add('PACKAGE_SEAL_PACKAGE_PATH') | Out-Null }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedSourceHead) -and [string]$seal.sourceHead -ne $ExpectedSourceHead) { $errors.Add('PACKAGE_SEAL_SOURCE_HEAD_MISMATCH') | Out-Null }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedBuildCommit) -and $ExpectedBuildCommit -ne 'unknown' -and [string]$seal.buildCommit -ne $ExpectedBuildCommit) { $errors.Add('PACKAGE_SEAL_BUILD_COMMIT_MISMATCH') | Out-Null }
    if (-not [string]::IsNullOrWhiteSpace($CurrentSourceIdentitySha256) -and [string]$seal.sourceIdentitySha256 -ne $CurrentSourceIdentitySha256) { $errors.Add('PACKAGE_SEAL_SOURCE_IDENTITY_MISMATCH') | Out-Null }

    if ((Get-C04Sha256 $manifestPath) -ne [string]$seal.packageManifestSha256) { $errors.Add('PACKAGE_MANIFEST_HASH_MISMATCH') | Out-Null }
    if ((Get-Item -LiteralPath $manifestPath).Length -ne [long]$seal.packageManifestBytes) { $errors.Add('PACKAGE_MANIFEST_SIZE_MISMATCH') | Out-Null }

    $expected = @{}
    foreach ($row in @(Import-Csv -LiteralPath $manifestPath)) { $expected[[string]$row.path] = $row }
    $actualRows = @(Get-C04PackageRows -PackageRoot $packageRoot)
    $actual = @{}
    foreach ($row in $actualRows) { $actual[[string]$row.path] = $row }
    foreach ($key in $expected.Keys) {
        if (-not $actual.ContainsKey($key)) { $errors.Add('PACKAGE_MISSING_FILE:' + $key) | Out-Null; continue }
        if ([long]$expected[$key].bytes -ne [long]$actual[$key].bytes) { $errors.Add('PACKAGE_SIZE_MISMATCH:' + $key) | Out-Null }
        if ([string]$expected[$key].sha256 -ne [string]$actual[$key].sha256) { $errors.Add('PACKAGE_HASH_MISMATCH:' + $key) | Out-Null }
    }
    foreach ($key in $actual.Keys) {
        if (-not $expected.ContainsKey($key)) { $errors.Add('PACKAGE_EXTRA_FILE:' + $key) | Out-Null }
    }

    $playerRelative = $playerFull.Substring($packageRoot.Length).TrimStart('\', '/').Replace('\', '/')
    if ([string]$seal.playerRelativePath -ne $playerRelative) { $errors.Add('PACKAGE_SEAL_PLAYER_PATH') | Out-Null }
    if (Test-Path -LiteralPath $playerFull) {
        if ([long]$seal.playerBytes -ne (Get-Item -LiteralPath $playerFull).Length) { $errors.Add('PACKAGE_PLAYER_SIZE_MISMATCH') | Out-Null }
        if ([string]$seal.playerSha256 -ne (Get-C04Sha256 $playerFull)) { $errors.Add('PACKAGE_PLAYER_HASH_MISMATCH') | Out-Null }
    }

    $reportRelative = [string]$seal.buildReportRelativePath
    if ([string]::IsNullOrWhiteSpace($reportRelative)) { $errors.Add('PACKAGE_SEAL_REPORT_PATH') | Out-Null }
    else {
        $reportFull = Join-Path $packageRoot $reportRelative
        if (-not (Test-Path -LiteralPath $reportFull)) { $errors.Add('PACKAGE_REPORT_MISSING') | Out-Null }
        else {
            if ((Get-C04Sha256 $reportFull) -ne [string]$seal.buildReportSha256) { $errors.Add('PACKAGE_REPORT_HASH_MISMATCH') | Out-Null }
            $check = Test-C04BuildReport -Path $reportFull -ExpectedOutput $playerFull -AllowedNonFatalStepErrors $script:C04KnownNonFatalBuildErrors
            foreach ($errorItem in $check.errors) { $errors.Add('PACKAGE_REPORT:' + $errorItem) | Out-Null }
        }
    }

    $provenanceRelative = [string]$seal.pluginProvenanceRelativePath
    if ([string]::IsNullOrWhiteSpace($provenanceRelative)) { $errors.Add('PACKAGE_SEAL_PROVENANCE_PATH') | Out-Null }
    else {
        $provenanceFull = Join-Path $packageRoot $provenanceRelative
        if (-not (Test-Path -LiteralPath $provenanceFull)) { $errors.Add('PACKAGE_PROVENANCE_MISSING') | Out-Null }
        elseif ((Get-C04Sha256 $provenanceFull) -ne [string]$seal.pluginProvenanceSha256) { $errors.Add('PACKAGE_PROVENANCE_HASH_MISMATCH') | Out-Null }
    }

    return [pscustomobject]@{ ok = ($errors.Count -eq 0); errors = @($errors); seal = $seal; manifest = $manifestPath }
}

function Write-C04EvidenceSeal {
    param(
        [string]$EvidenceRoot,
        [string]$Status,
        [string]$SourceHead
    )
    $manifestPath = Join-Path $EvidenceRoot 'manifest.csv'
    $sealPath = Join-Path $EvidenceRoot 'seal.json'
    $rows = Get-C04FileTable -Root $EvidenceRoot -ExcludeRelativeDirectories @(
        'staging/unity-project',
        'staging/repo',
        'build/player'
    ) -ExcludeRelativeFiles @('manifest.csv', 'seal.json')
    $manifest = Write-C04ManifestFile -Path $manifestPath -Rows $rows -Kind 'c04-evidence'
    Write-C04Json -Path $sealPath -Object ([ordered]@{
        schemaVersion = 1
        kind = 'c04-evidence-seal'
        runnerVersion = $script:C04RunnerVersion
        status = $Status
        sourceHead = $SourceHead
        sealedAtUtc = [DateTime]::UtcNow.ToString('O')
        manifestSha256 = (Get-C04Sha256 $manifestPath)
        manifestBytes = (Get-Item -LiteralPath $manifestPath).Length
        fileCount = $manifest.fileCount
        totalBytes = $manifest.bytes
    })
    return [pscustomobject]@{ manifestPath = $manifestPath; sealPath = $sealPath; manifest = $manifest }
}

function Test-C04EvidenceSeal {
    param([string]$EvidenceRoot)
    $errors = New-Object System.Collections.Generic.List[string]
    $manifestPath = Join-Path $EvidenceRoot 'manifest.csv'
    $sealPath = Join-Path $EvidenceRoot 'seal.json'
    if (-not (Test-Path -LiteralPath $manifestPath)) { return @('EVIDENCE_MANIFEST_MISSING') }
    if (-not (Test-Path -LiteralPath $sealPath)) { return @('EVIDENCE_SEAL_MISSING') }
    try { $seal = Get-Content -LiteralPath $sealPath -Raw | ConvertFrom-Json } catch { return @('EVIDENCE_SEAL_MALFORMED') }
    if ((Get-C04Sha256 $manifestPath) -ne [string]$seal.manifestSha256) { $errors.Add('EVIDENCE_SEAL_MANIFEST_HASH') | Out-Null }
    $expected = @{}
    foreach ($row in @(Import-Csv -LiteralPath $manifestPath)) { $expected[[string]$row.path] = $row }
    $actualRows = @(Get-C04FileTable -Root $EvidenceRoot -ExcludeRelativeDirectories @('staging/unity-project', 'staging/repo', 'build/player') -ExcludeRelativeFiles @('manifest.csv', 'seal.json'))
    $actual = @{}
    foreach ($row in $actualRows) { $actual[[string]$row.path] = $row }
    foreach ($key in $expected.Keys) {
        if (-not $actual.ContainsKey($key)) { $errors.Add('EVIDENCE_MISSING:' + $key) | Out-Null; continue }
        if ([string]$expected[$key].sha256 -ne [string]$actual[$key].sha256) { $errors.Add('EVIDENCE_HASH:' + $key) | Out-Null }
    }
    foreach ($key in $actual.Keys) {
        if (-not $expected.ContainsKey($key)) { $errors.Add('EVIDENCE_EXTRA:' + $key) | Out-Null }
    }
    return @($errors)
}

# ---------------------------------------------------------------------------
# D04-A verifier handoff (only the fixed external CLI, never a local fallback)
# ---------------------------------------------------------------------------

function Resolve-C04Dotnet {
    if (-not [string]::IsNullOrWhiteSpace($DotnetPath)) {
        if (-not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) { throw "C04FAIL:DOTNET_NOT_FOUND:$DotnetPath" }
        return (Get-C04FullPath $DotnetPath)
    }
    $candidates = @(
        'D:\Unity\dotnet\dotnet.exe',
        (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
    )
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) { return $candidate }
    }
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    return $null
}

function Find-C04Verifier {
    if (-not [string]::IsNullOrWhiteSpace($VerifierPath)) {
        if (-not (Test-Path -LiteralPath $VerifierPath -PathType Leaf)) { throw "C04FAIL:VERIFIER_NOT_FOUND:$VerifierPath" }
        return (Get-C04FullPath $VerifierPath)
    }
    $candidates = @(
        (Join-Path $root 'dotnet\tools\C04PlayerCheatVerifier2\bin\Release\net8.0\C04PlayerCheatVerifier2.dll'),
        (Join-Path $root 'dotnet\tools\C04PlayerCheatVerifier2\bin\Release\net9.0\C04PlayerCheatVerifier2.dll')
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    return $null
}

function Get-C04VerifierArguments {
    param(
        [string]$Verifier,
        [string]$EvidenceJson,
        [string]$CaseName,
        [string]$ExpectedSourceHead,
        [string]$PackageManifest
    )
    return @($Verifier, 'verify', '--evidence', $EvidenceJson, '--case', $CaseName, '--source-head', $ExpectedSourceHead, '--package-manifest', $PackageManifest)
}

function Invoke-C04Verifier {
    param(
        [string]$Verifier,
        [string]$EvidenceJson,
        [string]$CaseName,
        [string]$ExpectedSourceHead,
        [string]$PackageManifest,
        [string]$OutPath,
        [string]$ErrPath
    )
    $dotnet = Resolve-C04Dotnet
    if (-not $dotnet) { throw 'C04FAIL:DOTNET_NOT_FOUND' }
    $arguments = Get-C04VerifierArguments -Verifier $Verifier -EvidenceJson $EvidenceJson -CaseName $CaseName -ExpectedSourceHead $ExpectedSourceHead -PackageManifest $PackageManifest
    $result = Start-C04BoundedProcess -FilePath $dotnet -Arguments $arguments -TimeoutSeconds $VerifierTimeoutSeconds -StdOutPath $OutPath -StdErrPath $ErrPath -Label ("verifier-$CaseName") -DrainGrace $DrainGraceSeconds -Heartbeat $HeartbeatSeconds
    $status = if ($result.timedOut) { 'TIMEOUT' } elseif ($result.cancelled) { 'CANCELLED' } elseif (-not $result.exitCodeKnown) { 'UNKNOWN' } elseif ([int]$result.exitCode -eq 0) { 'PASS' } else { 'FAIL' }
    return [pscustomobject]@{ status = $status; result = $result; arguments = $arguments }
}

function Get-C04CaseStructureErrors {
    param([string]$EvidenceJson)
    $errors = New-Object System.Collections.Generic.List[string]
    if (-not (Test-Path -LiteralPath $EvidenceJson -PathType Leaf)) { $errors.Add('MISSING_EVIDENCE_JSON') | Out-Null; return @($errors) }
    try { $null = Get-Content -LiteralPath $EvidenceJson -Raw | ConvertFrom-Json }
    catch { $errors.Add('EVIDENCE_JSON_MALFORMED') | Out-Null }
    return @($errors)
}

# ---------------------------------------------------------------------------
# Stage execution
# ---------------------------------------------------------------------------

function Get-C04ExitCodeForFailure {
    param([string]$Code)
    if ([string]::IsNullOrWhiteSpace($Code)) { return 1 }
    if ($Code -eq 'MUTEX_TIMEOUT') { return 6 }
    if ($Code -match 'CANCEL') { return 5 }
    if ($Code -match 'TIMEOUT') { return 4 }
    if ($Code -match '^(BUILD|UNITY|SHARED|PLUGIN|STAGING|PACKAGE_REPORT|BUILD_REPORT)') { return 3 }
    if ($Code -match '^(VERIFIER|A_VERIFIER|USAGE|PRECONDITION|REFUSE|EVIDENCE_ROOT|PLAYER_NOT_FOUND|SKIPBUILD|SOURCE_|REPARSE)') { return 2 }
    return 1
}

function Invoke-C04SharedBuild {
    param(
        [string]$StagingRoot,
        [string]$StagingRepo
    )
    $project = Join-Path $StagingRepo 'unity\Mahjong.Unity'
    $plugins = Join-Path $project 'Assets\Plugins\Mahjong'
    $provenance = Join-Path $project 'Library\SharedPluginProvenance\provenance.json'
    $logs = Join-Path $StagingRoot 'logs'
    New-Item -ItemType Directory -Force -Path $logs | Out-Null
    Write-C04Log "STAGE shared-build staging-repo=$StagingRepo"
    $sharedArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $StagingRepo 'tools\unity\build-shared.ps1'), '-Configuration', 'Release')
    if ($DotnetPath) { $sharedArgs += @('-DotnetPath', $DotnetPath) }
    $result = Start-C04BoundedProcess -FilePath 'powershell.exe' -Arguments $sharedArgs -WorkingDirectory $StagingRepo -TimeoutSeconds $SharedBuildTimeoutSeconds -StdOutPath (Join-Path $logs 'build-shared.out.log') -StdErrPath (Join-Path $logs 'build-shared.err.log') -Label 'shared-build' -DrainGrace $DrainGraceSeconds -Heartbeat $HeartbeatSeconds
    $script:C04Stages.Add($result) | Out-Null
    if ($result.timedOut) { throw "C04FAIL:SHARED_BUILD_TIMEOUT:shared build timed out after $SharedBuildTimeoutSeconds seconds" }
    if ($result.cancelled) { throw 'C04FAIL:CANCELLED:shared build cancelled' }
    if (-not $result.exitCodeKnown) { throw 'C04FAIL:UNKNOWN_EXIT:shared build native result unknown' }
    if ([int]$result.exitCode -ne 0) { throw "C04FAIL:SHARED_BUILD:shared build exited $($result.exitCode)" }
    Write-C04Log "STAGE check-shared-plugins"
    $guardArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $StagingRepo 'tools\unity\check-shared-plugins.ps1'), '-ProjectRoot', $StagingRepo, '-Configuration', 'Release')
    $guard = Start-C04BoundedProcess -FilePath 'powershell.exe' -Arguments $guardArgs -WorkingDirectory $StagingRepo -TimeoutSeconds $SharedBuildTimeoutSeconds -StdOutPath (Join-Path $logs 'check-shared.out.log') -StdErrPath (Join-Path $logs 'check-shared.err.log') -Label 'check-shared-plugins' -DrainGrace $DrainGraceSeconds -Heartbeat $HeartbeatSeconds
    $script:C04Stages.Add($guard) | Out-Null
    if ($guard.timedOut) { throw "C04FAIL:SHARED_GUARD_TIMEOUT:shared plugin guard timed out after $SharedBuildTimeoutSeconds seconds" }
    if ($guard.cancelled) { throw 'C04FAIL:CANCELLED:shared plugin guard cancelled' }
    if (-not $guard.exitCodeKnown) { throw 'C04FAIL:UNKNOWN_EXIT:shared plugin guard native result unknown' }
    if ([int]$guard.exitCode -ne 0) { throw "C04FAIL:SHARED_GUARD:shared plugin guard exited $($guard.exitCode)" }
    $closure = Get-C04PluginClosure -ProvenancePath $provenance -PluginDir $plugins
    if (-not $closure.ok) { throw ('C04FAIL:PLUGIN_CLOSURE:' + ($closure.errors -join ',')) }
    $closureRows = @($closure.files | ForEach-Object { [pscustomobject]@{ path = $_.name; bytes = $_.bytes; sha256 = $_.sha256 } })
    $closureManifest = Write-C04ManifestFile -Path (Join-Path $StagingRoot 'plugin-closure.csv') -Rows $closureRows -Kind 'c04-fresh-plugin-closure'
    Write-C04Json -Path (Join-Path $StagingRoot 'plugin-closure.json') -Object ([ordered]@{
        schemaVersion = 1
        pluginSetSha256 = $closure.pluginSetSha256
        provenanceSha256 = $closure.provenanceSha256
        inputCount = $closure.inputCount
        fileCount = @($closure.files).Count
        manifestSha256 = $closureManifest.sha256
    })
    return [pscustomobject]@{
        pluginDir = $plugins
        provenancePath = $provenance
        closure = $closure
        buildResult = $result
        guardResult = $guard
        closureManifest = $closureManifest
    }
}

function Wait-C04UnityProjectReleased {
    param([string]$ProjectPath, [int]$TimeoutSeconds = 120)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $stable = 0
    while ((Get-Date) -lt $deadline) {
        $holders = @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($ProjectPath, [StringComparison]::OrdinalIgnoreCase) -ge 0 })
        if ($holders.Count -eq 0) {
            $stable++
            if ($stable -ge 2) { return $true }
        }
        else { $stable = 0 }
        Start-Sleep -Seconds 2
    }
    return $false
}

function Invoke-C04UnityWarmup {
    param(
        [string]$StagingProject,
        [string]$UnityLogPath
    )
    $warmupArgs = @('-batchmode', '-quit', '-projectPath', $StagingProject, '-logFile', $UnityLogPath)
    Write-C04Log "STAGE unity-import-warmup project=$StagingProject"
    $result = Start-C04BoundedProcess -FilePath $UnityExe -Arguments $warmupArgs -TimeoutSeconds $UnityImportTimeoutSeconds -StdOutPath (Join-Path (Split-Path -Parent $UnityLogPath) 'unity-warmup.stdout.log') -StdErrPath (Join-Path (Split-Path -Parent $UnityLogPath) 'unity-warmup.stderr.log') -Label 'unity-import-warmup' -DrainGrace $DrainGraceSeconds -Heartbeat $HeartbeatSeconds
    $script:C04Stages.Add($result) | Out-Null
    if ($result.timedOut) { throw "C04FAIL:UNITY_WARMUP_TIMEOUT:Unity import warmup timed out after $UnityImportTimeoutSeconds seconds" }
    if ($result.cancelled) { throw 'C04FAIL:CANCELLED:Unity import warmup cancelled' }
    if (-not $result.exitCodeKnown) { throw 'C04FAIL:UNKNOWN_EXIT:Unity import warmup native result unknown' }
    if ([int]$result.exitCode -ne 0) { throw "C04FAIL:UNITY_WARMUP:Unity import warmup exited $($result.exitCode)" }
    if (-not (Wait-C04UnityProjectReleased -ProjectPath $StagingProject -TimeoutSeconds 120)) {
        throw 'C04FAIL:UNITY_WARMUP:staged Unity project remained locked after warmup'
    }
    return $result
}

function Invoke-C04UnityBuild {
    param(
        [string]$StagingProject,
        [string]$PlayerPath,
        [string]$ReportPath,
        [string]$UnityLogPath,
        [object]$PluginClosure
    )
    $unityArgs = @(
        '-batchmode', '-quit',
        '-projectPath', $StagingProject,
        '-executeMethod', 'C04PlayerCheatBuild.Build',
        '-c04BuildOutput', $PlayerPath,
        '-c04BuildReport', $ReportPath,
        '-logFile', $UnityLogPath
    )
    Write-C04Log "STAGE unity-player-build project=$StagingProject"
    $result = Start-C04BoundedProcess -FilePath $UnityExe -Arguments $unityArgs -TimeoutSeconds $UnityBuildTimeoutSeconds -StdOutPath (Join-Path (Split-Path -Parent $UnityLogPath) 'unity.stdout.log') -StdErrPath (Join-Path (Split-Path -Parent $UnityLogPath) 'unity.stderr.log') -Label 'unity-player-build' -DrainGrace $DrainGraceSeconds -Heartbeat $HeartbeatSeconds
    $script:C04Stages.Add($result) | Out-Null
    $reportCheck = Test-C04BuildReport -Path $ReportPath -ExpectedOutput $PlayerPath -ExpectedPluginFiles @($PluginClosure.files) -AllowedNonFatalStepErrors $script:C04KnownNonFatalBuildErrors
    $failures = New-Object System.Collections.Generic.List[string]
    if ($result.timedOut) { $failures.Add('UNITY_BUILD_TIMEOUT') | Out-Null }
    elseif ($result.cancelled) { $failures.Add('UNITY_BUILD_CANCELLED') | Out-Null }
    elseif (-not $result.exitCodeKnown) { $failures.Add('UNITY_BUILD_UNKNOWN_EXIT') | Out-Null }
    elseif ([int]$result.exitCode -ne 0) { $failures.Add('UNITY_BUILD_NATIVE_EXIT_' + [int]$result.exitCode) | Out-Null }
    foreach ($reportError in $reportCheck.errors) { $failures.Add($reportError) | Out-Null }
    if ($failures.Count -gt 0) {
        throw ('C04FAIL:BUILD_FAILED:' + ($failures -join ','))
    }
    return [pscustomobject]@{ process = $result; reportCheck = $reportCheck }
}

function Invoke-C04CaseRun {
    param(
        [string]$CaseName,
        [string]$EvidenceRoot,
        [string]$PlayerPath,
        [string]$ExpectedSourceHead,
        [string]$BuildCommit,
        [string]$PackageManifest,
        [string]$Verifier,
        [string]$CurrentSourceIdentitySha256
    )
    $caseDir = Join-Path $EvidenceRoot $CaseName
    New-Item -ItemType Directory -Force -Path $caseDir | Out-Null
    $playerLog = Join-Path $caseDir 'player.log'
    $playerArgs = @(
        '-c04-player-cheats', $caseDir,
        '-c04-player-cheats-case', $CaseName,
        '-c04-source-head', $ExpectedSourceHead,
        '-c04-build-commit', $BuildCommit,
        '-c04-player-path', $PlayerPath,
        '-c04-unity-version', '6000.3.27f1',
        '-screen-width', '1280', '-screen-height', '720',
        '-logFile', $playerLog
    )
    Write-C04Log "STAGE player case=$CaseName"
    $process = Start-C04BoundedProcess -FilePath $PlayerPath -Arguments $playerArgs -TimeoutSeconds $PlayerTimeoutSeconds -StdOutPath (Join-Path $caseDir 'player.stdout.log') -StdErrPath (Join-Path $caseDir 'player.stderr.log') -Label ("player-$CaseName") -DrainGrace $DrainGraceSeconds -Heartbeat $HeartbeatSeconds

    $packageState = Test-C04PackageSeal -PlayerPath $PlayerPath -ExpectedSourceHead $ExpectedSourceHead -ExpectedBuildCommit $BuildCommit -CurrentSourceIdentitySha256 $CurrentSourceIdentitySha256
    $evidencePath = Join-Path $caseDir ('case-' + $CaseName + '.json')
    $structureErrors = @(Get-C04CaseStructureErrors -EvidenceJson $evidencePath)
    $verifierResult = $null
    $errors = New-Object System.Collections.Generic.List[string]
    if ($process.timedOut) { $errors.Add('PLAYER_TIMEOUT') | Out-Null }
    elseif ($process.cancelled) { $errors.Add('PLAYER_CANCELLED') | Out-Null }
    elseif (-not $process.exitCodeKnown) { $errors.Add('PLAYER_UNKNOWN_EXIT') | Out-Null }
    elseif ([int]$process.exitCode -ne 0) { $errors.Add('PLAYER_NATIVE_EXIT_' + [int]$process.exitCode) | Out-Null }
    if (-not $process.drainComplete) { $errors.Add('PLAYER_DRAIN_INCOMPLETE') | Out-Null }
    foreach ($item in $structureErrors) { $errors.Add($item) | Out-Null }
    if (-not $packageState.ok) { foreach ($item in $packageState.errors) { $errors.Add('PACKAGE_AFTER_RUN:' + $item) | Out-Null } }

    if ($errors.Count -eq 0) {
        $verifierResult = Invoke-C04Verifier -Verifier $Verifier -EvidenceJson $evidencePath -CaseName $CaseName -ExpectedSourceHead $ExpectedSourceHead -PackageManifest $PackageManifest -OutPath (Join-Path $caseDir 'verifier.stdout.log') -ErrPath (Join-Path $caseDir 'verifier.stderr.log')
        $script:C04Stages.Add($verifierResult.result) | Out-Null
        if ($verifierResult.status -ne 'PASS') { $errors.Add('VERIFIER_' + $verifierResult.status) | Out-Null }
    }

    $status = if ($errors.Count -eq 0) { 'PASS' } else { 'FAIL' }
    $evidenceBytes = if (Test-Path -LiteralPath $evidencePath) { (Get-Item -LiteralPath $evidencePath).Length } else { 0 }
    $evidenceSha = if (Test-Path -LiteralPath $evidencePath) { Get-C04Sha256 $evidencePath } else { '' }
    $caseResult = [ordered]@{
        caseName = $CaseName
        status = $status
        nativeExit = $process.exitCode
        nativeExitKnown = $process.exitCodeKnown
        timedOut = $process.timedOut
        cancelled = $process.cancelled
        drainComplete = $process.drainComplete
        descendantWriter = $process.descendantWriter
        packageState = if ($packageState.ok) { 'UNCHANGED' } else { 'FAILED' }
        evidencePath = $evidencePath
        evidenceBytes = $evidenceBytes
        evidenceSha256 = $evidenceSha
        verifierStatus = if ($verifierResult) { $verifierResult.status } else { 'NOT_RUN' }
        errors = @($errors)
        process = $process
    }
    Write-C04Json -Path (Join-Path $caseDir 'case-result.json') -Object $caseResult
    return [pscustomobject]$caseResult
}

function Write-C04FinalSummary {
    param(
        [string]$EvidenceRoot,
        [string]$Status,
        [int]$ExitCode,
        [string]$GameStatus,
        [object]$SourceBefore,
        [object]$SourceAfter,
        [bool]$SourceStable,
        [object]$Package,
        [object[]]$Cases,
        [object[]]$Stages,
        [string]$FirstFailureCode,
        [string]$FirstFailureMessage,
        [string[]]$Errors
    )
    $finished = [DateTime]::UtcNow
    $summary = [ordered]@{
        schemaVersion = 1
        task = 'D04-C'
        runnerVersion = $script:C04RunnerVersion
        startedUtc = $script:C04StartedUtc
        finishedUtc = $finished.ToString('O')
        durationSeconds = [math]::Round(($finished - [datetime]$script:C04StartedUtc).TotalSeconds, 3)
        status = $Status
        gameStatus = $GameStatus
        fullD04 = 'NOT_PASSED'
        exitCode = $ExitCode
        sourceHead = $SourceHead
        buildCommit = $BuildCommit
        caseMode = $Case
        skipBuild = [bool]$SkipBuild
        firstFailureCode = $FirstFailureCode
        firstFailureMessage = $FirstFailureMessage
        source = [ordered]@{
            before = $SourceBefore
            after = $SourceAfter
            stable = $SourceStable
        }
        package = $Package
        cases = @($Cases)
        stages = @($Stages)
        errors = @($Errors)
    }
    Write-C04Json -Path (Join-Path $EvidenceRoot 'summary.json') -Object $summary

    $text = New-Object System.Collections.Generic.List[string]
    $text.Add("C04_D04_C_STATUS=$Status") | Out-Null
    $text.Add("GAME_STATUS=$GameStatus") | Out-Null
    $text.Add("FULL_D04=NOT_PASSED") | Out-Null
    $text.Add("SOURCE_HEAD=$SourceHead") | Out-Null
    $text.Add("BUILD_COMMIT=$BuildCommit") | Out-Null
    $text.Add("SOURCE_STABLE=$SourceStable") | Out-Null
    if ($FirstFailureCode) { $text.Add("FIRST_FAILURE=$FirstFailureCode : $FirstFailureMessage") | Out-Null }
    foreach ($stage in @($Stages)) {
        $text.Add(("STAGE {0} exit={1} known={2} timeout={3} cancel={4} drain={5} ownedStopped={6}" -f $stage.label, $stage.exitCode, $stage.exitCodeKnown, $stage.timedOut, $stage.cancelled, $stage.drainComplete, $stage.ownedDescendantsStopped)) | Out-Null
    }
    foreach ($caseResult in @($Cases)) {
        $text.Add(("CASE {0} status={1} native={2} verifier={3} errors={4}" -f $caseResult.caseName, $caseResult.status, $caseResult.nativeExit, $caseResult.verifierStatus, (@($caseResult.errors) -join ','))) | Out-Null
    }
    foreach ($errorItem in @($Errors)) { $text.Add("ERROR $errorItem") | Out-Null }
    Write-C04Text -Path (Join-Path $EvidenceRoot 'summary.txt') -Text (($text -join [Environment]::NewLine) + [Environment]::NewLine)
    return [pscustomobject]@{ summary = $summary; summaryPath = (Join-Path $EvidenceRoot 'summary.json') }
}

# ---------------------------------------------------------------------------
# Self-test hook: functions only, no main flow
# ---------------------------------------------------------------------------

if ($SelfTest) {
    return
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

$script:C04StartedUtc = [DateTime]::UtcNow
$script:C04Stages = New-Object System.Collections.Generic.List[object]
$script:C04Cases = New-Object System.Collections.Generic.List[object]
$script:C04Errors = New-Object System.Collections.Generic.List[string]
$script:C04FirstFailureCode = ''
$script:C04FirstFailureMessage = ''
$script:C04SourceBefore = $null
$script:C04SourceAfter = $null
$script:C04SourceStable = $false
$script:C04Package = $null
$script:C04ExitCode = 2
$script:C04Status = 'FAIL'
$script:C04GameStatus = 'NOT_RUN'
$mutex = $null
$mutexAcquired = $false

try {
    if ([string]::IsNullOrWhiteSpace($SourceHead)) { throw 'C04FAIL:USAGE:C04 requires -SourceHead.' }
    if ($SkipBuild -and [string]::IsNullOrWhiteSpace($PlayerPath)) { throw 'C04FAIL:USAGE:SkipBuild requires -PlayerPath.' }

    if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
        $EvidenceRoot = Join-Path $root ('build\c04-d04c-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    }
    $EvidenceRoot = Get-C04FullPath $EvidenceRoot
    $buildRoot = Get-C04FullPath (Join-Path $root 'build')
    if (-not (Test-C04PathWithin $EvidenceRoot $buildRoot) -or $EvidenceRoot.Equals($buildRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "C04FAIL:EVIDENCE_ROOT_OUTSIDE_BUILD:$EvidenceRoot"
    }
    if ($Case -ne 'build' -and -not $SkipBuild -and [string]::IsNullOrWhiteSpace($PlayerPath)) {
        $PlayerPath = Join-Path $EvidenceRoot 'build\player\Mahjong.exe'
    }
    if (Test-Path -LiteralPath $EvidenceRoot) { throw "C04FAIL:REFUSE_OVERWRITE:evidence root already exists: $EvidenceRoot" }
    New-Item -ItemType Directory -Force -Path $EvidenceRoot | Out-Null
    $script:C04LogPath = Join-Path $EvidenceRoot 'runner.log'
    Write-C04Log ("C04 runner={0} case={1} skipBuild={2} sourceHead={3}" -f $script:C04RunnerVersion, $Case, $SkipBuild, $SourceHead)

    try { [Console]::add_CancelKeyPress({ param($sender, $eventArgs) $eventArgs.Cancel = $true; $script:C04Cancelled = $true }) } catch { }

    $mutex = New-Object System.Threading.Mutex($false, $mutexName)
    $mutexDeadline = (Get-Date).AddSeconds($MutexWaitSeconds)
    Write-C04Log "WAIT mutex=$mutexName max=${MutexWaitSeconds}s"
    while (-not $mutexAcquired) {
        if ($mutex.WaitOne(30000)) { $mutexAcquired = $true; break }
        Write-C04Log ("HEARTBEAT mutex wait remaining={0:N0}s" -f (($mutexDeadline - (Get-Date)).TotalSeconds))
        if ((Get-Date) -ge $mutexDeadline) { throw 'C04FAIL:MUTEX_TIMEOUT:shared Unity validation mutex wait exceeded' }
        if ($script:C04Cancelled) { throw 'C04FAIL:CANCELLED:cancelled while waiting for mutex' }
    }

    $script:C04SourceBefore = Get-C04SourceIdentity
    Write-C04Json -Path (Join-Path $EvidenceRoot 'source-identity-before.json') -Object $script:C04SourceBefore
    $stagingRoot = Join-Path $EvidenceRoot 'staging'
    New-Item -ItemType Directory -Force -Path $stagingRoot | Out-Null

    if ($SkipBuild) {
        Write-C04Log "SKIPBUILD validate sealed package player=$PlayerPath"
        $sealCheck = Test-C04PackageSeal -PlayerPath $PlayerPath -ExpectedSourceHead $SourceHead -ExpectedBuildCommit $BuildCommit -CurrentSourceIdentitySha256 $script:C04SourceBefore.identitySha256
        if (-not $sealCheck.ok) { throw ('C04FAIL:SKIPBUILD_INVALID:' + ($sealCheck.errors -join ',')) }
        $packageRoot = Split-Path -Parent (Get-C04FullPath $PlayerPath)
        $packageArtifacts = Join-Path $EvidenceRoot 'package'
        New-Item -ItemType Directory -Force -Path $packageArtifacts | Out-Null
        foreach ($name in @('c04-package-manifest.csv', 'c04-package-seal.json', 'c04-build-report.json', 'c04-plugin-provenance.json')) {
            $sourceFile = Join-Path $packageRoot $name
            if (Test-Path -LiteralPath $sourceFile) { Copy-Item -LiteralPath $sourceFile -Destination (Join-Path $packageArtifacts $name) -Force }
        }
        $reportPath = Join-Path $packageArtifacts 'c04-build-report.json'
        $reportCheck = Test-C04BuildReport -Path $reportPath -ExpectedOutput (Get-C04FullPath $PlayerPath) -AllowedNonFatalStepErrors $script:C04KnownNonFatalBuildErrors
        if (-not $reportCheck.ok) { throw ('C04FAIL:BUILD_REPORT:' + ($reportCheck.errors -join ',')) }
        $script:C04Package = [ordered]@{
            mode = 'SkipBuild'
            playerPath = (Get-C04FullPath $PlayerPath)
            playerBytes = (Get-Item -LiteralPath $PlayerPath).Length
            playerSha256 = Get-C04Sha256 $PlayerPath
            packageRoot = $packageRoot
            packageManifestSha256 = (Get-C04Sha256 (Join-Path $packageRoot 'c04-package-manifest.csv'))
            packageSealSha256 = (Get-C04Sha256 (Join-Path $packageRoot 'c04-package-seal.json'))
            buildReportSha256 = (Get-C04Sha256 (Join-Path $packageRoot 'c04-build-report.json'))
            pluginProvenanceSha256 = (Get-C04Sha256 (Join-Path $packageRoot 'c04-plugin-provenance.json'))
        }
    }
    else {
        $playerFull = if ([string]::IsNullOrWhiteSpace($PlayerPath)) { Join-Path $EvidenceRoot 'build\player\Mahjong.exe' } else { Get-C04FullPath $PlayerPath }
        if (-not (Test-C04PathWithin $playerFull $EvidenceRoot)) { throw "C04FAIL:PACKAGE_PLAYER_OUTSIDE:$playerFull" }
        $packageRoot = Split-Path -Parent $playerFull
        New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
        $stagingRepo = New-C04StagedRepository -SourceRoot $root -StagingRepo (Join-Path $stagingRoot 'repo')
        Write-C04Log ("STAGE pinned source snapshot files={0} bytes={1}" -f $stagingRepo.manifest.fileCount, $stagingRepo.manifest.bytes)
        $identityAfterCopy = Get-C04SourceIdentity
        if ($identityAfterCopy.identitySha256 -ne $script:C04SourceBefore.identitySha256) {
            throw 'C04FAIL:SOURCE_MUTATED_DURING_STAGING:tracked source changed while staging'
        }

        $stagingBuild = Invoke-C04SharedBuild -StagingRoot $stagingRoot -StagingRepo $stagingRepo.repo
        $stagingProject = $stagingRepo.project
        $stagedInputRows = Get-C04FileTable -Root $stagingProject -ExcludeDirectoryNames @('Library', 'Temp', 'Logs', 'obj', 'UserSettings') -ExcludeFileSuffixes @('.pdb', '.pdb.meta')
        $stagedInputManifest = Write-C04ManifestFile -Path (Join-Path $stagingRoot 'staged-project-input-manifest.csv') -Rows $stagedInputRows -Kind 'c04-staged-project-input'

        $reportFull = Join-Path $packageRoot 'c04-build-report.json'
        $unityLog = Join-Path $stagingRoot 'logs\unity-player-build.log'
        $warmupLog = Join-Path $stagingRoot 'logs\unity-import-warmup.log'
        $warmup = Invoke-C04UnityWarmup -StagingProject $stagingProject -UnityLogPath $warmupLog
        $unityBuild = Invoke-C04UnityBuild -StagingProject $stagingProject -PlayerPath $playerFull -ReportPath $reportFull -UnityLogPath $unityLog -PluginClosure $stagingBuild.closure
        if ($BuildReport) {
            $requestedReport = Get-C04FullPath $BuildReport
            if ($requestedReport -ne $reportFull) {
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $requestedReport) | Out-Null
                Copy-Item -LiteralPath $reportFull -Destination $requestedReport -Force
            }
        }

        $sourceErrors = @(Test-C04ManifestAgainstRoot -Root $stagingRepo.repo -ManifestPath $stagingRepo.manifest.path -ExcludeDirectoryNames @('bin', 'obj', 'Library', 'Temp', 'Logs') -ExcludeRelativeDirectories @('unity/Mahjong.Unity/Assets/Plugins/Mahjong'))
        $stagingErrors = @(Test-C04ManifestAgainstRoot -Root $stagingProject -ManifestPath $stagedInputManifest.path -ExcludeDirectoryNames @('Library', 'Temp', 'Logs', 'obj', 'UserSettings') -ExcludeFileSuffixes @('.pdb', '.pdb.meta'))
        if ($sourceErrors.Count -gt 0 -or $stagingErrors.Count -gt 0) {
            Write-C04Json -Path (Join-Path $stagingRoot 'staging-workspace-diff.json') -Object ([ordered]@{
                source = @($sourceErrors)
                project = @($stagingErrors)
                note = 'Unity may regenerate ProjectSettings, solution/csproj, UserSettings, font import artifacts and test-run JSON in the disposable staging workspace; code files below remain fatal.'
            })
        }
        $fatalSource = @($sourceErrors | Where-Object { $_ -match ':(dotnet|tools|tests)/' })
        if ($fatalSource.Count -gt 0) { throw ('C04FAIL:STAGING_SOURCE_CHANGED:' + ($fatalSource -join ',')) }
        $fatalStaging = @($stagingErrors | Where-Object { $_ -match '\.(cs|asmdef)(:|$)' })
        if ($fatalStaging.Count -gt 0) { throw ('C04FAIL:STAGING_CHANGED_DURING_BUILD:' + ($fatalStaging -join ',')) }

        $identityAfterBuild = Get-C04SourceIdentity
        if ($identityAfterBuild.identitySha256 -ne $script:C04SourceBefore.identitySha256) {
            throw 'C04FAIL:SOURCE_MUTATED_DURING_BUILD:tracked source changed during the build'
        }

        $provenanceCopy = Join-Path $packageRoot 'c04-plugin-provenance.json'
        Copy-Item -LiteralPath $stagingBuild.provenancePath -Destination $provenanceCopy -Force
        $packageSeal = Write-C04PackageSeal -PackageRoot $packageRoot -PlayerPath $playerFull -ReportPath $reportFull -ProvenancePath $provenanceCopy -SourceHead $SourceHead -SourceIdentitySha256 $script:C04SourceBefore.identitySha256 -BuildCommit $BuildCommit -PluginClosure $stagingBuild.closure
        $sealCheck = Test-C04PackageSeal -PlayerPath $playerFull -ExpectedSourceHead $SourceHead -ExpectedBuildCommit $BuildCommit -CurrentSourceIdentitySha256 $script:C04SourceBefore.identitySha256
        if (-not $sealCheck.ok) { throw ('C04FAIL:PACKAGE_SEAL_INVALID:' + ($sealCheck.errors -join ',')) }

        $packageArtifacts = Join-Path $EvidenceRoot 'package'
        New-Item -ItemType Directory -Force -Path $packageArtifacts | Out-Null
        foreach ($name in @('c04-package-manifest.csv', 'c04-package-seal.json', 'c04-build-report.json', 'c04-plugin-provenance.json')) {
            Copy-Item -LiteralPath (Join-Path $packageRoot $name) -Destination (Join-Path $packageArtifacts $name) -Force
        }
        $script:C04Package = [ordered]@{
            mode = 'FreshBuild'
            playerPath = $playerFull
            playerBytes = (Get-Item -LiteralPath $playerFull).Length
            playerSha256 = Get-C04Sha256 $playerFull
            packageRoot = $packageRoot
            packageManifestSha256 = (Get-C04Sha256 (Join-Path $packageRoot 'c04-package-manifest.csv'))
            packageSealSha256 = (Get-C04Sha256 (Join-Path $packageRoot 'c04-package-seal.json'))
            buildReportSha256 = (Get-C04Sha256 $reportFull)
            pluginProvenanceSha256 = (Get-C04Sha256 $provenanceCopy)
            pluginSetSha256 = $stagingBuild.closure.pluginSetSha256
            pluginCount = @($stagingBuild.closure.files).Count
            stagingProject = $stagingProject
            unityLog = $unityLog
            unityLogSha256 = if (Test-Path -LiteralPath $unityLog) { Get-C04Sha256 $unityLog } else { '' }
            buildResult = [string]$unityBuild.reportCheck.report.result
            buildErrors = $unityBuild.reportCheck.report.errors
            buildWarnings = $unityBuild.reportCheck.report.warnings
            buildStepErrorCount = [int]$unityBuild.reportCheck.report.stepErrorCount
            buildStepExceptionCount = [int]$unityBuild.reportCheck.report.stepExceptionCount
            buildKnownNonFatalErrors = @($unityBuild.reportCheck.knownNonFatalStepErrors).Count
            buildUnknownStepErrors = @($unityBuild.reportCheck.unknownStepErrors).Count
            buildErrorClassification = if (@($unityBuild.reportCheck.knownNonFatalStepErrors).Count -gt 0) { 'UNITY_PACKAGE_ASSET_HOST_TYPE_NONFATAL' } else { 'NONE' }
            warmupLog = $warmupLog
            warmupLogSha256 = if (Test-Path -LiteralPath $warmupLog) { Get-C04Sha256 $warmupLog } else { '' }
        }
    }

    $script:C04SourceAfter = Get-C04SourceIdentity
    $script:C04SourceStable = ($script:C04SourceAfter.identitySha256 -eq $script:C04SourceBefore.identitySha256)
    if (-not $script:C04SourceStable) { throw 'C04FAIL:SOURCE_MUTATED:tracked source changed during the run' }
    Write-C04Json -Path (Join-Path $EvidenceRoot 'source-identity-after.json') -Object $script:C04SourceAfter

    if ($Case -eq 'build') {
        $script:C04Status = 'PASS'
        $script:C04GameStatus = 'NOT_RUN'
        $script:C04ExitCode = 0
        Write-C04Log 'BUILD_ONLY component PASS; GAME semantics NOT_RUN'
    }
    else {
        $verifier = Find-C04Verifier
        if (-not $verifier) {
            $script:C04Status = 'BLOCKED'
            $script:C04GameStatus = 'NOT_RUN'
            $script:C04FirstFailureCode = 'A_VERIFIER_NOT_INTEGRATED'
            $script:C04FirstFailureMessage = 'D04-A C04PlayerCheatVerifier2 DLL not found; refusing to use the retired local Strict verifier.'
            $script:C04Errors.Add('A_VERIFIER_NOT_INTEGRATED') | Out-Null
            $script:C04ExitCode = 2
            Write-C04Log 'BLOCKED A verifier not integrated; Player cases were not run'
        }
        else {
            $packageManifest = Join-Path (Split-Path -Parent (Get-C04FullPath $PlayerPath)) 'c04-package-manifest.csv'
            $cases = if ($Case -eq 'all') { @('authorized', 'negative') } else { @($Case) }
            foreach ($caseName in $cases) {
                $caseResult = Invoke-C04CaseRun -CaseName $caseName -EvidenceRoot $EvidenceRoot -PlayerPath (Get-C04FullPath $PlayerPath) -ExpectedSourceHead $SourceHead -BuildCommit $BuildCommit -PackageManifest $packageManifest -Verifier $verifier -CurrentSourceIdentitySha256 $script:C04SourceBefore.identitySha256
                $script:C04Stages.Add($caseResult.process) | Out-Null
                $script:C04Cases.Add($caseResult) | Out-Null
            }
            $failed = @($script:C04Cases | Where-Object { $_.status -ne 'PASS' })
            if ($failed.Count -eq 0) {
                $script:C04Status = 'PASS'
                $script:C04GameStatus = 'PASS'
                $script:C04ExitCode = 0
            }
            else {
                $script:C04Status = 'FAIL'
                $script:C04GameStatus = 'FAIL'
                $script:C04ExitCode = 1
                $script:C04FirstFailureCode = 'CASE_FAILED'
                $script:C04FirstFailureMessage = (@($failed | ForEach-Object { $_.caseName + ':' + (@($_.errors) -join ',') }) -join '; ')
            }
        }
    }
}
catch {
    $message = $_.Exception.Message
    $code = 'UNEXPECTED'
    if ($message -match '^C04FAIL:([A-Za-z0-9_]+):(.*)$') { $code = $Matches[1]; $message = $Matches[2] }
    $script:C04Status = if ($code -eq 'CANCELLED') { 'CANCELLED' } elseif ($code -eq 'MUTEX_TIMEOUT') { 'BLOCKED' } else { 'FAIL' }
    $script:C04FirstFailureCode = $code
    $script:C04FirstFailureMessage = $message
    $script:C04Errors.Add(('{0}: {1}' -f $code, $message)) | Out-Null
    $script:C04ExitCode = Get-C04ExitCodeForFailure $code
    Write-Host ("FAIL {0}: {1}" -f $code, $message)
    if ($script:C04LogPath) { Write-C04Log ("FAIL {0}: {1}" -f $code, $message) }
}
finally {
    if ($script:C04LogPath) {
        try {
            if ($null -eq $script:C04SourceAfter) {
                try { $script:C04SourceAfter = Get-C04SourceIdentity } catch { }
            }
            if ($null -ne $script:C04SourceBefore -and $null -ne $script:C04SourceAfter) {
                $script:C04SourceStable = ($script:C04SourceAfter.identitySha256 -eq $script:C04SourceBefore.identitySha256)
            }
            $summaryResult = Write-C04FinalSummary -EvidenceRoot $EvidenceRoot -Status $script:C04Status -ExitCode $script:C04ExitCode -GameStatus $script:C04GameStatus -SourceBefore $script:C04SourceBefore -SourceAfter $script:C04SourceAfter -SourceStable $script:C04SourceStable -Package $script:C04Package -Cases $script:C04Cases.ToArray() -Stages $script:C04Stages.ToArray() -FirstFailureCode $script:C04FirstFailureCode -FirstFailureMessage $script:C04FirstFailureMessage -Errors $script:C04Errors.ToArray()
            $sealResult = Write-C04EvidenceSeal -EvidenceRoot $EvidenceRoot -Status $script:C04Status -SourceHead $SourceHead
            $sealErrors = @(Test-C04EvidenceSeal -EvidenceRoot $EvidenceRoot)
            if ($sealErrors.Count -gt 0) {
                Write-Host ("SEAL_CHECK_FAILED " + ($sealErrors -join ','))
                if ($script:C04ExitCode -eq 0) { $script:C04ExitCode = 1 }
            }
            Write-Host ("SUMMARY " + $summaryResult.summaryPath)
            Write-Host ("SEAL " + $sealResult.sealPath)
        }
        catch {
            Write-Host ("FINALIZE_FAILED " + $_.Exception.Message)
            if ($script:C04ExitCode -eq 0) { $script:C04ExitCode = 1 }
        }
    }
    if ($mutexAcquired -and $mutex) { try { $mutex.ReleaseMutex() } catch { } }
    if ($mutex) { try { $mutex.Dispose() } catch { } }
}

exit $script:C04ExitCode
