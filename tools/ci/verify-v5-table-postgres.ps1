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

    Stages:
      1. isolated cluster + two idempotent migrations (the test assembly repeats)
      2. fresh Infrastructure/Test/CrashWorker builds
      3. direct xUnit runner (real selected tests, zero skips accepted)
      4. independent writer -> self-kill after durable commit -> immediate
         PostgreSQL crash restart -> independent reader/recovery
      5. pg_dump from the live test DB -> restore into a new empty DB -> reader
      6. ownership-verified stop/delete, logs, XML, SHA-256 manifest and summary

    A missing PG binary/key/dotnet path is BLOCKED (exit 2), never a skipped or
    zero-test green.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$DotnetPath,
    [int]$Port = 55592,
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
    if (Test-PortInUse -Value $Port) {
        throw "BLOCKED: port $Port is already in use; refusing to attach to an unknown PostgreSQL."
    }
    if ($Port -eq 5432 -or $Port -eq 5433 -or $Port -eq 55460 -or $Port -eq 55463) {
        throw "BLOCKED: port $Port is reserved; choose a unique P5.2 test port."
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
    if ($startCode -ne 0) { throw "pg_ctl start failed with exit $startCode (see $startLog)." }
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
    if ($startCode -ne 0) { throw "pg_ctl restart start failed with exit $startCode." }
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
            '-Configuration', $Configuration, '-RunnerMode', 'direct',
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
    Write-Step ("FAIL: " + $message)
    Add-Stage 'result' 'FAIL' 1 $message
    $script:Exit = 1
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

    foreach ($name in $script:EnvNames) {
        [System.Environment]::SetEnvironmentVariable($name, $script:EnvSnapshot[$name], 'Process')
    }

    try {
        $summary = [ordered]@{
            generatedUtc = (Get-Date).ToUniversalTime().ToString('o')
            sdk = $(if ($sdkVersion) { $sdkVersion } else { '' })
            port = $Port
            database = $script:Database
            restoreDatabase = $script:RestoreDatabase
            payloadKeyRecorded = $false
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

if ($script:Exit -eq 0) { Write-Host 'P52-TABLE-POSTGRES-PASS' } else { Write-Host 'P52-TABLE-POSTGRES-FAIL' }
exit $script:Exit
