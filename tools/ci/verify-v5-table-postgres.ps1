#requires -Version 5.1
<#
.SYNOPSIS
    Repeatable real-PostgreSQL verification for P5.2 table events, receipts,
    leases, checkpoints, scopes and settlement outbox.

.DESCRIPTION
    The wrapper owns one disposable loopback-only PostgreSQL cluster on a unique
    non-default port under this worktree's ignored artifacts directory. It never
    stops or attaches to an existing PostgreSQL service, never uses the lobby
    wrapper port 55460, and restores every process environment variable in
    finally. The application payload key is generated in memory per run and is
    never written to a log, SQL file or the repository.

    Port selection is explicit-or-bounded-default:
      * -Port <n> is EXACT. If it is invalid, reserved, already listened on, or
        refuses an exclusive IPv4 loopback bind, the run is BLOCKED; the port is
        never silently replaced.
      * Without -Port, the wrapper starts at -DefaultPort (55592) and takes the
        first of at most -PortCandidateLimit (default 16) consecutive ports that
        has no active TCP listener AND accepts an exclusive 127.0.0.1 bind. The
        lobby port is skipped when -RunUnifiedVerification is set. If every
        candidate fails, the run is BLOCKED and names each candidate state; it
        never falls through to an unverified port.

    A connect-only probe is insufficient: Windows can reject bind() with
    WSAEACCES (10013, "Permission denied") for an administered/excluded port
    range or for a port held by another exclusive-address socket while nothing
    accepts connections. The probe therefore checks the active-listener table
    and then performs a real exclusive bind. It releases that socket before
    pg_ctl binds, so a small TOCTOU window remains; a genuine startup failure is
    reported as FAIL with the native pg_ctl exit code and the preserved server
    log, and is never retried, hidden, or remapped to another port.

    Stages:
      1. isolated cluster + two idempotent migrations (the test assembly repeats)
      2. fresh Infrastructure/Test/CrashWorker builds
      3. direct xUnit runner (real selected tests, zero skips accepted)
      4. independent writer -> self-kill after durable commit -> immediate
         PostgreSQL crash restart -> independent reader/recovery
      5. pg_dump from the live test DB -> restore into a new empty DB -> reader
      6. ownership-verified stop/delete, logs, XML, SHA-256 manifest and summary

    -RunUnifiedVerification forwards -RunnerMode (default direct) to
    tools/ci/verify-v5-postgres.ps1 -> tools/verify-v5.ps1 for the discovered
    14-project run. Opt in to dotnetTest explicitly; the table-specific direct
    stage and its XML gate are not changed by the mode.

    A missing PG binary/key/dotnet path is BLOCKED (exit 2), never a skipped or
    zero-test green.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    # Unified-verifier runner mode for the -RunUnifiedVerification leg. The
    # default stays 'direct', which is the current CI/acceptance behavior; a
    # caller must opt in to 'dotnetTest' explicitly. The table-specific stages
    # (including their native XML gate) are unchanged in every mode.
    [ValidateSet('auto', 'dotnetTest', 'direct')]
    [string]$RunnerMode = 'direct',
    [string]$DotnetPath,
    # Explicit port: used exactly or the run is BLOCKED; never substituted.
    [int]$Port,
    # First candidate only when -Port was not supplied.
    [int]$DefaultPort = 55592,
    # Upper bound on consecutive default-port candidates (1..64).
    [int]$PortCandidateLimit = 16,
    [string]$Root,
    [string]$LogDir,
    [string]$PgZipPath,
    # Caller integration: keep this table cluster alive while the existing lobby
    # wrapper runs the unchanged auto-discovering verifier against BOTH stores.
    [switch]$RunUnifiedVerification,
    [string]$UnifiedLogDir,
    [string]$LobbyRoot,
    [int]$LobbyPort = 55593
)

$ErrorActionPreference = 'Stop'
$script:RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$script:Stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = Join-Path $script:RepoRoot ("artifacts\p52-pg-cluster-" + $script:Stamp)
}
if ([string]::IsNullOrWhiteSpace($LogDir)) {
    $LogDir = Join-Path $script:RepoRoot ("artifacts\p52-table-postgres-" + $script:Stamp)
}
$Root = [System.IO.Path]::GetFullPath($Root)
$LogDir = [System.IO.Path]::GetFullPath($LogDir)
$script:LogDir = $LogDir
$script:Stages = New-Object System.Collections.Generic.List[object]
$script:Lines = New-Object System.Collections.Generic.List[string]
$script:Exit = 0
$script:ClusterStarted = $false
$script:OwnedMarker = $null
$script:PortSelection = $null
$script:EnvRestoreRecords = @()
$script:PgBin = Join-Path $Root 'pgsql\bin'
$script:PgCtl = Join-Path $script:PgBin 'pg_ctl.exe'
$script:InitDb = Join-Path $script:PgBin 'initdb.exe'
$script:Psql = Join-Path $script:PgBin 'psql.exe'
$script:Createdb = Join-Path $script:PgBin 'createdb.exe'
$script:Dropdb = Join-Path $script:PgBin 'dropdb.exe'
$script:PgIsReady = Join-Path $script:PgBin 'pg_isready.exe'
$script:PgDump = Join-Path $script:PgBin 'pg_dump.exe'
$script:PgRestore = Join-Path $script:PgBin 'pg_restore.exe'
$script:DataDir = Join-Path $Root 'data'
$script:Marker = Join-Path $Root 'pg-owner.json'
$script:PgLog = Join-Path $Root 'logs\postgres.log'
$script:Database = 'mahjong_p52_test'
$script:RestoreDatabase = 'mahjong_p52_restore'
$script:PgZipSha256 = 'EBB3B6AF4FA69DEA9951B66855BC4D42DC04E56CCB9AA7024CE3C58BD89D6B0C'
$script:EnvNames = @(
    'MAHJONG_P52_TEST_POSTGRES',
    'MAHJONG_P52_TEST_POSTGRES_ADMIN',
    'MAHJONG_P52_PG_EVIDENCE',
    'MAHJONG_TABLE_PG_PAYLOAD_KEY',
    'MAHJONG_P52_CRASH_MARKER',
    'MAHJONG_P52_CRASH_RESULT',
    'MAHJONG_P54B_TEST_PG',
    'MAHJONG_P54B_TEST_PG_ADMIN',
    'MAHJONG_P54B_TEST_PG_EMPTY',
    'MAHJONG_P54B_TEST_PG_KEY',
    'MAHJONG_P54B_TEST_PG_EVIDENCE',
    'DOTNET_CLI_UI_LANGUAGE',
    'DOTNET_CLI_TELEMETRY_OPTOUT',
    'DOTNET_NOLOGO',
    'TEMP',
    'TMP'
)
$script:EnvSnapshot = @{}
foreach ($name in $script:EnvNames) {
    $script:EnvSnapshot[$name] = [System.Environment]::GetEnvironmentVariable($name, 'Process')
}
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

function Write-Step([string]$Message) {
    $line = '[p52-pg] ' + $Message
    Write-Host $line
    $script:Lines.Add($line)
}

function Add-Stage([string]$Name, [string]$Status, [object]$ExitCode, [string]$Detail) {
    $script:Stages.Add([ordered]@{ stage = $Name; status = $Status; exitCode = $ExitCode; detail = $Detail })
    Write-Step ("stage {0}: {1} (exit {2}) {3}" -f $Name, $Status, $(if ($null -eq $ExitCode) { 'EXCEPTION' } else { $ExitCode }), $Detail)
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Get-TextSha256([string]$Text) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
        return ([System.BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '')
    }
    finally { $sha.Dispose() }
}

