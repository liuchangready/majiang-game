<#
.SYNOPSIS
    Diagnose and repair the local Unity Android IL2CPP toolchain gaps recorded as
    P2.2 D-01 (android-cli.exe 0xC0000409) and D-02 (Ninja missing).

.DESCRIPTION
    This script never writes to the shared Unity Editor, shared Android SDK,
    shared JDK, registry, EditorPrefs or global PATH. All new tools and evidence
    are written below the repository's git-ignored build/ directory:

      build\android-toolchain\private\          stable, task-private tools
      build\android-toolchain-repair\<stamp>-<guid>\  per-run evidence

    Repairs performed:
      * Ninja: download the official ninja-build/ninja Windows release, verify
        its SHA-256 against a pinned digest, verify the executable version, and
        install it into the private tool directory.
      * Android CLI: Unity 6.3 invokes the new android-cli.exe from
        %USERPROFILE%\.android\bin. That binary exits 0xC0000409 on
        "sdk list" on this host. A private adapter forwards the documented
        "sdk list" / "--version" / "--help" calls to the installed official
        classic sdkmanager 16.0, passes its stdout/stderr and exit code through
        unchanged, and refuses mutating commands. No package list is fabricated
        and no error is truncated.

    The script records SHA-256 fingerprints of every shared tool it reads and
    fails if any of them changed during the repair.

.PARAMETER CheckOnly
    Diagnose and verify the private tools without downloading or writing them.
    Exit 0 only when every required tool is present and verified.

.PARAMETER Force
    Re-download Ninja even when the private copy already matches the pin.

.PARAMETER SkipCliSmoke
    Skip the (slow, read-only) "sdk list" smoke run of the private CLI adapter.
    Only intended for a first quick diagnosis.

.NOTES
    Exit: 0 repaired/verified; 1 repair or verification FAIL; 2 BLOCKED because a
    prerequisite, download or hash could not be satisfied. Always run under
    Windows PowerShell so the wrapper sees the same host as the build.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,

    [string]$UnityPath,

    [string]$SdkRoot,

    [string]$PrivateRoot,

    [string]$NinjaVersion = '1.13.2',

    [string]$NinjaUrl = 'https://github.com/ninja-build/ninja/releases/download/v1.13.2/ninja-win.zip',

    [string]$NinjaZipSha256 = '07FC8261B42B20E71D1720B39068C2E14FFCEE6396B76FB7A795FB460B78DC65',

    [string]$NinjaExeSha256 = 'E52A7AD9538D9618C67A0BD777964E2EEC8A30F68B810A2F6ADCE1F2DAF847B8',

    [switch]$CheckOnly,

    [switch]$Force,

    [switch]$SkipCliSmoke
)

$ErrorActionPreference = 'Stop'
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }
$script:Failures = New-Object System.Collections.Generic.List[string]
$script:Blockers = New-Object System.Collections.Generic.List[string]
$script:Checks = New-Object System.Collections.Generic.List[object]

function Write-Utf8NoBom {
    param([Parameter(Mandatory)][string]$Path, [AllowEmptyString()][string]$Text)
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Text, $utf8)
}

function Add-Check {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][ValidateSet('PASS', 'FAIL', 'BLOCKED', 'INFO')][string]$Status,
        [string]$Detail
    )
    $script:Checks.Add([pscustomobject]@{ name = $Name; status = $Status; detail = $Detail })
    $color = switch ($Status) {
        'PASS' { 'Green' }
        'FAIL' { 'Red' }
        'BLOCKED' { 'Yellow' }
        default { 'Gray' }
    }
    Write-Host ("[{0}] {1}: {2}" -f $Status, $Name, $Detail) -ForegroundColor $color
    if ($Status -eq 'FAIL') { $script:Failures.Add("$Name`: $Detail") }
    if ($Status -eq 'BLOCKED') { $script:Blockers.Add("$Name`: $Detail") }
}

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Assert-PrivateToolPath {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$PrivateRoot
    )
    $rootFull = [System.IO.Path]::GetFullPath($PrivateRoot).TrimEnd('\', '/')
    $pathFull = [System.IO.Path]::GetFullPath($Path)
    $prefix = $rootFull + [System.IO.Path]::DirectorySeparatorChar
    if (-not $pathFull.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the private tool root: $pathFull (root=$rootFull)"
    }
    return $pathFull
}

function Stop-BoundedProcessTree {
    param(
        [Parameter(Mandatory)][System.Diagnostics.Process]$Process,
        [object]$ExpectedCreationDate
    )
    $creation = $null
    try {
        $creation = (Get-CimInstance Win32_Process -Filter ("ProcessId=$($Process.Id)") -ErrorAction SilentlyContinue).CreationDate
    }
    catch { }
    if ($ExpectedCreationDate -and $creation -and ([datetime]$creation -ne [datetime]$ExpectedCreationDate)) {
        return
    }
    & taskkill.exe /PID $Process.Id /T /F 2>&1 | Out-Null
}

