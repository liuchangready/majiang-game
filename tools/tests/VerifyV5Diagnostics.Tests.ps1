<#
.SYNOPSIS
    Isolated self-test for the diagnostics collection added to tools\verify-v5.ps1.

.DESCRIPTION
    Builds a throwaway xunit.v3 / Microsoft.Testing.Platform fixture project under
    this task's own TEMP directory (never inside the repository, never a business
    test project) and runs the REAL unified verification script against it with
    -NoProjectDiscovery -SkipPluginCopyCheck, using the same runner modes and the
    same direct-runner arguments as the production manifest.

    Proven, per scenario:
      S1 dotnetTest FAIL : non-zero exit, FAIL row, real counts (2 total / 1
                           failed / 0 skipped) preserved, and the raw framework
                           log - the same TestResults\<asm>_<tfm>_<arch>.log file
                           dotnet test names in its error line - is collected
                           under <log dir>\test-results\<project>\dotnetTest\
                           (the subtree the CI workflow uploads) and contains
                           the failing case name and the assertion message.
      S2 stale -> PASS   : a fake OLD failure log planted in the project's
                           TestResults directory BEFORE the run cannot leak into
                           the new run: the run PASSes, the collected evidence
                           contains no stale content, and the planted file is
                           gone from the project (purge-before-run contract).
      S3 direct FAIL     : same failure proven via -RunnerMode direct: non-zero
                           exit, FAIL row, counts preserved, case name and
                           assertion present in direct-<project>.log - under
                           this stack the in-process MTP runner writes no
                           TestResults file, so the captured console IS the
                           framework evidence; collection must mirror whatever
                           files do exist and invent or leak none.
      S4 stale -> PASS   : same purge contract under direct mode.
      S5 zero tests      : the pre-existing zero-test-case gate still fails the
                           run after the diagnostics changes (exit 1, FAIL).
      S6 locked+PASS     : an OLD log locked so it cannot be deleted makes the
                           purge fail; the run still PASSes, the locked stale
                           file is NOT copied into the current evidence
                           (before-snapshot exclusion), the current-run
                           framework log still is, and the gap is recorded.
      S7 locked+FAIL     : same locked stale file with a NEW failing run: FAIL
                           with real counts, the current failure content is
                           collected (changed file => proven current), the stale
                           marker still never appears in the evidence.
      S8 unit            : the production collection/invocation helpers, extracted
                           from the script AST like the caller review probe,
                           keep same-named logs from BOTH source roots apart
                           (full project-relative path), keep the dotnetTest and
                           direct invocations of an auto run in separate
                           directories (no overwrite), and Invoke-Captured
                           persists partial output + the exception and returns
                           $null (never a fake 0) when the command throws.

    Gate contract under test: diagnostics collection must never change an exit
    code, a count, or a PASS/FAIL verdict - only add evidence.

    This script does not touch the PostgreSQL work, production tests, or any
    tracked file outside tools\tests\.

    Usage:
      powershell -ExecutionPolicy Bypass -File tools\tests\VerifyV5Diagnostics.Tests.ps1
                 [-DotnetPath D:\Unity\dotnet\dotnet.exe] [-KeepWorkspace]

    Exit codes: 0 = all checks passed; 1 = at least one check failed;
                2 = environment blocked (dotnet missing).
