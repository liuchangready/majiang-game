#Requires -Version 5.1
<#
.SYNOPSIS
  Independent controlled collector for one P4.5 Windows Player recovery case.

.DESCRIPTION
  This script is intentionally self-contained. It does not dot-source
  p45-e2e-common.ps1, does not call the old collector signer, and exposes no
  "sign/accept this JSON" entry point.

  The reviewer runs this script in a fresh PowerShell process. The script:
    * freezes source HEAD/dirty state, build command/exit/log, the shared plugin
      closure and the complete Player package (launcher, Data, DLLs, all files);
    * creates a non-reused run/case/attempt/challenge before any launch;
    * starts the Player itself with Start-Process, keeps the real Process object,
      observes PID/start/path/actual argv/exit or kills only that object;
    * waits for the structured export, seals the raw authority/cheat/export
      bytes with stable-copy checks, and invokes the fixed P45RecoveryVerifier;
    * performs one declared save fault in the same data domain, then repeats the
      direct observation for the AFTER attempt;
    * writes a linear record chain and a complete case manifest.

  It reports collection facts only. callerAcceptance is always
  PENDING_INDEPENDENT_REVIEW and no code path can set it to accepted.

.PARAMETER EvidenceClass
  GAME for a real Windows Player. FIXTURE is for the test harness only; a
  FIXTURE case is never a GAME positive and the verifier keeps gamePositive=false.

.PARAMETER SaveFault
  None, TruncatePrimary, BadChecksumPrimary, IncompatiblePrimary,
  MissingPrimaryWithBackup, BothMissing or BothBad. The fault is applied only
  inside this case's private data directory after BEFORE has exited.

.EXAMPLE
  powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File tools/unity/run-p45-controlled-recovery-collection.ps1 `
    -Case Normal -SaveFault None `
    -PlayerPath build/p45-controlled-collection/player/Mahjong.exe `
    -BuildCommit <40-hex> -BuildLog build/.../unity-build.log -BuildCommand '<exact>' `
    -BuildExitCode 0 -Baseline <baseline.save> `
    -VerifierPath dotnet/tools/P45RecoveryVerifier/bin/Release/net8.0/P45RecoveryVerifier.dll `
    -DotnetPath D:\Unity\dotnet\dotnet.exe `
    -EvidenceRoot build/p45-controlled-collection/evidence/<unique>
#>
[CmdletBinding()]
param(
    [ValidateSet('Normal', 'Force')]
    [string]$Case = 'Normal',

    [ValidateSet('None', 'TruncatePrimary', 'BadChecksumPrimary', 'IncompatiblePrimary',
        'MissingPrimaryWithBackup', 'BothMissing', 'BothBad')]
    [string]$SaveFault = 'None',

    [string]$PlayerPath = '',

    [string]$BuildCommit = '',

    [string]$BuildLog = '',

    [string]$BuildCommand = '',

    [int]$BuildExitCode = 0,

    [string]$Baseline = '',

    [string]$VerifierPath = '',

    [string]$DotnetPath = 'D:\Unity\dotnet\dotnet.exe',
    [string]$EvidenceRoot = '',
    [string]$RunId = '',
    [int]$TimeoutSeconds = 180,
    [int]$MutexTimeoutSeconds = 1800,
    [string]$ExpectedPlayerSha256 = '',
    [string]$ExpectedPackageSha256 = '',
    [ValidateSet('GAME', 'FIXTURE')]
    [string]$EvidenceClass = 'GAME',
    [switch]$AllowDirtySource,
    [string[]]$AllowedDirtyPath = @(),
    [switch]$ProbeIdentity,
    [int]$ProbePid = 0,
    [string]$ProbeStartTimeUtc = '',
    [string]$ProbePath = '',
    [string]$ProbeSha256 = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$script:ToolVersion = 'P45-CONTROLLED-COLLECTION/1'
$script:SchemaVersion = 1
$script:Utf8 = New-Object System.Text.UTF8Encoding($false)
$script:Utf8Bom = New-Object System.Text.UTF8Encoding($true)
$script:RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$script:UnityMutexName = 'Local\MahjongV5P45UnityValidation'
$script:Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$script:Records = New-Object System.Collections.Generic.List[object]
$script:OwnershipRoot = ''
$script:LastRecordSha = ''
$script:RecordSequence = 0
$script:InputManifestSha256 = $null
$script:CaseId = ''
$script:CaseRoot = ''
$script:DataRoot = ''
$script:RecordsPath = ''
$script:Challenges = @{}

function Throw-Ctl {
    param([int]$Code, [string]$Status, [string]$Message)
    $safe = (($Message -replace '[\r\n|]+', ' ').Trim())
    throw ('P45CTL|' + $Code.ToString([Globalization.CultureInfo]::InvariantCulture) + '|' + $Status + '|' + $safe)
}

function Get-ErrText {
    param([object]$Record)
    if ($null -eq $Record) { return 'unknown error' }
    if ($Record -is [System.Management.Automation.ErrorRecord]) {
        return ([string]$Record.Exception.Message -replace '[\r\n]+', ' ').Trim()
    }
    return ([string]$Record -replace '[\r\n]+', ' ').Trim()
}

function Get-ExitCodeFromError {
    param([object]$Record)
    $text = Get-ErrText $Record
    if ($text -match '^P45CTL\|(\d+)\|') { return [int]$Matches[1] }
    return 99
}

function Get-StatusFromError {
    param([object]$Record)
    $text = Get-ErrText $Record
    if ($text -match '^P45CTL\|\d+\|([A-Z_]+)\|') { return [string]$Matches[1] }
    return 'INTERNAL_ERROR'
}

function Get-UtcText {
    ([DateTime]::UtcNow).ToString('o', [Globalization.CultureInfo]::InvariantCulture)
}

function Get-FullPath {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return '' }
    return [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
}

function Get-Sha256Bytes {
    param([byte[]]$Bytes)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-', '').ToUpperInvariant() }
    finally { $sha.Dispose() }
}

function Get-Sha256Text {
    param([string]$Text)
    return Get-Sha256Bytes -Bytes ($script:Utf8.GetBytes($Text))
}

function Get-FileSig {
    param([string]$Path)
    if (-not [IO.File]::Exists($Path)) { return $null }
    $item = Get-Item -LiteralPath $Path
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash($stream)
        $length = $stream.Length
    }
    finally {
        $sha.Dispose()
        $stream.Dispose()
    }
    [pscustomobject]@{
        path = $Path
        bytes = [long]$length
        sha256 = ([BitConverter]::ToString($hash)).Replace('-', '').ToUpperInvariant()
        lastWriteUtc = $item.LastWriteTimeUtc.ToString('o', [Globalization.CultureInfo]::InvariantCulture)
    }
}

function Get-Sha256File {
    param([string]$Path)
    $sig = Get-FileSig -Path $Path
    if ($null -eq $sig) { return $null }
    return [string]$sig.sha256
}

function Get-TextShared {
    param([string]$Path)
    for ($i = 0; $i -lt 20; $i++) {
        try {
            $share = [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete
            $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, $share)
            try {
                $reader = New-Object System.IO.StreamReader($stream, $script:Utf8, $true)
                try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
            }
            finally { $stream.Dispose() }
        }
        catch { Start-Sleep -Milliseconds 50 }
    }
    return $null
}

function Get-Sha256Shared {
    param([string]$Path)
    for ($i = 0; $i -lt 20; $i++) {
        try {
            $share = [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete
            $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, $share)
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $hash = $sha.ComputeHash($stream) } finally { $sha.Dispose(); $stream.Dispose() }
            return ([BitConverter]::ToString($hash)).Replace('-', '').ToUpperInvariant()
        }
        catch { Start-Sleep -Milliseconds 50 }
    }
    return $null
}

function Test-OwnedNativeHandle {
    param([System.Runtime.InteropServices.SafeHandle]$Handle, [string]$Context)
    Initialize-OwnershipNative
    $info = [P45Ctl.Ownership]::InspectHandle($Handle)
    if ($null -eq $info -or $info.IsDirectory -or $info.IsReparse) {
        Throw-Ctl 19 'RESOURCE_ALIAS' ($Context + ': write handle is not an owned regular file.')
    }
    $rootInfo = Get-PathIdentity -Path $script:OwnershipRoot
    if ($null -eq $rootInfo -or $rootInfo.IsReparse) {
        Throw-Ctl 19 'RESOURCE_ALIAS' ($Context + ': ownership root is not a regular directory.')
    }
    $finalText = $info.FinalPath.TrimEnd('\', '/')
    $rootText = $rootInfo.FinalPath.TrimEnd('\', '/')
    if (-not ($finalText -eq $rootText -or ($rootText -ne '' -and $finalText.StartsWith($rootText + '\', [StringComparison]::OrdinalIgnoreCase)))) {
        Throw-Ctl 19 'RESOURCE_ALIAS' ($Context + ': write handle escapes ownership root: ' + $finalText)
    }
    if ($info.NumberOfLinks -ne 1) {
        Throw-Ctl 19 'RESOURCE_ALIAS' ($Context + ': write handle is hard-linked (' + $info.NumberOfLinks + ' links).')
    }
    return $info
}

function Test-OwnedHandle {
    param([System.IO.FileStream]$Stream, [string]$Context)
    return Test-OwnedNativeHandle -Handle $Stream.SafeFileHandle -Context $Context
}

function Open-OwnedFileHandle {
    param([string]$Path, [ValidateSet('CreateNew', 'Append', 'Truncate')][string]$Mode, [string]$Context)
    Assert-OwnedWriteParent -Path $Path -Context $Context
    [IO.Directory]::CreateDirectory((Split-Path -Parent $Path)) | Out-Null
    $exists = [IO.File]::Exists($Path)
    if ($Mode -eq 'CreateNew' -and $exists) {
        Throw-Ctl 19 'RESOURCE_ALIAS' ($Context + ': refusing to replace an existing leaf: ' + $Path)
    }
    $fs = $null
    try {
        if ($exists) {
            $fs = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        }
        else {
            $fs = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        }
        $null = Test-OwnedHandle -Stream $fs -Context $Context
        if ($Mode -eq 'Append') { [void]$fs.Seek(0, [IO.SeekOrigin]::End) }
        elseif ($Mode -eq 'Truncate') { $fs.SetLength(0); $fs.Position = 0 }
        return $fs
    }
    catch {
        if ($null -ne $fs) { try { $fs.Dispose() } catch { } }
        throw
    }
}

function Write-OwnedText {
    param([string]$Path, [string]$Text)
    $fs = Open-OwnedFileHandle -Path $Path -Mode Truncate -Context 'owned text write'
    try {
        $bytes = $script:Utf8.GetBytes($Text)
        $fs.Write($bytes, 0, $bytes.Length)
        $fs.Flush($true)
    }
    finally { $fs.Dispose() }
}

function Append-OwnedText {
    param([string]$Path, [string]$Text)
    $fs = Open-OwnedFileHandle -Path $Path -Mode Append -Context 'owned append'
    try {
        $bytes = $script:Utf8.GetBytes($Text)
        $fs.Write($bytes, 0, $bytes.Length)
        $fs.Flush($true)
    }
    finally { $fs.Dispose() }
}

function Write-OwnedBytes {
    param([string]$Path, [byte[]]$Bytes)
    $fs = Open-OwnedFileHandle -Path $Path -Mode Truncate -Context 'owned byte write'
    try {
        $fs.Write($Bytes, 0, $Bytes.Length)
        $fs.Flush($true)
    }
    finally { $fs.Dispose() }
}

function Read-OwnedBytes {
    param([string]$Path, [string]$Context)
    $fs = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    try {
        $null = Test-OwnedHandle -Stream $fs -Context $Context
        $ms = New-Object System.IO.MemoryStream
        try { $fs.CopyTo($ms); return $ms.ToArray() } finally { $ms.Dispose() }
    }
    finally { $fs.Dispose() }
}

function Remove-OwnedFile {
    param([string]$Path, [string]$Context, [int]$HoldMilliseconds = 0)
    Initialize-OwnershipNative
    if (-not [IO.File]::Exists($Path)) { return }
    $handle = [P45Ctl.Ownership]::OpenDelete((Get-FullPath $Path))
    if ($null -eq $handle -or $handle.IsInvalid) {
        Throw-Ctl 19 'RESOURCE_ALIAS' ($Context + ': could not open an object-bound delete handle.')
    }
    try {
        $null = Test-OwnedNativeHandle -Handle $handle -Context $Context
        if ($HoldMilliseconds -gt 0) { Start-Sleep -Milliseconds $HoldMilliseconds }
        [P45Ctl.Ownership]::MarkDelete($handle)
    }
    finally { $handle.Dispose() }
}

function Write-TextAtomic {
    param([string]$Path, [string]$Text)
    Write-OwnedText -Path $Path -Text $Text
}

function Write-JsonAtomic {
    param([string]$Path, [object]$Object)
    $json = ($Object | ConvertTo-Json -Depth 40)
    Write-TextAtomic -Path $Path -Text ($json + [Environment]::NewLine)
}

function Read-JsonFile {
    param([string]$Path)
    if (-not [IO.File]::Exists($Path)) { Throw-Ctl 17 'EVIDENCE_MISSING' ('JSON evidence is missing: ' + $Path) }
    $text = [IO.File]::ReadAllText($Path, $script:Utf8)
    try { return ($text | ConvertFrom-Json) }
    catch { Throw-Ctl 18 'EVIDENCE_INVALID' ('JSON evidence is not parseable: ' + $Path + ' ' + (Get-ErrText $_)) }
}

function Get-Prop {
    param([object]$Object, [string]$Name, [object]$Default = $null)
    if ($null -eq $Object) { return $Default }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $Default }
    return $property.Value
}

function Get-RelativePath {
    param([string]$Base, [string]$Path)
    $baseFull = (Get-FullPath $Base)
    $pathFull = (Get-FullPath $Path)
    if ($pathFull.StartsWith($baseFull, [StringComparison]::OrdinalIgnoreCase)) {
        return $pathFull.Substring($baseFull.Length).TrimStart('\', '/').Replace('\', '/')
    }
    return $pathFull.Replace('\', '/')
}

function Add-Record {
    param(
        [Parameter(Mandatory = $true)][string]$Kind,
        [Parameter(Mandatory = $true)][object]$Data,
        [string]$RelativePath = ''
    )

    $payloadText = ($Data | ConvertTo-Json -Depth 40 -Compress)
    $recordSha = Get-Sha256Text -Text $payloadText
    $record = [ordered]@{
        schemaVersion = 1
        kind = $Kind
        sequence = $script:RecordSequence
        previousRecordSha256 = $script:LastRecordSha
        payloadText = $payloadText
        recordSha256 = $recordSha
        recordedAt = Get-UtcText
    }
    $line = ($record | ConvertTo-Json -Depth 10 -Compress)
    Assert-OwnedWriteParent -Path $script:RecordsPath -Context 'record chain append'
    Append-OwnedText -Path $script:RecordsPath -Text ($line + [Environment]::NewLine)
    if (-not [string]::IsNullOrWhiteSpace($RelativePath)) {
        Write-JsonAtomic -Path (Join-Path $script:CaseRoot $RelativePath) -Object $Data
    }
    $script:LastRecordSha = $recordSha
    $script:RecordSequence++
    return $record
}

function Get-SourceSnapshot {
    $head = (& git -C $script:RepoRoot rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($head)) {
        Throw-Ctl 14 'SOURCE_UNAVAILABLE' 'Could not read the source HEAD from the worktree.'
    }
    $branch = (& git -C $script:RepoRoot rev-parse --abbrev-ref HEAD 2>$null)
    $status = @(& git -C $script:RepoRoot status --porcelain=v1 2>$null)
    $diffStat = (@(& git -C $script:RepoRoot diff --stat 2>$null) -join [Environment]::NewLine)
    $diffText = (@(& git -C $script:RepoRoot diff --no-ext-diff --binary 2>$null) -join [Environment]::NewLine)
    $dirtyPaths = New-Object System.Collections.Generic.List[string]
    foreach ($line in $status) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $path = if ($line.Length -gt 3) { $line.Substring(3).Trim() } else { $line.Trim() }
        if ($path -match ' -> ') { $path = ($path -split ' -> ')[-1] }
        $path = $path.Trim('"').Replace('\', '/')
        $dirtyPaths.Add($path) | Out-Null
    }
    [pscustomobject]@{
        head = ([string]$head).Trim().ToLowerInvariant()
        branch = ([string]$branch).Trim()
        statusPorcelain = ($status -join [Environment]::NewLine)
        diffStat = $diffStat
        diffText = $diffText
        diffSha256 = Get-Sha256Text -Text $diffText
        dirty = ($dirtyPaths.Count -gt 0)
        dirtyPaths = @($dirtyPaths.ToArray())
    }
}

function Get-PackageManifest {
    param([string]$Root)
    $rootFull = Get-FullPath $Root
    $records = New-Object System.Collections.Generic.List[object]
    foreach ($file in @(Get-ChildItem -LiteralPath $rootFull -Recurse -File -Force -ErrorAction Stop)) {
        $relative = Get-RelativePath -Base $rootFull -Path $file.FullName
        $sig = Get-FileSig -Path $file.FullName
        $records.Add([pscustomobject]@{
            path = $relative
            bytes = [long]$sig.bytes
            sha256 = [string]$sig.sha256
        }) | Out-Null
    }
    if ($records.Count -eq 0) { Throw-Ctl 14 'PACKAGE_EMPTY' ('The Player package directory has no files: ' + $rootFull) }
    $array = @($records.ToArray() | Sort-Object -Property path -CaseSensitive)
    $lines = New-Object System.Collections.Generic.List[string]
    $total = [long]0
    foreach ($record in $array) {
        $total += [long]$record.bytes
        $lines.Add($record.path + '|' + $record.bytes.ToString([Globalization.CultureInfo]::InvariantCulture) + '|' + $record.sha256) | Out-Null
    }
    [pscustomobject]@{
        root = $rootFull
        fileCount = $array.Count
        totalBytes = $total
        filesSha256 = Get-Sha256Text -Text ($lines -join "`n")
        files = $array
    }
}

