#requires -Version 5.1
<#
.SYNOPSIS
    End-to-end P5.3 lobby PostgreSQL verification:
      start disposable loopback cluster -> apply migration twice -> build ->
      full lobby test suite -> restart-write -> real crash restart ->
      restart-read -> archive raw logs -> delete owned cluster.

    The restart phases run in separate OS processes. The database is stopped
    with "immediate" (crash-like) shutdown between them, so a pass cannot come
    from an in-memory store surviving in one process.
#>
[CmdletBinding()]
param(
    [string]$Root = (Join-Path $env:TEMP 'mahjong-p53-pg'),
    [int]$Port = 55432,
    [string]$DbName = 'mahjong_lobby_test',
    [string]$ZipPath,
    [string]$Dotnet = 'D:\Unity\dotnet\dotnet.exe',
    [switch]$KeepFiles
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'pg-common.ps1')

$context = New-PgContext -Root $Root -Port $Port -DbName $DbName
$results = New-Object System.Collections.Generic.List[object]
$archivePath = Join-Path (Split-Path -Parent $context.Root) ('p53-pg-evidence-' + (Get-Date).ToString('yyyyMMdd-HHmmss'))
$transcriptPath = $null
$failed = $false

function Add-Result {
    param([string]$Name, [string]$Status, [string]$Command, [string]$Detail, $Data)
    $results.Add([pscustomobject]@{
            step    = $Name
            status  = $Status
            command = $Command
            detail  = $Detail
            data    = $Data
        })
    Write-Host "[$Status] $Name - $Detail"
}

