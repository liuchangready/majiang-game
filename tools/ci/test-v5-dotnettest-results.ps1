#requires -Version 5.1
<#
.SYNOPSIS
    Negative/positive matrix for the production `dotnet test` native per-case
    evidence gate, plus real live dotnet-test probes.

.DESCRIPTION
    D-A68-02 is the inherited evidence gap: CI ran the official in-process
    runner directly and called the result "dotnetTest", while `dotnet test`
    itself only produced aggregate console output. This tool test exercises the
    REAL production decision function extracted from tools\verify-v5.ps1 with
    the PowerShell AST:

        Test-DotnetTestInvocationEvidence

    Every fixture negative must be rejected by that same production function.
    The script never mirrors the algorithm itself.

    Synthetic fixtures (under the ignored artifacts root) cover:
      missing / empty / malformed / truncated XML, zero tests, assembly
      summary-only, wrong assembly leaf, wrong assembly file path, wrong
      runner framework identity, out-of-run LogDir, outside the project
      results directory, stale file, duplicate native IDs, assembly/collection
      aggregate mismatches, failed/skipped/not-run/errors results, non-zero
      native exit with a green XML, missing console summary, console/count
      mismatch, unexpected file name, and a path escape. A green positive with
      deliberately duplicate DISPLAY NAMES proves that display names are not
      treated as native IDs.

    Real live probes (under ignored build\) create two throwaway xunit.v3
    4.0.2/MTP projects, run the exact production `dotnet test` native report
    switches, and feed the resulting files back through the production gate:
      - a passing project (3 cases, two identical display names) must PASS;
      - a genuinely failing project must exit non-zero, emit a real per-case
        XML with a failed node, and be rejected by the gate. This proves the
        probe is not reading an older DLL or a missing build.

    Fixtures are evidence shapes, never claimed as real dotnet-test output;
    the live probes are the only real-run records.

    Exit codes: 0 = every case behaved as expected; 1 = at least one case failed.
#>
param(
    [string]$DotnetPath,
    [string]$EvidenceDir
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$verifyScript = Join-Path $repoRoot 'tools\verify-v5.ps1'
if (-not (Test-Path -LiteralPath $verifyScript)) { throw "Production verifier not found: $verifyScript" }
if (-not $DotnetPath) {
    if (Test-Path -LiteralPath 'D:\Unity\dotnet\dotnet.exe') { $DotnetPath = 'D:\Unity\dotnet\dotnet.exe' }
    else { $DotnetPath = (Get-Command dotnet -ErrorAction Stop).Source }
}
if (-not (Test-Path -LiteralPath $DotnetPath)) { throw "dotnet not found: $DotnetPath" }

$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($verifyScript, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors -and $parseErrors.Count) { throw "Production verifier does not parse: $($parseErrors[0].Message)" }
foreach ($fnName in @('Invoke-Captured', 'Get-DotnetTestConsoleCounts', 'Get-DotnetTestResultsArgument', 'Read-DirectResultXml', 'Test-DotnetTestInvocationEvidence')) {
    $fn = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $fnName }, $true)
    if (-not $fn) { throw "Cannot extract production function $fnName from $verifyScript" }
    . ([scriptblock]::Create($fn.Extent.Text))
}

if (-not $EvidenceDir) {
    $EvidenceDir = Join-Path $repoRoot ('artifacts/dotnettest-results-selftest-' + [guid]::NewGuid().ToString('N'))
}
$EvidenceDir = [IO.Path]::GetFullPath($EvidenceDir)
New-Item -ItemType Directory -Force -Path $EvidenceDir | Out-Null

function Write-Utf8([string]$Path, [string]$Content) {
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    [System.IO.File]::WriteAllText($Path, $Content, (New-Object System.Text.UTF8Encoding($false)))
}

