#requires -Version 5.1
<#
  P5.4b-B2-T repeatable formal runner.

  Creates an isolated loopback PostgreSQL 16.10 cluster on caller-chosen free ports, applies
  the accepted Lobby/table_store migrations, starts the REAL LobbyService over HTTPS and the
  REAL TableService over WSS (PostgreSQL host registry), runs the B2 terminal worker with
  four players and an independent spectator to a natural wall exhaustion, records exact PG
  event/receipt/outbox facts, then restarts the TableService and verifies the terminal is
  still unique and readable. No in-memory service substitute, no self-signed positive ticket,
  no fabricated terminal.

  Exit codes: 0 PASS, 1 FAIL, 2 BLOCKED (missing PG/dotnet/resource or a production
  integration blocker proved by the worker).
#>
[CmdletBinding()]
param(
    [string]$DotnetPath = '',
    [string]$PgZipPath = '',
    [int]$PgPort = 58571,
    [int]$LobbyPort = 58572,
    [int]$TablePort = 58573,
    [string]$Root = '',
    [string]$LogDir = '',
    [int]$DurationSeconds = 240,
    [int]$TestTimeoutSeconds = 420
)

$ErrorActionPreference = 'Stop'
$script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$script:RunId = [Guid]::NewGuid().ToString('N')
$script:Stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = Join-Path $script:RepoRoot ('artifacts\p54b-b2t-pg-' + $script:Stamp + '-' + $script:RunId)
}
if ([string]::IsNullOrWhiteSpace($LogDir)) {
    $LogDir = Join-Path $script:RepoRoot ('artifacts\p54b-b2t-logs-' + $script:Stamp + '-' + $script:RunId)
}
$Root = [System.IO.Path]::GetFullPath($Root)
$LogDir = [System.IO.Path]::GetFullPath($LogDir)
$script:ArtifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $script:RepoRoot 'artifacts'))
foreach ($pathToCheck in @($Root, $LogDir)) {
    if (-not $pathToCheck.StartsWith($script:ArtifactsRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "BLOCKED: B2-T resources must stay under this workspace's artifacts directory: $pathToCheck"
    }
}
$script:LogDir = $LogDir
$script:Stages = New-Object System.Collections.Generic.List[object]
$script:HostRecords = @{}
$script:HostProcesses = New-Object System.Collections.Generic.List[object]
$script:ClusterStarted = $false
$script:Blocker = $false
$script:PgBin = Join-Path $Root 'pgsql\bin'
$script:PgCtl = Join-Path $script:PgBin 'pg_ctl.exe'
$script:InitDb = Join-Path $script:PgBin 'initdb.exe'
$script:Psql = Join-Path $script:PgBin 'psql.exe'
$script:Createdb = Join-Path $script:PgBin 'createdb.exe'
$script:PgIsReady = Join-Path $script:PgBin 'pg_isready.exe'
$script:DataDir = Join-Path $Root 'data'
$script:Marker = Join-Path $Root 'pg-owner.json'
$script:PgLog = Join-Path $Root 'logs\postgres.log'
$script:LobbyDb = 'mahjong_b2_lobby'
$script:TableDb = 'mahjong_b2_table'
$script:PgZipSha256 = 'EBB3B6AF4FA69DEA9951B66855BC4D42DC04E56CCB9AA7024CE3C58BD89D6B0C'
$script:RoomId = ''
$script:StateVersion = '0'
$script:SettlementEvents = '0'
$script:TableEnvironment = $null
$script:TableDll = ''
$script:Natural = $null
$script:WorkerDll = ''

New-Item -ItemType Directory -Force -Path $Root, $LogDir | Out-Null
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$env:TEMP = Join-Path $LogDir 'tmp'
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null

function Write-Step([string]$Message) {
    $line = '[p54b-b2t] ' + $Message
    Write-Host $line
    Add-Content -LiteralPath (Join-Path $LogDir 'runner.log') -Value $line -Encoding utf8
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
    Remove-Item -LiteralPath $out, $err, $LogPath -Force -ErrorAction SilentlyContinue
    $argumentLine = ($Arguments | ForEach-Object {
        $value = [string]$_
        if ($value -match '[\s"]') { '"' + ($value -replace '"', '\"') + '"' } else { $value }
    }) -join ' '
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    $psi.Arguments = $argumentLine
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $psi
    $timedOut = $false
    $outBuilder = New-Object System.Text.StringBuilder
    $errBuilder = New-Object System.Text.StringBuilder
    $exitCode = $null
    try {
        if (-not $process.Start()) { throw "The process could not be started: $FilePath" }
        $outBuffer = New-Object char[] 4096
        $errBuffer = New-Object char[] 4096
        $outTask = $process.StandardOutput.ReadAsync($outBuffer, 0, $outBuffer.Length)
        $errTask = $process.StandardError.ReadAsync($errBuffer, 0, $errBuffer.Length)
        $clock = [System.Diagnostics.Stopwatch]::StartNew()
        $exitClock = $null
        while ($true) {
            if ($outTask -ne $null -and $outTask.IsCompleted) {
                try {
                    $n = $outTask.GetAwaiter().GetResult()
                    if ($n -gt 0) {
                        [void]$outBuilder.Append($outBuffer, 0, $n)
                        $outTask = $process.StandardOutput.ReadAsync($outBuffer, 0, $outBuffer.Length)
                    }
                    else { $outTask = $null }
                }
                catch { $outTask = $null }
            }
            if ($errTask -ne $null -and $errTask.IsCompleted) {
                try {
                    $n = $errTask.GetAwaiter().GetResult()
                    if ($n -gt 0) {
                        [void]$errBuilder.Append($errBuffer, 0, $n)
                        $errTask = $process.StandardError.ReadAsync($errBuffer, 0, $errBuffer.Length)
                    }
                    else { $errTask = $null }
                }
                catch { $errTask = $null }
            }
            if (-not $process.HasExited) {
                if ($clock.ElapsedMilliseconds -ge ($TimeoutSeconds * 1000)) {
                    $timedOut = $true
                    try { & taskkill.exe /PID $process.Id /T /F 2>$null | Out-Null } catch { }
                    try { if (-not $process.HasExited) { $process.Kill() } } catch { }
                    try { $process.WaitForExit(5000) | Out-Null } catch { }
                }
            }
            elseif ($null -eq $exitClock) {
                $exitClock = [System.Diagnostics.Stopwatch]::StartNew()
            }
            if ($process.HasExited -and $outTask -eq $null -and $errTask -eq $null) { break }
            if ($process.HasExited -and $exitClock -ne $null -and $exitClock.ElapsedMilliseconds -ge 3000) {
                try { $process.StandardOutput.Close() } catch { }
                try { $process.StandardError.Close() } catch { }
                break
            }
            Start-Sleep -Milliseconds 10
        }
        try { $process.WaitForExit(5000) | Out-Null } catch { }
        $outText = $outBuilder.ToString()
        $errText = $errBuilder.ToString()
        try { $exitCode = $process.ExitCode } catch { }
    }
    finally {
        try { $process.Dispose() } catch { }
        Set-Content -LiteralPath $out -Value $outText -Encoding utf8
        Set-Content -LiteralPath $err -Value $errText -Encoding utf8
        Set-Content -LiteralPath $LogPath -Value (($outText + [Environment]::NewLine + $errText)) -Encoding utf8
    }
    if ($timedOut) { return 124 }
    return $exitCode
}

function Get-PgScalar {
    param([string]$Database, [string]$Sql, [string]$User = 'mahjong_pg')
    $log = Join-Path $LogDir ('psql-' + [Guid]::NewGuid().ToString('N') + '.log')
    $code = Invoke-Native -FilePath $script:Psql -Arguments @(
        '-h', '127.0.0.1', '-p', "$PgPort", '-U', $User, '-d', $Database, '-tAc', $Sql
    ) -LogPath $log -TimeoutSeconds 60
    if ($code -ne 0) { throw "psql failed with exit $code (see $log)." }
    $text = Get-Content -LiteralPath $log -Raw
    return $text.Trim()
}

function Invoke-PsqlFile {
    param([string]$Database, [string]$User, [string]$Path)
    $log = Join-Path $LogDir ('psql-file-' + [Guid]::NewGuid().ToString('N') + '.log')
    $code = Invoke-Native -FilePath $script:Psql -Arguments @(
        '-h', '127.0.0.1', '-p', "$PgPort", '-U', $User, '-d', $Database, '-v', 'ON_ERROR_STOP=1', '-f', $Path
    ) -LogPath $log -TimeoutSeconds 120
    if ($code -ne 0) { throw "psql file $Path failed with exit $code (see $log)." }
}

function Assert-OwnedCluster {
    if (-not (Test-Path -LiteralPath $script:Marker)) { throw 'BLOCKED: cluster ownership marker is missing.' }
    $marker = Get-Content -LiteralPath $script:Marker -Raw | ConvertFrom-Json
    if ($marker.owner -ne 'mahjong-p54b-b2t' -or $marker.root -ne $Root -or $marker.dataDir -ne $script:DataDir) {
        throw 'BLOCKED: cluster ownership marker does not match this run.'
    }
}

function Initialize-Cluster {
    foreach ($value in @($PgPort, $LobbyPort, $TablePort)) {
        if (Test-PortInUse -Value $value) { throw "BLOCKED: port $value is already in use." }
    }
    if (-not (Test-Path -LiteralPath $script:Marker)) {
        [ordered]@{
            owner = 'mahjong-p54b-b2t'
            runId = $script:RunId
            root = $Root
            dataDir = $script:DataDir
            port = $PgPort
            createdUtc = (Get-Date).ToUniversalTime().ToString('o')
        } | ConvertTo-Json | Set-Content -LiteralPath $script:Marker -Encoding utf8
    }
    Assert-OwnedCluster | Out-Null
    if (-not (Test-Path -LiteralPath $script:PgCtl)) {
        New-Item -ItemType Directory -Force -Path (Join-Path $Root 'download') | Out-Null
        $zipCopy = Join-Path $Root 'download\postgresql-binaries.zip'
        if ($PgZipPath) { Copy-Item -LiteralPath $PgZipPath -Destination $zipCopy -Force }
        elseif (Test-Path -LiteralPath 'D:\majiang-pg-tmp\cache\postgresql-16.10-1-windows-x64-binaries.zip') {
            Copy-Item -LiteralPath 'D:\majiang-pg-tmp\cache\postgresql-16.10-1-windows-x64-binaries.zip' -Destination $zipCopy
        }
        else { throw 'BLOCKED: PostgreSQL 16.10 binary zip was not found.' }
        if ((Get-Sha256 $zipCopy) -ne $script:PgZipSha256) { throw 'BLOCKED: PostgreSQL zip SHA-256 mismatch.' }
        $tar = Join-Path $env:SystemRoot 'System32\tar.exe'
        $extract = Invoke-Native -FilePath $tar -Arguments @('-xf', $zipCopy, '-C', $Root) -LogPath (Join-Path $LogDir 'extract.log') -TimeoutSeconds 300
        if ($extract -ne 0) { throw "PostgreSQL extraction failed with exit $extract." }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $script:DataDir 'PG_VERSION'))) {
        $init = Invoke-Native -FilePath $script:InitDb -Arguments @(
            '-D', $script:DataDir, '-U', 'mahjong_pg',
            '--auth-local=trust', '--auth-host=trust', '--encoding=UTF8', '--locale=C'
        ) -LogPath (Join-Path $LogDir 'initdb.log') -TimeoutSeconds 180
        if ($init -ne 0) { throw "initdb failed with exit $init." }
        $settings = @(
            '', '# --- P5.4b-B2-T disposable loopback cluster ---',
            "listen_addresses = '127.0.0.1'", "port = $PgPort", "unix_socket_directories = ''",
            'fsync = on', 'synchronous_commit = on', 'full_page_writes = on',
            "log_timezone = 'UTC'", "timezone = 'UTC'"
        ) -join [Environment]::NewLine
        [System.IO.File]::AppendAllText(
            (Join-Path $script:DataDir 'postgresql.conf'),
            $settings + [Environment]::NewLine,
            (New-Object System.Text.UTF8Encoding($false)))
    }
    New-Item -ItemType Directory -Force -Path (Join-Path $Root 'logs') | Out-Null
    $start = Invoke-Native -FilePath $script:PgCtl -Arguments @(
        '-D', $script:DataDir, '-l', $script:PgLog, '-w', '-t', '60', 'start'
    ) -LogPath (Join-Path $LogDir 'pg-start.log') -TimeoutSeconds 90
    if ($start -ne 0) { throw "pg_ctl start failed with exit $start." }
    $script:ClusterStarted = $true
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        & $script:PgIsReady -h 127.0.0.1 -p $PgPort -q | Out-Null
        if ($LASTEXITCODE -eq 0) { break }
        Start-Sleep -Milliseconds 250
    }
    if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL did not become ready.' }
    $dataDir = Get-PgScalar -Database 'postgres' -Sql 'SHOW data_directory'
    if (-not [string]::Equals(
        ([System.IO.Path]::GetFullPath($dataDir).TrimEnd('\')),
        ([System.IO.Path]::GetFullPath($script:DataDir).TrimEnd('\')),
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing listener whose data_directory is not this cluster: $dataDir"
    }
}

function New-RoleAndDatabase {
    $roleSql = @'
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'b2_lobby_owner') THEN
    CREATE ROLE b2_lobby_owner LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'b2_lobby_app') THEN
    CREATE ROLE b2_lobby_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'b2_table_owner') THEN
    CREATE ROLE b2_table_owner LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mahjong_table_runtime') THEN
    CREATE ROLE mahjong_table_runtime LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'b2_observer') THEN
    CREATE ROLE b2_observer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE;
  END IF;
END
$$;
'@
    $roleFile = Join-Path $LogDir 'roles.sql'
    Set-Content -LiteralPath $roleFile -Value $roleSql -Encoding utf8
    Invoke-PsqlFile -Database 'postgres' -User 'mahjong_pg' -Path $roleFile
    foreach ($pair in @(@($script:LobbyDb, 'b2_lobby_owner'), @($script:TableDb, 'b2_table_owner'))) {
        $db = $pair[0]
        $owner = $pair[1]
        $exists = Get-PgScalar -Database 'postgres' -Sql "SELECT 1 FROM pg_database WHERE datname='$db'"
        if ($exists -ne '1') {
            $create = Invoke-Native -FilePath $script:Createdb -Arguments @(
                '-h', '127.0.0.1', '-p', "$PgPort", '-U', 'mahjong_pg', '-O', $owner, $db
            ) -LogPath (Join-Path $LogDir ('createdb-' + $db + '.log')) -TimeoutSeconds 60
            if ($create -ne 0) { throw "createdb $db failed with exit $create." }
        }
    }
}

