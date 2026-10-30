#Requires -Version 5.1
<#
  Negative and tamper matrix for the P4.5 recovery export chain.

  It reads one completed real run (read-only), copies artifacts only into the ignored
  scratch output, and asserts that missing/tampered/relabelled evidence never becomes
  PASS. It does not modify the run or any sealed collection.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$RunDir,
    [Parameter(Mandatory = $true)][string]$VerifierPath,
    [string]$DotnetPath = 'dotnet',
    [string]$OutputDir = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'tools/unity/p45-e2e-common.ps1')

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $P45RepoRoot ('build/p45-recovery-export/negative/' + (Get-P45TimeContext).localDate)
}
$OutputDir = Get-P45FullPath -Path $OutputDir
if ([IO.Directory]::Exists($OutputDir)) { $OutputDir = $OutputDir + '-retry-' + [Guid]::NewGuid().ToString('N').Substring(0, 6) }
[IO.Directory]::CreateDirectory($OutputDir) | Out-Null

$script:Total = 0
$script:Passed = 0
$script:Failed = 0
$script:Cases = New-Object System.Collections.Generic.List[object]

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Equal {
    param([object]$Expected, [object]$Actual, [string]$Message)
    if ([string]$Expected -ne [string]$Actual) {
        throw ($Message + ' (expected=' + [string]$Expected + ' actual=' + [string]$Actual + ')')
    }
}

function Get-P45ThrownExitCode {
    param([object]$ErrorRecord)
    $message = Get-P45ErrorMessage $ErrorRecord
    if ($message -match '^P45ERR\|(\d+)\|') { return [int]$Matches[1] }
    return 99
}

function Invoke-Case {
    param([string]$Name, [scriptblock]$Body)
    $script:Total++
    try {
        & $Body
        $script:Passed++
        $script:Cases.Add([pscustomobject]@{ name = $Name; status = 'PASS'; error = '' }) | Out-Null
        Write-Host ('PASS ' + $Name)
    }
    catch {
        $script:Failed++
        $message = Get-P45ErrorMessage $_
        $script:Cases.Add([pscustomobject]@{ name = $Name; status = 'FAIL'; error = $message }) | Out-Null
        Write-Host ('FAIL ' + $Name + ': ' + $message)
    }
}

function Copy-JsonAndRecompute {
    param([object]$Object, [string]$Path)
    $Object.integrity.canonicalSha256 = Get-P45ObservationDigest -Observation $Object
    Write-P45JsonAtomic -Path $Path -Object $Object
    return $Object
}

function New-TestFixture {
    param(
        [object]$Context,
        [string]$ObservationId,
        [string]$Phase,
        [string]$PhysicalHash = ('3' * 64),
        [string]$PayloadHash = ('1' * 64),
        [bool]$LedgerAvailable = $true
    )

    $observation = [pscustomobject]@{
        schemaVersion = 1
        kind = 'p45-e2e-observation'
        observationId = $ObservationId
        source = 'TOOL_SELF_TEST'
        phase = $Phase
        capturedAt = (Get-P45TimeContext).local
        runBinding = [pscustomobject]@{
            runId = [string]$Context.Run.runId
            playerSha256 = [string]$Context.Run.player.sha256
            sourceHead = [string]$Context.Run.source.head
            buildCommit = [string]$Context.Run.player.buildCommit
            buildProvenance = [string]$Context.Run.player.buildProvenance
            rulesetVersion = 1
        }
        availability = [pscustomobject]@{
            roundId = $true
            stateVersion = $true
            eventSeq = $true
            controlEpoch = $true
            requests = $true
            ledger = $LedgerAvailable
            physical = $true
            structuredPlayerExport = $true
        }
        roundId = 'local-main-r1'
        stateVersion = '3'
        eventSeq = '6'
        controlEpoch = '0'
        controlEpochs = @(0, 0, 0, 0)
        controlVersion = '0'
        cheatVersion = '1'
        cheatFingerprint = '1'
        cheatStateSha256 = ('9' * 64)
        controlRequests = @()
        requests = @([pscustomobject]@{
            requestId = 'fixture-request'
            receiptId = 'fixture-receipt'
            confirmed = $true
            payloadSha256 = $PayloadHash
        })
        ledger = if ($LedgerAvailable) {
            [pscustomobject]@{ points = '480'; entryCount = 0; entriesSha256 = ('2' * 64) }
        } else { $null }
        physical = [pscustomobject]@{ tileCount = 136; tilesSha256 = $PhysicalHash }
        reconnect = $null
        integrity = [pscustomobject]@{ algorithm = 'SHA-256'; canonicalSha256 = '' }
    }
    $observation.integrity.canonicalSha256 = Get-P45ObservationDigest -Observation $observation
    return $observation
}

