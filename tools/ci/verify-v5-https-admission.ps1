#requires -Version 5.1
<#
  P5.4b-B1 repeatable runner.

  It creates an isolated loopback PostgreSQL 16.10 cluster on fixed free ports,
  applies the accepted Lobby and table_store migrations under dedicated owner
  roles, starts the REAL LobbyService over HTTPS (Postgres store) and the REAL
  TableService over WSS (Postgres host registry), then runs the B1 worker.
  There is no in-memory service substitute and no self-signed positive ticket.

  Exit codes: 0 PASS, 1 FAIL, 2 BLOCKED (missing PG/dotnet/resource or a
  production integration blocker proved by the worker).
#>
[CmdletBinding()]
param(
    [string]$DotnetPath = '',
    [string]$PgZipPath = '',
    [int]$PgPort = 58471,
    [int]$LobbyPort = 58472,
    [int]$TablePort = 58473,
    [string]$Root = '',
    [string]$LogDir = '',
    [int]$SuiteTimeoutSeconds = 360
)

$ErrorActionPreference = 'Stop'
$script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$script:Stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = Join-Path $script:RepoRoot ('artifacts\p54b-b1-pg-' + $script:Stamp)
}
if ([string]::IsNullOrWhiteSpace($LogDir)) {
    $LogDir = Join-Path $script:RepoRoot ('artifacts\p54b-b1-logs-' + $script:Stamp)
}
$Root = [System.IO.Path]::GetFullPath($Root)
$LogDir = [System.IO.Path]::GetFullPath($LogDir)
$script:LogDir = $LogDir
$script:Stages = New-Object System.Collections.Generic.List[object]
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
$script:LobbyDb = 'mahjong_b1_lobby'
$script:TableDb = 'mahjong_b1_table'
$script:PgZipSha256 = 'EBB3B6AF4FA69DEA9951B66855BC4D42DC04E56CCB9AA7024CE3C58BD89D6B0C'
$script:RoomId = ''
$script:Suite = $null

New-Item -ItemType Directory -Force -Path $Root, $LogDir | Out-Null
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$env:TEMP = Join-Path $LogDir 'tmp'
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null

