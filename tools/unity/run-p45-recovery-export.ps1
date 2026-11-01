#Requires -Version 5.1
<#
.SYNOPSIS
  Reproducible Windows Player P4.5 recovery export runner.

.DESCRIPTION
  Prepares an isolated GAME run, copies an explicitly supplied baseline into it, launches
  the opt-in RecoveryProbe Player for BEFORE_RECOVERY, imports the raw export through the
  independent verifier, restarts the same data domain for AFTER_RECOVERY, imports it, and
  compares the two sealed evidence collections. It does not run a full match and does not
  claim G2 or P4.5 completion.
#>
[CmdletBinding()]
param(
    [ValidateSet('Normal', 'Force', 'Both')]
    [string]$Case = 'Both',

    [Parameter(Mandatory = $true)]
    [string]$PlayerPath,

    [Parameter(Mandatory = $true)]
    [string]$BuildCommit,

    [Parameter(Mandatory = $true)]
    [string]$BuildLog,

    [string]$BuildCommand = '',

    [string]$Baseline = 'D:/majiang-wt/p45-windows-validation-20261030/build/p45-windows-validation/series-r2/private-saves/baseline.save',

    [string]$EvidenceRoot = '',

    [Parameter(Mandatory = $true)]
    [string]$VerifierPath,

    [string]$DotnetPath = 'D:\Unity\dotnet\dotnet.exe',
    [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'p45-e2e-common.ps1')

if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
    $EvidenceRoot = Join-Path $P45RepoRoot ('build/p45-recovery-export/runs/' + (Get-P45TimeContext).localDate)
}
$EvidenceRoot = Get-P45FullPath -Path $EvidenceRoot
[IO.Directory]::CreateDirectory($EvidenceRoot) | Out-Null
$transcriptPath = Join-Path $EvidenceRoot 'runner.log'
$results = New-Object System.Collections.Generic.List[object]

function Write-RunnerLog {
    param([string]$Text)
    $line = (Get-P45TimeContext).local + ' ' + $Text
    Write-Host $line
    [IO.File]::AppendAllText($transcriptPath, $line + [Environment]::NewLine, $script:P45Utf8)
}

function Wait-P45RecoveryExport {
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [int]$Seconds = 120
    )

    $exportPath = Join-Path $Directory 'export.json'
    $authorityPath = Join-Path $Directory 'authority.save'
    $donePath = Join-Path $Directory 'export.done'
    $errorPath = Join-Path $Directory 'export.error.json'
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ([IO.File]::Exists($errorPath)) {
            throw ('Player exporter failed: ' + [IO.File]::ReadAllText($errorPath, $script:P45Utf8))
        }
        if ([IO.File]::Exists($donePath) -and [IO.File]::Exists($exportPath) -and [IO.File]::Exists($authorityPath)) {
            return
        }
        Start-Sleep -Milliseconds 200
    }
    throw ('Timed out waiting for the Player export under ' + $Directory)
}

