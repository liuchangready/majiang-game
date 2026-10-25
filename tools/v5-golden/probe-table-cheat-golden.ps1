<#
.SYNOPSIS
    Single-field mutation probe and binding-readiness gate for the N3 table cheat admission
    golden consumer (tests/v5-golden/cheat-v1.json tileMarkHandler).

.DESCRIPTION
    Builds Mahjong.TableRuntime.Tests in Release (non-incremental), copies the real V4-exported
    tests/v5-golden/cheat-v1.json into a brand-new TEMP evidence directory, and runs the compiled
    N3TableCheatGoldenAdmissionTests class against isolated mutated copies through the independent
    V5_TABLE_CHEAT_GOLDEN_PATH override.

    Two results are reported separately:
      * binding readiness: every bound golden case has a discovered, executed, passing test and
        the baseline total equals the discovered method count (no skips, no hidden tests);
      * mutation matrix: every positive control stays green and every single-field negative
        produces a real failed test with the intended failure message.

    Rules:
      - The repository fixture and build output are never modified; only new TEMP copies are written.
      - The environment variable is restored in try/finally around every run and around the whole
        script, including forced failures.
      - A negative is accepted only when the runner reports a numeric non-zero exit, Total equal
        to the baseline, Failed >= 1, Errors = 0, Skipped = 0, Not Run = 0, a real [FAIL] line,
        and the expected failure text. A build failure, a missing fixture, an unparsed summary or
        Total = 0 is a tool failure, never an expected FAIL.
      - Every negative changes one logical field (the two structural controls are labelled).
      - Nothing here proves N3/G1/G5; it only proves this consumer binding and its mutation
        resistance.

    Exit codes: 0 = matrix and readiness both pass; 1 = unexpected matrix result; 2 = tool/parse
    failure; 3 = binding readiness failure; otherwise the native build failure code is propagated.

.PARAMETER RepoRoot
    Repository root. Defaults to two levels above this script.

.PARAMETER EvidenceRoot
    Evidence directory. Must not exist; defaults to a new GUID directory under TEMP.

.PARAMETER DotnetPath
    dotnet executable. Defaults to D:/Unity/dotnet/dotnet.exe.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = '',
    [string]$EvidenceRoot = '',
    [string]$DotnetPath = 'D:/Unity/dotnet/dotnet.exe'
)

$ErrorActionPreference = 'Continue'
$script:ToolFailure = $false
$script:ClassFilter = '*N3TableCheatGoldenAdmissionTests*'

function Write-Step {
    param([string]$Message)
    Write-Host ('[{0}] {1}' -f (Get-Date -Format 'HH:mm:ss'), $Message)
}

function Write-Utf8NoBom {
    param([string]$Path, [string]$Text)
    [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($false)))
}

function Get-Sha256 {
    param([string]$Path)
    return (Get-FileHash -Path $Path -Algorithm SHA256).Hash
}

function Get-NormalizedSha256 {
    param([string]$Path)
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $text = [System.Text.Encoding]::UTF8.GetString($bytes)
    $normalized = $text.Replace("`r`n", "`n")
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($normalized))
    } finally {
        $sha.Dispose()
    }
    return (($hash | ForEach-Object { $_.ToString('X2') }) -join '')
}

function Get-GitBlobSha1 {
    param([string]$Root, [string]$RelativePath)
    $output = & git -C $Root hash-object -- $RelativePath 2>&1
    if ($LASTEXITCODE -ne 0) {
        return 'GIT-UNAVAILABLE'
    }
    return (($output | Out-String).Trim())
}

function Restore-GoldenEnvironment {
    param([string]$Previous)
    if ($null -eq $Previous) {
        Remove-Item Env:V5_TABLE_CHEAT_GOLDEN_PATH -ErrorAction SilentlyContinue
    } else {
        $env:V5_TABLE_CHEAT_GOLDEN_PATH = $Previous
    }
}

function ConvertTo-MutatedFixture {
    param(
        [string]$Template,
        [string]$Destination,
        [scriptblock]$Apply
    )
    $text = [System.IO.File]::ReadAllText($Template, [System.Text.Encoding]::UTF8)
    $root = $text | ConvertFrom-Json
    & $Apply $root
    $json = $root | ConvertTo-Json -Depth 100
    Write-Utf8NoBom $Destination $json
}

