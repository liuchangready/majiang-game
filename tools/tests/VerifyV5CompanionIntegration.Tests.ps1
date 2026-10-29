#requires -Version 5.1
<#
.SYNOPSIS
    Isolated self-test for the companion golden-fixture gate in tools\verify-v5.ps1.

.DESCRIPTION
    Runs the REAL unified entry (tools\verify-v5.ps1) in child PowerShell
    processes against isolated throwaway manifests/solutions and the REAL
    accepted standalone verifier (tools\verify-v5-companion-fixtures.ps1).
    Nothing outside this task's own ignored build\companion-integration\<guid>
    scratch root is written; tracked fixtures are only read/copied.

    Proven scenarios:
      S0 positive          : default unified entry over the real four fixtures
                             exits 0, reports "companion gate : PASS", writes
                             the raw command/native exit/raw log/meta, and
                             records all four fixture identities, case counts and
                             exact accepted SHA-256 values.
      S1 missing verifier  : a non-existent verifier path makes the whole entry
                             fail (non-zero), never a fake PASS.
      S2 missing fixtures  : an empty fixture directory (0 samples) is rejected
                             by the accepted verifier with FIXTURE_MISSING and
                             the unified entry exits non-zero.
      S3 byte tamper       : one changed byte is rejected by the hash gate
                             (HASH_MISMATCH) and fails the whole entry.
      S4 semantic tamper   : a same-length schema change is also rejected at the
                             hash gate first (the accepted verifier's deeper
                             structure branches remain covered by its own
                             negative suite); the entry still exits non-zero.
      S5 blocked           : a verifier reporting BLOCKED (native exit 2) makes
                             the entry report BLOCKED and exit 2, never PASS.
      S6 timeout           : a hanging verifier is killed by the bounded timeout
                             and the entry fails; the meta records timed out.
      S7 no masking        : a REAL passing .NET probe test row plus a tampered
                             companion fixture yields OVERALL: FAIL. A prior
                             project PASS cannot mask the companion gate.

    The original production-ish .NET projects, PostgreSQL, Unity, the sln,
    workflow and wrappers are not run or modified. The diagnostics self-test
    (VerifyV5Diagnostics.Tests.ps1) is a separate regression and is not replaced.

    Usage:
      powershell -ExecutionPolicy Bypass -File tools\tests\VerifyV5CompanionIntegration.Tests.ps1
                 [-DotnetPath D:\Unity\dotnet\dotnet.exe] [-KeepWorkspace]

    Exit codes: 0 = all checks passed; 1 = at least one check failed;
                2 = environment blocked (dotnet or required script missing).