function Invoke-RecoveryCase {
    param([ValidateSet('Normal', 'Force')][string]$Kind)

    $caseRoot = Join-Path $EvidenceRoot ($Kind.ToLowerInvariant() + '-' + (Get-P45TimeContext).localDate)
    Assert-P45UnderDirectory -Path $caseRoot -Directory $EvidenceRoot
    if ([IO.Directory]::Exists($caseRoot)) {
        throw ('Refusing to reuse an existing case directory: ' + $caseRoot)
    }

    $baselineFull = Get-P45FullPath -Path $Baseline
    $baselineSha = Get-P45Sha256 -Path $baselineFull
    if (-not [string]::Equals($baselineSha, '8C867AA83D1AC540A8E0266DDD62DC1FE2C8491FCAA1D3DA8061ACFB5C6E7A44', [StringComparison]::OrdinalIgnoreCase)) {
        throw ('Baseline SHA-256 changed or is not the frozen v3 sample: ' + $baselineSha)
    }

    Write-RunnerLog ('CASE_START ' + $Kind + ' root=' + $caseRoot)
    $created = New-P45Run -RunDir $caseRoot -PlayerPath $PlayerPath -EvidenceClass GAME -Mode RecoveryProbe `
        -BuildProvenance BUILD_LOG_ATTACHED -BuildCommit $BuildCommit -BuildLog $BuildLog -BuildCommand $BuildCommand
    $context = Get-P45Run -RunDir $created.Root
    Write-RunnerLog ('PREPARED run=' + $context.Run.runId + ' player=' + $context.Run.player.sha256 +
        ' package=' + $context.Run.player.package.filesSha256 + ' source=' + $context.Run.source.head +
        ' dirty=' + $context.Run.source.dirty)

    $savePath = Join-Path $context.Run.paths.save 'local-main.save'
    [IO.File]::Copy($baselineFull, $savePath, $false)
    Write-RunnerLog ('BASELINE_COPIED sha=' + (Get-P45Sha256 -Path $savePath) + ' bytes=' + (Get-Item -LiteralPath $savePath).Length)

    # BEFORE_RECOVERY: export lands while the process is still alive, then it either exits
    # cleanly (Normal) or is killed by this run's own Stop -Force (Force).
    $beforeExitAfter = ($Kind -eq 'Normal')
    $before = New-P45Attempt -Context $context -Mode RecoveryProbe -RecoveryPhase Before `
        -RecoveryExitAfterExport:$beforeExitAfter -Wait:$beforeExitAfter -TimeoutSeconds $TimeoutSeconds
    $beforeExportDir = [string]$before.Launch.recoveryExport.directory
    Wait-P45RecoveryExport -Directory $beforeExportDir -Seconds $TimeoutSeconds
    if ($Kind -eq 'Force') {
        $kill = Stop-P45OwnedProcess -Context $context -Force
        Write-RunnerLog ('BEFORE_FORCE_KILL disposition=' + $kill.Disposition + ' native=' + $kill.NativeExitCode)
    }
    else {
        Write-RunnerLog ('BEFORE_NORMAL_EXIT disposition=' + $before.Launch.disposition + ' native=' + $before.NativeExitCode)
    }

    $beforeImport = Import-P45PlayerExport -Context $context -ExportDirectory $beforeExportDir -Phase Before `
        -VerifierPath $VerifierPath -DotnetPath $DotnetPath
    $beforeCollection = New-P45Collection -Context $context
    Write-RunnerLog ('BEFORE_IMPORTED observation=' + $beforeImport.Sha256 + ' authority=' + $beforeImport.AuthoritySaveSha256 +
        ' collection=' + $beforeCollection.ManifestSha256)

    # AFTER_RECOVERY: restart the same local/outbox data domain, export the first recovered
    # baseline, then exit cleanly. No command is submitted by the probe.
    $after = New-P45Attempt -Context $context -Mode RecoveryProbe -RecoveryPhase After `
        -RecoveryExitAfterExport -Wait -TimeoutSeconds $TimeoutSeconds
    $afterExportDir = [string]$after.Launch.recoveryExport.directory
    Wait-P45RecoveryExport -Directory $afterExportDir -Seconds $TimeoutSeconds
    Write-RunnerLog ('AFTER_EXIT disposition=' + $after.Launch.disposition + ' native=' + $after.NativeExitCode)

    $afterImport = Import-P45PlayerExport -Context $context -ExportDirectory $afterExportDir -Phase After `
        -VerifierPath $VerifierPath -DotnetPath $DotnetPath
    $afterCollection = New-P45Collection -Context $context
    Write-RunnerLog ('AFTER_IMPORTED observation=' + $afterImport.Sha256 + ' authority=' + $afterImport.AuthoritySaveSha256 +
        ' collection=' + $afterCollection.ManifestSha256)

    $comparison = Compare-P45Observations -Context $context -BeforePath $beforeImport.Destination `
        -AfterPath $afterImport.Destination -VerifierPath $VerifierPath -DotnetPath $DotnetPath
    Write-RunnerLog ('COMPARE outcome=' + $comparison.Result.outcome + ' pass=' + $comparison.Result.pass +
        ' gate=' + $comparison.Result.realPassGate + ' requiredEqual=' + $comparison.Result.requiredFieldsEqual)

    $beforeVerify = Test-P45Manifest -ManifestPath $beforeCollection.ManifestPath
    $afterVerify = Test-P45Manifest -ManifestPath $afterCollection.ManifestPath
    Write-RunnerLog ('VERIFY before=' + $beforeVerify.ManifestSha256 + ' after=' + $afterVerify.ManifestSha256)

    $scenarioName = if ($Kind -eq 'Normal') { 'normal_exit_cold_recovery' } else { 'forced_kill_cold_recovery' }
    foreach ($scenario in @($context.Run.scenarios)) {
        if ($scenario.name -eq $scenarioName) {
            $scenario.status = if ($comparison.Result.pass) { 'PASS' } else { 'FAIL' }
            $scenario.evidence = @($beforeCollection.ManifestPath, $afterCollection.ManifestPath, $comparison.ResultPath)
            $scenario.note = 'Recovery export pair only. A PASS here is not a full match, three-technique, spectator, Android/iOS or G2 pass.'
        }
    }
    Save-P45Run -Context $context

    $record = [pscustomobject]@{
        case = $Kind
        runId = $context.Run.runId
        runDir = $context.Root
        playerSha256 = [string]$context.Run.player.sha256
        packageSha256 = [string]$context.Run.player.package.filesSha256
        sourceHead = [string]$context.Run.source.head
        buildCommit = [string]$context.Run.player.buildCommit
        baselineSha256 = $baselineSha
        before = [pscustomobject]@{
            attempt = $before.Attempt
            pid = $before.Launch.pid
            startTimeUtc = $before.Launch.startTimeUtc
            disposition = if ($Kind -eq 'Force') { 'FORCE_KILLED' } else { $before.Launch.disposition }
            nativeExitCode = if ($Kind -eq 'Force') { $kill.NativeExitCode } else { $before.NativeExitCode }
            observationSha256 = $beforeImport.Sha256
            authoritySaveSha256 = $beforeImport.AuthoritySaveSha256
            collection = $beforeCollection.Collection
            manifestSha256 = $beforeCollection.ManifestSha256
        }
        after = [pscustomobject]@{
            attempt = $after.Attempt
            pid = $after.Launch.pid
            startTimeUtc = $after.Launch.startTimeUtc
            disposition = $after.Launch.disposition
            nativeExitCode = $after.NativeExitCode
            observationSha256 = $afterImport.Sha256
            authoritySaveSha256 = $afterImport.AuthoritySaveSha256
            collection = $afterCollection.Collection
            manifestSha256 = $afterCollection.ManifestSha256
        }
        compare = [pscustomobject]@{
            outcome = $comparison.Result.outcome
            pass = $comparison.Result.pass
            requiredFieldsEqual = $comparison.Result.requiredFieldsEqual
            realPassGate = $comparison.Result.realPassGate
            chainStatus = $comparison.Result.chainStatus
            rationale = $comparison.Result.rationale
            resultPath = $comparison.ResultPath
            resultSha256 = Get-P45Sha256 -Path $comparison.ResultPath
        }
        verify = [pscustomobject]@{
            beforeManifestSha256 = $beforeVerify.ManifestSha256
            afterManifestSha256 = $afterVerify.ManifestSha256
        }
    }
    $results.Add($record) | Out-Null
    return $record
}