function Invoke-GoldenRun {
    param(
        [string]$FixturePath,
        [string]$LogPath,
        [string]$AssemblyPath,
        [string]$Dotnet
    )
    $previous = $env:V5_TABLE_CHEAT_GOLDEN_PATH
    $output = @()
    try {
        $env:V5_TABLE_CHEAT_GOLDEN_PATH = $FixturePath
        $output = & $Dotnet $AssemblyPath -noLogo -class $script:ClassFilter 2>&1
        $exit = $LASTEXITCODE
    } finally {
        Restore-GoldenEnvironment $previous
    }

    $logText = ($output | Out-String)
    Write-Utf8NoBom $LogPath $logText

    $summary = [regex]::Matches(
        $logText,
        'Total:\s*(\d+),\s*Errors:\s*(\d+),\s*Failed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Not Run:\s*(\d+)')
    $failedMarkers = [regex]::Matches($logText, '\[FAIL\]').Count
    $parsed = $summary.Count -gt 0
    $total = -1
    $errors = -1
    $failed = -1
    $skipped = -1
    $notRun = -1
    if ($parsed) {
        $match = $summary[$summary.Count - 1]
        $total = [int]$match.Groups[1].Value
        $errors = [int]$match.Groups[2].Value
        $failed = [int]$match.Groups[3].Value
        $skipped = [int]$match.Groups[4].Value
        $notRun = [int]$match.Groups[5].Value
    }

    $missingFixture = $logText -match 'points to a missing fixture|Could not locate tests/v5-golden'

    return [pscustomobject]@{
        Exit           = $exit
        Parsed         = $parsed
        Total          = $total
        Errors         = $errors
        Failed         = $failed
        Skipped        = $skipped
        NotRun         = $notRun
        FailedMarkers  = $failedMarkers
        MissingFixture = $missingFixture
        Text           = $logText
        Log            = $LogPath
    }
}

function Invoke-ListDiscovered {
    param([string]$AssemblyPath, [string]$Dotnet, [string]$LogPath)
    $output = & $Dotnet $AssemblyPath -noLogo -list 'full/json' -class $script:ClassFilter 2>&1
    $exit = $LASTEXITCODE
    $text = ($output | Out-String)
    Write-Utf8NoBom $LogPath $text

    $items = @()
    $parsed = $false
    try {
        $parsedObject = $text | ConvertFrom-Json
        if ($parsedObject -is [System.Array]) {
            $items = @($parsedObject)
        } else {
            $items = @($parsedObject)
        }
        $parsed = $true
    } catch {
        $items = @()
    }

    $methods = @()
    foreach ($item in $items) {
        if ($null -ne $item.Method -and -not [string]::IsNullOrWhiteSpace($item.Method)) {
            $methods += ([string]$item.Method)
        }
    }

    $manifestItem = @($items | Where-Object {
        $null -ne $_.DisplayName -and ([string]$_.DisplayName).Contains('N3-BINDING-MANIFEST')
    }) | Select-Object -First 1

    $manifestText = ''
    if ($null -ne $manifestItem) {
        $display = [string]$manifestItem.DisplayName
        $index = $display.IndexOf('N3-BINDING-MANIFEST')
        if ($index -ge 0) {
            $manifestText = $display.Substring($index)
        }
    }

    return [pscustomobject]@{
        Exit         = $exit
        Parsed       = $parsed
        Methods      = $methods
        ManifestText = $manifestText
        Text         = $text
        Log          = $LogPath
    }
}

# ---------------------------------------------------------------------------
# Paths and evidence root
# ---------------------------------------------------------------------------

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
}
$RepoRoot = (Resolve-Path $RepoRoot).Path
$FixtureRelative = 'tests/v5-golden/cheat-v1.json'
$FixturePath = Join-Path $RepoRoot ($FixtureRelative -replace '/', '\')
$ProjectPath = Join-Path $RepoRoot 'dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj'
$AssemblyPath = Join-Path $RepoRoot 'dotnet/tests/Mahjong.TableRuntime.Tests/bin/Release/net8.0/Mahjong.TableRuntime.Tests.dll'

foreach ($required in @($FixturePath, $ProjectPath, $DotnetPath)) {
    if (-not (Test-Path -LiteralPath $required)) {
        Write-Host ('PROBE-FAIL: required path is missing: {0}' -f $required)
        exit 2
    }
}

if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
    $EvidenceRoot = Join-Path $env:TEMP ('table-cheat-golden-probe-' + [guid]::NewGuid().ToString('N'))
}
if (Test-Path -LiteralPath $EvidenceRoot) {
    Write-Host ('PROBE-FAIL: evidence root already exists; every run needs a new directory: {0}' -f $EvidenceRoot)
    exit 2
}
New-Item -ItemType Directory -Path $EvidenceRoot | Out-Null

$previousEnvironment = $env:V5_TABLE_CHEAT_GOLDEN_PATH

