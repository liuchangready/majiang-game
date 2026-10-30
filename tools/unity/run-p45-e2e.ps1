#Requires -Version 5.1
<#
.SYNOPSIS
  Windows Player E2E tooling for the V5 P4.5 preparation task.

.DESCRIPTION
  This is a process/evidence tool, not a substitute for the formal P4.5 match
  run or G2 acceptance. It prepares isolated runs, launches only the explicitly
  supplied Player, records launch/exit/timeout/force-kill facts, archives
  evidence non-destructively, verifies manifests, and compares imported
  structured recovery observations.

  The only built-in automated Player path is the existing
  GameBootstrapper -smoke-localhost / -smoke-data path. That path proves menu,
  lobby, real local-table start, one legal action, one visible event, return to
  menu and clean exit. It does not prove a full match or recovery.

  Modes:
    LocalSmoke   Hidden background Player, existing -smoke-localhost path.
    Manual       Explicit -Visible manual Player window, no smoke sequence.
    ToolSelfTest Hidden auxiliary process for tool verification only.

  Exit codes are documented in doc/v5-.../V5-P4.5-E2E-TOOLING-DELIVERY-...md.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Prepare', 'Launch', 'Stop', 'Restart', 'Collect', 'Verify', 'Import', 'Compare')]
    [string]$Command,

    [string]$RunDir,
    [string]$Root,
    [string]$PlayerPath,

    [ValidateSet('GAME', 'TOOL_SELF_TEST')]
    [string]$EvidenceClass = 'GAME',

    [ValidateSet('LocalSmoke', 'Manual', 'ToolSelfTest')]
    [string]$Mode = 'LocalSmoke',

    [switch]$Visible,
    [switch]$Wait,
    [int]$TimeoutSeconds = 60,
    [int]$GraceSeconds = 5,
    [switch]$Force,

    [ValidateSet('UNKNOWN', 'USER_DECLARED', 'BUILD_LOG_ATTACHED')]
    [string]$BuildProvenance = 'UNKNOWN',
    [string]$BuildCommit = 'UNKNOWN',
    [string]$BuildSourceNote = '',
    [string]$BuildLog = '',
    [string]$ExpectedPlayerSha256 = '',

    [ValidateSet('hold', 'autoexit', 'exit0', 'exit7')]
    [string]$SelfTestMode = 'hold',
    [int]$SelfTestHoldSeconds = 60,

    [ValidateSet('Before', 'After')]
    [string]$Phase,
    [string]$ObservationPath,
    [string]$BeforeObservationPath,
    [string]$AfterObservationPath,
    [string]$ManifestPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

. (Join-Path $PSScriptRoot 'p45-e2e-common.ps1')

function Write-P45Result {
    param(
        [Parameter(Mandatory = $true)][string]$CommandName,
        [Parameter(Mandatory = $true)][int]$ExitCode,
        [Parameter(Mandatory = $true)][string]$Status,
        [object]$Data
    )

    $payload = [pscustomobject]@{
        schemaVersion = $script:P45SchemaVersion
        toolVersion = $script:P45ToolVersion
        command = $CommandName
        exitCode = $ExitCode
        status = $Status
        generatedAt = (Get-P45TimeContext).local
        data = $Data
    }
    $json = $payload | ConvertTo-Json -Depth 30 -Compress
    Write-Output ('P45_RESULT: ' + $json)
}

function Get-P45RequiredRunContext {
    if ([string]::IsNullOrWhiteSpace($RunDir)) {
        Throw-P45 2 'USAGE' ('Command ' + $Command + ' requires -RunDir.')
    }
    return Get-P45Run -RunDir $RunDir
}