#>
[CmdletBinding()]
param(
    [string]$DotnetPath = 'D:\Unity\dotnet\dotnet.exe',

    # Keep the temporary workspace for manual inspection instead of deleting it.
    [switch]$KeepWorkspace
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$verifyScript = Join-Path $repoRoot 'tools\verify-v5.ps1'

$script:checks = 0
$script:failures = New-Object System.Collections.Generic.List[string]

function Assert-True([bool]$Condition, [string]$Label) {
    $script:checks++
    if ($Condition) { Write-Host "  PASS  $Label" }
    else {
        Write-Host "  FAIL  $Label"
        $script:failures.Add($Label)
    }
}

# --- 0. environment --------------------------------------------------------------
if (-not (Test-Path -LiteralPath $DotnetPath)) {
    Write-Host "BLOCKED: dotnet not found at $DotnetPath"
    exit 2
}
if (-not (Test-Path -LiteralPath $verifyScript)) {
    Write-Host "BLOCKED: verify script not found at $verifyScript"
    exit 2
}

# --- fixture definition -----------------------------------------------------------
$probeInstanceGuid = '{D4A0E7C1-6B3A-4A7E-9B2C-1F2E3D4C5B6A}'
$sentinel = 'DIAGNOSTIC-PROBE-SENTINEL'
$staleMarker = 'VERIFY-V5-STALE-OLD-RUN'

$failSource = @'
using Xunit;

namespace Probe.Tests
{
    public class ProbeTests
    {
        [Fact]
        public void AlwaysPasses()
        {
            Assert.True(true);
        }

        [Fact]
        public void ControlledFailure()
        {
            Assert.Fail("DIAGNOSTIC-PROBE-SENTINEL failing assertion");
        }
    }
}
'@

$passSource = @'
using Xunit;

namespace Probe.Tests
{
    public class ProbeTests
    {
        [Fact]
        public void AlwaysPasses()
        {
            Assert.True(true);
        }

        [Fact]
        public void SecondPasses()
        {
            Assert.True(1 + 1 == 2);
        }
    }
}
'@

$zeroSource = @'
using Xunit;

namespace Probe.Tests
{
    // No [Fact] members: the verification gate must fail zero-test projects.
    public class EmptyProbe
    {
    }
}
'@

$probeCsproj = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>
    <LangVersion>9.0</LangVersion>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
    <AssemblyName>Probe.Tests</AssemblyName>
    <RootNamespace>Probe.Tests</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" Version="4.0.2" />
  </ItemGroup>
</Project>
'@

$probeSln = @"
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Probe.Tests", "Probe.Tests\Probe.Tests.csproj", "$probeInstanceGuid"
EndProject
Global
	GlobalSection(SolutionConfigurationPlatforms) = preSolution
		Debug|Any CPU = Debug|Any CPU
		Release|Any CPU = Release|Any CPU
	EndGlobalSection
	GlobalSection(ProjectConfigurationPlatforms) = postSolution
		$probeInstanceGuid.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		$probeInstanceGuid.Debug|Any CPU.Build.0 = Debug|Any CPU
		$probeInstanceGuid.Release|Any CPU.ActiveCfg = Release|Any CPU
		$probeInstanceGuid.Release|Any CPU.Build.0 = Release|Any CPU
	EndGlobalSection
EndGlobal
"@

# The manifest deliberately mirrors the production manifest's testRunner section
# (same direct-runner arguments) so the fixture exercises the identical runner
# surface. Project paths are absolute because the fixture lives outside the
# repository; required=false because the in-sln membership check resolves sln
# paths relative to the repository's dotnet\ folder and cannot see a foreign
# solution - the probe is built individually, exactly like an auto-discovered
# project (e.g. Mahjong.Outbox.CrashWorker) already is in production runs.
$manifestPath = $null
$probeDir = $null
$workspace = $null

function Write-Utf8File([string]$Path, [string]$Content) {
    $dir = Split-Path $Path -Parent
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    [IO.File]::WriteAllText($Path, $Content.Replace("`n", "`r`n"), (New-Object System.Text.UTF8Encoding($false)))
}

function New-Workspace {
    $script:workspace = Join-Path ([IO.Path]::GetTempPath()) ("VerifyV5Diagnostics-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + "-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    $script:probeDir = Join-Path $workspace 'Probe.Tests'
    Write-Utf8File -Path (Join-Path $workspace 'probe.sln') -Content $probeSln
    Write-Utf8File -Path (Join-Path $probeDir 'Probe.Tests.csproj') -Content $probeCsproj

    $slnPath = (Join-Path $workspace 'probe.sln') -replace '\\', '/'
    $projPath = (Join-Path $probeDir 'Probe.Tests.csproj') -replace '\\', '/'
    $manifest = @{
        schemaVersion = 1
        solution      = $slnPath
        description   = 'Throwaway fixture for VerifyV5Diagnostics.Tests.ps1; lives entirely under the task TEMP directory.'
        testRunner    = @{
            directRunnerArgs = @('-noLogo', '-reporter', 'default')
        }
        projects      = @(
            @{
                path     = $projPath
                kind     = 'test'
                required = $false
            }
        )
    }
    $script:manifestPath = Join-Path $workspace 'manifest.json'
    $json = ConvertTo-Json -InputObject $manifest -Depth 5
    Write-Utf8File -Path $manifestPath -Content $json
}

function Set-ProbeSource([string]$Source) {
    Write-Utf8File -Path (Join-Path $probeDir 'ProbeTests.cs') -Content $Source
}

function Invoke-Verify {
    param([string]$RunnerMode, [string]$LogDir)
    $consoleLog = Join-Path $LogDir '-parent-console.log'
    $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $verifyScript `
        -Configuration Release -RunnerMode $RunnerMode -ManifestPath $manifestPath `
        -LogDir $LogDir -SkipPluginCopyCheck -NoProjectDiscovery -DotnetPath $DotnetPath *>&1
    $exit = $LASTEXITCODE
    $outDir = Split-Path $consoleLog -Parent
    if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
    ($output | Out-String) | Set-Content -Path $consoleLog -Encoding UTF8
    return $exit
}

function Get-CollectedResultsDir([string]$LogDir, [string]$Mode) {
    return Join-Path $LogDir (Join-Path 'test-results' (Join-Path 'Probe.Tests' $Mode))
}

function Seed-StaleLog {
    # Place a fake OLD failure log where the framework would write the current
    # run's log. If purge-before-run works, this file must be gone after the run
    # and must never appear in the collected evidence.
    $dir = Join-Path $probeDir 'bin\Release\net8.0\TestResults'
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    Write-Utf8File -Path (Join-Path $dir 'Stale_OldRun_net8.0_x64.log') -Content "$staleMarker ControlledFailure $sentinel (pretend log from an earlier run)"
}

function Assert-RowCounts {
    param([string]$LogDir, [string]$RunnerColumn, [int]$Total, [int]$Failed, [int]$Skipped)
    $summary = Get-Content -LiteralPath (Join-Path $LogDir 'summary.log') -Raw
    $pattern = 'Probe.Tests\s+\S+\s+\S+\s+\S+\s+' + [regex]::Escape($RunnerColumn) + '\s+' + $Total + '\s+' + $Failed + '\s+' + $Skipped + '\s+(\S+)'
    $match = [regex]::Match($summary, $pattern)
    return $match.Success
}

$expectedHead = $null
try { $expectedHead = (& git -C $repoRoot rev-parse HEAD 2>$null | Out-String).Trim() } catch { }

try {
    New-Workspace

    # --- S1: dotnetTest mode, real failing case -----------------------------------
    Write-Host ""
    Write-Host "S1: dotnetTest mode - real failing xunit.v3/MTP case"
    Set-ProbeSource $failSource
    $log1 = Join-Path $workspace 'logs-s1'
    $exit1 = Invoke-Verify -RunnerMode 'dotnetTest' -LogDir $log1
    $summary1 = Get-Content -LiteralPath (Join-Path $log1 'summary.log') -Raw
    Assert-True ($exit1 -ne 0) "S1 exit code is non-zero (got $exit1)"
    Assert-True ($summary1 -match 'OVERALL: FAIL') "S1 summary reports OVERALL: FAIL"
    Assert-True (Assert-RowCounts -LogDir $log1 -RunnerColumn 'dotnet test' -Total 2 -Failed 1 -Skipped 0) "S1 row keeps real counts 2 total / 1 failed / 0 skipped as FAIL"

    $meta1 = Join-Path $log1 'runs\Probe.Tests\dotnetTest.meta.txt'
    Assert-True (Test-Path -LiteralPath $meta1) "S1 per-invocation meta file exists (runs\Probe.Tests\dotnetTest.meta.txt)"
    if (Test-Path -LiteralPath $meta1) {
        $meta1Text = Get-Content -LiteralPath $meta1 -Raw
        Assert-True ($meta1Text -match 'exit code\s*:\s*1') "S1 meta records exit code 1"
        Assert-True ($meta1Text -match 'total\s*:\s*2') "S1 meta records total 2"
        Assert-True ($meta1Text -match 'failed\s*:\s*1') "S1 meta records failed 1"
        if ($expectedHead) { Assert-True ($meta1Text.Contains($expectedHead)) "S1 meta records the real head SHA" }
    }

    $collected1 = Get-CollectedResultsDir -LogDir $log1 -Mode 'dotnetTest'
    $frameworkLog1 = Join-Path $collected1 'bin\Release\net8.0\TestResults\Probe.Tests_net8.0_x64.log'
    Assert-True (Test-Path -LiteralPath $frameworkLog1) "S1 raw framework log collected under <log dir>\test-results\Probe.Tests\dotnetTest\ with its full project-relative path (the subtree CI uploads as artifacts/verify-v5/**)"
    if (Test-Path -LiteralPath $frameworkLog1) {
        $frameworkText1 = Get-Content -LiteralPath $frameworkLog1 -Raw
        Assert-True ($frameworkText1.Contains($sentinel)) "S1 framework log contains the failing assertion message"
        Assert-True ($frameworkText1.Contains('Probe.Tests.ProbeTests.ControlledFailure')) "S1 framework log names the failing case (Probe.Tests.ProbeTests.ControlledFailure)"
    }

    # --- S2: stale log planted, then current PASS run ------------------------------
    Write-Host ""
    Write-Host "S2: dotnetTest mode - planted OLD failure log must not leak into a PASS run"
    Set-ProbeSource $passSource
    Seed-StaleLog
    $log2 = Join-Path $workspace 'logs-s2'
    $exit2 = Invoke-Verify -RunnerMode 'dotnetTest' -LogDir $log2
    $summary2 = Get-Content -LiteralPath (Join-Path $log2 'summary.log') -Raw
    Assert-True ($exit2 -eq 0) "S2 exit code is 0 with the stale failure log planted beforehand (got $exit2)"
    Assert-True ($summary2 -match 'OVERALL: PASS') "S2 summary reports OVERALL: PASS"
    Assert-True (Assert-RowCounts -LogDir $log2 -RunnerColumn 'dotnet test' -Total 2 -Failed 0 -Skipped 0) "S2 row keeps real counts 2 total / 0 failed / 0 skipped as PASS"

    $collected2 = Join-Path $log2 'test-results'
    $staleLeaked = $false
    if (Test-Path -LiteralPath $collected2) {
        $staleLeaked = $null -ne (Get-ChildItem -LiteralPath $collected2 -Recurse -File | Where-Object { $_.Name -eq 'Stale_OldRun_net8.0_x64.log' })
        foreach ($file in (Get-ChildItem -LiteralPath $collected2 -Recurse -File)) {
            $text = Get-Content -LiteralPath $file.FullName -Raw
            if ($text -and $text.Contains($staleMarker)) { $staleLeaked = $true }
        }
    }
    Assert-True (-not $staleLeaked) "S2 collected evidence contains no stale old-run log or marker"
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $probeDir 'bin\Release\net8.0\TestResults\Stale_OldRun_net8.0_x64.log'))) "S2 planted stale log was purged from the project before the run (run-exclusive collection)"

    # --- S3: direct mode, real failing case ----------------------------------------
    Write-Host ""
    Write-Host "S3: direct mode (in-process MTP runner) - real failing case"
    Set-ProbeSource $failSource
    $log3 = Join-Path $workspace 'logs-s3'
    $exit3 = Invoke-Verify -RunnerMode 'direct' -LogDir $log3
    $summary3 = Get-Content -LiteralPath (Join-Path $log3 'summary.log') -Raw
    Assert-True ($exit3 -ne 0) "S3 exit code is non-zero (got $exit3)"
    Assert-True ($summary3 -match 'OVERALL: FAIL') "S3 summary reports OVERALL: FAIL"
    Assert-True (Assert-RowCounts -LogDir $log3 -RunnerColumn 'in-process (direct)' -Total 2 -Failed 1 -Skipped 0) "S3 row keeps real counts 2 total / 1 failed / 0 skipped as FAIL"

    $directLog3 = Join-Path $log3 'direct-Probe.Tests.log'
    Assert-True (Test-Path -LiteralPath $directLog3) "S3 direct runner log exists"
    if (Test-Path -LiteralPath $directLog3) {
        $directText3 = Get-Content -LiteralPath $directLog3 -Raw
        Assert-True ($directText3.Contains('Probe.Tests.ProbeTests.ControlledFailure')) "S3 direct log names the failing case"
        Assert-True ($directText3.Contains($sentinel)) "S3 direct log contains the failing assertion message"
        Assert-True ($directText3 -match 'Total:\s*2,\s*Errors:\s*0,\s*Failed:\s*1') "S3 direct log keeps the real runner summary counts"
    }

    $meta3 = Join-Path $log3 'runs\Probe.Tests\direct.meta.txt'
    Assert-True (Test-Path -LiteralPath $meta3) "S3 per-invocation meta file exists (runs\Probe.Tests\direct.meta.txt)"

    # The in-process MTP runner on this stack writes no TestResults file; its
    # evidence is the captured console. Collection must mirror whatever the run
    # did produce and invent nothing.
    $binResults3 = Join-Path $probeDir 'bin\Release\net8.0\TestResults'
    $mirrored3 = $true
    if (Test-Path -LiteralPath $binResults3) {
        $collected3 = Get-CollectedResultsDir -LogDir $log3 -Mode 'direct'
        foreach ($file in (Get-ChildItem -LiteralPath $binResults3 -Recurse -File)) {
            if (-not (Test-Path -LiteralPath (Join-Path $collected3 $file.Name))) { $mirrored3 = $false }
        }
    }
    Assert-True $mirrored3 "S3 collection mirrors every framework file the direct run produced (and invents none)"

    # --- S4: stale log planted, then current PASS run (direct mode) -----------------
    Write-Host ""
    Write-Host "S4: direct mode - planted OLD failure log must not leak into a PASS run"
    Set-ProbeSource $passSource
    Seed-StaleLog
    $log4 = Join-Path $workspace 'logs-s4'
    $exit4 = Invoke-Verify -RunnerMode 'direct' -LogDir $log4
    $summary4 = Get-Content -LiteralPath (Join-Path $log4 'summary.log') -Raw
    Assert-True ($exit4 -eq 0) "S4 exit code is 0 with the stale failure log planted beforehand (got $exit4)"
    Assert-True ($summary4 -match 'OVERALL: PASS') "S4 summary reports OVERALL: PASS"
    $collected4 = Join-Path $log4 'test-results'
    $staleLeaked4 = $false
    if (Test-Path -LiteralPath $collected4) {
        foreach ($file in (Get-ChildItem -LiteralPath $collected4 -Recurse -File)) {
            $text = Get-Content -LiteralPath $file.FullName -Raw
            if ($text -and ($text.Contains($staleMarker) -or $text.Contains($sentinel))) { $staleLeaked4 = $true }
        }
    }
    Assert-True (-not $staleLeaked4) "S4 collected evidence contains no stale/failure content from before the run"

    # --- S5: zero-test gate survives the diagnostics changes ------------------------
    Write-Host ""
    Write-Host "S5: dotnetTest mode - zero executed test cases still FAIL"
    Set-ProbeSource $zeroSource
    $log5 = Join-Path $workspace 'logs-s5'
    $exit5 = Invoke-Verify -RunnerMode 'dotnetTest' -LogDir $log5
    $summary5 = Get-Content -LiteralPath (Join-Path $log5 'summary.log') -Raw
    Assert-True ($exit5 -ne 0) "S5 exit code is non-zero for a zero-test project (got $exit5)"
    Assert-True ($summary5 -match 'OVERALL: FAIL') "S5 summary reports OVERALL: FAIL"
    Assert-True ($summary5 -match 'zero executed test cases') "S5 zero-test gate message present"

    # --- S6: locked OLD log -> purge fails -> current PASS run ----------------------
    Write-Host ""
    Write-Host "S6: locked OLD failure log - purge fails, stale content must not enter a PASS run"
    Set-ProbeSource $passSource
    Seed-StaleLog
    $lockedStalePath = Join-Path $probeDir 'bin\Release\net8.0\TestResults\Stale_OldRun_net8.0_x64.log'
    $lockedHandle6 = [IO.File]::Open($lockedStalePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $log6 = Join-Path $workspace 'logs-s6'
        $exit6 = Invoke-Verify -RunnerMode 'dotnetTest' -LogDir $log6
        $summary6 = Get-Content -LiteralPath (Join-Path $log6 'summary.log') -Raw
        Assert-True ($exit6 -eq 0) "S6 exit code is 0 even though the purge failed (got $exit6)"
        Assert-True ($summary6 -match 'OVERALL: PASS') "S6 summary reports OVERALL: PASS"
        Assert-True (Test-Path -LiteralPath (Join-Path $log6 'test-results\Probe.Tests\dotnetTest\bin\Release\net8.0\TestResults\Probe.Tests_net8.0_x64.log')) "S6 current-run framework log still collected despite the purge failure"
        $staleLeaked6 = $false
        $evidence6 = Join-Path $log6 'test-results'
        if (Test-Path -LiteralPath $evidence6) {
            foreach ($file in (Get-ChildItem -LiteralPath $evidence6 -Recurse -File)) {
                $text = Get-Content -LiteralPath $file.FullName -Raw
                if ($text -and $text.Contains($staleMarker)) { $staleLeaked6 = $true }
            }
        }
        Assert-True (-not $staleLeaked6) "S6 locked stale content appears nowhere in the collected evidence"
        $meta6Text = Get-Content -LiteralPath (Join-Path $log6 'runs\Probe.Tests\dotnetTest.meta.txt') -Raw
        Assert-True ($meta6Text -match 'NOT CLEARED') "S6 meta records the purge failure"
        Assert-True ($meta6Text -match 'excluded stale files') "S6 meta records the excluded stale file(s)"
    }
    finally { $lockedHandle6.Dispose() }

    # --- S7: locked OLD log + NEW failing run ----------------------------------------
    Write-Host ""
    Write-Host "S7: locked OLD failure log + current failing run - real failure detected, stale still excluded"
    Set-ProbeSource $failSource
    $lockedHandle7 = [IO.File]::Open($lockedStalePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $log7 = Join-Path $workspace 'logs-s7'
        $exit7 = Invoke-Verify -RunnerMode 'dotnetTest' -LogDir $log7
        $summary7 = Get-Content -LiteralPath (Join-Path $log7 'summary.log') -Raw
        Assert-True ($exit7 -ne 0) "S7 exit code is non-zero (got $exit7)"
        Assert-True ($summary7 -match 'OVERALL: FAIL') "S7 summary reports OVERALL: FAIL"
        Assert-True (Assert-RowCounts -LogDir $log7 -RunnerColumn 'dotnet test' -Total 2 -Failed 1 -Skipped 0) "S7 row keeps real counts 2 total / 1 failed / 0 skipped as FAIL"
        $currentLog7 = Join-Path $log7 'test-results\Probe.Tests\dotnetTest\bin\Release\net8.0\TestResults\Probe.Tests_net8.0_x64.log'
        Assert-True (Test-Path -LiteralPath $currentLog7) "S7 current failing run's framework log collected"
        if (Test-Path -LiteralPath $currentLog7) {
            $currentText7 = Get-Content -LiteralPath $currentLog7 -Raw
            Assert-True ($currentText7.Contains($sentinel)) "S7 collected log carries the CURRENT failure content"
            Assert-True (-not $currentText7.Contains($staleMarker)) "S7 collected log carries no stale marker"
        }
        $staleLeaked7 = $false
        $evidence7 = Join-Path $log7 'test-results'
        foreach ($file in (Get-ChildItem -LiteralPath $evidence7 -Recurse -File)) {
            $text = Get-Content -LiteralPath $file.FullName -Raw
            if ($text -and $text.Contains($staleMarker)) { $staleLeaked7 = $true }
        }
        Assert-True (-not $staleLeaked7) "S7 stale marker appears nowhere in the collected evidence"
    }
    finally { $lockedHandle7.Dispose() }

    # --- S8: unit-level regression on the production helpers (caller-review technique)
    Write-Host ""
    Write-Host "S8: unit - source-root collision / dual invocation / runner exception"
    $parseErrors8 = $null
    $tokens8 = $null
    $ast8 = [System.Management.Automation.Language.Parser]::ParseFile($verifyScript, [ref]$tokens8, [ref]$parseErrors8)
    Assert-True (-not ($parseErrors8 -and $parseErrors8.Count)) "S8 production script parses cleanly"
    foreach ($fnName8 in @('Get-TestResultsSnapshot', 'Clear-TestResultsDirs', 'Save-TestResults', 'Invoke-Captured')) {
        $fnAst8 = $ast8.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $fnName8 }.GetNewClosure(), $true)
        if ($null -ne $fnAst8) { Invoke-Expression $fnAst8.Extent.Text }
        Assert-True ($null -ne $fnAst8) "S8 extracted $fnName8 from the production script AST"
    }

    $unitLog = Join-Path $workspace 'unit-s8'
    $unitFixture = Join-Path $unitLog 'fixture'
    $unitBinResults = Join-Path $unitFixture 'bin\Release\net8.0\TestResults'
    $unitRootResults = Join-Path $unitFixture 'TestResults'
    New-Item -ItemType Directory -Path $unitBinResults, $unitRootResults -Force | Out-Null

    $LogDir = $unitLog  # the extracted helpers resolve $LogDir dynamically
    # Production ordering: purge first, then the runner invocation "writes" its
    # files, then Save-TestResults collects what the invocation produced.
    $purgeStatuses8 = @(Clear-TestResultsDirs -Dirs @($unitBinResults, $unitRootResults))
    Write-Utf8File -Path (Join-Path $unitBinResults 'same.log') -Content 'BIN-CURRENT-FAILURE'
    Write-Utf8File -Path (Join-Path $unitRootResults 'same.log') -Content 'ROOT-CURRENT-FAILURE'
    $saved8 = Save-TestResults -ProjectName 'Collide.Tests' -Mode 'dotnetTest' -ProjectDir $unitFixture -Dirs @($unitBinResults, $unitRootResults) -PurgeStatuses $purgeStatuses8
    Assert-True (@($saved8.Collected).Count -eq 2) "S8 collision: 2 source files produce 2 collected records (was the round-1 defect)"
    $binCopy8 = Join-Path $unitLog 'test-results\Collide.Tests\dotnetTest\bin\Release\net8.0\TestResults\same.log'
    $rootCopy8 = Join-Path $unitLog 'test-results\Collide.Tests\dotnetTest\TestResults\same.log'
    Assert-True ((Test-Path -LiteralPath $binCopy8) -and (Test-Path -LiteralPath $rootCopy8)) "S8 collision: both same-named files exist at distinct project-relative paths"
    if ((Test-Path -LiteralPath $binCopy8) -and (Test-Path -LiteralPath $rootCopy8)) {
        Assert-True (((Get-Content -LiteralPath $binCopy8 -Raw).Trim()) -eq 'BIN-CURRENT-FAILURE') "S8 collision: bin copy keeps its own content"
        Assert-True (((Get-Content -LiteralPath $rootCopy8 -Raw).Trim()) -eq 'ROOT-CURRENT-FAILURE') "S8 collision: project-root copy keeps its own content"
    }
    $hashMatches8 = $true
    foreach ($entry8 in $saved8.Collected) {
        $rel8 = ($entry8 -replace '\s+sha256=.*$', '')
        $hash8 = ($entry8 -replace '^.*sha256=', '').Trim()
        $abs8 = Join-Path $unitLog $rel8
        if (-not (Test-Path -LiteralPath $abs8)) { $hashMatches8 = $false; continue }
        if ((Get-FileHash -LiteralPath $abs8 -Algorithm SHA256).Hash -ne $hash8) { $hashMatches8 = $false }
    }
    Assert-True $hashMatches8 "S8 every collected record's sha256 matches the file actually on disk"

    # Dual invocation (an auto run executes dotnetTest, then possibly direct):
    # each invocation lands in its own mode directory; neither overwrites the other.
    $purgeStatuses8b = @(Clear-TestResultsDirs -Dirs @($unitBinResults, $unitRootResults))
    Write-Utf8File -Path (Join-Path $unitBinResults 'same.log') -Content 'DIRECT-CURRENT'
    $null = Save-TestResults -ProjectName 'Collide.Tests' -Mode 'direct' -ProjectDir $unitFixture -Dirs @($unitBinResults, $unitRootResults) -PurgeStatuses $purgeStatuses8b
    $directCopy8 = Join-Path $unitLog 'test-results\Collide.Tests\direct\bin\Release\net8.0\TestResults\same.log'
    Assert-True ((Test-Path -LiteralPath $directCopy8) -and (((Get-Content -LiteralPath $directCopy8 -Raw).Trim()) -eq 'DIRECT-CURRENT')) "S8 dual invocation: direct invocation lands in its own mode directory"
    Assert-True (((Get-Content -LiteralPath $binCopy8 -Raw).Trim()) -eq 'BIN-CURRENT-FAILURE') "S8 dual invocation: earlier dotnetTest copy is not overwritten"

    # Invoke-Captured: a throwing invocation keeps partial output + the exception
    # and returns $null - never a fabricated 0.
    $exceptionLog8 = Join-Path $unitLog 'exception-console.log'
    $exceptionExit8 = Invoke-Captured -LogPath $exceptionLog8 -Command { Write-Output 'OUTPUT-BEFORE-EXCEPTION'; throw 'SIMULATED-RUNNER-EXCEPTION' }
    Assert-True ($null -eq $exceptionExit8) "S8 exception: exit code is $null (never a fake 0)"
    Assert-True (Test-Path -LiteralPath $exceptionLog8) "S8 exception: captured output file exists"
    if (Test-Path -LiteralPath $exceptionLog8) {
        $exceptionText8 = Get-Content -LiteralPath $exceptionLog8 -Raw
        Assert-True ($exceptionText8.Contains('OUTPUT-BEFORE-EXCEPTION')) "S8 exception: output produced before the throw is preserved"
        Assert-True ($exceptionText8.Contains('SIMULATED-RUNNER-EXCEPTION')) "S8 exception: the exception itself is recorded in the log"
    }
    $exit3Log8 = Join-Path $unitLog 'exit3-console.log'
    $exit3Code8 = Invoke-Captured -LogPath $exit3Log8 -Command { cmd /c exit 3 }
    Assert-True ($exit3Code8 -eq 3) "S8 success path still returns the real exit code (got $exit3Code8)"
    Remove-Variable LogDir -ErrorAction SilentlyContinue

    # --- run identity ---------------------------------------------------------------
    Write-Host ""
    Write-Host "Run identity evidence"
    $runInfo1 = Join-Path $log1 'run-info.txt'
    Assert-True (Test-Path -LiteralPath $runInfo1) "run-info.txt exists in the log directory"
    if ((Test-Path -LiteralPath $runInfo1) -and $expectedHead) {
        $runInfoText = Get-Content -LiteralPath $runInfo1 -Raw
        Assert-True ($runInfoText.Contains($expectedHead)) "run-info.txt records the real head SHA"
    }
}
finally {
    if (-not $KeepWorkspace -and $workspace -and (Test-Path -LiteralPath $workspace)) {
        Remove-Item -LiteralPath $workspace -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ""
if ($script:failures.Count -gt 0) {
    Write-Host ("VerifyV5Diagnostics: FAIL ({0}/{1} checks failed):" -f $script:failures.Count, $script:checks)
    foreach ($failure in $script:failures) { Write-Host "  - $failure" }
    exit 1
}
Write-Host ("VerifyV5Diagnostics: PASS ({0}/{1} checks)" -f $script:checks, $script:checks)
exit 0