function Test-PackageUnchanged {
    param([object]$Expected, [string]$Root, [string]$Context)
    $actual = Get-PackageManifest -Root $Root
    if (-not [string]::Equals([string]$actual.filesSha256, [string]$Expected.filesSha256, [StringComparison]::OrdinalIgnoreCase) -or
        [int]$actual.fileCount -ne [int]$Expected.fileCount) {
        Throw-Ctl 4 'PACKAGE_CHANGED' ($Context + ': Player package changed (expected ' + [string]$Expected.filesSha256 +
            ' / ' + [string]$Expected.fileCount + ' files, actual ' + [string]$actual.filesSha256 + ' / ' + [string]$actual.fileCount + ' files).')
    }
    return $actual
}

function Get-PluginClosure {
    $pluginRoot = Join-Path $script:RepoRoot 'unity\Mahjong.Unity\Assets\Plugins\Mahjong'
    if (-not [IO.Directory]::Exists($pluginRoot)) {
        return [pscustomobject]@{ root = $pluginRoot; fileCount = 0; filesSha256 = $null; files = @() }
    }
    $records = New-Object System.Collections.Generic.List[object]
    foreach ($file in @(Get-ChildItem -LiteralPath $pluginRoot -Recurse -File -ErrorAction Stop)) {
        $relative = Get-RelativePath -Base $pluginRoot -Path $file.FullName
        $sig = Get-FileSig -Path $file.FullName
        $records.Add([pscustomobject]@{ path = $relative; bytes = [long]$sig.bytes; sha256 = [string]$sig.sha256 }) | Out-Null
    }
    $array = @($records.ToArray() | Sort-Object -Property path -CaseSensitive)
    $lines = @($array | ForEach-Object { $_.path + '|' + $_.bytes + '|' + $_.sha256 })
    [pscustomobject]@{
        root = $pluginRoot
        fileCount = $array.Count
        filesSha256 = Get-Sha256Text -Text ($lines -join "`n")
        files = $array
    }
}

function Get-CommandText {
    param([string[]]$Arguments)
    $parts = @()
    foreach ($arg in $Arguments) {
        $text = [string]$arg
        if ($text.Length -eq 0) { $parts += '""'; continue }
        if ($text -match '[\s"]') {
            $parts += ('"' + ($text -replace '"', '\"') + '"')
        }
        else { $parts += $text }
    }
    return ($parts -join ' ')
}

function Get-OwnNativeExitCode {
    param([System.Diagnostics.Process]$Process)
    if (-not ('P45Ctl.NativeExit' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace P45Ctl {
    public static class NativeExit {
        private const uint QueryLimitedInformation = 0x1000;
        private const uint StillActive = 259;
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
        public static int? ByHandle(IntPtr handle) {
            if (handle == IntPtr.Zero) { return null; }
            uint code;
            if (!GetExitCodeProcess(handle, out code)) { return null; }
            if (code == StillActive) { return null; }
            return unchecked((int)code);
        }
        public static int? ByPid(int processId) {
            IntPtr handle = OpenProcess(QueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero) { return null; }
            try { return ByHandle(handle); } finally { CloseHandle(handle); }
        }
    }
}
'@ -ErrorAction Stop
    }
    $handle = [IntPtr]::Zero
    try { $handle = $Process.Handle } catch { $handle = [IntPtr]::Zero }
    if ($handle -ne [IntPtr]::Zero) {
        try { $value = [P45Ctl.NativeExit]::ByHandle($handle) } catch { $value = $null }
        if ($null -ne $value) { return [int]$value }
    }
    try {
        $value = [P45Ctl.NativeExit]::ByPid([int]$Process.Id)
        if ($null -ne $value) { return [int]$value }
    }
    catch { }
    try { return [int]$Process.ExitCode } catch { return $null }
}

function Get-OwnProcessIdentity {
    param([System.Diagnostics.Process]$Process)
    $start = $null
    for ($i = 0; $i -lt 20; $i++) {
        try { $start = $Process.StartTime.ToUniversalTime().ToString('o', [Globalization.CultureInfo]::InvariantCulture); break }
        catch {
            Start-Sleep -Milliseconds 50
            try { $Process.Refresh() } catch { break }
            if ($Process.HasExited) { break }
        }
    }
    $path = $null
    try { $path = $Process.MainModule.FileName } catch { $path = $null }
    if ([string]::IsNullOrWhiteSpace($path)) {
        try { $path = (Get-Process -Id $Process.Id -ErrorAction Stop).Path } catch { $path = $null }
    }
    if (-not [string]::IsNullOrWhiteSpace($path)) { $path = (Get-FullPath $path) }
    [pscustomobject]@{
        pid = [int]$Process.Id
        startTimeUtc = $start
        processPath = $path
        processSha256 = if ([string]::IsNullOrWhiteSpace($path)) { $null } else { Get-Sha256File -Path $path }
        hasExited = [bool]$Process.HasExited
    }
}

function Get-CommandLineForPid {
    param([int]$ProcessId)
    try {
        $process = Get-CimInstance -ClassName Win32_Process -Filter ('ProcessId=' + $ProcessId) -ErrorAction Stop
        if ($null -eq $process) { return $null }
        return [string]$process.CommandLine
    }
    catch { return $null }
}

function Test-LiveIdentity {
    param(
        [int]$ProcessId,
        [string]$ExpectedStartUtc,
        [string]$ExpectedPath,
        [string]$ExpectedSha256
    )
    try { $process = Get-Process -Id $ProcessId -ErrorAction Stop }
    catch { return [pscustomobject]@{ Exists = $false; Reason = 'PROCESS_NOT_FOUND' } }
    $actualPath = $null
    try { $actualPath = $process.Path } catch { $actualPath = $null }
    if ([string]::IsNullOrWhiteSpace($actualPath)) {
        return [pscustomobject]@{ Exists = $false; Reason = 'PROCESS_PATH_UNAVAILABLE' }
    }
    if (-not [string]::Equals((Get-FullPath $actualPath), (Get-FullPath $ExpectedPath), [StringComparison]::OrdinalIgnoreCase)) {
        return [pscustomobject]@{ Exists = $false; Reason = 'PROCESS_PATH_MISMATCH' }
    }
    $start = $null
    try { $start = $process.StartTime.ToUniversalTime().ToString('o', [Globalization.CultureInfo]::InvariantCulture) } catch { $start = $null }
    if ([string]::IsNullOrWhiteSpace($start) -or
        -not [string]::Equals($start, $ExpectedStartUtc, [StringComparison]::Ordinal)) {
        return [pscustomobject]@{ Exists = $false; Reason = 'PROCESS_START_TIME_MISMATCH' }
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedSha256)) {
        $actualHash = Get-Sha256File -Path $actualPath
        if (-not [string]::Equals([string]$actualHash, $ExpectedSha256, [StringComparison]::OrdinalIgnoreCase)) {
            return [pscustomobject]@{ Exists = $false; Reason = 'PROCESS_EXE_HASH_MISMATCH' }
        }
    }
    return [pscustomobject]@{ Exists = $true; Reason = 'MATCH' }
}

function Copy-StableEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [ValidateSet('Read', 'ReadWrite')][string]$ShareMode = 'Read',
        [int]$HoldMilliseconds = 0
    )
    if (-not [IO.File]::Exists($Source)) { Throw-Ctl 17 'EVIDENCE_MISSING' ('Artifact disappeared before sealing: ' + $Source) }
    $sourceRoot = if ($Source.StartsWith($script:DataRoot, [StringComparison]::OrdinalIgnoreCase)) { $script:DataRoot }
        elseif ($Source.StartsWith($script:CaseRoot, [StringComparison]::OrdinalIgnoreCase)) { $script:CaseRoot }
        else { $script:OwnershipRoot }
    $null = Assert-OwnedRegularFile -Path $Source -Root $sourceRoot -Context 'seal source' -AllowHardLink
    Assert-OwnedWriteParent -Path $Destination -Context 'seal destination'
    [IO.Directory]::CreateDirectory((Split-Path -Parent $Destination)) | Out-Null
    if ([IO.File]::Exists($Destination)) { Throw-Ctl 3 'EVIDENCE_REUSE' ('Refusing to overwrite a sealed artifact: ' + $Destination) }

    $before = Get-FileSig -Path $Source
    $share = if ($ShareMode -eq 'ReadWrite') { [IO.FileShare]::ReadWrite } else { [IO.FileShare]::Read }
    $sourceStream = [IO.File]::Open($Source, [IO.FileMode]::Open, [IO.FileAccess]::Read, $share)
    try {
        $destHandle = [P45Ctl.Ownership]::CreateNewDelete($Destination)
        if ($null -eq $destHandle -or $destHandle.IsInvalid) {
            throw (New-Object System.ComponentModel.Win32Exception([System.Runtime.InteropServices.Marshal]::GetLastWin32Error()))
        }
        $destStream = New-Object System.IO.FileStream($destHandle, [IO.FileAccess]::Write, 1048576, $false)
    }
    catch {
        Throw-Ctl 19 'RESOURCE_ALIAS' ('seal destination already exists or is an alias: ' + $Destination)
    }
    $null = Test-OwnedHandle -Stream $destStream -Context 'seal destination handle'
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $buffer = New-Object byte[] 1048576
        while ($true) {
            $read = $sourceStream.Read($buffer, 0, $buffer.Length)
            if ($read -le 0) { break }
            $null = $sha.TransformBlock($buffer, 0, $read, $null, 0)
            $destStream.Write($buffer, 0, $read)
        }
        $null = $sha.TransformFinalBlock((New-Object byte[] 0), 0, 0)
        $copiedHash = ([BitConverter]::ToString($sha.Hash)).Replace('-', '').ToUpperInvariant()
        $destStream.Flush($true)
        $copiedBytes = [long]$destStream.Length
    }
    catch {
        try { $destStream.Dispose() } catch { }
        throw
    }
    finally {
        $sha.Dispose()
        $sourceStream.Dispose()
    }
    if ($HoldMilliseconds -gt 0) { Start-Sleep -Milliseconds $HoldMilliseconds }
    $after = Get-FileSig -Path $Source
    if ([string]::Equals($copiedHash, '0000000000000000000000000000000000000000000000000000000000000000', [StringComparison]::Ordinal)) {
        $cleanupError = $null
        try { [P45Ctl.Ownership]::MarkDelete($destStream.SafeFileHandle) } catch { $cleanupError = Get-ErrText $_ }
        try { $destStream.Dispose() } catch { }
        if ($null -ne $cleanupError) { Throw-Ctl 18 'COPY_CLEANUP_FAILED' ('Empty copy cleanup failed: ' + $cleanupError) }
        Throw-Ctl 18 'EVIDENCE_INVALID' ('The stable copy produced an empty hash: ' + $Source)
    }
    if ($before.bytes -ne $after.bytes -or
        -not [string]::Equals([string]$before.sha256, [string]$after.sha256, [StringComparison]::OrdinalIgnoreCase) -or
        $copiedBytes -ne $after.bytes -or
        -not [string]::Equals([string]$copiedHash, [string]$after.sha256, [StringComparison]::OrdinalIgnoreCase)) {
        # The destination is deleted through the exact validated, DELETE-capable handle that wrote it.
        $cleanupError = $null
        try { [P45Ctl.Ownership]::MarkDelete($destStream.SafeFileHandle) } catch { $cleanupError = Get-ErrText $_ }
        try { $destStream.Dispose() } catch { }
        if ($null -ne $cleanupError) { Throw-Ctl 18 'COPY_CLEANUP_FAILED' ('Unstable copy cleanup failed: ' + $cleanupError) }
        Throw-Ctl 18 'COPY_UNSTABLE' ('Artifact changed while being sealed: ' + $Source)
    }
    try { $destStream.Dispose() } catch { }
    [pscustomobject]@{
        sourcePath = $Source
        sealedPath = $Destination
        bytes = [long]$after.bytes
        sha256 = [string]$after.sha256
        beforeSha256 = [string]$before.sha256
        afterSha256 = [string]$after.sha256
        stable = $true
    }
}

