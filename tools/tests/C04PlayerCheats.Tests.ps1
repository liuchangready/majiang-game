<#
    SCRIPT_TOOL_FIXTURE negative matrix for C04PlayerCheats. These tests
    exercise the evidence verifier and overwrite guard only; they do not run
    Unity, the Player or a game.
#>
$ErrorActionPreference = 'Stop'
$runner = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'tools\unity\run-p45-c04-player-cheats.ps1'
. $runner -SelfTest

$failures = New-Object System.Collections.Generic.List[string]
$passed = 0

function Assert-ContainsError {
    param([string[]]$Errors, [string]$Expected, [string]$Label)
    if ($Errors -contains $Expected) {
        $script:passed++
    }
    else {
        $script:failures.Add("$Label expected=$Expected actual=$($Errors -join ',')") | Out-Null
    }
}

function New-Fixture {
    $request = [pscustomobject]@{
        requestId = ('a' * 32)
        kind = 'SubmitBeamSwap'
        status = 'APPLIED'
        reason = ''
        payloadSha256 = ('B' * 64)
        rawPayloadSha256 = ('C' * 64)
        roundId = 'c04-g3-r1'
        seat = 0
        decisionId = 'decision-1'
        controlEpoch = 0
        stateVersionBefore = 1
        stateVersionAfter = 2
        source = 'TablePresenter'
    }
    return [pscustomobject]@{
        schemaVersion = 1
        layer = 'WINDOWS_PLAYER'
        scenarioVersion = 'c04-g3-player-cheats-v1'
        caseName = 'authorized'
        scenarioKind = 1
        status = 'PASS'
        sourceHead = 'f3ba015b7b54061a0b144ddf546cd6f8e37548ca'
        unityVersion = '6000.3.27f1'
        blockedRows = @('G3-01', 'G3-02')
        assertions = @(
            [pscustomobject]@{ id = 'G3-01-UI-ENTRY'; pass = $true; expected = 'x'; actual = 'y' },
            [pscustomobject]@{ id = 'G3-02-UI-ENTRY'; pass = $true; expected = 'x'; actual = 'y' },
            [pscustomobject]@{ id = 'G3-03-RECEIPT'; pass = $true; expected = 'x'; actual = 'y' },
            [pscustomobject]@{ id = 'G3-04-BEAM-FEE'; pass = $true; expected = 'x'; actual = 'y' },
            [pscustomobject]@{ id = 'G3-05-BEFORE'; pass = $true; expected = 'x'; actual = 'y' }
        )
        requests = @($request)
    }
}

$head = 'f3ba015b7b54061a0b144ddf546cd6f8e37548ca'
$valid = New-Fixture
$errors = Assert-C04Evidence -Evidence $valid -ExpectedCase 'authorized' -NativeExit 0 -ExpectedSourceHead $head
if ($errors.Count -eq 0) { $passed++ } else { $failures.Add('valid fixture rejected: ' + ($errors -join ',')) | Out-Null }

$wrongCase = New-Fixture
$wrongCase.caseName = 'negative'
Assert-ContainsError (Assert-C04Evidence -Evidence $wrongCase -ExpectedCase 'authorized' -NativeExit 0 -ExpectedSourceHead $head) 'CASE_NAME' 'wrong-case'

$wrongHead = New-Fixture
$wrongHead.sourceHead = 'deadbeef'
Assert-ContainsError (Assert-C04Evidence -Evidence $wrongHead -ExpectedCase 'authorized' -NativeExit 0 -ExpectedSourceHead $head) 'SOURCE_HEAD' 'wrong-identity'

$missingReceipt = New-Fixture
$missingReceipt.requests = @()
Assert-ContainsError (Assert-C04Evidence -Evidence $missingReceipt -ExpectedCase 'authorized' -NativeExit 0 -ExpectedSourceHead $head) 'NO_REQUESTS' 'missing-receipt'

$missingStatus = New-Fixture
$missingStatus.requests[0].status = ''
Assert-ContainsError (Assert-C04Evidence -Evidence $missingStatus -ExpectedCase 'authorized' -NativeExit 0 -ExpectedSourceHead $head) 'MISSING_RECEIPT_STATUS' 'missing-status'

$badHash = New-Fixture
$badHash.requests[0].rawPayloadSha256 = 'not-a-hash'
Assert-ContainsError (Assert-C04Evidence -Evidence $badHash -ExpectedCase 'authorized' -NativeExit 0 -ExpectedSourceHead $head) 'MISSING_RAW_HASH' 'hash-mismatch'

