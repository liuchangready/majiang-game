#requires -Version 5.1
<#
.SYNOPSIS
    Real end-to-end verification for the P5.4b-A durable table host: the accepted
    P5.2 table_store PostgreSQL database wired behind REAL TableService host
    processes (cold bootstrap, kill -9, cold recovery, dual-host fencing,
    fail-closed corruption, scope revocation, cross-account refusal).

.DESCRIPTION
    The wrapper owns one disposable loopback-only PostgreSQL 16.10 cluster on a
    unique non-default port under this worktree's ignored artifacts directory
    (same verified package pin, ownership marker and cleanup discipline as
    tools/ci/verify-v5-table-postgres.ps1, which this script does NOT modify and
    whose shared cluster it never attaches to). It starts REAL Mahjong.TableService
    processes (never an in-proc substitute), drives them with an independent
    PersistenceWorker through the real WSS wire, kills the host process with -9
    after its durable commit and requires a successor process to cold-recover the
    room: the historical receipt stays queryable, the same-key retry under the new
    scope fails closed (retryable RESPONSE_WINDOW_AUTHORITY_UNAVAILABLE — the
    accepted D-1-01(b) trusted boundary cannot derive pre-crash response
    eligibility in a fresh process) and the APPLIED ledger row is unchanged.
    Fault injection (tampered event hash, event gap, scope
    revocation) happens ONLY inside the independent test worker.

    Stages:
      1. isolated cluster (mahjong_p54b_test migrated, mahjong_p54b_empty never migrated)
      2. XML gating self-check (negative cases must stay red)
      3. fresh builds: TableService, 3 accepted test assemblies, DurableHost.Tests,
         PersistenceWorker
      4. regression gates: TableService (152), TableRuntime (400), Infrastructure
         (35) direct xUnit v3 DLL runs - every count attribute gated, zero skips
      5. durable-host test suite (real PG, fail-closed env guard)
      6. E2E over CONTROLLED WSS (worker certgen test root/leaf; every host serves
         the leaf, every probe validates it with a strict per-connection
         CustomRootTrust callback - name and validity included):
         phase-1 (bootstrap + APPLIED + same-key replay + the public discard fact
         recorded, session held open) -> kill -9 the host while the client is still
         connected (the true crash shape: no graceful teardown can poison the
         checkpoint) -> phase-2 cold recovery in a NEW process (receipt query;
         same-key retry fails closed; APPLIED ledger row unchanged; the pre-crash
         public discard history re-read from the live successor must still START
         with the recorded tiles - a re-dealt wall cannot)
      7. controlled-WSS refusal negatives: a foreign root, a wrong-name leaf and an
         expired leaf are each refused by the strict per-connection trust on the
         real persistent host chain (no global trust, no bypass callback)
      8. dual host over one DB: foreign live lease refuses, first host still serves
      9. DB-unavailable host refuses to start (fail closed, no listener)
     10. revoked scope: recorded receipt still queryable, zero new side effects
     11. cross-account receipt query refused (SCOPE_MISMATCH), unknown request inert
     12. tampered event hash / event gap: each corrupted room is killed mid-session
         (crash shape), and a successor process refuses the room and readiness
         reports known-and-not-serving (503)
     13. ownership-verified stop/delete, logs, XML, SHA-256 manifest and summary

    NOT covered by design (NOT_RUN for the next P5.4b batch): PRODUCTION TLS/WSS
    termination (real CA-issued certificates, public endpoints, Lobby HTTPS ticket
    issuance), multi-client full-match play, account asset settlement and
    cross-physical-network paths. Everything above runs on CONTROLLED WSS with the
    test root the worker itself generates - it proves the persistent host chain
    terminates TLS and enforces per-connection validation, not a production
    certificate deployment.

    A missing PG binary/key/dotnet path is BLOCKED (exit 2), never a skipped or
    zero-test green.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$DotnetPath,
    [int]$Port = 55594,
    [string]$Root,
    [string]$LogDir,
    [string]$PgZipPath,
    [int]$HostPort1 = 56610,
    [int]$HostPort2 = 56611,
    [int]$HostPort3 = 56612
)

$ErrorActionPreference = 'Stop'
$script:RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$script:Stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = Join-Path $script:RepoRoot ("artifacts\p54b-pg-cluster-" + $script:Stamp)
}
if ([string]::IsNullOrWhiteSpace($LogDir)) {
    $LogDir = Join-Path $script:RepoRoot ("artifacts\p54b-durable-host-" + $script:Stamp)
}
$Root = [System.IO.Path]::GetFullPath($Root)
$LogDir = [System.IO.Path]::GetFullPath($LogDir)
$script:LogDir = $LogDir
$script:Stages = New-Object System.Collections.Generic.List[object]
$script:Lines = New-Object System.Collections.Generic.List[string]
$script:Exit = 0
$script:ClusterStarted = $false
$script:HostProcesses = New-Object System.Collections.Generic.List[object]
$script:WorkerProcesses = New-Object System.Collections.Generic.List[object]
$script:PgBin = Join-Path $Root 'pgsql\bin'
$script:PgCtl = Join-Path $script:PgBin 'pg_ctl.exe'
$script:InitDb = Join-Path $script:PgBin 'initdb.exe'
$script:Psql = Join-Path $script:PgBin 'psql.exe'
$script:Createdb = Join-Path $script:PgBin 'createdb.exe'
$script:PgIsReady = Join-Path $script:PgBin 'pg_isready.exe'
$script:DataDir = Join-Path $Root 'data'
$script:Marker = Join-Path $Root 'pg-owner.json'
$script:PgLog = Join-Path $Root 'logs\postgres.log'
$script:Database = 'mahjong_p54b_test'
$script:EmptyDatabase = 'mahjong_p54b_empty'
# The accepted P5.2 Infrastructure suite keeps its own strict env guard with a
# mahjong_p52_test database-name prefix; this wrapper hosts that suite on its OWN
# disposable cluster with an identically-named database (never the shared P5.2 one).
$script:P52Database = 'mahjong_p52_test'
$script:PgZipSha256 = 'EBB3B6AF4FA69DEA9951B66855BC4D42DC04E56CCB9AA7024CE3C58BD89D6B0C'
$script:EnvNames = @(
    'MAHJONG_P54B_TEST_PG',
    'MAHJONG_P54B_TEST_PG_ADMIN',
    'MAHJONG_P54B_TEST_PG_EMPTY',
    'MAHJONG_P54B_TEST_PG_KEY',
    'MAHJONG_P54B_TEST_PG_EVIDENCE',
    'MAHJONG_P52_TEST_POSTGRES',
    'MAHJONG_P52_TEST_POSTGRES_ADMIN',
    'MAHJONG_P52_PG_EVIDENCE',
    'MAHJONG_TABLE_PG_PAYLOAD_KEY',
    'TABLESERVICE_STORE',
    'TABLESERVICE_HTTP_PORT',
    'TABLESERVICE_PG_CONNECTION',
    'TABLESERVICE_PG_PAYLOAD_KEY',
    'TABLESERVICE_PG_APPLY_MIGRATIONS',
    'TABLESERVICE_TICKET_KEYS_FILE',
    'TABLESERVICE_ROOM_TICK_INTERVAL_MS',
    'TABLESERVICE_HEARTBEAT_INTERVAL_MS',
    'TABLESERVICE_IDLE_DEAD_MS',
    'TABLESERVICE_HANDSHAKE_TIMEOUT_MS',
    'TABLESERVICE_LEASE_TTL_SECONDS',
    'TABLESERVICE_TLS_PFX',
    'TABLESERVICE_TLS_PFX_PASSWORD',
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
    $line = '[p54b-host] ' + $Message
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

function Test-PortInUse([int]$Value) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $task = $client.ConnectAsync('127.0.0.1', $Value)
        if (-not $task.Wait(400)) { return $false }
        return $client.Connected
    }
    catch { return $false }
    finally { $client.Dispose() }
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

    # Two measured PS 5.1 traps shape this launcher (both hit by this runner):
    #
    # 1. Start-Process loses the exit code. Whenever the cmdlet redirects
    #    stdout/stderr, the returned Process object's ExitCode reads EMPTY after
    #    exit and coercing empty with "if (-not $exitCode)" reported every failing
    #    child as 0 — one real E2E worker failure became a vacuous PASS (R7). The
    #    raw .NET Process API keeps the code, so the child is launched through it.
    #
    # 2. Pipe EOF belongs to the LAST handle holder, not the child. `pg_ctl start`
    #    exits while its spawned postgres keeps the inherited stdout/stderr pipes
    #    open for its whole life (same for a lingering Roslyn compiler server
    #    behind dotnet build), so awaiting ReadToEnd after WaitForExit hung the
    #    whole runner (R9). The pipes are therefore drained INCREMENTALLY — for
    #    the child's whole life (a full pipe would block the child) and then for
    #    a bounded grace after its exit — accumulating every byte that actually
    #    arrived instead of waiting for an EOF a grandchild may never send.
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    $psi.Arguments = $argumentLine
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $psi
    $outWriter = [System.IO.StreamWriter]::new($out)
    $errWriter = [System.IO.StreamWriter]::new($err)
    try {
        if (-not $process.Start()) {
            throw "The process could not be started: $FilePath"
        }
        $deadline = [System.Diagnostics.Stopwatch]::StartNew()
        $outBuilder = New-Object System.Text.StringBuilder
        $errBuilder = New-Object System.Text.StringBuilder
        foreach ($side in @(
            @{ Reader = $process.StandardOutput; Target = $outBuilder },
            @{ Reader = $process.StandardError; Target = $errBuilder })) {
            $reader = $side.Reader
            $buffer = New-Object char[] 4096
            while ($true) {
                $read = $reader.ReadAsync($buffer, 0, $buffer.Length)
                while (-not $read.IsCompleted -and
                       -not $process.HasExited -and
                       $deadline.ElapsedMilliseconds -lt ($TimeoutSeconds * 1000)) {
                    Start-Sleep -Milliseconds 25
                }
                if (-not $read.IsCompleted) {
                    # The child is gone (or the budget is spent); give in-flight
                    # bytes a bounded grace, then stop without an EOF.
                    $grace = [System.Diagnostics.Stopwatch]::StartNew()
                    while (-not $read.IsCompleted -and
                           $grace.ElapsedMilliseconds -lt 10000 -and
                           $deadline.ElapsedMilliseconds -lt ($TimeoutSeconds * 1000)) {
                        Start-Sleep -Milliseconds 50
                    }
                    if ($read.IsCompleted) {
                        $n = $read.GetAwaiter().GetResult()
                        if ($n -gt 0) { [void]$side.Target.Append($buffer, 0, $n) }
                    }
                    break
                }
                $n = $read.GetAwaiter().GetResult()
                if ($n -le 0) { break }
                [void]$side.Target.Append($buffer, 0, $n)
            }
        }
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill() } catch { }
            throw "Command timed out after $TimeoutSeconds seconds: $FilePath"
        }
        $process.WaitForExit()

        $outWriter.Write($outBuilder.ToString())
        $outWriter.Flush()
        $errWriter.Write($errBuilder.ToString())
        $errWriter.Flush()
        # Read before Dispose: the exit code needs the process handle.
        $exitCode = $process.ExitCode
    }
    finally {
        $outWriter.Dispose()
        $errWriter.Dispose()
        $process.Dispose()
    }

    $text = ''
    if (Test-Path -LiteralPath $out) { $text += (Get-Content -LiteralPath $out -Raw) }
    if (Test-Path -LiteralPath $err) {
        $errorText = Get-Content -LiteralPath $err -Raw
        if ($errorText) { $text += $errorText }
    }
    Set-Content -LiteralPath $LogPath -Value $text -Encoding utf8
    return [int]$exitCode
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
    if ($marker.owner -ne 'mahjong-p54b-pg' -or
        -not [string]::Equals([System.IO.Path]::GetFullPath($marker.root), $Root, [StringComparison]::OrdinalIgnoreCase) -or
        [int]$marker.port -ne $Port) {
        throw "Refusing to touch ${Root}: marker does not belong to this exact root/port."
    }
    return $marker
}

