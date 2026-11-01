<#
.SYNOPSIS
    C04-G3 fixed Windows Player cheat probe runner.

.DESCRIPTION
    Windows PowerShell 5.1 compatible. This is a bounded evidence runner, not a
    whole-match or random-action driver. It refuses to overwrite an existing
    evidence root, acquires Local\MahjongV5P45UnityValidation for the shared
    plugin/Unity/Player stages, records native exits and hashes every artifact,
    and never edits tracked assets.

.PARAMETER Case
    authorized, negative, or all. Default authorized.
#>
[CmdletBinding()]
param(
    [ValidateSet('authorized', 'negative', 'all')]
    [string]$Case = 'authorized',
    [string]$EvidenceRoot,
    [string]$PlayerPath,
    [string]$SourceHead,
    [string]$BuildCommit = 'unknown',
    [string]$UnityExe = 'D:\Unity\Hub\Editor\6000.3.27f1\Editor\Unity.exe',
    [string]$ProjectPath,
    [string]$BuildReport,
    [switch]$SkipBuild,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $ProjectPath) { $ProjectPath = Join-Path $root 'unity\Mahjong.Unity' }
$mutexName = 'Local\MahjongV5P45UnityValidation'

function Assert-C04Evidence {
    param(
        [Parameter(Mandatory = $true)] $Evidence,
        [Parameter(Mandatory = $true)] [string]$ExpectedCase,
        [Parameter(Mandatory = $true)] [int]$NativeExit,
        [Parameter(Mandatory = $true)] [string]$ExpectedSourceHead
    )

    $errors = New-Object System.Collections.Generic.List[string]
    if ($NativeExit -ne 0) { $errors.Add("NATIVE_EXIT_$NativeExit") | Out-Null }
    if ([int]$Evidence.schemaVersion -ne 1) { $errors.Add('SCHEMA_VERSION') | Out-Null }
    if ([string]$Evidence.layer -ne 'WINDOWS_PLAYER') { $errors.Add('LAYER') | Out-Null }
    if ([string]::IsNullOrWhiteSpace([string]$Evidence.scenarioVersion)) { $errors.Add('SCENARIO_VERSION') | Out-Null }
    if ([string]$Evidence.caseName -ne $ExpectedCase) { $errors.Add('CASE_NAME') | Out-Null }
    if ([string]$Evidence.sourceHead -ne $ExpectedSourceHead) { $errors.Add('SOURCE_HEAD') | Out-Null }
    if ([string]::IsNullOrWhiteSpace([string]$Evidence.unityVersion)) { $errors.Add('UNITY_VERSION') | Out-Null }

    $assertions = @($Evidence.assertions)
    if ($assertions.Count -eq 0) { $errors.Add('NO_ASSERTIONS') | Out-Null }
    $ids = @{}
    foreach ($assertion in $assertions) {
        $id = [string]$assertion.id
        if ([string]::IsNullOrWhiteSpace($id)) { $errors.Add('EMPTY_ASSERTION_ID') | Out-Null; continue }
        if ($ids.ContainsKey($id)) { $errors.Add('DUPLICATE_ASSERTION_' + $id) | Out-Null }
        $ids[$id] = $true
        if ($assertion.pass -ne $true -and $assertion.pass -ne $false) {
            $errors.Add('ASSERTION_NOT_BOOLEAN_' + $id) | Out-Null
        }
    }

    if ($ExpectedCase -eq 'authorized') {
        foreach ($required in @('G3-01-UI-ENTRY', 'G3-02-UI-ENTRY', 'G3-03-RECEIPT', 'G3-04-BEAM-FEE', 'G3-05-BEFORE')) {
            if (-not $ids.ContainsKey($required)) { $errors.Add('MISSING_ASSERTION_' + $required) | Out-Null }
        }
    }
    elseif ($ExpectedCase -eq 'negative') {
        foreach ($required in @('G3-06-UNAUTHORIZED-REJECTED', 'G3-06-INSUFFICIENT-REJECTED', 'G3-06-UNAUTHORIZED-ZERO', 'G3-06-INSUFFICIENT-ZERO')) {
            if (-not $ids.ContainsKey($required)) { $errors.Add('MISSING_ASSERTION_' + $required) | Out-Null }
        }
    }

    $requests = @($Evidence.requests)
    if ($requests.Count -eq 0) { $errors.Add('NO_REQUESTS') | Out-Null }
    foreach ($request in $requests) {
        $requestId = [string]$request.requestId
        if ($requestId -notmatch '^[0-9a-fA-F]{32}$') { $errors.Add('BAD_REQUEST_ID') | Out-Null }
        if ([string]::IsNullOrWhiteSpace([string]$request.kind)) { $errors.Add('MISSING_REQUEST_KIND') | Out-Null }
        if ([string]::IsNullOrWhiteSpace([string]$request.status)) { $errors.Add('MISSING_RECEIPT_STATUS') | Out-Null }
        if ([string]$request.payloadSha256 -notmatch '^[0-9A-Fa-f]{64}$') { $errors.Add('BAD_CANONICAL_HASH') | Out-Null }
        if ([string]::IsNullOrWhiteSpace([string]$request.rawPayloadSha256) -or
            [string]$request.rawPayloadSha256 -notmatch '^[0-9A-Fa-f]{64}$') {
            $errors.Add('MISSING_RAW_HASH') | Out-Null
        }
    }

    $blocked = @($Evidence.blockedRows)
    if (($blocked | Where-Object { $_ -notin @('G3-01', 'G3-02', 'G3-03', 'G3-04', 'G3-05', 'G3-06') }).Count -gt 0) {
        $errors.Add('UNKNOWN_BLOCKED_ROW') | Out-Null
    }

    return $errors
}