$nonzero = New-Fixture
Assert-ContainsError (Assert-C04Evidence -Evidence $nonzero -ExpectedCase 'authorized' -NativeExit 3 -ExpectedSourceHead $head) 'NATIVE_EXIT_3' 'nonzero-native'

$timeout = New-Fixture
Assert-ContainsError (Assert-C04Evidence -Evidence $timeout -ExpectedCase 'authorized' -NativeExit 124 -ExpectedSourceHead $head) 'NATIVE_EXIT_124' 'timeout-native'

$noAssertions = New-Fixture
$noAssertions.assertions = @()
Assert-ContainsError (Assert-C04Evidence -Evidence $noAssertions -ExpectedCase 'authorized' -NativeExit 0 -ExpectedSourceHead $head) 'NO_ASSERTIONS' 'empty-assertions'

$unknownBlocked = New-Fixture
$unknownBlocked.blockedRows = @('G3-99')
Assert-ContainsError (Assert-C04Evidence -Evidence $unknownBlocked -ExpectedCase 'authorized' -NativeExit 0 -ExpectedSourceHead $head) 'UNKNOWN_BLOCKED_ROW' 'unknown-blocked'

# Strict production guard: a PASS evidence object must carry all six-row success
# assertions, five unique APPLIED requests with raw hashes, and no blocked rows.
$strictIds = @('G3-01-RECEIPT','G3-02-RECEIPT','G3-02-CHOICE-RECEIPT','G3-03-RECEIPT','G3-03-CHOICE-RECEIPT','G3-04-FINAL-LEDGER')
$strictBase = New-Fixture
$strictBase.blockedRows = @()
$strictBase.assertions = @($strictIds | ForEach-Object { [pscustomobject]@{ id = $_; pass = $true; expected = 'x'; actual = 'y' } })
$strictBase.requests = @(1..5 | ForEach-Object {
    $r = $strictBase.requests[0].PSObject.Copy()
    $r.requestId = ('{0:x32}' -f $_)
    $r.status = 'APPLIED'
    $r.decisionId = 'decision-' + $_
    $r
})
$strictErrors = Assert-C04EvidenceStrict -Evidence $strictBase -ExpectedCase 'authorized' -NativeExit 0 -ExpectedSourceHead $head
if ($strictErrors.Count -eq 0) { $passed++ } else { $failures.Add('strict valid fixture rejected: ' + ($strictErrors -join ',')) | Out-Null }

$variants = @(
    @{ Name = 'status-fail'; Mutate = { param($e) $e.status = 'FAIL' } },
    @{ Name = 'all-assertions-false'; Mutate = { param($e) foreach ($a in $e.assertions) { $a.pass = $false } } },
    @{ Name = 'rejected-as-success'; Mutate = { param($e) $e.requests[0].status = 'REJECTED'; $e.requests[0].reason = 'MALFORMED_MESSAGE' } },
    @{ Name = 'all-blocked'; Mutate = { param($e) $e.blockedRows = @('G3-01','G3-02') } },
    @{ Name = 'foreign-identity'; Mutate = { param($e) $e.requests[0].seat = 99; $e.requests[0].decisionId = '' } },
    @{ Name = 'bad-raw-hash'; Mutate = { param($e) $e.requests[0].rawPayloadSha256 = 'not-a-hash' } },
    @{ Name = 'duplicate-request'; Mutate = { param($e) $e.requests[1].requestId = $e.requests[0].requestId } }
)
foreach ($variant in $variants) {
    $copy = ($strictBase | ConvertTo-Json -Depth 8 | ConvertFrom-Json)
    & $variant.Mutate $copy
    $variantErrors = Assert-C04EvidenceStrict -Evidence $copy -ExpectedCase 'authorized' -NativeExit 0 -ExpectedSourceHead $head
    if ($variantErrors.Count -gt 0) { $passed++ } else { $failures.Add('strict accepted invalid variant ' + $variant.Name) | Out-Null }
}
# Overwrite refusal is a real runner guard, not a fabricated verifier result.
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ('c04-tool-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $temp | Out-Null
$threw = $false
try {
    & $runner -EvidenceRoot $temp -PlayerPath (Join-Path $temp 'missing.exe') -SourceHead $head -SkipBuild
}
catch {
    $threw = $_.Exception.Message -match 'REFUSE_OVERWRITE'
}
if ($threw) { $passed++ } else { $failures.Add('overwrite-refusal did not throw REFUSE_OVERWRITE') | Out-Null }
Remove-Item -LiteralPath $temp -Recurse -Force

Write-Host "C04PlayerCheats.Tests passed=$passed failed=$($failures.Count)"
foreach ($failure in $failures) { Write-Host "FAIL $failure" }
if ($failures.Count -gt 0) { exit 1 }
exit 0