try {
    Initialize-PgRoot -Context $context
    $transcriptPath = Join-Path $context.LogsDir ('verify-all-' + (Get-Date).ToString('yyyyMMdd-HHmmss') + '.log')
    Start-Transcript -Path $transcriptPath -Force | Out-Null

    Write-Section 'Step 1/8: start disposable PostgreSQL'
    $startScript = Join-Path $PSScriptRoot 'start-postgres.ps1'
    $migrateScript = Join-Path $PSScriptRoot 'migrate.ps1'
    $runScript = Join-Path $PSScriptRoot 'run-tests.ps1'
    $probeScript = Join-Path $PSScriptRoot 'run-probe.ps1'
    $restartScript = Join-Path $PSScriptRoot 'restart-postgres.ps1'
    $cleanupScript = Join-Path $PSScriptRoot 'cleanup-postgres.ps1'

    & $startScript -Root $context.Root -Port $context.Port -DbName $context.DbName -ZipPath $ZipPath
    Add-Result -Name 'start-postgres' -Status 'PASS' `
        -Command "start-postgres.ps1 -Root $($context.Root) -Port $($context.Port) -DbName $($context.DbName)" `
        -Detail 'loopback-only cluster ready'

    Write-Section 'Step 2/8: apply migration twice'
    & $migrateScript -Root $context.Root -Port $context.Port -DbName $context.DbName -Times 2
    Add-Result -Name 'migrate-x2' -Status 'PASS' `
        -Command "migrate.ps1 -Times 2" -Detail '001_lobby.sql applied twice with exit code 0'

    Write-Section 'Step 3/8: build lobby test project and restart probe'
    $testBuildCommand = "$Dotnet build $($context.TestProject) -c Release --nologo"
    $probeBuildCommand = "$Dotnet build $($context.ProbeProject) -c Release --nologo"
    Write-Host "Command: $testBuildCommand"
    Write-Host "Command: $probeBuildCommand"
    $previousErrorAction = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $Dotnet build $context.TestProject -c Release --nologo 2>&1 | Tee-Object -FilePath (Join-Path $context.LogsDir 'build-test.log')
        $testBuildExitCode = $LASTEXITCODE
        & $Dotnet build $context.ProbeProject -c Release --nologo 2>&1 | Tee-Object -FilePath (Join-Path $context.LogsDir 'build-probe.log')
        $probeBuildExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorAction
    }
    if ($testBuildExitCode -ne 0) {
        throw "The test project build failed with exit code $testBuildExitCode."
    }
    if ($probeBuildExitCode -ne 0) {
        throw "The restart probe build failed with exit code $probeBuildExitCode."
    }
    Add-Result -Name 'build' -Status 'PASS' -Command "$testBuildCommand; $probeBuildCommand" `
        -Detail 'test project + restart probe: 0 warnings, 0 errors expected (see build-test.log/build-probe.log)'

    Write-Section 'Step 4/8: full lobby suite against real PostgreSQL'
    $fullLog = Join-Path $context.LogsDir 'full-suite.log'
    & $runScript -Mode full -Root $context.Root -Port $context.Port -DbName $context.DbName `
        -Dotnet $Dotnet -LogPath $fullLog
    $fullSummary = Get-Content -LiteralPath ($fullLog + '.summary.json') -Raw | ConvertFrom-Json
    Add-Result -Name 'full-suite' -Status 'PASS' `
        -Command "$Dotnet test $($context.TestProject) -c Release --nologo --no-build" `
        -Detail ("Total={0} Passed={1} Failed={2} Errors={3} Skipped={4}" -f $fullSummary.total, $fullSummary.passed, $fullSummary.failed, $fullSummary.errors, $fullSummary.skipped) `
        -Data $fullSummary
    if ($fullSummary.failed -ne 0 -or $fullSummary.errors -ne 0 -or $fullSummary.total -lt 1) {
        throw "The full suite did not satisfy the expected outcome."
    }
    if ($fullSummary.skipped -ne 0) {
        throw "The full suite skipped $($fullSummary.skipped) tests; the ordinary Lobby suite must have zero skips."
    }

    Write-Section 'Step 5/8: restart-write probe (persist receipt + consumed ticket)'
    $writeLog = Join-Path $context.LogsDir 'restart-write-probe.log'
    & $probeScript -Phase write -Root $context.Root -Port $context.Port -DbName $context.DbName `
        -Dotnet $Dotnet -LogPath $writeLog
    $writeSummary = Get-Content -LiteralPath ($writeLog + '.summary.json') -Raw | ConvertFrom-Json
    if ($writeSummary.result -ne 'PASS' -or $writeSummary.exitCode -ne 0) {
        throw "The restart-write probe did not pass."
    }
    Add-Result -Name 'restart-write-probe' -Status 'PASS' `
        -Command "$Dotnet $($context.ProbeAssembly) write" `
        -Detail ("ProbeResult={0} ExitCode={1}" -f $writeSummary.result, $writeSummary.exitCode) `
        -Data $writeSummary

    Write-Section 'Step 6/8: crash restart of the real PostgreSQL process'
    & $restartScript -Root $context.Root -Port $context.Port -DbName $context.DbName -Mode immediate
    $restartData = Get-Content -LiteralPath (Join-Path $context.LogsDir 'restart.json') -Raw | ConvertFrom-Json
    Add-Result -Name 'postgres-immediate-restart' -Status 'PASS' `
        -Command "restart-postgres.ps1 -Mode immediate" `
        -Detail ("postmaster {0} -> {1}" -f $restartData.before, $restartData.after) `
        -Data $restartData

    Write-Section 'Step 7/8: restart-read probe in a new process after the crash restart'
    $readLog = Join-Path $context.LogsDir 'restart-read-probe.log'
    & $probeScript -Phase read -Root $context.Root -Port $context.Port -DbName $context.DbName `
        -Dotnet $Dotnet -LogPath $readLog
    $readSummary = Get-Content -LiteralPath ($readLog + '.summary.json') -Raw | ConvertFrom-Json
    if ($readSummary.result -ne 'PASS' -or $readSummary.exitCode -ne 0) {
        throw "The restart-read probe did not pass."
    }
    Add-Result -Name 'restart-read-probe' -Status 'PASS' `
        -Command "$Dotnet $($context.ProbeAssembly) read" `
        -Detail ("ProbeResult={0} ExitCode={1}" -f $readSummary.result, $readSummary.exitCode) `
        -Data $readSummary

    Write-Section 'Step 8/8: durable row snapshot'
    $snapshot = & $context.Psql -h 127.0.0.1 -p $context.Port -U mahjong_pg -d $context.DbName -tAc @'
SELECT
  (SELECT count(*) FROM lobby.account) || ' accounts, ' ||
  (SELECT count(*) FROM lobby.room) || ' rooms, ' ||
  (SELECT count(*) FROM lobby.assignment) || ' assignments, ' ||
  (SELECT count(*) FROM lobby.request_receipt) || ' receipts, ' ||
  (SELECT count(*) FROM lobby.table_ticket_consumption) || ' consumptions'
'@ 2>&1
    $snapshotText = ($snapshot -join ' ').Trim()
    Write-Host $snapshotText
    Add-Result -Name 'durable-snapshot' -Status 'PASS' -Command 'SELECT counts FROM lobby.*' -Detail $snapshotText
}
catch {
    $failed = $true
    Add-Result -Name 'verify-all' -Status 'FAIL' -Command 'verify-all.ps1' -Detail $_.Exception.Message
}
finally {
    if ($transcriptPath) {
        try { Stop-Transcript | Out-Null } catch { }
    }

    Write-Section 'Archiving raw evidence'
    try {
        if (Test-Path -LiteralPath $context.Root) {
            New-Item -ItemType Directory -Path $archivePath -Force | Out-Null
            if (Test-Path -LiteralPath $context.LogsDir) {
                Copy-Item -LiteralPath $context.LogsDir -Destination $archivePath -Recurse -Force
            }
            if (Test-Path -LiteralPath $context.EvidenceDir) {
                Copy-Item -LiteralPath $context.EvidenceDir -Destination $archivePath -Recurse -Force
            }
            Write-Host "Evidence archived to $archivePath"
        }
    }
    catch {
        $failed = $true
        Add-Result -Name 'archive-evidence' -Status 'FAIL' -Command 'Copy-Item' -Detail $_.Exception.Message
    }

    if ($KeepFiles) {
        Write-Host ''
        Write-Host "Keeping the disposable PostgreSQL instance at $($context.Root) (requested)."
    } else {
        Write-Section 'Cleaning up the disposable PostgreSQL instance'
        try {
            & $cleanupScript -Root $context.Root -Port $context.Port -DbName $context.DbName
            Add-Result -Name 'cleanup' -Status 'PASS' -Command 'cleanup-postgres.ps1' -Detail 'owned cluster deleted'
        }
        catch {
            $failed = $true
            Add-Result -Name 'cleanup' -Status 'FAIL' -Command 'cleanup-postgres.ps1' -Detail $_.Exception.Message
        }
    }

    # Write the summary after cleanup so the archive records the final cleanup
    # outcome too (the transcript cannot stay open across deleting its own dir).
    try {
        New-Item -ItemType Directory -Path $archivePath -Force | Out-Null
        $results | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $archivePath 'verify-all-summary.json') -Encoding utf8
    }
    catch {
        $failed = $true
        Write-Host "Could not write the final summary to ${archivePath}: $($_.Exception.Message)"
    }

    Write-Section 'Verification summary'
    $results | Format-Table -AutoSize step, status, detail -Wrap | Out-String | Write-Host
    Write-Host "Evidence archive: $archivePath"
    if ($failed) {
        Write-Host 'RESULT: FAIL'
        exit 1
    }
    Write-Host 'RESULT: PASS'
    exit 0
}