function Assert-C04EvidenceStrict {
    param(
        [Parameter(Mandatory = $true)] $Evidence,
        [Parameter(Mandatory = $true)] [string]$ExpectedCase,
        [Parameter(Mandatory = $true)] [int]$NativeExit,
        [Parameter(Mandatory = $true)] [string]$ExpectedSourceHead
    )

    $errors = New-Object System.Collections.Generic.List[string]
    if ($NativeExit -ne 0) { $errors.Add("NATIVE_EXIT_$NativeExit") | Out-Null }
    if ([int]$Evidence.schemaVersion -ne 1) { $errors.Add('SCHEMA_VERSION') | Out-Null }
    if ([string]$Evidence.layer -ne 'WINDOWS_PLAYER') { $errors.Add('LAYER') | Out-Null }
    if ([string]$Evidence.caseName -ne $ExpectedCase) { $errors.Add('CASE_NAME') | Out-Null }
    if ([string]$Evidence.sourceHead -ne $ExpectedSourceHead) { $errors.Add('SOURCE_HEAD') | Out-Null }
    if ([string]::IsNullOrWhiteSpace([string]$Evidence.unityVersion)) { $errors.Add('UNITY_VERSION') | Out-Null }

    if ([string]$Evidence.status -ne 'PASS') { $errors.Add('EVIDENCE_STATUS_NOT_PASS') | Out-Null }
    if ([string]$Evidence.scenarioVersion -ne 'c04-g3-player-cheats-v1') { $errors.Add('SCENARIO_VERSION_EXACT') | Out-Null }
    if (@($Evidence.blockedRows).Count -gt 0) { $errors.Add('BLOCKED_ROWS_PRESENT') | Out-Null }

    $assertionIds = @{}
    foreach ($assertion in @($Evidence.assertions)) {
        $aid = [string]$assertion.id
        if ($assertionIds.ContainsKey($aid)) { $errors.Add('DUPLICATE_ASSERTION_' + $aid) | Out-Null }
        $assertionIds[$aid] = $true
        if ($assertion.pass -isnot [bool]) { $errors.Add('ASSERTION_NOT_BOOLEAN_' + $aid) | Out-Null }
        if ([string]$assertion.expected -in @('x','invented','observed') -or
            [string]$assertion.actual -in @('y','invented','observed')) {
            $errors.Add('PLACEHOLDER_OBSERVATION_' + $aid) | Out-Null
        }
    }

    $allowedKinds = @('SubmitTileMark','SubmitSleeveBloom','SubmitSleeveBloomChoice','SubmitBeamSwap','SubmitBeamSwapChoice')
    $seenKinds = @{}
    foreach ($request in @($Evidence.requests)) {
        if ([string]$request.requestId -notmatch '^[0-9a-fA-F]{32}$') { $errors.Add('BAD_REQUEST_ID') | Out-Null }
        if ($allowedKinds -notcontains [string]$request.kind) { $errors.Add('BAD_REQUEST_KIND') | Out-Null }
        $seenKinds[[string]$request.kind] = $true
        if ([string]$request.roundId -ne 'c04-g3-r1') { $errors.Add('FOREIGN_ROUND') | Out-Null }
        if (-not ($request.PSObject.Properties.Name -contains 'controlEpoch')) {
            $errors.Add('MISSING_CONTROL_EPOCH') | Out-Null
        }
        elseif ($request.controlEpoch -is [bool] -or $request.controlEpoch -is [string] -or $null -eq $request.controlEpoch) {
            $errors.Add('CONTROL_EPOCH_NOT_NUMERIC') | Out-Null
        }
        elseif ([double]$request.controlEpoch % 1 -ne 0) {
            $errors.Add('FRACTIONAL_CONTROL_EPOCH') | Out-Null
        }
        elseif ([long]$request.controlEpoch -ne 0) { $errors.Add('FOREIGN_CONTROL_EPOCH') | Out-Null }
        if ([string]$request.seat -ne '0') { $errors.Add('FOREIGN_SEAT') | Out-Null }
        if (-not ($request.PSObject.Properties.Name -contains 'stateVersionBefore') -or
            -not ($request.PSObject.Properties.Name -contains 'stateVersionAfter')) {
            $errors.Add('MISSING_STATE_VERSION') | Out-Null
        }
        elseif ([long]$request.stateVersionAfter -lt [long]$request.stateVersionBefore) {
            $errors.Add('STATE_VERSION_REGRESSION') | Out-Null
        }
        if ([string]$request.rawPayloadSha256 -eq [string]$request.payloadSha256) {
            $errors.Add('RAW_HASH_EQUALS_CANONICAL') | Out-Null
        }
    }

    if ($ExpectedCase -eq 'authorized') {
        foreach ($requiredKind in $allowedKinds) {
            if (-not $seenKinds.ContainsKey($requiredKind)) { $errors.Add('MISSING_ACTION_' + $requiredKind) | Out-Null }
        }
        if (-not ($assertionIds.ContainsKey('G3-05-FINAL'))) { $errors.Add('MISSING_G3_05_FINAL') | Out-Null }
    }

    $assertions = @($Evidence.assertions)
    foreach ($assertion in $assertions) {
        if ($assertion.pass -ne $true) { $errors.Add('ASSERTION_FAILED_' + [string]$assertion.id) | Out-Null }
    }

    $requests = @($Evidence.requests)
    $ids = @{}
    foreach ($request in $requests) {
        $id = [string]$request.requestId
        if ($ids.ContainsKey($id)) { $errors.Add('DUPLICATE_REQUEST_' + $id) | Out-Null }
        $ids[$id] = $true
        if ([string]::IsNullOrWhiteSpace([string]$request.roundId)) { $errors.Add('MISSING_ROUND') | Out-Null }
        if ([string]::IsNullOrWhiteSpace([string]$request.decisionId)) { $errors.Add('MISSING_DECISION') | Out-Null }
        if ([int]$request.seat -ne 0) { $errors.Add('WRONG_SEAT_' + [string]$request.seat) | Out-Null }
        if ([string]$request.payloadSha256 -notmatch '^[0-9A-Fa-f]{64}$') { $errors.Add('BAD_CANONICAL_HASH') | Out-Null }
        if ([string]$request.rawPayloadSha256 -notmatch '^[0-9A-Fa-f]{64}$') { $errors.Add('BAD_RAW_HASH') | Out-Null }
    }

    if ($ExpectedCase -eq 'authorized') {
        foreach ($required in @('G3-01-RECEIPT','G3-02-RECEIPT','G3-02-CHOICE-RECEIPT','G3-03-RECEIPT','G3-03-CHOICE-RECEIPT','G3-04-FINAL-LEDGER')) {
            if (-not ($assertions | Where-Object { $_.id -eq $required -and $_.pass -eq $true })) {
                $errors.Add('MISSING_SUCCESS_ASSERTION_' + $required) | Out-Null
            }
        }
        $applied = @($requests | Where-Object { $_.status -eq 'APPLIED' })
        if ($applied.Count -lt 5) { $errors.Add('SUCCESS_REQUESTS_LT_5') | Out-Null }
        foreach ($request in $requests) {
            if ($request.status -ne 'APPLIED') { $errors.Add('NON_APPLIED_IN_AUTHORIZED') | Out-Null }
        }
    }
    elseif ($ExpectedCase -eq 'negative') {
        foreach ($required in @('G3-06-UNAUTHORIZED-REJECTED','G3-06-INSUFFICIENT-REJECTED','G3-06-UNAUTHORIZED-ZERO','G3-06-INSUFFICIENT-ZERO')) {
            if (-not ($assertions | Where-Object { $_.id -eq $required -and $_.pass -eq $true })) {
                $errors.Add('MISSING_NEGATIVE_ASSERTION_' + $required) | Out-Null
            }
        }
        $rejected = @($requests | Where-Object { $_.status -eq 'REJECTED' })
        if ($rejected.Count -lt 2) {
            $errors.Add('REJECTED_REQUEST_LT_2') | Out-Null
        }
        if (@($rejected | Select-Object -ExpandProperty requestId -Unique).Count -lt 2) {
            $errors.Add('REJECTED_IDS_NOT_DISTINCT') | Out-Null
        }
        if (@($rejected | Select-Object -ExpandProperty reason -Unique).Count -lt 2) {
            $errors.Add('REJECTED_REASONS_NOT_DISTINCT') | Out-Null
        }
    }

    return $errors
}
function Write-C04Manifest {
    param([string]$EvidenceRoot)
    $manifest = Join-Path $EvidenceRoot 'manifest.csv'
    $rows = foreach ($file in Get-ChildItem -Path $EvidenceRoot -File -Recurse |
                        Where-Object { $_.Name -notmatch '^manifest\.csv$' } |
                        Sort-Object FullName) {
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash
        '"{0}","{1}","{2}"' -f $file.FullName.Replace('"', '""'), $file.Length, $hash
    }
    '"path","bytes","sha256"' | Set-Content -LiteralPath $manifest -Encoding UTF8
    $rows | Add-Content -LiteralPath $manifest -Encoding UTF8
    return $manifest
}

