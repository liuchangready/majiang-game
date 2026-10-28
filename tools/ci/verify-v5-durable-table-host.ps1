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
      6. E2E: phase-1 (bootstrap + APPLIED + same-key replay, session held open)
         -> kill -9 the host while the client is still connected (the true crash
         shape: no graceful teardown can poison the checkpoint) -> phase-2 cold
         recovery in a NEW process (receipt query; same-key retry fails closed;
         APPLIED ledger row unchanged)
      7. dual host over one DB: foreign live lease refuses, first host still serves
      8. DB-unavailable host refuses to start (fail closed, no listener)
      9. revoked scope: recorded receipt still queryable, zero new side effects
     10. cross-account receipt query refused (SCOPE_MISMATCH), unknown request inert
     11. tampered event hash / event gap: each corrupted room is killed mid-session
         (crash shape), and a successor process refuses the room and readiness
         reports known-and-not-serving (503)
     12. ownership-verified stop/delete, logs, XML, SHA-256 manifest and summary

    NOT covered by design (NOT_RUN for the next P5.4b batch): production TLS/WSS
    termination, Lobby HTTPS ticket issuance, multi-client full-match play,
    account asset settlement and cross-physical-network paths.

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

function Get-HttpStatus([string]$Url) {
    try {
        $request = [System.Net.HttpWebRequest]::Create($Url)
        $request.Method = 'GET'
        $request.Timeout = 3000
        $request.ReadWriteTimeout = 3000
        $response = $request.GetResponse()
        $status = [int]$response.StatusCode
        $response.Close()
        return $status
    }
    catch [System.Net.WebException] {
        if ($_.Exception.Response) {
            return [int]$_.Exception.Response.StatusCode
        }
        return 0
    }
    catch { return 0 }
}