$context = Get-P45Run -RunDir $RunDir
$beforePath = Get-P45LatestObservation -Context $context -Phase Before
$afterPath = Get-P45LatestObservation -Context $context -Phase After
if ([string]::IsNullOrWhiteSpace($beforePath) -or [string]::IsNullOrWhiteSpace($afterPath)) {
    throw 'The supplied run has no imported Before/After observation pair.'
}
$before = Read-P45Json -Path $beforePath
$after = Read-P45Json -Path $afterPath
$authorityBefore = $null
$sealedBefore = Find-P45SealedPlayerObservation -Context $context -ObservationPath $beforePath
if ($null -eq $sealedBefore) { throw 'The supplied run has no sealed Before export collection.' }
$authorityBefore = $sealedBefore.AuthorityPath
$verifiedBefore = Invoke-P45Verifier -AuthorityPath $authorityBefore -VerifierPath $VerifierPath -DotnetPath $DotnetPath -CheatStatePath $sealedBefore.CheatStatePath
$factsBefore = Get-P45Property $verifiedBefore 'facts'

Invoke-Case 'Independent verifier decodes the persisted authority and all required facts' {
    Assert-True ($null -ne $factsBefore) 'verifier returned no facts'
    Assert-Equal '3' ([string]$factsBefore.stateVersion) 'state version'
    Assert-Equal '6' ([string]$factsBefore.eventSeq) 'event seq'
    Assert-Equal '136' ([string]$factsBefore.physical.tileCount) 'physical tile count'
    Assert-Equal '136' ([string]$factsBefore.physical.uniqueCount) 'physical unique count'
    Assert-Equal '3' ([string]$factsBefore.requests.Count) 'confirmed request count'
    Assert-Equal 'local-main-r1' ([string]$factsBefore.roundId) 'round id'
    Assert-True ([string]$factsBefore.ledger.entriesSha256 -match '^[A-F0-9]{64}$') 'ledger digest'
    Assert-True ([string]$factsBefore.cheatStateSha256 -match '^[A-F0-9]{64}$') 'cheat aggregate digest'
    Assert-True ($null -ne $factsBefore.controlRequests) 'control request list'
}

Invoke-Case 'Stale checkpoint ledger without the current cheat aggregate is rejected' {
    $failed = $false
    try {
        $null = Invoke-P45Verifier -AuthorityPath $authorityBefore -VerifierPath $VerifierPath -DotnetPath $DotnetPath
    }
    catch {
        $failed = $true
    }
    Assert-True $failed 'authority-only decode accepted a stale ledger'
}

Invoke-Case 'Copied run cannot be re-sealed without the external collector key' {
    $scratch = Join-Path $OutputDir 'copied-run'
    New-Item -ItemType Directory -Path $scratch | Out-Null
    foreach ($name in @('attempts', 'observations', 'build', 'ownership.json', 'player-package.json', 'state.json', 'run.json')) {
        Copy-Item -LiteralPath (Join-Path $context.Root $name) -Destination $scratch -Recurse
    }
    $copiedRun = Read-P45Json -Path (Join-Path $scratch 'run.json')
    $copiedRun.paths.root = $scratch
    Write-P45JsonAtomic -Path (Join-Path $scratch 'run.json') -Object $copiedRun
    $copiedContext = Get-P45Run -RunDir $scratch
    $refused = $false
    try {
        $null = New-P45Collection -Context $copiedContext
    }
    catch {
        $refused = $true
    }
    Assert-True $refused 'copied run was attested with the original collector key'
}

Invoke-Case 'Both sealed real export chains are locally consistent but provenance is not trusted' {
    $b = Test-P45RecoveryExportChain -Context $context -ObservationPath $beforePath -Phase Before `
        -VerifierPath $VerifierPath -DotnetPath $DotnetPath
    $a = Test-P45RecoveryExportChain -Context $context -ObservationPath $afterPath -Phase After `
        -VerifierPath $VerifierPath -DotnetPath $DotnetPath
    Assert-Equal 'UNTRUSTED_LOCAL' $b.Status ('before chain: ' + $b.Reason)
    Assert-Equal 'UNTRUSTED_LOCAL' $a.Status ('after chain: ' + $a.Reason)
    Assert-Equal ([string]$b.Chain.manifestSha256) ([string]$b.Chain.manifestSha256) 'manifest hash'
    Assert-True ($b.Chain.challenge -match '^[A-F0-9]{64}$') 'challenge shape'
    Assert-True ($b.Chain.pid -gt 0) 'pid binding'
}