function Invoke-BoundedProcess {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$Arguments = @(),
        [int]$TimeoutSeconds = 60,
        [string]$WorkingDirectory,
        [hashtable]$Environment = @{},
        [string]$RawArguments
    )
    $argumentLine = if ($PSBoundParameters.ContainsKey('RawArguments')) {
        $RawArguments
    }
    else {
        ($Arguments | ForEach-Object {
            if ($_ -notmatch '[\s"]') { return $_ }
            return '"' + ($_ -replace '(\\*)"', '$1$1\"') + '"'
        }) -join ' '
    }
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $FilePath
    $startInfo.Arguments = $argumentLine
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    if ($WorkingDirectory) { $startInfo.WorkingDirectory = $WorkingDirectory }
    foreach ($key in $Environment.Keys) {
        $startInfo.EnvironmentVariables[$key] = [string]$Environment[$key]
    }
    $started = Get-Date
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    $null = $process.Start()
    $creationDate = $null
    try {
        $creationDate = (Get-CimInstance Win32_Process -Filter ("ProcessId=$($process.Id)") -ErrorAction SilentlyContinue).CreationDate
    }
    catch { }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $timedOut = $false
    if (-not $process.WaitForExit([Math]::Max(0, $TimeoutSeconds * 1000))) {
        $timedOut = $true
        Stop-BoundedProcessTree -Process $process -ExpectedCreationDate $creationDate
        try { $process.StandardOutput.Close() } catch { }
        try { $process.StandardError.Close() } catch { }
    }
    try {
        [System.Threading.Tasks.Task]::WaitAll(@($stdoutTask, $stderrTask), 1000) | Out-Null
    }
    catch { }
    $stdout = if ($stdoutTask.IsCompleted) { $stdoutTask.Result } else { '<drain timeout>' }
    $stderr = if ($stderrTask.IsCompleted) { $stderrTask.Result } else { '<drain timeout>' }
    $exitCode = if ($timedOut) { -1 } else { $process.ExitCode }
    return [pscustomobject]@{
        ExitCode    = $exitCode
        TimedOut    = $timedOut
        Stdout      = $stdout
        Stderr      = $stderr
        DurationSec = [Math]::Round(((Get-Date) - $started).TotalSeconds, 2)
        CommandLine = "$FilePath $argumentLine"
    }
}

function Find-UnityForProject {
    param([Parameter(Mandatory)][string]$ProjectVersionPath, [string]$ExplicitPath)
    $text = Get-Content -LiteralPath $ProjectVersionPath -Raw
    $match = [regex]::Match($text, 'm_EditorVersion:\s*(\S+)')
    if (-not $match.Success) { throw "Cannot parse m_EditorVersion from $ProjectVersionPath" }
    $version = $match.Groups[1].Value
    $candidates = New-Object System.Collections.Generic.List[string]
    if ($ExplicitPath) { $candidates.Add($ExplicitPath) }
    if ($env:UNITY_PATH) { $candidates.Add($env:UNITY_PATH) }
    foreach ($root in @('D:\Unity\Hub\Editor', 'C:\Program Files\Unity\Hub\Editor')) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue | ForEach-Object {
            $candidates.Add((Join-Path $_.FullName 'Editor\Unity.exe'))
        }
    }
    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $candidate)) { continue }
        $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($candidate)
        if ($info.ProductVersion -and ($info.ProductVersion -match ('^' + [regex]::Escape($version) + '([_+].*)?$'))) {
            return [pscustomobject]@{ Path = $candidate; ProjectVersion = $version; FileVersion = $info.FileVersion; ProductVersion = $info.ProductVersion }
        }
    }
    return $null
}

function Get-JdkVersion {
    param([Parameter(Mandatory)][string]$JavaExe)
    if (-not (Test-Path -LiteralPath $JavaExe)) { return $null }
    $run = Invoke-BoundedProcess -FilePath $JavaExe -Arguments @('-version') -TimeoutSeconds 20
    $line = (($run.Stderr + "`n" + $run.Stdout) -split "`r?`n" | Where-Object { $_ -match 'version "' } | Select-Object -First 1)
    return [pscustomobject]@{ ExitCode = $run.ExitCode; Line = $line; TimedOut = $run.TimedOut }
}