if ($SelfTest) {
    return
}

if ([string]::IsNullOrWhiteSpace($EvidenceRoot) -or
    [string]::IsNullOrWhiteSpace($PlayerPath) -or
    [string]::IsNullOrWhiteSpace($SourceHead)) {
    throw 'C04 requires -EvidenceRoot, -PlayerPath and -SourceHead.'
}

if (Test-Path -LiteralPath $EvidenceRoot) {
    throw "REFUSE_OVERWRITE: evidence root already exists: $EvidenceRoot"
}

if ($SkipBuild -and -not (Test-Path -LiteralPath $PlayerPath)) {
    throw "PLAYER_NOT_FOUND: $PlayerPath"
}

New-Item -ItemType Directory -Force -Path $EvidenceRoot | Out-Null
$EvidenceRoot = (Resolve-Path -LiteralPath $EvidenceRoot).Path
if (-not $EvidenceRoot.StartsWith((Join-Path $root 'build') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "EVIDENCE_ROOT_OUTSIDE_BUILD: $EvidenceRoot"
}
$playerHash = ''
$playerBytes = 0
if (Test-Path -LiteralPath $PlayerPath) {
    $playerHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $PlayerPath).Hash
    $playerBytes = (Get-Item -LiteralPath $PlayerPath).Length
}