try {
    switch ($Command) {
        'Prepare' {
            if ([string]::IsNullOrWhiteSpace($PlayerPath)) {
                Throw-P45 2 'USAGE' 'Prepare requires -PlayerPath pointing at the exact Windows .exe to launch.'
            }
            $created = New-P45Run -RunRoot $Root -RunDir $RunDir -PlayerPath $PlayerPath `
                -EvidenceClass $EvidenceClass -Mode $Mode -Visible:$Visible `
                -BuildProvenance $BuildProvenance -BuildCommit $BuildCommit `
                -BuildSourceNote $BuildSourceNote -BuildLog $BuildLog `
                -ExpectedPlayerSha256 $ExpectedPlayerSha256
            Write-Host ('PREPARED: ' + $created.Root)
            Write-Host ('RUN_ID: ' + $created.Run.runId)
            Write-Host ('PLAYER_SHA256: ' + $created.Run.player.sha256)
            Write-Host ('SOURCE_HEAD: ' + $created.Run.source.head + ' dirty=' + $created.Run.source.dirty)
            Write-Host ('BUILD_PROVENANCE: ' + $created.Run.player.buildProvenance + ' buildCommit=' + $created.Run.player.buildCommit)
            Write-P45Result -CommandName $Command -ExitCode 0 -Status 'PREPARED' -Data ([pscustomobject]@{
                runId = $created.Run.runId
                runDir = $created.Root
                evidenceClass = $created.Run.evidenceClass
                mode = $created.Run.launch.mode
                playerSha256 = $created.Run.player.sha256
                sourceHead = $created.Run.source.head
                sourceDirty = $created.Run.source.dirty
                buildProvenance = $created.Run.player.buildProvenance
                buildCommit = $created.Run.player.buildCommit
                ownershipMarkerSha256 = $created.Run.ownership.markerSha256
            })
            exit 0
        }

        'Launch' {
            $context = Get-P45RequiredRunContext
            if (-not $PSBoundParameters.ContainsKey('Mode')) { $Mode = [string]$context.Run.launch.mode }
            if (-not $PSBoundParameters.ContainsKey('Visible')) { $Visible = [bool]$context.Run.launch.visible }
            $result = New-P45Attempt -Context $context -Mode $Mode -Visible:$Visible -Wait:$Wait `
                -TimeoutSeconds $TimeoutSeconds -SelfTestMode $SelfTestMode -SelfTestHoldSeconds $SelfTestHoldSeconds
            $stateText = $result.Status
            Write-Host ('LAUNCH attempt=' + $result.Attempt + ' pid=' + $result.Launch.pid + ' status=' + $stateText + ' nativeExit=' + $result.NativeExitCode)
            Write-P45Result -CommandName $Command -ExitCode $result.ExitCode -Status $stateText -Data ([pscustomobject]@{
                runId = $context.Run.runId
                attempt = $result.Attempt
                pid = $result.Launch.pid
                visible = $result.Launch.visible
                hidden = $result.Launch.hidden
                mode = $result.Launch.mode
                disposition = $result.Launch.disposition
                nativeExitCode = $result.NativeExitCode
                toolExitCode = $result.ExitCode
                logFile = $result.Launch.logFile
                screenshotDirectory = $result.Launch.screenshotDirectory
                attemptDirectory = $result.AttemptDirectory
            })
            exit $result.ExitCode
        }

        'Stop' {
            $context = Get-P45RequiredRunContext
            $result = Stop-P45OwnedProcess -Context $context -GraceSeconds $GraceSeconds -Force:$Force
            Write-Host ('STOP status=' + $result.Status + ' nativeExit=' + $result.NativeExitCode + ' attempt=' + $result.Attempt)
            Write-P45Result -CommandName $Command -ExitCode $result.ExitCode -Status $result.Status -Data ([pscustomobject]@{
                runId = $context.Run.runId
                attempt = $result.Attempt
                disposition = $result.Disposition
                nativeExitCode = $result.NativeExitCode
                toolExitCode = $result.ExitCode
                reason = Get-P45Property $result 'Reason' ''
            })
            exit $result.ExitCode
        }

        'Restart' {
            $context = Get-P45RequiredRunContext
            $state = Get-P45State -Context $context
            if ($null -ne (Get-P45Property $state 'activeProcess')) {
                $stop = Stop-P45OwnedProcess -Context $context -GraceSeconds $GraceSeconds -Force:$Force
                Write-Host ('RESTART pre-stop status=' + $stop.Status + ' nativeExit=' + $stop.NativeExitCode)
            }
            if (-not $PSBoundParameters.ContainsKey('Mode')) { $Mode = [string]$context.Run.launch.mode }
            if (-not $PSBoundParameters.ContainsKey('Visible')) { $Visible = [bool]$context.Run.launch.visible }
            $result = New-P45Attempt -Context $context -Mode $Mode -Visible:$Visible -Wait:$Wait `
                -TimeoutSeconds $TimeoutSeconds -SelfTestMode $SelfTestMode -SelfTestHoldSeconds $SelfTestHoldSeconds
            Write-Host ('RESTART attempt=' + $result.Attempt + ' pid=' + $result.Launch.pid + ' status=' + $result.Status + ' nativeExit=' + $result.NativeExitCode)
            Write-P45Result -CommandName $Command -ExitCode $result.ExitCode -Status $result.Status -Data ([pscustomobject]@{
                runId = $context.Run.runId
                attempt = $result.Attempt
                pid = $result.Launch.pid
                visible = $result.Launch.visible
                hidden = $result.Launch.hidden
                disposition = $result.Launch.disposition
                nativeExitCode = $result.NativeExitCode
                toolExitCode = $result.ExitCode
                attemptDirectory = $result.AttemptDirectory
                persistenceRoot = [string]$context.Run.paths.root
            })
            exit $result.ExitCode
        }

        'Collect' {
            $context = Get-P45RequiredRunContext
            $result = New-P45Collection -Context $context
            Write-Host ('COLLECTED collection=' + $result.Collection + ' entries=' + $result.EntryCount)
            Write-Host ('MANIFEST: ' + $result.ManifestPath)
            Write-Host ('MANIFEST_SHA256: ' + $result.ManifestSha256)
            Write-Host ('EVIDENCE_STATUS: ' + $result.Status)
            Write-P45Result -CommandName $Command -ExitCode 0 -Status $result.Status -Data ([pscustomobject]@{
                runId = $context.Run.runId
                collection = $result.Collection
                manifestPath = $result.ManifestPath
                manifestSha256 = $result.ManifestSha256
                entryCount = $result.EntryCount
                evidenceClass = $context.Run.evidenceClass
            })
            exit 0
        }

        'Verify' {
            if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
                if ([string]::IsNullOrWhiteSpace($RunDir)) {
                    Throw-P45 2 'USAGE' 'Verify requires -ManifestPath or -RunDir.'
                }
                $context = Get-P45Run -RunDir $RunDir
                $collectionsRoot = Join-Path $context.Root 'evidence\collections'
                $latest = @(Get-ChildItem -LiteralPath $collectionsRoot -Directory -ErrorAction SilentlyContinue |
                    Sort-Object Name -Descending | Select-Object -First 1)
                if ($latest.Count -eq 0) {
                    Throw-P45 5 'NOT_FOUND' 'No evidence collection exists; run Collect first or pass -ManifestPath.'
                }
                $ManifestPath = Join-Path $latest[0].FullName 'manifest.json'
            }
            $verified = Test-P45Manifest -ManifestPath $ManifestPath
            Write-Host ('VERIFIED entries=' + $verified.EntryCount + ' sha256=' + $verified.ManifestSha256)
            Write-P45Result -CommandName $Command -ExitCode 0 -Status 'MANIFEST_VALID' -Data ([pscustomobject]@{
                manifestPath = $verified.ManifestPath
                manifestSha256 = $verified.ManifestSha256
                entryCount = $verified.EntryCount
                evidenceStatus = $verified.Status
                collection = $verified.Collection
            })
            exit 0
        }

        'Import' {
            $context = Get-P45RequiredRunContext
            if ([string]::IsNullOrWhiteSpace($ObservationPath) -or [string]::IsNullOrWhiteSpace($Phase)) {
                Throw-P45 2 'USAGE' 'Import requires -ObservationPath and -Phase Before|After.'
            }
            $imported = Import-P45Observation -Context $context -ObservationPath $ObservationPath -Phase $Phase
            Write-Host ('IMPORTED phase=' + $imported.Phase + ' source=' + $imported.Source + ' eligible=' + $imported.Eligible)
            Write-Host ('DESTINATION: ' + $imported.Destination)
            Write-P45Result -CommandName $Command -ExitCode 0 -Status 'OBSERVATION_IMPORTED' -Data ([pscustomobject]@{
                runId = $context.Run.runId
                phase = $imported.Phase
                source = $imported.Source
                destination = $imported.Destination
                sha256 = $imported.Sha256
                eligibleForRealPass = $imported.Eligible
                eligibilityReasons = $imported.EligibilityReasons
            })
            exit 0
        }

        'Compare' {
            $context = Get-P45RequiredRunContext
            if (-not [string]::IsNullOrWhiteSpace($ObservationPath)) {
                Throw-P45 2 'USAGE' 'Compare uses -BeforeObservationPath and -AfterObservationPath; -ObservationPath is only for Import.'
            }
            $beforePath = $BeforeObservationPath
            $afterPath = $AfterObservationPath
            if ([string]::IsNullOrWhiteSpace($beforePath)) {
                $beforePath = Get-P45LatestObservation -Context $context -Phase 'Before'
            }
            if ([string]::IsNullOrWhiteSpace($afterPath)) {
                $afterPath = Get-P45LatestObservation -Context $context -Phase 'After'
            }
            if ([string]::IsNullOrWhiteSpace($beforePath) -or [string]::IsNullOrWhiteSpace($afterPath)) {
                Throw-P45 5 'NOT_FOUND' 'Compare needs imported Before and After observations, or explicit -BeforeObservationPath/-AfterObservationPath.'
            }
            $comparison = Compare-P45Observations -Context $context -BeforePath $beforePath -AfterPath $afterPath
            Write-Host ('COMPARE outcome=' + $comparison.Result.outcome + ' pass=' + $comparison.Result.pass + ' requiredEqual=' + $comparison.Result.requiredFieldsEqual)
            Write-Host ('COMPARE_EVIDENCE: ' + $comparison.ResultPath)
            Write-P45Result -CommandName $Command -ExitCode $comparison.ExitCode -Status $comparison.Result.outcome -Data ([pscustomobject]@{
                runId = $context.Run.runId
                outcome = $comparison.Result.outcome
                pass = $comparison.Result.pass
                requiredFieldsEqual = $comparison.Result.requiredFieldsEqual
                eligibleForRealPass = $comparison.Result.eligibleForRealPass
                realPassGate = $comparison.Result.realPassGate
                rationale = $comparison.Result.rationale
                resultPath = $comparison.ResultPath
                before = $comparison.Result.before
                after = $comparison.Result.after
                mismatches = $comparison.Result.mismatches
            })
            exit $comparison.ExitCode
        }
    }
}
catch {
    $message = $_.Exception.Message
    $code = 99
    $status = 'INTERNAL_ERROR'
    $detail = $message
    if ($message -like 'P45ERR|*') {
        $parts = $message.Split([char[]]@('|'), 4)
        if ($parts.Length -eq 4) {
            $code = [int]$parts[1]
            $status = $parts[2]
            $detail = $parts[3]
        }
    }
    Write-Host ('P45_ERROR: ' + $status + ': ' + $detail)
    $errorPayload = [pscustomobject]@{
        schemaVersion = $script:P45SchemaVersion
        toolVersion = $script:P45ToolVersion
        command = $Command
        exitCode = $code
        status = $status
        message = $detail
        generatedAt = (Get-P45TimeContext).local
    }
    Write-Output ('P45_RESULT: ' + ($errorPayload | ConvertTo-Json -Depth 10 -Compress))
    exit $code
}