function Get-InstalledPackageVersions {
    param([Parameter(Mandatory)][string]$SdkRoot)
    $result = [ordered]@{ platforms = @(); buildTools = @(); cmdlineTools = @(); ndk = @(); cmake = @() }
    foreach ($pair in @(
        @{ key = 'platforms'; rel = 'platforms' },
        @{ key = 'buildTools'; rel = 'build-tools' },
        @{ key = 'cmdlineTools'; rel = 'cmdline-tools' },
        @{ key = 'ndk'; rel = 'ndk' },
        @{ key = 'cmake'; rel = 'cmake' }
    )) {
        $dir = Join-Path $SdkRoot $pair.rel
        if (Test-Path -LiteralPath $dir) {
            $result[$pair.key] = @(Get-ChildItem -LiteralPath $dir -Directory -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
        }
    }
    return $result
}

function Get-SharedFingerprint {
    param([Parameter(Mandatory)][object[]]$Paths)
    $items = @()
    foreach ($entry in $Paths) {
        $path = [string]$entry.path
        $hash = $null
        $bytes = $null
        $lastWrite = $null
        if ($path -and (Test-Path -LiteralPath $path)) {
            $hash = Get-Sha256 -Path $path
            $item = Get-Item -LiteralPath $path
            $bytes = $item.Length
            $lastWrite = $item.LastWriteTimeUtc.ToString('o')
        }
        $items += [pscustomobject]@{
            role = [string]$entry.role
            path = $path
            exists = [bool]($path -and (Test-Path -LiteralPath $path))
            bytes = $bytes
            sha256 = $hash
            lastWriteUtc = $lastWrite
        }
    }
    return $items
}

function Compare-SharedFingerprint {
    param([Parameter(Mandatory)][object[]]$Before, [Parameter(Mandatory)][object[]]$After)
    $changed = @()
    $byRole = @{}
    foreach ($item in $Before) { $byRole[$item.role] = $item }
    foreach ($afterItem in $After) {
        $beforeItem = $byRole[$afterItem.role]
        if (-not $beforeItem) {
            $changed += "$($afterItem.role): missing before snapshot"
            continue
        }
        if ($beforeItem.sha256 -ne $afterItem.sha256 -or $beforeItem.bytes -ne $afterItem.bytes) {
            $changed += ("$($afterItem.role): {0}/{1} -> {2}/{3}" -f $beforeItem.bytes, $beforeItem.sha256, $afterItem.bytes, $afterItem.sha256)
        }
    }
    return $changed
}

function New-EvidenceRunDirectory {
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$Stamp)
    New-Item -ItemType Directory -Force -Path $Root | Out-Null
    for ($attempt = 0; $attempt -lt 10; $attempt++) {
        $runId = "$Stamp-$([guid]::NewGuid().ToString('N').Substring(0, 12))"
        $candidate = Join-Path $Root $runId
        if (Test-Path -LiteralPath $candidate) { continue }
        try {
            New-Item -ItemType Directory -Path $candidate -ErrorAction Stop | Out-Null
            return $candidate
        }
        catch {
            if (Test-Path -LiteralPath $candidate) { continue }
            throw
        }
    }
    throw "Could not allocate a fresh evidence directory under $Root"
}

# ---------------------------------------------------------------------------
# Paths and evidence root
# ---------------------------------------------------------------------------
if (-not $RepoRoot) {
    $RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
}
$RepoRoot = [System.IO.Path]::GetFullPath($RepoRoot)
$buildRoot = Join-Path $RepoRoot 'build'
$evidenceRoot = Join-Path $buildRoot 'android-toolchain-repair'
$runDir = New-EvidenceRunDirectory -Root $evidenceRoot -Stamp (Get-Date -Format 'yyyyMMdd-HHmmss')
if (-not $PrivateRoot) {
    $PrivateRoot = Join-Path $buildRoot 'android-toolchain\private'
}
$PrivateRoot = [System.IO.Path]::GetFullPath($PrivateRoot)
Assert-PrivateToolPath -Path (Join-Path $PrivateRoot 'placeholder.txt') -PrivateRoot $buildRoot | Out-Null

Start-Transcript -Path (Join-Path $runDir 'repair.log') -Force | Out-Null

$summary = [ordered]@{
    schemaVersion = 1
    startedAt = (Get-Date).ToString('o')
    finishedAt = $null
    mode = $(if ($CheckOnly) { 'check-only' } else { 'repair' })
    repoRoot = $RepoRoot
    frozenSha = $null
    privateRoot = $PrivateRoot
    evidenceDir = $runDir
    checks = @()
    failures = @()
    blockers = @()
    diagnostics = $null
    sharedToolsBefore = @()
    sharedToolsAfter = @()
    sharedToolsChanged = @()
    manifest = $null
}

