#requires -Version 5.1
<#
.SYNOPSIS
    Runs the ordinary lobby test suite (full or one class) against the
    disposable PostgreSQL instance. Cross-process restart phases are handled by
    the separate console probe (run-probe.ps1), so this runner never accepts or
    skips a phase-only test.
#>
[CmdletBinding()]
param(
    [ValidateSet('full', 'class')]
    [string]$Mode = 'full',
    [string]$Root = (Join-Path $env:TEMP 'mahjong-p53-pg'),
    [int]$Port = 55432,
    [string]$DbName = 'mahjong_lobby_test',
    [string]$Class = 'Mahjong.LobbyService.Tests.PostgresLobbyStoreTests',
    [string]$Method,
    [string]$Dotnet = 'D:\Unity\dotnet\dotnet.exe',
    [string]$LogPath,
    [string]$EvidencePath
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
if (-not (Test-Path -LiteralPath $context.TestAssembly)) {
    throw "The test assembly is missing. Build the test project first (verify-all.ps1 does this)."
}
if (-not $LogPath) {
    $LogPath = Join-Path $context.LogsDir ("tests-$Mode-" + (Get-Date).ToString('yyyyMMdd-HHmmss') + '.log')
}
if (-not $EvidencePath) {
    $EvidencePath = Join-Path $context.EvidenceDir 'postgres-evidence.jsonl'
}

$env:MAHJONG_LOBBY_TEST_POSTGRES = $context.Connection
$env:MAHJONG_LOBBY_PG_EVIDENCE = $EvidencePath
$env:MAHJONG_LOBBY_PG_ROOT = $context.Root
$env:MAHJONG_LOBBY_PG_DATA = $context.DataDir

$arguments = @()
switch ($Mode) {
    'full' {
        $arguments = @('test', $context.TestProject, '-c', 'Release', '--nologo', '--no-build')
    }
    'class' {
        $arguments = @($context.TestAssembly, '-class', $Class, '-noColor', '-noLogo')
        if ($Method) {
            $arguments += @('-method', $Method)
        }
    }
}

Write-Section "Running lobby tests (mode=$Mode)"
Write-Host "Command: $Dotnet $($arguments -join ' ')"
Write-Host "Connection: $($context.Connection) (loopback test database)"
Write-Host "Evidence:   $EvidencePath"
Write-Host "Log:        $LogPath"

$previousLanguage = $env:DOTNET_CLI_UI_LANGUAGE
$previousErrorAction = $ErrorActionPreference
try {
    # English gives a stable summary; the parser below also accepts the
    # localized Chinese summary in case the host forces its UI culture.
    $env:DOTNET_CLI_UI_LANGUAGE = 'en'
    $ErrorActionPreference = 'Continue'
    & $Dotnet @arguments 2>&1 | Tee-Object -FilePath $LogPath
    $exitCode = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $previousErrorAction
    if ($null -eq $previousLanguage) {
        Remove-Item Env:\DOTNET_CLI_UI_LANGUAGE -ErrorAction SilentlyContinue
    } else {
        $env:DOTNET_CLI_UI_LANGUAGE = $previousLanguage
    }
}

$summary = [ordered]@{
    mode        = $Mode
    exitCode    = $exitCode
    logPath     = $LogPath
    evidence    = $EvidencePath
    total       = $null
    errors      = $null
    failed      = $null
    skipped     = $null
    passed      = $null
    timestamp   = (Get-Date).ToUniversalTime().ToString('o')
}

$summaryText = Get-Content -LiteralPath $LogPath -Raw
$match = [regex]::Match($summaryText, 'Total:\s*(\d+),\s*Errors:\s*(\d+),\s*Failed:\s*(\d+),\s*Skipped:\s*(\d+)')
if ($match.Success) {
    $summary.total = [int]$match.Groups[1].Value
    $summary.errors = [int]$match.Groups[2].Value
    $summary.failed = [int]$match.Groups[3].Value
    $summary.skipped = [int]$match.Groups[4].Value
    $summary.passed = $summary.total - $summary.errors - $summary.failed - $summary.skipped
} else {
    # VSTest / dotnet test: Passed! - Failed: 0, Passed: 89, Skipped: 0, Total: 89
    $match = [regex]::Match(
        $summaryText,
        'Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)')
    if ($match.Success) {
        $summary.failed = [int]$match.Groups[1].Value
        $summary.passed = [int]$match.Groups[2].Value
        $summary.skipped = [int]$match.Groups[3].Value
        $summary.total = [int]$match.Groups[4].Value
        $summary.errors = 0
    } else {
        # zh-CN: 失败: 0，通过: 89，已跳过: 0，总计: 89
        $match = [regex]::Match(
            $summaryText,
            '失败:\s*(\d+)[，,]\s*通过:\s*(\d+)[，,]\s*已跳过:\s*(\d+)[，,]\s*总计:\s*(\d+)')
        if ($match.Success) {
            $summary.failed = [int]$match.Groups[1].Value
            $summary.passed = [int]$match.Groups[2].Value
            $summary.skipped = [int]$match.Groups[3].Value
            $summary.total = [int]$match.Groups[4].Value
            $summary.errors = 0
        }
    }
}

$summary | ConvertTo-Json | Set-Content -LiteralPath ($LogPath + '.summary.json') -Encoding utf8

Write-Host ''
Write-Host "Exit code: $exitCode"
Write-Host "Counts: Total=$($summary.total) Passed=$($summary.passed) Failed=$($summary.failed) Errors=$($summary.errors) Skipped=$($summary.skipped)"

if ($exitCode -ne 0) {
    throw "The test run failed with exit code $exitCode. Raw log: $LogPath"
}
if ($null -eq $summary.total) {
    throw "Could not parse the xUnit summary from $LogPath."
}
if ($summary.skipped -gt 0) {
    throw "Unexpected skipped tests ($($summary.skipped)) in mode $Mode. Raw log: $LogPath"
}
