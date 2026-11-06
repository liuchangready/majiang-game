#requires -Version 5.1
<#
.SYNOPSIS
    Negative/positive matrix for the production direct-runner XML evidence gate.

.DESCRIPTION
    This is a tool-boundary test, not a re-run of the 2,294 formal test cases.
    It extracts the real production functions from tools\verify-v5.ps1 with the
    PowerShell AST and calls them against throwaway xUnit.net v3-shaped result
    XML fixtures. Every negative case must be rejected by the SAME production
    decision function used by the unified verifier (Test-DirectInvocationEvidence);
    the script never mirrors that algorithm itself.

    Coverage:
      - green XML (including a LogDir/XML path containing spaces)
      - missing / empty / malformed / truncated XML
      - zero tests, assembly-summary-only (no <test> node)
      - wrong assembly identity
      - assembly total vs test-node count mismatch
      - collection aggregate vs test-node mismatch
      - failed / errors / skipped / not-run cases
      - native exit non-zero while XML looks green
      - stale XML (old last-write time) and XML outside this run's LogDir
      - missing console summary and console/XML count mismatch

    Fixtures are written only under this worktree's ignored artifacts directory,
    in a fresh GUID-named tree. Formal tests and production sources are untouched.
#>
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$verifyScript = Join-Path $repoRoot 'tools\verify-v5.ps1'
if (-not (Test-Path -LiteralPath $verifyScript)) { throw "Production verifier not found: $verifyScript" }

$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($verifyScript, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors -and $parseErrors.Count) { throw "Production verifier does not parse: $($parseErrors[0].Message)" }
foreach ($fnName in @('Get-DirectRunnerCounts', 'Get-TestSummaryFromDirectRunner', 'Read-DirectResultXml', 'Test-DirectInvocationEvidence')) {
    $fn = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $fnName }, $true)
    if (-not $fn) { throw "Cannot extract production function $fnName from $verifyScript" }
    . ([scriptblock]::Create($fn.Extent.Text))
}