try {
    $frozenSha = (& git -C $RepoRoot rev-parse HEAD 2>&1 | Out-String).Trim()
    $summary.frozenSha = $frozenSha
    Write-Host "repo=$RepoRoot head=$frozenSha privateRoot=$PrivateRoot evidence=$runDir"

    # -----------------------------------------------------------------------
    # Read-only shared inventory
    # -----------------------------------------------------------------------
    $projectVersionPath = Join-Path $RepoRoot 'unity\Mahjong.Unity\ProjectSettings\ProjectVersion.txt'
    $unity = Find-UnityForProject -ProjectVersionPath $projectVersionPath -ExplicitPath $UnityPath
    if (-not $unity) {
        Add-Check -Name 'Unity editor' -Status BLOCKED -Detail "no editor matches ProjectVersion.txt"
        $summary.diagnostics = [ordered]@{ unity = $null }
    }
    else {
        Add-Check -Name 'Unity editor' -Status PASS -Detail "path=$($unity.Path) productVersion=$($unity.ProductVersion)"
    }

    $editorData = if ($unity) { Join-Path (Split-Path -Parent $unity.Path) 'Data' } else { $null }
    $androidPlayer = if ($editorData) { Join-Path $editorData 'PlaybackEngines\AndroidPlayer' } else { $null }
    $il2cppExe = if ($editorData) { Join-Path $editorData 'il2cpp\build\deploy\il2cpp.exe' } else { $null }
    $unityJdkHome = if ($androidPlayer) { Join-Path $androidPlayer 'OpenJDK' } else { $null }
    $javaExe = if ($unityJdkHome) { Join-Path $unityJdkHome 'bin\java.exe' } else { $null }

    if (-not $SdkRoot) {
        if ($env:ANDROID_HOME) { $SdkRoot = $env:ANDROID_HOME }
        elseif ($env:ANDROID_SDK_ROOT) { $SdkRoot = $env:ANDROID_SDK_ROOT }
        else { $SdkRoot = Join-Path $env:LOCALAPPDATA 'Android\Sdk' }
    }
    $SdkRoot = [System.IO.Path]::GetFullPath($SdkRoot)

    $cmakeExe = Join-Path $SdkRoot 'cmake\3.22.1\bin\cmake.exe'
    $sdkmanager16 = Join-Path $SdkRoot 'cmdline-tools\16.0\bin\sdkmanager.bat'
    $sdkmanager23 = Join-Path $SdkRoot 'cmdline-tools\latest-23-newcli-backup\bin\sdkmanager.bat'
    $adbExe = Join-Path $SdkRoot 'platform-tools\adb.exe'
    $sharedAndroidCli = Join-Path $env:USERPROFILE '.android\bin\android-cli.exe'
    $ndkRoot = Join-Path $SdkRoot 'ndk'
    $ndkDir = if (Test-Path -LiteralPath $ndkRoot) {
        Get-ChildItem -LiteralPath $ndkRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object { Test-Path (Join-Path $_.FullName 'source.properties') } |
            Sort-Object Name -Descending | Select-Object -First 1
    } else { $null }
    $ndkRevision = $null
    if ($ndkDir) {
        $line = Select-String -LiteralPath (Join-Path $ndkDir.FullName 'source.properties') -Pattern '^Pkg.Revision\s*=' | Select-Object -First 1
        if ($line) { $ndkRevision = ($line.Line -replace '^Pkg\.Revision\s*=\s*', '').Trim() }
    }

    foreach ($entry in @(
        @{ name = 'Unity Android Player module'; path = $androidPlayer },
        @{ name = 'Unity IL2CPP compiler'; path = $il2cppExe },
        @{ name = 'Unity OpenJDK 17'; path = $javaExe },
        @{ name = 'Android SDK'; path = $SdkRoot },
        @{ name = 'Android NDK'; path = $(if ($ndkDir) { $ndkDir.FullName } else { $null }) },
        @{ name = 'Android CMake 3.22.1'; path = $cmakeExe },
        @{ name = 'classic sdkmanager 16.0'; path = $sdkmanager16 },
        @{ name = 'adb'; path = $adbExe }
    )) {
        if ($entry.path -and (Test-Path -LiteralPath $entry.path)) {
            Add-Check -Name $entry.name -Status PASS -Detail $entry.path
        }
        else {
            Add-Check -Name $entry.name -Status BLOCKED -Detail "missing: $($entry.path)"
        }
    }

    $cmakeVersion = $null
    if (Test-Path -LiteralPath $cmakeExe) {
        $cmakeRun = Invoke-BoundedProcess -FilePath $cmakeExe -Arguments @('--version') -TimeoutSeconds 30
        $cmakeVersion = (($cmakeRun.Stdout + "`n" + $cmakeRun.Stderr) -split "`r?`n" | Where-Object { $_ -match '^cmake version ' } | Select-Object -First 1)
        if ($cmakeRun.ExitCode -eq 0 -and $cmakeVersion) {
            Add-Check -Name 'CMake version' -Status PASS -Detail "$cmakeVersion ($cmakeExe)"
        }
        else {
            Add-Check -Name 'CMake version' -Status FAIL -Detail "exit=$($cmakeRun.ExitCode); $cmakeVersion"
        }
    }

    $jdkVersion = if ($javaExe) { Get-JdkVersion -JavaExe $javaExe } else { $null }
    $jdkLine = if ($jdkVersion) { $jdkVersion.Line } else { $null }
    if ($jdkVersion -and $jdkVersion.ExitCode -eq 0 -and $jdkLine -match '"(?<major>[0-9]+)\.') {
        $jdkMajor = [int]$Matches['major']
        if ($jdkMajor -eq 17) {
            Add-Check -Name 'JDK version' -Status PASS -Detail "$jdkLine ($javaExe)"
        }
        else {
            Add-Check -Name 'JDK version' -Status FAIL -Detail "Unity JDK is not 17: $jdkLine"
        }
    }
    else {
        Add-Check -Name 'JDK version' -Status BLOCKED -Detail "could not read java -version from $javaExe"
    }

    $packages = Get-InstalledPackageVersions -SdkRoot $SdkRoot
    Add-Check -Name 'SDK packages' -Status INFO -Detail ("platforms={0}; buildTools={1}; ndk={2}; cmake={3}; cmdlineTools={4}" -f ($packages.platforms -join ','), ($packages.buildTools -join ','), ($packages.ndk -join ','), ($packages.cmake -join ','), ($packages.cmdlineTools -join ','))

    # Android CLI crash and classic sdkmanager facts (read-only).
    $androidCliVersion = $null
    $androidCliList = $null
    if (Test-Path -LiteralPath $sharedAndroidCli) {
        $versionRun = Invoke-BoundedProcess -FilePath $sharedAndroidCli -Arguments @('--version') -TimeoutSeconds 30
        $androidCliVersion = (($versionRun.Stdout + "`n" + $versionRun.Stderr) -split "`r?`n" | Where-Object { $_ -match '^[0-9]+\.[0-9]+' } | Select-Object -First 1)
        Add-Check -Name 'Shared android-cli version' -Status INFO -Detail "$androidCliVersion exit=$($versionRun.ExitCode) path=$sharedAndroidCli"
        $androidCliList = Invoke-BoundedProcess -FilePath $sharedAndroidCli -Arguments @('sdk','list') -TimeoutSeconds 90
        $androidCliHasList = ($androidCliList.Stdout -match 'Installed packages:')
        Add-Check -Name 'Shared android-cli sdk list' -Status INFO `
            -Detail ("external defect recorded: exit={0} timedOut={1} installedList={2} bytes={3}" -f $androidCliList.ExitCode, $androidCliList.TimedOut, $androidCliHasList, $androidCliList.Stdout.Length)
    }
    else {
        Add-Check -Name 'Shared android-cli version' -Status INFO -Detail "not present: $sharedAndroidCli"
    }

    $sdkmanager16Version = $null
    if (Test-Path -LiteralPath $sdkmanager16) {
        $smRun = Invoke-BoundedProcess -FilePath 'cmd.exe' -RawArguments ("/d /c `"`"$sdkmanager16`" --version`"") -TimeoutSeconds 60
        $sdkmanager16Version = (($smRun.Stdout + "`n" + $smRun.Stderr) -split "`r?`n" | Where-Object { $_ -match '^[0-9]+\.[0-9]+' } | Select-Object -First 1)
        Add-Check -Name 'Classic sdkmanager 16.0' -Status $(if ($smRun.ExitCode -eq 0) { 'PASS' } else { 'FAIL' }) `
            -Detail "version=$sdkmanager16Version exit=$($smRun.ExitCode) path=$sdkmanager16"
    }

    # Ninja diagnosis.
    $pathNinja = Get-Command ninja.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    $sdkNinja = @(Get-ChildItem -LiteralPath (Join-Path $SdkRoot 'cmake') -Recurse -Filter ninja.exe -ErrorAction SilentlyContinue)
    $privateNinja = Join-Path $PrivateRoot ("ninja\{0}\ninja.exe" -f $NinjaVersion)
    Add-Check -Name 'Ninja presence' -Status INFO `
        -Detail ("pre-repair gap recorded: onPath={0}; sdkCmakeBins={1}; private={2}" -f $(if ($pathNinja) { $pathNinja.Source } else { 'no' }), $sdkNinja.Count, $(if (Test-Path -LiteralPath $privateNinja) { $privateNinja } else { 'no' }))

    $summary.diagnostics = [ordered]@{
        unity = $unity
        unityAndroidPlayer = $androidPlayer
        il2cpp = $il2cppExe
        jdkHome = $unityJdkHome
        java = $javaExe
        jdkVersion = $jdkLine
        sdkRoot = $SdkRoot
        sdkPackages = $packages
        ndk = $(if ($ndkDir) { $ndkDir.FullName } else { $null })
        ndkRevision = $ndkRevision
        cmake = $cmakeExe
        cmakeVersion = $cmakeVersion
        sdkmanager16 = $sdkmanager16
        sdkmanager16Version = $sdkmanager16Version
        sdkmanager23 = $sdkmanager23
        adb = $adbExe
        sharedAndroidCli = $sharedAndroidCli
        sharedAndroidCliVersion = $androidCliVersion
        sharedAndroidCliSdkListExit = $(if ($androidCliList) { $androidCliList.ExitCode } else { $null })
        sharedAndroidCliSdkListTimedOut = $(if ($androidCliList) { $androidCliList.TimedOut } else { $null })
        sharedAndroidCliSdkListBytes = $(if ($androidCliList) { $androidCliList.Stdout.Length } else { $null })
        ninjaOnPath = $(if ($pathNinja) { $pathNinja.Source } else { $null })
        sdkCmakeNinjaCount = $sdkNinja.Count
        privateNinja = $(if (Test-Path -LiteralPath $privateNinja) { $privateNinja } else { $null })
    }
    Write-Utf8NoBom -Path (Join-Path $runDir 'diagnostics.json') -Text ($summary.diagnostics | ConvertTo-Json -Depth 8)

    $sharedSpecs = @(
        @{ role = 'unity'; path = $(if ($unity) { $unity.Path } else { $null }) },
        @{ role = 'unity-java'; path = $javaExe },
        @{ role = 'sdk-cmake'; path = $cmakeExe },
        @{ role = 'sdk-sdkmanager16'; path = $sdkmanager16 },
        @{ role = 'sdk-sdkmanager23'; path = $sdkmanager23 },
        @{ role = 'shared-android-cli'; path = $sharedAndroidCli },
        @{ role = 'sdk-adb'; path = $adbExe },
        @{ role = 'sdk-ndk-source-properties'; path = $(if ($ndkDir) { Join-Path $ndkDir.FullName 'source.properties' } else { $null }) }
    )
    $before = @(Get-SharedFingerprint -Paths $sharedSpecs)
    $summary.sharedToolsBefore = $before
    Write-Utf8NoBom -Path (Join-Path $runDir 'shared-tools-before.json') -Text ($before | ConvertTo-Json -Depth 6)

    # -----------------------------------------------------------------------
    # Repair / verify private tools
    # -----------------------------------------------------------------------
    if (-not $CheckOnly) {
        # Ninja
        $needNinja = $Force -or (-not (Test-Path -LiteralPath $privateNinja))
        if (-not $needNinja) {
            $existingHash = Get-Sha256 -Path $privateNinja
            if ($existingHash -ne $NinjaExeSha256) { $needNinja = $true }
        }
        if ($needNinja) {
            $downloadDir = Join-Path $runDir 'downloads'
            New-Item -ItemType Directory -Force -Path $downloadDir | Out-Null
            $zipPath = Join-Path $downloadDir 'ninja-win.zip'
            try {
                Invoke-WebRequest -Uri $NinjaUrl -OutFile $zipPath -UseBasicParsing -TimeoutSec 180
            }
            catch {
                Add-Check -Name 'Ninja download' -Status BLOCKED -Detail $_.Exception.Message
            }
            if (Test-Path -LiteralPath $zipPath) {
                $zipHash = Get-Sha256 -Path $zipPath
                if ($zipHash -ne $NinjaZipSha256) {
                    Add-Check -Name 'Ninja download hash' -Status BLOCKED -Detail "expected=$NinjaZipSha256 actual=$zipHash"
                }
                else {
                    Add-Check -Name 'Ninja download hash' -Status PASS -Detail "sha256=$zipHash bytes=$((Get-Item -LiteralPath $zipPath).Length) source=$NinjaUrl"
                    $extractDir = Join-Path $downloadDir 'extract'
                    if (Test-Path -LiteralPath $extractDir) { Remove-Item -LiteralPath $extractDir -Recurse -Force }
                    Expand-Archive -LiteralPath $zipPath -DestinationPath $extractDir -Force
                    $extractedExe = Join-Path $extractDir 'ninja.exe'
                    $extractedHash = Get-Sha256 -Path $extractedExe
                    if ($extractedHash -ne $NinjaExeSha256) {
                        Add-Check -Name 'Ninja executable hash' -Status BLOCKED -Detail "expected=$NinjaExeSha256 actual=$extractedHash"
                    }
                    else {
                        $targetDir = Split-Path -Parent $privateNinja
                        New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
                        Copy-Item -LiteralPath $extractedExe -Destination $privateNinja -Force
                        Add-Check -Name 'Ninja install' -Status PASS -Detail "path=$privateNinja sha256=$extractedHash"
                    }
                }
            }
        }
        else {
            Add-Check -Name 'Ninja install' -Status PASS -Detail "reused pinned copy: $privateNinja"
        }

        # Private CLI adapter (real sdkmanager passthrough, no fake data).
        $cliDir = Join-Path $PrivateRoot 'android-cli'
        New-Item -ItemType Directory -Force -Path $cliDir | Out-Null
        $shimPath = Join-Path $cliDir 'android-cli-sdkmanager16.cmd'
        $shimLines = @(
            '@echo off',
            'setlocal EnableExtensions',
            'rem Private adapter for Unity 6.3 ANDROID_CLI_BIN.',
            'rem The bundled android-cli.exe 1.0.15985488 exits 0xC0000409 on "sdk list" on this host.',
            'rem This adapter delegates only read-only discovery to the installed classic sdkmanager 16.0,',
            'rem passes its stdout/stderr and exit code through unchanged, and refuses mutating commands.',
            ('set "CLASSIC=' + $sdkmanager16 + '"'),
            'set "LOG=%ANDROID_CLI_SHIM_LOG%"',
            'if "%LOG%"=="" set "LOG=%~dp0android-cli-shim.log"',
            'set "ARGS=%*"',
            ('>>"%LOG%" echo [%DATE% %TIME%] args=%ARGS%'),
            'if not exist "%CLASSIC%" (',
            '  >>"%LOG%" echo [shim] missing classic sdkmanager: %CLASSIC%',
            '  exit /b 2',
            ')',
            'if "%ARGS%"=="" goto refuse',
            'echo %ARGS% | findstr /C:"--version" >nul',
            'if %ERRORLEVEL%==0 goto version',
            'echo %ARGS% | findstr /C:"sdk list" >nul',
            'if %ERRORLEVEL%==0 goto list',
            'echo %ARGS% | findstr /C:"--help" >nul',
            'if %ERRORLEVEL%==0 goto help',
            'goto refuse',
            ':version',
            'call "%CLASSIC%" --version',
            'set "RC=%ERRORLEVEL%"',
            '>>"%LOG%" echo [shim] version rc=%RC%',
            'exit /b %RC%',
            ':list',
            'call "%CLASSIC%" --list',
            'set "RC=%ERRORLEVEL%"',
            '>>"%LOG%" echo [shim] list rc=%RC%',
            'exit /b %RC%',
            ':help',
            'call "%CLASSIC%" --help',
            'set "RC=%ERRORLEVEL%"',
            '>>"%LOG%" echo [shim] help rc=%RC%',
            'exit /b %RC%',
            ':refuse',
            '>>"%LOG%" echo [shim] refused non-readonly invocation: %ARGS%',
            '>&2 echo [android-cli-shim] refused non-readonly invocation: %ARGS%',
            'exit /b 2'
        )
        Write-Utf8NoBom -Path $shimPath -Text (($shimLines -join "`r`n") + "`r`n")
        $shimHash = Get-Sha256 -Path $shimPath
        Add-Check -Name 'Private Android CLI adapter' -Status PASS -Detail "path=$shimPath sha256=$shimHash delegate=$sdkmanager16"

        $activatePath = Join-Path $PrivateRoot 'activate.ps1'
        $activateLines = @(
            '# Generated by repair-android-toolchain.ps1; private, process-scoped.',
            ('$env:ANDROID_CLI_BIN = ''' + $shimPath + ''''),
            ('$env:JAVA_HOME = ''' + $unityJdkHome + ''''),
            ('$env:PATH = ''' + (Split-Path -Parent $privateNinja) + ';' + (Join-Path $unityJdkHome 'bin') + ';'' + $env:PATH'),
            '# Usage: . .\build\android-toolchain\private\activate.ps1'
        )
        Write-Utf8NoBom -Path $activatePath -Text (($activateLines -join "`r`n") + "`r`n")
    }

    # -----------------------------------------------------------------------
    # Verification
    # -----------------------------------------------------------------------
    $ninjaOk = $false
    $ninjaActualVersion = $null
    $ninjaActualHash = $null
    if (Test-Path -LiteralPath $privateNinja) {
        $ninjaActualHash = Get-Sha256 -Path $privateNinja
        $ninjaRun = Invoke-BoundedProcess -FilePath $privateNinja -Arguments @('--version') -TimeoutSeconds 20
        $ninjaActualVersion = (($ninjaRun.Stdout + "`n" + $ninjaRun.Stderr) -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 1)
        $ninjaOk = ($ninjaRun.ExitCode -eq 0) -and ($ninjaActualVersion -eq $NinjaVersion) -and ($ninjaActualHash -eq $NinjaExeSha256)
        Add-Check -Name 'Ninja verification' -Status $(if ($ninjaOk) { 'PASS' } else { 'FAIL' }) `
            -Detail "path=$privateNinja version=$ninjaActualVersion sha256=$ninjaActualHash exit=$($ninjaRun.ExitCode)"
    }
    else {
        Add-Check -Name 'Ninja verification' -Status FAIL -Detail "missing private ninja: $privateNinja"
    }

    $shimPath = Join-Path $PrivateRoot 'android-cli\android-cli-sdkmanager16.cmd'
    $cliSmoke = $null
    $cliOk = $false
    if (Test-Path -LiteralPath $shimPath) {
        if (-not $SkipCliSmoke) {
            $shimLog = Join-Path $runDir 'android-cli-shim.log'
            $cliSmoke = Invoke-BoundedProcess -FilePath 'cmd.exe' -RawArguments ("/d /c `"`"$shimPath`" sdk list`"") -TimeoutSeconds 300 `
                -Environment @{ ANDROID_CLI_SHIM_LOG = $shimLog }
            $hasHeader = ($cliSmoke.Stdout -match 'Installed packages:')
            $cliOk = (-not $cliSmoke.TimedOut) -and ($cliSmoke.ExitCode -eq 0) -and $hasHeader -and ($cliSmoke.Stdout.Length -gt 100)
            Add-Check -Name 'Private CLI sdk list' -Status $(if ($cliOk) { 'PASS' } else { 'FAIL' }) `
                -Detail ("exit={0} timedOut={1} installedList={2} bytes={3} seconds={4}" -f $cliSmoke.ExitCode, $cliSmoke.TimedOut, $hasHeader, $cliSmoke.Stdout.Length, $cliSmoke.DurationSec)
            Write-Utf8NoBom -Path (Join-Path $runDir 'android-cli-sdk-list.stdout.txt') -Text $cliSmoke.Stdout
            Write-Utf8NoBom -Path (Join-Path $runDir 'android-cli-sdk-list.stderr.txt') -Text $cliSmoke.Stderr
        }
        else {
            $cliOk = $true
            Add-Check -Name 'Private CLI sdk list' -Status INFO -Detail 'skipped by -SkipCliSmoke'
        }
    }
    else {
        Add-Check -Name 'Private CLI adapter' -Status FAIL -Detail "missing: $shimPath"
    }

    $after = @(Get-SharedFingerprint -Paths $sharedSpecs)
    $summary.sharedToolsAfter = $after
    $changed = @(Compare-SharedFingerprint -Before $before -After $after)
    $summary.sharedToolsChanged = $changed
    Write-Utf8NoBom -Path (Join-Path $runDir 'shared-tools-after.json') -Text ($after | ConvertTo-Json -Depth 6)
    if ($changed.Count -gt 0) {
        Add-Check -Name 'Shared tools unchanged' -Status FAIL -Detail ($changed -join '; ')
    }
    else {
        Add-Check -Name 'Shared tools unchanged' -Status PASS -Detail "$($after.Count) shared files identical before/after"
    }

    $manifestPath = Join-Path $PrivateRoot 'toolchain.json'
    if ($ninjaOk -and $cliOk -and (Test-Path -LiteralPath $manifestPath)) {
        $summary.manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    }

    # Manifest is written after verification data is available.
    if (-not $CheckOnly) {
        $manifest = [ordered]@{
            schemaVersion = 1
            generatedAt = (Get-Date).ToString('o')
            frozenSha = $frozenSha
            privateRoot = $PrivateRoot
            ninjaVersion = $NinjaVersion
            ninjaDir = (Split-Path -Parent $privateNinja)
            ninjaExe = $privateNinja
            ninjaSha256 = $ninjaActualHash
            ninjaSource = $NinjaUrl
            ninjaSourceSha256 = $NinjaZipSha256
            androidCliBin = $shimPath
            androidCliMode = 'classic-sdkmanager-16-passthrough'
            androidCliDelegate = $sdkmanager16
            androidCliDelegateSha256 = (Get-Sha256 -Path $sdkmanager16)
            androidCliReason = 'bundled android-cli.exe 1.0.15985488 exits 0xC0000409 on sdk list'
            jdkHome = $unityJdkHome
            jdkVersion = $jdkLine
            sdkRoot = $SdkRoot
            sdkPackages = $packages
            ndk = $(if ($ndkDir) { $ndkDir.FullName } else { $null })
            ndkRevision = $ndkRevision
            cmake = $cmakeExe
            cmakeVersion = $cmakeVersion
            pathPrepend = @((Split-Path -Parent $privateNinja))
            env = [ordered]@{
                ANDROID_CLI_BIN = $shimPath
                JAVA_HOME = $unityJdkHome
            }
            licenses = @(
                'Ninja 1.13.2 is Apache-2.0; source and digest recorded above.',
                'sdkmanager 16.0 is read from the already-installed Android SDK; no new SDK license was accepted.'
            )
        }
        Write-Utf8NoBom -Path $manifestPath -Text ($manifest | ConvertTo-Json -Depth 8)
        $summary.manifest = $manifest
        Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $runDir 'toolchain.json') -Force
        Add-Check -Name 'Private toolchain manifest' -Status PASS -Detail "$manifestPath sha256=$(Get-Sha256 -Path $manifestPath)"
    }

    $ready = $ninjaOk -and $cliOk -and (Test-Path -LiteralPath $shimPath) -and (Test-Path -LiteralPath $manifestPath) -and ($changed.Count -eq 0)
    Add-Check -Name 'Android toolchain repair' -Status $(if ($ready) { 'PASS' } else { 'FAIL' }) `
        -Detail ("ninja={0}; cli={1}; shim={2}; manifest={3}; sharedUnchanged={4}" -f $ninjaOk, $cliOk, (Test-Path -LiteralPath $shimPath), (Test-Path -LiteralPath $manifestPath), ($changed.Count -eq 0))
}
catch {
    Add-Check -Name 'Repair script' -Status FAIL -Detail $_.Exception.Message
    Write-Host "STACK: $($_.ScriptStackTrace)"
}

$summary.finishedAt = (Get-Date).ToString('o')
$summary.checks = $script:Checks.ToArray()
$summary.failures = $script:Failures.ToArray()
$summary.blockers = $script:Blockers.ToArray()
$code = if ($script:Failures.Count -gt 0) { 1 } elseif ($script:Blockers.Count -gt 0) { 2 } else { 0 }
$summary.exitCode = $code
Write-Utf8NoBom -Path (Join-Path $runDir 'summary.json') -Text ($summary | ConvertTo-Json -Depth 12)
$resultLines = @(
    "mode=$($summary.mode)",
    "head=$($summary.frozenSha)",
    "privateRoot=$PrivateRoot",
    "checks=$($script:Checks.Count) failures=$($script:Failures.Count) blockers=$($script:Blockers.Count)",
    "exit=$code"
)
Write-Utf8NoBom -Path (Join-Path $runDir 'results.txt') -Text (($resultLines -join "`r`n") + "`r`n")
Write-Host ''
Write-Host ("SUMMARY mode={0} checks={1} failures={2} blockers={3} exit={4}" -f $summary.mode, $script:Checks.Count, $script:Failures.Count, $script:Blockers.Count, $code)
Write-Host "evidence=$runDir"
try { Stop-Transcript | Out-Null } catch { }
exit $code