function Apply-Migrations {
    $lobbyMigration = Join-Path $script:RepoRoot 'dotnet\src\Mahjong.LobbyService\Migrations\001_lobby.sql'
    $tableMigration = Join-Path $script:RepoRoot 'dotnet\src\Mahjong.Infrastructure\Migrations\001_table_store.sql'
    if (-not (Test-Path -LiteralPath $lobbyMigration) -or -not (Test-Path -LiteralPath $tableMigration)) {
        throw 'BLOCKED: accepted migration files are missing.'
    }
    Invoke-PsqlFile -Database $script:LobbyDb -User 'b2_lobby_owner' -Path $lobbyMigration
    Invoke-PsqlFile -Database $script:TableDb -User 'b2_table_owner' -Path $tableMigration
    $lobbyGrant = @'
GRANT CONNECT ON DATABASE mahjong_b2_lobby TO b2_lobby_app;
GRANT USAGE ON SCHEMA lobby TO b2_lobby_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA lobby TO b2_lobby_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA lobby TO b2_lobby_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA lobby
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO b2_lobby_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA lobby
  GRANT USAGE, SELECT ON SEQUENCES TO b2_lobby_app;
GRANT CONNECT ON DATABASE mahjong_b2_lobby TO b2_observer;
GRANT USAGE ON SCHEMA lobby TO b2_observer;
GRANT SELECT ON ALL TABLES IN SCHEMA lobby TO b2_observer;
'@
    $lobbyGrantFile = Join-Path $LogDir 'lobby-grants.sql'
    Set-Content -LiteralPath $lobbyGrantFile -Value $lobbyGrant -Encoding utf8
    Invoke-PsqlFile -Database $script:LobbyDb -User 'b2_lobby_owner' -Path $lobbyGrantFile
    $tableGrant = @'
GRANT CONNECT ON DATABASE mahjong_b2_table TO b2_observer;
GRANT USAGE ON SCHEMA table_store TO b2_observer;
GRANT SELECT ON ALL TABLES IN SCHEMA table_store TO b2_observer;
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA table_store TO b2_table_owner;
'@
    $tableGrantFile = Join-Path $LogDir 'table-grants.sql'
    Set-Content -LiteralPath $tableGrantFile -Value $tableGrant -Encoding utf8
    Invoke-PsqlFile -Database $script:TableDb -User 'b2_table_owner' -Path $tableGrantFile
}

