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
$script:C04RunnerVersion = 'c04-d04-c-runner-v3'
# Compatibility-only diagnostic text for older probes/tests. It is never used
# to accept a BuildReport error.
$script:C04KnownNonFatalBuildErrors = @(
    'Host type is not matching any asset type at Path Packages/com.unity.render-pipelines.universal/Shaders/AutodeskInteractive/AutodeskInteractive.shadergraph.',
    'Host type is not matching any asset type at Path Packages/com.unity.render-pipelines.core/Editor/Lighting/ProbeVolume/VirtualOffset/TraceVirtualOffset.urtshader.'
)
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $ProjectPath) { $ProjectPath = Join-Path $root 'unity\Mahjong.Unity' }
$mutexName = 'Local\MahjongV5P45UnityValidation'
$script:C04TempRoot = Join-Path $root ('build\temp\c04-runner-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $script:C04TempRoot | Out-Null
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

function Assert-C04PathSafe {
    param([string]$Path, [string]$Label = 'path')
    $full = (Get-C04FullPath $Path).TrimEnd('\', '/')
    $rootPart = [System.IO.Path]::GetPathRoot($full)
    if ([string]::IsNullOrWhiteSpace($rootPart)) { throw "C04FAIL:PATH_UNSAFE:$Label is not rooted: $Path" }
    $relative = $full.Substring($rootPart.Length)
    $current = $rootPart.TrimEnd('\', '/')
    foreach ($segment in ($relative -split '[\\/]')) {
        if ([string]::IsNullOrWhiteSpace($segment)) { continue }
        $current = Join-Path $current $segment
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "C04FAIL:REPARSE_POINT:$Label contains a reparse point: $current"
            }
        }
    }
    return $full
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
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "C04FAIL:REPARSE_POINT:staging contains a file reparse point: $($item.FullName)"
            }
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

function Initialize-C04JobSupport {
    if ($script:C04JobReady) { return }
    if ($script:C04JobUnavailable) { return }
    $source = @"
using System;
using System.Runtime.InteropServices;
public static class C04ProcessJob {
    [StructLayout(LayoutKind.Sequential)] public struct IO_COUNTERS { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] public struct BASIC_LIMIT { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] public struct EXTENDED_LIMIT { public BASIC_LIMIT BasicLimitInformation; public IO_COUNTERS IoInfo; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [StructLayout(LayoutKind.Sequential)] public struct BASIC_ACCOUNTING { public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime; public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses; }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll")] public static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref EXTENDED_LIMIT info, int length);
    [DllImport("kernel32.dll")] public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] public static extern bool TerminateJobObject(IntPtr job, uint exitCode);
    [DllImport("kernel32.dll")] public static extern bool QueryInformationJobObject(IntPtr job, int infoClass, ref BASIC_ACCOUNTING info, int length, IntPtr returned);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
}
"@
    $tempDir = Join-Path $root ('build\temp\addtype-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $tempDir | Out-Null
    $oldTemp = $env:TEMP; $oldTmp = $env:TMP
    try {
        $env:TEMP = $tempDir; $env:TMP = $tempDir
        Add-Type -TypeDefinition $source -Language CSharp -ErrorAction Stop | Out-Null
        $script:C04JobReady = $true
    }
    catch { $script:C04JobReady = $false; $script:C04JobUnavailable = $true; Write-C04Log ("JOB_SUPPORT_UNAVAILABLE " + $_.Exception.Message) }
    finally { $env:TEMP = $oldTemp; $env:TMP = $oldTmp }
}

function New-C04JobHandle {
    Initialize-C04JobSupport
    if (-not $script:C04JobReady) { return [IntPtr]::Zero }
    try {
    $job = [C04ProcessJob]::CreateJobObject([IntPtr]::Zero, $null)
    if ($job -eq [IntPtr]::Zero) { throw 'C04FAIL:OWNERSHIP_UNKNOWN:CreateJobObject failed' }
    $info = New-Object C04ProcessJob+EXTENDED_LIMIT
    $info.BasicLimitInformation.LimitFlags = 0x2000
    if (-not [C04ProcessJob]::SetInformationJobObject($job, 9, [ref]$info, [Runtime.InteropServices.Marshal]::SizeOf($info))) {
        [void][C04ProcessJob]::CloseHandle($job)
        throw 'C04FAIL:OWNERSHIP_UNKNOWN:SetInformationJobObject failed'
    }
    return $job
    } catch { return [IntPtr]::Zero }
}

function Get-C04JobActiveProcesses {
    param([IntPtr]$Job)
    if ($Job -eq [IntPtr]::Zero) { return 0 }
    $info = New-Object C04ProcessJob+BASIC_ACCOUNTING
    if (-not [C04ProcessJob]::QueryInformationJobObject($Job, 1, [ref]$info, [Runtime.InteropServices.Marshal]::SizeOf($info), [IntPtr]::Zero)) { return -1 }
    return [int]$info.ActiveProcesses
}

function Stop-C04Job {
    param([IntPtr]$Job, [string]$Reason)
    if ($Job -eq [IntPtr]::Zero) { return [pscustomobject]@{ stopped = 0; remaining = 0 } }
    $before = Get-C04JobActiveProcesses $Job
    if ($before -lt 0) {
        $okUnknown = [C04ProcessJob]::TerminateJobObject($Job, 1)
        [void][C04ProcessJob]::CloseHandle($Job)
        Write-C04Log ("STOP_JOB query_unknown terminated={0} reason={1}" -f $okUnknown, $Reason)
        return [pscustomobject]@{ stopped = 0; remaining = -1 }
    }
    $ok = [C04ProcessJob]::TerminateJobObject($Job, 1)
    Start-Sleep -Milliseconds 200
    $after = Get-C04JobActiveProcesses $Job
    [void][C04ProcessJob]::CloseHandle($Job)
    Write-C04Log ("STOP_JOB active_before={0} active_after={1} terminated={2} reason={3}" -f $before, $after, $ok, $Reason)
    $remaining = if (-not $ok -or $after -lt 0) { -1 } elseif ($after -eq 0) { 0 } else { $after }
    return [pscustomobject]@{ stopped = [Math]::Max(0, $before); remaining = $remaining }
}
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
    return @(Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, CreationDate, Name, CommandLine -ErrorAction Stop)
}

function Get-C04ProcessCreationUtc {
    param($Process)
    try { return ([datetime]$Process.CreationDate).ToUniversalTime() }
    catch { return [datetime]::MinValue }
}

function Get-C04OwnedProcessSet {
    param([int]$RootProcessId, [hashtable]$Observed, [datetime]$SinceUtc = [datetime]::MinValue)
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
    if ($Observed) { foreach ($key in $Observed.Keys) { $queue.Enqueue([int]$key) } }
    while ($queue.Count -gt 0) {
        $current = [int]$queue.Dequeue()
        if ($seen.ContainsKey($current)) { continue }
        $seen[$current] = $true
        if (-not $byParent.ContainsKey($current)) { continue }
        foreach ($child in $byParent[$current]) {
            $pidValue = [int]$child.ProcessId
            if ($pidValue -eq $PID -or $seen.ContainsKey($pidValue)) { continue }
            $childCreated = Get-C04ProcessCreationUtc $child
            if ($SinceUtc -ne [datetime]::MinValue -and $childCreated -ne [datetime]::MinValue -and $childCreated -lt $SinceUtc.AddSeconds(-5)) { continue }
            $result.Add($child) | Out-Null
            $queue.Enqueue($pidValue)
        }
    }
    return $result.ToArray()
}

function Stop-C04ExactProcess {
    param(
        [int]$ProcessId,
        [datetime]$CreationTimeUtc,
        [string]$Reason
    )
    if ($ProcessId -le 0 -or $ProcessId -eq $PID) { return $false }
    if ($CreationTimeUtc -eq [datetime]::MinValue) {
        throw "C04FAIL:OWNERSHIP_UNKNOWN:no exact start time for pid=$ProcessId; refusing to stop"
    }
    try {
        $current = @(Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessId" -ErrorAction Stop)
        if ($current.Count -ne 1) { return $true }
        $actual = Get-C04ProcessCreationUtc $current[0]
        if ($actual.Ticks -ne $CreationTimeUtc.Ticks) { return $false }
        Stop-Process -Id $ProcessId -Force -ErrorAction Stop
        Write-C04Log ("STOP_OWNED pid={0} created={1} reason={2}" -f $ProcessId, $CreationTimeUtc.ToString('O'), $Reason)
        return $true
    }
    catch {
        return $false
    }
}

function Stop-C04OwnedSet {
    param(
        [int]$RootProcessId,
        [hashtable]$Observed,
        [string]$Reason,
        [bool]$IncludeRoot = $false,
        [datetime]$RootCreationUtc = [datetime]::MinValue,
        [datetime]$SinceUtc = [datetime]::MinValue
    )
    $stopped = 0
    for ($pass = 0; $pass -lt 5; $pass++) {
        $had = $false
        if ($IncludeRoot -and $RootProcessId -gt 0) {
            $rootAlive = @(Get-CimInstance Win32_Process -Filter "ProcessId=$RootProcessId" -ErrorAction SilentlyContinue)
            if ($rootAlive.Count -eq 1) {
                $had = $true
                if (Stop-C04ExactProcess -ProcessId $RootProcessId -CreationTimeUtc $RootCreationUtc -Reason $Reason) { $stopped++ }
            }
        }
        $set = @(Get-C04OwnedProcessSet -RootProcessId $RootProcessId -Observed $Observed -SinceUtc $SinceUtc)
        foreach ($process in $set) {
            $had = $true
            $created = Get-C04ProcessCreationUtc $process
            if ($Observed -and $Observed.ContainsKey("$([int]$process.ProcessId)")) { $created = [datetime]$Observed["$([int]$process.ProcessId)"] }
            if (Stop-C04ExactProcess -ProcessId ([int]$process.ProcessId) -CreationTimeUtc $created -Reason $Reason) { $stopped++ }
        }
        if (-not $had) { break }
        Start-Sleep -Milliseconds 100
    }
    $remaining = @(Get-C04OwnedProcessSet -RootProcessId $RootProcessId -Observed $Observed -SinceUtc $SinceUtc)
    return [pscustomobject]@{ stopped = $stopped; remaining = $remaining.Count }
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

    if ($TimeoutSeconds -le 0) { throw "C04FAIL:USAGE:$Label timeout must be positive" }
    if ($DrainGrace -lt 0 -or $Heartbeat -lt 0) { throw "C04FAIL:USAGE:$Label drain/heartbeat must not be negative" }
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
    $launchDir = Join-Path $script:C04TempRoot ("launch-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $launchDir | Out-Null
    $specPath = Join-Path $launchDir 'spec.json'
    $signalPath = Join-Path $launchDir 'go.signal'
    $wrapperPath = Join-Path $launchDir 'launch.ps1'
    $targetResultPath = Join-Path $launchDir 'target-result.json'
    $spec = [ordered]@{ file = $FilePath; argline = $argumentString; cwd = $WorkingDirectory; result = $targetResultPath }
    Write-C04Json -Path $specPath -Object $spec
    $wrapperLines = @(
        'param([string]$Spec,[string]$Signal)',
        '$ErrorActionPreference=''Stop''',
        'while (-not (Test-Path -LiteralPath $Signal)) { Start-Sleep -Milliseconds 25 }',
        '$s = Get-Content -LiteralPath $Spec -Raw | ConvertFrom-Json',
        'try {',
        '  $psi = New-Object System.Diagnostics.ProcessStartInfo',
        '  $psi.FileName = [string]$s.file',
        '  $psi.Arguments = [string]$s.argline',
        '  $psi.UseShellExecute = $false',
        '  if ($s.cwd) { $psi.WorkingDirectory = [string]$s.cwd }',
        '  $p = [System.Diagnostics.Process]::Start($psi)',
        '  if ($null -eq $p) { throw ''TARGET_START_NULL'' }',
        '  $targetPid = $p.Id',
        '  $p.WaitForExit()',
        '  $targetExit = $p.ExitCode',
        '  $result = [ordered]@{ status = ''EXITED''; wrapperPid = $PID; targetPid = $targetPid; exitCode = $targetExit }',
        '  [System.IO.File]::WriteAllText([string]$s.result, ($result | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))',
        '  exit $targetExit',
        '} catch {',
        '  $result = [ordered]@{ status = ''START_FAILED''; wrapperPid = $PID; targetPid = 0; exitCode = $null; error = $_.Exception.ToString() }',
        '  [System.IO.File]::WriteAllText([string]$s.result, ($result | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))',
        '  exit 90',
        '}'
    )
    [System.IO.File]::WriteAllLines($wrapperPath, $wrapperLines, (New-Object System.Text.UTF8Encoding($false)))

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = (Get-Command powershell.exe).Source
    $psi.Arguments = (@('-NoProfile','-ExecutionPolicy','Bypass','-File',$wrapperPath,'-Spec',$specPath,'-Signal',$signalPath) | ForEach-Object { ConvertTo-C04QuotedArgument ([string]$_) }) -join ' '
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $psi
    $outStream = $null
    $errStream = $null
    $outTask = $null
    $errTask = $null
    $startedAt = Get-Date
    $startTimeUtc = [datetime]::MinValue
    $pidValue = 0
    $processStarted = $false
    $ownershipKnown = $false
    $timedOut = $false
    $cancelled = $false
    $drained = $false
    $descendantWriter = $false
    $ownedStopped = 0
    $cleanupFailed = $false
    $caught = $null
    $observed = @{}
    $jobHandle = [IntPtr]::Zero
    $jobClosed = $false
    $jobRemaining = 0
    try {
        $jobHandle = New-C04JobHandle
        if ($jobHandle -eq [IntPtr]::Zero) { throw "C04FAIL:OWNERSHIP_UNKNOWN:$Label job containment unavailable" }
        $outStream = [System.IO.File]::Open($StdOutPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)
        $errStream = [System.IO.File]::Open($StdErrPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)
        if (-not $process.Start()) { throw "C04FAIL:PROCESS_START:$Label" }
        $processStarted = $true
        $pidValue = $process.Id
        $current = @(Get-CimInstance Win32_Process -Filter "ProcessId=$pidValue" -ErrorAction SilentlyContinue)
        if ($current.Count -eq 1) { $startTimeUtc = Get-C04ProcessCreationUtc $current[0] }
        if ($startTimeUtc -eq [datetime]::MinValue) { try { $startTimeUtc = $process.StartTime.ToUniversalTime() } catch { } }
        if ($startTimeUtc -eq [datetime]::MinValue) { throw "C04FAIL:OWNERSHIP_UNKNOWN:$Label pid=$pidValue has no exact start identity" }
        $ownershipKnown = $true
        if (-not [C04ProcessJob]::AssignProcessToJobObject($jobHandle, $process.Handle)) {
            [void][C04ProcessJob]::CloseHandle($jobHandle); $jobHandle = [IntPtr]::Zero
            throw "C04FAIL:OWNERSHIP_UNKNOWN:$Label could not assign wrapper to job"
        }
        Write-C04Log ("JOB {0} assigned pid={1} active={2}" -f $Label, $pidValue, (Get-C04JobActiveProcesses $jobHandle))
        [System.IO.File]::WriteAllText($signalPath, 'go')
        $outTask = $process.StandardOutput.BaseStream.CopyToAsync($outStream)
        $errTask = $process.StandardError.BaseStream.CopyToAsync($errStream)
        Write-C04Log ("START {0} pid={1} start={2} timeout={3}s file={4}" -f $Label, $pidValue, $startTimeUtc.ToString('O'), $TimeoutSeconds, $FilePath)
        $lastHeartbeat = Get-Date
        $nextDescendantScan = (Get-Date).AddMilliseconds(250)
        while (-not $process.HasExited) {
            $elapsed = ((Get-Date) - $startedAt).TotalSeconds
            if ($elapsed -ge $TimeoutSeconds) { $timedOut = $true; break }
            if ($CancelProvider) {
                try { if (& $CancelProvider) { $cancelled = $true; break } } catch { }
            }
            if ($script:C04Cancelled) { $cancelled = $true; break }
            if ($CancelFilePath -and (Test-Path -LiteralPath $CancelFilePath)) { $cancelled = $true; break }
            if ($script:C04CancelFile -and (Test-Path -LiteralPath $script:C04CancelFile)) { $cancelled = $true; break }
            if ((Get-Date) -ge $nextDescendantScan) {
                foreach ($child in @(Get-C04OwnedProcessSet -RootProcessId $pidValue -Observed $observed -SinceUtc $startTimeUtc)) {
                    $childPid = [int]$child.ProcessId
                    if (-not $observed.ContainsKey("$childPid")) {
                        $observed["$childPid"] = Get-C04ProcessCreationUtc $child
                    }
                }
                $nextDescendantScan = (Get-Date).AddMilliseconds(250)
            }
            if ($Heartbeat -gt 0 -and ((Get-Date) - $lastHeartbeat).TotalSeconds -ge $Heartbeat) {
                Write-C04Log ("HEARTBEAT {0} pid={1} elapsed={2:N1}s" -f $Label, $pidValue, $elapsed)
                $lastHeartbeat = Get-Date
            }
            Start-Sleep -Milliseconds 100
        }

        $beforeCleanup = @(Get-C04OwnedProcessSet -RootProcessId $pidValue -Observed $observed -SinceUtc $startTimeUtc)
        if ($beforeCleanup.Count -gt 0) { $descendantWriter = $true }
        $cleanupReason = if ($timedOut) { 'TIMEOUT' } elseif ($cancelled) { 'CANCEL' } else { 'AFTER_ROOT_EXIT' }
        if (-not $process.HasExited -and $ownershipKnown) {
            [void](Stop-C04ExactProcess -ProcessId $pidValue -CreationTimeUtc $startTimeUtc -Reason $cleanupReason)
            try { [void]$process.WaitForExit(5000) } catch { }
        }
        $activeBefore = Get-C04JobActiveProcesses $jobHandle
        if ($activeBefore -gt 0) { $descendantWriter = $true }
        $cleanup = Stop-C04Job -Job $jobHandle -Reason $cleanupReason
        $jobClosed = $true
        $jobHandle = [IntPtr]::Zero
        $ownedStopped += $cleanup.stopped
        $jobRemaining = $cleanup.remaining
        $fallbackSet = Stop-C04OwnedSet -RootProcessId $pidValue -Observed $observed -Reason $cleanupReason -IncludeRoot $false -SinceUtc $startTimeUtc
        $ownedStopped += $fallbackSet.stopped
        if ($fallbackSet.remaining -ne 0) { $cleanupFailed = $true }
        if ($cleanup.remaining -ne 0) { $cleanupFailed = $true }
        try { if (-not $process.HasExited) { [void]$process.WaitForExit(5000) } } catch { }

        $waitMs = [Math]::Max(1000, $DrainGrace * 1000)
        $drained = Wait-C04StreamTasks -Tasks ([System.Threading.Tasks.Task[]]@($outTask, $errTask)) -Milliseconds $waitMs
        if (-not $drained) {
            $descendantWriter = $true
            $cleanup2 = if (-not $jobClosed) { Stop-C04Job -Job $jobHandle -Reason 'UNRESOLVED_WRITER' } else { [pscustomobject]@{ stopped = 0; remaining = 0 } }
            $jobClosed = $true
            $jobHandle = [IntPtr]::Zero
            $ownedStopped += $cleanup2.stopped
            if ($cleanup2.remaining -ne 0) { $cleanupFailed = $true }
            $drained = Wait-C04StreamTasks -Tasks ([System.Threading.Tasks.Task[]]@($outTask, $errTask)) -Milliseconds $waitMs
        }
    }
    catch {
        $caught = $_
    }
    finally {
        if (-not $process.HasExited -and $ownershipKnown) {
            try { [void](Stop-C04ExactProcess -ProcessId $pidValue -CreationTimeUtc $startTimeUtc -Reason 'EXCEPTION_OR_FINAL') } catch { $cleanupFailed = $true }
        }
        if (-not $jobClosed -and $jobHandle -ne [IntPtr]::Zero) {
            try {
                $cleanup3 = Stop-C04Job -Job $jobHandle -Reason 'EXCEPTION_OR_FINAL'
                $ownedStopped += $cleanup3.stopped
                $jobRemaining = $cleanup3.remaining
                $fallback3 = Stop-C04OwnedSet -RootProcessId $pidValue -Observed $observed -Reason 'EXCEPTION_OR_FINAL' -IncludeRoot $false -SinceUtc $startTimeUtc
                $ownedStopped += $fallback3.stopped
                if ($fallback3.remaining -ne 0) { $cleanupFailed = $true }
                if ($cleanup3.remaining -ne 0) { $cleanupFailed = $true }
            }
            catch { $cleanupFailed = $true }
            $jobClosed = $true
            $jobHandle = [IntPtr]::Zero
        }
        try { if ($outTask) { [void]$outTask.Wait(2000) } } catch { }
        try { if ($errTask) { [void]$errTask.Wait(2000) } } catch { }
        try { if ($outStream) { $outStream.Flush(); $outStream.Dispose() } } catch { }
        try { if ($errStream) { $errStream.Flush(); $errStream.Dispose() } } catch { }
    }

    $remainingObserved = if ($jobRemaining -ne 0) { $jobRemaining } else { @(Get-C04OwnedProcessSet -RootProcessId $pidValue -Observed $observed -SinceUtc $startTimeUtc).Count }
    if ($remainingObserved -ne 0) { $cleanupFailed = $true }
    $targetStartFailed = $false
    $targetPid = 0
    $targetExit = $null
    if (Test-Path -LiteralPath $targetResultPath -PathType Leaf) {
        try {
            $targetResult = Get-Content -LiteralPath $targetResultPath -Raw | ConvertFrom-Json
            $targetPid = [int]$targetResult.targetPid
            if ([string]$targetResult.status -ne 'EXITED') { $targetStartFailed = $true }
            elseif ($null -ne $targetResult.exitCode) { $targetExit = [int]$targetResult.exitCode }
            else { $targetStartFailed = $true }
        }
        catch { $targetStartFailed = $true }
    }
    elseif (-not $timedOut -and -not $cancelled) { $targetStartFailed = $true }
    $rawExit = $targetExit
    $exitKnown = (-not ($timedOut -or $cancelled)) -and (-not $targetStartFailed) -and ($null -ne $targetExit) -and ($cleanupFailed -eq $false) -and ($remainingObserved -eq 0)
    try { $process.Dispose() } catch { }

    $result = [pscustomobject]@{
        label = $Label
        file = $FilePath
        arguments = @($Arguments)
        commandLine = $argumentString
        pid = $pidValue
        startTimeUtc = $startTimeUtc.ToString('O')
        ownershipKnown = $ownershipKnown
        wrapperPid = $pidValue
        targetPid = $targetPid
        targetStartFailed = $targetStartFailed
        exitCode = if ($exitKnown) { $rawExit } else { $null }
        exitCodeKnown = $exitKnown
        rawExitCode = $rawExit
        timedOut = $timedOut
        cancelled = $cancelled
        drainComplete = [bool]$drained
        descendantWriter = $descendantWriter
        ownedDescendantsStopped = $ownedStopped
        ownedProcessesRemaining = $remainingObserved
        cleanupFailed = $cleanupFailed
        durationSeconds = [math]::Round(((Get-Date) - $startedAt).TotalSeconds, 3)
        stdoutPath = $StdOutPath
        stderrPath = $StdErrPath
        stdoutBytes = if (Test-Path -LiteralPath $StdOutPath) { (Get-Item -LiteralPath $StdOutPath).Length } else { 0 }
        stderrBytes = if (Test-Path -LiteralPath $StdErrPath) { (Get-Item -LiteralPath $StdErrPath).Length } else { 0 }
    }
    Write-C04Log ("END {0} pid={1} exit={2} known={3} timeout={4} cancel={5} drained={6} ownedStopped={7} remaining={8} cleanupFailed={9} duration={10:N2}s" -f $Label, $pidValue, $result.exitCode, $exitKnown, $timedOut, $cancelled, $drained, $ownedStopped, $remainingObserved, $cleanupFailed, $result.durationSeconds)
    if ($caught) { throw $caught }
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
    if ($result.targetStartFailed) { throw "C04FAIL:TARGET_START_FAILED:$Label target did not start or produce a native result" }
    if (-not $result.exitCodeKnown) { throw "C04FAIL:UNKNOWN_EXIT:$Label native result unknown (pid=$($result.pid))" }
    if ($result.targetStartFailed) { throw 'C04FAIL:TARGET_START_FAILED:result target did not start or produce a native result' }
    if (-not $result.drainComplete) { throw "C04FAIL:DRAIN_INCOMPLETE:$Label output streams were not drained" }
    if ($result.targetStartFailed) { throw "C04FAIL:TARGET_START_FAILED:$Label target did not start or produce a native result" }
    if ($result.ownedProcessesRemaining -ne 0 -or $result.cleanupFailed) { throw "C04FAIL:OWNED_WRITER_REMAINS:$Label owned writers remain (pid=$($result.pid))" }
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

function Get-C04SourceInputRows {
    param([Parameter(Mandatory = $true)][string]$RepoRoot)
    $repoFull = (Get-C04FullPath $RepoRoot).TrimEnd('\', '/')
    $rows = New-Object System.Collections.Generic.List[object]
    $addDirectory = {
        param([string]$Relative, [string[]]$ExcludeNames = @(), [string[]]$ExcludeRelative = @())
        $dir = Join-Path $repoFull ($Relative.Replace('/', '\'))
        if (-not (Test-Path -LiteralPath $dir -PathType Container)) { return }
        foreach ($row in @(Get-C04FileTable -Root $dir -ExcludeDirectoryNames $ExcludeNames -ExcludeRelativeDirectories $ExcludeRelative)) {
            $rows.Add([pscustomobject]@{ path = ($Relative.TrimEnd('/') + '/' + [string]$row.path); bytes = [long]$row.bytes; sha256 = [string]$row.sha256 }) | Out-Null
        }
    }
    & $addDirectory 'dotnet' @('bin', 'obj')
    & $addDirectory 'tests' @('bin', 'obj')
    & $addDirectory 'unity/Mahjong.Unity' @('Library', 'Temp', 'Logs', 'obj', 'UserSettings') @('Assets/Plugins/Mahjong')
    foreach ($tool in @('build-shared.ps1', 'check-shared-plugins.ps1', 'shared-plugin-provenance-lib.ps1', 'run-p45-c04-player-cheats.ps1')) {
        $path = Join-Path $repoFull ('tools\unity\' + $tool)
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $rows.Add([pscustomobject]@{ path = 'tools/unity/' + $tool; bytes = [long](Get-Item -LiteralPath $path).Length; sha256 = (Get-C04Sha256 $path) }) | Out-Null
        }
    }
    foreach ($rootFile in @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'Directory.Build.rsp', 'NuGet.config', 'global.json')) {
        $path = Join-Path $repoFull $rootFile
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $rows.Add([pscustomobject]@{ path = $rootFile; bytes = [long](Get-Item -LiteralPath $path).Length; sha256 = (Get-C04Sha256 $path) }) | Out-Null
        }
    }
    return @($rows | Sort-Object -Property path)
}

function Get-C04SourceIdentity {
    param([string]$RepoRoot = $root)
    Assert-C04PathSafe -Path $RepoRoot -Label 'source-root'
    $head = ((& git -C $RepoRoot rev-parse HEAD) -join '').Trim()
    if ($LASTEXITCODE -ne 0) { throw 'C04FAIL:GIT:HEAD unreadable' }
    $rows = @(Get-C04SourceInputRows -RepoRoot $RepoRoot)
    if ($rows.Count -eq 0) { throw 'C04FAIL:SOURCE_EMPTY:no build input files found' }
    $canonical = New-Object System.Text.StringBuilder
    foreach ($row in $rows) {
        $canonical.Append(('{0}|{1}|{2}' -f $row.path, $row.bytes, $row.sha256) + "`n") | Out-Null
    }
    return [pscustomobject]@{
        head = $head
        inputFileCount = $rows.Count
        inputBytes = [long](@($rows | Measure-Object -Property bytes -Sum).Sum)
        inputRows = $rows
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
    foreach ($tool in @('build-shared.ps1', 'check-shared-plugins.ps1', 'shared-plugin-provenance-lib.ps1', 'run-p45-c04-player-cheats.ps1')) {
        $toolSource = Join-Path $SourceRoot ('tools\unity\' + $tool)
        if (Test-Path -LiteralPath $toolSource -PathType Leaf) { Copy-Item -LiteralPath $toolSource -Destination (Join-Path $toolsTarget $tool) -Force }
    }
    foreach ($rootFile in @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'Directory.Build.rsp', 'NuGet.config', 'global.json')) {
        $sourceFile = Join-Path $SourceRoot $rootFile
        if (Test-Path -LiteralPath $sourceFile -PathType Leaf) { Copy-Item -LiteralPath $sourceFile -Destination (Join-Path $StagingRepo $rootFile) -Force }
    }
    Assert-C04NoReparsePoint -Path $StagingRepo -Label 'staging-repository'
    $sourceRows = @(Get-C04SourceInputRows -RepoRoot $SourceRoot)
    $stagedRows = @(Get-C04SourceInputRows -RepoRoot $StagingRepo)
    $sourceMap = @{}
    foreach ($row in $sourceRows) { $sourceMap[[string]$row.path] = $row }
    $stagedMap = @{}
    foreach ($row in $stagedRows) { $stagedMap[[string]$row.path] = $row }
    $mismatches = New-Object System.Collections.Generic.List[string]
    foreach ($key in $sourceMap.Keys) {
        if (-not $stagedMap.ContainsKey($key)) { $mismatches.Add('SOURCE_COPY_MISSING:' + $key) | Out-Null; continue }
        if ([long]$sourceMap[$key].bytes -ne [long]$stagedMap[$key].bytes -or
            [string]$sourceMap[$key].sha256 -ne [string]$stagedMap[$key].sha256) {
            $mismatches.Add('SOURCE_COPY_CHANGED:' + $key) | Out-Null
        }
    }
    foreach ($key in $stagedMap.Keys) {
        if (-not $sourceMap.ContainsKey($key)) { $mismatches.Add('SOURCE_COPY_EXTRA:' + $key) | Out-Null }
    }
    if ($mismatches.Count -gt 0) { throw ('C04FAIL:STAGING_COPY_MISMATCH:' + ($mismatches -join ',')) }
    $manifestPath = Join-Path (Split-Path -Parent $StagingRepo) 'staged-source-manifest.csv'
    $manifest = Write-C04ManifestFile -Path $manifestPath -Rows $stagedRows -Kind 'c04-pinned-source-snapshot'
    return [pscustomobject]@{ repo = $StagingRepo; manifest = $manifest; rows = $stagedRows; project = (Join-Path $StagingRepo 'unity\Mahjong.Unity'); sourceIdentity = (Get-C04TextSha256 ((@($stagedRows | ForEach-Object { '{0}|{1}|{2}' -f $_.path, $_.bytes, $_.sha256 })) -join "`n")) }
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
    # Compatibility-only parameter: no allowlist can excuse a BuildReport error.
    $errors = New-Object System.Collections.Generic.List[string]
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        $errors.Add('BUILD_REPORT_MISSING') | Out-Null
        return [pscustomobject]@{ ok = $false; errors = @($errors); report = $null; stepErrors = @(); stepCount = 0 }
    }
    try { $report = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
    catch {
        $errors.Add('BUILD_REPORT_MALFORMED') | Out-Null
        return [pscustomobject]@{ ok = $false; errors = @($errors); report = $null; stepErrors = @(); stepCount = 0 }
    }

    if ([int]$report.schema -ne 1) { $errors.Add('BUILD_REPORT_SCHEMA') | Out-Null }
    if ([string]$report.result -ne 'Succeeded') { $errors.Add('BUILD_REPORT_RESULT:' + [string]$report.result) | Out-Null }
    if ([string]$report.target -ne 'StandaloneWindows64') { $errors.Add('BUILD_REPORT_TARGET') | Out-Null }
    if (-not [string]::IsNullOrWhiteSpace([string]$report.exception)) { $errors.Add('BUILD_REPORT_EXCEPTION_PRESENT') | Out-Null }
    if ($null -eq $report.errors -or $report.errors -is [bool] -or $report.errors -is [string]) {
        $errors.Add('BUILD_REPORT_ERRORS_NOT_NUMERIC') | Out-Null
        $errorCount = -1
    }
    else {
        $errorCount = [int]$report.errors
        if ($errorCount -ne 0) { $errors.Add('BUILD_REPORT_ERRORS:' + $errorCount) | Out-Null }
    }

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
                $errors.Add('BUILD_REPORT_STEP_ERROR:' + $detail) | Out-Null
            }
        }
    }
    if ($stepCount -eq 0) { $errors.Add('BUILD_REPORT_NO_STEPS') | Out-Null }
    if ($errorCount -gt 0 -and $stepErrors.Count -eq 0) { $errors.Add('BUILD_REPORT_UNATTRIBUTED_ERRORS') | Out-Null }
    if ($errorCount -gt 0 -and $stepErrors.Count -gt 0 -and $errorCount -ne $stepErrors.Count) {
        $errors.Add('BUILD_REPORT_ATTRIBUTION_COUNT_MISMATCH:' + $errorCount + '!=' + $stepErrors.Count) | Out-Null
    }

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
        if ($null -eq $report.bytes -or $report.bytes -is [bool] -or $report.bytes -is [string]) {
            $errors.Add('BUILD_REPORT_BYTES_MISSING') | Out-Null
        }
        elseif ([long]$report.bytes -ne [long]$bytes) { $errors.Add('BUILD_OUTPUT_SIZE_MISMATCH') | Out-Null }
        if ([string]::IsNullOrWhiteSpace([string]$report.outputSha256)) {
            $errors.Add('BUILD_REPORT_OUTPUT_SHA_MISSING') | Out-Null
        }
        elseif ([string]$report.outputSha256 -notmatch '^[0-9a-fA-F]{64}$') { $errors.Add('BUILD_REPORT_OUTPUT_SHA_MALFORMED') | Out-Null }
        elseif ([string]$report.outputSha256.ToLowerInvariant() -ne $sha) { $errors.Add('BUILD_OUTPUT_HASH_MISMATCH') | Out-Null }
    }

    foreach ($scene in @('Assets/Scenes/Boot.unity', 'Assets/Scenes/Menu.unity', 'Assets/Scenes/Lobby.unity', 'Assets/Scenes/Table.unity', 'Assets/Scenes/Spectator.unity', 'Assets/Scenes/Settlement.unity')) {
        if (@($report.scenes) -notcontains $scene) { $errors.Add('BUILD_SCENE_MISSING:' + $scene) | Out-Null }
    }

    $plugins = @($report.plugins)
    if ($plugins.Count -eq 0) { $errors.Add('BUILD_REPORT_PLUGINS_EMPTY') | Out-Null }
    if ($ExpectedPluginFiles.Count -gt 0) {
        $reported = @{}
        foreach ($plugin in $plugins) { $reported[[string]$plugin.name] = $plugin }
        foreach ($file in @($ExpectedPluginFiles | Where-Object { ([string]$_.name).EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase) })) {
            if (-not $reported.ContainsKey([string]$file.name)) { $errors.Add('BUILD_REPORT_PLUGIN_MISSING:' + [string]$file.name) | Out-Null }
            elseif ([string]$reported[[string]$file.name].sha256.ToLowerInvariant() -ne ([string]$file.sha256).ToLowerInvariant()) { $errors.Add('BUILD_REPORT_PLUGIN_HASH:' + [string]$file.name) | Out-Null }
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
        report = $report
        stepErrors = @($stepErrors)
        stepCount = $stepCount
    }
}

# ---------------------------------------------------------------------------
# Package manifest, package seal, evidence seal
# ---------------------------------------------------------------------------

function Get-C04PackageRows {
    param([string]$PackageRoot)
    return @(Get-C04FileTable -Root $PackageRoot -ExcludeRelativeFiles @('c04-package-manifest.csv', 'c04-package-seal.json', 'c04-build-receipt.json'))
}

function Resolve-C04RelativeMember {
    param([string]$Root, [string]$Relative, [string]$Label)
    if ([string]::IsNullOrWhiteSpace($Relative)) { throw "C04FAIL:PACKAGE_MEMBER_PATH:$Label is empty" }
    if ([System.IO.Path]::IsPathRooted($Relative)) { throw "C04FAIL:PACKAGE_MEMBER_PATH:$Label is absolute" }
    $parts = $Relative.Replace('\', '/').Split('/')
    if ($parts -contains '..') { throw "C04FAIL:PACKAGE_MEMBER_PATH:$Label traverses upward" }
    $full = Get-C04FullPath (Join-Path $Root ($Relative.Replace('/', '\')))
    if (-not (Test-C04PathWithin $full $Root)) { throw "C04FAIL:PACKAGE_MEMBER_PATH:$Label escapes package root" }
    [void](Assert-C04PathSafe -Path $full -Label $Label)
    return $full
}

function Write-C04PackageSeal {
    param(
        [string]$PackageRoot,
        [string]$PlayerPath,
        [string]$ReportPath,
        [string]$ProvenancePath,
        [string]$PluginClosurePath,
        [string]$BuildEvidenceRoot,
        [string]$SourceHead,
        [string]$SourceIdentitySha256,
        [string]$SourceManifestSha256,
        [string]$StagedInputManifestSha256,
        [string]$StagedFinalManifestSha256,
        [string]$StagedCodeManifestSha256,
        [string]$BuildCommit,
        [string]$BuildId = 'unknown',
        [int]$SourceInputFileCount = 0,
        [long]$SourceInputBytes = 0,
        [int]$StagedInputFileCount = 0,
        [object]$PluginClosure
    )
    $packageRootFull = (Assert-C04PathSafe -Path $PackageRoot -Label 'package-root').TrimEnd('\', '/')
    $manifestPath = Join-Path $packageRootFull 'c04-package-manifest.csv'
    $sealPath = Join-Path $packageRootFull 'c04-package-seal.json'
    $receiptPath = Join-Path $packageRootFull 'c04-build-receipt.json'
    $playerFull = (Assert-C04PathSafe -Path $PlayerPath -Label 'player').TrimEnd('\', '/')
    if (-not (Test-C04PathWithin $playerFull $packageRootFull)) { throw 'C04FAIL:PACKAGE_PLAYER_OUTSIDE' }
    $playerRelative = $playerFull.Substring($packageRootFull.Length).TrimStart('\', '/').Replace('\', '/')
    $reportFull = (Assert-C04PathSafe -Path $ReportPath -Label 'build-report').TrimEnd('\', '/')
    $provenanceFull = (Assert-C04PathSafe -Path $ProvenancePath -Label 'plugin-provenance').TrimEnd('\', '/')
    if ([string]::IsNullOrWhiteSpace($PluginClosurePath)) {
        $PluginClosurePath = Join-Path $packageRootFull 'c04-plugin-closure.csv'
        if (-not (Test-Path -LiteralPath $PluginClosurePath)) {
            $closureRows = @($PluginClosure.files | ForEach-Object { [pscustomobject]@{ path = $_.name; bytes = $_.bytes; sha256 = $_.sha256 } })
            $null = Write-C04ManifestFile -Path $PluginClosurePath -Rows $closureRows -Kind 'c04-plugin-closure'
        }
    }
    $closureFull = (Assert-C04PathSafe -Path $PluginClosurePath -Label 'plugin-closure').TrimEnd('\', '/')
    foreach ($required in @($playerFull, $reportFull, $provenanceFull, $closureFull)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "C04FAIL:PACKAGE_REQUIRED_MISSING:$required" }
        if (-not (Test-C04PathWithin $required $packageRootFull)) { throw "C04FAIL:PACKAGE_MEMBER_OUTSIDE:$required" }
    }
    if ([string]::IsNullOrWhiteSpace($BuildEvidenceRoot)) { throw 'C04FAIL:PACKAGE_BUILD_EVIDENCE_MISSING' }
    $buildRoot = (Get-C04FullPath (Join-Path $root 'build')).TrimEnd('\', '/')
    $evidenceRootFull = (Assert-C04PathSafe -Path $BuildEvidenceRoot -Label 'build-evidence').TrimEnd('\', '/')
    if (-not (Test-C04PathWithin $evidenceRootFull $buildRoot) -or $evidenceRootFull.Equals($buildRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'C04FAIL:PACKAGE_BUILD_EVIDENCE_OUTSIDE' }
    $evidenceRelative = $evidenceRootFull.Substring($buildRoot.Length).TrimStart('\', '/').Replace('\', '/')
    $sourceManifestPath = Join-Path $evidenceRootFull 'source-input-manifest.csv'
    $stagedManifestPath = Join-Path $evidenceRootFull 'staging\staged-project-input-manifest.csv'
    $stagedFinalPath = Join-Path $evidenceRootFull 'staging\staged-project-final-manifest.csv'
    $stagedCodePath = Join-Path $evidenceRootFull 'staging\staged-code-manifest.csv'
    $evidenceClosurePath = Join-Path $evidenceRootFull 'staging\plugin-closure.csv'
    $evidenceReportPath = Join-Path $evidenceRootFull 'build\player\c04-build-report.json'
    foreach ($requiredEvidence in @($sourceManifestPath, $stagedManifestPath, $stagedFinalPath, $stagedCodePath, $evidenceClosurePath, $evidenceReportPath)) {
        if (-not (Test-Path -LiteralPath $requiredEvidence -PathType Leaf)) { throw "C04FAIL:PACKAGE_BUILD_EVIDENCE_INCOMPLETE:$requiredEvidence" }
    }
    $sourceManifestSha = Get-C04Sha256 $sourceManifestPath
    $stagedManifestSha = Get-C04Sha256 $stagedManifestPath
    $stagedFinalSha = Get-C04Sha256 $stagedFinalPath
    $stagedCodeSha = Get-C04Sha256 $stagedCodePath
    $evidenceClosureSha = Get-C04Sha256 $evidenceClosurePath
    $evidenceReportSha = Get-C04Sha256 $evidenceReportPath
    if ($sourceManifestSha -ne $SourceManifestSha256) { throw 'C04FAIL:PACKAGE_SOURCE_MANIFEST_MISMATCH' }
    if ($stagedManifestSha -ne $StagedInputManifestSha256) { throw 'C04FAIL:PACKAGE_STAGED_MANIFEST_MISMATCH' }
    if ($stagedFinalSha -ne $StagedFinalManifestSha256) { throw 'C04FAIL:PACKAGE_STAGED_FINAL_MANIFEST_MISMATCH' }
    if ($stagedCodeSha -ne $StagedCodeManifestSha256) { throw 'C04FAIL:PACKAGE_STAGED_CODE_MANIFEST_MISMATCH' }
    if ($evidenceClosureSha -ne (Get-C04Sha256 $closureFull)) { throw 'C04FAIL:PACKAGE_CLOSURE_MISMATCH' }
    if ($evidenceReportSha -ne (Get-C04Sha256 $reportFull)) { throw 'C04FAIL:PACKAGE_EVIDENCE_REPORT_MISMATCH' }
    $evidenceClosureRows = @(Import-Csv -LiteralPath $evidenceClosurePath | ForEach-Object { [pscustomobject]@{ name = $_.path; bytes = [long]$_.bytes; sha256 = [string]$_.sha256 } })
    $evidencePluginSet = Get-C04PluginSetSha $evidenceClosureRows
    if ($evidencePluginSet -ne $PluginClosure.pluginSetSha256) { throw 'C04FAIL:PACKAGE_PLUGIN_SET_MISMATCH' }
    $report = Read-C04Json -Path $reportFull
    if ($null -eq $report) { throw 'C04FAIL:PACKAGE_REPORT_MALFORMED' }
    $manifest = Write-C04ManifestFile -Path $manifestPath -Rows (Get-C04PackageRows -PackageRoot $packageRootFull) -Kind 'c04-player-package'
    $manifestSha = Get-C04Sha256 $manifestPath
    $reportSha = Get-C04Sha256 $reportFull
    $provenanceSha = Get-C04Sha256 $provenanceFull
    $closureSha = Get-C04Sha256 $closureFull
    $playerSha = Get-C04Sha256 $playerFull
    $playerBytes = (Get-Item -LiteralPath $playerFull).Length
    $receipt = [ordered]@{
        schemaVersion = 1
        kind = 'c04-fresh-build-receipt'
        freshBuild = $true
        buildId = $BuildId
        buildEvidenceRelativePath = $evidenceRelative
        runnerVersion = $script:C04RunnerVersion
        builtAtUtc = [DateTime]::UtcNow.ToString('O')
        sourceHead = $SourceHead
        buildCommit = $BuildCommit
        sourceInputManifestSha256 = $sourceManifestSha
        sourceInputFileCount = $SourceInputFileCount
        sourceInputBytes = $SourceInputBytes
        stagedInputManifestSha256 = $stagedManifestSha
        stagedFinalManifestSha256 = $stagedFinalSha
        stagedCodeManifestSha256 = $stagedCodeSha
        stagedInputFileCount = $StagedInputFileCount
        packageManifestSha256 = $manifestSha
        buildReportSha256 = $evidenceReportSha
        pluginProvenanceSha256 = $provenanceSha
        pluginClosureSha256 = $evidenceClosureSha
        pluginSetSha256 = $PluginClosure.pluginSetSha256
        pluginFileCount = @($PluginClosure.files).Count
        playerRelativePath = $playerRelative
        playerBytes = $playerBytes
        playerSha256 = $playerSha
        buildResult = [string]$report.result
        buildErrors = $report.errors
        buildWarnings = $report.warnings
        buildStepErrorCount = [int]$report.stepErrorCount
        buildStepExceptionCount = [int]$report.stepExceptionCount
    }
    Write-C04Json -Path $receiptPath -Object $receipt
    $receiptSha = Get-C04Sha256 $receiptPath
    $seal = [ordered]@{
        schemaVersion = 1
        kind = 'c04-player-package-seal'
        runnerVersion = $script:C04RunnerVersion
        sealedAtUtc = [DateTime]::UtcNow.ToString('O')
        sourceHead = $SourceHead
        sourceIdentitySha256 = $SourceIdentitySha256
        sourceManifestSha256 = $sourceManifestSha
        stagedFinalManifestSha256 = $stagedFinalSha
        stagedCodeManifestSha256 = $stagedCodeSha
        buildEvidenceRelativePath = $evidenceRelative
        buildCommit = $BuildCommit
        packageRoot = $packageRootFull
        playerRelativePath = $playerRelative
        playerBytes = $playerBytes
        playerSha256 = $playerSha
        packageManifestRelativePath = 'c04-package-manifest.csv'
        packageManifestBytes = (Get-Item -LiteralPath $manifestPath).Length
        packageManifestSha256 = $manifestSha
        packageFileCount = $manifest.fileCount
        packageBytes = $manifest.bytes
        buildReportRelativePath = $reportFull.Substring($packageRootFull.Length).TrimStart('\', '/').Replace('\', '/')
        buildReportSha256 = $reportSha
        buildReceiptRelativePath = 'c04-build-receipt.json'
        buildReceiptSha256 = $receiptSha
        pluginProvenanceRelativePath = $provenanceFull.Substring($packageRootFull.Length).TrimStart('\', '/').Replace('\', '/')
        pluginProvenanceSha256 = $provenanceSha
        pluginClosureRelativePath = $closureFull.Substring($packageRootFull.Length).TrimStart('\', '/').Replace('\', '/')
        pluginClosureSha256 = $evidenceClosureSha
        pluginSetSha256 = $PluginClosure.pluginSetSha256
        pluginFileCount = @($PluginClosure.files).Count
    }
    Write-C04Json -Path $sealPath -Object $seal
    return [pscustomobject]@{ sealPath = $sealPath; seal = $seal; receiptPath = $receiptPath; receipt = $receipt; manifestPath = $manifestPath; manifest = $manifest }
}

function Test-C04UnityPlayerArtifact {
    param([string]$PlayerPath)
    $errors = New-Object System.Collections.Generic.List[string]
    $playerDir = Split-Path -Parent $PlayerPath
    try {
        $bytes = [System.IO.File]::ReadAllBytes($PlayerPath)
        $text = [System.Text.Encoding]::ASCII.GetString($bytes)
        if (-not $text.Contains('UnityPlayer.dll')) { $errors.Add('PLAYER_NOT_UNITY_EXECUTABLE') | Out-Null }
    }
    catch { $errors.Add('PLAYER_UNREADABLE') | Out-Null }
    $unityPlayer = Join-Path $playerDir 'UnityPlayer.dll'
    if (-not (Test-Path -LiteralPath $unityPlayer -PathType Leaf)) { $errors.Add('PLAYER_UNITY_DLL_MISSING') | Out-Null }
    else {
        $item = Get-Item -LiteralPath $unityPlayer
        if ($item.Length -lt 1048576) { $errors.Add('PLAYER_UNITY_DLL_TOO_SMALL') | Out-Null }
        $stream = [System.IO.File]::OpenRead($unityPlayer)
        try { if ($stream.ReadByte() -ne 0x4D -or $stream.ReadByte() -ne 0x5A) { $errors.Add('PLAYER_UNITY_DLL_NOT_PE') | Out-Null } } finally { $stream.Dispose() }
    }
    $dataRoot = Join-Path $playerDir 'Mahjong_Data'
    $global = Join-Path $dataRoot 'globalgamemanagers'
    if (-not (Test-Path -LiteralPath $global -PathType Leaf) -or (Get-Item -LiteralPath $global).Length -lt 1024) { $errors.Add('PLAYER_DATA_GLOBALGAMEMANAGERS_MISSING') | Out-Null }
    $managed = Join-Path $dataRoot 'Managed'
    if (-not (Test-Path -LiteralPath $managed -PathType Container) -or @(Get-ChildItem -LiteralPath $managed -Filter '*.dll' -File).Count -eq 0) { $errors.Add('PLAYER_MANAGED_ASSEMBLIES_MISSING') | Out-Null }
    return @($errors)
}

function Test-C04PackageSeal {
    param(
        [Parameter(Mandatory = $true)][string]$PlayerPath,
        [string]$ExpectedSourceHead,
        [string]$ExpectedBuildCommit,
        [string]$CurrentSourceIdentitySha256
    )
    $errors = New-Object System.Collections.Generic.List[string]
    $playerFull = (Assert-C04PathSafe -Path $PlayerPath -Label 'player').TrimEnd('\', '/')
    $packageRoot = (Split-Path -Parent $playerFull).TrimEnd('\', '/')
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
    foreach ($identity in @(@{Value=[string]$seal.sourceHead; Name='SOURCE_HEAD'}, @{Value=[string]$seal.buildCommit; Name='BUILD_COMMIT'})) {
        if ($identity.Value -notmatch '^[0-9a-fA-F]{40}$') { $errors.Add('PACKAGE_SEAL_' + $identity.Name + '_MALFORMED') | Out-Null; continue }
        & git -C $root merge-base --is-ancestor $identity.Value HEAD 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) { $errors.Add('PACKAGE_SEAL_' + $identity.Name + '_NOT_IN_HISTORY') | Out-Null }
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedSourceHead) -and [string]$seal.sourceHead -ne $ExpectedSourceHead) { $errors.Add('PACKAGE_SEAL_SOURCE_HEAD_MISMATCH') | Out-Null }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedBuildCommit) -and $ExpectedBuildCommit -ne 'unknown' -and [string]$seal.buildCommit -ne $ExpectedBuildCommit) { $errors.Add('PACKAGE_SEAL_BUILD_COMMIT_MISMATCH') | Out-Null }
    $sourceIdentity = if (-not [string]::IsNullOrWhiteSpace($CurrentSourceIdentitySha256)) { $CurrentSourceIdentitySha256 } else { (Get-C04SourceIdentity).identitySha256 }
    if ([string]$seal.sourceIdentitySha256 -ne $sourceIdentity) { $errors.Add('PACKAGE_SEAL_SOURCE_IDENTITY_MISMATCH') | Out-Null }

    if ((Get-C04Sha256 $manifestPath) -ne [string]$seal.packageManifestSha256) { $errors.Add('PACKAGE_MANIFEST_HASH_MISMATCH') | Out-Null }
    if ((Get-Item -LiteralPath $manifestPath).Length -ne [long]$seal.packageManifestBytes) { $errors.Add('PACKAGE_MANIFEST_SIZE_MISMATCH') | Out-Null }
    $expected = @{}
    foreach ($row in @(Import-Csv -LiteralPath $manifestPath)) {
        if ($expected.ContainsKey([string]$row.path)) { $errors.Add('PACKAGE_MANIFEST_DUPLICATE:' + [string]$row.path) | Out-Null; continue }
        $expected[[string]$row.path] = $row
    }
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
        $playerBytes = (Get-Item -LiteralPath $playerFull).Length
        if ([long]$seal.playerBytes -ne $playerBytes) { $errors.Add('PACKAGE_PLAYER_SIZE_MISMATCH') | Out-Null }
        if ([string]$seal.playerSha256 -ne (Get-C04Sha256 $playerFull)) { $errors.Add('PACKAGE_PLAYER_HASH_MISMATCH') | Out-Null }
        if ($playerBytes -lt 65536) { $errors.Add('PACKAGE_PLAYER_NOT_REAL_BINARY') | Out-Null }
        else {
            foreach ($artifactError in @(Test-C04UnityPlayerArtifact -PlayerPath $playerFull)) { $errors.Add($artifactError) | Out-Null }
            $stream = [System.IO.File]::OpenRead($playerFull)
            try { if ($stream.ReadByte() -ne 0x4D -or $stream.ReadByte() -ne 0x5A) { $errors.Add('PACKAGE_PLAYER_NOT_PE') | Out-Null } }
            finally { $stream.Dispose() }
        }
    }

    $receiptRelative = [string]$seal.buildReceiptRelativePath
    $receipt = $null
    if ([string]::IsNullOrWhiteSpace($receiptRelative)) { $errors.Add('PACKAGE_SEAL_RECEIPT_PATH') | Out-Null }
    else {
        try { $receiptFull = Resolve-C04RelativeMember -Root $packageRoot -Relative $receiptRelative -Label 'build-receipt' } catch { $errors.Add('PACKAGE_RECEIPT_PATH:' + $_.Exception.Message) | Out-Null; $receiptFull = $null }
        if ($receiptFull) {
            if (-not (Test-Path -LiteralPath $receiptFull)) { $errors.Add('PACKAGE_RECEIPT_MISSING') | Out-Null }
            else {
                if ((Get-C04Sha256 $receiptFull) -ne [string]$seal.buildReceiptSha256) { $errors.Add('PACKAGE_RECEIPT_HASH_MISMATCH') | Out-Null }
                try { $receipt = Get-Content -LiteralPath $receiptFull -Raw | ConvertFrom-Json } catch { $errors.Add('PACKAGE_RECEIPT_MALFORMED') | Out-Null }
            }
        }
    }
    if ($receipt) {
        if ([int]$receipt.schemaVersion -ne 1 -or [string]$receipt.kind -ne 'c04-fresh-build-receipt' -or $receipt.freshBuild -ne $true) { $errors.Add('PACKAGE_RECEIPT_KIND') | Out-Null }
        if ([string]::IsNullOrWhiteSpace([string]$receipt.buildId)) { $errors.Add('PACKAGE_RECEIPT_BUILD_ID') | Out-Null }
        if ([string]$receipt.sourceHead -ne [string]$seal.sourceHead) { $errors.Add('PACKAGE_RECEIPT_SOURCE_HEAD') | Out-Null }
        if ([string]$receipt.buildCommit -ne [string]$seal.buildCommit) { $errors.Add('PACKAGE_RECEIPT_BUILD_COMMIT') | Out-Null }
        if ([string]::IsNullOrWhiteSpace([string]$receipt.sourceInputManifestSha256) -or [string]$receipt.sourceInputManifestSha256 -ne [string]$seal.sourceManifestSha256) { $errors.Add('PACKAGE_RECEIPT_SOURCE_INPUT') | Out-Null }
        if ([string]$receipt.packageManifestSha256 -ne [string]$seal.packageManifestSha256) { $errors.Add('PACKAGE_RECEIPT_MANIFEST') | Out-Null }
        if ([string]$receipt.buildReportSha256 -ne [string]$seal.buildReportSha256) { $errors.Add('PACKAGE_RECEIPT_REPORT') | Out-Null }
        if ([string]$receipt.pluginProvenanceSha256 -ne [string]$seal.pluginProvenanceSha256) { $errors.Add('PACKAGE_RECEIPT_PROVENANCE') | Out-Null }
        if ([string]$receipt.pluginClosureSha256 -ne [string]$seal.pluginClosureSha256) { $errors.Add('PACKAGE_RECEIPT_CLOSURE') | Out-Null }
        if ([string]$receipt.pluginSetSha256 -ne [string]$seal.pluginSetSha256) { $errors.Add('PACKAGE_RECEIPT_PLUGIN_SET') | Out-Null }
        if ([string]$receipt.playerSha256 -ne [string]$seal.playerSha256 -or [long]$receipt.playerBytes -ne [long]$seal.playerBytes) { $errors.Add('PACKAGE_RECEIPT_PLAYER') | Out-Null }
        if ([int]$receipt.buildErrors -ne 0) { $errors.Add('PACKAGE_RECEIPT_BUILD_ERRORS') | Out-Null }
    }
    $evidenceRelative = [string]$seal.buildEvidenceRelativePath
    if ([string]::IsNullOrWhiteSpace($evidenceRelative)) { $errors.Add('PACKAGE_EVIDENCE_PATH') | Out-Null }
    else {
        $buildRoot = (Get-C04FullPath (Join-Path $root 'build')).TrimEnd('\', '/')
        $evidenceFull = Get-C04FullPath (Join-Path $buildRoot ($evidenceRelative.Replace('/', '\')))
        if (-not (Test-C04PathWithin $evidenceFull $buildRoot) -or $evidenceFull.Equals($buildRoot, [StringComparison]::OrdinalIgnoreCase)) { $errors.Add('PACKAGE_EVIDENCE_OUTSIDE') | Out-Null }
        elseif (-not (Test-Path -LiteralPath $evidenceFull -PathType Container)) { $errors.Add('PACKAGE_EVIDENCE_MISSING') | Out-Null }
        else {
            $sourceManifest = Join-Path $evidenceFull 'source-input-manifest.csv'
            $stagedManifest = Join-Path $evidenceFull 'staging\staged-project-input-manifest.csv'
            $stagedFinalManifest = Join-Path $evidenceFull 'staging\staged-project-final-manifest.csv'
            $evidenceClosure = Join-Path $evidenceFull 'staging\plugin-closure.csv'
            $evidenceReport = Join-Path $evidenceFull 'build\player\c04-build-report.json'
            foreach ($requiredEvidence in @($sourceManifest, $stagedManifest, $stagedFinalManifest, $evidenceClosure, $evidenceReport)) {
                if (-not (Test-Path -LiteralPath $requiredEvidence -PathType Leaf)) { $errors.Add('PACKAGE_EVIDENCE_INCOMPLETE:' + (Split-Path -Leaf $requiredEvidence)) | Out-Null }
            }
            if (Test-Path -LiteralPath $sourceManifest) {
                if ((Get-C04Sha256 $sourceManifest) -ne [string]$seal.sourceManifestSha256) { $errors.Add('PACKAGE_EVIDENCE_SOURCE_HASH') | Out-Null }
                $expectedSource = @{}
                foreach ($row in @(Get-C04SourceInputRows -RepoRoot $root)) { $expectedSource[[string]$row.path] = $row }
                $actualSource = @{}
                foreach ($row in @(Import-Csv -LiteralPath $sourceManifest)) { $actualSource[[string]$row.path] = $row }
                if ($expectedSource.Count -ne $actualSource.Count) { $errors.Add('PACKAGE_EVIDENCE_SOURCE_COUNT') | Out-Null }
                foreach ($key in $expectedSource.Keys) {
                    if (-not $actualSource.ContainsKey($key)) { $errors.Add('PACKAGE_EVIDENCE_SOURCE_MISSING:' + $key) | Out-Null; continue }
                    if ([long]$expectedSource[$key].bytes -ne [long]$actualSource[$key].bytes -or [string]$expectedSource[$key].sha256 -ne [string]$actualSource[$key].sha256) { $errors.Add('PACKAGE_EVIDENCE_SOURCE_CHANGED:' + $key) | Out-Null }
                }
            }
            if (Test-Path -LiteralPath $stagedManifest) {
                if ((Get-C04Sha256 $stagedManifest) -ne [string]$receipt.stagedInputManifestSha256) { $errors.Add('PACKAGE_EVIDENCE_STAGED_HASH') | Out-Null }
            }
            if (Test-Path -LiteralPath $stagedFinalManifest) {
                if ((Get-C04Sha256 $stagedFinalManifest) -ne [string]$seal.stagedFinalManifestSha256) { $errors.Add('PACKAGE_EVIDENCE_STAGED_FINAL_HASH') | Out-Null }
            }
            $stagedCodeManifest = Join-Path $evidenceFull 'staging\staged-code-manifest.csv'
            if (-not (Test-Path -LiteralPath $stagedCodeManifest -PathType Leaf)) { $errors.Add('PACKAGE_EVIDENCE_CODE_MANIFEST_MISSING') | Out-Null }
            elseif ((Get-C04Sha256 $stagedCodeManifest) -ne [string]$seal.stagedCodeManifestSha256) { $errors.Add('PACKAGE_EVIDENCE_CODE_HASH') | Out-Null }
            $stagingProject = Join-Path $evidenceFull 'staging\repo\unity\Mahjong.Unity'
            if (Test-Path -LiteralPath $stagingProject -PathType Container) {
                foreach ($treeError in @(Test-C04ManifestAgainstRoot -Root $stagingProject -ManifestPath $stagedManifest -ExcludeDirectoryNames @('Library','Temp','Logs','obj','UserSettings') -ExcludeRelativeDirectories @('.vscode') -ExcludeFileSuffixes @('.csproj','.slnx','.pdb.meta'))) { $errors.Add('PACKAGE_EVIDENCE_STAGED_TREE:' + $treeError) | Out-Null }
                foreach ($treeError in @(Test-C04ManifestAgainstRoot -Root $stagingProject -ManifestPath $stagedFinalManifest -ExcludeDirectoryNames @('Library','Temp','Logs','obj','UserSettings') -ExcludeRelativeDirectories @('.vscode') -ExcludeFileSuffixes @('.csproj','.slnx','.pdb.meta'))) { $errors.Add('PACKAGE_EVIDENCE_FINAL_TREE:' + $treeError) | Out-Null }
            }
            else { $errors.Add('PACKAGE_EVIDENCE_STAGING_TREE_MISSING') | Out-Null }
            $stagingRepo = Join-Path $evidenceFull 'staging\repo'
            if ((Test-Path -LiteralPath $stagingRepo -PathType Container) -and (Test-Path -LiteralPath $stagedCodeManifest -PathType Leaf)) {
                foreach ($treeError in @(Test-C04ManifestAgainstRoot -Root $stagingRepo -ManifestPath $stagedCodeManifest -ExcludeDirectoryNames @('bin','obj','Library','Temp','Logs') -ExcludeRelativeDirectories @('unity'))) { $errors.Add('PACKAGE_EVIDENCE_CODE_TREE:' + $treeError) | Out-Null }
            }
            if (Test-Path -LiteralPath $evidenceClosure) {
                if ((Get-C04Sha256 $evidenceClosure) -ne [string]$receipt.pluginClosureSha256) { $errors.Add('PACKAGE_EVIDENCE_CLOSURE_HASH') | Out-Null }
                $rows = @(Import-Csv -LiteralPath $evidenceClosure | ForEach-Object { [pscustomobject]@{ name = $_.path; bytes = [long]$_.bytes; sha256 = [string]$_.sha256 } })
                if ((Get-C04PluginSetSha $rows) -ne [string]$seal.pluginSetSha256) { $errors.Add('PACKAGE_EVIDENCE_PLUGIN_SET') | Out-Null }
            }
            if (Test-Path -LiteralPath $evidenceReport) {
                if ((Get-C04Sha256 $evidenceReport) -ne [string]$seal.buildReportSha256) { $errors.Add('PACKAGE_EVIDENCE_REPORT_HASH') | Out-Null }
            }
            $evidencePlayer = Join-Path $evidenceFull ('build\player\' + $playerRelative.Replace('/', '\'))
            if (-not (Test-Path -LiteralPath $evidencePlayer -PathType Leaf)) { $errors.Add('PACKAGE_EVIDENCE_PLAYER_MISSING') | Out-Null }
            elseif ((Get-C04Sha256 $evidencePlayer) -ne [string]$seal.playerSha256 -or (Get-Item -LiteralPath $evidencePlayer).Length -ne [long]$seal.playerBytes) { $errors.Add('PACKAGE_EVIDENCE_PLAYER_MISMATCH') | Out-Null }

            $evidenceProvenance = Join-Path $evidenceFull 'staging\repo\unity\Mahjong.Unity\Library\SharedPluginProvenance\provenance.json'
            if (-not (Test-Path -LiteralPath $evidenceProvenance)) { $errors.Add('PACKAGE_EVIDENCE_PROVENANCE_MISSING') | Out-Null }
            elseif ((Get-C04Sha256 $evidenceProvenance) -ne [string]$seal.pluginProvenanceSha256) { $errors.Add('PACKAGE_EVIDENCE_PROVENANCE_HASH') | Out-Null }
            else {
                try { $provData = Get-Content -LiteralPath $evidenceProvenance -Raw | ConvertFrom-Json } catch { $provData = $null; $errors.Add('PACKAGE_EVIDENCE_PROVENANCE_MALFORMED') | Out-Null }
                if ($provData) {
                    $stagingRepo = Join-Path $evidenceFull 'staging\repo'
                    foreach ($input in @($provData.inputs)) {
                        $inputPath = Join-Path $stagingRepo ([string]$input.Path).Replace('/', '\')
                        if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) { $errors.Add('PACKAGE_EVIDENCE_INPUT_MISSING:' + [string]$input.Path) | Out-Null; continue }
                        if ([long]$input.Length -ne (Get-Item -LiteralPath $inputPath).Length -or [string]$input.Sha256 -ne (Get-C04Sha256 $inputPath)) { $errors.Add('PACKAGE_EVIDENCE_INPUT_MISMATCH:' + [string]$input.Path) | Out-Null }
                    }
                    $stagingPlugins = Join-Path $stagingRepo 'unity\Mahjong.Unity\Assets\Plugins\Mahjong'
                    foreach ($output in @($provData.outputs)) {
                        $outputPath = Join-Path $stagingPlugins ([string]$output.Name)
                        if (-not (Test-Path -LiteralPath $outputPath -PathType Leaf)) { $errors.Add('PACKAGE_EVIDENCE_OUTPUT_MISSING:' + [string]$output.Name) | Out-Null; continue }
                        if ([string]$output.Sha256 -ne (Get-C04Sha256 $outputPath)) { $errors.Add('PACKAGE_EVIDENCE_OUTPUT_MISMATCH:' + [string]$output.Name) | Out-Null }
                    }
                }
            }
        }
    }

    $reportRelative = [string]$seal.buildReportRelativePath
    if ([string]::IsNullOrWhiteSpace($reportRelative)) { $errors.Add('PACKAGE_SEAL_REPORT_PATH') | Out-Null }
    else {
        try { $reportFull = Resolve-C04RelativeMember -Root $packageRoot -Relative $reportRelative -Label 'build-report' } catch { $errors.Add('PACKAGE_REPORT_PATH:' + $_.Exception.Message) | Out-Null; $reportFull = $null }
        if ($reportFull) {
            if (-not (Test-Path -LiteralPath $reportFull)) { $errors.Add('PACKAGE_REPORT_MISSING') | Out-Null }
            else {
                if ((Get-C04Sha256 $reportFull) -ne [string]$seal.buildReportSha256) { $errors.Add('PACKAGE_REPORT_HASH_MISMATCH') | Out-Null }
                $closureRelative = [string]$seal.pluginClosureRelativePath
                $pluginFiles = @()
                try { $closureFull = Resolve-C04RelativeMember -Root $packageRoot -Relative $closureRelative -Label 'plugin-closure'; $pluginFiles = @(Import-Csv -LiteralPath $closureFull | ForEach-Object { [pscustomobject]@{ name = $_.path; bytes = [long]$_.bytes; sha256 = [string]$_.sha256 } }) }
                catch { $errors.Add('PACKAGE_CLOSURE_PATH:' + $_.Exception.Message) | Out-Null }
                $check = Test-C04BuildReport -Path $reportFull -ExpectedOutput $playerFull -ExpectedPluginFiles $pluginFiles
                foreach ($errorItem in $check.errors) { $errors.Add('PACKAGE_REPORT:' + $errorItem) | Out-Null }
            }
        }
    }

    $provenanceRelative = [string]$seal.pluginProvenanceRelativePath
    if ([string]::IsNullOrWhiteSpace($provenanceRelative)) { $errors.Add('PACKAGE_SEAL_PROVENANCE_PATH') | Out-Null }
    else {
        try { $provenanceFull = Resolve-C04RelativeMember -Root $packageRoot -Relative $provenanceRelative -Label 'plugin-provenance' } catch { $errors.Add('PACKAGE_PROVENANCE_PATH:' + $_.Exception.Message) | Out-Null; $provenanceFull = $null }
        if ($provenanceFull) {
            if (-not (Test-Path -LiteralPath $provenanceFull)) { $errors.Add('PACKAGE_PROVENANCE_MISSING') | Out-Null }
            elseif ((Get-C04Sha256 $provenanceFull) -ne [string]$seal.pluginProvenanceSha256) { $errors.Add('PACKAGE_PROVENANCE_HASH_MISMATCH') | Out-Null }
            else {
                try { $prov = Get-Content -LiteralPath $provenanceFull -Raw | ConvertFrom-Json } catch { $errors.Add('PACKAGE_PROVENANCE_MALFORMED') | Out-Null; $prov = $null }
                if ($prov) {
                    if ([int]$prov.schemaVersion -ne 2 -or [string]$prov.kind -ne 'mahjong-unity-shared-plugin-provenance') { $errors.Add('PACKAGE_PROVENANCE_KIND') | Out-Null }
                    if (@($prov.inputs).Count -eq 0) { $errors.Add('PACKAGE_PROVENANCE_INPUTS_EMPTY') | Out-Null }
                    if (@($prov.outputs).Count -eq 0) { $errors.Add('PACKAGE_PROVENANCE_OUTPUTS_EMPTY') | Out-Null }
                    if ($null -eq $prov.build -or [int]$prov.build.exitCode -ne 0) { $errors.Add('PACKAGE_PROVENANCE_BUILD_EXIT') | Out-Null }
                    $provMap = @{}
                    foreach ($output in @($prov.outputs)) { $provMap[[string]$output.Name] = $output }
                    foreach ($row in $pluginFiles) {
                        if (-not $provMap.ContainsKey([string]$row.name)) { $errors.Add('PACKAGE_PROVENANCE_OUTPUT_MISSING:' + [string]$row.name) | Out-Null; continue }
                        if ([string]$provMap[[string]$row.name].Sha256.ToLowerInvariant() -ne ([string]$row.sha256).ToLowerInvariant()) { $errors.Add('PACKAGE_PROVENANCE_OUTPUT_HASH:' + [string]$row.name) | Out-Null }
                    }
                }
            }
        }
    }

    return [pscustomobject]@{ ok = ($errors.Count -eq 0); errors = @($errors); seal = $seal; manifest = $manifestPath }
}
function Get-C04TransitionAllowlist {
    return @{
        'ProjectSettings/ProjectSettings.asset' = 'changed'
        'Assets/Art/UI/Fonts/NotoSansSC-Regular SDF.asset' = 'changed'
        'Assets/Resources/PerformanceTestRunInfo.json' = 'removed'
        'Assets/Resources/PerformanceTestRunInfo.json.meta' = 'removed'
        'Assets/Resources/PerformanceTestRunSettings.json' = 'removed'
        'Assets/Resources/PerformanceTestRunSettings.json.meta' = 'removed'
    }
}

function Copy-C04TransitionSnapshot {
    param([string]$Root, [string]$SnapshotRoot)
    New-Item -ItemType Directory -Force -Path $SnapshotRoot | Out-Null
    foreach ($relative in @((Get-C04TransitionAllowlist).Keys) + @('Mahjong.Unity.slnx')) {
        $source = Join-Path $Root ($relative.Replace('/', '\'))
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { continue }
        $target = Join-Path $SnapshotRoot ($relative.Replace('/', '\'))
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item -LiteralPath $source -Destination $target -Force
    }
}

function Test-C04AdditiveYamlTransition {
    param([string]$Before, [string]$After, [string]$Mode)
    if (-not (Test-Path -LiteralPath $Before) -or -not (Test-Path -LiteralPath $After)) { return $false }
    $beforeLines = @(Get-Content -LiteralPath $Before -Encoding UTF8 | ForEach-Object { $_.TrimEnd() } | Where-Object { $_.Trim().Length -gt 0 })
    $afterLines = @(Get-Content -LiteralPath $After -Encoding UTF8 | ForEach-Object { $_.TrimEnd() } | Where-Object { $_.Trim().Length -gt 0 })
    if ($beforeLines.Count -eq 0 -or -not $beforeLines[0].StartsWith('%YAML')) { return $false }
    $beforeCounts = @{}
    foreach ($line in $beforeLines) { $beforeCounts[$line] = 1 + [int]($beforeCounts[$line]) }
    $afterCounts = @{}
    foreach ($line in $afterLines) { $afterCounts[$line] = 1 + [int]($afterCounts[$line]) }
    foreach ($line in $beforeCounts.Keys) {
        if ([int]$afterCounts[$line] -lt [int]$beforeCounts[$line]) { return $false }
    }
    if ($Mode -eq 'project') {
        $cursor = 0
        foreach ($line in $beforeLines) {
            $found = -1
            for ($i = $cursor; $i -lt $afterLines.Count; $i++) { if ($afterLines[$i] -eq $line) { $found = $i; break } }
            if ($found -lt 0) { return $false }
            $cursor = $found + 1
        }
    }
    $allowedKeys = @()
    if ($Mode -eq 'project') { $allowedKeys = @('additionalIl2CppArgs,androidAppCategoryOther,AndroidKeyaliasName,AndroidKeystoreName,aotOptions,appleDeveloperTeamID,bluetoothUsageDescription,cameraUsageDescription,cloudProjectId,hmiCpuConfiguration,hmiPlayerDataPath,iOSLaunchScreenCustomStoryboardPath,iOSLaunchScreeniPadCustomStoryboardPath,iOSManualSigningProvisioningProfileID,locationUsageDescription,m_CertPath,m_ModelFolderPath,m_Name,m_PortalFolderPath,m_SubKind,m_VersionName,metroCertificateIssuer,metroCertificatePassword,metroCertificatePath,metroCertificateSubject,metroFTAName,metroPackageVersion,metroProtocolName,metroTileShortName,microphoneUsageDescription,monoEnv,organizationId,projectName,ps4BackgroundImagePath,ps4BGMPath,ps4ExtraSceSysFile,ps4IconImagesFolder,ps4NPtitleDatPath,ps4NPTitleSecret,ps4NPTrophyPackPath,ps4ParamSfxPath,ps4PatchChangeinfoPath,ps4PatchLatestPkgPath,ps4PatchPkgPath,ps4PrivacyGuardImagePath,ps4PronunciationSIGPath,ps4PronunciationXMLPath,ps4RemotePlayKeyMappingDir,ps4SaveDataImagePath,ps4SdkOverride,ps4ShareFilePath,ps4ShareOverlayImagePath,ps4StartupImagePath,ps4StartupImagesFolder,qnxGraphicConfPath,spritePackerPolicy,switchAccessibleURLs,switchApplicationErrorCodeCategory,switchCompilerFlags,switchLegalInformation,switchLocalCommunicationIds_0,switchLocalCommunicationIds_1,switchLocalCommunicationIds_2,switchLocalCommunicationIds_3,switchLocalCommunicationIds_4,switchLocalCommunicationIds_5,switchLocalCommunicationIds_6,switchLocalCommunicationIds_7,switchManualHTML,switchNetLibKey,switchNMETAOverride,switchNSODependencies,switchPresenceGroupId,switchPublisherNames_0,switchPublisherNames_1,switchPublisherNames_2,switchPublisherNames_3,switchPublisherNames_4,switchPublisherNames_5,switchPublisherNames_6,switchPublisherNames_7,switchPublisherNames_8,switchPublisherNames_9,switchPublisherNames_10,switchPublisherNames_11,switchPublisherNames_12,switchPublisherNames_13,switchPublisherNames_14,switchPublisherNames_15,switchTitleNames_0,switchTitleNames_1,switchTitleNames_2,switchTitleNames_3,switchTitleNames_4,switchTitleNames_5,switchTitleNames_6,switchTitleNames_7,switchTitleNames_8,switchTitleNames_9,switchTitleNames_10,switchTitleNames_11,switchTitleNames_12,switchTitleNames_13,switchTitleNames_14,switchTitleNames_15,templatePackageId,tvOSManualSigningProvisioningProfileID,vcxProjDefaultLanguage,VisionOSManualSigningProvisioningProfileID,webGLEmscriptenArgs,webGLModulesDirectory,XboxOneAppManifestOverridePath,XboxOneContentId,XboxOneDescription,XboxOneGameOsOverridePath,XboxOneOverrideIdentityName,XboxOneOverrideIdentityPublisher,XboxOnePackagingOverridePath,XboxOneProductId,XboxOneSandboxId,XboxOneSCId,XboxOneTitleId,XboxOneUpdateKey'.Split(',')) }
    elseif ($Mode -eq 'font') { $allowedKeys = @('m_LockedProperties','m_MipmapLimitGroupName','m_PlatformBlob','path','sourceFontFileName','sourceFontFileGUID','characterSequence','referencedFontAssetGUID','referencedTextAssetGUID','m_SourceFontFilePath','Name') }
    else { return $false }
    foreach ($line in $afterCounts.Keys) {
        $extra = [int]$afterCounts[$line] - [int]($beforeCounts[$line])
        if ($extra -le 0) { continue }
        if ($line -notmatch '^\s*([A-Za-z0-9_]+):\s*$') { return $false }
        $key = $Matches[1]
        if ($allowedKeys -notcontains $key) { return $false }
        $limit = if ($key -eq 'm_SubKind') { 18 } else { 1 }
        if ($extra -gt $limit) { return $false }
    }
    return $true
}
function Split-C04StagingTransitions {
    param([string[]]$Errors, [string]$BeforeRoot, [string]$AfterRoot)
    $allowed = Get-C04TransitionAllowlist
    $fatal = New-Object System.Collections.Generic.List[string]
    $transitions = New-Object System.Collections.Generic.List[object]
    foreach ($line in @($Errors)) {
        $valid = $false
        if ($line -match '^STAGING_(CHANGED|MISSING|EXTRA):(.+)$') {
            $kind = $Matches[1]
            $path = $Matches[2]
            $action = if ($kind -eq 'CHANGED') { 'changed' } elseif ($kind -eq 'MISSING') { 'removed' } else { 'extra' }
            $before = if ($BeforeRoot) { Join-Path $BeforeRoot ($path.Replace('/', '\')) } else { '' }
            $after = if ($AfterRoot) { Join-Path $AfterRoot ($path.Replace('/', '\')) } else { '' }
            if ($allowed.ContainsKey($path) -and $allowed[$path] -eq $action -and $BeforeRoot -and $AfterRoot) {
                if ($action -eq 'changed') { $mode = if ($path -match 'ProjectSettings.asset$') { 'project' } elseif ($path -match 'SDF.asset$') { 'font' } else { 'other' }; $valid = Test-C04AdditiveYamlTransition -Before $before -After $after -Mode $mode }
                elseif ($action -eq 'removed') { $valid = (Test-Path -LiteralPath $before -PathType Leaf) -and -not (Test-Path -LiteralPath $after) }
            }
            if (-not $valid -and $kind -eq 'EXTRA' -and $AfterRoot -and (Test-C04GeneratedUnityFile -Relative $path -Path $after)) { $valid = $true }
            if (-not $valid -and $kind -eq 'CHANGED' -and $path -eq 'Mahjong.Unity.slnx' -and $BeforeRoot -and $AfterRoot -and (Test-C04GeneratedUnityFile -Relative $path -Path $before) -and (Test-C04GeneratedUnityFile -Relative $path -Path $after)) { $valid = $true }
            if ($valid) {
                $transitions.Add([pscustomobject]@{
                    path = $path
                    action = $action
                    beforeSha256 = if ($before -and (Test-Path -LiteralPath $before)) { Get-C04Sha256 $before } else { '' }
                    afterSha256 = if ($after -and (Test-Path -LiteralPath $after)) { Get-C04Sha256 $after } else { '' }
                }) | Out-Null
                continue
            }
        }
        $fatal.Add($line) | Out-Null
    }
    return [pscustomobject]@{ fatal = $fatal.ToArray(); transitions = $transitions.ToArray() }
}
function Get-C04PartialArtifactRows {
    param([string]$EvidenceRoot)
    $rows = New-Object System.Collections.Generic.List[object]
    foreach ($relative in @('build/player', 'staging/repo')) {
        $root = Join-Path $EvidenceRoot ($relative.Replace('/', '\'))
        if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
        foreach ($row in @(Get-C04FileTable -Root $root -ExcludeDirectoryNames @('Library', 'Temp', 'Logs', 'obj', 'bin', 'UserSettings'))) {
            $rows.Add([pscustomobject]@{ path = ($relative + '/' + [string]$row.path); bytes = [long]$row.bytes; sha256 = [string]$row.sha256 }) | Out-Null
        }
    }
    return @($rows | Sort-Object -Property path)
}

function Write-C04EvidenceSeal {
    param(
        [string]$EvidenceRoot,
        [string]$Status,
        [string]$SourceHead
    )
    $partialRows = @(Get-C04PartialArtifactRows -EvidenceRoot $EvidenceRoot)
    $partialPath = Join-Path $EvidenceRoot 'partial-artifacts.csv'
    $partialManifest = Write-C04ManifestFile -Path $partialPath -Rows $partialRows -Kind 'c04-partial-artifacts'
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
        partialManifestSha256 = (Get-C04Sha256 $partialPath)
        partialManifestBytes = (Get-Item -LiteralPath $partialPath).Length
        partialFileCount = $partialManifest.fileCount
    })
    return [pscustomobject]@{ manifestPath = $manifestPath; sealPath = $sealPath; manifest = $manifest; partialPath = $partialPath; partialManifest = $partialManifest }
}

function Test-C04EvidenceSeal {
    param([string]$EvidenceRoot)
    $errors = New-Object System.Collections.Generic.List[string]
    $manifestPath = Join-Path $EvidenceRoot 'manifest.csv'
    $sealPath = Join-Path $EvidenceRoot 'seal.json'
    $partialPath = Join-Path $EvidenceRoot 'partial-artifacts.csv'
    if (-not (Test-Path -LiteralPath $manifestPath)) { return @('EVIDENCE_MANIFEST_MISSING') }
    if (-not (Test-Path -LiteralPath $sealPath)) { return @('EVIDENCE_SEAL_MISSING') }
    if (-not (Test-Path -LiteralPath $partialPath)) { return @('EVIDENCE_PARTIAL_MANIFEST_MISSING') }
    try { $seal = Get-Content -LiteralPath $sealPath -Raw | ConvertFrom-Json } catch { return @('EVIDENCE_SEAL_MALFORMED') }
    if ((Get-C04Sha256 $manifestPath) -ne [string]$seal.manifestSha256) { $errors.Add('EVIDENCE_SEAL_MANIFEST_HASH') | Out-Null }
    if ((Get-C04Sha256 $partialPath) -ne [string]$seal.partialManifestSha256) { $errors.Add('EVIDENCE_SEAL_PARTIAL_HASH') | Out-Null }
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
    $partialExpected = @{}
    foreach ($row in @(Import-Csv -LiteralPath $partialPath)) { $partialExpected[[string]$row.path] = $row }
    $partialActual = @{}
    foreach ($row in @(Get-C04PartialArtifactRows -EvidenceRoot $EvidenceRoot)) { $partialActual[[string]$row.path] = $row }
    foreach ($key in $partialExpected.Keys) {
        if (-not $partialActual.ContainsKey($key)) { $errors.Add('EVIDENCE_PARTIAL_MISSING:' + $key) | Out-Null; continue }
        if ([string]$partialExpected[$key].sha256 -ne [string]$partialActual[$key].sha256) { $errors.Add('EVIDENCE_PARTIAL_HASH:' + $key) | Out-Null }
    }
    foreach ($key in $partialActual.Keys) {
        if (-not $partialExpected.ContainsKey($key)) { $errors.Add('EVIDENCE_PARTIAL_EXTRA:' + $key) | Out-Null }
    }
    return @($errors)
}

# ---------------------------------------------------------------------------
# D04-A verifier handoff (only the fixed external CLI, never a local fallback)
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
    $status = if ($result.timedOut) { 'TIMEOUT' }
        elseif ($result.cancelled) { 'CANCELLED' }
        elseif (-not $result.exitCodeKnown) { 'UNKNOWN' }
        elseif ($result.targetStartFailed) { 'TARGET_START_FAILED' }
        elseif (-not $result.drainComplete) { 'DRAIN_INCOMPLETE' }
        elseif ($result.ownedProcessesRemaining -ne 0 -or $result.cleanupFailed) { 'OWNED_WRITER_REMAINS' }
        elseif ([int]$result.exitCode -eq 0) { 'PASS' }
        else { 'FAIL' }
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
    if ($Code -match 'UNKNOWN') { return 7 }
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
    if ($result.targetStartFailed) { throw 'C04FAIL:TARGET_START_FAILED:result target did not start or produce a native result' }
    if (-not $result.drainComplete) { throw 'C04FAIL:DRAIN_INCOMPLETE:shared build output was not drained' }
    if ($result.ownedProcessesRemaining -ne 0 -or $result.cleanupFailed) { throw 'C04FAIL:OWNED_WRITER_REMAINS:shared build left an owned writer' }
    if ([int]$result.exitCode -ne 0) { throw "C04FAIL:SHARED_BUILD:shared build exited $($result.exitCode)" }
    Write-C04Log "STAGE check-shared-plugins"
    $guardArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $StagingRepo 'tools\unity\check-shared-plugins.ps1'), '-ProjectRoot', $StagingRepo, '-Configuration', 'Release')
    $guard = Start-C04BoundedProcess -FilePath 'powershell.exe' -Arguments $guardArgs -WorkingDirectory $StagingRepo -TimeoutSeconds $SharedBuildTimeoutSeconds -StdOutPath (Join-Path $logs 'check-shared.out.log') -StdErrPath (Join-Path $logs 'check-shared.err.log') -Label 'check-shared-plugins' -DrainGrace $DrainGraceSeconds -Heartbeat $HeartbeatSeconds
    $script:C04Stages.Add($guard) | Out-Null
    if ($guard.timedOut) { throw "C04FAIL:SHARED_GUARD_TIMEOUT:shared plugin guard timed out after $SharedBuildTimeoutSeconds seconds" }
    if ($guard.cancelled) { throw 'C04FAIL:CANCELLED:shared plugin guard cancelled' }
    if (-not $guard.exitCodeKnown) { throw 'C04FAIL:UNKNOWN_EXIT:shared plugin guard native result unknown' }
    if ($guard.targetStartFailed) { throw 'C04FAIL:TARGET_START_FAILED:guard target did not start or produce a native result' }
    if (-not $guard.drainComplete) { throw 'C04FAIL:DRAIN_INCOMPLETE:shared plugin guard output was not drained' }
    if ($guard.ownedProcessesRemaining -ne 0 -or $guard.cleanupFailed) { throw 'C04FAIL:OWNED_WRITER_REMAINS:shared plugin guard left an owned writer' }
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
    if ($result.targetStartFailed) { throw 'C04FAIL:TARGET_START_FAILED:result target did not start or produce a native result' }
    if (-not $result.drainComplete) { throw 'C04FAIL:DRAIN_INCOMPLETE:Unity import warmup output was not drained' }
    if ($result.ownedProcessesRemaining -ne 0 -or $result.cleanupFailed) { throw 'C04FAIL:OWNED_WRITER_REMAINS:Unity import warmup left an owned writer' }
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
    if ($result.timedOut) { throw "C04FAIL:UNITY_BUILD_TIMEOUT:Unity build timed out after $UnityBuildTimeoutSeconds seconds" }
    if ($result.cancelled) { throw 'C04FAIL:CANCELLED:Unity build cancelled' }
    if (-not $result.exitCodeKnown) { throw 'C04FAIL:UNKNOWN_EXIT:Unity build native result unknown' }
    if ($result.targetStartFailed) { throw 'C04FAIL:TARGET_START_FAILED:result target did not start or produce a native result' }
    if (-not $result.drainComplete) { throw 'C04FAIL:DRAIN_INCOMPLETE:Unity build output was not drained' }
    if ($result.ownedProcessesRemaining -ne 0 -or $result.cleanupFailed) { throw 'C04FAIL:OWNED_WRITER_REMAINS:Unity build left an owned writer' }
    $reportCheck = Test-C04BuildReport -Path $ReportPath -ExpectedOutput $PlayerPath -ExpectedPluginFiles @($PluginClosure.files)
    return [pscustomobject]@{ process = $result; reportCheck = $reportCheck; nativeFailed = ([int]$result.exitCode -ne 0) }
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
    $controlCode = ''
    if ($process.timedOut) { $errors.Add('PLAYER_TIMEOUT') | Out-Null; $controlCode = 'PLAYER_TIMEOUT' }
    elseif ($process.cancelled) { $errors.Add('PLAYER_CANCELLED') | Out-Null; $controlCode = 'PLAYER_CANCELLED' }
    elseif ($process.targetStartFailed) { $errors.Add('PLAYER_TARGET_START_FAILED') | Out-Null; $controlCode = 'PLAYER_TARGET_START_FAILED' }
    elseif (-not $process.exitCodeKnown) { $errors.Add('PLAYER_UNKNOWN_EXIT') | Out-Null; $controlCode = 'PLAYER_UNKNOWN_EXIT' }
    elseif (-not $process.drainComplete) { $errors.Add('PLAYER_DRAIN_INCOMPLETE') | Out-Null; $controlCode = 'PLAYER_DRAIN_INCOMPLETE' }
    elseif ($process.ownedProcessesRemaining -ne 0 -or $process.cleanupFailed) { $errors.Add('PLAYER_OWNED_WRITER_REMAINS') | Out-Null; $controlCode = 'PLAYER_OWNED_WRITER_REMAINS' }
    elseif ([int]$process.exitCode -ne 0) { $errors.Add('PLAYER_NATIVE_EXIT_' + [int]$process.exitCode) | Out-Null }
    foreach ($item in $structureErrors) { $errors.Add($item) | Out-Null }
    if (-not $packageState.ok) { foreach ($item in $packageState.errors) { $errors.Add('PACKAGE_AFTER_RUN:' + $item) | Out-Null } }

    if ($errors.Count -eq 0) {
        $verifierResult = Invoke-C04Verifier -Verifier $Verifier -EvidenceJson $evidencePath -CaseName $CaseName -ExpectedSourceHead $ExpectedSourceHead -PackageManifest $PackageManifest -OutPath (Join-Path $caseDir 'verifier.stdout.log') -ErrPath (Join-Path $caseDir 'verifier.stderr.log')
        $script:C04Stages.Add($verifierResult.result) | Out-Null
        if ($verifierResult.status -ne 'PASS') {
            $errors.Add('VERIFIER_' + $verifierResult.status) | Out-Null
            if ($verifierResult.status -eq 'TIMEOUT') { $controlCode = 'VERIFIER_TIMEOUT' }
            elseif ($verifierResult.status -eq 'CANCELLED') { $controlCode = 'VERIFIER_CANCELLED' }
            elseif ($verifierResult.status -eq 'UNKNOWN') { $controlCode = 'VERIFIER_UNKNOWN_EXIT' }
            elseif ($verifierResult.status -eq 'TARGET_START_FAILED') { $controlCode = 'VERIFIER_TARGET_START_FAILED' }
            elseif ($verifierResult.status -eq 'DRAIN_INCOMPLETE') { $controlCode = 'VERIFIER_DRAIN_INCOMPLETE' }
            elseif ($verifierResult.status -eq 'OWNED_WRITER_REMAINS') { $controlCode = 'VERIFIER_OWNED_WRITER_REMAINS' }
        }
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
        controlCode = $controlCode
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
    foreach ($bound in @(@{Name='MutexWaitSeconds'; Value=$MutexWaitSeconds}, @{Name='SharedBuildTimeoutSeconds'; Value=$SharedBuildTimeoutSeconds}, @{Name='UnityImportTimeoutSeconds'; Value=$UnityImportTimeoutSeconds}, @{Name='UnityBuildTimeoutSeconds'; Value=$UnityBuildTimeoutSeconds}, @{Name='PlayerTimeoutSeconds'; Value=$PlayerTimeoutSeconds}, @{Name='VerifierTimeoutSeconds'; Value=$VerifierTimeoutSeconds})) {
        if ([int]$bound.Value -le 0) { throw "C04FAIL:USAGE:$($bound.Name) must be positive" }
    }

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
    $EvidenceRoot = Assert-C04PathSafe -Path $EvidenceRoot -Label 'evidence-root'
    if (Test-Path -LiteralPath $EvidenceRoot) { throw "C04FAIL:REFUSE_OVERWRITE:evidence root already exists: $EvidenceRoot" }
    New-Item -ItemType Directory -Force -Path $EvidenceRoot | Out-Null
    $script:C04LogPath = Join-Path $EvidenceRoot 'runner.log'
    Write-C04Log ("C04 runner={0} case={1} skipBuild={2} sourceHead={3}" -f $script:C04RunnerVersion, $Case, $SkipBuild, $SourceHead)

    try { [Console]::add_CancelKeyPress({ param($sender, $eventArgs) $eventArgs.Cancel = $true; $script:C04Cancelled = $true }) } catch { }

    $mutex = New-Object System.Threading.Mutex($false, $mutexName)
    $mutexDeadline = (Get-Date).AddSeconds($MutexWaitSeconds)
    Write-C04Log "WAIT mutex=$mutexName max=${MutexWaitSeconds}s"
    while (-not $mutexAcquired) {
        if ($mutex.WaitOne(1000)) { $mutexAcquired = $true; break }
        if ($script:C04Cancelled -or ($CancelFile -and (Test-Path -LiteralPath $CancelFile))) { throw 'C04FAIL:CANCELLED:cancelled while waiting for mutex' }
        if ((Get-Date) -ge $mutexDeadline) { throw 'C04FAIL:MUTEX_TIMEOUT:shared Unity validation mutex wait exceeded' }
        if (((Get-Date) - $script:C04StartedUtc).TotalSeconds % 30 -lt 1) {
            Write-C04Log ("HEARTBEAT mutex wait remaining={0:N0}s" -f (($mutexDeadline - (Get-Date)).TotalSeconds))
        }
    }

    $script:C04SourceBefore = Get-C04SourceIdentity
    Write-C04Json -Path (Join-Path $EvidenceRoot 'source-identity-before.json') -Object $script:C04SourceBefore
    $sourceInputManifest = Write-C04ManifestFile -Path (Join-Path $EvidenceRoot 'source-input-manifest.csv') -Rows @($script:C04SourceBefore.inputRows) -Kind 'c04-source-input'
    $stagingRoot = Join-Path $EvidenceRoot 'staging'
    New-Item -ItemType Directory -Force -Path $stagingRoot | Out-Null

    if ($SkipBuild) {
        Write-C04Log "SKIPBUILD validate sealed package player=$PlayerPath"
        $sealCheck = Test-C04PackageSeal -PlayerPath $PlayerPath -ExpectedSourceHead $SourceHead -ExpectedBuildCommit $BuildCommit -CurrentSourceIdentitySha256 $script:C04SourceBefore.identitySha256
        if (-not $sealCheck.ok) { throw ('C04FAIL:SKIPBUILD_INVALID:' + ($sealCheck.errors -join ',')) }
        $packageRoot = Split-Path -Parent (Get-C04FullPath $PlayerPath)
        $packageArtifacts = Join-Path $EvidenceRoot 'package'
        New-Item -ItemType Directory -Force -Path $packageArtifacts | Out-Null
        foreach ($name in @('c04-package-manifest.csv', 'c04-package-seal.json', 'c04-build-receipt.json', 'c04-build-report.json', 'c04-plugin-provenance.json', 'c04-plugin-closure.csv')) {
            $sourceFile = Join-Path $packageRoot $name
            if (Test-Path -LiteralPath $sourceFile) { Copy-Item -LiteralPath $sourceFile -Destination (Join-Path $packageArtifacts $name) -Force }
        }
        $pluginFiles = @(Import-Csv -LiteralPath (Join-Path $packageRoot 'c04-plugin-closure.csv') | ForEach-Object { [pscustomobject]@{ name = $_.path; bytes = [long]$_.bytes; sha256 = [string]$_.sha256 } })
        $reportPath = Join-Path $packageArtifacts 'c04-build-report.json'
        $reportCheck = Test-C04BuildReport -Path $reportPath -ExpectedOutput (Get-C04FullPath $PlayerPath) -ExpectedPluginFiles $pluginFiles
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

        $reportFull = Join-Path $packageRoot 'c04-build-report.json'
        $unityLog = Join-Path $stagingRoot 'logs\unity-player-build.log'
        $warmupLog = Join-Path $stagingRoot 'logs\unity-import-warmup.log'
        $preWarmupRows = Get-C04FileTable -Root $stagingProject -ExcludeDirectoryNames @('Library', 'Temp', 'Logs', 'obj', 'UserSettings') -ExcludeRelativeDirectories @('.vscode') -ExcludeFileSuffixes @('.csproj', '.slnx', '.pdb.meta')
        $preWarmupManifest = Write-C04ManifestFile -Path (Join-Path $stagingRoot 'staged-project-pre-warmup.csv') -Rows $preWarmupRows -Kind 'pre-warmup'
        $preWarmupSnapshot = Join-Path $stagingRoot 'transitions-pre-warmup'
        Copy-C04TransitionSnapshot -Root $stagingProject -SnapshotRoot $preWarmupSnapshot
        $stagedCodeRows = Get-C04FileTable -Root $stagingRepo.repo -ExcludeDirectoryNames @('bin', 'obj', 'Library', 'Temp', 'Logs') -ExcludeRelativeDirectories @('unity')
        $stagedCodeManifest = Write-C04ManifestFile -Path (Join-Path $stagingRoot 'staged-code-manifest.csv') -Rows $stagedCodeRows -Kind 'staged-code'
        $warmup = Invoke-C04UnityWarmup -StagingProject $stagingProject -UnityLogPath $warmupLog
        $warmupErrors = @(Test-C04ManifestAgainstRoot -Root $stagingProject -ManifestPath $preWarmupManifest.path -ExcludeDirectoryNames @('Library', 'Temp', 'Logs', 'obj', 'UserSettings') -ExcludeRelativeDirectories @('.vscode') -ExcludeFileSuffixes @('.csproj', '.slnx', '.pdb.meta'))
        $warmupDiff = Split-C04StagingTransitions -Errors $warmupErrors -BeforeRoot $preWarmupSnapshot -AfterRoot $stagingProject
        if ($warmupDiff.fatal.Count -gt 0) { throw ('C04FAIL:STAGING_CHANGED_DURING_WARMUP:' + ($warmupDiff.fatal -join ',')) }
        $stagedInputRows = Get-C04FileTable -Root $stagingProject -ExcludeDirectoryNames @('Library', 'Temp', 'Logs', 'obj', 'UserSettings') -ExcludeRelativeDirectories @('.vscode') -ExcludeFileSuffixes @('.csproj', '.slnx', '.pdb.meta')
        $stagedInputManifest = Write-C04ManifestFile -Path (Join-Path $stagingRoot 'staged-project-input-manifest.csv') -Rows $stagedInputRows -Kind 'c04-staged-project-input'
        $preBuildSnapshot = Join-Path $stagingRoot 'transitions-pre-build'
        Copy-C04TransitionSnapshot -Root $stagingProject -SnapshotRoot $preBuildSnapshot
        $unityBuild = Invoke-C04UnityBuild -StagingProject $stagingProject -PlayerPath $playerFull -ReportPath $reportFull -UnityLogPath $unityLog -PluginClosure $stagingBuild.closure
        if ($BuildReport) {
            $requestedReport = Get-C04FullPath $BuildReport
            if ($requestedReport -ne $reportFull) {
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $requestedReport) | Out-Null
                Copy-Item -LiteralPath $reportFull -Destination $requestedReport -Force
            }
        }

        $stagingErrors = @(Test-C04ManifestAgainstRoot -Root $stagingProject -ManifestPath $stagedInputManifest.path -ExcludeDirectoryNames @('Library', 'Temp', 'Logs', 'obj', 'UserSettings') -ExcludeRelativeDirectories @('.vscode') -ExcludeFileSuffixes @('.csproj', '.slnx', '.pdb.meta'))
        $stagingDiff = Split-C04StagingTransitions -Errors $stagingErrors -BeforeRoot $preBuildSnapshot -AfterRoot $stagingProject
        if ($stagingDiff.transitions.Count -gt 0 -or $stagingDiff.fatal.Count -gt 0) {
            Write-C04Json -Path (Join-Path $stagingRoot 'staging-workspace-diff.json') -Object ([ordered]@{ transitions = @($stagingDiff.transitions); fatal = @($stagingDiff.fatal) })
        }
        if ($stagingDiff.fatal.Count -gt 0) {
            throw ('C04FAIL:STAGING_CHANGED_DURING_BUILD:' + ($stagingDiff.fatal -join ','))
        }

        $finalRows = Get-C04FileTable -Root $stagingProject -ExcludeDirectoryNames @('Library', 'Temp', 'Logs', 'obj', 'UserSettings') -ExcludeRelativeDirectories @('.vscode') -ExcludeFileSuffixes @('.csproj', '.slnx', '.pdb.meta')
        $finalManifest = Write-C04ManifestFile -Path (Join-Path $stagingRoot 'staged-project-final-manifest.csv') -Rows $finalRows -Kind 'staged-final'
        $codeErrors = @(Test-C04ManifestAgainstRoot -Root $stagingRepo.repo -ManifestPath $stagedCodeManifest.path -ExcludeDirectoryNames @('bin', 'obj', 'Library', 'Temp', 'Logs') -ExcludeRelativeDirectories @('unity'))
        if ($codeErrors.Count -gt 0) { throw ('C04FAIL:STAGED_CODE_CHANGED:' + ($codeErrors -join ',')) }
        $finalPluginClosure = Get-C04PluginClosure -ProvenancePath $stagingBuild.provenancePath -PluginDir $stagingBuild.pluginDir
        if (-not $finalPluginClosure.ok) { throw ('C04FAIL:PLUGIN_CLOSURE_FINAL:' + ($finalPluginClosure.errors -join ',')) }
        if ($finalPluginClosure.pluginSetSha256 -ne $stagingBuild.closure.pluginSetSha256) {
            throw 'C04FAIL:PLUGIN_CLOSURE_CHANGED:staged plugin bytes changed after the build'
        }
        $identityAfterBuild = Get-C04SourceIdentity
        if ($identityAfterBuild.identitySha256 -ne $script:C04SourceBefore.identitySha256) {
            throw 'C04FAIL:SOURCE_MUTATED_DURING_BUILD:build-relevant source changed during the build'
        }
        if ($unityBuild.nativeFailed -or -not $unityBuild.reportCheck.ok) {
            throw ('C04FAIL:BUILD_FAILED:' + (($unityBuild.reportCheck.errors + @('UNITY_NATIVE_EXIT_' + $unityBuild.process.exitCode)) -join ','))
        }

        $provenanceCopy = Join-Path $packageRoot 'c04-plugin-provenance.json'
        Copy-Item -LiteralPath $stagingBuild.provenancePath -Destination $provenanceCopy -Force
        $closureCopy = Join-Path $packageRoot 'c04-plugin-closure.csv'
        Copy-Item -LiteralPath (Join-Path $stagingRoot 'plugin-closure.csv') -Destination $closureCopy -Force
        $packageSeal = Write-C04PackageSeal -PackageRoot $packageRoot -PlayerPath $playerFull -ReportPath $reportFull -ProvenancePath $provenanceCopy -PluginClosurePath $closureCopy -BuildEvidenceRoot $EvidenceRoot -SourceHead $SourceHead -SourceIdentitySha256 $script:C04SourceBefore.identitySha256 -SourceManifestSha256 $sourceInputManifest.sha256 -StagedInputManifestSha256 $stagedInputManifest.sha256 -StagedFinalManifestSha256 $finalManifest.sha256 -BuildCommit $BuildCommit -BuildId (Split-Path -Leaf $EvidenceRoot) -SourceInputFileCount $script:C04SourceBefore.inputFileCount -SourceInputBytes $script:C04SourceBefore.inputBytes -StagedInputFileCount $stagedInputManifest.fileCount -PluginClosure $finalPluginClosure
        $sealCheck = Test-C04PackageSeal -PlayerPath $playerFull -ExpectedSourceHead $SourceHead -ExpectedBuildCommit $BuildCommit -CurrentSourceIdentitySha256 $script:C04SourceBefore.identitySha256
        if (-not $sealCheck.ok) { throw ('C04FAIL:PACKAGE_SEAL_INVALID:' + ($sealCheck.errors -join ',')) }

        $packageArtifacts = Join-Path $EvidenceRoot 'package'
        New-Item -ItemType Directory -Force -Path $packageArtifacts | Out-Null
        foreach ($name in @('c04-package-manifest.csv', 'c04-package-seal.json', 'c04-build-receipt.json', 'c04-build-report.json', 'c04-plugin-provenance.json', 'c04-plugin-closure.csv')) {
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
            buildReceiptSha256 = (Get-C04Sha256 (Join-Path $packageRoot 'c04-build-receipt.json'))
            pluginClosureSha256 = (Get-C04Sha256 $closureCopy)
            stagingTransitions = @($stagingDiff.transitions)
            warmupLog = $warmupLog
            warmupLogSha256 = if (Test-Path -LiteralPath $warmupLog) { Get-C04Sha256 $warmupLog } else { '' }
        }
    }

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
                $controlCodes = @($failed | ForEach-Object { [string]$_.controlCode } | Where-Object { $_ })
                if (@($controlCodes | Where-Object { $_ -match 'CANCEL' }).Count -gt 0) { $script:C04ExitCode = 5; $script:C04Status = 'CANCELLED' }
                elseif (@($controlCodes | Where-Object { $_ -match 'TIMEOUT' }).Count -gt 0) { $script:C04ExitCode = 4 }
                elseif (@($controlCodes | Where-Object { $_ -match 'UNKNOWN' }).Count -gt 0) { $script:C04ExitCode = 7 }
                else { $script:C04ExitCode = 1 }
                $script:C04FirstFailureCode = if ($controlCodes.Count -gt 0) { [string]$controlCodes[0] } else { 'CASE_FAILED' }
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
            try { $script:C04SourceAfter = Get-C04SourceIdentity } catch { $script:C04SourceAfter = $null }
            if ($null -ne $script:C04SourceBefore -and $null -ne $script:C04SourceAfter) {
                $script:C04SourceStable = ($script:C04SourceAfter.identitySha256 -eq $script:C04SourceBefore.identitySha256)
                if (-not $script:C04SourceStable) {
                    $script:C04Status = 'FAIL'
                    $script:C04FirstFailureCode = 'SOURCE_MUTATED_DURING_RUN'
                    $script:C04FirstFailureMessage = 'build-relevant source changed during case execution'
                    $script:C04Errors.Add('SOURCE_MUTATED_DURING_RUN') | Out-Null
                    $script:C04ExitCode = 1
                }
            }
            else {
                $script:C04SourceStable = $false
                $script:C04Status = 'FAIL'
                $script:C04FirstFailureCode = 'SOURCE_IDENTITY_AFTER_FAILED'
                $script:C04FirstFailureMessage = 'could not recompute source identity after execution'
                $script:C04ExitCode = 1
            }
            $summaryResult = Write-C04FinalSummary -EvidenceRoot $EvidenceRoot -Status $script:C04Status -ExitCode $script:C04ExitCode -GameStatus $script:C04GameStatus -SourceBefore $script:C04SourceBefore -SourceAfter $script:C04SourceAfter -SourceStable $script:C04SourceStable -Package $script:C04Package -Cases $script:C04Cases.ToArray() -Stages $script:C04Stages.ToArray() -FirstFailureCode $script:C04FirstFailureCode -FirstFailureMessage $script:C04FirstFailureMessage -Errors $script:C04Errors.ToArray()
            $sealResult = Write-C04EvidenceSeal -EvidenceRoot $EvidenceRoot -Status $script:C04Status -SourceHead $SourceHead
            $sealErrors = @(Test-C04EvidenceSeal -EvidenceRoot $EvidenceRoot)
            if ($sealErrors.Count -gt 0) {
                Write-Host ("SEAL_CHECK_FAILED " + ($sealErrors -join ','))
                $script:C04Status = 'FAIL'
                $script:C04FirstFailureCode = 'SEAL_CHECK_FAILED'
                $script:C04FirstFailureMessage = ($sealErrors -join ',')
                $script:C04Errors.Add('SEAL_CHECK_FAILED') | Out-Null
                $script:C04ExitCode = 1
                [System.IO.File]::Delete((Join-Path $EvidenceRoot 'seal.json'))
                $summaryResult = Write-C04FinalSummary -EvidenceRoot $EvidenceRoot -Status 'FAIL' -ExitCode 1 -GameStatus $script:C04GameStatus -SourceBefore $script:C04SourceBefore -SourceAfter $script:C04SourceAfter -SourceStable $script:C04SourceStable -Package $script:C04Package -Cases $script:C04Cases.ToArray() -Stages $script:C04Stages.ToArray() -FirstFailureCode $script:C04FirstFailureCode -FirstFailureMessage $script:C04FirstFailureMessage -Errors $script:C04Errors.ToArray()
                $sealResult = Write-C04EvidenceSeal -EvidenceRoot $EvidenceRoot -Status 'FAIL' -SourceHead $SourceHead
                $sealErrors2 = @(Test-C04EvidenceSeal -EvidenceRoot $EvidenceRoot)
                if ($sealErrors2.Count -gt 0) {
                    [System.IO.File]::Delete((Join-Path $EvidenceRoot 'seal.json'))
                    [System.IO.File]::WriteAllText((Join-Path $EvidenceRoot 'FINALIZE_FAILED.txt'), ($sealErrors2 -join [Environment]::NewLine))
                    $script:C04ExitCode = 1
                }
            }
            Write-Host ("SUMMARY " + $summaryResult.summaryPath)
            Write-Host ("SEAL " + (Join-Path $EvidenceRoot 'seal.json'))
        }
        catch {
            Write-Host ("FINALIZE_FAILED " + $_.Exception.Message)
            try { [System.IO.File]::Delete((Join-Path $EvidenceRoot 'seal.json')) } catch { }
            try { [System.IO.File]::WriteAllText((Join-Path $EvidenceRoot 'FINALIZE_FAILED.txt'), $_.Exception.ToString()) } catch { }
            $script:C04Status = 'FAIL'
            $script:C04ExitCode = 1
        }
    }
    if ($mutexAcquired -and $mutex) { try { $mutex.ReleaseMutex() } catch { } }
    if ($mutex) { try { $mutex.Dispose() } catch { } }
}

exit $script:C04ExitCode
