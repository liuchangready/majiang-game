#requires -Version 5.1
<#
.SYNOPSIS
    Stops and deletes the disposable PostgreSQL cluster created by
    start-postgres.ps1. It refuses to delete anything that does not carry the
    matching ownership marker and absolute path.
#>
[CmdletBinding()]
param(
    [string]$Root = (Join-Path $env:TEMP 'mahjong-p53-pg'),
    [int]$Port = 55432,
    [string]$DbName = 'mahjong_lobby_test',
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'pg-common.ps1')

$context = New-PgContext -Root $Root -Port $Port -DbName $DbName
$owned = Assert-PgOwned -Context $context -AllowMissingRoot
if (-not $owned) {
    Write-Host "Nothing to clean up: $($context.Root) does not exist."
    return
}

if (Test-Path -LiteralPath $context.PgCtl) {
    if (Test-PgReady -Context $context) {
        Write-Section 'Stopping the disposable PostgreSQL instance'
        & $context.PgCtl -D $context.DataDir -w -t 60 stop -m fast
        if ($LASTEXITCODE -ne 0) {
            throw "pg_ctl stop failed with exit code $LASTEXITCODE; refusing to delete a running cluster."
        }
    } else {
        # A stale postmaster.pid is harmless for a throwaway cluster, but make
        # sure no postgres process is still attached to this data directory.
        $running = @(Get-CimInstance Win32_Process -Filter "Name = 'postgres.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.CommandLine -and $_.CommandLine.Contains($context.DataDir) })
        if ($running.Count -gt 0) {
            throw "A postgres.exe process still references $($context.DataDir); refusing to delete."
        }
    }
}

$resolved = [System.IO.Path]::GetFullPath($context.Root)
$marker = Get-Content -LiteralPath $context.MarkerPath -Raw | ConvertFrom-Json
$markerRoot = [System.IO.Path]::GetFullPath($marker.root)
if (-not [string]::Equals($resolved, $markerRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to delete ${resolved}: the ownership marker points to $markerRoot."
}
if ($resolved.Length -lt 10 -or $resolved -eq [System.IO.Path]::GetPathRoot($resolved).TrimEnd('\')) {
    throw "Refusing to delete suspiciously short/root path: $resolved"
}
if ($resolved.StartsWith($context.RepoRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to delete a path inside the git worktree: $resolved"
}

Write-Section 'Deleting the disposable PostgreSQL instance'
Write-Host "Verified ownership marker: $($context.MarkerPath)"
Write-Host "Target root: $resolved"
if ($WhatIf) {
    Write-Host 'WhatIf: no files were deleted.'
    return
}

Remove-Item -LiteralPath $resolved -Recurse -Force
if (Test-Path -LiteralPath $resolved) {
    throw "Cleanup failed; $resolved still exists."
}
Write-Host "Deleted $resolved"