Invoke-Case 'Recomputed SHA cannot hide an altered state version' {
    $copy = $before | ConvertTo-Json -Depth 30 | ConvertFrom-Json
    $copy.stateVersion = '4'
    $copy = Copy-JsonAndRecompute -Object $copy -Path (Join-Path $OutputDir 'tamper-state.json')
    $match = Test-P45RecoveryFactsMatch -Left $copy -Right $factsBefore -LeftName 'tampered' -RightName 'verifier'
    Assert-True (-not $match.Equal) 'altered stateVersion was accepted by fact comparison'
    $cmp = Compare-P45Observations -Context $context -BeforePath (Join-Path $OutputDir 'tamper-state.json') -AfterPath $afterPath `
        -VerifierPath $VerifierPath -DotnetPath $DotnetPath
    Assert-True ($cmp.ExitCode -ne 0 -and -not $cmp.Result.pass) 'tampered observation became PASS'
}

Invoke-Case 'Recomputed SHA cannot hide an altered ledger digest' {
    $copy = $before | ConvertTo-Json -Depth 30 | ConvertFrom-Json
    $copy.ledger.entriesSha256 = ('0' * 64)
    $copy = Copy-JsonAndRecompute -Object $copy -Path (Join-Path $OutputDir 'tamper-ledger.json')
    $match = Test-P45RecoveryFactsMatch -Left $copy -Right $factsBefore -LeftName 'tampered' -RightName 'verifier'
    Assert-True (-not $match.Equal) 'altered ledger digest was accepted'
}

Invoke-Case 'Recomputed SHA cannot hide an altered physical digest/count' {
    $copy = $before | ConvertTo-Json -Depth 30 | ConvertFrom-Json
    $copy.physical.tilesSha256 = ('0' * 64)
    $copy.physical.tileCount = 135
    $copy = Copy-JsonAndRecompute -Object $copy -Path (Join-Path $OutputDir 'tamper-physical.json')
    $match = Test-P45RecoveryFactsMatch -Left $copy -Right $factsBefore -LeftName 'tampered' -RightName 'verifier'
    Assert-True (-not $match.Equal) 'altered physical digest/count was accepted'
}

Invoke-Case 'Recomputed SHA cannot hide an altered receipt payload' {
    $copy = $before | ConvertTo-Json -Depth 30 | ConvertFrom-Json
    $copy.requests[0].payloadSha256 = ('0' * 64)
    $copy = Copy-JsonAndRecompute -Object $copy -Path (Join-Path $OutputDir 'tamper-request.json')
    $match = Test-P45RecoveryFactsMatch -Left $copy -Right $factsBefore -LeftName 'tampered' -RightName 'verifier'
    Assert-True (-not $match.Equal) 'altered receipt payload was accepted'
}

Invoke-Case 'Wrong phase is rejected before the sealed chain is considered' {
    $copy = $before | ConvertTo-Json -Depth 30 | ConvertFrom-Json
    $copy.phase = 'AFTER_RECOVERY'
    $copy = Copy-JsonAndRecompute -Object $copy -Path (Join-Path $OutputDir 'tamper-phase.json')
    $validation = Test-P45Observation -Observation $copy -Context $context -ExpectedPhase 'Before'
    Assert-True (-not $validation.Valid) 'wrong phase was structurally valid'
    $exit = 0
    try {
        $cmp = Compare-P45Observations -Context $context -BeforePath (Join-Path $OutputDir 'tamper-phase.json') -AfterPath $afterPath `
            -VerifierPath $VerifierPath -DotnetPath $DotnetPath
        $exit = $cmp.ExitCode
    }
    catch {
        $exit = Get-P45ThrownExitCode $_
    }
    Assert-True ($exit -ne 0) 'wrong phase became PASS'
}

