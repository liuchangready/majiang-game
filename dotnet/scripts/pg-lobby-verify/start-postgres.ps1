#requires -Version 5.1
<#
.SYNOPSIS
    Starts a disposable, loopback-only PostgreSQL 16 cluster for the P5.3
    lobby store verification. Downloads the official EDB Windows binaries when
    they are not present, verifies the pinned SHA-256, initializes a dedicated
    data directory and creates the test database.
#>
[CmdletBinding()]
param(
    [string]$Root = (Join-Path $env:TEMP 'mahjong-p53-pg'),
    [int]$Port = 55432,
    [string]$DbName = 'mahjong_lobby_test',
    [string]$ZipPath
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'pg-common.ps1')

$context = New-PgContext -Root $Root -Port $Port -DbName $DbName
Initialize-PgRoot -Context $context
Assert-PgOwned -Context $context | Out-Null

if (-not (Test-Path -LiteralPath $context.PgCtl)) {
    Write-Host "PostgreSQL binaries not found under $($context.Bin)."

    if ($ZipPath) {
        $source = [System.IO.Path]::GetFullPath($ZipPath)
        if (-not (Test-Path -LiteralPath $source)) {
            throw "The supplied PostgreSQL zip was not found: $source"
        }
        Copy-Item -LiteralPath $source -Destination $context.ZipPath -Force
    }

    if (-not (Test-Path -LiteralPath $context.ZipPath)) {
        Write-Host "Downloading official PostgreSQL $($script:PgVersion) Windows binaries from $($script:PgDownloadUrl)"
        & curl.exe -L --fail --silent --show-error -o $context.ZipPath $script:PgDownloadUrl
        if ($LASTEXITCODE -ne 0) {
            throw "The PostgreSQL binary download failed with exit code $LASTEXITCODE."
        }
    }

    $hash = (Get-FileHash -LiteralPath $context.ZipPath -Algorithm SHA256).Hash
    if ($hash -ne $script:PgDownloadSha256) {
        throw "The PostgreSQL binaries do not match the pinned SHA-256. Expected $($script:PgDownloadSha256), got $hash."
    }
    Write-Host "Verified SHA-256 $hash"

    & tar.exe -xf $context.ZipPath -C $context.Root
    if ($LASTEXITCODE -ne 0) {
        throw "Extracting the PostgreSQL binaries failed with exit code $LASTEXITCODE."
    }
}

if (-not (Test-Path -LiteralPath (Join-Path $context.DataDir 'PG_VERSION'))) {
    Assert-PgPortAvailable -Context $context
    Write-Host "Initializing a dedicated database cluster at $($context.DataDir)"
    $init = Invoke-NativeText -FilePath $context.InitDb -Arguments @(
        '-D', $context.DataDir, '-U', 'mahjong_pg',
        '--auth-local=trust', '--auth-host=trust', '--encoding=UTF8', '--locale=C')
    $init.Output | ForEach-Object { Write-Host $_ }
    if ($init.ExitCode -ne 0) {
        throw "initdb failed with exit code $($init.ExitCode)."
    }

    # Explicit, reviewable settings. listen_addresses is loopback only and the
    # port is dedicated; the Unix socket is disabled on Windows.
    $settings = @(
        '',
        '# --- added by dotnet/scripts/pg-lobby-verify (disposable test cluster) ---',
        "listen_addresses = '127.0.0.1'",
        "port = $($context.Port)",
        "unix_socket_directories = ''",
        'fsync = on',
        'synchronous_commit = on',
        'full_page_writes = on',
        "log_timezone = 'UTC'",
        "timezone = 'UTC'"
    ) -join [Environment]::NewLine
    [System.IO.File]::AppendAllText(
        (Join-Path $context.DataDir 'postgresql.conf'),
        $settings + [Environment]::NewLine,
        (New-Object System.Text.UTF8Encoding($false)))
}