function Start-ServiceProcess {
    param([string]$Name, [string]$Dll, [hashtable]$Environment, [string]$OutLog, [string]$ErrLog)
    $saved = @{}
    foreach ($key in $Environment.Keys) {
        $saved[$key] = [System.Environment]::GetEnvironmentVariable($key, 'Process')
        [System.Environment]::SetEnvironmentVariable($key, [string]$Environment[$key], 'Process')
    }
    try {
        $process = Start-Process -FilePath $DotnetPath -ArgumentList @($Dll) -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput $OutLog -RedirectStandardError $ErrLog
    }
    finally {
        foreach ($key in $saved.Keys) {
            [System.Environment]::SetEnvironmentVariable($key, $saved[$key], 'Process')
        }
    }
    $script:HostProcesses.Add($process)
    $script:HostRecords[$process.Id] = [ordered]@{
        pid = $process.Id
        path = $Dll
        startTimeUtc = $process.StartTime.ToUniversalTime()
        name = $Name
    }
    return $process
}

function Stop-HostProcess([string]$Name) {
    foreach ($record in @($script:HostRecords.Values)) {
        if ($record.name -ne $Name) { continue }
        $process = Get-Process -Id $record.pid -ErrorAction SilentlyContinue
        if ($null -eq $process) { continue }
        if ($process.StartTime.ToUniversalTime() -ne $record.startTimeUtc) {
            throw "Refusing to stop PID $($record.pid): start time no longer matches this B2-T run."
        }
        Stop-Process -Id $record.pid -Force
        Start-Sleep -Milliseconds 700
    }
}

