<#
.SYNOPSIS
    Single unified verification entry for the V5 .NET tree.

.DESCRIPTION
    Reads tools\v5-verify.manifest.json, builds dotnet\Mahjong.sln, then runs every
    test project it knows about - the manifest entries plus any *.Tests project
    auto-discovered under dotnet/tests or dotnet/src (so newly merged test projects
    are verified without editing this script or the manifest).

    Guarantees:
      - every project (manifest or discovered) is reported with a per-project
        result (PASS/FAIL/BLOCKED/NOT_RUN), real executed test counts and log paths
      - failing projects, zero-test-case projects and missing required projects
        fail the whole run; nothing is silently skipped and the script never
        reports green when a row did not pass
      - console helpers (e.g. Mahjong.Outbox.CrashWorker) are verified as build
        artifacts only and are never counted as tests
      - projects not scheduled in the sln (auto-discovered ones) are always built
        individually before anything runs - in every runner mode - and a fresh
        build failure fails the project outright; stale outputs left by an
        earlier build are never executed
      - if `dotnet test` cannot produce a parseable summary, the log is checked
        for build/restore errors first: any compile error fails the project even
        when an older DLL is sitting in bin. Only a project whose current source
        verifiably built in this run (solution build or per-project build) and
        whose log shows no build errors may fall back to the official in-process
        runner (xunit.v3 / Microsoft.Testing.Platform executable) of the same
        test assembly; the summary states which runner produced the counts
      - every test execution is attributable: run-info.txt records the exact head
        SHA / branch / CI run identity, and runs\<project>\<mode>.meta.txt records
        the exact command line, working directory, exit code, parsed counts and
        the collected framework-log list of each dotnetTest/direct invocation
      - raw test-framework failure logs (xunit.v3 / MTP TestResults, e.g.
        bin\<cfg>\<tfm>\TestResults\<asm>_<tfm>_<arch>.log - the file that names
        the failing case) are collected under <log dir>\test-results\<project>\
        with relative paths preserved. Collection is run-exclusive: the project's
        TestResults directories are removed immediately before each runner
        invocation, so a log found after the run was produced by THIS run only
        and stale logs can never be presented as current-run evidence. Collection
        is best-effort evidence and never changes a gate result or exit code
      - the Unity plugin copy is exercised into a scratch directory under the log
        dir; tracked plugin DLLs and ProjectSettings are never overwritten

    Local usage:
      powershell -ExecutionPolicy Bypass -File tools\verify-v5.ps1 [-Configuration Release]
                 [-DotnetPath D:\Unity\dotnet\dotnet.exe] [-RunnerMode auto|dotnetTest|direct]
                 [-ManifestPath <path>] [-LogDir <path>] [-SkipPluginCopyCheck]
                 [-NoProjectDiscovery]

    -NoProjectDiscovery disables auto-discovery of *.Tests projects under
    dotnet\tests|src; only manifest projects are verified. Used by the isolated
    diagnostics self-test (tools\tests\VerifyV5Diagnostics.Tests.ps1) to verify a
    throwaway fixture project without touching the production tree.

    Exit codes: 0 = every row PASS; 1 = at least one row FAIL; 2 = environment
    blocked (manifest/solution/dotnet missing).
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$DotnetPath,

    [string]$ManifestPath,

    [string]$LogDir,

    [ValidateSet('auto', 'dotnetTest', 'direct')]
    [string]$RunnerMode = 'auto',

    [switch]$SkipPluginCopyCheck,

    [switch]$NoProjectDiscovery
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$root = Split-Path -Parent $PSScriptRoot
if (-not $ManifestPath) { $ManifestPath = Join-Path $PSScriptRoot 'v5-verify.manifest.json' }
if (-not $LogDir) {
    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $LogDir = Join-Path $root "artifacts\verify-v5\$timestamp"
}

# Each run gets an exclusive log directory - a second run in the same second
# (or an explicit -LogDir that already exists) never appends into a previous
# run's evidence.
$suffix = 0
$LogDirBase = $LogDir
while (Test-Path $LogDir) {
    $suffix++
    $LogDir = "$LogDirBase-$suffix"
}

function Write-Step([string]$message) { Write-Host "[verify-v5] $message" }

function Resolve-DotnetPath {
    if ($DotnetPath) { return $DotnetPath }
    if ($env:DOTNET_ROOT -and (Test-Path (Join-Path $env:DOTNET_ROOT 'dotnet.exe'))) {
        return (Join-Path $env:DOTNET_ROOT 'dotnet.exe')
    }
    if (Test-Path 'D:\Unity\dotnet\dotnet.exe') { return 'D:\Unity\dotnet\dotnet.exe' }
    try { return (Get-Command dotnet -ErrorAction Stop).Source } catch { return $null }
}

