#requires -Version 5.1
<#
.SYNOPSIS
    Stops and deletes the disposable PostgreSQL cluster created by
    start-postgres.ps1. It refuses to touch a listener whose data_directory is
    not this cluster, and -WhatIf performs no stop or deletion.

    All mutating operations (pg_ctl stop and Remove-Item) are gated behind the
    WhatIf check, after read-only ownership/path validation.
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

# Read-only validation first: absolute path, local ownership marker, and (if a
# listener exists) the server's own data_directory. A foreign listener must
# never be stopped, and no port-only readiness check is sufficient.
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

$listenerPresent = Test-TcpPortOpen -Port $context.Port
if ($listenerPresent) {
    $identity = Get-PgServerIdentity -Context $context -Database 'postgres'
    Assert-PgServerIsOwned -Context $context -Identity $identity | Out-Null
}

Write-Section 'Disposable PostgreSQL cleanup plan'
Write-Host "Verified ownership marker: $($context.MarkerPath)"
Write-Host "Target root: $resolved"
Write-Host "Listener on 127.0.0.1:$($context.Port): $listenerPresent"

if ($WhatIf) {
    Write-Host 'WhatIf: no stop and no files were deleted.'
    return
}

if ($listenerPresent) {
    if (Test-PgReady -Context $context) {
        Write-Section 'Stopping the verified disposable PostgreSQL instance'
        & $context.PgCtl -D $context.DataDir -w -t 60 stop -m fast
        if ($LASTEXITCODE -ne 0) {
            throw "pg_ctl stop failed with exit code $LASTEXITCODE; refusing to delete a running cluster."
        }
    }
    else {
        # A listener exists but is not ready. Its data_directory was already
        # verified above; still avoid deleting while a process references it.
        $running = @(Get-PgProcessIds -Context $context)
        if ($running.Count -gt 0) {
            throw "A postgres.exe process still references $($context.DataDir); refusing to delete."
        }
    }
}
else {
    $running = @(Get-PgProcessIds -Context $context)
    if ($running.Count -gt 0) {
        throw "A postgres.exe process still references $($context.DataDir); refusing to delete."
    }
}

Write-Section 'Deleting the disposable PostgreSQL instance'
Remove-Item -LiteralPath $resolved -Recurse -Force
if (Test-Path -LiteralPath $resolved) {
    throw "Cleanup failed; $resolved still exists."
}
Write-Host "Deleted $resolved"