function Stop-HostProcesses {
    foreach ($record in @($script:HostRecords.Values)) {
        $process = Get-Process -Id $record.pid -ErrorAction SilentlyContinue
        if ($null -eq $process) { continue }
        if ($process.StartTime.ToUniversalTime() -ne $record.startTimeUtc) {
            throw "Refusing to stop PID $($record.pid): start time no longer matches this B2-T run."
        }
        try { Stop-Process -Id $record.pid -Force -ErrorAction Stop } catch { throw }
    }
    Start-Sleep -Milliseconds 700
}

function Wait-HttpsStatus {
    param([string]$Url, [string]$Expected, [string]$Name, [int]$TimeoutSeconds = 90)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $log = Join-Path $LogDir ($Name + '-' + [Guid]::NewGuid().ToString('N') + '.log')
        $code = Invoke-Native -FilePath $DotnetPath -Arguments @(
            $script:WorkerDll, 'health', '--url', $Url, '--ca-root', $script:TlsRootPem, '--expect', $Expected
        ) -LogPath $log -TimeoutSeconds 30
        if ($code -eq 0) { return }
        Start-Sleep -Milliseconds 500
    }
    throw "BLOCKED: $Name did not reach HTTPS $Expected at $Url."
}

function Stop-And-RemoveCluster {
    if ($script:ClusterStarted) {
        $stop = Invoke-Native -FilePath $script:PgCtl -Arguments @(
            '-D', $script:DataDir, '-m', 'fast', '-w', '-t', '60', 'stop'
        ) -LogPath (Join-Path $LogDir 'pg-stop.log') -TimeoutSeconds 90
        if ($stop -ne 0) { throw "pg_ctl stop failed with exit $stop." }
    }
    Remove-Item -LiteralPath $Root -Recurse -Force -ErrorAction SilentlyContinue
}

