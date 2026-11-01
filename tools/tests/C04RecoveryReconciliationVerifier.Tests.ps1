#Requires -Version 5.1
<#
.SYNOPSIS
    Tool-level positive/negative matrix for verify-p45-c04-recovery-reconciliation.ps1.

.DESCRIPTION
    Builds synthetic but schema-exact result/frame/expected/input fixtures in the
    ignored scratch directory and asserts that the verifier accepts the three
    expected outcomes only when the canonical request, real lookup, effect vector,
    zero-send boundary and frame hashes all agree. Every negative case must exit 1.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$VerifierPath,
    [string]$OutputDir = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repo ('build/c04-recovery-verifier-tests/' + [Guid]::NewGuid().ToString('N'))
}
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
if (Test-Path -LiteralPath $OutputDir) { throw ('OUTPUT_REUSE ' + $OutputDir) }
[IO.Directory]::CreateDirectory($OutputDir) | Out-Null

$script:Total = 0
$script:Passed = 0
$script:Failed = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Get-Sha {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Write-Json {
    param([string]$Path, $Object)
    $json = $Object | ConvertTo-Json -Depth 20
    [IO.File]::WriteAllText($Path, $json, (New-Object Text.UTF8Encoding($false)))
}

function New-Fixture {
    param([string]$Name, [string]$Outcome)
    $dir = Join-Path $OutputDir $Name
    [IO.Directory]::CreateDirectory($dir) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $dir 'frames')) | Out-Null
    $framePath = Join-Path $dir 'frames\0001-out.bin'
    [IO.File]::WriteAllBytes($framePath, [Text.Encoding]::UTF8.GetBytes('c04-frame-' + $Name))
    $frameSha = Get-Sha $framePath
    $payload = (('AB' * 32) -join '')
    $canonical = (('CD' * 32) -join '')
    $ledger = (('EF' * 32) -join '')
    $physical = (('12' * 32) -join '')
    $request = [ordered]@{
        requestId = 'req-1'
        scopeId = 'scope-1'
        accountId = 'acct-1'
        status = if ($Outcome -eq 'RECOVERED') { 'APPLIED' } elseif ($Outcome -eq 'FRESH_START') { 'NOT_REPLAYED_IN_NEW_SCOPE' } else { 'NOT_APPLICABLE_SAFE_REFUSAL' }
        payloadSha256 = $payload
        evaluatedStateVersion = '0'
        commandVersion = '1'
        eventSeq = '1'
    }
    $frames = @()
    $state = [ordered]@{}
    $resolution = [ordered]@{}
    $checks = [ordered]@{}
    if ($Outcome -eq 'RECOVERED') {
        $frames = @([ordered]@{
            direction = 'OUT'
            file = 'frames/0001-out.bin'
            sha256 = $frameSha
            type = 'ReceiptQuery'
            requestId = 'req-1'
            scopeId = 'scope-1'
            status = ''
            correlationId = 'receipt-query-1'
        })
        $state = [ordered]@{
            stateVersion = '1'; eventSeq = '1'; handCount = 13; riverCount = 1; tileInRiver = $true
            discardEvents = 1; ledgerSha256 = $ledger; physicalCount = 136; physicalUnique = 136
            oldScopeQueries = 1; oldRequestCommands = 0
        }
        $resolution = [ordered]@{
            requestId = 'req-1'; scopeId = 'scope-1'; disposition = 'Applied'; receiptStatus = 'Applied'
            fromLookup = $true; digest = $canonical
        }
        $checks = [ordered]@{
            exactOriginalIdentity = $true; foundFromRealLookup = $true; appliedOnceEffectVector = $true
            zeroDeltaAfterReconcile = $true; repeatQuerySentNothing = $true; noOriginalCommandResend = $true
            physical136Unique = $true; oldScopeQueryCount = 1
        }
    }
    elseif ($Outcome -eq 'FRESH_START') {
        $state = [ordered]@{ stateVersion = '0'; oldReceiptPresent = $false; oldOutboxUnresolved = 1 }
        $checks = [ordered]@{ noOldScopeFrame = $true; freshStateVersionZero = $true; oldOutboxKept = $true; oldReceiptAbsent = $true }
    }
    else {
        $state = [ordered]@{ ready = $false; oldOutboxUnresolved = 1 }
        $checks = [ordered]@{ zeroSendBoundary = $true; oldOutboxKept = $true; unchangedPrimary = $true; unchangedBackup = $true }
    }

    $primary = (('31' * 32) -join '')
    $backup = (('32' * 32) -join '')
    $input = [ordered]@{
        schemaVersion = 1; kind = 'c04-recovery-case-input'; caseId = 'C04-G6-01'; fault = 'None'
        expectedOutcome = $Outcome; playerSha256 = (('41' * 32) -join ''); packageSha256 = (('42' * 32) -join '')
        packageFileCount = 7; sourceHead = (('43' * 32) -join ''); buildCommit = (('44' * 32) -join '')
        primaryBeforeFaultSha256 = $primary; backupBeforeFaultSha256 = $backup
    }
    $result = [ordered]@{
        schemaVersion = 1; kind = 'c04-recovery-result'; caseId = 'C04-G6-01'; fault = 'None'
        expectedOutcome = $Outcome; actualOutcome = $Outcome
        request = $request
        canonicalDigest = $canonical
        state = $state
        resolution = $resolution
        checks = $checks
        frames = $frames
        oldOutboxUnresolved = 1
        fileHashes = [ordered]@{ primary = $primary; backup = $backup }
        process = [ordered]@{ path = 'C:\bound\Mahjong.exe'; pid = 42; sha256 = $input.playerSha256 }
        build = [ordered]@{
            playerSha256 = $input.playerSha256; packageSha256 = $input.packageSha256
            packageFileCount = 7; sourceHead = $input.sourceHead; buildCommit = $input.buildCommit
        }
    }
    $expectedLines = @(
        'requestId=req-1',
        'scopeId=scope-1',
        'accountId=acct-1',
        ('payloadSha256=' + $payload),
        ('canonicalDigest=' + $canonical),
        'evaluatedStateVersion=0',
        'commandVersion=1',
        'eventSeq=1',
        'preStateVersion=0',
        'preEventSeq=0',
        'preHandCount=14',
        'preRiverCount=0',
        ('ledgerSha256=' + $ledger),
        ('physicalSha256=' + $physical),
        'tileId=12',
        'discardEvents=1'
    )
    [IO.File]::WriteAllLines((Join-Path $dir 'expected.txt'), $expectedLines, (New-Object Text.UTF8Encoding($false)))
    Write-Json -Path (Join-Path $dir 'input.json') -Object $input
    Write-Json -Path (Join-Path $dir 'result.json') -Object $result
    return [pscustomobject]@{ Directory = $dir; Result = $result; Input = $input }
}