function New-DotnetTestXml {
    param(
        [string]$AssemblyPath = 'D:\fixture path\bin\Release\net8.0\Probe.Tests.dll',
        [string]$Framework = 'xUnit.net v3 4.0.2+probe',
        [string[]]$Results = @('Pass', 'Pass', 'Pass'),
        [int]$Total = -1,
        [int]$Passed = -1,
        [int]$Failed = -1,
        [int]$Skipped = -1,
        [int]$Errors = 0,
        [int]$NotRun = -1,
        [switch]$AssemblyOnly,
        [switch]$AddErrorNode,
        [int]$CollectionTotal = -1,
        [int]$CollectionPassed = -1,
        [int]$CollectionFailed = -1,
        [int]$CollectionSkipped = -1,
        [int]$CollectionNotRun = -1,
        [string[]]$Ids = @(),
        [string[]]$Names = @(),
        [int]$SchemaVersion = 3
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
    if ($CollectionFailed -lt 0) { $CollectionFailed = $failCount }
    if ($CollectionSkipped -lt 0) { $CollectionSkipped = $skipCount }
    if ($CollectionNotRun -lt 0) { $CollectionNotRun = $notRunCount }

    $builder = New-Object System.Text.StringBuilder
    [void]$builder.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
    [void]$builder.AppendLine(('<assemblies schema-version="{0}">' -f $SchemaVersion))
    [void]$builder.AppendLine(('  <assembly name="{0}" test-framework="{1}" target-framework=".NETCoreApp,Version=v8.0" total="{2}" passed="{3}" failed="{4}" skipped="{5}" errors="{6}" not-run="{7}">' -f $AssemblyPath, $Framework, $Total, $Passed, $Failed, $Skipped, $Errors, $NotRun))
    if ($AddErrorNode) {
        [void]$builder.AppendLine('    <errors><error type="Probe.Tests.BrokenFixture" name="collection error"><failure><message>controlled framework error</message></failure></error></errors>')
    }
    if (-not $AssemblyOnly) {
        [void]$builder.AppendLine(('    <collection name="Test collection for Probe.Tests" total="{0}" passed="{1}" failed="{2}" skipped="{3}" not-run="{4}">' -f $CollectionTotal, $CollectionPassed, $CollectionFailed, $CollectionSkipped, $CollectionNotRun))
        for ($i = 0; $i -lt $Results.Count; $i++) {
            $id = $(if ($Ids.Count -gt $i) { $Ids[$i] } else { '00000000-0000-0000-0000-{0:D12}' -f ($i + 1) })
            $testName = $(if ($Names.Count -gt $i) { $Names[$i] } else { 'Probe.Tests.Case{0}' -f ($i + 1) })
            [void]$builder.AppendLine(('      <test id="{0}" name="{1}" result="{2}" />' -f $id, $testName, $Results[$i]))
        }
        [void]$builder.AppendLine('    </collection>')
    }
    [void]$builder.AppendLine('  </assembly>')
    [void]$builder.AppendLine('</assemblies>')
    return $builder.ToString()
}

function New-DotnetTestConsoleSummary {
    param([int]$Total, [int]$Passed = -1, [int]$Failed = 0, [int]$Skipped = 0)
    if ($Passed -lt 0) { $Passed = $Total - $Failed - $Skipped }
    $verdict = $(if ($Failed -eq 0) { 'Passed!' } else { 'Failed!' })
    return ('  Probe.Tests.dll (net8.0|x64)' + [Environment]::NewLine +
        ('  {0} - Failed: {1}, Passed: {2}, Skipped: {3}, Total: {4}, Duration: 1s' -f $verdict, $Failed, $Passed, $Skipped, $Total))
}

$results = New-Object System.Collections.Generic.List[object]

function Invoke-GateCase {
    param(
        [string]$Case,
        [string]$Xml = $null,
        [string]$Console = $null,
        [object]$NativeExit = 0,
        [string]$ExpectedAssembly = 'Probe.Tests',
        [string]$ExpectedAssemblyPath = 'D:\fixture path\bin\Release\net8.0\Probe.Tests.dll',
        [string]$ExpectedFramework = 'xUnit.net v3 4.0.2',
        [int]$AgeDays = 0,
        [switch]$NoXml,
        [switch]$EmptyXml,
        [switch]$OutsideResults,
        [switch]$OutsideLog,
        [switch]$EscapePath,
        [string]$FileName = 'dotnet-test-results.xml',
        [bool]$ExpectedOk
    )
    $caseDir = Join-Path $EvidenceDir $Case
    New-Item -ItemType Directory -Force -Path $caseDir | Out-Null
    $resultsDir = Join-Path $caseDir 'results'
    New-Item -ItemType Directory -Force -Path $resultsDir | Out-Null
    $xmlPath = Join-Path $resultsDir $FileName
    if ($EscapePath) {
        $xmlPath = Join-Path $resultsDir '..\escaped-result.xml'
    }
    if (-not $NoXml) {
        Write-Utf8 -Path $xmlPath -Content $(if ($EmptyXml) { '' } else { $Xml })
        if ($AgeDays -gt 0) {
            (Get-Item -LiteralPath $xmlPath).LastWriteTimeUtc = [DateTime]::UtcNow.AddDays(-$AgeDays)
        }
    }
    $consoleCounts = $null
    if ($null -ne $Console) {
        $consolePath = Join-Path $caseDir 'dotnetTest-Probe.log'
        Write-Utf8 -Path $consolePath -Content $Console
        $consoleCounts = Get-DotnetTestConsoleCounts $consolePath
    }
    $decisionResults = $resultsDir
    if ($OutsideResults) {
        $decisionResults = Join-Path $caseDir 'other-results'
        New-Item -ItemType Directory -Force -Path $decisionResults | Out-Null
    }
    $decisionLog = $caseDir
    if ($OutsideLog) {
        $decisionLog = Join-Path $caseDir 'other-run'
        New-Item -ItemType Directory -Force -Path $decisionLog | Out-Null
    }
    $started = [DateTime]::UtcNow
    $evidence = Test-DotnetTestInvocationEvidence -XmlPath $xmlPath -ExpectedAssemblyName $ExpectedAssembly `
        -ExpectedAssemblyPath $ExpectedAssemblyPath -ResultsDir $decisionResults -LogDir $decisionLog `
        -RunStartedUtc $started -ConsoleCounts $consoleCounts -NativeExit $NativeExit -ExpectedFramework $ExpectedFramework
    $passed = ([bool]$evidence.Ok -eq $ExpectedOk) -and (-not [string]::IsNullOrWhiteSpace($evidence.Reason))
    $record = [pscustomobject]@{
        Case         = $Case
        ExpectedOk   = $ExpectedOk
        ActualOk     = [bool]$evidence.Ok
        Reason       = $evidence.Reason
        NodeCount    = $evidence.TestNodeCount
        ConsoleTotal = $(if ($consoleCounts) { $consoleCounts.Total } else { $null })
        Pass         = $passed
        Directory    = $caseDir
    }
    $results.Add($record)
    return $record
}

# --- results-directory argument helper ----------------------------------------
$spaceDir = Join-Path $EvidenceDir '00 space path/results'
New-Item -ItemType Directory -Force -Path $spaceDir | Out-Null
$spaceArg = Get-DotnetTestResultsArgument $spaceDir
$spaceArgOk = $(if ([System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT) {
    ($spaceArg -notmatch '\s') -and (Test-Path -LiteralPath $spaceArg)
} else {
    $spaceArg -eq $spaceDir
})
$spaceRecord = [pscustomobject]@{ Case = '00 results path argument'; ExpectedOk = $true; ActualOk = $spaceArgOk; Reason = "space-free argument='$spaceArg'"; NodeCount = $null; ConsoleTotal = $null; Pass = $spaceArgOk; Directory = $spaceDir }
$results.Add($spaceRecord)
if (-not $spaceArgOk) { throw "Results-directory argument helper failed for '$spaceDir' -> '$spaceArg'" }

# --- console parser ------------------------------------------------------------
$parserDir = Join-Path $EvidenceDir '00 console parser'
New-Item -ItemType Directory -Force -Path $parserDir | Out-Null
$enLog = Join-Path $parserDir 'en.log'
$enFailedLog = Join-Path $parserDir 'en-failed.log'
Write-Utf8 -Path $enLog -Content (New-DotnetTestConsoleSummary -Total 180)
Write-Utf8 -Path $enFailedLog -Content (New-DotnetTestConsoleSummary -Total 2 -Passed 1 -Failed 1)
$enCounts = Get-DotnetTestConsoleCounts $enLog
$enFailedCounts = Get-DotnetTestConsoleCounts $enFailedLog
foreach ($parserCase in @(
        @{ Name = 'parser english passed'; Counts = $enCounts; Total = 180; Passed = 180; Failed = 0; Skipped = 0 },
        @{ Name = 'parser english failed'; Counts = $enFailedCounts; Total = 2; Passed = 1; Failed = 1; Skipped = 0 }
    )) {
    $ok = ($null -ne $parserCase.Counts -and
        [int]$parserCase.Counts.Total -eq $parserCase.Total -and
        [int]$parserCase.Counts.Passed -eq $parserCase.Passed -and
        [int]$parserCase.Counts.Failed -eq $parserCase.Failed -and
        [int]$parserCase.Counts.Skipped -eq $parserCase.Skipped)
    $record = [pscustomobject]@{ Case = $parserCase.Name; ExpectedOk = $true; ActualOk = $ok; Reason = 'console summary parse'; NodeCount = $null; ConsoleTotal = $(if ($parserCase.Counts) { $parserCase.Counts.Total } else { $null }); Pass = $ok; Directory = $parserDir }
    $results.Add($record)
    if (-not $ok) { throw "Console parser case failed: $($parserCase.Name)" }
}

# --- positive controls ---------------------------------------------------------
$greenXml = New-DotnetTestXml -Names @('same display name is legal', 'same display name is legal', 'third case')
$greenConsole = New-DotnetTestConsoleSummary -Total 3
$positive = Invoke-GateCase -Case '01 positive green with duplicate display names' -Xml $greenXml -Console $greenConsole -ExpectedOk $true
if (-not $positive.Pass) { throw "Positive control was rejected: $($positive.Reason)" }
$positivePath = Invoke-GateCase -Case '02 positive path with spaces' -Xml (New-DotnetTestXml -Results @('Pass')) -Console (New-DotnetTestConsoleSummary -Total 1) -ExpectedOk $true
if (-not $positivePath.Pass) { throw "Positive path-with-spaces case was rejected: $($positivePath.Reason)" }

# --- missing / empty / malformed / truncated -----------------------------------
$null = Invoke-GateCase -Case '03 missing xml' -Xml '' -Console $greenConsole -NoXml -ExpectedOk $false
$null = Invoke-GateCase -Case '04 empty xml' -Xml '' -Console $greenConsole -EmptyXml -ExpectedOk $false
$null = Invoke-GateCase -Case '05 malformed xml' -Xml 'this is not XML at all' -Console $greenConsole -ExpectedOk $false
$null = Invoke-GateCase -Case '06 truncated xml' -Xml ($greenXml.Substring(0, [Math]::Max(20, $greenXml.Length - 60))) -Console $greenConsole -ExpectedOk $false

# --- structure: zero, summary-only, identity, aggregate mismatch ----------------
$null = Invoke-GateCase -Case '07 zero tests' -Xml (New-DotnetTestXml -Results @() -Total 0 -Passed 0 -AssemblyOnly) -Console (New-DotnetTestConsoleSummary -Total 0) -ExpectedOk $false
$null = Invoke-GateCase -Case '08 assembly summary only' -Xml (New-DotnetTestXml -Results @() -Total 3 -Passed 3 -AssemblyOnly) -Console $greenConsole -ExpectedOk $false
$null = Invoke-GateCase -Case '09 wrong assembly leaf' -Xml (New-DotnetTestXml -AssemblyPath 'D:\fixture path\bin\Release\net8.0\Other.Tests.dll') -Console $greenConsole -ExpectedOk $false
$null = Invoke-GateCase -Case '10 wrong assembly path same leaf' -Xml (New-DotnetTestXml -AssemblyPath 'D:\other\bin\Release\net8.0\Probe.Tests.dll') -Console $greenConsole -ExpectedOk $false
$null = Invoke-GateCase -Case '11 wrong runner framework' -Xml (New-DotnetTestXml -Framework 'xUnit.net v3 4.0.1+old') -Console $greenConsole -ExpectedOk $false
$null = Invoke-GateCase -Case '12 assembly total vs node mismatch' -Xml (New-DotnetTestXml -Results @('Pass', 'Pass') -Total 3 -Passed 3 -CollectionTotal 3) -Console (New-DotnetTestConsoleSummary -Total 3) -ExpectedOk $false
$null = Invoke-GateCase -Case '13 collection total vs node mismatch' -Xml (New-DotnetTestXml -Results @('Pass', 'Pass', 'Pass') -CollectionTotal 4) -Console $greenConsole -ExpectedOk $false
$null = Invoke-GateCase -Case '14 assembly passed mismatch' -Xml (New-DotnetTestXml -Results @('Pass', 'Pass') -Total 2 -Passed 1 -Failed 0 -Skipped 0 -NotRun 0) -Console (New-DotnetTestConsoleSummary -Total 2) -ExpectedOk $false
$null = Invoke-GateCase -Case '15 collection passed mismatch' -Xml (New-DotnetTestXml -Results @('Pass', 'Pass') -CollectionPassed 1) -Console (New-DotnetTestConsoleSummary -Total 2) -ExpectedOk $false

# --- uniqueness / freshness / attribution --------------------------------------
$null = Invoke-GateCase -Case '16 duplicate native ids' -Xml (New-DotnetTestXml -Ids @('same-id', 'same-id', 'third-id')) -Console $greenConsole -ExpectedOk $false
$null = Invoke-GateCase -Case '17 stale xml' -Xml $greenXml -Console $greenConsole -AgeDays 1 -ExpectedOk $false
$null = Invoke-GateCase -Case '18 results directory outside logdir' -Xml $greenXml -Console $greenConsole -OutsideLog -ExpectedOk $false
$null = Invoke-GateCase -Case '19 xml outside project results directory' -Xml $greenXml -Console $greenConsole -OutsideResults -ExpectedOk $false
$null = Invoke-GateCase -Case '20 path escape' -Xml $greenXml -Console $greenConsole -EscapePath -ExpectedOk $false
$null = Invoke-GateCase -Case '21 unexpected file name' -Xml $greenXml -Console $greenConsole -FileName 'other.xml' -ExpectedOk $false

# --- non-green framework results / native authority ----------------------------
$null = Invoke-GateCase -Case '22 failed test' -Xml (New-DotnetTestXml -Results @('Fail', 'Pass') -Total 2 -Passed 1 -Failed 1) -Console (New-DotnetTestConsoleSummary -Total 2 -Passed 1 -Failed 1) -ExpectedOk $false
$null = Invoke-GateCase -Case '23 skipped test' -Xml (New-DotnetTestXml -Results @('Skip', 'Pass') -Total 2 -Passed 1 -Skipped 1) -Console (New-DotnetTestConsoleSummary -Total 2 -Passed 1 -Skipped 1) -ExpectedOk $false
$null = Invoke-GateCase -Case '24 not-run test' -Xml (New-DotnetTestXml -Results @('NotRun', 'Pass') -Total 2 -Passed 1 -NotRun 1) -Console (New-DotnetTestConsoleSummary -Total 2 -Passed 1) -ExpectedOk $false
$null = Invoke-GateCase -Case '25 framework errors' -Xml (New-DotnetTestXml -Results @('Pass') -Total 1 -Passed 1 -Errors 1 -AddErrorNode) -Console (New-DotnetTestConsoleSummary -Total 1) -ExpectedOk $false
$null = Invoke-GateCase -Case '26 native exit nonzero with green xml' -Xml $greenXml -Console $greenConsole -NativeExit 1 -ExpectedOk $false
$null = Invoke-GateCase -Case '27 native exit unknown' -Xml $greenXml -Console $greenConsole -NativeExit $null -ExpectedOk $false

# --- console cross-check -------------------------------------------------------
$null = Invoke-GateCase -Case '28 console summary missing' -Xml $greenXml -Console $null -ExpectedOk $false
$null = Invoke-GateCase -Case '29 console total mismatch' -Xml $greenXml -Console (New-DotnetTestConsoleSummary -Total 2) -ExpectedOk $false
$null = Invoke-GateCase -Case '30 console failed mismatch' -Xml $greenXml -Console (New-DotnetTestConsoleSummary -Total 3 -Passed 2 -Failed 1) -ExpectedOk $false
$null = Invoke-GateCase -Case '31 console skipped mismatch' -Xml $greenXml -Console (New-DotnetTestConsoleSummary -Total 3 -Passed 2 -Skipped 1) -ExpectedOk $false

# --- live fixture projects (the only real dotnet-test records) -----------------
function New-LiveFixtureProject {
    param([string]$Root, [string]$Name, [string]$AssemblyName, [string]$TestSource)
    $projectDir = Join-Path $Root $Name
    New-Item -ItemType Directory -Force -Path $projectDir | Out-Null
    $csproj = Join-Path $projectDir ($Name + '.csproj')
    $csprojText = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <AssemblyName>$AssemblyName</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" Version="4.0.2" />
  </ItemGroup>
</Project>
"@
    Write-Utf8 -Path $csproj -Content $csprojText
    Write-Utf8 -Path (Join-Path $projectDir 'LiveTests.cs') -Content $TestSource
    return [pscustomobject]@{ Dir = $projectDir; Csproj = $csproj; AssemblyName = $AssemblyName; Dll = (Join-Path $projectDir ('bin\Release\net8.0\' + $AssemblyName + '.dll')) }
}

function Invoke-LiveCase {
    param(
        [string]$Case,
        [object]$Fixture,
        [bool]$ExpectGateOk,
        [bool]$ExpectZeroExit
    )
    $resultsDir = Join-Path $Fixture.Dir 'native-results'
    if (Test-Path -LiteralPath $resultsDir) { Remove-Item -LiteralPath $resultsDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $resultsDir | Out-Null
    $log = Join-Path $Fixture.Dir 'dotnet-test.log'
    $resultsArgument = Get-DotnetTestResultsArgument $resultsDir
    $testArgs = @(
        'test', $Fixture.Csproj, '-c', 'Release', '--nologo', '--',
        '--report-xunit-xml', '--report-xunit-xml-filename', 'dotnet-test-results.xml',
        '--results-directory', $resultsArgument
    )
    $started = [DateTime]::UtcNow
    $exit = Invoke-Captured -LogPath $log -Command { & $DotnetPath @testArgs }
    $console = Get-DotnetTestConsoleCounts $log
    $xmlPath = Join-Path $resultsDir 'dotnet-test-results.xml'
    $evidence = Test-DotnetTestInvocationEvidence -XmlPath $xmlPath -ExpectedAssemblyName $Fixture.AssemblyName `
        -ExpectedAssemblyPath $Fixture.Dll -ResultsDir $resultsDir -LogDir $Fixture.Dir `
        -RunStartedUtc $started -ConsoleCounts $console -NativeExit $exit
    $xmlExists = Test-Path -LiteralPath $xmlPath -PathType Leaf
    $failedNodes = 0
    $duplicateDisplayNames = 0
    $firstFailure = $null
    if ($xmlExists) {
        try {
            $doc = [xml](Get-Content -LiteralPath $xmlPath -Raw)
            $failedNode = @($doc.SelectNodes('//test[@result="Fail"]')) | Select-Object -First 1
            $failedNodes = @($doc.SelectNodes('//test[@result="Fail"]')).Count
            if ($failedNode) { $firstFailure = $failedNode.name }
            $duplicateDisplayNames = @($doc.SelectNodes('//test') | Group-Object name | Where-Object { $_.Count -gt 1 }).Count
        }
        catch { }
    }
    $exitOk = ([bool]($exit -eq 0) -eq $ExpectZeroExit)
    $passed = ($exitOk -and ([bool]$evidence.Ok -eq $ExpectGateOk) -and $xmlExists -and (-not [string]::IsNullOrWhiteSpace($evidence.Reason)))
    if (-not $ExpectGateOk) { $passed = $passed -and ($exit -ne 0) -and ($failedNodes -gt 0) }
    if ($ExpectGateOk) { $passed = $passed -and ($duplicateDisplayNames -gt 0) }
    $record = [pscustomobject]@{
        Case         = $Case
        ExpectedOk   = $ExpectGateOk
        ActualOk     = [bool]$evidence.Ok
        Reason       = $evidence.Reason
        NodeCount    = $evidence.TestNodeCount
        ConsoleTotal = $(if ($console) { $console.Total } else { $null })
        Pass         = $passed
        Directory    = $Fixture.Dir
        NativeExit   = $exit
        XmlExists    = $xmlExists
        FailedNodes  = $failedNodes
        FirstFailure = $firstFailure
        DuplicateDisplayNames = $duplicateDisplayNames
    }
    $results.Add($record)
    return $record
}

$liveRoot = Join-Path $repoRoot ('build/dotnettest-selftest-' + [guid]::NewGuid().ToString('N') + '/live-fixtures')
New-Item -ItemType Directory -Force -Path $liveRoot | Out-Null
$positiveSource = @'
using Xunit;

namespace Probe.Positive
{
    public sealed class AlphaTests
    {
        [Fact(DisplayName = "same display name is legal")]
        public void First() { Assert.True(true); }

        [Fact]
        public void Second() { Assert.True(true); }
    }

    public sealed class BetaTests
    {
        [Fact(DisplayName = "same display name is legal")]
        public void Third() { Assert.True(true); }
    }
}
'@
$negativeSource = @'
using Xunit;

namespace Probe.Negative
{
    public sealed class FailingTests
    {
        [Fact]
        public void ControlledFailure()
        {
            Assert.True(false, "controlled native failure fixture");
        }
    }
}
'@
$positiveFixture = New-LiveFixtureProject -Root $liveRoot -Name 'Probe.Positive.Tests' -AssemblyName 'Probe.Positive.Tests' -TestSource $positiveSource
$negativeFixture = New-LiveFixtureProject -Root $liveRoot -Name 'Probe.Negative.Tests' -AssemblyName 'Probe.Negative.Tests' -TestSource $negativeSource
$livePositive = Invoke-LiveCase -Case '32 live dotnet test positive' -Fixture $positiveFixture -ExpectGateOk $true -ExpectZeroExit $true
$liveNegative = Invoke-LiveCase -Case '33 live dotnet test controlled failure' -Fixture $negativeFixture -ExpectGateOk $false -ExpectZeroExit $false

$failedCases = @($results | Where-Object { -not $_.Pass })
$summary = [ordered]@{
    generatedUtc            = (Get-Date).ToUniversalTime().ToString('o')
    productionScript        = $verifyScript
    productionScriptSha256  = (Get-FileHash -LiteralPath $verifyScript -Algorithm SHA256).Hash
    dotnet                  = $DotnetPath
    dotnetVersion           = ((& $DotnetPath --version) | Out-String).Trim()
    cases                   = $results.Count
    passed                  = @($results | Where-Object { $_.Pass }).Count
    failed                  = $failedCases.Count
    liveFixtureRoot         = $liveRoot
    results                 = $results
}
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $EvidenceDir 'results.json') -Encoding UTF8
$results | Format-Table -AutoSize Case, ExpectedOk, ActualOk, Pass, ConsoleTotal, NodeCount, NativeExit, FailedNodes, DuplicateDisplayNames, Reason
Write-Output "Evidence: $EvidenceDir"
Write-Output "Live fixtures: $liveRoot"
Write-Output ("RESULT: {0}/{1} cases behaved as expected" -f $summary.passed, $summary.cases)
if ($failedCases.Count) { exit 1 }
exit 0