#>
[CmdletBinding()]
param(
    [string]$DotnetPath = 'D:\Unity\dotnet\dotnet.exe',
    [switch]$KeepWorkspace
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$verifyScript = Join-Path $repoRoot 'tools\verify-v5.ps1'
$companionScript = Join-Path $repoRoot 'tools\verify-v5-companion-fixtures.ps1'
$fixtureRoot = Join-Path $repoRoot 'tests\v5-golden'
$buildRoot = Join-Path $repoRoot 'build\companion-integration'

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

function ConvertTo-NativeArgument([string]$Value) {
    if ($null -eq $Value) { return '""' }
    if ($Value -notmatch '[\s"]') { return $Value }
    return '"' + ($Value -replace '"', '\"') + '"'
}

# --- 0. environment --------------------------------------------------------------
if (-not (Test-Path -LiteralPath $DotnetPath)) {
    Write-Host "BLOCKED: dotnet not found at $DotnetPath"
    exit 2
}
foreach ($required in @($verifyScript, $companionScript)) {
    if (-not (Test-Path -LiteralPath $required)) {
        Write-Host "BLOCKED: required script not found: $required"
        exit 2
    }
}
foreach ($name in @('movement-v1.json', 'shuffle-reversed-v1.json', 'visibility-v1.json', 'ai-discard-flags-v1.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $fixtureRoot $name))) {
        Write-Host "BLOCKED: accepted companion fixture not found: $(Join-Path $fixtureRoot $name)"
        exit 2
    }
}

# --- 1. isolated workspace -------------------------------------------------------
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$scratchRoot = Join-Path $buildRoot ("$stamp-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$allowedRoot = [IO.Path]::GetFullPath($buildRoot)
$scratchFull = [IO.Path]::GetFullPath($scratchRoot)
if (-not $scratchFull.StartsWith($allowedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    Write-Host "BLOCKED: scratch root escapes the owned build\companion-integration root: $scratchFull"
    exit 2
}
$previousTemp = $env:TEMP
$previousTmp = $env:TMP
$previousSharedCompilation = $env:UseSharedCompilation
$previousMsBuildServer = $env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER
$env:TEMP = Join-Path $scratchFull 'temp'
$env:TMP = $env:TEMP
# Keep compiler/build servers out of this isolated test run so their temporary
# analyzer files do not stay locked after the owned scratch root is cleaned.
$env:UseSharedCompilation = 'false'
$env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER = '1'
New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null
New-Item -ItemType Directory -Force -Path $scratchFull | Out-Null

function Write-Utf8File([string]$Path, [string]$Content) {
    $dir = Split-Path $Path -Parent
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    [IO.File]::WriteAllText($Path, $Content, (New-Object System.Text.UTF8Encoding($false)))
}

$emptySolutionText = @'
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
Global
	GlobalSection(SolutionConfigurationPlatforms) = preSolution
		Debug|Any CPU = Debug|Any CPU
		Release|Any CPU = Release|Any CPU
	EndGlobalSection
EndGlobal
'@

function New-EmptyFixture([string]$Name) {
    $dir = Join-Path $scratchFull $Name
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $sln = Join-Path $dir 'empty.sln'
    Write-Utf8File -Path $sln -Content $emptySolutionText
    $manifest = @{
        schemaVersion = 1
        solution      = $sln
        description   = 'Isolated companion-integration fixture (no .NET projects).'
        testRunner    = @{ directRunnerArgs = @('-noLogo', '-reporter', 'default') }
        projects      = @()
    }
    $manifestPath = Join-Path $dir 'manifest.json'
    Write-Utf8File -Path $manifestPath -Content (ConvertTo-Json -InputObject $manifest -Depth 6)
    return [pscustomobject]@{ Dir = $dir; Manifest = $manifestPath; Solution = $sln }
}

function Invoke-Verify {
    param(
        [string]$ManifestPath,
        [string]$LogDir,
        [string[]]$ExtraArgs = @(),
        [int]$TimeoutSeconds = 300,
        [int]$CompanionTimeoutSeconds = 120
    )
    $hostExe = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    $arguments = @(
        '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
        '-File', $verifyScript,
        '-Configuration', 'Release',
        '-ManifestPath', $ManifestPath,
        '-LogDir', $LogDir,
        '-SkipPluginCopyCheck',
        '-NoProjectDiscovery',
        '-DotnetPath', $DotnetPath,
        '-CompanionTimeoutSeconds', "$CompanionTimeoutSeconds"
    ) + $ExtraArgs
    $commandText = (@($hostExe) + $arguments | ForEach-Object { ConvertTo-NativeArgument $_ }) -join ' '
    $argumentText = ($arguments | ForEach-Object { ConvertTo-NativeArgument $_ }) -join ' '

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $hostExe
    $startInfo.Arguments = $argumentText
    $startInfo.WorkingDirectory = $repoRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    $timedOut = $false
    if (-not $process.Start()) { throw 'could not start the unified verifier child process' }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        try { $process.Kill() } catch { }
        $timedOut = $true
    }
    $stdout = ''
    $stderr = ''
    try { if ($stdoutTask.Wait(10000)) { $stdout = $stdoutTask.Result } } catch { }
    try { if ($stderrTask.Wait(10000)) { $stderr = $stderrTask.Result } } catch { }
    $null = $process.WaitForExit(10000)
    $exit = $(if ($timedOut) { 124 } else { $process.ExitCode })
    $process.Dispose()

    if (-not (Test-Path -LiteralPath $LogDir)) { New-Item -ItemType Directory -Force -Path $LogDir | Out-Null }
    $consoleLog = Join-Path $LogDir '-parent-console.log'
    $text = @(
        "# command: $commandText"
        "# exit   : $exit"
        ""
        "===== stdout ====="
        $stdout
        "===== stderr ====="
        $stderr
    ) -join [Environment]::NewLine
    Set-Content -Path $consoleLog -Value $text -Encoding UTF8
    return [pscustomobject]@{ ExitCode = $exit; Stdout = $stdout; Stderr = $stderr; ConsoleLog = $consoleLog }
}

function Get-Summary([string]$LogDir) {
    return (Get-Content -LiteralPath (Join-Path $LogDir 'summary.log') -Raw -Encoding UTF8)
}
function Get-Meta([string]$LogDir) {
    return (Get-Content -LiteralPath (Join-Path $LogDir 'companion-fixtures.meta.txt') -Raw -Encoding UTF8)
}
function Get-Raw([string]$LogDir) {
    return (Get-Content -LiteralPath (Join-Path $LogDir 'companion-fixtures.log') -Raw -Encoding UTF8)
}
function Test-MetaRawLogHash([string]$LogDir) {
    $meta = Get-Meta $LogDir
    $logMatch = [regex]::Match($meta, '(?m)^raw log\s*:\s*(.+)$')
    $hashMatch = [regex]::Match($meta, '(?m)^raw log sha256\s*:\s*([A-Fa-f0-9]{64})\s*$')
    if (-not $logMatch.Success -or -not $hashMatch.Success) { return $false }
    $path = $logMatch.Groups[1].Value.Trim()
    if (-not (Test-Path -LiteralPath $path)) { return $false }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    return ($actual -eq $hashMatch.Groups[1].Value)
}
function Test-MetaNativeExitIsNonZero([string]$LogDir) {
    $meta = Get-Meta $LogDir
    $m = [regex]::Match($meta, '(?m)^raw native exit\s*:\s*(-?\d+)\s*$')
    if (-not $m.Success) { return $false }
    return ([int]$m.Groups[1].Value -ne 0)
}
function Assert-CompanionGate([string]$LogDir, [string]$Expected) {
    $summary = Get-Summary $LogDir
    Assert-True ($summary -match ('(?m)^companion gate\s*:\s*' + [regex]::Escape($Expected) + '\b')) "summary companion gate is $Expected"
}
function Assert-Overall([string]$LogDir, [string]$Expected) {
    $summary = Get-Summary $LogDir
    Assert-True ($summary -match ('(?m)^OVERALL:\s*' + [regex]::Escape($Expected) + '\b')) "summary OVERALL is $Expected"
}

function Copy-Fixtures([string]$Destination) {
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    foreach ($name in @('movement-v1.json', 'shuffle-reversed-v1.json', 'visibility-v1.json', 'ai-discard-flags-v1.json')) {
        Copy-Item -LiteralPath (Join-Path $fixtureRoot $name) -Destination (Join-Path $Destination $name) -Force
    }
    return $Destination
}

# Passing probe used only by S7 (same pattern as the accepted diagnostics self-test).
$probeInstanceGuid = '{D4A0E7C1-6B3A-4A7E-9B2C-1F2E3D4C5B6A}'
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
$probeSource = @'
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
    }
}
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