# Runs a native command, tees all output into a log file, returns exit code.
function Invoke-Captured {
    param(
        [string]$LogPath,
        [scriptblock]$Command
    )
    $output = & $Command 2>&1
    $exit = $LASTEXITCODE
    $output | Out-String | Set-Content -Path $LogPath -Encoding UTF8
    return $exit
}

function Resolve-UnderRoot([string]$Base, [string]$Path) {
    # Manifest paths are repository-relative; the isolated diagnostics self-test
    # passes absolute paths into its own TEMP fixture. Join-Path would CONCATENATE
    # a rooted path on a different drive (Join-Path D:\repo C:\x -> D:\repo\C:\x),
    # so rooted paths are returned unchanged.
    $p = $Path -replace '/', '\'
    if ([IO.Path]::IsPathRooted($p)) { return $p }
    return Join-Path $Base $p
}

function Get-CsprojProperty {
    param([string]$CsprojPath, [string]$Name)
    $xpath = '//*[local-name()="' + $Name + '"]'
    $value = (Select-Xml -Path $CsprojPath -XPath $xpath).Node
    if ($value) { return "$($value.InnerText)".Trim() }
    return $null
}

function Get-TestSummaryFromVstest([string]$logPath) {
    # English-forced vstest line, e.g.:
    #   Passed! - Failed: 0, Passed: 233, Skipped: 0, Total: 233, Duration: 2s 760ms - X.dll (net8.0|x64)
    $line = Select-String -Path $logPath -Pattern '(Passed|Failed)! - Failed: (\d+), Passed: (\d+), Skipped: (\d+), Total: (\d+)' |
        Select-Object -Last 1
    if (-not $line) { return $null }
    return @{
        Total   = [int]$line.Matches[0].Groups[5].Value
        Failed  = [int]$line.Matches[0].Groups[2].Value
        Skipped = [int]$line.Matches[0].Groups[4].Value
    }
}

function Get-TestSummaryFromDirectRunner([string]$logPath) {
    # xunit.v3 / MTP direct summary. The runner omits all-zero suffixes, so both
    # forms must parse:
    #      Mahjong.Domain.Tests  Total: 233, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 3.058s
    #      ProbeZero.Tests  Total: 0
    $line = Select-String -Path $logPath -Pattern 'Total:\s*(\d+)(?:,\s*Errors:\s*(\d+))?(?:,\s*Failed:\s*(\d+))?(?:,\s*Skipped:\s*(\d+))?(?:,\s*Not Run:\s*(\d+))?' |
        Select-Object -Last 1
    if (-not $line) { return $null }
    $errors = $(if ($line.Matches[0].Groups[2].Success) { [int]$line.Matches[0].Groups[2].Value } else { 0 })
    $failed = $(if ($line.Matches[0].Groups[3].Success) { [int]$line.Matches[0].Groups[3].Value } else { 0 })
    $skipped = $(if ($line.Matches[0].Groups[4].Success) { [int]$line.Matches[0].Groups[4].Value } else { 0 })
    $notRun = $(if ($line.Matches[0].Groups[5].Success) { [int]$line.Matches[0].Groups[5].Value } else { 0 })
    return @{
        Total   = [int]$line.Matches[0].Groups[1].Value
        Failed  = $errors + $failed
        Skipped = $skipped + $notRun
    }
}

# --- run identity / diagnostics collection helpers ------------------------------
# Git failures are recorded verbatim, never fabricated.

function Get-RunHeadInfo {
    $info = @{ Head = '(unknown)'; Branch = '(unknown)' }
    try {
        $head = (& git -C $root rev-parse HEAD 2>$null | Out-String).Trim()
        if ($head) { $info.Head = $head }
        $branch = (& git -C $root rev-parse --abbrev-ref HEAD 2>$null | Out-String).Trim()
        if ($branch) { $info.Branch = $branch }
    }
    catch {
        $info.Head = "git unavailable: $($_.Exception.Message)"
    }
    return $info
}

function Get-CiProvenanceLines {
    # Only public run-metadata variables. No token/secret values are read.
    $lines = @()
    foreach ($name in @('GITHUB_REPOSITORY', 'GITHUB_RUN_ID', 'GITHUB_RUN_ATTEMPT', 'GITHUB_SHA', 'GITHUB_REF', 'GITHUB_REF_NAME', 'RUNNER_OS', 'CI')) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if ($value) { $lines += ("{0}={1}" -f $name, $value) }
    }
    return $lines
}

