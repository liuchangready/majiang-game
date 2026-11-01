#requires -Version 5.1
<#
.SYNOPSIS
    Negative/positive matrix for the production P5.2 table XML gate and the
    PG restore/ownership/cleanup guards.

.DESCRIPTION
    This is a tool-boundary test, not a PostgreSQL or business-test run. It
    extracts the real production functions from
    tools\ci\verify-v5-table-postgres.ps1 with the PowerShell AST and calls the
    SAME decision functions used by the wrapper:

        Test-TableResultEvidence       (production XML/native-exit gate)
        Get-ClusterOwnershipState      (marker/root/port/database ownership)
        Test-RestorePreflightEmpty     (pre-restore empty target + identity)
        Test-CleanupRootContained      (strict path guard before delete)

    The script never mirrors those algorithms itself. Synthetic XML fixtures
    cover the actual xUnit.net v3 shape plus assembly-only, duplicate IDs,
    wrong assembly, missing/invalid attributes, collection/assembly count
    mismatches, zero tests, failed/skipped/not-run/errors, stray nodes,
    multiple assemblies, XXE, stale XML and XML outside the current LogDir.
    A legal 13-case XML is the positive control.

    The native helper matrix (exit 0/1/2/17, timeout, startup failure, quiet
    success, redaction) is the separate production-function test in
    tools\ci\test-v5-native-exit.ps1; this script does not duplicate it.

    Fixtures are written only under this worktree's ignored artifacts directory,
    in a fresh GUID-named tree. Production sources are untouched.
#>
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$wrapperScript = Join-Path $PSScriptRoot 'verify-v5-table-postgres.ps1'
if (-not (Test-Path -LiteralPath $wrapperScript)) { throw "Production wrapper not found: $wrapperScript" }

$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($wrapperScript, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw "Production wrapper does not parse: $($parseErrors[0].Message)" }
foreach ($name in @('Get-Sha256', 'Write-Step', 'Test-TableResultEvidence', 'Get-ClusterOwnershipState', 'Test-RestorePreflightEmpty', 'Test-CleanupRootContained')) {
    $fn = $ast.Find({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
        }, $true)
    if (-not $fn) { throw "Cannot extract production function $name" }
    . ([scriptblock]::Create($fn.Extent.Text))
}