if (Test-PgReady -Context $context) {
    Write-Host "PostgreSQL is already accepting connections on 127.0.0.1:$($context.Port)."
} else {
    Assert-PgPortAvailable -Context $context
    Write-Host "Starting PostgreSQL from $($context.DataDir) on 127.0.0.1:$($context.Port)"
    $startCode = Start-PgServer -Context $context -TimeoutSeconds 60
    if ($startCode -ne 0) {
        if (Test-Path -LiteralPath $context.LogFile) {
            Write-Host '--- postgres.log tail ---'
            Get-Content -LiteralPath $context.LogFile -Tail 40 | ForEach-Object { Write-Host $_ }
        }
        throw "pg_ctl start failed with exit code $startCode."
    }
}

$deadline = (Get-Date).AddSeconds(60)
while (-not (Test-PgReady -Context $context)) {
    if ((Get-Date) -gt $deadline) {
        throw "PostgreSQL did not become ready on 127.0.0.1:$($context.Port)."
    }
    Start-Sleep -Milliseconds 250
}

$dbCheck = Invoke-NativeText -FilePath $context.Psql -Arguments @(
    '-h', '127.0.0.1', '-p', "$($context.Port)", '-U', 'mahjong_pg', '-d', 'postgres', '-tAc',
    "SELECT 1 FROM pg_database WHERE datname = '$($context.DbName)'")
$dbExists = ($dbCheck.Output -join '').Trim()
if ($dbCheck.ExitCode -ne 0) {
    throw "Could not list databases: $($dbCheck.Output -join ' ')"
}
if ($dbExists -ne '1') {
    $create = Invoke-NativeText -FilePath $context.Createdb -Arguments @(
        '-h', '127.0.0.1', '-p', "$($context.Port)", '-U', 'mahjong_pg', $context.DbName)
    if ($create.ExitCode -ne 0) {
        throw "createdb failed with exit code $($create.ExitCode)."
    }
    Write-Host "Created database $($context.DbName)."
}

# Ownership marker inside the database. The test harness refuses to truncate
# or write a database that does not carry this marker, so a chance collision of
# host/port/name cannot silently attach to a user-owned database.
$rootLiteral = $context.Root.Replace("'", "''")
$markerSql = @"
CREATE SCHEMA IF NOT EXISTS lobby_verify;
CREATE TABLE IF NOT EXISTS lobby_verify.meta (
    owner text PRIMARY KEY,
    root text NOT NULL,
    created_at timestamptz NOT NULL
);
INSERT INTO lobby_verify.meta (owner, root, created_at)
VALUES ('$($script:PgOwner)', '$rootLiteral', now())
ON CONFLICT (owner) DO UPDATE SET root = EXCLUDED.root, created_at = EXCLUDED.created_at;
"@
$marker = Invoke-PgPsql -Context $context -Arguments @('-v', 'ON_ERROR_STOP=1', '-c', $markerSql)
if ($marker.ExitCode -ne 0) {
    throw "Could not write the disposable-database ownership marker."
}

$versionResult = Invoke-NativeText -FilePath $context.Psql -Arguments @(
    '-h', '127.0.0.1', '-p', "$($context.Port)", '-U', 'mahjong_pg', '-d', $context.DbName, '-tAc',
    'SHOW server_version')
$version = $versionResult.Output
Write-Section 'PostgreSQL test instance ready'
Write-Host "Root:            $($context.Root)"
Write-Host "Data directory:  $($context.DataDir)"
Write-Host "Listen address:  127.0.0.1:$($context.Port) (loopback only)"
Write-Host "Database:        $($context.DbName)"
Write-Host "Server version:  $(($version -join '').Trim())"
Write-Host "Connection:      $($context.Connection)   (no password; throwaway trust auth on loopback)"
Write-Host "Log:             $($context.LogFile)"
Write-Host ''
Write-Host "Set the tests with: `$env:MAHJONG_LOBBY_TEST_POSTGRES='$($context.Connection)'"
