#Requires -Version 5.1
[CmdletBinding()]
param(
  [Parameter(Mandatory=$false)][ValidateSet('Probe','Devices','StartEmulator','ShutdownEmulator','DeviceInfo','ValidateArtifact','Install','Start','Stop','Capture','Input','BackgroundResume')]
  [string]$Action = 'Probe',
  [string]$Serial = 'emulator-5580',
  [int]$EmulatorPort = 5580,
  [string]$AvdName = 'medium_phone',
  [ValidateSet('auto','off','on')][string]$AccelMode = 'auto',
  [int]$AvdWaitSeconds = 600,
  [string]$Apk,
  [string]$EvidenceDir,
  [string]$StateFile,
  [string]$PackageId = 'com.majiang.game',
  [ValidateRange(1,600)][int]$AdbTimeoutSeconds = 30,
  [ValidateSet('tap','swipe','back')][string]$InputAction = 'tap',
  [int]$X = -1,
  [int]$Y = -1,
  [int]$X2 = -1,
  [int]$Y2 = -1,
  [int]$DurationMs = 300,
  [string]$AndroidSdkRoot = 'D:\Android\Sdk',
  [string]$AvdHome = 'D:\Android\avd',
  [string[]]$ExtraEmulatorArgs = @(),
  [switch]$ArtifactFixtureTest,
  [switch]$StateFixtureTest,
  [switch]$InputFixtureTest,
  [switch]$TreeFixtureTest,
  [switch]$EnvFixtureTest,
  [switch]$DeviceContractFixtureTest,
  [switch]$ReceiptFixtureTest,
  [switch]$TargetStateFixtureTest,
  [switch]$RootEligibilityFixtureTest
)
$ErrorActionPreference = 'Stop'
if (-not ('C05RJobNative' -as [type])) {
  Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class C05RJobNative {
  [StructLayout(LayoutKind.Sequential)] struct BasicLimit {
    public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
    public uint LimitFlags;
    public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
    public uint ActiveProcessLimit;
    public UIntPtr Affinity;
    public uint PriorityClass, SchedulingClass;
  }
  [StructLayout(LayoutKind.Sequential)] struct IoCounters {
    public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
    public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
  }
  [StructLayout(LayoutKind.Sequential)] struct ExtendedLimit {
    public BasicLimit BasicLimitInformation;
    public IoCounters IoInfo;
    public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
  }
  [StructLayout(LayoutKind.Sequential)] struct BasicAccounting {
    public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
    public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
  }
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr CreateJobObject(IntPtr attributes, string name);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimit info, uint length);
  [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool TerminateJobObject(IntPtr job, uint exitCode);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool QueryInformationJobObject(IntPtr job, int infoClass, out BasicAccounting info, uint length, IntPtr returnedLength);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool CloseHandle(IntPtr handle);
  public static IntPtr CreateKillOnCloseJob() {
    IntPtr job=CreateJobObject(IntPtr.Zero,null);
    if(job==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    ExtendedLimit info=new ExtendedLimit();info.BasicLimitInformation.LimitFlags=0x2000;
    if(!SetInformationJobObject(job,9,ref info,(uint)Marshal.SizeOf(typeof(ExtendedLimit)))) { int e=Marshal.GetLastWin32Error();CloseHandle(job);throw new Win32Exception(e); }
    return job;
  }
  public static void Assign(IntPtr job,int processId) {
    IntPtr process=OpenProcess(0x0101,false,processId);
    if(process==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    try { if(!AssignProcessToJobObject(job,process)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    finally { CloseHandle(process); }
  }
  public static void Terminate(IntPtr job) { if(!TerminateJobObject(job,1)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
  public static int ActiveProcesses(IntPtr job) {
    BasicAccounting info;
    if(!QueryInformationJobObject(job,1,out info,(uint)Marshal.SizeOf(typeof(BasicAccounting)),IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
    return checked((int)info.ActiveProcesses);
  }
  public static void Close(IntPtr handle) { if(handle!=IntPtr.Zero && !CloseHandle(handle)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
}
'@
}
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$adb = Join-Path $AndroidSdkRoot 'platform-tools\adb.exe'
$emulator = Join-Path $AndroidSdkRoot 'emulator\emulator.exe'

function ConvertTo-C05RWindowsArgument([string]$Value) {
  $builder = New-Object Text.StringBuilder; [void]$builder.Append('"'); $slashes = 0
  foreach ($ch in $Value.ToCharArray()) {
    if ($ch -eq [char]92) { $slashes++; continue }
    if ($ch -eq [char]34) { for ($i=0; $i -lt (2*$slashes+1); $i++) { [void]$builder.Append([char]92) }; [void]$builder.Append([char]34); $slashes = 0; continue }
    for ($i=0; $i -lt $slashes; $i++) { [void]$builder.Append([char]92) }
    $slashes = 0; [void]$builder.Append($ch)
  }
  for ($i=0; $i -lt (2*$slashes); $i++) { [void]$builder.Append([char]92) }
  [void]$builder.Append('"'); return $builder.ToString()
}
function Get-C05RDescendantProcessIds([int]$RootId) {
  # Android emulator.exe is a launcher: qemu-system-*, netsimd and crashpad are its
  # children. Killing only the launcher leaked them in a real diagnostic, so an owned
  # stop must take the whole ancestry-verified tree.
  $all = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Select-Object ProcessId,ParentProcessId)
  $found = New-Object System.Collections.Generic.List[int]
  $frontier = @($RootId)
  while ($frontier.Count -gt 0) {
    $next = New-Object System.Collections.Generic.List[int]
    foreach ($p in $all) {
      $candidate = [int]$p.ProcessId
      if (($frontier -contains [int]$p.ParentProcessId) -and $candidate -ne $RootId -and -not $found.Contains($candidate)) {
        $found.Add($candidate); $next.Add($candidate)
      }
    }
    $frontier = $next.ToArray()
  }
  return $found.ToArray()
}
function Get-C05RProcessParentId([int]$ProcessId) {
  $row = Get-CimInstance Win32_Process -Filter ("ProcessId=$ProcessId") -ErrorAction SilentlyContinue
  if ($row) { return [int]$row.ParentProcessId }
  return 0
}
function New-C05RProcessRecord([int]$ProcessId, [int]$ParentId) {
  $p = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
  if (-not $p) { return $null }
  $parentStart = 0
  if ($ParentId -gt 0) {
    $parent = Get-Process -Id $ParentId -ErrorAction SilentlyContinue
    if ($parent) { $parentStart = $parent.StartTime.ToUniversalTime().Ticks }
  }
  return [pscustomobject]@{ pid=$ProcessId; startUtcTicks=$p.StartTime.ToUniversalTime().Ticks;
    name=$p.ProcessName; parentPid=$ParentId; parentStartUtcTicks=$parentStart }
}
function Get-C05RObservedDescendantRecords([int]$RootId, [long]$RootTicks, [object[]]$ExistingRecords = @()) {
  $root = Get-Process -Id $RootId -ErrorAction SilentlyContinue
  if (-not $root -or $root.StartTime.ToUniversalTime().Ticks -ne $RootTicks) {
    return [pscustomobject]@{ rootIdentityMismatch=$true; trackingComplete=$false; records=@($ExistingRecords); error='Root was not verifiably alive for ancestry observation.' }
  }
  $records = New-Object System.Collections.Generic.List[object]
  foreach ($record in @($ExistingRecords)) { if ($record) { $records.Add($record) } }
  $rows = @(Get-CimInstance Win32_Process -ErrorAction Stop | Select-Object ProcessId,ParentProcessId)
  $frontier = @([pscustomobject]@{ pid=$RootId; ticks=$RootTicks; depth=0 })
  while ($frontier.Count) {
    $next = New-Object System.Collections.Generic.List[object]
    foreach ($parent in $frontier) {
      $liveParent = Get-Process -Id ([int]$parent.pid) -ErrorAction SilentlyContinue
      if (-not $liveParent -or $liveParent.StartTime.ToUniversalTime().Ticks -ne [long]$parent.ticks) { continue }
      foreach ($row in $rows) {
        $id = [int]$row.ProcessId
        if ([int]$row.ParentProcessId -ne [int]$parent.pid -or $id -eq $RootId -or @($records | Where-Object { $_.pid -eq $id }).Count) { continue }
        $child = Get-Process -Id $id -ErrorAction SilentlyContinue
        if (-not $child) { continue }
        $ticks = $child.StartTime.ToUniversalTime().Ticks
        if ($ticks -lt [long]$parent.ticks) { continue }
        $record = [pscustomobject]@{ pid=$id; ticks=$ticks; parentPid=[int]$parent.pid; parentTicks=[long]$parent.ticks; depth=([int]$parent.depth+1); observedWhileParentLive=$true }
        $records.Add($record); $next.Add([pscustomobject]@{ pid=$id; ticks=$ticks; depth=$record.depth })
      }
    }
    $frontier = $next.ToArray()
  }
  $root = Get-Process -Id $RootId -ErrorAction SilentlyContinue
  $complete = [bool]($root -and $root.StartTime.ToUniversalTime().Ticks -eq $RootTicks)
  return [pscustomobject]@{ rootIdentityMismatch=(-not $complete); trackingComplete=$complete; records=$records.ToArray(); error=$(if ($complete) { $null } else { 'Root exited or changed identity during ancestry observation.' }) }
}
function Get-C05RSafeObservedDescendantRecords([int]$RootId, [long]$RootTicks, [object[]]$ExistingRecords = @()) {
  try { return Get-C05RObservedDescendantRecords $RootId $RootTicks $ExistingRecords }
  catch { return [pscustomobject]@{ rootIdentityMismatch=$true; trackingComplete=$false; records=@($ExistingRecords); error=$_.Exception.ToString() } }
}
function Stop-C05RObservedDescendants([int]$RootId, [long]$RootTicks, [object[]]$Records = @(), [bool]$TrackingComplete = $false, [switch]$StopRoot, [IntPtr]$JobHandle = [IntPtr]::Zero) {
  if ($JobHandle -ne [IntPtr]::Zero) {
    $jobErrors = New-Object System.Collections.Generic.List[string]; $active = $null; $stoppedJob = $false
    try {
      $active = [C05RJobNative]::ActiveProcesses($JobHandle)
      if ($active -gt 0) { [C05RJobNative]::Terminate($JobHandle); $stoppedJob = $true }
      $deadline = [DateTime]::UtcNow.AddSeconds(5)
      while ([DateTime]::UtcNow -lt $deadline) { $active = [C05RJobNative]::ActiveProcesses($JobHandle); if ($active -eq 0) { break }; Start-Sleep -Milliseconds 50 }
    } catch { $jobErrors.Add($_.Exception.ToString()); $active = $null }
    $jobStatus = if ($null -eq $active) { 'OWNERSHIP_UNVERIFIED' } elseif ($active -ne 0 -or $jobErrors.Count) { 'CLEANUP_FAILED' } else { 'PASS' }
    return [pscustomobject]@{ status=$jobStatus; rootPid=$RootId; rootStartUtcTicks=$RootTicks; rootIdentityMismatch=$false; trackingComplete=($null -ne $active); unknown=($null -eq $active); jobOwned=$true; jobTerminated=$stoppedJob; stoppedPids=@(); rejectedPids=@(); residualPids=$(if ($null -ne $active) { @() } else { $null }); residualCount=$active; knownResidualCount=$active; killErrors=@($jobErrors) }
  }
  $stopped = New-Object System.Collections.Generic.List[int]
  $rejected = New-Object System.Collections.Generic.List[int]
  $killErrors = New-Object System.Collections.Generic.List[string]
  $rootNow = Get-Process -Id $RootId -ErrorAction SilentlyContinue
  if (-not $rootNow -or $rootNow.StartTime.ToUniversalTime().Ticks -ne $RootTicks) {
    return [pscustomobject]@{ status='OWNERSHIP_UNVERIFIED'; rootPid=$RootId; rootStartUtcTicks=$RootTicks; rootIdentityMismatch=$true; trackingComplete=$false; unknown=$true; stoppedPids=@(); rejectedPids=@($RootId); residualPids=$null; residualCount=$null; knownResidualCount=$null; killErrors=@('Root PID is absent or no longer matches its captured start time; descendant ownership cannot be established without the live root or its job object.') }
  }
  $deadline = [DateTime]::UtcNow.AddSeconds(5)
  foreach ($record in @($Records | Sort-Object -Property depth -Descending)) {
    if (-not $record.observedWhileParentLive) { $rejected.Add([int]$record.pid); continue }
    $live = Get-Process -Id ([int]$record.pid) -ErrorAction SilentlyContinue
    if (-not $live) { continue }
    if ($live.StartTime.ToUniversalTime().Ticks -ne [long]$record.ticks) { $rejected.Add([int]$record.pid); continue }
    $row = Get-CimInstance Win32_Process -Filter ("ProcessId=$([int]$record.pid)") -ErrorAction SilentlyContinue
    if (-not $row -or [int]$row.ParentProcessId -ne [int]$record.parentPid) { $rejected.Add([int]$record.pid); continue }
    if ([int]$record.parentPid -ne $RootId) {
      $parent = Get-Process -Id ([int]$record.parentPid) -ErrorAction SilentlyContinue
      if ($parent -and $parent.StartTime.ToUniversalTime().Ticks -ne [long]$record.parentTicks) { $rejected.Add([int]$record.pid); continue }
    } else {
      $parent = Get-Process -Id $RootId -ErrorAction SilentlyContinue
      if ($parent -and $parent.StartTime.ToUniversalTime().Ticks -ne $RootTicks) { $rejected.Add([int]$record.pid); continue }
    }
    try {
      $live.Kill()
      $remainingMs = [int][Math]::Max(0,[Math]::Min(1000,($deadline-[DateTime]::UtcNow).TotalMilliseconds))
      if ($remainingMs -gt 0 -and $live.WaitForExit($remainingMs)) { $stopped.Add([int]$record.pid) }
    } catch { $killErrors.Add("PID $($record.pid): $($_.Exception.Message)") }
  }
  if ($StopRoot) {
    $rootNow = Get-Process -Id $RootId -ErrorAction SilentlyContinue
    if ($rootNow) {
      if ($rootNow.StartTime.ToUniversalTime().Ticks -ne $RootTicks) { $rejected.Add($RootId) }
      else {
        try { $rootNow.Kill(); $remainingMs=[int][Math]::Max(0,[Math]::Min(1000,($deadline-[DateTime]::UtcNow).TotalMilliseconds)); if ($remainingMs -gt 0 -and $rootNow.WaitForExit($remainingMs)) { $stopped.Add($RootId) } }
        catch { $killErrors.Add("Root PID ${RootId}: $($_.Exception.Message)") }
      }
    }
  }
  $residual = New-Object System.Collections.Generic.List[int]
  do {
    $residual.Clear()
    foreach ($record in @($Records)+@([pscustomobject]@{pid=$RootId;ticks=$RootTicks})) {
      $live=Get-Process -Id ([int]$record.pid) -ErrorAction SilentlyContinue
      if ($live -and $live.StartTime.ToUniversalTime().Ticks -eq [long]$record.ticks) { $residual.Add([int]$record.pid) }
    }
    if ($residual.Count -eq 0) { break }
    Start-Sleep -Milliseconds 100
  } while ([DateTime]::UtcNow -lt $deadline)
  $status = if (-not $TrackingComplete) { 'OWNERSHIP_UNVERIFIED' } elseif ($rejected.Count -or $killErrors.Count -or $residual.Count) { 'CLEANUP_FAILED' } else { 'PASS' }
  return [pscustomobject]@{ status=$status; rootPid=$RootId; rootStartUtcTicks=$RootTicks; rootIdentityMismatch=(-not $TrackingComplete); trackingComplete=$TrackingComplete; unknown=(-not $TrackingComplete); stoppedPids=@($stopped); rejectedPids=@($rejected); residualPids=$(if ($TrackingComplete) { @($residual) } else { $null }); residualCount=$(if ($TrackingComplete) { $residual.Count } else { $null }); knownResidualCount=$residual.Count; killErrors=@($killErrors); trackingError=$(if ($TrackingComplete) { $null } else { 'Descendant ancestry was not completely observed while the root identity was live.' }) }
}
function Stop-C05ROwnedProcess([int]$ProcessId, [long]$StartTicks) {
  $observed = Get-C05RSafeObservedDescendantRecords $ProcessId $StartTicks
  $result = Stop-C05RObservedDescendants $ProcessId $StartTicks $observed.records $observed.trackingComplete -StopRoot
  return ($result.status -eq 'PASS')
}
function Stop-C05RVerifiedRecordSet([object[]]$Records, [int]$LauncherPid, [int]$QemuPid) {
  $live = New-Object System.Collections.Generic.List[object]
  $eligible = New-Object System.Collections.Generic.List[object]
  foreach ($r in @($Records)) {
    if ($null -eq $r) { continue }
    $p = Get-Process -Id ([int]$r.pid) -ErrorAction SilentlyContinue
    if (-not $p) { continue }
    if ($p.StartTime.ToUniversalTime().Ticks -ne [long]$r.startUtcTicks) { continue } # PID reused: not ours
    $live.Add($r)
    $isLauncherRoot = ([int]$r.pid -eq $LauncherPid -and [int]$r.parentPid -le 0)
    # A successful launcher handoff records qemu with parentPid=0 when the launcher has
    # already exited; both emitted forms are anchored to the captured QemuPid + live start.
    $isQemuRoot = ([int]$r.pid -eq $QemuPid -and ([int]$r.parentPid -eq $LauncherPid -or [int]$r.parentPid -eq 0) -and ([string]$r.name -match '^qemu-system'))
    $parentOk = $isLauncherRoot -or $isQemuRoot
    if (-not $parentOk -and [int]$r.parentPid -gt 0) {
      $parent = Get-Process -Id ([int]$r.parentPid) -ErrorAction SilentlyContinue
      if ($parent -and $parent.StartTime.ToUniversalTime().Ticks -eq [long]$r.parentStartUtcTicks -and
          (Get-C05RProcessParentId ([int]$r.pid)) -eq [int]$r.parentPid) { $parentOk = $true }
    }
    if ($parentOk) { $eligible.Add($r) }
  }
  for ($i = $eligible.Count - 1; $i -ge 0; $i--) {
    $p = Get-Process -Id ([int]$eligible[$i].pid) -ErrorAction SilentlyContinue
    if ($p -and $p.StartTime.ToUniversalTime().Ticks -eq [long]$eligible[$i].startUtcTicks) { try { $p.Kill() } catch { } }
  }
  $deadline = (Get-Date).AddSeconds(15)
  while ((Get-Date) -lt $deadline) {
    $alive = @($live | Where-Object { $p = Get-Process -Id ([int]$_.pid) -ErrorAction SilentlyContinue; $p -and $p.StartTime.ToUniversalTime().Ticks -eq [long]$_.startUtcTicks })
    if ($alive.Count -eq 0) { break }
    Start-Sleep -Milliseconds 250
  }
  # Return every live recorded process, including records rejected for invalid ancestry.
  return @($live | Where-Object { $p = Get-Process -Id ([int]$_.pid) -ErrorAction SilentlyContinue; $p -and $p.StartTime.ToUniversalTime().Ticks -eq [long]$_.startUtcTicks })
}
function Push-C05RChildEnvironment([string]$TempDir) {
  $names = @('ANDROID_HOME','ANDROID_SDK_ROOT','ANDROID_AVD_HOME','TEMP','TMP')
  $saved = @{}
  foreach ($n in $names) { $saved[$n] = [Environment]::GetEnvironmentVariable($n, 'Process') }
  $env:ANDROID_HOME = $AndroidSdkRoot
  $env:ANDROID_SDK_ROOT = $AndroidSdkRoot
  $env:ANDROID_AVD_HOME = $AvdHome
  if ($TempDir) {
    New-Item -ItemType Directory -Force -Path $TempDir | Out-Null
    $env:TEMP = $TempDir; $env:TMP = $TempDir
  }
  return $saved
}
function Pop-C05RChildEnvironment([hashtable]$Saved) {
  foreach ($n in $Saved.Keys) {
    if ([string]::IsNullOrEmpty($Saved[$n])) { Remove-Item -Path ('Env:' + $n) -ErrorAction SilentlyContinue }
    else { Set-Item -Path ('Env:' + $n) -Value $Saved[$n] }
  }
}
function Start-C05RProcess([string]$FilePath, [string[]]$Arguments) {
  $runDir = Join-Path $env:TEMP ('c05-adb-run-' + [guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Path $runDir | Out-Null
  $statusPath = Join-Path $runDir 'native-exit.txt'
  $argumentLine = (@($Arguments | ForEach-Object { ConvertTo-C05RWindowsArgument ([string]$_) }) -join ' ')
  $payload = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject ([pscustomobject]@{file=$FilePath;argumentLine=$argumentLine;statusPath=$statusPath}) -Compress)))
  $bridgeScript = '$ErrorActionPreference=''Stop'';$ProgressPreference=''SilentlyContinue'';$request=ConvertFrom-Json ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($env:C05R_JOB_PAYLOAD)));[void][Console]::ReadLine();$child=New-Object Diagnostics.Process;$si=New-Object Diagnostics.ProcessStartInfo;$si.FileName=$request.file;$si.Arguments=$request.argumentLine;$si.UseShellExecute=$false;$si.CreateNoWindow=$true;$si.RedirectStandardOutput=$true;$si.RedirectStandardError=$true;$child.StartInfo=$si;try{[void]$child.Start();$outTask=$child.StandardOutput.BaseStream.CopyToAsync([Console]::OpenStandardOutput());$errTask=$child.StandardError.BaseStream.CopyToAsync([Console]::OpenStandardError());$child.WaitForExit();$code=[int]$child.ExitCode;[IO.File]::WriteAllText($request.statusPath,[string]$code);[Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($outTask,$errTask));exit $code}catch{try{$bytes=[Text.Encoding]::UTF8.GetBytes($_.Exception.ToString());[Console]::OpenStandardError().Write($bytes,0,$bytes.Length)}catch{};exit 1}finally{$child.Dispose()}'
  $psi = New-Object Diagnostics.ProcessStartInfo
  $psi.FileName = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
  $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($bridgeScript))
  $psi.Arguments = (@('-NoProfile','-NonInteractive','-EncodedCommand',$encoded) | ForEach-Object { ConvertTo-C05RWindowsArgument ([string]$_) }) -join ' '
  $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true; $psi.WindowStyle = 'Hidden'
  $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
  $psi.EnvironmentVariables['C05R_JOB_PAYLOAD'] = $payload
  $proc = New-Object Diagnostics.Process; $proc.StartInfo = $psi; $job = [C05RJobNative]::CreateKillOnCloseJob()
  $savedEnv = Push-C05RChildEnvironment
  try { [void]$proc.Start() } finally { Pop-C05RChildEnvironment $savedEnv }
  try {
    [C05RJobNative]::Assign($job,$proc.Id)
    $proc | Add-Member -NotePropertyName C05RJobHandle -NotePropertyValue $job
    $proc | Add-Member -NotePropertyName C05RStatusPath -NotePropertyValue $statusPath
    $proc | Add-Member -NotePropertyName C05RRunDir -NotePropertyValue $runDir
    $proc.StandardInput.WriteLine('GO'); $proc.StandardInput.Close()
  } catch {
    try { [C05RJobNative]::Terminate($job) } catch { }
    if (-not $proc.HasExited) { try { $proc.Kill(); [void]$proc.WaitForExit(3000) } catch { } }
    [C05RJobNative]::Close($job); $proc.Dispose(); Remove-Item -LiteralPath $statusPath -Force -ErrorAction SilentlyContinue; Remove-Item -LiteralPath $runDir -Force -ErrorAction SilentlyContinue; throw
  }
  return $proc
}
function Invoke-C05RToFiles([string]$FilePath, [string[]]$Arguments, [int]$TimeoutSeconds, [string]$OutPath, [string]$ErrPath) {
  $line = (@($Arguments | ForEach-Object { ConvertTo-C05RWindowsArgument ([string]$_) }) -join ' ')
  $savedEnv = Push-C05RChildEnvironment
  try {
    $proc = Start-Process -FilePath $FilePath -ArgumentList $line -RedirectStandardOutput $OutPath -RedirectStandardError $ErrPath -PassThru -WindowStyle Hidden
  } finally { Pop-C05RChildEnvironment $savedEnv }
  $ticks = $proc.StartTime.ToUniversalTime().Ticks
  $exited = $proc.WaitForExit($TimeoutSeconds * 1000)
  $killed = $false
  if (-not $exited) { $killed = Stop-C05ROwnedProcess $proc.Id $ticks; [void]$proc.WaitForExit(2000) }
  $proc.Refresh()
  $stdout = if (Test-Path -LiteralPath $OutPath) { [string](Get-Content -LiteralPath $OutPath -Raw -ErrorAction SilentlyContinue) } else { '' }
  $stderr = if (Test-Path -LiteralPath $ErrPath) { [string](Get-Content -LiteralPath $ErrPath -Raw -ErrorAction SilentlyContinue) } else { '' }
  $exit = if ($exited -and $proc.HasExited) { [int]$proc.ExitCode } else { $null }
  return [pscustomobject]@{ file=$FilePath; args=$Arguments; exit=$exit; timedOut=(-not $exited); timeoutSeconds=$TimeoutSeconds;
    killedOwnedProcess=$killed; stdout=$stdout; stderr=$stderr }
}
function Invoke-C05RAdb([string[]]$AdbArgs, [int]$TimeoutSeconds = $AdbTimeoutSeconds) {
  $proc = Start-C05RProcess $adb (@('-s',$Serial) + $AdbArgs)
  $pidOwn = $proc.Id; $ticks = $proc.StartTime.ToUniversalTime().Ticks
  $outTask = $proc.StandardOutput.ReadToEndAsync(); $errTask = $proc.StandardError.ReadToEndAsync()
  $watch = [Diagnostics.Stopwatch]::StartNew(); $exited = $false
  while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds) { if ($proc.WaitForExit(50)) { $exited = $true; break } }
  $watch.Stop()
  $drained = $false
  try { $drained = [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($outTask,$errTask), [TimeSpan]::FromSeconds(3)) } catch { }
  $initialDrainComplete = $drained
  $cleanup = Stop-C05RObservedDescendants $pidOwn $ticks @() $true -StopRoot:(-not $exited) -JobHandle $proc.C05RJobHandle
  if (-not $exited) { [void]$proc.WaitForExit(3000) }
  if (-not $drained -or -not $exited) { try { $drained = [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($outTask,$errTask), [TimeSpan]::FromSeconds(3)) } catch { } }
  $stdout = if ($outTask.IsCompleted) { $outTask.Result } else { '' }; $stderr = if ($errTask.IsCompleted) { $errTask.Result } else { '' }
  $native = if (Test-Path -LiteralPath $proc.C05RStatusPath -PathType Leaf) { [int](Get-Content -LiteralPath $proc.C05RStatusPath -Raw) } else { $null }
  try { [C05RJobNative]::Close($proc.C05RJobHandle) } catch { $cleanup.status='CLEANUP_FAILED'; $cleanup.killErrors=@($cleanup.killErrors)+@($_.Exception.ToString()) }
  $cleanupFailed = $cleanup.status -ne 'PASS' -or $cleanup.trackingComplete -ne $true -or $cleanup.unknown -eq $true -or $null -eq $cleanup.residualCount -or $cleanup.residualCount -ne 0 -or $null -eq $cleanup.knownResidualCount -or $cleanup.knownResidualCount -ne 0 -or @($cleanup.rejectedPids).Count -gt 0 -or @($cleanup.killErrors).Count -gt 0
  $status = if ($cleanupFailed) { if ($cleanup.status -eq 'OWNERSHIP_UNVERIFIED') { 'OWNERSHIP_UNVERIFIED' } else { 'CLEANUP_FAILED' } } elseif ($null -eq $native) { 'TIMEOUT' } elseif (-not $initialDrainComplete) { 'OUTPUT_DRAIN_TIMEOUT' } elseif (-not $drained) { 'OUTPUT_DRAIN_TIMEOUT' } elseif ($native -eq 0) { 'PASS' } else { 'FAIL' }
  $result = [pscustomobject]@{ action=($AdbArgs -join ' '); exit=$native; timedOut=($null -eq $native); timeoutSeconds=$TimeoutSeconds;
    pid=$pidOwn; startUtcTicks=$ticks; drainComplete=$drained; initialDrainComplete=$initialDrainComplete; cleanup=$cleanup; stdout=$stdout; stderr=$stderr; status=$status }
  $proc.Dispose(); Remove-Item -LiteralPath $proc.C05RStatusPath -Force -ErrorAction SilentlyContinue; Remove-Item -LiteralPath $proc.C05RRunDir -Force -ErrorAction SilentlyContinue; return $result
}
function Invoke-C05RAdbBinary([string[]]$AdbArgs, [int]$TimeoutSeconds = $AdbTimeoutSeconds) {
  $proc = Start-C05RProcess $adb (@('-s',$Serial) + $AdbArgs)
  $pidOwn = $proc.Id; $ticks = $proc.StartTime.ToUniversalTime().Ticks; $memory = New-Object IO.MemoryStream
  $outTask = $proc.StandardOutput.BaseStream.CopyToAsync($memory); $errTask = $proc.StandardError.ReadToEndAsync()
  $watch = [Diagnostics.Stopwatch]::StartNew(); $exited = $false
  while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds) { if ($proc.WaitForExit(50)) { $exited = $true; break } }
  $watch.Stop()
  $drained = $false
  try { $drained = [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($outTask,$errTask), [TimeSpan]::FromSeconds(3)) } catch { }
  $initialDrainComplete = $drained
  $cleanup = Stop-C05RObservedDescendants $pidOwn $ticks @() $true -StopRoot:(-not $exited) -JobHandle $proc.C05RJobHandle
  if (-not $exited) { [void]$proc.WaitForExit(3000) }
  if (-not $drained -or -not $exited) { try { $drained = [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($outTask,$errTask), [TimeSpan]::FromSeconds(3)) } catch { } }
  $stderr = if ($errTask.IsCompleted) { $errTask.Result } else { '' }; $bytes = $memory.ToArray()
  $native = if (Test-Path -LiteralPath $proc.C05RStatusPath -PathType Leaf) { [int](Get-Content -LiteralPath $proc.C05RStatusPath -Raw) } else { $null }
  try { [C05RJobNative]::Close($proc.C05RJobHandle) } catch { $cleanup.status='CLEANUP_FAILED'; $cleanup.killErrors=@($cleanup.killErrors)+@($_.Exception.ToString()) }
  $cleanupFailed = $cleanup.status -ne 'PASS' -or $cleanup.trackingComplete -ne $true -or $cleanup.unknown -eq $true -or $null -eq $cleanup.residualCount -or $cleanup.residualCount -ne 0 -or $null -eq $cleanup.knownResidualCount -or $cleanup.knownResidualCount -ne 0 -or @($cleanup.rejectedPids).Count -gt 0 -or @($cleanup.killErrors).Count -gt 0
  $status = if ($cleanupFailed) { if ($cleanup.status -eq 'OWNERSHIP_UNVERIFIED') { 'OWNERSHIP_UNVERIFIED' } else { 'CLEANUP_FAILED' } } elseif ($null -eq $native) { 'TIMEOUT' } elseif (-not $initialDrainComplete) { 'OUTPUT_DRAIN_TIMEOUT' } elseif (-not $drained) { 'OUTPUT_DRAIN_TIMEOUT' } elseif ($native -eq 0) { 'PASS' } else { 'FAIL' }
  $result = [pscustomobject]@{ action=($AdbArgs -join ' '); exit=$native; timedOut=($null -eq $native); timeoutSeconds=$TimeoutSeconds;
    pid=$pidOwn; startUtcTicks=$ticks; drainComplete=$drained; initialDrainComplete=$initialDrainComplete; cleanup=$cleanup; stderr=$stderr; bytes=$bytes; status=$status }
  $memory.Dispose(); $proc.Dispose(); Remove-Item -LiteralPath $proc.C05RStatusPath -Force -ErrorAction SilentlyContinue; Remove-Item -LiteralPath $proc.C05RRunDir -Force -ErrorAction SilentlyContinue; return $result
}
function Save-C05RJson([string]$Path, [object]$Value) { [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 20), (New-Object Text.UTF8Encoding($false))) }
function Get-C05REvidenceDir([string]$ActionName) {
  $buildRoot = [IO.Path]::GetFullPath((Join-Path $root 'build')) + [IO.Path]::DirectorySeparatorChar
  $parent = if ($EvidenceDir) { [IO.Path]::GetFullPath($EvidenceDir) } else { Join-Path $root 'build\c05-android-emulator-device' }
  if (-not $parent.StartsWith($buildRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing evidence output outside this worktree build directory.' }
  if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
  $dir = Join-Path $parent ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $ActionName + '-' + [guid]::NewGuid().ToString('N').Substring(0,8))
  if (-not $dir.StartsWith($buildRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing evidence output outside this worktree build directory.' }
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  return $dir
}
function Get-C05RBadgingContract([string]$Metadata) {
  $packageLines = [regex]::Matches($Metadata, "(?m)^package:\s+name='(?<value>[^']+)'(?:\s|$).*$")
  $minLines = [regex]::Matches($Metadata, "(?m)^minSdkVersion:'(?<value>\d+)'\s*$")
  $targetLines = [regex]::Matches($Metadata, "(?m)^targetSdkVersion:'(?<value>\d+)'\s*$")
  $abiLines = [regex]::Matches($Metadata, '(?m)^native-code:\s*(?<value>[^\r\n]*)\s*$')
  $activityLines = [regex]::Matches($Metadata, "(?m)^launchable-activity:\s+name='(?<value>[^']+)'(?:\s|$).*$")
  if ($packageLines.Count -ne 1 -or $minLines.Count -ne 1 -or $targetLines.Count -ne 1 -or $abiLines.Count -ne 1 -or $activityLines.Count -ne 1) {
    throw 'FAIL: APK badging package/SDK/native-code/launchable-activity records are missing or duplicated.'
  }
  $abiText = $abiLines[0].Groups['value'].Value
  if (-not [regex]::IsMatch($abiText, "^'[A-Za-z0-9_.-]+'(?:\s+'[A-Za-z0-9_.-]+')*$")) { throw 'FAIL: malformed APK native-code ABI record.' }
  $abis = @([regex]::Matches($abiText, "'(?<abi>[A-Za-z0-9_.-]+)'") | ForEach-Object { $_.Groups['abi'].Value })
  return [pscustomobject]@{ package=$packageLines[0].Groups['value'].Value; minSdk=[int]$minLines[0].Groups['value'].Value;
    targetSdk=[int]$targetLines[0].Groups['value'].Value; abis=$abis; activity=$activityLines[0].Groups['value'].Value }
}
function Get-C05RRequiredProperty([object]$Object, [string]$Name) {
  if ($null -eq $Object -or -not $Object.PSObject.Properties[$Name]) { throw "FAIL: evidence is missing required field '$Name'." }
  return $Object.$Name
}
function Get-C05RStrictInt([object]$Object, [string]$Name) {
  $property = if ($Object) { $Object.PSObject.Properties[$Name] } else { $null }
  if (-not $property -or $null -eq $property.Value) { throw "FAIL: required integer field '$Name' is missing or null." }
  $value = $property.Value
  if ($value -is [string] -or $value -is [bool]) { throw "FAIL: required integer field '$Name' has the wrong type." }
  if ($value -is [double] -or $value -is [single] -or $value -is [decimal]) {
    $asDouble = [double]$value
    if ([double]::IsNaN($asDouble) -or [double]::IsInfinity($asDouble) -or $asDouble -ne [math]::Truncate($asDouble)) {
      throw "FAIL: required integer field '$Name' is fractional or non-finite."
    }
  }
  try { $number = [long]$value } catch { throw "FAIL: required integer field '$Name' is not numeric." }
  if ($number -lt [int]::MinValue -or $number -gt [int]::MaxValue) { throw "FAIL: required integer field '$Name' overflows Int32." }
  return [int]$number
}
function Get-C05RStrictBool([object]$Object, [string]$Name, [bool]$Expected = $true) {
  $property = if ($Object) { $Object.PSObject.Properties[$Name] } else { $null }
  if (-not $property -or $null -eq $property.Value) { throw "FAIL: required boolean field '$Name' is missing or null." }
  if ($property.Value -isnot [bool]) { throw "FAIL: required boolean field '$Name' has a non-boolean type." }
  if ([bool]$property.Value -ne $Expected) { throw "FAIL: required boolean field '$Name' is not $Expected." }
  return [bool]$property.Value
}
function Get-C05RStrictArray([object]$Object, [string]$Name) {
  $property = if ($Object) { $Object.PSObject.Properties[$Name] } else { $null }
  if (-not $property -or $null -eq $property.Value) { throw "FAIL: required array field '$Name' is missing or null." }
  return @($property.Value)
}
function Get-C05RVerifiedArtifact([string]$Path, [string]$ExpectedPackage) {
  if ([string]::IsNullOrWhiteSpace($Path)) { throw 'USAGE: this action requires -Apk bound to successful C05 ANDROID_EMULATOR build evidence.' }
  $apkPath = [IO.Path]::GetFullPath($Path)
  if ([IO.Path]::GetExtension($apkPath) -ine '.apk' -or -not (Test-Path -LiteralPath $apkPath -PathType Leaf)) { throw 'FAIL: selected artifact must be an existing .apk file.' }
  $runDir = Split-Path $apkPath -Parent
  $summaryPath = Join-Path $runDir 'summary.json'; $reportPath = Join-Path $runDir 'build-report.json'; $metadataPath = Join-Path $runDir 'apk-metadata.txt'
  foreach ($required in @($summaryPath,$reportPath,$metadataPath)) { if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw 'FAIL: selected APK lacks successful C05 emulator build evidence.' } }
  $summary = Get-Content $summaryPath -Raw | ConvertFrom-Json
  $report = Get-Content $reportPath -Raw | ConvertFrom-Json
  $metadata = Get-Content $metadataPath -Raw
  $file = Get-Item $apkPath; $sha = (Get-FileHash $apkPath -Algorithm SHA256).Hash
  $actualReportSha = (Get-FileHash $reportPath -Algorithm SHA256).Hash
  $actualMetadataSha = (Get-FileHash $metadataPath -Algorithm SHA256).Hash
  $summaryReportSha = [string](Get-C05RRequiredProperty $summary 'buildReportSha256')
  $summaryMetadataSha = [string](Get-C05RRequiredProperty $summary 'metadataSha256')
  if ($summaryReportSha -ine $actualReportSha) { throw 'FAIL: build-report.json SHA256 differs from the summary hash binding.' }
  if ($summaryMetadataSha -ine $actualMetadataSha) { throw 'FAIL: apk-metadata.txt SHA256 differs from the summary hash binding.' }
  if ([string]$summary.status -ne 'PASS' -or (Get-C05RStrictInt $summary 'nativeExit') -ne 0 -or
      (Get-C05RStrictInt $summary 'apkBytes') -ne [long]$file.Length -or
      [string](Get-C05RRequiredProperty $summary 'apkSha256') -ine $sha) {
    throw 'FAIL: selected APK does not match a successful fresh emulator-variant build summary.'
  }
  $sourceHead = [string](Get-C05RRequiredProperty $summary 'sourceHead')
  if ($sourceHead -notmatch '^[0-9a-fA-F]{40}$') { throw 'FAIL: summary sourceHead is missing or malformed.' }
  $reportSourceHead = [string](Get-C05RRequiredProperty $report 'sourceHead')
  if ($reportSourceHead -notmatch '^[0-9a-fA-F]{40}$' -or $reportSourceHead -cne $sourceHead) {
    throw 'FAIL: summary/report sourceHead is missing or contradictory.'
  }
  $summaryCache = [string](Get-C05RRequiredProperty $summary 'buildCacheMode')
  $schema = Get-C05RStrictInt $report 'schema'
  $result = [string](Get-C05RRequiredProperty $report 'result')
  $errors = Get-C05RStrictInt $report 'errors'
  $stepErrors = Get-C05RStrictInt $report 'stepErrors'
  $stepExceptions = Get-C05RStrictInt $report 'stepExceptions'
  $failedAssertions = Get-C05RStrictArray $report 'failedAssertions'
  $rewritten = Get-C05RStrictArray $report 'productionInputsRewritten'
  $reportPackage = [string](Get-C05RRequiredProperty $report 'packageId')
  $reportCache = [string](Get-C05RRequiredProperty $report 'cacheMode')
  $resolvedTarget = Get-C05RStrictInt $report 'resolvedTargetSdk'
  if ($schema -ne 2 -or $result -ne 'Succeeded' -or $errors -ne 0 -or $stepErrors -ne 0 -or $stepExceptions -ne 0 -or
      $failedAssertions.Count -ne 0 -or $rewritten.Count -ne 0) {
    throw 'FAIL: BuildReport is not a clean schema-2 success (errors/stepErrors/stepExceptions/failedAssertions/rewrites must be zero/empty).'
  }
  $reportMinSdk = Get-C05RStrictInt $report 'minSdkVersion'
  [void](Get-C05RStrictBool $report 'tmpResources')
  [void](Get-C05RStrictBool $report 'landscapeOnly')
  [void](Get-C05RStrictBool $report 'fullscreen')
  [void](Get-C05RStrictBool $report 'outsideSafeArea')
  if ($report.target -ne 'Android' -or $report.backend -ne 'IL2CPP' -or $report.architectures -ne 'X86_64' -or
      $report.variant -ne 'ANDROID_EMULATOR' -or $reportPackage -ne $ExpectedPackage -or
      $reportMinSdk -lt 21) {
    throw 'FAIL: selected APK BuildReport/variant/ABI/package/settings do not match the emulator x86_64 full game contract.'
  }
  if ($summaryCache -cne $reportCache -or @('CLEAN','REUSE') -notcontains $reportCache) {
    throw 'FAIL: summary/report build cache mode is missing or contradictory.'
  }
  $requiredScenes = @('Assets/Scenes/Boot.unity','Assets/Scenes/Menu.unity','Assets/Scenes/Lobby.unity',
    'Assets/Scenes/Table.unity','Assets/Scenes/Spectator.unity','Assets/Scenes/Settlement.unity')
  $reportScenes = @($report.scenes)
  if ($reportScenes.Count -ne $requiredScenes.Count) { throw 'FAIL: BuildReport scene count differs from the fixed six production scenes.' }
  for ($i = 0; $i -lt $requiredScenes.Count; $i++) {
    if ([string]$reportScenes[$i] -cne $requiredScenes[$i]) { throw "FAIL: BuildReport scene order differs at index $i ('$($reportScenes[$i])' vs '$($requiredScenes[$i])')." }
  }
  # Semantic metadata checks come before the output-path check so a wrong-package
  # fixture is rejected for the package gate, not incidentally for a path mismatch.
  $badging = Get-C05RBadgingContract $metadata
  if ($badging.package -cne $ExpectedPackage) { throw "FAIL: APK package '$($badging.package)' differs from expected '$ExpectedPackage'." }
  if ($badging.abis.Count -ne 1 -or $badging.abis[0] -cne 'x86_64') { throw 'FAIL: APK is not exactly x86_64.' }
  if ([string]::IsNullOrWhiteSpace($badging.activity)) { throw 'FAIL: APK has no launchable activity.' }
  if ($badging.minSdk -ne [int]$report.minSdkVersion) { throw 'FAIL: APK minSdk differs from the BuildReport.' }
  if ($badging.targetSdk -ne $resolvedTarget) { throw 'FAIL: APK targetSdk differs from the BuildReport resolved target.' }
  if ($badging.targetSdk -lt $badging.minSdk) { throw 'FAIL: APK targetSdk is below minSdk.' }
  if ([IO.Path]::GetFullPath([string]$report.output) -ne $apkPath) { throw 'FAIL: selected APK path differs from the BuildReport output path.' }
  return [pscustomobject]@{ apkPath=$apkPath; apkName=(Split-Path $apkPath -Leaf); sha256=$sha; bytes=$file.Length;
    sourceHead=$sourceHead; packageId=$reportPackage; activity=$badging.activity;
    buildSummarySha256=(Get-FileHash $summaryPath -Algorithm SHA256).Hash;
    buildReportSha256=$actualReportSha; metadataSha256=$actualMetadataSha; runDir=$runDir }
}
function Test-C05RStateIdentity([object]$State, [string]$ExpectedSerial, [string]$ExpectedAvd) {
  if ($null -eq $State) { throw 'FAIL: emulator state is missing.' }
  $port = [int]$State.port
  if ($port -lt 5554 -or $port -gt 5682) { throw 'FAIL: state emulator port is outside the supported range.' }
  $derivedSerial = "emulator-$port"
  if ([string]$State.serial -cne $derivedSerial) { throw "FAIL: state serial '$($State.serial)' does not correspond to port $port ('$derivedSerial')." }
  if ([string]$State.serial -cne $ExpectedSerial) { throw "FAIL: state serial '$($State.serial)' differs from requested '$ExpectedSerial'." }
  if ([string]$State.avd -cne $ExpectedAvd) { throw "FAIL: state AVD '$($State.avd)' differs from requested '$ExpectedAvd'." }
  if ([int]$State.emulatorPid -le 0 -or [long]$State.startUtcTicks -le 0) { throw 'FAIL: state lacks an owned emulator PID/start-time identity.' }
  if ($State.PSObject.Properties['qemuPid'] -and [int]$State.qemuPid -gt 0 -and [long]$State.qemuStartUtcTicks -le 0) {
    throw 'FAIL: state qemu PID lacks its start-time identity.'
  }
  return $true
}
function Get-C05RState([string]$Path, [string]$ExpectedSerial, [string]$ExpectedAvd) {
  if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'USAGE: this action requires -StateFile produced by StartEmulator.' }
  $state = Get-Content $Path -Raw | ConvertFrom-Json
  [void](Test-C05RStateIdentity $state $ExpectedSerial $ExpectedAvd)
  $proc = Get-Process -Id ([int]$state.emulatorPid) -ErrorAction SilentlyContinue
  $launcherAlive = [bool]($proc -and $proc.StartTime.ToUniversalTime().Ticks -eq [long]$state.startUtcTicks)
  $qemuAlive = $false
  if ([int]$state.qemuPid -gt 0) {
    $qproc = Get-Process -Id ([int]$state.qemuPid) -ErrorAction SilentlyContinue
    $qemuAlive = [bool]($qproc -and $qproc.StartTime.ToUniversalTime().Ticks -eq [long]$state.qemuStartUtcTicks)
  }
  if (-not $launcherAlive -and -not $qemuAlive) { throw 'BLOCKED: recorded emulator launcher/qemu PID+start-time is not alive; refusing to touch another emulator.' }
  $stateResult = Invoke-C05RAdb @('get-state')
  if ($stateResult.status -ne 'PASS' -or ([string]$stateResult.stdout).Trim() -ne 'device') { throw "BLOCKED: recorded serial is not an authorized adb device (status=$($stateResult.status))." }
  $avdResult = Invoke-C05RAdb @('emu','avd','name')
  $avdActual = (([string]$avdResult.stdout) -split "`r?`n")[0].Trim()
  if ($avdResult.status -ne 'PASS' -or $avdActual -cne $ExpectedAvd) { throw "BLOCKED: adb serial belongs to AVD '$avdActual', not '$ExpectedAvd'." }
  $state | Add-Member -NotePropertyName statePath -NotePropertyValue ([IO.Path]::GetFullPath($Path)) -Force
  return $state
}
function Assert-C05RInputBounds([string]$Kind, [int]$Px, [int]$Py, [int]$Px2, [int]$Py2, [int]$Duration, [int]$Width, [int]$Height) {
  if ($Width -le 0 -or $Height -le 0) { throw 'FAIL: display size is unknown; refusing unvalidated input coordinates.' }
  if ($Kind -eq 'tap') {
    if ($Px -lt 0 -or $Py -lt 0 -or $Px -ge $Width -or $Py -ge $Height) { throw 'FAIL: tap coordinate is outside the reported display.' }
  } elseif ($Kind -eq 'swipe') {
    if ($Px -lt 0 -or $Py -lt 0 -or $Px2 -lt 0 -or $Py2 -lt 0 -or $Px -ge $Width -or $Px2 -ge $Width -or $Py -ge $Height -or $Py2 -ge $Height) {
      throw 'FAIL: swipe coordinate is outside the reported display.'
    }
  } elseif ($Kind -ne 'back') { throw 'FAIL: unsupported input action.' }
  if ($Duration -lt 50 -or $Duration -gt 5000) { throw 'FAIL: input duration must be 50..5000 ms.' }
  return $true
}
function Get-C05RDisplayMetricsFromText([string]$Text, [int]$Rotation) {
  $override = [regex]::Match([string]$Text, '(?m)Override size:\s*(?<w>\d+)x(?<h>\d+)')
  $physical = [regex]::Match([string]$Text, '(?m)Physical size:\s*(?<w>\d+)x(?<h>\d+)')
  $chosen = if ($override.Success) { $override } elseif ($physical.Success) { $physical } else { $null }
  if ($null -eq $chosen) { throw 'FAIL: display size output has neither Override nor Physical size.' }
  $w = [int]$chosen.Groups['w'].Value; $h = [int]$chosen.Groups['h'].Value
  $physicalWidth = 0; $physicalHeight = 0
  if ($physical.Success) { $physicalWidth = [int]$physical.Groups['w'].Value; $physicalHeight = [int]$physical.Groups['h'].Value }
  $logicalWidth = $w; $logicalHeight = $h
  if (($Rotation % 2) -ne 0) { $logicalWidth = $h; $logicalHeight = $w }
  return [pscustomobject]@{ width=$logicalWidth; height=$logicalHeight; source=$(if ($override.Success) { 'Override' } else { 'Physical' });
    physicalWidth=$physicalWidth; physicalHeight=$physicalHeight;
    rotation=$Rotation; rawText=[string]$Text }
}
function Get-C05RDisplaySize([string]$SerialName) {
  $r = Invoke-C05RAdb @('shell','wm','size')
  if ($r.status -ne 'PASS') { throw 'FAIL: could not read display size.' }
  $rotation = 0; $rotationObserved = $false
  $ri = $null
  $rw = Invoke-C05RAdb @('shell','dumpsys','window','displays')
  if ($rw.status -eq 'PASS') {
    $mr = [regex]::Match([string]$rw.stdout, '(?m)\b(?:mCurrentRotation=ROTATION_|mDisplayRotation=ROTATION_|mRotation=ROTATION_|mCurrentRotation=|mDisplayRotation=|orientation=)(?<r>[0-3]|0|90|180|270)\b')
    if ($mr.Success) {
      $val = $mr.Groups['r'].Value
      $rotation = switch ($val) { '90' { 1 } '180' { 2 } '270' { 3 } default { [int]$val } }
      $rotationObserved = $true
    }
  }
  if (-not $rotationObserved) {
    $ri = Invoke-C05RAdb @('shell','dumpsys','input')
    if ($ri.status -eq 'PASS') {
      $m = [regex]::Match([string]$ri.stdout, '(?m)SurfaceOrientation:\s*(?<r>[0-3])')
      if (-not $m.Success) {
        $m = [regex]::Match([string]$ri.stdout, '(?m)\borientation=(?<r>[0-3])\b')
      }
      if ($m.Success) {
        $rotation = [int]$m.Groups['r'].Value
        $rotationObserved = $true
      }
    }
  }
  # A successfully observed SurfaceOrientation from dumpsys input is authoritative;
  # only query user_rotation if neither window display rotation nor input orientation was observed.
  if (-not $rotationObserved) {
    $rs = Invoke-C05RAdb @('shell','settings','get','system','user_rotation')
    if ($rs.status -eq 'PASS' -and ([string]$rs.stdout).Trim() -match '^(?<r>[0-3])$') {
      $rotation = [int]$Matches['r']
      $rotationObserved = $true
    }
  }
  $metrics = Get-C05RDisplayMetricsFromText ([string]$r.stdout) $rotation
  $metrics | Add-Member -NotePropertyName rotationObserved -NotePropertyValue $rotationObserved -Force
  $metrics | Add-Member -NotePropertyName raw -NotePropertyValue $r -Force
  $metrics | Add-Member -NotePropertyName inputDump -NotePropertyValue $ri -Force
  return $metrics
}
function Get-C05RForegroundState([string]$ActivityDump, [string]$WindowDump, [string]$Package) {
  $componentPattern = '(?<pkg>[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)+)/(?<activity>[A-Za-z0-9_.$]+)'
  $resumedComponents = New-Object System.Collections.Generic.List[string]
  foreach ($m in [regex]::Matches([string]$ActivityDump, '(?m)^\s*(?:mResumedActivity|topResumedActivity|ResumedActivity)\s*[:=]\s*(?<rest>[^\r\n]+)')) {
    $c = [regex]::Match($m.Groups['rest'].Value, $componentPattern)
    if ($c.Success) { $resumedComponents.Add($c.Value) }
  }
  $focusedComponents = New-Object System.Collections.Generic.List[string]
  foreach ($m in [regex]::Matches([string]$WindowDump, '(?m)(?:mCurrentFocus|mFocusedApp)\s*[:=]\s*(?<rest>[^\r\n]+)')) {
    $c = [regex]::Match($m.Groups['rest'].Value, $componentPattern)
    if ($c.Success) { $focusedComponents.Add($c.Value) }
  }
  $resumedUnique = @($resumedComponents | Select-Object -Unique)
  $focusedUnique = @($focusedComponents | Select-Object -Unique)
  $resumed = if ($resumedUnique.Count -eq 1) { $resumedUnique[0] } else { $null }
  $focused = if ($focusedUnique.Count -eq 1) { $focusedUnique[0] } else { $null }
  $resumedPackage = if ($resumed -match '^(?<p>[^/]+)/') { $Matches['p'] } else { $null }
  $focusedPackage = if ($focused -match '^(?<p>[^/]+)/') { $Matches['p'] } else { $null }
  $ambiguous = ($resumedUnique.Count -gt 1 -or $focusedUnique.Count -gt 1)
  return [pscustomobject]@{ resumedComponent=$resumed; focusedComponent=$focused;
    resumedPackage=$resumedPackage; focusedPackage=$focusedPackage;
    resumedMatches=[bool](-not $ambiguous -and $resumedPackage -ceq $Package);
    focusedMatches=[bool](-not $ambiguous -and $focusedPackage -ceq $Package);
    ambiguous=$ambiguous; resumedCandidates=@($resumedUnique); focusedCandidates=@($focusedUnique) }
}
function Get-C05RDeviceFileDigest([string]$Path) {
  if ([string]::IsNullOrWhiteSpace($Path)) { throw 'FAIL: installed APK path is empty.' }
  $r = Invoke-C05RAdb @('shell','sha256sum',$Path) 60
  if ($r.status -ne 'PASS') { throw "FAIL: device sha256sum probe failed (status=$($r.status), exit=$($r.exit))." }
  if (([string]$r.stdout) -notmatch '^\s*(?<sha>[0-9a-fA-F]{64})\b') { throw 'FAIL: device sha256sum output is malformed.' }
  return $Matches['sha'].ToLowerInvariant()
}
function Get-C05RInstalledIdentity([string]$ExpectedPackage) {
  $pm = Invoke-C05RAdb @('shell','pm','path',$ExpectedPackage) 60
  if ($pm.status -ne 'PASS') { throw 'FAIL: could not query installed package path.' }
  $paths = @([regex]::Matches([string]$pm.stdout, '(?m)^package:(?<p>[^\r\n]+)\s*$') | ForEach-Object { $_.Groups['p'].Value.Trim() })
  if (-not $paths.Count) { throw 'FAIL: package is not installed (pm path empty).' }
  if ($paths.Count -ne 1) { throw "FAIL: monolithic artifact expects exactly one installed base APK, found $($paths.Count) paths (unexpected split APK)." }
  $base = $paths[0]
  $digest = Get-C05RDeviceFileDigest $base
  $abiResult = Invoke-C05RAdb @('shell','getprop','ro.product.cpu.abi') 30
  if ($abiResult.status -ne 'PASS' -or [string]::IsNullOrWhiteSpace([string]$abiResult.stdout)) { throw 'FAIL: device ABI probe failed.' }
  $abi = ([string]$abiResult.stdout).Trim()
  return [pscustomobject]@{ package=$ExpectedPackage; paths=@($paths); basePath=$base; baseDigest=$digest;
    abi=$abi; abiMatches=[bool]($abi -ceq 'x86_64'); pmPathRaw=[string]$pm.stdout }
}
function Test-C05RInstalledBinding([string]$LocalSha, [string]$DeviceSha, [string]$DeviceAbi) {
  if ([string]::IsNullOrWhiteSpace($LocalSha) -or [string]::IsNullOrWhiteSpace($DeviceSha)) { return $false }
  if ($LocalSha -notmatch '^[0-9a-fA-F]{64}$' -or $DeviceSha -notmatch '^[0-9a-fA-F]{64}$') { return $false }
  if ($DeviceAbi -cne 'x86_64') { return $false }
  return ($LocalSha -ieq $DeviceSha)
}
function Get-C05RTargetProcessState([string]$ExpectedPackage) {
  $command = "pidof $ExpectedPackage; echo C05_PIDOF_EXIT:`$?"
  $probe = Invoke-C05RAdb @('shell', $command) 30
  $script:C05RLastProcessProbe = [pscustomobject]@{
    command=$command; status=[string]$probe.status; exit=$probe.exit; timedOut=[bool]$probe.timedOut;
    drainComplete=$(if ($probe.PSObject.Properties['drainComplete']) { [bool]$probe.drainComplete } else { $true });
    stdout=[string]$probe.stdout; stderr=[string]$probe.stderr; observedUtc=(Get-Date).ToUniversalTime().ToString('o')
  }
  $script:C05RLastProcessProbeReason = 'UNKNOWN'
  if ([string]$probe.status -ne 'PASS') { $script:C05RLastProcessProbeReason = "PROBE_$($probe.status)"; return 'UNKNOWN' }
  if ($probe.PSObject.Properties['timedOut'] -and [bool]$probe.timedOut) { $script:C05RLastProcessProbeReason = 'TIMEOUT'; return 'UNKNOWN' }
  if ($probe.PSObject.Properties['drainComplete'] -and -not [bool]$probe.drainComplete) { $script:C05RLastProcessProbeReason = 'DRAIN_TIMEOUT'; return 'UNKNOWN' }
  if ($null -eq $probe.exit -or [int]$probe.exit -ne 0) { $script:C05RLastProcessProbeReason = 'NATIVE_NONZERO'; return 'UNKNOWN' }
  if (-not [string]::IsNullOrWhiteSpace([string]$probe.stderr)) { $script:C05RLastProcessProbeReason = 'STDERR'; return 'UNKNOWN' }
  $stdout = [string]$probe.stdout
  $markers = @([regex]::Matches($stdout, '(?m)^C05_PIDOF_EXIT:(?<code>[0-9]+)\s*$'))
  if ($markers.Count -ne 1) { $script:C05RLastProcessProbeReason = 'MARKER_COUNT'; return 'UNKNOWN' }
  $marker = $markers[0]
  $after = $stdout.Substring($marker.Index + $marker.Length)
  if (-not [string]::IsNullOrWhiteSpace($after)) { $script:C05RLastProcessProbeReason = 'TRAILING_TEXT'; return 'UNKNOWN' }
  $before = $stdout.Substring(0, $marker.Index)
  $pids = New-Object System.Collections.Generic.List[string]
  foreach ($line in @($before -split "`r?`n")) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    foreach ($token in @($line.Trim() -split '\s+')) {
      if ($token -notmatch '^\d+$') { $script:C05RLastProcessProbeReason = 'MALFORMED_PID'; return 'UNKNOWN' }
      $pids.Add($token)
    }
  }
  $code = [int]$marker.Groups['code'].Value
  if ($code -eq 0 -and $pids.Count -gt 0) { $script:C05RLastProcessProbeReason = 'RUNNING'; return 'RUNNING' }
  if ($code -eq 1 -and $pids.Count -eq 0) { $script:C05RLastProcessProbeReason = 'ABSENT'; return 'ABSENT' }
  $script:C05RLastProcessProbeReason = 'CONTRADICTORY_RECORD'
  return 'UNKNOWN'
}
function Get-C05RStateDirectory([object]$State, [string]$StateFile) {
  if ($State -and $State.PSObject.Properties['statePath'] -and -not [string]::IsNullOrWhiteSpace([string]$State.statePath)) {
    return Split-Path ([string]$State.statePath)
  }
  if (-not [string]::IsNullOrWhiteSpace($StateFile)) { return Split-Path ([IO.Path]::GetFullPath($StateFile)) }
  return $null
}
function Get-C05RInstanceId([object]$State) {
  if ($null -eq $State) { return $null }
  $qemuPid = if ($State.PSObject.Properties['qemuPid']) { [int]$State.qemuPid } else { 0 }
  $qemuStart = if ($State.PSObject.Properties['qemuStartUtcTicks']) { [long]$State.qemuStartUtcTicks } else { 0 }
  return ('{0}|{1}|{2}|{3}|{4}' -f [string]$State.serial, [int]$State.emulatorPid, [long]$State.startUtcTicks, $qemuPid, $qemuStart)
}
function Get-C05RInstallReceipt([object]$State, [string]$StateFile, [object]$Artifact) {
  $stateDir = Get-C05RStateDirectory $State $StateFile
  if (-not $stateDir) { throw 'BLOCKED: no state directory; Install must run against this exact owned instance first.' }
  $receiptPath = Join-Path $stateDir 'install-receipt.json'
  if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) { throw 'BLOCKED: install receipt is missing; Install must run first for this exact serial/instance.' }
  $receipt = Get-Content $receiptPath -Raw | ConvertFrom-Json
  if ([string]$receipt.status -ne 'PASS' -or [string]$receipt.serial -cne [string]$State.serial -or [string]$receipt.avd -cne [string]$State.avd -or
      [string]$receipt.packageId -cne [string]$Artifact.packageId -or [string]$receipt.localApkSha256 -ine [string]$Artifact.sha256) {
    throw 'FAIL: install receipt does not bind this serial/AVD/package/local APK SHA.'
  }
  $instanceId = Get-C05RInstanceId $State
  if (-not $receipt.PSObject.Properties['instanceId'] -or [string]::IsNullOrWhiteSpace([string]$receipt.instanceId)) {
    throw 'FAIL: install receipt must carry a nonblank instanceId from the current Install.'
  }
  if ([string]$receipt.instanceId -cne $instanceId) { throw 'FAIL: install receipt instanceId differs from the live owned emulator instance.' }
  if (-not $receipt.PSObject.Properties['monolithic'] -or $receipt.monolithic -isnot [bool] -or -not [bool]$receipt.monolithic) {
    throw 'FAIL: install receipt must declare monolithic=true for this single-APK artifact.'
  }
  $installed = Get-C05RInstalledIdentity $Artifact.packageId
  if (-not (Test-C05RInstalledBinding $Artifact.sha256 $installed.baseDigest $installed.abi)) {
    throw "FAIL: installed base APK digest/ABI does not match the selected artifact (digest=$($installed.baseDigest), abi=$($installed.abi))."
  }
  return [pscustomobject]@{ receipt=$receipt; installed=$installed; receiptPath=$receiptPath; instanceId=$instanceId }
}
function Test-C05RCaptureObservation([object]$Capture) {
  if ($null -eq $Capture -or $null -eq $Capture.screencap -or $null -eq $Capture.pidof -or $null -eq $Capture.logcat) { return $false }
  $screenOk = ($Capture.screencap.status -eq 'PASS' -and ($null -ne $Capture.screencap.exit) -and ($Capture.screencap.exit -eq 0) -and [int]$Capture.bytes -ge 256)
  $pidOk = ($Capture.pidof.status -eq 'PASS' -and ($null -ne $Capture.pidof.exit) -and ($Capture.pidof.exit -eq 0) -and -not [string]::IsNullOrWhiteSpace([string]$Capture.pidof.stdout))
  $logOk = ($Capture.logcat.status -eq 'PASS' -and $null -ne $Capture.logcat.exit -and $Capture.logcat.exit -eq 0 -and -not [string]::IsNullOrWhiteSpace([string]$Capture.logcat.stdout) -and $Capture.logcat.drainComplete -ne $false)
  return [bool]($screenOk -and $pidOk -and $logOk)
}
function Get-C05RPngSample([string]$Path) {
  try {
    Add-Type -AssemblyName System.Drawing
    $bitmap = [Drawing.Bitmap]::FromFile($Path)
    $nonBlack = $false
    for ($x=0; $x -lt $bitmap.Width -and -not $nonBlack; $x += [Math]::Max(1,[int]($bitmap.Width/40))) {
      for ($y=0; $y -lt $bitmap.Height; $y += [Math]::Max(1,[int]($bitmap.Height/40))) {
        $c = $bitmap.GetPixel($x,$y)
        if ($c.R -gt 8 -or $c.G -gt 8 -or $c.B -gt 8) { $nonBlack = $true; break }
      }
    }
    $result = [pscustomobject]@{ width=$bitmap.Width; height=$bitmap.Height; nonBlackSample=$nonBlack }
    $bitmap.Dispose(); return $result
  } catch { return [pscustomobject]@{ width=0; height=0; nonBlackSample=$null; error=$_.Exception.Message } }
}
function Save-C05RCapture([string]$Dir, [string]$Prefix) {
  $stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
  $png = Join-Path $Dir "$stamp-$Prefix.png"
  if (Test-Path -LiteralPath $png) {
    throw "FAIL: Destination capture file already exists and cannot be overwritten: $png"
  }
  $screen = Invoke-C05RAdbBinary @('exec-out','screencap','-p') 60
  $validPng = ($screen.status -eq 'PASS' -and $screen.exit -eq 0 -and $screen.bytes.Length -ge 256 -and
    $screen.bytes[0] -eq 0x89 -and $screen.bytes[1] -eq 0x50 -and $screen.bytes[2] -eq 0x4E -and $screen.bytes[3] -eq 0x47)
  if ($validPng) {
    [IO.File]::WriteAllBytes($png, [byte[]]$screen.bytes)
  }
  $sample = if (Test-Path -LiteralPath $png) { Get-C05RPngSample $png } else { $null }
  $pidResult = Invoke-C05RAdb @('shell','pidof',$PackageId)
  $log = $null
  if ($pidResult.status -eq 'PASS' -and $pidResult.exit -eq 0 -and -not [string]::IsNullOrWhiteSpace($pidResult.stdout)) {
    $log = Invoke-C05RAdb @('logcat','-d','-v','threadtime','--pid',([string]$pidResult.stdout).Trim()) 60
    $logOutPath = Join-Path $Dir "$stamp-$Prefix-logcat.stdout.txt"
    $logErrPath = Join-Path $Dir "$stamp-$Prefix-logcat.stderr.txt"
    $logReceiptPath = Join-Path $Dir "$stamp-$Prefix-logcat-receipt.json"
    foreach ($path in @($logOutPath,$logErrPath,$logReceiptPath)) { if (Test-Path -LiteralPath $path) { throw "FAIL: Destination logcat evidence already exists and cannot be overwritten: $path" } }
    [IO.File]::WriteAllText($logOutPath,[string]$log.stdout,(New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText($logErrPath,[string]$log.stderr,(New-Object Text.UTF8Encoding($false)))
    Save-C05RJson $logReceiptPath ([pscustomobject]@{status=$log.status;exit=$log.exit;timedOut=$log.timedOut;drainComplete=$log.drainComplete;cleanup=$log.cleanup;stdoutPath=$logOutPath;stderrPath=$logErrPath})
    if ($log.status -ne 'PASS' -or $null -eq $log.exit -or $log.exit -ne 0 -or [string]::IsNullOrWhiteSpace([string]$log.stdout) -or $log.drainComplete -eq $false) {
      $log.status = 'FAIL'
    }
  }
  $bytesLen = if (Test-Path -LiteralPath $png) { (Get-Item -LiteralPath $png).Length } else { 0 }
  $fileHash = if (Test-Path -LiteralPath $png) { (Get-FileHash -LiteralPath $png -Algorithm SHA256).Hash } else { '' }
  return [pscustomobject]@{ screenshot=(Split-Path $png -Leaf); bytes=$bytesLen;
    sha256=$fileHash; sample=$sample; screencap=$screen; pidof=$pidResult; logcat=$log }
}

# ----------------------------- deterministic fixtures -----------------------------
if ($ArtifactFixtureTest) {
  $fixtureRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
  $dir = Join-Path $fixtureRoot ('build\c05-emulator-artifact-fixtures\' + [guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  try {
    $apk = Join-Path $dir 'Mahjong.apk'; [IO.File]::WriteAllBytes($apk,[byte[]](1..64))
    $scenes = @('Assets/Scenes/Boot.unity','Assets/Scenes/Menu.unity','Assets/Scenes/Lobby.unity','Assets/Scenes/Table.unity','Assets/Scenes/Spectator.unity','Assets/Scenes/Settlement.unity')
    $validReport = [pscustomobject]@{ schema=2; cacheMode='CLEAN'; result='Succeeded'; errors=0; stepErrors=0; stepExceptions=0;
      stepErrorMessages=@(); warnings=0; target='Android'; backend='IL2CPP'; architectures='X86_64'; variant='ANDROID_EMULATOR';
      tmpResources=$true; minSdkVersion=25; targetSdkSetting=0; resolvedTargetSdk=36; sourceHead=('a'*40);
      landscapeOnly=$true; fullscreen=$true; outsideSafeArea=$true; output=''; packageId='com.majiang.game'; scenes=$scenes;
      failedAssertions=@(); productionInputsRewritten=@() }
    $metadata = "package: name='com.majiang.game' versionCode='1'`nminSdkVersion:'25'`ntargetSdkVersion:'36'`nnative-code: 'x86_64'`nlaunchable-activity: name='com.unity3d.player.UnityPlayerActivity'  label='' icon=''"
    function New-FixtureRun([string]$Name, [scriptblock]$MutateReport, [string]$Meta, [switch]$NoReport, [switch]$BreakReportHash, [switch]$BreakMetadataHash) {
      $p = Join-Path $dir $Name; New-Item -ItemType Directory -Force -Path $p | Out-Null
      $apkCopy = Join-Path $p 'Mahjong.apk'; [IO.File]::Copy($apk, $apkCopy)
      $r = $validReport | Select-Object *
      if ($MutateReport) { & $MutateReport $r }
      $r.output = [IO.Path]::GetFullPath($apkCopy)
      $reportPath = Join-Path $p 'build-report.json'; $metaPath = Join-Path $p 'apk-metadata.txt'
      if (-not $NoReport) { Save-C05RJson $reportPath $r }
      [IO.File]::WriteAllText($metaPath, $Meta)
      $reportSha = if (Test-Path $reportPath) { (Get-FileHash $reportPath -Algorithm SHA256).Hash } else { '0'*64 }
      $metaSha = (Get-FileHash $metaPath -Algorithm SHA256).Hash
      $summary = [pscustomobject]@{ status='PASS'; nativeExit=0; apkBytes=(Get-Item $apkCopy).Length; apkSha256=(Get-FileHash $apkCopy -Algorithm SHA256).Hash;
        sourceHead=('a'*40); buildCacheMode='CLEAN'; buildReportSha256=$reportSha; metadataSha256=$metaSha }
      if ($BreakReportHash) { $summary.buildReportSha256 = '0'*64 }
      if ($BreakMetadataHash) { $summary.metadataSha256 = '0'*64 }
      Save-C05RJson (Join-Path $p 'summary.json') $summary
      return $apkCopy
    }
    $accepted = Get-C05RVerifiedArtifact (New-FixtureRun 'valid' $null $metadata) 'com.majiang.game'
    if ($accepted.activity -ne 'com.unity3d.player.UnityPlayerActivity') { throw 'valid fixture artifact activity mismatch' }
    $rejected = 0
    $cases = @(
      @{ name='arm64'; expect='BuildReport|X86_64'; run={ Get-C05RVerifiedArtifact (New-FixtureRun 'a' { param($r) $r.architectures='ARM64' } $metadata) 'com.majiang.game' } },
      @{ name='variant-device'; expect='BuildReport|variant'; run={ Get-C05RVerifiedArtifact (New-FixtureRun 'b' { param($r) $r.variant='ANDROID_DEVICE' } $metadata) 'com.majiang.game' } },
      @{ name='backend-mono'; expect='BuildReport|IL2CPP'; run={ Get-C05RVerifiedArtifact (New-FixtureRun 'c' { param($r) $r.backend='Mono' } $metadata) 'com.majiang.game' } },
      @{ name='package-wrong'; expect='package'; run={ $m=$metadata.Replace("name='com.majiang.game'","name='com.other.game'"); Get-C05RVerifiedArtifact (New-FixtureRun 'd' $null $m) 'com.majiang.game' } },
      @{ name='abi-extra'; expect='x86_64'; run={ $m=$metadata.Replace("native-code: 'x86_64'","native-code: 'x86_64' 'arm64-v8a'"); Get-C05RVerifiedArtifact (New-FixtureRun 'e' $null $m) 'com.majiang.game' } },
      @{ name='activity-missing'; expect='activity'; run={ $m=$metadata -replace "(?m)^launchable-activity:.*\r?\n?",''; Get-C05RVerifiedArtifact (New-FixtureRun 'f' $null $m) 'com.majiang.game' } },
      @{ name='sha-tamper'; expect='summary|APK'; run={ $p=New-FixtureRun 'g' $null $metadata; [IO.File]::AppendAllText($p,'x'); Get-C05RVerifiedArtifact $p 'com.majiang.game' } },
      @{ name='missing-report'; expect='evidence|report'; run={ $p=New-FixtureRun 'h' $null $metadata -NoReport; Get-C05RVerifiedArtifact $p 'com.majiang.game' } },
      @{ name='step-errors'; expect='stepErrors|BuildReport'; run={ Get-C05RVerifiedArtifact (New-FixtureRun 'i' { param($r) $r.stepErrors=1 } $metadata) 'com.majiang.game' } },
      @{ name='step-exceptions'; expect='stepExceptions|BuildReport'; run={ Get-C05RVerifiedArtifact (New-FixtureRun 'j' { param($r) $r.stepExceptions=1 } $metadata) 'com.majiang.game' } },
      @{ name='failed-assertions'; expect='failedAssertions|BuildReport'; run={ Get-C05RVerifiedArtifact (New-FixtureRun 'k' { param($r) $r.failedAssertions=@('CONFIRMED_FAILURE') } $metadata) 'com.majiang.game' } },
      @{ name='rewritten-inputs'; expect='rewritten|BuildReport'; run={ Get-C05RVerifiedArtifact (New-FixtureRun 'l' { param($r) $r.productionInputsRewritten=@('x') } $metadata) 'com.majiang.game' } },
      @{ name='report-hash-mismatch'; expect='SHA256'; run={ Get-C05RVerifiedArtifact (New-FixtureRun 'm' $null $metadata -BreakReportHash) 'com.majiang.game' } },
      @{ name='metadata-hash-mismatch'; expect='SHA256'; run={ Get-C05RVerifiedArtifact (New-FixtureRun 'n' $null $metadata -BreakMetadataHash) 'com.majiang.game' } },
      @{ name='target-mismatch'; expect='targetSdk'; run={ $m=$metadata.Replace("targetSdkVersion:'36'","targetSdkVersion:'35'"); Get-C05RVerifiedArtifact (New-FixtureRun 'o' $null $m) 'com.majiang.game' } },
      @{ name='resolved-mismatch'; expect='targetSdk'; run={ Get-C05RVerifiedArtifact (New-FixtureRun 'p' { param($r) $r.resolvedTargetSdk=35 } $metadata) 'com.majiang.game' } },
      @{ name='fractional-errors'; expect='fractional'; run={ Get-C05RVerifiedArtifact (New-FixtureRun 'q' { param($r) $r.errors=0.4 } $metadata) 'com.majiang.game' } },
      @{ name='fractional-step-errors'; expect='fractional'; run={ Get-C05RVerifiedArtifact (New-FixtureRun 'r' { param($r) $r.stepErrors=0.4 } $metadata) 'com.majiang.game' } },
      @{ name='fractional-step-exceptions'; expect='fractional'; run={ Get-C05RVerifiedArtifact (New-FixtureRun 's' { param($r) $r.stepExceptions=0.4 } $metadata) 'com.majiang.game' } },
      @{ name='fractional-schema'; expect='fractional'; run={ Get-C05RVerifiedArtifact (New-FixtureRun 't' { param($r) $r.schema=2.4 } $metadata) 'com.majiang.game' } },
      @{ name='bool-string-tmp'; expect='boolean'; run={ Get-C05RVerifiedArtifact (New-FixtureRun 'u' { param($r) $r.tmpResources='false' } $metadata) 'com.majiang.game' } },
      @{ name='bool-string-landscape'; expect='boolean'; run={ Get-C05RVerifiedArtifact (New-FixtureRun 'v' { param($r) $r.landscapeOnly='false' } $metadata) 'com.majiang.game' } },
      @{ name='bool-string-fullscreen'; expect='boolean'; run={ Get-C05RVerifiedArtifact (New-FixtureRun 'w' { param($r) $r.fullscreen='false' } $metadata) 'com.majiang.game' } },
      @{ name='bool-string-safearea'; expect='boolean'; run={ Get-C05RVerifiedArtifact (New-FixtureRun 'x' { param($r) $r.outsideSafeArea='false' } $metadata) 'com.majiang.game' } }
    )
    $names = New-Object System.Collections.Generic.List[string]
    foreach ($case in $cases) {
      try { & $case.run | Out-Null; throw 'case was accepted' }
      catch {
        if ($_.Exception.Message -eq 'case was accepted') { throw "negative artifact case accepted: $($case.name)" }
        if ($_.Exception.Message -notmatch $case.expect) { throw "artifact case '$($case.name)' rejected for the wrong reason: $($_.Exception.Message)" }
        $rejected++; $names.Add($case.name)
      }
    }
    if ($rejected -ne 24 -or $names.Count -ne 24) { throw "artifact negative matrix rejected=$rejected expected=24" }
    Write-Output 'ARTIFACT_FIXTURE PASS valid-x86_64-emulator-accepted arm64-variant-backend-package-extra-abi-activity-sha-missing-report-step-errors-step-exceptions-failed-assertions-rewritten-report-hash-metadata-hash-target-resolved-fractional-boolean-string-rejected count=24'
    exit 0
  } finally {
    $full = [IO.Path]::GetFullPath($dir); $base = [IO.Path]::GetFullPath((Join-Path $fixtureRoot 'build')) + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($base,[StringComparison]::OrdinalIgnoreCase)) { throw 'unsafe artifact fixture cleanup' }
    [IO.Directory]::Delete($full,$true)
  }
}
if ($StateFixtureTest) {
  $valid = [pscustomobject]@{ serial='emulator-5580'; avd='medium_phone'; emulatorPid=1234; startUtcTicks=638000000000000000; port=5580 }
  if (-not (Test-C05RStateIdentity $valid 'emulator-5580' 'medium_phone')) { throw 'valid state rejected' }
  $rejected = 0
  foreach ($bad in @(
      @{ serial='emulator-5582'; avd='medium_phone'; pid=1234; ticks=638000000000000000; port=5582 },
      @{ serial='emulator-5580'; avd='other_avd'; pid=1234; ticks=638000000000000000; port=5580 },
      @{ serial='emulator-5580'; avd='medium_phone'; pid=0; ticks=638000000000000000; port=5580 },
      @{ serial='emulator-5580'; avd='medium_phone'; pid=1234; ticks=0; port=5580 },
      @{ serial='emulator-5580'; avd='medium_phone'; pid=1234; ticks=638000000000000000; port=5000 })) {
    $state = [pscustomobject]@{ serial=$bad.serial; avd=$bad.avd; emulatorPid=$bad.pid; startUtcTicks=$bad.ticks; port=$bad.port }
    try { Test-C05RStateIdentity $state 'emulator-5580' 'medium_phone' | Out-Null } catch { $rejected++ }
  }
  $child = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-Command','Start-Sleep -Seconds 60') -WindowStyle Hidden -PassThru
  try {
    $ticks = $child.StartTime.ToUniversalTime().Ticks
    if (Stop-C05ROwnedProcess $child.Id ($ticks+1)) { throw 'stale start-time identity was not preserved' }
    if (-not (Get-Process -Id $child.Id -ErrorAction SilentlyContinue)) { throw 'foreign process was terminated' }
    if (-not (Stop-C05ROwnedProcess $child.Id $ticks)) { throw 'exact owned process was not stopped' }
  } finally { if (Get-Process -Id $child.Id -ErrorAction SilentlyContinue) { [void](Stop-C05ROwnedProcess $child.Id $child.StartTime.ToUniversalTime().Ticks) } }
  if ($rejected -ne 5) { throw "state negative matrix rejected=$rejected expected=5" }
  Write-Output 'STATE_FIXTURE PASS serial-avd-pid-start-port-identity-rejected stale-start-preserved exact-owned-stopped count=5'
  exit 0
}
if ($InputFixtureTest) {
  $rejected = 0
  if (-not (Assert-C05RInputBounds 'tap' 100 100 -1 -1 300 1080 2400)) { throw 'valid tap rejected' }
  if (-not (Assert-C05RInputBounds 'swipe' 100 100 500 500 300 1080 2400)) { throw 'valid swipe rejected' }
  if (-not (Assert-C05RInputBounds 'back' -1 -1 -1 -1 300 1080 2400)) { throw 'valid back rejected' }
  foreach ($bad in @(
      @{ k='tap'; x=-1; y=10; x2=-1; y2=-1; d=300; w=1080; h=2400 },
      @{ k='tap'; x=1080; y=10; x2=-1; y2=-1; d=300; w=1080; h=2400 },
      @{ k='swipe'; x=10; y=10; x2=2000; y2=10; d=300; w=1080; h=2400 },
      @{ k='tap'; x=10; y=10; x2=-1; y2=-1; d=10; w=1080; h=2400 },
      @{ k='tap'; x=10; y=10; x2=-1; y2=-1; d=9000; w=1080; h=2400 },
      @{ k='tap'; x=10; y=10; x2=-1; y2=-1; d=300; w=0; h=0 },
      @{ k='pinch'; x=10; y=10; x2=-1; y2=-1; d=300; w=1080; h=2400 })) {
    try { Assert-C05RInputBounds $bad.k $bad.x $bad.y $bad.x2 $bad.y2 $bad.d $bad.w $bad.h | Out-Null } catch { $rejected++ }
  }
  if ($rejected -ne 7) { throw "input negative matrix rejected=$rejected expected=7" }
  Write-Output 'INPUT_FIXTURE PASS tap-swipe-back-valid negative-coordinate-duration-display-action-rejected count=7'
  exit 0
}
if ($TreeFixtureTest) {
  $dir = Join-Path $root ('build\c05-emulator-tree-fixtures\' + [guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  $parent = $null; $grandId = 0
  try {
    $marker = Join-Path $dir 'grandchild.pid'
    $spawn = Join-Path $dir 'spawn.ps1'
    $spawnText = "`$c = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-Command','Start-Sleep -Seconds 120') -WindowStyle Hidden -PassThru`r`n" +
      "Set-Content -LiteralPath '$($marker.Replace("'","''"))' -Value `$c.Id`r`nStart-Sleep -Seconds 120`r`n"
    [IO.File]::WriteAllText($spawn, $spawnText, (New-Object Text.UTF8Encoding($false)))
    $parent = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',$spawn) -WindowStyle Hidden -PassThru
    $deadline = (Get-Date).AddSeconds(30)
    while (-not (Test-Path -LiteralPath $marker) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
    if (-not (Test-Path -LiteralPath $marker)) { throw 'tree fixture grandchild was not started' }
    $grandId = [int]((Get-Content -LiteralPath $marker -Raw).Trim())
    if (-not (Get-Process -Id $grandId -ErrorAction SilentlyContinue)) { throw 'tree fixture grandchild is not alive' }
    $ticks = $parent.StartTime.ToUniversalTime().Ticks
    if (-not (Stop-C05ROwnedProcess $parent.Id $ticks)) { throw 'tree fixture owned parent stop failed' }
    Start-Sleep -Seconds 2
    if (Get-Process -Id $parent.Id -ErrorAction SilentlyContinue) { throw 'tree fixture parent survived' }
    if (Get-Process -Id $grandId -ErrorAction SilentlyContinue) { throw 'tree fixture grandchild survived the owned-tree stop' }
    Write-Output 'TREE_FIXTURE PASS parent-and-descendant-tree-stopped count=0'
    exit 0
  } finally {
    foreach ($left in @($grandId, $(if ($parent) { $parent.Id } else { 0 }))) {
      if ($left -gt 0) { $p = Get-Process -Id $left -ErrorAction SilentlyContinue; if ($p) { try { $p.Kill() } catch { } } }
    }
    if (Test-Path -LiteralPath $dir) {
      $full = [IO.Path]::GetFullPath($dir); $base = [IO.Path]::GetFullPath((Join-Path $root 'build')) + [IO.Path]::DirectorySeparatorChar
      if (-not $full.StartsWith($base,[StringComparison]::OrdinalIgnoreCase)) { throw 'unsafe tree fixture cleanup' }
      [IO.Directory]::Delete($full,$true)
    }
  }
}
if ($EnvFixtureTest) {
  $dir = Join-Path $root ('build\c05-emulator-env-fixtures\' + [guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  try {
    $record = Join-Path $dir 'env.txt'; $tempDir = Join-Path $dir 'child-temp'
    $probe = Join-Path $dir 'probe.ps1'
    $probeText = "@('ANDROID_HOME','ANDROID_SDK_ROOT','ANDROID_AVD_HOME','TEMP','TMP') | ForEach-Object { `$n=`$_; `$v=[Environment]::GetEnvironmentVariable(`$n,'Process'); `"`$n=`$v`" } | Set-Content -LiteralPath '$($record.Replace("'","''"))'`r`n"
    [IO.File]::WriteAllText($probe, $probeText, (New-Object Text.UTF8Encoding($false)))
    $beforeTemp = [Environment]::GetEnvironmentVariable('TEMP','Process')
    $saved = Push-C05RChildEnvironment $tempDir
    try {
      $child = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',$probe) -WindowStyle Hidden -PassThru
      if (-not $child.WaitForExit(30000)) { throw 'env fixture child timed out' }
    } finally { Pop-C05RChildEnvironment $saved }
    $values = @{}
    foreach ($line in @(Get-Content -LiteralPath $record)) { $i = $line.IndexOf('='); if ($i -gt 0) { $values[$line.Substring(0,$i)] = $line.Substring($i+1) } }
    foreach ($name in @('ANDROID_HOME','ANDROID_SDK_ROOT')) { if ($values[$name] -ne $AndroidSdkRoot) { throw "env fixture $name='$($values[$name])' expected '$AndroidSdkRoot'" } }
    if ($values['ANDROID_AVD_HOME'] -ne $AvdHome) { throw "env fixture ANDROID_AVD_HOME='$($values['ANDROID_AVD_HOME'])' expected '$AvdHome'" }
    if ($values['TEMP'] -ne $tempDir -or $values['TMP'] -ne $tempDir) { throw 'env fixture child TEMP/TMP not bound to the run-local directory' }
    if ([Environment]::GetEnvironmentVariable('TEMP','Process') -ne $beforeTemp) { throw 'env fixture parent TEMP was not restored' }
    Write-Output 'ENV_FIXTURE PASS android-home-sdk-avd-and-run-local-temp-inherited count=0'
    exit 0
  } finally {
    if (Test-Path -LiteralPath $dir) {
      $full = [IO.Path]::GetFullPath($dir); $base = [IO.Path]::GetFullPath((Join-Path $root 'build')) + [IO.Path]::DirectorySeparatorChar
      if (-not $full.StartsWith($base,[StringComparison]::OrdinalIgnoreCase)) { throw 'unsafe env fixture cleanup' }
      [IO.Directory]::Delete($full,$true)
    }
  }
}
if ($DeviceContractFixtureTest) {
  $rejected = 0
  $fgOther = Get-C05RForegroundState 'mResumedActivity: com.other.app/.Other; historicalActivity: com.majiang.game/.OldActivity' 'mCurrentFocus=com.other.app/.Other; historicalWindow=com.majiang.game' 'com.majiang.game'
  if ($fgOther.resumedMatches -or $fgOther.focusedMatches) { throw 'foreground fixture accepted another app with target only in history' }
  $rejected++
  $fgTarget = Get-C05RForegroundState 'mResumedActivity: com.majiang.game/com.unity3d.player.UnityPlayerActivity' 'mCurrentFocus=com.majiang.game/com.unity3d.player.UnityPlayerActivity' 'com.majiang.game'
  if (-not $fgTarget.resumedMatches -or -not $fgTarget.focusedMatches) { throw 'foreground fixture rejected the exact target component' }
  $override = Get-C05RDisplayMetricsFromText "Physical size: 1080x2400`nOverride size: 720x1280" 0
  if ($override.width -ne 720 -or $override.height -ne 1280 -or $override.source -ne 'Override') { throw 'display fixture did not prefer Override 720x1280' }
  $rotated = Get-C05RDisplayMetricsFromText "Physical size: 1080x2400`nOverride size: 720x1280" 1
  if ($rotated.width -ne 1280 -or $rotated.height -ne 720) { throw 'display fixture did not apply rotated logical dimensions' }
  $stateBad = [pscustomobject]@{ serial='emulator-5580'; avd='medium_phone'; emulatorPid=1234; startUtcTicks=1; port=5582 }
  $badAccepted = $false
  try { Test-C05RStateIdentity $stateBad 'emulator-5580' 'medium_phone' | Out-Null; $badAccepted = $true } catch { }
  if ($badAccepted) { throw 'state-port fixture accepted mismatched serial/port' }
  $rejected++
  $stateGood = [pscustomobject]@{ serial='emulator-5580'; avd='medium_phone'; emulatorPid=1234; startUtcTicks=1; port=5580 }
  if (-not (Test-C05RStateIdentity $stateGood 'emulator-5580' 'medium_phone')) { throw 'state fixture rejected valid serial/port' }
  if (Test-C05RInstalledBinding ('a'*64) ('b'*64) 'x86_64') { throw 'install fixture accepted a digest mismatch' }
  if (Test-C05RInstalledBinding ('a'*64) ('a'*64) 'arm64-v8a') { throw 'install fixture accepted a non-x86_64 device ABI' }
  if (-not (Test-C05RInstalledBinding ('a'*64) ('A'*64) 'x86_64')) { throw 'install fixture rejected a case-insensitive digest match' }
  $badCapture = [pscustomobject]@{ screencap=[pscustomobject]@{status='FAIL';exit=7}; bytes=0; pidof=[pscustomobject]@{status='FAIL';exit=1;stdout=''} }
  if (Test-C05RCaptureObservation $badCapture) { throw 'capture-observation fixture accepted failed screencap/PID' }
  $missingExitCapture = [pscustomobject]@{ screencap=[pscustomobject]@{status='PASS'}; bytes=2048; pidof=[pscustomobject]@{status='PASS';stdout='1234'} }
  if (Test-C05RCaptureObservation $missingExitCapture) { throw 'capture-observation fixture accepted missing native exit' }
  $goodCapture = [pscustomobject]@{ screencap=[pscustomobject]@{status='PASS';exit=0}; bytes=2048; pidof=[pscustomobject]@{status='PASS';exit=0;stdout='1234'} }
  $goodCapture | Add-Member -NotePropertyName logcat -NotePropertyValue ([pscustomobject]@{status='PASS';exit=0;stdout='fixture log';drainComplete=$true})
  if (-not (Test-C05RCaptureObservation $goodCapture)) { throw 'capture-observation fixture rejected a valid observation' }
  foreach ($badLog in @([pscustomobject]@{status='FAIL';exit=7;stdout='';drainComplete=$true},[pscustomobject]@{status='PASS';exit=0;stdout='';drainComplete=$true})) {
    $bad = $goodCapture | Select-Object *; $bad.logcat = $badLog
    if (Test-C05RCaptureObservation $bad) { throw 'capture-observation fixture accepted failed or empty native logcat' }
    $rejected++
  }
  if ($rejected -ne 4) { throw "device-contract negative matrix rejected=$rejected expected=4" }
  Write-Output 'DEVICE_CONTRACT_FIXTURE PASS valid-nonempty-logcat native7-empty-logcat-rejected capture-required-observations count=4'
  exit 0
}

if ($ReceiptFixtureTest) {
  $dir = Join-Path $root ('build\c05-emulator-receipt-fixtures\' + [guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  try {
    $statePath = Join-Path $dir 'state.json'
    $state = [pscustomobject]@{ serial='emulator-5580'; avd='medium_phone'; emulatorPid=1234; startUtcTicks=100; qemuPid=0; qemuStartUtcTicks=0; statePath=$statePath }
    $artifact = [pscustomobject]@{ packageId='com.majiang.game'; sha256=('a'*64) }
    function Get-C05RInstalledIdentity { param($ExpectedPackage) [pscustomobject]@{ baseDigest=('a'*64); abi='x86_64'; paths=@('/data/app/base.apk') } }
    $rejected = 0
    foreach ($kind in @('missing','blank','mismatch','non-monolithic')) {
      $receipt = [pscustomobject]@{ status='PASS'; serial=$state.serial; avd=$state.avd; packageId=$artifact.packageId; localApkSha256=$artifact.sha256; monolithic=$true }
      if ($kind -ne 'missing') {
        $v = switch ($kind) { 'blank' { '' } 'mismatch' { 'different-instance' } 'non-monolithic' { Get-C05RInstanceId $state } }
        $receipt | Add-Member -NotePropertyName instanceId -NotePropertyValue $v
      }
      if ($kind -eq 'non-monolithic') { $receipt.monolithic = $false }
      $receipt | ConvertTo-Json | Set-Content (Join-Path $dir 'install-receipt.json') -Encoding UTF8
      $accepted = $false
      try { Get-C05RInstallReceipt $state $statePath $artifact | Out-Null; $accepted = $true } catch { }
      if ($accepted) { throw "receipt fixture accepted $kind" }
      $rejected++
    }
    $valid = [pscustomobject]@{ status='PASS'; serial=$state.serial; avd=$state.avd; packageId=$artifact.packageId; localApkSha256=$artifact.sha256; monolithic=$true; instanceId=(Get-C05RInstanceId $state) }
    $valid | ConvertTo-Json | Set-Content (Join-Path $dir 'install-receipt.json') -Encoding UTF8
    if (-not (Get-C05RInstallReceipt $state $statePath $artifact)) { throw 'valid receipt rejected' }
    if ($rejected -ne 4) { throw "receipt negative matrix rejected=$rejected expected=4" }
    Write-Output 'RECEIPT_FIXTURE PASS missing-blank-mismatch-nonmonolithic-rejected valid-accepted count=4'
    exit 0
  } finally {
    if (Test-Path -LiteralPath $dir) { [IO.Directory]::Delete($dir, $true) }
  }
}
if ($TargetStateFixtureTest) {
  $cases = [ordered]@{
    absent = [pscustomobject]@{ probe=[pscustomobject]@{status='PASS';exit=0;timedOut=$false;drainComplete=$true;stdout="C05_PIDOF_EXIT:1`n";stderr=''}; expected='ABSENT' }
    running = [pscustomobject]@{ probe=[pscustomobject]@{status='PASS';exit=0;timedOut=$false;drainComplete=$true;stdout="1234`nC05_PIDOF_EXIT:0`n";stderr=''}; expected='RUNNING' }
    'multi-pid' = [pscustomobject]@{ probe=[pscustomobject]@{status='PASS';exit=0;timedOut=$false;drainComplete=$true;stdout="1234 5678`nC05_PIDOF_EXIT:0`n";stderr=''}; expected='RUNNING' }
    timeout = [pscustomobject]@{ probe=[pscustomobject]@{status='TIMEOUT';exit=$null;timedOut=$true;drainComplete=$false;stdout='';stderr=''}; expected='UNKNOWN' }
    drain = [pscustomobject]@{ probe=[pscustomobject]@{status='PASS';exit=0;timedOut=$false;drainComplete=$false;stdout="C05_PIDOF_EXIT:1`n";stderr=''}; expected='UNKNOWN' }
    'native2' = [pscustomobject]@{ probe=[pscustomobject]@{status='FAIL';exit=2;timedOut=$false;drainComplete=$true;stdout='';stderr=''}; expected='UNKNOWN' }
    stderr = [pscustomobject]@{ probe=[pscustomobject]@{status='PASS';exit=0;timedOut=$false;drainComplete=$true;stdout="C05_PIDOF_EXIT:1`n";stderr='pidof: permission denied'}; expected='UNKNOWN' }
    duplicate = [pscustomobject]@{ probe=[pscustomobject]@{status='PASS';exit=0;timedOut=$false;drainComplete=$true;stdout="C05_PIDOF_EXIT:1`nC05_PIDOF_EXIT:1`n";stderr=''}; expected='UNKNOWN' }
    malformed = [pscustomobject]@{ probe=[pscustomobject]@{status='PASS';exit=0;timedOut=$false;drainComplete=$true;stdout="garbage`nC05_PIDOF_EXIT:1`n";stderr=''}; expected='UNKNOWN' }
    contradiction = [pscustomobject]@{ probe=[pscustomobject]@{status='PASS';exit=0;timedOut=$false;drainComplete=$true;stdout="1234`nC05_PIDOF_EXIT:1`n";stderr=''}; expected='UNKNOWN' }
    'code0-no-pid' = [pscustomobject]@{ probe=[pscustomobject]@{status='PASS';exit=0;timedOut=$false;drainComplete=$true;stdout="C05_PIDOF_EXIT:0`n";stderr=''}; expected='UNKNOWN' }
    'no-marker' = [pscustomobject]@{ probe=[pscustomobject]@{status='PASS';exit=0;timedOut=$false;drainComplete=$true;stdout="1234`n";stderr=''}; expected='UNKNOWN' }
  }
  $rejected = 0
  foreach ($name in $cases.Keys) {
    $script:TargetProbeCase = $cases[$name]
    function Invoke-C05RAdb { param([string[]]$AdbArgs,[int]$TimeoutSeconds) return $script:TargetProbeCase.probe }
    $actual = Get-C05RTargetProcessState 'com.majiang.game'
    if ($actual -ne $script:TargetProbeCase.expected) { throw "target-state fixture '$name' expected $($script:TargetProbeCase.expected) got $actual" }
    if ($script:TargetProbeCase.expected -eq 'UNKNOWN') { $rejected++ }
    if ($null -eq $script:C05RLastProcessProbe -or [string]::IsNullOrWhiteSpace([string]$script:C05RLastProcessProbe.command)) { throw "target-state fixture '$name' did not preserve the raw probe" }
  }
  if ($rejected -ne 9) { throw "target-state negative matrix rejected=$rejected expected=9" }
  Write-Output 'TARGET_STATE_FIXTURE PASS absent-running-multipid-positive timeout-drain-native2-stderr-duplicate-malformed-contradiction-code0nopid-nomarker-unknown count=9'
  exit 0
}
if ($RootEligibilityFixtureTest) {
  $owned = $null; $foreign = $null
  try {
    $owned = Start-Process powershell.exe -ArgumentList @('-NoProfile','-Command','Start-Sleep -Seconds 90') -WindowStyle Hidden -PassThru
    $ownedTicks = $owned.StartTime.ToUniversalTime().Ticks
    $ownedRecord = [pscustomobject]@{ pid=$owned.Id; startUtcTicks=$ownedTicks; name='qemu-system-x86_64-headless'; parentPid=0; parentStartUtcTicks=0 }
    $survivors = @(Stop-C05RVerifiedRecordSet @($ownedRecord) 2147483000 $owned.Id)
    if ($survivors.Count -ne 0 -or (Get-Process -Id $owned.Id -ErrorAction SilentlyContinue)) { throw 'parent0 qemu handoff record was not stopped' }
    $foreign = Start-Process powershell.exe -ArgumentList @('-NoProfile','-Command','Start-Sleep -Seconds 90') -WindowStyle Hidden -PassThru
    $foreignTicks = $foreign.StartTime.ToUniversalTime().Ticks
    $foreignRecord = [pscustomobject]@{ pid=$foreign.Id; startUtcTicks=$foreignTicks; name='powershell'; parentPid=0; parentStartUtcTicks=0 }
    $survivors = @(Stop-C05RVerifiedRecordSet @($foreignRecord) 2147483000 $foreign.Id)
    if ($survivors.Count -ne 1 -or -not (Get-Process -Id $foreign.Id -ErrorAction SilentlyContinue)) { throw 'foreign record with non-qemu name was not preserved' }
    Write-Output 'ROOT_ELIGIBILITY_FIXTURE PASS parent0-qemu-stopped foreign-name-preserved count=1'
    exit 0
  } finally {
    foreach ($pair in @(@($owned, $ownedTicks), @($foreign, $foreignTicks))) {
      if ($pair[0]) { $p=Get-Process -Id $pair[0].Id -ErrorAction SilentlyContinue; if ($p -and $p.StartTime.ToUniversalTime().Ticks -eq $pair[1]) { $p.Kill(); [void]$p.WaitForExit(5000) } }
    }
  }
}

# Artifact validation is a file-only preflight and must not start or query adb.
if ($Action -eq 'ValidateArtifact') {
  $dir = Get-C05REvidenceDir 'validate-artifact'
  $verified = Get-C05RVerifiedArtifact $Apk $PackageId
  $artifactReceipt = [pscustomobject]@{ status='PASS'; action='ValidateArtifact'; artifact=$verified; validatedUtc=(Get-Date).ToUniversalTime().ToString('o') }
  Save-C05RJson (Join-Path $dir 'artifact-validation.json') $artifactReceipt
  Write-Host "ARTIFACT_VALIDATION PASS sha256=$($verified.sha256) sourceHead=$($verified.sourceHead)"
  exit 0
}

# ----------------------------- real device/emulator actions -----------------------------
if (-not (Test-Path -LiteralPath $adb -PathType Leaf)) { throw "BLOCKED: adb missing at $adb" }

if ($Action -eq 'Probe') {
  $dir = Get-C05REvidenceDir 'probe'
  if (-not (Test-Path -LiteralPath $emulator -PathType Leaf)) { throw "BLOCKED: emulator missing at $emulator" }
  $probe = [ordered]@{}
  $probe.timestampUtc = (Get-Date).ToUniversalTime().ToString('o')
  $probe.androidSdkRoot = $AndroidSdkRoot; $probe.androidAvdHome = $AvdHome
  $probe.envInherited = [pscustomobject]@{ ANDROID_HOME=$env:ANDROID_HOME; ANDROID_SDK_ROOT=$env:ANDROID_SDK_ROOT; ANDROID_AVD_HOME=$env:ANDROID_AVD_HOME }
  $probe.adbVersion = Invoke-C05RToFiles $adb @('version') 60 (Join-Path $dir 'adb-version.out') (Join-Path $dir 'adb-version.err')
  $probe.adbDevices = Invoke-C05RToFiles $adb @('devices','-l') 60 (Join-Path $dir 'adb-devices.out') (Join-Path $dir 'adb-devices.err')
  $probe.adbDevices | Add-Member -NotePropertyName status -NotePropertyValue $(if ($probe.adbDevices.exit -eq 0) { 'PASS' } else { 'FAIL' }) -Force
  Write-Host 'PROBE_STEP adb-complete'
  $probe.emulatorVersion = Invoke-C05RToFiles $emulator @('-version') 60 (Join-Path $dir 'emulator-version.out') (Join-Path $dir 'emulator-version.err')
  $probe.accelCheck = Invoke-C05RToFiles $emulator @('-accel-check') 120 (Join-Path $dir 'accel-check.out') (Join-Path $dir 'accel-check.err')
  $accelCodeMatch = [regex]::Match([string]$probe.accelCheck.stdout, '(?m)^(\d+)\s*$')
  $probe.accelCheck | Add-Member -NotePropertyName reportedExit -NotePropertyValue $(if ($accelCodeMatch.Success) { [int]$accelCodeMatch.Groups[1].Value } else { $null }) -Force
  $probe.avdList = Invoke-C05RToFiles $emulator @('-list-avds') 60 (Join-Path $dir 'avd-list.out') (Join-Path $dir 'avd-list.err')
  Write-Host 'PROBE_STEP emulator-complete'
  $probe.avdConfig = if (Test-Path (Join-Path $AvdHome "$AvdName.avd\config.ini")) { [string](Get-Content (Join-Path $AvdHome "$AvdName.avd\config.ini") -Raw) } else { $null }
  Write-Host 'PROBE_STEP avd-config-complete'
  $probe.systemImages = @(Get-ChildItem (Join-Path $AndroidSdkRoot 'system-images') -Recurse -File -Filter 'source.properties' -ErrorAction SilentlyContinue | Select-Object -First 200 | ForEach-Object { [pscustomobject]@{ path=$_.FullName; text=([string](Get-Content $_.FullName -Raw)).Substring(0,[Math]::Min(4096,([string](Get-Content $_.FullName -Raw)).Length)) } })
  Write-Host 'PROBE_STEP system-images-complete'
  $probe.diskFree = try {
    [pscustomobject]@{ E=([long]([IO.DriveInfo]::new('E')).AvailableFreeSpace); D=([long]([IO.DriveInfo]::new('D')).AvailableFreeSpace); source='DriveInfo' }
  } catch {
    [pscustomobject]@{ E=$null; D=$null; source='DriveInfo'; error=$_.Exception.Message }
  }
  Write-Host 'PROBE_STEP disk-complete'
  # Serialize section by section: PowerShell 5.1 ConvertTo-Json can stall on the
  # combined ordered dictionary; each section here is a small plain object.
  $fields = [ordered]@{
    timestampUtc=$probe.timestampUtc; androidSdkRoot=$probe.androidSdkRoot; androidAvdHome=$probe.androidAvdHome;
    envInherited=$probe.envInherited; adbVersion=$probe.adbVersion; adbDevices=$probe.adbDevices;
    emulatorVersion=$probe.emulatorVersion; accelCheck=$probe.accelCheck; avdList=$probe.avdList;
    avdConfig=$probe.avdConfig; systemImages=$probe.systemImages; diskFree=$probe.diskFree
  }
  $chunks = New-Object System.Collections.Generic.List[string]
  foreach ($key in $fields.Keys) {
    $valueJson = ConvertTo-Json -InputObject $fields[$key] -Depth 5 -Compress
    $chunks.Add(('"' + $key + '":' + $valueJson))
  }
  $probeJson = '{' + ($chunks -join ',') + '}'
  if ([Text.Encoding]::UTF8.GetByteCount($probeJson) -gt 4MB) { throw 'BLOCKED: probe JSON exceeded the 4 MiB bounded-output cap; no oversized evidence written.' }
  [IO.File]::WriteAllText((Join-Path $dir 'probe.json'), $probeJson, (New-Object Text.UTF8Encoding($false)))
  Write-Host 'PROBE_STEP saved'
  Write-Host "PROBE SAVED $dir"; Write-Host "ACCEL_EXIT=$($probe.accelCheck.reportedExit)"; Write-Host "AVDS=$(([string]$probe.avdList.stdout).Trim())"
  [Environment]::Exit(0)
}

$dir = Get-C05REvidenceDir $Action.ToLowerInvariant()
$devices = Invoke-C05RToFiles $adb @('devices','-l') 60 (Join-Path $dir 'adb-devices.out') (Join-Path $dir 'adb-devices.err')
$devices | Add-Member -NotePropertyName status -NotePropertyValue $(if ($devices.exit -eq 0) { 'PASS' } else { 'FAIL' }) -Force
Save-C05RJson (Join-Path $dir 'device-list.json') $devices
if ($devices.status -ne 'PASS') { throw "FAIL: adb devices exit=$($devices.exit) status=$($devices.status); raw result saved." }
$entries = @(([string]$devices.stdout) -split "`r?`n" | ForEach-Object { if ($_ -match '^([^ ]+)\s+(device|unauthorized|offline)\b') { [pscustomobject]@{ serial=$Matches[1]; state=$Matches[2] } } })
if ($Action -eq 'Devices') {
  [string]$devices.stdout
  if (@($entries | Where-Object state -eq 'device').Count) { Write-Host "DEVICE_STATUS=AVAILABLE count=$(@($entries | Where-Object state -eq 'device').Count)" }
  elseif (@($entries | Where-Object state -eq 'offline').Count) { Write-Host 'DEVICE_STATUS=BLOCKED_OFFLINE' }
  elseif (@($entries | Where-Object state -eq 'unauthorized').Count) { Write-Host 'DEVICE_STATUS=BLOCKED_UNAUTHORIZED' }
  else { Write-Host 'DEVICE_STATUS=NOT_RUN_NO_DEVICE' }
  exit 0
}

function Stop-C05RStartupTree([int]$LauncherPid, [long]$LauncherStart, [int]$QemuPid, [long]$QemuStart) {
  $launcherAlive = $false; $qemuAlive = $false
  $lp = Get-Process -Id $LauncherPid -ErrorAction SilentlyContinue
  if ($lp -and $lp.StartTime.ToUniversalTime().Ticks -eq $LauncherStart) { $launcherAlive = $true }
  if ($QemuPid -gt 0) {
    $qp = Get-Process -Id $QemuPid -ErrorAction SilentlyContinue
    if ($qp -and $qp.StartTime.ToUniversalTime().Ticks -eq $QemuStart) { $qemuAlive = $true }
  }
  $ok = $true
  if ($launcherAlive) { $ok = (Stop-C05ROwnedProcess $LauncherPid $LauncherStart) -and $ok }
  if ($qemuAlive) {
    $qstill = Get-Process -Id $QemuPid -ErrorAction SilentlyContinue
    if ($qstill -and $qstill.StartTime.ToUniversalTime().Ticks -eq $QemuStart) { $ok = (Stop-C05ROwnedProcess $QemuPid $QemuStart) -and $ok }
  }
  return $ok
}
if ($Action -eq 'StartEmulator') {
  $derivedSerial = "emulator-$EmulatorPort"
  if ($EmulatorPort -lt 5554 -or $EmulatorPort -gt 5682 -or ($EmulatorPort % 2) -ne 0) { throw "BLOCKED: emulator port $EmulatorPort is outside the even 5554..5682 range." }
  if ($Serial -cne $derivedSerial) { throw "BLOCKED: serial '$Serial' does not correspond to emulator port $EmulatorPort ('$derivedSerial')." }
  $forbiddenExtra = @('-avd','-port','-read-only','-no-snapshot-save','-no-snapshot-load','-accel','-wipe-data','-writable-system','-no-metrics','-no-audio','-no-boot-anim')
  foreach ($extra in @($ExtraEmulatorArgs)) {
    if ($forbiddenExtra -contains ([string]$extra).ToLowerInvariant()) { throw "BLOCKED: ExtraEmulatorArgs may not override identity/isolation flag '$extra'." }
  }
  if ([string]::IsNullOrWhiteSpace($AvdName)) { throw 'BLOCKED: -AvdName is required.' }
  if ($entries.serial -contains $Serial) { throw "BLOCKED: serial '$Serial' already exists; refusing to take over an emulator this runner did not start." }
  $avdList = Invoke-C05RToFiles $emulator @('-list-avds') 60 (Join-Path $dir 'avd-list.out') (Join-Path $dir 'avd-list.err')
  $avds = @((([string]$avdList.stdout) -split "`r?`n") | Where-Object { $_.Trim() })
  if ($avds -notcontains $AvdName) { throw "BLOCKED: AVD '$AvdName' is not installed under $AvdHome." }
  $accel = Invoke-C05RToFiles $emulator @('-accel-check') 120 (Join-Path $dir 'accel-check.out') (Join-Path $dir 'accel-check.err')
  $accelMatch = [regex]::Match([string]$accel.stdout, '(?m)^(\d+)\s*$')
  $accelExit = if ($accelMatch.Success) { [int]$accelMatch.Groups[1].Value } else { $accel.exit }
  $accelText = (([string]$accel.stdout) + ([string]$accel.stderr)).Trim()
  # `auto` is the only safe default: accelerate whenever the host proves it can
  # (`-accel-check` exit 0), and refuse the round when it cannot.
  #
  # There is deliberately no silent fall back to `-accel off`. An unaccelerated
  # x86_64 guest does not boot here: QEMU dies with EXCEPTION_ACCESS_VIOLATION_READ
  # at 0x4 before sys.boot_completed, so "try again without acceleration" could
  # only ever produce a crash report that reads like a missing-capability verdict
  # even when AEHD is installed and usable. `off` stays reachable only as an
  # explicit diagnostic choice, never as an automatic retry.
  $effectiveAccel = if ($accelExit -eq 0) { 'on' } else { 'off' }
  if ($AccelMode -ne 'auto') { $effectiveAccel = $AccelMode }
  if ($effectiveAccel -eq 'off' -and $accelExit -eq 0) {
    Write-Warning ("ACCEL_AVAILABLE_BUT_DISABLED: -accel-check exit 0 (" +
      (($accelText -split "`r?`n" | Where-Object { $_ -match 'installed|usable|WHPX|HAXM' }) -join ' ') +
      ') but -AccelMode off was requested; software acceleration cannot boot this x86_64 image.')
  }
  $emuArgs = @('-avd',$AvdName,'-port',[string]$EmulatorPort,'-no-snapshot-load','-read-only','-no-snapshot-save','-no-audio',
    '-no-boot-anim','-no-window','-gpu','swiftshader_indirect','-accel',$effectiveAccel,'-no-metrics')
  # Software acceleration (TCG) is a diagnostic mode only. Keep guest Vulkan off
  # there because the gfxstream/Vulkan path crashes QEMU in a real minidump.
  # While GLES-on-SwiftShader was intended as a bootable fallback, an
  # unaccelerated x86_64 guest still dies with ACCESS_VIOLATION_READ at 0x4
  # before sys.boot_completed, so this path can report a crash but never a verdict.
  $vulkanDisabled = $false
  if ($effectiveAccel -eq 'off') { $emuArgs += @('-feature','-Vulkan'); $vulkanDisabled = $true }
  $emuArgs += @($ExtraEmulatorArgs)
  $emuLog = Join-Path $dir 'emulator.log'
  $emuOutLog = Join-Path $dir 'emulator.stdout.log'; $emuErrLog = Join-Path $dir 'emulator.stderr.log'
  $emuTemp = Join-Path $dir 'emulator-temp'
  $line = (@($emuArgs | ForEach-Object { ConvertTo-C05RWindowsArgument ([string]$_) }) -join ' ')
  $savedEmuEnv = Push-C05RChildEnvironment $emuTemp
  try {
    $proc = Start-Process -FilePath $emulator -ArgumentList $line -RedirectStandardOutput $emuOutLog -RedirectStandardError $emuErrLog -PassThru -WindowStyle Hidden
  } finally { Pop-C05RChildEnvironment $savedEmuEnv }
  $ownedPid = $proc.Id; $ownedStart = $proc.StartTime.ToUniversalTime().Ticks
  $bootSucceeded = $false
  try {
  # emulator.exe is a launcher: it can exit after handing off to qemu-system-*. Track the
  # real guest process so an early launcher exit is not misread as an emulator crash and
  # so a crashed guest is reported with its own minidump instead of a phantom timeout.
  $qemuPid = 0; $qemuStart = 0; $qemuDeadline = (Get-Date).AddSeconds(30)
  while ((Get-Date) -lt $qemuDeadline) {
    foreach ($childId in @(Get-C05RDescendantProcessIds $ownedPid)) {
      $candidate = Get-Process -Id $childId -ErrorAction SilentlyContinue
      if ($candidate -and $candidate.ProcessName -match '^qemu-system') {
        $qemuPid = $candidate.Id; $qemuStart = $candidate.StartTime.ToUniversalTime().Ticks; break
      }
    }
    if ($qemuPid -gt 0) { break }
    if ($proc.HasExited) { break }
    Start-Sleep -Milliseconds 250
  }
  $statePath = Join-Path $dir 'state.json'
  $state = [pscustomobject]@{ serial=$Serial; avd=$AvdName; emulatorPid=$ownedPid; startUtcTicks=$ownedStart; port=$EmulatorPort;
    qemuPid=$qemuPid; qemuStartUtcTicks=$qemuStart; vulkanDisabled=$vulkanDisabled;
    accelCheckExit=$accelExit; accelCheckText=$accelText; accelMode=$AccelMode; effectiveAccel=$effectiveAccel;
    emulatorArgs=$emuArgs; startedUtc=(Get-Date).ToUniversalTime().ToString('o'); evidenceDir=$dir }
  Save-C05RJson $statePath $state
  $booted = $false; $bootDeadline = (Get-Date).AddSeconds($AvdWaitSeconds); $lastBeat = Get-Date; $bootRuns = New-Object System.Collections.Generic.List[object]
  while ((Get-Date) -lt $bootDeadline) {
    $launcherAlive = -not $proc.HasExited
    $qemuAlive = $false
    if ($qemuPid -gt 0) {
      $qproc = Get-Process -Id $qemuPid -ErrorAction SilentlyContinue
      $qemuAlive = [bool]($qproc -and $qproc.StartTime.ToUniversalTime().Ticks -eq $qemuStart)
    }
    if (-not $launcherAlive -and -not $qemuAlive) { break }
    $boot = Invoke-C05RAdb @('shell','getprop','sys.boot_completed') 15
    $bootRuns.Add([pscustomobject]@{ utc=(Get-Date).ToUniversalTime().ToString('o'); status=$boot.status; stdout=$boot.stdout; stderr=$boot.stderr })
    if ($boot.status -eq 'PASS' -and ([string]$boot.stdout).Trim() -eq '1') { $booted = $true; break }
    if (((Get-Date) - $lastBeat).TotalSeconds -ge 30) {
      Write-Host ("HEARTBEAT {0:o} phase=emulator-boot elapsed={1}s launcherPid={2} qemuPid={3} launcherAlive={4} qemuAlive={5}" -f (Get-Date), [int](((Get-Date)-$bootDeadline).TotalSeconds + $AvdWaitSeconds), $ownedPid, $qemuPid, $launcherAlive, $qemuAlive)
      $lastBeat = Get-Date
    }
    Start-Sleep -Seconds 5
  }
  $exited = $proc.HasExited; $native = if ($exited) { $proc.ExitCode } else { $null }
  if (-not $booted) {
    $killed = $false
    if (-not $proc.HasExited) { $killed = Stop-C05ROwnedProcess $ownedPid $ownedStart }
    elseif ($qemuPid -gt 0) { $killed = Stop-C05ROwnedProcess $qemuPid $qemuStart }
    [void]$proc.WaitForExit(5000)
    $crashes = @(Get-ChildItem (Join-Path $dir 'emulator-temp') -Recurse -File -Filter '*.dmp' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)
    $crashFile = $null
    if ($crashes.Count -gt 0) {
      $crashFile = Join-Path $dir 'crash-report.txt'
      $crashreport = Join-Path $AndroidSdkRoot 'emulator\crashreport.exe'
      if (Test-Path -LiteralPath $crashreport) {
        $crashText = @(& $crashreport -d $crashes[0].FullName -m 2>&1)
        [IO.File]::WriteAllText($crashFile, ($crashText -join "`n"), (New-Object Text.UTF8Encoding($false)))
      }
    }
    $state | Add-Member -NotePropertyName bootCompleted -NotePropertyValue $false -Force
    $state | Add-Member -NotePropertyName killedOwnedProcess -NotePropertyValue $killed -Force
    $state | Add-Member -NotePropertyName launcherExited -NotePropertyValue $proc.HasExited -Force
    $state | Add-Member -NotePropertyName bootAttempts -NotePropertyValue $bootRuns.ToArray() -Force
    $state | Add-Member -NotePropertyName stdoutLog -NotePropertyValue (Split-Path $emuOutLog -Leaf) -Force
    $state | Add-Member -NotePropertyName stderrLog -NotePropertyValue (Split-Path $emuErrLog -Leaf) -Force
    if ($crashFile) { $state | Add-Member -NotePropertyName crashReport -NotePropertyValue (Split-Path $crashFile -Leaf) -Force }
    Save-C05RJson $statePath $state
    [IO.File]::WriteAllText($emuLog, "[boot failed; stdout/stderr captured in emulator.stdout.log / emulator.stderr.log]`n", (New-Object Text.UTF8Encoding($false)))
    $reason = if ($crashFile) { "emulator guest crashed before boot (minidump decoded into crash-report.txt)" }
      elseif ($proc.HasExited -and -not ($qemuPid -gt 0)) { "emulator launcher exited before a guest process appeared" }
      else { "emulator did not reach sys.boot_completed=1 within $AvdWaitSeconds s" }
    # Name the actual cause. A crash under SOFTWARE acceleration is a broken
    # diagnostic mode, not proof the host cannot accelerate: reporting it as an
    # acceleration verdict made a healthy AEHD host look BLOCKED and sent rounds
    # into a futile retry loop.
    $cause = if ($effectiveAccel -eq 'off' -and $accelExit -eq 0) {
      "SOFTWARE_ACCEL_CRASH: -AccelMode off was used even though -accel-check reported exit 0 " +
        "(hardware acceleration is available); an unaccelerated x86_64 guest cannot boot. " +
        'Re-run with -AccelMode auto.'
    } elseif ($effectiveAccel -eq 'off') {
      "NO_ACCELERATION: -accel-check reported exit $accelExit, so the guest ran unaccelerated and crashed."
    } else {
      $reason
    }
    throw "BLOCKED_ACCEL: $cause (accelExit=$accelExit effective=$effectiveAccel); owned launcher/qemu PID+descendant stop attempted=$killed. Logs saved."
  }
  $abi = Invoke-C05RAdb @('shell','getprop','ro.product.cpu.abi')
  if ($abi.status -ne 'PASS' -or ([string]$abi.stdout).Trim() -notmatch 'x86_64') { [void](Stop-C05ROwnedProcess $ownedPid $ownedStart); throw "BLOCKED: emulator booted but ABI is '$(([string]$abi.stdout).Trim())', not x86_64." }
  $state | Add-Member -NotePropertyName bootCompleted -NotePropertyValue $true -Force
  $state | Add-Member -NotePropertyName deviceAbi -NotePropertyValue (([string]$abi.stdout).Trim()) -Force
  $state | Add-Member -NotePropertyName bootAttempts -NotePropertyValue $bootRuns.ToArray() -Force
  $state | Add-Member -NotePropertyName stdoutLog -NotePropertyValue (Split-Path $emuOutLog -Leaf) -Force
  $state | Add-Member -NotePropertyName stderrLog -NotePropertyValue (Split-Path $emuErrLog -Leaf) -Force
  $ownedRecords = New-Object System.Collections.Generic.List[object]
  $launcherRecord = New-C05RProcessRecord $ownedPid 0
  if ($launcherRecord) { $ownedRecords.Add($launcherRecord) }
  if ($qemuPid -gt 0) {
    $parentForQemu = if (-not $proc.HasExited) { $ownedPid } else { 0 }
    $qemuRecord = New-C05RProcessRecord $qemuPid $parentForQemu
    if ($qemuRecord) { $ownedRecords.Add($qemuRecord) }
  }
  $currentRoot = if (-not $proc.HasExited) { $ownedPid } else { $qemuPid }
  if ($currentRoot -gt 0) {
    foreach ($childId in @(Get-C05RDescendantProcessIds $currentRoot)) {
      $record = New-C05RProcessRecord $childId (Get-C05RProcessParentId $childId)
      if ($record) { $ownedRecords.Add($record) }
    }
  }
  $uniqueRecords = @($ownedRecords | Sort-Object pid,startUtcTicks -Unique)
  $state | Add-Member -NotePropertyName ownedProcesses -NotePropertyValue @($uniqueRecords) -Force
  $state | Add-Member -NotePropertyName descendantPids -NotePropertyValue @($uniqueRecords | ForEach-Object { [int]$_.pid }) -Force
  Save-C05RJson $statePath $state
  [IO.File]::WriteAllText($emuLog, "[running at boot; stdout/stderr stream to emulator.stdout.log / emulator.stderr.log until shutdown]`n", (New-Object Text.UTF8Encoding($false)))
  Write-Host "EMULATOR_BOOT PASS serial=$Serial avd=$AvdName abi=$(([string]$abi.stdout).Trim()) state=$statePath"
  $bootSucceeded = $true
  exit 0
  } finally {
    if (-not $bootSucceeded) {
      # Common bounded finalization for state-write, ABI-probe, adb and cancellation failures.
      [void](Stop-C05RStartupTree $ownedPid $ownedStart ([int]$qemuPid) ([long]$qemuStart))
    }
  }
}

# All device actions require an owned state and (except shutdown) a verified APK.
$state = Get-C05RState $StateFile $Serial $AvdName
$artifact = $null
if ($Action -in @('Install','Start','Stop','Capture','Input','BackgroundResume')) { $artifact = Get-C05RVerifiedArtifact $Apk $PackageId }
$identity = if ($artifact) { [pscustomobject]@{ apk=$artifact.apkName; apkSha256=$artifact.sha256; apkBytes=$artifact.bytes;
  buildHead=$artifact.sourceHead; packageId=$artifact.packageId; activity=$artifact.activity; variant='ANDROID_EMULATOR'; abi='x86_64';
  buildSummarySha256=$artifact.buildSummarySha256; buildReportSha256=$artifact.buildReportSha256; runtimeArtifactLinkage='VERIFIED_BUILD_EVIDENCE' } } else { $null }

switch ($Action) {
  'DeviceInfo' {
    $obs = [ordered]@{}
    $obs.serial = $Serial
    $obs.avd = (([string](Invoke-C05RAdb @('emu','avd','name')).stdout) -split "`r?`n")[0].Trim()
    $obs.abi = Invoke-C05RAdb @('shell','getprop','ro.product.cpu.abi')
    $obs.sdk = Invoke-C05RAdb @('shell','getprop','ro.build.version.sdk')
    $obs.release = Invoke-C05RAdb @('shell','getprop','ro.build.version.release')
    $obs.model = Invoke-C05RAdb @('shell','getprop','ro.product.model')
    $obs.size = Invoke-C05RAdb @('shell','wm','size')
    $obs.density = Invoke-C05RAdb @('shell','wm','density')
    $obs.rotation = Invoke-C05RAdb @('shell','settings','get','system','user_rotation')
    $obs.hardwareEgl = Invoke-C05RAdb @('shell','getprop','ro.hardware.egl')
    $obs.openglesVersion = Invoke-C05RAdb @('shell','getprop','ro.opengles.version')
    $obs.kernelQemu = Invoke-C05RAdb @('shell','getprop','ro.kernel.qemu')
    $obs.display = Invoke-C05RAdb @('shell','dumpsys','display')
    $obs.surfaceFlinger = Invoke-C05RAdb @('shell','dumpsys','SurfaceFlinger')
    $obs.state = $state
    $failed = @($obs.Values | Where-Object { $_ -is [pscustomobject] -and $_.PSObject.Properties['status'] -and $_.status -ne 'PASS' })
    Save-C05RJson (Join-Path $dir 'device-info.json') $obs
    if ($failed.Count) { throw "FAIL: $($failed.Count) device-info adb command(s) failed; raw results saved." }
    Write-Host "DEVICE_INFO PASS serial=$Serial abi=$(([string]$obs.abi.stdout).Trim()) sdk=$(([string]$obs.sdk.stdout).Trim()) size=$(([string]$obs.size.stdout).Trim())"; exit 0
  }
  'Install' {
    $r = Invoke-C05RAdb @('install','-r','-t',$artifact.apkPath) 180
    $installed = $null; $probeError = $null
    if ($r.status -eq 'PASS') {
      try { $installed = Get-C05RInstalledIdentity $PackageId } catch { $probeError = $_.Exception.Message }
    }
    $pkg = if ($r.status -eq 'PASS') { Invoke-C05RAdb @('shell','dumpsys','package',$PackageId) } else { $null }
    $bindingOk = [bool]($installed -and (Test-C05RInstalledBinding $artifact.sha256 $installed.baseDigest $installed.abi))
    $receipt = [pscustomobject]@{
      status=$(if ($r.status -eq 'PASS' -and $bindingOk -and -not $probeError) { 'PASS' } else { 'FAIL' });
      serial=$Serial; avd=$state.avd; packageId=$PackageId; localApkSha256=$artifact.sha256;
      instanceId=(Get-C05RInstanceId $state); monolithic=$true;
      deviceBaseApkPath=$(if ($installed) { $installed.basePath } else { $null });
      deviceBaseApkSha256=$(if ($installed) { $installed.baseDigest } else { $null });
      deviceAbi=$(if ($installed) { $installed.abi } else { $null });
      pmPaths=$(if ($installed) { @($installed.paths) } else { @() });
      probeError=$probeError;
      installedUtc=(Get-Date).ToUniversalTime().ToString('o')
    }
    Save-C05RJson (Join-Path $dir 'install-receipt.json') $receipt
    $stateDir = Get-C05RStateDirectory $state $StateFile
    if ($stateDir) { Save-C05RJson (Join-Path $stateDir 'install-receipt.json') $receipt }
    $result = [pscustomobject]@{ status=$receipt.status; serial=$Serial; artifact=$identity; adb=$r; installed=$installed; receipt=$receipt; packageDump=$pkg }
    Save-C05RJson (Join-Path $dir 'install-result.json') $result
    if ($receipt.status -ne 'PASS') {
      throw "Install or installed base-APK byte/ABI binding failed (local=$($artifact.sha256), device=$($receipt.deviceBaseApkSha256), abi=$($receipt.deviceAbi), probe=$probeError)."
    }
    Write-Host "INSTALL PASS artifact=$($artifact.sha256) deviceBase=$($receipt.deviceBaseApkSha256) package=$PackageId"; exit 0
  }
  'Start' {
    $receiptInfo = Get-C05RInstallReceipt $state $StateFile $artifact
    $component = "$PackageId/$($artifact.activity)"
    $launch = Invoke-C05RAdb @('shell','am','start','-W','-n',$component) 120
    $pidResult = Invoke-C05RAdb @('shell','pidof',$PackageId)
    $resumed = Invoke-C05RAdb @('shell','dumpsys','activity','activities')
    $focused = Invoke-C05RAdb @('shell','dumpsys','window','displays')
    $fg = Get-C05RForegroundState ([string]$resumed.stdout) ([string]$focused.stdout) $PackageId
    $status = if ($launch.status -ne 'PASS' -or $pidResult.status -ne 'PASS' -or [string]::IsNullOrWhiteSpace($pidResult.stdout) -or
      $resumed.status -ne 'PASS' -or $focused.status -ne 'PASS' -or -not $fg.resumedMatches -or -not $fg.focusedMatches) { 'FAIL' } else { 'PASS' }
    $result = [pscustomobject]@{ status=$status; serial=$Serial; artifact=$identity; component=$component; launch=$launch; pidof=$pidResult;
      foreground=$fg; resumed=$fg.resumedMatches; focused=$fg.focusedMatches; activityDump=$resumed; windowDump=$focused; receipt=$receiptInfo.receipt }
    Save-C05RJson (Join-Path $dir 'launch-result.json') $result
    if ($status -ne 'PASS') { throw 'Launch, target pidof, exact resumed package or exact foreground window check failed; native results saved.' }
    Write-Host "START PASS package=$PackageId activity=$($artifact.activity) pid=$(([string]$pidResult.stdout).Trim())"; exit 0
  }
  'Stop' {
    [void](Get-C05RInstallReceipt $state $StateFile $artifact)
    $r = Invoke-C05RAdb @('shell','am','force-stop',$PackageId) 60
    $processState = if ($r.status -eq 'PASS') { Get-C05RTargetProcessState $PackageId } else { 'UNKNOWN' }
    $status = if ($r.status -ne 'PASS') { 'FAIL' } elseif ($processState -eq 'ABSENT') { 'PASS' } elseif ($processState -eq 'RUNNING') { 'FAIL' } else { 'UNKNOWN' }
    $result = [pscustomobject]@{ status=$status; serial=$Serial; artifact=$identity; adb=$r; processState=$processState;
      processStateReason=$script:C05RLastProcessProbeReason; processProbe=$script:C05RLastProcessProbe }
    Save-C05RJson (Join-Path $dir 'stop-result.json') $result
    if ($status -ne 'PASS') { throw "Stop did not prove target termination (status=$status, processState=$processState); offline/timeout is UNKNOWN, not PASS." }
    Write-Host 'STOP PASS'; exit 0
  }
  'Capture' {
    [void](Get-C05RInstallReceipt $state $StateFile $artifact)
    $resumed = Invoke-C05RAdb @('shell','dumpsys','activity','activities')
    $focused = Invoke-C05RAdb @('shell','dumpsys','window','displays')
    $fg = Get-C05RForegroundState ([string]$resumed.stdout) ([string]$focused.stdout) $PackageId
    $preflightPid = Invoke-C05RAdb @('shell','pidof',$PackageId)
    $capture = Save-C05RCapture $dir 'screen'
    $requiredFail = $capture.screencap.status -ne 'PASS' -or $capture.screencap.exit -ne 0 -or $capture.bytes -lt 256 -or $capture.pidof.status -ne 'PASS' -or
      $capture.pidof.exit -ne 0 -or [string]::IsNullOrWhiteSpace([string]$capture.pidof.stdout) -or $preflightPid.status -ne 'PASS' -or
      $preflightPid.exit -ne 0 -or [string]::IsNullOrWhiteSpace([string]$preflightPid.stdout) -or -not $fg.resumedMatches -or -not $fg.focusedMatches -or
      $capture.logcat.status -ne 'PASS' -or $capture.logcat.exit -ne 0 -or $capture.logcat.drainComplete -eq $false -or [string]::IsNullOrWhiteSpace([string]$capture.logcat.stdout)
    $status = if ($requiredFail) { 'FAIL' } elseif ($capture.sample.nonBlackSample -ne $true) { 'BLACK_OR_DECODE_FAILED_REQUIRES_INVESTIGATION' } else { 'CAPTURED_REQUIRES_REVIEW' }
    $result = [pscustomobject]@{ status=$status; serial=$Serial; artifact=$identity; foreground=$fg; preflightPid=$preflightPid;
      screenshot=$capture.screenshot; bytes=$capture.bytes; sha256=$capture.sha256; sample=$capture.sample; screenshotIsGamePass=$false;
      screencap=$capture.screencap; pidof=$capture.pidof; logcat=$capture.logcat; logcatStatus=$(if ($capture.logcat) { $capture.logcat.status } else { 'NO_PID' }) }
    Save-C05RJson (Join-Path $dir 'capture-summary.json') $result
    if ($requiredFail) { throw 'Capture required observation(s) failed (foreground/pid/screencap); raw exits/stdout/stderr saved.' }
    Write-Host "CAPTURED status=$status bytes=$($capture.bytes) SHA256=$($capture.sha256); visual semantics require independent reviewer."; exit 0
  }
  'Input' {
    [void](Get-C05RInstallReceipt $state $StateFile $artifact)
    $resumed = Invoke-C05RAdb @('shell','dumpsys','activity','activities')
    $focused = Invoke-C05RAdb @('shell','dumpsys','window','displays')
    $fg = Get-C05RForegroundState ([string]$resumed.stdout) ([string]$focused.stdout) $PackageId
    $pidBefore = Invoke-C05RAdb @('shell','pidof',$PackageId)
    if (-not $fg.resumedMatches -or -not $fg.focusedMatches -or $pidBefore.status -ne 'PASS' -or [string]::IsNullOrWhiteSpace([string]$pidBefore.stdout)) {
      throw 'FAIL: target package is not the exact current foreground/resumed app with a live PID; refusing native input.'
    }
    $size = Get-C05RDisplaySize $Serial
    [void](Assert-C05RInputBounds $InputAction $X $Y $X2 $Y2 $DurationMs $size.width $size.height)
    $before = Save-C05RCapture $dir 'before'
    $beforeFail = -not (Test-C05RCaptureObservation $before)
    if ($beforeFail) { throw 'FAIL: pre-input required screenshot/PID observation failed; native input not sent.' }
    if ($InputAction -eq 'tap') { $inputArgs = @('shell','input','tap',[string]$X,[string]$Y) }
    elseif ($InputAction -eq 'swipe') { $inputArgs = @('shell','input','swipe',[string]$X,[string]$Y,[string]$X2,[string]$Y2,[string]$DurationMs) }
    else { $inputArgs = @('shell','input','keyevent','KEYCODE_BACK') }
    $started = (Get-Date).ToUniversalTime().ToString('o')
    $r = Invoke-C05RAdb $inputArgs 60
    Start-Sleep -Milliseconds 800
    $after = Save-C05RCapture $dir 'after'
    $afterFail = -not (Test-C05RCaptureObservation $after)
    $changed = $before.sha256 -ne $after.sha256
    $status = if ($r.status -ne 'PASS' -or $afterFail) { 'FAIL' } elseif ($changed) { 'EXECUTED_REQUIRES_REVIEW' } else { 'NO_VISIBLE_CHANGE_REQUIRES_REVIEW' }
    $result = [pscustomobject]@{ status=$status; serial=$Serial; artifact=$identity; inputAction=$InputAction; foreground=$fg;
      coordinates=[pscustomobject]@{ x=$X; y=$Y; x2=$X2; y2=$Y2; durationMs=$DurationMs; displayWidth=$size.width; displayHeight=$size.height };
      command=($inputArgs -join ' '); startedUtc=$started; adb=$r; before=$before; after=$after; screenshotBytesChanged=$changed;
      visibleChangeRequiresReview=$true }
    Save-C05RJson (Join-Path $dir 'input-result.json') $result
    if ($status -eq 'FAIL') { throw 'Input command or required post-input screenshot/PID observation failed; raw results saved.' }
    Write-Host "INPUT $status action=$InputAction screenshotBytesChanged=$changed; visible effect requires independent reviewer."; exit 0
  }
  'BackgroundResume' {
    [void](Get-C05RInstallReceipt $state $StateFile $artifact)
    $resumedBefore = Invoke-C05RAdb @('shell','dumpsys','activity','activities')
    $focusedBefore = Invoke-C05RAdb @('shell','dumpsys','window','displays')
    $fgBefore = Get-C05RForegroundState ([string]$resumedBefore.stdout) ([string]$focusedBefore.stdout) $PackageId
    if (-not $fgBefore.resumedMatches -or -not $fgBefore.focusedMatches) { throw 'FAIL: target package is not the exact current foreground app before background/resume.' }
    $before = Save-C05RCapture $dir 'foreground'
    $homeKey = Invoke-C05RAdb @('shell','input','keyevent','KEYCODE_HOME') 60
    Start-Sleep -Seconds 2
    $background = Save-C05RCapture $dir 'background'
    $component = "$PackageId/$($artifact.activity)"
    $resume = Invoke-C05RAdb @('shell','am','start','-W','-n',$component) 120
    Start-Sleep -Seconds 2
    $after = Save-C05RCapture $dir 'resumed'
    $pidResult = Invoke-C05RAdb @('shell','pidof',$PackageId)
    $resumedAfter = Invoke-C05RAdb @('shell','dumpsys','activity','activities')
    $focusedAfter = Invoke-C05RAdb @('shell','dumpsys','window','displays')
    $fgAfter = Get-C05RForegroundState ([string]$resumedAfter.stdout) ([string]$focusedAfter.stdout) $PackageId
    $beforeOk = Test-C05RCaptureObservation $before
    $backgroundOk = Test-C05RCaptureObservation $background
    $afterOk = Test-C05RCaptureObservation $after
    $requiredObservations = ($beforeOk -and $backgroundOk -and $afterOk)
    $status = if ($homeKey.status -eq 'PASS' -and $resume.status -eq 'PASS' -and $pidResult.status -eq 'PASS' -and
      -not [string]::IsNullOrWhiteSpace([string]$pidResult.stdout) -and $fgAfter.resumedMatches -and $fgAfter.focusedMatches -and
      $requiredObservations) { 'EXECUTED_REQUIRES_REVIEW' } else { 'FAIL' }
    $result = [pscustomobject]@{ status=$status; serial=$Serial; artifact=$identity; homeKey=$homeKey; resume=$resume; pidof=$pidResult;
      foreground=$before; background=$background; resumed=$after; foregroundBefore=$fgBefore; foregroundAfter=$fgAfter;
      requiredObservations=$requiredObservations; beforeObservation=$beforeOk; backgroundObservation=$backgroundOk; afterObservation=$afterOk;
      visibleChangeRequiresReview=$true }
    Save-C05RJson (Join-Path $dir 'background-resume.json') $result
    if ($status -eq 'FAIL') { throw 'Background/resume sequence or required before/background/after observations failed; raw results saved.' }
    Write-Host 'BACKGROUND_RESUME EXECUTED_REQUIRES_REVIEW; visual states require independent reviewer.'; exit 0
  }
  'ShutdownEmulator' {
    $ownedProperty = $state.PSObject.Properties['ownedProcesses']
    $records = if ($ownedProperty) { @($ownedProperty.Value) } else { @() }
    $legacyProperty = $state.PSObject.Properties['descendantPids']
    $legacyPids = if ($legacyProperty) { @($legacyProperty.Value) } else { @() }
    $liveOwned = @($records | Where-Object {
      $p = Get-Process -Id ([int]$_.pid) -ErrorAction SilentlyContinue
      $p -and $p.StartTime.ToUniversalTime().Ticks -eq [long]$_.startUtcTicks })
    if (-not $liveOwned.Count) {
      $failure = [pscustomobject]@{ status='FAIL'; serial=$Serial; state=$state; error='no live verified owned launcher/qemu process; refusing to touch unverified descendantPids';
        legacyDescendantPids=@($legacyPids) }
      Save-C05RJson (Join-Path $dir 'shutdown-result.json') $failure
      throw 'BLOCKED: no live verified owned emulator identity; refusing to terminate unverified descendantPids.'
    }
    $r = Invoke-C05RAdb @('emu','kill') 30
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
      $still = @($records | Where-Object {
        $p = Get-Process -Id ([int]$_.pid) -ErrorAction SilentlyContinue
        $p -and $p.StartTime.ToUniversalTime().Ticks -eq [long]$_.startUtcTicks })
      if (-not $still.Count) { break }
      Start-Sleep -Seconds 2
    }
    $survivors = @($records | Where-Object {
      $p = Get-Process -Id ([int]$_.pid) -ErrorAction SilentlyContinue
      $p -and $p.StartTime.ToUniversalTime().Ticks -eq [long]$_.startUtcTicks })
    $forced = $false
    if ($survivors.Count) {
      $forced = $true
      $survivors = @(Stop-C05RVerifiedRecordSet $records ([int]$state.emulatorPid) ([int]$state.qemuPid))
    }
    $exitState = if ($survivors.Count) { 'INCOMPLETE' } elseif ($r.status -ne 'PASS') { 'UNKNOWN' } elseif ($forced) { 'FORCED' } else { 'GRACEFUL' }
    $status = if ($exitState -eq 'GRACEFUL') { 'PASS' } elseif ($exitState -eq 'FORCED' -and $survivors.Count -eq 0) { 'PASS_FORCED' } else { 'FAIL' }
    $result = [pscustomobject]@{ status=$status; exitState=$exitState;
      serial=$Serial; state=$state; kill=$r; processExited=($survivors.Count -eq 0); forcedOwnedTreeStop=$forced;
      ownedProcesses=@($records); survivingOwnedProcesses=@($survivors) }
    Save-C05RJson (Join-Path $dir 'shutdown-result.json') $result
    if ($status -eq 'FAIL') { throw "Emulator shutdown did not complete cleanly (exitState=$exitState); survivors/state preserved." }
    Write-Host "SHUTDOWN $status exitState=$exitState"; exit 0
  }
}