function Initialize-Cluster {
    if (Test-PortInUse -Value $Port) {
        throw "BLOCKED: port $Port is already in use; refusing to attach to an unknown PostgreSQL."
    }
    foreach ($hostPort in @($HostPort1, $HostPort2, $HostPort3)) {
        if (Test-PortInUse -Value $hostPort) {
            throw "BLOCKED: host port $hostPort is already in use; refusing to start over an unknown listener."
        }
    }
    if ($Port -eq 5432 -or $Port -eq 5433 -or $Port -eq 55460 -or $Port -eq 55463 -or
        $Port -eq 55592 -or $Port -eq 55593) {
        throw "BLOCKED: port $Port is reserved by another wrapper; choose a unique P5.4b test port."
    }

    if (Test-Path -LiteralPath $Root) {
        if (-not (Test-Path -LiteralPath $script:Marker)) {
            $items = @(Get-ChildItem -LiteralPath $Root -Force -ErrorAction SilentlyContinue)
            if ($items.Count -gt 0) {
                throw "BLOCKED: $Root is non-empty and has no P5.4b ownership marker."
            }
        }
    }
    else {
        New-Item -ItemType Directory -Path $Root -Force | Out-Null
    }

    if (-not (Test-Path -LiteralPath $script:Marker)) {
        $marker = [ordered]@{
            owner = 'mahjong-p54b-pg'
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

        # Use the Windows system bsdtar explicitly: a resolved GNU tar (e.g. from a
        # Git-bash PATH) treats a drive-letter path as a remote host spec.
        $systemTar = Join-Path $env:SystemRoot 'System32\tar.exe'
        if (Test-Path -LiteralPath $systemTar) {
            $extractCode = Invoke-Native -FilePath $systemTar -Arguments @('-xf', $zipCopy, '-C', $Root) -LogPath (Join-Path $LogDir 'extract.log') -TimeoutSeconds 300
        }
        else {
            Expand-Archive -LiteralPath $zipCopy -DestinationPath $Root -Force
            $extractCode = 0
        }

        if ($extractCode -ne 0) { throw "Extracting PostgreSQL failed with exit $extractCode." }
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
            '# --- P5.4b disposable verification cluster ---',
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
    if ($startCode -ne 0) { throw "pg_ctl start failed with exit $startCode (see $startLog)." }
    $script:ClusterStarted = $true
    Wait-PgReady
    $dataDir = Get-PgDataDirectory
    if (-not [string]::Equals([System.IO.Path]::GetFullPath($dataDir), [System.IO.Path]::GetFullPath($script:DataDir), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to use a listener whose data_directory ($dataDir) is not this cluster."
    }

    foreach ($database in @($script:Database, $script:EmptyDatabase, $script:P52Database)) {
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

function Stop-And-RemoveCluster {
    if (-not (Test-Path -LiteralPath $Root)) { return }
    $marker = Assert-OwnedCluster

    # Every host process this wrapper spawned must be gone before the cluster stops.
    foreach ($hostProcess in $script:HostProcesses) {
        if (-not $hostProcess.HasExited) {
            Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue
        }
    }

    foreach ($workerProcess in $script:WorkerProcesses) {
        if (-not $workerProcess.HasExited) {
            Stop-Process -Id $workerProcess.Id -Force -ErrorAction SilentlyContinue
        }
    }

    if (Test-PortInUse -Value $Port) {
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

    Remove-TreeLongPath $safeRoot
    if ([System.IO.Directory]::Exists('\\?\' + $safeRoot)) { throw "Cleanup failed: $safeRoot still exists." }
    Write-Step "ownership-verified cluster removed: $safeRoot"
}

function Remove-TreeLongPath {
    # D-R1-04: recursive deletion that survives MAX_PATH. PS 5.1's Remove-Item
    # -Recurse walks every child through the legacy normalization limit, so a deep
    # artifacts cluster Root (PG relation names stack fast) dies mid-delete and
    # leaves the directory half-removed. The \\?\ extended-length form hands the
    # tree to System.IO.Directory.Delete verbatim — no registry pivot
    # (LongPathsEnabled stays untouched), no shell out, no worker process.
    # Ownership discipline lives with the CALLER: every guard in
    # Stop-And-RemoveCluster runs before this, and a failure here propagates so
    # the wrapper's cleanup gate turns the run FAIL — never a quiet PASS over a
    # half-deleted cluster.
    param([Parameter(Mandatory = $true)][string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'Remove-TreeLongPath: refusing an empty path.' }
    # Refuse relative input on the RAW path: GetFullPath would silently resolve it
    # against the process CWD and turn the refusal into a no-op deletion elsewhere.
    if (-not [System.IO.Path]::IsPathRooted($Path)) { throw "Remove-TreeLongPath: refusing a relative path: $Path" }
    # \\?\ disables normalization, so the input must already be canonical: fullpath
    # it (legal here — guarded callers pass short roots; only the CHILDREN are deep).
    $full = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    $extended = '\\?\' + $full
    if (-not [System.IO.Directory]::Exists($extended)) { return }
    [System.IO.Directory]::Delete($extended, $true)
    if ([System.IO.Directory]::Exists($extended)) {
        throw "Remove-TreeLongPath: the tree still exists after delete: $full"
    }
}

function Assert-ClusterRemovalSelfTest {
    # D-R1-04: the removal mechanics, exercised on the production shape (a SHORT
    # root whose CHILDREN overflow MAX_PATH) plus the two failure disciplines:
    # a locked tree must propagate the failure (never swallow), and a relative
    # path must be refused. Deep paths are built through \\?\ creation itself,
    # because PS 5.1's plain CreateDirectory hits the same 260 limit it is being
    # tested against.
    $tmp = Join-Path $script:LogDir 'tmp\cluster-removal-selfcheck'
    New-Item -ItemType Directory -Path $tmp -Force | Out-Null

    $pad = 'relational-map-segment-with-long-name-'
    $deep = $tmp
    for ($i = 0; $i -lt 8; $i++) { $deep = Join-Path $deep ($pad + $i) }
    $deepFile = Join-Path $deep 'pg_filenode.map.bin'
    if ($deepFile.Length -le 260) { throw 'cluster removal self-check: the probe tree did not reach MAX_PATH depth.' }
    New-Item -ItemType Directory -Path ('\\?\' + $deep) -Force | Out-Null
    [System.IO.File]::WriteAllText(('\\?\' + $deepFile), 'probe')

    $checked = 0
    Remove-TreeLongPath $tmp
    if ([System.IO.Directory]::Exists('\\?\' + $deep)) {
        throw "cluster removal self-check: the deep tree survived deletion at '$deep'."
    }
    $checked++

    # Locked tree: an exclusive handle on a child must surface as a THROW — a
    # silent partial delete would let the wrapper report a clean cluster removal.
    New-Item -ItemType Directory -Path ('\\?\' + $deep) -Force | Out-Null
    [System.IO.File]::WriteAllText(('\\?\' + $deepFile), 'locked')
    $lock = [System.IO.File]::Open(('\\?\' + $deepFile), 'Open', 'Read', 'None')
    try {
        $threw = $false
        try { Remove-TreeLongPath $tmp } catch { $threw = $true }
        if (-not $threw) { throw 'cluster removal self-check: deleting a locked tree did not fail loudly.' }
    }
    finally { $lock.Dispose() }
    Remove-TreeLongPath $tmp
    if ([System.IO.Directory]::Exists('\\?\' + $deep)) {
        throw 'cluster removal self-check: the locked tree could not be removed after the handle was released.'
    }
    $checked++

    $threw = $false
    try { Remove-TreeLongPath (Join-Path 'relative' 'tree') } catch { $threw = $true }
    if (-not $threw) { throw 'cluster removal self-check: a relative path was not refused.' }
    $checked++

    return $checked
}

function Get-XmlCounts([string]$XmlPath) {
    if (-not (Test-Path -LiteralPath $XmlPath)) { throw "Result XML is missing: $XmlPath" }
    # D-R2-03: PS 5.1's default Get-Content decoding is the ANSI code page; an NUnit XML
    # declaring UTF-8 with a Chinese failure message then decodes into mojibake that no
    # longer matches its own end tags. XmlDocument.Load honours the file's BOM/declared
    # encoding exactly, so a real failing run is counted instead of being misreported as
    # "bad XML". Malformed XML still throws (the gating self-check pins that).
    $doc = New-Object System.Xml.XmlDocument
    $doc.PreserveWhitespace = $false
    try {
        $doc.Load($XmlPath)
    }
    catch [System.Xml.XmlException] {
        $message = $_.Exception.Message
        if ($message.Length -gt 300) { $message = $message.Substring(0, 300) + '…' }
        throw "Result XML is not well-formed: $XmlPath ($message)"
    }
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

function Get-StableEvidenceFiles([string]$Directory) {
    # D-R2-03: the runner points TEMP/TMP at "$LogDir\tmp", so Roslyn's
    # VBCSCompiler/AnalyzerAssemblyLoader caches live INSIDE the evidence tree and vanish
    # whenever the compiler server shuts down. Temporary build state is not acceptance
    # evidence: isolate it by enumerating every stable top-level entry and never descending
    # into "tmp". Everything returned here IS stable evidence.
    if (-not (Test-Path -LiteralPath $Directory)) { return @() }
    $files = New-Object System.Collections.Generic.List[object]
    foreach ($file in @(Get-ChildItem -LiteralPath $Directory -File -Force -ErrorAction Stop)) {
        if ($file.Name -ne 'manifest.sha256.json' -and $file.Name -ne 'manifest.errors.json') {
            $files.Add($file)
        }
    }

    foreach ($dir in @(Get-ChildItem -LiteralPath $Directory -Directory -Force -ErrorAction Stop)) {
        if ([string]::Equals($dir.Name, 'tmp', [StringComparison]::OrdinalIgnoreCase)) { continue }
        foreach ($file in @(Get-ChildItem -LiteralPath $dir.FullName -File -Recurse -Force -ErrorAction Stop)) {
            $files.Add($file)
        }
    }

    return @($files | Sort-Object FullName)
}

function Write-EvidenceManifest {
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [scriptblock]$BeforeHash
    )
    $manifestPath = Join-Path $Directory 'manifest.sha256.json'
    $errorsPath = Join-Path $Directory 'manifest.errors.json'
    $files = @(Get-StableEvidenceFiles $Directory)
    if ($BeforeHash) { & $BeforeHash $files }
    $entries = New-Object System.Collections.Generic.List[object]
    $errors = New-Object System.Collections.Generic.List[object]
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($Directory.Length).TrimStart('\')
        try {
            if (-not (Test-Path -LiteralPath $file.FullName)) {
                throw 'stable evidence vanished between enumeration and hashing'
            }

            $entries.Add([ordered]@{
                path = $relative
                sha256 = Get-Sha256 $file.FullName
                bytes = (Get-Item -LiteralPath $file.FullName -Force).Length
            })
        }
        catch {
            $errors.Add([ordered]@{ path = $relative; error = $_.Exception.Message })
        }
    }

    # Both files are always written: a vanished file must never cost the manifest for the
    # remaining stable evidence, and the loss itself stays recorded. The caller turns a
    # non-zero error count into FAIL (never into a silent green).
    $entryArray = $entries.ToArray()
    $errorArray = $errors.ToArray()
    $entryJson = if ($entryArray.Count -eq 0) { '[]' } else { $entryArray | ConvertTo-Json -Depth 4 }
    $errorJson = if ($errorArray.Count -eq 0) { '[]' } else { $errorArray | ConvertTo-Json -Depth 4 }
    Set-Content -LiteralPath $manifestPath -Value $entryJson -Encoding utf8
    Set-Content -LiteralPath $errorsPath -Value $errorJson -Encoding utf8
    return $errors.Count
}

function Assert-EvidenceManifestSelfTest {
    $root = Join-Path $script:LogDir 'tmp\evidence-manifest-selfcheck'
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
    New-Item -ItemType Directory -Path (Join-Path $root 'tmp\VBCSCompiler') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $root 'logs') -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $root 'logs\a.xml'), '<assemblies/>')
    [System.IO.File]::WriteAllText((Join-Path $root 'logs\b.log'), 'stable log')
    [System.IO.File]::WriteAllText((Join-Path $root 'tmp\VBCSCompiler\volatile.dll'), 'temporary compiler cache')

    $listed = @(Get-StableEvidenceFiles $root)
    if (@($listed | Where-Object { $_.Name -eq 'volatile.dll' }).Count -ne 0) {
        throw 'evidence manifest self-check: a temporary compiler cache entered the stable set.'
    }
    if (@($listed | Where-Object { $_.Name -eq 'a.xml' }).Count -ne 1 -or
        @($listed | Where-Object { $_.Name -eq 'b.log' }).Count -ne 1) {
        throw 'evidence manifest self-check: a stable log/XML was excluded.'
    }

    # A stable evidence file disappearing between enumeration and hashing must leave the
    # OTHER stable entries hashed, record the loss, and report one error.
    $errors = Write-EvidenceManifest -Directory $root -BeforeHash {
        param($files)
        Remove-Item -LiteralPath (Join-Path $root 'logs\a.xml') -Force
    }
    if ($errors -ne 1) {
        throw "evidence manifest self-check: expected exactly one vanished-file error, got $errors."
    }
    $manifest = @(Get-Content -LiteralPath (Join-Path $root 'manifest.sha256.json') -Raw | ConvertFrom-Json)
    if (@($manifest | Where-Object { $_.path -eq 'logs\b.log' }).Count -ne 1) {
        throw 'evidence manifest self-check: the surviving stable log was not hashed.'
    }
    if (@($manifest | Where-Object { $_.path -eq 'tmp\VBCSCompiler\volatile.dll' }).Count -ne 0) {
        throw 'evidence manifest self-check: the temporary compiler cache was hashed.'
    }
    $errorList = @(Get-Content -LiteralPath (Join-Path $root 'manifest.errors.json') -Raw | ConvertFrom-Json)
    if (@($errorList | Where-Object { $_.path -eq 'logs\a.xml' }).Count -ne 1) {
        throw 'evidence manifest self-check: the vanished stable file was not recorded.'
    }

    # Normal shape: all stable files hashed, zero errors.
    [System.IO.File]::WriteAllText((Join-Path $root 'logs\a.xml'), '<assemblies/>')
    $cleanErrors = Write-EvidenceManifest -Directory $root
    if ($cleanErrors -ne 0) {
        throw "evidence manifest self-check: clean stable evidence reported $cleanErrors error(s)."
    }

    Remove-Item -LiteralPath $root -Recurse -Force
    return 2
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

    # D-R2-03: the exact shape that broke the caller run — a UTF-8 NUnit XML whose failure
    # message contains Chinese text and whose counts are a real red (19/20). It must parse
    # (the counts are read) and still be refused by the gate.
    $chinese = Join-Path $tmp 'failed-chinese.xml'
    $chineseXml = @'
<?xml version="1.0" encoding="utf-8"?>
<assemblies schema-version="3">
  <assembly total="20" passed="19" failed="1" skipped="0" errors="0" not-run="0">
    <test name="ColdRecovery_Continued" result="Fail">
      <failure><message>由于线程退出或应用程序请求，已中止 I/O 操作。</message></failure>
    </test>
  </assembly>
</assemblies>
'@
    [System.IO.File]::WriteAllText($chinese, $chineseXml, (New-Object System.Text.UTF8Encoding($false)))
    $chineseCounts = Get-XmlCounts $chinese
    if ($chineseCounts.total -ne 20 -or $chineseCounts.passed -ne 19 -or $chineseCounts.failed -ne 1) {
        throw "XML gating self-check misread the UTF-8/Chinese failure XML: total=$($chineseCounts.total) passed=$($chineseCounts.passed) failed=$($chineseCounts.failed)."
    }
    if (Test-XmlCountsPass $chineseCounts) {
        throw "XML gating self-check accepted the UTF-8/Chinese 19-of-20 red result."
    }
    $checked++

    # Genuinely malformed XML must still be refused, not silently counted as zero.
    $malformed = Join-Path $tmp 'malformed.xml'
    [System.IO.File]::WriteAllText($malformed, '<assemblies><assembly total="1" passed="1"', (New-Object System.Text.UTF8Encoding($false)))
    $threw = $false
    try { $null = Get-XmlCounts $malformed } catch { $threw = $true }
    if (-not $threw) { throw "XML gating self-check accepted malformed XML." }
    $checked++

    return $checked
}

function Assert-WorkerGateSelfTest {
    # D-R1-03: the hold gate must survive every stdout shape a live worker can
    # produce mid-flush. Each case pins one observed hazard: a zero-byte file
    # (Get-Content -Raw yields $null — a bare .IndexOf on it crashed the runner),
    # evidence JSON landing before the marker line, a segmented partial write, the
    # healthy marker, crash output with no flushed evidence, and the vanished-file
    # TOCTOU outcome. The negative cases must stay red, the healthy case green.
    $tmp = Join-Path $script:LogDir 'tmp\worker-gate-selfcheck'
    New-Item -ItemType Directory -Path $tmp -Force | Out-Null
    $evidence = Join-Path $tmp 'evidence.json'
    Set-Content -LiteralPath $evidence -Value '{"room":"selfcheck"}' -Encoding utf8
    $missing = Join-Path $tmp 'missing.json'

    $empty = Join-Path $tmp 'empty.log'
    [System.IO.File]::WriteAllText($empty, '')  # true zero-byte file, not a newline
    $jsonFirst = Join-Path $tmp 'json-first.log'
    Set-Content -LiteralPath $jsonFirst -Value '{"room":"selfcheck"}' -Encoding utf8
    $segmented = Join-Path $tmp 'segmented.log'
    Set-Content -LiteralPath $segmented -Value 'evidence written, holding-sess' -Encoding utf8
    $marked = Join-Path $tmp 'marked.log'
    Set-Content -LiteralPath $marked -Value "evidence written`r`nholding-session-open" -Encoding utf8

    $checked = 0
    $emptyText = Read-WorkerText $empty
    if ($null -eq $emptyText -or $emptyText -ne '') {
        throw 'worker gate self-check: a zero-byte stdout must snapshot as an empty string.'
    }
    if (Test-WorkerHeldOpen -EvidencePath $evidence -StdoutText $emptyText) {
        throw "worker gate self-check accepted case 'empty'."
    }
    $checked++

    if (Test-WorkerHeldOpen -EvidencePath $evidence -StdoutText (Read-WorkerText $jsonFirst)) {
        throw "worker gate self-check accepted case 'json-first'."
    }
    $checked++

    if (Test-WorkerHeldOpen -EvidencePath $evidence -StdoutText (Read-WorkerText $segmented)) {
        throw "worker gate self-check accepted case 'segmented'."
    }
    $checked++

    if (-not (Test-WorkerHeldOpen -EvidencePath $evidence -StdoutText (Read-WorkerText $marked))) {
        throw "worker gate self-check rejected case 'marked'."
    }
    $checked++

    if (Test-WorkerHeldOpen -EvidencePath $missing -StdoutText (Read-WorkerText $marked)) {
        throw "worker gate self-check accepted case 'no-evidence'."
    }
    $checked++

    if (Test-WorkerHeldOpen -EvidencePath $evidence -StdoutText (Read-WorkerText (Join-Path $tmp 'vanished.log'))) {
        throw "worker gate self-check accepted case 'vanished'."
    }
    $checked++

    return $checked
}

function Invoke-TestRun {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$DllPath,
        [string[]]$ExtraArguments = @(),
        [int]$TimeoutSeconds = 900
    )
    $xml = Join-Path $script:LogDir ($Name + '.xml')
    $log = Join-Path $script:LogDir ($Name + '.log')
    $code = Invoke-Native -FilePath $DotnetPath -Arguments (@($DllPath, '-noColor', '-noLogo', '-result-xml', $xml) + $ExtraArguments) -LogPath $log -TimeoutSeconds $TimeoutSeconds
    if (-not (Test-Path -LiteralPath $xml)) {
        $tail = ''
        if (Test-Path -LiteralPath $log) {
            $tail = Get-Content -LiteralPath $log -Raw
            if ($tail.Length -gt 1500) { $tail = $tail.Substring($tail.Length - 1500) }
        }

        throw "$Name produced no result XML (process exit $code) — the run died before finishing. Log tail: $tail"
    }

    $counts = Get-XmlCounts $xml
    if ($code -ne 0 -or -not (Test-XmlCountsPass $counts)) {
        throw "$Name failed: exit=$code total=$($counts.total) passed=$($counts.passed) failed=$($counts.failed) errors=$($counts.errors) skipped=$($counts.skipped) notRun=$($counts.notRun)."
    }

    $dllSha256 = (Get-FileHash -LiteralPath $DllPath -Algorithm SHA256).Hash
    Add-Stage $Name 'PASS' $code "dll=$DllPath sha256=$dllSha256 total=$($counts.total) passed=$($counts.passed) failed=0 skipped=0 errors=0 notRun=0"
}

function Invoke-Worker {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [int]$TimeoutSeconds = 180
    )
    $log = Join-Path $script:LogDir ($Name + '.log')
    $code = Invoke-Native -FilePath $DotnetPath -Arguments (@($script:WorkerDll) + $Arguments) -LogPath $log -TimeoutSeconds $TimeoutSeconds
    if ($code -ne 0) {
        $tail = ''
        if (Test-Path -LiteralPath $log) {
            $tail = (Get-Content -LiteralPath $log -Raw)
            if ($tail.Length -gt 600) { $tail = $tail.Substring($tail.Length - 600) }
        }
        throw "worker $Name failed with exit $code (see $log): $tail"
    }

    return $code
}

function Invoke-HostHealth {
    # D-R1-02: readiness under the controlled-WSS shape rides the SAME strict
    # per-connection trust as the admissions themselves — the worker's health mode
    # validates the served certificate (isolated root, name, validity) and exits 0
    # only on the EXACT expected status. Expect 200 for liveness (Start-TableHost
    # owns that call), 503 for known-and-not-serving readiness below. A plain
    # PowerShell GET could not tell a live host from a wrong-certificate impostor,
    # so there is deliberately no unvalidated fallback.
    param(
        [Parameter(Mandatory = $true)][int]$HostPort,
        [Parameter(Mandatory = $true)][string]$Name
    )
    return Invoke-Native -FilePath $DotnetPath -Arguments @(
        $script:WorkerDll, 'health',
        '--url', "https://127.0.0.1:$HostPort/health/ready",
        '--ca-root', $script:TlsRootPem,
        '--expect', '503') -LogPath (Join-Path $script:LogDir ($Name + '.log')) -TimeoutSeconds 30
}

function Start-TableHost {
    param(
        [Parameter(Mandatory = $true)][int]$HostPort,
        [Parameter(Mandatory = $true)][string]$Name,
        [string]$TlsPfx = $script:TlsLeafPfx,
        [switch]$SkipHealthGate
    )
    $out = Join-Path $script:LogDir ($Name + '.stdout.log')
    $err = Join-Path $script:LogDir ($Name + '.stderr.log')
    Remove-Item -LiteralPath $out, $err -Force -ErrorAction SilentlyContinue
    # A production host binds a FIXED, declared port (TABLESERVICE_HTTP_PORT) so its
    # admission/health endpoints are reachable at a known address — never an ephemeral
    # one the wrapper would have to scrape out of stdout.
    $env:TABLESERVICE_HTTP_PORT = "$HostPort"
    if ($TlsPfx) {
        # Windows snapshots the child's environment block at process creation, so the
        # per-host leaf is set here and cleared right after Start: each host reads ITS
        # OWN leaf even though later stages spawn different hosts from this process.
        $env:TABLESERVICE_TLS_PFX = $TlsPfx
        $env:TABLESERVICE_TLS_PFX_PASSWORD = ''
    }
    $process = Start-Process -FilePath $DotnetPath -ArgumentList ('"' + $script:HostDll + '"') -WindowStyle Hidden -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
    if ($TlsPfx) {
        Remove-Item -LiteralPath 'env:TABLESERVICE_TLS_PFX' -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath 'env:TABLESERVICE_TLS_PFX_PASSWORD' -ErrorAction SilentlyContinue
    }
    $script:HostProcesses.Add($process) | Out-Null
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        if ($process.HasExited) {
            $stderrText = ''
            if (Test-Path -LiteralPath $err) { $stderrText = Get-Content -LiteralPath $err -Raw }
            # No exit code cited here: PS 5.1 Start-Process loses ExitCode on redirected
            # processes (measured), and the stderr text is the real diagnosis anyway.
            throw "Table host $Name exited before becoming live (see stderr): $stderrText"
        }
        # Plain TCP bound is only the WAIT signal (a raw connect proves a listener, not
        # TLS); the strict health gate below is the real liveness proof.
        if (Test-PortInUse -Value $HostPort) { break }
        Start-Sleep -Milliseconds 200
    }

    if (-not (Test-PortInUse -Value $HostPort)) {
        try { Stop-Process -Id $process.Id -Force } catch { }
        throw "Table host $Name never bound 127.0.0.1:$HostPort within 60 seconds."
    }

    if (-not $SkipHealthGate) {
        # Liveness = a worker HTTPS health GET through the SAME strict per-connection
        # root trust as the WSS admissions (isolated root.pem; no OS store, no global
        # callback). An http:// probe here would silently bless a host serving no TLS.
        $healthLog = Join-Path $script:LogDir ($Name + '.health.log')
        $healthCode = Invoke-Native -FilePath $DotnetPath -Arguments @(
            $script:WorkerDll, 'health',
            '--url', "https://127.0.0.1:$HostPort/health/live",
            '--ca-root', $script:TlsRootPem,
            '--expect', '200') -LogPath $healthLog -TimeoutSeconds 60
        if ($healthCode -ne 0) {
            throw "Table host $Name failed the strict TLS health gate (exit $healthCode, see $healthLog)."
        }
    }

    Write-Step "host $Name live on wss://127.0.0.1:$HostPort (pid $($process.Id), leaf $([System.IO.Path]::GetFileName($TlsPfx)))"
    return $process
}

function Stop-TableHost {
    # A hidden console host cannot receive Ctrl+C; every stop in this wrapper is a
    # forced kill, which is exactly the crash discipline the recovery chain demands.
    # The kill record keeps the evidence honest (forced=true, real exit code).
    param(
        [Parameter(Mandatory = $true)][object]$Process,
        [Parameter(Mandatory = $true)][string]$Name
    )
    if ($Process.HasExited) { return }
    $record = [ordered]@{
        name = $Name
        pid = $Process.Id
        killedUtc = (Get-Date).ToUniversalTime().ToString('o')
        forced = $true
    }
    Stop-Process -Id $Process.Id -Force
    $Process.WaitForExit(30000) | Out-Null
    # PS 5.1 Start-Process loses ExitCode on redirected processes (measured); record it
    # only when actually observed — never a fabricated zero.
    $observedExit = $Process.ExitCode
    if ($observedExit) { $record.exitCode = $observedExit }
    $jsonPath = Join-Path $script:LogDir ($Name + '.stop.json')
    $record | ConvertTo-Json | Set-Content -LiteralPath $jsonPath -Encoding utf8
    Write-Step "host $Name force-killed (pid $($Process.Id))"
}

function Read-WorkerText {
    # D-R1-03: a LOCAL SNAPSHOT of a live worker's redirected stream file, with the
    # empty case made explicit. Test-Path alone is a TOCTOU lie — the worker can
    # create, truncate or lock the file between the probe and the read — and
    # Get-Content -Raw returns $null for a zero-byte file, so a bare
    # $snapshot.IndexOf(...) crashed the runner ("You cannot call a method on a
    # null-valued expression") instead of reporting the worker's own diagnosis.
    # Always returns a string: '' when the file is absent, empty, unreadable or
    # mid-flush; the full text otherwise.
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Path)
    # Materialize before returning: `return [string](Get-Content ...)` on a zero-byte
    # file emits NOTHING from the cast expression and the caller receives $null again —
    # the exact hole this helper exists to close (measured, PS 5.1.19041).
    $text = ''
    try {
        if (Test-Path -LiteralPath $Path) {
            $raw = Get-Content -LiteralPath $Path -Raw -ErrorAction SilentlyContinue
            if ($null -ne $raw) { $text = [string]$raw }
        }
    }
    catch { $text = '' }
    return $text
}

function Test-WorkerHeldOpen {
    # D-R1-03: the hold gate as a PURE function of (evidence file, stdout snapshot).
    # Both facts are required: evidence JSON alone (written before the marker) and a
    # marker line alone (crash output with no flushed evidence) each hold the gate
    # closed. The self-test matrix exercises exactly this predicate.
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$EvidencePath,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$StdoutText
    )
    if ([string]::IsNullOrEmpty($EvidencePath) -or -not (Test-Path -LiteralPath $EvidencePath)) { return $false }
    return $StdoutText.IndexOf('holding-session-open', [StringComparison]::Ordinal) -ge 0
}

function Start-WorkerHold {
    # Runs a --hold-open worker phase as a LIVE process: the worker writes its evidence
    # JSON while the ticketed session stays open, then blocks forever. The runner polls
    # for the worker's own "holding-session-open" completion line (written only after
    # the evidence file is fully flushed) and returns the STILL-RUNNING process, so the
    # caller can order the host's forced kill BEFORE the worker's death — the true
    # crash shape the cross-boot recovery contract recovers from.
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$EvidencePath,
        [int]$TimeoutSeconds = 180
    )
    $out = Join-Path $script:LogDir ($Name + '.stdout.log')
    $err = Join-Path $script:LogDir ($Name + '.stderr.log')
    Remove-Item -LiteralPath $out, $err -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $EvidencePath -Force -ErrorAction SilentlyContinue
    $argumentList = @('"' + $script:WorkerDll + '"') + ($Arguments | ForEach-Object { '"' + $_ + '"' })
    $process = Start-Process -FilePath $DotnetPath -ArgumentList $argumentList -WindowStyle Hidden -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
    $script:WorkerProcesses.Add($process) | Out-Null
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if ($process.HasExited) {
            # No exit code cited here: PS 5.1 Start-Process loses ExitCode on redirected
            # processes (measured); the worker's own stderr line carries the real reason.
            $stderrText = Read-WorkerText $err
            $stdoutText = Read-WorkerText $out
            throw "worker $Name exited before holding its session open (see stderr/stdout). stderr: $stderrText stdout: $stdoutText"
        }
        if (Test-WorkerHeldOpen -EvidencePath $EvidencePath -StdoutText (Read-WorkerText $out)) {
            Write-Step "worker $Name evidence ready, session held open (pid $($process.Id))"
            return $process
        }
        Start-Sleep -Milliseconds 200
    }

    try { Stop-Process -Id $process.Id -Force } catch { }
    throw "worker $Name did not produce held-open evidence within $TimeoutSeconds seconds."
}

function Stop-HeldWorker {
    # Force-kills a held-open worker AFTER its host was already killed: the client
    # therefore dies while disconnected, never driving a graceful teardown commit.
    param(
        [Parameter(Mandatory = $true)][object]$Process,
        [Parameter(Mandatory = $true)][string]$Name
    )
    if (-not $Process.HasExited) { Stop-Process -Id $Process.Id -Force }
    $Process.WaitForExit(30000) | Out-Null
    Write-Step "held worker $Name force-killed (crash-shape client, pid $($Process.Id))"
}

try {
    New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $LogDir 'tmp') -Force | Out-Null
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
    Add-Stage 'cluster' 'PASS' 0 "server=$serverVersion data_directory=$dataDirectory port=$Port db=$script:Database emptyDb=$script:EmptyDatabase p52Db=$script:P52Database"

    $xmlSelfChecks = Assert-XmlGatingSelfTest
    Add-Stage 'xml-gating-selfcheck' 'PASS' 0 "negative XML cases=$xmlSelfChecks"

    $workerGateSelfChecks = Assert-WorkerGateSelfTest
    Add-Stage 'worker-gate-selfcheck' 'PASS' 0 "hold-gate cases=$workerGateSelfChecks"

    $removalSelfChecks = Assert-ClusterRemovalSelfTest
    Add-Stage 'cluster-removal-selfcheck' 'PASS' 0 "long-path removal cases=$removalSelfChecks"

    $manifestSelfChecks = Assert-EvidenceManifestSelfTest
    Add-Stage 'evidence-manifest-selfcheck' 'PASS' 0 "stable-evidence manifest cases=$manifestSelfChecks"

    $keyBytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($keyBytes) } finally { $rng.Dispose() }
    $payloadKey = [Convert]::ToBase64String($keyBytes)
    $adminConnection = "Host=127.0.0.1;Port=$Port;Database=$script:Database;Username=mahjong_pg;Pooling=false;Timeout=10;Command Timeout=30"
    $runtimeConnection = "Host=127.0.0.1;Port=$Port;Database=$script:Database;Username=mahjong_table_runtime;Pooling=false;Timeout=10;Command Timeout=30"
    $emptyAdminConnection = "Host=127.0.0.1;Port=$Port;Database=$script:EmptyDatabase;Username=mahjong_pg;Pooling=false;Timeout=10;Command Timeout=30"
    $env:MAHJONG_P54B_TEST_PG = $runtimeConnection
    $env:MAHJONG_P54B_TEST_PG_ADMIN = $adminConnection
    $env:MAHJONG_P54B_TEST_PG_EMPTY = $emptyAdminConnection
    $env:MAHJONG_P54B_TEST_PG_KEY = $payloadKey
    $env:MAHJONG_P54B_TEST_PG_EVIDENCE = (Join-Path $LogDir 'tests-evidence')

    # The accepted P5.2 Infrastructure suite runs against its own database on THIS
    # wrapper's disposable cluster (its guard enforces the mahjong_p52_test prefix).
    $p52AdminConnection = "Host=127.0.0.1;Port=$Port;Database=$script:P52Database;Username=mahjong_pg;Pooling=false;Timeout=10;Command Timeout=30"
    $p52RuntimeConnection = "Host=127.0.0.1;Port=$Port;Database=$script:P52Database;Username=mahjong_table_runtime;Pooling=false;Timeout=10;Command Timeout=30"
    $env:MAHJONG_P52_TEST_POSTGRES = $p52RuntimeConnection
    $env:MAHJONG_P52_TEST_POSTGRES_ADMIN = $p52AdminConnection
    $env:MAHJONG_P52_PG_EVIDENCE = (Join-Path $LogDir 'p52-evidence')
    $env:MAHJONG_TABLE_PG_PAYLOAD_KEY = $payloadKey

    $srcRoot = Join-Path $script:RepoRoot 'dotnet\src'
    $testsRoot = Join-Path $script:RepoRoot 'dotnet\tests'
    $serviceProject = Join-Path $srcRoot 'Mahjong.TableService\Mahjong.TableService.csproj'
    $serviceTestsProject = Join-Path $testsRoot 'Mahjong.TableService.Tests\Mahjong.TableService.Tests.csproj'
    $runtimeTestsProject = Join-Path $testsRoot 'Mahjong.TableRuntime.Tests\Mahjong.TableRuntime.Tests.csproj'
    $infraTestsProject = Join-Path $testsRoot 'Mahjong.Infrastructure.Tests\Mahjong.Infrastructure.Tests.csproj'
    $durableTestsProject = Join-Path $testsRoot 'Mahjong.TableService.DurableHost.Tests\Mahjong.TableService.DurableHost.Tests.csproj'
    $workerProject = Join-Path $testsRoot 'Mahjong.TableService.PersistenceWorker\Mahjong.TableService.PersistenceWorker.csproj'
    $script:HostDll = Join-Path $srcRoot "Mahjong.TableService\bin\$Configuration\net8.0\Mahjong.TableService.dll"
    $script:WorkerDll = Join-Path $testsRoot "Mahjong.TableService.PersistenceWorker\bin\$Configuration\net8.0\Mahjong.TableService.PersistenceWorker.dll"
    $serviceTestsDll = Join-Path $testsRoot "Mahjong.TableService.Tests\bin\$Configuration\net8.0\Mahjong.TableService.Tests.dll"
    $runtimeTestsDll = Join-Path $testsRoot "Mahjong.TableRuntime.Tests\bin\$Configuration\net8.0\Mahjong.TableRuntime.Tests.dll"
    $infraTestsDll = Join-Path $testsRoot "Mahjong.Infrastructure.Tests\bin\$Configuration\net8.0\Mahjong.Infrastructure.Tests.dll"
    $durableTestsDll = Join-Path $testsRoot "Mahjong.TableService.DurableHost.Tests\bin\$Configuration\net8.0\Mahjong.TableService.DurableHost.Tests.dll"

    $buildIndex = 0
    foreach ($pair in @(
        @($serviceProject, $script:HostDll),
        @($serviceTestsProject, $serviceTestsDll),
        @($runtimeTestsProject, $runtimeTestsDll),
        @($infraTestsProject, $infraTestsDll),
        @($durableTestsProject, $durableTestsDll),
        @($workerProject, $script:WorkerDll))) {
        $buildIndex++
        $project = $pair[0]
        $builtDll = $pair[1]
        $buildLog = Join-Path $LogDir ("build-" + $buildIndex + '.log')
        $buildCode = Invoke-Native -FilePath $DotnetPath -Arguments @(
            'build', $project, '-c', $Configuration, '--no-incremental', '--nologo',
            '-v:minimal', '-nr:false'
        ) -LogPath $buildLog -TimeoutSeconds 420
        if ($buildCode -ne 0) { throw "Fresh build failed with exit $buildCode (see $buildLog): $project" }
        # A failed-but-masked build must never surface as a green stage: the fresh
        # output DLL is asserted to exist immediately, before anything can run it.
        if (-not (Test-Path -LiteralPath $builtDll)) {
            throw "Fresh build reported success but the output DLL is missing (see $buildLog): $builtDll"
        }
    }

    # MSBuild node reuse let a crashed shared node masquerade as a green build once;
    # CI hygiene: stop every build server this run may have touched, so the test
    # DLLs below execute with no compiler server holding stale state.
    $shutdownLog = Join-Path $LogDir 'build-server-shutdown.log'
    $null = Invoke-Native -FilePath $DotnetPath -Arguments @('build-server', 'shutdown') -LogPath $shutdownLog -TimeoutSeconds 120
    Add-Stage 'fresh-build' 'PASS' 0 "6 projects built with $sdkVersion (--no-incremental, -nr:false); build servers shut down"

    Invoke-TestRun -Name 'tests-tableservice' -DllPath $serviceTestsDll -TimeoutSeconds 900
    Invoke-TestRun -Name 'tests-tableruntime' -DllPath $runtimeTestsDll -TimeoutSeconds 1200
    Invoke-TestRun -Name 'tests-infrastructure' -DllPath $infraTestsDll -TimeoutSeconds 900

    # The durable-host suite is the real-PostgreSQL fail-closed gate for the wiring
    # itself: build refusals, bootstrap rows, cold recovery, dual-host fencing,
    # tampered hash, event gap, revoked scope, graceful stop, plus the D-R1-01 anchor
    # matrix and the bounded-recovery-retry pair. The 20-test suite has measured at
    # ~850-950s on this box; a 900s cap would kill it mid-run.
    Invoke-TestRun -Name 'tests-durablehost' -DllPath $durableTestsDll -TimeoutSeconds 1500

    # A leftover durable host would make the suite below ambiguous: the regression
    # suites above already reset the shared database they used.
    $resetCode = Invoke-Worker -Name 'e2e-reset' -Arguments @('reset', '--admin', $adminConnection) -TimeoutSeconds 120
    Add-Stage 'e2e-reset' 'PASS' $resetCode 'table_store truncated for the E2E chain'

    $keysDir = Join-Path $LogDir 'keys'
    $null = Invoke-Worker -Name 'e2e-keygen' -Arguments @('keygen', '--dir', $keysDir) -TimeoutSeconds 60
    if (-not (Test-Path -LiteralPath (Join-Path $keysDir 'public.pem'))) {
        throw "The worker did not write the ticket validation key file."
    }
    Add-Stage 'e2e-keygen' 'PASS' 0 "keys=$keysDir (public.pem served to hosts; private half never leaves the run directory)"

    # D-R1-02: the persistent chain runs CONTROLLED WSS. The test worker generates an
    # ISOLATED root/leaf pair (plus wrong-name / expired / foreign-root negatives) on
    # disk; every host serves the good leaf, every probe and health GET validates it
    # through a strict per-connection CustomRootTrust callback. No machine store, no
    # global callback, no production CA dependency.
    $script:TlsDir = Join-Path $LogDir 'tls'
    $script:TlsRootPem = Join-Path $script:TlsDir 'root.pem'
    $script:TlsLeafPfx = Join-Path $script:TlsDir 'leaf.pfx'
    $null = Invoke-Worker -Name 'e2e-certgen' -Arguments @('certgen', '--out', $script:TlsDir) -TimeoutSeconds 120
    foreach ($tlsArtifact in @('root.pem', 'leaf.pfx', 'leaf-wrongname.pfx', 'leaf-expired.pfx', 'root-other.pem')) {
        if (-not (Test-Path -LiteralPath (Join-Path $script:TlsDir $tlsArtifact))) {
            throw "certgen did not produce $tlsArtifact — the controlled-WSS chain cannot start."
        }
    }
    Add-Stage 'e2e-certgen' 'PASS' 0 "tls=$($script:TlsDir) (isolated test root + leaf/wrong-name/expired/foreign-root negatives; controlled WSS only — production TLS NOT_RUN)"

    $env:TABLESERVICE_STORE = 'postgres'
    $env:TABLESERVICE_PG_CONNECTION = $runtimeConnection
    $env:TABLESERVICE_PG_PAYLOAD_KEY = $payloadKey
    $env:TABLESERVICE_PG_APPLY_MIGRATIONS = 'false'
    $env:TABLESERVICE_TICKET_KEYS_FILE = Join-Path $keysDir 'public.pem'
    $env:TABLESERVICE_ROOM_TICK_INTERVAL_MS = '50'
    $env:TABLESERVICE_HEARTBEAT_INTERVAL_MS = '500'
    $env:TABLESERVICE_IDLE_DEAD_MS = '60000'
    $env:TABLESERVICE_HANDSHAKE_TIMEOUT_MS = '2000'
    $env:TABLESERVICE_LEASE_TTL_SECONDS = '5'

    $mainRoom = 'p54b-e2e-main'
    $hashRoom = 'p54b-e2e-hash'
    $gapRoom = 'p54b-e2e-gap'
    $phase1State = Join-Path $LogDir 'phase1-state.json'
    # Every E2E probe rides CONTROLLED WSS: wss:// + the isolated test root, validated
    # per-connection by the worker's strict callback. No ws:// remains on this chain.
    $tableUrl = [scriptblock] { param($port, $room) "wss://127.0.0.1:$port/v1/table?room=$room" }
    $workerArgs = @('phase1',
        '--url', (& $tableUrl $HostPort1 $mainRoom),
        '--keys', $keysDir, '--room', $mainRoom, '--account', 'acct-e2e-0', '--seat', '0',
        '--out', $phase1State,
        '--ca-root', $script:TlsRootPem)

    # ---- E2E phase 1: bootstrap + APPLIED + same-key replay, on a REAL host process.
    # The worker HOLDS its ticketed session open after flushing the evidence: the host
    # is then killed -9 with a live client, so no graceful teardown can commit a
    # disconnect control (a persisted monotonic grace deadline that the accepted
    # cross-boot recovery contract refuses to trust). The kill -9 with a connected
    # client IS the crash shape a successor process must recover from.
    $hostA = Start-TableHost -HostPort $HostPort1 -Name 'host-a'
    $phase1Worker = Start-WorkerHold -Name 'e2e-phase1' -Arguments ($workerArgs + @('--hold-open')) -EvidencePath $phase1State -TimeoutSeconds 180
    $phase1 = Get-Content -LiteralPath $phase1State -Raw | ConvertFrom-Json
    $eventsInDb = [int](Get-PgScalar -Database $script:Database -Sql "SELECT count(*) FROM table_store.table_event WHERE room_id = '$mainRoom'")
    $appliedReceipts = [int](Get-PgScalar -Database $script:Database -Sql "SELECT count(*) FROM table_store.request_receipt WHERE room_id = '$mainRoom' AND status = 'APPLIED'")
    $scopeRows = [int](Get-PgScalar -Database $script:Database -Sql "SELECT count(*) FROM table_store.request_scope WHERE room_id = '$mainRoom'")
    $leaseOwner = Get-PgScalar -Database $script:Database -Sql "SELECT owner_instance FROM table_store.table_lease WHERE room_id = '$mainRoom'"
    if ($eventsInDb -le 0) { throw 'phase-1 left no durable event rows.' }
    if ($appliedReceipts -ne 1) { throw "phase-1 left $appliedReceipts APPLIED receipt rows (expected exactly 1)." }
    if ($scopeRows -lt 2) { throw "phase-1 left $scopeRows scope rows (service + player scopes both expected)." }
    if ($leaseOwner -notlike 'tablesvc-*') { throw "phase-1 lease owner '$leaseOwner' is not a tablesvc host instance." }
    Add-Stage 'e2e-phase1' 'PASS' 0 "room=$mainRoom appliedVersion=$($phase1.commandVersion) events=$eventsInDb receipts=$appliedReceipts scopes=$scopeRows leaseOwner=$leaseOwner session=held-open"

    # ---- kill -9 the host FIRST (client still connected: the crash shape), and only
    # then the held worker, whose socket died with the host — no teardown commit.
    Stop-TableHost -Process $hostA -Name 'host-a'
    Stop-HeldWorker -Process $phase1Worker -Name 'e2e-phase1'
    # Cold-recovery determinism: host B may only take over once A's lease (TTL from
    # TABLESERVICE_LEASE_TTL_SECONDS) has expired, so wait out TTL + margin.
    Start-Sleep -Seconds ([int]$env:TABLESERVICE_LEASE_TTL_SECONDS + 3)
    Add-Stage 'e2e-kill-host' 'PASS' 'KILLED' "host-a pid killed with live client after durable commit version $($phase1.commandVersion)"

    # ---- E2E phase 2: a NEW host process cold-recovers the SAME room from the SAME DB.
    $phase2State = Join-Path $LogDir 'phase2-state.json'
    $hostB = Start-TableHost -HostPort $HostPort2 -Name 'host-b'
    $null = Invoke-Worker -Name 'e2e-phase2-cold-recovery' -Arguments @('phase2',
        '--url', (& $tableUrl $HostPort2 $mainRoom),
        '--keys', $keysDir, '--room', $mainRoom, '--account', 'acct-e2e-0', '--seat', '0',
        '--state', $phase1State, '--out', $phase2State,
        '--ca-root', $script:TlsRootPem) -TimeoutSeconds 180
    $phase2 = Get-Content -LiteralPath $phase2State -Raw | ConvertFrom-Json
    if (-not $phase2.receiptFound -or -not $phase2.sameKeyRetryFailedClosed) {
        throw 'Cold recovery did not answer the historical receipt / fail-closed same-key retry.'
    }
    if ($phase2.ledgerRows -ne 1) {
        throw "The historical APPLIED ledger row was rewritten or duplicated (rows $($phase2.ledgerRows))."
    }
    # D-R1-01: the successor's projection is re-read from the LIVE new process — the
    # pre-crash public discard history must still start with the recorded tiles.
    if (-not $phase2.discardsSurvivedRecovery) {
        throw 'Cold recovery lost the pre-crash public discard fact (re-dealt wall suspicion).'
    }
    Add-Stage 'e2e-phase2-cold-recovery' 'PASS' 0 "recoveredVersion=$($phase2.recoveredBaselineStateVersion) receiptVersion=$($phase2.receiptCommandVersion) ledgerRows=$($phase2.ledgerRows) discardTiles=$($phase2.publicDiscardTiles) (fail-closed retry, ledger + public fact unchanged)"

    # ---- dual host over the same DB: the foreign live lease fails closed.
    $probePath = Join-Path $LogDir 'dualhost-probe.json'
    $hostC = Start-TableHost -HostPort $HostPort3 -Name 'host-c'
    $null = Invoke-Worker -Name 'e2e-dualhost-probe' -Arguments @('admitprobe',
        '--url', (& $tableUrl $HostPort3 $mainRoom),
        '--keys', $keysDir, '--room', $mainRoom, '--account', 'acct-e2e-0', '--seat', '0',
        '--out', $probePath,
        '--ca-root', $script:TlsRootPem) -TimeoutSeconds 120
    $probe = Get-Content -LiteralPath $probePath -Raw | ConvertFrom-Json
    if ($probe.welcomed) { throw 'The second host WELCOMED an admission for a room it does not own.' }
    $readyC = Invoke-HostHealth -HostPort $HostPort3 -Name 'host-c-ready'
    if ($readyC -ne 0) { throw "The foreign-lease host readiness did not report 503 through the strict TLS gate (exit $readyC)." }
    Stop-TableHost -Process $hostC -Name 'host-c'
    Add-Stage 'e2e-dualhost-fencing' 'PASS' 0 "second host welcomed=$($probe.welcomed) readiness=503 (strict TLS); first host untouched"

    # ---- revoked scope: the recorded receipt stays queryable with zero new side effects.
    $phase2Revoked = Join-Path $LogDir 'phase2-revoked-scope.json'
    $null = Invoke-Worker -Name 'e2e-tamper-scope-revoke' -Arguments @('tamper-scope-revoke', '--state', $phase1State) -TimeoutSeconds 60
    $null = Invoke-Worker -Name 'e2e-phase2-revoked-scope' -Arguments @('phase2',
        '--url', (& $tableUrl $HostPort2 $mainRoom),
        '--keys', $keysDir, '--room', $mainRoom, '--account', 'acct-e2e-0', '--seat', '0',
        '--state', $phase1State, '--out', $phase2Revoked, '--expect-revoked-scope',
        '--ca-root', $script:TlsRootPem) -TimeoutSeconds 180
    # The worker's exit gate alone is not the evidence: assert the recorded run — the
    # revoked historical scope's receipt is still FOUND, the re-admission produced a
    # FRESH scope id, the same-key retry failed closed and the ledger kept exactly its
    # original APPLIED row.
    $revoked = Get-Content -LiteralPath $phase2Revoked -Raw | ConvertFrom-Json
    if (-not $revoked.receiptFound -or -not $revoked.sameKeyRetryFailedClosed) {
        throw 'The revoked-scope re-admission did not answer the historical receipt / fail-closed same-key retry.'
    }
    if (-not $revoked.historicalScopeStillAuthorized) {
        throw 'The revoked scope was dropped from the receipt surface instead of staying queryable.'
    }
    if ($revoked.ledgerRows -ne 1) {
        throw "The historical APPLIED ledger row was rewritten or duplicated after revocation (rows $($revoked.ledgerRows))."
    }
    if (-not $revoked.expectRevokedScope) {
        throw 'The revoked-scope run was not recorded with the expect-revoked-scope mark.'
    }
    Add-Stage 'e2e-revoked-scope-receipt' 'PASS' 0 "revoked scope receipt queryable (freshScope=$($revoked.freshScopeId.Substring(0, 8))…); same-key retry failed closed; ledger unchanged"

    # ---- cross-account: a foreign account's receipt query is refused; unknown is inert.
    $crossPath = Join-Path $LogDir 'crossaccount.json'
    $null = Invoke-Worker -Name 'e2e-crossaccount' -Arguments @('crossaccount',
        '--url', (& $tableUrl $HostPort2 $mainRoom),
        '--keys', $keysDir, '--room', $mainRoom, '--state', $phase1State,
        '--account-b', 'acct-e2e-1', '--out', $crossPath,
        '--ca-root', $script:TlsRootPem) -TimeoutSeconds 120
    $cross = Get-Content -LiteralPath $crossPath -Raw | ConvertFrom-Json
    if (-not $cross.foreignQueryRefused -or $cross.ledgerRows -ne 1) {
        throw 'Cross-account refusal or unknown-request inertness failed.'
    }
    Add-Stage 'e2e-crossaccount' 'PASS' 0 "foreign refused ($($cross.foreignDetail)); unknown inert; ledger unchanged (rows=$($cross.ledgerRows))"

    # ---- controlled-WSS refusal negatives (D-R1-02). Each negative is differential:
    # the SAME worker, SAME host chain and SAME probe validated positively with the
    # good leaf moments earlier (phase2 / dualhost / crossaccount above), so a nonzero
    # exit here is attributable to certificate validation alone. The untrusted-root
    # case rides the LIVE main host; the wrong-name and expired cases spawn throwaway
    # hosts whose ONLY difference from the good chain is the served leaf (liveness is
    # deliberately the plain TCP gate there — validating the bad leaf would be
    # self-contradictory — and each throwaway is killed right after its probe).
    $negRoom = 'p54b-e2e-wss-neg'
    $untrustedExit = Invoke-Native -FilePath $DotnetPath -Arguments @(
        $script:WorkerDll, 'admitprobe',
        '--url', (& $tableUrl $HostPort2 $negRoom),
        '--ca-root', (Join-Path $script:TlsDir 'root-other.pem'),
        '--keys', $keysDir, '--room', $negRoom, '--account', 'acct-e2e-0', '--seat', '0',
        '--out', (Join-Path $LogDir 'wss-untrusted-root.json')) -LogPath (Join-Path $LogDir 'e2e-wss-untrusted-root.log') -TimeoutSeconds 120
    if ($untrustedExit -eq 0) { throw 'The host chain admitted a probe trusting a FOREIGN root certificate.' }
    Add-Stage 'e2e-wss-untrusted-root' 'PASS' $untrustedExit "exit=$untrustedExit (nonzero) — foreign root refused on the live host chain"

    $wrongHost = Start-TableHost -HostPort $HostPort3 -Name 'host-wrongname' -TlsPfx (Join-Path $script:TlsDir 'leaf-wrongname.pfx') -SkipHealthGate
    try {
        $wrongNameExit = Invoke-Native -FilePath $DotnetPath -Arguments @(
            $script:WorkerDll, 'admitprobe',
            '--url', (& $tableUrl $HostPort3 $negRoom),
            '--ca-root', $script:TlsRootPem,
            '--keys', $keysDir, '--room', $negRoom, '--account', 'acct-e2e-0', '--seat', '0',
            '--out', (Join-Path $LogDir 'wss-wrong-name.json')) -LogPath (Join-Path $LogDir 'e2e-wss-wrong-name.log') -TimeoutSeconds 120
        if ($wrongNameExit -eq 0) { throw 'The host chain admitted a probe whose server certificate names ANOTHER endpoint.' }
        if ($wrongHost.HasExited) {
            throw 'The wrong-name throwaway host exited during its negative probe; the refusal is not attributable to TLS validation.'
        }
    }
    finally { Stop-TableHost -Process $wrongHost -Name 'host-wrongname' }
    Add-Stage 'e2e-wss-wrong-name' 'PASS' $wrongNameExit "exit=$wrongNameExit (nonzero) — wrong-name leaf refused with the CORRECT root (listener alive throughout)"

    $expiredHost = Start-TableHost -HostPort $HostPort3 -Name 'host-expired' -TlsPfx (Join-Path $script:TlsDir 'leaf-expired.pfx') -SkipHealthGate
    try {
        $expiredExit = Invoke-Native -FilePath $DotnetPath -Arguments @(
            $script:WorkerDll, 'admitprobe',
            '--url', (& $tableUrl $HostPort3 $negRoom),
            '--ca-root', $script:TlsRootPem,
            '--keys', $keysDir, '--room', $negRoom, '--account', 'acct-e2e-0', '--seat', '0',
            '--out', (Join-Path $LogDir 'wss-expired.json')) -LogPath (Join-Path $LogDir 'e2e-wss-expired.log') -TimeoutSeconds 120
        if ($expiredExit -eq 0) { throw 'The host chain admitted a probe whose server certificate is EXPIRED.' }
        if ($expiredHost.HasExited) {
            throw 'The expired-leaf throwaway host exited during its negative probe; the refusal is not attributable to TLS validation.'
        }
    }
    finally { Stop-TableHost -Process $expiredHost -Name 'host-expired' }
    Add-Stage 'e2e-wss-expired-leaf' 'PASS' $expiredExit "exit=$expiredExit (nonzero) — expired leaf refused with the CORRECT root (listener alive throughout)"

    # ---- a second room for the corruption scenarios, on the SAME live host process.
    # This phase-1 also HOLDS its session open: host-b is later killed -9 with the
    # client still connected, so the hash room's durable checkpoint stays in the crash
    # shape and the successor's refusal below is attributable to the TAMPERING alone
    # (never to a teardown-committed grace deadline).
    $hashState = Join-Path $LogDir 'hash-room-phase1.json'
    $hashWorker = Start-WorkerHold -Name 'e2e-phase1-hash-room' -Arguments (@('phase1',
        '--url', (& $tableUrl $HostPort2 $hashRoom),
        '--keys', $keysDir, '--room', $hashRoom, '--account', 'acct-e2e-hash', '--seat', '0',
        '--out', $hashState, '--hold-open',
        '--ca-root', $script:TlsRootPem)) -EvidencePath $hashState -TimeoutSeconds 180
    Add-Stage 'e2e-second-room' 'PASS' 0 "room=$hashRoom bootstrapped on host-b over controlled WSS (session held for crash shape)"

    # ---- DB unavailable: the host refuses to start, exits nonzero, no listener.
    # Launched through Invoke-Native so the exit code is REAL: the fail-closed gate
    # below compares the code to zero, and the PS 5.1 Start-Process redirect quirk
    # (empty ExitCode coerced to 0) would turn that gate into a vacuous PASS.
    $env:TABLESERVICE_PG_CONNECTION = "Host=127.0.0.1;Port=$Port;Database=mahjong_p54b_absent;Username=mahjong_table_runtime;Pooling=false;Timeout=1"
    $dbDownLog = Join-Path $script:LogDir 'dbdown'
    $dbDownErr = $dbDownLog + '.stderr'
    $dbDownExit = 0
    try {
        # The DLL path goes in BARE: Invoke-Native's argument helper applies Windows
        # quoting itself. Pre-wrapping it in literal quotes (a Start-Process
        # -ArgumentList habit) gets double-escaped into \"...\" and dotnet then
        # reports the quoted string as a file it cannot find (R13).
        $dbDownExit = Invoke-Native -FilePath $DotnetPath -Arguments @($script:HostDll) -LogPath $dbDownLog -TimeoutSeconds 60
    } catch {
        throw ('The DB-unavailable host did NOT refuse to start within 60 seconds. ' + $_.Exception.Message)
    }

    $stderrText = Read-WorkerText $dbDownErr
    if ($dbDownExit -eq 0) { throw 'The DB-unavailable host exited 0; fail-closed refused to hold.' }
    if ($stderrText.IndexOf('host build refused', [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "The DB-unavailable host stderr carried no 'host build refused' line: $stderrText"
    }
    $env:TABLESERVICE_PG_CONNECTION = $runtimeConnection
    Add-Stage 'e2e-db-unavailable' 'PASS' $dbDownExit "exit=$dbDownExit (nonzero), listener never bound, stderr redacted refusal line present"

    # ---- tampered event hash: successor process refuses the room fail-closed.
    # Kill host-b FIRST (the held hash-room client dies with it: crash shape, no
    # teardown commit), then the blocked worker.
    Stop-TableHost -Process $hostB -Name 'host-b'
    Stop-HeldWorker -Process $hashWorker -Name 'e2e-phase1-hash-room'
    Start-Sleep -Seconds ([int]$env:TABLESERVICE_LEASE_TTL_SECONDS + 3)
    $null = Invoke-Worker -Name 'e2e-tamper-event-hash' -Arguments @('tamper-event-hash', '--room', $hashRoom) -TimeoutSeconds 60
    $hostD = Start-TableHost -HostPort $HostPort2 -Name 'host-d'
    $hashProbePath = Join-Path $LogDir 'tamper-hash-probe.json'
    $null = Invoke-Worker -Name 'e2e-tamper-hash-probe' -Arguments @('admitprobe',
        '--url', (& $tableUrl $HostPort2 $hashRoom),
        '--keys', $keysDir, '--room', $hashRoom, '--account', 'acct-e2e-hash', '--seat', '0',
        '--out', $hashProbePath,
        '--ca-root', $script:TlsRootPem) -TimeoutSeconds 120
    $hashProbe = Get-Content -LiteralPath $hashProbePath -Raw | ConvertFrom-Json
    if ($hashProbe.welcomed) { throw 'A hash-corrupted journal was SERVED by the successor process.' }
    $readyD = Invoke-HostHealth -HostPort $HostPort2 -Name 'host-d-ready'
    if ($readyD -ne 0) { throw "The hash-corrupt host readiness did not report 503 through the strict TLS gate (exit $readyD)." }
    Add-Stage 'e2e-tamper-event-hash' 'PASS' 0 "room=$hashRoom welcomed=$($hashProbe.welcomed) readiness=503 (strict TLS)"

    # ---- event gap: the same fail-closed discipline, on a room whose ONLY durable
    # defect is the deleted event row. A THIRD room is crash-shaped on host-d (session
    # held open, host killed -9 mid-session), so the successor's refusal is
    # attributable to the gap alone — the main room's earlier phases ended with clean
    # client exits and legitimately carry teardown grace, which would muddy the
    # refusal reason.
    $gapState = Join-Path $LogDir 'gap-room-phase1.json'
    $gapWorker = Start-WorkerHold -Name 'e2e-phase1-gap-room' -Arguments (@('phase1',
        '--url', (& $tableUrl $HostPort2 $gapRoom),
        '--keys', $keysDir, '--room', $gapRoom, '--account', 'acct-e2e-gap', '--seat', '0',
        '--out', $gapState, '--hold-open',
        '--ca-root', $script:TlsRootPem)) -EvidencePath $gapState -TimeoutSeconds 180
    Add-Stage 'e2e-gap-room' 'PASS' 0 "room=$gapRoom bootstrapped on host-d over controlled WSS (session held for crash shape)"
    Stop-TableHost -Process $hostD -Name 'host-d'
    Stop-HeldWorker -Process $gapWorker -Name 'e2e-phase1-gap-room'
    Start-Sleep -Seconds ([int]$env:TABLESERVICE_LEASE_TTL_SECONDS + 3)
    $null = Invoke-Worker -Name 'e2e-tamper-event-gap' -Arguments @('tamper-event-gap', '--room', $gapRoom) -TimeoutSeconds 60
    $hostE = Start-TableHost -HostPort $HostPort1 -Name 'host-e'
    $gapProbePath = Join-Path $LogDir 'tamper-gap-probe.json'
    $null = Invoke-Worker -Name 'e2e-tamper-gap-probe' -Arguments @('admitprobe',
        '--url', (& $tableUrl $HostPort1 $gapRoom),
        '--keys', $keysDir, '--room', $gapRoom, '--account', 'acct-e2e-gap', '--seat', '0',
        '--out', $gapProbePath,
        '--ca-root', $script:TlsRootPem) -TimeoutSeconds 120
    $gapProbe = Get-Content -LiteralPath $gapProbePath -Raw | ConvertFrom-Json
    if ($gapProbe.welcomed) { throw 'A journal with a missing committed event was SERVED.' }
    $readyE = Invoke-HostHealth -HostPort $HostPort1 -Name 'host-e-ready'
    if ($readyE -ne 0) { throw "The gap host readiness did not report 503 through the strict TLS gate (exit $readyE)." }
    Stop-TableHost -Process $hostE -Name 'host-e'
    Add-Stage 'e2e-tamper-event-gap' 'PASS' 0 "room=$gapRoom welcomed=$($gapProbe.welcomed) readiness=503 (strict TLS)"

    Add-Stage 'result' 'PASS' 0 'all P5.4b-A durable-host stages passed'
}
catch {
    $message = $_.Exception.Message
    Write-Step ("FAIL: " + $message)
    Add-Stage 'result' 'FAIL' 1 $message
    $script:Exit = 1
}
finally {
    try {
        foreach ($hostProcess in $script:HostProcesses) {
            if (-not $hostProcess.HasExited) {
                Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue
            }
        }

        foreach ($workerProcess in $script:WorkerProcesses) {
            if (-not $workerProcess.HasExited) {
                Stop-Process -Id $workerProcess.Id -Force -ErrorAction SilentlyContinue
            }
        }
    }
    catch { Write-Step ("host cleanup warning: " + $_.Exception.Message) }

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

    foreach ($name in $script:EnvNames) {
        [System.Environment]::SetEnvironmentVariable($name, $script:EnvSnapshot[$name], 'Process')
    }

    try {
        $summary = [ordered]@{
            generatedUtc = (Get-Date).ToUniversalTime().ToString('o')
            sdk = $(if ($sdkVersion) { $sdkVersion } else { '' })
            port = $Port
            database = $script:Database
            emptyDatabase = $script:EmptyDatabase
            p52Database = $script:P52Database
            hostPorts = @($HostPort1, $HostPort2, $HostPort3)
            payloadKeyRecorded = $false
            # D-R1-02 scope honesty: the chain above IS WSS end to end, but on the
            # CONTROLLED test root the worker certgen produced — never a production
            # certificate deployment, which stays explicitly NOT_RUN.
            wssMode = 'controlled WSS (worker certgen test root, strict per-connection CustomRootTrust; production TLS/WSS termination NOT_RUN)'
            notRunNextBatch = @('production TLS/WSS termination', 'Lobby HTTPS ticket issuance', 'multi-client full match', 'account asset settlement', 'cross-physical-network')
            stages = $script:Stages
            lines = $script:Lines
            exit = $script:Exit
        }
        $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $LogDir 'wrapper-summary.json') -Encoding utf8
        $script:Lines | Set-Content -LiteralPath (Join-Path $LogDir 'wrapper.log') -Encoding utf8
    }
    catch { Write-Step ("wrapper summary warning: " + $_.Exception.Message) }

    # D-R2-03: the evidence manifest is a hard gate, not a best-effort warning. Stable
    # logs/XML/results always get a manifest; a stable file lost between enumeration and
    # hashing is recorded in manifest.errors.json and turns the run FAIL. Temporary build
    # state under "$LogDir\tmp" is isolated and can never wipe out the whole manifest.
    try {
        $manifestErrors = Write-EvidenceManifest -Directory $LogDir
        if ($manifestErrors -gt 0) {
            Write-Step ("evidence manifest: $manifestErrors stable evidence file(s) could not be hashed (see manifest.errors.json)")
            Add-Stage 'evidence-manifest' 'FAIL' 1 "$manifestErrors stable evidence file(s) missing or unreadable"
            $script:Exit = 1
        }
    }
    catch {
        Write-Step ("evidence manifest FAILURE: " + $_.Exception.Message)
        Add-Stage 'evidence-manifest' 'FAIL' 1 $_.Exception.Message
        $script:Exit = 1
    }
}

if ($script:Exit -eq 0) { Write-Host 'P54B-DURABLE-TABLE-HOST-PASS' } else { Write-Host 'P54B-DURABLE-TABLE-HOST-FAIL' }
exit $script:Exit