$evidenceDir = Join-Path $repoRoot ('artifacts/direct-results-selftest-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $evidenceDir | Out-Null

function Write-Utf8([string]$Path, [string]$Content) {
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    [System.IO.File]::WriteAllText($Path, $Content, (New-Object System.Text.UTF8Encoding($false)))
}

function New-ResultXml {
    param(
        [string]$AssemblyName = 'Probe.Tests',
        [string[]]$Results = @('Pass', 'Pass', 'Pass'),
        [int]$Total = -1,
        [int]$Passed = -1,
        [int]$Failed = -1,
        [int]$Skipped = -1,
        [int]$Errors = 0,
        [int]$NotRun = -1,
        [switch]$AssemblyOnly,
        [switch]$AddErrorNode,
        [int]$CollectionTotal = -1
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

    $assemblyFull = 'D:\fixture path\bin\Release\net8.0\' + $AssemblyName + '.dll'
    $builder = New-Object System.Text.StringBuilder
    [void]$builder.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
    [void]$builder.AppendLine('<assemblies schema-version="3">')
    [void]$builder.AppendLine(('  <assembly name="{0}" test-framework="xUnit.net v3 4.0.2" target-framework=".NETCoreApp,Version=v8.0" total="{1}" passed="{2}" failed="{3}" skipped="{4}" errors="{5}" not-run="{6}">' -f $assemblyFull, $Total, $Passed, $Failed, $Skipped, $Errors, $NotRun))
    if ($AddErrorNode) {
        [void]$builder.AppendLine('    <errors><error type="Probe.Tests.BrokenFixture" name="collection error"><failure><message>framework error with no test node</message></failure></error></errors>')
    }
    if (-not $AssemblyOnly) {
        [void]$builder.AppendLine(('    <collection name="Test collection for {0}.ProbeTests" total="{1}" passed="{2}" failed="{3}" skipped="{4}" not-run="{5}">' -f $AssemblyName, $CollectionTotal, $passCount, $failCount, $skipCount, $notRunCount))
        for ($i = 0; $i -lt $Results.Count; $i++) {
            [void]$builder.AppendLine(('      <test id="00000000-0000-0000-0000-{0:D12}" name="{1}.ProbeTests.Case{2}" result="{3}" />' -f ($i + 1), $AssemblyName, ($i + 1), $Results[$i]))
        }
        [void]$builder.AppendLine('    </collection>')
    }
    [void]$builder.AppendLine('  </assembly>')
    [void]$builder.AppendLine('</assemblies>')
    return $builder.ToString()
}

function New-ConsoleSummary([int]$Total, [int]$Errors = 0, [int]$Failed = 0, [int]$Skipped = 0, [int]$NotRun = 0) {
    return ('  Probe.Tests  Total: {0}, Errors: {1}, Failed: {2}, Skipped: {3}, Not Run: {4}, Time: 0.123s' -f $Total, $Errors, $Failed, $Skipped, $NotRun)
}

$results = New-Object System.Collections.Generic.List[object]

function Invoke-EvidenceCase {
    param(
        [string]$Case,
        [string]$Xml,
        [string]$Console,
        [object]$NativeExit = 0,
        [string]$ExpectedAssembly = 'Probe.Tests',
        [int]$AgeDays = 0,
        [switch]$NoXml,
        [switch]$EmptyXml,
        [switch]$OutsideLogDir,
        [bool]$ExpectedOk
    )
    $caseDir = Join-Path $evidenceDir $Case
    New-Item -ItemType Directory -Force -Path $caseDir | Out-Null
    $xmlPath = Join-Path $caseDir 'Probe.Tests.result.xml'
    if (-not $NoXml) {
        Write-Utf8 -Path $xmlPath -Content $(if ($EmptyXml) { '' } else { $Xml })
        if ($AgeDays -gt 0) {
            (Get-Item -LiteralPath $xmlPath).LastWriteTimeUtc = [DateTime]::UtcNow.AddDays(-$AgeDays)
        }
    }
    $consolePath = Join-Path $caseDir 'direct-Probe.Tests.log'
    Write-Utf8 -Path $consolePath -Content $Console
    $consoleCounts = Get-DirectRunnerCounts $consolePath
    $logDirForDecision = $caseDir
    if ($OutsideLogDir) {
        $other = Join-Path $caseDir 'other-run'
        New-Item -ItemType Directory -Force -Path $other | Out-Null
        $logDirForDecision = $other
    }
    $started = [DateTime]::UtcNow
    $evidence = Test-DirectInvocationEvidence -XmlPath $xmlPath -ExpectedAssemblyName $ExpectedAssembly `
        -LogDir $logDirForDecision -RunStartedUtc $started -ConsoleCounts $consoleCounts -NativeExit $NativeExit
    $passed = ([bool]$evidence.Ok -eq $ExpectedOk) -and (-not [string]::IsNullOrWhiteSpace($evidence.Reason))
    $record = [pscustomobject]@{
        Case          = $Case
        ExpectedOk    = $ExpectedOk
        ActualOk      = [bool]$evidence.Ok
        Reason        = $evidence.Reason
        NodeCount     = $evidence.TestNodeCount
        ConsoleTotal  = $(if ($consoleCounts) { $consoleCounts.Total } else { $null })
        Pass          = $passed
        Directory     = $caseDir
    }
    $results.Add($record)
    return $record
}

# --- positive control (LogDir and XML path deliberately contain spaces) --------
$greenXml = New-ResultXml
$greenConsole = New-ConsoleSummary -Total 3
$positive = Invoke-EvidenceCase -Case '01 positive green in dir with spaces' -Xml $greenXml -Console $greenConsole -ExpectedOk $true
if (-not $positive.Pass) { throw "Positive control was rejected: $($positive.Reason)" }

# --- missing / empty / malformed / truncated XML -------------------------------
$null = Invoke-EvidenceCase -Case '02 missing xml' -Xml '' -Console $greenConsole -NoXml -ExpectedOk $false
$null = Invoke-EvidenceCase -Case '03 empty xml' -Xml '' -Console $greenConsole -EmptyXml -ExpectedOk $false
$null = Invoke-EvidenceCase -Case '04 malformed xml' -Xml 'this is not XML at all' -Console $greenConsole -ExpectedOk $false
$null = Invoke-EvidenceCase -Case '05 truncated xml' -Xml ($greenXml.Substring(0, [Math]::Max(20, $greenXml.Length - 60))) -Console $greenConsole -ExpectedOk $false

# --- structure: zero tests, summary-only, wrong assembly, count mismatches -----
$null = Invoke-EvidenceCase -Case '06 zero tests' -Xml (New-ResultXml -Results @() -Total 0 -Passed 0) -Console (New-ConsoleSummary -Total 0) -ExpectedOk $false
$null = Invoke-EvidenceCase -Case '07 assembly summary without test nodes' -Xml (New-ResultXml -Results @() -Total 3 -Passed 3 -AssemblyOnly) -Console $greenConsole -ExpectedOk $false
$null = Invoke-EvidenceCase -Case '08 wrong assembly' -Xml (New-ResultXml -AssemblyName 'Other.Tests') -Console $greenConsole -ExpectedOk $false
$null = Invoke-EvidenceCase -Case '09 total vs node mismatch' -Xml (New-ResultXml -Results @('Pass', 'Pass') -Total 3 -Passed 3 -CollectionTotal 3) -Console (New-ConsoleSummary -Total 3) -ExpectedOk $false
$null = Invoke-EvidenceCase -Case '10 collection total vs node mismatch' -Xml (New-ResultXml -Results @('Pass', 'Pass', 'Pass') -CollectionTotal 4) -Console $greenConsole -ExpectedOk $false

# --- framework results that are not green --------------------------------------
$null = Invoke-EvidenceCase -Case '11 failed test' -Xml (New-ResultXml -Results @('Fail', 'Pass') -Total 2 -Passed 1 -Failed 1) -Console (New-ConsoleSummary -Total 2 -Failed 1) -NativeExit 0 -ExpectedOk $false
$null = Invoke-EvidenceCase -Case '12 framework errors without a test node' -Xml (New-ResultXml -Results @('Pass') -Total 1 -Passed 1 -Errors 1 -AddErrorNode) -Console (New-ConsoleSummary -Total 1 -Errors 1) -NativeExit 0 -ExpectedOk $false
$null = Invoke-EvidenceCase -Case '13 skipped test' -Xml (New-ResultXml -Results @('Skip', 'Pass') -Total 2 -Passed 1 -Skipped 1) -Console (New-ConsoleSummary -Total 2 -Skipped 1) -ExpectedOk $false
$null = Invoke-EvidenceCase -Case '14 not-run test' -Xml (New-ResultXml -Results @('NotRun', 'Pass') -Total 2 -Passed 1 -NotRun 1) -Console (New-ConsoleSummary -Total 2 -NotRun 1) -ExpectedOk $false

# --- authority / freshness / attribution ---------------------------------------
$null = Invoke-EvidenceCase -Case '15 native exit nonzero but xml green' -Xml $greenXml -Console $greenConsole -NativeExit 1 -ExpectedOk $false
$null = Invoke-EvidenceCase -Case '16 stale xml' -Xml $greenXml -Console $greenConsole -AgeDays 1 -ExpectedOk $false
$null = Invoke-EvidenceCase -Case '17 xml outside logdir' -Xml $greenXml -Console $greenConsole -OutsideLogDir -ExpectedOk $false
$null = Invoke-EvidenceCase -Case '18 console summary missing' -Xml $greenXml -Console 'no summary line here' -ExpectedOk $false
$null = Invoke-EvidenceCase -Case '19 console total mismatch' -Xml $greenXml -Console (New-ConsoleSummary -Total 2) -ExpectedOk $false

$failedCases = @($results | Where-Object { -not $_.Pass })
$summary = [ordered]@{
    generatedUtc = (Get-Date).ToUniversalTime().ToString('o')
    productionScript = $verifyScript
    productionScriptSha256 = (Get-FileHash -LiteralPath $verifyScript -Algorithm SHA256).Hash
    cases = $results.Count
    passed = @($results | Where-Object { $_.Pass }).Count
    failed = $failedCases.Count
    results = $results
}
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidenceDir 'results.json') -Encoding UTF8
$results | Format-Table -AutoSize Case, ExpectedOk, ActualOk, Pass, ConsoleTotal, NodeCount, Reason
Write-Output "Evidence: $evidenceDir"
Write-Output ("RESULT: {0}/{1} cases behaved as expected" -f $summary.passed, $summary.cases)
if ($failedCases.Count) { exit 1 }
exit 0