function Start-TableHost {
    param(
        [Parameter(Mandatory = $true)][int]$HostPort,
        [Parameter(Mandatory = $true)][string]$Name
    )
    $out = Join-Path $script:LogDir ($Name + '.stdout.log')
    $err = Join-Path $script:LogDir ($Name + '.stderr.log')
    Remove-Item -LiteralPath $out, $err -Force -ErrorAction SilentlyContinue
    # A production host binds a FIXED, declared port (TABLESERVICE_HTTP_PORT) so its
    # admission/health endpoints are reachable at a known address — never an ephemeral
    # one the wrapper would have to scrape out of stdout.
    $env:TABLESERVICE_HTTP_PORT = "$HostPort"
    $process = Start-Process -FilePath $DotnetPath -ArgumentList ('"' + $script:HostDll + '"') -WindowStyle Hidden -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
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
        $live = Get-HttpStatus ("http://127.0.0.1:$HostPort/health/live")
        if ($live -eq 200) {
            Write-Step "host $Name live on 127.0.0.1:$HostPort (pid $($process.Id))"
            return $process
        }
        Start-Sleep -Milliseconds 200
    }

    try { Stop-Process -Id $process.Id -Force } catch { }
    throw "Table host $Name did not become live within 60 seconds."
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
            $stderrText = ''
            $stdoutText = ''
            if (Test-Path -LiteralPath $err) { $stderrText = Get-Content -LiteralPath $err -Raw }
            if (Test-Path -LiteralPath $out) { $stdoutText = Get-Content -LiteralPath $out -Raw }
            # No exit code cited here: PS 5.1 Start-Process loses ExitCode on redirected
            # processes (measured); the worker's own stderr line carries the real reason.
            throw "worker $Name exited before holding its session open (see stderr/stdout). stderr: $stderrText stdout: $stdoutText"
        }
        if ((Test-Path -LiteralPath $EvidencePath) -and (Test-Path -LiteralPath $out) -and
            ((Get-Content -LiteralPath $out -Raw).IndexOf('holding-session-open', [StringComparison]::Ordinal) -ge 0)) {
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
    # tampered hash, event gap, revoked scope, graceful stop.
    Invoke-TestRun -Name 'tests-durablehost' -DllPath $durableTestsDll -TimeoutSeconds 900

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
    $phase1State = Join-Path $LogDir 'phase1-state.json'
    $workerArgs = @('phase1',
        '--url', "ws://127.0.0.1:$HostPort1/v1/table?room=$mainRoom",
        '--keys', $keysDir, '--room', $mainRoom, '--account', 'acct-e2e-0', '--seat', '0',
        '--out', $phase1State)

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
        '--url', "ws://127.0.0.1:$HostPort2/v1/table?room=$mainRoom",
        '--keys', $keysDir, '--room', $mainRoom, '--account', 'acct-e2e-0', '--seat', '0',
        '--state', $phase1State, '--out', $phase2State) -TimeoutSeconds 180
    $phase2 = Get-Content -LiteralPath $phase2State -Raw | ConvertFrom-Json
    if (-not $phase2.receiptFound -or -not $phase2.sameKeyRetryFailedClosed) {
        throw 'Cold recovery did not answer the historical receipt / fail-closed same-key retry.'
    }
    if ($phase2.ledgerRows -ne 1) {
        throw "The historical APPLIED ledger row was rewritten or duplicated (rows $($phase2.ledgerRows))."
    }
    Add-Stage 'e2e-phase2-cold-recovery' 'PASS' 0 "recoveredVersion=$($phase2.recoveredBaselineStateVersion) receiptVersion=$($phase2.receiptCommandVersion) ledgerRows=$($phase2.ledgerRows) (fail-closed retry, ledger unchanged)"

    # ---- dual host over the same DB: the foreign live lease fails closed.
    $probePath = Join-Path $LogDir 'dualhost-probe.json'
    $hostC = Start-TableHost -HostPort $HostPort3 -Name 'host-c'
    $null = Invoke-Worker -Name 'e2e-dualhost-probe' -Arguments @('admitprobe',
        '--url', "ws://127.0.0.1:$HostPort3/v1/table?room=$mainRoom",
        '--keys', $keysDir, '--room', $mainRoom, '--account', 'acct-e2e-0', '--seat', '0',
        '--out', $probePath) -TimeoutSeconds 120
    $probe = Get-Content -LiteralPath $probePath -Raw | ConvertFrom-Json
    if ($probe.welcomed) { throw 'The second host WELCOMED an admission for a room it does not own.' }
    $readyC = Get-HttpStatus ("http://127.0.0.1:$HostPort3/health/ready")
    if ($readyC -ne 503) { throw "The foreign-lease host readiness reported $readyC (expected 503 known-not-serving)." }
    Stop-TableHost -Process $hostC -Name 'host-c'
    Add-Stage 'e2e-dualhost-fencing' 'PASS' 0 "second host welcomed=$($probe.welcomed) readiness=503; first host untouched"

    # ---- revoked scope: the recorded receipt stays queryable with zero new side effects.
    $phase2Revoked = Join-Path $LogDir 'phase2-revoked-scope.json'
    $null = Invoke-Worker -Name 'e2e-tamper-scope-revoke' -Arguments @('tamper-scope-revoke', '--state', $phase1State) -TimeoutSeconds 60
    $null = Invoke-Worker -Name 'e2e-phase2-revoked-scope' -Arguments @('phase2',
        '--url', "ws://127.0.0.1:$HostPort2/v1/table?room=$mainRoom",
        '--keys', $keysDir, '--room', $mainRoom, '--account', 'acct-e2e-0', '--seat', '0',
        '--state', $phase1State, '--out', $phase2Revoked, '--expect-revoked-scope') -TimeoutSeconds 180
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
        '--url', "ws://127.0.0.1:$HostPort2/v1/table?room=$mainRoom",
        '--keys', $keysDir, '--room', $mainRoom, '--state', $phase1State,
        '--account-b', 'acct-e2e-1', '--out', $crossPath) -TimeoutSeconds 120
    $cross = Get-Content -LiteralPath $crossPath -Raw | ConvertFrom-Json
    if (-not $cross.foreignQueryRefused -or $cross.ledgerRows -ne 1) {
        throw 'Cross-account refusal or unknown-request inertness failed.'
    }
    Add-Stage 'e2e-crossaccount' 'PASS' 0 "foreign refused ($($cross.foreignDetail)); unknown inert; ledger unchanged (rows=$($cross.ledgerRows))"

    # ---- a second room for the corruption scenarios, on the SAME live host process.
    # This phase-1 also HOLDS its session open: host-b is later killed -9 with the
    # client still connected, so the hash room's durable checkpoint stays in the crash
    # shape and the successor's refusal below is attributable to the TAMPERING alone
    # (never to a teardown-committed grace deadline).
    $hashState = Join-Path $LogDir 'hash-room-phase1.json'
    $hashWorker = Start-WorkerHold -Name 'e2e-phase1-hash-room' -Arguments @('phase1',
        '--url', "ws://127.0.0.1:$HostPort2/v1/table?room=$hashRoom",
        '--keys', $keysDir, '--room', $hashRoom, '--account', 'acct-e2e-hash', '--seat', '0',
        '--out', $hashState, '--hold-open') -EvidencePath $hashState -TimeoutSeconds 180
    Add-Stage 'e2e-second-room' 'PASS' 0 "room=$hashRoom bootstrapped on host-b (session held for crash shape)"

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

    $stderrText = ''
    if (Test-Path -LiteralPath $dbDownErr) { $stderrText = (Get-Content -LiteralPath $dbDownErr -Raw) }
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
        '--url', "ws://127.0.0.1:$HostPort2/v1/table?room=$hashRoom",
        '--keys', $keysDir, '--room', $hashRoom, '--account', 'acct-e2e-hash', '--seat', '0',
        '--out', $hashProbePath) -TimeoutSeconds 120
    $hashProbe = Get-Content -LiteralPath $hashProbePath -Raw | ConvertFrom-Json
    if ($hashProbe.welcomed) { throw 'A hash-corrupted journal was SERVED by the successor process.' }
    $readyD = Get-HttpStatus ("http://127.0.0.1:$HostPort2/health/ready")
    if ($readyD -ne 503) { throw "The hash-corrupt host readiness reported $readyD (expected 503)." }
    Add-Stage 'e2e-tamper-event-hash' 'PASS' 0 "room=$hashRoom welcomed=$($hashProbe.welcomed) readiness=503"

    # ---- event gap: the same fail-closed discipline, on a room whose ONLY durable
    # defect is the deleted event row. A THIRD room is crash-shaped on host-d (session
    # held open, host killed -9 mid-session), so the successor's refusal is
    # attributable to the gap alone — the main room's earlier phases ended with clean
    # client exits and legitimately carry teardown grace, which would muddy the
    # refusal reason.
    $gapRoom = 'p54b-e2e-gap'
    $gapState = Join-Path $LogDir 'gap-room-phase1.json'
    $gapWorker = Start-WorkerHold -Name 'e2e-phase1-gap-room' -Arguments @('phase1',
        '--url', "ws://127.0.0.1:$HostPort2/v1/table?room=$gapRoom",
        '--keys', $keysDir, '--room', $gapRoom, '--account', 'acct-e2e-gap', '--seat', '0',
        '--out', $gapState, '--hold-open') -EvidencePath $gapState -TimeoutSeconds 180
    Add-Stage 'e2e-gap-room' 'PASS' 0 "room=$gapRoom bootstrapped on host-d (session held for crash shape)"
    Stop-TableHost -Process $hostD -Name 'host-d'
    Stop-HeldWorker -Process $gapWorker -Name 'e2e-phase1-gap-room'
    Start-Sleep -Seconds ([int]$env:TABLESERVICE_LEASE_TTL_SECONDS + 3)
    $null = Invoke-Worker -Name 'e2e-tamper-event-gap' -Arguments @('tamper-event-gap', '--room', $gapRoom) -TimeoutSeconds 60
    $hostE = Start-TableHost -HostPort $HostPort1 -Name 'host-e'
    $gapProbePath = Join-Path $LogDir 'tamper-gap-probe.json'
    $null = Invoke-Worker -Name 'e2e-tamper-gap-probe' -Arguments @('admitprobe',
        '--url', "ws://127.0.0.1:$HostPort1/v1/table?room=$gapRoom",
        '--keys', $keysDir, '--room', $gapRoom, '--account', 'acct-e2e-gap', '--seat', '0',
        '--out', $gapProbePath) -TimeoutSeconds 120
    $gapProbe = Get-Content -LiteralPath $gapProbePath -Raw | ConvertFrom-Json
    if ($gapProbe.welcomed) { throw 'A journal with a missing committed event was SERVED.' }
    $readyE = Get-HttpStatus ("http://127.0.0.1:$HostPort1/health/ready")
    if ($readyE -ne 503) { throw "The gap host readiness reported $readyE (expected 503)." }
    Stop-TableHost -Process $hostE -Name 'host-e'
    Add-Stage 'e2e-tamper-event-gap' 'PASS' 0 "room=$gapRoom welcomed=$($gapProbe.welcomed) readiness=503"

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
            notRunNextBatch = @('production TLS/WSS termination', 'Lobby HTTPS ticket issuance', 'multi-client full match', 'account asset settlement', 'cross-physical-network')
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

if ($script:Exit -eq 0) { Write-Host 'P54B-DURABLE-TABLE-HOST-PASS' } else { Write-Host 'P54B-DURABLE-TABLE-HOST-FAIL' }
exit $script:Exit
