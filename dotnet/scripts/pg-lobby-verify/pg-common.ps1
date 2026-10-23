# Shared helpers for the disposable PostgreSQL verification cluster used by
# the P5.3 lobby PostgreSQL tests. This file is dot-sourced, it performs no
# destructive action by itself.
#
# Safety model:
#   * only loopback (listen_addresses = '127.0.0.1')
#   * a dedicated non-default port (5432/5433 are refused)
#   * a dedicated data directory created by this script
#   * an ownership marker file; cleanup refuses any directory it does not own
#   * host authentication is "trust" only because the cluster is a throwaway
#     loopback instance and therefore has no password to leak. No secret is
#     written to logs, evidence or this repository.

$script:PgOwner = 'mahjong-p53-pg'
$script:PgVersion = '16.10-1'
$script:PgDownloadUrl = 'https://get.enterprisedb.com/postgresql/postgresql-16.10-1-windows-x64-binaries.zip'
$script:PgDownloadSha256 = 'EBB3B6AF4FA69DEA9951B66855BC4D42DC04E56CCB9AA7024CE3C58BD89D6B0C'

function Get-RepoRoot {
    param([string]$StartPath)
    $current = [System.IO.Path]::GetFullPath($StartPath)
    while ($current) {
        if (Test-Path (Join-Path $current 'AGENTS.md')) {
            return $current
        }
        $parent = Split-Path -Parent $current
        if ($parent -eq $current) {
            break
        }
        $current = $parent
    }
    throw "Could not locate the repository root (AGENTS.md) above $StartPath."
}