$evidenceDir = Join-Path $repoRoot ('artifacts/table-evidence-selftest-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $evidenceDir | Out-Null
$results = New-Object System.Collections.Generic.List[object]

function Write-Utf8([string]$Path, [string]$Content) {
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    [System.IO.File]::WriteAllText($Path, $Content, (New-Object System.Text.UTF8Encoding($false)))
}

function Add-Result([string]$Case, [bool]$Pass, [string]$Reason) {
    $results.Add([pscustomobject]@{ Case = $Case; Pass = $Pass; Reason = $Reason })
}

function New-ResultXml {
    param(
        [string]$AssemblyName = 'C:\fixture\bin\Mahjong.Infrastructure.Tests.dll',
        [string]$Framework = 'xUnit.net v3 4.0.2',
        [string]$Schema = '3',
        [string[]]$Results = @('Pass', 'Pass', 'Pass'),
        [int]$Total = -1,
        [int]$Passed = -1,
        [int]$Failed = -1,
        [int]$Skipped = -1,
        [int]$Errors = 0,
        [int]$NotRun = -1,
        [int]$CollectionTotal = -1,
        [int]$CollectionPassed = -1,
        [switch]$AssemblyOnly,
        [switch]$AddErrorNode
    )
    $passCount = @($Results | Where-Object { $_ -eq 'Pass' }).Count
    $failCount = @($Results | Where-Object { $_ -eq 'Fail' }).Count
    $skipCount = @($Results | Where-Object { $_ -eq 'Skip' }).Count
    $notRunCount = @($Results | Where-Object { $_ -eq 'NotRun' }).Count
    if ($Total -lt 0) { $Total = $Results.Count }
    if ($Passed -lt 0) { $Passed = $passCount }
    if ($Failed -lt 0) { $Failed = $failCount }
    if ($Skipped -lt 0) { $Skipped = $skipCount }
    if ($NotRun -lt 0) { $NotRun = $notRunCount }
    if ($CollectionTotal -lt 0) { $CollectionTotal = $Results.Count }
    if ($CollectionPassed -lt 0) { $CollectionPassed = $passCount }

    $builder = New-Object System.Text.StringBuilder
    [void]$builder.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
    [void]$builder.AppendLine(('<assemblies schema-version="{0}">' -f $Schema))
    [void]$builder.AppendLine(('  <assembly name="{0}" test-framework="{1}" target-framework=".NETCoreApp,Version=v8.0" total="{2}" passed="{3}" failed="{4}" skipped="{5}" errors="{6}" not-run="{7}">' -f $AssemblyName, $Framework, $Total, $Passed, $Failed, $Skipped, $Errors, $NotRun))
    if ($AddErrorNode) {
        [void]$builder.AppendLine('    <errors><error type="Probe.Broken" name="fixture error"><failure><message>framework error</message></failure></error></errors>')
    }
    if (-not $AssemblyOnly) {
        [void]$builder.AppendLine(('    <collection name="postgres-table-p52" total="{0}" passed="{1}" failed="{2}" skipped="{3}" not-run="{4}">' -f $CollectionTotal, $CollectionPassed, $failCount, $skipCount, $notRunCount))
        for ($index = 0; $index -lt $Results.Count; $index++) {
            [void]$builder.AppendLine(('      <test id="00000000-0000-0000-0000-{0:D12}" name="Mahjong.Infrastructure.Tests.Probe.Case{1}" result="{2}" />' -f ($index + 1), ($index + 1), $Results[$index]))
        }
        [void]$builder.AppendLine('    </collection>')
    }
    [void]$builder.AppendLine('  </assembly>')
    [void]$builder.AppendLine('</assemblies>')
    return $builder.ToString()
}

function Invoke-XmlCase {
    param(
        [string]$Case,
        [string]$Xml,
        [object]$NativeExit = 0,
        [string]$ExpectedAssembly = 'Mahjong.Infrastructure.Tests',
        [int]$AgeDays = 0,
        [switch]$NoXml,
        [switch]$EmptyXml,
        [switch]$OutsideLogDir,
        [bool]$ExpectedOk
    )
    $caseDir = Join-Path $evidenceDir $Case
    New-Item -ItemType Directory -Force -Path $caseDir | Out-Null
    $xmlPath = Join-Path $caseDir 'Mahjong.Infrastructure.Tests.xml'
    if (-not $NoXml) {
        Write-Utf8 -Path $xmlPath -Content $(if ($EmptyXml) { '' } else { $Xml })
        if ($AgeDays -gt 0) { (Get-Item -LiteralPath $xmlPath).LastWriteTimeUtc = [DateTime]::UtcNow.AddDays(-$AgeDays) }
    }
    $logDirForDecision = $caseDir
    if ($OutsideLogDir) {
        $other = Join-Path $caseDir 'other-run'
        New-Item -ItemType Directory -Force -Path $other | Out-Null
        $logDirForDecision = $other
    }
    $started = [DateTime]::UtcNow
    $decision = Test-TableResultEvidence -XmlPath $xmlPath -ExpectedAssemblyName $ExpectedAssembly `
        -NativeExit $NativeExit -LogDir $logDirForDecision -RunStartedUtc $started
    $pass = ([bool]$decision.Ok -eq $ExpectedOk) -and (-not [string]::IsNullOrWhiteSpace([string]$decision.Reason))
    Add-Result -Case $Case -Pass $pass -Reason ("expectedOk={0} actualOk={1} nodes={2} collections={3} reason={4}" -f $ExpectedOk, $decision.Ok, $decision.TestNodeCount, $decision.CollectionCount, $decision.Reason)
}

# ---------------------------------------------------------------- XML matrix
$green = New-ResultXml
$green13 = New-ResultXml -Results @(1..13 | ForEach-Object { 'Pass' })
$greenFailed = New-ResultXml -Results @('Fail')
$greenSkipped = New-ResultXml -Results @('Skip')
$greenNotRun = New-ResultXml -Results @('NotRun')

Invoke-XmlCase -Case '01 positive green 3' -Xml $green -ExpectedOk $true
Invoke-XmlCase -Case '02 positive green 13' -Xml $green13 -ExpectedOk $true
Invoke-XmlCase -Case '03 missing xml' -Xml '' -NoXml -ExpectedOk $false
Invoke-XmlCase -Case '04 empty xml' -Xml '' -EmptyXml -ExpectedOk $false
Invoke-XmlCase -Case '05 malformed xml' -Xml 'this is not XML at all' -ExpectedOk $false
Invoke-XmlCase -Case '06 truncated xml' -Xml ($green.Substring(0, [Math]::Max(20, $green.Length - 60))) -ExpectedOk $false
Invoke-XmlCase -Case '07 zero tests' -Xml (New-ResultXml -Results @() -Total 0 -Passed 0 -AssemblyOnly) -NativeExit 0 -ExpectedOk $false
Invoke-XmlCase -Case '08 assembly-only 42/42' -Xml (New-ResultXml -Results @() -Total 42 -Passed 42 -AssemblyOnly) -ExpectedOk $false
Invoke-XmlCase -Case '09 wrong assembly' -Xml (New-ResultXml -AssemblyName 'C:\fixture\bin\Other.Tests.dll') -ExpectedOk $false
Invoke-XmlCase -Case '10 wrong framework' -Xml (New-ResultXml -Framework 'xUnit.net v2 2.9.0') -ExpectedOk $false
Invoke-XmlCase -Case '11 non-numeric schema' -Xml ($green -replace 'schema-version="3"', 'schema-version="three"') -ExpectedOk $false
Invoke-XmlCase -Case '12 duplicate id' -Xml ($green -replace 'id="00000000-0000-0000-0000-000000000002"', 'id="00000000-0000-0000-0000-000000000001"') -ExpectedOk $false
Invoke-XmlCase -Case '13 missing id' -Xml ([regex]::Replace($green, 'id="00000000-0000-0000-0000-000000000001"', '', 1)) -ExpectedOk $false
Invoke-XmlCase -Case '14 missing name' -Xml ($green -replace 'name="Mahjong.Infrastructure.Tests.Probe.Case1"', '') -ExpectedOk $false
Invoke-XmlCase -Case '15 unknown result' -Xml ([regex]::Replace($green, 'result="Pass"', 'result="Bogus"', 1)) -ExpectedOk $false
Invoke-XmlCase -Case '16 assembly total mismatch' -Xml ([regex]::Replace($green, '(<assembly[^>]*\btotal=")3(")', '${1}4${2}', 1)) -ExpectedOk $false
Invoke-XmlCase -Case '17 collection total mismatch' -Xml ([regex]::Replace($green, '(<collection[^>]*\btotal=")3(")', '${1}4${2}', 1)) -ExpectedOk $false
Invoke-XmlCase -Case '18 collection passed mismatch' -Xml ([regex]::Replace($green, '(<collection[^>]*\bpassed=")3(")', '${1}2${2}', 1)) -ExpectedOk $false
Invoke-XmlCase -Case '19 assembly passed mismatch' -Xml ([regex]::Replace($green, '(<assembly[^>]*\bpassed=")3(")', '${1}2${2}', 1)) -ExpectedOk $false
Invoke-XmlCase -Case '20 errors with error node' -Xml (New-ResultXml -Results @('Pass') -Errors 1 -AddErrorNode) -ExpectedOk $false
Invoke-XmlCase -Case '21 errors attribute without node' -Xml (New-ResultXml -Results @('Pass') -Errors 1) -ExpectedOk $false
Invoke-XmlCase -Case '22 error node without errors attribute' -Xml (New-ResultXml -Results @('Pass') -Errors 0 -AddErrorNode) -ExpectedOk $false
Invoke-XmlCase -Case '23 failed result' -Xml $greenFailed -ExpectedOk $false
Invoke-XmlCase -Case '24 skipped result' -Xml $greenSkipped -ExpectedOk $false
Invoke-XmlCase -Case '25 not-run result' -Xml $greenNotRun -ExpectedOk $false
Invoke-XmlCase -Case '26 missing not-run attribute' -Xml ([regex]::Replace($green, '(<assembly[^>]*?) not-run="\d+"', '$1', 1)) -ExpectedOk $false
Invoke-XmlCase -Case '27 non-numeric assembly total' -Xml ([regex]::Replace($green, '(<assembly[^>]*\btotal=")\d+(")', '${1}three${2}', 1)) -ExpectedOk $false
Invoke-XmlCase -Case '28 negative passed attribute' -Xml ([regex]::Replace($green, '(<assembly[^>]*\bpassed=")\d+(")', '${1}-1${2}', 1)) -ExpectedOk $false
$assemblyText = [regex]::Match($green, '(?s)<assembly.*?</assembly>').Value
$multiAssembly = $green.Insert($green.IndexOf('</assembly>') + '</assembly>'.Length, $assemblyText)
Invoke-XmlCase -Case '29 multiple assemblies' -Xml $multiAssembly -ExpectedOk $false
$strayTest = $green -replace '</assembly>', '<test id="stray" name="Stray" result="Pass" /></assembly>'
Invoke-XmlCase -Case '30 stray test outside collection' -Xml $strayTest -ExpectedOk $false
$extraNode = $green -replace '</assembly>', '<warning>extra</warning></assembly>'
Invoke-XmlCase -Case '31 extra node under assembly' -Xml $extraNode -ExpectedOk $false
$xxe = '<?xml version="1.0"?><!DOCTYPE assemblies [<!ENTITY xxe SYSTEM "file:///C:/Windows/win.ini">]><assemblies schema-version="3"><assembly name="&xxe;" test-framework="xUnit.net v3 4.0.2" total="1" passed="1" failed="0" skipped="0" errors="0" not-run="0"><collection name="x" total="1" passed="1" failed="0" skipped="0" not-run="0"><test id="1" name="x" result="Pass" /></collection></assembly></assemblies>'
Invoke-XmlCase -Case '32 xxe doctype' -Xml $xxe -ExpectedOk $false
Invoke-XmlCase -Case '33 native exit 1 with green xml' -Xml $green -NativeExit 1 -ExpectedOk $false
Invoke-XmlCase -Case '34 native exit null with green xml' -Xml $green -NativeExit $null -ExpectedOk $false
Invoke-XmlCase -Case '35 stale xml' -Xml $green -AgeDays 1 -ExpectedOk $false
Invoke-XmlCase -Case '36 xml outside logdir' -Xml $green -OutsideLogDir -ExpectedOk $false

# R1-03: a result='Pass' node with an explicit failure/reason/error/unknown
# child is contradictory and must not be green. Legitimate xUnit Pass payloads
# (output, traits) remain accepted.
function New-OneTestXml([string]$Child, [string]$Result = 'Pass') {
    $xml = New-ResultXml -Results @($Result)
    $node = ('<test id="00000000-0000-0000-0000-000000000001" name="Mahjong.Infrastructure.Tests.Probe.Case1" result="{0}" />' -f $Result)
    return $xml.Replace($node, $node.Replace(' />', ('>' + $Child + '</test>')))
}
Invoke-XmlCase -Case '61 pass-with-failure-child' -Xml (New-OneTestXml '<failure exception-type="Probe.Error"><message>actual recorded failure</message></failure>') -ExpectedOk $false
Invoke-XmlCase -Case '62 pass-with-reason-child' -Xml (New-OneTestXml '<reason><message>skip reason under pass</message></reason>') -ExpectedOk $false
Invoke-XmlCase -Case '63 pass-with-error-child' -Xml (New-OneTestXml '<error type="Probe.Error"><message>error under pass</message></error>') -ExpectedOk $false
Invoke-XmlCase -Case '64 pass-with-unknown-child' -Xml (New-OneTestXml '<contradiction>unknown</contradiction>') -ExpectedOk $false
Invoke-XmlCase -Case '65 skip-with-failure-child' -Xml (New-OneTestXml '<failure exception-type="Probe.Error"><message>failure under skip</message></failure>' 'Skip') -ExpectedOk $false
Invoke-XmlCase -Case '66 fail-with-reason-child' -Xml (New-OneTestXml '<reason><message>reason under fail</message></reason>' 'Fail') -ExpectedOk $false
Invoke-XmlCase -Case '67 pass-with-output-child' -Xml (New-OneTestXml '<output>legitimate test output</output>') -ExpectedOk $true
Invoke-XmlCase -Case '68 pass-with-traits-child' -Xml (New-OneTestXml '<traits><trait name="category" value="unit" /></traits>') -ExpectedOk $true
Invoke-XmlCase -Case '69 pass-with-failure-and-output' -Xml (New-OneTestXml '<output>partial output</output><failure exception-type="Probe.Error"><message>failure</message></failure>') -ExpectedOk $false
Invoke-XmlCase -Case '73 pass-with-failure-nested-in-output' -Xml (New-OneTestXml '<output><failure exception-type="Probe.Error"><message>hidden failure</message></failure></output>') -ExpectedOk $false
Invoke-XmlCase -Case '74 pass-with-failure-nested-in-traits' -Xml (New-OneTestXml '<traits><failure exception-type="Probe.Error"><message>hidden failure</message></failure></traits>') -ExpectedOk $false
Invoke-XmlCase -Case '75 pass-with-unknown-nested-in-traits' -Xml (New-OneTestXml '<traits><contradiction>unknown</contradiction></traits>') -ExpectedOk $false

# R2 residual: validate the complete assembly <errors> subtree, not only the
# narrow errors/error XPath. Empty/whitespace containers remain valid.
Invoke-XmlCase -Case '76 assembly-empty-errors' -Xml ($green -replace '</assembly>', '<errors /></assembly>') -ExpectedOk $true
Invoke-XmlCase -Case '77 assembly-errors-failure' -Xml ($green -replace '</assembly>', '<errors><failure exception-type="Probe.Error"><message>actual recorded failure</message></failure></errors></assembly>') -ExpectedOk $false
Invoke-XmlCase -Case '78 assembly-errors-unknown' -Xml ($green -replace '</assembly>', '<errors><unknown><message>unrecognized error payload</message></unknown></errors></assembly>') -ExpectedOk $false
Invoke-XmlCase -Case '79 assembly-errors-text' -Xml ($green -replace '</assembly>', '<errors>actual recorded framework error</errors></assembly>') -ExpectedOk $false
Invoke-XmlCase -Case '80 assembly-errors-standard' -Xml ($green -replace '</assembly>', '<errors><error type="assembly-cleanup"><failure exception-type="Probe.Error"><message>actual recorded failure</message></failure></error></errors></assembly>') -ExpectedOk $false
Invoke-XmlCase -Case '81 assembly-errors-whitespace-comment' -Xml ($green -replace '</assembly>', '<errors>   <!-- framework cleanup note -->   </errors></assembly>') -ExpectedOk $true
Invoke-XmlCase -Case '82 assembly-multiple-errors-containers' -Xml ($green -replace '</assembly>', '<errors /><errors /></assembly>') -ExpectedOk $false


# ------------------------------------------------------- PG ownership matrix
$ownershipRoot = Join-Path $evidenceDir 'ownership'
New-Item -ItemType Directory -Force -Path $ownershipRoot | Out-Null

function New-MarkerCase([string]$Name, [object]$Marker, [switch]$Missing, [switch]$InvalidJson) {
    $dir = Join-Path $ownershipRoot $Name
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $markerPath = Join-Path $dir 'pg-owner.json'
    if ($InvalidJson) { Write-Utf8 -Path $markerPath -Content '{ not valid json' }
    elseif (-not $Missing) { Write-Utf8 -Path $markerPath -Content ($Marker | ConvertTo-Json) }
    $state = Get-ClusterOwnershipState -MarkerPath $markerPath -Root $dir -Port 55592 -Database 'mahjong_p52_test'
    return [pscustomobject]@{ Dir = $dir; MarkerPath = $markerPath; State = $state }
}

$validMarker = [ordered]@{ owner = 'mahjong-p52-pg'; root = (Join-Path $ownershipRoot 'valid'); port = 55592; database = 'mahjong_p52_test'; createdUtc = (Get-Date).ToUniversalTime().ToString('o') }
$validCase = New-MarkerCase -Name 'valid' -Marker $validMarker
Add-Result -Case '37 marker valid' -Pass ([bool]$validCase.State.Ok -and -not [string]::IsNullOrWhiteSpace([string]$validCase.State.MarkerSha256)) -Reason $validCase.State.Reason

$missingCase = New-MarkerCase -Name 'missing' -Marker $null -Missing
Add-Result -Case '38 marker missing' -Pass ((-not [bool]$missingCase.State.Ok) -and $missingCase.State.Reason -match 'missing') -Reason $missingCase.State.Reason

$invalidCase = New-MarkerCase -Name 'invalid-json' -Marker $null -InvalidJson
Add-Result -Case '39 marker invalid json' -Pass (-not [bool]$invalidCase.State.Ok) -Reason $invalidCase.State.Reason

$wrongOwner = [ordered]@{ owner = 'someone-else'; root = (Join-Path $ownershipRoot 'wrong-owner'); port = 55592; database = 'mahjong_p52_test' }
$wrongOwnerCase = New-MarkerCase -Name 'wrong-owner' -Marker $wrongOwner
Add-Result -Case '40 marker wrong owner' -Pass ((-not [bool]$wrongOwnerCase.State.Ok) -and $wrongOwnerCase.State.Reason -match 'does not belong') -Reason $wrongOwnerCase.State.Reason

$wrongRoot = [ordered]@{ owner = 'mahjong-p52-pg'; root = (Join-Path $ownershipRoot 'some-other-root'); port = 55592; database = 'mahjong_p52_test' }
$wrongRootCase = New-MarkerCase -Name 'wrong-root' -Marker $wrongRoot
Add-Result -Case '41 marker wrong root' -Pass ((-not [bool]$wrongRootCase.State.Ok) -and $wrongRootCase.State.Reason -match 'does not belong') -Reason $wrongRootCase.State.Reason

$wrongPort = [ordered]@{ owner = 'mahjong-p52-pg'; root = (Join-Path $ownershipRoot 'wrong-port'); port = 55593; database = 'mahjong_p52_test' }
$wrongPortCase = New-MarkerCase -Name 'wrong-port' -Marker $wrongPort
Add-Result -Case '42 marker wrong port' -Pass ((-not [bool]$wrongPortCase.State.Ok) -and $wrongPortCase.State.Reason -match 'does not belong') -Reason $wrongPortCase.State.Reason

$wrongDatabase = [ordered]@{ owner = 'mahjong-p52-pg'; root = (Join-Path $ownershipRoot 'wrong-database'); port = 55592; database = 'mahjong_other' }
$wrongDatabaseCase = New-MarkerCase -Name 'wrong-database' -Marker $wrongDatabase
Add-Result -Case '43 marker wrong database' -Pass ((-not [bool]$wrongDatabaseCase.State.Ok) -and $wrongDatabaseCase.State.Reason -match 'does not belong') -Reason $wrongDatabaseCase.State.Reason

$foreignRoot = Join-Path $ownershipRoot 'foreign-sentinel'
New-Item -ItemType Directory -Force -Path $foreignRoot | Out-Null
$sentinelPath = Join-Path $foreignRoot 'sentinel.txt'
Write-Utf8 -Path $sentinelPath -Content 'must-survive'
$foreignState = Get-ClusterOwnershipState -MarkerPath (Join-Path $foreignRoot 'pg-owner.json') -Root $foreignRoot -Port 55592 -Database 'mahjong_p52_test'
Add-Result -Case '44 foreign root sentinel preserved' -Pass ((-not [bool]$foreignState.Ok) -and (Test-Path -LiteralPath $sentinelPath) -and ((Get-Content -LiteralPath $sentinelPath -Raw -Encoding UTF8).Trim() -eq 'must-survive')) -Reason $foreignState.Reason

# --------------------------------------------------- restore preflight matrix
function Add-PreflightCase([string]$Case, [string]$Raw, [bool]$ExpectedOk) {
    $decision = Test-RestorePreflightEmpty -RawOutput $Raw -ExpectedDatabase 'mahjong_p52_restore' -ExpectedHost '127.0.0.1' -ExpectedPort 55592
    $pass = ([bool]$decision.Ok -eq $ExpectedOk) -and (-not [string]::IsNullOrWhiteSpace([string]$decision.Reason)) -and $(if ($ExpectedOk) { $decision.Values.tables -eq 0 } else { $true })
    Add-Result -Case $Case -Pass $pass -Reason $decision.Reason
}
Add-PreflightCase -Case '45 preflight empty' -Raw 'mahjong_p52_restore|127.0.0.1|55592|0|0|0' -ExpectedOk $true
Add-PreflightCase -Case '46 preflight non-empty tables' -Raw 'mahjong_p52_restore|127.0.0.1|55592|0|1|0' -ExpectedOk $false
Add-PreflightCase -Case '47 preflight non-empty schema' -Raw 'mahjong_p52_restore|127.0.0.1|55592|1|0|0' -ExpectedOk $false
Add-PreflightCase -Case '48 preflight non-empty relations' -Raw 'mahjong_p52_restore|127.0.0.1|55592|0|0|1' -ExpectedOk $false
Add-PreflightCase -Case '49 preflight wrong database' -Raw 'other_db|127.0.0.1|55592|0|0|0' -ExpectedOk $false
Add-PreflightCase -Case '50 preflight wrong host' -Raw 'mahjong_p52_restore|10.0.0.1|55592|0|0|0' -ExpectedOk $false
Add-PreflightCase -Case '51 preflight wrong port' -Raw 'mahjong_p52_restore|127.0.0.1|55593|0|0|0' -ExpectedOk $false
Add-PreflightCase -Case '52 preflight malformed fields' -Raw 'mahjong_p52_restore|127.0.0.1|55592|0|0' -ExpectedOk $false
Add-PreflightCase -Case '53 preflight non-numeric count' -Raw 'mahjong_p52_restore|127.0.0.1|55592|0|zero|0' -ExpectedOk $false
Add-PreflightCase -Case '54 preflight empty output' -Raw '' -ExpectedOk $false
Add-PreflightCase -Case '55 preflight two lines' -Raw "mahjong_p52_restore|127.0.0.1|55592|0|0|0`r`nmahjong_p52_restore|127.0.0.1|55592|0|0|0" -ExpectedOk $false

# ------------------------------------------------------- cleanup path guard
$artifactsGuard = Join-Path $evidenceDir 'artifacts-root'
New-Item -ItemType Directory -Force -Path (Join-Path $artifactsGuard 'p52-owned') | Out-Null
$guardCases = @(
    [ordered]@{ Case = '56 cleanup path contained'; Root = (Join-Path $artifactsGuard 'p52-owned'); Expected = $true },
    [ordered]@{ Case = '57 cleanup path is artifacts root'; Root = $artifactsGuard; Expected = $false },
    [ordered]@{ Case = '58 cleanup sibling prefixes'; Root = (Join-Path $evidenceDir 'artifacts-root-evil\p52-owned'); Expected = $false },
    [ordered]@{ Case = '59 cleanup outside artifacts'; Root = (Join-Path $evidenceDir 'elsewhere\p52-owned'); Expected = $false },
    [ordered]@{ Case = '60 cleanup empty path'; Root = ''; Expected = $false }
)
foreach ($guardCase in $guardCases) {
    $actual = Test-CleanupRootContained -Root $guardCase.Root -ArtifactsRoot $artifactsGuard
    Add-Result -Case $guardCase.Case -Pass ($actual -eq $guardCase.Expected) -Reason ("contained={0} expected={1} root={2}" -f $actual, $guardCase.Expected, $guardCase.Root)
}

# R1-04: execute the real production finalization TryStatementAst. It must
# exclude the manifest itself and fail nonzero when a mandatory seal write fails.
$sealAst = $ast.Find({
        param($node)
        $node -is [System.Management.Automation.Language.TryStatementAst] -and
        $node.Body.Extent.Text.Contains('$portMode =') -and
        $node.Body.Extent.Text.Contains('$manifest = @()')
    }, $true)
if ($null -eq $sealAst) { throw 'Cannot extract production finalization block' }

function Invoke-SealFixture([string]$Dir, [bool]$ManifestAsDirectory) {
    if (Test-Path -LiteralPath $Dir) { Remove-Item -LiteralPath $Dir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $Dir | Out-Null
    Write-Utf8 -Path (Join-Path $Dir 'a.txt') -Content 'alpha'
    Write-Utf8 -Path (Join-Path $Dir 'sub\b.txt') -Content 'beta'
    if ($ManifestAsDirectory) {
        New-Item -ItemType Directory -Path (Join-Path $Dir 'manifest.sha256.json') | Out-Null
    }
    $script:LogDir = $Dir
    $script:LogDirOwned = $true
    $script:SealOk = $false
    $script:Exit = 0
    $script:Stages = New-Object System.Collections.Generic.List[object]
    $script:Lines = New-Object System.Collections.Generic.List[string]
    $script:EnvRestoreRecords = @()
    $script:PortSelection = $null
    $script:Database = 'fixture'
    $script:RestoreDatabase = 'fixture_restore'
    $LogDir = $Dir
    $Port = 55592
    $sdkVersion = 'TOOL_FIXTURE'
    Invoke-Expression $sealAst.Extent.Text
}

$sealSuccessDir = Join-Path $evidenceDir '70-seal-success'
Invoke-SealFixture -Dir $sealSuccessDir -ManifestAsDirectory $false
$sealManifestPath = Join-Path $sealSuccessDir 'manifest.sha256.json'
$sealRows = @()
$sealBad = 0
$sealSelfRows = 0
if (Test-Path -LiteralPath $sealManifestPath) {
    $sealManifest = Get-Content -LiteralPath $sealManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $sealRows = @($sealManifest)
    $sealSelfRows = @($sealRows | Where-Object { $_.path -eq 'manifest.sha256.json' -or $_.path -eq 'manifest.sha256.json.tmp' }).Count
    foreach ($row in $sealRows) {
        $rowPath = Join-Path $sealSuccessDir $row.path
        if (-not (Test-Path -LiteralPath $rowPath)) { $sealBad++; continue }
        if ((Get-FileHash -LiteralPath $rowPath -Algorithm SHA256).Hash -ne $row.sha256) { $sealBad++ }
    }
}
$sealSuccessPass = ($script:Exit -eq 0) -and $script:SealOk -and
    ($sealRows.Count -ge 2) -and ($sealSelfRows -eq 0) -and ($sealBad -eq 0)
Add-Result -Case '70 seal success manifest excludes self and rehashes clean' -Pass $sealSuccessPass -Reason ("exit={0} sealOk={1} rows={2} self={3} bad={4}" -f $script:Exit, $script:SealOk, $sealRows.Count, $sealSelfRows, $sealBad)

$sealFailureDir = Join-Path $evidenceDir '71-seal-failure'
Invoke-SealFixture -Dir $sealFailureDir -ManifestAsDirectory $true
$sealFailurePass = ($script:Exit -ne 0) -and (-not $script:SealOk) -and
    (Test-Path -LiteralPath (Join-Path $sealFailureDir 'manifest.sha256.json') -PathType Container)
Add-Result -Case '71 seal failure exits nonzero and does not report pass' -Pass $sealFailurePass -Reason ("exit={0} sealOk={1}" -f $script:Exit, $script:SealOk)

# R1-04 end-to-end: an existing sealed LogDir must be byte-identical after a
# refusal, and the fresh Root must not be created.
$sealedDir = Join-Path $evidenceDir '72-sealed-existing'
New-Item -ItemType Directory -Force -Path $sealedDir | Out-Null
Write-Utf8 -Path (Join-Path $sealedDir 'sentinel.txt') -Content 'sealed sentinel'
Write-Utf8 -Path (Join-Path $sealedDir 'wrapper-summary.json') -Content '{"previous":"sealed"}'
Write-Utf8 -Path (Join-Path $sealedDir 'wrapper.log') -Content 'sealed log'
Write-Utf8 -Path (Join-Path $sealedDir 'manifest.sha256.json') -Content '{"previous":"manifest"}'
$sealedBefore = @{}
Get-ChildItem -LiteralPath $sealedDir -File | ForEach-Object { $sealedBefore[$_.Name] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
$freshRoot = Join-Path $repoRoot ('artifacts\seal-refusal-probe-' + [guid]::NewGuid().ToString('N'))
$refusalStdout = Join-Path $evidenceDir 'refusal.stdout.log'
$refusalStderr = Join-Path $evidenceDir 'refusal.stderr.log'
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = 'powershell.exe'
$psi.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $wrapperScript + '" -Port 0 -Root "' + $freshRoot + '" -LogDir "' + $sealedDir + '"'
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$childProcess = [System.Diagnostics.Process]::Start($psi)
$refusalStdoutText = $childProcess.StandardOutput.ReadToEnd()
$refusalStderrText = $childProcess.StandardError.ReadToEnd()
if (-not $childProcess.WaitForExit(30000)) {
    try { $childProcess.Kill() } catch { }
    throw 'sealed LogDir refusal probe did not exit within 30 seconds'
}
$refusalExit = $childProcess.ExitCode
Write-Utf8 -Path $refusalStdout -Content $refusalStdoutText
Write-Utf8 -Path $refusalStderr -Content $refusalStderrText
$sealedChanged = @()
foreach ($name in $sealedBefore.Keys) {
    $path = Join-Path $sealedDir $name
    if (-not (Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $sealedBefore[$name]) {
        $sealedChanged += $name
    }
}
$sealedCountAfter = @(Get-ChildItem -LiteralPath $sealedDir -Force).Count
$refusalPass = ($refusalExit -eq 2) -and
    ($sealedChanged.Count -eq 0) -and
    ($sealedCountAfter -eq $sealedBefore.Count) -and
    (-not (Test-Path -LiteralPath $freshRoot)) -and
    $refusalStdoutText.Contains('refusing to overwrite existing evidence')
Add-Result -Case '72 sealed LogDir refusal preserves bytes and creates no root' -Pass $refusalPass -Reason ("exit={0} changed={1} count={2} rootCreated={3} stdout={4}" -f $refusalExit, ($sealedChanged -join ','), $sealedCountAfter, (Test-Path -LiteralPath $freshRoot), (($refusalStdoutText -replace '\s+', ' ').Trim()))

$failedCases = @($results | Where-Object { -not $_.Pass })
$summary = [ordered]@{
    generatedUtc = (Get-Date).ToUniversalTime().ToString('o')
    productionScript = $wrapperScript
    productionScriptSha256 = (Get-FileHash -LiteralPath $wrapperScript -Algorithm SHA256).Hash
    cases = $results.Count
    passed = @($results | Where-Object { $_.Pass }).Count
    failed = $failedCases.Count
    results = $results
}
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidenceDir 'results.json') -Encoding UTF8
$results | Format-Table -AutoSize
Write-Output "Evidence: $evidenceDir"
Write-Output ("RESULT: {0}/{1} table evidence cases behaved as expected" -f $summary.passed, $summary.cases)
if ($failedCases.Count) { exit 1 }
exit 0