function Write-Step([string]$Message) {
    $line = '[p54b-b1] ' + $Message
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
    Remove-Item -LiteralPath $out, $err -Force -ErrorAction SilentlyContinue
    $argumentLine = ($Arguments | ForEach-Object {
        $value = [string]$_
        if ($value -match '[\s"]') { '"' + ($value -replace '"', '\"') + '"' } else { $value }
    }) -join ' '

    # Windows PowerShell 5.1: Start-Process loses the native exit code whenever it
    # redirects streams, and pipe EOF belongs to the last handle holder (postgres
    # or a lingering build server). Launch through the raw .NET Process, drain
    # incrementally, and read ExitCode before Dispose.
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
        if (-not $process.Start()) { throw "The process could not be started: $FilePath" }
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
                while (-not $read.IsCompleted -and -not $process.HasExited -and
                       $deadline.ElapsedMilliseconds -lt ($TimeoutSeconds * 1000)) {
                    Start-Sleep -Milliseconds 25
                }
                if (-not $read.IsCompleted) {
                    $grace = [System.Diagnostics.Stopwatch]::StartNew()
                    while (-not $read.IsCompleted -and $grace.ElapsedMilliseconds -lt 10000 -and
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
    if ($null -eq $exitCode) { throw "Native exit code unavailable: $FilePath" }
    return [int]$exitCode
}

function Get-PgScalar {
    param([string]$Database, [string]$Sql, [string]$User = 'mahjong_pg')
    $log = Join-Path $LogDir ('psql-' + [Guid]::NewGuid().ToString('N') + '.log')
    $code = Invoke-Native -FilePath $script:Psql -Arguments @(
        '-h', '127.0.0.1', '-p', "$PgPort", '-U', $User, '-v', 'ON_ERROR_STOP=1',
        '-d', $Database, '-tAc', $Sql) -LogPath $log -TimeoutSeconds 60
    if ($code -ne 0) { throw "psql failed with exit $code (see $log)." }
    return ((Get-Content -LiteralPath $log -Raw).Trim())
}

function Invoke-PsqlFile {
    param([string]$Database, [string]$User, [string]$Path)
    $log = Join-Path $LogDir ('psql-file-' + [Guid]::NewGuid().ToString('N') + '.log')
    $code = Invoke-Native -FilePath $script:Psql -Arguments @(
        '-h', '127.0.0.1', '-p', "$PgPort", '-U', $User, '-v', 'ON_ERROR_STOP=1',
        '-d', $Database, '-f', $Path) -LogPath $log -TimeoutSeconds 120
    if ($code -ne 0) { throw "psql file $Path failed with exit $code (see $log)." }
}

function Assert-OwnedCluster {
    if (-not (Test-Path -LiteralPath $script:Marker)) { throw "Refusing to touch ${Root}: marker missing." }
    $marker = Get-Content -LiteralPath $script:Marker -Raw | ConvertFrom-Json
    if ($marker.owner -ne 'mahjong-p54b-b1' -or
        -not [string]::Equals([System.IO.Path]::GetFullPath($marker.root), $Root, [StringComparison]::OrdinalIgnoreCase) -or
        [int]$marker.port -ne $PgPort) {
        throw "Refusing to touch ${Root}: marker mismatch."
    }
    return $marker
}

function Initialize-Cluster {
    foreach ($value in @($PgPort, $LobbyPort, $TablePort)) {
        if (Test-PortInUse -Value $value) { throw "BLOCKED: port $value is already in use." }
    }
    if (Test-Path -LiteralPath $Root) {
        $items = @(Get-ChildItem -LiteralPath $Root -Force -ErrorAction SilentlyContinue)
        if ($items.Count -gt 0 -and -not (Test-Path -LiteralPath $script:Marker)) {
            throw "BLOCKED: $Root is non-empty and has no B1 ownership marker."
        }
    }
    else {
        New-Item -ItemType Directory -Path $Root -Force | Out-Null
    }
    if (-not (Test-Path -LiteralPath $script:Marker)) {
        [ordered]@{
            owner = 'mahjong-p54b-b1'
            root = $Root
            port = $PgPort
            createdUtc = (Get-Date).ToUniversalTime().ToString('o')
        } | ConvertTo-Json | Set-Content -LiteralPath $script:Marker -Encoding utf8
    }
    Assert-OwnedCluster | Out-Null

    if (-not (Test-Path -LiteralPath $script:PgCtl)) {
        $downloadDir = Join-Path $Root 'download'
        New-Item -ItemType Directory -Path $downloadDir -Force | Out-Null
        $zipCopy = Join-Path $downloadDir 'postgresql-binaries.zip'
        if ($PgZipPath) { Copy-Item -LiteralPath $PgZipPath -Destination $zipCopy -Force }
        elseif (Test-Path -LiteralPath 'D:\majiang-pg-tmp\cache\postgresql-16.10-1-windows-x64-binaries.zip') {
            Copy-Item -LiteralPath 'D:\majiang-pg-tmp\cache\postgresql-16.10-1-windows-x64-binaries.zip' -Destination $zipCopy
        }
        else {
            $download = Invoke-Native -FilePath 'curl.exe' -Arguments @(
                '-L', '--fail', '--silent', '--show-error', '--max-time', '300', '-o', $zipCopy,
                'https://get.enterprisedb.com/postgresql/postgresql-16.10-1-windows-x64-binaries.zip'
            ) -LogPath (Join-Path $LogDir 'download.log') -TimeoutSeconds 320
            if ($download -ne 0) { throw "PostgreSQL download failed with exit $download." }
        }
        $hash = Get-Sha256 $zipCopy
        if ($hash -ne $script:PgZipSha256) { throw "BLOCKED: PostgreSQL zip SHA-256 mismatch: $hash" }
        $systemTar = Join-Path $env:SystemRoot 'System32\tar.exe'
        $extract = Invoke-Native -FilePath $systemTar -Arguments @('-xf', $zipCopy, '-C', $Root) -LogPath (Join-Path $LogDir 'extract.log') -TimeoutSeconds 300
        if ($extract -ne 0) { throw "PostgreSQL extraction failed with exit $extract." }
    }

    if (-not (Test-Path -LiteralPath (Join-Path $script:DataDir 'PG_VERSION'))) {
        $init = Invoke-Native -FilePath $script:InitDb -Arguments @(
            '-D', $script:DataDir, '-U', 'mahjong_pg',
            '--auth-local=trust', '--auth-host=trust', '--encoding=UTF8', '--locale=C'
        ) -LogPath (Join-Path $LogDir 'initdb.log') -TimeoutSeconds 180
        if ($init -ne 0) { throw "initdb failed with exit $init." }
        $settings = @(
            '', '# --- P5.4b-B1 disposable loopback cluster ---',
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
    if (-not [string]::Equals([System.IO.Path]::GetFullPath($dataDir), [System.IO.Path]::GetFullPath($script:DataDir), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing listener whose data_directory is not this cluster: $dataDir"
    }
}

function New-RoleAndDatabase {
    $roleSql = @'
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'b1_lobby_owner') THEN
    CREATE ROLE b1_lobby_owner LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'b1_lobby_app') THEN
    CREATE ROLE b1_lobby_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'b1_table_owner') THEN
    CREATE ROLE b1_table_owner LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mahjong_table_runtime') THEN
    CREATE ROLE mahjong_table_runtime LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'b1_observer') THEN
    CREATE ROLE b1_observer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE;
  END IF;
END
$$;
'@
    $roleFile = Join-Path $LogDir 'roles.sql'
    Set-Content -LiteralPath $roleFile -Value $roleSql -Encoding utf8
    Invoke-PsqlFile -Database 'postgres' -User 'mahjong_pg' -Path $roleFile

    foreach ($pair in @(@($script:LobbyDb, 'b1_lobby_owner'), @($script:TableDb, 'b1_table_owner'))) {
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
    Invoke-PsqlFile -Database $script:LobbyDb -User 'b1_lobby_owner' -Path $lobbyMigration
    Invoke-PsqlFile -Database $script:TableDb -User 'b1_table_owner' -Path $tableMigration

    $lobbyGrant = @'
GRANT CONNECT ON DATABASE mahjong_b1_lobby TO b1_lobby_app;
GRANT USAGE ON SCHEMA lobby TO b1_lobby_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA lobby TO b1_lobby_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA lobby TO b1_lobby_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA lobby
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO b1_lobby_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA lobby
  GRANT USAGE, SELECT ON SEQUENCES TO b1_lobby_app;
GRANT CONNECT ON DATABASE mahjong_b1_lobby TO b1_observer;
GRANT USAGE ON SCHEMA lobby TO b1_observer;
GRANT SELECT ON ALL TABLES IN SCHEMA lobby TO b1_observer;
'@
    $lobbyGrantFile = Join-Path $LogDir 'lobby-grants.sql'
    Set-Content -LiteralPath $lobbyGrantFile -Value $lobbyGrant -Encoding utf8
    Invoke-PsqlFile -Database $script:LobbyDb -User 'b1_lobby_owner' -Path $lobbyGrantFile

    $tableGrant = @'
GRANT CONNECT ON DATABASE mahjong_b1_table TO b1_observer;
GRANT USAGE ON SCHEMA table_store TO b1_observer;
GRANT SELECT ON ALL TABLES IN SCHEMA table_store TO b1_observer;
'@
    $tableGrantFile = Join-Path $LogDir 'table-grants.sql'
    Set-Content -LiteralPath $tableGrantFile -Value $tableGrant -Encoding utf8
    Invoke-PsqlFile -Database $script:TableDb -User 'b1_table_owner' -Path $tableGrantFile
}

function Assert-RoleSeparation {
    $lobbyOwner = Get-PgScalar -Database $script:LobbyDb -Sql "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname='mahjong_b1_lobby'"
    $tableOwner = Get-PgScalar -Database $script:TableDb -Sql "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname='mahjong_b1_table'"
    $lobbySchema = Get-PgScalar -Database $script:LobbyDb -Sql "SELECT pg_get_userbyid(nspowner) FROM pg_namespace WHERE nspname='lobby'"
    $tableSchema = Get-PgScalar -Database $script:TableDb -Sql "SELECT pg_get_userbyid(nspowner) FROM pg_namespace WHERE nspname='table_store'"
    $appSuper = Get-PgScalar -Database $script:LobbyDb -Sql "SELECT rolsuper::text || '/' || rolcreatedb::text || '/' || rolcreaterole::text FROM pg_roles WHERE rolname='b1_lobby_app'"
    $tableAppSuper = Get-PgScalar -Database $script:TableDb -Sql "SELECT rolsuper::text || '/' || rolcreatedb::text || '/' || rolcreaterole::text FROM pg_roles WHERE rolname='mahjong_table_runtime'"
    $lobbyDdl = Get-PgScalar -Database $script:LobbyDb -Sql "SELECT has_schema_privilege('b1_lobby_app','lobby','CREATE')::text"
    $tableDdl = Get-PgScalar -Database $script:TableDb -Sql "SELECT has_schema_privilege('mahjong_table_runtime','table_store','CREATE')::text"
    $lobbyDml = Get-PgScalar -Database $script:LobbyDb -Sql "SELECT has_table_privilege('b1_lobby_app','lobby.room','INSERT')::text"
    $tableDml = Get-PgScalar -Database $script:TableDb -Sql "SELECT has_table_privilege('mahjong_table_runtime','table_store.table_event','INSERT')::text"
    if ($lobbyOwner -ne 'b1_lobby_owner' -or $tableOwner -ne 'b1_table_owner' -or
        $lobbySchema -ne 'b1_lobby_owner' -or $tableSchema -ne 'b1_table_owner') {
        throw "Migration/owner identity mismatch: lobby=$lobbyOwner/$lobbySchema table=$tableOwner/$tableSchema"
    }
    if ($appSuper -ne 'false/false/false' -or $tableAppSuper -ne 'false/false/false') {
        throw "An application role has elevated attributes: lobby=$appSuper table=$tableAppSuper"
    }
    if ($lobbyDdl -ne 'false' -or $tableDdl -ne 'false' -or $lobbyDml -ne 'true' -or $tableDml -ne 'true') {
        throw "Role privilege split is wrong: lobbyDdl=$lobbyDdl tableDdl=$tableDdl lobbyDml=$lobbyDml tableDml=$tableDml"
    }
    return [ordered]@{
        lobbyOwner = $lobbyOwner; lobbySchema = $lobbySchema; lobbyAppAttributes = $appSuper
        lobbyAppDdl = $lobbyDdl; lobbyAppDml = $lobbyDml
        tableOwner = $tableOwner; tableSchema = $tableSchema; tableAppAttributes = $tableAppSuper
        tableAppDdl = $tableDdl; tableAppDml = $tableDml
    }
}

function Start-ServiceProcess {
    param(
        [string]$Name,
        [string]$Dll,
        [hashtable]$Environment,
        [string]$OutLog,
        [string]$ErrLog
    )
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
    return $process
}

function Wait-HttpsStatus {
    param([string]$Url, [string]$Expected, [string]$Name, [int]$TimeoutSeconds = 60)
    $workerDll = $script:WorkerDll
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $log = Join-Path $LogDir ($Name + '-' + [Guid]::NewGuid().ToString('N') + '.log')
        $code = Invoke-Native -FilePath $DotnetPath -Arguments @(
            $workerDll, 'https-health', '--url', $Url, '--ca-root', $script:TlsRootPem, '--expect', $Expected
        ) -LogPath $log -TimeoutSeconds 30
        if ($code -eq 0) { return }
        Start-Sleep -Milliseconds 500
    }
    throw "BLOCKED: $Name did not reach HTTPS $Expected at $Url."
}

function Stop-HostProcesses {
    foreach ($process in $script:HostProcesses) {
        if (-not $process.HasExited) {
            try { Stop-Process -Id $process.Id -Force -ErrorAction Stop } catch { }
        }
    }
    Start-Sleep -Milliseconds 500
}

function Remove-TreeLongPath([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $prefixed = '\\?\' + [System.IO.Path]::GetFullPath($Path)
    [System.IO.Directory]::Delete($prefixed, $true)
}

function Stop-And-RemoveCluster {
    if (-not (Test-Path -LiteralPath $Root)) { return }
    Assert-OwnedCluster | Out-Null
    Stop-HostProcesses
    if ($script:ClusterStarted) {
        $log = Join-Path $LogDir 'pg-stop.log'
        $null = Invoke-Native -FilePath $script:PgCtl -Arguments @('-D', $script:DataDir, '-m', 'immediate', '-w', '-t', '30', 'stop') -LogPath $log -TimeoutSeconds 60
    }
    foreach ($value in @($PgPort, $LobbyPort, $TablePort)) {
        if (Test-PortInUse -Value $value) {
            Write-Step "cleanup refused: port $value still has a listener"
            return
        }
    }
    Remove-TreeLongPath $Root
    Write-Step "ownership-verified cluster removed: $Root"
}

function Get-XmlCounts([string]$Path) {
    $document = New-Object System.Xml.XmlDocument
    $document.Load($Path)
    $assemblies = @($document.SelectNodes('/assemblies/assembly'))
    if ($assemblies.Count -eq 0) { throw "Test XML has no assembly node: $Path" }
    $total = 0; $passed = 0; $failed = 0; $errors = 0; $skipped = 0; $notRun = 0
    foreach ($assembly in $assemblies) {
        $total += [int]$assembly.GetAttribute('total')
        $passed += [int]$assembly.GetAttribute('passed')
        $failed += [int]$assembly.GetAttribute('failed')
        $errors += [int]$assembly.GetAttribute('errors')
        $skipped += [int]$assembly.GetAttribute('skipped')
        $notRun += [int]$assembly.GetAttribute('not-run')
    }
    return [ordered]@{
        total = $total; passed = $passed; failed = $failed
        errors = $errors; skipped = $skipped; notRun = $notRun
    }
}

function Invoke-TestRun {
    param([string]$Name, [string]$Dll)
    $xml = Join-Path $LogDir ($Name + '.xml')
    $log = Join-Path $LogDir ($Name + '.log')
    $code = Invoke-Native -FilePath $DotnetPath -Arguments @($Dll, '-noColor', '-noLogo', '-result-xml', $xml) -LogPath $log -TimeoutSeconds 300
    if (-not (Test-Path -LiteralPath $xml)) { throw "$Name produced no XML (exit $code)." }
    $counts = Get-XmlCounts $xml
    if ($code -ne 0 -or $counts.total -le 0 -or $counts.failed -ne 0 -or $counts.errors -ne 0 -or
        $counts.skipped -ne 0 -or $counts.notRun -ne 0 -or $counts.passed -ne $counts.total) {
        throw "$Name failed: exit=$code total=$($counts.total) passed=$($counts.passed) failed=$($counts.failed) errors=$($counts.errors) skipped=$($counts.skipped) notRun=$($counts.notRun)."
    }
    Add-Stage $Name 'PASS' $code "total=$($counts.total) passed=$($counts.passed) failed=0 errors=0 skipped=0 notRun=0 xmlSha256=$(Get-Sha256 $xml)"
}

function Write-Manifest {
    $files = @(Get-ChildItem -LiteralPath $LogDir -Recurse -File |
        Where-Object { $_.FullName -notlike '*\tmp\*' } |
        Sort-Object FullName)
    $entries = New-Object System.Collections.Generic.List[object]
    foreach ($file in $files) {
        $entries.Add([ordered]@{
            path = $file.FullName.Substring($LogDir.Length + 1)
            length = $file.Length
            sha256 = Get-Sha256 $file.FullName
        })
    }
    $manifestPath = Join-Path $LogDir 'manifest.sha256.json'
    $entries | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    return [ordered]@{ count = $entries.Count; sha256 = (Get-Sha256 $manifestPath); path = $manifestPath }
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
        $exit = 2
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
    $ownership = Assert-RoleSeparation
    Add-Stage 'role-separation' 'PASS' 0 "lobbyDbOwner=$($ownership.lobbyOwner) lobbySchemaOwner=$($ownership.lobbySchema) lobbyAppAttrs=$($ownership.lobbyAppAttributes) lobbyAppDdl=$($ownership.lobbyAppDdl) lobbyAppDml=$($ownership.lobbyAppDml); tableDbOwner=$($ownership.tableOwner) tableSchemaOwner=$($ownership.tableSchema) tableAppAttrs=$($ownership.tableAppAttributes) tableAppDdl=$($ownership.tableAppDdl) tableAppDml=$($ownership.tableAppDml)"
    $serverVersion = Get-PgScalar -Database 'postgres' -Sql 'SHOW server_version'
    $dataDir = Get-PgScalar -Database 'postgres' -Sql 'SHOW data_directory'
    Add-Stage 'cluster' 'PASS' 0 "postgres=$serverVersion data_directory=$dataDir port=$PgPort lobbyDb=$script:LobbyDb tableDb=$script:TableDb owners=$($ownership.lobbyOwner)/$($ownership.tableOwner)"

    $workerProject = Join-Path $script:RepoRoot 'dotnet\tests\Mahjong.P5.4b.B1.HttpsAdmission.Worker\Mahjong.P5.4b.B1.HttpsAdmission.Worker.csproj'
    $testsProject = Join-Path $script:RepoRoot 'dotnet\tests\Mahjong.P5.4b.B1.HttpsAdmission.Tests\Mahjong.P5.4b.B1.HttpsAdmission.Tests.csproj'
    $script:WorkerDll = Join-Path $script:RepoRoot 'dotnet\tests\Mahjong.P5.4b.B1.HttpsAdmission.Worker\bin\Release\net8.0\Mahjong.P5.4b.B1.HttpsAdmission.Worker.dll'
    $testsDll = Join-Path $script:RepoRoot 'dotnet\tests\Mahjong.P5.4b.B1.HttpsAdmission.Tests\bin\Release\net8.0\Mahjong.P5.4b.B1.HttpsAdmission.Tests.dll'
    $buildIndex = 0
    foreach ($pair in @(@($workerProject, $script:WorkerDll), @($testsProject, $testsDll))) {
        $buildIndex++
        $log = Join-Path $LogDir ("build-$buildIndex.log")
        $code = Invoke-Native -FilePath $DotnetPath -Arguments @('build', $pair[0], '-c', 'Release', '--no-incremental', '--nologo', '-v:minimal', '-nr:false') -LogPath $log -TimeoutSeconds 420
        if ($code -ne 0) { throw "Fresh build failed with exit $code (see $log)." }
        if (-not (Test-Path -LiteralPath $pair[1])) { throw "Fresh build output missing: $($pair[1])" }
    }
    $null = Invoke-Native -FilePath $DotnetPath -Arguments @('build-server', 'shutdown') -LogPath (Join-Path $LogDir 'build-server-shutdown.log') -TimeoutSeconds 120
    Add-Stage 'fresh-build' 'PASS' 0 "2 B1 projects built with $sdk (--no-incremental, -nr:false)"

    Invoke-TestRun -Name 'tests-b1' -Dll $testsDll

    $acceptedWorker = Join-Path $script:RepoRoot 'dotnet\tests\Mahjong.TableService.PersistenceWorker\bin\Release\net8.0\Mahjong.TableService.PersistenceWorker.dll'
    if (-not (Test-Path -LiteralPath $acceptedWorker)) {
        $acceptedProject = Join-Path $script:RepoRoot 'dotnet\tests\Mahjong.TableService.PersistenceWorker\Mahjong.TableService.PersistenceWorker.csproj'
        $code = Invoke-Native -FilePath $DotnetPath -Arguments @('build', $acceptedProject, '-c', 'Release', '--nologo', '-v:minimal', '-nr:false') -LogPath (Join-Path $LogDir 'build-accepted-worker.log') -TimeoutSeconds 420
        if ($code -ne 0) { throw "The accepted certgen/keygen worker did not build (exit $code)." }
    }
    $keysDir = Join-Path $Root 'material\keys'
    $tlsDir = Join-Path $Root 'material\tls'
    $code = Invoke-Native -FilePath $DotnetPath -Arguments @($acceptedWorker, 'keygen', '--dir', $keysDir) -LogPath (Join-Path $LogDir 'keygen.log') -TimeoutSeconds 60
    if ($code -ne 0) { throw "keygen failed with exit $code." }
    $code = Invoke-Native -FilePath $DotnetPath -Arguments @($acceptedWorker, 'certgen', '--out', $tlsDir) -LogPath (Join-Path $LogDir 'certgen.log') -TimeoutSeconds 120
    if ($code -ne 0) { throw "certgen failed with exit $code." }
    $script:TlsRootPem = Join-Path $tlsDir 'root.pem'
    Add-Stage 'keygen-certgen' 'PASS' 0 "isolated ES256 ticket key and test TLS chain generated; positive tickets are Lobby-signed only"

    $lobbyDll = Join-Path $script:RepoRoot 'dotnet\src\Mahjong.LobbyService\bin\Release\net8.0\Mahjong.LobbyService.dll'
    $tableDll = Join-Path $script:RepoRoot 'dotnet\src\Mahjong.TableService\bin\Release\net8.0\Mahjong.TableService.dll'
    $lobbyProject = Join-Path $script:RepoRoot 'dotnet\src\Mahjong.LobbyService\Mahjong.LobbyService.csproj'
    $tableProject = Join-Path $script:RepoRoot 'dotnet\src\Mahjong.TableService\Mahjong.TableService.csproj'
    foreach ($pair in @(@($lobbyProject, $lobbyDll), @($tableProject, $tableDll))) {
        $log = Join-Path $LogDir ('build-svc-' + [System.IO.Path]::GetFileNameWithoutExtension($pair[0]) + '.log')
        $code = Invoke-Native -FilePath $DotnetPath -Arguments @('build', $pair[0], '-c', 'Release', '--no-incremental', '--nologo', '-v:minimal', '-nr:false') -LogPath $log -TimeoutSeconds 420
        if ($code -ne 0) { throw "Service build failed with exit $code (see $log)." }
        if (-not (Test-Path -LiteralPath $pair[1])) { throw "Service build output missing: $($pair[1])" }
    }
    $null = Invoke-Native -FilePath $DotnetPath -Arguments @('build-server', 'shutdown') -LogPath (Join-Path $LogDir 'build-server-shutdown-services.log') -TimeoutSeconds 120
    Add-Stage 'fresh-build-services' 'PASS' 0 'LobbyService and TableService built Release with --no-incremental -nr:false; build servers shut down'

    $lobbyConnection = "Host=127.0.0.1;Port=$PgPort;Database=$script:LobbyDb;Username=b1_lobby_app;Pooling=false;Timeout=10;Command Timeout=30"
    $tableConnection = "Host=127.0.0.1;Port=$PgPort;Database=$script:TableDb;Username=mahjong_table_runtime;Pooling=false;Timeout=10;Command Timeout=30"
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
        Lobby__TicketTtlSeconds = '60'
        Lobby__TicketMaxTtlSeconds = '300'
        Lobby__TicketPrivateKeyPem = $privatePem
        Lobby__OidcEnabled = 'false'
        Lobby__ProjectionSchema = 'table_runtime'
    }
    Start-Sleep -Seconds 2
    if ($lobbyProcess.HasExited) { throw "LobbyService exited during startup." }
    Wait-HttpsStatus -Url "https://127.0.0.1:$LobbyPort/health/ready" -Expected '200' -Name 'lobby-ready'
    Add-Stage 'lobby-https' 'PASS' 0 "real LobbyService HTTPS Postgres store ready on 127.0.0.1:$LobbyPort pid=$($lobbyProcess.Id)"

    $tableProcess = Start-ServiceProcess -Name 'table' -Dll $tableDll -OutLog (Join-Path $LogDir 'table.stdout.log') -ErrLog (Join-Path $LogDir 'table.stderr.log') -Environment @{
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
        TABLESERVICE_HEARTBEAT_INTERVAL_MS = '5000'
        TABLESERVICE_IDLE_DEAD_MS = '120000'
        TABLESERVICE_ROOM_TICK_INTERVAL_MS = '250'
        TABLESERVICE_HANDSHAKE_TIMEOUT_MS = '5000'
    }
    Start-Sleep -Seconds 3
    if ($tableProcess.HasExited) { throw "TableService exited during startup." }
    Wait-HttpsStatus -Url "https://127.0.0.1:$TablePort/health/live" -Expected '200' -Name 'table-live'
    Wait-HttpsStatus -Url "https://127.0.0.1:$TablePort/health/ready" -Expected '200' -Name 'table-ready'
    Add-Stage 'table-wss' 'PASS' 0 "real TableService WSS Postgres host ready on 127.0.0.1:$TablePort pid=$($tableProcess.Id)"

    $suiteOut = Join-Path $LogDir 'suite.json'
    $secretsDir = Join-Path $Root 'material\guest-secrets'
    $outboxRoot = Join-Path $LogDir 'outbox'
    $crashOut = Join-Path $LogDir 'crash.json'
    $crashStarted = Join-Path $LogDir 'crash-started.json'
    $recoverOut = Join-Path $LogDir 'recover.json'
    $suiteLog = Join-Path $LogDir 'suite.log'
    $suiteArgs = @(
        $script:WorkerDll, 'suite',
        '--lobby-url', "https://127.0.0.1:$LobbyPort",
        '--table-port', "$TablePort",
        '--ca-root', $script:TlsRootPem,
        '--out', $suiteOut,
        '--secrets', $secretsDir,
        '--outbox-root', $outboxRoot,
        '--crash-out', $crashOut,
        '--crash-started', $crashStarted,
        '--recover-out', $recoverOut,
        '--tls-dir', $tlsDir,
        '--dotnet', $DotnetPath,
        '--worker-dll', $script:WorkerDll
    )
    $suiteCode = Invoke-Native -FilePath $DotnetPath -Arguments $suiteArgs -LogPath $suiteLog -TimeoutSeconds $SuiteTimeoutSeconds
    if (-not (Test-Path -LiteralPath $suiteOut)) { throw "The B1 suite produced no evidence JSON (exit $suiteCode)." }
    $script:Suite = Get-Content -LiteralPath $suiteOut -Raw | ConvertFrom-Json
    $script:RoomId = $script:Suite.RoomId
    if ($suiteCode -eq 0) {
        Add-Stage 'suite' 'PASS' 0 "room=$($script:RoomId) normalVersion=$($script:Suite.NormalActionStateVersion) crashVersion=$($script:Suite.CrashCommandVersion) recovered=$($script:Suite.RecoveryFound)"
    }
    elseif ($suiteCode -eq 2 -and $script:Suite.Blocked) {
        $script:Blocker = $true
        Add-Stage 'suite' 'BLOCKED' 2 "room=$($script:RoomId) code=$($script:Suite.BlockerCode) serverReceipt=$($script:Suite.ServerReceiptStatus)/$($script:Suite.ServerReceiptCommandVersion) scopePresent=$($script:Suite.ServerReceiptScopePresent) clientPending=$($script:Suite.ClientPendingAfterServerReceipt) rawQueryFound=$($script:Suite.RawReceiptQueryFound)"
    }
    else {
        throw "The B1 suite failed with exit $suiteCode (see $suiteLog)."
    }

    $room = $script:RoomId
    $stateVersion = Get-PgScalar -Database $script:TableDb -User 'b1_observer' -Sql "SELECT state_version FROM table_store.table_state WHERE room_id='$room'"
    $eventCount = Get-PgScalar -Database $script:TableDb -User 'b1_observer' -Sql "SELECT count(*) FROM table_store.table_event WHERE room_id='$room'"
    $applied = Get-PgScalar -Database $script:TableDb -User 'b1_observer' -Sql "SELECT count(*) FROM table_store.request_receipt WHERE room_id='$room' AND status='APPLIED'"
    $scopes = Get-PgScalar -Database $script:TableDb -User 'b1_observer' -Sql "SELECT count(*) FROM table_store.request_scope WHERE room_id='$room'"
    $outbox = Get-PgScalar -Database $script:TableDb -User 'b1_observer' -Sql "SELECT count(*) FROM table_store.settlement_outbox"
    $lobbyAccounts = Get-PgScalar -Database $script:LobbyDb -User 'b1_observer' -Sql "SELECT count(*) FROM lobby.account"
    $lobbyAssignments = Get-PgScalar -Database $script:LobbyDb -User 'b1_observer' -Sql "SELECT count(*) FROM lobby.assignment WHERE room_id='$room'"
    $lobbyTickets = Get-PgScalar -Database $script:LobbyDb -User 'b1_observer' -Sql "SELECT count(*) FROM lobby.table_ticket WHERE room_id='$room'"
    $lobbyConsumed = Get-PgScalar -Database $script:LobbyDb -User 'b1_observer' -Sql "SELECT count(*) FROM lobby.table_ticket_consumption WHERE room_id='$room'"
    $dbFacts = [ordered]@{
        room = $room
        tableStateVersion = [int]$stateVersion
        tableEventRows = [int]$eventCount
        appliedReceipts = [int]$applied
        scopes = [int]$scopes
        settlementOutboxRows = [int]$outbox
        lobbyAccounts = [int]$lobbyAccounts
        lobbyAssignmentsForRoom = [int]$lobbyAssignments
        lobbyTicketsForRoom = [int]$lobbyTickets
        lobbyTicketConsumptionRows = [int]$lobbyConsumed
        note = 'Lobby consumption is not wired by the accepted P5.1/P5.3 boundary; TableService owns the in-memory one-time jti registry.'
    }
    $dbFacts | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $LogDir 'db-facts.json') -Encoding utf8
    if ([int]$stateVersion -lt 1 -or [int]$applied -ne 1 -or [int]$eventCount -lt 1) {
        throw "PG facts do not match the one applied action: stateVersion=$stateVersion applied=$applied events=$eventCount"
    }
    if ([int]$outbox -ne 0) { throw "Unexpected settlement outbox rows: $outbox" }
    Add-Stage 'pg-facts' 'PASS' 0 "stateVersion=$stateVersion (>=1; later versions are authoritative deadline auto-play) events=$eventCount appliedReceipts=$applied scopes=$scopes settlementOutbox=$outbox lobbyAssignments=$lobbyAssignments lobbyTickets=$lobbyTickets lobbyConsumption=$lobbyConsumed"

    $suiteSummary = [ordered]@{
        blocked = [bool]$script:Suite.Blocked
        blockerCode = [string]$script:Suite.BlockerCode
        blockerDetail = [string]$script:Suite.BlockerDetail
        admittedPlayers = [int]$script:Suite.AdmittedPlayers
        spectatorAdmitted = [bool]$script:Suite.SpectatorAdmitted
        baselineFullForAll = [bool]$script:Suite.BaselineFullForAll
        privateStreamsIsolated = [bool]$script:Suite.PrivateStreamsIsolated
        serverReceiptObserved = [bool]$script:Suite.ServerReceiptObserved
        serverReceiptStatus = [string]$script:Suite.ServerReceiptStatus
        serverReceiptCommandVersion = [int]$script:Suite.ServerReceiptCommandVersion
        serverReceiptScopePresent = [bool]$script:Suite.ServerReceiptScopePresent
        clientPendingAfterServerReceipt = [int]$script:Suite.ClientPendingAfterServerReceipt
        rawReceiptQueryFound = [bool]$script:Suite.RawReceiptQueryFound
        rawReceiptQueryCommandVersion = [int]$script:Suite.RawReceiptQueryCommandVersion
        immediateReissueRejected = [bool]$script:Suite.ImmediateReissueRejected
        immediateReissueDetail = [string]$script:Suite.ImmediateReissueDetail
        negativeResults = @($script:Suite.NegativeResults)
        assertions = @($script:Suite.Assertions)
    }
    $suiteSummary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $LogDir 'suite-summary.json') -Encoding utf8

    if ($script:Blocker) {
        $exit = 2
    }
}
catch {
    $message = $_.Exception.Message
    Write-Step ('FAILED: ' + $message)
    Write-Step ('stack: ' + $_.ScriptStackTrace)
    if ($message -like 'BLOCKED:*') { $exit = 2 } else { $exit = 1 }
    Add-Stage 'exception' 'FAIL' $exit $message
}
finally {
    try { Stop-And-RemoveCluster } catch { Write-Step ('cleanup error: ' + $_.Exception.Message) }
    try {
        $manifest = Write-Manifest
        $summary = [ordered]@{
            exitCode = $exit
            status = $(if ($exit -eq 0) { 'PASS' } elseif ($exit -eq 2) { 'BLOCKED' } else { 'FAIL' })
            secretMaterialRoot = (Join-Path $Root 'material')
            secretMaterialRemoved = (-not (Test-Path -LiteralPath (Join-Path $Root 'material')))
            stages = $script:Stages
            suite = $script:Suite
            manifest = $manifest
        }
        $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $LogDir 'wrapper-summary.json') -Encoding utf8
        Write-Step ("manifest count={0} sha256={1}" -f $manifest.count, $manifest.sha256)
    }
    catch {
        Write-Step ('manifest error: ' + $_.Exception.Message)
        if ($exit -eq 0) { $exit = 1 }
    }
}

if ($exit -eq 0) {
    Write-Host 'P54B-B1-HTTPS-ADMISSION-PASS'
}
elseif ($exit -eq 2) {
    Write-Host 'P54B-B1-HTTPS-ADMISSION-BLOCKED'
}
else {
    Write-Host 'P54B-B1-HTTPS-ADMISSION-FAIL'
}
exit $exit