function New-PgContext {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [int]$Port = 55432,
        [string]$DbName = 'mahjong_lobby_test'
    )

    if ($Port -eq 5432 -or $Port -eq 5433) {
        throw "Refusing port $Port. Use a dedicated loopback test port such as 55432."
    }
    if ($Port -lt 1024 -or $Port -gt 65535) {
        throw "Port $Port is not a valid dedicated test port."
    }
    if ($DbName -notmatch '^mahjong_lobby_test') {
        throw "The database name must start with 'mahjong_lobby_test' (got '$DbName')."
    }

    $rootFull = [System.IO.Path]::GetFullPath($Root)
    $pathRoot = [System.IO.Path]::GetPathRoot($rootFull)
    if ([string]::Equals($rootFull.TrimEnd('\'), $pathRoot.TrimEnd('\'), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to use a drive root as the disposable PostgreSQL root: $rootFull"
    }

    $repoRoot = Get-RepoRoot -StartPath $PSScriptRoot
    if ($rootFull.StartsWith($repoRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to put the disposable PostgreSQL instance inside the git worktree: $rootFull"
    }

    $bin = Join-Path $rootFull 'pgsql\bin'
    [pscustomobject]@{
        Owner        = $script:PgOwner
        Root         = $rootFull
        Port         = $Port
        DbName       = $DbName
        Bin          = $bin
        PgCtl        = Join-Path $bin 'pg_ctl.exe'
        InitDb       = Join-Path $bin 'initdb.exe'
        Psql         = Join-Path $bin 'psql.exe'
        Createdb     = Join-Path $bin 'createdb.exe'
        PgIsReady    = Join-Path $bin 'pg_isready.exe'
        PostgresExe  = Join-Path $bin 'postgres.exe'
        DataDir      = Join-Path $rootFull 'data'
        LogsDir      = Join-Path $rootFull 'logs'
        LogFile      = Join-Path $rootFull 'logs\postgres.log'
        DownloadDir  = Join-Path $rootFull 'download'
        ZipPath      = Join-Path $rootFull 'download\postgresql-16.10-1-windows-x64-binaries.zip'
        MarkerPath   = Join-Path $rootFull 'pg-owner.json'
        Connection   = "Host=127.0.0.1;Port=$Port;Database=$DbName;Username=mahjong_pg"
        RepoRoot     = $repoRoot
        TestProject  = Join-Path $repoRoot 'dotnet\tests\Mahjong.LobbyService.Tests\Mahjong.LobbyService.Tests.csproj'
        TestAssembly = Join-Path $repoRoot 'dotnet\tests\Mahjong.LobbyService.Tests\bin\Release\net8.0\Mahjong.LobbyService.Tests.dll'
        Migration    = Join-Path $repoRoot 'dotnet\src\Mahjong.LobbyService\Migrations\001_lobby.sql'
        EvidenceDir  = Join-Path $rootFull 'evidence'
    }
}

function Assert-PgOwned {
    param(
        [Parameter(Mandatory = $true)]$Context,
        [switch]$AllowMissingRoot
    )

    if (-not (Test-Path -LiteralPath $Context.Root)) {
        if ($AllowMissingRoot) {
            return $false
        }
        throw "The disposable PostgreSQL root does not exist: $($Context.Root)"
    }

    if (-not (Test-Path -LiteralPath $Context.MarkerPath)) {
        throw "Refusing to touch $($Context.Root): the ownership marker $($Context.MarkerPath) is missing. " +
              "This directory was not created by this script."
    }

    $marker = Get-Content -LiteralPath $Context.MarkerPath -Raw | ConvertFrom-Json
    if ($marker.owner -ne $script:PgOwner) {
        throw "Refusing to touch $($Context.Root): owner marker is '$($marker.owner)', expected '$($script:PgOwner)'."
    }
    if (-not [string]::Equals([System.IO.Path]::GetFullPath($marker.root), $Context.Root, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to touch $($Context.Root): the marker belongs to '$($marker.root)'."
    }
    if ($marker.port -ne $Context.Port) {
        throw "Refusing to touch $($Context.Root): the marker port $($marker.port) differs from $($Context.Port)."
    }

    return $true
}

function Initialize-PgRoot {
    param([Parameter(Mandatory = $true)]$Context)

    if (Test-Path -LiteralPath $Context.Root) {
        $items = @(Get-ChildItem -LiteralPath $Context.Root -Force)
        if ($items.Count -gt 0 -and -not (Test-Path -LiteralPath $Context.MarkerPath)) {
            throw "Refusing to use non-empty directory $($Context.Root) without the ownership marker."
        }
    } else {
        New-Item -ItemType Directory -Path $Context.Root -Force | Out-Null
    }

    if (-not (Test-Path -LiteralPath $Context.MarkerPath)) {
        $marker = [ordered]@{
            owner      = $script:PgOwner
            root       = $Context.Root
            port       = $Context.Port
            database   = $Context.DbName
            createdUtc = (Get-Date).ToUniversalTime().ToString('o')
        }
        $marker | ConvertTo-Json | Set-Content -LiteralPath $Context.MarkerPath -Encoding utf8
    }

    foreach ($dir in @($Context.LogsDir, $Context.DownloadDir, (Join-Path $Context.Root 'evidence'))) {
        if (-not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }
    }
}

function Test-TcpPortOpen {
    param([string]$HostName = '127.0.0.1', [int]$Port, [int]$TimeoutMs = 400)

    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $task = $client.ConnectAsync($HostName, $Port)
        if (-not $task.Wait($TimeoutMs)) {
            return $false
        }
        return $client.Connected
    }
    catch {
        return $false
    }
    finally {
        $client.Dispose()
    }
}

function Assert-PgPortAvailable {
    param([Parameter(Mandatory = $true)]$Context)

    if (Test-TcpPortOpen -Port $Context.Port) {
        throw "Port $($Context.Port) on 127.0.0.1 is already in use; refusing to attach to or disturb an existing server."
    }
}

function Test-PgReady {
    param([Parameter(Mandatory = $true)]$Context)

    if (-not (Test-Path -LiteralPath $Context.PgIsReady)) {
        return $false
    }
    & $Context.PgIsReady -h 127.0.0.1 -p $Context.Port -q 2>$null | Out-Null
    return ($LASTEXITCODE -eq 0)
}

function Start-PgServer {
    param(
        [Parameter(Mandatory = $true)]$Context,
        [int]$TimeoutSeconds = 90
    )

    # pg_ctl starts a long-lived postgres child. If that child inherited the
    # caller's stdout pipe (CI shells, parallel wrappers), the caller would wait
    # forever for EOF even though pg_ctl itself exited. A hidden new window plus
    # file redirection keeps postgres off the caller's console/pipe.
    $stamp = (Get-Date).ToString('yyyyMMdd-HHmmss-fff')
    $stdoutPath = Join-Path $Context.LogsDir ("pg_ctl-start-$stamp.out")
    $stderrPath = Join-Path $Context.LogsDir ("pg_ctl-start-$stamp.err")
    $arguments = "-D `"$($Context.DataDir)`" -l `"$($Context.LogFile)`" -w -t $TimeoutSeconds start"
    $process = Start-Process -FilePath $Context.PgCtl -ArgumentList $arguments `
        -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath `
        -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(($TimeoutSeconds + 30) * 1000)) {
        try { $process.Kill() } catch { }
        throw "pg_ctl start did not exit within $($TimeoutSeconds + 30) seconds. See $stdoutPath and $stderrPath."
    }
    $process.WaitForExit()
    $process.Refresh()
    $code = $process.ExitCode
    if ($null -eq $code) {
        # Windows PowerShell can report no exit code for a process that was
        # reaped by the OS. The readiness probe is the authoritative outcome.
        $code = if (Test-PgReady -Context $Context) { 0 } else { 1 }
    }
    foreach ($path in @($stdoutPath, $stderrPath)) {
        if (Test-Path -LiteralPath $path) {
            $text = Get-Content -LiteralPath $path -Raw
            if ($text) {
                $text.TrimEnd() -split "`r?`n" | ForEach-Object { Write-Host $_ }
            }
        }
    }
    return $code
}

function Invoke-NativeText {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$Arguments = @()
    )

    # Windows PowerShell turns a native stderr line into an ErrorRecord. With
    # $ErrorActionPreference = 'Stop' a harmless psql NOTICE would abort the
    # run. Capture both streams as text and let the caller decide by exit code.
    $previous = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = & $FilePath @Arguments 2>&1
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }

    return [pscustomobject]@{ ExitCode = $code; Output = @($output) }
}

function Invoke-PgPsql {
    param(
        [Parameter(Mandatory = $true)]$Context,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [switch]$AllowFailure
    )

    $all = @('-h', '127.0.0.1', '-p', "$($Context.Port)", '-U', 'mahjong_pg', '-d', $Context.DbName) + $Arguments
    $result = Invoke-NativeText -FilePath $Context.Psql -Arguments $all
    if ($result.Output.Count -gt 0) {
        $result.Output | ForEach-Object { Write-Host $_ }
    }
    if ($result.ExitCode -ne 0 -and -not $AllowFailure) {
        throw "psql failed with exit code $($result.ExitCode)."
    }
    return [pscustomobject]@{
        ExitCode = $result.ExitCode
        Output   = ($result.Output -join [Environment]::NewLine)
    }
}

function Write-Section {
    param([string]$Title)
    Write-Host ''
    Write-Host ('=' * 78)
    Write-Host $Title
    Write-Host ('=' * 78)
}
