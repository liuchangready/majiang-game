#requires -Version 5.1
<#
.SYNOPSIS
    CI/local wrapper that runs the unified V5 verification (tools\verify-v5.ps1,
    unmodified) against a REAL disposable PostgreSQL cluster so the lobby tests
    execute instead of skipping.

.DESCRIPTION
    The CI failure at head a086291 is exactly one row: Mahjong.LobbyService.Tests
    reports 12 skipped cases (of 89) because MAHJONG_LOBBY_TEST_POSTGRES is unset
    on the runner, and the unified gate fails any skipped row. This wrapper closes
    that gap without touching the verifier, the tests or the accepted pg scripts:

      1. guards: dedicated loopback port (5432/5433 and the externally reserved
         55450-55452 are refused), a fresh -LogDir that does not already exist
         (the verifier would rename an existing dir and split the evidence),
         scripts present, dotnet resolved explicitly (DOTNET_ROOT or PATH - this
         wrapper never hardcodes a machine-local path)
      2. a UNIQUE disposable cluster root outside the worktree (TEMP\<unique>)
         handed to the accepted dotnet\scripts\pg-lobby-verify scripts, which own
         every safety property: official PostgreSQL 16.10 binaries with the
         pinned SHA-256, root ownership marker, loopback-only listener, and the
         read-only data_directory / database-marker verifications BEFORE any
         database operation. This wrapper never bypasses, re-implements or
         filters them, never kills an unknown process, and never falls back to a
         no-database run: a PostgreSQL failure fails the whole gate.
      3. sets MAHJONG_LOBBY_TEST_POSTGRES, MAHJONG_LOBBY_PG_ROOT,
         MAHJONG_LOBBY_PG_DATA and MAHJONG_LOBBY_PG_EVIDENCE for the child, runs
         migrate.ps1 twice (deployment repeatability), then invokes the REAL
         unified entry tools\verify-v5.ps1 in a child process and propagates its
         exit code verbatim (0/1/2, or any code from -VerifyScript test stubs).
      4. finally: archives the cluster's logs/evidence/ownership marker, runs the
         accepted cleanup-postgres.ps1 (marker-guarded; refuses foreign roots),
         restores the four environment variables to their pre-call values, and
         writes every phase log plus wrapper-summary.json and a SHA-256 manifest
         under <LogDir>\pg\. A cleanup failure fails the run but never masks the
         original verifier exit code; no temp database content or secret is
         copied into the evidence (trust-auth connection strings carry no
         password; restored env values are recorded as SHA-256 hashes only).

    -VerifyScript exists ONLY for the isolated self-test
    (tools\tests\VerifyV5PostgresCi.Tests.ps1) to prove exit-code propagation
    with a stub runner. A stub run is never a real PostgreSQL verification.

    Usage (CI / local):
      powershell -ExecutionPolicy Bypass -File tools\ci\verify-v5-postgres.ps1
                 [-Configuration Release] [-RunnerMode auto|dotnetTest|direct]
                 [-DotnetPath <path>] [-Root <dir>] [-Port 55460]
                 [-DbName mahjong_lobby_test] [-PgZipPath <cached zip>]
                 [-LogDir <fresh dir>] [-ManifestPath <path>]
                 [-SkipPluginCopyCheck] [-NoProjectDiscovery] [-VerifyScript <path>]

    Exit codes: the verifier's own exit code when it is non-zero; otherwise 1
    when any wrapper stage (precheck/start/migrate/cleanup/env-restore/evidence)
    failed; otherwise 0.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('auto', 'dotnetTest', 'direct')]
    [string]$RunnerMode = 'auto',

    [string]$DotnetPath,

    # Disposable cluster root; must be outside the worktree. Defaults to a unique
    # directory under TEMP so concurrent runs never collide.
    [string]$Root,

    [int]$Port = 55460,

    [string]$DbName = 'mahjong_lobby_test',

    # Optional local copy of the official PostgreSQL 16.10 zip; the accepted
    # start-postgres.ps1 verifies the pinned SHA-256 regardless of the source.
    [string]$PgZipPath,

    # Must NOT exist yet: the verifier renames an existing log directory and the
    # wrapper's PG evidence would be split from the verifier's logs.
    [string]$LogDir,

    [string]$ManifestPath,

    # Self-test hook only (error-propagation scenarios). Never a real gate.
    [string]$VerifyScript,

    [switch]$SkipPluginCopyCheck,

    [switch]$NoProjectDiscovery
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$script:WrapperStartUtc = (Get-Date).ToUniversalTime()
$script:HostExecutable = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
$script:RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)  # tools\ci -> repo
$script:PgScriptDir = Join-Path $script:RepoRoot 'dotnet\scripts\pg-lobby-verify'
$script:Stages = New-Object System.Collections.Generic.List[object]
$script:StepLines = New-Object System.Collections.Generic.List[string]
$script:StagedLogs = @{}
$script:FailedStages = New-Object System.Collections.Generic.List[string]
$script:VerifierExit = $null          # exact exit code of the unified verifier child
$script:VerifierRan = $false
$script:ChildFileCounter = 0          # names the per-child redirect files in staging
# OS-native tar.exe/curl.exe live in System32 (see Invoke-ScriptChild).
$script:System32Dir = $null
if ($env:SystemRoot) {
    $system32Candidate = Join-Path $env:SystemRoot 'System32'
    if (Test-Path -LiteralPath (Join-Path $system32Candidate 'tar.exe')) {
        $script:System32Dir = $system32Candidate
    }
}