$mutex = New-Object System.Threading.Mutex($false, $mutexName)
$acquired = $false
$results = New-Object System.Collections.Generic.List[string]
try {
    Write-Host "WAIT c04-player mutex=$mutexName"
    $acquired = $mutex.WaitOne([TimeSpan]::FromMinutes(60))
    if (-not $acquired) { throw 'MUTEX_TIMEOUT' }

    if (-not $SkipBuild) {
        $buildLog = Join-Path $EvidenceRoot 'build-shared.log'
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\unity\build-shared.ps1') -Configuration Release 2>&1 |
            Tee-Object -FilePath $buildLog
        if ($LASTEXITCODE -ne 0) { throw "BUILD_SHARED_EXIT_$LASTEXITCODE" }

        $guardLog = Join-Path $EvidenceRoot 'check-shared-plugins.log'
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\unity\check-shared-plugins.ps1') 2>&1 |
            Tee-Object -FilePath $guardLog
        if ($LASTEXITCODE -ne 0) { throw "SHARED_PLUGIN_GUARD_EXIT_$LASTEXITCODE" }

        if (-not $BuildReport) { $BuildReport = Join-Path $EvidenceRoot 'player-build.json' }
        $buildPlayerLog = Join-Path $EvidenceRoot 'player-build.log'
        $p = Start-Process -FilePath $UnityExe -ArgumentList @(
            '-batchmode', '-nographics', '-quit', '-projectPath', $ProjectPath,
            '-executeMethod', 'C04PlayerCheatBuild.Build',
            '-c04BuildOutput', $PlayerPath,
            '-c04BuildReport', $BuildReport,
            '-logFile', $buildPlayerLog) -PassThru -Wait -WindowStyle Hidden
        $results.Add("player-build exit=$($p.ExitCode)") | Out-Null
        if ($p.ExitCode -ne 0) { throw "PLAYER_BUILD_EXIT_$($p.ExitCode)" }
        $playerHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $PlayerPath).Hash
        $playerBytes = (Get-Item -LiteralPath $PlayerPath).Length
    }

    $cases = if ($Case -eq 'all') { @('authorized', 'negative') } else { @($Case) }
    $summary = New-Object System.Collections.Generic.List[string]
    $summary.Add("sourceHead=$SourceHead") | Out-Null
    $summary.Add("buildCommit=$BuildCommit") | Out-Null
    $summary.Add("playerPath=$PlayerPath") | Out-Null
    $summary.Add("playerBytes=$playerBytes") | Out-Null
    $summary.Add("playerSha256=$playerHash") | Out-Null

    foreach ($caseName in $cases) {
        $caseDir = Join-Path $EvidenceRoot $caseName
        New-Item -ItemType Directory -Force -Path $caseDir | Out-Null
        $playerLog = Join-Path $caseDir 'player.log'
        $p = Start-Process -FilePath $PlayerPath -ArgumentList @(
            '-c04-player-cheats', $caseDir,
            '-c04-player-cheats-case', $caseName,
            '-c04-source-head', $SourceHead,
            '-c04-build-commit', $BuildCommit,
            '-c04-player-path', $PlayerPath,
            '-c04-unity-version', '6000.3.27f1',
            '-screen-width', '1280', '-screen-height', '720',
            '-logFile', $playerLog) -PassThru -Wait -WindowStyle Hidden
        $nativeExit = $p.ExitCode
        $evidencePath = Join-Path $caseDir ('case-' + $caseName + '.json')
        $errors = New-Object System.Collections.Generic.List[string]
        if (-not (Test-Path -LiteralPath $evidencePath)) {
            $errors.Add('MISSING_EVIDENCE_JSON') | Out-Null
        }
        else {
            try {
                $evidence = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
                $errors.AddRange([string[]](Assert-C04EvidenceStrict -Evidence $evidence -ExpectedCase $caseName -NativeExit $nativeExit -ExpectedSourceHead $SourceHead))
            }
            catch {
                $errors.Add('EVIDENCE_PARSE_' + $_.Exception.Message) | Out-Null
            }
        }

        $status = if ($nativeExit -eq 0 -and $errors.Count -eq 0) { 'PASS' } else { 'FAIL' }
        $results.Add("case=$caseName nativeExit=$nativeExit status=$status errors=$($errors -join ',')") | Out-Null
        $summary.Add("case=$caseName nativeExit=$nativeExit status=$status errors=$($errors -join ',')") | Out-Null
    }

    $summaryPath = Join-Path $EvidenceRoot 'summary.txt'
    $summary | Set-Content -LiteralPath $summaryPath -Encoding UTF8
    $manifest = Write-C04Manifest -EvidenceRoot $EvidenceRoot
    $results | ForEach-Object { Write-Host $_ }
    Write-Host "MANIFEST $manifest"
    Write-Host "SUMMARY $summaryPath"

    $failed = @($results | Where-Object { $_ -match 'status=FAIL' }).Count
    exit $(if ($failed -gt 0) { 1 } else { 0 })
}
finally {
    if ($acquired) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