# Connect-only liveness check. Used ONLY by cleanup to decide whether the owned
# postmaster is currently accepting connections; it is NOT a port preflight.
function Test-TcpListenerReachable([int]$Value) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $task = $client.ConnectAsync('127.0.0.1', $Value)
        if (-not $task.Wait(400)) { return $false }
        return $client.Connected
    }
    catch { return $false }
    finally { $client.Dispose() }
}

<#
.SYNOPSIS
    Proves whether PostgreSQL could actually bind 127.0.0.1:<port> right now.
.DESCRIPTION
    A successful TCP connect is not a port-availability proof. On Windows,
    bind() can fail with WSAEACCES (10013) while the port has no listener at all
    (administered/excluded range, or another socket holding the port with
    SO_EXCLUSIVEADDRUSE). This probe therefore:
      1. rejects out-of-range and reserved ports,
      2. consults the OS active-listener table (no socket side effects),
      3. attempts a real exclusive IPv4 loopback bind and classifies the socket
         error instead of collapsing every failure to "free" or "in use".
    The probe closes its socket before returning, so it cannot reserve the port
    for pg_ctl; that TOCTOU window is inherent and is reported honestly later.
#>
function Get-PortProbe {
    param(
        [int]$Value,
        [string]$Address = '127.0.0.1',
        [int[]]$ReservedPorts = @(5432, 5433, 55460, 55463)
    )
    $result = [ordered]@{
        port = $Value
        address = $Address
        state = 'Unbindable'
        errorCode = 0
        detail = ''
    }
    if ($Value -lt 1 -or $Value -gt 65535) {
        $result.state = 'Invalid'
        $result.detail = "port $Value is outside 1..65535"
        return $result
    }
    if ($ReservedPorts -contains $Value) {
        $result.state = 'Reserved'
        $result.detail = "port $Value is reserved by this wrapper"
        return $result
    }

    $listeners = @()
    try {
        $listeners = @([System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() |
            Where-Object { $_.Port -eq $Value })
    }
    catch {
        # A listener-table read failure must not be turned into "free": fall
        # through to the real bind probe, which is authoritative.
        $result.detail = "active-listener table unavailable: $($_.Exception.Message). "
    }
    if ($listeners.Count -gt 0) {
        $result.state = 'InUse'
        $result.detail += 'active TCP listener(s): ' + (($listeners | ForEach-Object { "$($_.Address):$($_.Port)" }) -join ', ')
        return $result
    }

    $listener = $null
    try {
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Parse($Address), $Value)
        # Match the Windows semantics PostgreSQL relies on: an exclusive
        # address-use socket is exactly what makes a foreign reservation
        # surface as WSAEACCES rather than a silent shared bind.
        $listener.Server.ExclusiveAddressUse = $true
        $listener.Start()
        $result.state = 'Bindable'
        return $result
    }
    catch {
        $socketException = $_.Exception
        while ($null -ne $socketException -and -not ($socketException -is [System.Net.Sockets.SocketException])) {
            $socketException = $socketException.InnerException
        }
        if ($null -ne $socketException) {
            $result.errorCode = [int]$socketException.SocketErrorCode
            $result.detail += $socketException.Message
            switch ($result.errorCode) {
                10013 { $result.state = 'AccessDenied' }
                10048 { $result.state = 'InUse' }
                default { $result.state = 'Unbindable' }
            }
        }
        else {
            $result.detail += $_.Exception.Message
        }
        return $result
    }
    finally {
        if ($null -ne $listener) {
            try { $listener.Stop() } catch { }
        }
    }
}

<#
.SYNOPSIS
    Resolves the one table port this run may use.
.DESCRIPTION
    Explicit mode (-ExplicitPort) is exact and fails closed; it never falls
    through to another port. Default mode scans at most -CandidateLimit
    consecutive ports from -DefaultPort and returns the first that is bindable
    and not excluded (the lobby port). Every rejection is recorded so an
    exhausted scan is a named BLOCKED, not a fake PASS.
    -Probe exists as a narrow dependency-injection seam for the self-test (a
    synthetic WSAEACCES cannot be created on every machine); production passes
    no -Probe and uses Get-PortProbe.
#>
function Resolve-TablePort {
    param(
        [int]$RequestedPort,
        [switch]$ExplicitPort,
        [int]$DefaultPort = 55592,
        [int]$CandidateLimit = 16,
        [int[]]$ReservedPorts = @(5432, 5433, 55460, 55463),
        [int[]]$ExcludedPorts = @(),
        [scriptblock]$Probe = $null
    )
    if ($CandidateLimit -lt 1 -or $CandidateLimit -gt 64) {
        throw "BLOCKED: port candidate limit $CandidateLimit is outside 1..64."
    }
    if (-not $ExplicitPort -and ($DefaultPort -lt 1 -or $DefaultPort -gt 65535)) {
        throw "BLOCKED: default port $DefaultPort is outside 1..65535."
    }

    $invokeProbe = {
        param([int]$Candidate)
        if ($null -eq $Probe) {
            return (Get-PortProbe -Value $Candidate -ReservedPorts $ReservedPorts)
        }
        return (& $Probe $Candidate $ReservedPorts)
    }

    if ($ExplicitPort) {
        $probeResult = & $invokeProbe $RequestedPort
        if ($ExcludedPorts -contains $RequestedPort) {
            throw "BLOCKED: explicit table port $RequestedPort equals the lobby port; refusing to share or silently substitute another port."
        }
        if ($probeResult.state -ne 'Bindable') {
            throw ("BLOCKED: explicit table port 127.0.0.1:$RequestedPort is not usable " +
                "(state=$($probeResult.state), socketError=$($probeResult.errorCode), detail=$($probeResult.detail)). " +
                'Refusing to substitute another port.')
        }
        return [ordered]@{ mode = 'explicit'; port = $RequestedPort; candidateIndex = 0; candidates = @("$RequestedPort=Bindable") }
    }

    $candidates = @()
    for ($offset = 0; $offset -lt $CandidateLimit; $offset++) {
        $candidate = $DefaultPort + $offset
        if ($candidate -gt 65535) { break }
        $candidates += $candidate
    }
    if ($candidates.Count -eq 0) {
        throw "BLOCKED: default port $DefaultPort yields no candidate in 1..65535."
    }

    $states = @()
    for ($index = 0; $index -lt $candidates.Count; $index++) {
        $candidate = $candidates[$index]
        if ($ExcludedPorts -contains $candidate) {
            $states += "$candidate=lobby-excluded"
            continue
        }
        $probeResult = & $invokeProbe $candidate
        $states += "$candidate=$($probeResult.state)"
        if ($probeResult.state -eq 'Bindable') {
            return [ordered]@{ mode = 'default'; port = $candidate; candidateIndex = $index; candidates = $states }
        }
    }

    throw ("BLOCKED: no bindable default table port in $($candidates[0])..$($candidates[-1]) " +
        "($($candidates.Count) candidate(s), limit $CandidateLimit): $($states -join '; '). " +
        'Refusing to guess an unverified port.')
}

function Get-LogTail([string]$Path, [int]$Lines = 12) {
    if (-not (Test-Path -LiteralPath $Path)) { return '(log missing)' }
    try {
        $text = @(Get-Content -LiteralPath $Path -Tail $Lines -ErrorAction Stop)
        return (($text -join ' | ') -replace '\s+', ' ').Trim()
    }
    catch { return "(log unreadable: $($_.Exception.Message))" }
}