function Write-RunInfoFile {
    param([string]$Phase, [hashtable]$Head)
    $scriptHash = '(unavailable)'
    try { $scriptHash = (Get-FileHash -Path $PSCommandPath -Algorithm SHA256).Hash } catch { }
    $ciLines = Get-CiProvenanceLines
    $text = @(
        "verify-v5 run identity  ($Phase)"
        "utc          : $((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'))"
        "local        : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')"
        "head         : $($Head.Head)"
        "branch       : $($Head.Branch)"
        "config       : $Configuration"
        "runner mode  : $RunnerMode"
        "manifest     : $ManifestPath"
        "log dir      : $LogDir"
        "script       : $PSCommandPath"
        "script sha256: $scriptHash"
        "dotnet       : (resolved later; see sdk-diagnostic.log)"
        "ci provenance: $(if ($ciLines.Count) { $ciLines -join ' ' } else { '(none - local run)' })"
    )
    Set-Content -Path (Join-Path $LogDir 'run-info.txt') -Value ($text -join [Environment]::NewLine) -Encoding UTF8
}

# Run-exclusive framework-log collection: the project's TestResults directories
# (bin\<cfg>\<tfm>\TestResults - where xunit.v3/MTP writes its failure log - and
# the classic project-root TestResults) are removed immediately before each
# runner invocation, so anything found after the run was produced by THIS run.
function Get-TestResultsCandidates {
    param([string]$ProjectDir, [string]$Configuration, [string]$Tfm)
    return @(
        (Join-Path $ProjectDir (Join-Path 'bin' (Join-Path $Configuration (Join-Path $Tfm 'TestResults')))),
        (Join-Path $ProjectDir 'TestResults')
    )
}

function Clear-TestResultsDirs {
    param([string[]]$Dirs)
    $removed = 0
    foreach ($dir in $Dirs) {
        if (-not (Test-Path -LiteralPath $dir)) { continue }
        try {
            Remove-Item -LiteralPath $dir -Recurse -Force
            $removed++
        }
        catch {
            Add-Content -Path (Join-Path $LogDir 'collection-errors.log') -Value ("purge failed for ${dir}: $($_.Exception.Message)") -Encoding UTF8
        }
    }
    return $removed
}

