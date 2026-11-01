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
    $LogDir = Join-Path $script:RepoRoot ("artifacts\p52-table-postgres-" + $script:Stamp + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
}
$Root = [System.IO.Path]::GetFullPath($Root)
$LogDir = [System.IO.Path]::GetFullPath($LogDir)
$script:LogDir = $LogDir
$script:LogDirOwned = $false
$script:SealOk = $false
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

function Initialize-OutputDirectory([string]$Path) {
    # Acquire the evidence directory BEFORE any write, including failure/finally
    # paths. An existing non-empty directory is treated as sealed evidence and
    # is never overwritten. An empty directory is claimed with an owner marker.
    $full = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    if (Test-Path -LiteralPath $full) {
        if (-not (Test-Path -LiteralPath $full -PathType Container)) {
            throw "BLOCKED: LogDir exists and is not a directory: $full"
        }
        $items = @(Get-ChildItem -LiteralPath $full -Force -ErrorAction Stop)
        if ($items.Count -gt 0) {
            throw "BLOCKED: LogDir is not empty; refusing to overwrite existing evidence: $full"
        }
    }
    else {
        try { New-Item -ItemType Directory -Path $full -Force -ErrorAction Stop | Out-Null }
        catch { throw "BLOCKED: cannot create LogDir '$full': $($_.Exception.Message)" }
    }
    $ownerPath = Join-Path $full 'p52-logdir-owner.json'
    $owner = [ordered]@{
        schema = 'p52-logdir-owner.v1'
        owner = 'mahjong-p52-logdir'
        root = $full
        pid = $PID
        createdUtc = (Get-Date).ToUniversalTime().ToString('o')
    }
    try {
        $utf8 = New-Object System.Text.UTF8Encoding($false)
        [System.IO.File]::WriteAllText($ownerPath, ($owner | ConvertTo-Json -Depth 4), $utf8)
    }
    catch {
        Remove-Item -LiteralPath $ownerPath -Force -ErrorAction SilentlyContinue
        if (@(Get-ChildItem -LiteralPath $full -Force -ErrorAction SilentlyContinue).Count -eq 0) {
            Remove-Item -LiteralPath $full -Force -ErrorAction SilentlyContinue
        }
        throw "BLOCKED: LogDir ownership marker could not be written: $($_.Exception.Message)"
    }
    $script:LogDirOwned = $true
    return $full
}

function Add-Stage([string]$Name, [string]$Status, [object]$ExitCode, [string]$Detail, [object]$Killed = $null) {
    $script:Stages.Add([ordered]@{
            stage = $Name
            status = $Status
            exitCode = $ExitCode
            killed = $Killed
            detail = $Detail
        })
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

function Write-JsonEvidence([string]$Path, [object]$Value) {
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 12), $utf8)
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

