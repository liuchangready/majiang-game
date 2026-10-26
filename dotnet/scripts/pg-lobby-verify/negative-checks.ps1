#requires -Version 5.1
<#
.SYNOPSIS
    Negative verification for the disposable PostgreSQL harness:
      R1: a stopped cluster B must not attach to an unrelated running cluster A
          merely because B's configured port is occupied by A.
      R2: cleanup -WhatIf must not stop or delete an active owned cluster.

    Both clusters are created by this script from the official pinned binaries.
    No user instance is used. Evidence stays under the negative root (A and B
    are deleted, the parent/evidence directory is kept).
#>
[CmdletBinding()]
param(
    [string]$Root = (Join-Path $env:TEMP 'mahjong-p53-pg-negative'),
    # Both clusters intentionally use the same configured port: B is started
    # and stopped first, then A takes the port. B's local marker therefore has
    # the same port and must be refused by the server data_directory check.
    [int]$PortA = 55436,
    [string]$DbA = 'mahjong_lobby_test_neg_a',
    [string]$DbB = 'mahjong_lobby_test_neg_b',
    [string]$ZipPath
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'pg-common.ps1')

$rootA = Join-Path $Root 'a'
$rootB = Join-Path $Root 'b'
$contextA = New-PgContext -Root $rootA -Port $PortA -DbName $DbA
$contextB = New-PgContext -Root $rootB -Port $PortA -DbName $DbB
$evidencePath = Join-Path $Root 'negative-evidence.jsonl'
$logDir = Join-Path $Root 'logs'
$zipParam = @{}
if ($ZipPath) {
    $zipParam['ZipPath'] = $ZipPath
}
$results = New-Object System.Collections.Generic.List[object]
$failed = $false
$cleanedA = $false
$cleanedB = $false

New-Item -ItemType Directory -Path $Root -Force | Out-Null
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
Remove-Item -LiteralPath $evidencePath -Force -ErrorAction SilentlyContinue

function Add-Evidence {
    param([string]$Event, [hashtable]$Fields = @{})
    $payload = [ordered]@{
        ts    = (Get-Date).ToUniversalTime().ToString('o')
        event = $Event
    }
    foreach ($key in $Fields.Keys) {
        $payload[$key] = $Fields[$key]
    }
    $payload | ConvertTo-Json -Compress | Add-Content -LiteralPath $evidencePath -Encoding utf8
}

function Add-Result {
    param([string]$Name, [string]$Status, [string]$Detail, $Data = $null)
    $results.Add([pscustomobject]@{
            check  = $Name
            status = $Status
            detail = $Detail
            data   = $Data
        })
    Write-Host "[$Status] $Name - $Detail"
    Add-Evidence -Event $Name -Fields @{ status = $Status; detail = $Detail; data = $Data }
}

function Assert-Check {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw "NEGATIVE_CHECK_FAILED: $Message"
    }
}

function Invoke-ScriptCapture {
    param([string]$ScriptPath, [hashtable]$Parameters, [string]$LogName)

    $code = 0
    $output = @()
    try {
        $output = @(& $ScriptPath @Parameters 2>&1)
    }
    catch {
        $code = 1
        $output = @($_.Exception.Message)
    }

    $text = ($output | ForEach-Object { "$_" }) -join [Environment]::NewLine
    Set-Content -LiteralPath (Join-Path $logDir ($LogName + '.log')) -Value $text -Encoding utf8
    return [pscustomobject]@{ ExitCode = $code; Output = $text }
}

function Get-PgScalar {
    param($Context, [string]$Sql)
    $result = Invoke-NativeText -FilePath $Context.Psql -Arguments @(
        '-h', '127.0.0.1', '-p', "$($Context.Port)", '-U', 'mahjong_pg',
        '-d', $Context.DbName, '-tAc', $Sql)
    if ($result.ExitCode -ne 0) {
        throw "psql scalar failed: $($result.Output -join ' ')"
    }
    return (($result.Output -join '').Trim())
}

