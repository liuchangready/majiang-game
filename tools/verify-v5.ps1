<#
.SYNOPSIS
    Single unified verification entry for the V5 .NET tree.

.DESCRIPTION
    Reads tools\v5-verify.manifest.json, builds dotnet\Mahjong.sln, then runs every
    test project it knows about - the manifest entries plus any *.Tests project
    auto-discovered under dotnet/tests or dotnet/src (so newly merged test projects
    are verified without editing this script or the manifest).

    Guarantees:
      - the accepted standalone companion golden-fixture verifier
        (tools\verify-v5-companion-fixtures.ps1) is ALWAYS executed by the default
        local/CI entry over the four accepted fixtures. It runs the full CLI
        (byte counts, raw LF/CRLF SHA-256, strict UTF-8/JSON, schema/fixture
        identity, provenance pins, case identity and structure). Missing script,
        missing/zero fixtures, blocked tools, a crash/timeout, or any non-zero
        native exit fails the whole run even when every .NET project row passed.
        The raw command, native exit, raw stdout/stderr log and the four
        certified fixture identities/hashes are written to summary.log and
        companion-fixtures.meta.txt. Historical freeze-commit blob checks are
        NOT_RUN by default (shallow CI has no full history); current fixture
        bytes and embedded provenance are still verified offline.
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
      - every real `dotnet test` invocation passes the locked xunit.v3 4.0.2 /
        Microsoft.Testing.Platform native report switches through `dotnet test`
        (`-- --report-xunit-xml --report-xunit-xml-filename
        dotnet-test-results.xml --results-directory <unique dir>`), so the
        per-case report is produced by the test application in THIS invocation,
        not from console output and not by a direct run renamed as dotnetTest.
        The unique report is parsed and gated exactly like the direct XML
        (schema, exact assembly file identity, one assembly, collection/case
        totals, unique native test IDs, zero failed/errors/skipped/not-run,
        non-zero case count, fresh file inside this run/project's results
        directory, native exit 0 and agreement with the console summary).
        Missing/corrupt/stale/out-of-run/wrong-assembly XML, a non-zero native
        exit with a green XML, or a console/native mismatch fails the row and
        the run; dotnetTest-only never falls back to direct. If `dotnet test`
        cannot produce a parseable summary AND no native report exists, the log
        is checked for build/restore errors first: any compile error fails the
        project even when an older DLL is sitting in bin. Only then may `auto`
        mode fall back to the official in-process runner of the same test
        assembly; the meta/summary name the actual runner and never call that
        fallback dotnetTest evidence.
      - each real in-process direct invocation writes the runner's native
        per-case result XML (-result-xml) to
        <log dir>\direct-xml\<project>\direct-results.xml, a path unique to this
        run/project/mode that is cleared immediately before the invocation. The
        XML is parsed and structurally checked (schema, exact assembly identity,
        collection aggregates and test case nodes) and must agree with the raw
        console summary and the native exit: missing, malformed, empty,
        truncated, stale, wrong-assembly or count-mismatched XML fails the row
        and the run, and a console summary can never substitute for it. The
        exact XML path and SHA-256 are recorded in the meta/summary, so CI's
        artifacts/verify-v5/** upload contains the evidence. The raw framework
        failure logs/meta provenance are retained unchanged; dotnetTest-only
        mode never claims a direct XML run. The two runners use distinct
        evidence roots (<log dir>\dotnetTest-xml vs <log dir>\direct-xml) and
        are never cross-labelled.
      - every test execution is attributable: run-info.txt records the exact head
        SHA / branch / CI run identity, and runs\<project>\<mode>.meta.txt records
        the exact command line, working directory, exit code (or the fact that
        the invocation threw - never rendered as 0), parsed counts, the collected
        framework-log list and the purge status of each dotnetTest/direct
        invocation; a runner invocation that throws still persists every line of
        console output produced before the throw plus the exception itself into
        its captured log
      - raw test-framework failure logs (xunit.v3 / MTP TestResults, e.g.
        bin\<cfg>\<tfm>\TestResults\<asm>_<tfm>_<arch>.log - the file that names
        the failing case) are collected under
        <log dir>\test-results\<project>\<mode>\<path relative to the project>,
        so the two invocations of an auto run (dotnetTest and fallback direct)
        and same-named logs from different source roots can never overwrite each
        other. Collection is run-exclusive: the project's TestResults
        directories are removed immediately before each runner invocation, and
        only provably current files are collected from a directory that could
        not be purged: a file survives collection exclusion when it matches the
        before-purge snapshot by unchanged readable hashes; a file whose hash
        was unreadable at snapshot time (e.g. an exclusively locked file whose
        lock was released later) is excluded conservatively - a missing hash is
        never treated as evidence of a content change - and when the snapshot
        enumeration itself failed, the ENTIRE directory is excluded. Every
        exclusion is recorded in the meta file and collection-errors.log
        instead of posing as current-run evidence. Collection is best-effort
        evidence and never changes a gate result or exit code
      - the Unity plugin copy is exercised into a scratch directory under the log
        dir; tracked plugin DLLs and ProjectSettings are never overwritten

    Local usage:
      powershell -ExecutionPolicy Bypass -File tools\verify-v5.ps1 [-Configuration Release]
                 [-DotnetPath D:\Unity\dotnet\dotnet.exe] [-RunnerMode auto|dotnetTest|direct]
                 [-ManifestPath <path>] [-LogDir <path>] [-SkipPluginCopyCheck]
                 [-NoProjectDiscovery] [-CompanionVerifierPath <path>]
                 [-CompanionFixtureDir <path>] [-CompanionTimeoutSeconds <1..3600>]

    -NoProjectDiscovery disables auto-discovery of *.Tests projects under
    dotnet\tests|src; only manifest projects are verified. Used by the isolated
    diagnostics self-test (tools\tests\VerifyV5Diagnostics.Tests.ps1) to verify a
    throwaway fixture project without touching the production tree.

    -CompanionVerifierPath / -CompanionFixtureDir are path overrides for the
    isolated companion-integration self-test only. They are NOT skip switches:
    the companion gate always executes and still demands the full 4/4 accepted
    certification. Production/CI runs must keep the defaults.

    Exit codes: 0 = every row PASS and the companion gate PASS; 1 = at least one
    row FAIL or the companion gate FAIL; 2 = environment blocked (manifest/
    solution/dotnet missing, or the companion verifier reports BLOCKED).
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

    [switch]$NoProjectDiscovery,

    # Path overrides for the isolated companion-integration self-test only.
    # Defaults are the production verifier and accepted fixture directory.
    [string]$CompanionVerifierPath,

    [string]$CompanionFixtureDir,

    [ValidateRange(1, 3600)]
    [int]$CompanionTimeoutSeconds = 600
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
# The dotnet test application resolves --results-directory against its own
# working directory (the test bin folder), not the verifier's cwd. A relative
# LogDir would therefore put the native report somewhere else. Normalize once
# here so every evidence path, gate containment check and meta record is
# absolute and identical to the path handed to the runner.
$LogDir = [IO.Path]::GetFullPath($LogDir)

function Write-Step([string]$message) { Write-Host "[verify-v5] $message" }

function Format-ExitCode([object]$Exit) {
    # A missing exit code means the invocation threw - it must never render as
    # (or be mistaken for) a successful 0.
    if ($null -eq $Exit) { 'EXCEPTION' }
    else { "$Exit" }
}

function Resolve-DotnetPath {
    if ($DotnetPath) { return $DotnetPath }
    if ($env:DOTNET_ROOT -and (Test-Path (Join-Path $env:DOTNET_ROOT 'dotnet.exe'))) {
        return (Join-Path $env:DOTNET_ROOT 'dotnet.exe')
    }
    if (Test-Path 'D:\Unity\dotnet\dotnet.exe') { return 'D:\Unity\dotnet\dotnet.exe' }
    try { return (Get-Command dotnet -ErrorAction Stop).Source } catch { return $null }
}

# Runs a native command, tees all output into a log file, returns exit code.
# Output is streamed into the list inside try{}, and the list is flushed to the
# log in finally{}, so whatever the command produced before a throw is preserved.
# On a thrown invocation the function returns $null - callers must treat a
# missing exit code as a failing outcome (never as 0 / success).
#
# Windows PowerShell 5.1 wraps native stderr in a RemoteException ErrorRecord and
# $ErrorActionPreference='Stop' makes that record terminate the pipeline before
# $LASTEXITCODE can be read (D-CAPTURE-01). The capture boundary therefore:
#   - switches $ErrorActionPreference to 'Continue' for this invocation only, so
#     stderr records are delivered instead of aborting the pipeline;
#   - extracts the verbatim native stderr line from NativeCommandError records,
#     while rethrowing any real PowerShell error record (fail-closed);
#   - clears LASTEXITCODE before invoking and only accepts a code produced by
#     this invocation, so a stale value or a command that never ran is never
#     reported as success;
#   - clears/reads/restores LASTEXITCODE through the script and global scopes,
#     never through the unqualified name: a caller function's local
#     $LASTEXITCODE would otherwise shadow the real native value (D-CAPTURE-01-
#     SCOPE). If the two scopes disagree after the call, the outcome is treated
#     as uncertain instead of picking one;
#   - restores $ErrorActionPreference and the caller's script/global
#     LASTEXITCODE existence and value in finally. Caller-local values are never
#     modified; a variable that did not exist before the call is removed, not
#     left behind as a new null variable.
# PowerShell 7.3+ has an opt-in native-command error preference; it is disabled
# locally when the current host defines it, so stderr stays a diagnostic here.
function Invoke-Captured {
    param(
        [string]$LogPath,
        [scriptblock]$Command
    )
    $output = New-Object System.Collections.Generic.List[string]
    $exit = $null
    $previousErrorActionPreference = $ErrorActionPreference
    $scriptExitVariable = Get-Variable -Name 'LASTEXITCODE' -Scope Script -ErrorAction SilentlyContinue
    $scriptExitExisted = ($null -ne $scriptExitVariable)
    $previousScriptExitCode = $(if ($scriptExitExisted) { $scriptExitVariable.Value } else { $null })
    $globalExitVariable = Get-Variable -Name 'LASTEXITCODE' -Scope Global -ErrorAction SilentlyContinue
    $globalExitExisted = ($null -ne $globalExitVariable)
    $previousGlobalExitCode = $(if ($globalExitExisted) { $globalExitVariable.Value } else { $null })
    $nativePreferenceVariable = Get-Variable -Name 'PSNativeCommandUseErrorActionPreference' -ErrorAction SilentlyContinue
    $hadNativePreference = ($null -ne $nativePreferenceVariable)
    $previousNativePreference = $(if ($hadNativePreference) { $nativePreferenceVariable.Value } else { $null })
    $producedExitCode = $false
    try {
        $logDir = Split-Path $LogPath -Parent
        if ($logDir -and -not (Test-Path -LiteralPath $logDir)) {
            New-Item -ItemType Directory -Force -Path $logDir | Out-Null
        }

        $ErrorActionPreference = 'Continue'
        if ($hadNativePreference) {
            $PSNativeCommandUseErrorActionPreference = $false
        }
        # Remove instead of assigning $null: a variable that did not exist before
        # this call must not be left behind as a new null variable. Restoration
        # below is conditional on the saved existence, so an absent variable is
        # removed again rather than recreated.
        Remove-Variable -Name 'LASTEXITCODE' -Scope Script -ErrorAction SilentlyContinue
        Remove-Variable -Name 'LASTEXITCODE' -Scope Global -ErrorAction SilentlyContinue

        & $Command 2>&1 | ForEach-Object {
            if ($_ -is [System.Management.Automation.ErrorRecord]) {
                $errorId = "$($_.FullyQualifiedErrorId)"
                if ($errorId -like 'NativeCommandError*') {
                    # Native stderr: keep the diagnostic verbatim, never render
                    # PowerShell's "program : ..." wrapper or CategoryInfo noise.
                    $output.Add([string]$_.Exception.Message)
                }
                else {
                    # A real PowerShell error (not native stderr): preserve the
                    # previous fail-closed behavior.
                    throw $_
                }
            }
            else {
                $output.Add([string]$_)
            }
        }

        # Read the scopes native commands actually update. An unqualified
        # $LASTEXITCODE here could be a caller-local value and would mask both
        # a real exit code and the absence of a native command.
        $scriptExit = $script:LASTEXITCODE
        $globalExit = $global:LASTEXITCODE
        $nativeExit = $null
        $exitConflict = $false
        if ($null -ne $scriptExit -and $null -ne $globalExit) {
            if ("$scriptExit" -eq "$globalExit") { $nativeExit = $scriptExit }
            else { $exitConflict = $true }
        }
        elseif ($null -ne $scriptExit) { $nativeExit = $scriptExit }
        elseif ($null -ne $globalExit) { $nativeExit = $globalExit }

        if ($exitConflict) {
            $output.Add("[verify-v5] invocation produced conflicting script/global LASTEXITCODE values ('$scriptExit' vs '$globalExit'); outcome is uncertain and treated as a failure")
        }
        elseif ($null -ne $nativeExit -and "$nativeExit" -match '^-?\d+$') {
            $exit = [int]$nativeExit
            $producedExitCode = $true
        }
        else {
            $output.Add('[verify-v5] invocation produced no native exit code; outcome is uncertain and treated as a failure')
        }
    }
    catch {
        $output.Add("[verify-v5] invocation threw an exception: $($_.Exception.GetType().FullName): $($_.Exception.Message)")
        if ($_.ScriptStackTrace) { $output.Add($_.ScriptStackTrace) }
        $exit = $null
        $producedExitCode = $false
    }
    finally {
        if ($producedExitCode -and $null -ne $exit) {
            # Evidence only; the caller receives $exit explicitly. The automatic
            # variable itself is restored below so this helper never leaks state.
            $output.Add("[verify-v5] native exit code: $exit")
        }
        $logDir = Split-Path $LogPath -Parent
        if ($logDir -and -not (Test-Path -LiteralPath $logDir)) {
            try { New-Item -ItemType Directory -Force -Path $logDir | Out-Null } catch { }
        }
        try { ($output | Out-String) | Set-Content -Path $LogPath -Encoding UTF8 }
        catch { Write-Warning "[verify-v5] could not write captured log ${LogPath}: $($_.Exception.Message)" }
        $ErrorActionPreference = $previousErrorActionPreference
        if ($hadNativePreference) {
            $PSNativeCommandUseErrorActionPreference = $previousNativePreference
        }
        else {
            Remove-Variable -Name 'PSNativeCommandUseErrorActionPreference' -ErrorAction SilentlyContinue
        }
        if ($scriptExitExisted) {
            $script:LASTEXITCODE = $previousScriptExitCode
        }
        else {
            Remove-Variable -Name 'LASTEXITCODE' -Scope Script -ErrorAction SilentlyContinue
        }
        if ($globalExitExisted) {
            $global:LASTEXITCODE = $previousGlobalExitCode
        }
        else {
            Remove-Variable -Name 'LASTEXITCODE' -Scope Global -ErrorAction SilentlyContinue
        }
    }
    return $exit
}

function ConvertTo-NativeArgument([string]$Value) {
    # Child process argument strings are space-joined. Paths containing
    # whitespace must be quoted so the child PowerShell sees one -File argument.
    if ($null -eq $Value) { return '""' }
    if ($Value -notmatch '[\s"]') { return $Value }
    return '"' + ($Value -replace '"', '\"') + '"'
}

function Invoke-CompanionVerifier {
    # Runs the accepted standalone verifier in a hidden, bounded child process.
    # System.Diagnostics.Process is used directly (not Start-Process -PassThru):
    # under Windows PowerShell 5.1 the latter can return a null ExitCode for a
    # process whose output was redirected, which would turn a real PASS into a
    # false failure. Stdout/stderr are read asynchronously to avoid pipe
    # deadlocks, then persisted verbatim. A timeout or spawn failure yields a
    # null exit code and is never treated as 0.
    param(
        [string]$VerifierPath,
        [string]$FixtureDir,
        [string]$LogPath,
        [int]$TimeoutSeconds
    )
    $hostExe = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    $arguments = @(
        '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
        '-File', $VerifierPath,
        '-RepoRoot', $root,
        '-FixtureDir', $FixtureDir
    )
    $commandText = (@($hostExe) + $arguments | ForEach-Object { ConvertTo-NativeArgument $_ }) -join ' '
    $argumentText = ($arguments | ForEach-Object { ConvertTo-NativeArgument $_ }) -join ' '
    $stdoutPath = "$LogPath.stdout.log"
    $stderrPath = "$LogPath.stderr.log"
    $exit = $null
    $timedOut = $false
    $threw = $null
    $process = $null
    $stdout = ''
    $stderr = ''
    try {
        $startInfo = New-Object System.Diagnostics.ProcessStartInfo
        $startInfo.FileName = $hostExe
        $startInfo.Arguments = $argumentText
        $startInfo.WorkingDirectory = $cwd
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $process = New-Object System.Diagnostics.Process
        $process.StartInfo = $startInfo
        if (-not $process.Start()) { throw 'Process.Start() returned false' }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $timedOut = $true
            try { $process.Kill() } catch { }
        }
        # Drain both pipes with a bounded grace period. After a timeout the kill
        # is expected to close them immediately; this guard keeps a stuck pipe
        # from defeating the timeout.
        try { if ($stdoutTask.Wait(10000)) { $stdout = $stdoutTask.Result } } catch { }
        try { if ($stderrTask.Wait(10000)) { $stderr = $stderrTask.Result } } catch { }
        $null = $process.WaitForExit(10000)
        if (-not $timedOut) { $exit = $process.ExitCode }
    }
    catch {
        $threw = "$($_.Exception.GetType().FullName): $($_.Exception.Message)"
    }
    finally {
        if ($null -ne $process) {
            try { $process.Dispose() } catch { }
        }
    }
    try { Set-Content -Path $stdoutPath -Value $stdout -Encoding UTF8 } catch { }
    try { Set-Content -Path $stderrPath -Value $stderr -Encoding UTF8 } catch { }
    $exitLabel = $(if ($null -ne $exit) { "$exit" } else { 'EXCEPTION (spawn/timeout failure; never treated as 0)' })
    $logText = @(
        "# companion verifier raw invocation log"
        "# command : $commandText"
        "# cwd     : $cwd"
        "# timeout : $TimeoutSeconds s"
        "# exit    : $exitLabel"
        "# timedout: $timedOut"
        "# error   : $(if ($threw) { $threw } else { '(none)' })"
        ""
        "===== stdout ====="
        $stdout
        "===== stderr ====="
        $stderr
    ) -join [Environment]::NewLine
    try { Set-Content -Path $LogPath -Value $logText -Encoding UTF8 } catch { Write-Warning "[verify-v5] could not write companion raw log ${LogPath}: $($_.Exception.Message)" }
    return [pscustomobject]@{
        ExitCode = $exit
        TimedOut = $timedOut
        Threw    = $threw
        Command  = $commandText
        LogPath  = $LogPath
        Stdout   = $stdout
        Stderr   = $stderr
    }
}

function Get-CompanionFixtureCertification {
    # Parses the accepted verifier's own PASS certification. This is NOT a
    # replacement validator: the standalone verifier already performed the full
    # byte/JSON/structure validation and produced the exit code. The unified
    # entry only refuses an exit-0 run whose output does not actually certify
    # all four expected fixtures (e.g. a truncated/fake/zero-sample script).
    param([string]$Text)
    $expected = @('movement-v1.json', 'shuffle-reversed-v1.json', 'visibility-v1.json', 'ai-discard-flags-v1.json')
    $pattern = '^\s*-\s+(?<file>[A-Za-z0-9._-]+\.json)\s+\((?<kind>schema|fixture):\s*(?<identity>[^,]+),\s*(?<cases>\d+)\s+cases,\s*SHA-256 LF:\s*(?<sha>[0-9A-Fa-f]{64})\)\s*$'
    $entries = @()
    foreach ($line in @($Text -split "`r?`n")) {
        $m = [regex]::Match($line, $pattern)
        if ($m.Success) {
            $entries += [pscustomobject]@{
                File     = $m.Groups['file'].Value
                Kind     = $m.Groups['kind'].Value
                Identity = $m.Groups['identity'].Value.Trim()
                Cases    = [int]$m.Groups['cases'].Value
                Sha256   = $m.Groups['sha'].Value.ToUpperInvariant()
            }
        }
    }
    $byFile = @{}
    foreach ($entry in $entries) {
        if (-not $byFile.ContainsKey($entry.File)) { $byFile[$entry.File] = $entry }
    }
    $missing = @($expected | Where-Object { -not $byFile.ContainsKey($_) })
    $duplicate = ($entries.Count -ne $byFile.Count)
    $passMarker = ($Text -match '(?m)^PASS: 4/4 companion golden fixtures verified successfully:\s*$')
    return [pscustomobject]@{
        Entries   = $entries
        ByFile    = $byFile
        Missing   = $missing
        Duplicate = $duplicate
        Complete  = ($passMarker -and $missing.Count -eq 0 -and $byFile.Count -eq 4 -and -not $duplicate)
    }
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

function Get-DotnetTestResultsArgument([string]$Path) {
    # `dotnet test` passes MTP options through the MSBuild bridge as a
    # space-joined argument string. A results-directory containing whitespace is
    # split into several arguments and the test app exits before writing a
    # report. On Windows use the directory's short (8.3) alias ONLY for the
    # command-line argument; the real (long) path is still what the evidence
    # gate checks and what the meta/summary records. If no space-free alias is
    # available the original path is returned and the invocation fails closed
    # (no report -> no PASS).
    if ($Path -notmatch '\s') { return $Path }
    if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) { return $Path }
    try {
        $command = 'for %I in ("' + $Path + '") do @echo %~sI'
        $cmd = $(if ($env:ComSpec) { $env:ComSpec } else { 'cmd.exe' })
        $short = (& $cmd /d /c $command 2>$null | Out-String).Trim()
        if ($short -and (Test-Path -LiteralPath $short) -and ($short -notmatch '\s')) {
            return $short
        }
    }
    catch { }
    return $Path
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

function Get-DotnetTestConsoleCounts([string]$logPath) {
    # `dotnet test` on this locked runner (xunit.v3 4.0.2 + Microsoft.Testing.Platform
    # 2.5.1) prints the VSTest-style final line through the MTP MSBuild bridge:
    #   Passed! - Failed: 0, Passed: 180, Skipped: 0, Total: 180, Duration: ...
    # The SDK UI language is environment-controlled, so the zh-CN form is also
    # accepted. This console summary is only a CROSS-CHECK: the native per-case
    # XML remains authoritative. A missing/unparseable line is never treated as
    # zero tests; the caller fails closed.
    $patterns = @(
        'Failed:\s*(?<failed>\d+)\s*[,\uFF0C]\s*Passed:\s*(?<passed>\d+)\s*[,\uFF0C]\s*Skipped:\s*(?<skipped>\d+)\s*[,\uFF0C]\s*Total:\s*(?<total>\d+)',
        '\u5931\u8D25[:\uFF1A]\s*(?<failed>\d+)\s*[,\uFF0C]\s*\u901A\u8FC7[:\uFF1A]\s*(?<passed>\d+)\s*[,\uFF0C]\s*\u5DF2\u8DF3\u8FC7[:\uFF1A]\s*(?<skipped>\d+)\s*[,\uFF0C]\s*\u603B\u8BA1[:\uFF1A]\s*(?<total>\d+)'
    )
    foreach ($pattern in $patterns) {
        $line = Select-String -Path $logPath -Pattern $pattern -ErrorAction SilentlyContinue | Select-Object -Last 1
        if ($line) {
            return [ordered]@{
                Total   = [int]$line.Matches[0].Groups['total'].Value
                Passed  = [int]$line.Matches[0].Groups['passed'].Value
                Failed  = [int]$line.Matches[0].Groups['failed'].Value
                Skipped = [int]$line.Matches[0].Groups['skipped'].Value
            }
        }
    }

    # Fallback for an MTP-native summary block (no VSTest bridge line). All four
    # fields must be present in the SAME log; a partial block is unparseable.
    $total = Select-String -Path $logPath -Pattern '^\s*total:\s*(\d+)\s*$' -ErrorAction SilentlyContinue | Select-Object -Last 1
    $passed = Select-String -Path $logPath -Pattern '^\s*(?:succeeded|passed):\s*(\d+)\s*$' -ErrorAction SilentlyContinue | Select-Object -Last 1
    $failed = Select-String -Path $logPath -Pattern '^\s*failed:\s*(\d+)\s*$' -ErrorAction SilentlyContinue | Select-Object -Last 1
    $skipped = Select-String -Path $logPath -Pattern '^\s*skipped:\s*(\d+)\s*$' -ErrorAction SilentlyContinue | Select-Object -Last 1
    if ($total -and $passed -and $failed -and $skipped) {
        return [ordered]@{
            Total   = [int]$total.Matches[0].Groups[1].Value
            Passed  = [int]$passed.Matches[0].Groups[1].Value
            Failed  = [int]$failed.Matches[0].Groups[1].Value
            Skipped = [int]$skipped.Matches[0].Groups[1].Value
        }
    }
    return $null
}

function Get-DirectRunnerCounts([string]$logPath) {
    # xunit.v3 / MTP direct summary. The runner omits all-zero suffixes, so both
    # forms must parse:
    #      Mahjong.Domain.Tests  Total: 233, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 3.058s
    #      ProbeZero.Tests  Total: 0
    # Passed is derived from the other four counters because the console summary
    # does not print it. A negative derived value means the line was not a real
    # summary for this assembly and is rejected by the evidence gate.
    $line = Select-String -Path $logPath -Pattern 'Total:\s*(\d+)(?:,\s*Errors:\s*(\d+))?(?:,\s*Failed:\s*(\d+))?(?:,\s*Skipped:\s*(\d+))?(?:,\s*Not Run:\s*(\d+))?' |
        Select-Object -Last 1
    if (-not $line) { return $null }
    $errors = $(if ($line.Matches[0].Groups[2].Success) { [int]$line.Matches[0].Groups[2].Value } else { 0 })
    $failed = $(if ($line.Matches[0].Groups[3].Success) { [int]$line.Matches[0].Groups[3].Value } else { 0 })
    $skipped = $(if ($line.Matches[0].Groups[4].Success) { [int]$line.Matches[0].Groups[4].Value } else { 0 })
    $notRun = $(if ($line.Matches[0].Groups[5].Success) { [int]$line.Matches[0].Groups[5].Value } else { 0 })
    $total = [int]$line.Matches[0].Groups[1].Value
    return [ordered]@{
        Total   = $total
        Passed  = $total - $errors - $failed - $skipped - $notRun
        Failed  = $failed
        Skipped = $skipped
        Errors  = $errors
        NotRun  = $notRun
    }
}

function Get-TestSummaryFromDirectRunner([string]$logPath) {
    $counts = Get-DirectRunnerCounts $logPath
    if ($null -eq $counts) { return $null }
    return @{
        Total   = $counts.Total
        Failed  = $counts.Errors + $counts.Failed
        Skipped = $counts.Skipped + $counts.NotRun
    }
}

function Read-DirectResultXml {
    # Parse and structurally validate the xUnit.net v3 -result-xml document that
    # the direct runner produced for THIS invocation. This is a real framework
    # result file, never a console-derived or hand-written substitute: test case
    # nodes, collection aggregates and assembly totals are all cross-checked.
    # The function only reports structural validity; Test-DirectInvocationEvidence
    # applies the current-run path/freshness, assembly identity, console and
    # native-exit gates.
    param(
        [string]$XmlPath,
        [string]$ExpectedAssemblyName
    )
    $result = [ordered]@{
        Ok              = $false
        Reason          = ''
        AssemblyName    = ''
        TestFramework   = ''
        SchemaVersion   = ''
        TestNodeCount   = 0
        CollectionCount = 0
        ErrorNodeCount  = 0
        Counts          = $null
    }
    try {
        if (-not (Test-Path -LiteralPath $XmlPath)) { throw "result XML is missing: $XmlPath" }
        $settings = New-Object System.Xml.XmlReaderSettings
        $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $reader = [System.Xml.XmlReader]::Create($XmlPath, $settings)
        try {
            $doc = New-Object System.Xml.XmlDocument
            $doc.XmlResolver = $null
            $doc.Load($reader)
        }
        finally { $reader.Dispose() }

        if ($doc.DocumentElement -and $doc.DocumentElement.LocalName -ne 'assemblies') {
            throw "result XML root element is '$($doc.DocumentElement.LocalName)', expected 'assemblies'"
        }
        $root = $doc.DocumentElement
        $schemaVersion = $root.GetAttribute('schema-version')
        if ([string]::IsNullOrWhiteSpace($schemaVersion) -or $schemaVersion -notmatch '^\d+$') {
            throw "result XML has no integer assemblies/@schema-version"
        }
        $result.SchemaVersion = $schemaVersion

        $assemblies = @($root.SelectNodes('./assembly'))
        if ($assemblies.Count -ne 1) { throw "result XML must contain exactly one <assembly> element (found $($assemblies.Count))" }
        $assembly = $assemblies[0]
        $assemblyName = $assembly.GetAttribute('name')
        if ([string]::IsNullOrWhiteSpace($assemblyName)) { throw 'result XML assembly/@name is missing' }
        $result.AssemblyName = $assemblyName
        $actualLeaf = [IO.Path]::GetFileNameWithoutExtension($assemblyName)
        if ([string]::IsNullOrWhiteSpace($ExpectedAssemblyName) -or $actualLeaf -ine $ExpectedAssemblyName) {
            throw "result XML assembly identity mismatch: expected '$ExpectedAssemblyName', got '$assemblyName'"
        }
        $framework = $assembly.GetAttribute('test-framework')
        if ([string]::IsNullOrWhiteSpace($framework) -or $framework -notlike 'xUnit.net v3*') {
            throw "result XML is not an xUnit.net v3 result (test-framework='$framework')"
        }
        $result.TestFramework = $framework

        $required = @('total', 'passed', 'failed', 'skipped', 'errors', 'not-run')
        $attrs = @{}
        foreach ($attribute in $required) {
            if (-not $assembly.HasAttribute($attribute)) { throw "result XML assembly is missing required attribute '$attribute'" }
            $raw = $assembly.GetAttribute($attribute)
            if ($raw -notmatch '^\d+$') { throw "result XML assembly attribute '$attribute' is not a non-negative integer: '$raw'" }
            $attrs[$attribute] = [int]$raw
        }

        $tests = @($assembly.SelectNodes('.//test'))
        $result.TestNodeCount = $tests.Count
        $testIds = @{}
        $nodePassed = 0
        $nodeFailed = 0
        $nodeSkipped = 0
        $nodeNotRun = 0
        foreach ($test in $tests) {
            $id = $test.GetAttribute('id')
            $name = $test.GetAttribute('name')
            $testResult = $test.GetAttribute('result')
            if ([string]::IsNullOrWhiteSpace($id)) { throw 'result XML contains a <test> without @id' }
            if ([string]::IsNullOrWhiteSpace($name)) { throw "result XML test '$id' has no @name" }
            if ($testIds.ContainsKey($id)) { throw "result XML contains duplicate test id '$id'" }
            $testIds[$id] = $true
            switch ($testResult) {
                'Pass' { $nodePassed++ }
                'Fail' { $nodeFailed++ }
                'Skip' { $nodeSkipped++ }
                'NotRun' { $nodeNotRun++ }
                default { throw "result XML test '$name' has unsupported result '$testResult'" }
            }
        }

        $collections = @($assembly.SelectNodes('./collection'))
        $result.CollectionCount = $collections.Count
        if ($tests.Count -gt 0 -and $collections.Count -eq 0) {
            throw 'result XML has test case nodes but no <collection> aggregates'
        }
        $collectionTotal = 0
        foreach ($collection in $collections) {
            $collectionName = $collection.GetAttribute('name')
            if ([string]::IsNullOrWhiteSpace($collectionName)) { throw 'result XML <collection> has no @name' }
            foreach ($attribute in @('total', 'passed', 'failed', 'skipped', 'not-run')) {
                if (-not $collection.HasAttribute($attribute)) {
                    throw "result XML collection '$collectionName' is missing required attribute '$attribute'"
                }
                if ($collection.GetAttribute($attribute) -notmatch '^\d+$') {
                    throw "result XML collection '$collectionName' attribute '$attribute' is not a non-negative integer"
                }
            }
            $childTests = @($collection.SelectNodes('./test'))
            $cp = 0; $cf = 0; $cs = 0; $cn = 0
            foreach ($test in $childTests) {
                switch ($test.GetAttribute('result')) {
                    'Pass' { $cp++ }
                    'Fail' { $cf++ }
                    'Skip' { $cs++ }
                    'NotRun' { $cn++ }
                }
            }
            $ct = [int]$collection.GetAttribute('total')
            if ($ct -ne $childTests.Count) {
                throw "result XML collection '$collectionName' total=$ct but contains $($childTests.Count) test case node(s)"
            }
            if ([int]$collection.GetAttribute('passed') -ne $cp -or
                [int]$collection.GetAttribute('failed') -ne $cf -or
                [int]$collection.GetAttribute('skipped') -ne $cs -or
                [int]$collection.GetAttribute('not-run') -ne $cn) {
                throw "result XML collection '$collectionName' aggregate counts do not match its test case nodes"
            }
            $collectionTotal += $ct
        }
        if ($collectionTotal -ne $tests.Count) {
            throw "result XML collection totals ($collectionTotal) do not match test case nodes ($($tests.Count))"
        }
        if ($attrs['total'] -ne $tests.Count) {
            throw "result XML assembly total=$($attrs['total']) but $($tests.Count) test case node(s) exist"
        }
        if ($attrs['passed'] -ne $nodePassed -or
            $attrs['failed'] -ne $nodeFailed -or
            $attrs['skipped'] -ne $nodeSkipped -or
            $attrs['not-run'] -ne $nodeNotRun) {
            throw "result XML assembly counts (passed=$($attrs['passed']), failed=$($attrs['failed']), skipped=$($attrs['skipped']), not-run=$($attrs['not-run'])) do not match test case nodes (passed=$nodePassed, failed=$nodeFailed, skipped=$nodeSkipped, not-run=$nodeNotRun)"
        }
        if ($attrs['total'] -ne ($attrs['passed'] + $attrs['failed'] + $attrs['skipped'] + $attrs['not-run'])) {
            throw "result XML assembly total=$($attrs['total']) is not the sum of passed/failed/skipped/not-run"
        }

        $errorNodes = @($assembly.SelectNodes('.//errors/error'))
        $result.ErrorNodeCount = $errorNodes.Count
        # Collection-level failures/exceptions are legitimate framework output and
        # must never be dropped just because no <test> node represents them. The
        # evidence gate rejects any non-zero errors below; here we only ensure a
        # positive errors attribute is backed by a real error node (or vice versa).
        if (($attrs['errors'] -gt 0) -ne ($errorNodes.Count -gt 0)) {
            throw "result XML errors=$($attrs['errors']) but $($errorNodes.Count) framework error node(s) exist"
        }

        $result.Counts = [ordered]@{
            Total   = $attrs['total']
            Passed  = $attrs['passed']
            Failed  = $attrs['failed']
            Skipped = $attrs['skipped']
            Errors  = $attrs['errors']
            NotRun  = $attrs['not-run']
        }
        $result.Ok = $true
        $result.Reason = 'structurally valid xUnit.net v3 result XML'
    }
    catch {
        $result.Ok = $false
        $result.Reason = "invalid direct result XML: $($_.Exception.Message)"
    }
    return [pscustomobject]$result
}

function Test-DirectInvocationEvidence {
    # Production decision for one direct runner invocation. It is deliberately
    # strict: the current-run XML must exist under this run's LogDir, be fresh,
    # identify the exact test assembly, agree with the raw console summary and
    # with the native exit, and contain a non-empty all-Pass test case set with
    # zero errors/skips/not-run. A console summary alone can never stand in for
    # XML, and XML can never override a non-zero native exit.
    param(
        [string]$XmlPath,
        [string]$ExpectedAssemblyName,
        [string]$LogDir,
        [datetime]$RunStartedUtc,
        [object]$ConsoleCounts,
        [object]$NativeExit
    )
    $result = [ordered]@{
        Ok            = $false
        Reason        = ''
        XmlCounts     = $null
        TestNodeCount = 0
        AssemblyName  = ''
        XmlSha256     = ''
        XmlLastWriteUtc = $null
    }
    try {
        if ([string]::IsNullOrWhiteSpace($XmlPath)) { throw 'no direct result XML path was assigned' }
        if (-not (Test-Path -LiteralPath $XmlPath)) { throw "direct result XML is missing: $XmlPath" }
        $fullXml = [IO.Path]::GetFullPath($XmlPath)
        $fullLog = [IO.Path]::GetFullPath($LogDir).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if (-not $fullXml.StartsWith($fullLog, [StringComparison]::OrdinalIgnoreCase)) {
            throw "direct result XML is outside this run's LogDir: $fullXml"
        }
        $lastWrite = (Get-Item -LiteralPath $fullXml).LastWriteTimeUtc
        $result.XmlLastWriteUtc = $lastWrite
        if ($lastWrite -lt $RunStartedUtc.AddSeconds(-5)) {
            throw "direct result XML predates this invocation (last write $($lastWrite.ToString('o')) < start $($RunStartedUtc.ToString('o')))"
        }
        try { $result.XmlSha256 = (Get-FileHash -LiteralPath $fullXml -Algorithm SHA256).Hash } catch { }

        $xml = Read-DirectResultXml -XmlPath $fullXml -ExpectedAssemblyName $ExpectedAssemblyName
        $result.TestNodeCount = $xml.TestNodeCount
        $result.AssemblyName = $xml.AssemblyName
        if (-not $xml.Ok) { throw $xml.Reason }
        $result.XmlCounts = $xml.Counts
        $c = $xml.Counts

        if ($null -eq $NativeExit) { throw 'native exit is unknown (invocation threw); never treated as 0' }
        if ([int]$NativeExit -ne 0) { throw "native exit is $NativeExit, not 0" }
        if ($c.Total -le 0) { throw 'result XML contains zero executed test cases' }
        if ($c.Total -ne $xml.TestNodeCount) { throw "result XML total=$($c.Total) but $($xml.TestNodeCount) test case node(s) exist" }
        if ($c.Failed -gt 0 -or $c.Errors -gt 0 -or $c.Skipped -gt 0 -or $c.NotRun -gt 0) {
            throw "result XML is not green: failed=$($c.Failed), errors=$($c.Errors), skipped=$($c.Skipped), not-run=$($c.NotRun)"
        }
        if ($c.Passed -ne $c.Total) { throw "result XML passed=$($c.Passed) does not equal total=$($c.Total)" }
        if ($null -eq $ConsoleCounts) { throw 'raw console summary is missing/unparseable; XML cannot be cross-checked' }
        foreach ($name in @('Total', 'Passed', 'Failed', 'Skipped', 'Errors', 'NotRun')) {
            if ([int]$ConsoleCounts[$name] -ne [int]$c[$name]) {
                throw "result XML $name=$($c[$name]) does not match console summary $name=$($ConsoleCounts[$name])"
            }
        }

        $result.Ok = $true
        $result.Reason = "$($c.Total)/$($c.Total) test cases Pass; XML matches console and native exit 0"
    }
    catch {
        $result.Ok = $false
        $result.Reason = $_.Exception.Message
    }
    return [pscustomobject]$result
}

function Test-DotnetTestInvocationEvidence {
    # Production decision for one real `dotnet test` invocation. The file must
    # be the xunit.v3 native per-case report produced by THIS invocation through
    # the MTP report switches, inside this run/project's dedicated results
    # directory, fresh, for the exact test assembly file, with one assembly and
    # a complete all-Pass case set. The console summary and the native exit are
    # cross-checks, never substitutes. A dotnetTest-only run must call this
    # function and must never fall back to the direct runner on failure.
    param(
        [string]$XmlPath,
        [string]$ExpectedAssemblyName,
        [string]$ExpectedAssemblyPath,
        [string]$ResultsDir,
        [string]$LogDir,
        [datetime]$RunStartedUtc,
        [object]$ConsoleCounts,
        [object]$NativeExit,
        [string]$ExpectedFramework = 'xUnit.net v3 4.0.2',
        [string]$ExpectedSchemaVersion = '3',
        [string]$ExpectedFileName = 'dotnet-test-results.xml'
    )
    $result = [ordered]@{
        Ok              = $false
        Reason          = ''
        XmlCounts       = $null
        TestNodeCount   = 0
        AssemblyName    = ''
        TestFramework   = ''
        XmlSha256       = ''
        XmlLastWriteUtc = $null
    }
    $pathComparison = $(if ([System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT) {
        [StringComparison]::OrdinalIgnoreCase
    } else {
        [StringComparison]::Ordinal
    })
    try {
        if ([string]::IsNullOrWhiteSpace($XmlPath)) { throw 'no dotnet test result XML path was assigned' }
        if ([string]::IsNullOrWhiteSpace($ResultsDir)) { throw 'no dotnet test results directory was assigned' }
        if ([string]::IsNullOrWhiteSpace($LogDir)) { throw 'no run LogDir was assigned' }
        $fullLog = [IO.Path]::GetFullPath($LogDir).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if (-not (Test-Path -LiteralPath $ResultsDir)) { throw "dotnet test results directory is missing: $ResultsDir" }
        $fullResults = [IO.Path]::GetFullPath($ResultsDir).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if (-not $fullResults.StartsWith($fullLog, $pathComparison)) {
            throw "dotnet test results directory is outside this run's LogDir: $fullResults"
        }
        if (-not (Test-Path -LiteralPath $XmlPath)) { throw "dotnet test result XML is missing: $XmlPath" }
        $fullXml = [IO.Path]::GetFullPath($XmlPath)
        if (-not $fullXml.StartsWith($fullResults, $pathComparison)) {
            throw "dotnet test result XML is outside this run/project's results directory: $fullXml"
        }
        if ([IO.Path]::GetFileName($fullXml) -ine $ExpectedFileName) {
            throw "dotnet test result XML has unexpected file name '$([IO.Path]::GetFileName($fullXml))' (expected '$ExpectedFileName')"
        }
        $lastWrite = (Get-Item -LiteralPath $fullXml).LastWriteTimeUtc
        $result.XmlLastWriteUtc = $lastWrite
        if ($lastWrite -lt $RunStartedUtc.AddSeconds(-5)) {
            throw "dotnet test result XML predates this invocation (last write $($lastWrite.ToString('o')) < start $($RunStartedUtc.ToString('o')))"
        }
        try { $result.XmlSha256 = (Get-FileHash -LiteralPath $fullXml -Algorithm SHA256).Hash } catch { }

        $xml = Read-DirectResultXml -XmlPath $fullXml -ExpectedAssemblyName $ExpectedAssemblyName
        $result.TestNodeCount = $xml.TestNodeCount
        $result.AssemblyName = $xml.AssemblyName
        $result.TestFramework = $xml.TestFramework
        if (-not $xml.Ok) { throw "invalid dotnet test result XML: $($xml.Reason)" }
        if ($null -eq $xml.Counts) { throw 'dotnet test result XML has no parsed counts' }

        if ($xml.TestFramework -cnotmatch ('^' + [regex]::Escape($ExpectedFramework) + '(?:\+[0-9A-Za-z][0-9A-Za-z.-]*)?$')) {
            throw "dotnet test runner identity mismatch: expected '$ExpectedFramework' with optional '+build' metadata, got '$($xml.TestFramework)'"
        }
        if ($xml.SchemaVersion -ne $ExpectedSchemaVersion) {
            throw "dotnet test result XML schema identity mismatch: expected supported schema '$ExpectedSchemaVersion', got '$($xml.SchemaVersion)'"
        }
        if (-not [string]::IsNullOrWhiteSpace($ExpectedAssemblyPath)) {
            if ([string]::IsNullOrWhiteSpace($xml.AssemblyName) -or -not [IO.Path]::IsPathRooted($xml.AssemblyName)) {
                throw "dotnet test result XML assembly name is not a rooted assembly path: '$($xml.AssemblyName)'"
            }
            $actualAssembly = [IO.Path]::GetFullPath($xml.AssemblyName)
            $expectedAssembly = [IO.Path]::GetFullPath($ExpectedAssemblyPath)
            if (-not [string]::Equals($actualAssembly, $expectedAssembly, $pathComparison)) {
                throw "dotnet test result XML assembly path mismatch: expected '$expectedAssembly', got '$actualAssembly'"
            }
        }

        $result.XmlCounts = $xml.Counts
        $c = $xml.Counts
        if ($null -eq $NativeExit) { throw 'native exit is unknown (invocation threw); never treated as 0' }
        if ([int]$NativeExit -ne 0) { throw "native exit is $NativeExit, not 0" }
        if ($c.Total -le 0) { throw 'dotnet test result XML contains zero executed test cases' }
        if ($c.Total -ne $xml.TestNodeCount) { throw "result XML total=$($c.Total) but $($xml.TestNodeCount) test case node(s) exist" }
        if ($c.Failed -gt 0 -or $c.Errors -gt 0 -or $c.Skipped -gt 0 -or $c.NotRun -gt 0) {
            throw "dotnet test result XML is not green: failed=$($c.Failed), errors=$($c.Errors), skipped=$($c.Skipped), not-run=$($c.NotRun)"
        }
        if ($c.Passed -ne $c.Total) { throw "dotnet test result XML passed=$($c.Passed) does not equal total=$($c.Total)" }
        if ($null -eq $ConsoleCounts) { throw 'raw `dotnet test` console summary is missing/unparseable; the native XML cannot be cross-checked' }
        foreach ($name in @('Total', 'Passed', 'Failed', 'Skipped')) {
            if ($null -eq $ConsoleCounts[$name]) { throw "raw console summary has no $name count" }
            if ([int]$ConsoleCounts[$name] -ne [int]$c[$name]) {
                throw "result XML $name=$($c[$name]) does not match console summary $name=$($ConsoleCounts[$name])"
            }
        }

        $result.Ok = $true
        $result.Reason = "$($c.Total)/$($c.Total) test cases Pass; native dotnet test XML matches console and native exit 0"
    }
    catch {
        $result.Ok = $false
        $result.Reason = $_.Exception.Message
    }
    return [pscustomobject]$result
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
# If a directory cannot be purged (e.g. a file is locked by a crashed process),
# its before-purge snapshot is kept and files matching that snapshot (same
# project-relative path AND content hash) are EXCLUDED from the collected
# evidence and recorded as a gap - a log whose provenance cannot be proven to
# belong to this run must never pose as current-run evidence.
function Get-TestResultsCandidates {
    param([string]$ProjectDir, [string]$Configuration, [string]$Tfm)
    return @(
        (Join-Path $ProjectDir (Join-Path 'bin' (Join-Path $Configuration (Join-Path $Tfm 'TestResults')))),
        (Join-Path $ProjectDir 'TestResults')
    )
}

function Get-TestResultsSnapshot {
    # Files that could not be hashed at snapshot time (e.g. exclusively locked)
    # get HashOk = $false. A missing hash is NOT evidence the content changed
    # later: Save-TestResults must treat such entries conservatively (exclude),
    # because the lock may have been released before the collection re-hash.
    param([string]$Dir)
    $list = @()
    $files = @()
    try { $files = @(Get-ChildItem -LiteralPath $Dir -Recurse -File -ErrorAction Stop) }
    catch { return @{ Files = $list; Error = "snapshot failed: $($_.Exception.Message)" } }
    foreach ($file in $files) {
        $rel = $file.FullName.Substring($Dir.Length).TrimStart('\', '/')
        $hash = $null
        try { $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash } catch { }
        $list += @{ Rel = $rel; Sha256 = $hash; HashOk = [bool]$hash }
    }
    return @{ Files = $list; Error = $null }
}

function Clear-TestResultsDirs {
    # Returns one status object per directory:
    #   @{ Dir; Cleared(bool); Stale = @(@{Rel; Sha256; HashOk}); SnapshotError; Note }
    # Stale is non-empty only when the directory could NOT be purged. When the
    # before-snapshot itself could not enumerate the directory (SnapshotError),
    # nothing in that directory can be attributed to the current run, so
    # Save-TestResults excludes the entire directory.
    param([string[]]$Dirs)
    $statuses = @()
    foreach ($dir in $Dirs) {
        $status = @{ Dir = $dir; Cleared = $true; Stale = @(); SnapshotError = $null; Note = '' }
        if (-not (Test-Path -LiteralPath $dir)) {
            $status.Note = 'not present before the run'
            $statuses += $status
            continue
        }
        $before = Get-TestResultsSnapshot -Dir $dir
        try {
            Remove-Item -LiteralPath $dir -Recurse -Force
        }
        catch {
            $status.Cleared = $false
            $status.Stale = @($before.Files)
            $status.SnapshotError = $before.Error
            if ($before.Error) {
                $status.Note = "purge failed AND before-snapshot enumeration failed ($($before.Error)); the ENTIRE directory is excluded from current-run evidence"
                Add-Content -Path (Join-Path $LogDir 'collection-errors.log') -Value ("purge failed for ${dir} and snapshot enumeration failed; the whole directory is excluded from current-run evidence: $($before.Error)") -Encoding UTF8
            }
            else {
                $unreadable = @(@($before.Files) | Where-Object { -not $_.HashOk }).Count
                $status.Note = "purge failed: $($_.Exception.Message); before-snapshot with $(@($before.Files).Count) file(s) conservatively excluded ($unreadable of them unreadable at snapshot time)"
                Add-Content -Path (Join-Path $LogDir 'collection-errors.log') -Value ("purge failed for ${dir}: $($_.Exception.Message); excluding $(@($before.Files).Count) snapshot file(s) from current-run evidence ($unreadable unreadable at snapshot time)") -Encoding UTF8
            }
        }
        $statuses += $status
    }
    return $statuses
}

function Save-TestResults {
    # Copies every file the invocation produced into
    #   <log dir>\test-results\<project>\<mode>\<path relative to the PROJECT dir>
    # The mode segment (dotnetTest / direct) keeps the two invocations of an auto
    # run apart; the project-relative path keeps same-named logs from different
    # source roots (bin\...\TestResults vs project-root TestResults) apart.
    # Returns @{ Collected = @(strings with sha256); ExcludedStale = @(strings) }.
    param(
        [string]$ProjectName,
        [string]$Mode,
        [string]$ProjectDir,
        [string[]]$Dirs,
        [object[]]$PurgeStatuses
    )
    $destRoot = Join-Path $LogDir (Join-Path 'test-results' (Join-Path $ProjectName $Mode))
    $collected = @()
    $excludedStale = @()
    foreach ($dir in $Dirs) {
        if (-not (Test-Path -LiteralPath $dir)) { continue }
        $status = $null
        foreach ($candidate in $PurgeStatuses) {
            if ($candidate.Dir -eq $dir) { $status = $candidate; break }
        }
        $files = @()
        try { $files = @(Get-ChildItem -LiteralPath $dir -Recurse -File -ErrorAction Stop) }
        catch {
            Add-Content -Path (Join-Path $LogDir 'collection-errors.log') -Value ("enumerate failed for ${dir}: $($_.Exception.Message)") -Encoding UTF8
            continue
        }
        foreach ($file in $files) {
            $relFromProject = $file.FullName.Substring($ProjectDir.Length).TrimStart('\', '/')
            # Stale-snapshot entries are relative to the DIRECTORY being purged,
            # so the comparison must use the same base (not the project root).
            $relFromDir = $file.FullName.Substring($dir.Length).TrimStart('\', '/')
            $sourceHash = $null
            try { $sourceHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash } catch { }
            $staleReason = $null
            if ($null -ne $status -and -not $status.Cleared) {
                # Only files whose current provenance is PROVABLE are collected.
                # A hash that was unavailable at snapshot time does not become
                # evidence of a content change just because it is readable now:
                # an exclusive lock may simply have been released in between.
                if ($status.SnapshotError) {
                    $staleReason = "directory excluded (snapshot enumeration failed: $($status.SnapshotError))"
                }
                else {
                    $snap = $null
                    foreach ($staleFile in $status.Stale) {
                        if ($staleFile.Rel -eq $relFromDir) { $snap = $staleFile; break }
                    }
                    if ($null -ne $snap) {
                        if (-not $snap.HashOk -or -not $sourceHash) {
                            $staleReason = "conservative exclusion (hash unavailable at snapshot time; content change unprovable)"
                        }
                        elseif ($snap.Sha256 -eq $sourceHash) {
                            $staleReason = "unchanged since the before-snapshot  sha256=$sourceHash"
                        }
                        # else: both hashes readable and different -> the file was
                        # rewritten during this invocation -> provably current.
                    }
                    # Not in the snapshot -> did not exist when the dir was
                    # snapshotted -> produced by this invocation.
                }
            }
            if ($staleReason) {
                $excludedStale += ("{0}  excluded ({1})" -f $relFromProject, $staleReason)
                continue
            }
            $dest = Join-Path $destRoot $relFromProject
            try {
                $destDir = Split-Path $dest -Parent
                if (-not (Test-Path -LiteralPath $destDir)) { New-Item -ItemType Directory -Force -Path $destDir | Out-Null }
                Copy-Item -LiteralPath $file.FullName -Destination $dest -Force
                $hash = '(unavailable)'
                try { $hash = (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash } catch { }
                $collected += ("{0}  sha256={1}" -f (Join-Path (Join-Path (Join-Path 'test-results' $ProjectName) $Mode) $relFromProject), $hash)
            }
            catch {
                Add-Content -Path (Join-Path $LogDir 'collection-errors.log') -Value ("copy failed for $($file.FullName): $($_.Exception.Message)") -Encoding UTF8
            }
        }
    }
    return @{ Collected = $collected; ExcludedStale = $excludedStale }
}

function Write-RunMetaFile {
    # Per-invocation attribution. The final row result of the project is in
    # summary.log; this file pins down exactly what was executed and produced.
    # Best-effort: a failure to write it must not crash the run.
    param(
        [string]$Name, [string]$ProjectPath, [string]$Kind, [string]$Mode,
        [string]$RunnerTag, [string]$Runner, [string]$Command, [string]$Cwd,
        [object]$ExitCode, [object]$Total, [object]$Failed, [object]$Skipped,
        [string[]]$Collected, [string[]]$ExcludedStale, [object[]]$PurgeStatuses, [hashtable]$Head,
        [string]$ResultXml = '', [string]$ResultXmlSha256 = '', [string]$XmlGate = '',
        [string]$NativeResultXml = '', [string]$NativeResultXmlSha256 = '', [string]$NativeXmlGate = '',
        [string]$NativeCounts = ''
    )
    try {
        $dir = Join-Path $LogDir (Join-Path 'runs' $Name)
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
        $exitLabel = $(if ($null -ne $ExitCode) { "$ExitCode" } else { '(invocation exception; exit unknown - never treated as 0)' })
        $text = @(
            "project     : $Name ($ProjectPath)"
            "kind        : $Kind"
            "runner mode : $Mode"
            "runner tag  : $RunnerTag"
            "runner used : $Runner"
            "command     : $Command"
            "cwd         : $Cwd"
            "exit code   : $exitLabel"
            "total       : $Total"
            "failed      : $Failed"
            "skipped     : $Skipped"
            "head        : $($Head.Head)"
            "TestResults purge before this invocation:"
        )
        if ($ResultXml) {
            $text += "result xml  : $ResultXml"
            $text += "result xml sha256: $(if ($ResultXmlSha256) { $ResultXmlSha256 } else { '(unavailable)' })"
            $text += "result xml gate  : $XmlGate"
        }
        if ($NativeResultXml) {
            $text += "dotnetTest xml  : $NativeResultXml"
            $text += "dotnetTest xml sha256: $(if ($NativeResultXmlSha256) { $NativeResultXmlSha256 } else { '(unavailable)' })"
            $text += "dotnetTest xml gate  : $NativeXmlGate"
            $text += "dotnetTest counts    : $(if ($NativeCounts) { $NativeCounts } else { '(unavailable)' })"
        }
        foreach ($status in $PurgeStatuses) {
            $label = 'cleared'
            if (-not $status.Cleared) {
                if ($status.SnapshotError) { $label = 'NOT CLEARED (snapshot enumeration failed; ENTIRE directory excluded from current-run evidence)' }
                else { $label = 'NOT CLEARED (stale conservatively excluded by before-snapshot)' }
            }
            if ($status.Note) { $label = "$label - $($status.Note)" }
            $text += "  $($status.Dir) -> $label"
        }
        $text += "collected framework logs:"
        if (@($Collected).Count -gt 0) { $text += (@($Collected) | ForEach-Object { "  $_" }) }
        else { $text += "  (none)" }
        if (@($ExcludedStale).Count -gt 0) {
            $text += "excluded stale files (purge failed; provenance NOT current-run):"
            $text += (@($ExcludedStale) | ForEach-Object { "  $_" })
        }
        Set-Content -Path (Join-Path $dir "$RunnerTag.meta.txt") -Value ($text -join [Environment]::NewLine) -Encoding UTF8
    }
    catch {
        Add-Content -Path (Join-Path $LogDir 'collection-errors.log') -Value ("meta write failed for $Name/${RunnerTag}: $($_.Exception.Message)") -Encoding UTF8
    }
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
    Write-Step ("solution build: PASS (exit {0})" -f (Format-ExitCode $slnExit))
}
else {
    Write-Step ("solution build: FAIL (exit {0}); per-project builds follow for attribution" -f (Format-ExitCode $slnExit))
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
        $dotnetTestPurges = @(Clear-TestResultsDirs -Dirs $testResultsDirs)

        # The native per-case report is produced by the test application through
        # the xunit.v3/MTP report switches. Its directory is unique to this
        # run/project and is removed before the invocation, so a report left by
        # an earlier run can never be attributed to this one. If the purge fails
        # the project fails before `dotnet test` starts (fail closed).
        $project.DotnetTestResultsDir = Join-Path $LogDir (Join-Path 'dotnetTest-xml' $name)
        $project.DotnetTestResultXml = Join-Path $project.DotnetTestResultsDir 'dotnet-test-results.xml'
        $nativePrepareError = $null
        try {
            if (Test-Path -LiteralPath $project.DotnetTestResultsDir) {
                Remove-Item -LiteralPath $project.DotnetTestResultsDir -Recurse -Force
            }
            New-Item -ItemType Directory -Force -Path $project.DotnetTestResultsDir | Out-Null
            if (Test-Path -LiteralPath $project.DotnetTestResultXml) {
                Remove-Item -LiteralPath $project.DotnetTestResultXml -Force
            }
        }
        catch {
            $nativePrepareError = "$($_.Exception.GetType().FullName): $($_.Exception.Message)"
        }
        if ($nativePrepareError) {
            $project.Result = 'FAIL'
            $project.Detail = "could not prepare a fresh dotnet test native results directory '$($project.DotnetTestResultsDir)': $nativePrepareError; refusing to run with potentially stale evidence"
            $anyFail = $true
            continue
        }

        Write-Step "dotnet test : $name (native xunit.v3 XML)"
        $nativeResultsArgument = Get-DotnetTestResultsArgument $project.DotnetTestResultsDir
        $project.DotnetTestResultsArgument = $nativeResultsArgument
        $nativeArgs = @(
            '--report-xunit-xml',
            '--report-xunit-xml-filename', 'dotnet-test-results.xml',
            '--results-directory', $nativeResultsArgument
        )
        $dotnetTestCommand = (@(@($DotnetPath, 'test', $csproj, '-c', $Configuration, '--nologo', '--') + $nativeArgs) |
            ForEach-Object { ConvertTo-NativeArgument ([string]$_) }) -join ' '
        $dotnetTestStartedUtc = [DateTime]::UtcNow
        $exit = Invoke-Captured -LogPath $project.DotnetTestLog -Command {
            & $DotnetPath test $csproj -c $Configuration --nologo -- `
                --report-xunit-xml `
                --report-xunit-xml-filename dotnet-test-results.xml `
                --results-directory $nativeResultsArgument
        }
        $dotnetTestSaved = Save-TestResults -ProjectName $name -Mode 'dotnetTest' -ProjectDir $projectDir -Dirs $testResultsDirs -PurgeStatuses $dotnetTestPurges
        $consoleCounts = Get-DotnetTestConsoleCounts $project.DotnetTestLog
        $nativeExists = Test-Path -LiteralPath $project.DotnetTestResultXml -PathType Leaf

        if ($consoleCounts -or $nativeExists) {
            # Once dotnet test actually produced a summary or a native report,
            # this is the runner for the row. A failed native gate is a row FAIL;
            # auto mode must not silently replace it with a direct run.
            $nativeEvidence = Test-DotnetTestInvocationEvidence -XmlPath $project.DotnetTestResultXml -ExpectedAssemblyName $assemblyName `
                -ExpectedAssemblyPath $testDll -ResultsDir $project.DotnetTestResultsDir -LogDir $LogDir `
                -RunStartedUtc $dotnetTestStartedUtc -ConsoleCounts $consoleCounts -NativeExit $exit
            $project.NativeEvidence = $nativeEvidence
            $project.Runner = 'dotnet test (MTP)'
            $project.ExitCode = $(if ($null -ne $exit) { $exit } else { 'EXCEPTION' })
            if ($nativeEvidence.XmlCounts) {
                $nc = $nativeEvidence.XmlCounts
                $project.NativeCounts = $nc
                $project.Total = $nc.Total
                $project.Failed = $nc.Failed + $nc.Errors
                $project.Skipped = $nc.Skipped + $nc.NotRun
            }
            elseif ($consoleCounts) {
                $project.Total = $consoleCounts.Total
                $project.Failed = $consoleCounts.Failed
                $project.Skipped = $consoleCounts.Skipped
            }
            $nativeCountsText = $(if ($project.NativeCounts) {
                "total=$($project.NativeCounts.Total); passed=$($project.NativeCounts.Passed); failed=$($project.NativeCounts.Failed); skipped=$($project.NativeCounts.Skipped); errors=$($project.NativeCounts.Errors); not-run=$($project.NativeCounts.NotRun); nodes=$($nativeEvidence.TestNodeCount); framework=$($nativeEvidence.TestFramework)"
            } else { '(unavailable)' })
            Write-RunMetaFile -Name $name -ProjectPath $project.Path -Kind $project.Kind -Mode $RunnerMode `
                -RunnerTag 'dotnetTest' -Runner 'dotnet test (MTP; native xunit.v3 XML)' -Command $dotnetTestCommand -Cwd $cwd -ExitCode $exit `
                -Total $(if ($nativeEvidence.XmlCounts) { $nativeEvidence.XmlCounts.Total } elseif ($consoleCounts) { $consoleCounts.Total } else { '(no parseable summary)' }) `
                -Failed $(if ($nativeEvidence.XmlCounts) { $nativeEvidence.XmlCounts.Failed + $nativeEvidence.XmlCounts.Errors } elseif ($consoleCounts) { $consoleCounts.Failed } else { '-' }) `
                -Skipped $(if ($nativeEvidence.XmlCounts) { $nativeEvidence.XmlCounts.Skipped + $nativeEvidence.XmlCounts.NotRun } elseif ($consoleCounts) { $consoleCounts.Skipped } else { '-' }) `
                -Collected @($dotnetTestSaved.Collected) -ExcludedStale @($dotnetTestSaved.ExcludedStale) `
                -PurgeStatuses $dotnetTestPurges -Head $headInfo `
                -NativeResultXml $project.DotnetTestResultXml -NativeResultXmlSha256 $nativeEvidence.XmlSha256 `
                -NativeXmlGate "$(if ($nativeEvidence.Ok) { 'PASS' } else { 'FAIL' }): $($nativeEvidence.Reason)" `
                -NativeCounts $nativeCountsText
            if (-not $nativeEvidence.Ok) {
                $project.Result = 'FAIL'
                $project.Detail = "dotnet test native XML gate FAIL: $($nativeEvidence.Reason); see $($project.DotnetTestResultXml) and $($project.DotnetTestLog)"
                $anyFail = $true
                continue
            }
        }
        else {
            # Neither a console summary nor a native report: inspect the log
            # before any fallback. A build or restore error fails the project
            # even if a previous run left a valid DLL in bin - the fallback path
            # must never resurrect stale outputs as a green result.
            $buildErrors = @()
            if ($project.DotnetTestLog -and (Test-Path $project.DotnetTestLog)) {
                $buildErrors = @(Select-String -Path $project.DotnetTestLog -Pattern 'error [A-Z]+[0-9]+' -ErrorAction SilentlyContinue)
            }
            Write-RunMetaFile -Name $name -ProjectPath $project.Path -Kind $project.Kind -Mode $RunnerMode `
                -RunnerTag 'dotnetTest' -Runner 'dotnet test (no native report)' -Command $dotnetTestCommand -Cwd $cwd -ExitCode $exit `
                -Total '(no parseable summary)' -Failed '-' -Skipped '-' `
                -Collected @($dotnetTestSaved.Collected) -ExcludedStale @($dotnetTestSaved.ExcludedStale) `
                -PurgeStatuses $dotnetTestPurges -Head $headInfo `
                -NativeResultXml $project.DotnetTestResultXml -NativeResultXmlSha256 '' `
                -NativeXmlGate 'NOT_PRODUCED: no console summary and no native per-case XML' `
                -NativeCounts '(unavailable)'
            if ($buildErrors.Count -gt 0) {
                $project.Result = 'FAIL'
                $project.Detail = "dotnet test hit build/restore errors (first: $($buildErrors[0].Line.Trim())); stale outputs were not used; see $($project.DotnetTestLog)"
                $anyFail = $true
                continue
            }
            if ($RunnerMode -eq 'dotnetTest') {
                $project.Result = 'FAIL'
                $project.Detail = "dotnetTest-only: dotnet test produced neither a parseable console summary nor a native per-case XML report (exit $(Format-ExitCode $exit)); no direct fallback is permitted; see $($project.DotnetTestLog)"
                $anyFail = $true
                continue
            }
            $project.FallbackReason =
                "dotnet test produced no parseable test summary and no native report ($(if ($null -ne $exit) { "exit $exit" } else { 'invocation exception' })) and the log shows no build errors; " +
                "see $($project.DotnetTestLog). The current source built OK in this run, so this is treated as a " +
                "runner/environment issue and the official in-process runner of the same test assembly decides the result. " +
                "This row's counts are direct-runner counts, NOT dotnetTest evidence."
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
        $directPurges = @(Clear-TestResultsDirs -Dirs $directResultsDirs)
        Write-Step "direct run  : $name"

        # Every real direct invocation writes the runner's native per-case XML to
        # its own path inside this run's LogDir. The path is unique per project
        # and mode, is cleared immediately before the invocation, and is recorded
        # in the meta/summary so a stale or shared file can never pose as current.
        $project.ResultXml = Join-Path $LogDir (Join-Path 'direct-xml' (Join-Path $name 'direct-results.xml'))
        $resultXmlDir = Split-Path $project.ResultXml -Parent
        if (-not (Test-Path -LiteralPath $resultXmlDir)) { New-Item -ItemType Directory -Force -Path $resultXmlDir | Out-Null }
        if (Test-Path -LiteralPath $project.ResultXml -PathType Leaf) { Remove-Item -LiteralPath $project.ResultXml -Force }
        elseif (Test-Path -LiteralPath $project.ResultXml) { Remove-Item -LiteralPath $project.ResultXml -Recurse -Force }

        # Manifest runner args stay authoritative; only a pre-existing result-xml
        # argument is replaced so this invocation cannot write outside its path.
        $directArgs = New-Object System.Collections.Generic.List[string]
        $manifestDirectArgs = @($manifest.testRunner.directRunnerArgs)
        for ($argIndex = 0; $argIndex -lt $manifestDirectArgs.Count; $argIndex++) {
            if ("$($manifestDirectArgs[$argIndex])" -eq '-result-xml') { $argIndex++; continue }
            $directArgs.Add([string]$manifestDirectArgs[$argIndex])
        }
        $directArgs.Add('-result-xml')
        $directArgs.Add($project.ResultXml)
        $directCommand = (@(@($DotnetPath, $testDll) + @($directArgs.ToArray())) | ForEach-Object { ConvertTo-NativeArgument ([string]$_) }) -join ' '
        $directRunStartedUtc = [DateTime]::UtcNow
        $exit = Invoke-Captured -LogPath $project.DirectLog -Command { & $DotnetPath $testDll @($directArgs.ToArray()) }
        $directSaved = Save-TestResults -ProjectName $name -Mode 'direct' -ProjectDir $directProjectDir -Dirs $directResultsDirs -PurgeStatuses $directPurges
        $consoleCounts = Get-DirectRunnerCounts $project.DirectLog
        $summary = Get-TestSummaryFromDirectRunner $project.DirectLog
        $evidence = Test-DirectInvocationEvidence -XmlPath $project.ResultXml -ExpectedAssemblyName $assemblyName `
            -LogDir $LogDir -RunStartedUtc $directRunStartedUtc -ConsoleCounts $consoleCounts -NativeExit $exit
        $project.DirectEvidence = $evidence
        $project.XmlGate = $(if ($evidence.Ok) { 'PASS' } else { 'FAIL' })
        if ($summary) {
            $project.Runner = $(if ($project.Runner -eq 'none') { 'in-process (direct)' } else { $project.Runner })
            $project.Total = $summary.Total
            $project.Failed = $summary.Failed
            $project.Skipped = $summary.Skipped
            $project.ExitCode = $(if ($null -ne $exit) { $exit } else { 'EXCEPTION' })
        }
        Write-RunMetaFile -Name $name -ProjectPath $project.Path -Kind $project.Kind -Mode $RunnerMode `
            -RunnerTag 'direct' -Runner 'in-process (direct)' -Command $directCommand -Cwd $cwd -ExitCode $exit `
            -Total $(if ($summary) { $summary.Total } else { '(no parseable summary)' }) `
            -Failed $(if ($summary) { $summary.Failed } else { '-' }) `
            -Skipped $(if ($summary) { $summary.Skipped } else { '-' }) `
            -Collected @($directSaved.Collected) -ExcludedStale @($directSaved.ExcludedStale) `
            -PurgeStatuses $directPurges -Head $headInfo `
            -ResultXml $project.ResultXml -ResultXmlSha256 $evidence.XmlSha256 -XmlGate "$($project.XmlGate): $($evidence.Reason)"
        if (-not $evidence.Ok) {
            $project.Result = 'FAIL'
            $project.Detail = "direct result XML gate FAIL: $($evidence.Reason); see $($project.ResultXml) and $($project.DirectLog)"
            $anyFail = $true
            continue
        }
        if (-not $summary) {
            $project.Result = 'FAIL'
            $project.Detail = "no parseable console summary for the direct runner (exit $(Format-ExitCode $exit)); XML cannot be cross-checked; see $($project.DirectLog)"
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
        $pluginDetail = "build-shared.ps1 exited $(Format-ExitCode $pluginExit)"
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

# --- 4b. accepted companion golden fixtures (mandatory unified-entry gate) -----
# The standalone verifier remains the sole validator. This stage only invokes the
# FULL CLI (never -InternalStructureTestMode), captures its real native exit and
# raw output, and fails the whole run on any failure/block/crash/zero output.
$companionResult = 'FAIL'
$companionDetail = ''
$companionVerifier = if ($CompanionVerifierPath) {
    Resolve-UnderRoot $root $CompanionVerifierPath
} else {
    Join-Path $root 'tools\verify-v5-companion-fixtures.ps1'
}
$companionFixtures = if ($CompanionFixtureDir) {
    Resolve-UnderRoot $root $CompanionFixtureDir
} else {
    Join-Path $root 'tests\v5-golden'
}
$companionLog = Join-Path $LogDir 'companion-fixtures.log'
$companionMeta = Join-Path $LogDir 'companion-fixtures.meta.txt'
$companionScriptHash = '(missing)'
if (Test-Path -LiteralPath $companionVerifier) {
    try { $companionScriptHash = (Get-FileHash -LiteralPath $companionVerifier -Algorithm SHA256).Hash } catch { $companionScriptHash = '(unavailable)' }
}
Write-Step "companion   : $companionVerifier"
Write-Step "fixtures    : $companionFixtures"
$companionRun = Invoke-CompanionVerifier -VerifierPath $companionVerifier -FixtureDir $companionFixtures `
    -LogPath $companionLog -TimeoutSeconds $CompanionTimeoutSeconds
$companionText = ''
try { if (Test-Path -LiteralPath $companionLog) { $companionText = Get-Content -LiteralPath $companionLog -Raw -Encoding UTF8 } } catch { }
$companionCert = Get-CompanionFixtureCertification -Text $companionText

if ($companionRun.TimedOut) {
    $companionResult = 'FAIL'
    $companionDetail = "verifier timed out after $CompanionTimeoutSeconds s; native exit not trusted"
}
elseif ($companionRun.Threw) {
    $companionResult = 'FAIL'
    $companionDetail = "verifier invocation threw: $($companionRun.Threw)"
}
elseif ($null -eq $companionRun.ExitCode) {
    $companionResult = 'FAIL'
    $companionDetail = 'verifier produced no native exit code (crash/spawn failure; never treated as 0)'
}
elseif ($companionRun.ExitCode -eq 2) {
    $companionResult = 'BLOCKED'
    $companionDetail = 'verifier native exit 2 (environment BLOCKED)'
}
elseif ($companionRun.ExitCode -ne 0) {
    $firstFailure = @($companionText -split "`r?`n" | Where-Object { $_ -match '^\s*-\s+[A-Z0-9_]+:' } | Select-Object -First 1)
    $companionResult = 'FAIL'
    $companionDetail = "verifier native exit $($companionRun.ExitCode)$(if ($firstFailure.Count) { "; first failure: $($firstFailure[0].Trim())" } else { '' })"
}
elseif (-not $companionCert.Complete) {
    $missing = @($companionCert.Missing)
    $companionResult = 'FAIL'
    $companionDetail = "verifier native exit 0 but did not certify 4/4 ($($companionCert.ByFile.Count)/4 parsed$(if ($missing.Count) { "; missing: $($missing -join ', ')" } else { '' })$(if ($companionCert.Duplicate) { '; duplicate fixture lines' } else { '' }))"
}
else {
    $companionResult = 'PASS'
    $companionDetail = '4/4 accepted fixtures certified by the standalone verifier (full CLI; native exit 0)'
}
if ($companionResult -eq 'FAIL') { $anyFail = $true }

$companionLogHash = '(unavailable)'
if (Test-Path -LiteralPath $companionLog) {
    try { $companionLogHash = (Get-FileHash -LiteralPath $companionLog -Algorithm SHA256).Hash } catch { }
}
$companionMetaLines = @(
    "stage            : companion-fixtures"
    "verifier         : $companionVerifier"
    "verifier sha256  : $companionScriptHash"
    "repo root        : $root"
    "fixture dir      : $companionFixtures"
    "command          : $($companionRun.Command)"
    "cwd              : $cwd"
    "timeout seconds  : $CompanionTimeoutSeconds"
    "raw native exit  : $(if ($null -ne $companionRun.ExitCode) { $companionRun.ExitCode } else { 'EXCEPTION (never treated as 0)' })"
    "timed out        : $($companionRun.TimedOut)"
    "invoke error     : $(if ($companionRun.Threw) { $companionRun.Threw } else { '(none)' })"
    "result           : $companionResult"
    "detail           : $companionDetail"
    "raw log          : $companionLog"
    "raw log sha256   : $companionLogHash"
    "stdout log       : $companionLog.stdout.log"
    "stderr log       : $companionLog.stderr.log"
    "git blob pins    : NOT_RUN (not requested; historical freeze blobs are not fetched in shallow CI; current fixture bytes and embedded provenance are verified offline)"
    "certified fixtures:"
)
if ($companionCert.Entries.Count -gt 0) {
    foreach ($entry in $companionCert.Entries) {
        $companionMetaLines += ("  {0}  {1}={2}  cases={3}  sha256={4}" -f $entry.File, $entry.Kind, $entry.Identity, $entry.Cases, $entry.Sha256)
    }
}
else {
    $companionMetaLines += "  (none parsed from the verifier output)"
}
try { Set-Content -Path $companionMeta -Value ($companionMetaLines -join [Environment]::NewLine) -Encoding UTF8 } catch { Write-Warning "[verify-v5] could not write companion meta ${companionMeta}: $($_.Exception.Message)" }

# --- 5. summary ----------------------------------------------------------------
$summaryPath = Join-Path $LogDir 'summary.log'
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("verify-v5 summary  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  config=$Configuration")
$lines.Add("dotnet SDK     : $sdkVersion  path: $DotnetPath  cwd: $cwd  log: $sdkLog")
$ciLines = Get-CiProvenanceLines
$lines.Add("head           : $($headInfo.Head)  branch: $($headInfo.Branch)")
$lines.Add("runner mode    : $RunnerMode  (dotnetTest evidence root: dotnetTest-xml\<project>\dotnet-test-results.xml; direct evidence root: direct-xml\<project>\direct-results.xml; never cross-labelled)")
$lines.Add("ci provenance  : $(if ($ciLines.Count) { $ciLines -join ' ' } else { '(none - local run)' })")
$lines.Add("run identity   : run-info.txt; per-invocation command/exit/counts/result-xml: runs\<project>\<mode>.meta.txt; raw framework logs: test-results\<project>\; direct runner XML: direct-xml\<project>\direct-results.xml")
$lines.Add("")
$lines.Add("solution build : $(if ($buildOk) { "PASS (exit $(Format-ExitCode $slnExit))" } else { "FAIL (exit $(Format-ExitCode $slnExit))" })  log: $slnBuildLog")
$lines.Add("plugin copy    : $pluginResult  ($($pluginDetail))$(if ($pluginLog) { "  log: $pluginLog" })")
$lines.Add("companion gate : $companionResult  ($companionDetail)  log: $companionLog  meta: $companionMeta")
$lines.Add("    command    : $($companionRun.Command)")
$lines.Add("    raw exit   : $(if ($null -ne $companionRun.ExitCode) { $companionRun.ExitCode } else { 'EXCEPTION (never treated as 0)' })  raw log sha256: $companionLogHash")
if ($companionCert.Entries.Count -gt 0) {
    foreach ($entry in $companionCert.Entries) {
        $lines.Add(("    fixture    : {0}  {1}={2}  cases={3}  sha256={4}" -f $entry.File, $entry.Kind, $entry.Identity, $entry.Cases, $entry.Sha256))
    }
}
else {
    $lines.Add("    fixture    : (none certified/parsed from the verifier output)")
}
$lines.Add("    git pins   : NOT_RUN (historical freeze blobs not requested/fetched; current bytes and embedded provenance verified offline)")
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
    if ($project.ResultXml) {
        $lines.Add("    direct xml: $($project.ResultXml)  sha256: $(if ($project.DirectEvidence) { $project.DirectEvidence.XmlSha256 } else { '(unavailable)' })  gate: $(if ($project.DirectEvidence) { "$($project.XmlGate) - $($project.DirectEvidence.Reason)" } else { '(not evaluated)' })")
    }
    if ($project.DotnetTestResultXml) {
        $lines.Add("    dotnetTest xml: $($project.DotnetTestResultXml)  sha256: $(if ($project.NativeEvidence) { $project.NativeEvidence.XmlSha256 } else { '(unavailable)' })  gate: $(if ($project.NativeEvidence) { "$(if ($project.NativeEvidence.Ok) { 'PASS' } else { 'FAIL' }) - $($project.NativeEvidence.Reason)" } else { 'NOT_PRODUCED/PARSEABLE SUMMARY ABSENT' })")
        if ($project.NativeCounts) {
            $lines.Add("    dotnetTest counts: total=$($project.NativeCounts.Total) passed=$($project.NativeCounts.Passed) failed=$($project.NativeCounts.Failed) skipped=$($project.NativeCounts.Skipped) errors=$($project.NativeCounts.Errors) not-run=$($project.NativeCounts.NotRun) nodes=$(if ($project.NativeEvidence) { $project.NativeEvidence.TestNodeCount } else { '?' }) framework=$(if ($project.NativeEvidence) { $project.NativeEvidence.TestFramework } else { '?' })")
        }
    }
    if ($project.BuildNote) {
        $lines.Add("    build    : $($project.BuildNote)")
    }
    if ($project.Comment) {
        $lines.Add("    note     : $($project.Comment)")
    }
}
$lines.Add("")
$overall = $(if ($anyFail -or $companionResult -eq 'FAIL') { 'FAIL' } elseif ($companionResult -eq 'BLOCKED') { 'BLOCKED' } else { 'PASS' })
$passedCount = @($ordered | Where-Object { $_.Result -eq 'PASS' }).Count
$failedCount = @($ordered | Where-Object { $_.Result -eq 'FAIL' }).Count
$blockedCount = @($ordered | Where-Object { $_.Result -eq 'BLOCKED' }).Count
$notRunCount = @($ordered | Where-Object { $_.Result -eq 'NOT_RUN' }).Count
$lines.Add("OVERALL: $overall  (project rows: $passedCount PASS, $failedCount FAIL, $blockedCount BLOCKED, $notRunCount NOT_RUN; companion gate: $companionResult)")
$lines | Set-Content -Path $summaryPath -Encoding UTF8
Write-Host ""
$lines | ForEach-Object { Write-Host $_ }

exit $(if ($overall -eq 'FAIL') { 1 } elseif ($overall -eq 'BLOCKED') { 2 } else { 0 })