function Write-Manifest {
    $entries = New-Object System.Collections.Generic.List[object]
    foreach ($file in @(Get-ChildItem -LiteralPath $LogDir -File -Recurse -ErrorAction SilentlyContinue | Sort-Object FullName)) {
        $relative = $file.FullName.Substring($LogDir.Length + 1)
        if ($relative.StartsWith('tmp\', [StringComparison]::OrdinalIgnoreCase)) { continue }
        if (-not (Test-Path -LiteralPath $file.FullName)) { continue }
        $entries.Add([ordered]@{
            path = $relative
            bytes = $file.Length
            sha256 = Get-Sha256 $file.FullName
        })
    }
    $manifestPath = Join-Path $LogDir 'manifest.sha256.json'
    $entries | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    return [ordered]@{
        count = $entries.Count
        sha256 = Get-Sha256 $manifestPath
        path = $manifestPath
    }
}

$exit = 0
try {
    if (-not $DotnetPath) {
        $candidates = @($env:DOTNET_ROOT, 'D:\Unity\dotnet', 'C:\Program Files\dotnet')
        foreach ($candidate in $candidates) {
            if ($candidate -and (Test-Path -LiteralPath (Join-Path $candidate 'dotnet.exe'))) {
                $DotnetPath = Join-Path $candidate 'dotnet.exe'
                break
            }
        }
    }
    if (-not $DotnetPath -or -not (Test-Path -LiteralPath $DotnetPath)) {
        Add-Stage 'guard-dotnet' 'BLOCKED' 2 'dotnet was not found'
        throw 'BLOCKED: dotnet not found.'
    }
    $sdk = (& $DotnetPath --version | Select-Object -First 1)
    $head = (& git -C $script:RepoRoot rev-parse HEAD).Trim()
    $branch = (& git -C $script:RepoRoot branch --show-current).Trim()
    $dirtyLines = @(& git -C $script:RepoRoot status --porcelain=v1)
    $dirty = if ($dirtyLines.Count -gt 0) { 'yes' } else { 'no' }
    Add-Stage 'guard' 'PASS' 0 "sdk=$sdk head=$head branch=$branch trackedDirty=$dirty"

    Initialize-Cluster
    New-RoleAndDatabase
    Apply-Migrations
    $serverVersion = Get-PgScalar -Database 'postgres' -Sql 'SHOW server_version'
    Add-Stage 'cluster' 'PASS' 0 "postgres=$serverVersion port=$PgPort lobbyDb=$($script:LobbyDb) tableDb=$($script:TableDb)"

    $workerProject = Join-Path $script:RepoRoot 'dotnet\tests\Mahjong.P5.4b.B2.Terminal.Worker\Mahjong.P5.4b.B2.Terminal.Worker.csproj'
    $script:WorkerDll = Join-Path $script:RepoRoot 'dotnet\tests\Mahjong.P5.4b.B2.Terminal.Worker\bin\Release\net8.0\Mahjong.P5.4b.B2.Terminal.Worker.dll'
    $testsProject = Join-Path $script:RepoRoot 'dotnet\tests\Mahjong.TableService.Tests\Mahjong.TableService.Tests.csproj'
    $testsDll = Join-Path $script:RepoRoot 'dotnet\tests\Mahjong.TableService.Tests\bin\Release\net8.0\Mahjong.TableService.Tests.dll'
    $lobbyProject = Join-Path $script:RepoRoot 'dotnet\src\Mahjong.LobbyService\Mahjong.LobbyService.csproj'
    $script:TableDll = Join-Path $script:RepoRoot 'dotnet\src\Mahjong.TableService\bin\Release\net8.0\Mahjong.TableService.dll'
    $lobbyDll = Join-Path $script:RepoRoot 'dotnet\src\Mahjong.LobbyService\bin\Release\net8.0\Mahjong.LobbyService.dll'
    foreach ($pair in @(@($workerProject, $script:WorkerDll), @($testsProject, $testsDll), @($lobbyProject, $lobbyDll), @((Join-Path $script:RepoRoot 'dotnet\src\Mahjong.TableService\Mahjong.TableService.csproj'), $script:TableDll))) {
        $log = Join-Path $LogDir ('build-' + [System.IO.Path]::GetFileNameWithoutExtension($pair[0]) + '.log')
        $code = Invoke-Native -FilePath $DotnetPath -Arguments @('build', $pair[0], '-c', 'Release', '--no-incremental', '--nologo', '-v:minimal', '-nr:false') -LogPath $log -TimeoutSeconds 420
        if ($code -ne 0) { throw "Fresh build failed with exit $code (see $log)." }
        if (-not (Test-Path -LiteralPath $pair[1])) { throw "Fresh build output missing: $($pair[1])" }
    }
    $null = Invoke-Native -FilePath $DotnetPath -Arguments @('build-server', 'shutdown') -LogPath (Join-Path $LogDir 'build-server-shutdown.log') -TimeoutSeconds 120
    Add-Stage 'fresh-build' 'PASS' 0 'B2-T worker, TableService tests, LobbyService and TableService built Release with --no-incremental -nr:false'

    $testXml = Join-Path $LogDir 'table-service-tests.xml'
    $testLog = Join-Path $LogDir 'table-service-tests.log'
    $code = Invoke-Native -FilePath $DotnetPath -Arguments @($testsDll, '-noColor', '-noLogo', '-xml', $testXml) -LogPath $testLog -TimeoutSeconds $TestTimeoutSeconds
    if (-not (Test-Path -LiteralPath $testXml)) { throw "TableService tests produced no XML (exit $code)." }
    $rawXml = Get-Content -LiteralPath $testXml -Raw
    $totalMatch = [regex]::Match($rawXml, 'total="(\d+)"')
    $failedMatch = [regex]::Match($rawXml, 'failed="(\d+)"')
    $skippedMatch = [regex]::Match($rawXml, 'skipped="(\d+)"')
    if (-not $totalMatch.Success -or -not $failedMatch.Success) {
        throw "TableService test XML has no total/failed counts (exit $code)."
    }
    $total = [int]$totalMatch.Groups[1].Value
    $failed = [int]$failedMatch.Groups[1].Value
    $skipped = if ($skippedMatch.Success) { [int]$skippedMatch.Groups[1].Value } else { 0 }
    if ($code -ne 0 -or $total -eq 0 -or $failed -ne 0 -or $skipped -ne 0) {
        throw "TableService tests failed: native exit $code, total=$total failed=$failed skipped=$skipped."
    }
    Add-Stage 'tests-table-service' 'PASS' $code "total=$total failed=$failed skipped=0 xml=$testXml"

    $acceptedWorker = Join-Path $script:RepoRoot 'dotnet\tests\Mahjong.TableService.PersistenceWorker\bin\Release\net8.0\Mahjong.TableService.PersistenceWorker.dll'
    $acceptedProject = Join-Path $script:RepoRoot 'dotnet\tests\Mahjong.TableService.PersistenceWorker\Mahjong.TableService.PersistenceWorker.csproj'
    $code = Invoke-Native -FilePath $DotnetPath -Arguments @('build', $acceptedProject, '-c', 'Release', '--no-incremental', '--nologo', '-v:minimal', '-nr:false') -LogPath (Join-Path $LogDir 'build-certgen.log') -TimeoutSeconds 420
    if ($code -ne 0) { throw "certgen/keygen helper build failed with exit $code." }
    $keysDir = Join-Path $Root 'material\keys'
    $tlsDir = Join-Path $Root 'material\tls'
    $code = Invoke-Native -FilePath $DotnetPath -Arguments @($acceptedWorker, 'keygen', '--dir', $keysDir) -LogPath (Join-Path $LogDir 'keygen.log') -TimeoutSeconds 60
    if ($code -ne 0) { throw "keygen failed with exit $code." }
    $code = Invoke-Native -FilePath $DotnetPath -Arguments @($acceptedWorker, 'certgen', '--out', $tlsDir) -LogPath (Join-Path $LogDir 'certgen.log') -TimeoutSeconds 120
    if ($code -ne 0) { throw "certgen failed with exit $code." }
    $script:TlsRootPem = Join-Path $tlsDir 'root.pem'
    Add-Stage 'keygen-certgen' 'PASS' 0 'isolated ES256 ticket key and test TLS root/leaf generated'

    $lobbyConnection = "Host=127.0.0.1;Port=$PgPort;Database=$($script:LobbyDb);Username=b2_lobby_app;Pooling=false;Timeout=10;Command Timeout=30"
    $tableConnection = "Host=127.0.0.1;Port=$PgPort;Database=$($script:TableDb);Username=mahjong_table_runtime;Pooling=false;Timeout=10;Command Timeout=30"
    $keyBytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($keyBytes) } finally { $rng.Dispose() }
    $payloadKey = [Convert]::ToBase64String($keyBytes)
    $privatePem = Get-Content -LiteralPath (Join-Path $keysDir 'private.pem') -Raw
    $publicPemPath = Join-Path $keysDir 'public.pem'

    $lobbyProcess = Start-ServiceProcess -Name 'lobby' -Dll $lobbyDll -OutLog (Join-Path $LogDir 'lobby.stdout.log') -ErrLog (Join-Path $LogDir 'lobby.stderr.log') -Environment @{
        ASPNETCORE_URLS = "https://127.0.0.1:$LobbyPort"
        ASPNETCORE_ENVIRONMENT = 'Production'
        Kestrel__Certificates__Default__Path = (Join-Path $tlsDir 'leaf.pfx')
        Kestrel__Certificates__Default__Password = ''
        Lobby__StorageProvider = 'Postgres'
        Lobby__PostgresConnectionString = $lobbyConnection
        Lobby__AutoMigrate = 'false'
        Lobby__ReservationTtlSeconds = '60'
        Lobby__TicketTtlSeconds = '120'
        Lobby__TicketMaxTtlSeconds = '300'
        Lobby__TicketPrivateKeyPem = $privatePem
        Lobby__OidcEnabled = 'false'
        Lobby__ProjectionSchema = 'table_runtime'
    }
    Start-Sleep -Seconds 2
    if ($lobbyProcess.HasExited) { throw 'LobbyService exited during startup.' }
    Wait-HttpsStatus -Url "https://127.0.0.1:$LobbyPort/health/ready" -Expected '200' -Name 'lobby-ready'
    Add-Stage 'lobby-https' 'PASS' 0 "real LobbyService HTTPS Postgres store ready on 127.0.0.1:$LobbyPort pid=$($lobbyProcess.Id)"

    $script:TableEnvironment = @{
        TABLESERVICE_STORE = 'postgres'
        TABLESERVICE_PG_CONNECTION = $tableConnection
        TABLESERVICE_PG_PAYLOAD_KEY = $payloadKey
        TABLESERVICE_PG_APPLY_MIGRATIONS = 'false'
        TABLESERVICE_TICKET_KEYS_FILE = $publicPemPath
        TABLESERVICE_TICKET_ISSUER = 'mahjong-lobby'
        TABLESERVICE_TICKET_AUDIENCE = 'mahjong-table'
        TABLESERVICE_HTTP_PORT = "$TablePort"
        TABLESERVICE_TLS_PFX = (Join-Path $tlsDir 'leaf.pfx')
        TABLESERVICE_TLS_PFX_PASSWORD = ''
        TABLESERVICE_LEASE_TTL_SECONDS = '5'
        TABLESERVICE_HEARTBEAT_INTERVAL_MS = '10000'
        TABLESERVICE_IDLE_DEAD_MS = '120000'
        TABLESERVICE_ROOM_TICK_INTERVAL_MS = '100'
        TABLESERVICE_HANDSHAKE_TIMEOUT_MS = '5000'
    }
    $tableProcess = Start-ServiceProcess -Name 'table' -Dll $script:TableDll -OutLog (Join-Path $LogDir 'table.stdout.log') -ErrLog (Join-Path $LogDir 'table.stderr.log') -Environment $script:TableEnvironment
    Start-Sleep -Seconds 3
    if ($tableProcess.HasExited) { throw 'TableService exited during startup.' }
    Wait-HttpsStatus -Url "https://127.0.0.1:$TablePort/health/live" -Expected '200' -Name 'table-live'
    Wait-HttpsStatus -Url "https://127.0.0.1:$TablePort/health/ready" -Expected '200' -Name 'table-ready'
    Add-Stage 'table-wss' 'PASS' 0 "real TableService WSS Postgres host ready on 127.0.0.1:$TablePort pid=$($tableProcess.Id)"

    $naturalOut = Join-Path $LogDir 'b2-natural.json'
    $naturalLog = Join-Path $LogDir 'b2-natural.log'
    $naturalCode = Invoke-Native -FilePath $DotnetPath -Arguments @(
        $script:WorkerDll, 'natural',
        '--lobby-url', "https://127.0.0.1:$LobbyPort",
        '--table-port', "$TablePort",
        '--ca-root', $script:TlsRootPem,
        '--out', $naturalOut,
        '--secrets', (Join-Path $Root 'material\guest-secrets'),
        '--outbox-root', (Join-Path $Root 'material\outbox'),
        '--duration-seconds', "$DurationSeconds"
    ) -LogPath $naturalLog -TimeoutSeconds ($DurationSeconds + 90)
    if (-not (Test-Path -LiteralPath $naturalOut)) { throw "The B2 natural worker produced no evidence (exit $naturalCode)." }
    $script:Natural = Get-Content -LiteralPath $naturalOut -Raw | ConvertFrom-Json
    $script:RoomId = [string]$script:Natural.roomId
    if ($naturalCode -eq 0 -and $script:Natural.terminalReached -eq $true) {
        Add-Stage 'natural-terminal' 'PASS' 0 "room=$($script:RoomId) actions=$($script:Natural.actions) version=$($script:Natural.terminal.stateVersion)"
    }
    elseif ($naturalCode -eq 2) {
        $script:Blocker = $true
        $wall = Get-PgScalar -Database $script:TableDb -User 'b2_observer' -Sql "SELECT count(*) FROM table_store.table_event WHERE room_id='$($script:RoomId)' AND event_type=14"
        $draw = Get-PgScalar -Database $script:TableDb -User 'b2_observer' -Sql "SELECT count(*) FROM table_store.table_event WHERE room_id='$($script:RoomId)' AND event_type=12"
        $outbox = Get-PgScalar -Database $script:TableDb -User 'b2_observer' -Sql "SELECT count(*) FROM table_store.settlement_outbox WHERE match_id='$($script:RoomId)'"
        $receipt = Get-PgScalar -Database $script:TableDb -User 'b2_observer' -Sql "SELECT count(*) FROM table_store.request_receipt WHERE room_id='$($script:RoomId)' AND request_id LIKE 'svc-exhaustive-%' AND status='APPLIED'"
        $stateVersion = Get-PgScalar -Database $script:TableDb -User 'b2_observer' -Sql "SELECT state_version FROM table_store.table_state WHERE room_id='$($script:RoomId)'"
        $eventRows = Get-PgScalar -Database $script:TableDb -User 'b2_observer' -Sql "SELECT event_type::text || ':' || count(*)::text FROM table_store.table_event WHERE room_id='$($script:RoomId)' GROUP BY event_type ORDER BY event_type"
        $receipts = Get-PgScalar -Database $script:TableDb -User 'b2_observer' -Sql "SELECT count(*) FROM table_store.request_receipt WHERE room_id='$($script:RoomId)'"
        $applied = Get-PgScalar -Database $script:TableDb -User 'b2_observer' -Sql "SELECT count(*) FROM table_store.request_receipt WHERE room_id='$($script:RoomId)' AND status='APPLIED'"
        $receiptStatuses = Get-PgScalar -Database $script:TableDb -User 'b2_observer' -Sql "SELECT status || ':' || count(*)::text FROM table_store.request_receipt WHERE room_id='$($script:RoomId)' GROUP BY status ORDER BY status"
        $exhaustiveAny = Get-PgScalar -Database $script:TableDb -User 'b2_observer' -Sql "SELECT count(*) FROM table_store.request_receipt WHERE room_id='$($script:RoomId)' AND request_id LIKE 'svc-exhaustive-%'"
        $exhaustiveStatuses = Get-PgScalar -Database $script:TableDb -User 'b2_observer' -Sql "SELECT status || ':' || count(*)::text FROM table_store.request_receipt WHERE room_id='$($script:RoomId)' AND request_id LIKE 'svc-exhaustive-%' GROUP BY status ORDER BY status"
        $outboxPublished = Get-PgScalar -Database $script:TableDb -User 'b2_observer' -Sql "SELECT count(*) FROM table_store.settlement_outbox WHERE match_id='$($script:RoomId)' AND published_at IS NOT NULL"
        $dbFacts = [ordered]@{
            room = $script:RoomId
            stateVersion = $stateVersion
            eventRows = $eventRows
            receipts = $receipts
            applied = $applied
            receiptStatuses = $receiptStatuses
            exhaustiveReceiptAnyStatus = $exhaustiveAny
            exhaustiveReceiptStatuses = $exhaustiveStatuses
            wallExhaustedEvents = $wall
            exhaustiveDrawEvents = $draw
            outbox = $outbox
            outboxPublished = $outboxPublished
            exhaustiveReceipt = $receipt
            terminalReached = $false
        }
        $dbFacts | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $LogDir 'db-facts.json') -Encoding utf8
        Add-Stage 'natural-terminal' 'BLOCKED' 2 "room=$($script:RoomId) actions=$($script:Natural.actions) wallExhausted=$wall draw=$draw outbox=$outbox exhaustiveReceipt=$receipt"
        throw "BLOCKED: the service attempted the trusted exhaustive draw once but the terminal commit failed; see table.stderr.log / db-facts.json."
    }
    else {
        throw "The B2 natural worker failed with exit $naturalCode (see $naturalLog)."
    }

    $eventRows = Get-PgScalar -Database $script:TableDb -User 'b2_observer' -Sql "SELECT event_type::text || ':' || count(*)::text FROM table_store.table_event WHERE room_id='$($script:RoomId)' GROUP BY event_type ORDER BY event_type"
    $outbox = Get-PgScalar -Database $script:TableDb -User 'b2_observer' -Sql "SELECT count(*) FROM table_store.settlement_outbox WHERE match_id='$($script:RoomId)'"
    $outboxPublished = Get-PgScalar -Database $script:TableDb -User 'b2_observer' -Sql "SELECT count(*) FROM table_store.settlement_outbox WHERE match_id='$($script:RoomId)' AND published_at IS NOT NULL"
    $receipts = Get-PgScalar -Database $script:TableDb -User 'b2_observer' -Sql "SELECT count(*) FROM table_store.request_receipt WHERE room_id='$($script:RoomId)'"
    $applied = Get-PgScalar -Database $script:TableDb -User 'b2_observer' -Sql "SELECT count(*) FROM table_store.request_receipt WHERE room_id='$($script:RoomId)' AND status='APPLIED'"
    $exhaustiveReceipts = Get-PgScalar -Database $script:TableDb -User 'b2_observer' -Sql "SELECT count(*) FROM table_store.request_receipt WHERE room_id='$($script:RoomId)' AND request_id LIKE 'svc-exhaustive-%' AND status='APPLIED'"
    $stateVersion = Get-PgScalar -Database $script:TableDb -User 'b2_observer' -Sql "SELECT state_version FROM table_store.table_state WHERE room_id='$($script:RoomId)'"
    $drawEvents = Get-PgScalar -Database $script:TableDb -User 'b2_observer' -Sql "SELECT count(*) FROM table_store.table_event WHERE room_id='$($script:RoomId)' AND event_type=12"
    $wallEvents = Get-PgScalar -Database $script:TableDb -User 'b2_observer' -Sql "SELECT count(*) FROM table_store.table_event WHERE room_id='$($script:RoomId)' AND event_type=14"
    $roundEndEvents = Get-PgScalar -Database $script:TableDb -User 'b2_observer' -Sql "SELECT count(*) FROM table_store.table_event WHERE room_id='$($script:RoomId)' AND event_type=15"
    $script:StateVersion = $stateVersion
    $script:SettlementEvents = [string]([int]$drawEvents + [int]$roundEndEvents)
    $dbFacts = [ordered]@{
        room = $script:RoomId
        stateVersion = $stateVersion
        eventRows = $eventRows
        receipts = $receipts
        applied = $applied
        exhaustiveReceipts = $exhaustiveReceipts
        outbox = $outbox
        outboxPublished = $outboxPublished
        exhaustiveDrawEvents = $drawEvents
        wallExhaustedEvents = $wallEvents
        roundEndedEvents = $roundEndEvents
    }
    $dbFacts | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $LogDir 'db-facts.json') -Encoding utf8
    if ($outbox -ne '1' -or $drawEvents -ne '1' -or $wallEvents -ne '1' -or $roundEndEvents -ne '1' -or $exhaustiveReceipts -ne '1') {
        throw "Terminal PG facts mismatch: outbox=$outbox draw=$drawEvents wall=$wallEvents roundEnd=$roundEndEvents exhaustiveReceipt=$exhaustiveReceipts"
    }
    Add-Stage 'pg-terminal-facts' 'PASS' 0 "stateVersion=$stateVersion outbox=$outbox exhaustiveReceipt=$exhaustiveReceipts events=$eventRows"

    Stop-HostProcess -Name 'table'
    $tableRestart = Start-ServiceProcess -Name 'table' -Dll $script:TableDll -OutLog (Join-Path $LogDir 'table.restart.stdout.log') -ErrLog (Join-Path $LogDir 'table.restart.stderr.log') -Environment $script:TableEnvironment
    Start-Sleep -Seconds 3
    if ($tableRestart.HasExited) { throw 'TableService exited during restart.' }
    Wait-HttpsStatus -Url "https://127.0.0.1:$TablePort/health/ready" -Expected '200' -Name 'table-restart-ready'
    $verifyOut = Join-Path $LogDir 'b2-verify.json'
    $verifyLog = Join-Path $LogDir 'b2-verify.log'
    $verifyCode = Invoke-Native -FilePath $DotnetPath -Arguments @(
        $script:WorkerDll, 'verify',
        '--lobby-url', "https://127.0.0.1:$LobbyPort",
        '--table-port', "$TablePort",
        '--ca-root', $script:TlsRootPem,
        '--out', $verifyOut,
        '--outbox-root', (Join-Path $Root 'material\verify-outbox'),
        '--room', $script:RoomId,
        '--state-version', $stateVersion,
        '--settlement-events', $script:SettlementEvents
    ) -LogPath $verifyLog -TimeoutSeconds 120
    if (-not (Test-Path -LiteralPath $verifyOut)) { throw "The B2 verify worker produced no evidence (exit $verifyCode)." }
    $verify = Get-Content -LiteralPath $verifyOut -Raw | ConvertFrom-Json
    if ($verifyCode -ne 0 -or $verify.phase -ne 'GAME_PHASE_ROUND_END' -or $verify.stateVersion -ne $stateVersion) {
        throw "Post-restart terminal verification failed: exit=$verifyCode phase=$($verify.phase) version=$($verify.stateVersion) expected=$stateVersion"
    }
    Add-Stage 'restart-terminal' 'PASS' 0 "room=$($script:RoomId) version=$stateVersion phase=ROUND_END events=$($verify.settlementEvents)"

    $postOutbox = Get-PgScalar -Database $script:TableDb -User 'b2_observer' -Sql "SELECT count(*) FROM table_store.settlement_outbox WHERE match_id='$($script:RoomId)'"
    $postVersion = Get-PgScalar -Database $script:TableDb -User 'b2_observer' -Sql "SELECT state_version FROM table_store.table_state WHERE room_id='$($script:RoomId)'"
    if ($postOutbox -ne '1' -or $postVersion -ne $stateVersion) {
        throw "Post-restart uniqueness failed: outbox=$postOutbox version=$postVersion expected=$stateVersion"
    }
    Add-Stage 'post-restart-unique' 'PASS' 0 "outbox=$postOutbox stateVersion=$postVersion"
}
catch {
    $message = $_.Exception.Message
    if ($message -like 'BLOCKED:*') {
        Add-Stage 'fatal' 'BLOCKED' 2 $message
        $exit = 2
    }
    else {
        Add-Stage 'fatal' 'FAIL' 1 $message
        $exit = 1
    }
}
finally {
    try { Stop-HostProcesses } catch { Write-Step ('cleanup host stop failed: ' + $_.Exception.Message) }
    try { Stop-And-RemoveCluster } catch { Write-Step ('cleanup cluster failed: ' + $_.Exception.Message) }
    try {
        $manifest = Write-Manifest
        $summary = [ordered]@{
            runId = $script:RunId
            stages = $script:Stages
            natural = $script:Natural
            roomId = $script:RoomId
            stateVersion = $script:StateVersion
            manifest = $manifest
        }
        $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $LogDir 'wrapper-summary.json') -Encoding utf8
        Write-Step ("manifest: count={0} sha256={1}" -f $manifest.count, $manifest.sha256)
    }
    catch { Write-Step ('manifest failed: ' + $_.Exception.Message) }
    $pass = @($script:Stages | Where-Object { $_.status -eq 'PASS' }).Count
    $blocked = @($script:Stages | Where-Object { $_.status -eq 'BLOCKED' }).Count
    Write-Step ("done: exit={0} passStages={1} blockedStages={2}" -f $exit, $pass, $blocked)
}
exit $exit
