#requires -Version 5.1
<#
.SYNOPSIS
    Runs one phase of the standalone cross-process PostgreSQL restart probe.
    This is a console program, not an xUnit test: the ordinary Lobby test
    assembly has no phase-only skips. The runner executes write -> immediate
    restart -> read in separate OS processes and fails on any non-zero exit.
#>
[CmdletBinding()]
param(
    [ValidateSet('write', 'read')]
    [string]$Phase = 'write',
    [string]$Root = (Join-Path $env:TEMP 'mahjong-p53-pg'),
    [int]$Port = 55432,
    [string]$DbName = 'mahjong_lobby_test',
    [string]$Dotnet = 'D:\Unity\dotnet\dotnet.exe',
    [string]$LogPath,
    [string]$EvidencePath,
    [string]$HandoffPath
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
if (-not (Test-Path -LiteralPath $context.ProbeAssembly)) {
    throw "The restart probe is missing. Build Mahjong.LobbyService.PostgresProbe first."
}
if (-not $LogPath) {
    $LogPath = Join-Path $context.LogsDir ("probe-$Phase-" + (Get-Date).ToString('yyyyMMdd-HHmmss') + '.log')
}
if (-not $EvidencePath) {
    $EvidencePath = Join-Path $context.EvidenceDir 'postgres-evidence.jsonl'
}
if (-not $HandoffPath) {
    $HandoffPath = Join-Path $context.EvidenceDir 'restart-handoff.json'
}

$env:MAHJONG_LOBBY_TEST_POSTGRES = $context.Connection
$env:MAHJONG_LOBBY_PG_EVIDENCE = $EvidencePath
$env:MAHJONG_LOBBY_PG_HANDOFF = $HandoffPath
$env:MAHJONG_LOBBY_PG_ROOT = $context.Root
$env:MAHJONG_LOBBY_PG_DATA = $context.DataDir

Write-Section "Running PostgreSQL restart probe (phase=$Phase)"
Write-Host "Command: $Dotnet $($context.ProbeAssembly) $Phase"
Write-Host "Connection: $($context.Connection) (loopback test database)"
Write-Host "Evidence:   $EvidencePath"
Write-Host "Handoff:    $HandoffPath"
Write-Host "Log:        $LogPath"

$previousLanguage = $env:DOTNET_CLI_UI_LANGUAGE
$previousErrorAction = $ErrorActionPreference
try {
    $env:DOTNET_CLI_UI_LANGUAGE = 'en'
    $ErrorActionPreference = 'Continue'
    & $Dotnet $context.ProbeAssembly $Phase 2>&1 | Tee-Object -FilePath $LogPath
    $exitCode = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $previousErrorAction
    if ($null -eq $previousLanguage) {
        Remove-Item Env:\DOTNET_CLI_UI_LANGUAGE -ErrorAction SilentlyContinue
    }
    else {
        $env:DOTNET_CLI_UI_LANGUAGE = $previousLanguage
    }
}

$text = Get-Content -LiteralPath $LogPath -Raw
$passed = $text -match ('PROBE_RESULT: PASS phase=' + [regex]::Escape($Phase) + '\b')
$summary = [ordered]@{
    phase     = $Phase
    exitCode  = $exitCode
    logPath   = $LogPath
    evidence  = $EvidencePath
    handoff   = $HandoffPath
    result    = if ($exitCode -eq 0 -and $passed) { 'PASS' } else { 'FAIL' }
    timestamp = (Get-Date).ToUniversalTime().ToString('o')
}
$summary | ConvertTo-Json | Set-Content -LiteralPath ($LogPath + '.summary.json') -Encoding utf8

Write-Host ''
Write-Host "Exit code: $exitCode"
Write-Host "Probe result: $($summary.result)"

if ($exitCode -ne 0) {
    throw "The $Phase probe failed with exit code $exitCode. Raw log: $LogPath"
}
if (-not $passed) {
    throw "The $Phase probe did not print its PASS sentinel. Raw log: $LogPath"
}
