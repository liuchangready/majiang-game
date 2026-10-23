#requires -Version 5.1
<#
.SYNOPSIS
    Stops and starts the disposable PostgreSQL cluster. Mode "immediate"
    simulates a crash (no clean shutdown) so the following start exercises WAL
    crash recovery; mode "fast" is a normal shutdown.
#>
[CmdletBinding()]
param(
    [ValidateSet('immediate', 'fast', 'smart')]
    [string]$Mode = 'immediate',
    [string]$Root = (Join-Path $env:TEMP 'mahjong-p53-pg'),
    [int]$Port = 55432,
    [string]$DbName = 'mahjong_lobby_test'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'pg-common.ps1')

$context = New-PgContext -Root $Root -Port $Port -DbName $DbName
Assert-PgOwned -Context $context | Out-Null
if (-not (Test-PgReady -Context $context)) {
    throw "PostgreSQL is not ready on 127.0.0.1:$($context.Port); nothing to restart."
}

$before = & $context.Psql -h 127.0.0.1 -p $context.Port -U mahjong_pg -d $context.DbName -tAc `
    'SELECT pg_postmaster_start_time()::text' 2>&1
$beforeText = ($before -join '').Trim()
$logOffset = 0
if (Test-Path -LiteralPath $context.LogFile) {
    $logOffset = (Get-Item -LiteralPath $context.LogFile).Length
}

Write-Section "Stopping PostgreSQL (mode=$Mode)"
& $context.PgCtl -D $context.DataDir -w -t 60 stop -m $Mode
if ($LASTEXITCODE -ne 0) {
    throw "pg_ctl stop -m $Mode failed with exit code $LASTEXITCODE."
}

$deadline = (Get-Date).AddSeconds(30)
while (Test-PgReady -Context $context) {
    if ((Get-Date) -gt $deadline) {
        throw "PostgreSQL still accepts connections on 127.0.0.1:$($context.Port) after the stop."
    }
    Start-Sleep -Milliseconds 200
}
Write-Host "Server stopped (was running since $beforeText)."

Write-Section 'Starting PostgreSQL again'
$startCode = Start-PgServer -Context $context -TimeoutSeconds 60
if ($startCode -ne 0) {
    throw "pg_ctl start failed with exit code $startCode."
}

$deadline = (Get-Date).AddSeconds(60)
while (-not (Test-PgReady -Context $context)) {
    if ((Get-Date) -gt $deadline) {
        throw "PostgreSQL did not become ready on 127.0.0.1:$($context.Port) after the restart."
    }
    Start-Sleep -Milliseconds 250
}

$after = & $context.Psql -h 127.0.0.1 -p $context.Port -U mahjong_pg -d $context.DbName -tAc `
    'SELECT pg_postmaster_start_time()::text' 2>&1
$afterText = ($after -join '').Trim()
if ($beforeText -eq $afterText) {
    throw "The postmaster start time did not change; the server was not actually restarted."
}

$topology = & $context.Psql -h 127.0.0.1 -p $context.Port -U mahjong_pg -d $context.DbName -tAc `
    "SELECT current_setting('listen_addresses') || '|' || current_setting('port') || '|' || current_setting('data_directory')" 2>&1

$recoveryLines = @()
if (Test-Path -LiteralPath $context.LogFile) {
    $stream = [System.IO.File]::Open($context.LogFile, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        if ($logOffset -lt $stream.Length) {
            $stream.Position = $logOffset
            $reader = New-Object System.IO.StreamReader($stream)
            $newText = $reader.ReadToEnd()
            $recoveryLines = @($newText -split "`r?`n" | Where-Object {
                    $_ -match 'not properly shut down|redo starts at|database system is ready|automatic recovery|consistent recovery state'
                })
        }
    }
    finally {
        $stream.Dispose()
    }
}

Write-Section 'Restart result'
Write-Host "Postmaster before: $beforeText"
Write-Host "Postmaster after:  $afterText"
Write-Host "Topology:          $(($topology -join ' ').Trim())"
Write-Host 'Recovery log lines:'
if ($recoveryLines.Count -eq 0) {
    Write-Host '  (none captured; inspect the log)'
} else {
    $recoveryLines | ForEach-Object { Write-Host "  $_" }
}

$restartRecord = [ordered]@{
    mode            = $Mode
    before          = $beforeText
    after           = $afterText
    topology        = ($topology -join ' ').Trim()
    recoveryLines   = $recoveryLines
    timestamp       = (Get-Date).ToUniversalTime().ToString('o')
}
$restartRecord | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $context.LogsDir 'restart.json') -Encoding utf8
if ($recoveryLines.Count -gt 0) {
    $recoveryLines | Set-Content -LiteralPath (Join-Path $context.LogsDir 'restart-recovery.log') -Encoding utf8
}
