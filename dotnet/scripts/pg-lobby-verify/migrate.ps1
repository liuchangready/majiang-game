#requires -Version 5.1
<#
.SYNOPSIS
    Applies the P5.3 lobby migration to the disposable PostgreSQL database and
    re-applies it to prove that the deployment step is repeatable.
#>
[CmdletBinding()]
param(
    [string]$Root = (Join-Path $env:TEMP 'mahjong-p53-pg'),
    [int]$Port = 55432,
    [string]$DbName = 'mahjong_lobby_test',
    [int]$Times = 2
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'pg-common.ps1')

$context = New-PgContext -Root $Root -Port $Port -DbName $DbName
Assert-PgOwned -Context $context | Out-Null
if (-not (Test-TcpPortOpen -Port $context.Port)) {
    throw "PostgreSQL is not listening on 127.0.0.1:$($context.Port). Run start-postgres.ps1 first."
}
$identity = Get-PgServerIdentity -Context $context -Database 'postgres'
Assert-PgServerIsOwned -Context $context -Identity $identity | Out-Null
if (-not (Test-PgDatabaseExists -Context $context)) {
    throw "Database '$($context.DbName)' does not exist on the verified disposable cluster."
}
Assert-PgDatabaseMarker -Context $context | Out-Null
if (-not (Test-PgReady -Context $context)) {
    throw "PostgreSQL is not ready on 127.0.0.1:$($context.Port). Run start-postgres.ps1 first."
}
if (-not (Test-Path -LiteralPath $context.Migration)) {
    throw "The lobby migration was not found at $($context.Migration)."
}
if ($Times -lt 1 -or $Times -gt 10) {
    throw "Times must be between 1 and 10."
}

for ($run = 1; $run -le $Times; $run++) {
    Write-Section "Applying 001_lobby.sql (run $run of $Times)"
    $result = Invoke-PgPsql -Context $context -Arguments @('-v', 'ON_ERROR_STOP=1', '-f', $context.Migration)
    if ($result.ExitCode -ne 0) {
        throw "Migration run $run failed with exit code $($result.ExitCode)."
    }
    Write-Host "Migration run ${run}: exit code 0"
}

Write-Section 'Schema verification'
$tableCount = & $context.Psql -h 127.0.0.1 -p $context.Port -U mahjong_pg -d $context.DbName -tAc `
    "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'lobby'"
$indexes = & $context.Psql -h 127.0.0.1 -p $context.Port -U mahjong_pg -d $context.DbName -tAc `
    "SELECT indexname FROM pg_indexes WHERE schemaname = 'lobby' ORDER BY indexname"
$constraint = & $context.Psql -h 127.0.0.1 -p $context.Port -U mahjong_pg -d $context.DbName -tAc `
    "SELECT indexdef FROM pg_indexes WHERE schemaname = 'lobby' AND indexname = 'uq_assignment_active_account'"
Write-Host "lobby tables: $(($tableCount -join '').Trim())"
Write-Host 'lobby indexes:'
$indexes | Where-Object { $_ } | ForEach-Object { Write-Host "  $_" }
Write-Host "active-account fence: $(($constraint -join ' ').Trim())"

Write-Host ''
Write-Host "Migration applied $Times time(s) successfully (exit code 0 each run)."