try {
    $sourceRawSha = Get-Sha256 $FixturePath
    $sourceNormalizedSha = Get-NormalizedSha256 $FixturePath
    $sourceGitSha1 = Get-GitBlobSha1 $RepoRoot $FixtureRelative

    $meta = [ordered]@{
        evidenceRoot         = $EvidenceRoot
        repoRoot             = $RepoRoot
        fixture              = $FixtureRelative
        fixtureRawSha256     = $sourceRawSha
        fixtureLfSha256      = $sourceNormalizedSha
        fixtureGitSha1       = $sourceGitSha1
        overrideVariable     = 'V5_TABLE_CHEAT_GOLDEN_PATH'
        classFilter          = $script:ClassFilter
        dotnet               = $DotnetPath
        dotnetVersion        = ((& $DotnetPath --version 2>&1 | Out-String).Trim())
        startedAtUtc         = (Get-Date).ToUniversalTime().ToString('o')
    }
    Write-Utf8NoBom (Join-Path $EvidenceRoot 'meta.json') ($meta | ConvertTo-Json)
    Write-Step ('evidence root: ' + $EvidenceRoot)
    Write-Step ('fixture raw SHA256: ' + $sourceRawSha)

    # -----------------------------------------------------------------------
    # Build once; a build failure propagates its numeric exit code.
    # -----------------------------------------------------------------------
    $buildLog = Join-Path $EvidenceRoot 'build.log'
    $buildOutput = & $DotnetPath build $ProjectPath -c Release --no-incremental -v:minimal 2>&1
    $buildExit = $LASTEXITCODE
    Write-Utf8NoBom $buildLog (($buildOutput | Out-String))
    Write-Step ('build exit: ' + $buildExit)
    if ($buildExit -ne 0) {
        Write-Host ('PROBE-FAIL: build failed with native exit {0}; see {1}' -f $buildExit, $buildLog)
        exit $buildExit
    }
    if (-not (Test-Path -LiteralPath $AssemblyPath)) {
        Write-Host ('PROBE-FAIL: test assembly missing after a successful build: {0}' -f $AssemblyPath)
        exit 2
    }

    # -----------------------------------------------------------------------
    # Binding readiness: real eligibility from the machine-readable manifest, not just
    # method-name presence. Blocked cases make readiness non-zero but do NOT stop the matrix.
    # -----------------------------------------------------------------------
    $list = Invoke-ListDiscovered -AssemblyPath $AssemblyPath -Dotnet $DotnetPath -LogPath (Join-Path $EvidenceRoot 'list-discovered.json')
    $expectedBoundCaseTokens = [ordered]@{
        'tm-allowed'                = 'golden_tm_allowed_commits_once_and_replays_without_a_second_charge'
        'tm-wrong-phase-round-end'  = 'golden_tm_wrong_phase_round_end_rejects_with_the_terminated_round_reason'
        'tm-no-active-decision'     = 'golden_tm_no_active_decision_rejects_when_pending_exists_without_clock_timing'
        'tm-deadline-expired'       = 'golden_tm_deadline_expired_rejects_at_the_exact_deadline_before_any_tick'
    }
    $expectedBlockedCaseIds = @('tm-decision-mismatch')
    $blockedReasons = @{
        'tm-decision-mismatch' = 'authority has no active round (V4 roundIdGiven=null); the accepted actor always owns a concrete _round and has no safe null-round port'
    }
    $expectedFaultInjection = @('tm-wrong-phase-round-end', 'tm-no-active-decision')
    $supplementaryMethod = 'supplementary_stale_round_identity_is_rejected_with_round_mismatch'

    $baselineCopy = Join-Path $EvidenceRoot 'cheat-v1.real.json'
    Copy-Item -LiteralPath $FixturePath -Destination $baselineCopy -Force
    $baselineResult = Invoke-GoldenRun -FixturePath $baselineCopy -LogPath (Join-Path $EvidenceRoot 'P01-baseline.log') -AssemblyPath $AssemblyPath -Dotnet $DotnetPath

    $readinessReasons = @()
    if ($list.Exit -ne 0) {
        $readinessReasons += ('method discovery exit ' + $list.Exit)
    }
    if (-not $list.Parsed) {
        $readinessReasons += 'method discovery JSON could not be parsed'
    }
    if ($list.Methods.Count -le 0) {
        $readinessReasons += 'method discovery returned zero methods'
    }
    if (-not $baselineResult.Parsed) {
        $readinessReasons += 'baseline run produced no parsable test summary'
    }
    if ($baselineResult.MissingFixture) {
        $readinessReasons += 'baseline run took the missing-fixture path'
    }
    if ($baselineResult.Total -le 0) {
        $readinessReasons += ('baseline Total is ' + $baselineResult.Total + ' (zero discovered/executed cannot pass)')
    }
    if ($list.Methods.Count -gt 0 -and $baselineResult.Total -ne $list.Methods.Count) {
        $readinessReasons += ('baseline Total ' + $baselineResult.Total + ' != discovered methods ' + $list.Methods.Count)
    }
    if ($baselineResult.Exit -ne 0) {
        $readinessReasons += ('baseline native exit ' + $baselineResult.Exit)
    }
    if ($baselineResult.Failed -ne 0) {
        $readinessReasons += ('baseline Failed ' + $baselineResult.Failed)
    }
    if ($baselineResult.Errors -ne 0) {
        $readinessReasons += ('baseline Errors ' + $baselineResult.Errors)
    }
    if ($baselineResult.Skipped -ne 0) {
        $readinessReasons += ('baseline Skipped ' + $baselineResult.Skipped)
    }
    if ($baselineResult.NotRun -ne 0) {
        $readinessReasons += ('baseline Not Run ' + $baselineResult.NotRun)
    }
    if ($baselineResult.FailedMarkers -ne 0) {
        $readinessReasons += ('baseline [FAIL] markers ' + $baselineResult.FailedMarkers)
    }

    $manifestFields = @{}
    $manifestBoundCases = @()
    $manifestBlockedCases = @()
    $manifestFaultInjection = @()
    if ([string]::IsNullOrWhiteSpace($list.ManifestText)) {
        $readinessReasons += 'N3-BINDING-MANIFEST was not discovered'
    } else {
        foreach ($part in ($list.ManifestText -split '\|' | Select-Object -Skip 1)) {
            $kv = $part -split '=', 2
            if ($kv.Count -eq 2) {
                $manifestFields[$kv[0]] = $kv[1]
            }
        }

        foreach ($required in @('denominator', 'bound', 'blocked', 'boundCases', 'blockedCases', 'faultInjection', 'reason')) {
            if (-not $manifestFields.ContainsKey($required)) {
                $readinessReasons += ('manifest is missing field ' + $required)
            }
        }

        if ($readinessReasons.Count -eq 0) {
            $manifestBoundCases = @($manifestFields['boundCases'] -split ',')
            $manifestBlockedCases = @($manifestFields['blockedCases'] -split ',')
            $manifestFaultInjection = @($manifestFields['faultInjection'] -split ',')

            if ($manifestFields['denominator'] -ne '5') {
                $readinessReasons += ('manifest denominator is ' + $manifestFields['denominator'] + ', expected 5')
            }
            if ($manifestFields['bound'] -ne ([string]$manifestBoundCases.Count)) {
                $readinessReasons += ('manifest bound=' + $manifestFields['bound'] + ' != boundCases ' + $manifestBoundCases.Count)
            }
            if ($manifestFields['blocked'] -ne ([string]$manifestBlockedCases.Count)) {
                $readinessReasons += ('manifest blocked=' + $manifestFields['blocked'] + ' != blockedCases ' + $manifestBlockedCases.Count)
            }
            if (($manifestBoundCases.Count + $manifestBlockedCases.Count) -ne 5) {
                $readinessReasons += 'manifest boundCases + blockedCases != denominator 5'
            }
            if (($manifestBlockedCases -join ',') -ne ($expectedBlockedCaseIds -join ',')) {
                $readinessReasons += ('manifest blockedCases [' + ($manifestBlockedCases -join ',') + '] != expected [' + ($expectedBlockedCaseIds -join ',') + ']')
            }
            if (($manifestFaultInjection -join ',') -ne ($expectedFaultInjection -join ',')) {
                $readinessReasons += ('manifest faultInjection [' + ($manifestFaultInjection -join ',') + '] != expected [' + ($expectedFaultInjection -join ',') + ']')
            }
            if ([string]::IsNullOrWhiteSpace($manifestFields['reason'])) {
                $readinessReasons += 'manifest blocked reason token is empty'
            }

            foreach ($caseId in $expectedBoundCaseTokens.Keys) {
                if ($manifestBoundCases -notcontains $caseId) {
                    $readinessReasons += ('manifest does not declare bound case ' + $caseId)
                }
                $method = $expectedBoundCaseTokens[$caseId]
                if ($list.Methods -notcontains $method) {
                    $readinessReasons += ('missing bound test method ' + $caseId + ' -> ' + $method)
                }
            }

            foreach ($caseId in $expectedBlockedCaseIds) {
                if ($manifestBoundCases -contains $caseId) {
                    $readinessReasons += ('blocked case ' + $caseId + ' is also declared bound')
                }
                if (@($list.Methods | Where-Object { $_ -like ('golden_' + ($caseId -replace '-', '_') + '*') }).Count -gt 0) {
                    $readinessReasons += ('blocked case ' + $caseId + ' has a golden binding method; BLOCKED must not be claimed as bound')
                }
            }

            if ($list.Methods -notcontains $supplementaryMethod) {
                $readinessReasons += ('missing supplementary stale-round guard ' + $supplementaryMethod)
            }
        }
    }

    $boundCount = 0
    if ($readinessReasons.Count -eq 0) {
        $boundCount = $manifestBoundCases.Count
    }
    $blockedCount = $manifestBlockedCases.Count
    if ($readinessReasons.Count -gt 0) {
        $readinessStatus = 'FAIL'
        $readinessRatio = ('0/5')
    } elseif ($blockedCount -gt 0) {
        $readinessStatus = 'BLOCKED'
        $readinessRatio = ('' + $boundCount + '/5')
    } else {
        $readinessStatus = 'PASS'
        $readinessRatio = ('' + $boundCount + '/5')
    }

    $blockedReasonRows = @()
    foreach ($caseId in $manifestBlockedCases) {
        $reason = $blockedReasons[$caseId]
        if ([string]::IsNullOrWhiteSpace($reason)) {
            $reason = 'unspecified blocked reason'
        }
        $blockedReasonRows += [pscustomobject]@{
            caseId = $caseId
            reason = $reason
        }
    }

    $readiness = [ordered]@{
        status              = $readinessStatus
        denominator         = 5
        boundCases          = $boundCount
        blockedCases        = $blockedCount
        blockedCaseIds      = $manifestBlockedCases
        blockedReasons      = $blockedReasonRows
        faultInjectionCases = $manifestFaultInjection
        readinessRatio      = $readinessRatio
        discoveredMethods   = $list.Methods.Count
        baselineTotal       = $baselineResult.Total
        baselineFailed      = $baselineResult.Failed
        baselineSkipped     = $baselineResult.Skipped
        baselineErrors      = $baselineResult.Errors
        baselineNotRun      = $baselineResult.NotRun
        reasons             = $readinessReasons
        log                 = $baselineResult.Log
    }
    Write-Utf8NoBom (Join-Path $EvidenceRoot 'readiness.json') ($readiness | ConvertTo-Json -Depth 8)

    Write-Host ''
    Write-Host ('=== BINDING READINESS: {0} ===' -f $readinessStatus)
    Write-Host ('denominator=5 bound={0} blocked={1} readiness={2} blockedCaseIds=[{3}]' -f `
        $boundCount, $blockedCount, $readinessRatio, ($manifestBlockedCases -join ','))
    foreach ($row in $blockedReasonRows) {
        Write-Host ('READINESS-BLOCKED-CASE: {0}: {1}' -f $row.caseId, $row.reason)
    }
    foreach ($reason in $readinessReasons) {
        Write-Host ('READINESS-FAIL-REASON: ' + $reason)
    }
    Write-Step ('binding readiness ' + $readinessStatus + ' (' + $readinessRatio + '); continuing to the full mutation matrix.')

    # -----------------------------------------------------------------------
    # Case matrix
    # -----------------------------------------------------------------------
    $allowed = 'tm-allowed'
    $roundEnd = 'tm-wrong-phase-round-end'
    $noDecision = 'tm-no-active-decision'
    $deadline = 'tm-deadline-expired'
    $mismatch = 'tm-decision-mismatch'

    $outcomePattern = 'is Allowed but carries reason|is Rejected but has a null reason'
    $reasonPattern = 'Strings differ|Values differ|is Allowed but carries reason'

    $cases = @(
        # Positive controls: must stay green. They prove the probe reads the TEMP copy and that the
        # tests bind semantics rather than a whole-file hash or a fixed byte layout.
        @{ Id = 'P01-baseline-copy'; Expect = 'pass'; Raw = $true; Path = 'unmodified TEMP copy of the real fixture'; Apply = { param($r) } }
        @{ Id = 'P02-reserialize-only'; Expect = 'pass'; Path = 'whole-file JSON re-serialization, no field changed'; Apply = { param($r) } }
        @{ Id = 'P03-provenance-only'; Expect = 'pass'; Path = 'root.v4Commit + root.exportedAtNote'; Apply = { param($r) $r.v4Commit = '0000000000000000000000000000000000000000'; $r.exportedAtNote = 'probe control: provenance is not a behavioral binding' } }
        @{ Id = 'P04-note-only'; Expect = 'pass'; Path = 'tileMarkHandler[' + $allowed + '].note'; Apply = { param($r) $c = $r.tileMarkHandler | Where-Object { $_.caseId -eq 'tm-allowed' }; $c.note = 'probe control note; note is not a verdict field' } }
        @{ Id = 'P05-nonbound-note-only'; Expect = 'pass'; Path = 'tileMarkHandler[tm-not-your-turn].note'; Apply = { param($r) $c = $r.tileMarkHandler | Where-Object { $_.caseId -eq 'tm-not-your-turn' }; $c.note = 'probe control note on a non-bound case' } }

        # Outcome single-field negatives: a rejected case becomes Allowed while its reason remains;
        # the allowed case becomes Rejected while its reason remains null.
        @{ Id = 'N01-outcome-allowed'; Expect = 'fail'; Path = 'tileMarkHandler[tm-allowed].outcome'; Pattern = $outcomePattern; Apply = { param($r) $c = $r.tileMarkHandler | Where-Object { $_.caseId -eq 'tm-allowed' }; $c.outcome = 'Rejected' } }
        @{ Id = 'N02-outcome-round-end'; Expect = 'fail'; Path = 'tileMarkHandler[tm-wrong-phase-round-end].outcome'; Pattern = $outcomePattern; Apply = { param($r) $c = $r.tileMarkHandler | Where-Object { $_.caseId -eq 'tm-wrong-phase-round-end' }; $c.outcome = 'Allowed' } }
        @{ Id = 'N03-outcome-no-decision'; Expect = 'fail'; Path = 'tileMarkHandler[tm-no-active-decision].outcome'; Pattern = $outcomePattern; Apply = { param($r) $c = $r.tileMarkHandler | Where-Object { $_.caseId -eq 'tm-no-active-decision' }; $c.outcome = 'Allowed' } }
        @{ Id = 'N04-outcome-deadline'; Expect = 'fail'; Path = 'tileMarkHandler[tm-deadline-expired].outcome'; Pattern = $outcomePattern; Apply = { param($r) $c = $r.tileMarkHandler | Where-Object { $_.caseId -eq 'tm-deadline-expired' }; $c.outcome = 'Allowed' } }
        @{ Id = 'N05-outcome-mismatch'; Expect = 'fail'; Path = 'tileMarkHandler[tm-decision-mismatch].outcome'; Pattern = $outcomePattern; Apply = { param($r) $c = $r.tileMarkHandler | Where-Object { $_.caseId -eq 'tm-decision-mismatch' }; $c.outcome = 'Allowed' } }

        # Reason single-field negatives: swap the V4 verdict reason for another valid V4 reason.
        @{ Id = 'N06-reason-allowed'; Expect = 'fail'; Path = 'tileMarkHandler[tm-allowed].reason'; Pattern = $reasonPattern; Apply = { param($r) $c = $r.tileMarkHandler | Where-Object { $_.caseId -eq 'tm-allowed' }; $c.reason = 'NOT_YOUR_TURN' } }
        @{ Id = 'N07-reason-round-end'; Expect = 'fail'; Path = 'tileMarkHandler[tm-wrong-phase-round-end].reason'; Pattern = $reasonPattern; Apply = { param($r) $c = $r.tileMarkHandler | Where-Object { $_.caseId -eq 'tm-wrong-phase-round-end' }; $c.reason = 'NOT_YOUR_TURN' } }
        @{ Id = 'N08-reason-no-decision'; Expect = 'fail'; Path = 'tileMarkHandler[tm-no-active-decision].reason'; Pattern = $reasonPattern; Apply = { param($r) $c = $r.tileMarkHandler | Where-Object { $_.caseId -eq 'tm-no-active-decision' }; $c.reason = 'NOT_YOUR_TURN' } }
        @{ Id = 'N09-reason-deadline'; Expect = 'fail'; Path = 'tileMarkHandler[tm-deadline-expired].reason'; Pattern = $reasonPattern; Apply = { param($r) $c = $r.tileMarkHandler | Where-Object { $_.caseId -eq 'tm-deadline-expired' }; $c.reason = 'NOT_YOUR_TURN' } }
        @{ Id = 'N10-reason-mismatch'; Expect = 'fail'; Path = 'tileMarkHandler[tm-decision-mismatch].reason'; Pattern = $reasonPattern; Apply = { param($r) $c = $r.tileMarkHandler | Where-Object { $_.caseId -eq 'tm-decision-mismatch' }; $c.reason = 'NOT_YOUR_TURN' } }

        # Structural single-field negatives.
        @{ Id = 'N11-unknown-case-rename'; Expect = 'fail'; Path = 'tileMarkHandler[tm-deadline-expired].caseId -> tm-unknown-case'; Pattern = 'does not define'; Apply = { param($r) $c = $r.tileMarkHandler | Where-Object { $_.caseId -eq 'tm-deadline-expired' }; $c.caseId = 'tm-unknown-case' } }
        @{ Id = 'N12-missing-case'; Expect = 'fail'; Path = 'tileMarkHandler[tm-decision-mismatch] removed'; Pattern = 'missing V4 case|missing the bound case'; Apply = { param($r) $r.tileMarkHandler = @($r.tileMarkHandler | Where-Object { $_.caseId -ne 'tm-decision-mismatch' }) } }
        @{ Id = 'N13-missing-reason-field'; Expect = 'fail'; Path = 'tileMarkHandler[tm-no-active-decision].reason removed'; Pattern = "missing property 'reason'|is missing 'reason'"; Apply = { param($r) $c = $r.tileMarkHandler | Where-Object { $_.caseId -eq 'tm-no-active-decision' }; $c.PSObject.Properties.Remove('reason') } }
        @{ Id = 'N14-wrong-type-reason'; Expect = 'fail'; Path = 'tileMarkHandler[tm-no-active-decision].reason = 42'; Pattern = 'reason must be a string or null'; Apply = { param($r) $c = $r.tileMarkHandler | Where-Object { $_.caseId -eq 'tm-no-active-decision' }; $c.reason = 42 } }
        @{ Id = 'N15-wrong-type-outcome'; Expect = 'fail'; Path = 'tileMarkHandler[tm-deadline-expired].outcome = true'; Pattern = 'outcome must be a string'; Apply = { param($r) $c = $r.tileMarkHandler | Where-Object { $_.caseId -eq 'tm-deadline-expired' }; $c.outcome = $true } }
        @{ Id = 'N16-wrong-type-caseid'; Expect = 'fail'; Path = 'tileMarkHandler[tm-allowed].caseId = 7'; Pattern = 'caseId must be a string'; Apply = { param($r) $c = $r.tileMarkHandler | Where-Object { $_.caseId -eq 'tm-allowed' }; $c.caseId = 7 } }
        @{ Id = 'N17-wrong-type-note'; Expect = 'fail'; Path = 'tileMarkHandler[tm-allowed].note = 7'; Pattern = 'note must be a string'; Apply = { param($r) $c = $r.tileMarkHandler | Where-Object { $_.caseId -eq 'tm-allowed' }; $c.note = 7 } }
        @{ Id = 'N18-null-reason-rejected'; Expect = 'fail'; Path = 'tileMarkHandler[tm-deadline-expired].reason = null'; Pattern = 'is Rejected but has a null reason'; Apply = { param($r) $c = $r.tileMarkHandler | Where-Object { $_.caseId -eq 'tm-deadline-expired' }; $c.reason = $null } }
        @{ Id = 'N19-duplicate-case'; Expect = 'fail'; Path = 'tileMarkHandler[tm-allowed] duplicated'; Pattern = 'duplicate caseId'; Apply = { param($r) $dup = $r.tileMarkHandler | Where-Object { $_.caseId -eq 'tm-allowed' }; $r.tileMarkHandler = @($r.tileMarkHandler) + @($dup) } }
        @{ Id = 'N20-unknown-property'; Expect = 'fail'; Path = 'tileMarkHandler[tm-allowed].goldenInputs added'; Pattern = "unknown property 'goldenInputs'"; Apply = { param($r) $c = $r.tileMarkHandler | Where-Object { $_.caseId -eq 'tm-allowed' }; $c | Add-Member -NotePropertyName goldenInputs -NotePropertyValue @{} } }
        @{ Id = 'N21-unknown-extra-case'; Expect = 'fail'; Path = 'tileMarkHandler[tm-invented] added'; Pattern = 'does not define'; Apply = { param($r) $extra = [pscustomobject]@{ caseId = 'tm-invented'; note = 'probe'; outcome = 'Rejected'; reason = 'WRONG_PHASE' }; $r.tileMarkHandler = @($r.tileMarkHandler) + @($extra) } }
        @{ Id = 'N22-schema-sentinel'; Expect = 'fail'; Path = 'root.schema'; Pattern = 'schema is'; Apply = { param($r) $r.schema = 'v5-cheat-golden-v2' } }
        @{ Id = 'N23-empty-handler'; Expect = 'fail'; Path = 'tileMarkHandler = [] (structural, not single-field)'; Pattern = 'must contain the 20 V4 cases, actual 0|missing the bound case'; Apply = { param($r) $r.tileMarkHandler = @() } }
    )

    $rows = New-Object System.Collections.ArrayList
    $unexpected = 0
    $expectedTotal = $baselineResult.Total

    foreach ($case in $cases) {
        $fixtureCopy = Join-Path $EvidenceRoot ($case.Id + '.json')
        if ($case.ContainsKey('Raw') -and $case.Raw) {
            Copy-Item -LiteralPath $baselineCopy -Destination $fixtureCopy -Force
        } else {
            ConvertTo-MutatedFixture -Template $baselineCopy -Destination $fixtureCopy -Apply $case.Apply
        }
        $mutatedSha = Get-Sha256 $fixtureCopy
        $logPath = Join-Path $EvidenceRoot ($case.Id + '.log')
        $result = Invoke-GoldenRun -FixturePath $fixtureCopy -LogPath $logPath -AssemblyPath $AssemblyPath -Dotnet $DotnetPath

        $patternMatched = $true
        if ($case.ContainsKey('Pattern')) {
            $patternMatched = ($result.Text -match $case.Pattern)
        }

        if (-not $result.Parsed) {
            $script:ToolFailure = $true
            Write-Host ('TOOL-FAILURE: no test summary parsed for {0}; see {1}' -f $case.Id, $logPath)
            $unexpected++
            $status = 'TOOL-FAILURE'
        } elseif ($result.MissingFixture) {
            $script:ToolFailure = $true
            Write-Host ('TOOL-FAILURE: fixture-not-found path taken for {0}; see {1}' -f $case.Id, $logPath)
            $unexpected++
            $status = 'TOOL-FAILURE'
        } elseif ($result.Total -le 0) {
            $script:ToolFailure = $true
            Write-Host ('TOOL-FAILURE: Total={0} for {1}; zero executed tests cannot be an expected FAIL; see {2}' -f $result.Total, $case.Id, $logPath)
            $unexpected++
            $status = 'TOOL-FAILURE'
        } else {
            $countsOk = ($result.Total -eq $expectedTotal) -and
                        ($result.Errors -eq 0) -and
                        ($result.Skipped -eq 0) -and
                        ($result.NotRun -eq 0) -and
                        ($result.Total -gt 0)

            if ($case.Expect -eq 'pass') {
                $ok = $countsOk -and ($result.Exit -eq 0) -and ($result.Failed -eq 0) -and ($result.FailedMarkers -eq 0)
                $status = if ($ok) { 'PASS' } else { 'UNEXPECTED-FAIL' }
            } else {
                $ok = $countsOk -and ($result.Exit -eq 1) -and ($result.Failed -ge 1) -and
                      ($result.FailedMarkers -ge 1) -and $patternMatched
                $status = if ($ok) { 'PASS' } else { 'UNEXPECTED-PASS' }
            }

            if (-not $ok) {
                $unexpected++
                Write-Host ('UNEXPECTED: {0} expected {1} but exit={2} total={3} failed={4} errors={5} skipped={6} notRun={7} patternMatched={8}; {9}' -f `
                    $case.Id, $case.Expect, $result.Exit, $result.Total, $result.Failed, `
                    $result.Errors, $result.Skipped, $result.NotRun, $patternMatched, $logPath)
            }
        }

        $null = $rows.Add([pscustomobject]@{
            id             = $case.Id
            expectation    = $case.Expect
            jsonPath       = $case.Path
            status         = $status
            exit           = $result.Exit
            total          = $result.Total
            passed         = if ($result.Total -ge 0) { $result.Total - $result.Failed - $result.Errors } else { -1 }
            failed         = $result.Failed
            errors         = $result.Errors
            skipped        = $result.Skipped
            notRun         = $result.NotRun
            failMarkers    = $result.FailedMarkers
            patternMatched = $patternMatched
            fixtureSha     = $mutatedSha
            log            = $result.Log
        })

        Write-Step ('{0,-30} {1,-5} exit={2} total={3} failed={4} pattern={5} {6}' -f `
            $case.Id, $case.Expect, $result.Exit, $result.Total, $result.Failed, $patternMatched, $status)
    }

    $csv = $rows | ConvertTo-Csv -NoTypeInformation
    Write-Utf8NoBom (Join-Path $EvidenceRoot 'matrix.csv') (($csv -join "`r`n") + "`r`n")

    $sourceRawAfter = Get-Sha256 $FixturePath
    $sourcePreserved = ($sourceRawAfter -eq $sourceRawSha)
    if (-not $sourcePreserved) {
        Write-Host ('PROBE-FAIL: repository fixture changed during the probe: {0} -> {1}' -f $sourceRawSha, $sourceRawAfter)
    }

    $matrixPass = ($unexpected -eq 0) -and (-not $script:ToolFailure) -and $sourcePreserved
    $matrixResult = if ($matrixPass) { 'PASS' } else { 'FAIL' }
    $summary = [ordered]@{
        evidenceRoot             = $EvidenceRoot
        bindingReadiness         = $readinessStatus
        readinessRatio           = $readiness.readinessRatio
        denominator              = 5
        boundCases               = $boundCount
        blockedCases             = $blockedCount
        blockedCaseIds           = $manifestBlockedCases
        blockedReasons           = $blockedReasonRows
        faultInjectionCases      = $manifestFaultInjection
        discoveredMethods        = $list.Methods.Count
        mutationMatrix           = $matrixResult
        cases                    = $rows.Count
        positiveControls         = ($rows | Where-Object { $_.expectation -eq 'pass' }).Count
        negativeControls         = ($rows | Where-Object { $_.expectation -eq 'fail' }).Count
        expectedTotal            = $expectedTotal
        unexpectedResults        = $unexpected
        toolFailure              = $script:ToolFailure
        sourceFixtureRawSha256   = $sourceRawSha
        sourceFixtureLfSha256    = $sourceNormalizedSha
        sourceFixtureGitSha1     = $sourceGitSha1
        sourceFixturePreserved   = $sourcePreserved
        sourceFixtureRawSha256After = $sourceRawAfter
        finishedAtUtc            = (Get-Date).ToUniversalTime().ToString('o')
    }
    Write-Utf8NoBom (Join-Path $EvidenceRoot 'summary.json') ($summary | ConvertTo-Json -Depth 8)

    Write-Host ''
    Write-Host '=== PROBE SUMMARY ==='
    $rows | Format-Table id, expectation, status, exit, total, failed, errors, skipped, notRun, patternMatched -AutoSize | Out-String -Width 220 | Write-Host
    Write-Host ('bindingReadiness={0} readiness={1} denominator=5 bound={2} blocked={3} blockedCaseIds=[{4}] discoveredMethods={5} mutationMatrix={6} cases={7} positive={8} negative={9} expectedTotal={10} unexpected={11} toolFailure={12}' -f `
        $readinessStatus, $readiness.readinessRatio, $boundCount, $blockedCount, ($manifestBlockedCases -join ','), `
        $list.Methods.Count, $matrixResult, $rows.Count, $summary.positiveControls, $summary.negativeControls, `
        $expectedTotal, $unexpected, $script:ToolFailure)
    foreach ($row in $blockedReasonRows) {
        Write-Host ('BLOCKED-CASE: {0}: {1}' -f $row.caseId, $row.reason)
    }
    Write-Host ('source raw SHA256: {0}' -f $sourceRawSha)
    Write-Host ('source LF  SHA256: {0}' -f $sourceNormalizedSha)
    Write-Host ('source git  SHA1  : {0}' -f $sourceGitSha1)
    Write-Host ('evidence: {0}' -f $EvidenceRoot)

    if (-not $matrixPass) {
        Write-Host 'PROBE-FAIL: unexpected result(s) in the mutation matrix.'
        if ($script:ToolFailure) { exit 2 }
        exit 1
    }

    if ($readinessStatus -eq 'FAIL') {
        Write-Host 'PROBE-FAIL: mutation matrix passed but binding readiness is FAIL.'
        exit 2
    }

    if ($readinessStatus -eq 'BLOCKED') {
        Write-Host ('PROBE-BLOCKED: mutation matrix PASS, binding readiness BLOCKED {0}; blockedCaseIds=[{1}].' -f `
            $readiness.readinessRatio, ($manifestBlockedCases -join ','))
        foreach ($row in $blockedReasonRows) {
            Write-Host ('BLOCKED-REASON: {0}: {1}' -f $row.caseId, $row.reason)
        }
        exit 3
    }

    Write-Host 'PROBE-PASS: mutation matrix PASS and binding readiness PASS with every declared bound case.'
    exit 0
} finally {
    Restore-GoldenEnvironment $previousEnvironment
}