$envVariableNames = @(
    'MAHJONG_LOBBY_TEST_POSTGRES',
    'MAHJONG_LOBBY_PG_ROOT',
    'MAHJONG_LOBBY_PG_DATA',
    'MAHJONG_LOBBY_PG_EVIDENCE'
)

function Write-Step([string]$Message) {
    $line = "[verify-v5-postgres] $Message"
    Write-Host $line
    $script:StepLines.Add($line)
}

function Add-Stage {
    param([string]$Name, [string]$Status, [object]$ExitCode, [string]$Detail, [string]$LogName)
    $script:Stages.Add([ordered]@{
        stage    = $Name
        status   = $Status
        exitCode = $ExitCode
        detail   = $Detail
        log      = $LogName
    })
    if ($Status -eq 'FAIL') { $script:FailedStages.Add($Name) }
    $suffix = $(if ($Detail) { " - $Detail" } else { '' })
    Write-Step ("stage {0}: {1} (exit {2}){3}" -f $Name, $Status, $(if ($null -ne $ExitCode) { $ExitCode } else { 'EXCEPTION' }), $suffix)
}

function Add-StagedLog([string]$Name, $Lines) {
    if (-not $script:StagedLogs.ContainsKey($Name)) {
        $script:StagedLogs[$Name] = New-Object System.Collections.Generic.List[string]
    }
    foreach ($line in @($Lines)) { $script:StagedLogs[$Name].Add([string]$line) }
}