# Quote exactly one argument for the Windows CommandLineToArgvW parser. This is
# deliberately explicit instead of Start-Process's implicit array join, so a
# path containing spaces and a value containing embedded quotes survive with
# their boundaries intact.
function ConvertTo-NativeArgumentLine {
    param([string[]]$Arguments)
    $quoted = New-Object System.Collections.Generic.List[string]
    foreach ($value in @($Arguments)) {
        $text = [string]$value
        if ($text.Length -eq 0) { $quoted.Add('""'); continue }
        if ($text -notmatch '[\s"]') { $quoted.Add($text); continue }
        $builder = New-Object System.Text.StringBuilder
        [void]$builder.Append('"')
        $backslashes = 0
        foreach ($character in $text.ToCharArray()) {
            if ($character -eq '\') { $backslashes++; continue }
            if ($character -eq '"') {
                [void]$builder.Append(('\' * (($backslashes * 2) + 1)))
                [void]$builder.Append('"')
                $backslashes = 0
            }
            else {
                if ($backslashes -gt 0) {
                    [void]$builder.Append(('\' * $backslashes))
                    $backslashes = 0
                }
                [void]$builder.Append($character)
            }
        }
        if ($backslashes -gt 0) { [void]$builder.Append(('\' * ($backslashes * 2))) }
        [void]$builder.Append('"')
        $quoted.Add($builder.ToString())
    }
    return ($quoted -join ' ')
}

# Keep the real argv boundaries for evidence, but never persist a caller-named
# secret value or a Password=/Pwd= field. The raw stdout/stderr streams are not
# rewritten; production secrets are passed only through the child environment.
function Get-RedactedArgumentList {
    param(
        [string[]]$Arguments,
        [string[]]$SensitiveValues = @()
    )
    $redacted = New-Object System.Collections.Generic.List[string]
    $applied = $false
    $redactedCount = 0
    $credentialFields = 0
    $credentialPattern = [regex]'(?i)(?<![A-Za-z0-9_])(password|pwd)\s*=\s*'
    foreach ($value in @($Arguments)) {
        $text = [string]$value
        foreach ($secret in @($SensitiveValues)) {
            if (-not [string]::IsNullOrEmpty($secret) -and $text.IndexOf($secret, [StringComparison]::Ordinal) -ge 0) {
                $text = $text.Replace($secret, '<redacted>')
                $applied = $true
                $redactedCount++
            }
        }

        # Connection-string aware scan. A quoted Password/Pwd value may contain
        # ';', doubled quotes or backslash-escaped quotes; redact the complete
        # value, not just the part before the first semicolon.
        $builder = New-Object System.Text.StringBuilder
        $cursor = 0
        while ($cursor -lt $text.Length) {
            $match = $credentialPattern.Match($text, $cursor)
            if (-not $match.Success) {
                [void]$builder.Append($text.Substring($cursor))
                break
            }
            $valueStart = $match.Index + $match.Length
            [void]$builder.Append($text.Substring($cursor, $valueStart - $cursor))
            $end = $valueStart
            if ($valueStart -lt $text.Length -and ($text[$valueStart] -eq [char]'"' -or $text[$valueStart] -eq [char]39)) {
                $quote = $text[$valueStart]
                $end = $valueStart + 1
                $closed = $false
                while ($end -lt $text.Length) {
                    $character = $text[$end]
                    if ($character -eq $quote) {
                        # Actual Npgsql grammar: a doubled quote is the only
                        # in-value quote escape. A backslash is a literal
                        # character, not an escape prefix.
                        if ($end + 1 -lt $text.Length -and $text[$end + 1] -eq $quote) { $end += 2; continue }
                        $closed = $true
                        $end++
                        break
                    }
                    $end++
                }
                if (-not $closed) { $end = $text.Length }
            }
            else {
                while ($end -lt $text.Length -and $text[$end] -ne [char]';') {
                    if ($text[$end] -eq [char]'\' -and $end + 1 -lt $text.Length) { $end += 2 }
                    else { $end++ }
                }
            }
            [void]$builder.Append('<redacted>')
            $credentialFields++
            $applied = $true
            $cursor = $end
        }
        $text = $builder.ToString()
        $redacted.Add($text)
    }
    return [ordered]@{
        arguments = @($redacted)
        applied = $applied
        redactedCount = $redactedCount
        credentialFieldsRedacted = $credentialFields
    }
}

function Read-FileBytesShared([string]$Path) {
    # Start-Process keeps its async redirect readers open for a short time even
    # after WaitForExit. Open with FileShare.ReadWrite so evidence collection
    # cannot fail on that benign parent-side handle.
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $memory = New-Object System.IO.MemoryStream
        $stream.CopyTo($memory)
        # The leading comma prevents PowerShell from enumerating a byte[] into
        # the pipeline (an empty file would otherwise become $null).
        return , $memory.ToArray()
    }
    finally { $stream.Dispose() }
}

function Read-FileTextShared([string]$Path) {
    $bytes = Read-FileBytesShared $Path
    return [System.Text.Encoding]::UTF8.GetString($bytes)
}

function Get-FileEvidence {
    param([string]$Path)
    $exists = $false
    $bytes = [long]0
    $sha = ''
    if (-not [string]::IsNullOrWhiteSpace($Path) -and (Test-Path -LiteralPath $Path)) {
        $exists = $true
        $content = Read-FileBytesShared $Path
        $bytes = [long]$content.Length
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try { $sha = ([System.BitConverter]::ToString($sha256.ComputeHash($content))).Replace('-', '') }
        finally { $sha256.Dispose() }
    }
    return [ordered]@{ path = $Path; exists = $exists; bytes = $bytes; sha256 = $sha }
}

function Write-NativeInvocationMetadata {
    param(
        [Parameter(Mandatory = $true)][string]$LogPath,
        [Parameter(Mandatory = $true)][object]$Record
    )
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $json = $Record | ConvertTo-Json -Depth 12
    [System.IO.File]::WriteAllText($LogPath + '.meta.json', $json, $utf8)
    $logs = $Record.logs
    $argvJson = ConvertTo-Json -InputObject @($Record.argv) -Compress
    $lines = @(
        ('schema=' + [string]$Record.schema),
        ('exe=' + [string]$Record.exe),
        ('exeSha256=' + [string]$Record.exeSha256),
        ('cwd=' + [string]$Record.cwd),
        ('argvJson=' + $argvJson),
        ('commandLine=' + [string]$Record.commandLine),
        ('startedUtc=' + [string]$Record.startedUtc),
        ('endedUtc=' + [string]$Record.endedUtc),
        ('durationMs=' + [string]$Record.durationMs),
        ('pid=' + $(if ($null -eq $Record.pid) { '' } else { [string]$Record.pid })),
        ('started=' + [string]$Record.started),
        ('nativeExit=' + $(if ($null -eq $Record.nativeExit) { '' } else { [string]$Record.nativeExit })),
        ('exitUnavailableReason=' + [string]$Record.exitUnavailableReason),
        ('timedOut=' + [string]$Record.timedOut),
        ('killed=' + [string]$Record.killed),
        ('timeoutSeconds=' + [string]$Record.timeoutSeconds),
        ('stdoutPath=' + [string]$logs.stdout.path),
        ('stdoutBytes=' + [string]$logs.stdout.bytes),
        ('stdoutSha256=' + [string]$logs.stdout.sha256),
        ('stderrPath=' + [string]$logs.stderr.path),
        ('stderrBytes=' + [string]$logs.stderr.bytes),
        ('stderrSha256=' + [string]$logs.stderr.sha256),
        ('mergedPath=' + [string]$logs.merged.path),
        ('mergedBytes=' + [string]$logs.merged.bytes),
        ('mergedSha256=' + [string]$logs.merged.sha256),
        ('redactionApplied=' + [string]$Record.redaction.applied),
        ('redactionCount=' + [string]$Record.redaction.redactedArgumentCount),
        ('credentialFieldsRedacted=' + [string]$Record.redaction.credentialFieldsRedacted),
        ('secretValuesRecorded=' + [string]$Record.redaction.secretValuesRecorded),
        ('error=' + [string]$Record.error)
    )
    [System.IO.File]::WriteAllText($LogPath + '.meta.txt', (($lines -join [Environment]::NewLine) + [Environment]::NewLine), $utf8)
}

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$LogPath,
        [int]$TimeoutSeconds = 180,
        [string[]]$SensitiveValues = @()
    )
    $out = $LogPath + '.stdout'
    $err = $LogPath + '.stderr'
    Remove-Item -LiteralPath $out, $err, ($LogPath + '.meta.json'), ($LogPath + '.meta.txt') -Force -ErrorAction SilentlyContinue
    $redaction = Get-RedactedArgumentList -Arguments $Arguments -SensitiveValues $SensitiveValues
    $displayArguments = @($redaction.arguments)
    # Execute the real argv; only the recorded copy is redacted.
    $argumentLine = ConvertTo-NativeArgumentLine -Arguments $Arguments
    $displayArgumentLine = ConvertTo-NativeArgumentLine -Arguments $displayArguments
    $cwd = (Get-Location).Path
    $resolvedExe = $FilePath
    if (-not [IO.Path]::IsPathRooted($FilePath)) {
        try {
            $command = Get-Command $FilePath -ErrorAction Stop | Select-Object -First 1
            if ($command -and $command.Source) { $resolvedExe = $command.Source }
        }
        catch { }
    }
    $exeEvidence = Get-FileEvidence -Path $resolvedExe
    $startedUtc = [DateTime]::UtcNow
    $process = $null
    $processId = $null
    $started = $false
    $nativeExit = $null
    $exitReason = ''
    $timedOut = $false
    $killed = $false
    $stillRunning = $false
    $failure = ''

    try {
        $process = Start-Process -FilePath $FilePath -ArgumentList $argumentLine -WorkingDirectory $cwd `
            -WindowStyle Hidden -PassThru -RedirectStandardOutput $out -RedirectStandardError $err -ErrorAction Stop
        $started = $true
        $processId = $process.Id
        # Windows PowerShell 5.1 can otherwise lose the native exit code after
        # WaitForExit: ExitCode becomes null, and [int]null silently becomes 0.
        # Retain the process handle while it is running and fail closed if the
        # platform still cannot supply an exit code.
        $null = $process.Handle
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $timedOut = $true
            try { $process.Kill(); $killed = $true } catch { $failure = 'timeout kill failed: ' + $_.Exception.Message }
            try {
                if (-not $process.WaitForExit(5000)) { $stillRunning = $true }
            }
            catch { $failure = (($failure + ' post-kill wait failed: ' + $_.Exception.Message).Trim()) }
        }
        else {
            $process.WaitForExit()
        }
        try { $nativeExit = $process.ExitCode }
        catch { $exitReason = 'Process.ExitCode threw: ' + $_.Exception.Message }
        if ($null -eq $nativeExit -and [string]::IsNullOrWhiteSpace($exitReason)) { $exitReason = 'Process.ExitCode returned null' }
    }
    catch {
        $failure = $_.Exception.Message
    }
    if (-not $started -and [string]::IsNullOrWhiteSpace($failure)) { $failure = 'Start-Process did not return a process' }

    $endedUtc = [DateTime]::UtcNow
    # Exact byte concatenation of the two real streams. Quiet helpers legitimately
    # produce a zero-byte file; that is never treated as a failure by itself.
    $mergedStream = New-Object System.IO.MemoryStream
    foreach ($streamPath in @($out, $err)) {
        if (Test-Path -LiteralPath $streamPath) {
            $streamBytes = Read-FileBytesShared $streamPath
            $mergedStream.Write($streamBytes, 0, $streamBytes.Length)
        }
    }
    [System.IO.File]::WriteAllBytes($LogPath, $mergedStream.ToArray())

    $record = [ordered]@{
        schema = 'p52-native-invocation.v1'
        exe = $resolvedExe
        exeSha256 = [string]$exeEvidence.sha256
        cwd = $cwd
        argv = $displayArguments
        commandLine = $displayArgumentLine
        startedUtc = $startedUtc.ToString('o')
        endedUtc = $endedUtc.ToString('o')
        durationMs = [long](($endedUtc - $startedUtc).TotalMilliseconds)
        pid = $processId
        started = $started
        nativeExit = $nativeExit
        exitUnavailableReason = $exitReason
        timedOut = $timedOut
        killed = $killed
        stillRunning = $stillRunning
        timeoutSeconds = $TimeoutSeconds
        logs = [ordered]@{
            stdout = Get-FileEvidence -Path $out
            stderr = Get-FileEvidence -Path $err
            merged = Get-FileEvidence -Path $LogPath
        }
        redaction = [ordered]@{
            applied = [bool]$redaction.applied
            redactedArgumentCount = [int]$redaction.redactedCount
            credentialFieldsRedacted = [int]$redaction.credentialFieldsRedacted
            secretValuesRecorded = $false
            note = 'argv/commandLine redacted; raw stdout/stderr untouched; process environment is never recorded'
        }
        error = $failure
    }
    Write-NativeInvocationMetadata -LogPath $LogPath -Record $record

    if (-not $started) { throw ("Failed to start native command '{0}': {1}" -f $FilePath, $failure) }
    if ($timedOut) { throw "Command timed out after $TimeoutSeconds seconds: $FilePath" }
    if ($stillRunning) { throw "Process did not exit after timeout kill: $FilePath (pid $processId)" }
    if ($null -eq $nativeExit) { throw "Native exit code unavailable: $FilePath ($exitReason)" }
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
        [int]$TimeoutSeconds = 90,
        [string[]]$SensitiveValues = @()
    )
    $out = $LogPath + '.stdout'
    $err = $LogPath + '.stderr'
    $killPath = $LogPath + '.kill.json'
    Remove-Item -LiteralPath $out, $err, $MarkerPath, $killPath, ($LogPath + '.meta.json'), ($LogPath + '.meta.txt') -Force -ErrorAction SilentlyContinue
    $arguments = @($WorkerDll, 'writer')
    $redaction = Get-RedactedArgumentList -Arguments $arguments -SensitiveValues $SensitiveValues
    $displayArguments = @($redaction.arguments)
    # Execute the real argv; only the recorded copy is redacted.
    $argumentLine = ConvertTo-NativeArgumentLine -Arguments $arguments
    $displayArgumentLine = ConvertTo-NativeArgumentLine -Arguments $displayArguments
    $cwd = (Get-Location).Path
    $resolvedExe = $DotnetPath
    if (-not [IO.Path]::IsPathRooted($DotnetPath)) {
        try {
            $command = Get-Command $DotnetPath -ErrorAction Stop | Select-Object -First 1
            if ($command -and $command.Source) { $resolvedExe = $command.Source }
        }
        catch { }
    }
    $exeEvidence = Get-FileEvidence -Path $resolvedExe
    $startedUtc = [DateTime]::UtcNow
    $process = $null
    $processId = $null
    $started = $false
    $nativeExit = $null
    $exitReason = ''
    $timedOut = $false
    $killed = $false
    $stillRunning = $false
    $failure = ''
    $killReason = ''
    $markerSha = ''

    try {
        $process = Start-Process -FilePath $DotnetPath -ArgumentList $argumentLine -WorkingDirectory $cwd `
            -WindowStyle Hidden -PassThru -RedirectStandardOutput $out -RedirectStandardError $err -ErrorAction Stop
        $started = $true
        $processId = $process.Id
        $null = $process.Handle
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        while (-not (Test-Path -LiteralPath $MarkerPath)) {
            if ($process.HasExited) {
                $failure = "Crash writer exited before its post-commit marker (exit $($process.ExitCode))."
                break
            }
            if ((Get-Date) -gt $deadline) {
                $timedOut = $true
                $killReason = 'timeout'
                try { $process.Kill(); $killed = $true } catch { $failure = 'timeout kill failed: ' + $_.Exception.Message }
                try {
                    if (-not $process.WaitForExit(5000)) { $stillRunning = $true }
                }
                catch { }
                $failure = "Crash writer did not produce its post-commit marker within $TimeoutSeconds seconds."
                break
            }
            Start-Sleep -Milliseconds 100
        }
        if ([string]::IsNullOrWhiteSpace($failure)) {
            # Marker exists and is flushed, so the durable transaction already
            # committed. Kill only this writer process before it can publish.
            $killReason = 'post-commit-marker'
            $markerSha = (Get-FileEvidence -Path $MarkerPath).sha256
            try { $process.Kill(); $killed = $true } catch { $failure = 'writer kill failed: ' + $_.Exception.Message }
            try {
                if (-not $process.WaitForExit(5000)) { $stillRunning = $true }
            }
            catch { }
            if ([string]::IsNullOrWhiteSpace($failure) -and -not $killed) { $failure = 'writer was not forcibly terminated' }
        }
        try { $nativeExit = $process.ExitCode }
        catch { $exitReason = 'Process.ExitCode threw: ' + $_.Exception.Message }
        if ($null -eq $nativeExit -and [string]::IsNullOrWhiteSpace($exitReason)) { $exitReason = 'Process.ExitCode returned null' }
    }
    catch {
        $failure = $_.Exception.Message
    }
    if (-not $started -and [string]::IsNullOrWhiteSpace($failure)) { $failure = 'Start-Process did not return a process' }

    $endedUtc = [DateTime]::UtcNow
    $mergedStream = New-Object System.IO.MemoryStream
    foreach ($streamPath in @($out, $err)) {
        if (Test-Path -LiteralPath $streamPath) {
            $streamBytes = Read-FileBytesShared $streamPath
            $mergedStream.Write($streamBytes, 0, $streamBytes.Length)
        }
    }
    [System.IO.File]::WriteAllBytes($LogPath, $mergedStream.ToArray())

    if ($killed -and $null -ne $nativeExit) {
        $killRecord = [ordered]@{
            pid = $processId
            marker = $MarkerPath
            markerSha256 = $markerSha
            killedUtc = $endedUtc.ToString('o')
            exitCode = $nativeExit
            forced = $true
            reason = $killReason
        }
        $killRecord | ConvertTo-Json | Set-Content -LiteralPath $killPath -Encoding utf8
    }

    $record = [ordered]@{
        schema = 'p52-native-invocation.v1'
        mode = 'crash-writer'
        exe = $resolvedExe
        exeSha256 = [string]$exeEvidence.sha256
        cwd = $cwd
        argv = $displayArguments
        commandLine = $displayArgumentLine
        startedUtc = $startedUtc.ToString('o')
        endedUtc = $endedUtc.ToString('o')
        durationMs = [long](($endedUtc - $startedUtc).TotalMilliseconds)
        pid = $processId
        started = $started
        nativeExit = $nativeExit
        exitUnavailableReason = $exitReason
        timedOut = $timedOut
        killed = $killed
        forced = $killed
        stillRunning = $stillRunning
        timeoutSeconds = $TimeoutSeconds
        killReason = $killReason
        marker = $MarkerPath
        markerSha256 = $markerSha
        killRecordPath = $killPath
        logs = [ordered]@{
            stdout = Get-FileEvidence -Path $out
            stderr = Get-FileEvidence -Path $err
            merged = Get-FileEvidence -Path $LogPath
        }
        redaction = [ordered]@{
            applied = [bool]$redaction.applied
            redactedArgumentCount = [int]$redaction.redactedCount
            credentialFieldsRedacted = [int]$redaction.credentialFieldsRedacted
            secretValuesRecorded = $false
            note = 'argv/commandLine redacted; raw stdout/stderr untouched; process environment is never recorded'
        }
        error = $failure
    }
    Write-NativeInvocationMetadata -LogPath $LogPath -Record $record

    if (-not $started) { throw ("Failed to start crash writer '{0}': {1}" -f $resolvedExe, $failure) }
    if ($timedOut) { throw "Crash writer did not produce its post-commit marker within $TimeoutSeconds seconds." }
    if ($stillRunning) { throw "Crash writer did not exit after the timeout kill (pid $processId)." }
    if ($null -eq $nativeExit) { throw "Crash writer native exit code unavailable. $exitReason" }
    if (-not [string]::IsNullOrWhiteSpace($failure)) { throw $failure }
    if (-not $killed) { throw 'Crash writer was not forcibly killed after its durable commit.' }
    return [ordered]@{
        killed = $true
        pid = $processId
        exitCode = [int]$nativeExit
        timedOut = $timedOut
        killRecordPath = $killPath
        metaPath = ($LogPath + '.meta.json')
        killReason = $killReason
    }
}

function Get-PgScalar([string]$Database, [string]$Sql) {
    $args = @('-h', '127.0.0.1', '-p', "$Port", '-U', 'mahjong_pg', '-v', 'ON_ERROR_STOP=1', '-d', $Database, '-tAc', $Sql)
    $log = Join-Path $script:LogDir ('psql-' + [Guid]::NewGuid().ToString('N') + '.log')
    $code = Invoke-Native -FilePath $script:Psql -Arguments $args -LogPath $log -TimeoutSeconds 60
    if ($code -ne 0) { throw "psql failed with exit $code (see $log)." }
    # A valid query with zero rows legitimately produces a zero-byte log. Do not
    # call .Trim() on a null result: empty means empty, not an exception.
    $text = if (Test-Path -LiteralPath $log) { Read-FileTextShared $log } else { '' }
    if ($null -eq $text) { return '' }
    return $text.Trim()
}

function Get-PgDataDirectory {
    return (Get-PgScalar -Database 'postgres' -Sql 'SHOW data_directory').Trim()
}

function Get-ClusterOwnershipState {
    param(
        [string]$MarkerPath,
        [string]$Root,
        [int]$Port,
        [string]$Database
    )
    $state = [ordered]@{ Ok = $false; Reason = ''; Marker = $null; MarkerSha256 = '' }
    try {
        if ([string]::IsNullOrWhiteSpace($MarkerPath) -or -not (Test-Path -LiteralPath $MarkerPath)) {
            $state.Reason = 'ownership marker is missing'
            return [pscustomobject]$state
        }
        $state.MarkerSha256 = (Get-FileHash -LiteralPath $MarkerPath -Algorithm SHA256).Hash
        $marker = Get-Content -LiteralPath $MarkerPath -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json
        if ($null -eq $marker) {
            $state.Reason = 'ownership marker is empty'
            return [pscustomobject]$state
        }
        $state.Marker = $marker
        if ($marker.owner -ne 'mahjong-p52-pg') {
            $state.Reason = "ownership marker does not belong to this wrapper (owner='$($marker.owner)')"
            return [pscustomobject]$state
        }
        if ([string]::IsNullOrWhiteSpace($Root) -or [string]::IsNullOrWhiteSpace([string]$marker.root)) {
            $state.Reason = 'ownership marker does not name a comparable root'
            return [pscustomobject]$state
        }
        $markerRoot = [IO.Path]::GetFullPath([string]$marker.root)
        $expectedRoot = [IO.Path]::GetFullPath($Root)
        if (-not [string]::Equals($markerRoot, $expectedRoot, [StringComparison]::OrdinalIgnoreCase)) {
            $state.Reason = "ownership marker does not belong to this exact root (marker='$markerRoot', expected='$expectedRoot')"
            return [pscustomobject]$state
        }
        if ([int]$marker.port -ne $Port) {
            $state.Reason = "ownership marker does not belong to this exact port (marker=$($marker.port), expected=$Port)"
            return [pscustomobject]$state
        }
        if (-not [string]::IsNullOrWhiteSpace($Database) -and [string]$marker.database -ne $Database) {
            $state.Reason = "ownership marker does not belong to this exact database (marker='$($marker.database)', expected='$Database')"
            return [pscustomobject]$state
        }
        $state.Ok = $true
        $state.Reason = 'ownership marker verified'
    }
    catch {
        $state.Reason = 'ownership marker cannot be verified: ' + $_.Exception.Message
    }
    return [pscustomobject]$state
}

function Assert-OwnedCluster {
    $state = Get-ClusterOwnershipState -MarkerPath $script:Marker -Root $Root -Port $Port -Database $script:Database
    if (-not $state.Ok) {
        throw "Refusing to touch ${Root}: $($state.Reason)."
    }
    return $state.Marker
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
        $extractLog = Join-Path $LogDir 'pg-extract.log'
        $extractCode = Invoke-Native -FilePath 'tar.exe' -Arguments @('-xf', $zipCopy, '-C', $Root) -LogPath $extractLog -TimeoutSeconds 300
        if ($extractCode -ne 0) { throw "Extracting PostgreSQL failed with exit $extractCode (see $extractLog)." }
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

function Get-OwnedClusterProcesses {
    # PostgreSQL on Windows may render data_directory with forward slashes in
    # its command line. Match both representations so the pre-delete check
    # cannot silently miss the postmaster.
    $native = [string]$script:DataDir
    $forward = $native.Replace('\', '/')
    return @(Get-CimInstance Win32_Process -Filter "Name = 'postgres.exe'" -ErrorAction SilentlyContinue |
        Where-Object {
            $_.CommandLine -and (
                $_.CommandLine.IndexOf($native, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $_.CommandLine.IndexOf($forward, [StringComparison]::OrdinalIgnoreCase) -ge 0)
        })
}

function Remove-OwnedDirectoryLongPath([string]$Path) {
    # The official PostgreSQL zip contains a deep pgAdmin tree. Windows
    # PowerShell 5.1 Remove-Item can stop at MAX_PATH even when the root itself
    # is verified; enumerate/delete through the \\?\ form instead.
    $full = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    if (-not (Test-Path -LiteralPath $full)) { return }
    $prefix = '\\?\' + $full
    foreach ($file in [System.IO.Directory]::EnumerateFiles($prefix, '*', [System.IO.SearchOption]::AllDirectories)) {
        [System.IO.File]::SetAttributes($file, [System.IO.FileAttributes]::Normal)
        [System.IO.File]::Delete($file)
    }
    $directories = [System.IO.Directory]::EnumerateDirectories($prefix, '*', [System.IO.SearchOption]::AllDirectories) |
        Sort-Object { ($_ -split '\\').Count } -Descending
    foreach ($directory in $directories) { [System.IO.Directory]::Delete($directory) }
    [System.IO.Directory]::Delete($prefix)
}

function Stop-And-RemoveCluster {
    if (-not (Test-Path -LiteralPath $Root)) { return }
    $stopCode = $null
    $stopLog = ''
    $markerCopy = ''
    $markerSha = ''
    $liveDataDir = ''
    $rootDeleted = $false
    $cleanupError = ''
    $logCopyError = ''
    $evidenceError = ''
    $preProcesses = @()
    $postProcesses = @()
    $artifactsRoot = Join-Path $script:RepoRoot 'artifacts'
    $contained = Test-CleanupRootContained -Root $Root -ArtifactsRoot $artifactsRoot
    try {
        # The original strict ownership check gates every action, including the
        # evidence copy: a copied marker is never a substitute for verification.
        $marker = Assert-OwnedCluster
        if (-not [string]::IsNullOrWhiteSpace($script:LogDir)) {
            $markerCopy = Join-Path $script:LogDir 'pg-owner.json'
            [System.IO.File]::WriteAllBytes($markerCopy, [System.IO.File]::ReadAllBytes($script:Marker))
            $markerSha = Get-Sha256 $markerCopy
        }
        $preProcesses = @(Get-OwnedClusterProcesses)

        if (Test-TcpListenerReachable -Value $Port) {
            $liveDataDir = Get-PgDataDirectory
            if (-not [string]::Equals([System.IO.Path]::GetFullPath($liveDataDir), [System.IO.Path]::GetFullPath($script:DataDir), [StringComparison]::OrdinalIgnoreCase)) {
                throw "Refusing cleanup: listener data_directory is not this cluster."
            }
            $stopLog = Join-Path $script:LogDir 'pg-cleanup-stop.log'
            $stopCode = Invoke-Native -FilePath $script:PgCtl -Arguments @(
                '-D', $script:DataDir, '-w', '-t', '60', 'stop', '-m', 'fast'
            ) -LogPath $stopLog -TimeoutSeconds 90
            if ($stopCode -ne 0) { throw "pg_ctl cleanup stop failed with exit $stopCode; refusing deletion." }
            $script:ClusterStarted = $false
        }

        $postProcesses = @(Get-OwnedClusterProcesses)
        if ($postProcesses.Count -gt 0) {
            throw "A postgres.exe process still references $($script:DataDir); refusing deletion."
        }

        # Preserve the final server/pg_ctl logs after the stop and before the
        # root is deleted. A copy failure blocks deletion rather than losing the
        # shutdown evidence.
        try {
            $pgCopyDir = Join-Path $script:LogDir 'pg'
            New-Item -ItemType Directory -Path $pgCopyDir -Force | Out-Null
            if (Test-Path -LiteralPath $script:PgLog) {
                Copy-Item -LiteralPath $script:PgLog -Destination (Join-Path $pgCopyDir 'postgres.log') -Force
            }
            if (Test-Path -LiteralPath (Join-Path $Root 'logs')) {
                Copy-Item -Path (Join-Path $Root 'logs\*') -Destination $pgCopyDir -Force -ErrorAction SilentlyContinue
            }
        }
        catch {
            throw ("Refusing deletion: final PostgreSQL logs could not be preserved: " + $_.Exception.Message)
        }

        if (-not $contained) {
            throw "Refusing recursive cleanup outside the worktree artifacts root: $($Root.TrimEnd('\'))"
        }
        # Re-verify immediately before the destructive step.
        $null = Assert-OwnedCluster
        $safeRoot = $Root.TrimEnd('\')
        Remove-OwnedDirectoryLongPath -Path $safeRoot
        if (Test-Path -LiteralPath $safeRoot) { throw "Cleanup failed: $safeRoot still exists." }
        $rootDeleted = $true
        Write-Step "ownership-verified cluster removed: $safeRoot"
    }
    catch {
        $cleanupError = $_.Exception.Message
        throw
    }
    finally {
        if (-not [string]::IsNullOrWhiteSpace($script:LogDir)) {
            try {
                $pgCopyDir = Join-Path $script:LogDir 'pg'
                if (-not (Test-Path -LiteralPath $pgCopyDir)) { New-Item -ItemType Directory -Path $pgCopyDir -Force | Out-Null }
                if (Test-Path -LiteralPath $script:PgLog) {
                    Copy-Item -LiteralPath $script:PgLog -Destination (Join-Path $pgCopyDir 'postgres.log') -Force
                }
                if (Test-Path -LiteralPath (Join-Path $Root 'logs')) {
                    Copy-Item -Path (Join-Path $Root 'logs\*') -Destination $pgCopyDir -Force -ErrorAction SilentlyContinue
                }
            }
            catch {
                $logCopyError = $_.Exception.Message
                Write-Step ("cleanup log copy FAIL: " + $logCopyError)
                if ($script:Exit -eq 0) { $script:Exit = 1 }
            }

            try {
                $evidence = [ordered]@{
                    schema = 'p52-table-pg-cleanup.v1'
                    generatedUtc = (Get-Date).ToUniversalTime().ToString('o')
                    root = $Root
                    rootContained = $contained
                    markerPath = $script:Marker
                    markerPreservedPath = $markerCopy
                    markerSha256 = $markerSha
                    dataDirectoryOnServer = $liveDataDir
                    expectedDataDirectory = $script:DataDir
                    dataDirectoryMatches = $(if ($liveDataDir) {
                            [string]::Equals([System.IO.Path]::GetFullPath($liveDataDir), [System.IO.Path]::GetFullPath($script:DataDir), [StringComparison]::OrdinalIgnoreCase)
                        }
                        else { $null })
                    stopNativeExit = $stopCode
                    stopLog = $stopLog
                    preStopProcessCount = $preProcesses.Count
                    preStopProcessIds = @($preProcesses | ForEach-Object { [int]$_.ProcessId })
                    postStopProcessCount = $postProcesses.Count
                    postStopProcessIds = @($postProcesses | ForEach-Object { [int]$_.ProcessId })
                    rootDeleted = $rootDeleted
                    rootExistsAfter = (Test-Path -LiteralPath $Root)
                    logCopyError = $logCopyError
                    error = $cleanupError
                }
                $utf8 = New-Object System.Text.UTF8Encoding($false)
                [System.IO.File]::WriteAllText((Join-Path $script:LogDir 'cleanup-evidence.json'), ($evidence | ConvertTo-Json -Depth 8), $utf8)
            }
            catch {
                $evidenceError = $_.Exception.Message
                Write-Step ("cleanup evidence FAIL: " + $evidenceError)
                if ($script:Exit -eq 0) { $script:Exit = 1 }
            }
        }

        if ($rootDeleted -and [string]::IsNullOrWhiteSpace($cleanupError) -and [string]::IsNullOrWhiteSpace($logCopyError) -and [string]::IsNullOrWhiteSpace($evidenceError)) {
            $stageExit = if ($null -eq $stopCode) { 0 } else { $stopCode }
            $stopText = if ($null -eq $stopCode) { 'not-needed' } else { [string]$stopCode }
            Add-Stage 'cleanup' 'PASS' $stageExit "marker verified; stop=$stopText; rootDeleted=true; root=$Root"
        }
    }
}

function Test-TableResultEvidence {
    # Production decision for one xUnit.net v3 -result-xml invocation. It is
    # intentionally stricter than the old assembly-summary gate: one assembly
    # with the expected identity and framework, non-empty real test nodes,
    # unique IDs, collection aggregates that equal their child nodes, assembly
    # aggregates that equal the test nodes, and an all-Pass green set. The
    # native exit is mandatory and can never be replaced by a green XML.
    param(
        [string]$XmlPath,
        [string]$ExpectedAssemblyName,
        [object]$NativeExit,
        [string]$LogDir,
        [datetime]$RunStartedUtc
    )
    $result = [ordered]@{
        Ok              = $false
        Reason          = ''
        Counts          = $null
        TestNodeCount   = 0
        CollectionCount = 0
        AssemblyName    = ''
        TestFramework   = ''
        SchemaVersion   = ''
        XmlSha256       = ''
        XmlLastWriteUtc = $null
    }
    try {
        if ([string]::IsNullOrWhiteSpace($XmlPath)) { throw 'no table result XML path was assigned' }
        if (-not (Test-Path -LiteralPath $XmlPath)) { throw "result XML is missing: $XmlPath" }
        if ([string]::IsNullOrWhiteSpace($ExpectedAssemblyName)) { throw 'expected assembly name is missing' }
        if ([string]::IsNullOrWhiteSpace($LogDir)) { throw 'result LogDir is missing' }
        if ($RunStartedUtc -eq [DateTime]::MinValue) { throw 'result invocation start time is missing' }
        $fullXml = [IO.Path]::GetFullPath($XmlPath)
        $fullLog = [IO.Path]::GetFullPath($LogDir).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if (-not $fullXml.StartsWith($fullLog, [StringComparison]::OrdinalIgnoreCase)) {
            throw "result XML is outside this run's LogDir: $fullXml"
        }
        $lastWrite = (Get-Item -LiteralPath $fullXml).LastWriteTimeUtc
        $result.XmlLastWriteUtc = $lastWrite
        if ($lastWrite -lt $RunStartedUtc.AddSeconds(-5)) {
            throw "result XML predates this invocation (last write $($lastWrite.ToString('o')) < start $($RunStartedUtc.ToString('o')))"
        }
        $result.XmlSha256 = (Get-FileHash -LiteralPath $fullXml -Algorithm SHA256).Hash

        $settings = New-Object System.Xml.XmlReaderSettings
        $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $settings.MaxCharactersInDocument = 67108864
        $reader = [System.Xml.XmlReader]::Create($fullXml, $settings)
        try {
            $doc = New-Object System.Xml.XmlDocument
            $doc.XmlResolver = $null
            $doc.Load($reader)
        }
        finally { $reader.Dispose() }

        if ($null -eq $doc.DocumentElement -or $doc.DocumentElement.LocalName -ne 'assemblies') {
            throw "result XML root element is not <assemblies>"
        }
        $root = $doc.DocumentElement
        $schemaVersion = $root.GetAttribute('schema-version')
        if ([string]::IsNullOrWhiteSpace($schemaVersion) -or $schemaVersion -notmatch '^\d+$') {
            throw 'result XML has no integer assemblies/@schema-version'
        }
        $result.SchemaVersion = $schemaVersion

        $rootChildren = @($root.ChildNodes | Where-Object { $_.NodeType -eq [System.Xml.XmlNodeType]::Element })
        if ($rootChildren.Count -ne 1 -or $rootChildren[0].LocalName -ne 'assembly') {
            throw "result XML must contain exactly one <assembly> element (found $($rootChildren.Count) element(s))"
        }
        $assembly = $rootChildren[0]
        $assemblyName = $assembly.GetAttribute('name')
        if ([string]::IsNullOrWhiteSpace($assemblyName)) { throw 'result XML assembly/@name is missing' }
        $result.AssemblyName = $assemblyName
        $actualLeaf = [IO.Path]::GetFileNameWithoutExtension($assemblyName)
        if ($actualLeaf -ine $ExpectedAssemblyName) {
            throw "result XML assembly identity mismatch: expected '$ExpectedAssemblyName', got '$assemblyName'"
        }
        $framework = $assembly.GetAttribute('test-framework')
        if ([string]::IsNullOrWhiteSpace($framework) -or $framework -notlike 'xUnit.net v3*') {
            throw "result XML is not an xUnit.net v3 result (test-framework='$framework')"
        }
        $result.TestFramework = $framework

        $assemblyChildren = @($assembly.ChildNodes | Where-Object { $_.NodeType -eq [System.Xml.XmlNodeType]::Element })
        foreach ($child in $assemblyChildren) {
            if ($child.LocalName -notin @('collection', 'errors')) {
                throw "result XML contains unexpected <$($child.LocalName)> under <assembly>"
            }
        }
        $errorsContainers = @($assembly.SelectNodes('./errors'))
        if ($errorsContainers.Count -gt 1) {
            throw "result XML contains $($errorsContainers.Count) <errors> containers under <assembly>"
        }
        $directErrorNodes = @()
        if ($errorsContainers.Count -eq 1) {
            $errorsContainer = $errorsContainers[0]
            foreach ($errorsChild in @($errorsContainer.ChildNodes)) {
                if ($errorsChild.NodeType -eq [System.Xml.XmlNodeType]::Element) {
                    if ($errorsChild.LocalName -ne 'error') {
                        throw "result XML contains unexpected <$($errorsChild.LocalName)> under assembly <errors>"
                    }
                    if ([string]::IsNullOrWhiteSpace($errorsChild.GetAttribute('type')) -and
                        [string]::IsNullOrWhiteSpace($errorsChild.GetAttribute('name'))) {
                        throw 'result XML assembly <errors>/<error> has neither @type nor @name'
                    }
                    foreach ($errorChild in @($errorsChild.ChildNodes | Where-Object { $_.NodeType -eq [System.Xml.XmlNodeType]::Element })) {
                        if ($errorChild.LocalName -notin @('failure', 'message', 'stack-trace', 'output')) {
                            throw "result XML assembly <error> contains unsupported <$($errorChild.LocalName)>"
                        }
                    }
                }
                elseif ($errorsChild.NodeType -eq [System.Xml.XmlNodeType]::Text -or $errorsChild.NodeType -eq [System.Xml.XmlNodeType]::CDATA) {
                    if (-not [string]::IsNullOrWhiteSpace($errorsChild.Value)) {
                        throw 'result XML assembly <errors> contains nonempty text instead of structured error nodes'
                    }
                }
            }
            $directErrorNodes = @($errorsContainer.SelectNodes('./error'))
        }

        $required = @('total', 'passed', 'failed', 'skipped', 'errors', 'not-run')
        $attrs = @{}
        foreach ($attribute in $required) {
            if (-not $assembly.HasAttribute($attribute)) { throw "result XML assembly is missing required attribute '$attribute'" }
            $raw = $assembly.GetAttribute($attribute)
            if ($raw -notmatch '^\d+$') { throw "result XML assembly attribute '$attribute' is not a non-negative integer: '$raw'" }
            $attrs[$attribute] = [int]$raw
        }

        $collections = @($assembly.SelectNodes('./collection'))
        $result.CollectionCount = $collections.Count
        $collectionTotal = 0
        $structuredTestCount = 0
        $nodePassed = 0
        $nodeFailed = 0
        $nodeSkipped = 0
        $nodeNotRun = 0
        $testIds = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach ($collection in $collections) {
            $collectionName = $collection.GetAttribute('name')
            if ([string]::IsNullOrWhiteSpace($collectionName)) { throw 'result XML <collection> has no @name' }
            $collectionChildren = @($collection.ChildNodes | Where-Object { $_.NodeType -eq [System.Xml.XmlNodeType]::Element })
            foreach ($child in $collectionChildren) {
                if ($child.LocalName -ne 'test') {
                    throw "result XML contains unexpected <$($child.LocalName)> under collection '$collectionName'"
                }
            }
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
                $id = $test.GetAttribute('id')
                $name = $test.GetAttribute('name')
                $testResult = $test.GetAttribute('result')
                if ([string]::IsNullOrWhiteSpace($id)) { throw 'result XML contains a <test> without @id' }
                if ([string]::IsNullOrWhiteSpace($name)) { throw "result XML test '$id' has no @name" }
                if (-not $testIds.Add($id)) { throw "result XML contains duplicate test id '$id'" }
                switch ($testResult) {
                    'Pass' { $cp++ }
                    'Fail' { $cf++ }
                    'Skip' { $cs++ }
                    'NotRun' { $cn++ }
                    default { throw "result XML test '$name' has unsupported result '$testResult'" }
                }
                $allowedChildren = @('output', 'traits')
                if ($testResult -eq 'Fail') { $allowedChildren += 'failure' }
                elseif ($testResult -eq 'Skip' -or $testResult -eq 'NotRun') { $allowedChildren += 'reason' }
                foreach ($testChild in @($test.ChildNodes | Where-Object { $_.NodeType -eq [System.Xml.XmlNodeType]::Element })) {
                    if ($testChild.LocalName -notin $allowedChildren) {
                        throw "result XML test '$name' result=$testResult contains contradictory <$($testChild.LocalName)> child"
                    }
                }
                $forbiddenDescendants = @('error', 'errors', 'skip', 'skipped')
                if ($testResult -eq 'Pass') { $forbiddenDescendants += @('failure', 'reason') }
                elseif ($testResult -eq 'Fail') { $forbiddenDescendants += @('reason') }
                else { $forbiddenDescendants += @('failure') }
                foreach ($descendant in @($test.SelectNodes('.//*'))) {
                    if ($descendant.LocalName -in $forbiddenDescendants) {
                        throw "result XML test '$name' result=$testResult contains contradictory nested <$($descendant.LocalName)> element"
                    }
                }
                foreach ($traitsNode in @($test.SelectNodes('./traits'))) {
                    foreach ($traitChild in @($traitsNode.ChildNodes | Where-Object { $_.NodeType -eq [System.Xml.XmlNodeType]::Element })) {
                        if ($traitChild.LocalName -ne 'trait') {
                            throw "result XML test '$name' contains unsupported <$($traitChild.LocalName)> under <traits>"
                        }
                    }
                }
                foreach ($outputNode in @($test.SelectNodes('./output'))) {
                    if (@($outputNode.ChildNodes | Where-Object { $_.NodeType -eq [System.Xml.XmlNodeType]::Element }).Count -gt 0) {
                        throw "result XML test '$name' contains unexpected element content under <output>"
                    }
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
            $structuredTestCount += $childTests.Count
            $nodePassed += $cp
            $nodeFailed += $cf
            $nodeSkipped += $cs
            $nodeNotRun += $cn
        }

        $allTestNodes = @($assembly.SelectNodes('.//test')).Count
        if ($allTestNodes -ne $structuredTestCount) {
            throw "result XML contains $($allTestNodes - $structuredTestCount) test node(s) outside a <collection>"
        }
        if ($collectionTotal -ne $structuredTestCount) {
            throw "result XML collection totals ($collectionTotal) do not match test case nodes ($structuredTestCount)"
        }
        if ($attrs['total'] -ne $structuredTestCount) {
            throw "result XML assembly total=$($attrs['total']) but $structuredTestCount test case node(s) exist"
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
        $errorNodes = $directErrorNodes
        if (($attrs['errors'] -gt 0) -ne ($errorNodes.Count -gt 0)) {
            throw "result XML errors=$($attrs['errors']) but $($errorNodes.Count) framework error node(s) exist"
        }

        $result.TestNodeCount = $structuredTestCount
        $result.Counts = [ordered]@{
            total = $attrs['total']
            passed = $attrs['passed']
            failed = $attrs['failed']
            skipped = $attrs['skipped']
            errors = $attrs['errors']
            notRun = $attrs['not-run']
        }

        if ($null -eq $NativeExit) { throw 'native exit is unknown (invocation threw); never treated as 0' }
        if (-not ($NativeExit -is [int] -or $NativeExit -is [long])) { throw "native exit is not an integer: '$NativeExit'" }
        if ([long]$NativeExit -ne 0) { throw "native exit is $NativeExit, not 0" }
        if ($attrs['total'] -le 0) { throw 'result XML contains zero executed test cases' }
        if ($attrs['failed'] -gt 0 -or $attrs['errors'] -gt 0 -or $attrs['skipped'] -gt 0 -or $attrs['not-run'] -gt 0) {
            throw "result XML is not green: failed=$($attrs['failed']), errors=$($attrs['errors']), skipped=$($attrs['skipped']), not-run=$($attrs['not-run'])"
        }
        if ($attrs['passed'] -ne $attrs['total']) {
            throw "result XML passed=$($attrs['passed']) does not equal total=$($attrs['total'])"
        }

        $result.Ok = $true
        $result.Reason = "$($attrs['total'])/$($attrs['total']) test cases Pass; unique IDs, collection totals, assembly counts and native exit 0 agree"
    }
    catch {
        $result.Ok = $false
        $result.Reason = $_.Exception.Message
    }
    return [pscustomobject]$result
}

function Test-RestorePreflightEmpty {
    # Parses the single psql -tA row produced by the pre-restore query:
    #   database|host|port|table_store_schema_objects|table_store_tables|table_store_relations
    # Zero identity match is not enough: every object count must be exactly 0.
    param(
        [string]$RawOutput,
        [string]$ExpectedDatabase,
        [string]$ExpectedHost,
        [int]$ExpectedPort
    )
    $result = [ordered]@{ Ok = $false; Reason = ''; Fields = @(); Values = $null }
    try {
        if ([string]::IsNullOrWhiteSpace($RawOutput)) { throw 'restore preflight query produced no output' }
        $lines = @(($RawOutput -split "\r?\n") | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        if ($lines.Count -ne 1) { throw "restore preflight must return exactly one non-empty line (got $($lines.Count))" }
        $fields = @($lines[0].Trim() -split '\|')
        $result.Fields = $fields
        if ($fields.Count -ne 6) { throw "restore preflight must return 6 fields (got $($fields.Count))" }
        $database = $fields[0].Trim()
        $serverHost = $fields[1].Trim()
        $serverPort = $fields[2].Trim()
        if ($database -ne $ExpectedDatabase) { throw "restore preflight database '$database' does not match '$ExpectedDatabase'" }
        if ($serverHost -ne $ExpectedHost) { throw "restore preflight host '$serverHost' does not match '$ExpectedHost'" }
        if ($serverPort -notmatch '^\d+$') { throw "restore preflight port '$serverPort' is not an integer" }
        if ([int]$serverPort -ne $ExpectedPort) { throw "restore preflight port $serverPort does not match $ExpectedPort" }
        $counts = @()
        foreach ($index in 3..5) {
            $text = $fields[$index].Trim()
            if ($text -notmatch '^\d+$') { throw "restore preflight count '$text' is not a non-negative integer" }
            $counts += [int]$text
        }
        $result.Values = [ordered]@{
            database = $database
            host = $serverHost
            port = [int]$serverPort
            schemaObjects = $counts[0]
            tables = $counts[1]
            relations = $counts[2]
        }
        if ($counts[0] -ne 0 -or $counts[1] -ne 0 -or $counts[2] -ne 0) {
            throw "restore target is not empty: schema_objects=$($counts[0]) tables=$($counts[1]) relations=$($counts[2])"
        }
        $result.Ok = $true
        $result.Reason = 'restore target is empty and its database/host/port identity matches'
    }
    catch {
        $result.Reason = $_.Exception.Message
    }
    return [pscustomobject]$result
}

function Test-CleanupRootContained {
    # Pure path guard used before any recursive delete. It requires a depth
    # guard, strict containment under this worktree's artifacts directory, and
    # rejects the artifacts root itself or a sibling such as artifacts-evil.
    param([string]$Root, [string]$ArtifactsRoot)
    try {
        if ([string]::IsNullOrWhiteSpace($Root) -or [string]::IsNullOrWhiteSpace($ArtifactsRoot)) { return $false }
        $full = [IO.Path]::GetFullPath($Root).TrimEnd('\')
        $guard = [IO.Path]::GetFullPath($ArtifactsRoot).TrimEnd('\')
        if ($full.Length -lt 20) { return $false }
        if ([string]::Equals($full, $guard, [StringComparison]::OrdinalIgnoreCase)) { return $false }
        return $full.StartsWith($guard + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
    }
    catch { return $false }
}

function Assert-XmlGatingSelfTest {
    $tmp = Join-Path $script:LogDir 'tmp\xml-selfcheck'
    New-Item -ItemType Directory -Path $tmp -Force | Out-Null
    $assemblyName = 'Mahjong.Infrastructure.Tests.dll'
    $framework = 'xUnit.net v3 4.0.2'
    function New-SelfCheckXml {
        param(
            [string[]]$Results = @(),
            [int]$Total = -1,
            [int]$Passed = -1,
            [int]$Failed = -1,
            [int]$Skipped = -1,
            [int]$Errors = 0,
            [int]$NotRun = -1,
            [switch]$AssemblyOnly,
            [switch]$AddErrorNode,
            [switch]$OmitNotRun
        )
        $passCount = @($Results | Where-Object { $_ -eq 'Pass' }).Count
        $failCount = @($Results | Where-Object { $_ -eq 'Fail' }).Count
        $skipCount = @($Results | Where-Object { $_ -eq 'Skip' }).Count
        $notRunCount = @($Results | Where-Object { $_ -eq 'NotRun' }).Count
        if ($Total -lt 0) { $Total = $Results.Count }
        if ($Passed -lt 0) { $Passed = $passCount }
        if ($Failed -lt 0) { $Failed = $failCount }
        if ($Skipped -lt 0) { $Skipped = $skipCount }
        if ($NotRun -lt 0) { $NotRun = $notRunCount }
        $assemblyAttrs = 'name="{0}" test-framework="{1}" total="{2}" passed="{3}" failed="{4}" skipped="{5}" errors="{6}"' -f $assemblyName, $framework, $Total, $Passed, $Failed, $Skipped, $Errors
        if (-not $OmitNotRun) { $assemblyAttrs += (' not-run="{0}"' -f $NotRun) }
        $builder = New-Object System.Text.StringBuilder
        [void]$builder.AppendLine('<assemblies schema-version="3">')
        [void]$builder.AppendLine(('  <assembly {0}>' -f $assemblyAttrs))
        if ($AddErrorNode) { [void]$builder.AppendLine('    <errors><error type="Probe.Broken" name="fixture error"><failure><message>error</message></failure></error></errors>') }
        if (-not $AssemblyOnly) {
            [void]$builder.AppendLine(('    <collection name="probe" total="{0}" passed="{1}" failed="{2}" skipped="{3}" not-run="{4}">' -f $Results.Count, $passCount, $failCount, $skipCount, $notRunCount))
            for ($index = 0; $index -lt $Results.Count; $index++) {
                [void]$builder.AppendLine(('      <test id="00000000-0000-0000-0000-{0:D12}" name="Probe.Case{1}" result="{2}" />' -f ($index + 1), ($index + 1), $Results[$index]))
            }
            [void]$builder.AppendLine('    </collection>')
        }
        [void]$builder.AppendLine('  </assembly>')
        [void]$builder.AppendLine('</assemblies>')
        return $builder.ToString()
    }

    $cases = @(
        [ordered]@{ name = 'zero-tests'; xml = (New-SelfCheckXml -Total 0 -Passed 0) },
        [ordered]@{ name = 'failed'; xml = (New-SelfCheckXml -Results @('Fail')) },
        [ordered]@{ name = 'errors'; xml = (New-SelfCheckXml -Results @('Pass') -Errors 1 -AddErrorNode) },
        [ordered]@{ name = 'skipped'; xml = (New-SelfCheckXml -Results @('Skip')) },
        [ordered]@{ name = 'not-run'; xml = (New-SelfCheckXml -Results @('NotRun')) },
        [ordered]@{ name = 'passed-mismatch'; xml = (New-SelfCheckXml -Results @('Pass') -Total 2 -Passed 1) },
        [ordered]@{ name = 'missing-not-run'; xml = (New-SelfCheckXml -Results @('Pass') -OmitNotRun) }
    )
    $checked = 0
    foreach ($case in $cases) {
        $path = Join-Path $tmp ($case.name + '.xml')
        Set-Content -LiteralPath $path -Value $case.xml -Encoding utf8
        $started = [DateTime]::UtcNow.AddSeconds(-1)
        $decision = Test-TableResultEvidence -XmlPath $path -ExpectedAssemblyName 'Mahjong.Infrastructure.Tests' `
            -NativeExit 0 -LogDir $tmp -RunStartedUtc $started
        if ($decision.Ok) { throw "XML gating self-check accepted negative case '$($case.name)'." }
        if ([string]::IsNullOrWhiteSpace($decision.Reason)) { throw "XML gating self-check rejected '$($case.name)' without a reason." }
        $checked++
    }

    return $checked
}

try {
    $LogDir = Initialize-OutputDirectory -Path $LogDir
    $script:LogDir = $LogDir
}
catch {
    Write-Host ("P52-TABLE-POSTGRES-BLOCKED: " + $_.Exception.Message)
    exit 2
}

try {
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
    $postmasterStart = Get-PgScalar -Database $script:Database -Sql 'SELECT pg_postmaster_start_time()'
    $identityRow = Get-PgScalar -Database $script:Database -Sql "SELECT current_database()::text || '|' || COALESCE(host(inet_server_addr()), '') || '|' || COALESCE(inet_server_port()::text, '')"
    $identityParts = @($identityRow.Trim() -split '\|')
    if ($identityParts.Count -ne 3 -or
        $identityParts[0] -ne $script:Database -or
        $identityParts[1] -ne '127.0.0.1' -or
        $identityParts[2] -notmatch '^\d+$' -or
        [int]$identityParts[2] -ne $Port) {
        throw "Cluster identity mismatch: query returned '$($identityRow.Trim())', expected $($script:Database)|127.0.0.1|$Port."
    }
    $markerState = Get-ClusterOwnershipState -MarkerPath $script:Marker -Root $Root -Port $Port -Database $script:Database
    if (-not $markerState.Ok) { throw "Cluster identity mismatch: $($markerState.Reason)." }
    $clusterIdentity = [ordered]@{
        schema = 'p52-table-pg-identity.v1'
        generatedUtc = (Get-Date).ToUniversalTime().ToString('o')
        host = '127.0.0.1'
        port = $Port
        database = $script:Database
        restoreDatabase = $script:RestoreDatabase
        serverVersion = $serverVersion.Trim()
        postmasterStart = $postmasterStart.Trim()
        dataDirectory = $dataDirectory
        expectedDataDirectory = $script:DataDir
        dataDirectoryMatches = [string]::Equals([System.IO.Path]::GetFullPath($dataDirectory), [System.IO.Path]::GetFullPath($script:DataDir), [StringComparison]::OrdinalIgnoreCase)
        markerPath = $script:Marker
        markerSha256 = $markerState.MarkerSha256
        marker = $markerState.Marker
        connectionIdentity = $identityRow.Trim()
    }
    $utf8Identity = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText((Join-Path $LogDir 'cluster-identity.json'), ($clusterIdentity | ConvertTo-Json -Depth 8), $utf8Identity)
    Add-Stage 'cluster' 'PASS' 0 "server=$($serverVersion.Trim()) data_directory=$dataDirectory port=$Port database=$($script:Database) markerSha256=$($markerState.MarkerSha256)"

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
    $testsStartedUtc = [DateTime]::UtcNow
    $testsCode = Invoke-Native -FilePath $DotnetPath -Arguments @(
        $testsDll, '-noColor', '-noLogo', '-result-xml', $testsXml
    ) -LogPath $testsLog -TimeoutSeconds 900
    $testsEvidence = Test-TableResultEvidence -XmlPath $testsXml -ExpectedAssemblyName 'Mahjong.Infrastructure.Tests' `
        -NativeExit $testsCode -LogDir $LogDir -RunStartedUtc $testsStartedUtc
    Write-JsonEvidence (Join-Path $LogDir 'tests-evidence.json') ([ordered]@{
            schema = 'p52-table-result-evidence.v1'
            invocation = 'all'
            xmlPath = $testsXml
            logPath = $testsLog
            metaJsonPath = ($testsLog + '.meta.json')
            nativeExit = $testsCode
            decision = $testsEvidence
        })
    if (-not $testsEvidence.Ok) {
        throw "Tests failed (native exit=$testsCode): $($testsEvidence.Reason)"
    }
    $counts = $testsEvidence.Counts
    $testsDllSha256 = (Get-FileHash -LiteralPath $testsDll -Algorithm SHA256).Hash
    Add-Stage 'tests' 'PASS' $testsCode "dll=$testsDll sha256=$testsDllSha256 total=$($counts.total) passed=$($counts.passed) failed=0 skipped=0 notRun=0 nodes=$($testsEvidence.TestNodeCount) collections=$($testsEvidence.CollectionCount)"

    $targetXml = Join-Path $LogDir 'tests-targeted.xml'
    $targetLog = Join-Path $LogDir 'tests-targeted.log'
    $targetStartedUtc = [DateTime]::UtcNow
    $targetCode = Invoke-Native -FilePath $DotnetPath -Arguments @(
        $testsDll, '-noColor', '-noLogo',
        '-class', 'Mahjong.Infrastructure.Tests.PostgresTableStoreTests',
        '-result-xml', $targetXml
    ) -LogPath $targetLog -TimeoutSeconds 600
    $targetEvidence = Test-TableResultEvidence -XmlPath $targetXml -ExpectedAssemblyName 'Mahjong.Infrastructure.Tests' `
        -NativeExit $targetCode -LogDir $LogDir -RunStartedUtc $targetStartedUtc
    Write-JsonEvidence (Join-Path $LogDir 'tests-targeted-evidence.json') ([ordered]@{
            schema = 'p52-table-result-evidence.v1'
            invocation = 'PostgresTableStoreTests'
            xmlPath = $targetXml
            logPath = $targetLog
            metaJsonPath = ($targetLog + '.meta.json')
            nativeExit = $targetCode
            decision = $targetEvidence
        })
    if (-not $targetEvidence.Ok) {
        throw "Targeted class run failed (native exit=$targetCode): $($targetEvidence.Reason)"
    }
    $targetCounts = $targetEvidence.Counts
    Add-Stage 'tests-targeted' 'PASS' $targetCode "class=PostgresTableStoreTests total=$($targetCounts.total) passed=$($targetCounts.passed) failed=0 skipped=0 nodes=$($targetEvidence.TestNodeCount) collections=$($targetEvidence.CollectionCount)"

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
    $markerJson = Get-Content -LiteralPath $markerPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Add-Stage 'crash-writer' 'PASS' $writerResult.exitCode "pid=$($writerResult.pid), committed state=$($markerJson.stateVersion) seq=$($markerJson.lastEventSeq), killed before publication" -Killed $true

    $restart = Restart-ClusterImmediate
    Add-Stage 'postgres-crash-restart' 'PASS' 0 ("postmaster start changed {0} -> {1}; recovery lines={2}" -f $restart.before, $restart.after, $restart.recoveryLines.Count)

    $readerLog = Join-Path $LogDir 'crash-reader.log'
    Remove-Item -LiteralPath $resultPath -Force -ErrorAction SilentlyContinue
    $readerCode = Invoke-Native -FilePath $DotnetPath -Arguments @($workerDll, 'reader') -LogPath $readerLog -TimeoutSeconds 180
    if ($readerCode -ne 0 -or -not (Test-Path -LiteralPath $resultPath)) {
        throw "Crash reader failed with exit $readerCode (see $readerLog)."
    }
    $readerJson = Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
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
    $dumpItem = Get-Item -LiteralPath $dumpPath
    if ($dumpItem.Length -lt 5) { throw "pg_dump produced a $($dumpItem.Length)-byte file, not a custom dump." }
    $dumpHeader = New-Object byte[] 5
    $dumpStream = [System.IO.File]::OpenRead($dumpPath)
    try { $null = $dumpStream.Read($dumpHeader, 0, 5) } finally { $dumpStream.Dispose() }
    $dumpMagic = [System.Text.Encoding]::ASCII.GetString($dumpHeader)
    if ($dumpMagic -ne 'PGDMP') { throw "pg_dump file magic is '$dumpMagic', expected PGDMP." }
    $dumpEvidence = [ordered]@{
        schema = 'p52-pg-dump-artifact.v1'
        path = $dumpPath
        bytes = [long]$dumpItem.Length
        sha256 = (Get-Sha256 $dumpPath)
        magic = $dumpMagic
        format = 'PostgreSQL custom dump (--format=custom, --schema=table_store)'
        nativeExit = $dumpCode
        logPath = $dumpLog
        metaJsonPath = ($dumpLog + '.meta.json')
        database = $script:Database
    }
    Write-JsonEvidence (Join-Path $LogDir 'pg-dump-artifact.json') $dumpEvidence

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

    # Prove the just-created restore target is empty before pg_restore runs.
    # The query also binds the actual database/host/port identity of this run.
    $preflightQuery = "SELECT current_database()::text || '|' || COALESCE(host(inet_server_addr()), '') || '|' || COALESCE(inet_server_port()::text, '') || '|' || (SELECT count(*) FROM information_schema.schemata WHERE schema_name = 'table_store')::text || '|' || (SELECT count(*) FROM information_schema.tables WHERE table_schema = 'table_store')::text || '|' || (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'table_store')::text"
    $preflightLog = Join-Path $LogDir 'restore-preflight-empty.log'
    $preflightCode = Invoke-Native -FilePath $script:Psql -Arguments @(
        '-h', '127.0.0.1', '-p', "$Port", '-U', 'mahjong_pg', '-d', $script:RestoreDatabase,
        '-v', 'ON_ERROR_STOP=1', '-tA', '-c', $preflightQuery
    ) -LogPath $preflightLog -TimeoutSeconds 60
    $preflightStdoutPath = $preflightLog + '.stdout'
    $preflightStdout = if (Test-Path -LiteralPath $preflightStdoutPath) { Read-FileTextShared $preflightStdoutPath } else { '' }
    $preflight = Test-RestorePreflightEmpty -RawOutput $preflightStdout -ExpectedDatabase $script:RestoreDatabase `
        -ExpectedHost '127.0.0.1' -ExpectedPort $Port
    $preflightEvidence = [ordered]@{
        schema = 'p52-pg-restore-preflight.v1'
        query = $preflightQuery
        database = $script:RestoreDatabase
        host = '127.0.0.1'
        port = $Port
        nativeExit = $preflightCode
        stdoutPath = $preflightStdoutPath
        logPath = $preflightLog
        metaJsonPath = ($preflightLog + '.meta.json')
        rawOutput = $preflightStdout
        decision = $preflight
    }
    Write-JsonEvidence (Join-Path $LogDir 'restore-preflight.json') $preflightEvidence
    if ($preflightCode -ne 0 -or -not $preflight.Ok) {
        throw "Refusing pg_restore: empty-target preflight failed (exit=$preflightCode): $($preflight.Reason)"
    }

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

    $postCountsQuery = "SELECT (SELECT count(*) FROM table_store.table_event)::text || '|' || (SELECT count(*) FROM table_store.request_receipt)::text || '|' || (SELECT count(*) FROM table_store.settlement_outbox)::text || '|' || (SELECT count(*) FROM information_schema.tables WHERE table_schema = 'table_store')::text"
    $postCountsRaw = (Get-PgScalar -Database $script:RestoreDatabase -Sql $postCountsQuery).Trim()
    $postCountParts = @($postCountsRaw -split '\|')
    if ($postCountParts.Count -ne 4) { throw "Post-restore row-count query returned '$postCountsRaw' (expected 4 fields)." }
    foreach ($part in $postCountParts) {
        if ($part.Trim() -notmatch '^\d+$') { throw "Post-restore row-count query returned a non-integer field '$part'." }
    }
    $restoredEvents = [int]$postCountParts[0].Trim()
    $restoredReceipts = [int]$postCountParts[1].Trim()
    $restoredOutbox = [int]$postCountParts[2].Trim()
    $restoredTables = [int]$postCountParts[3].Trim()
    if ($restoredEvents -le 0) { throw "Restored database has no table_event rows." }
    if ($restoredReceipts -le 0) { throw "Restored database has no request_receipt rows." }
    if ($restoredOutbox -le 0) { throw "Restored database has no settlement_outbox rows." }
    if ($restoredTables -le 0) { throw "Restored database has no table_store tables." }

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
    $restoreReader = Get-Content -LiteralPath $restoreResultPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not [bool]$restoreReader.recovered -or [int]$restoreReader.eventCount -le 0) {
        throw "Restored database did not recover the committed event/receipt/outbox state."
    }
    if ([int]$restoreReader.eventCount -ne $restoredEvents) {
        throw "Restored reader eventCount=$($restoreReader.eventCount) does not match post-restore table_event rows=$restoredEvents."
    }
    if ($restoreReader.outboxSha256 -ne $markerJson.outboxSha256) {
        throw "Restored reader outbox SHA does not match the marker outbox SHA."
    }
    if (-not [bool]$restoreReader.terminal) {
        throw "Restored reader did not observe the terminal durable state."
    }
    $restoreEvidence = [ordered]@{
        schema = 'p52-pg-backup-restore.v1'
        generatedUtc = (Get-Date).ToUniversalTime().ToString('o')
        dump = $dumpEvidence
        preRestore = $preflightEvidence
        drop = [ordered]@{ nativeExit = $dropCode; logPath = $dropLog; metaJsonPath = ($dropLog + '.meta.json') }
        create = [ordered]@{ nativeExit = $createCode; logPath = $createLog; metaJsonPath = ($createLog + '.meta.json') }
        restore = [ordered]@{
            nativeExit = $restoreCode
            logPath = $restoreLog
            metaJsonPath = ($restoreLog + '.meta.json')
            database = $script:RestoreDatabase
            postCounts = [ordered]@{ events = $restoredEvents; receipts = $restoredReceipts; outbox = $restoredOutbox; tables = $restoredTables }
        }
        grant = [ordered]@{ nativeExit = $grantCode; logPath = $grantLog; metaJsonPath = ($grantLog + '.meta.json') }
        reader = [ordered]@{
            nativeExit = $restoreReaderCode
            logPath = $restoreReaderLog
            metaJsonPath = ($restoreReaderLog + '.meta.json')
            resultPath = $restoreResultPath
            resultSha256 = (Get-Sha256 $restoreResultPath)
            recovered = [bool]$restoreReader.recovered
            terminal = [bool]$restoreReader.terminal
            eventCount = [int]$restoreReader.eventCount
            recoveryOwnerEpoch = [int]$restoreReader.recoveryOwnerEpoch
            outboxSha256 = $restoreReader.outboxSha256
            markerOutboxSha256 = $markerJson.outboxSha256
        }
        closure = [ordered]@{
            emptyBeforeRestore = [bool]$preflight.Ok
            dumpNativeExitZero = ($dumpCode -eq 0)
            restoreNativeExitZero = ($restoreCode -eq 0)
            eventCountMatchesTableRows = ([int]$restoreReader.eventCount -eq $restoredEvents)
            outboxShaMatchesMarker = ($restoreReader.outboxSha256 -eq $markerJson.outboxSha256)
        }
    }
    Write-JsonEvidence (Join-Path $LogDir 'backup-restore-evidence.json') $restoreEvidence
    Add-Stage 'backup-restore' 'PASS' 0 "pg_dump/pg_restore into verified-empty DB; events=$restoredEvents receipts=$restoredReceipts outbox=$restoredOutbox tables=$restoredTables; reader eventCount=$($restoreReader.eventCount) ownerEpoch=$($restoreReader.recoveryOwnerEpoch)"

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
        if (-not $script:LogDirOwned) { throw 'LogDir ownership was not acquired; refusing to seal evidence' }
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
        $summaryPath = Join-Path $LogDir 'wrapper-summary.json'
        $wrapperLogPath = Join-Path $LogDir 'wrapper.log'
        $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding utf8 -ErrorAction Stop
        $logLines = @($script:Lines)
        $utf8Log = New-Object System.Text.UTF8Encoding($false)
        [System.IO.File]::WriteAllText($wrapperLogPath, ($logLines -join [Environment]::NewLine), $utf8Log)
        $summaryReadback = Get-Content -LiteralPath $summaryPath -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json
        if ($null -eq $summaryReadback -or [int]$summaryReadback.exit -ne [int]$script:Exit) {
            throw 'wrapper-summary.json write/readback mismatch'
        }
        if (-not (Test-Path -LiteralPath $wrapperLogPath -PathType Leaf)) {
            throw 'wrapper.log write/readback failed'
        }
        $null = Get-Content -LiteralPath $wrapperLogPath -Raw -Encoding UTF8 -ErrorAction Stop

        $manifestPath = Join-Path $LogDir 'manifest.sha256.json'
        $manifestTempPath = $manifestPath + '.tmp'
        if (Test-Path -LiteralPath $manifestPath -PathType Container) {
            throw "manifest path is a directory: $manifestPath"
        }
        $manifest = @()
        Get-ChildItem -LiteralPath $LogDir -File -Recurse -ErrorAction Stop | Sort-Object FullName | Where-Object {
            $full = [IO.Path]::GetFullPath($_.FullName)
            $full -ne [IO.Path]::GetFullPath($manifestPath) -and $full -ne [IO.Path]::GetFullPath($manifestTempPath)
        } | ForEach-Object {
            $manifest += [ordered]@{
                path = $_.FullName.Substring($LogDir.Length).TrimStart('\')
                sha256 = Get-Sha256 $_.FullName
                bytes = $_.Length
            }
        }
        if ($manifest.Count -eq 0) { throw 'manifest has no rows' }
        $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestTempPath -Encoding utf8 -ErrorAction Stop
        $manifestReadback = Get-Content -LiteralPath $manifestTempPath -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json
        if (@($manifestReadback).Count -ne $manifest.Count) { throw 'manifest write/readback row-count mismatch' }
        Move-Item -LiteralPath $manifestTempPath -Destination $manifestPath -Force -ErrorAction Stop
        $sealedManifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json
        if (@($sealedManifest).Count -ne $manifest.Count) { throw 'sealed manifest row-count mismatch' }
        if (@(@($sealedManifest) | Where-Object { $_.path -eq 'manifest.sha256.json' -or $_.path -eq 'manifest.sha256.json.tmp' }).Count -gt 0) {
            throw 'sealed manifest includes itself'
        }
        foreach ($row in @($sealedManifest)) {
            $rowPath = Join-Path $LogDir ([string]$row.path)
            $rowItem = Get-Item -LiteralPath $rowPath -ErrorAction Stop
            if ([long]$rowItem.Length -ne [long]$row.bytes) { throw "sealed row bytes mismatch: $($row.path)" }
            if ((Get-Sha256 $rowPath) -ne [string]$row.sha256) { throw "sealed row hash mismatch: $($row.path)" }
        }
        $script:SealOk = $true
    }
    catch {
        $script:SealOk = $false
        Write-Step ("evidence seal FAIL: " + $_.Exception.Message)
        if ($script:Exit -eq 0) { $script:Exit = 1 }
    }
}

if ($script:Exit -eq 0 -and $script:SealOk) { Write-Host 'P52-TABLE-POSTGRES-PASS' }
elseif ($script:Exit -eq 2) { Write-Host 'P52-TABLE-POSTGRES-BLOCKED' }
else { Write-Host 'P52-TABLE-POSTGRES-FAIL' }
exit $script:Exit