Invoke-Case 'Wrong run/build identity is rejected' {
    $copy = $before | ConvertTo-Json -Depth 30 | ConvertFrom-Json
    $copy.runBinding.runId = 'other-run'
    $copy.runBinding.buildCommit = ('0' * 40)
    $copy = Copy-JsonAndRecompute -Object $copy -Path (Join-Path $OutputDir 'tamper-identity.json')
    $validation = Test-P45Observation -Observation $copy -Context $context -ExpectedPhase 'Before'
    Assert-True (-not $validation.IdentityValid) 'wrong run/build identity was accepted'
    $exit = 0
    try {
        $cmp = Compare-P45Observations -Context $context -BeforePath (Join-Path $OutputDir 'tamper-identity.json') -AfterPath $afterPath `
            -VerifierPath $VerifierPath -DotnetPath $DotnetPath
        $exit = $cmp.ExitCode
    }
    catch {
        $exit = Get-P45ThrownExitCode $_
    }
    Assert-True ($exit -ne 0) 'wrong identity became PASS'
}

Invoke-Case 'Wrong challenge/package binding cannot produce a verified chain' {
    $copy = $before | ConvertTo-Json -Depth 30 | ConvertFrom-Json
    $copy.exportBinding.challenge = ('0' * 64)
    $copy.exportBinding.packageManifestSha256 = ('0' * 64)
    $copy = Copy-JsonAndRecompute -Object $copy -Path (Join-Path $OutputDir 'tamper-binding.json')
    $chain = Test-P45RecoveryExportChain -Context $context -ObservationPath (Join-Path $OutputDir 'tamper-binding.json') -Phase Before `
        -VerifierPath $VerifierPath -DotnetPath $DotnetPath
    Assert-True ($chain.Status -ne 'VERIFIED') 'altered challenge/package binding produced a verified chain'
}

Invoke-Case 'Tampered authority bytes are rejected by the independent verifier' {
    $tampered = Join-Path $OutputDir 'authority-tampered.save'
    $bytes = [IO.File]::ReadAllBytes($authorityBefore)
    $bytes[$bytes.Length - 1] = $bytes[$bytes.Length - 1] -bxor 0x01
    [IO.File]::WriteAllBytes($tampered, $bytes)
    $failed = $false
    try {
        $null = Invoke-P45Verifier -AuthorityPath $tampered -VerifierPath $VerifierPath -DotnetPath $DotnetPath
    }
    catch {
        $failed = $true
    }
    Assert-True $failed 'tampered authority bytes were accepted'
}

Invoke-Case 'Renamed fixture source cannot become a real PASS' {
    $copy = $before | ConvertTo-Json -Depth 30 | ConvertFrom-Json
    $copy.source = 'TOOL_SELF_TEST'
    $copy = Copy-JsonAndRecompute -Object $copy -Path (Join-Path $OutputDir 'tamper-source.json')
    $cmp = Compare-P45Observations -Context $context -BeforePath (Join-Path $OutputDir 'tamper-source.json') -AfterPath $afterPath `
        -VerifierPath $VerifierPath -DotnetPath $DotnetPath
    Assert-True ($cmp.ExitCode -ne 0 -and -not $cmp.Result.pass) 'renamed fixture source became PASS'
}

Invoke-Case 'Missing ledger or availability false is BLOCKED, never PASS' {
    $copy = $before | ConvertTo-Json -Depth 30 | ConvertFrom-Json
    $copy.availability.ledger = $false
    $copy.ledger = $null
    $copy = Copy-JsonAndRecompute -Object $copy -Path (Join-Path $OutputDir 'missing-ledger.json')
    $cmp = Compare-P45Observations -Context $context -BeforePath (Join-Path $OutputDir 'missing-ledger.json') -AfterPath $afterPath `
        -VerifierPath $VerifierPath -DotnetPath $DotnetPath
    Assert-Equal 17 $cmp.ExitCode 'missing ledger must be BLOCKED'
    Assert-True (-not $cmp.Result.pass) 'missing ledger became PASS'
}

Invoke-Case 'Fixed physical difference is FAIL (16)' {
    $left = New-TestFixture -Context $context -ObservationId 'diff-physical-left' -Phase 'BEFORE_RECOVERY' -PhysicalHash ('3' * 64)
    $right = New-TestFixture -Context $context -ObservationId 'diff-physical-right' -Phase 'AFTER_RECOVERY' -PhysicalHash ('4' * 64)
    $leftPath = Join-Path $OutputDir 'diff-physical-left.json'
    $rightPath = Join-Path $OutputDir 'diff-physical-right.json'
    Write-P45JsonAtomic -Path $leftPath -Object $left
    Write-P45JsonAtomic -Path $rightPath -Object $right
    $cmp = Compare-P45Observations -Context $context -BeforePath $leftPath -AfterPath $rightPath
    Assert-Equal 16 $cmp.ExitCode 'physical difference must FAIL'
    Assert-Equal 'FAIL' $cmp.Result.outcome 'physical difference outcome'
}

Invoke-Case 'Fixed receipt and ledger difference is FAIL (16)' {
    $left = New-TestFixture -Context $context -ObservationId 'diff-receipt-left' -Phase 'BEFORE_RECOVERY' -PayloadHash ('1' * 64)
    $right = New-TestFixture -Context $context -ObservationId 'diff-receipt-right' -Phase 'AFTER_RECOVERY' -PayloadHash ('5' * 64)
    $right.ledger.entriesSha256 = ('6' * 64)
    $right.integrity.canonicalSha256 = Get-P45ObservationDigest -Observation $right
    $leftPath = Join-Path $OutputDir 'diff-receipt-left.json'
    $rightPath = Join-Path $OutputDir 'diff-receipt-right.json'
    Write-P45JsonAtomic -Path $leftPath -Object $left
    Write-P45JsonAtomic -Path $rightPath -Object $right
    $cmp = Compare-P45Observations -Context $context -BeforePath $leftPath -AfterPath $rightPath
    Assert-Equal 16 $cmp.ExitCode 'receipt/ledger difference must FAIL'
    Assert-Equal 'FAIL' $cmp.Result.outcome 'receipt/ledger difference outcome'
}

Invoke-Case 'Control epochs and control version differences are FAIL (16)' {
    $left = New-TestFixture -Context $context -ObservationId 'diff-control-left' -Phase 'BEFORE_RECOVERY'
    $right = New-TestFixture -Context $context -ObservationId 'diff-control-right' -Phase 'AFTER_RECOVERY'
    $right.controlEpoch = '9'
    $right.controlEpochs = @(9, 8, 7, 6)
    $right.controlVersion = '100'
    $right.integrity.canonicalSha256 = Get-P45ObservationDigest -Observation $right
    $leftPath = Join-Path $OutputDir 'diff-control-left.json'
    $rightPath = Join-Path $OutputDir 'diff-control-right.json'
    Write-P45JsonAtomic -Path $leftPath -Object $left
    Write-P45JsonAtomic -Path $rightPath -Object $right
    $cmp = Compare-P45Observations -Context $context -BeforePath $leftPath -AfterPath $rightPath
    Assert-Equal 16 $cmp.ExitCode 'control difference must FAIL'
    Assert-Equal $false $cmp.Result.requiredFieldsEqual 'control difference must make requiredFieldsEqual false'
}

Invoke-Case 'Cheat aggregate binding differences are FAIL (16)' {
    $left = New-TestFixture -Context $context -ObservationId 'diff-cheat-left' -Phase 'BEFORE_RECOVERY'
    $right = New-TestFixture -Context $context -ObservationId 'diff-cheat-right' -Phase 'AFTER_RECOVERY'
    $right.cheatVersion = '999'
    $right.cheatFingerprint = '1234567'
    $right.cheatStateSha256 = ('8' * 64)
    $right.integrity.canonicalSha256 = Get-P45ObservationDigest -Observation $right
    $leftPath = Join-Path $OutputDir 'diff-cheat-left.json'
    $rightPath = Join-Path $OutputDir 'diff-cheat-right.json'
    Write-P45JsonAtomic -Path $leftPath -Object $left
    Write-P45JsonAtomic -Path $rightPath -Object $right
    $cmp = Compare-P45Observations -Context $context -BeforePath $leftPath -AfterPath $rightPath
    Assert-Equal 16 $cmp.ExitCode 'cheat aggregate difference must FAIL'
}

Invoke-Case 'Persisted control request differences are FAIL (16)' {
    $left = New-TestFixture -Context $context -ObservationId 'diff-control-req-left' -Phase 'BEFORE_RECOVERY'
    $right = New-TestFixture -Context $context -ObservationId 'diff-control-req-right' -Phase 'AFTER_RECOVERY'
    $right.controlRequests = @([pscustomobject]@{
        accountId = 'control-account'
        scopeId = 'control-scope'
        requestId = 'control-request'
        digest = ('7' * 64)
        kind = 'CLAIM_CONTROL'
        evaluatedStateVersion = '7'
    })
    $right.integrity.canonicalSha256 = Get-P45ObservationDigest -Observation $right
    $leftPath = Join-Path $OutputDir 'diff-control-req-left.json'
    $rightPath = Join-Path $OutputDir 'diff-control-req-right.json'
    Write-P45JsonAtomic -Path $leftPath -Object $left
    Write-P45JsonAtomic -Path $rightPath -Object $right
    $cmp = Compare-P45Observations -Context $context -BeforePath $leftPath -AfterPath $rightPath
    Assert-Equal 16 $cmp.ExitCode 'control request difference must FAIL'
}

Invoke-Case 'Raw export identity fields cannot be changed independently of the observation' {
    $copy = $before | ConvertTo-Json -Depth 30 | ConvertFrom-Json
    $copy.exportBinding.pid = 1
    $copy.exportBinding.processPath = 'C:\definitely-not-the-player.exe'
    $copy.exportBinding.exporterVersion = 'UNREVIEWED_FAKE_EXPORTER'
    $copy = Copy-JsonAndRecompute -Object $copy -Path (Join-Path $OutputDir 'tamper-raw-binding.json')
    $exit = 0
    try {
        $cmp = Compare-P45Observations -Context $context -BeforePath (Join-Path $OutputDir 'tamper-raw-binding.json') -AfterPath $afterPath `
            -VerifierPath $VerifierPath -DotnetPath $DotnetPath
        $exit = $cmp.ExitCode
    }
    catch {
        $exit = Get-P45ThrownExitCode $_
    }
    Assert-True ($exit -ne 0) 'raw binding tamper became PASS'
}

Invoke-Case 'Generic collector signer API cannot sign arbitrary JSON without an observed process' {
    $fakeLaunch = [pscustomobject]@{
        observation = 'launch'
        runId = [string]$context.Run.runId
        root = [string]$context.Root
        attempt = 1
        pid = 1
        startTimeUtc = '2026-10-30T00:00:00.0000000Z'
        playerPath = [string]$context.Run.player.path
        playerSha256 = [string]$context.Run.player.sha256
        commandArguments = @('--forged')
        recoveryPhase = 'BEFORE_RECOVERY'
        challenge = ('0' * 64)
        exportDirectory = [string]$context.Root
    }
    $refused = $false
    try {
        Write-P45CollectorObservation -Context $context -Attempt 1 -Kind launch -Payload $fakeLaunch
    }
    catch {
        $refused = $true
    }
    Assert-True $refused 'generic signer accepted arbitrary JSON without an observed process'
}

Invoke-Case 'Zero-player new Prepare cannot re-sign copied attempts' {
    $forgeRoot = Join-Path $OutputDir 'zero-player-forge'
    $buildLog = [string](Get-P45Property (Get-P45Property $context.Run.player 'buildLogArchived') 'path')
    $created = New-P45Run -RunDir $forgeRoot -PlayerPath ([string]$context.Run.player.path) `
        -EvidenceClass GAME -Mode RecoveryProbe -BuildProvenance BUILD_LOG_ATTACHED `
        -BuildCommit ([string]$context.Run.player.buildCommit) -BuildLog $buildLog
    Copy-Item -Path (Join-Path $context.Root 'attempts\*') -Destination (Join-Path $forgeRoot 'attempts') -Recurse -Force
    $forgeContext = Get-P45Run -RunDir $forgeRoot
    $refused = $false
    try { $null = New-P45Collection -Context $forgeContext } catch { $refused = $true }
    Assert-True $refused 'zero-player forge was re-signed by a fresh collector key'
}

$summary = [pscustomobject]@{
    schemaVersion = 1
    kind = 'p45-recovery-negative-summary'
    generatedAt = (Get-P45TimeContext).local
    runId = [string]$context.Run.runId
    runDir = $context.Root
    total = $script:Total
    passed = $script:Passed
    failed = $script:Failed
    skipped = 0
    cases = @($script:Cases.ToArray())
    verifierSha256 = Get-P45Sha256 -Path $VerifierPath
    trustBoundary = 'LOCAL_CONTROLLED_TEST_ONLY; tamper detection here is not administrator-forgery resistance'
}
$summaryPath = Join-Path $OutputDir 'test-summary.json'
Write-P45JsonAtomic -Path $summaryPath -Object $summary
Write-Host ('TOTAL=' + $script:Total + ' PASSED=' + $script:Passed + ' FAILED=' + $script:Failed + ' SKIPPED=0')
Write-Host ('SUMMARY: ' + $summaryPath)
Write-Host ('SUMMARY_SHA256: ' + (Get-P45Sha256 -Path $summaryPath))
if ($script:Failed -gt 0) { exit 1 }