$cases = if ($Case -eq 'Both') { @('Normal', 'Force') } else { @($Case) }
foreach ($kind in $cases) {
    Invoke-RecoveryCase -Kind $kind | Out-Null
}

$summary = [pscustomobject]@{
    schemaVersion = $script:P45SchemaVersion
    kind = 'p45-recovery-export-run-summary'
    generatedAt = (Get-P45TimeContext).local
    evidenceRoot = $EvidenceRoot
    baseline = [pscustomobject]@{
        path = (Get-P45FullPath -Path $Baseline)
        sha256 = Get-P45Sha256 -Path $Baseline
    }
    player = [pscustomobject]@{
        path = (Get-P45FullPath -Path $PlayerPath)
        sha256 = Get-P45Sha256 -Path $PlayerPath
    }
    verifier = [pscustomobject]@{
        path = (Get-P45FullPath -Path $VerifierPath)
        sha256 = Get-P45Sha256 -Path $VerifierPath
    }
    cases = @($results.ToArray())
    trustBoundary = 'LOCAL_CONTROLLED_TEST_ONLY; no hardware root and no administrator-forgery resistance'
    note = 'Recovery export pairs only. This is not a full match, not G2, and not P4.5 completion.'
}
$summaryPath = Join-Path $EvidenceRoot 'summary.json'
Write-P45JsonAtomic -Path $summaryPath -Object $summary
Write-RunnerLog ('SUMMARY ' + $summaryPath + ' sha=' + (Get-P45Sha256 -Path $summaryPath))