function Get-A-Integrity {
    $identity = Get-PgServerIdentity -Context $contextA -Database 'postgres'
    Assert-PgServerIsOwned -Context $contextA -Identity $identity | Out-Null
    $marker = Get-PgDatabaseMarker -Context $contextA
    $pids = @(Get-PgProcessIds -Context $contextA | Sort-Object)
    $databases = Get-PgScalar -Context $contextA 'SELECT string_agg(datname, '','' ORDER BY datname) FROM pg_database'
    $markerRows = Get-PgScalar -Context $contextA 'SELECT count(*) FROM lobby_verify.meta'
    $markerCreated = Get-PgScalar -Context $contextA 'SELECT created_at::text FROM lobby_verify.meta'
    return [pscustomobject]@{
        startedAt     = [string]$identity.startedAt
        pids          = ($pids -join ',')
        markerOwner   = [string]$marker.owner
        markerRoot    = Normalize-PgPath ([string]$marker.root)
        databases     = $databases
        markerRows    = $markerRows
        markerCreated = $markerCreated
    }
}

function Assert-A-Intact {
    param($Before, [string]$When)
    $after = Get-A-Integrity
    Assert-Check ($after.startedAt -eq $Before.startedAt) "${When}: A postmaster start time changed"
    Assert-Check ($after.pids -eq $Before.pids) "${When}: A process IDs changed"
    Assert-Check ($after.markerOwner -eq 'mahjong-p53-pg') "${When}: A marker owner changed"
    Assert-Check ($after.markerRoot -eq (Normalize-PgPath $contextA.Root)) "${When}: A marker root changed"
    Assert-Check ($after.databases -eq $Before.databases) "${When}: A database list changed"
    Assert-Check ($after.markerRows -eq $Before.markerRows) "${When}: A marker row count changed"
    Assert-Check ($after.markerCreated -eq $Before.markerCreated) "${When}: A marker created_at was overwritten"
    Assert-Check (Test-PgReady -Context $contextA) "${When}: A is no longer ready"
}

function Get-FileHashes {
    param([string[]]$Paths)
    $map = [ordered]@{}
    foreach ($path in $Paths) {
        if (Test-Path -LiteralPath $path) {
            $map[$path] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        }
        else {
            $map[$path] = 'MISSING'
        }
    }
    return $map
}