try {
    # --- S0: positive default path ------------------------------------------------
    Write-Host ""
    Write-Host "S0: positive - real verifier over the four accepted fixtures"
    $s0 = New-EmptyFixture 's0-positive'
    $s0log = Join-Path $s0.Dir 'logs'
    $r0 = Invoke-Verify -ManifestPath $s0.Manifest -LogDir $s0log
    Assert-True ($r0.ExitCode -eq 0) "S0 unified entry exits 0 (got $($r0.ExitCode))"
    Assert-CompanionGate $s0log 'PASS'
    Assert-Overall $s0log 'PASS'
    Assert-True (Test-Path -LiteralPath (Join-Path $s0log 'companion-fixtures.log')) "S0 raw companion log exists"
    Assert-True (Test-Path -LiteralPath (Join-Path $s0log 'companion-fixtures.meta.txt')) "S0 companion meta exists"
    $raw0 = Get-Raw $s0log
    $meta0 = Get-Meta $s0log
    Assert-True ($raw0 -match 'PASS: 4/4 companion golden fixtures verified successfully:') "S0 raw log contains the accepted 4/4 certification"
    Assert-True ($meta0 -match '(?m)^raw native exit\s*:\s*0\s*$') "S0 meta records raw native exit 0"
    Assert-True ($meta0 -match 'verify-v5-companion-fixtures\.ps1') "S0 meta records the real verifier path/command"
    Assert-True (Test-MetaRawLogHash $s0log) "S0 meta raw-log SHA-256 matches the actual log bytes"
    $expectedFixtures = @{
        'movement-v1.json'      = @('schema', 'v5-movement-golden-v1', '8', 'C2D5469E6C532476E5DA48C4478E7C080A401B7F2F41F095B7A8B880AB6C7C13')
        'shuffle-reversed-v1.json' = @('schema', 'v5-shuffle-reversed-golden-v1', '32', '5A5EA43F774E8341D740E6B5840AAFD4B671AEDFF9830D59DA161D758C14B619')
        'visibility-v1.json'    = @('schema', 'v5-visibility-golden-v1', '4', '30291EC270EFB6F2A7D8C6984D53E7AE2DBD5AE45A41A7114EE69288749F346E')
        'ai-discard-flags-v1.json' = @('fixture', 'ai-discard-flags-v1', '3', '448BAB14DEF9809DBDCC4BE1148CD53370FEE1A44541535794AC7DFBE5B5CBE8')
    }
    foreach ($name in $expectedFixtures.Keys) {
        $e = $expectedFixtures[$name]
        $pattern = '(?m)^\s+' + [regex]::Escape($name) + '\s+' + $e[0] + '=' + [regex]::Escape($e[1]) + '\s+cases=' + $e[2] + '\s+sha256=' + $e[3] + '\s*$'
        Assert-True ($meta0 -match $pattern) "S0 meta records identity/cases/SHA for $name ($($e[0])=$($e[1]), $($e[2]) cases)"
        Assert-True ($raw0 -match [regex]::Escape($name) -and $raw0 -match [regex]::Escape($e[3])) "S0 raw log names $name with its accepted SHA-256"
    }

    # --- S1: missing verifier script ---------------------------------------------
    Write-Host ""
    Write-Host "S1: missing verifier script must fail the entry"
    $s1 = New-EmptyFixture 's1-missing-script'
    $s1log = Join-Path $s1.Dir 'logs'
    $missingScript = Join-Path $scratchFull 'does-not-exist\verify-v5-companion-fixtures.ps1'
    $r1 = Invoke-Verify -ManifestPath $s1.Manifest -LogDir $s1log -ExtraArgs @('-CompanionVerifierPath', $missingScript)
    Assert-True ($r1.ExitCode -ne 0) "S1 unified entry exits non-zero (got $($r1.ExitCode))"
    Assert-CompanionGate $s1log 'FAIL'
    Assert-Overall $s1log 'FAIL'
    Assert-True (Test-MetaNativeExitIsNonZero $s1log) "S1 meta records a non-zero raw native exit"
    Assert-True (Test-MetaRawLogHash $s1log) "S1 meta raw-log SHA-256 matches the actual log bytes"
    Assert-True ((Get-Raw $s1log) -notmatch 'PASS: 4/4') "S1 missing script never emits a 4/4 PASS"

    # --- S2: missing/zero fixtures ------------------------------------------------
    Write-Host ""
    Write-Host "S2: empty fixture directory (0 samples) must fail the entry"
    $s2 = New-EmptyFixture 's2-missing-fixtures'
    $s2log = Join-Path $s2.Dir 'logs'
    $emptyFixtures = Join-Path $scratchFull 'fixtures-empty'
    New-Item -ItemType Directory -Force -Path $emptyFixtures | Out-Null
    $r2 = Invoke-Verify -ManifestPath $s2.Manifest -LogDir $s2log -ExtraArgs @('-CompanionFixtureDir', $emptyFixtures)
    Assert-True ($r2.ExitCode -ne 0) "S2 unified entry exits non-zero (got $($r2.ExitCode))"
    Assert-CompanionGate $s2log 'FAIL'
    Assert-Overall $s2log 'FAIL'
    $raw2 = Get-Raw $s2log
    Assert-True ($raw2 -match 'FIXTURE_MISSING') "S2 raw log records FIXTURE_MISSING for the absent samples"
    Assert-True ($raw2 -notmatch 'PASS: 4/4') "S2 zero samples never emit a 4/4 PASS"
    Assert-True (Test-MetaNativeExitIsNonZero $s2log) "S2 meta records a non-zero raw native exit"

    # --- S3: byte tamper ----------------------------------------------------------
    Write-Host ""
    Write-Host "S3: one-byte tamper must be rejected by the raw hash gate"
    $s3 = New-EmptyFixture 's3-byte-tamper'
    $s3log = Join-Path $s3.Dir 'logs'
    $byteFixtures = Copy-Fixtures (Join-Path $scratchFull 'fixtures-byte')
    $tampered = Join-Path $byteFixtures 'movement-v1.json'
    $bytes = [IO.File]::ReadAllBytes($tampered)
    $originalLength = $bytes.Length
    $bytes[100] = $bytes[100] -bxor 0xFF
    [IO.File]::WriteAllBytes($tampered, $bytes)
    Assert-True (([IO.File]::ReadAllBytes($tampered).Length) -eq $originalLength) "S3 tamper preserves the byte count (hash, not length, is the gate)"
    $r3 = Invoke-Verify -ManifestPath $s3.Manifest -LogDir $s3log -ExtraArgs @('-CompanionFixtureDir', $byteFixtures)
    Assert-True ($r3.ExitCode -ne 0) "S3 unified entry exits non-zero (got $($r3.ExitCode))"
    Assert-CompanionGate $s3log 'FAIL'
    Assert-Overall $s3log 'FAIL'
    $raw3 = Get-Raw $s3log
    Assert-True ($raw3 -match 'HASH_MISMATCH') "S3 raw log records HASH_MISMATCH"
    Assert-True ($raw3 -notmatch 'PASS: 4/4') "S3 byte tamper never emits a 4/4 PASS"
    Assert-True (Test-MetaNativeExitIsNonZero $s3log) "S3 meta records a non-zero raw native exit"

    # --- S4: semantic tamper ------------------------------------------------------
    Write-Host ""
    Write-Host "S4: semantic/schema tamper must also fail the entry (hash gate first)"
    $s4 = New-EmptyFixture 's4-semantic-tamper'
    $s4log = Join-Path $s4.Dir 'logs'
    $semanticFixtures = Copy-Fixtures (Join-Path $scratchFull 'fixtures-semantic')
    $semanticFile = Join-Path $semanticFixtures 'movement-v1.json'
    $text = [IO.File]::ReadAllText($semanticFile, (New-Object System.Text.UTF8Encoding($false)))
    $text = $text.Replace('v5-movement-golden-v1', 'v5-movement-golden-v9')
    [IO.File]::WriteAllText($semanticFile, $text, (New-Object System.Text.UTF8Encoding($false)))
    Assert-True ([IO.File]::ReadAllText($semanticFile).Contains('v5-movement-golden-v9')) "S4 semantic tamper actually changed the schema value"
    $r4 = Invoke-Verify -ManifestPath $s4.Manifest -LogDir $s4log -ExtraArgs @('-CompanionFixtureDir', $semanticFixtures)
    Assert-True ($r4.ExitCode -ne 0) "S4 unified entry exits non-zero (got $($r4.ExitCode))"
    Assert-CompanionGate $s4log 'FAIL'
    Assert-Overall $s4log 'FAIL'
    $raw4 = Get-Raw $s4log
    Assert-True ($raw4 -match 'HASH_MISMATCH') "S4 hash gate rejects the semantic tamper before deeper structure checks (as accepted)"
    Assert-True ($raw4 -notmatch 'PASS: 4/4') "S4 semantic tamper never emits a 4/4 PASS"

    # --- S5: BLOCKED native exit 2 ------------------------------------------------
    Write-Host ""
    Write-Host "S5: verifier BLOCKED (native exit 2) must propagate as BLOCKED/exit 2"
    $s5 = New-EmptyFixture 's5-blocked'
    $s5log = Join-Path $s5.Dir 'logs'
    $blockedVerifier = Join-Path $scratchFull 'blocked-verifier.ps1'
    Write-Utf8File -Path $blockedVerifier -Content @'
Write-Output "BLOCKED: missing required tool 'Get-FileHash'"
exit 2
'@
    $r5 = Invoke-Verify -ManifestPath $s5.Manifest -LogDir $s5log -ExtraArgs @('-CompanionVerifierPath', $blockedVerifier)
    Assert-True ($r5.ExitCode -eq 2) "S5 unified entry exits 2 for BLOCKED (got $($r5.ExitCode))"
    Assert-CompanionGate $s5log 'BLOCKED'
    Assert-Overall $s5log 'BLOCKED'
    Assert-True ((Get-Meta $s5log) -match '(?m)^raw native exit\s*:\s*2\s*$') "S5 meta records raw native exit 2"

    # --- S6: bounded timeout ------------------------------------------------------
    Write-Host ""
    Write-Host "S6: a hanging verifier must hit the bounded timeout and fail"
    $s6 = New-EmptyFixture 's6-timeout'
    $s6log = Join-Path $s6.Dir 'logs'
    $hangVerifier = Join-Path $scratchFull 'hang-verifier.ps1'
    Write-Utf8File -Path $hangVerifier -Content @'
Start-Sleep -Seconds 30
Write-Output "PASS: 4/4 companion golden fixtures verified successfully:"
exit 0
'@
    $r6 = Invoke-Verify -ManifestPath $s6.Manifest -LogDir $s6log -ExtraArgs @('-CompanionVerifierPath', $hangVerifier) -CompanionTimeoutSeconds 1
    Assert-True ($r6.ExitCode -ne 0) "S6 unified entry exits non-zero after timeout (got $($r6.ExitCode))"
    Assert-CompanionGate $s6log 'FAIL'
    Assert-Overall $s6log 'FAIL'
    $meta6 = Get-Meta $s6log
    Assert-True ($meta6 -match '(?m)^timed out\s*:\s*True\s*$') "S6 meta records the bounded timeout"
    Assert-True ((Get-Raw $s6log) -notmatch 'PASS: 4/4') "S6 timed-out script cannot fake a 4/4 PASS"

    # --- S7: prior .NET PASS must not mask a companion FAIL ----------------------
    Write-Host ""
    Write-Host "S7: a passing .NET probe row cannot mask a failing companion gate"
    $s7 = Join-Path $scratchFull 's7-no-masking'
    $probeDir = Join-Path $s7 'Probe.Tests'
    Write-Utf8File -Path (Join-Path $probeDir 'Probe.Tests.csproj') -Content $probeCsproj
    Write-Utf8File -Path (Join-Path $probeDir 'ProbeTests.cs') -Content $probeSource
    Write-Utf8File -Path (Join-Path $s7 'probe.sln') -Content $probeSln
    $sln7 = Join-Path $s7 'probe.sln'
    $manifest7 = @{
        schemaVersion = 1
        solution      = $sln7
        description   = 'Isolated passing .NET probe for no-masking integration proof.'
        testRunner    = @{ directRunnerArgs = @('-noLogo', '-reporter', 'default') }
        projects      = @(@{ path = (Join-Path $probeDir 'Probe.Tests.csproj'); kind = 'test'; required = $false })
    }
    $manifest7Path = Join-Path $s7 'manifest.json'
    Write-Utf8File -Path $manifest7Path -Content (ConvertTo-Json -InputObject $manifest7 -Depth 6)
    $s7log = Join-Path $s7 'logs'
    $r7 = Invoke-Verify -ManifestPath $manifest7Path -LogDir $s7log -ExtraArgs @('-RunnerMode', 'direct', '-CompanionFixtureDir', $byteFixtures)
    Assert-True ($r7.ExitCode -ne 0) "S7 unified entry exits non-zero despite the passing probe (got $($r7.ExitCode))"
    $summary7 = Get-Summary $s7log
    Assert-True ($summary7 -match 'Probe\.Tests\s+\S+\s+\S+\s+\S+\s+in-process \(direct\)\s+1\s+0\s+0\s+PASS') "S7 summary has a real Probe.Tests PASS row (1 total / 0 failed)"
    Assert-CompanionGate $s7log 'FAIL'
    Assert-Overall $s7log 'FAIL'
    Assert-True ($summary7 -match 'companion gate:\s*FAIL') "S7 OVERALL line explicitly names the failing companion gate"

    # --- S8: diagnostics self-test remains compatible (not replaced) -------------
    # The accepted diagnostics self-test invokes this same entry with a throwaway
    # manifest and no companion overrides. Its companion stage therefore runs the
    # real four fixtures and must PASS; it is executed separately by the delivery
    # command for the full diagnostics regression. No switch disables it.
    Assert-True (Test-Path -LiteralPath (Join-Path $repoRoot 'tools\tests\VerifyV5Diagnostics.Tests.ps1')) "S8 the original diagnostics self-test is still present (run separately, not replaced)"
}
finally {
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
    $env:UseSharedCompilation = $previousSharedCompilation
    $env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER = $previousMsBuildServer
    if ($KeepWorkspace) {
        Write-Host ""
        Write-Host "Kept workspace: $scratchFull"
    }
    else {
        $cleanupFull = [IO.Path]::GetFullPath($scratchFull)
        if ($cleanupFull.StartsWith($allowedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $cleanupFull)) {
            $cleanupError = $null
            for ($attempt = 0; $attempt -lt 20; $attempt++) {
                try {
                    Remove-Item -LiteralPath $cleanupFull -Recurse -Force -ErrorAction Stop
                    $cleanupError = $null
                    break
                }
                catch {
                    $cleanupError = $_
                    Start-Sleep -Milliseconds 500
                }
            }
            if ($null -ne $cleanupError) {
                Write-Warning "could not fully remove the owned scratch root ${cleanupFull}: $($cleanupError.Exception.Message)"
            }
        }
    }
}

Write-Host ""
if ($script:checks -eq 0) {
    Write-Host "FAIL: no integration checks executed"
    exit 1
}
if ($script:failures.Count -gt 0) {
    Write-Host ("FAIL: {0}/{1} checks failed" -f $script:failures.Count, $script:checks)
    foreach ($failure in $script:failures) { Write-Host "  - $failure" }
    exit 1
}
Write-Host ("PASS: {0}/{0} unified-entry companion integration checks passed" -f $script:checks)
exit 0