function Invoke-FixedVerifier {
    param(
        [string]$AuthorityPath,
        [string]$CheatStatePath,
        [string]$OutputDirectory,
        [string]$Prefix
    )
    [IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
    $stdoutPath = Join-Path $OutputDirectory ($Prefix + '.stdout.log')
    $stderrPath = Join-Path $OutputDirectory ($Prefix + '.stderr.log')
    $exitPath = Join-Path $OutputDirectory ($Prefix + '.exit.txt')
    $verifierArgs = @($script:VerifierResolvedPath, $AuthorityPath)
    if (-not [string]::IsNullOrWhiteSpace($CheatStatePath)) { $verifierArgs += $CheatStatePath }
    $exe = $script:DotnetResolvedPath
    if ($script:VerifierResolvedPath -match '\.exe$') {
        $exe = $script:VerifierResolvedPath
        $verifierArgs = @($AuthorityPath)
        if (-not [string]::IsNullOrWhiteSpace($CheatStatePath)) { $verifierArgs += $CheatStatePath }
    }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = Get-CommandText -Arguments $verifierArgs
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $proc = $null
    try { $proc = [System.Diagnostics.Process]::Start($psi) }
    catch { Throw-Ctl 18 'VERIFIER_FAILED' ('Could not start the fixed verifier: ' + (Get-ErrText $_)) }
    if ($null -eq $proc) { Throw-Ctl 18 'VERIFIER_FAILED' 'The fixed verifier process could not be started.' }
    $outTask = $proc.StandardOutput.ReadToEndAsync()
    $errTask = $proc.StandardError.ReadToEndAsync()
    $proc.WaitForExit()
    $text = [string]$outTask.Result
    $stderrText = [string]$errTask.Result
    $exit = [int]$proc.ExitCode
    $proc.Dispose()
    Write-OwnedText -Path $stdoutPath -Text $text
    Write-OwnedText -Path $stderrPath -Text $stderrText
    Write-OwnedText -Path $exitPath -Text ([string]$exit + [Environment]::NewLine)
    $parsed = $null
    try { $parsed = $text | ConvertFrom-Json } catch { $parsed = $null }
    [pscustomobject]@{
        exitCode = [int]$exit
        stdoutPath = $stdoutPath
        stderrPath = $stderrPath
        exitPath = $exitPath
        stdoutSha256 = Get-Sha256File -Path $stdoutPath
        stderrSha256 = Get-Sha256File -Path $stderrPath
        parsed = $parsed
        ok = ($exit -eq 0 -and $null -ne $parsed -and [bool](Get-Prop $parsed 'ok' $false))
    }
}

function Wait-Export {
    param([string]$Directory, [System.Diagnostics.Process]$Process, [int]$Seconds)
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    $donePath = Join-Path $Directory 'export.done'
    $exportPath = Join-Path $Directory 'export.json'
    $authorityPath = Join-Path $Directory 'authority.save'
    $cheatPath = Join-Path $Directory 'cheat-state.bin'
    $errorPath = Join-Path $Directory 'export.error.json'
    while ([DateTime]::UtcNow -lt $deadline) {
        if ([IO.File]::Exists($errorPath)) {
            return [pscustomobject]@{ status = 'ERROR'; reason = 'EXPORT_ERROR_FILE'; errorPath = $errorPath }
        }
        if ([IO.File]::Exists($donePath) -and [IO.File]::Exists($exportPath) -and
            [IO.File]::Exists($authorityPath) -and [IO.File]::Exists($cheatPath)) {
            $doneText = Get-TextShared -Path $donePath
            $exportSha = Get-Sha256Shared -Path $exportPath
            if ($null -eq $doneText -or $null -eq $exportSha) { Start-Sleep -Milliseconds 50; continue }
            $done = $doneText.Trim()
            $expected = $exportSha + '  export.json'
            if (-not [string]::Equals($done, $expected, [StringComparison]::OrdinalIgnoreCase)) {
                return [pscustomobject]@{ status = 'INVALID'; reason = 'EXPORT_DONE_MISMATCH'; errorPath = $null }
            }
            return [pscustomobject]@{ status = 'DONE'; reason = 'DONE'; errorPath = $null }
        }
        if ($Process.HasExited) {
            if ([IO.File]::Exists($errorPath)) {
                return [pscustomobject]@{ status = 'ERROR'; reason = 'EXPORT_ERROR_FILE'; errorPath = $errorPath }
            }
            return [pscustomobject]@{ status = 'EXITED_NO_EXPORT'; reason = 'PROCESS_EXITED_WITHOUT_EXPORT'; errorPath = $null }
        }
        Start-Sleep -Milliseconds 100
    }
    return [pscustomobject]@{ status = 'TIMEOUT'; reason = 'EXPORT_TIMEOUT'; errorPath = $null }
}

function Complete-OwnedProcess {
    param(
        [System.Diagnostics.Process]$Process,
        [string]$Mode,
        [int]$Seconds,
        [string]$ExpectedStartUtc,
        [string]$ExpectedPath,
        [string]$ExpectedSha256
    )
    $force = ($Mode -eq 'Force')
    if ($Process.HasExited) {
        $native = $null
        $native = Get-OwnNativeExitCode -Process $Process
        try { $Process.Dispose() } catch { }
        $disposition = if ($force) { 'EXITED_BEFORE_FORCE' } elseif ($native -eq 0) { 'EXITED_OK' } else { 'EXITED_NONZERO' }
        return [pscustomobject]@{
            disposition = $disposition
            nativeExitCode = $native
            requested = $false
            killReason = $null
            handleClosed = $true
        }
    }
    if (-not $force) {
        $exited = $false
        try { $exited = $Process.WaitForExit($Seconds * 1000) } catch { $exited = $false }
        if (-not $exited) {
            try { $Process.Kill() } catch { }
            try { [void]$Process.WaitForExit(5000) } catch { }
            $nativeTimeout = $null
            $nativeTimeout = Get-OwnNativeExitCode -Process $Process
            try { $Process.Dispose() } catch { }
            return [pscustomobject]@{
                disposition = 'TIMEOUT_KILLED_OWN_PROCESS'
                nativeExitCode = $nativeTimeout
                requested = $true
                killReason = 'TIMEOUT'
                handleClosed = $true
            }
        }
        $native = $null
        $native = Get-OwnNativeExitCode -Process $Process
        try { $Process.Dispose() } catch { }
        return [pscustomobject]@{
            disposition = if ($native -eq 0) { 'EXITED_OK' } else { 'EXITED_NONZERO' }
            nativeExitCode = $native
            requested = $false
            killReason = $null
            handleClosed = $true
        }
    }

    $identity = Test-LiveIdentity -ProcessId $Process.Id -ExpectedStartUtc $ExpectedStartUtc `
        -ExpectedPath $ExpectedPath -ExpectedSha256 $ExpectedSha256
    if (-not $identity.Exists) {
        Throw-Ctl 19 'OWNED_PROCESS_IDENTITY' ('Refusing to kill the owned process after identity re-check: ' + $identity.Reason)
    }
    try { $Process.Kill() }
    catch { Throw-Ctl 13 'STOP_FAILED' ('Could not kill the owned process: ' + (Get-ErrText $_)) }
    try { [void]$Process.WaitForExit(10000) } catch { }
    $native = $null
    $native = Get-OwnNativeExitCode -Process $Process
    try { $Process.Dispose() } catch { }
    return [pscustomobject]@{
        disposition = 'FORCE_KILLED'
        nativeExitCode = $native
        requested = $true
        killReason = 'TOOL_FORCE'
        handleClosed = $true
    }
}

function Initialize-OwnershipNative {
    if ('P45Ctl.Ownership' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
namespace P45Ctl {
    [StructLayout(LayoutKind.Sequential)]
    public struct ByHandleFileInformation {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
    public sealed class PathIdentity {
        public string FinalPath;
        public uint Attributes;
        public bool IsReparse;
        public bool IsDirectory;
        public uint NumberOfLinks;
        public ulong VolumeSerial;
        public ulong FileIndex;
    }
    public static class Ownership {
        private const uint FileReadAttributes = 0x80;
        private const uint GenericWrite = 0x40000000;
        private const uint DeleteAccess = 0x00010000;
        private const uint ShareRead = 0x1;
        private const uint ShareWrite = 0x2;
        private const uint ShareDelete = 0x4;
        private const uint OpenExisting = 3;
        private const uint BackupSemantics = 0x02000000;
        private const uint OpenReparsePoint = 0x00200000;
        private const uint ReparsePointAttribute = 0x400;
        private const int FileDispositionInfoClass = 4;
        [StructLayout(LayoutKind.Sequential)]
        private struct FileDispositionInfo {
            [MarshalAs(UnmanagedType.U1)]
            public bool DeleteFile;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
            uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder buffer,
            uint length, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass,
            ref FileDispositionInfo info, int size);
        public static SafeFileHandle OpenDelete(string path) {
            return CreateFileW(path, DeleteAccess | FileReadAttributes,
                ShareRead | ShareWrite | ShareDelete, IntPtr.Zero, OpenExisting,
                BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        }
        public static SafeFileHandle CreateNewDelete(string path) {
            // CREATE_NEW=1, no sharing, write + read attributes + delete in the same handle.
            return CreateFileW(path, GenericWrite | DeleteAccess | FileReadAttributes,
                0, IntPtr.Zero, 1, 0, IntPtr.Zero);
        }
        public static void MarkDelete(SafeFileHandle handle) {
            var info = new FileDispositionInfo();
            info.DeleteFile = true;
            if (!SetFileInformationByHandle(handle, FileDispositionInfoClass, ref info,
                Marshal.SizeOf(typeof(FileDispositionInfo)))) {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        private static SafeFileHandle Open(string path, uint extraFlags) {
            return CreateFileW(path, FileReadAttributes, ShareRead | ShareWrite | ShareDelete, IntPtr.Zero,
                OpenExisting, BackupSemantics | extraFlags, IntPtr.Zero);
        }
        public static PathIdentity Inspect(string path) {
            using (SafeFileHandle link = Open(path, OpenReparsePoint)) {
                if (link.IsInvalid) { return null; }
                ByHandleFileInformation linkInfo;
                if (!GetFileInformationByHandle(link, out linkInfo)) { return null; }
                if ((linkInfo.FileAttributes & ReparsePointAttribute) != 0) {
                    var reparse = new PathIdentity();
                    reparse.FinalPath = Strip(GetFinalPath(link));
                    reparse.Attributes = linkInfo.FileAttributes;
                    reparse.IsReparse = true;
                    reparse.IsDirectory = (linkInfo.FileAttributes & 0x10) != 0;
                    reparse.NumberOfLinks = linkInfo.NumberOfLinks;
                    reparse.VolumeSerial = linkInfo.VolumeSerialNumber;
                    reparse.FileIndex = ((ulong)linkInfo.FileIndexHigh << 32) | linkInfo.FileIndexLow;
                    return reparse;
                }
            }
            using (SafeFileHandle handle = Open(path, 0)) {
                if (handle.IsInvalid) { return null; }
                ByHandleFileInformation info;
                if (!GetFileInformationByHandle(handle, out info)) { return null; }
                var result = new PathIdentity();
                result.FinalPath = Strip(GetFinalPath(handle));
                result.Attributes = info.FileAttributes;
                result.IsReparse = (info.FileAttributes & ReparsePointAttribute) != 0;
                result.IsDirectory = (info.FileAttributes & 0x10) != 0;
                result.NumberOfLinks = info.NumberOfLinks;
                result.VolumeSerial = info.VolumeSerialNumber;
                result.FileIndex = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
                return result;
            }
        }
        public static PathIdentity InspectHandle(SafeFileHandle handle) {
            if (handle == null || handle.IsInvalid) { return null; }
            ByHandleFileInformation info;
            if (!GetFileInformationByHandle(handle, out info)) { return null; }
            var result = new PathIdentity();
            result.FinalPath = Strip(GetFinalPath(handle));
            result.Attributes = info.FileAttributes;
            result.IsReparse = (info.FileAttributes & ReparsePointAttribute) != 0;
            result.IsDirectory = (info.FileAttributes & 0x10) != 0;
            result.NumberOfLinks = info.NumberOfLinks;
            result.VolumeSerial = info.VolumeSerialNumber;
            result.FileIndex = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
            return result;
        }
        private static string GetFinalPath(SafeFileHandle handle) {
            var buffer = new StringBuilder(32768);
            uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity) { return string.Empty; }
            return buffer.ToString(0, (int)length);
        }
        private static string Strip(string value) {
            if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) {
                return @"\\" + value.Substring(8);
            }
            if (value.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)) {
                return value.Substring(4);
            }
            return value;
        }
    }
}
'@ -ErrorAction Stop
}

function Get-PathIdentity {
    param([string]$Path)
    Initialize-OwnershipNative
    if (-not [IO.File]::Exists($Path) -and -not [IO.Directory]::Exists($Path)) { return $null }
    return [P45Ctl.Ownership]::Inspect((Get-FullPath $Path))
}

function Assert-NoReparseAncestors {
    param([string]$Path, [string]$Root, [string]$Context)
    $rootFull = Get-FullPath $Root
    $targetFull = Get-FullPath $Path
    $rootInfo = Get-PathIdentity -Path $rootFull
    if ($null -eq $rootInfo) { Throw-Ctl 19 'RESOURCE_ALIAS' ($Context + ': ownership root is missing: ' + $rootFull) }
    if ($rootInfo.IsReparse) { Throw-Ctl 19 'RESOURCE_ALIAS' ($Context + ': ownership root is a reparse point: ' + $rootFull) }
    $relative = if ($targetFull.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
        $targetFull.Substring($rootFull.Length).TrimStart('\', '/')
    }
    else { $targetFull }
    $current = $rootFull
    if (-not [string]::IsNullOrWhiteSpace($relative)) {
        foreach ($part in ($relative -split '[\\/]')) {
            if ([string]::IsNullOrWhiteSpace($part)) { continue }
            $current = Join-Path $current $part
            $info = Get-PathIdentity -Path $current
            if ($null -eq $info) { break }
            if ($info.IsReparse) { Throw-Ctl 19 'RESOURCE_ALIAS' ($Context + ': reparse/junction in path: ' + $current) }
        }
    }
}

function Assert-OwnedDirectory {
    param([string]$Path, [string]$Root, [string]$Context, [switch]$Exact)
    $targetFull = Get-FullPath $Path
    Assert-NoReparseAncestors -Path $targetFull -Root $Root -Context $Context
    $info = Get-PathIdentity -Path $targetFull
    if ($null -eq $info -or -not $info.IsDirectory) {
        Throw-Ctl 19 'RESOURCE_ALIAS' ($Context + ': expected an owned directory: ' + $targetFull)
    }
    if ($info.IsReparse) { Throw-Ctl 19 'RESOURCE_ALIAS' ($Context + ': directory is a reparse point: ' + $targetFull) }
    $rootInfo = Get-PathIdentity -Path (Get-FullPath $Root)
    if ($null -eq $rootInfo) { Throw-Ctl 19 'RESOURCE_ALIAS' ($Context + ': ownership root is missing.') }
    if (-not [string]::Equals($info.FinalPath.TrimEnd('\', '/'), $targetFull.TrimEnd('\', '/'), [StringComparison]::OrdinalIgnoreCase)) {
        Throw-Ctl 19 'RESOURCE_ALIAS' ($Context + ': directory final path is not the expected path: ' + $info.FinalPath + ' expected ' + $targetFull)
    }
    $finalText = $info.FinalPath.TrimEnd('\', '/'); $rootText = $rootInfo.FinalPath.TrimEnd('\', '/');
    if (-not ($finalText -eq $rootText -or $finalText.StartsWith($rootText + '\', [StringComparison]::OrdinalIgnoreCase))) {
        Throw-Ctl 19 'RESOURCE_ALIAS' ($Context + ': directory escapes ownership root: ' + $info.FinalPath)
    }
    return $info
}

function Assert-OwnedRegularFile {
    param([string]$Path, [string]$Root, [string]$Context, [switch]$AllowMissing, [switch]$AllowHardLink)
    $targetFull = Get-FullPath $Path
    if (-not [IO.File]::Exists($targetFull)) {
        if ($AllowMissing) { return $null }
        Throw-Ctl 17 'SAVE_MISSING' ($Context + ': file is missing: ' + $targetFull)
    }
    Assert-NoReparseAncestors -Path $targetFull -Root $Root -Context $Context
    $info = Get-PathIdentity -Path $targetFull
    if ($null -eq $info -or $info.IsDirectory) { Throw-Ctl 19 'RESOURCE_ALIAS' ($Context + ': expected a regular file: ' + $targetFull) }
    if ($info.IsReparse) { Throw-Ctl 19 'RESOURCE_ALIAS' ($Context + ': file is a reparse point: ' + $targetFull) }
    $rootInfo = Get-PathIdentity -Path (Get-FullPath $Root)
    if ($null -eq $rootInfo -or $rootInfo.IsReparse) { Throw-Ctl 19 'RESOURCE_ALIAS' ($Context + ': ownership root is not a regular directory.') }
    $finalText = $info.FinalPath.TrimEnd('\', '/'); $rootText = $rootInfo.FinalPath.TrimEnd('\', '/');
    if (-not ($finalText -eq $rootText -or $finalText.StartsWith($rootText + '\', [StringComparison]::OrdinalIgnoreCase))) {
        Throw-Ctl 19 'RESOURCE_ALIAS' ($Context + ': file escapes ownership root: ' + $info.FinalPath)
    }
    if (-not $AllowHardLink -and $info.NumberOfLinks -ne 1) {
        Throw-Ctl 19 'RESOURCE_ALIAS' ($Context + ': file is hard-linked (' + $info.NumberOfLinks + ' links): ' + $targetFull)
    }
    return $info
}

function Assert-OwnedWriteParent {
    param([string]$Path, [string]$Context)
    if ([string]::IsNullOrWhiteSpace($script:OwnershipRoot)) { return }
    if (-not [IO.Directory]::Exists($script:OwnershipRoot)) { return }
    $parent = Split-Path -Parent (Get-FullPath $Path)
    while (-not [IO.Directory]::Exists($parent)) {
        $next = Split-Path -Parent $parent
        if ([string]::IsNullOrWhiteSpace($next) -or $next -eq $parent) { break }
        $parent = $next
    }
    Assert-NoReparseAncestors -Path $parent -Root $script:OwnershipRoot -Context $Context
    $info = Get-PathIdentity -Path $parent
    if ($null -eq $info -or -not $info.IsDirectory -or $info.IsReparse) {
        Throw-Ctl 19 'RESOURCE_ALIAS' ($Context + ': write parent is not an owned directory: ' + $parent)
    }
    $rootInfo = Get-PathIdentity -Path $script:OwnershipRoot
    $finalText = if ($null -eq $info) { '' } else { $info.FinalPath.TrimEnd('\', '/') }
    $rootText = if ($null -eq $rootInfo) { '' } else { $rootInfo.FinalPath.TrimEnd('\', '/') }
    if ($null -eq $rootInfo -or $rootInfo.IsReparse -or
        -not ($finalText -eq $rootText -or ($rootText -ne '' -and $finalText.StartsWith($rootText + '\', [StringComparison]::OrdinalIgnoreCase)))) {
        Throw-Ctl 19 'RESOURCE_ALIAS' ($Context + ': write parent escapes ownership root: ' + $finalText)
    }
}

function Assert-OwnedDataDomain {
    param([string]$DataDirectory, [string]$EvidenceRoot, [string]$Context)
    $null = Assert-OwnedDirectory -Path $DataDirectory -Root $EvidenceRoot -Context $Context -Exact
    foreach ($name in @('local-main.save', 'local-main.save.prev')) {
        $path = Join-Path $DataDirectory $name
        if ([IO.File]::Exists($path)) {
            $null = Assert-OwnedRegularFile -Path $path -Root $DataDirectory -Context ($Context + ' ' + $name)
        }
    }
}

function Apply-SaveFault {
    param([string]$DataDirectory, [string]$Fault)
    $primary = Join-Path $DataDirectory 'local-main.save'
    $backup = Join-Path $DataDirectory 'local-main.save.prev'
    if ([string]::IsNullOrWhiteSpace($script:OwnershipRoot)) { Throw-Ctl 19 'RESOURCE_ALIAS' 'The evidence ownership root is not initialized.' }
    $evidenceRoot = $script:OwnershipRoot
    Assert-OwnedDataDomain -DataDirectory $DataDirectory -EvidenceRoot $evidenceRoot -Context 'before save fault'
    if (-not [IO.File]::Exists($primary)) { Throw-Ctl 17 'SAVE_MISSING' 'The primary save disappeared before the fault operation.' }
    if (-not [IO.File]::Exists($backup)) { Throw-Ctl 17 'SAVE_MISSING' 'The seeded backup save disappeared before the fault operation.' }
    $applied = $Fault
    switch ($Fault) {
        'None' { }
        'TruncatePrimary' {
            $bytes = Read-OwnedBytes -Path $primary -Context 'read truncate primary'
            $keep = [Math]::Max(1, [int]($bytes.Length / 2))
            $out = New-Object byte[] $keep
            [Array]::Copy($bytes, $out, $keep)
            Write-OwnedBytes -Path $primary -Bytes $out
        }
        'BadChecksumPrimary' {
            $bytes = Read-OwnedBytes -Path $primary -Context 'read checksum primary'
            if ($bytes.Length -le 44) { Throw-Ctl 18 'SAVE_FORMAT' 'The baseline save is too short for a checksum fault.' }
            $bytes[44] = [byte]($bytes[44] -bxor 0x5A)
            Write-OwnedBytes -Path $primary -Bytes $bytes
        }
        'IncompatiblePrimary' {
            $bytes = Read-OwnedBytes -Path $primary -Context 'read incompatible primary'
            if ($bytes.Length -lt 44) { Throw-Ctl 18 'SAVE_FORMAT' 'The baseline save is too short for a version fault.' }
            $bytes[4] = 2; $bytes[5] = 0; $bytes[6] = 0; $bytes[7] = 0
            Write-OwnedBytes -Path $primary -Bytes $bytes
        }
        'MissingPrimaryWithBackup' {
            Remove-OwnedFile -Path $primary -Context 'delete primary'
        }
        'BothMissing' {
            Remove-OwnedFile -Path $primary -Context 'delete primary'
            Remove-OwnedFile -Path $backup -Context 'delete backup'
        }
        'BothBad' {
            $bytes = Read-OwnedBytes -Path $primary -Context 'read corrupt primary'
            $keep = [Math]::Max(1, [int]($bytes.Length / 3))
            $out = New-Object byte[] $keep
            [Array]::Copy($bytes, $out, $keep)
            Write-OwnedBytes -Path $primary -Bytes $out
            $backupBytes = Read-OwnedBytes -Path $backup -Context 'read corrupt backup'
            $backupBytes[44] = [byte]($backupBytes[44] -bxor 0xA5)
            Write-OwnedBytes -Path $backup -Bytes $backupBytes
        }
        default { Throw-Ctl 2 'USAGE' ('Unsupported save fault: ' + $Fault) }
    }
    [pscustomobject]@{ fault = $applied; primaryPath = $primary; backupPath = $backup }
}

function Test-SaveEnvelope {
    param([string]$Path)
    if (-not [IO.File]::Exists($Path)) { return $false }
    try {
        $bytes = [IO.File]::ReadAllBytes($Path)
        if ($bytes.Length -lt 44) { return $false }
        $magic = [BitConverter]::ToUInt32($bytes, 0)
        $version = [BitConverter]::ToInt32($bytes, 4)
        $length = [BitConverter]::ToInt32($bytes, 8)
        if ($magic -ne 0x4C483450 -or $version -ne 1 -or $length -lt 0) { return $false }
        if ($bytes.Length -ne (44 + $length)) { return $false }
        $expected = New-Object byte[] 32
        [Array]::Copy($bytes, 12, $expected, 0, 32)
        $payload = New-Object byte[] $length
        [Array]::Copy($bytes, 44, $payload, 0, $length)
        $actual = [Security.Cryptography.SHA256]::Create()
        try { $hash = $actual.ComputeHash($payload) } finally { $actual.Dispose() }
        return ([BitConverter]::ToString($hash)).Replace('-', '').ToUpperInvariant() -eq
            ([BitConverter]::ToString($expected)).Replace('-', '').ToUpperInvariant()
    }
    catch { return $false }
}

function Get-SaveObservation {
    param([string]$DataDirectory, [string]$CheatStatePath, [string]$VerifierOutputDirectory, [string]$Prefix)
    $primary = Join-Path $DataDirectory 'local-main.save'
    $backup = Join-Path $DataDirectory 'local-main.save.prev'
    $primarySig = Get-FileSig -Path $primary
    $backupSig = Get-FileSig -Path $backup
    $primaryVerifier = $null
    $backupVerifier = $null
    $primaryValid = $false
    $backupValid = $false
    if (-not [string]::IsNullOrWhiteSpace($CheatStatePath) -and [IO.File]::Exists($CheatStatePath)) {
        if ($null -ne $primarySig) {
            $primaryVerifier = Invoke-FixedVerifier -AuthorityPath $primary -CheatStatePath $CheatStatePath `
                -OutputDirectory $VerifierOutputDirectory -Prefix ($Prefix + '-primary')
            $primaryValid = [bool]$primaryVerifier.ok
        }
        if ($null -ne $backupSig) {
            $backupVerifier = Invoke-FixedVerifier -AuthorityPath $backup -CheatStatePath $CheatStatePath `
                -OutputDirectory $VerifierOutputDirectory -Prefix ($Prefix + '-backup')
            $backupValid = [bool]$backupVerifier.ok
        }
    }
    else {
        $primaryValid = Test-SaveEnvelope -Path $primary
        $backupValid = Test-SaveEnvelope -Path $backup
    }
    $primaryVerifierExit = if ($null -eq $primaryVerifier) { $null } else { [int]$primaryVerifier.exitCode }
    $backupVerifierExit = if ($null -eq $backupVerifier) { $null } else { [int]$backupVerifier.exitCode }
    $primaryBytes = if ($null -eq $primarySig) { 0 } else { [long]$primarySig.bytes }
    $backupBytes = if ($null -eq $backupSig) { 0 } else { [long]$backupSig.bytes }
    $primarySha = if ($null -eq $primarySig) { $null } else { [string]$primarySig.sha256 }
    $backupSha = if ($null -eq $backupSig) { $null } else { [string]$backupSig.sha256 }
    [pscustomobject]@{
        primary = [pscustomobject]@{ exists = ($null -ne $primarySig); bytes = $primaryBytes; sha256 = $primarySha; verifierExitCode = $primaryVerifierExit; valid = $primaryValid }
        backup = [pscustomobject]@{ exists = ($null -ne $backupSig); bytes = $backupBytes; sha256 = $backupSha; verifierExitCode = $backupVerifierExit; valid = $backupValid }
    }
}

function Get-ExpectedOutcome {
    param([string]$Fault)
    switch ($Fault) {
        'None' { return 'RECOVERED' }
        'TruncatePrimary' { return 'RECOVERED_FROM_BACKUP' }
        'BadChecksumPrimary' { return 'RECOVERED_FROM_BACKUP' }
        'IncompatiblePrimary' { return 'REJECTED' }
        'MissingPrimaryWithBackup' { return 'RECOVERED_FROM_BACKUP' }
        'BothMissing' { return 'FRESH_START' }
        'BothBad' { return 'REJECTED' }
        default { return 'UNKNOWN' }
    }
}

function Get-FactsSummary {
    param([object]$Facts)
    if ($null -eq $Facts) { return $null }
    [pscustomobject]@{
        roomId = [string](Get-Prop $Facts 'roomId')
        roundId = [string](Get-Prop $Facts 'roundId')
        rulesetVersion = Get-Prop $Facts 'rulesetVersion'
        stateVersion = [string](Get-Prop $Facts 'stateVersion')
        eventSeq = [string](Get-Prop $Facts 'eventSeq')
        stateFingerprint = [string](Get-Prop $Facts 'stateFingerprint')
        stateHash = [string](Get-Prop $Facts 'stateHash')
        controlVersion = [string](Get-Prop $Facts 'controlVersion')
        cheatVersion = [string](Get-Prop $Facts 'cheatVersion')
        cheatFingerprint = [string](Get-Prop $Facts 'cheatFingerprint')
        cheatStateSha256 = [string](Get-Prop $Facts 'cheatStateSha256')
        authoritySaveSha256 = [string](Get-Prop $Facts 'authoritySaveSha256')
        authoritySaveBytes = Get-Prop $Facts 'authoritySaveBytes'
        controlEpochs = @((Get-Prop $Facts 'controlEpochs' @()) | ForEach-Object { [string]$_ })
        requestCount = @((Get-Prop $Facts 'requests' @())).Count
        controlRequestCount = @((Get-Prop $Facts 'controlRequests' @())).Count
        ledgerPoints = [string](Get-Prop (Get-Prop $Facts 'ledger') 'points')
        ledgerEntryCount = [string](Get-Prop (Get-Prop $Facts 'ledger') 'entryCount')
        ledgerEntriesSha256 = [string](Get-Prop (Get-Prop $Facts 'ledger') 'entriesSha256')
        physicalTileCount = [string](Get-Prop (Get-Prop $Facts 'physical') 'tileCount')
        physicalUniqueCount = [string](Get-Prop (Get-Prop $Facts 'physical') 'uniqueCount')
        physicalTilesSha256 = [string](Get-Prop (Get-Prop $Facts 'physical') 'tilesSha256')
    }
}

function Get-FactsComparison {
    param([object]$Left, [object]$Right)
    $mismatches = New-Object System.Collections.Generic.List[string]
    if ($null -eq $Left -or $null -eq $Right) {
        return [pscustomobject]@{ equal = $false; mismatches = @('one or both facts are missing') }
    }
    foreach ($field in @('roomId', 'roundId', 'rulesetVersion', 'stateVersion', 'eventSeq',
            'stateFingerprint', 'stateHash', 'controlVersion', 'cheatVersion', 'cheatFingerprint',
            'cheatStateSha256')) {
        $a = [string](Get-Prop $Left $field)
        $b = [string](Get-Prop $Right $field)
        if (-not [string]::Equals($a, $b, [StringComparison]::OrdinalIgnoreCase)) {
            $mismatches.Add($field + ': before=' + $a + ' after=' + $b) | Out-Null
        }
    }
    $aEpochs = (@((Get-Prop $Left 'controlEpochs' @())) | ForEach-Object { [string]$_ }) -join ','
    $bEpochs = (@((Get-Prop $Right 'controlEpochs' @())) | ForEach-Object { [string]$_ }) -join ','
    if (-not [string]::Equals($aEpochs, $bEpochs, [StringComparison]::Ordinal)) {
        $mismatches.Add('controlEpochs: before=' + $aEpochs + ' after=' + $bEpochs) | Out-Null
    }
    $leftLedger = Get-Prop $Left 'ledger'
    $rightLedger = Get-Prop $Right 'ledger'
    foreach ($field in @('points', 'initialPoints', 'entryCount', 'entriesSha256', 'balancesSha256', 'totalVersion', 'checkpointVersion', 'source')) {
        $a = [string](Get-Prop $leftLedger $field)
        $b = [string](Get-Prop $rightLedger $field)
        if (-not [string]::Equals($a, $b, [StringComparison]::OrdinalIgnoreCase)) {
            $mismatches.Add('ledger.' + $field + ': before=' + $a + ' after=' + $b) | Out-Null
        }
    }
    $aBalances = (@((Get-Prop $leftLedger 'balances' @())) | ForEach-Object { [string]$_ }) -join ','
    $bBalances = (@((Get-Prop $rightLedger 'balances' @())) | ForEach-Object { [string]$_ }) -join ','
    if (-not [string]::Equals($aBalances, $bBalances, [StringComparison]::Ordinal)) {
        $mismatches.Add('ledger.balances: before=' + $aBalances + ' after=' + $bBalances) | Out-Null
    }
    $leftPhysical = Get-Prop $Left 'physical'
    $rightPhysical = Get-Prop $Right 'physical'
    foreach ($field in @('tileCount', 'uniqueCount', 'wallCount', 'deadWallCount', 'tilesSha256')) {
        $a = [string](Get-Prop $leftPhysical $field)
        $b = [string](Get-Prop $rightPhysical $field)
        if (-not [string]::Equals($a, $b, [StringComparison]::OrdinalIgnoreCase)) {
            $mismatches.Add('physical.' + $field + ': before=' + $a + ' after=' + $b) | Out-Null
        }
    }
    $requestLinesLeft = @((Get-Prop $Left 'requests' @()) | ForEach-Object {
            ([string](Get-Prop $_ 'accountId') + '|' + [string](Get-Prop $_ 'scopeId') + '|' + [string](Get-Prop $_ 'requestId') + '|' +
                [string](Get-Prop $_ 'receiptId') + '|' + [string](Get-Prop $_ 'confirmed') + '|' + [string](Get-Prop $_ 'status') + '|' +
                [string](Get-Prop $_ 'payloadSha256') + '|' + [string](Get-Prop $_ 'evaluatedStateVersion') + '|' +
                [string](Get-Prop $_ 'commandVersion') + '|' + [string](Get-Prop $_ 'eventSeq'))
        }) | Sort-Object -CaseSensitive
    $requestLinesRight = @((Get-Prop $Right 'requests' @()) | ForEach-Object {
            ([string](Get-Prop $_ 'accountId') + '|' + [string](Get-Prop $_ 'scopeId') + '|' + [string](Get-Prop $_ 'requestId') + '|' +
                [string](Get-Prop $_ 'receiptId') + '|' + [string](Get-Prop $_ 'confirmed') + '|' + [string](Get-Prop $_ 'status') + '|' +
                [string](Get-Prop $_ 'payloadSha256') + '|' + [string](Get-Prop $_ 'evaluatedStateVersion') + '|' +
                [string](Get-Prop $_ 'commandVersion') + '|' + [string](Get-Prop $_ 'eventSeq'))
        }) | Sort-Object -CaseSensitive
    if (-not [string]::Equals(($requestLinesLeft -join "`n"), ($requestLinesRight -join "`n"), [StringComparison]::Ordinal)) {
        $mismatches.Add('requests: before and after differ') | Out-Null
    }
    $controlLinesLeft = @((Get-Prop $Left 'controlRequests' @()) | ForEach-Object {
            ([string](Get-Prop $_ 'accountId') + '|' + [string](Get-Prop $_ 'scopeId') + '|' + [string](Get-Prop $_ 'requestId') + '|' +
                [string](Get-Prop $_ 'digest') + '|' + [string](Get-Prop $_ 'kind') + '|' + [string](Get-Prop $_ 'evaluatedStateVersion'))
        }) | Sort-Object -CaseSensitive
    $controlLinesRight = @((Get-Prop $Right 'controlRequests' @()) | ForEach-Object {
            ([string](Get-Prop $_ 'accountId') + '|' + [string](Get-Prop $_ 'scopeId') + '|' + [string](Get-Prop $_ 'requestId') + '|' +
                [string](Get-Prop $_ 'digest') + '|' + [string](Get-Prop $_ 'kind') + '|' + [string](Get-Prop $_ 'evaluatedStateVersion'))
        }) | Sort-Object -CaseSensitive
    if (-not [string]::Equals(($controlLinesLeft -join "`n"), ($controlLinesRight -join "`n"), [StringComparison]::Ordinal)) {
        $mismatches.Add('controlRequests: before and after differ') | Out-Null
    }
    [pscustomobject]@{ equal = ($mismatches.Count -eq 0); mismatches = @($mismatches.ToArray()) }
}

function Invoke-Attempt {
    param(
        [int]$AttemptNumber,
        [ValidateSet('Before', 'After')][string]$Phase,
        [bool]$ForceBefore,
        [bool]$ExitAfterExport
    )
    $attemptName = $AttemptNumber.ToString('0000', [Globalization.CultureInfo]::InvariantCulture)
    $attemptDir = Join-Path $script:CaseRoot ('attempts\' + $attemptName + '-' + $Phase.ToLowerInvariant())
    [IO.Directory]::CreateDirectory($attemptDir) | Out-Null
    $sealedDir = Join-Path $attemptDir 'sealed'
    [IO.Directory]::CreateDirectory($sealedDir) | Out-Null
    $verifierDir = Join-Path $attemptDir 'verifier'
    [IO.Directory]::CreateDirectory($verifierDir) | Out-Null
    $exportDir = Join-Path $script:DataRoot ('export-' + $attemptName + '-' + $Phase.ToLowerInvariant())
    if ([IO.Directory]::Exists($exportDir)) { Throw-Ctl 3 'EVIDENCE_REUSE' ('Export directory already exists: ' + $exportDir) }
    [IO.Directory]::CreateDirectory($exportDir) | Out-Null
    $null = Assert-OwnedDirectory -Path $script:DataRoot -Root $script:EvidenceRoot -Context 'launch data root' -Exact
    $null = Assert-OwnedDirectory -Path $exportDir -Root $script:DataRoot -Context 'launch export root' -Exact
    $null = Assert-OwnedDirectory -Path $attemptDir -Root $script:CaseRoot -Context 'attempt directory' -Exact
    $logPath = Join-Path $attemptDir 'player.log'
    $stdoutPath = Join-Path $attemptDir 'stdout.log'
    $stderrPath = Join-Path $attemptDir 'stderr.log'
    $phaseText = if ($Phase -eq 'Before') { 'BEFORE_RECOVERY' } else { 'AFTER_RECOVERY' }
    $challenge = $script:Challenges[$AttemptNumber]
    $arguments = @(
        '-screen-width', '1280',
        '-screen-height', '720',
        '-screen-fullscreen', '0',
        '-logfile', $logPath,
        '-smoke-data', $script:EvidenceRoot,
        '-p45-recovery-export', $exportDir,
        '-p45-recovery-phase', $phaseText,
        '-p45-recovery-challenge', $challenge,
        '-p45-recovery-run', $script:RunId,
        '-p45-recovery-attempt', $attemptName,
        '-p45-recovery-data-dir', $script:DataRoot,
        '-p45-recovery-player-sha', $script:PlayerSha,
        '-p45-recovery-package-sha', $script:Package.filesSha256,
        '-p45-recovery-package-count', ([string][int]$script:Package.fileCount),
        '-p45-recovery-source-head', $script:Source.head,
        '-p45-recovery-build-commit', $script:BuildCommit.ToLowerInvariant(),
        '-p45-recovery-build-provenance', 'BUILD_LOG_ATTACHED',
        '-p45-recovery-ruleset', '1'
    )
    if ($ExitAfterExport) { $arguments += '-p45-recovery-exit-after-export' }
    $argumentText = Get-CommandText -Arguments $arguments

    $launchData = [ordered]@{
        attempt = $AttemptNumber
        phase = $phaseText
        mode = if ($ForceBefore) { 'Force' } else { 'Normal' }
        pid = $null
        startTimeUtc = $null
        processPath = $null
        processSha256 = $null
        expectedExecutable = $script:PlayerPath
        expectedArguments = $arguments
        expectedArgumentText = $argumentText
        actualCommandLine = $null
        argvMatched = $false
        packageFilesSha256 = $script:Package.filesSha256
        sourceHead = $script:Source.head
        buildCommit = $script:BuildCommit.ToLowerInvariant()
        challenge = $challenge
        exportDirectory = $exportDir
        dataDirectory = $script:DataRoot
        status = 'STARTING'
        error = $null
    }
    $process = $null
    $launchRecord = $null
    try {
        $logHandle = Open-OwnedFileHandle -Path $logPath -Mode Truncate -Context 'player log precreate'
        $logHandle.Dispose()
        $process = Start-Process -FilePath $script:PlayerPath -ArgumentList $argumentText `
            -WorkingDirectory (Split-Path -Parent $script:PlayerPath) -WindowStyle Hidden `
            -PassThru -ErrorAction Stop
    }
    catch {
        $launchData.status = 'LAUNCH_FAILED'
        $launchData.error = Get-ErrText $_
        $launchRecord = Add-Record -Kind 'attempt-launch' -Data $launchData -RelativePath ('attempts\' + $attemptName + '-' + $Phase.ToLowerInvariant() + '\launch-observation.json')
        Throw-Ctl 12 'LAUNCH_FAILED' ('Could not start the Player: ' + (Get-ErrText $_))
    }
    if ($null -eq $process) { Throw-Ctl 12 'LAUNCH_FAILED' 'Start-Process returned no Process object.' }

    Start-Sleep -Milliseconds 150
    $identity = Get-OwnProcessIdentity -Process $process
    $launchData.pid = [int]$identity.pid
    $launchData.startTimeUtc = $identity.startTimeUtc
    $launchData.processPath = $identity.processPath
    $launchData.processSha256 = $identity.processSha256
    $actualCommandLine = Get-CommandLineForPid -ProcessId ([int]$process.Id)
    $launchData.actualCommandLine = $actualCommandLine
    $tokens = @($script:PlayerPath, $exportDir, $phaseText, $challenge, $script:RunId, $attemptName,
        $script:DataRoot, $script:PlayerSha, $script:Package.filesSha256, $script:Source.head, $script:BuildCommit.ToLowerInvariant())
    $argvMatched = $false
    if (-not [string]::IsNullOrWhiteSpace($actualCommandLine)) {
        $argvMatched = $true
        foreach ($token in $tokens) {
            if ($actualCommandLine.IndexOf([string]$token, [StringComparison]::OrdinalIgnoreCase) -lt 0) { $argvMatched = $false; break }
        }
    }
    $launchData.argvMatched = $argvMatched
    if ([string]::IsNullOrWhiteSpace($identity.startTimeUtc) -or
        [string]::IsNullOrWhiteSpace($identity.processPath) -or
        -not [string]::Equals($identity.processPath, $script:PlayerPath, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([string]$identity.processSha256, $script:PlayerSha, [StringComparison]::OrdinalIgnoreCase)) {
        $launchData.status = 'IDENTITY_REJECTED'
        $launchData.error = 'PID/start/path/hash did not match the exact Player selected before launch.'
        $launchRecord = Add-Record -Kind 'attempt-launch' -Data $launchData -RelativePath ('attempts\' + $attemptName + '-' + $Phase.ToLowerInvariant() + '\launch-observation.json')
        try { if (-not $process.HasExited) { $process.Kill() } } catch { }
        try { [void]$process.WaitForExit(5000) } catch { }
        try { $process.Close() } catch { }
        Throw-Ctl 19 'PROCESS_IDENTITY' $launchData.error
    }
    if (-not $argvMatched -and $script:EvidenceClass -eq 'GAME') {
        $launchData.status = 'ARGV_REJECTED'
        $launchData.error = 'The OS command line for the owned PID did not contain every critical launch token.'
        $launchRecord = Add-Record -Kind 'attempt-launch' -Data $launchData -RelativePath ('attempts\' + $attemptName + '-' + $Phase.ToLowerInvariant() + '\launch-observation.json')
        try { if (-not $process.HasExited) { $process.Kill() } } catch { }
        try { [void]$process.WaitForExit(5000) } catch { }
        try { $process.Close() } catch { }
        Throw-Ctl 19 'PROCESS_ARGV' $launchData.error
    }
    $launchData.status = 'RUNNING'
    $launchRecord = Add-Record -Kind 'attempt-launch' -Data $launchData -RelativePath ('attempts\' + $attemptName + '-' + $Phase.ToLowerInvariant() + '\launch-observation.json')

    $wait = Wait-Export -Directory $exportDir -Process $process -Seconds $TimeoutSeconds
    $completionMode = if ($ForceBefore) { 'Force' } else { 'Normal' }
    $exit = Complete-OwnedProcess -Process $process -Mode $completionMode `
        -Seconds ([Math]::Max(30, [int]($TimeoutSeconds / 3))) -ExpectedStartUtc ([string]$identity.startTimeUtc) `
        -ExpectedPath ([string]$identity.processPath) -ExpectedSha256 ([string]$identity.processSha256)
    $process = $null

    $exitData = [ordered]@{
        attempt = $AttemptNumber
        phase = $phaseText
        pid = [int]$identity.pid
        startTimeUtc = [string]$identity.startTimeUtc
        processPath = [string]$identity.processPath
        disposition = [string]$exit.disposition
        nativeExitCode = $exit.nativeExitCode
        requested = [bool]$exit.requested
        killReason = $exit.killReason
        handleClosed = [bool]$exit.handleClosed
        exportWaitStatus = [string]$wait.status
        exportWaitReason = [string]$wait.reason
        observedAt = Get-UtcText
    }
    Add-Record -Kind 'attempt-exit' -Data $exitData -RelativePath ('attempts\' + $attemptName + '-' + $Phase.ToLowerInvariant() + '\exit-observation.json') | Out-Null

    $artifacts = New-Object System.Collections.Generic.List[object]
    $artifactNames = @('authority.save', 'cheat-state.bin', 'export.json', 'export.done')
    if ([IO.File]::Exists((Join-Path $exportDir 'export.error.json'))) { $artifactNames += 'export.error.json' }
    foreach ($name in $artifactNames) {
        $source = Join-Path $exportDir $name
        if (-not [IO.File]::Exists($source)) { continue }
        $record = Copy-StableEvidence -Source $source -Destination (Join-Path $sealedDir $name)
        $artifacts.Add([pscustomobject]@{
            name = $name
            bytes = [long]$record.bytes
            sha256 = [string]$record.sha256
            beforeSha256 = [string]$record.beforeSha256
            afterSha256 = [string]$record.afterSha256
            stable = [bool]$record.stable
        }) | Out-Null
    }
    $playerLogSource = $logPath
    if ([IO.File]::Exists($playerLogSource)) {
        $logRecord = Copy-StableEvidence -Source $playerLogSource -Destination (Join-Path $sealedDir 'player.log')
        $artifacts.Add([pscustomobject]@{ name = 'player.log'; bytes = [long]$logRecord.bytes; sha256 = [string]$logRecord.sha256; beforeSha256 = [string]$logRecord.beforeSha256; afterSha256 = [string]$logRecord.afterSha256; stable = $true }) | Out-Null
    }
    # Player stdout/stderr redirection handles are owned by Start-Process and can
    # remain open after the process exits. They are not part of the evidence
    # contract; the Player -logfile is sealed instead. The fixed verifier's own
    # stdout/stderr are sealed separately.

    $exportIdentity = [ordered]@{
        checked = $false
        ok = $false
        reason = 'NO_EXPORT'
        exportSha256 = $null
    }
    $sealedExportPath = Join-Path $sealedDir 'export.json'
    if ([IO.File]::Exists($sealedExportPath)) {
        $exportIdentity.checked = $true
        $exportDoc = Read-JsonFile -Path $sealedExportPath
        $sealedAuthority = Join-Path $sealedDir 'authority.save'
        $sealedCheat = Join-Path $sealedDir 'cheat-state.bin'
        $authoritySig = Get-FileSig -Path $sealedAuthority
        $cheatSig = Get-FileSig -Path $sealedCheat
        $expectedAttempt = $attemptName
        $identityErrors = New-Object System.Collections.Generic.List[string]
        if ([string](Get-Prop $exportDoc 'kind') -ne 'p45-player-structured-export') { $identityErrors.Add('kind') | Out-Null }
        if ([string](Get-Prop $exportDoc 'exporterVersion') -ne 'p45-player-structured-export/1') { $identityErrors.Add('exporterVersion') | Out-Null }
        if (-not [string]::Equals([string](Get-Prop $exportDoc 'phase'), $phaseText, [StringComparison]::Ordinal)) { $identityErrors.Add('phase') | Out-Null }
        if (-not [string]::Equals([string](Get-Prop $exportDoc 'runId'), $script:RunId, [StringComparison]::Ordinal)) { $identityErrors.Add('runId') | Out-Null }
        if (-not [string]::Equals([string](Get-Prop $exportDoc 'attempt'), $expectedAttempt, [StringComparison]::Ordinal)) { $identityErrors.Add('attempt') | Out-Null }
        if (-not [string]::Equals([string](Get-Prop $exportDoc 'challenge'), $challenge, [StringComparison]::Ordinal)) { $identityErrors.Add('challenge') | Out-Null }
        if ([int](Get-Prop $exportDoc 'pid' 0) -ne [int]$identity.pid) { $identityErrors.Add('pid') | Out-Null }
        if (-not [string]::Equals([string](Get-Prop $exportDoc 'processStartUtc'), [string]$identity.startTimeUtc, [StringComparison]::Ordinal)) { $identityErrors.Add('processStartUtc') | Out-Null }
        if (-not [string]::Equals([string](Get-Prop $exportDoc 'processPath'), [string]$identity.processPath, [StringComparison]::OrdinalIgnoreCase)) { $identityErrors.Add('processPath') | Out-Null }
        if (-not [string]::Equals([string](Get-Prop $exportDoc 'playerSha256'), $script:PlayerSha, [StringComparison]::OrdinalIgnoreCase)) { $identityErrors.Add('playerSha256') | Out-Null }
        if (-not [string]::Equals([string](Get-Prop $exportDoc 'packageManifestSha256'), [string]$script:Package.filesSha256, [StringComparison]::OrdinalIgnoreCase)) { $identityErrors.Add('packageManifestSha256') | Out-Null }
        if (-not [string]::Equals([string](Get-Prop $exportDoc 'sourceHead'), $script:Source.head, [StringComparison]::OrdinalIgnoreCase)) { $identityErrors.Add('sourceHead') | Out-Null }
        if (-not [string]::Equals([string](Get-Prop $exportDoc 'buildCommit'), $script:BuildCommit.ToLowerInvariant(), [StringComparison]::OrdinalIgnoreCase)) { $identityErrors.Add('buildCommit') | Out-Null }
        if ($null -eq $authoritySig -or [long](Get-Prop $exportDoc 'authorityBytes' -1) -ne [long]$authoritySig.bytes -or
            -not [string]::Equals([string](Get-Prop $exportDoc 'authoritySha256'), [string]$authoritySig.sha256, [StringComparison]::OrdinalIgnoreCase)) { $identityErrors.Add('authority') | Out-Null }
        if ($null -eq $cheatSig -or [long](Get-Prop $exportDoc 'cheatStateBytes' -1) -ne [long]$cheatSig.bytes -or
            -not [string]::Equals([string](Get-Prop $exportDoc 'cheatStateSha256'), [string]$cheatSig.sha256, [StringComparison]::OrdinalIgnoreCase)) { $identityErrors.Add('cheatState') | Out-Null }
        $sealedDone = Join-Path $sealedDir 'export.done'
        if (-not [IO.File]::Exists($sealedDone)) { $identityErrors.Add('export.done') | Out-Null }
        elseif (-not [string]::Equals((([IO.File]::ReadAllText($sealedDone, $script:Utf8)).Trim()), ((Get-Sha256File -Path $sealedExportPath) + '  export.json'), [StringComparison]::OrdinalIgnoreCase)) {
            $identityErrors.Add('export.done.sha256') | Out-Null
        }
        $exportIdentity.exportSha256 = Get-Sha256File -Path $sealedExportPath
        if ($identityErrors.Count -eq 0) {
            $exportIdentity.ok = $true
            $exportIdentity.reason = 'MATCH'
        }
        else {
            $exportIdentity.reason = ($identityErrors -join ',')
        }
        Add-Record -Kind 'attempt-export-identity' -Data $exportIdentity -RelativePath ('attempts\' + $attemptName + '-' + $Phase.ToLowerInvariant() + '\export-identity.json') | Out-Null
        if (-not $exportIdentity.ok) {
            Throw-Ctl 19 'EXPORT_IDENTITY' ($phaseText + ': the Player export does not match the directly observed process/package/build/challenge: ' + $exportIdentity.reason)
        }
    }

    $verifier = $null
    if ([IO.File]::Exists((Join-Path $sealedDir 'authority.save')) -and [IO.File]::Exists((Join-Path $sealedDir 'cheat-state.bin'))) {
        $verifier = Invoke-FixedVerifier -AuthorityPath (Join-Path $sealedDir 'authority.save') `
            -CheatStatePath (Join-Path $sealedDir 'cheat-state.bin') -OutputDirectory $verifierDir -Prefix 'recovery'
    }
    $facts = $null
    if ($null -ne $verifier -and $verifier.ok) { $facts = Get-Prop $verifier.parsed 'facts' }
    $factsData = [ordered]@{
        attempt = $AttemptNumber
        phase = $phaseText
        verifierExitCode = if ($null -eq $verifier) { $null } else { [int]$verifier.exitCode }
        verifierStdoutSha256 = if ($null -eq $verifier) { $null } else { [string]$verifier.stdoutSha256 }
        verifierStderrSha256 = if ($null -eq $verifier) { $null } else { [string]$verifier.stderrSha256 }
        verifierOk = if ($null -eq $verifier) { $false } else { [bool]$verifier.ok }
        facts = Get-FactsSummary -Facts $facts
        factsSha256 = if ($null -eq $facts) { $null } else { Get-Sha256Text -Text ($facts | ConvertTo-Json -Depth 40 -Compress) }
    }
    Add-Record -Kind 'attempt-facts' -Data $factsData -RelativePath ('attempts\' + $attemptName + '-' + $Phase.ToLowerInvariant() + '\facts.json') | Out-Null

    $sealedNames = @($artifacts | ForEach-Object { [string]$_.name })
    $hasAllRequired = $true
    foreach ($requiredName in @('authority.save', 'cheat-state.bin', 'export.json', 'export.done')) {
        if ($sealedNames -notcontains $requiredName) { $hasAllRequired = $false }
    }
    $structuredOk = ($wait.status -eq 'DONE') -and [bool]$exportIdentity.checked -and [bool]$exportIdentity.ok -and
        $hasAllRequired -and $null -ne $verifier -and [bool]$verifier.ok
    if ($Phase -eq 'Before') {
        if (-not $structuredOk) {
            Throw-Ctl 17 'BEFORE_EXPORT_INCOMPLETE' ('BEFORE_RECOVERY did not produce a complete structured export with exact process/export identity (wait=' +
                [string]$wait.status + ' checked=' + [string]$exportIdentity.checked + ' ok=' + [string]$exportIdentity.ok +
                ' required=' + [string]$hasAllRequired + ' verifier=' + [string]($null -ne $verifier -and $verifier.ok) + ').')
        }
    }
    else {
        $allowedRejection = ([string]$script:ExpectedOutcome -eq 'REJECTED' -and
            ([string]$wait.status -eq 'ERROR' -or [string]$wait.status -eq 'EXITED_NO_EXPORT') -and $null -eq $verifier)
        if (-not $allowedRejection -and -not $structuredOk) {
            Throw-Ctl 17 'AFTER_EXPORT_INCOMPLETE' ('AFTER_RECOVERY did not produce a complete structured export or a declared rejection (wait=' +
                [string]$wait.status + ' checked=' + [string]$exportIdentity.checked + ' ok=' + [string]$exportIdentity.ok +
                ' required=' + [string]$hasAllRequired + ' verifier=' + [string]($null -ne $verifier -and $verifier.ok) + ').')
        }
    }

    Test-PackageUnchanged -Expected $script:Package -Root (Split-Path -Parent $script:PlayerPath) -Context ($phaseText + ' after exit') | Out-Null

    [pscustomobject]@{
        attempt = $AttemptNumber
        phase = $phaseText
        launch = $launchData
        exit = $exitData
        wait = $wait
        artifacts = @($artifacts.ToArray())
        verifier = $verifier
        facts = $facts
        factsData = $factsData
        exportIdentity = [pscustomobject]$exportIdentity
    }
}

function Get-CaseManifest {
    param([string]$CaseRoot)
    $records = New-Object System.Collections.Generic.List[object]
    foreach ($file in @(Get-ChildItem -LiteralPath $CaseRoot -Recurse -File -Force -ErrorAction Stop)) {
        $relative = Get-RelativePath -Base $CaseRoot -Path $file.FullName
        if ($relative -in @('manifest.json', 'manifest.sha256.txt')) { continue }
        $sig = Get-FileSig -Path $file.FullName
        $records.Add([pscustomobject]@{ path = $relative; bytes = [long]$sig.bytes; sha256 = [string]$sig.sha256 }) | Out-Null
    }
    $array = @($records.ToArray() | Sort-Object -Property path -CaseSensitive)
    [pscustomobject]@{
        schemaVersion = 1
        kind = 'p45-controlled-collection-case-manifest'
        toolVersion = $script:ToolVersion
        caseId = $script:CaseId
        runId = $script:RunId
        evidenceClass = $script:EvidenceClass
        generatedAt = Get-UtcText
        inputManifestSha256 = $script:InputManifestSha256
        recordCount = $script:RecordSequence
        recordChainHead = $script:LastRecordSha
        files = $array
        counts = [pscustomobject]@{ files = $array.Count }
    }
}

# Single explicit read-only diagnostic. It never stops or kills any process and
# emits no evidence record. Tests use it to prove wrong PID/start/path are
# rejected and that an unrelated live process is untouched.
if ($ProbeIdentity) {
    $result = Test-LiveIdentity -ProcessId $ProbePid -ExpectedStartUtc $ProbeStartTimeUtc `
        -ExpectedPath $ProbePath -ExpectedSha256 $ProbeSha256
    $payload = [pscustomobject]@{
        kind = 'p45-controlled-collection-identity-probe'
        match = [bool]$result.Exists
        reason = [string]$result.Reason
        pid = $ProbePid
        diagnosticsOnly = $true
    }
    Write-Output ($payload | ConvertTo-Json -Depth 10 -Compress)
    if ($result.Exists) { exit 0 }
    exit 19
}

# Normal collection path starts here.
trap {
    $trapCode = Get-ExitCodeFromError $_
    Write-Host ('P45_CONTROLLED_FAILURE: ' + (Get-ErrText $_))
    exit $trapCode
}
foreach ($requiredName in @('PlayerPath', 'BuildCommit', 'BuildLog', 'BuildCommand', 'Baseline', 'VerifierPath')) {
    if ([string]::IsNullOrWhiteSpace([string](Get-Variable -Name $requiredName -ValueOnly))) {
        Throw-Ctl 2 'USAGE' ('Collection requires -' + $requiredName + '.')
    }
}
$script:PlayerPath = Get-FullPath -Path $PlayerPath
$script:BuildCommit = $BuildCommit
$script:EvidenceClass = $EvidenceClass
$script:VerifierResolvedPath = Get-FullPath -Path $VerifierPath
$script:DotnetResolvedPath = Get-FullPath -Path $DotnetPath
$script:BaselinePath = Get-FullPath -Path $Baseline
$script:BuildLogPath = Get-FullPath -Path $BuildLog
if (-not [IO.File]::Exists($script:PlayerPath)) { Throw-Ctl 5 'PLAYER_NOT_FOUND' ('Player not found: ' + $script:PlayerPath) }
if ([IO.Path]::GetExtension($script:PlayerPath) -ine '.exe') { Throw-Ctl 2 'USAGE' 'PlayerPath must be a Windows .exe file.' }
if (-not [IO.File]::Exists($script:BaselinePath)) { Throw-Ctl 5 'BASELINE_NOT_FOUND' ('Baseline not found: ' + $script:BaselinePath) }
if (-not [IO.File]::Exists($script:BuildLogPath)) { Throw-Ctl 5 'BUILD_LOG_NOT_FOUND' ('Build log not found: ' + $script:BuildLogPath) }
if (-not [IO.File]::Exists($script:VerifierResolvedPath)) { Throw-Ctl 5 'VERIFIER_NOT_FOUND' ('Verifier not found: ' + $script:VerifierResolvedPath) }
if (-not [IO.File]::Exists($script:DotnetResolvedPath)) { Throw-Ctl 5 'DOTNET_NOT_FOUND' ('dotnet not found: ' + $script:DotnetResolvedPath) }
if ($BuildCommit -notmatch '^[A-Fa-f0-9]{40}$') { Throw-Ctl 2 'USAGE' 'BuildCommit must be a 40-character hexadecimal commit.' }
if ($BuildExitCode -ne 0) { Throw-Ctl 17 'BUILD_FAILED' ('The fresh build did not exit 0: ' + $BuildExitCode) }
if ([string]::IsNullOrWhiteSpace($BuildCommand)) { Throw-Ctl 2 'USAGE' 'BuildCommand must record the exact fresh build command.' }

$script:PlayerSha = Get-Sha256File -Path $script:PlayerPath
if (-not [string]::IsNullOrWhiteSpace($ExpectedPlayerSha256) -and
    -not [string]::Equals($script:PlayerSha, $ExpectedPlayerSha256, [StringComparison]::OrdinalIgnoreCase)) {
    Throw-Ctl 4 'PLAYER_DIGEST' ('Player SHA-256 does not match the expected digest: ' + $script:PlayerSha)
}
$script:Source = Get-SourceSnapshot
if ($script:EvidenceClass -eq 'GAME' -and
    -not [string]::Equals($buildCommit.ToLowerInvariant(), $script:Source.head, [StringComparison]::Ordinal)) {
    Throw-Ctl 17 'BUILD_SOURCE_MISMATCH' ('The declared build commit ' + $BuildCommit + ' is not the frozen source HEAD ' + $script:Source.head)
}
$allowedDirty = @()
foreach ($allowedEntry in @($AllowedDirtyPath)) {
    foreach ($allowedPart in ([string]$allowedEntry -split '[,;]')) {
        $normalized = $allowedPart.Trim().Replace('\', '/').TrimStart('./')
        if (-not [string]::IsNullOrWhiteSpace($normalized)) { $allowedDirty += $normalized }
    }
}
$unexpectedDirty = @($script:Source.dirtyPaths | Where-Object { $allowedDirty -notcontains $_ })
if ($script:Source.dirty -and -not $AllowDirtySource -and $unexpectedDirty.Count -gt 0) {
    Throw-Ctl 17 'SOURCE_DIRTY' ('The worktree has unapproved changes: ' + ($unexpectedDirty -join ', '))
}
if ($unexpectedDirty.Count -gt 0) {
    Throw-Ctl 17 'SOURCE_DIRTY' ('The worktree has changes outside the explicit allowlist: ' + ($unexpectedDirty -join ', '))
}
$buildLogText = [IO.File]::ReadAllText($script:BuildLogPath, $script:Utf8)
if ($buildLogText -notmatch 'Build Successful') {
    Throw-Ctl 17 'BUILD_LOG_INVALID' 'The build log does not contain the Unity "Build Successful" marker.'
}
$script:Package = Get-PackageManifest -Root (Split-Path -Parent $script:PlayerPath)
if (-not [string]::IsNullOrWhiteSpace($ExpectedPackageSha256) -and
    -not [string]::Equals([string]$script:Package.filesSha256, $ExpectedPackageSha256, [StringComparison]::OrdinalIgnoreCase)) {
    Throw-Ctl 4 'PACKAGE_DIGEST' ('Player package digest does not match the expected digest: ' + [string]$script:Package.filesSha256)
}
$script:PluginClosure = Get-PluginClosure
$collectorScriptPath = Get-FullPath -Path $PSCommandPath
$verifierScriptPath = Get-FullPath -Path (Join-Path $PSScriptRoot 'verify-p45-controlled-recovery-collection.ps1')
if (-not [IO.File]::Exists($verifierScriptPath)) { Throw-Ctl 5 'VERIFIER_SCRIPT_NOT_FOUND' ('Verifier script not found: ' + $verifierScriptPath) }
$collectorSha = Get-Sha256File -Path $collectorScriptPath
$verifierScriptSha = Get-Sha256File -Path $verifierScriptPath
$verifierSha = Get-Sha256File -Path $script:VerifierResolvedPath
$dotnetVersion = (@(& $script:DotnetResolvedPath --version 2>$null) -join ' ').Trim()
if ($LASTEXITCODE -ne 0) { Throw-Ctl 5 'DOTNET_BROKEN' 'dotnet --version failed.' }

if ([string]::IsNullOrWhiteSpace($RunId)) {
    $stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss', [Globalization.CultureInfo]::InvariantCulture)
    $RunId = 'p45-cc-' + $stamp + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
}
if ($RunId -notmatch '^[A-Za-z0-9._-]{8,80}$') { Throw-Ctl 2 'USAGE' 'RunId contains unsupported characters.' }
$script:RunId = $RunId
$script:CaseId = $RunId + '-' + $Case.ToLowerInvariant() + '-' + $SaveFault.ToLowerInvariant()

$root = if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
    Join-Path $script:RepoRoot ('build/p45-controlled-collection/' + $script:RunId)
}
else { Get-FullPath -Path $EvidenceRoot }
if ([IO.Directory]::Exists($root) -or [IO.File]::Exists($root)) {
    Throw-Ctl 3 'EVIDENCE_REUSE' ('Refusing to reuse an existing evidence root: ' + $root)
}
$script:EvidenceRoot = $root
$script:OwnershipRoot = $root
$script:CaseRoot = Join-Path $root 'case'
$script:DataRoot = Join-Path $root 'local'
$script:RecordsPath = Join-Path $script:CaseRoot 'records.jsonl'
[IO.Directory]::CreateDirectory($script:CaseRoot) | Out-Null
[IO.Directory]::CreateDirectory($script:DataRoot) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $root 'tools')) | Out-Null

$mutex = New-Object System.Threading.Mutex($false, $script:UnityMutexName)
$acquired = $false
$resultStatus = 'BLOCKED'
$resultExit = 17
$summary = $null
try {
    $waitStart = [DateTime]::UtcNow
    $acquired = $mutex.WaitOne([Math]::Max(1000, $MutexTimeoutSeconds * 1000))
    if (-not $acquired) { Throw-Ctl 13 'UNITY_MUTEX_TIMEOUT' ('Timed out waiting for ' + $script:UnityMutexName) }
    $mutexWaitSeconds = [Math]::Round(([DateTime]::UtcNow - $waitStart).TotalSeconds, 3)

    # Seal the fixed tool code and build log before any launch.
    [IO.Directory]::CreateDirectory((Join-Path $script:CaseRoot 'tools')) | Out-Null
    [IO.File]::Copy($collectorScriptPath, (Join-Path $script:CaseRoot 'tools\run-p45-controlled-recovery-collection.ps1'), $false)
    [IO.File]::Copy($verifierScriptPath, (Join-Path $script:CaseRoot 'tools\verify-p45-controlled-recovery-collection.ps1'), $false)
    [IO.File]::Copy($script:BuildLogPath, (Join-Path $root 'build-player.log'), $false)
    Write-TextAtomic -Path (Join-Path $root 'source-diff.txt') -Text ([string]$script:Source.diffText)

    $script:Challenges = @{}
    $script:Challenges[1] = (([Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')).Substring(0, 64).ToUpperInvariant())
    $script:Challenges[2] = (([Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')).Substring(0, 64).ToUpperInvariant())

    $expectedOutcome = Get-ExpectedOutcome -Fault $SaveFault
    $script:ExpectedOutcome = $expectedOutcome
    $baselineSig = Get-FileSig -Path $script:BaselinePath
    $inputManifest = [ordered]@{
        schemaVersion = 1
        kind = 'p45-controlled-collection-input'
        toolVersion = $script:ToolVersion
        createdAt = Get-UtcText
        runId = $script:RunId
        caseId = $script:CaseId
        case = $Case
        saveFault = $SaveFault
        expectedOutcome = $expectedOutcome
        evidenceClass = $script:EvidenceClass
        source = $script:Source
        build = [pscustomobject]@{
            command = $BuildCommand
            exitCode = $BuildExitCode
            logPath = $script:BuildLogPath
            logSha256 = Get-Sha256File -Path $script:BuildLogPath
            logBytes = (Get-Item -LiteralPath $script:BuildLogPath).Length
            commit = $script:BuildCommit.ToLowerInvariant()
        }
        player = [pscustomobject]@{
            path = $script:PlayerPath
            sha256 = $script:PlayerSha
            bytes = (Get-Item -LiteralPath $script:PlayerPath).Length
            packageFilesSha256 = $script:Package.filesSha256
            packageFileCount = $script:Package.fileCount
            packageTotalBytes = $script:Package.totalBytes
            packageFiles = @($script:Package.files)
        }
        pluginClosure = $script:PluginClosure
        baseline = [pscustomobject]@{ path = $script:BaselinePath; sha256 = [string]$baselineSig.sha256; bytes = [long]$baselineSig.bytes }
        verifier = [pscustomobject]@{ path = $script:VerifierResolvedPath; sha256 = $verifierSha; dotnetPath = $script:DotnetResolvedPath; dotnetVersion = $dotnetVersion }
        scripts = [pscustomobject]@{
            collectorPath = $collectorScriptPath
            collectorSha256 = $collectorSha
            verifierScriptPath = $verifierScriptPath
            verifierScriptSha256 = $verifierScriptSha
        }
        unityMutex = [pscustomobject]@{ name = $script:UnityMutexName; waitSeconds = $mutexWaitSeconds; acquired = $true }
        plannedAttempts = @(
            [pscustomobject]@{ attempt = 1; phase = 'BEFORE_RECOVERY'; mode = if ($Case -eq 'Force') { 'Force' } else { 'Normal' }; challenge = $script:Challenges[1] },
            [pscustomobject]@{ attempt = 2; phase = 'AFTER_RECOVERY'; mode = 'Normal'; challenge = $script:Challenges[2] }
        )
    }
    $inputManifestPath = Join-Path $root 'input-manifest.json'
    Write-JsonAtomic -Path $inputManifestPath -Object $inputManifest
    $script:InputManifestSha256 = Get-Sha256File -Path $inputManifestPath
    Write-TextAtomic -Path (Join-Path $root 'input-manifest.sha256.txt') -Text ($script:InputManifestSha256 + '  input-manifest.json' + [Environment]::NewLine)
    [IO.File]::Copy($inputManifestPath, (Join-Path $script:CaseRoot 'input-manifest.json'), $false)

    $caseOpen = [ordered]@{
        schemaVersion = 1
        kind = 'p45-controlled-collection-case'
        caseId = $script:CaseId
        runId = $script:RunId
        case = $Case
        saveFault = $SaveFault
        expectedOutcome = $expectedOutcome
        evidenceClass = $script:EvidenceClass
        inputManifestSha256 = $script:InputManifestSha256
        playerSha256 = $script:PlayerSha
        packageFilesSha256 = $script:Package.filesSha256
        sourceHead = $script:Source.head
        buildCommit = $script:BuildCommit.ToLowerInvariant()
        dataDirectory = $script:DataRoot
        plannedAttempts = $inputManifest.plannedAttempts
        createdAt = Get-UtcText
    }
    Add-Record -Kind 'case-open' -Data $caseOpen -RelativePath 'case.json' | Out-Null

    # Seed the same data domain: primary plus a valid previous copy.
    [IO.File]::Copy($script:BaselinePath, (Join-Path $script:DataRoot 'local-main.save'), $false)
    [IO.File]::Copy($script:BaselinePath, (Join-Path $script:DataRoot 'local-main.save.prev'), $false)
    Assert-OwnedDataDomain -DataDirectory $script:DataRoot -EvidenceRoot $script:EvidenceRoot -Context 'seeded data domain'
    $seedObservation = Get-SaveObservation -DataDirectory $script:DataRoot -CheatStatePath $null -VerifierOutputDirectory (Join-Path $script:CaseRoot 'save-fault') -Prefix 'seed'
    if (-not [bool](Get-Prop $seedObservation.primary 'valid') -or -not [bool](Get-Prop $seedObservation.backup 'valid')) {
        Throw-Ctl 18 'BASELINE_INVALID' 'The frozen baseline does not decode as a valid save (primary/backup seed).'
    }

    $before = Invoke-Attempt -AttemptNumber 1 -Phase Before -ForceBefore ($Case -eq 'Force') -ExitAfterExport ($Case -eq 'Normal')
    Assert-OwnedDataDomain -DataDirectory $script:DataRoot -EvidenceRoot $script:EvidenceRoot -Context 'after BEFORE before fault'
    $beforeFault = Get-SaveObservation -DataDirectory $script:DataRoot -CheatStatePath (Join-Path $script:CaseRoot ('attempts\0001-before\sealed\cheat-state.bin')) -VerifierOutputDirectory (Join-Path $script:CaseRoot 'save-fault') -Prefix 'before-fault'
    $faultInfo = Apply-SaveFault -DataDirectory $script:DataRoot -Fault $SaveFault
    Assert-OwnedDataDomain -DataDirectory $script:DataRoot -EvidenceRoot $script:EvidenceRoot -Context 'after fault before AFTER'
    $afterFault = Get-SaveObservation -DataDirectory $script:DataRoot -CheatStatePath (Join-Path $script:CaseRoot ('attempts\0001-before\sealed\cheat-state.bin')) -VerifierOutputDirectory (Join-Path $script:CaseRoot 'save-fault') -Prefix 'after-fault'
    $faultData = [ordered]@{
        schemaVersion = 1
        kind = 'p45-controlled-collection-save-fault'
        fault = $SaveFault
        appliedAt = Get-UtcText
        before = $beforeFault
        after = $afterFault
        dataDirectory = $script:DataRoot
        primaryPath = $faultInfo.primaryPath
        backupPath = $faultInfo.backupPath
    }
    Add-Record -Kind 'save-fault-applied' -Data $faultData -RelativePath 'save-fault\fault.json' | Out-Null

    $after = Invoke-Attempt -AttemptNumber 2 -Phase After -ForceBefore $false -ExitAfterExport $true

    $beforeFacts = $before.facts
    $afterFacts = $after.facts
    $comparison = Get-FactsComparison -Left $beforeFacts -Right $afterFacts
    $comparisonData = [ordered]@{
        schemaVersion = 1
        kind = 'p45-controlled-collection-facts-comparison'
        expectedOutcome = $expectedOutcome
        factsEqual = [bool]$comparison.equal
        mismatches = @($comparison.mismatches)
        beforeFactsSha256 = $before.factsData.factsSha256
        afterFactsSha256 = $after.factsData.factsSha256
        comparedAt = Get-UtcText
    }
    Add-Record -Kind 'facts-compare' -Data $comparisonData -RelativePath 'facts-comparison.json' | Out-Null

    $observedOutcome = 'UNKNOWN'
    $beforeExit = $before.exit
    $afterExit = $after.exit
    $beforeRecovered = ([string]$beforeExit.disposition -eq 'EXITED_OK' -or [string]$beforeExit.disposition -eq 'FORCE_KILLED') -and
        [string]$beforeExit.exportWaitStatus -eq 'DONE' -and [bool]$before.exportIdentity.checked -and
        [bool]$before.exportIdentity.ok -and [bool]$before.factsData.verifierOk
    if (-not $beforeRecovered) {
        $observedOutcome = 'BEFORE_NOT_RECOVERED'
    }
    elseif ([string]$after.wait.status -eq 'DONE') {
        if ([bool]$comparison.equal) { $observedOutcome = 'RECOVERED' }
        else {
            $afterStateVersion = [string](Get-Prop $afterFacts 'stateVersion')
            if ($afterStateVersion -eq '0' -or $afterStateVersion -eq '1') { $observedOutcome = 'FRESH_START' }
            else { $observedOutcome = 'RECOVERED_DIFFERENT' }
        }
    }
    elseif ([string]$after.wait.status -eq 'ERROR') {
        $observedOutcome = 'REJECTED'
    }
    elseif ([string]$after.wait.status -eq 'EXITED_NO_EXPORT') {
        $observedOutcome = 'REJECTED'
    }
    else {
        $observedOutcome = 'AFTER_INCOMPLETE'
    }

    $expectedRejection = ($expectedOutcome -eq 'REJECTED')
    $expectedFresh = ($expectedOutcome -eq 'FRESH_START')
    $expectedRecovered = ($expectedOutcome -eq 'RECOVERED' -or $expectedOutcome -eq 'RECOVERED_FROM_BACKUP')
    $semanticsMatch = $false
    if ($expectedRecovered) {
        $semanticsMatch = ($observedOutcome -eq 'RECOVERED' -and [bool]$comparison.equal -and
            [int]$afterExit.nativeExitCode -eq 0 -and [bool]$after.factsData.verifierOk)
        if ($expectedOutcome -eq 'RECOVERED_FROM_BACKUP') {
            $primaryBad = -not [bool](Get-Prop $afterFault.primary 'valid')
            $backupGood = [bool](Get-Prop $afterFault.backup 'valid')
            $semanticsMatch = $semanticsMatch -and $primaryBad -and $backupGood
        }
    }
    elseif ($expectedFresh) {
        $semanticsMatch = ($observedOutcome -eq 'FRESH_START' -and -not [bool]$comparison.equal)
    }
    elseif ($expectedRejection) {
        $semanticsMatch = ($observedOutcome -eq 'REJECTED' -and -not [bool]$after.factsData.verifierOk)
    }

    $chainOk = $true
    foreach ($attempt in @($before, $after)) {
        foreach ($artifact in @($attempt.artifacts)) {
            if (-not [bool](Get-Prop $artifact 'stable' $false)) { $chainOk = $false }
        }
        if ($attempt.launch.status -ne 'RUNNING') { $chainOk = $false }
        if (-not [bool]$attempt.launch.argvMatched -and $script:EvidenceClass -eq 'GAME') { $chainOk = $false }
    }
    $packageAfter = Test-PackageUnchanged -Expected $script:Package -Root (Split-Path -Parent $script:PlayerPath) -Context 'final package check'
    $pluginAfter = Get-PluginClosure
    $pluginUnchanged = [string]::Equals([string]$pluginAfter.filesSha256, [string]$script:PluginClosure.filesSha256, [StringComparison]::OrdinalIgnoreCase)
    $collectorShaAfter = Get-Sha256File -Path $collectorScriptPath
    $verifierShaAfter = Get-Sha256File -Path $script:VerifierResolvedPath
    $verifierScriptShaAfter = Get-Sha256File -Path $verifierScriptPath
    $toolsUnchanged = [string]::Equals($collectorShaAfter, $collectorSha, [StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals($verifierShaAfter, $verifierSha, [StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals($verifierScriptShaAfter, $verifierScriptSha, [StringComparison]::OrdinalIgnoreCase)

    $status = 'BLOCKED'
    $exitCode = 17
    if ($null -eq $packageAfter -or -not $pluginUnchanged -or -not $toolsUnchanged) {
        $status = 'BLOCKED'
        $exitCode = 17
    }
    elseif (-not $chainOk) {
        $status = 'BLOCKED'
        $exitCode = 17
    }
    elseif (-not $semanticsMatch) {
        $status = 'FAIL'
        $exitCode = 16
    }
    else {
        $status = 'COMPLETE'
        $exitCode = 0
    }

    $caseResult = [ordered]@{
        schemaVersion = 1
        kind = 'p45-controlled-collection-case-result'
        caseId = $script:CaseId
        runId = $script:RunId
        case = $Case
        saveFault = $SaveFault
        expectedOutcome = $expectedOutcome
        observedOutcome = $observedOutcome
        factsEqual = [bool]$comparison.equal
        collectionChain = if ($chainOk) { 'PASS' } else { 'FAIL' }
        packageUnchanged = [bool]$packageAfter
        pluginClosureUnchanged = [bool]$pluginUnchanged
        toolsUnchanged = [bool]$toolsUnchanged
        callerAcceptance = 'PENDING_INDEPENDENT_REVIEW'
        callerAcceptanceEligible = $false
        gamePositive = ($script:EvidenceClass -eq 'GAME')
        status = $status
        beforeSummary = Get-FactsSummary -Facts $beforeFacts
        afterSummary = Get-FactsSummary -Facts $afterFacts
        beforeExit = $beforeExit
        afterExit = $afterExit
        saveFaultSummary = $faultData
        mismatches = @($comparison.mismatches)
        completedAt = Get-UtcText
    }
    Add-Record -Kind 'case-result' -Data $caseResult -RelativePath 'case-result.json' | Out-Null

    $chain = [pscustomobject]@{
        schemaVersion = 1
        kind = 'p45-controlled-collection-record-chain'
        recordCount = $script:RecordSequence
        headRecordSha256 = $script:LastRecordSha
        recordsFileSha256 = Get-Sha256File -Path $script:RecordsPath
        generatedAt = Get-UtcText
    }
    Write-JsonAtomic -Path (Join-Path $script:CaseRoot 'chain.json') -Object $chain
    $manifest = Get-CaseManifest -CaseRoot $script:CaseRoot
    $manifestPath = Join-Path $script:CaseRoot 'manifest.json'
    Write-JsonAtomic -Path $manifestPath -Object $manifest
    $manifestHash = Get-Sha256File -Path $manifestPath
    Write-TextAtomic -Path (Join-Path $script:CaseRoot 'manifest.sha256.txt') -Text ($manifestHash + '  manifest.json' + [Environment]::NewLine)

    $summary = [pscustomobject]@{
        schemaVersion = 1
        kind = 'p45-controlled-collection-run-summary'
        toolVersion = $script:ToolVersion
        status = $status
        exitCode = $exitCode
        caseId = $script:CaseId
        runId = $script:RunId
        case = $Case
        saveFault = $SaveFault
        expectedOutcome = $expectedOutcome
        observedOutcome = $observedOutcome
        factsEqual = [bool]$comparison.equal
        collectionChain = if ($chainOk) { 'PASS' } else { 'FAIL' }
        callerAcceptance = 'PENDING_INDEPENDENT_REVIEW'
        gamePositive = ($script:EvidenceClass -eq 'GAME')
        inputManifestSha256 = $script:InputManifestSha256
        caseManifestSha256 = $manifestHash
        evidenceRoot = $root
        dataRoot = $script:DataRoot
        playerSha256 = $script:PlayerSha
        packageFilesSha256 = $script:Package.filesSha256
        sourceHead = $script:Source.head
        buildCommit = $script:BuildCommit.ToLowerInvariant()
        records = $script:RecordSequence
        beforeAttempt = [pscustomobject]@{ pid = $beforeExit.pid; disposition = $beforeExit.disposition; nativeExitCode = $beforeExit.nativeExitCode; exportWaitStatus = $beforeExit.exportWaitStatus }
        afterAttempt = [pscustomobject]@{ pid = $afterExit.pid; disposition = $afterExit.disposition; nativeExitCode = $afterExit.nativeExitCode; exportWaitStatus = $afterExit.exportWaitStatus }
        privacy = 'Sealed raw authority/cheat/export/player logs stay under the ignored build root. Reports contain digests and aggregate facts only.'
        trustBoundary = 'LOCAL_CONTROLLED_REVIEWER_RUN; same-account debugger/admin forgery and reviewer self-forgery are outside the boundary; no hardware root and no non-forgeable signature.'
    }
    Write-JsonAtomic -Path (Join-Path $root 'collector-result.json') -Object $summary
    Write-Host ('P45_CONTROLLED_RESULT: ' + ($summary | ConvertTo-Json -Depth 20 -Compress))
    $resultStatus = $status
    $resultExit = $exitCode
}
catch {
    $message = Get-ErrText $_
    $code = Get-ExitCodeFromError $_
    $status = Get-StatusFromError $_
    $resultStatus = $status
    $resultExit = $code
    $failure = [ordered]@{
        schemaVersion = 1
        kind = 'p45-controlled-collection-failure'
        caseId = $script:CaseId
        runId = $script:RunId
        status = $status
        exitCode = $code
        message = $message
        failedAt = Get-UtcText
    }
    try {
        if ([IO.Directory]::Exists($script:CaseRoot)) {
            try {
                Add-Record -Kind 'case-failure' -Data $failure -RelativePath 'failure.json' | Out-Null
            }
            catch {
                # The journal itself may be the aliased leaf under review. Record the
                # failure through a separate owned file so the rejection is visible.
                try { Write-JsonAtomic -Path (Join-Path $script:CaseRoot 'failure.json') -Object $failure } catch { }
            }
            $caseResultPath = Join-Path $script:CaseRoot 'case-result.json'
            $resultObject = [ordered]@{
                schemaVersion = 1
                kind = 'p45-controlled-collection-case-result'
                caseId = $script:CaseId
                runId = $script:RunId
                case = $Case
                saveFault = $SaveFault
                expectedOutcome = Get-ExpectedOutcome -Fault $SaveFault
                observedOutcome = 'NOT_RUN'
                factsEqual = $false
                collectionChain = 'FAIL'
                callerAcceptance = 'PENDING_INDEPENDENT_REVIEW'
                callerAcceptanceEligible = $false
                gamePositive = $false
                status = $status
                error = $message
                completedAt = Get-UtcText
            }
            Write-JsonAtomic -Path $caseResultPath -Object $resultObject
            $chain = [pscustomobject]@{ schemaVersion = 1; kind = 'p45-controlled-collection-record-chain'; recordCount = $script:RecordSequence; headRecordSha256 = $script:LastRecordSha; recordsFileSha256 = Get-Sha256File -Path $script:RecordsPath; generatedAt = Get-UtcText }
            Write-JsonAtomic -Path (Join-Path $script:CaseRoot 'chain.json') -Object $chain
            $manifest = Get-CaseManifest -CaseRoot $script:CaseRoot
            Write-JsonAtomic -Path (Join-Path $script:CaseRoot 'manifest.json') -Object $manifest
            $manifestHash = Get-Sha256File -Path (Join-Path $script:CaseRoot 'manifest.json')
            Write-TextAtomic -Path (Join-Path $script:CaseRoot 'manifest.sha256.txt') -Text ($manifestHash + '  manifest.json' + [Environment]::NewLine)
            $failureSummary = [pscustomobject]@{
                schemaVersion = 1
                kind = 'p45-controlled-collection-run-summary'
                toolVersion = $script:ToolVersion
                status = $status
                exitCode = $code
                caseId = $script:CaseId
                runId = $script:RunId
                case = $Case
                saveFault = $SaveFault
                observedOutcome = 'NOT_RUN'
                factsEqual = $false
                collectionChain = 'FAIL'
                callerAcceptance = 'PENDING_INDEPENDENT_REVIEW'
                gamePositive = $false
                error = $message
                inputManifestSha256 = if ($null -eq $script:InputManifestSha256) { $null } else { $script:InputManifestSha256 }
                caseManifestSha256 = $manifestHash
                evidenceRoot = $script:EvidenceRoot
                recordedAt = Get-UtcText
            }
            Write-JsonAtomic -Path (Join-Path $script:EvidenceRoot 'collector-result.json') -Object $failureSummary
        }
    }
    catch { }
    Write-Host ('P45_CONTROLLED_FAILURE: ' + $message)
}
finally {
    if ($acquired) {
        try { $mutex.ReleaseMutex() } catch { }
    }
    try { $mutex.Dispose() } catch { }
}

exit $resultExit