# Restores every captured process variable and returns value-free evidence:
# names, whether the variable existed before, and a SHA-256 of the restored
# value (or a marker for unset/empty). Secret values are never recorded.
function Restore-ProcessEnvironment {
    param([hashtable]$Snapshot)
    $records = @()
    foreach ($name in @($Snapshot.Keys | Sort-Object)) {
        $target = $Snapshot[$name]
        if ($null -eq $target) {
            Remove-Item -LiteralPath ('Env:' + $name) -ErrorAction SilentlyContinue
        }
        else {
            Set-Item -LiteralPath ('Env:' + $name) -Value $target -ErrorAction Stop
        }
        $item = Get-Item -LiteralPath ('Env:' + $name) -ErrorAction SilentlyContinue
        $actual = if ($null -eq $item) { $null } else { [string]$item.Value }
        $restored = if ($null -eq $target) { $null -eq $actual } else { $actual -ceq $target }
        $sha = if ($null -eq $target) { '(unset)' } elseif ($target -eq '') { '(empty)' } else { Get-TextSha256 $target }
        $records += [ordered]@{
            variable = $name
            wasSetBeforeRun = ($null -ne $target)
            restored = [bool]$restored
            restoredSha256 = $sha
        }
    }
    return $records
}

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$LogPath,
        [int]$TimeoutSeconds = 180
    )
    $out = $LogPath + '.stdout'
    $err = $LogPath + '.stderr'
    Remove-Item -LiteralPath $out, $err -Force -ErrorAction SilentlyContinue
    $argumentLine = ($Arguments | ForEach-Object {
        $value = [string]$_
        if ($value -match '[\s"]') {
            '"' + ($value -replace '"', '\"') + '"'
        }
        else {
            $value
        }
    }) -join ' '
    $process = Start-Process -FilePath $FilePath -ArgumentList $argumentLine -WindowStyle Hidden -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
    # Windows PowerShell 5.1 can otherwise lose the native exit code after
    # WaitForExit: ExitCode becomes null, and [int]null silently becomes 0.
    # Retain the process handle while it is running and fail closed if the
    # platform still cannot supply an exit code.
    $null = $process.Handle
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        try { $process.Kill() } catch { }
        throw "Command timed out after $TimeoutSeconds seconds: $FilePath"
    }

    $text = ''
    if (Test-Path -LiteralPath $out) { $text += (Get-Content -LiteralPath $out -Raw) }
    if (Test-Path -LiteralPath $err) {
        $errorText = Get-Content -LiteralPath $err -Raw
        if ($errorText) { $text += $errorText }
    }
    Set-Content -LiteralPath $LogPath -Value $text -Encoding utf8
    $nativeExit = $process.ExitCode
    if ($null -eq $nativeExit) { throw "Native exit code unavailable: $FilePath" }
    return [int]$nativeExit
}

function Wait-PgReady([int]$TimeoutSeconds = 60) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        & $script:PgIsReady -h 127.0.0.1 -p $Port -q | Out-Null
        if ($LASTEXITCODE -eq 0) { return }
        Start-Sleep -Milliseconds 250
    }
    throw "PostgreSQL did not become ready on 127.0.0.1:$Port."
}

function Invoke-WriterAndKill {
    param(
        [Parameter(Mandatory = $true)][string]$WorkerDll,
        [Parameter(Mandatory = $true)][string]$MarkerPath,
        [Parameter(Mandatory = $true)][string]$LogPath,
        [int]$TimeoutSeconds = 90
    )
    $out = $LogPath + '.stdout'
    $err = $LogPath + '.stderr'
    Remove-Item -LiteralPath $out, $err, $MarkerPath -Force -ErrorAction SilentlyContinue
    $process = Start-Process -FilePath $DotnetPath -ArgumentList ('"' + $WorkerDll + '" writer') -WindowStyle Hidden -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
    $null = $process.Handle
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while (-not (Test-Path -LiteralPath $MarkerPath)) {
        if ($process.HasExited) {
            throw "Crash writer exited before its post-commit marker (exit $($process.ExitCode))."
        }
        if ((Get-Date) -gt $deadline) {
            try { $process.Kill() } catch { }
            throw "Crash writer did not produce its post-commit marker within $TimeoutSeconds seconds."
        }
        Start-Sleep -Milliseconds 100
    }

    # Marker exists and is flushed, so the durable transaction already committed.
    # Kill the independent writer process before it can publish anything.
    $writerPid = $process.Id
    Stop-Process -Id $process.Id -Force
    $process.WaitForExit()
    $nativeExit = $process.ExitCode
    if ($null -eq $nativeExit) { throw 'Crash writer native exit code unavailable.' }
    $killRecord = [ordered]@{
        pid = $writerPid
        marker = $MarkerPath
        markerSha256 = (Get-Sha256 $MarkerPath)
        killedUtc = (Get-Date).ToUniversalTime().ToString('o')
        exitCode = $nativeExit
        forced = $true
    }
    $killRecord | ConvertTo-Json | Set-Content -LiteralPath ($LogPath + '.kill.json') -Encoding utf8
    $text = ''
    if (Test-Path -LiteralPath $out) { $text += (Get-Content -LiteralPath $out -Raw) }
    if (Test-Path -LiteralPath $err) { $text += (Get-Content -LiteralPath $err -Raw) }
    Set-Content -LiteralPath $LogPath -Value $text -Encoding utf8
    return [ordered]@{ killed = $true; pid = $writerPid; exitCode = [int]$nativeExit }
}

function Get-PgScalar([string]$Database, [string]$Sql) {
    $args = @('-h', '127.0.0.1', '-p', "$Port", '-U', 'mahjong_pg', '-v', 'ON_ERROR_STOP=1', '-d', $Database, '-tAc', $Sql)
    $log = Join-Path $script:LogDir ('psql-' + [Guid]::NewGuid().ToString('N') + '.log')
    $code = Invoke-Native -FilePath $script:Psql -Arguments $args -LogPath $log -TimeoutSeconds 60
    if ($code -ne 0) { throw "psql failed with exit $code (see $log)." }
    return ((Get-Content -LiteralPath $log -Raw).Trim())
}

function Get-PgDataDirectory {
    return (Get-PgScalar -Database 'postgres' -Sql 'SHOW data_directory').Trim()
}

function Assert-OwnedCluster {
    if (-not (Test-Path -LiteralPath $script:Marker)) {
        throw "Refusing to touch ${Root}: ownership marker is missing."
    }
    $marker = Get-Content -LiteralPath $script:Marker -Raw | ConvertFrom-Json
    if ($marker.owner -ne 'mahjong-p52-pg' -or
        -not [string]::Equals([System.IO.Path]::GetFullPath($marker.root), $Root, [StringComparison]::OrdinalIgnoreCase) -or
        [int]$marker.port -ne $Port) {
        throw "Refusing to touch ${Root}: marker does not belong to this exact root/port."
    }
    return $marker
}