function Get-Sha256Hex([string]$Value) {
    # Used to prove environment restoration without recording (potentially
    # secret) values: a hash is comparable, never reversible.
    $bytes = [System.Text.Encoding]::UTF8.GetBytes("$Value")
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return ([System.BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Test-WrapperTcpPortOpen {
    param([string]$HostName = '127.0.0.1', [int]$Port, [int]$TimeoutMs = 400)
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $task = $client.ConnectAsync($HostName, $Port)
        if (-not $task.Wait($TimeoutMs)) { return $false }
        return $client.Connected
    }
    catch { return $false }
    finally { $client.Dispose() }
}

function Save-PgEnvironmentSnapshot {
    # Records the pre-call state of the four PG variables so the finally block
    # can restore them exactly, including "was unset".
    $snapshot = @{}
    foreach ($name in $envVariableNames) {
        $value = [Environment]::GetEnvironmentVariable($name)
        $snapshot[$name] = @{ WasSet = ($null -ne $value); Value = $value }
    }
    return $snapshot
}

function Restore-PgEnvironmentSnapshot {
    # Restores the pre-call values (or removes the variables that were unset)
    # and returns per-variable proof records. Values are NOT recorded - only a
    # SHA-256 of the post-restore state, so a preset secret can never leak into
    # the evidence.
    param([hashtable]$Snapshot)
    $records = @()
    foreach ($name in $envVariableNames) {
        $snap = $Snapshot[$name]
        $restored = $false
        $afterSha = '(unset)'
        try {
            if ($snap.WasSet) { [Environment]::SetEnvironmentVariable($name, $snap.Value) }
            else { [Environment]::SetEnvironmentVariable($name, $null) }
            $after = [Environment]::GetEnvironmentVariable($name)
            if ($snap.WasSet) {
                $restored = ($null -ne $after -and $after -ceq $snap.Value)
            }
            else {
                $restored = ($null -eq $after)
            }
            if ($null -ne $after) { $afterSha = Get-Sha256Hex $after }
        }
        catch {
            $afterSha = "restore error: $($_.Exception.Message)"
        }
        if (-not $restored) { $script:FailedStages.Add("env-restore:$name") }
        $records += [ordered]@{
            variable        = $name
            wasSetBeforeRun = [bool]$snap.WasSet
            restored        = [bool]$restored
            restoredSha256  = $afterSha
        }
    }
    return $records
}

function Resolve-WrapperDotnetPath {
    # Explicit path, then DOTNET_ROOT, then PATH. There is deliberately no
    # machine-local fallback: a GitHub runner must resolve its own toolchain.
    param([string]$Explicit)
    if ($Explicit) {
        return @{ Path = $Explicit; Source = 'explicit -DotnetPath' }
    }
    if ($env:DOTNET_ROOT -and (Test-Path -LiteralPath (Join-Path $env:DOTNET_ROOT 'dotnet.exe'))) {
        return @{ Path = (Join-Path $env:DOTNET_ROOT 'dotnet.exe'); Source = 'DOTNET_ROOT' }
    }
    try {
        $command = Get-Command dotnet -ErrorAction Stop
        return @{ Path = $command.Source; Source = 'PATH' }
    }
    catch { return $null }
}

function Invoke-ScriptChild {
    # Runs a PowerShell child process and captures its output plus the real
    # exit code. Every mechanism here is dictated by postgres.exe outliving the
    # script that started it, and by Windows PowerShell 5.1 limitations, each
    # of which was reproduced empirically on this tree:
    #   - PowerShell-level redirection (1> file), Start-Process
    #     -RedirectStandardOutput AND invoking through & cmd.exe are all
    #     pipe/console-backed in 5.1: the parent blocks until EVERY descendant
    #     (the long-lived server) releases them - a hard deadlock while the
    #     cluster runs.
    #   - Start-Process -Wait waits for ALL descendants (same deadlock), while
    #     .ExitCode read off a polled Start-Process -PassThru object is $null.
    #   - THE WORKING MECHANISM: a detached cmd.exe with its OWN hidden
    #     console (Start-Process -WindowStyle Hidden), which performs real
    #     cmd-level 1>/2> file redirection for the child and, after the child
    #     exits, writes !errorlevel! (cmd /v:on delayed expansion) to a file.
    #     The wrapper polls HasExited and reads that file - it returns as soon
    #     as the DIRECT child exits, no matter how long the server lives.
    param([string]$ScriptPath, [hashtable]$Parameters = @{}, [string[]]$Switches = @())
    $script:ChildFileCounter++
    $stdoutFile = Join-Path $script:StagingDir ("child-{0}-stdout.log" -f $script:ChildFileCounter)
    $stderrFile = Join-Path $script:StagingDir ("child-{0}-stderr.log" -f $script:ChildFileCounter)
    $exitCodeFile = Join-Path $script:StagingDir ("child-{0}-exitcode.txt" -f $script:ChildFileCounter)

    $innerCommand = '"' + $script:HostExecutable + '" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $ScriptPath + '"'
    foreach ($key in ($Parameters.Keys | Sort-Object)) {
        $value = $Parameters[$key]
        if ($null -ne $value -and "$value" -ne '') {
            $innerCommand += (' -' + $key + ' "' + ([string]$value) + '"')
        }
    }
    foreach ($switchName in $Switches) { $innerCommand += (' -' + $switchName) }
    $innerCommand += (' 1> "' + $stdoutFile + '" 2> "' + $stderrFile + '"')
    $innerCommand += (' & echo !errorlevel! > "' + $exitCodeFile + '"')

    $output = New-Object System.Collections.Generic.List[string]
    $exit = $null
    $previousEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    # The accepted scripts call bare tar.exe/curl.exe, which the OS ships in
    # System32. When this wrapper was itself started from an MSYS/Git-Bash
    # shell, /usr/bin precedes System32 in PATH and MSYS GNU tar rejects the
    # C:\... extraction path ("Cannot connect to C:"). GitHub runners and plain
    # PowerShell sessions already resolve System32 first, so ordering System32
    # ahead for the wrapper's own children is strictly a local-dev robustness
    # fix, never a behavior change on CI.
    $previousPath = $env:PATH
    if ($script:System32Dir -and -not $previousPath.StartsWith($script:System32Dir + ';', [System.StringComparison]::OrdinalIgnoreCase)) {
        $env:PATH = $script:System32Dir + ';' + $previousPath
    }
    try {
        # /d: skip autorun; /v:on: delayed expansion for !errorlevel!;
        # /s: strip exactly the outer quote pair below.
        $cmdArguments = '/d /v:on /s /c "' + $innerCommand + '"'
        $cmdProcess = Start-Process -FilePath "$env:SystemRoot\System32\cmd.exe" -ArgumentList $cmdArguments `
            -WindowStyle Hidden -PassThru
        while (-not $cmdProcess.HasExited) { Start-Sleep -Milliseconds 200 }
        $cmdProcess.WaitForExit() | Out-Null
        # The errorlevel file is written by cmd right before it exits; allow a
        # short grace period for the flush.
        for ($waited = 0; $waited -lt 5000 -and -not (Test-Path -LiteralPath $exitCodeFile); $waited += 100) {
            Start-Sleep -Milliseconds 100
        }
        if (Test-Path -LiteralPath $exitCodeFile) {
            $recorded = (Get-Content -LiteralPath $exitCodeFile -Raw -ErrorAction SilentlyContinue)
            if ($recorded) {
                $parsed = 0
                if ([int]::TryParse($recorded.Trim(), [ref]$parsed)) { $exit = $parsed }
            }
        }
    }
    catch {
        $output.Add("[verify-v5-postgres] child invocation threw: $($_.Exception.GetType().FullName): $($_.Exception.Message)")
    }
    finally {
        $env:PATH = $previousPath
        $ErrorActionPreference = $previousEap
    }
    if (Test-Path -LiteralPath $stdoutFile) {
        foreach ($line in (Get-Content -LiteralPath $stdoutFile -ErrorAction SilentlyContinue)) { $output.Add([string]$line) }
    }
    if (Test-Path -LiteralPath $stderrFile) {
        foreach ($line in (Get-Content -LiteralPath $stderrFile -ErrorAction SilentlyContinue)) {
            if ($line) { $output.Add("[stderr] $line") }
        }
    }
    return [pscustomobject]@{ ExitCode = $exit; Output = $output }
}

function Copy-ClusterEvidenceToStaging {
    # Archives the disposable cluster's server logs, evidence files and the
    # ownership marker into the staging area BEFORE cleanup deletes the root.
    # The data directory and the downloaded binaries are never copied.
    param([string]$ClusterRoot, [string]$StagingDir)
    $archived = New-Object System.Collections.Generic.List[string]
    if (-not (Test-Path -LiteralPath $ClusterRoot)) { return $archived }
    foreach ($sub in @('logs', 'evidence')) {
        $source = Join-Path $ClusterRoot $sub
        if (Test-Path -LiteralPath $source) {
            $dest = Join-Path (Join-Path $StagingDir 'cluster') $sub
            New-Item -ItemType Directory -Force -Path $dest | Out-Null
            foreach ($file in (Get-ChildItem -LiteralPath $source -File -ErrorAction SilentlyContinue)) {
                Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $dest $file.Name) -Force
                $archived.Add("cluster/$sub/$($file.Name)")
            }
        }
    }
    $marker = Join-Path $ClusterRoot 'pg-owner.json'
    if (Test-Path -LiteralPath $marker) {
        Copy-Item -LiteralPath $marker -Destination (Join-Path $StagingDir 'owner-marker.json') -Force
        $archived.Add('owner-marker.json')
    }
    return $archived
}

# --- 0. guards (nothing has been created yet; a guard failure exits directly) ----

$reservedPorts = @(5432, 5433, 55450, 55451, 55452)
if ($reservedPorts -contains $Port) {
    Write-Host "BLOCKED: port $Port is refused by this wrapper (5432/5433 default instances; 55450-55452 reserved by another task). Use the dedicated port, e.g. 55460."
    exit 2
}
if ($Port -lt 1024 -or $Port -gt 65535) {
    Write-Host "BLOCKED: port $Port is not a valid dedicated loopback test port."
    exit 2
}
if ($DbName -notmatch '^mahjong_lobby_test') {
    Write-Host "BLOCKED: database name '$DbName' must start with 'mahjong_lobby_test'."
    exit 2
}

$startScript = Join-Path $script:PgScriptDir 'start-postgres.ps1'
$migrateScript = Join-Path $script:PgScriptDir 'migrate.ps1'
$cleanupScript = Join-Path $script:PgScriptDir 'cleanup-postgres.ps1'
foreach ($required in @($startScript, $migrateScript, $cleanupScript)) {
    if (-not (Test-Path -LiteralPath $required)) {
        Write-Host "BLOCKED: accepted pg script missing: $required"
        exit 2
    }
}
if (-not $VerifyScript) { $VerifyScript = Join-Path $script:RepoRoot 'tools\verify-v5.ps1' }
if (-not (Test-Path -LiteralPath $VerifyScript)) {
    Write-Host "BLOCKED: unified verification script not found: $VerifyScript"
    exit 2
}

$resolvedDotnet = Resolve-WrapperDotnetPath -Explicit $DotnetPath
if ($null -eq $resolvedDotnet -or -not (Test-Path -LiteralPath $resolvedDotnet.Path)) {
    Write-Host "BLOCKED: dotnet.exe not found (pass -DotnetPath or set DOTNET_ROOT)."
    exit 2
}

if (-not $LogDir) {
    # Fresh, collision-free directory under the repository's evidence root.
    while ($true) {
        $candidate = Join-Path $script:RepoRoot (
            "artifacts\verify-v5\{0}-pg-{1}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), [guid]::NewGuid().ToString('N').Substring(0, 8))
        if (-not (Test-Path -LiteralPath $candidate)) { $LogDir = $candidate; break }
    }
}
if (Test-Path -LiteralPath $LogDir) {
    Write-Host "BLOCKED: -LogDir already exists: $LogDir. The verifier would rename it and the PG evidence would be split from the verifier logs. Pass a fresh directory."
    exit 2
}

if (-not $Root) {
    # Unique root OUTSIDE the worktree; the accepted scripts additionally refuse
    # drive roots and anything inside the repository.
    $Root = Join-Path ([IO.Path]::GetTempPath()) (
        "mahjong-p53-pg-ci-{0}-{1}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), [guid]::NewGuid().ToString('N').Substring(0, 8))
}
$rootFull = [System.IO.Path]::GetFullPath($Root)
if ($rootFull.StartsWith($script:RepoRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    Write-Host "BLOCKED: refusing a disposable cluster root inside the worktree: $rootFull"
    exit 2
}

# Staging lives outside the repository and outside the cluster root (which
# cleanup deletes); it is flushed into <LogDir>\pg in the finally block. It
# also receives the child processes' redirected output files (see
# Invoke-ScriptChild), so it must exist before the first child is spawned.
$stagingDir = Join-Path ([IO.Path]::GetTempPath()) (
    "verify-v5-postgres-staging-{0}-{1}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $stagingDir | Out-Null
$pgEvidencePath = Join-Path $stagingDir 'postgres-evidence.jsonl'

Write-Step "repo       : $script:RepoRoot"
Write-Step "dotnet     : $($resolvedDotnet.Path) (source: $($resolvedDotnet.Source))"
Write-Step "cluster    : root=$rootFull port=$Port db=$DbName"
Write-Step "zip        : $(if ($PgZipPath) { $PgZipPath } else { '(official download; SHA-256 pinned by start-postgres.ps1)' })"
Write-Step "logdir     : $LogDir"

# --- 1. main flow ----------------------------------------------------------------

$envSnapshot = Save-PgEnvironmentSnapshot
$headInfo = @{ Head = '(unknown)'; Branch = '(unknown)' }
$previousEapGit = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    $head = (& git -C $script:RepoRoot rev-parse HEAD 2>$null | Out-String).Trim()
    if ($head) { $headInfo.Head = $head }
    $branch = (& git -C $script:RepoRoot rev-parse --abbrev-ref HEAD 2>$null | Out-String).Trim()
    if ($branch) { $headInfo.Branch = $branch }
} catch { $headInfo.Head = "git unavailable: $($_.Exception.Message)" }
finally { $ErrorActionPreference = $previousEapGit }

$setupError = $null
$clusterArchive = @()
$identityLine = ''

try {
    # --- stage: port pre-check (refuse; never kill an unknown listener) ----------
    if (Test-WrapperTcpPortOpen -Port $Port) {
        throw "Port 127.0.0.1:$Port is already in use. Refusing to start or disturb an existing listener; choose another dedicated port."
    }
    Add-Stage -Name 'precheck' -Status 'PASS' -ExitCode 0 -Detail "port 127.0.0.1:$Port free" -LogName ''

    # --- stage: start the disposable cluster (accepted script owns all checks) ---
    Write-Step "starting disposable PostgreSQL via start-postgres.ps1..."
    $startParams = @{ Root = $rootFull; Port = "$Port"; DbName = $DbName }
    if ($PgZipPath) { $startParams['ZipPath'] = $PgZipPath }
    $start = Invoke-ScriptChild -ScriptPath $startScript -Parameters $startParams
    Add-StagedLog -Name 'start-postgres' -Lines $start.Output
    foreach ($line in $start.Output) {
        if ($line -match 'Server identity:\s*(.+)$') { $identityLine = $Matches[1].Trim() }
    }
    if ($null -eq $start.ExitCode -or $start.ExitCode -ne 0) {
        throw "start-postgres.ps1 failed (exit $(if ($null -ne $start.ExitCode) { $start.ExitCode } else { 'EXCEPTION' })). The gate cannot run without the disposable cluster; there is no skip/memory fallback."
    }
    Add-Stage -Name 'start-postgres' -Status 'PASS' -ExitCode $start.ExitCode -Detail "identity: $identityLine" -LogName 'start-postgres.log'

    # --- stage: prove the ownership marker the tests will demand exists ----------
    $markerPath = Join-Path $rootFull 'pg-owner.json'
    if (-not (Test-Path -LiteralPath $markerPath)) {
        throw "start-postgres.ps1 reported success but the ownership marker is missing: $markerPath"
    }
    Add-Stage -Name 'ownership-marker' -Status 'PASS' -ExitCode 0 -Detail $markerPath -LogName ''

    # --- stage: environment for the child test run --------------------------------
    [Environment]::SetEnvironmentVariable('MAHJONG_LOBBY_TEST_POSTGRES', "Host=127.0.0.1;Port=$Port;Database=$DbName;Username=mahjong_pg")
    [Environment]::SetEnvironmentVariable('MAHJONG_LOBBY_PG_ROOT', $rootFull)
    [Environment]::SetEnvironmentVariable('MAHJONG_LOBBY_PG_DATA', (Join-Path $rootFull 'data'))
    [Environment]::SetEnvironmentVariable('MAHJONG_LOBBY_PG_EVIDENCE', $pgEvidencePath)
    Add-Stage -Name 'environment' -Status 'PASS' -ExitCode 0 -Detail "4 variables set for the child (pre-call values restored in finally)" -LogName ''

    # --- stage: migration twice (accepted deployment step, repeatability proof) --
    Write-Step "applying the lobby migration twice via migrate.ps1..."
    $migrate = Invoke-ScriptChild -ScriptPath $migrateScript -Parameters @{ Root = $rootFull; Port = "$Port"; DbName = $DbName; Times = '2' }
    Add-StagedLog -Name 'migrate' -Lines $migrate.Output
    if ($null -eq $migrate.ExitCode -or $migrate.ExitCode -ne 0) {
        throw "migrate.ps1 failed (exit $(if ($null -ne $migrate.ExitCode) { $migrate.ExitCode } else { 'EXCEPTION' }))."
    }
    Add-Stage -Name 'migrate' -Status 'PASS' -ExitCode $migrate.ExitCode -Detail '001_lobby.sql applied twice, exit 0 each run' -LogName 'migrate.log'

    # --- stage: the REAL unified verifier, unmodified, in a child process ---------
    Write-Step "running the unified verifier: $VerifyScript"
    $verifyParams = @{
        Configuration = $Configuration
        RunnerMode    = $RunnerMode
        LogDir        = $LogDir
        DotnetPath    = $resolvedDotnet.Path
    }
    if ($ManifestPath) { $verifyParams['ManifestPath'] = $ManifestPath }
    $verifySwitches = @()
    if ($SkipPluginCopyCheck) { $verifySwitches += 'SkipPluginCopyCheck' }
    if ($NoProjectDiscovery) { $verifySwitches += 'NoProjectDiscovery' }
    $verify = Invoke-ScriptChild -ScriptPath $VerifyScript -Parameters $verifyParams -Switches $verifySwitches
    Add-StagedLog -Name 'verifier-console' -Lines $verify.Output
    $script:VerifierRan = $true
    $script:VerifierExit = $verify.ExitCode
    Add-Stage -Name 'verify' -Status $(if ($null -ne $script:VerifierExit -and $script:VerifierExit -eq 0) { 'PASS' } else { 'FAIL' }) `
        -ExitCode $script:VerifierExit -Detail "unified verifier exit propagated verbatim" -LogName 'verifier-console.log'
}
catch {
    $setupError = $_.Exception.Message
    Add-StagedLog -Name 'wrapper-error' -Lines @($setupError, $_.ScriptStackTrace)
    Add-Stage -Name 'wrapper' -Status 'FAIL' -ExitCode $null -Detail $setupError -LogName 'wrapper-error.log'
}

# --- 2. finally: evidence, cleanup, env restore -----------------------------------
try {
    # Archive cluster logs/marker BEFORE cleanup deletes the root.
    try {
        $clusterArchive = @(Copy-ClusterEvidenceToStaging -ClusterRoot $rootFull -StagingDir $stagingDir)
        Add-Stage -Name 'archive-cluster-evidence' -Status 'PASS' -ExitCode 0 -Detail "$(@($clusterArchive).Count) file(s)" -LogName ''
    }
    catch {
        Add-Stage -Name 'archive-cluster-evidence' -Status 'FAIL' -ExitCode $null -Detail $_.Exception.Message -LogName ''
    }

    # Cleanup ONLY this run's owned cluster (the accepted script re-verifies the
    # marker, the absolute path and the server data_directory before deleting).
    # A root that was never created has nothing owned to clean.
    if (Test-Path -LiteralPath $rootFull) {
        $cleanup = Invoke-ScriptChild -ScriptPath $cleanupScript -Parameters @{ Root = $rootFull; Port = "$Port"; DbName = $DbName }
        Add-StagedLog -Name 'cleanup' -Lines $cleanup.Output
        if ($null -eq $cleanup.ExitCode -or $cleanup.ExitCode -ne 0) {
            Add-Stage -Name 'cleanup' -Status 'FAIL' -ExitCode $cleanup.ExitCode -Detail "the disposable root may still exist at $rootFull; the original stage failures above are preserved" -LogName 'cleanup.log'
        }
        else {
            Add-Stage -Name 'cleanup' -Status 'PASS' -ExitCode $cleanup.ExitCode -Detail "owned cluster deleted: $rootFull" -LogName 'cleanup.log'
        }
    }
    else {
        Add-StagedLog -Name 'cleanup' -Lines @('(cleanup skipped: the cluster root was never created - nothing owned to clean)')
        Add-Stage -Name 'cleanup' -Status 'PASS' -ExitCode 0 -Detail 'root was never created; nothing to clean' -LogName 'cleanup.log'
    }

    # Restore the pre-call environment values.
    $envRestoreRecords = @(Restore-PgEnvironmentSnapshot -Snapshot $envSnapshot)
    $restoredAll = @($envRestoreRecords | Where-Object { -not $_.restored }).Count -eq 0
    Add-Stage -Name 'env-restore' -Status $(if ($restoredAll) { 'PASS' } else { 'FAIL' }) -ExitCode $(if ($restoredAll) { 0 } else { $null }) `
        -Detail "values recorded as SHA-256 only" -LogName ''

    # Flush staging into <LogDir>\pg (created even when the verifier never ran),
    # write the SHA-256 manifest and the wrapper summary. Evidence is never
    # deleted from here.
    try {
        $pgDir = Join-Path $LogDir 'pg'
        New-Item -ItemType Directory -Force -Path $pgDir | Out-Null
        foreach ($name in $script:StagedLogs.Keys) {
            $logPath = Join-Path $pgDir $name
            $script:StagedLogs[$name] | Out-String | Set-Content -LiteralPath "$logPath.log" -Encoding UTF8
        }
        foreach ($record in $envRestoreRecords) {
            ([pscustomobject]$record | ConvertTo-Json -Compress) |
                Add-Content -LiteralPath (Join-Path $pgDir 'env-restore.jsonl') -Encoding UTF8
        }
        # Flush the archived cluster files (server logs, evidence files), the
        # ownership marker and the test-side evidence stream. The data directory
        # and downloaded binaries are never copied.
        foreach ($map in @(
            @{ Source = (Join-Path $stagingDir 'cluster\logs'); Dest = (Join-Path $pgDir 'root-logs') },
            @{ Source = (Join-Path $stagingDir 'cluster\evidence'); Dest = (Join-Path $pgDir 'root-evidence') }
        )) {
            if (Test-Path -LiteralPath $map.Source) {
                New-Item -ItemType Directory -Force -Path $map.Dest | Out-Null
                Copy-Item -Path (Join-Path $map.Source '*') -Destination $map.Dest -Force
            }
        }
        foreach ($fileName in @('owner-marker.json', 'postgres-evidence.jsonl')) {
            $stagedFile = Join-Path $stagingDir $fileName
            if (Test-Path -LiteralPath $stagedFile) {
                Copy-Item -LiteralPath $stagedFile -Destination (Join-Path $pgDir $fileName) -Force
            }
        }

        $plannedExit = 0
        if ($null -ne $script:VerifierExit -and $script:VerifierExit -ne 0) { $plannedExit = $script:VerifierExit }
        elseif ($script:FailedStages.Count -gt 0 -or $null -eq $script:VerifierExit) { $plannedExit = 1 }

        # The write-evidence stage completes exactly when this summary is
        # written, so it cannot be appended to $script:Stages beforehand;
        # synthesize its (already-known-to-be-reaching-this-point) entry into
        # the serialized list. A failure in any of the writes below still lands
        # in the catch as a write-evidence FAIL stage. (Copied via ToArray():
        # PS 5.1's @() on a List[object] holding OrderedDictionary entries
        # throws a bare "parameter type mismatch" ArgumentException.)
        $summaryStages = $script:Stages.ToArray()
        $summaryStages += [pscustomobject]@{
            stage    = 'write-evidence'
            status   = 'PASS'
            exitCode = 0
            detail   = $pgDir
            log      = ''
        }

        $summary = [ordered]@{
            schema        = 'verify-v5-postgres-wrapper-summary/1'
            utcStart      = $script:WrapperStartUtc.ToString('o')
            utcEnd        = (Get-Date).ToUniversalTime().ToString('o')
            head          = $headInfo.Head
            branch        = $headInfo.Branch
            configuration = $Configuration
            runnerMode    = $RunnerMode
            manifestPath  = $(if ($ManifestPath) { $ManifestPath } else { '(verifier default)' })
            verifyScript  = $VerifyScript
            dotnet        = [ordered]@{ path = $resolvedDotnet.Path; source = $resolvedDotnet.Source }
            cluster       = [ordered]@{
                root          = $rootFull
                port          = $Port
                database      = $DbName
                zipSource     = $(if ($PgZipPath) { $PgZipPath } else { 'official download (SHA-256 pinned by start-postgres.ps1)' })
                serverIdentity = $identityLine
                rootDeleted   = (-not (Test-Path -LiteralPath $rootFull))
            }
            # synthesized write-evidence entry (see $summaryStages above)
            stages        = $summaryStages
            verifier      = [ordered]@{
                ran      = [bool]$script:VerifierRan
                exitCode = $(if ($null -ne $script:VerifierExit) { $script:VerifierExit } else { 'NOT_RUN' })
                logDir   = $LogDir
            }
            environment   = [ordered]@{
                set = [ordered]@{
                    'MAHJONG_LOBBY_TEST_POSTGRES' = "Host=127.0.0.1;Port=$Port;Database=$DbName;Username=mahjong_pg"
                    'MAHJONG_LOBBY_PG_ROOT'       = $rootFull
                    'MAHJONG_LOBBY_PG_DATA'       = (Join-Path $rootFull 'data')
                    'MAHJONG_LOBBY_PG_EVIDENCE'   = '<staging>\postgres-evidence.jsonl -> <logdir>\pg\'
                }
                restoredToPreCall = $restoredAll
                records           = $envRestoreRecords
            }
            failedStages  = @($script:FailedStages)
            setupError    = $(if ($setupError) { $setupError } else { $null })
            plannedExit   = $plannedExit
        }
        ([pscustomobject]$summary | ConvertTo-Json -Depth 8) |
            Set-Content -LiteralPath (Join-Path $pgDir 'wrapper-summary.json') -Encoding UTF8
        $script:StepLines | Out-String | Set-Content -LiteralPath (Join-Path $pgDir 'wrapper-steps.log') -Encoding UTF8
        # The SHA-256 manifest is computed LAST so it covers the summary and
        # step log too; it never lists itself.
        $shaLines = New-Object System.Collections.Generic.List[string]
        foreach ($file in (Get-ChildItem -LiteralPath $pgDir -Recurse -File)) {
            if ($file.Name -eq 'evidence-sha256.txt') { continue }
            $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            $rel = $file.FullName.Substring($pgDir.Length).TrimStart('\', '/')
            $shaLines.Add("$hash  $rel")
        }
        $shaLines | Set-Content -LiteralPath (Join-Path $pgDir 'evidence-sha256.txt') -Encoding UTF8
        Add-Stage -Name 'write-evidence' -Status 'PASS' -ExitCode 0 -Detail $pgDir -LogName ''
    }
    catch {
        Add-Stage -Name 'write-evidence' -Status 'FAIL' -ExitCode $null -Detail $_.Exception.Message -LogName ''
    }
    finally {
        # Remove the temp staging area only after proving the delete target is
        # this run's own uniquely-named directory.
        try {
            $stagingFull = [System.IO.Path]::GetFullPath($stagingDir)
            if ($stagingFull.StartsWith(([IO.Path]::GetTempPath()), [System.StringComparison]::OrdinalIgnoreCase) -and
                $stagingFull -match 'verify-v5-postgres-staging-') {
                Remove-Item -LiteralPath $stagingFull -Recurse -Force -ErrorAction SilentlyContinue
            }
        } catch { }
    }
} catch {
    # The finally body itself is guarded per stage; this catch must never fire,
    # but a failure here must still not mask the verifier exit.
    Write-Host "[verify-v5-postgres] UNEXPECTED finally failure: $($_.Exception.Message)"
    $script:FailedStages.Add('finally')
}

# --- 3. exit -----------------------------------------------------------------------
$finalExit = 0
if ($null -ne $script:VerifierExit -and $script:VerifierExit -ne 0) {
    # Propagate the unified verifier's exit code verbatim (0/1/2 or a stub code).
    $finalExit = $script:VerifierExit
}
elseif ($null -eq $script:VerifierExit -or $script:FailedStages.Count -gt 0) {
    # Setup/evidence/cleanup/env-restore failure, or the verifier never ran.
    $finalExit = 1
}

Write-Step ("verifier exit : {0}" -f $(if ($null -ne $script:VerifierExit) { $script:VerifierExit } else { 'NOT_RUN' }))
Write-Step ("failed stages : {0}" -f $(if ($script:FailedStages.Count) { $script:FailedStages -join ', ' } else { '(none)' }))
Write-Step ("overall exit  : {0}" -f $finalExit)
exit $finalExit