function Invoke-Case {
    param([string]$Name, [string]$Outcome, [bool]$ExpectPass, [scriptblock]$Mutate)
    $script:Total++
    try {
        $fixture = New-Fixture -Name $Name -Outcome $Outcome
        if ($null -ne $Mutate) {
            & $Mutate $fixture
            Write-Json -Path (Join-Path $fixture.Directory 'result.json') -Object $fixture.Result
        }
        $verification = Join-Path $fixture.Directory 'verification.json'
        $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $VerifierPath `
            -ResultPath (Join-Path $fixture.Directory 'result.json') `
            -ExpectedPath (Join-Path $fixture.Directory 'expected.txt') `
            -InputPath (Join-Path $fixture.Directory 'input.json') `
            -OutputPath $verification 2>&1
        $exit = $LASTEXITCODE
        if ($ExpectPass) {
            Assert-True ($exit -eq 0) ('expected PASS but verifier exit=' + $exit + ' output=' + ($output -join ' '))
            Assert-True (Test-Path -LiteralPath $verification) 'verification.json was not written'
            $parsed = [IO.File]::ReadAllText($verification) | ConvertFrom-Json
            Assert-True ([string]$parsed.status -eq 'PASS') ('verification status is ' + $parsed.status)
        }
        else {
            Assert-True ($exit -ne 0) ('expected FAIL but verifier exit=' + $exit + ' output=' + ($output -join ' '))
            Assert-True (Test-Path -LiteralPath $verification) 'verification.json was not written for a negative case'
            $parsed = [IO.File]::ReadAllText($verification) | ConvertFrom-Json
            Assert-True ([string]$parsed.status -eq 'FAIL') ('negative verification status is ' + $parsed.status)
        }
        $script:Passed++
        Write-Host ('PASS ' + $Name)
    }
    catch {
        $script:Failed++
        Write-Host ('FAIL ' + $Name + ': ' + $_.Exception.Message)
    }
}

Invoke-Case -Name 'valid-recovered' -Outcome 'RECOVERED' -ExpectPass $true -Mutate $null
Invoke-Case -Name 'valid-fresh' -Outcome 'FRESH_START' -ExpectPass $true -Mutate $null
Invoke-Case -Name 'valid-rejected' -Outcome 'REJECTED' -ExpectPass $true -Mutate $null
Invoke-Case -Name 'wrong-outcome' -Outcome 'RECOVERED' -ExpectPass $false -Mutate {
    param($fixture)
    $fixture.Result.actualOutcome = 'FRESH_START'
}
Invoke-Case -Name 'resent-command' -Outcome 'RECOVERED' -ExpectPass $false -Mutate {
    param($fixture)
    $frame = $fixture.Result.frames[0] | ConvertTo-Json | ConvertFrom-Json
    $frame.type = 'Command'
    $fixture.Result.frames = @($fixture.Result.frames[0], $frame)
}
Invoke-Case -Name 'fake-lookup' -Outcome 'RECOVERED' -ExpectPass $false -Mutate {
    param($fixture)
    $fixture.Result.resolution.fromLookup = $false
}
Invoke-Case -Name 'rejected-sent' -Outcome 'REJECTED' -ExpectPass $false -Mutate {
    param($fixture)
    $fixture.Result.frames = @([ordered]@{
        direction = 'OUT'; file = 'frames/0001-out.bin'; sha256 = (Get-Sha (Join-Path $fixture.Directory 'frames\0001-out.bin'))
        type = 'Hello'; requestId = ''; scopeId = ''; status = ''; correlationId = ''
    })
}
Invoke-Case -Name 'tampered-frame' -Outcome 'RECOVERED' -ExpectPass $false -Mutate {
    param($fixture)
    $fixture.Result.frames[0].sha256 = (('00' * 32) -join '')
}

$summary = [ordered]@{
    schemaVersion = 1
    kind = 'c04-recovery-verifier-tool-tests'
    total = $script:Total
    passed = $script:Passed
    failed = $script:Failed
    status = if ($script:Failed -eq 0) { 'PASS' } else { 'FAIL' }
    outputDir = $OutputDir
    generatedAt = [DateTimeOffset]::UtcNow.ToString('o')
}
$summaryPath = Join-Path $OutputDir 'summary.json'
Write-Json -Path $summaryPath -Object $summary
$summarySha = Get-Sha $summaryPath
Write-Host ('C04_VERIFIER_TOOL_TESTS total=' + $script:Total + ' passed=' + $script:Passed +
    ' failed=' + $script:Failed + ' summary=' + $summaryPath + ' sha=' + $summarySha)
if ($script:Failed -ne 0) { exit 1 }
exit 0