function Initialize-Cluster {
    # Narrow TOCTOU recheck: the port was proven bindable moments ago, but do
    # not silently continue if it changed between preflight and initdb. Any
    # later loss (after this point) is a genuine pg_ctl failure, reported with
    # its native exit and preserved log rather than retried or remapped.
    $recheck = Get-PortProbe -Value $Port
    if ($recheck.state -ne 'Bindable') {
        throw ("BLOCKED: port 127.0.0.1:$Port was bindable at preflight but is not now " +
            "(state=$($recheck.state), socketError=$($recheck.errorCode), detail=$($recheck.detail)); " +
            'refusing to retry or substitute a different port.')
    }

    if (Test-Path -LiteralPath $Root) {
        if (-not (Test-Path -LiteralPath $script:Marker)) {
            $items = @(Get-ChildItem -LiteralPath $Root -Force -ErrorAction SilentlyContinue)
            if ($items.Count -gt 0) {
                throw "BLOCKED: $Root is non-empty and has no P5.2 ownership marker."
            }
        }
    }
    else {
        New-Item -ItemType Directory -Path $Root -Force | Out-Null
    }

    if (-not (Test-Path -LiteralPath $script:Marker)) {
        $marker = [ordered]@{
            owner = 'mahjong-p52-pg'
            root = $Root
            port = $Port
            database = $script:Database
            createdUtc = (Get-Date).ToUniversalTime().ToString('o')
        }
        $marker | ConvertTo-Json | Set-Content -LiteralPath $script:Marker -Encoding utf8
    }
    Assert-OwnedCluster | Out-Null

    if (-not (Test-Path -LiteralPath $script:PgCtl)) {
        $downloadDir = Join-Path $Root 'download'
        New-Item -ItemType Directory -Path $downloadDir -Force | Out-Null
        $zipCopy = Join-Path $downloadDir 'postgresql-binaries.zip'
        if ($PgZipPath) {
            Copy-Item -LiteralPath $PgZipPath -Destination $zipCopy -Force
        }
        elseif (Test-Path -LiteralPath 'D:\majiang-pg-tmp\cache\postgresql-16.10-1-windows-x64-binaries.zip') {
            Copy-Item -LiteralPath 'D:\majiang-pg-tmp\cache\postgresql-16.10-1-windows-x64-binaries.zip' -Destination $zipCopy
        }
        else {
            $downloadCode = Invoke-Native -FilePath 'curl.exe' -Arguments @(
                '-L', '--fail', '--silent', '--show-error', '--max-time', '300',
                '-o', $zipCopy,
                'https://get.enterprisedb.com/postgresql/postgresql-16.10-1-windows-x64-binaries.zip'
            ) -LogPath (Join-Path $LogDir 'download.log') -TimeoutSeconds 320
            if ($downloadCode -ne 0) { throw "PostgreSQL download failed with exit $downloadCode." }
        }
        $hash = Get-Sha256 $zipCopy
        if ($hash -ne $script:PgZipSha256) {
            throw "BLOCKED: PostgreSQL zip SHA-256 mismatch. Expected $($script:PgZipSha256), got $hash."
        }
        & tar.exe -xf $zipCopy -C $Root
        if ($LASTEXITCODE -ne 0) { throw "Extracting PostgreSQL failed with exit $LASTEXITCODE." }
    }

    if (-not (Test-Path -LiteralPath (Join-Path $script:DataDir 'PG_VERSION'))) {
        $initLog = Join-Path $script:LogDir 'initdb.log'
        $initCode = Invoke-Native -FilePath $script:InitDb -Arguments @(
            '-D', $script:DataDir, '-U', 'mahjong_pg',
            '--auth-local=trust', '--auth-host=trust', '--encoding=UTF8', '--locale=C'
        ) -LogPath $initLog -TimeoutSeconds 180
        if ($initCode -ne 0) { throw "initdb failed with exit $initCode (see $initLog)." }

        $settings = @(
            '',
            '# --- P5.2 disposable verification cluster ---',
            "listen_addresses = '127.0.0.1'",
            "port = $Port",
            "unix_socket_directories = ''",
            'fsync = on',
            'synchronous_commit = on',
            'full_page_writes = on',
            "log_timezone = 'UTC'",
            "timezone = 'UTC'"
        ) -join [Environment]::NewLine
        [System.IO.File]::AppendAllText(
            (Join-Path $script:DataDir 'postgresql.conf'),
            $settings + [Environment]::NewLine,
            (New-Object System.Text.UTF8Encoding($false)))
    }

    $startLog = Join-Path $script:LogDir 'pg-start.log'
    New-Item -ItemType Directory -Path (Join-Path $Root 'logs') -Force | Out-Null
    $startCode = Invoke-Native -FilePath $script:PgCtl -Arguments @(
        '-D', $script:DataDir, '-l', $script:PgLog, '-w', '-t', '60', 'start'
    ) -LogPath $startLog -TimeoutSeconds 90
    if ($startCode -ne 0) {
        # Preserve the native exit code and BOTH original logs. Do not retry
        # pg_ctl and do not delete/stop any other cluster.
        $tail = Get-LogTail -Path $script:PgLog -Lines 20
        $diagnostics = @(
            "pg_ctl native exit: $startCode",
            "pg_ctl log: $startLog",
            "server log: $($script:PgLog)",
            "server log tail: $tail"
        ) -join [Environment]::NewLine
        Set-Content -LiteralPath (Join-Path $script:LogDir 'pg-start-diagnostics.txt') -Value $diagnostics -Encoding utf8
        Add-Stage 'pg-start' 'FAIL' $startCode ("pg_ctl native exit=$startCode; server log tail: " + $tail)
        throw "pg_ctl start failed with native exit $startCode (see $startLog and $($script:PgLog)). $tail"
    }
    Add-Stage 'pg-start' 'PASS' $startCode "pg_ctl native exit=0; server log=$($script:PgLog)"
    $script:ClusterStarted = $true
    Wait-PgReady
    $dataDir = Get-PgDataDirectory
    if (-not [string]::Equals([System.IO.Path]::GetFullPath($dataDir), [System.IO.Path]::GetFullPath($script:DataDir), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to use a listener whose data_directory ($dataDir) is not this cluster."
    }

    $ownedDatabases = @($script:Database, $script:RestoreDatabase)
    if ($RunUnifiedVerification) {
        # The discovered durable-host suite owns separate state and an unmigrated
        # negative-probe database on this same verified disposable cluster.
        $ownedDatabases += @('mahjong_p54b_test', 'mahjong_p54b_empty')
    }
    foreach ($database in $ownedDatabases) {
        $exists = Get-PgScalar -Database 'postgres' -Sql "SELECT 1 FROM pg_database WHERE datname = '$database'"
        if ($exists -ne '1') {
            $createLog = Join-Path $script:LogDir ('createdb-' + $database + '.log')
            $createCode = Invoke-Native -FilePath $script:Createdb -Arguments @(
                '-h', '127.0.0.1', '-p', "$Port", '-U', 'mahjong_pg', $database
            ) -LogPath $createLog -TimeoutSeconds 60
            if ($createCode -ne 0) { throw "createdb $database failed with exit $createCode." }
        }
    }
}

function Restart-ClusterImmediate {
    $before = Get-PgScalar -Database 'postgres' -Sql 'SELECT pg_postmaster_start_time()'
    $offset = 0
    if (Test-Path -LiteralPath $script:PgLog) { $offset = (Get-Item -LiteralPath $script:PgLog).Length }

    $stopLog = Join-Path $script:LogDir 'pg-stop-immediate.log'
    $stopCode = Invoke-Native -FilePath $script:PgCtl -Arguments @(
        '-D', $script:DataDir, '-w', '-t', '60', 'stop', '-m', 'immediate'
    ) -LogPath $stopLog -TimeoutSeconds 90
    if ($stopCode -ne 0) { throw "pg_ctl immediate stop failed with exit $stopCode." }
    $script:ClusterStarted = $false

    $startLog = Join-Path $script:LogDir 'pg-restart.log'
    $startCode = Invoke-Native -FilePath $script:PgCtl -Arguments @(
        '-D', $script:DataDir, '-l', $script:PgLog, '-w', '-t', '60', 'start'
    ) -LogPath $startLog -TimeoutSeconds 90
    if ($startCode -ne 0) {
        $tail = Get-LogTail -Path $script:PgLog -Lines 20
        Add-Stage 'pg-restart' 'FAIL' $startCode ("pg_ctl restart native exit=$startCode; server log tail: " + $tail)
        throw "pg_ctl restart start failed with native exit $startCode (see $startLog and $($script:PgLog)). $tail"
    }
    $script:ClusterStarted = $true
    Wait-PgReady
    $after = Get-PgScalar -Database 'postgres' -Sql 'SELECT pg_postmaster_start_time()'
    if ($before -eq $after) { throw "PostgreSQL postmaster start time did not change; no real restart." }

    $recoveryText = ''
    if (Test-Path -LiteralPath $script:PgLog) {
        $stream = [System.IO.File]::Open($script:PgLog, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        try {
            if ($offset -lt $stream.Length) {
                $stream.Position = $offset
                $reader = New-Object System.IO.StreamReader($stream)
                $recoveryText = $reader.ReadToEnd()
            }
        }
        finally { $stream.Dispose() }
    }
    Set-Content -LiteralPath (Join-Path $script:LogDir 'postgres-restart-recovery.log') -Value $recoveryText -Encoding utf8
    $evidence = $recoveryText -split "`r?`n" | Where-Object {
        $_ -match 'not properly shut down|automatic recovery|redo starts at|database system is ready|consistent recovery state'
    }
    if ($evidence.Count -eq 0) {
        throw "No crash-recovery log lines were found after the immediate restart."
    }
    $evidence | Set-Content -LiteralPath (Join-Path $script:LogDir 'postgres-restart-recovery-lines.log') -Encoding utf8
    return [ordered]@{ before = $before; after = $after; recoveryLines = $evidence }
}

function Stop-And-RemoveCluster {
    if (-not (Test-Path -LiteralPath $Root)) { return }
    $marker = Assert-OwnedCluster
    if (Test-TcpListenerReachable -Value $Port) {
        $dataDir = Get-PgDataDirectory
        if (-not [string]::Equals([System.IO.Path]::GetFullPath($dataDir), [System.IO.Path]::GetFullPath($script:DataDir), [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing cleanup: listener data_directory is not this cluster."
        }
        $stopLog = Join-Path $script:LogDir 'pg-cleanup-stop.log'
        $stopCode = Invoke-Native -FilePath $script:PgCtl -Arguments @(
            '-D', $script:DataDir, '-w', '-t', '60', 'stop', '-m', 'fast'
        ) -LogPath $stopLog -TimeoutSeconds 90
        if ($stopCode -ne 0) { throw "pg_ctl cleanup stop failed with exit $stopCode; refusing deletion." }
        $script:ClusterStarted = $false
    }

    $safeRoot = $Root.TrimEnd('\')
    if ($safeRoot.Length -lt 20 -or
        -not $safeRoot.StartsWith([System.IO.Path]::GetFullPath((Join-Path $script:RepoRoot 'artifacts')) + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing recursive cleanup outside the worktree artifacts root: $safeRoot"
    }

    $processes = @(Get-CimInstance Win32_Process -Filter "Name = 'postgres.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($script:DataDir, [StringComparison]::OrdinalIgnoreCase) -ge 0 })
    if ($processes.Count -gt 0) {
        throw "A postgres.exe process still references $($script:DataDir); refusing deletion."
    }

    Remove-Item -LiteralPath $safeRoot -Recurse -Force
    if (Test-Path -LiteralPath $safeRoot) { throw "Cleanup failed: $safeRoot still exists." }
    Write-Step "ownership-verified cluster removed: $safeRoot"
}

function Get-XmlCounts([string]$XmlPath) {
    if (-not (Test-Path -LiteralPath $XmlPath)) { throw "Result XML is missing: $XmlPath" }
    [xml]$doc = Get-Content -LiteralPath $XmlPath -Raw
    $assembly = $doc.SelectSingleNode('//assembly')
    if ($null -eq $assembly) { throw "Result XML has no assembly element: $XmlPath" }
    foreach ($attribute in @('total', 'passed', 'failed', 'skipped', 'errors', 'not-run')) {
        if (-not $assembly.HasAttribute($attribute)) {
            throw "Result XML is missing required assembly attribute '$attribute': $XmlPath"
        }
    }

    return [ordered]@{
        total = [int]$assembly.GetAttribute('total')
        passed = [int]$assembly.GetAttribute('passed')
        failed = [int]$assembly.GetAttribute('failed')
        skipped = [int]$assembly.GetAttribute('skipped')
        errors = [int]$assembly.GetAttribute('errors')
        notRun = [int]$assembly.GetAttribute('not-run')
    }
}

function Test-XmlCountsPass($Counts) {
    return $Counts.total -gt 0 -and
        $Counts.passed -eq $Counts.total -and
        $Counts.failed -eq 0 -and
        $Counts.errors -eq 0 -and
        $Counts.skipped -eq 0 -and
        $Counts.notRun -eq 0
}

function Assert-XmlGatingSelfTest {
    $tmp = Join-Path $script:LogDir 'tmp\xml-selfcheck'
    New-Item -ItemType Directory -Path $tmp -Force | Out-Null
    $cases = @(
        [ordered]@{ name = 'zero-tests'; attrs = 'total="0" passed="0" failed="0" skipped="0" errors="0" not-run="0"' },
        [ordered]@{ name = 'failed'; attrs = 'total="1" passed="0" failed="1" skipped="0" errors="0" not-run="0"' },
        [ordered]@{ name = 'errors'; attrs = 'total="1" passed="0" failed="0" skipped="0" errors="1" not-run="0"' },
        [ordered]@{ name = 'skipped'; attrs = 'total="1" passed="0" failed="0" skipped="1" errors="0" not-run="0"' },
        [ordered]@{ name = 'not-run'; attrs = 'total="1" passed="0" failed="0" skipped="0" errors="0" not-run="1"' },
        [ordered]@{ name = 'passed-mismatch'; attrs = 'total="2" passed="1" failed="0" skipped="0" errors="0" not-run="0"' },
        [ordered]@{ name = 'missing-not-run'; attrs = 'total="1" passed="1" failed="0" skipped="0" errors="0"' }
    )
    $checked = 0
    foreach ($case in $cases) {
        $path = Join-Path $tmp ($case.name + '.xml')
        $xml = '<assemblies schema-version="3"><assembly ' + $case.attrs + ' /></assemblies>'
        Set-Content -LiteralPath $path -Value $xml -Encoding utf8
        $green = $false
        try {
            $counts = Get-XmlCounts $path
            $green = Test-XmlCountsPass $counts
        }
        catch {
            if ($case.name -ne 'missing-not-run') { throw }
            $green = $false
        }
        if ($green) { throw "XML gating self-check accepted negative case '$($case.name)'." }
        $checked++
    }

    return $checked
}

try {
    New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $LogDir 'tmp') -Force | Out-Null

    # Resolve the port BEFORE any cluster directory, download or build. A
    # caller-supplied -Port is exact; only the default path may scan, and that
    # scan is bounded and skips the lobby port when unified verification runs.
    $explicitPort = $PSBoundParameters.ContainsKey('Port')
    $excludedPorts = @()
    if ($RunUnifiedVerification) {
        if ($LobbyPort -lt 1 -or $LobbyPort -gt 65535) {
            throw "BLOCKED: lobby port $LobbyPort is outside 1..65535."
        }
        $excludedPorts = @($LobbyPort)
    }
    $portDecision = Resolve-TablePort -RequestedPort $Port -ExplicitPort:$explicitPort `
        -DefaultPort $DefaultPort -CandidateLimit $PortCandidateLimit -ExcludedPorts $excludedPorts
    $Port = [int]$portDecision.port
    $script:PortSelection = $portDecision
    Add-Stage 'port-preflight' 'PASS' 0 ("mode={0} port=127.0.0.1:{1} candidateIndex={2} candidates=[{3}]" -f
        $portDecision.mode, $Port, $portDecision.candidateIndex, ($portDecision.candidates -join ', '))

    if (-not $DotnetPath) {
        if ($env:DOTNET_ROOT -and (Test-Path -LiteralPath (Join-Path $env:DOTNET_ROOT 'dotnet.exe'))) {
            $DotnetPath = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
        }
        elseif (Get-Command dotnet -ErrorAction SilentlyContinue) {
            $DotnetPath = (Get-Command dotnet).Source
        }
        elseif (Test-Path -LiteralPath 'D:\Unity\dotnet\dotnet.exe') {
            $DotnetPath = 'D:\Unity\dotnet\dotnet.exe'
        }
    }
    if (-not $DotnetPath) { throw 'BLOCKED: dotnet was not found in DOTNET_ROOT/PATH or the local SDK path.' }
    if (-not (Test-Path -LiteralPath $DotnetPath)) {
        Add-Stage 'guard-dotnet' 'BLOCKED' 2 "dotnet not found: $DotnetPath"
        $script:Exit = 2
        return
    }
    & $DotnetPath --version | Out-Null
    $sdkVersion = (& $DotnetPath --version | Select-Object -First 1)
    $env:TEMP = Join-Path $LogDir 'tmp'
    $env:TMP = Join-Path $LogDir 'tmp'

    Initialize-Cluster
    $serverVersion = Get-PgScalar -Database $script:Database -Sql 'SHOW server_version'
    $dataDirectory = Get-PgDataDirectory
    Add-Stage 'cluster' 'PASS' 0 "server=$serverVersion data_directory=$dataDirectory port=$Port"

    $xmlSelfChecks = Assert-XmlGatingSelfTest
    Add-Stage 'xml-gating-selfcheck' 'PASS' 0 "negative XML cases=$xmlSelfChecks"

    $keyBytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($keyBytes) } finally { $rng.Dispose() }
    $payloadKey = [Convert]::ToBase64String($keyBytes)
    $adminConnection = "Host=127.0.0.1;Port=$Port;Database=$script:Database;Username=mahjong_pg;Pooling=false;Timeout=10;Command Timeout=30"
    $runtimeConnection = "Host=127.0.0.1;Port=$Port;Database=$script:Database;Username=mahjong_table_runtime;Pooling=false;Timeout=10;Command Timeout=30"
    $restoreAdminConnection = "Host=127.0.0.1;Port=$Port;Database=$script:RestoreDatabase;Username=mahjong_pg;Pooling=false;Timeout=10;Command Timeout=30"
    $restoreRuntimeConnection = "Host=127.0.0.1;Port=$Port;Database=$script:RestoreDatabase;Username=mahjong_table_runtime;Pooling=false;Timeout=10;Command Timeout=30"
    $env:MAHJONG_P52_TEST_POSTGRES = $runtimeConnection
    $env:MAHJONG_P52_TEST_POSTGRES_ADMIN = $adminConnection
    $env:MAHJONG_P52_PG_EVIDENCE = (Join-Path $LogDir 'tests')
    $env:MAHJONG_TABLE_PG_PAYLOAD_KEY = $payloadKey

    $testsProject = Join-Path $script:RepoRoot 'dotnet\tests\Mahjong.Infrastructure.Tests\Mahjong.Infrastructure.Tests.csproj'
    $workerProject = Join-Path $script:RepoRoot 'dotnet\tests\Mahjong.Infrastructure.CrashWorker\Mahjong.Infrastructure.CrashWorker.csproj'
    $testsDll = Join-Path $script:RepoRoot ("dotnet\tests\Mahjong.Infrastructure.Tests\bin\$Configuration\net8.0\Mahjong.Infrastructure.Tests.dll")
    $workerDll = Join-Path $script:RepoRoot ("dotnet\tests\Mahjong.Infrastructure.CrashWorker\bin\$Configuration\net8.0\Mahjong.Infrastructure.CrashWorker.dll")

    $buildTestsLog = Join-Path $LogDir 'build-tests.log'
    $buildTestsCode = Invoke-Native -FilePath $DotnetPath -Arguments @(
        'build', $testsProject, '-c', $Configuration, '--no-incremental', '--nologo', '-v:minimal'
    ) -LogPath $buildTestsLog -TimeoutSeconds 300
    if ($buildTestsCode -ne 0) { throw "Fresh test build failed with exit $buildTestsCode (see $buildTestsLog)." }
    $buildWorkerLog = Join-Path $LogDir 'build-worker.log'
    $buildWorkerCode = Invoke-Native -FilePath $DotnetPath -Arguments @(
        'build', $workerProject, '-c', $Configuration, '--no-incremental', '--nologo', '-v:minimal'
    ) -LogPath $buildWorkerLog -TimeoutSeconds 300
    if ($buildWorkerCode -ne 0) { throw "Fresh crash-worker build failed with exit $buildWorkerCode (see $buildWorkerLog)." }
    Add-Stage 'fresh-build' 'PASS' 0 "tests and crash worker built with $sdkVersion"

    $testsXml = Join-Path $LogDir 'tests.xml'
    $testsLog = Join-Path $LogDir 'tests.log'
    $testsCode = Invoke-Native -FilePath $DotnetPath -Arguments @(
        $testsDll, '-noColor', '-noLogo', '-result-xml', $testsXml
    ) -LogPath $testsLog -TimeoutSeconds 900
    $counts = Get-XmlCounts $testsXml
    if ($testsCode -ne 0 -or -not (Test-XmlCountsPass $counts)) {
        throw "Tests failed: exit=$testsCode total=$($counts.total) passed=$($counts.passed) failed=$($counts.failed) errors=$($counts.errors) skipped=$($counts.skipped) notRun=$($counts.notRun)."
    }
    $testsDllSha256 = (Get-FileHash -LiteralPath $testsDll -Algorithm SHA256).Hash
    Add-Stage 'tests' 'PASS' $testsCode "dll=$testsDll sha256=$testsDllSha256 total=$($counts.total) passed=$($counts.passed) failed=0 skipped=0 notRun=0"

    $targetXml = Join-Path $LogDir 'tests-targeted.xml'
    $targetLog = Join-Path $LogDir 'tests-targeted.log'
    $targetCode = Invoke-Native -FilePath $DotnetPath -Arguments @(
        $testsDll, '-noColor', '-noLogo',
        '-class', 'Mahjong.Infrastructure.Tests.PostgresTableStoreTests',
        '-result-xml', $targetXml
    ) -LogPath $targetLog -TimeoutSeconds 600
    $targetCounts = Get-XmlCounts $targetXml
    if ($targetCode -ne 0 -or -not (Test-XmlCountsPass $targetCounts)) {
        throw "Targeted class run failed: exit=$targetCode total=$($targetCounts.total) passed=$($targetCounts.passed) failed=$($targetCounts.failed) errors=$($targetCounts.errors) skipped=$($targetCounts.skipped) notRun=$($targetCounts.notRun)."
    }
    Add-Stage 'tests-targeted' 'PASS' $targetCode "class=PostgresTableStoreTests total=$($targetCounts.total) passed=$($targetCounts.passed) failed=0 skipped=0"

    $resetLog = Join-Path $LogDir 'crash-reset.log'
    $resetCode = Invoke-Native -FilePath $DotnetPath -Arguments @($workerDll, 'reset') -LogPath $resetLog -TimeoutSeconds 120
    if ($resetCode -ne 0) { throw "Crash reset failed with exit $resetCode." }

    $markerPath = Join-Path $LogDir 'crash-commit-marker.json'
    $resultPath = Join-Path $LogDir 'crash-reader-result.json'
    $env:MAHJONG_P52_CRASH_MARKER = $markerPath
    $env:MAHJONG_P52_CRASH_RESULT = $resultPath
    $writerLog = Join-Path $LogDir 'crash-writer.log'
    $writerResult = Invoke-WriterAndKill -WorkerDll $workerDll -MarkerPath $markerPath -LogPath $writerLog -TimeoutSeconds 120
    if (-not (Test-Path -LiteralPath $markerPath)) {
        throw "Crash writer did not leave its post-commit marker (exit $($writerResult.exitCode))."
    }
    if (-not $writerResult.killed) {
        throw "Crash writer was not forcibly killed after its durable commit."
    }
    $markerJson = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
    Add-Stage 'crash-writer' 'PASS' 'KILLED' "pid=$($writerResult.pid), committed state=$($markerJson.stateVersion) seq=$($markerJson.lastEventSeq), killed before publication"

    $restart = Restart-ClusterImmediate
    Add-Stage 'postgres-crash-restart' 'PASS' 0 ("postmaster start changed {0} -> {1}; recovery lines={2}" -f $restart.before, $restart.after, $restart.recoveryLines.Count)

    $readerLog = Join-Path $LogDir 'crash-reader.log'
    Remove-Item -LiteralPath $resultPath -Force -ErrorAction SilentlyContinue
    $readerCode = Invoke-Native -FilePath $DotnetPath -Arguments @($workerDll, 'reader') -LogPath $readerLog -TimeoutSeconds 180
    if ($readerCode -ne 0 -or -not (Test-Path -LiteralPath $resultPath)) {
        throw "Crash reader failed with exit $readerCode (see $readerLog)."
    }
    $readerJson = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if ([int]$readerJson.stateVersion -ne [int]$markerJson.stateVersion -or
        [int]$readerJson.lastEventSeq -ne [int]$markerJson.lastEventSeq -or
        [int]$readerJson.eventCount -ne [int]$markerJson.eventCount -or
        $readerJson.receiptStatus -ne 'Applied' -or
        $readerJson.outboxSha256 -ne $markerJson.outboxSha256 -or
        -not [bool]$readerJson.terminal -or
        -not [bool]$readerJson.outboxPresent -or
        -not [bool]$readerJson.outboxPayloadMatches -or
        -not [bool]$readerJson.cheatStateRestored -or
        -not [bool]$readerJson.recovered) {
        throw "Crash reader did not reproduce terminal receipt/136/hash/ledger/outbox state from the marker."
    }
    Add-Stage 'crash-reader' 'PASS' $readerCode "state=$($readerJson.stateVersion) seq=$($readerJson.lastEventSeq) terminal=$($readerJson.terminal) receipt=$($readerJson.receiptStatus) cheatVersion=$($readerJson.cheatVersion) outbox=$($readerJson.outboxSha256.Substring(0,12))..."

    $dumpPath = Join-Path $LogDir 'table_store.dump'
    $dumpLog = Join-Path $LogDir 'pg-dump.log'
    $dumpCode = Invoke-Native -FilePath $script:PgDump -Arguments @(
        '-h', '127.0.0.1', '-p', "$Port", '-U', 'mahjong_pg',
        '--format=custom', '--schema=table_store', '--file', $dumpPath, $script:Database
    ) -LogPath $dumpLog -TimeoutSeconds 180
    if ($dumpCode -ne 0 -or -not (Test-Path -LiteralPath $dumpPath)) { throw "pg_dump failed with exit $dumpCode." }

    $dropLog = Join-Path $LogDir 'drop-restore-db.log'
    $dropCode = Invoke-Native -FilePath $script:Dropdb -Arguments @(
        '-h', '127.0.0.1', '-p', "$Port", '-U', 'mahjong_pg', '--if-exists', $script:RestoreDatabase
    ) -LogPath $dropLog -TimeoutSeconds 60
    if ($dropCode -ne 0) { throw "dropdb restore failed with exit $dropCode." }
    $createLog = Join-Path $LogDir 'create-restore-db.log'
    $createCode = Invoke-Native -FilePath $script:Createdb -Arguments @(
        '-h', '127.0.0.1', '-p', "$Port", '-U', 'mahjong_pg', $script:RestoreDatabase
    ) -LogPath $createLog -TimeoutSeconds 60
    if ($createCode -ne 0) { throw "createdb restore failed with exit $createCode." }

    $restoreLog = Join-Path $LogDir 'pg-restore.log'
    $restoreCode = Invoke-Native -FilePath $script:PgRestore -Arguments @(
        '-h', '127.0.0.1', '-p', "$Port", '-U', 'mahjong_pg', '--dbname', $script:RestoreDatabase, $dumpPath
    ) -LogPath $restoreLog -TimeoutSeconds 180
    if ($restoreCode -ne 0) { throw "pg_restore failed with exit $restoreCode." }

    $grantSql = "GRANT USAGE ON SCHEMA table_store TO mahjong_table_runtime; GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA table_store TO mahjong_table_runtime; GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA table_store TO mahjong_table_runtime;"
    $grantLog = Join-Path $LogDir 'restore-grant.log'
    $grantCode = Invoke-Native -FilePath $script:Psql -Arguments @(
        '-h', '127.0.0.1', '-p', "$Port", '-U', 'mahjong_pg', '-d', $script:RestoreDatabase, '-v', 'ON_ERROR_STOP=1', '-c', $grantSql
    ) -LogPath $grantLog -TimeoutSeconds 60
    if ($grantCode -ne 0) { throw "Restore grant failed with exit $grantCode." }

    $restoredEvents = Get-PgScalar -Database $script:RestoreDatabase -Sql 'SELECT count(*) FROM table_store.table_event'
    if ([int]$restoredEvents -le 0) { throw "Restored database has no table_event rows." }

    $env:MAHJONG_P52_TEST_POSTGRES = $restoreRuntimeConnection
    $env:MAHJONG_P52_TEST_POSTGRES_ADMIN = $restoreAdminConnection
    $restoreReaderLog = Join-Path $LogDir 'restore-reader.log'
    $restoreResultPath = Join-Path $LogDir 'restore-reader-result.json'
    $env:MAHJONG_P52_CRASH_RESULT = $restoreResultPath
    Remove-Item -LiteralPath $restoreResultPath -Force -ErrorAction SilentlyContinue
    $restoreReaderCode = Invoke-Native -FilePath $DotnetPath -Arguments @($workerDll, 'reader') -LogPath $restoreReaderLog -TimeoutSeconds 180
    if ($restoreReaderCode -ne 0 -or -not (Test-Path -LiteralPath $restoreResultPath)) {
        throw "Restored-database reader failed with exit $restoreReaderCode."
    }
    $restoreReader = Get-Content -LiteralPath $restoreResultPath -Raw | ConvertFrom-Json
    if (-not [bool]$restoreReader.recovered -or [int]$restoreReader.eventCount -le 0) {
        throw "Restored database did not recover the committed event/receipt/outbox state."
    }
    Add-Stage 'backup-restore' 'PASS' 0 "pg_dump/pg_restore into empty DB; restored events=$restoredEvents; recovery ownerEpoch=$($restoreReader.recoveryOwnerEpoch)"

    if ($RunUnifiedVerification) {
        if (-not $UnifiedLogDir) { $UnifiedLogDir = Join-Path $LogDir 'unified' }
        if (-not $LobbyRoot) {
            $LobbyRoot = Join-Path $script:EnvSnapshot['TEMP'] ('mahjong-batch-lobby-' + [Guid]::NewGuid().ToString('N'))
        }
        # Tests start from the live owned database, not the backup restore copy.
        $env:MAHJONG_P52_TEST_POSTGRES = $runtimeConnection
        $env:MAHJONG_P52_TEST_POSTGRES_ADMIN = $adminConnection
        $env:MAHJONG_P54B_TEST_PG = "Host=127.0.0.1;Port=$Port;Database=mahjong_p54b_test;Username=mahjong_table_runtime;Pooling=false;Timeout=10;Command Timeout=30"
        $env:MAHJONG_P54B_TEST_PG_ADMIN = "Host=127.0.0.1;Port=$Port;Database=mahjong_p54b_test;Username=mahjong_pg;Pooling=false;Timeout=10;Command Timeout=30"
        $env:MAHJONG_P54B_TEST_PG_EMPTY = "Host=127.0.0.1;Port=$Port;Database=mahjong_p54b_empty;Username=mahjong_pg;Pooling=false;Timeout=10;Command Timeout=30"
        $env:MAHJONG_P54B_TEST_PG_KEY = $payloadKey
        $env:MAHJONG_P54B_TEST_PG_EVIDENCE = Join-Path $LogDir 'durable-host-tests'
        $hostExe = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
        $unifiedCode = Invoke-Native -FilePath $hostExe -Arguments @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
            (Join-Path $script:RepoRoot 'tools/ci/verify-v5-postgres.ps1'),
            '-Configuration', $Configuration, '-RunnerMode', $RunnerMode,
            '-DotnetPath', $DotnetPath, '-Root', $LobbyRoot, '-Port', "$LobbyPort",
            '-PgZipPath', (Join-Path $Root 'download/postgresql-binaries.zip'),
            '-LogDir', $UnifiedLogDir
        ) -LogPath (Join-Path $LogDir 'unified.log') -TimeoutSeconds 1800
        if ($unifiedCode -ne 0) { throw "Unified verification failed with exit $unifiedCode (see $UnifiedLogDir)." }
        Add-Stage 'unified-verification' 'PASS' $unifiedCode 'unchanged verifier; real table and lobby PostgreSQL; project discovery and plugin copy enabled'
    }

    Add-Stage 'result' 'PASS' 0 'all P5.2 real-PostgreSQL stages passed'
}
catch {
    $message = $_.Exception.Message
    $blocked = $message.StartsWith('BLOCKED:', [StringComparison]::OrdinalIgnoreCase)
    $prefix = if ($blocked) { '' } else { 'FAIL: ' }
    Write-Step ($prefix + $message)
    Add-Stage 'result' $(if ($blocked) { 'BLOCKED' } else { 'FAIL' }) $(if ($blocked) { 2 } else { 1 }) $message
    $script:Exit = if ($blocked) { 2 } else { 1 }
}
finally {
    try {
        if (Test-Path -LiteralPath $script:Marker) {
            if (-not (Test-Path -LiteralPath (Join-Path $script:LogDir 'pg'))) {
                New-Item -ItemType Directory -Path (Join-Path $script:LogDir 'pg') -Force | Out-Null
            }
            if (Test-Path -LiteralPath $script:PgLog) {
                Copy-Item -LiteralPath $script:PgLog -Destination (Join-Path $script:LogDir 'pg\postgres.log') -Force
            }
            if (Test-Path -LiteralPath (Join-Path $Root 'logs')) {
                Copy-Item -Path (Join-Path $Root 'logs\*') -Destination (Join-Path $script:LogDir 'pg') -Force -ErrorAction SilentlyContinue
            }
        }
    }
    catch { Write-Step ("cleanup log copy warning: " + $_.Exception.Message) }

    try { Stop-And-RemoveCluster }
    catch {
        Write-Step ("cleanup FAIL: " + $_.Exception.Message)
        Add-Stage 'cleanup' 'FAIL' 1 $_.Exception.Message
        if ($script:Exit -eq 0) { $script:Exit = 1 }
    }

    # Restore every captured process variable even after a preflight refusal.
    # The summary records names and value hashes only, never the values.
    try {
        $script:EnvRestoreRecords = @(Restore-ProcessEnvironment -Snapshot $script:EnvSnapshot)
        $notRestored = @($script:EnvRestoreRecords | Where-Object { -not $_.restored })
        if ($notRestored.Count -gt 0) {
            Write-Step ("environment restore FAIL: " + (($notRestored | ForEach-Object { $_.variable }) -join ', '))
            if ($script:Exit -eq 0) { $script:Exit = 1 }
        }
    }
    catch {
        Write-Step ("environment restore exception: " + $_.Exception.Message)
        Add-Stage 'environment-restore' 'FAIL' 1 $_.Exception.Message
        if ($script:Exit -eq 0) { $script:Exit = 1 }
    }

    try {
        $portMode = ''
        $portCandidates = @()
        if ($null -ne $script:PortSelection) {
            $portMode = [string]$script:PortSelection.mode
            $portCandidates = @($script:PortSelection.candidates)
        }
        $summary = [ordered]@{
            generatedUtc = (Get-Date).ToUniversalTime().ToString('o')
            sdk = $(if ($sdkVersion) { $sdkVersion } else { '' })
            port = $Port
            portMode = $portMode
            portCandidates = $portCandidates
            database = $script:Database
            restoreDatabase = $script:RestoreDatabase
            payloadKeyRecorded = $false
            environmentRestored = (@($script:EnvRestoreRecords | Where-Object { -not $_.restored }).Count -eq 0)
            environment = $script:EnvRestoreRecords
            stages = $script:Stages
            lines = $script:Lines
            exit = $script:Exit
        }
        $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $LogDir 'wrapper-summary.json') -Encoding utf8
        $script:Lines | Set-Content -LiteralPath (Join-Path $LogDir 'wrapper.log') -Encoding utf8
        $manifest = @()
        Get-ChildItem -LiteralPath $LogDir -File -Recurse | Sort-Object FullName | ForEach-Object {
            $manifest += [ordered]@{
                path = $_.FullName.Substring($LogDir.Length).TrimStart('\')
                sha256 = Get-Sha256 $_.FullName
                bytes = $_.Length
            }
        }
        $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $LogDir 'manifest.sha256.json') -Encoding utf8
    }
    catch { Write-Step ("evidence manifest warning: " + $_.Exception.Message) }
}

if ($script:Exit -eq 0) { Write-Host 'P52-TABLE-POSTGRES-PASS' }
elseif ($script:Exit -eq 2) { Write-Host 'P52-TABLE-POSTGRES-BLOCKED' }
else { Write-Host 'P52-TABLE-POSTGRES-FAIL' }
exit $script:Exit
