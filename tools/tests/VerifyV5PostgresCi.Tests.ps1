#requires -Version 5.1
<#
.SYNOPSIS
    Isolated self-test for tools\ci\verify-v5-postgres.ps1 (the CI real-PostgreSQL
    wrapper around the unified V5 gate).

.DESCRIPTION
    Scenarios (each is independent; all evidence lives under TEMP, never in the
    repository):

      S0  static guarantees: the wrapper parses, contains no machine-local
          dotnet literal, defaults to the dedicated port 55460, refuses the
          forbidden ports, never deletes the cluster root itself (deletion is
          only ever the accepted cleanup script's job) and wires all four
          MAHJONG_LOBBY_PG_* variables plus the real verifier.
      S6  unit behavior of the extracted pure functions: environment
          snapshot/restore (set/set, unset/unset, mixed), DotnetPath
          resolution order, and the TCP probe both ways.
      S1  REAL exclusive PostgreSQL + REAL unified verifier on a throwaway
          xunit.v3 fixture: the fixture asserts the four environment variables,
          the loopback connection string shape, the ownership marker, a live
          TCP connection to the cluster and appends a line to the PG evidence
          stream. The wrapper must exit 0, the unified gate must report
          OVERALL: PASS, cleanup must delete the root, every phase log plus
          wrapper-summary.json and the SHA-256 manifest must exist and match.
      S2  REAL setup failure (-PgZipPath points at a nonexistent file): the
          gate must fail non-zero with the verifier NOT_RUN, no skip/memory
          fallback, and cleanup must still remove the half-initialized root.
      S4  REAL port conflict: a live TcpListener on the requested port must be
          refused before anything is created, and the foreign listener must
          survive untouched.
      S3  runner-failure propagation through a stub verifier exiting 3 (the
          ONLY mocked scenario; proves verbatim exit-code propagation, never
          counts as a PostgreSQL verification).
      S5  REAL cleanup failure: the stub verifier parks a detached process
          holding FileShare.None on a file inside the owned root, so the
          accepted cleanup script genuinely fails to delete; the wrapper must
          exit 1 while preserving verifierExit=0 and recording cleanup FAIL.

    Ports used: 55460/55461 only (never 55450-55452); every scenario runs
    sequentially so the two ports are never contended. The disposable zip is
    reused from the local cache; its SHA-256 is pinned inside the accepted
    start-postgres.ps1, which the wrapper invokes unmodified.

    Exit codes: 0 every assertion passed; 1 at least one assertion failed;
    2 the harness itself was blocked or threw.
#>
[CmdletBinding()]
param(
    [string]$DotnetPath = 'D:\Unity\dotnet\dotnet.exe',
    [string]$ZipPath = 'D:\majiang-pg-tmp\cache\postgresql-16.10-1-windows-x64-binaries.zip'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$wrapperScript = Join-Path $repoRoot 'tools\ci\verify-v5-postgres.ps1'
$realVerifier = Join-Path $repoRoot 'tools\verify-v5.ps1'

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

function Assert-Equal([string]$Expected, [string]$Actual, [string]$Label) {
    Assert-True ($Expected -ceq $Actual) "$Label (expected '$Expected', actual '$Actual')"
}

# --- 0. environment --------------------------------------------------------------
if (-not (Test-Path -LiteralPath $DotnetPath)) {
    Write-Host "BLOCKED: dotnet not found at $DotnetPath"
    exit 2
}
if (-not (Test-Path -LiteralPath $wrapperScript)) {
    Write-Host "BLOCKED: wrapper not found at $wrapperScript"
    exit 2
}
if (-not (Test-Path -LiteralPath $realVerifier)) {
    Write-Host "BLOCKED: real verifier not found at $realVerifier"
    exit 2
}
if (-not (Test-Path -LiteralPath $ZipPath)) {
    Write-Host "BLOCKED: cached PostgreSQL zip not found at $ZipPath (the wrapper would have to download; this self-test requires the local cache)"
    exit 2
}

$selfTemp = Join-Path ([IO.Path]::GetTempPath()) ("VerifyV5PostgresCi-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + "-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $selfTemp | Out-Null

function Write-Utf8File([string]$Path, [string]$Content) {
    $dir = Split-Path $Path -Parent
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    [IO.File]::WriteAllText($Path, $Content.Replace("`n", "`r`n"), (New-Object System.Text.UTF8Encoding($false)))
}

function Invoke-WrapperRun {
    # Runs the wrapper exactly like CI would (child powershell), captures the
    # full console output into the scenario's evidence directory and returns
    # the REAL process exit code. Arguments are flattened into an array because
    # hashtable splatting onto a native command is not supported.
    param([hashtable]$Arguments, [string]$ConsoleOutPath)
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $wrapperScript)
    foreach ($key in ($Arguments.Keys | Sort-Object)) {
        $value = $Arguments[$key]
        if ($value -is [bool]) {
            if ($value) { $argList += "-$key" }
        }
        elseif ($null -ne $value -and "$value" -ne '') {
            $argList += @("-$key", "$value")
        }
    }
    $previousEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & powershell @argList *>&1
        $exit = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousEap }
    $outDir = Split-Path $ConsoleOutPath -Parent
    if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
    ($output | Out-String) | Set-Content -Path $ConsoleOutPath -Encoding UTF8
    return $exit
}

function Get-WrapperSummary([string]$LogDir) {
    $path = Join-Path (Join-Path $LogDir 'pg') 'wrapper-summary.json'
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    return ConvertFrom-Json (Get-Content -LiteralPath $path -Raw)
}

function Get-StageStatus($Summary, [string]$StageName) {
    if ($null -eq $Summary) { return '(no summary)' }
    foreach ($stage in @($Summary.stages)) {
        if ($stage.stage -eq $StageName) { return [string]$stage.status }
    }
    return '(missing)'
}

function Get-StageExit($Summary, [string]$StageName) {
    if ($null -eq $Summary) { return '(no summary)' }
    foreach ($stage in @($Summary.stages)) {
        if ($stage.stage -eq $StageName) { return [string]$stage.exitCode }
    }
    return '(missing)'
}

# --- fixture definition (S1) ------------------------------------------------------
$probeInstanceGuid = '{7C2E5A19-4D83-4F61-B0AE-2E9C8D7F1A34}'

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
    <AssemblyName>PgCi.Probe.Tests</AssemblyName>
    <RootNamespace>PgCi.Probe.Tests</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" Version="4.0.2" />
  </ItemGroup>
</Project>
'@

function New-PgFixtureWorkspace {
    # Throwaway xunit.v3 project whose single [Fact] only passes when the
    # wrapper gave it a REAL, isolated, owned PostgreSQL cluster: connection
    # string shape, ownership marker, live loopback listener, data directory
    # identity, and a writable PG evidence stream.
    param([string]$Port, [string]$DbName)
    $workspace = Join-Path $selfTemp ("fixture-" + $DbName)
    $probeDir = Join-Path $workspace 'PgCi.Probe.Tests'

    $probeSource = @"
using System;
using System.IO;
using System.Net.Sockets;
using Xunit;

namespace PgCi.Probe.Tests
{
    public class PgCiProbeTests
    {
        [Fact]
        public void Wrapper_provides_real_isolated_postgres()
        {
            var connection = Environment.GetEnvironmentVariable("MAHJONG_LOBBY_TEST_POSTGRES");
            var root = Environment.GetEnvironmentVariable("MAHJONG_LOBBY_PG_ROOT");
            var dataDirectory = Environment.GetEnvironmentVariable("MAHJONG_LOBBY_PG_DATA");
            var evidence = Environment.GetEnvironmentVariable("MAHJONG_LOBBY_PG_EVIDENCE");

            Assert.False(string.IsNullOrWhiteSpace(connection), "connection env missing");
            Assert.False(string.IsNullOrWhiteSpace(root), "root env missing");
            Assert.False(string.IsNullOrWhiteSpace(dataDirectory), "data env missing");
            Assert.False(string.IsNullOrWhiteSpace(evidence), "evidence env missing");

            Assert.StartsWith("Host=127.0.0.1;", connection);
            Assert.Contains("Port=$Port;", connection);
            Assert.Contains("Database=$DbName;", connection);

            Assert.True(Directory.Exists(root), "cluster root must exist");
            Assert.True(File.Exists(Path.Combine(root, "pg-owner.json")), "ownership marker must exist");
            Assert.Equal(Path.Combine(root, "data"), dataDirectory);
            Assert.True(Directory.Exists(dataDirectory), "data directory must exist");

            using (var client = new TcpClient())
            {
                client.Connect("127.0.0.1", $Port);
                Assert.True(client.Connected, "the disposable cluster must be listening on the dedicated loopback port");
            }

            File.AppendAllText(evidence, "pgci-probe scenario=$DbName port=$Port" + Environment.NewLine);
        }
    }
}
"@

    $sln = @"
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "PgCi.Probe.Tests", "PgCi.Probe.Tests\PgCi.Probe.Tests.csproj", "$probeInstanceGuid"
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

    Write-Utf8File -Path (Join-Path $workspace 'probe.sln') -Content $sln
    Write-Utf8File -Path (Join-Path $probeDir 'PgCi.Probe.Tests.csproj') -Content $probeCsproj
    Write-Utf8File -Path (Join-Path $probeDir 'PgCiProbeTests.cs') -Content $probeSource

    $manifest = @{
        schemaVersion = 1
        solution      = (Join-Path $workspace 'probe.sln') -replace '\\', '/'
        description   = "Throwaway fixture for VerifyV5PostgresCi.Tests.ps1 ($DbName); lives entirely under the task TEMP directory."
        testRunner    = @{
            directRunnerArgs = @('-noLogo', '-reporter', 'default')
        }
        projects      = @(
            @{
                path     = (Join-Path $probeDir 'PgCi.Probe.Tests.csproj') -replace '\\', '/'
                kind     = 'test'
                required = $false
            }
        )
    }
    $manifestPath = Join-Path $workspace 'manifest.json'
    Write-Utf8File -Path $manifestPath -Content (ConvertTo-Json -InputObject $manifest -Depth 5)
    return @{ ManifestPath = $manifestPath; Workspace = $workspace }
}

# --- stub verifiers (S3/S5: error-propagation control ONLY) ------------------------
function New-StubVerifier {
    param([int]$ExitCode, [string]$Tag, [switch]$HoldRootLock, [string]$PidFile)
    $stubPath = Join-Path $selfTemp "stub-verifier-$Tag.ps1"
    $holderPath = Join-Path $selfTemp "root-lock-holder.ps1"
    if ($HoldRootLock) {
        # The holder is a DETACHED process: it must outlive this stub child so
        # the wrapper's cleanup phase genuinely cannot delete the root.
        $holderSource = @"
param([string]`$LockedPath)
`$stream = [System.IO.File]::Open(`$LockedPath, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
Start-Sleep -Seconds 180
`$stream.Dispose()
"@
        Write-Utf8File -Path $holderPath -Content $holderSource
        $stubSource = @"
param(
    [string]`$Configuration,
    [string]`$RunnerMode,
    [string]`$LogDir,
    [string]`$DotnetPath,
    [string]`$ManifestPath,
    [switch]`$SkipPluginCopyCheck,
    [switch]`$NoProjectDiscovery
)
`$root = `$env:MAHJONG_LOBBY_PG_ROOT
if (-not `$root -or -not (Test-Path (Join-Path `$root 'pg-owner.json'))) { Write-Host 'STUB: root/marker missing'; exit 9 }
`$hold = Join-Path `$root 'hold.lock'
`$holderArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', '$holderPath', `$hold)
`$detached = Start-Process -FilePath 'powershell' -ArgumentList `$holderArgs -WindowStyle Hidden -PassThru
Set-Content -Path '$PidFile' -Value `$detached.Id
Write-Host "STUB: holding `$hold via pid `$(`$detached.Id)"
exit 0
"@
    }
    else {
        $stubSource = @"
param(
    [string]`$Configuration,
    [string]`$RunnerMode,
    [string]`$LogDir,
    [string]`$DotnetPath,
    [string]`$ManifestPath,
    [switch]`$SkipPluginCopyCheck,
    [switch]`$NoProjectDiscovery
)
Write-Host 'STUB verifier: controlled failure exit $ExitCode'
exit $ExitCode
"@
    }
    Write-Utf8File -Path $stubPath -Content $stubSource
    return $stubPath
}

# =====================================================================================
# S0: static guarantees
# =====================================================================================
Write-Host ''
Write-Host '=== S0: static guarantees ==='
try {
    $wrapperText = Get-Content -LiteralPath $wrapperScript -Raw
    $parseErrors = $null
    [void][System.Management.Automation.Language.Parser]::ParseInput($wrapperText, [ref]$null, [ref]$parseErrors)
    Assert-True ($parseErrors.Count -eq 0) "S0 wrapper parses clean ($($parseErrors.Count) errors)"

    Assert-True (-not ($wrapperText -match 'D:.Unity')) 'S0 no machine-local D:\Unity literal anywhere in the wrapper'

    $paramAst = [System.Management.Automation.Language.Parser]::ParseInput($wrapperText, [ref]$null, [ref]$parseErrors).ParamBlock
    $defaultPortLine = ($paramAst.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'Port' }).DefaultValue.Extent.Text
    Assert-Equal '55460' $defaultPortLine 'S0 default port is the dedicated 55460'

    foreach ($forbidden in @('5432', '5433', '55450', '55451', '55452')) {
        Assert-True ($wrapperText -match [regex]::Escape($forbidden)) "S0 wrapper mentions forbidden port $forbidden (guard list)"
    }

    foreach ($required in @('MAHJONG_LOBBY_TEST_POSTGRES', 'MAHJONG_LOBBY_PG_ROOT', 'MAHJONG_LOBBY_PG_DATA', 'MAHJONG_LOBBY_PG_EVIDENCE')) {
        Assert-True ($wrapperText -match [regex]::Escape($required)) "S0 wrapper wires $required"
    }
    Assert-True ($wrapperText -match 'verify-v5\.ps1') 'S0 wrapper references the real unified verifier'

    # Deletion of the disposable cluster must happen ONLY inside the accepted
    # cleanup script; the wrapper itself may delete nothing but its own staging.
    $removeLines = @($wrapperText -split "`r?`n" | Where-Object { $_ -match 'Remove-Item' })
    Assert-True ($removeLines.Count -ge 1) 'S0 wrapper has staging teardown'
    $allStaging = $true
    foreach ($line in $removeLines) { if ($line -notmatch 'staging') { $allStaging = $false } }
    Assert-True $allStaging 'S0 every wrapper Remove-Item targets the temp staging only (never the cluster root)'

    foreach ($script in @('start-postgres.ps1', 'migrate.ps1', 'cleanup-postgres.ps1')) {
        Assert-True ($wrapperText -match [regex]::Escape($script)) "S0 wrapper invokes the accepted $script"
    }
}
catch {
    Write-Host "  ERROR S0: $($_.Exception.Message)"
    $script:failures.Add("S0 threw: $($_.Exception.Message)")
}

# =====================================================================================
# S6: extracted pure functions (no PG, no child processes)
# =====================================================================================
Write-Host ''
Write-Host '=== S6: pure function behavior ==='
try {
    $wrapperAst = [System.Management.Automation.Language.Parser]::ParseFile($wrapperScript, [ref]$null, [ref]$null)
    foreach ($fnName in @('Save-PgEnvironmentSnapshot', 'Restore-PgEnvironmentSnapshot', 'Resolve-WrapperDotnetPath', 'Test-WrapperTcpPortOpen', 'Get-Sha256Hex')) {
        $fnAst = $wrapperAst.Find({ param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq $fnName }, $true)
        Assert-True ($null -ne $fnAst) "S6 wrapper defines $fnName"
        if ($null -ne $fnAst) { Invoke-Expression $fnAst.Extent.Text }
    }
    $envVariableNames = @('MAHJONG_LOBBY_TEST_POSTGRES', 'MAHJONG_LOBBY_PG_ROOT', 'MAHJONG_LOBBY_PG_DATA', 'MAHJONG_LOBBY_PG_EVIDENCE')
    $script:FailedStages = New-Object System.Collections.Generic.List[string]

    # case A: everything was set before the run -> restored to the sentinels
    $sentinels = @{
        'MAHJONG_LOBBY_TEST_POSTGRES' = 'Host=127.0.0.1;Port=9999;Database=mahjong_lobby_test;Username=caller_secret'
        'MAHJONG_LOBBY_PG_ROOT'       = 'C:\preexisting\root'
        'MAHJONG_LOBBY_PG_DATA'       = 'C:\preexisting\root\data'
        'MAHJONG_LOBBY_PG_EVIDENCE'   = 'C:\preexisting\evidence.jsonl'
    }
    foreach ($name in $envVariableNames) { [Environment]::SetEnvironmentVariable($name, $sentinels[$name]) }
    $snap = Save-PgEnvironmentSnapshot
    foreach ($name in $envVariableNames) { [Environment]::SetEnvironmentVariable($name, 'overwritten-by-wrapper') }
    $recordsA = @(Restore-PgEnvironmentSnapshot -Snapshot $snap)
    $allBack = $true
    foreach ($name in $envVariableNames) {
        if ([Environment]::GetEnvironmentVariable($name) -cne $sentinels[$name]) { $allBack = $false }
    }
    Assert-True $allBack 'S6 restore puts every pre-call sentinel value back exactly (case A)'
    Assert-True (@($recordsA | Where-Object { -not $_.restored }).Count -eq 0) 'S6 all records report restored=true (case A)'
    $shaOk = $true
    foreach ($record in $recordsA) {
        if ($record.restoredSha256 -cne (Get-Sha256Hex $sentinels[$record.variable])) { $shaOk = $false }
    }
    Assert-True $shaOk 'S6 restoredSha256 proves restoration without recording the (secret) value itself'
    Assert-True (@($recordsA | Where-Object { $_.variable -eq 'MAHJONG_LOBBY_TEST_POSTGRES' }).wasSetBeforeRun) 'S6 wasSetBeforeRun=true recorded for preset variables'

    # case B: everything was unset before the run -> unset again afterwards
    foreach ($name in $envVariableNames) { [Environment]::SetEnvironmentVariable($name, $null) }
    $snapB = Save-PgEnvironmentSnapshot
    foreach ($name in $envVariableNames) { [Environment]::SetEnvironmentVariable($name, 'leftover') }
    $recordsB = @(Restore-PgEnvironmentSnapshot -Snapshot $snapB)
    $allGone = $true
    foreach ($name in $envVariableNames) {
        if ($null -ne [Environment]::GetEnvironmentVariable($name)) { $allGone = $false }
    }
    Assert-True $allGone 'S6 restore removes variables that were unset before the run (case B)'
    Assert-True (@($recordsB | Where-Object { -not $_.restored }).Count -eq 0) 'S6 all records report restored=true (case B)'
    Assert-True (@($recordsB | Where-Object { $_.restoredSha256 -ne '(unset)' }).Count -eq 0) 'S6 unset restoration recorded as (unset), never a value'

    # case C: mixed preset/unset
    [Environment]::SetEnvironmentVariable('MAHJONG_LOBBY_TEST_POSTGRES', $sentinels['MAHJONG_LOBBY_TEST_POSTGRES'])
    [Environment]::SetEnvironmentVariable('MAHJONG_LOBBY_PG_ROOT', $null)
    [Environment]::SetEnvironmentVariable('MAHJONG_LOBBY_PG_DATA', $sentinels['MAHJONG_LOBBY_PG_DATA'])
    [Environment]::SetEnvironmentVariable('MAHJONG_LOBBY_PG_EVIDENCE', $null)
    $snapC = Save-PgEnvironmentSnapshot
    foreach ($name in $envVariableNames) { [Environment]::SetEnvironmentVariable($name, 'dirty') }
    $recordsC = @(Restore-PgEnvironmentSnapshot -Snapshot $snapC)
    $mixedOk = (([Environment]::GetEnvironmentVariable('MAHJONG_LOBBY_TEST_POSTGRES') -ceq $sentinels['MAHJONG_LOBBY_TEST_POSTGRES']) -and
        ($null -eq [Environment]::GetEnvironmentVariable('MAHJONG_LOBBY_PG_ROOT')) -and
        ([Environment]::GetEnvironmentVariable('MAHJONG_LOBBY_PG_DATA') -ceq $sentinels['MAHJONG_LOBBY_PG_DATA']) -and
        ($null -eq [Environment]::GetEnvironmentVariable('MAHJONG_LOBBY_PG_EVIDENCE')))
    Assert-True $mixedOk 'S6 mixed preset/unset restore is exact per variable (case C)'
    Assert-True (@($recordsC | Where-Object { -not $_.restored }).Count -eq 0) 'S6 all records report restored=true (case C)'

    # restore the caller's pre-selftest state (all four are unset in this shell)
    foreach ($name in $envVariableNames) { [Environment]::SetEnvironmentVariable($name, $null) }

    # DotnetPath resolution order
    $explicit = Resolve-WrapperDotnetPath -Explicit $DotnetPath
    Assert-True ($null -ne $explicit -and $explicit.Path -eq $DotnetPath -and $explicit.Source -eq 'explicit -DotnetPath') 'S6 explicit -DotnetPath wins with source explicit'

    $previousDotnetRoot = $env:DOTNET_ROOT
    try {
        $env:DOTNET_ROOT = 'D:\Unity\dotnet'
        $viaRoot = Resolve-WrapperDotnetPath -Explicit ''
        Assert-True ($null -ne $viaRoot -and $viaRoot.Path -eq 'D:\Unity\dotnet\dotnet.exe' -and $viaRoot.Source -eq 'DOTNET_ROOT') 'S6 DOTNET_ROOT resolves when explicit is absent'

        $env:DOTNET_ROOT = 'D:\definitely-not-here'
        $viaBogus = Resolve-WrapperDotnetPath -Explicit ''
        if ($null -eq $viaBogus) {
            Assert-True $true 'S6 bogus DOTNET_ROOT with no PATH dotnet resolves to null (BLOCKED at guard)'
        }
        else {
            Assert-True ($viaBogus.Source -eq 'PATH') 'S6 bogus DOTNET_ROOT falls through to PATH with source PATH'
        }
    }
    finally {
        if ($null -eq $previousDotnetRoot) { $env:DOTNET_ROOT = $null } else { $env:DOTNET_ROOT = $previousDotnetRoot }
    }

    # TCP probe both ways
    Assert-True (-not (Test-WrapperTcpPortOpen -Port 55460)) 'S6 TCP probe reports closed for a free port'
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 55461)
    $listener.Start()
    try {
        Assert-True (Test-WrapperTcpPortOpen -Port 55461) 'S6 TCP probe reports open against a live listener'
    }
    finally { $listener.Stop() }
}
catch {
    Write-Host "  ERROR S6: $($_.Exception.Message)"
    $script:failures.Add("S6 threw: $($_.Exception.Message)")
}

# =====================================================================================
# S1: REAL exclusive PostgreSQL + REAL unified verifier (the actual guarantee)
# =====================================================================================
Write-Host ''
Write-Host '=== S1: real disposable PostgreSQL + real unified gate (fixture) ==='
$s1Root = Join-Path $selfTemp 'root-s1'
$s1LogDir = Join-Path $selfTemp 'logs-s1'
$s1Fixture = New-PgFixtureWorkspace -Port '55460' -DbName 'mahjong_lobby_test_ci_s1'
try {
    $s1Exit = Invoke-WrapperRun -Arguments @{
        Root                 = $s1Root
        Port                 = 55460
        DbName               = 'mahjong_lobby_test_ci_s1'
        PgZipPath            = $ZipPath
        LogDir               = $s1LogDir
        ManifestPath         = $s1Fixture.ManifestPath
        DotnetPath           = $DotnetPath
        SkipPluginCopyCheck  = $true
        NoProjectDiscovery   = $true
    } -ConsoleOutPath (Join-Path $selfTemp 's1-console.log')
    Assert-Equal '0' ([string]$s1Exit) 'S1 wrapper exits 0 end-to-end'

    $s1Summary = Get-WrapperSummary $s1LogDir
    Assert-True ($null -ne $s1Summary) 'S1 wrapper-summary.json written'
    if ($null -ne $s1Summary) {
        Assert-Equal '0' ([string]$s1Summary.plannedExit) 'S1 summary plannedExit 0'
        Assert-Equal '0' ([string]$s1Summary.verifier.exitCode) 'S1 verifier child exit 0 recorded'
        Assert-True ([bool]$s1Summary.verifier.ran) 'S1 verifier ran flag true'
        Assert-True ($s1Summary.verifyScript -like '*verify-v5.ps1') 'S1 the REAL unified verifier was the runner'
        foreach ($stage in @('precheck', 'start-postgres', 'ownership-marker', 'environment', 'migrate', 'verify', 'archive-cluster-evidence', 'cleanup', 'env-restore', 'write-evidence')) {
            Assert-Equal 'PASS' (Get-StageStatus $s1Summary $stage) "S1 stage $stage PASS"
        }
        Assert-True ([bool]$s1Summary.cluster.rootDeleted) 'S1 cluster root deleted by cleanup'
        Assert-True (-not (Test-Path -LiteralPath $s1Root)) 'S1 root really gone from disk'
        Assert-True ($s1Summary.cluster.serverIdentity -like '*data*') 'S1 server identity line captured'
        Assert-Equal '0' ([string]@($s1Summary.failedStages).Count) 'S1 no failed stages'

        $mismatch = @($s1Summary.environment.records | Where-Object { -not $_.restored })
        Assert-True ($mismatch.Count -eq 0) 'S1 env restore: all four variables restored to pre-call state'
    }

    $s1PgDir = Join-Path $s1LogDir 'pg'
    foreach ($artifact in @('start-postgres.log', 'migrate.log', 'cleanup.log', 'verifier-console.log', 'wrapper-summary.json', 'wrapper-steps.log', 'evidence-sha256.txt', 'owner-marker.json', 'postgres-evidence.jsonl')) {
        Assert-True (Test-Path -LiteralPath (Join-Path $s1PgDir $artifact)) "S1 pg evidence: $artifact present"
    }
    Assert-True ((Get-ChildItem -LiteralPath (Join-Path $s1PgDir 'root-logs') -File -ErrorAction SilentlyContinue | Measure-Object).Count -ge 1) 'S1 cluster server logs archived'

    $evidenceText = Get-Content -LiteralPath (Join-Path $s1PgDir 'postgres-evidence.jsonl') -Raw
    Assert-True ($evidenceText -match 'pgci-probe scenario=mahjong_lobby_test_ci_s1') 'S1 fixture wrote through the PG evidence path and it survived into the log dir'

    # the SHA-256 manifest must actually describe the files it lists
    $shaEntry = $null
    foreach ($line in (Get-Content -LiteralPath (Join-Path $s1PgDir 'evidence-sha256.txt'))) {
        if ($line -match '^([0-9A-F]{64})  (wrapper-summary\.json)$') { $shaEntry = $Matches[1] }
    }
    Assert-True ($null -ne $shaEntry) 'S1 evidence-sha256.txt lists wrapper-summary.json'
    if ($shaEntry) {
        $actualHash = (Get-FileHash -LiteralPath (Join-Path $s1PgDir 'wrapper-summary.json') -Algorithm SHA256).Hash
        Assert-Equal $shaEntry $actualHash 'S1 sha manifest entry matches the file on disk'
    }

    $s1Overall = Get-Content -LiteralPath (Join-Path $s1LogDir 'summary.log') -Raw
    Assert-True ($s1Overall -match 'OVERALL: PASS') 'S1 unified gate summary.log says OVERALL: PASS'
    # row format: PROJECT KIND REQ INSLN RUNNER TOTAL FAILED SKIPPED RESULT LOG
    # the RUNNER column renders as one word ("direct") or two ("dotnet test"),
    # so allow an optional extra token before the counts.
    $rowMatch = [regex]::Match($s1Overall, 'PgCi\.Probe\.Tests\s+\S+\s+\S+\s+\S+\s+(?:\S+\s+)?(\S+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\S+)')
    Assert-True $rowMatch.Success 'S1 fixture project row present in the unified summary'
    if ($rowMatch.Success) {
        Assert-Equal '1' $rowMatch.Groups[2].Value 'S1 fixture row total = 1 test case'
        Assert-Equal '0' $rowMatch.Groups[3].Value 'S1 fixture row failed = 0'
        Assert-Equal '0' $rowMatch.Groups[4].Value 'S1 fixture row skipped = 0 (real PG means nothing to skip)'
        Assert-Equal 'PASS' $rowMatch.Groups[5].Value 'S1 fixture row result PASS'
    }

    $runInfo = Get-Content -LiteralPath (Join-Path $s1LogDir 'run-info.txt') -Raw
    $s1Head = ''
    try { $s1Head = (& git -C $repoRoot rev-parse HEAD 2>$null | Out-String).Trim() } catch { }
    if ($s1Head) { Assert-True ($runInfo -match $s1Head.Substring(0, 12)) 'S1 run-info.txt carries the real head SHA' }
}
catch {
    Write-Host "  ERROR S1: $($_.Exception.Message)"
    $script:failures.Add("S1 threw: $($_.Exception.Message)")
}

# =====================================================================================
# S2: REAL setup failure (bad zip path) - no fallback, cleanup still runs
# =====================================================================================
Write-Host ''
Write-Host '=== S2: real setup failure (nonexistent zip) ==='
$s2Root = Join-Path $selfTemp 'root-s2'
$s2LogDir = Join-Path $selfTemp 'logs-s2'
try {
    $s2Exit = Invoke-WrapperRun -Arguments @{
        Root       = $s2Root
        Port       = 55460
        DbName     = 'mahjong_lobby_test_ci_s2'
        PgZipPath  = (Join-Path $selfTemp 'definitely-not-there.zip')
        LogDir     = $s2LogDir
        DotnetPath = $DotnetPath
    } -ConsoleOutPath (Join-Path $selfTemp 's2-console.log')
    Assert-True ($s2Exit -ne 0) "S2 wrapper exits non-zero on setup failure (got $s2Exit)"
    Assert-Equal '1' ([string]$s2Exit) 'S2 wrapper exits exactly 1 (verifier never ran, no other stage masked)'

    $s2Summary = Get-WrapperSummary $s2LogDir
    Assert-True ($null -ne $s2Summary) 'S2 wrapper-summary.json written even on failure'
    if ($null -ne $s2Summary) {
        Assert-Equal 'NOT_RUN' ([string]$s2Summary.verifier.exitCode) 'S2 verifier recorded NOT_RUN (never fell back to memory)'
        Assert-True (-not [bool]$s2Summary.verifier.ran) 'S2 verifier ran flag false'
        Assert-Equal 'PASS' (Get-StageStatus $s2Summary 'cleanup') 'S2 cleanup still ran and PASSED on the half-initialized root'
        Assert-True ([bool]$s2Summary.cluster.rootDeleted) 'S2 half-initialized root deleted'
        Assert-Equal 'PASS' (Get-StageStatus $s2Summary 'env-restore') 'S2 env restore still performed on the failure path'
        Assert-True ($s2Summary.setupError -ne $null) 'S2 setupError recorded'
    }
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $s2LogDir 'summary.log'))) 'S2 no verifier summary.log (verifier never ran)'
    Assert-True (Test-Path -LiteralPath (Join-Path $s2LogDir 'pg\start-postgres.log')) 'S2 failing start-postgres output preserved'
}
catch {
    Write-Host "  ERROR S2: $($_.Exception.Message)"
    $script:failures.Add("S2 threw: $($_.Exception.Message)")
}

# =====================================================================================
# S4: REAL port conflict - refuse, never touch the foreign listener
# =====================================================================================
Write-Host ''
Write-Host '=== S4: real port conflict refusal ==='
$s4Root = Join-Path $selfTemp 'root-s4'
$s4LogDir = Join-Path $selfTemp 'logs-s4'
$s4Listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 55460)
$s4Listener.Start()
try {
    $s4Exit = Invoke-WrapperRun -Arguments @{
        Root       = $s4Root
        Port       = 55460
        DbName     = 'mahjong_lobby_test_ci_s4'
        LogDir     = $s4LogDir
        DotnetPath = $DotnetPath
    } -ConsoleOutPath (Join-Path $selfTemp 's4-console.log')
    Assert-True ($s4Exit -ne 0) "S4 wrapper exits non-zero when the port is taken (got $s4Exit)"
    Assert-True (-not (Test-Path -LiteralPath $s4Root)) 'S4 foreign conflict created nothing: explicit root absent'

    $s4Console = Get-Content -LiteralPath (Join-Path $selfTemp 's4-console.log') -Raw
    Assert-True ($s4Console -match 'already in use') 'S4 refusal message names the busy port'

    # the foreign listener must be untouched and answering
    $probe = New-Object System.Net.Sockets.TcpClient
    $probe.Connect('127.0.0.1', 55460)
    Assert-True $probe.Connected 'S4 foreign listener survives the refused wrapper run'
    $probe.Dispose()
}
catch {
    Write-Host "  ERROR S4: $($_.Exception.Message)"
    $script:failures.Add("S4 threw: $($_.Exception.Message)")
}
finally { $s4Listener.Stop() }

# =====================================================================================
# S3: runner-failure propagation through a stub verifier (mock = exit-code control)
# =====================================================================================
Write-Host ''
Write-Host '=== S3: stub verifier exit 3 propagated verbatim (mocked error control) ==='
$s3Root = Join-Path $selfTemp 'root-s3'
$s3LogDir = Join-Path $selfTemp 'logs-s3'
$s3Stub = New-StubVerifier -ExitCode 3 -Tag 's3'
try {
    $s3Exit = Invoke-WrapperRun -Arguments @{
        Root         = $s3Root
        Port         = 55461
        DbName       = 'mahjong_lobby_test_ci_s3'
        PgZipPath    = $ZipPath
        LogDir       = $s3LogDir
        DotnetPath   = $DotnetPath
        VerifyScript = $s3Stub
    } -ConsoleOutPath (Join-Path $selfTemp 's3-console.log')
    Assert-Equal '3' ([string]$s3Exit) 'S3 stub exit 3 propagated verbatim (not mapped to 1/2)'

    $s3Summary = Get-WrapperSummary $s3LogDir
    Assert-True ($null -ne $s3Summary) 'S3 wrapper-summary.json written'
    if ($null -ne $s3Summary) {
        Assert-Equal '3' ([string]$s3Summary.verifier.exitCode) 'S3 summary records verifier exit 3'
        Assert-Equal 'FAIL' (Get-StageStatus $s3Summary 'verify') 'S3 verify stage recorded FAIL'
        Assert-Equal 'PASS' (Get-StageStatus $s3Summary 'cleanup') 'S3 cleanup still PASSED'
        Assert-True ([bool]$s3Summary.cluster.rootDeleted) 'S3 root deleted despite the runner failure'
    }
}
catch {
    Write-Host "  ERROR S3: $($_.Exception.Message)"
    $script:failures.Add("S3 threw: $($_.Exception.Message)")
}

# =====================================================================================
# S5: REAL cleanup failure - verifier exit 0 preserved, cleanup FAIL recorded
# =====================================================================================
Write-Host ''
Write-Host '=== S5: real cleanup failure via FileShare.None lock on the owned root ==='
$s5Root = Join-Path $selfTemp 'root-s5'
$s5LogDir = Join-Path $selfTemp 'logs-s5'
$s5PidFile = Join-Path $selfTemp 's5-holder.pid'
$s5Stub = New-StubVerifier -ExitCode 0 -Tag 's5' -HoldRootLock -PidFile $s5PidFile
try {
    $s5Exit = Invoke-WrapperRun -Arguments @{
        Root         = $s5Root
        Port         = 55461
        DbName       = 'mahjong_lobby_test_ci_s5'
        PgZipPath    = $ZipPath
        LogDir       = $s5LogDir
        DotnetPath   = $DotnetPath
        VerifyScript = $s5Stub
    } -ConsoleOutPath (Join-Path $selfTemp 's5-console.log')
    Assert-Equal '1' ([string]$s5Exit) 'S5 wrapper exits 1 (cleanup failed, nothing masked)'

    $s5Summary = Get-WrapperSummary $s5LogDir
    Assert-True ($null -ne $s5Summary) 'S5 wrapper-summary.json written'
    if ($null -ne $s5Summary) {
        Assert-Equal '0' ([string]$s5Summary.verifier.exitCode) 'S5 verifier exit 0 PRESERVED in the summary (not masked by cleanup)'
        Assert-Equal 'FAIL' (Get-StageStatus $s5Summary 'cleanup') 'S5 cleanup stage recorded FAIL'
        Assert-True (-not [bool]$s5Summary.cluster.rootDeleted) 'S5 rootDeleted false'
        Assert-True (Test-Path -LiteralPath $s5Root) 'S5 locked root genuinely survived cleanup'
        Assert-Equal 'PASS' (Get-StageStatus $s5Summary 'env-restore') 'S5 env restore still ran after the cleanup failure'
    }
    Assert-True (Test-Path -LiteralPath (Join-Path $s5LogDir 'pg\cleanup.log')) 'S5 failing cleanup output preserved'

    # teardown: release the lock and remove the leftover root (test hygiene;
    # the scenario assertion above already captured the surviving-root proof)
    if (Test-Path -LiteralPath $s5PidFile) {
        $holderPid = [int](Get-Content -LiteralPath $s5PidFile -Raw)
        try { Stop-Process -Id $holderPid -Force -ErrorAction SilentlyContinue } catch { }
        Start-Sleep -Milliseconds 500
    }
    if (Test-Path -LiteralPath $s5Root) {
        Remove-Item -LiteralPath $s5Root -Recurse -Force -ErrorAction SilentlyContinue
        Assert-True (-not (Test-Path -LiteralPath $s5Root)) 'S5 teardown removed the leftover root after killing the lock holder'
    }
}
catch {
    Write-Host "  ERROR S5: $($_.Exception.Message)"
    $script:failures.Add("S5 threw: $($_.Exception.Message)")
    if (Test-Path -LiteralPath $s5PidFile) {
        try { Stop-Process -Id (Get-Content -LiteralPath $s5PidFile -Raw) -Force -ErrorAction SilentlyContinue } catch { }
    }
}

# =====================================================================================
# verdict
# =====================================================================================
Write-Host ''
Write-Host "=== VerifyV5PostgresCi self-test: $($script:checks) checks, $($script:failures.Count) failures ==="
foreach ($failure in $script:failures) { Write-Host "  FAILED: $failure" }

if (Test-Path -LiteralPath $selfTemp) {
    # keep the scenario evidence for the report, print the path
    Write-Host "scenario evidence kept at: $selfTemp"
}

if ($script:failures.Count -gt 0) { exit 1 }
exit 0
