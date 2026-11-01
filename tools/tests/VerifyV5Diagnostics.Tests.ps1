<#
.SYNOPSIS
    Isolated self-test for the diagnostics collection added to tools\verify-v5.ps1.

.DESCRIPTION
    Builds a throwaway xunit.v3 / Microsoft.Testing.Platform fixture project under
    this task's own TEMP directory (never inside the repository, never a business
    test project) and runs the REAL unified verification script against it with
    -NoProjectDiscovery -SkipPluginCopyCheck, using the same runner modes and the
    same direct-runner arguments as the production manifest.

    Current accepted runner contract: `dotnet test` invokes the xunit.v3/MTP
    test application with --report-xunit-xml and --results-directory. The
    native per-case XML and the raw framework `<asm>_<tfm>_<arch>.log` are
    therefore written together under
      <log dir>\dotnetTest-xml\<project>\
    (inside the run's LogDir, which CI uploads as artifacts/verify-v5/**).
    The production console capture is <log dir>\dotnetTest-<project>.log;
    summary.log is only the derived gate row. Tests below cross-check the row
    against the XML, the raw framework log and the per-invocation meta file.

    Proven, per scenario:
      S1 dotnetTest FAIL : non-zero exit, FAIL row, real counts (2 total / 1
                           failed / 0 skipped) preserved, the failing case and
                           assertion in both the native XML and the raw
                           framework log, and agreement with the meta/native
                           exit. The log is read from the accepted results root
                           above, not from the old project TestResults folder.
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
                           run after the diagnostics changes (exit 1, FAIL row,
                           native XML total=0, native exit non-zero). The 0/0/0
                           console line is a failure, never a success summary.
      S6 locked+PASS     : an OLD log locked so it cannot be deleted makes the
                           purge fail; the run still PASSes, the locked stale
                           file is NOT copied into the current evidence
                           (before-snapshot exclusion), the current-run raw
                           framework log under dotnetTest-xml\<project>\ is
                           still present, and the gap is recorded.
      S7 locked+FAIL     : same locked stale file with a NEW failing run: FAIL
                           with real counts, the current failure content is in
                           the XML + raw framework log (changed file => proven
                           current), the stale marker still never appears in
                           the evidence, and a missing test-results folder is
                           not allowed to abort S8-S11.
      S8 unit            : the production collection/invocation helpers, extracted
                           from the script AST like the caller review probe,
                           keep same-named logs from BOTH source roots apart
                           (full project-relative path), keep the dotnetTest and
                           direct invocations of an auto run in separate
                           directories (no overwrite), and Invoke-Captured
                           persists partial output + the exception and returns
                           $null (never a fake 0) when the command throws.
      S9 exclusive+PASS  : round-2-review regression. An OLD log locked with
                           FileShare.None cannot even be HASHED at snapshot time;
                           the lock is released while the runner executes, so a
                           naive re-hash then succeeds and could mistake the OLD
                           file for current. A missing snapshot hash must exclude
                           conservatively: the run still PASSes and the OLD
                           content never enters the evidence.
      S10 exclusive+FAIL : same exclusive-lock-then-mid-run-release with a NEW
                           failing run: real FAIL counts, current failure content
                           collected, OLD content still excluded.
      S11 snapshot-blind : fault-injected (allowed for this scenario, distinct
                           from real-lock evidence): the before-snapshot
                           enumeration itself fails while the purge also fails -
                           the ENTIRE untrusted directory is excluded from
                           current-run evidence and the gap recorded.

    Gate contract under test: diagnostics collection must never change an exit
    code, a count, or a PASS/FAIL verdict - only add evidence. The internal
    TOOL_FIXTURE negative matrix (before S9) proves that the row/evidence
    assertions used above reject wrong runners, wrong or total=0 counts, a fake
    PASS, unknown (null)/Boolean/fractional native outcomes, missing failure
    case/assertion evidence, a success summary next to a non-zero native exit,
    duplicate IDs, missing XML attributes, wrong assembly/framework/schema,
    and old marked raw evidence. Those are test-assertion fixtures, not product
    test cases.

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

# --- accepted structural XML contract (read-only reuse) --------------------------
# Do not reimplement a weaker XML parser here. The accepted production
# Read-DirectResultXml is extracted from the unchanged verifier with the AST and
# used as the structural authority for IDs/attributes/collection aggregates.
$script:acceptedXmlReaderReady = $false
$script:acceptedXmlReaderError = 'not initialized'
try {
    $prodTokens = $null
    $prodParseErrors = $null
    $prodAst = [System.Management.Automation.Language.Parser]::ParseFile($verifyScript, [ref]$prodTokens, [ref]$prodParseErrors)
    if ($prodParseErrors -and $prodParseErrors.Count) {
        throw "production verifier parse failed: $($prodParseErrors[0].Message)"
    }
    $readerAst = $prodAst.Find({
            param($n)
            $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Read-DirectResultXml'
        }, $true)
    if ($null -eq $readerAst) { throw 'production Read-DirectResultXml function not found' }
    Invoke-Expression $readerAst.Extent.Text
    if (-not (Get-Command -Name Read-DirectResultXml -CommandType Function -ErrorAction SilentlyContinue)) {
        throw 'extracted Read-DirectResultXml is not callable'
    }
    $script:acceptedXmlReaderReady = $true
    $script:acceptedXmlReaderError = 'ok'
}
catch {
    $script:acceptedXmlReaderError = $_.Exception.Message
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
    $script:lastVerifyStartedUtc = [DateTime]::UtcNow
    $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $verifyScript `
        -Configuration Release -RunnerMode $RunnerMode -ManifestPath $manifestPath `
        -LogDir $LogDir -SkipPluginCopyCheck -NoProjectDiscovery -DotnetPath $DotnetPath *>&1
    $exit = $LASTEXITCODE
    $script:lastVerifyFinishedUtc = [DateTime]::UtcNow
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

$script:knownRunners = @(
    'dotnet test (MTP)',
    'in-process (direct)',
    'dotnet test (no native report)',
    'dotnet test (NOT INVOKED)'
)

function Get-SummaryRows {
    # Parse the production summary table by header column offsets. This is
    # deliberately not a loose "dotnet\s+test" regex: the runner column is
    # read as a whole field and must equal one of the known production
    # runners, and the project/kind/count/result fields are bound to the same
    # physical row.
    param([string]$Text)
    $lineList = @($Text -split "`r?`n")
    $headerIndex = -1
    for ($i = 0; $i -lt $lineList.Count; $i++) {
        if ($lineList[$i] -match '^PROJECT\s+KIND\s+REQ\s+INSLN\s+RUNNER\s+TOTAL\s+FAILED\s+SKIPPED\s+RESULT(?:\s+LOG)?\s*$') {
            $headerIndex = $i
            break
        }
    }
    if ($headerIndex -lt 0) {
        return [pscustomobject]@{ Ok = $false; Reason = 'summary header missing or unrecognized'; Rows = @() }
    }

    $header = $lineList[$headerIndex]
    $names = @('PROJECT', 'KIND', 'REQ', 'INSLN', 'RUNNER', 'TOTAL', 'FAILED', 'SKIPPED', 'RESULT', 'LOG')
    $starts = @{}
    $last = -1
    foreach ($name in $names) {
        $index = $header.IndexOf($name)
        if ($index -lt 0) {
            return [pscustomobject]@{ Ok = $false; Reason = "summary header has no $name column"; Rows = @() }
        }
        if ($index -le $last) {
            return [pscustomobject]@{ Ok = $false; Reason = "summary header columns are out of order at $name"; Rows = @() }
        }
        $starts[$name] = $index
        $last = $index
    }

    $rows = @()
    for ($i = $headerIndex + 1; $i -lt $lineList.Count; $i++) {
        $line = $lineList[$i]
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -match '^\s') { continue }          # detail/continuation lines
        if ($line -match '^OVERALL:') { continue }
        if ($line.Length -le $starts['RESULT']) { continue }

        $fields = [ordered]@{}
        for ($j = 0; $j -lt $names.Count; $j++) {
            $name = $names[$j]
            $start = $starts[$name]
            $end = $(if ($j + 1 -lt $names.Count) { $starts[$names[$j + 1]] } else { $line.Length })
            if ($end -gt $line.Length) { $end = $line.Length }
            $fields[$name] = $line.Substring($start, $end - $start).Trim()
        }
        $rows += [pscustomobject]$fields
    }
    return [pscustomobject]@{ Ok = $true; Reason = 'ok'; Header = $header; Rows = @($rows) }
}

function Test-SummaryRow {
    # Strict row contract used by every scenario. Returns Ok/Reason plus the
    # bound Row so callers can cross-check XML/meta/native evidence.
    param(
        [string]$Text,
        [string]$Runner,
        [int]$Total,
        [int]$Failed,
        [int]$Skipped,
        [string]$Result,
        [switch]$AllowZeroTotal
    )
    if ($script:knownRunners -notcontains $Runner) {
        return [pscustomobject]@{ Ok = $false; Reason = "expected runner '$Runner' is not a known production runner"; Row = $null }
    }

    $table = Get-SummaryRows -Text $Text
    if (-not $table.Ok) {
        return [pscustomobject]@{ Ok = $false; Reason = $table.Reason; Row = $null }
    }
    $matches = @($table.Rows | Where-Object { $_.PROJECT -eq 'Probe.Tests' })
    if ($matches.Count -ne 1) {
        return [pscustomobject]@{ Ok = $false; Reason = "expected exactly one Probe.Tests row, found $($matches.Count)"; Row = $null }
    }
    $row = $matches[0]
    if ($script:knownRunners -notcontains $row.RUNNER) {
        return [pscustomobject]@{ Ok = $false; Reason = "row runner '$($row.RUNNER)' is not a known production runner"; Row = $row }
    }
    if ($row.RUNNER -ne $Runner) {
        return [pscustomobject]@{ Ok = $false; Reason = "row runner '$($row.RUNNER)' != expected '$Runner'"; Row = $row }
    }
    if ($row.KIND -ne 'test') {
        return [pscustomobject]@{ Ok = $false; Reason = "row kind '$($row.KIND)' != test"; Row = $row }
    }
    if ($row.RESULT -notin @('PASS', 'FAIL', 'BLOCKED', 'NOT_RUN')) {
        return [pscustomobject]@{ Ok = $false; Reason = "row result '$($row.RESULT)' is not a known result"; Row = $row }
    }

    $nTotal = 0; $nFailed = 0; $nSkipped = 0
    if (-not [int]::TryParse($row.TOTAL, [ref]$nTotal) -or
        -not [int]::TryParse($row.FAILED, [ref]$nFailed) -or
        -not [int]::TryParse($row.SKIPPED, [ref]$nSkipped)) {
        return [pscustomobject]@{ Ok = $false; Reason = 'row counts are not integers'; Row = $row }
    }
    if ($nTotal -lt 0 -or $nFailed -lt 0 -or $nSkipped -lt 0) {
        return [pscustomobject]@{ Ok = $false; Reason = 'row counts contain a negative value'; Row = $row }
    }
    if ($nFailed + $nSkipped -gt $nTotal) {
        return [pscustomobject]@{ Ok = $false; Reason = "failed+skipped ($($nFailed + $nSkipped)) exceeds total ($nTotal)"; Row = $row }
    }
    if (-not $AllowZeroTotal -and $nTotal -le 0) {
        return [pscustomobject]@{ Ok = $false; Reason = "total must be greater than zero, got $nTotal"; Row = $row }
    }
    if ($row.RESULT -eq 'PASS' -and ($nFailed -ne 0 -or $nSkipped -ne 0 -or $nTotal -le 0)) {
        return [pscustomobject]@{ Ok = $false; Reason = "PASS row is inconsistent: total=$nTotal failed=$nFailed skipped=$nSkipped"; Row = $row }
    }
    if ($row.RESULT -ne $Result) {
        return [pscustomobject]@{ Ok = $false; Reason = "row result '$($row.RESULT)' != expected '$Result'"; Row = $row }
    }
    if ($nTotal -ne $Total -or $nFailed -ne $Failed -or $nSkipped -ne $Skipped) {
        return [pscustomobject]@{ Ok = $false; Reason = "row counts $nTotal/$nFailed/$nSkipped != expected $Total/$Failed/$Skipped"; Row = $row }
    }
    if ([string]::IsNullOrWhiteSpace($row.LOG) -or $row.LOG -notmatch '\.log$') {
        return [pscustomobject]@{ Ok = $false; Reason = "row log column '$($row.LOG)' is not a log file"; Row = $row }
    }
    return [pscustomobject]@{ Ok = $true; Reason = 'ok'; Row = $row }
}

function Assert-SummaryRow {
    param(
        [string]$LogDir, [string]$Runner, [int]$Total, [int]$Failed, [int]$Skipped,
        [string]$Result, [string]$Label, [switch]$AllowZeroTotal
    )
    $text = Get-Content -LiteralPath (Join-Path $LogDir 'summary.log') -Raw
    $parsed = Test-SummaryRow -Text $text -Runner $Runner -Total $Total -Failed $Failed -Skipped $Skipped -Result $Result -AllowZeroTotal:$AllowZeroTotal
    Assert-True $parsed.Ok ("$Label [$($parsed.Reason)]")
    return $parsed
}

function Get-DotnetTestEvidence {
    # Current accepted evidence contract: native per-case XML and the raw
    # xunit.v3/MTP framework log are written into the run's --results-directory
    # (LogDir\dotnetTest-xml\<project>), not into the project TestResults tree.
    # Structural validity is delegated to the accepted production
    # Read-DirectResultXml; this function only adds diagnostics-specific exact
    # identity/schema and invocation-currentness checks.
    param([string]$LogDir, [object]$RunStartedUtc = $null)
    $xmlPath = Join-Path $LogDir (Join-Path 'dotnetTest-xml' (Join-Path 'Probe.Tests' 'dotnet-test-results.xml'))
    $rawDir = Join-Path $LogDir (Join-Path 'dotnetTest-xml' 'Probe.Tests')
    $metaPath = Join-Path $LogDir 'runs\Probe.Tests\dotnetTest.meta.txt'
    $expectedAssemblyPath = $null
    if ($script:probeDir) {
        $expectedAssemblyPath = Join-Path $script:probeDir 'bin\Release\net8.0\Probe.Tests.dll'
    }
    $result = [pscustomobject]@{
        XmlOk = $false; XmlReason = 'not read'; XmlPath = $xmlPath; Counts = $null; Nodes = @()
        Framework = ''; SchemaVersion = ''; XmlLastWriteUtc = $null; XmlSha256 = ''
        RawLogOk = $false; RawLogReason = 'not read'; RawLogPath = $null; RawLogText = ''; RawLogLastWriteUtc = $null
        MetaOk = $false; MetaPath = $metaPath; MetaText = ''
    }

    # Make this function usable when a read-only probe AST-extracts only the
    # shared diagnostics functions. The accepted structural reader still comes
    # from the unchanged production verifier, never from a copied parser.
    if (Get-Command -Name Read-DirectResultXml -CommandType Function -ErrorAction SilentlyContinue) {
        $script:acceptedXmlReaderReady = $true
        $script:acceptedXmlReaderError = 'ok'
    }
    if (-not (Get-Command -Name Read-DirectResultXml -CommandType Function -ErrorAction SilentlyContinue)) {
        $candidates = @()
        if ($verifyScript) { $candidates += $verifyScript }
        if ($repo) { $candidates += (Join-Path $repo 'tools\verify-v5.ps1') }
        $candidates += (Join-Path (Get-Location) 'tools\verify-v5.ps1')
        if ($PSScriptRoot) { $candidates += (Join-Path $PSScriptRoot '..\..\tools\verify-v5.ps1') }
        foreach ($candidate in @($candidates | Select-Object -Unique)) {
            if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
            try {
                $readerTokens = $null
                $readerErrors = $null
                $readerAst = [System.Management.Automation.Language.Parser]::ParseFile($candidate, [ref]$readerTokens, [ref]$readerErrors)
                if ($readerErrors -and $readerErrors.Count) { continue }
                $readerFn = $readerAst.Find({
                        param($n)
                        $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Read-DirectResultXml'
                    }, $true)
                if ($null -eq $readerFn) { continue }
                Invoke-Expression $readerFn.Extent.Text
                if (Get-Command -Name Read-DirectResultXml -CommandType Function -ErrorAction SilentlyContinue) {
                    $script:acceptedXmlReaderReady = $true
                    $script:acceptedXmlReaderError = 'ok'
                    break
                }
            }
            catch { }
        }
    }
    if ($null -eq $RunStartedUtc) { $RunStartedUtc = [datetime]::MinValue }
    else { $RunStartedUtc = [datetime]$RunStartedUtc }
    if ($RunStartedUtc -eq [datetime]::MinValue) {
        # Backward-compatible fallback for read-only probes that invoke these
        # shared functions directly without an explicit invocation timestamp.
        # Real scenarios always pass the observed start time. The fallback is
        # anchored to this run's own meta/summary and is deliberately
        # conservative; it is not used to bless an old/foreign raw log.
        $anchor = $null
        if (Test-Path -LiteralPath $metaPath -PathType Leaf) { $anchor = $metaPath }
        elseif (Test-Path -LiteralPath (Join-Path $LogDir 'summary.log') -PathType Leaf) { $anchor = Join-Path $LogDir 'summary.log' }
        if ($anchor) { $RunStartedUtc = (Get-Item -LiteralPath $anchor).LastWriteTimeUtc.AddMinutes(-10) }
        else { $RunStartedUtc = [DateTime]::UtcNow.AddMinutes(-10) }
    }
    if (-not $script:acceptedXmlReaderReady -or -not (Get-Command -Name Read-DirectResultXml -CommandType Function -ErrorAction SilentlyContinue)) {
        $result.XmlReason = "accepted structural reader unavailable: $($script:acceptedXmlReaderError)"
        $result.RawLogReason = 'accepted structural reader unavailable'
        return $result
    }

    if (-not (Test-Path -LiteralPath $xmlPath -PathType Leaf)) {
        $result.XmlReason = "native XML missing: $xmlPath"
    }
    else {
        $xml = Read-DirectResultXml -XmlPath $xmlPath -ExpectedAssemblyName 'Probe.Tests'
        if (-not $xml.Ok) {
            $result.XmlReason = $xml.Reason
        }
        else {
            $lastWrite = (Get-Item -LiteralPath $xmlPath).LastWriteTimeUtc
            $result.XmlLastWriteUtc = $lastWrite
            try { $result.XmlSha256 = (Get-FileHash -LiteralPath $xmlPath -Algorithm SHA256).Hash } catch { }
            if ($lastWrite -lt $RunStartedUtc.AddSeconds(-5)) {
                $result.XmlReason = "native XML predates this invocation (last write $($lastWrite.ToString('o')) < start $($RunStartedUtc.ToString('o')))"
            }
            elseif ($xml.TestFramework -cnotmatch '^xUnit\.net v3 4\.0\.2(?:\+[0-9A-Za-z][0-9A-Za-z.-]*)?$') {
                $result.XmlReason = "runner identity mismatch: expected xUnit.net v3 4.0.2 with optional '+build' metadata, got '$($xml.TestFramework)'"
            }
            elseif ($xml.SchemaVersion -ne '3') {
                $result.XmlReason = "result XML schema identity mismatch: expected '3', got '$($xml.SchemaVersion)'"
            }
            else {
                try {
                    $actualAssembly = [IO.Path]::GetFullPath([string]$xml.AssemblyName)
                    $assemblyOk = $true
                    if (-not [IO.Path]::IsPathRooted([string]$xml.AssemblyName)) {
                        $result.XmlReason = "assembly name is not a rooted native path: '$($xml.AssemblyName)'"
                        $assemblyOk = $false
                    }
                    elseif ($expectedAssemblyPath) {
                        $expectedAssembly = [IO.Path]::GetFullPath($expectedAssemblyPath)
                        if (-not [string]::Equals($actualAssembly, $expectedAssembly, [StringComparison]::OrdinalIgnoreCase)) {
                            $result.XmlReason = "assembly path mismatch: expected '$expectedAssembly', got '$actualAssembly'"
                            $assemblyOk = $false
                        }
                    }
                    elseif (-not $actualAssembly.EndsWith('\Probe.Tests\bin\Release\net8.0\Probe.Tests.dll', [StringComparison]::OrdinalIgnoreCase)) {
                        $result.XmlReason = "assembly path does not identify Probe.Tests Release output: '$actualAssembly'"
                        $assemblyOk = $false
                    }
                    if ($assemblyOk) {
                        [xml]$doc = Get-Content -LiteralPath $xmlPath -Raw -ErrorAction Stop
                        $nodes = @($doc.SelectNodes('//test'))
                        $c = $xml.Counts
                        $failedNodes = @($nodes | Where-Object { $_.result -eq 'Fail' }).Count
                        $passedNodes = @($nodes | Where-Object { $_.result -eq 'Pass' }).Count
                        $skippedNodes = @($nodes | Where-Object { $_.result -eq 'Skip' }).Count
                        $notRunNodes = @($nodes | Where-Object { $_.result -eq 'NotRun' }).Count
                        if ($nodes.Count -ne $c.Total -or $failedNodes -ne $c.Failed -or
                            $passedNodes -ne $c.Passed -or $skippedNodes -ne $c.Skipped -or
                            $notRunNodes -ne $c.NotRun) {
                            $result.XmlReason = 'test node status counts do not match the accepted XML aggregate counts'
                        }
                        else {
                            $result.XmlOk = $true
                            $result.XmlReason = 'ok'
                            $result.Counts = $c
                            $result.Nodes = $nodes
                            $result.Framework = $xml.TestFramework
                            $result.SchemaVersion = $xml.SchemaVersion
                        }
                    }
                }
                catch {
                    $result.XmlReason = "native XML identity/counts could not be validated: $($_.Exception.Message)"
                }
            }
        }
    }

    if (-not (Test-Path -LiteralPath $rawDir -PathType Container)) {
        $result.RawLogReason = "raw framework log directory missing: $rawDir"
    }
    else {
        $rawLogs = @(Get-ChildItem -LiteralPath $rawDir -File -Filter '*.log' -ErrorAction SilentlyContinue)
        if ($rawLogs.Count -ne 1) {
            $result.RawLogReason = "expected exactly one raw framework log under $rawDir, found $($rawLogs.Count)"
        }
        elseif ($rawLogs[0].Name -notmatch '^Probe\.Tests_net8\.0_x64\.log$') {
            $result.RawLogReason = "unexpected raw framework log name '$($rawLogs[0].Name)'"
        }
        else {
            $text = Get-Content -LiteralPath $rawLogs[0].FullName -Raw
            $lastWrite = $rawLogs[0].LastWriteTimeUtc
            $result.RawLogLastWriteUtc = $lastWrite
            if ([string]::IsNullOrWhiteSpace($text)) {
                $result.RawLogReason = 'raw framework log is empty'
            }
            elseif ($text.Contains('VERIFY-V5-STALE-OLD-RUN')) {
                $result.RawLogReason = 'raw framework log contains the known stale-old-run marker'
            }
            elseif ($lastWrite -lt $RunStartedUtc.AddSeconds(-5)) {
                $result.RawLogReason = "raw framework log predates this invocation (last write $($lastWrite.ToString('o')) < start $($RunStartedUtc.ToString('o')))"
            }
            else {
                $result.RawLogOk = $true
                $result.RawLogReason = 'ok'
                $result.RawLogPath = $rawLogs[0].FullName
                $result.RawLogText = $text
            }
        }
    }

    if (Test-Path -LiteralPath $metaPath -PathType Leaf) {
        $result.MetaOk = $true
        $result.MetaText = Get-Content -LiteralPath $metaPath -Raw
    }
    return $result
}

function Test-DotnetTestEvidence {
    # Cross-check the summary row against native XML, raw framework log and the
    # per-invocation meta file. A PASS is impossible with a non-zero native
    # exit; a failure is impossible without the failing case and assertion.
    # NativeExit is object-typed and an explicit null/unknown is rejected before
    # any comparison; callers must pass the observed exit, not an expected one.
    param(
        [string]$LogDir, [string]$SummaryText, [string]$Runner, [int]$Total,
        [int]$Failed, [int]$Skipped, [string]$Result, [object]$NativeExit,
        [object]$RunStartedUtc = $null, [string]$CaseName = '', [string]$Message = '',
        [string]$ExpectedHead = '', [switch]$AllowZeroTotal
    )
    if ($null -eq $NativeExit) {
        return [pscustomobject]@{ Ok = $false; Reason = 'native exit is unknown (null); never treated as 0'; Evidence = $null }
    }
    $nativeTypeCode = [System.Type]::GetTypeCode($NativeExit.GetType())
    $integralCodes = @(
        [System.TypeCode]::SByte, [System.TypeCode]::Byte,
        [System.TypeCode]::Int16, [System.TypeCode]::UInt16,
        [System.TypeCode]::Int32, [System.TypeCode]::UInt32,
        [System.TypeCode]::Int64, [System.TypeCode]::UInt64
    )
    if ($integralCodes -notcontains $nativeTypeCode) {
        return [pscustomobject]@{ Ok = $false; Reason = "native exit is not an integral observation (type $($NativeExit.GetType().FullName), value '$NativeExit')"; Evidence = $null }
    }
    $nativeInt = [long]$NativeExit

    $row = Test-SummaryRow -Text $SummaryText -Runner $Runner -Total $Total -Failed $Failed `
        -Skipped $Skipped -Result $Result -AllowZeroTotal:$AllowZeroTotal
    if (-not $row.Ok) { return [pscustomobject]@{ Ok = $false; Reason = "summary row: $($row.Reason)"; Evidence = $null } }

    $evidence = Get-DotnetTestEvidence -LogDir $LogDir -RunStartedUtc $RunStartedUtc
    if (-not $evidence.XmlOk) { return [pscustomobject]@{ Ok = $false; Reason = "native XML: $($evidence.XmlReason)"; Evidence = $evidence } }
    if (-not $evidence.RawLogOk) { return [pscustomobject]@{ Ok = $false; Reason = "raw framework log: $($evidence.RawLogReason)"; Evidence = $evidence } }
    if (-not $evidence.MetaOk) { return [pscustomobject]@{ Ok = $false; Reason = "invocation meta missing: $($evidence.MetaPath)"; Evidence = $evidence } }

    $c = $evidence.Counts
    $xmlFailed = $c.Failed + $c.Errors
    $xmlSkipped = $c.Skipped + $c.NotRun
    if ($c.Total -ne $Total -or $xmlFailed -ne $Failed -or $xmlSkipped -ne $Skipped) {
        return [pscustomobject]@{ Ok = $false; Reason = "summary $Total/$Failed/$Skipped != XML $($c.Total)/$xmlFailed/$xmlSkipped"; Evidence = $evidence }
    }
    if ($c.Errors -ne 0) {
        return [pscustomobject]@{ Ok = $false; Reason = "XML reports framework errors=$($c.Errors); the diagnostics fixture expects assertion failures only"; Evidence = $evidence }
    }
    $failedNodes = @($evidence.Nodes | Where-Object { $_.result -eq 'Fail' }).Count
    if ($failedNodes -ne $c.Failed) {
        return [pscustomobject]@{ Ok = $false; Reason = "XML has $failedNodes Fail test node(s) but declared failed=$($c.Failed)"; Evidence = $evidence }
    }
    if ($evidence.MetaText -notmatch "(?m)^exit code\s*:\s*$nativeInt\s*$") {
        return [pscustomobject]@{ Ok = $false; Reason = "meta native exit does not equal the observed exit $nativeInt"; Evidence = $evidence }
    }
    if ($evidence.MetaText -notmatch "(?m)^total\s*:\s*$Total\s*$" -or
        $evidence.MetaText -notmatch "(?m)^failed\s*:\s*$Failed\s*$" -or
        $evidence.MetaText -notmatch "(?m)^skipped\s*:\s*$Skipped\s*$") {
        return [pscustomobject]@{ Ok = $false; Reason = 'meta counts do not match the summary/XML counts'; Evidence = $evidence }
    }
    if ($ExpectedHead -and -not $evidence.MetaText.Contains($ExpectedHead)) {
        return [pscustomobject]@{ Ok = $false; Reason = 'meta does not record the real head SHA'; Evidence = $evidence }
    }
    if ($evidence.RawLogText -notmatch "(?m)^\s*total:\s*$Total\s*$" -or
        $evidence.RawLogText -notmatch "(?m)^\s*failed:\s*$Failed\s*$" -or
        $evidence.RawLogText -notmatch "(?m)^\s*skipped:\s*$Skipped\s*$") {
        return [pscustomobject]@{ Ok = $false; Reason = 'raw framework log counts do not match the summary/XML counts'; Evidence = $evidence }
    }

    if ($Total -eq 0) {
        if ($Result -ne 'FAIL') { return [pscustomobject]@{ Ok = $false; Reason = 'zero-test row is not FAIL'; Evidence = $evidence } }
        if ($nativeInt -eq 0) { return [pscustomobject]@{ Ok = $false; Reason = 'zero-test run has native exit 0'; Evidence = $evidence } }
        if ($evidence.RawLogText -notmatch '(?m)^Test run summary: (Failed!|Zero tests ran)') {
            return [pscustomobject]@{ Ok = $false; Reason = 'zero-test raw log is neither a failure nor a zero-tests-ran summary'; Evidence = $evidence }
        }
        return [pscustomobject]@{ Ok = $true; Reason = 'ok'; Evidence = $evidence }
    }

    if ($Result -eq 'PASS') {
        if ($nativeInt -ne 0) { return [pscustomobject]@{ Ok = $false; Reason = "PASS row has native exit $nativeInt"; Evidence = $evidence } }
        if ($evidence.RawLogText -notmatch '(?m)^Test run summary: Passed!') {
            return [pscustomobject]@{ Ok = $false; Reason = 'PASS raw log is not a Passed summary'; Evidence = $evidence }
        }
        $notPassed = @($evidence.Nodes | Where-Object { $_.result -ne 'Pass' })
        if ($notPassed.Count -gt 0) {
            return [pscustomobject]@{ Ok = $false; Reason = "$($notPassed.Count) XML test node(s) are not Pass in a PASS row"; Evidence = $evidence }
        }
    }
    else {
        if ($nativeInt -eq 0) { return [pscustomobject]@{ Ok = $false; Reason = 'FAIL row has native exit 0'; Evidence = $evidence } }
        if ($evidence.RawLogText -notmatch '(?m)^Test run summary: Failed!') {
            return [pscustomobject]@{ Ok = $false; Reason = 'FAIL raw log is not a Failed summary'; Evidence = $evidence }
        }
        if ([string]::IsNullOrWhiteSpace($CaseName) -or [string]::IsNullOrWhiteSpace($Message)) {
            return [pscustomobject]@{ Ok = $false; Reason = 'failure evidence requires a case name and assertion text'; Evidence = $evidence }
        }
        $caseNodes = @($evidence.Nodes | Where-Object { $_.name -eq $CaseName })
        if ($caseNodes.Count -ne 1) {
            return [pscustomobject]@{ Ok = $false; Reason = "expected exactly one XML node named '$CaseName', found $($caseNodes.Count)"; Evidence = $evidence }
        }
        if ($caseNodes[0].result -ne 'Fail') {
            return [pscustomobject]@{ Ok = $false; Reason = "XML case '$CaseName' result is '$($caseNodes[0].result)', not Fail"; Evidence = $evidence }
        }
        $failureMessage = [string]$caseNodes[0].failure.message
        if ($failureMessage -notmatch [regex]::Escape($Message)) {
            return [pscustomobject]@{ Ok = $false; Reason = "XML failure message does not contain '$Message'"; Evidence = $evidence }
        }
        if ($evidence.RawLogText -notmatch [regex]::Escape($CaseName)) {
            return [pscustomobject]@{ Ok = $false; Reason = "raw framework log does not name the failing case '$CaseName'"; Evidence = $evidence }
        }
        if ($evidence.RawLogText -notmatch [regex]::Escape($Message)) {
            return [pscustomobject]@{ Ok = $false; Reason = "raw framework log does not contain the assertion text '$Message'"; Evidence = $evidence }
        }
    }
    return [pscustomobject]@{ Ok = $true; Reason = 'ok'; Evidence = $evidence }
}

function Assert-DotnetTestEvidence {
    param(
        [string]$LogDir, [string]$Runner, [int]$Total, [int]$Failed, [int]$Skipped,
        [string]$Result, [object]$NativeExit, [object]$RunStartedUtc = $null,
        [string]$CaseName = '', [string]$Message = '',
        [string]$ExpectedHead = '', [string]$Label, [switch]$AllowZeroTotal
    )
    $summaryText = Get-Content -LiteralPath (Join-Path $LogDir 'summary.log') -Raw
    $checked = Test-DotnetTestEvidence -LogDir $LogDir -SummaryText $summaryText -Runner $Runner `
        -Total $Total -Failed $Failed -Skipped $Skipped -Result $Result -NativeExit $NativeExit `
        -RunStartedUtc $RunStartedUtc -CaseName $CaseName -Message $Message -ExpectedHead $ExpectedHead -AllowZeroTotal:$AllowZeroTotal
    Assert-True $checked.Ok ("$Label [$($checked.Reason)]")
    return $checked
}

function Start-MidRunUnlocker {
    # S9/S10 mechanic, mirroring the caller review probe: an OLD file is locked
    # with FileShare.None (so the before-snapshot cannot hash it) and the lock
    # is released from a second runspace as soon as the before-snapshot is
    # demonstrably done - proven by the collection-errors.log line the purge
    # failure writes right after snapshotting, or by the deletion of the
    # unlocked sentinel. The lock is therefore released while the runner is
    # still executing, so the collection re-hash succeeds and could mistake the
    # OLD file for current - exactly the round-2-review leak.
    param([System.IO.FileStream]$LockStream, [string]$UnlockedSentinel, [string]$PurgeFailureMarker, [hashtable]$ResultBox)
    $ps = [powershell]::Create()
    $null = $ps.AddScript({
        param($Stream, $Sentinel, $Marker, $Box)
        $deadline = [DateTime]::UtcNow.AddSeconds(180)
        while ([DateTime]::UtcNow -lt $deadline) {
            if (-not (Test-Path -LiteralPath $Sentinel)) { $Box.PurgedObserved = $true; break }
            if (Test-Path -LiteralPath $Marker) { $Box.PurgedObserved = $true; break }
            Start-Sleep -Milliseconds 200
        }
        $Stream.Dispose()
        $Box.Released = $true
    })
    $null = $ps.AddArgument($LockStream).AddArgument($UnlockedSentinel).AddArgument($PurgeFailureMarker).AddArgument($ResultBox)
    $null = $ps.BeginInvoke()
    return $ps
}

function Stop-MidRunUnlocker([powershell]$Unlocker) {
    if ($null -ne $Unlocker) {
        try { $Unlocker.Stop() } catch { }
        $Unlocker.Dispose()
    }
}

function Wait-UnlockerReleased([hashtable]$ResultBox) {
    for ($i = 0; $i -lt 100 -and -not $ResultBox.Released; $i++) { Start-Sleep -Milliseconds 100 }
    return $ResultBox.Released
}

$expectedHead = $null
try { $expectedHead = (& git -C $repoRoot rev-parse HEAD 2>$null | Out-String).Trim() } catch { }

try {
    New-Workspace
    Assert-True $script:acceptedXmlReaderReady "accepted production Read-DirectResultXml is available read-only [$($script:acceptedXmlReaderError)]"

    # --- S1: dotnetTest mode, real failing case -----------------------------------
    Write-Host ""
    Write-Host "S1: dotnetTest mode - real failing xunit.v3/MTP case"
    Set-ProbeSource $failSource
    $log1 = Join-Path $workspace 'logs-s1'
    $exit1 = Invoke-Verify -RunnerMode 'dotnetTest' -LogDir $log1
    $started1 = $script:lastVerifyStartedUtc
    $summary1 = Get-Content -LiteralPath (Join-Path $log1 'summary.log') -Raw
    Assert-True ($exit1 -ne 0) "S1 exit code is non-zero (got $exit1)"
    Assert-True ($summary1 -match 'OVERALL: FAIL') "S1 summary reports OVERALL: FAIL"
    $row1 = Assert-SummaryRow -LogDir $log1 -Runner 'dotnet test (MTP)' -Total 2 -Failed 1 -Skipped 0 `
        -Result 'FAIL' -Label "S1 row binds the MTP runner and keeps real counts 2 total / 1 failed / 0 skipped as FAIL"

    $meta1 = Join-Path $log1 'runs\Probe.Tests\dotnetTest.meta.txt'
    Assert-True (Test-Path -LiteralPath $meta1) "S1 per-invocation meta file exists (runs\Probe.Tests\dotnetTest.meta.txt)"
    if (Test-Path -LiteralPath $meta1) {
        $meta1Text = Get-Content -LiteralPath $meta1 -Raw
        Assert-True ($meta1Text -match 'exit code\s*:\s*1') "S1 meta records exit code 1"
        Assert-True ($meta1Text -match 'total\s*:\s*2') "S1 meta records total 2"
        Assert-True ($meta1Text -match 'failed\s*:\s*1') "S1 meta records failed 1"
        if ($expectedHead) { Assert-True ($meta1Text.Contains($expectedHead)) "S1 meta records the real head SHA" }
    }

    $evidence1 = Get-DotnetTestEvidence -LogDir $log1 -RunStartedUtc $started1
    Assert-True ($evidence1.XmlOk -and $evidence1.Counts.Total -eq 2 -and $evidence1.Counts.Failed -eq 1 -and $evidence1.Counts.Errors -eq 0) "S1 native xunit.v3/MTP XML keeps real counts 2 total / 1 failed / 0 errors [$($evidence1.XmlReason)]"
    $xmlCase1 = @()
    if ($evidence1.XmlOk) { $xmlCase1 = @($evidence1.Nodes | Where-Object { $_.name -eq 'Probe.Tests.ProbeTests.ControlledFailure' }) }
    Assert-True ($xmlCase1.Count -eq 1 -and $xmlCase1[0].result -eq 'Fail' -and ([string]$xmlCase1[0].failure.message).Contains($sentinel)) "S1 native XML names the failing case and contains the assertion message"
    Assert-True ($evidence1.RawLogOk -and $evidence1.RawLogText.Contains($sentinel) -and $evidence1.RawLogText.Contains('Probe.Tests.ProbeTests.ControlledFailure')) "S1 raw framework log (dotnetTest-xml\Probe.Tests\<asm>_<tfm>_<arch>.log, not summary.log) names the failing case and assertion [$($evidence1.RawLogReason)]"
    Assert-DotnetTestEvidence -LogDir $log1 -Runner 'dotnet test (MTP)' -Total 2 -Failed 1 -Skipped 0 `
        -Result 'FAIL' -NativeExit $exit1 -RunStartedUtc $started1 -CaseName 'Probe.Tests.ProbeTests.ControlledFailure' -Message $sentinel `
        -ExpectedHead $expectedHead -Label "S1 summary, native XML, raw framework log, meta and observed native exit agree on the controlled failure" | Out-Null

    # --- S2: stale log planted, then current PASS run ------------------------------
    Write-Host ""
    Write-Host "S2: dotnetTest mode - planted OLD failure log must not leak into a PASS run"
    Set-ProbeSource $passSource
    Seed-StaleLog
    $log2 = Join-Path $workspace 'logs-s2'
    $exit2 = Invoke-Verify -RunnerMode 'dotnetTest' -LogDir $log2
    $started2 = $script:lastVerifyStartedUtc
    $summary2 = Get-Content -LiteralPath (Join-Path $log2 'summary.log') -Raw
    Assert-True ($exit2 -eq 0) "S2 exit code is 0 with the stale failure log planted beforehand (got $exit2)"
    Assert-True ($summary2 -match 'OVERALL: PASS') "S2 summary reports OVERALL: PASS"
    $row2 = Assert-SummaryRow -LogDir $log2 -Runner 'dotnet test (MTP)' -Total 2 -Failed 0 -Skipped 0 `
        -Result 'PASS' -Label "S2 row binds the MTP runner and keeps real counts 2 total / 0 failed / 0 skipped as PASS"
    $evidence2 = Get-DotnetTestEvidence -LogDir $log2 -RunStartedUtc $started2
    Assert-True ($evidence2.RawLogOk -and -not $evidence2.RawLogText.Contains($staleMarker)) "S2 accepted raw framework log is current-run evidence and carries no stale marker [$($evidence2.RawLogReason)]"
    Assert-DotnetTestEvidence -LogDir $log2 -Runner 'dotnet test (MTP)' -Total 2 -Failed 0 -Skipped 0 `
        -Result 'PASS' -NativeExit $exit2 -RunStartedUtc $started2 -Label "S2 summary, native XML, raw framework log and observed native exit agree on the PASS run" | Out-Null

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
    $row3 = Assert-SummaryRow -LogDir $log3 -Runner 'in-process (direct)' -Total 2 -Failed 1 -Skipped 0 `
        -Result 'FAIL' -Label "S3 row binds the direct runner and keeps real counts 2 total / 1 failed / 0 skipped as FAIL"

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

    $directXml3 = Join-Path $log3 'direct-xml\Probe.Tests\direct-results.xml'
    $directXmlOk3 = $false
    if (Test-Path -LiteralPath $directXml3) {
        try {
            [xml]$doc3 = Get-Content -LiteralPath $directXml3 -Raw -ErrorAction Stop
            $a3 = @($doc3.SelectNodes('/assemblies/assembly'))[0]
            $case3 = @($doc3.SelectNodes('//test') | Where-Object { $_.name -eq 'Probe.Tests.ProbeTests.ControlledFailure' })
            $directXmlOk3 = ($null -ne $a3) -and ([int]$a3.total -eq 2) -and ([int]$a3.failed -eq 1) -and
                ($case3.Count -eq 1) -and ($case3[0].result -eq 'Fail') -and
                (([string]$case3[0].failure.message).Contains($sentinel))
        }
        catch { $directXmlOk3 = $false }
    }
    Assert-True $directXmlOk3 "S3 direct native XML keeps real counts and the failing case/assertion"

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
    $row4 = Assert-SummaryRow -LogDir $log4 -Runner 'in-process (direct)' -Total 2 -Failed 0 -Skipped 0 `
        -Result 'PASS' -Label "S4 row binds the direct runner and keeps real counts 2 total / 0 failed / 0 skipped as PASS"
    $directXml4 = Join-Path $log4 'direct-xml\Probe.Tests\direct-results.xml'
    $directXmlOk4 = $false
    if (Test-Path -LiteralPath $directXml4) {
        try {
            [xml]$doc4 = Get-Content -LiteralPath $directXml4 -Raw -ErrorAction Stop
            $a4 = @($doc4.SelectNodes('/assemblies/assembly'))[0]
            $tests4 = @($doc4.SelectNodes('//test'))
            $notPass4 = @($tests4 | Where-Object { $_.result -ne 'Pass' })
            $directXmlOk4 = ($null -ne $a4) -and ([int]$a4.total -eq 2) -and ([int]$a4.failed -eq 0) -and
                ($tests4.Count -eq 2) -and ($notPass4.Count -eq 0)
        }
        catch { $directXmlOk4 = $false }
    }
    Assert-True $directXmlOk4 "S4 direct native XML keeps real 2/0/0 PASS counts with two passing nodes"
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
    $started5 = $script:lastVerifyStartedUtc
    $summary5 = Get-Content -LiteralPath (Join-Path $log5 'summary.log') -Raw
    Assert-True ($exit5 -ne 0) "S5 exit code is non-zero for a zero-test project (got $exit5)"
    Assert-True ($summary5 -match 'OVERALL: FAIL') "S5 summary reports OVERALL: FAIL"
    $row5 = Assert-SummaryRow -LogDir $log5 -Runner 'dotnet test (MTP)' -Total 0 -Failed 0 -Skipped 0 `
        -Result 'FAIL' -AllowZeroTotal -Label "S5 zero-test row is a real FAIL with 0 total (never PASS)"
    Assert-True ($summary5 -match 'detail\s*:.*(native exit is 1|zero executed test cases)') "S5 zero-test diagnosis is present (current native-exit gate or the zero-test gate text)"
    $evidence5 = Get-DotnetTestEvidence -LogDir $log5 -RunStartedUtc $started5
    Assert-True ($evidence5.XmlOk -and $evidence5.Counts.Total -eq 0) "S5 native XML reports total=0 and no test nodes [$($evidence5.XmlReason)]"
    Assert-DotnetTestEvidence -LogDir $log5 -Runner 'dotnet test (MTP)' -Total 0 -Failed 0 -Skipped 0 `
        -Result 'FAIL' -NativeExit $exit5 -RunStartedUtc $started5 -AllowZeroTotal -Label "S5 summary, native XML, raw framework log and observed native exit agree that a zero-test run failed" | Out-Null

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
        $started6 = $script:lastVerifyStartedUtc
        $summary6 = Get-Content -LiteralPath (Join-Path $log6 'summary.log') -Raw
        Assert-True ($exit6 -eq 0) "S6 exit code is 0 even though the purge failed (got $exit6)"
        Assert-True ($summary6 -match 'OVERALL: PASS') "S6 summary reports OVERALL: PASS"
        $row6 = Assert-SummaryRow -LogDir $log6 -Runner 'dotnet test (MTP)' -Total 2 -Failed 0 -Skipped 0 `
            -Result 'PASS' -Label "S6 row still binds the MTP runner and real 2/0/0 PASS counts despite the purge failure"
        $evidence6 = Get-DotnetTestEvidence -LogDir $log6 -RunStartedUtc $started6
        Assert-True ($evidence6.RawLogOk -and $evidence6.RawLogText -match '(?m)^Test run summary: Passed!') "S6 current-run raw framework log is present under the run's dotnetTest-xml results root despite the purge failure [$($evidence6.RawLogReason)]"
        Assert-True ($evidence6.RawLogOk -and -not $evidence6.RawLogText.Contains($staleMarker)) "S6 accepted raw framework log is current-run evidence and carries no stale marker"
        $staleLeaked6 = $false
        $evidence6Dir = Join-Path $log6 'test-results'
        if (Test-Path -LiteralPath $evidence6Dir) {
            foreach ($file in (Get-ChildItem -LiteralPath $evidence6Dir -Recurse -File)) {
                $text = Get-Content -LiteralPath $file.FullName -Raw
                if ($text -and $text.Contains($staleMarker)) { $staleLeaked6 = $true }
            }
        }
        Assert-True (-not $staleLeaked6) "S6 locked stale content appears nowhere in the collected evidence"
        Assert-True (Test-Path -LiteralPath $lockedStalePath) "S6 the locked stale file really survived the purge (the failure is not simulated)"
        $meta6Text = Get-Content -LiteralPath (Join-Path $log6 'runs\Probe.Tests\dotnetTest.meta.txt') -Raw
        Assert-True ($meta6Text -match 'NOT CLEARED') "S6 meta records the purge failure"
        Assert-True ($meta6Text -match 'excluded stale files') "S6 meta records the excluded stale file(s)"
        Assert-DotnetTestEvidence -LogDir $log6 -Runner 'dotnet test (MTP)' -Total 2 -Failed 0 -Skipped 0 `
            -Result 'PASS' -NativeExit $exit6 -RunStartedUtc $started6 -Label "S6 summary, native XML, raw framework log and observed native exit agree on the PASS run" | Out-Null
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
        $started7 = $script:lastVerifyStartedUtc
        $summary7 = Get-Content -LiteralPath (Join-Path $log7 'summary.log') -Raw
        Assert-True ($exit7 -ne 0) "S7 exit code is non-zero (got $exit7)"
        Assert-True ($summary7 -match 'OVERALL: FAIL') "S7 summary reports OVERALL: FAIL"
        $row7 = Assert-SummaryRow -LogDir $log7 -Runner 'dotnet test (MTP)' -Total 2 -Failed 1 -Skipped 0 `
            -Result 'FAIL' -Label "S7 row binds the MTP runner and keeps real counts 2 total / 1 failed / 0 skipped as FAIL"
        $evidence7 = Get-DotnetTestEvidence -LogDir $log7 -RunStartedUtc $started7
        Assert-True ($evidence7.RawLogOk -and $evidence7.RawLogText.Contains($sentinel) -and $evidence7.RawLogText.Contains('Probe.Tests.ProbeTests.ControlledFailure')) "S7 current failing run's raw framework log carries the CURRENT case and failure content [$($evidence7.RawLogReason)]"
        Assert-True ($evidence7.RawLogOk -and -not $evidence7.RawLogText.Contains($staleMarker)) "S7 current raw framework log carries no stale marker"
        $staleLeaked7 = $false
        $evidence7Dir = Join-Path $log7 'test-results'
        if (Test-Path -LiteralPath $evidence7Dir) {
            foreach ($file in (Get-ChildItem -LiteralPath $evidence7Dir -Recurse -File)) {
                $text = Get-Content -LiteralPath $file.FullName -Raw
                if ($text -and $text.Contains($staleMarker)) { $staleLeaked7 = $true }
            }
        }
        Assert-True (-not $staleLeaked7) "S7 stale marker appears nowhere in the collected evidence (missing folder is not treated as a pass for the current failure)"
        $meta7Text = Get-Content -LiteralPath (Join-Path $log7 'runs\Probe.Tests\dotnetTest.meta.txt') -Raw
        Assert-True ($meta7Text -match 'NOT CLEARED' -and $meta7Text -match 'excluded stale files' -and $meta7Text -match 'Stale_OldRun') "S7 meta records the locked stale exclusion while keeping the current failure"
        Assert-DotnetTestEvidence -LogDir $log7 -Runner 'dotnet test (MTP)' -Total 2 -Failed 1 -Skipped 0 `
            -Result 'FAIL' -NativeExit $exit7 -RunStartedUtc $started7 -CaseName 'Probe.Tests.ProbeTests.ControlledFailure' -Message $sentinel `
            -Label "S7 summary, native XML, raw framework log, meta and observed native exit agree on the current failure" | Out-Null
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

    # --- TOOL_FIXTURE: negative matrix for the assertions themselves ----------------
    # These are test-assertion fixtures, not product test cases: they prove that
    # the row/evidence checks used by S1-S10 cannot be satisfied by a wrong
    # runner, wrong/zero counts, a fake PASS, missing current failure evidence,
    # a success summary next to a non-zero native exit, or incomplete XML.
    Write-Host ""
    Write-Host "TOOL_FIXTURE: negative matrix - row and evidence assertions reject false green"
    $negRoot = Join-Path $workspace 'negative-fixtures'
    New-Item -ItemType Directory -Force -Path $negRoot | Out-Null
    $negHeader = "{0,-34} {1,-7} {2,-4} {3,-6} {4,-19} {5,6} {6,7} {7,8} {8,-8} {9}" -f @('PROJECT', 'KIND', 'REQ', 'INSLN', 'RUNNER', 'TOTAL', 'FAILED', 'SKIPPED', 'RESULT', 'LOG')
    $makeNegSummary = {
        param($Project, $Runner, $Total, $Failed, $Skipped, $Result, $Repeat = 1)
        $row = "{0,-34} {1,-7} {2,-4} {3,-6} {4,-19} {5,6} {6,7} {7,8} {8,-8} {9}" -f @($Project, 'test', '-', 'NO!', $Runner, $Total, $Failed, $Skipped, $Result, 'dotnetTest-Probe.Tests.log')
        $body = ''
        for ($i = 0; $i -lt $Repeat; $i++) { $body += ($row + "`n") }
        return ($negHeader + "`n" + $body + "OVERALL: FAIL`n")
    }

    $negAccepted = & $makeNegSummary 'Probe.Tests' 'dotnet test (MTP)' 2 1 0 'FAIL'
    $r = Test-SummaryRow -Text $negAccepted -Runner 'dotnet test (MTP)' -Total 2 -Failed 1 -Skipped 0 -Result 'FAIL'
    Assert-True $r.Ok "TOOL_FIXTURE baseline: exact MTP row 2/1/0 FAIL is accepted [$($r.Reason)]"
    $r = Test-SummaryRow -Text (& $makeNegSummary 'Probe.Tests' 'dotnet test' 2 1 0 'FAIL') -Runner 'dotnet test (MTP)' -Total 2 -Failed 1 -Skipped 0 -Result 'FAIL'
    Assert-True (-not $r.Ok) "TOOL_FIXTURE reject old runner 'dotnet test' without (MTP) [$($r.Reason)]"
    $r = Test-SummaryRow -Text (& $makeNegSummary 'Probe.Tests' 'in-process (direct)' 2 1 0 'FAIL') -Runner 'dotnet test (MTP)' -Total 2 -Failed 1 -Skipped 0 -Result 'FAIL'
    Assert-True (-not $r.Ok) "TOOL_FIXTURE reject a direct runner row when dotnetTest(MTP) is expected [$($r.Reason)]"
    $r = Test-SummaryRow -Text (& $makeNegSummary 'Probe.Tests' 'dotnet test (MTP)' 3 1 0 'FAIL') -Runner 'dotnet test (MTP)' -Total 2 -Failed 1 -Skipped 0 -Result 'FAIL'
    Assert-True (-not $r.Ok) "TOOL_FIXTURE reject wrong total 3 when 2 is expected [$($r.Reason)]"
    $r = Test-SummaryRow -Text (& $makeNegSummary 'Probe.Tests' 'dotnet test (MTP)' 2 0 0 'FAIL') -Runner 'dotnet test (MTP)' -Total 2 -Failed 1 -Skipped 0 -Result 'FAIL'
    Assert-True (-not $r.Ok) "TOOL_FIXTURE reject wrong failed count 0 when 1 is expected [$($r.Reason)]"
    $r = Test-SummaryRow -Text (& $makeNegSummary 'Probe.Tests' 'dotnet test (MTP)' 2 1 1 'FAIL') -Runner 'dotnet test (MTP)' -Total 2 -Failed 1 -Skipped 0 -Result 'FAIL'
    Assert-True (-not $r.Ok) "TOOL_FIXTURE reject wrong skipped count 1 when 0 is expected [$($r.Reason)]"
    $r = Test-SummaryRow -Text (& $makeNegSummary 'Probe.Tests' 'dotnet test (MTP)' 0 0 0 'PASS') -Runner 'dotnet test (MTP)' -Total 0 -Failed 0 -Skipped 0 -Result 'PASS' -AllowZeroTotal
    Assert-True (-not $r.Ok) "TOOL_FIXTURE reject total=0 as PASS [$($r.Reason)]"
    $r = Test-SummaryRow -Text (& $makeNegSummary 'Probe.Tests' 'dotnet test (MTP)' 2 1 0 'PASS') -Runner 'dotnet test (MTP)' -Total 2 -Failed 1 -Skipped 0 -Result 'PASS'
    Assert-True (-not $r.Ok) "TOOL_FIXTURE reject a fake PASS row that still reports failed=1 [$($r.Reason)]"
    $r = Test-SummaryRow -Text (& $makeNegSummary 'Other.Tests' 'dotnet test (MTP)' 2 1 0 'FAIL') -Runner 'dotnet test (MTP)' -Total 2 -Failed 1 -Skipped 0 -Result 'FAIL'
    Assert-True (-not $r.Ok) "TOOL_FIXTURE reject another project's row [$($r.Reason)]"
    $r = Test-SummaryRow -Text (& $makeNegSummary 'Probe.Tests' 'dotnet test (MTP)' 2 1 0 'FAIL' 2) -Runner 'dotnet test (MTP)' -Total 2 -Failed 1 -Skipped 0 -Result 'FAIL'
    Assert-True (-not $r.Ok) "TOOL_FIXTURE reject ambiguous duplicate Probe.Tests rows [$($r.Reason)]"

    $negAssemblyPath = ((Join-Path $probeDir 'bin\Release\net8.0\Probe.Tests.dll') -replace '\\', '/')
    $negXmlFail = @"
<assemblies schema-version="3">
  <assembly name="$negAssemblyPath" test-framework="xUnit.net v3 4.0.2+0c7aa4991f" total="2" passed="1" failed="1" skipped="0" not-run="0" errors="0">
    <collection name="Test collection for Probe.Tests.ProbeTests" total="2" passed="1" failed="1" skipped="0" not-run="0">
      <test id="11111111-1111-1111-1111-111111111111" name="Probe.Tests.ProbeTests.ControlledFailure" result="Fail"><failure exception-type="Xunit.Sdk.FailException"><message>DIAGNOSTIC-PROBE-SENTINEL failing assertion</message></failure></test>
      <test id="22222222-2222-2222-2222-222222222222" name="Probe.Tests.ProbeTests.AlwaysPasses" result="Pass" />
    </collection>
  </assembly>
</assemblies>
"@
    $negXmlWrongCase = @"
<assemblies schema-version="3">
  <assembly name="$negAssemblyPath" test-framework="xUnit.net v3 4.0.2+0c7aa4991f" total="2" passed="1" failed="1" skipped="0" not-run="0" errors="0">
    <collection name="Test collection for Probe.Tests.ProbeTests" total="2" passed="1" failed="1" skipped="0" not-run="0">
      <test id="11111111-1111-1111-1111-111111111111" name="Probe.Tests.ProbeTests.SomeOtherCase" result="Fail"><failure exception-type="Xunit.Sdk.FailException"><message>DIAGNOSTIC-PROBE-SENTINEL failing assertion</message></failure></test>
      <test id="22222222-2222-2222-2222-222222222222" name="Probe.Tests.ProbeTests.AlwaysPasses" result="Pass" />
    </collection>
  </assembly>
</assemblies>
"@
    $negXmlPass = @"
<assemblies schema-version="3">
  <assembly name="$negAssemblyPath" test-framework="xUnit.net v3 4.0.2+0c7aa4991f" total="2" passed="2" failed="0" skipped="0" not-run="0" errors="0">
    <collection name="Test collection for Probe.Tests.ProbeTests" total="2" passed="2" failed="0" skipped="0" not-run="0">
      <test id="11111111-1111-1111-1111-111111111111" name="Probe.Tests.ProbeTests.AlwaysPasses" result="Pass" />
      <test id="22222222-2222-2222-2222-222222222222" name="Probe.Tests.ProbeTests.SecondPasses" result="Pass" />
    </collection>
  </assembly>
</assemblies>
"@
    $negXmlIncomplete = @"
<assemblies schema-version="3">
  <assembly name="$negAssemblyPath" test-framework="xUnit.net v3 4.0.2+0c7aa4991f" total="2" passed="1" failed="1" skipped="0" not-run="0" errors="0">
    <collection name="Test collection for Probe.Tests.ProbeTests" total="2" passed="1" failed="1" skipped="0" not-run="0">
      <test id="11111111-1111-1111-1111-111111111111" name="Probe.Tests.ProbeTests.ControlledFailure" result="Fail"><failure exception-type="Xunit.Sdk.FailException"><message>DIAGNOSTIC-PROBE-SENTINEL failing assertion</message></failure></test>
    </collection>
  </assembly>
</assemblies>
"@
    $negXmlTwoFailNodes = @"
<assemblies schema-version="3">
  <assembly name="$negAssemblyPath" test-framework="xUnit.net v3 4.0.2+0c7aa4991f" total="2" passed="1" failed="1" skipped="0" not-run="0" errors="0">
    <collection name="Test collection for Probe.Tests.ProbeTests" total="2" passed="1" failed="1" skipped="0" not-run="0">
      <test id="11111111-1111-1111-1111-111111111111" name="Probe.Tests.ProbeTests.ControlledFailure" result="Fail"><failure exception-type="Xunit.Sdk.FailException"><message>DIAGNOSTIC-PROBE-SENTINEL failing assertion</message></failure></test>
      <test id="22222222-2222-2222-2222-222222222222" name="Probe.Tests.ProbeTests.AlwaysPasses" result="Fail"><failure exception-type="Xunit.Sdk.FailException"><message>SECOND_ACTUAL_FAILURE</message></failure></test>
    </collection>
  </assembly>
</assemblies>
"@
    $negXmlDuplicateIds = $negXmlPass.Replace('id="22222222-2222-2222-2222-222222222222"', 'id="11111111-1111-1111-1111-111111111111"')
    $negXmlMissingAttrs = @"
<assemblies schema-version="3">
  <assembly name="$negAssemblyPath" test-framework="xUnit.net v3 4.0.2+0c7aa4991f" total="2" passed="2">
    <collection name="Test collection for Probe.Tests.ProbeTests" total="2" passed="2" failed="0" skipped="0" not-run="0">
      <test id="11111111-1111-1111-1111-111111111111" name="Probe.Tests.ProbeTests.AlwaysPasses" result="Pass" />
      <test id="22222222-2222-2222-2222-222222222222" name="Probe.Tests.ProbeTests.SecondPasses" result="Pass" />
    </collection>
  </assembly>
</assemblies>
"@
    $negXmlWrongAssembly = $negXmlPass.Replace($negAssemblyPath, 'Other.Tests.dll')
    $negXmlWrongFramework = $negXmlPass.Replace('xUnit.net v3 4.0.2+0c7aa4991f', 'xUnit.net v3 4.0.20')
    $negXmlWrongSchema = $negXmlPass.Replace('schema-version="3"', 'schema-version="999"')
    $negXmlMissingCollection = $negXmlPass -replace '(?s)<collection\b.*?</collection>', ''
    $negRawFail = "failed Probe.Tests.ProbeTests.ControlledFailure`n  DIAGNOSTIC-PROBE-SENTINEL failing assertion`nTest run summary: Failed!`n  total: 2`n  failed: 1`n  skipped: 0`n"
    $negRawPass = "Test run summary: Passed!`n  total: 2`n  failed: 0`n  skipped: 0`n"

    function Write-NegEvidence {
        param($Name, $Summary, $Xml, $Raw, $Exit, $Total, $Failed, $Skipped)
        $dir = Join-Path $negRoot $Name
        New-Item -ItemType Directory -Force -Path (Join-Path $dir 'dotnetTest-xml\Probe.Tests'), (Join-Path $dir 'runs\Probe.Tests') | Out-Null
        Write-Utf8File -Path (Join-Path $dir 'summary.log') -Content $Summary
        if ($null -ne $Xml) { Write-Utf8File -Path (Join-Path $dir 'dotnetTest-xml\Probe.Tests\dotnet-test-results.xml') -Content $Xml }
        if ($null -ne $Raw) {
            $artifactPath = Join-Path $dir 'dotnetTest-xml\Probe.Tests\dotnet-test-results.xml'
            $rawWithContext = "  In process file artifacts produced:`n    - $artifactPath`n" + $Raw
            Write-Utf8File -Path (Join-Path $dir 'dotnetTest-xml\Probe.Tests\Probe.Tests_net8.0_x64.log') -Content $rawWithContext
        }
        $meta = "exit code   : $Exit`ntotal       : $Total`nfailed      : $Failed`nskipped     : $Skipped`nhead        : deadbeef`n"
        Write-Utf8File -Path (Join-Path $dir 'runs\Probe.Tests\dotnetTest.meta.txt') -Content $meta
        return $dir
    }
    function Set-StaleRawEvidence {
        param([string]$Dir)
        $p = Join-Path $Dir 'dotnetTest-xml\Probe.Tests\Probe.Tests_net8.0_x64.log'
        $text = Get-Content -LiteralPath $p -Raw
        [IO.File]::WriteAllText($p, ($staleMarker + [Environment]::NewLine + $text), (New-Object System.Text.UTF8Encoding($false)))
        (Get-Item -LiteralPath $p).LastWriteTimeUtc = [DateTime]::UtcNow.AddDays(-2)
    }
    function Assert-NegEvidence-Rejected {
        param($Dir, $Summary, $Runner, $Total, $Failed, $Skipped, $Result, $Exit, $CaseName, $Message, $Label, [switch]$AllowZeroTotal)
        $started = [DateTime]::UtcNow
        $r = Test-DotnetTestEvidence -LogDir $Dir -SummaryText $Summary -Runner $Runner -Total $Total -Failed $Failed `
            -Skipped $Skipped -Result $Result -NativeExit $Exit -RunStartedUtc $started -CaseName $CaseName -Message $Message -AllowZeroTotal:$AllowZeroTotal
        Assert-True (-not $r.Ok) "TOOL_FIXTURE $Label [$($r.Reason)]"
    }

    $negSummaryFail = & $makeNegSummary 'Probe.Tests' 'dotnet test (MTP)' 2 1 0 'FAIL'
    $negSummaryPass = & $makeNegSummary 'Probe.Tests' 'dotnet test (MTP)' 2 0 0 'PASS'
    $negGood = Write-NegEvidence -Name 'baseline' -Summary $negSummaryFail -Xml $negXmlFail -Raw $negRawFail -Exit 1 -Total 2 -Failed 1 -Skipped 0
    $negGoodStart = [DateTime]::UtcNow
    $r = Test-DotnetTestEvidence -LogDir $negGood -SummaryText $negSummaryFail -Runner 'dotnet test (MTP)' -Total 2 -Failed 1 -Skipped 0 -Result 'FAIL' -NativeExit 1 -RunStartedUtc $negGoodStart -CaseName 'Probe.Tests.ProbeTests.ControlledFailure' -Message $sentinel
    Assert-True $r.Ok "TOOL_FIXTURE baseline: consistent native-shaped row/XML/raw/meta is accepted [$($r.Reason)]"
    $d = Write-NegEvidence -Name 'summary-pass-xml-fail' -Summary $negSummaryPass -Xml $negXmlFail -Raw $negRawFail -Exit 0 -Total 2 -Failed 0 -Skipped 0
    Assert-NegEvidence-Rejected -Dir $d -Summary $negSummaryPass -Runner 'dotnet test (MTP)' -Total 2 -Failed 0 -Skipped 0 -Result 'PASS' -Exit 0 -Label 'reject a PASS summary whose XML still reports a failure'
    $d = Write-NegEvidence -Name 'nonzero-exit-success' -Summary $negSummaryPass -Xml $negXmlPass -Raw $negRawPass -Exit 1 -Total 2 -Failed 0 -Skipped 0
    Assert-NegEvidence-Rejected -Dir $d -Summary $negSummaryPass -Runner 'dotnet test (MTP)' -Total 2 -Failed 0 -Skipped 0 -Result 'PASS' -Exit 1 -Label 'reject a success summary with native exit 1'
    $d = Write-NegEvidence -Name 'native-zero-fail' -Summary $negSummaryFail -Xml $negXmlFail -Raw $negRawFail -Exit 0 -Total 2 -Failed 1 -Skipped 0
    Assert-NegEvidence-Rejected -Dir $d -Summary $negSummaryFail -Runner 'dotnet test (MTP)' -Total 2 -Failed 1 -Skipped 0 -Result 'FAIL' -Exit 0 -CaseName 'Probe.Tests.ProbeTests.ControlledFailure' -Message $sentinel -Label 'reject a FAIL row whose native exit is 0'
    $d = Write-NegEvidence -Name 'missing-case' -Summary $negSummaryFail -Xml $negXmlWrongCase -Raw $negRawFail -Exit 1 -Total 2 -Failed 1 -Skipped 0
    Assert-NegEvidence-Rejected -Dir $d -Summary $negSummaryFail -Runner 'dotnet test (MTP)' -Total 2 -Failed 1 -Skipped 0 -Result 'FAIL' -Exit 1 -CaseName 'Probe.Tests.ProbeTests.ControlledFailure' -Message $sentinel -Label 'reject failure evidence whose XML lacks the failing case'
    $d = Write-NegEvidence -Name 'missing-assertion' -Summary $negSummaryFail -Xml $negXmlFail.Replace('DIAGNOSTIC-PROBE-SENTINEL failing assertion', 'different assertion') -Raw $negRawFail -Exit 1 -Total 2 -Failed 1 -Skipped 0
    Assert-NegEvidence-Rejected -Dir $d -Summary $negSummaryFail -Runner 'dotnet test (MTP)' -Total 2 -Failed 1 -Skipped 0 -Result 'FAIL' -Exit 1 -CaseName 'Probe.Tests.ProbeTests.ControlledFailure' -Message $sentinel -Label 'reject failure evidence whose XML lacks the assertion message'
    $d = Write-NegEvidence -Name 'raw-missing-case' -Summary $negSummaryFail -Xml $negXmlFail -Raw $negRawFail.Replace('Probe.Tests.ProbeTests.ControlledFailure', 'Probe.Tests.ProbeTests.SomeOtherCase') -Exit 1 -Total 2 -Failed 1 -Skipped 0
    Assert-NegEvidence-Rejected -Dir $d -Summary $negSummaryFail -Runner 'dotnet test (MTP)' -Total 2 -Failed 1 -Skipped 0 -Result 'FAIL' -Exit 1 -CaseName 'Probe.Tests.ProbeTests.ControlledFailure' -Message $sentinel -Label 'reject a raw log that does not name the current failing case'
    $d = Write-NegEvidence -Name 'raw-missing-message' -Summary $negSummaryFail -Xml $negXmlFail -Raw $negRawFail.Replace('DIAGNOSTIC-PROBE-SENTINEL failing assertion', 'different assertion') -Exit 1 -Total 2 -Failed 1 -Skipped 0
    Assert-NegEvidence-Rejected -Dir $d -Summary $negSummaryFail -Runner 'dotnet test (MTP)' -Total 2 -Failed 1 -Skipped 0 -Result 'FAIL' -Exit 1 -CaseName 'Probe.Tests.ProbeTests.ControlledFailure' -Message $sentinel -Label 'reject a raw log that lacks the assertion message'
    $d = Write-NegEvidence -Name 'raw-missing' -Summary $negSummaryFail -Xml $negXmlFail -Raw $null -Exit 1 -Total 2 -Failed 1 -Skipped 0
    Assert-NegEvidence-Rejected -Dir $d -Summary $negSummaryFail -Runner 'dotnet test (MTP)' -Total 2 -Failed 1 -Skipped 0 -Result 'FAIL' -Exit 1 -CaseName 'Probe.Tests.ProbeTests.ControlledFailure' -Message $sentinel -Label 'reject a missing raw framework log'
    $d = Write-NegEvidence -Name 'xml-missing' -Summary $negSummaryFail -Xml $null -Raw $negRawFail -Exit 1 -Total 2 -Failed 1 -Skipped 0
    Assert-NegEvidence-Rejected -Dir $d -Summary $negSummaryFail -Runner 'dotnet test (MTP)' -Total 2 -Failed 1 -Skipped 0 -Result 'FAIL' -Exit 1 -CaseName 'Probe.Tests.ProbeTests.ControlledFailure' -Message $sentinel -Label 'reject missing native XML (invocation exception/incomplete result)'
    $d = Write-NegEvidence -Name 'xml-malformed' -Summary $negSummaryFail -Xml '<assemblies' -Raw $negRawFail -Exit 1 -Total 2 -Failed 1 -Skipped 0
    Assert-NegEvidence-Rejected -Dir $d -Summary $negSummaryFail -Runner 'dotnet test (MTP)' -Total 2 -Failed 1 -Skipped 0 -Result 'FAIL' -Exit 1 -CaseName 'Probe.Tests.ProbeTests.ControlledFailure' -Message $sentinel -Label 'reject malformed native XML'
    $d = Write-NegEvidence -Name 'xml-incomplete' -Summary $negSummaryFail -Xml $negXmlIncomplete -Raw $negRawFail -Exit 1 -Total 2 -Failed 1 -Skipped 0
    Assert-NegEvidence-Rejected -Dir $d -Summary $negSummaryFail -Runner 'dotnet test (MTP)' -Total 2 -Failed 1 -Skipped 0 -Result 'FAIL' -Exit 1 -CaseName 'Probe.Tests.ProbeTests.ControlledFailure' -Message $sentinel -Label 'reject XML whose node count does not match total'

    $negGoodPass = Write-NegEvidence -Name 'baseline-pass' -Summary $negSummaryPass -Xml $negXmlPass -Raw $negRawPass -Exit 0 -Total 2 -Failed 0 -Skipped 0
    $r = Test-DotnetTestEvidence -LogDir $negGoodPass -SummaryText $negSummaryPass -Runner 'dotnet test (MTP)' -Total 2 -Failed 0 -Skipped 0 -Result 'PASS' -NativeExit 0 -RunStartedUtc ([DateTime]::UtcNow)
    Assert-True $r.Ok "TOOL_FIXTURE baseline PASS: consistent native-shaped row/XML/raw/meta is accepted [$($r.Reason)]"
    $d = Write-NegEvidence -Name 'pass-native-null' -Summary $negSummaryPass -Xml $negXmlPass -Raw $negRawPass -Exit $null -Total 2 -Failed 0 -Skipped 0
    Assert-NegEvidence-Rejected -Dir $d -Summary $negSummaryPass -Runner 'dotnet test (MTP)' -Total 2 -Failed 0 -Skipped 0 -Result 'PASS' -Exit $null -Label 'reject an explicitly unknown (null) native exit even when row/XML/raw look successful'
    $d = Write-NegEvidence -Name 'pass-native-bool' -Summary $negSummaryPass -Xml $negXmlPass -Raw $negRawPass -Exit $false -Total 2 -Failed 0 -Skipped 0
    Assert-NegEvidence-Rejected -Dir $d -Summary $negSummaryPass -Runner 'dotnet test (MTP)' -Total 2 -Failed 0 -Skipped 0 -Result 'PASS' -Exit $false -Label 'reject a Boolean native observation instead of an integral exit'
    $d = Write-NegEvidence -Name 'pass-native-fraction' -Summary $negSummaryPass -Xml $negXmlPass -Raw $negRawPass -Exit 0.1 -Total 2 -Failed 0 -Skipped 0
    Assert-NegEvidence-Rejected -Dir $d -Summary $negSummaryPass -Runner 'dotnet test (MTP)' -Total 2 -Failed 0 -Skipped 0 -Result 'PASS' -Exit 0.1 -Label 'reject a fractional native observation instead of an integral exit'
    $d = Write-NegEvidence -Name 'fail-two-failed-nodes' -Summary $negSummaryFail -Xml $negXmlTwoFailNodes -Raw $negRawFail -Exit 1 -Total 2 -Failed 1 -Skipped 0
    Assert-NegEvidence-Rejected -Dir $d -Summary $negSummaryFail -Runner 'dotnet test (MTP)' -Total 2 -Failed 1 -Skipped 0 -Result 'FAIL' -Exit 1 -CaseName 'Probe.Tests.ProbeTests.ControlledFailure' -Message $sentinel -Label 'reject two actual Fail nodes when the aggregate declares one failure'
    $d = Write-NegEvidence -Name 'duplicate-test-ids' -Summary $negSummaryPass -Xml $negXmlDuplicateIds -Raw $negRawPass -Exit 0 -Total 2 -Failed 0 -Skipped 0
    Assert-NegEvidence-Rejected -Dir $d -Summary $negSummaryPass -Runner 'dotnet test (MTP)' -Total 2 -Failed 0 -Skipped 0 -Result 'PASS' -Exit 0 -Label 'reject duplicate native test IDs'
    $d = Write-NegEvidence -Name 'missing-count-attributes' -Summary $negSummaryPass -Xml $negXmlMissingAttrs -Raw $negRawPass -Exit 0 -Total 2 -Failed 0 -Skipped 0
    Assert-NegEvidence-Rejected -Dir $d -Summary $negSummaryPass -Runner 'dotnet test (MTP)' -Total 2 -Failed 0 -Skipped 0 -Result 'PASS' -Exit 0 -Label 'reject missing required failed/errors/skipped/not-run attributes'
    $d = Write-NegEvidence -Name 'wrong-assembly' -Summary $negSummaryPass -Xml $negXmlWrongAssembly -Raw $negRawPass -Exit 0 -Total 2 -Failed 0 -Skipped 0
    Assert-NegEvidence-Rejected -Dir $d -Summary $negSummaryPass -Runner 'dotnet test (MTP)' -Total 2 -Failed 0 -Skipped 0 -Result 'PASS' -Exit 0 -Label 'reject a wrong assembly identity'
    $d = Write-NegEvidence -Name 'wrong-framework' -Summary $negSummaryPass -Xml $negXmlWrongFramework -Raw $negRawPass -Exit 0 -Total 2 -Failed 0 -Skipped 0
    Assert-NegEvidence-Rejected -Dir $d -Summary $negSummaryPass -Runner 'dotnet test (MTP)' -Total 2 -Failed 0 -Skipped 0 -Result 'PASS' -Exit 0 -Label 'reject a fourth-component xUnit runner identity (4.0.20)'
    $d = Write-NegEvidence -Name 'wrong-schema' -Summary $negSummaryPass -Xml $negXmlWrongSchema -Raw $negRawPass -Exit 0 -Total 2 -Failed 0 -Skipped 0
    Assert-NegEvidence-Rejected -Dir $d -Summary $negSummaryPass -Runner 'dotnet test (MTP)' -Total 2 -Failed 0 -Skipped 0 -Result 'PASS' -Exit 0 -Label 'reject an unsupported native XML schema 999'
    $d = Write-NegEvidence -Name 'missing-collection' -Summary $negSummaryPass -Xml $negXmlMissingCollection -Raw $negRawPass -Exit 0 -Total 2 -Failed 0 -Skipped 0
    Assert-NegEvidence-Rejected -Dir $d -Summary $negSummaryPass -Runner 'dotnet test (MTP)' -Total 2 -Failed 0 -Skipped 0 -Result 'PASS' -Exit 0 -Label 'reject test nodes without the required collection aggregate'
    $d = Write-NegEvidence -Name 'fail-stale-raw' -Summary $negSummaryFail -Xml $negXmlFail -Raw $negRawFail -Exit 1 -Total 2 -Failed 1 -Skipped 0
    Set-StaleRawEvidence -Dir $d
    Assert-NegEvidence-Rejected -Dir $d -Summary $negSummaryFail -Runner 'dotnet test (MTP)' -Total 2 -Failed 1 -Skipped 0 -Result 'FAIL' -Exit 1 -CaseName 'Probe.Tests.ProbeTests.ControlledFailure' -Message $sentinel -Label 'reject an old marked raw log even when its case/assertion/counts match'
    $d = Write-NegEvidence -Name 'pass-stale-raw' -Summary $negSummaryPass -Xml $negXmlPass -Raw $negRawPass -Exit 0 -Total 2 -Failed 0 -Skipped 0
    Set-StaleRawEvidence -Dir $d
    Assert-NegEvidence-Rejected -Dir $d -Summary $negSummaryPass -Runner 'dotnet test (MTP)' -Total 2 -Failed 0 -Skipped 0 -Result 'PASS' -Exit 0 -Label 'reject an old marked raw log on a PASS row'

    # --- S9: EXCLUSIVE lock (FileShare.None) released mid-run + current PASS --------
    # Round-2-review regression: the snapshot cannot hash an exclusively locked
    # file, but once the lock is released mid-run the collection re-hash succeeds
    # and a naive hash compare would collect the OLD file as current evidence.
    Write-Host ""
    Write-Host "S9: exclusively locked OLD log, lock released mid-run - hash-blind snapshot must exclude conservatively (PASS run)"
    Set-ProbeSource $passSource
    Seed-StaleLog
    $sentinelFile9 = Join-Path $probeDir 'bin\Release\net8.0\TestResults\Stale_UnlockedSentinel.tmp'
    Write-Utf8File -Path $sentinelFile9 -Content 'unlocked sentinel the purge can delete'
    $exclusiveStalePath9 = Join-Path $probeDir 'bin\Release\net8.0\TestResults\Stale_OldRun_net8.0_x64.log'
    $exclusiveStream9 = [IO.File]::Open($exclusiveStalePath9, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    $log9 = Join-Path $workspace 'logs-s9'
    $unlockBox9 = @{ Released = $false; PurgedObserved = $false }
    $unlocker9 = Start-MidRunUnlocker -LockStream $exclusiveStream9 -UnlockedSentinel $sentinelFile9 -PurgeFailureMarker (Join-Path $log9 'collection-errors.log') -ResultBox $unlockBox9
    try {
        $exit9 = Invoke-Verify -RunnerMode 'dotnetTest' -LogDir $log9
        $started9 = $script:lastVerifyStartedUtc
        $null = Wait-UnlockerReleased $unlockBox9
        $summary9 = Get-Content -LiteralPath (Join-Path $log9 'summary.log') -Raw
        Assert-True $unlockBox9.PurgedObserved "S9 scenario valid: the purge was observed before the lock was released"
        Assert-True ($exit9 -eq 0) "S9 exit code is 0 even with the exclusive lock released mid-run (got $exit9)"
        Assert-True ($summary9 -match 'OVERALL: PASS') "S9 summary reports OVERALL: PASS"
        $row9 = Assert-SummaryRow -LogDir $log9 -Runner 'dotnet test (MTP)' -Total 2 -Failed 0 -Skipped 0 `
            -Result 'PASS' -Label "S9 row binds the MTP runner and real 2/0/0 PASS counts"
        $evidence9 = Get-DotnetTestEvidence -LogDir $log9 -RunStartedUtc $started9
        Assert-True ($evidence9.RawLogOk -and $evidence9.RawLogText -match '(?m)^Test run summary: Passed!') "S9 current-run raw framework log is present under the run's dotnetTest-xml results root [$($evidence9.RawLogReason)]"
        Assert-True ($evidence9.RawLogOk -and -not $evidence9.RawLogText.Contains($staleMarker)) "S9 accepted raw framework log is current-run evidence and carries no stale marker"
        $staleLeaked9 = $false
        $evidence9Dir = Join-Path $log9 'test-results'
        if (Test-Path -LiteralPath $evidence9Dir) {
            foreach ($file in (Get-ChildItem -LiteralPath $evidence9Dir -Recurse -File)) {
                $text = Get-Content -LiteralPath $file.FullName -Raw
                if ($text -and $text.Contains($staleMarker)) { $staleLeaked9 = $true }
            }
        }
        Assert-True (-not $staleLeaked9) "S9 exclusively-locked OLD content appears nowhere in the collected evidence"
        $meta9Text = Get-Content -LiteralPath (Join-Path $log9 'runs\Probe.Tests\dotnetTest.meta.txt') -Raw
        Assert-True ($meta9Text -match 'NOT CLEARED') "S9 meta records the purge failure"
        Assert-True ($meta9Text -match 'hash unavailable at snapshot time') "S9 meta records the conservative exclusion reason"
        Assert-DotnetTestEvidence -LogDir $log9 -Runner 'dotnet test (MTP)' -Total 2 -Failed 0 -Skipped 0 `
            -Result 'PASS' -NativeExit $exit9 -RunStartedUtc $started9 -Label "S9 summary, native XML, raw framework log and observed native exit agree on the PASS run" | Out-Null
    }
    finally {
        Stop-MidRunUnlocker $unlocker9
        $exclusiveStream9.Dispose()
    }

    # --- S10: EXCLUSIVE lock released mid-run + current FAIL -------------------------
    Write-Host ""
    Write-Host "S10: exclusively locked OLD log, lock released mid-run + current failing run"
    Set-ProbeSource $failSource
    Seed-StaleLog
    $sentinelFile10 = Join-Path $probeDir 'bin\Release\net8.0\TestResults\Stale_UnlockedSentinel.tmp'
    Write-Utf8File -Path $sentinelFile10 -Content 'unlocked sentinel the purge can delete'
    $exclusiveStream10 = [IO.File]::Open($exclusiveStalePath9, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    $log10 = Join-Path $workspace 'logs-s10'
    $unlockBox10 = @{ Released = $false; PurgedObserved = $false }
    $unlocker10 = Start-MidRunUnlocker -LockStream $exclusiveStream10 -UnlockedSentinel $sentinelFile10 -PurgeFailureMarker (Join-Path $log10 'collection-errors.log') -ResultBox $unlockBox10
    try {
        $exit10 = Invoke-Verify -RunnerMode 'dotnetTest' -LogDir $log10
        $started10 = $script:lastVerifyStartedUtc
        $null = Wait-UnlockerReleased $unlockBox10
        $summary10 = Get-Content -LiteralPath (Join-Path $log10 'summary.log') -Raw
        Assert-True $unlockBox10.PurgedObserved "S10 scenario valid: the purge was observed before the lock was released"
        Assert-True ($exit10 -ne 0) "S10 exit code is non-zero (got $exit10)"
        Assert-True ($summary10 -match 'OVERALL: FAIL') "S10 summary reports OVERALL: FAIL"
        $row10 = Assert-SummaryRow -LogDir $log10 -Runner 'dotnet test (MTP)' -Total 2 -Failed 1 -Skipped 0 `
            -Result 'FAIL' -Label "S10 row binds the MTP runner and keeps real counts 2 total / 1 failed / 0 skipped as FAIL"
        $evidence10 = Get-DotnetTestEvidence -LogDir $log10 -RunStartedUtc $started10
        Assert-True ($evidence10.RawLogOk -and $evidence10.RawLogText.Contains($sentinel) -and $evidence10.RawLogText.Contains('Probe.Tests.ProbeTests.ControlledFailure')) "S10 current failing run's raw framework log carries the CURRENT failure content [$($evidence10.RawLogReason)]"
        Assert-True ($evidence10.RawLogOk -and -not $evidence10.RawLogText.Contains($staleMarker)) "S10 current raw framework log carries no stale marker"
        $staleLeaked10 = $false
        $evidence10Dir = Join-Path $log10 'test-results'
        if (Test-Path -LiteralPath $evidence10Dir) {
            foreach ($file in (Get-ChildItem -LiteralPath $evidence10Dir -Recurse -File)) {
                $text = Get-Content -LiteralPath $file.FullName -Raw
                if ($text -and $text.Contains($staleMarker)) { $staleLeaked10 = $true }
            }
        }
        Assert-True (-not $staleLeaked10) "S10 exclusively-locked OLD content appears nowhere in the collected evidence"
        $meta10Text = Get-Content -LiteralPath (Join-Path $log10 'runs\Probe.Tests\dotnetTest.meta.txt') -Raw
        Assert-True ($meta10Text -match 'NOT CLEARED' -and $meta10Text -match 'hash unavailable at snapshot time') "S10 meta records the purge failure and conservative hash exclusion"
        Assert-DotnetTestEvidence -LogDir $log10 -Runner 'dotnet test (MTP)' -Total 2 -Failed 1 -Skipped 0 `
            -Result 'FAIL' -NativeExit $exit10 -RunStartedUtc $started10 -CaseName 'Probe.Tests.ProbeTests.ControlledFailure' -Message $sentinel `
            -Label "S10 summary, native XML, raw framework log, meta and observed native exit agree on the current failure" | Out-Null
    }
    finally {
        Stop-MidRunUnlocker $unlocker10
        $exclusiveStream10.Dispose()
    }

    # --- S11: fault-injected snapshot enumeration failure (unit level) ---------------
    # Distinct from the real-lock evidence above: here the before-snapshot
    # enumeration itself fails (injected) while the purge also fails (real
    # FileShare.None lock). The ENTIRE untrusted directory must be excluded.
    Write-Host ""
    Write-Host "S11: unit - snapshot enumeration failure excludes the whole untrusted directory"
    $parseErrors11 = $null
    $tokens11 = $null
    $ast11 = [System.Management.Automation.Language.Parser]::ParseFile($verifyScript, [ref]$tokens11, [ref]$parseErrors11)
    foreach ($fnName11 in @('Get-TestResultsSnapshot', 'Clear-TestResultsDirs', 'Save-TestResults')) {
        $fnAst11 = $ast11.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $fnName11 }.GetNewClosure(), $true)
        if ($null -ne $fnAst11) { Invoke-Expression $fnAst11.Extent.Text }
        Assert-True ($null -ne $fnAst11) "S11 extracted $fnName11 from the production script AST"
    }

    $unitLog11 = Join-Path $workspace 'unit-s11'
    $unitFixture11 = Join-Path $unitLog11 'fixture'
    $unitResults11 = Join-Path $unitFixture11 'TestResults'
    New-Item -ItemType Directory -Path $unitResults11 -Force | Out-Null
    $oldLog11 = Join-Path $unitResults11 'old-run.log'
    Write-Utf8File -Path $oldLog11 -Content 'OLD-RUN-FAILURE-ONLY-SNAPSHOT-BLIND'

    # Proxy: the FIRST Get-ChildItem call (the before-snapshot inside
    # Clear-TestResultsDirs) throws; every later call passes through to the real
    # cmdlet, so Save-TestResults can enumerate what it must refuse to trust.
    $script:unitGciCalls = 0
    function Get-ChildItem {
        $script:unitGciCalls++
        if ($script:unitGciCalls -eq 1) { throw [System.IO.IOException]::new('injected snapshot enumeration failure') }
        Microsoft.PowerShell.Management\Get-ChildItem @args
    }
    $stream11 = [IO.File]::Open($oldLog11, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    try {
        $LogDir = $unitLog11
        $purgeStatuses11 = @(Clear-TestResultsDirs -Dirs @($unitResults11))
    }
    finally {
        $stream11.Dispose()
        Remove-Item -Path 'function:Get-ChildItem' -ErrorAction SilentlyContinue
    }
    # The lock is gone and the proxy is gone: Save can read everything, but the
    # snapshot is proven blind, so NOTHING from this directory may be collected.
    Write-Utf8File -Path (Join-Path $unitResults11 'new-run.log') -Content 'CURRENT-RUN-OUTPUT'
    $LogDir = $unitLog11
    $saved11 = Save-TestResults -ProjectName 'Blind.Tests' -Mode 'dotnetTest' -ProjectDir $unitFixture11 -Dirs @($unitResults11) -PurgeStatuses $purgeStatuses11
    Assert-True ($purgeStatuses11[0].Cleared -eq $false) "S11 the purge failed as arranged"
    Assert-True ($purgeStatuses11[0].SnapshotError -and $purgeStatuses11[0].SnapshotError -match 'injected snapshot enumeration failure') "S11 the status carries the snapshot enumeration failure"
    Assert-True (@($saved11.Collected).Count -eq 0) "S11 a snapshot-blind directory contributes zero collected files"
    Assert-True (@($saved11.ExcludedStale).Count -eq 2) "S11 both files of the untrusted directory are recorded as excluded"
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $unitLog11 'test-results\Blind.Tests\dotnetTest\TestResults\old-run.log'))) "S11 the OLD file was not collected"
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $unitLog11 'test-results\Blind.Tests\dotnetTest\TestResults\new-run.log'))) "S11 even the current-looking file is not claimed without a trustworthy snapshot"
    $errorsText11 = Get-Content -LiteralPath (Join-Path $unitLog11 'collection-errors.log') -Raw
    Assert-True ($errorsText11 -match 'snapshot enumeration failed') "S11 collection-errors.log records the snapshot blindness"

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