function Save-TestResults {
    param([string]$ProjectName, [string[]]$Dirs)
    $destRoot = Join-Path $LogDir (Join-Path 'test-results' $ProjectName)
    $collected = @()
    foreach ($dir in $Dirs) {
        if (-not (Test-Path -LiteralPath $dir)) { continue }
        $files = @()
        try { $files = @(Get-ChildItem -LiteralPath $dir -Recurse -File -ErrorAction Stop) }
        catch {
            Add-Content -Path (Join-Path $LogDir 'collection-errors.log') -Value ("enumerate failed for ${dir}: $($_.Exception.Message)") -Encoding UTF8
            continue
        }
        foreach ($file in $files) {
            $rel = $file.FullName.Substring($dir.Length).TrimStart('\', '/')
            $dest = Join-Path $destRoot $rel
            try {
                $destDir = Split-Path $dest -Parent
                if (-not (Test-Path -LiteralPath $destDir)) { New-Item -ItemType Directory -Force -Path $destDir | Out-Null }
                Copy-Item -LiteralPath $file.FullName -Destination $dest -Force
                $hash = '(unavailable)'
                try { $hash = (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash } catch { }
                $collected += ("{0}  sha256={1}" -f (Join-Path (Join-Path 'test-results' $ProjectName) $rel), $hash)
            }
            catch {
                Add-Content -Path (Join-Path $LogDir 'collection-errors.log') -Value ("copy failed for $($file.FullName): $($_.Exception.Message)") -Encoding UTF8
            }
        }
    }
    return $collected
}

function Write-RunMetaFile {
    # Per-invocation attribution. The final row result of the project is in
    # summary.log; this file pins down exactly what was executed and produced.
    param(
        [string]$Name, [string]$ProjectPath, [string]$Kind, [string]$Mode,
        [string]$RunnerTag, [string]$Runner, [string]$Command, [string]$Cwd,
        [object]$ExitCode, [object]$Total, [object]$Failed, [object]$Skipped,
        [string[]]$Collected, [int]$PurgedDirs, [hashtable]$Head
    )
    $dir = Join-Path $LogDir (Join-Path 'runs' $Name)
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $text = @(
        "project     : $Name ($ProjectPath)"
        "kind        : $Kind"
        "runner mode : $Mode"
        "runner tag  : $RunnerTag"
        "runner used : $Runner"
        "command     : $Command"
        "cwd         : $Cwd"
        "exit code   : $ExitCode"
        "total       : $Total"
        "failed      : $Failed"
        "skipped     : $Skipped"
        "head        : $($Head.Head)"
        "purged TestResults dirs before this invocation: $PurgedDirs"
        "collected framework logs:"
    )
    if (@($Collected).Count -gt 0) { $text += (@($Collected) | ForEach-Object { "  $_" }) }
    else { $text += "  (none)" }
    Set-Content -Path (Join-Path $dir "$RunnerTag.meta.txt") -Value ($text -join [Environment]::NewLine) -Encoding UTF8
}

# --- 0. environment ------------------------------------------------------------
# The exclusive log directory and the run identity are established before any
# BLOCKED exit so even an environment-blocked run leaves evidence of what was
# attempted (same exit codes as before - only the evidence is new).
$headInfo = Get-RunHeadInfo
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
Write-RunInfoFile -Phase 'start' -Head $headInfo

if (-not (Test-Path $ManifestPath)) {
    Write-Host "BLOCKED: manifest not found: $ManifestPath"
    exit 2
}
$manifest = Get-Content $ManifestPath -Raw | ConvertFrom-Json

$DotnetPath = Resolve-DotnetPath
$solution = Resolve-UnderRoot $root $manifest.solution
if (-not $DotnetPath) {
    Write-Host "BLOCKED: dotnet.exe not found (pass -DotnetPath)"
    exit 2
}
if (-not (Test-Path $solution)) {
    Write-Host "BLOCKED: solution not found: $solution"
    exit 2
}

# --- 0b. SDK diagnostics -------------------------------------------------------
# Record the SDK actually selected by the dotnet executable used for every build
# and test below, together with the working directory that decides global.json
# resolution. A diagnostic failure is an environment block (exit 2), never a PASS.
$cwd = (Get-Location).Path
$globalJsonPath = $null
$probe = $cwd
while ($probe) {
    $candidate = Join-Path $probe 'global.json'
    if (Test-Path $candidate) { $globalJsonPath = $candidate; break }
    $parent = Split-Path $probe -Parent
    if (-not $parent -or $parent -eq $probe) { break }
    $probe = $parent
}
$sdkLog = Join-Path $LogDir 'sdk-diagnostic.log'
$versionOutput = @(& $DotnetPath --version 2>&1)
$versionExit = $LASTEXITCODE
$infoOutput = @(& $DotnetPath --info 2>&1)
$infoExit = $LASTEXITCODE
$sdkVersion = (($versionOutput | Out-String).Trim())
$sdkText = @(
    "cwd        : $cwd"
    "dotnet     : $DotnetPath"
    "global.json: $(if ($globalJsonPath) { $globalJsonPath } else { '(none found from cwd)' })"
    "DOTNET_ROOT: $env:DOTNET_ROOT"
    "dotnet --version (exit $versionExit):"
    $sdkVersion
    ""
    "dotnet --info (exit $infoExit):"
    (($infoOutput | Out-String).TrimEnd())
) -join [Environment]::NewLine
Set-Content -Path $sdkLog -Value $sdkText -Encoding UTF8
if ($versionExit -ne 0 -or $infoExit -ne 0 -or [string]::IsNullOrWhiteSpace($sdkVersion)) {
    Write-Host "BLOCKED: dotnet SDK diagnostic failed (--version exit $versionExit, --info exit $infoExit); see $sdkLog"
    Write-Host $sdkText
    exit 2
}

Write-Step "dotnet     : $DotnetPath (SDK $sdkVersion, cwd $cwd)"
Write-Step "manifest   : $ManifestPath"
Write-Step "solution   : $solution"
Write-Step "config     : $Configuration"
Write-Step "runner mode: $RunnerMode"
Write-Step "logs       : $LogDir"

$anyFail = $false

# --- 1. project inventory (manifest + discovery) -------------------------------
$projects = @{}

foreach ($entry in $manifest.projects) {
    $projects[($entry.path -replace '/', '\')] = @{
        Path      = ($entry.path -replace '/', '\')
        Kind      = $entry.kind
        Required  = [bool]$entry.required
        Source    = 'manifest'
        Comment   = $entry.comment
    }
}

$discoveredAny = $false
if (-not $NoProjectDiscovery) {
    foreach ($pattern in @('dotnet\tests\*\*.Tests\*.Tests.csproj', 'dotnet\src\*\*.Tests\*.Tests.csproj', 'dotnet\tests\*\*.csproj')) {
        foreach ($found in (Get-ChildItem -Path (Join-Path $root $pattern) -File -ErrorAction SilentlyContinue)) {
            $rel = $found.FullName.Substring($root.Length + 1)
            if ($projects.ContainsKey($rel)) { continue }
            $isTest = $found.BaseName -like '*.Tests'
            $projects[$rel] = @{
                Path     = $rel
                Kind     = $(if ($isTest) { 'test' } else { 'helper' })
                Required = $false
                Source   = 'discovered'
                Comment  = $(if ($isTest) { 'auto-discovered *.Tests project' } else { 'auto-discovered non-test project under dotnet/tests; treated as build-only helper' })
            }
            $discoveredAny = $true
        }
    }
}

# Solution membership (reported; required rows must be scheduled in the sln).
# Project paths inside the .sln are relative to the solution file (dotnet\), while
# manifest paths are relative to the repository root - normalize before comparing.
$slnText = Get-Content $solution -Raw
$slnMembers = @()
foreach ($match in [regex]::Matches($slnText, 'Project\("\{[A-F0-9-]+\}"\)\s*=\s*"[^"]+",\s*"([^"]+\.csproj)"')) {
    $slnMembers += (Join-Path 'dotnet' ($match.Groups[1].Value -replace '/', '\'))
}

$ordered = @($projects.Values | Sort-Object Path)

Write-Step ("projects  : {0} total ({1} manifest, {2} discovered)" -f $ordered.Count, @($manifest.projects).Count, @($ordered | Where-Object { $_.Source -eq 'discovered' }).Count)

foreach ($project in $ordered) {
    $project.Name = [IO.Path]::GetFileNameWithoutExtension($project.Path)
    $exists = Test-Path (Resolve-UnderRoot $root $project.Path)
    $project.Exists = $exists
    $project.InSln = @($slnMembers | Where-Object { $_ -ieq $project.Path }).Count -gt 0

    if (-not $exists) {
        $project.Result = 'FAIL'
        $project.Detail = 'required project missing from disk'
        $anyFail = $true
    }
    elseif ($project.Required -and -not $project.InSln) {
        $project.Result = 'FAIL'
        $project.Detail = 'required project is not scheduled in the solution'
        $anyFail = $true
    }
    else {
        $project.Result = 'PENDING'
    }
}

# --- 2. build ------------------------------------------------------------------
$slnBuildLog = Join-Path $LogDir 'sln-build.log'
Write-Step "building solution..."
$slnExit = Invoke-Captured -LogPath $slnBuildLog -Command { & $DotnetPath build $solution -c $Configuration --nologo }
$buildOk = ($slnExit -eq 0)
if ($buildOk) {
    Write-Step ("solution build: PASS (exit {0})" -f $slnExit)
}
else {
    Write-Step ("solution build: FAIL (exit {0}); per-project builds follow for attribution" -f $slnExit)
    $anyFail = $true
}

foreach ($project in $ordered) {
    if ($project.Result -eq 'FAIL') { continue }
    if ($project.InSln -and $buildOk) {
        # Genuinely covered by the solution build in this run.
        $project.Built = $true
        $project.BuildLog = $slnBuildLog
        continue
    }
    # Not scheduled in the sln (discovered test or helper), or the solution build
    # failed. Build this project alone so `Built` always reflects a real build of
    # the current source - never a leftover binary from an earlier run.
    $csproj = Resolve-UnderRoot $root $project.Path
    $project.BuildLog = Join-Path $LogDir ("build-{0}.log" -f $project.Name)
    $exit = Invoke-Captured -LogPath $project.BuildLog -Command { & $DotnetPath build $csproj -c $Configuration --nologo }
    $project.Built = ($exit -eq 0)
    if (-not $project.Built) {
        $project.Result = 'FAIL'
        $project.Detail = "project build failed (exit $exit); stale build outputs were not used"
        $anyFail = $true
    }
    elseif (-not $project.InSln) {
        $project.BuildNote = 'built individually: not scheduled in the sln'
    }
}

# --- 3. per-project test/helper execution --------------------------------------
foreach ($project in $ordered) {
    if ($project.Result -eq 'FAIL') { continue }
    $name = [IO.Path]::GetFileNameWithoutExtension($project.Path)
    $project.Name = $name
    $csproj = Resolve-UnderRoot $root $project.Path

    if ($project.Kind -ne 'test') {
        if ($project.Built) {
            $project.Result = 'PASS'
            $project.Detail = 'console helper: build verified only, contributes no test cases'
        }
        else {
            $project.Result = 'NOT_RUN'
            $project.Detail = 'helper not built; no test run applies'
            $anyFail = $true
        }
        continue
    }

    if (-not $project.Built) {
        $project.Result = 'FAIL'
        $project.Detail = 'test project not built; tests NOT_RUN'
        $anyFail = $true
        continue
    }

    $tfm = Get-CsprojProperty -CsprojPath $csproj -Name 'TargetFramework'
    if (-not $tfm) { $tfm = 'netstandard2.1' } # dotnet/Directory.Build.props default
    $assemblyName = Get-CsprojProperty -CsprojPath $csproj -Name 'AssemblyName'
    if (-not $assemblyName) { $assemblyName = $name }
    $testDll = Resolve-UnderRoot $root (Join-Path (Split-Path $project.Path -Parent) "bin\$Configuration\$tfm\$assemblyName.dll")

    $project.Runner = 'none'
    $project.Total = 0
    $project.Failed = 0
    $project.Skipped = 0

    if ($RunnerMode -ne 'direct') {
        $project.DotnetTestLog = Join-Path $LogDir ("dotnetTest-{0}.log" -f $name)
        $projectDir = Resolve-UnderRoot $root (Split-Path $project.Path -Parent)
        $testResultsDirs = @(Get-TestResultsCandidates -ProjectDir $projectDir -Configuration $Configuration -Tfm $tfm)
        $purgedDirs = Clear-TestResultsDirs -Dirs $testResultsDirs
        Write-Step "dotnet test : $name"
        $dotnetTestCommand = "$DotnetPath test $csproj -c $Configuration --nologo"
        $exit = Invoke-Captured -LogPath $project.DotnetTestLog -Command { & $DotnetPath test $csproj -c $Configuration --nologo }
        $collected = @(Save-TestResults -ProjectName $name -Dirs $testResultsDirs)
        $summary = Get-TestSummaryFromVstest $project.DotnetTestLog
        Write-RunMetaFile -Name $name -ProjectPath $project.Path -Kind $project.Kind -Mode $RunnerMode `
            -RunnerTag 'dotnetTest' -Runner 'dotnet test' -Command $dotnetTestCommand -Cwd $cwd -ExitCode $exit `
            -Total $(if ($summary) { $summary.Total } else { '(no parseable summary)' }) `
            -Failed $(if ($summary) { $summary.Failed } else { '-' }) `
            -Skipped $(if ($summary) { $summary.Skipped } else { '-' }) `
            -Collected $collected -PurgedDirs $purgedDirs -Head $headInfo
        if ($summary) {
            $project.Runner = 'dotnet test'
            $project.Total = $summary.Total
            $project.Failed = $summary.Failed
            $project.Skipped = $summary.Skipped
            $project.ExitCode = $exit
        }
        else {
            # No parseable summary: inspect the log before any fallback. A build
            # or restore error fails the project even if a previous run left a
            # valid DLL in bin - the fallback path must never resurrect stale
            # outputs as a green result.
            $buildErrors = @()
            if ($project.DotnetTestLog -and (Test-Path $project.DotnetTestLog)) {
                $buildErrors = @(Select-String -Path $project.DotnetTestLog -Pattern 'error [A-Z]+[0-9]+' -ErrorAction SilentlyContinue)
            }
            if ($buildErrors.Count -gt 0) {
                $project.Result = 'FAIL'
                $project.Detail = "dotnet test hit build/restore errors (first: $($buildErrors[0].Line.Trim())); stale outputs were not used; see $($project.DotnetTestLog)"
                $anyFail = $true
                continue
            }
            $project.FallbackReason =
                "dotnet test produced no parseable test summary (exit $exit) and the log shows no build errors; " +
                "see $($project.DotnetTestLog). The current source built OK in this run, so this is treated as a " +
                "runner/environment issue and the official in-process runner of the same test assembly decides the result."
        }
    }

    if ($project.Runner -eq 'none' -and $RunnerMode -ne 'dotnetTest') {
        # Defensive guard: the in-process runner may only ever see an assembly the
        # current source produced in this run (unreachable via the normal flow
        # because not-built projects are failed in the build phase above).
        if (-not $project.Built) {
            $project.Result = 'FAIL'
            $project.Detail = 'test project was not built in this run; refusing to execute possibly stale outputs'
            $anyFail = $true
            continue
        }
        if (-not (Test-Path $testDll)) {
            $project.Result = 'BLOCKED'
            $project.Detail = "built test assembly not found: $testDll"
            $anyFail = $true
            continue
        }
        $project.DirectLog = Join-Path $LogDir ("direct-{0}.log" -f $name)
        $directProjectDir = Resolve-UnderRoot $root (Split-Path $project.Path -Parent)
        $directResultsDirs = @(Get-TestResultsCandidates -ProjectDir $directProjectDir -Configuration $Configuration -Tfm $tfm)
        $directPurgedDirs = Clear-TestResultsDirs -Dirs $directResultsDirs
        Write-Step "direct run  : $name"
        $directRunnerArgs = @($manifest.testRunner.directRunnerArgs) -join ' '
        $directCommand = "$DotnetPath $testDll $directRunnerArgs"
        $exit = Invoke-Captured -LogPath $project.DirectLog -Command { & $DotnetPath $testDll @($manifest.testRunner.directRunnerArgs) }
        $directCollected = @(Save-TestResults -ProjectName $name -Dirs $directResultsDirs)
        $summary = Get-TestSummaryFromDirectRunner $project.DirectLog
        Write-RunMetaFile -Name $name -ProjectPath $project.Path -Kind $project.Kind -Mode $RunnerMode `
            -RunnerTag 'direct' -Runner 'in-process (direct)' -Command $directCommand -Cwd $cwd -ExitCode $exit `
            -Total $(if ($summary) { $summary.Total } else { '(no parseable summary)' }) `
            -Failed $(if ($summary) { $summary.Failed } else { '-' }) `
            -Skipped $(if ($summary) { $summary.Skipped } else { '-' }) `
            -Collected $directCollected -PurgedDirs $directPurgedDirs -Head $headInfo
        if ($summary) {
            $project.Runner = $(if ($project.Runner -eq 'none') { 'in-process (direct)' } else { $project.Runner })
            $project.Total = $summary.Total
            $project.Failed = $summary.Failed
            $project.Skipped = $summary.Skipped
            $project.ExitCode = $exit
        }
        else {
            $project.Result = 'BLOCKED'
            $project.Detail = "no test summary from either runner (exit $exit); see logs"
            $anyFail = $true
            continue
        }
    }

    if ($project.Total -le 0) {
        $project.Result = 'FAIL'
        $project.Detail = "zero executed test cases (runner: $($project.Runner))"
        $anyFail = $true
    }
    elseif ($project.Failed -gt 0 -or $project.Skipped -gt 0 -or $project.ExitCode -ne 0) {
        $project.Result = 'FAIL'
        $project.Detail = "failed: $($project.Failed), skipped/not-run: $($project.Skipped), exit: $($project.ExitCode)"
        $anyFail = $true
    }
    else {
        $project.Result = 'PASS'
        $project.Detail = "total: $($project.Total)"
    }
}

# --- 4. Unity plugin copy check (scratch dir; never touches tracked assets) ----
$pluginResult = 'NOT_RUN'
$pluginDetail = 'skipped by -SkipPluginCopyCheck'
$pluginLog = $null
if (-not $SkipPluginCopyCheck) {
    $buildShared = Join-Path $root ($manifest.pluginCopy.script -replace '/', '\')
    $pluginDir = Join-Path $LogDir 'plugins'
    $pluginLog = Join-Path $LogDir 'plugin-copy.log'
    Write-Step "plugin copy : scratch dir $pluginDir"
    $hostExe = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    $pluginExit = Invoke-Captured -LogPath $pluginLog -Command {
        & $hostExe -NoProfile -ExecutionPolicy Bypass -File $buildShared -Configuration $Configuration -DotnetPath $DotnetPath -PluginOutputDir $pluginDir
    }
    $missing = @()
    foreach ($required in $manifest.pluginCopy.requiredAssemblies) {
        if (-not (Test-Path (Join-Path $pluginDir $required))) { $missing += $required }
    }
    $forbidden = @()
    foreach ($file in (Get-ChildItem -Path $pluginDir -Filter '*.dll' -ErrorAction SilentlyContinue)) {
        foreach ($pattern in $manifest.pluginCopy.forbiddenPatterns) {
            if ($file.Name -like $pattern) { $forbidden += $file.Name; break }
        }
    }
    if ($pluginExit -ne 0) {
        $pluginResult = 'FAIL'
        $pluginDetail = "build-shared.ps1 exited $pluginExit"
        $anyFail = $true
    }
    elseif ($missing.Count -gt 0) {
        $pluginResult = 'FAIL'
        $pluginDetail = "missing required assemblies: $($missing -join ', ')"
        $anyFail = $true
    }
    elseif ($forbidden.Count -gt 0) {
        $pluginResult = 'FAIL'
        $pluginDetail = "forbidden assemblies copied: $($forbidden -join ', ')"
        $anyFail = $true
    }
    else {
        $pluginResult = 'PASS'
        $pluginDetail = "$(@(Get-ChildItem -Path $pluginDir -Filter '*.dll').Count) assemblies in scratch dir; tracked Unity assets untouched"
    }
}

# --- 5. summary ----------------------------------------------------------------
$summaryPath = Join-Path $LogDir 'summary.log'
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("verify-v5 summary  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  config=$Configuration")
$lines.Add("dotnet SDK     : $sdkVersion  path: $DotnetPath  cwd: $cwd  log: $sdkLog")
$ciLines = Get-CiProvenanceLines
$lines.Add("head           : $($headInfo.Head)  branch: $($headInfo.Branch)")
$lines.Add("ci provenance  : $(if ($ciLines.Count) { $ciLines -join ' ' } else { '(none - local run)' })")
$lines.Add("run identity   : run-info.txt; per-invocation command/exit/counts: runs\<project>\<mode>.meta.txt; raw framework logs: test-results\<project>\")
$lines.Add("")
$lines.Add("solution build : $(if ($buildOk) { "PASS (exit $slnExit)" } else { "FAIL (exit $slnExit)" })  log: $slnBuildLog")
$lines.Add("plugin copy    : $pluginResult  ($($pluginDetail))$(if ($pluginLog) { "  log: $pluginLog" })")
$lines.Add("")
$lines.Add(("{0,-34} {1,-7} {2,-4} {3,-6} {4,-19} {5,6} {6,7} {7,8} {8,-8} {9}" -f @('PROJECT','KIND','REQ','INSLN','RUNNER','TOTAL','FAILED','SKIPPED','RESULT','LOG')))
foreach ($project in $ordered) {
    if (-not $project.Name) { $project.Name = [IO.Path]::GetFileNameWithoutExtension($project.Path) }
    $logCol = $(if ($project.DotnetTestLog) { Split-Path $project.DotnetTestLog -Leaf } else { '' })
    if ($project.DirectLog) { $logCol = "$(if ($logCol) { "$logCol + " })$(Split-Path $project.DirectLog -Leaf)" }
    if (-not $logCol -and $project.BuildLog) { $logCol = Split-Path $project.BuildLog -Leaf }
    $rowArgs = @(
        $project.Name,
        $project.Kind,
        $(if ($project.Required) { 'yes' } else { '-' }),
        $(if ($project.InSln) { 'yes' } else { 'NO!' }),
        "$($project.Runner)",
        "$($project.Total)",
        "$($project.Failed)",
        "$($project.Skipped)",
        "$($project.Result)",
        "$logCol"
    )
    $lines.Add(("{0,-34} {1,-7} {2,-4} {3,-6} {4,-19} {5,6} {6,7} {7,8} {8,-8} {9}" -f $rowArgs))
    if ($project.FallbackReason) {
        $lines.Add("    fallback : $($project.FallbackReason)")
    }
    if ($project.Detail) {
        $lines.Add("    detail   : $($project.Detail)")
    }
    if ($project.BuildNote) {
        $lines.Add("    build    : $($project.BuildNote)")
    }
    if ($project.Comment) {
        $lines.Add("    note     : $($project.Comment)")
    }
}
$lines.Add("")
$overall = $(if ($anyFail) { 'FAIL' } else { 'PASS' })
$passedCount = @($ordered | Where-Object { $_.Result -eq 'PASS' }).Count
$failedCount = @($ordered | Where-Object { $_.Result -eq 'FAIL' }).Count
$blockedCount = @($ordered | Where-Object { $_.Result -eq 'BLOCKED' }).Count
$notRunCount = @($ordered | Where-Object { $_.Result -eq 'NOT_RUN' }).Count
$lines.Add("OVERALL: $overall  (project rows: $passedCount PASS, $failedCount FAIL, $blockedCount BLOCKED, $notRunCount NOT_RUN)")
$lines | Set-Content -Path $summaryPath -Encoding UTF8
Write-Host ""
$lines | ForEach-Object { Write-Host $_ }

exit $(if ($anyFail) { 1 } else { 0 })