try {
    # Clean any previous roots owned by this harness. If a listener exists it
    # must be one of the two roots; stop that root first, then remove the other.
    if ((Test-Path -LiteralPath $rootA) -or (Test-Path -LiteralPath $rootB)) {
        $cleanupOrder = @()
        if (Test-TcpPortOpen -Port $PortA) {
            $probe = if (Test-Path -LiteralPath $contextA.Psql) { $contextA } else { $contextB }
            $identity = Get-PgServerIdentity -Context $probe -Database 'postgres'
            $actualData = Normalize-PgPath ([string]$identity.dataDirectory)
            if ($actualData -eq (Normalize-PgPath $contextA.DataDir)) {
                $cleanupOrder = @('a', 'b')
            }
            elseif ($actualData -eq (Normalize-PgPath $contextB.DataDir)) {
                $cleanupOrder = @('b', 'a')
            }
            else {
                throw "Refusing to clean: listener data_directory '$actualData' belongs to neither test root."
            }
        }
        else {
            $cleanupOrder = @('a', 'b')
        }

        foreach ($name in $cleanupOrder) {
            $pair = if ($name -eq 'a') { @{ Root = $rootA; Db = $DbA } } else { @{ Root = $rootB; Db = $DbB } }
            if (Test-Path -LiteralPath $pair.Root) {
                & (Join-Path $PSScriptRoot 'cleanup-postgres.ps1') -Root $pair.Root -Port $PortA -DbName $pair.Db
            }
        }
    }

    Assert-PgPortAvailable -Context $contextB

    # B is created first with the shared port, then stopped. A takes the port.
    Write-Section 'Start isolated cluster B on the shared port, then stop it'
    $startBArgs = @{ Root = $rootB; Port = $PortA; DbName = $DbB }
    foreach ($key in $zipParam.Keys) { $startBArgs[$key] = $zipParam[$key] }
    & (Join-Path $PSScriptRoot 'start-postgres.ps1') @startBArgs
    Add-Result -Name 'start-B' -Status 'PASS' -Detail "B initialized on 127.0.0.1:$PortA"
    $stopB = & $contextB.PgCtl -D $contextB.DataDir -w -t 60 stop -m fast 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Could not stop cluster B for the negative scenario: $($stopB -join ' ')"
    }
    Assert-Check (-not (Test-PgReady -Context $contextB)) 'B still accepts connections after its stop'
    Assert-Check (@(Get-PgProcessIds -Context $contextB).Count -eq 0) 'B still has postgres processes after its stop'
    Add-Result -Name 'stop-B' -Status 'PASS' -Detail 'B stopped; its data directory is retained'

    Write-Section 'Start isolated cluster A on the now-free shared port'
    Assert-PgPortAvailable -Context $contextA
    $startAArgs = @{ Root = $rootA; Port = $PortA; DbName = $DbA }
    foreach ($key in $zipParam.Keys) { $startAArgs[$key] = $zipParam[$key] }
    & (Join-Path $PSScriptRoot 'start-postgres.ps1') @startAArgs
    Add-Result -Name 'start-A' -Status 'PASS' -Detail "A ready on 127.0.0.1:$PortA" -Data (Get-A-Integrity)

    # Reusing the same, already-running owned cluster must be idempotent and
    # must not overwrite its marker (created_at is a useful immutability probe).
    $idempotentBase = Get-A-Integrity
    $restartSame = Invoke-ScriptCapture -ScriptPath (Join-Path $PSScriptRoot 'start-postgres.ps1') `
        -Parameters $startAArgs -LogName 'start-A-idempotent'
    Assert-Check ($restartSame.ExitCode -eq 0) "start on the same owned cluster failed: $($restartSame.Output)"
    Assert-A-Intact -Before $idempotentBase -When 'start-A-idempotent'
    Add-Result -Name 'start-A-idempotent' -Status 'PASS' `
        -Detail 'exit=0; same data_directory; marker created_at/root/rows unchanged'

    $baselineA = Get-A-Integrity
    $bProtectedFiles = @(
        $contextB.MarkerPath,
        (Join-Path $contextB.DataDir 'PG_VERSION'))
    $bHashesBefore = Get-FileHashes -Paths $bProtectedFiles
    $startBAgainstA = @{ Root = $rootB; Port = $PortA; DbName = $DbB }
    foreach ($key in $zipParam.Keys) { $startBAgainstA[$key] = $zipParam[$key] }
    $attacks = @(
        @{ Name = 'start-B-on-A-port'; Script = 'start-postgres.ps1'; Parameters = $startBAgainstA },
        @{ Name = 'migrate-B-on-A-port'; Script = 'migrate.ps1'; Parameters = @{ Root = $rootB; Port = $PortA; DbName = $DbB; Times = 1 } },
        @{ Name = 'run-tests-B-on-A-port'; Script = 'run-tests.ps1'; Parameters = @{ Mode = 'class'; Root = $rootB; Port = $PortA; DbName = $DbB; Class = 'Mahjong.LobbyService.Tests.PostgresLobbyStoreTests' } },
        @{ Name = 'restart-B-on-A-port'; Script = 'restart-postgres.ps1'; Parameters = @{ Root = $rootB; Port = $PortA; DbName = $DbB; Mode = 'fast' } },
        @{ Name = 'cleanup-B-on-A-port'; Script = 'cleanup-postgres.ps1'; Parameters = @{ Root = $rootB; Port = $PortA; DbName = $DbB } }
    )

    foreach ($attack in $attacks) {
        Write-Section "Negative check: $($attack.Name)"
        $outcome = Invoke-ScriptCapture -ScriptPath (Join-Path $PSScriptRoot $attack.Script) `
            -Parameters $attack.Parameters -LogName $attack.Name
        Assert-Check ($outcome.ExitCode -ne 0) "$($attack.Name) unexpectedly succeeded (exit 0)"
        Assert-Check ($outcome.Output -match 'data_directory|Refusing') "$($attack.Name) failed for an unrelated reason: $($outcome.Output)"
        Assert-A-Intact -Before $baselineA -When $attack.Name
        Add-Result -Name $attack.Name -Status 'PASS' `
            -Detail ("exit={0}; A unchanged; refusal={1}" -f $outcome.ExitCode, (($outcome.Output -split "`r?`n" | Where-Object { $_ -match 'data_directory|Refusing' } | Select-Object -First 1))) `
            -Data @{ exitCode = $outcome.ExitCode }
    }

    $bHashesAfter = Get-FileHashes -Paths $bProtectedFiles
    foreach ($path in $bProtectedFiles) {
        Assert-Check ($bHashesBefore[$path] -eq $bHashesAfter[$path]) "Negative checks changed stopped cluster B file $path"
    }
    Add-Result -Name 'B-files-unchanged' -Status 'PASS' -Detail 'B marker/PG_VERSION hashes unchanged after all refusals'

    Write-Section 'R2 negative check: cleanup -WhatIf on an active owned cluster'
    $beforeWhatIf = Get-A-Integrity
    $protectedFiles = @(
        (Join-Path $contextA.DataDir 'PG_VERSION'),
        $contextA.MarkerPath,
        (Join-Path $contextA.DataDir 'postmaster.pid'))
    $hashesBefore = Get-FileHashes -Paths $protectedFiles
    $whatIf = Invoke-ScriptCapture -ScriptPath (Join-Path $PSScriptRoot 'cleanup-postgres.ps1') `
        -Parameters @{ Root = $rootA; Port = $PortA; DbName = $DbA; WhatIf = $true } -LogName 'cleanup-A-whatif'
    Assert-Check ($whatIf.ExitCode -eq 0) "cleanup -WhatIf failed with exit $($whatIf.ExitCode): $($whatIf.Output)"
    Assert-A-Intact -Before $beforeWhatIf -When 'cleanup -WhatIf'
    Assert-Check (Test-Path -LiteralPath $contextA.Root) 'cleanup -WhatIf deleted the cluster root'
    $hashesAfter = Get-FileHashes -Paths $protectedFiles
    foreach ($path in $protectedFiles) {
        Assert-Check ($hashesBefore[$path] -eq $hashesAfter[$path]) "cleanup -WhatIf changed $path"
    }
    Add-Result -Name 'cleanup-A-WhatIf' -Status 'PASS' `
        -Detail 'exit=0; postmaster/PIDs/port/files unchanged; root still exists' `
        -Data @{ startTime = $beforeWhatIf.startedAt; pids = $beforeWhatIf.pids }

    Write-Section 'Normal cleanup of A and B'
    $cleanupA = Invoke-ScriptCapture -ScriptPath (Join-Path $PSScriptRoot 'cleanup-postgres.ps1') `
        -Parameters @{ Root = $rootA; Port = $PortA; DbName = $DbA } -LogName 'cleanup-A'
    Assert-Check ($cleanupA.ExitCode -eq 0) "cleanup A failed with exit $($cleanupA.ExitCode): $($cleanupA.Output)"
    Assert-Check (-not (Test-Path -LiteralPath $rootA)) 'cleanup A left its root behind'
    Assert-Check (-not (Test-PgReady -Context $contextA)) 'cleanup A left the port ready'
    $cleanedA = $true
    Add-Result -Name 'cleanup-A' -Status 'PASS' -Detail 'A stopped and deleted'

    $cleanupB = Invoke-ScriptCapture -ScriptPath (Join-Path $PSScriptRoot 'cleanup-postgres.ps1') `
        -Parameters @{ Root = $rootB; Port = $PortA; DbName = $DbB } -LogName 'cleanup-B'
    Assert-Check ($cleanupB.ExitCode -eq 0) "cleanup B failed with exit $($cleanupB.ExitCode): $($cleanupB.Output)"
    Assert-Check (-not (Test-Path -LiteralPath $rootB)) 'cleanup B left its root behind'
    $cleanedB = $true
    Add-Result -Name 'cleanup-B' -Status 'PASS' -Detail 'B deleted'
}
catch {
    $failed = $true
    Add-Result -Name 'negative-checks' -Status 'FAIL' -Detail $_.Exception.Message
}
finally {
    foreach ($pair in @(
            @{ Context = $contextA; Port = $PortA; Db = $DbA; Cleaned = $cleanedA },
            @{ Context = $contextB; Port = $PortA; Db = $DbB; Cleaned = $cleanedB })) {
        if (-not $pair.Cleaned -and (Test-Path -LiteralPath $pair.Context.Root)) {
            try {
                & (Join-Path $PSScriptRoot 'cleanup-postgres.ps1') -Root $pair.Context.Root -Port $pair.Port -DbName $pair.Db
            }
            catch {
                $failed = $true
                Add-Result -Name 'best-effort-cleanup' -Status 'FAIL' -Detail $_.Exception.Message
            }
        }
    }

    $summary = [ordered]@{
        timestamp    = (Get-Date).ToUniversalTime().ToString('o')
        result       = if ($failed) { 'FAIL' } else { 'PASS' }
        root         = $Root
        port         = $PortA
        databaseA    = $DbA
        databaseB    = $DbB
        evidence     = $evidencePath
        checks       = $results
    }
    $summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Root 'negative-summary.json') -Encoding utf8
    Write-Section 'Negative verification summary'
    $results | Format-Table -AutoSize check, status, detail -Wrap | Out-String | Write-Host
    Write-Host "Evidence: $evidencePath"
    if ($failed) {
        Write-Host 'RESULT: FAIL'
        exit 1
    }
    Write-Host 'RESULT: PASS'
    exit 0
}
