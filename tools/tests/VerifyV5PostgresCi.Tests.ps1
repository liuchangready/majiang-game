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
      S9  three-state process-environment restoration (absent /
          present-empty / present-nonempty) proven against a FRESH child
          process's own environment readback, because the parent's
          GetEnvironmentVariable misreports a just-deleted variable as '' on
          PowerShell 7.6/.NET 10 (the windows-2025 CI image) while Windows
          PowerShell 5.1 reads null - and the .NET method delete even leaves a
          `NAME=` empty block entry there, which only the provider removal and
          the child view expose. When -PwshPath points at a second host, the
          same cases re-run INSIDE that host as real child-process evidence
          (e.g. a 5.1 self-test with -PwshPath pwsh.exe, or the reverse).

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
    [string]$ZipPath = 'D:\majiang-pg-tmp\cache\postgresql-16.10-1-windows-x64-binaries.zip',
    # Optional second host (e.g. pwsh.exe) for the S9 dual-runtime env-restore
    # evidence. Empty = the dual-host scenario reports NOT_RUN on that leg.
    [string]$PwshPath = ''
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
    # Runs the wrapper as a child of THIS self-test's own host, captures the
    # full console output into the scenario's evidence directory and returns
    # the REAL process exit code. Arguments are flattened into an array because
    # hashtable splatting onto a native command is not supported. The host is
    # resolved from the current process (never a literal 'powershell'): a
    # self-test hosted by PowerShell 7 must exercise the wrapper under
    # PowerShell 7 exactly like the CI workflow's `shell: pwsh` does, while a
    # 5.1-hosted run keeps the original 5.1 chain.
    param([hashtable]$Arguments, [string]$ConsoleOutPath)
    $hostExecutable = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
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
        $output = & $hostExecutable @argList *>&1
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
    foreach ($fnName in @('Save-PgEnvironmentSnapshot', 'Restore-PgEnvironmentSnapshot', 'Get-WrapperEnvValueState', 'Test-WrapperChildEnvState', 'Remove-WrapperProcessEnvVariable', 'Resolve-WrapperDotnetPath', 'Test-WrapperTcpPortOpen', 'Get-Sha256Hex', 'Get-WrapperFileSha256', 'Test-WrapperPathExists', 'Test-WrapperClusterOwnership')) {
        $fnAst = $wrapperAst.Find({ param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq $fnName }, $true)
        Assert-True ($null -ne $fnAst) "S6 wrapper defines $fnName"
        if ($null -ne $fnAst) { Invoke-Expression $fnAst.Extent.Text }
    }
    $envVariableNames = @('MAHJONG_LOBBY_TEST_POSTGRES', 'MAHJONG_LOBBY_PG_ROOT', 'MAHJONG_LOBBY_PG_DATA', 'MAHJONG_LOBBY_PG_EVIDENCE')
    $script:FailedStages = New-Object System.Collections.Generic.List[string]
    # The extracted env-restore helpers spawn a fresh child of the host runtime
    # for their child-observation proof; give them this process's executable.
    $script:HostExecutable = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName

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

    # case B: everything was unset before the run -> unset again afterwards.
    # The unset is performed through the wrapper's own provider-based removal
    # and the verdict consults a fresh child's block readback: on PowerShell
    # 7/.NET 10 the historical [Environment]::SetEnvironmentVariable(n,$null)
    # idiom leaves a NAME= residue in the block (the parent then reads ''), so
    # a method-delete setup would silently test present-empty, not absent.
    # See doc/v5-重构/V5-CI-ENV-RESTORE-FOLLOWUP.md.
    foreach ($name in $envVariableNames) { [void](Remove-WrapperProcessEnvVariable -Name $name) }
    $snapB = Save-PgEnvironmentSnapshot
    foreach ($name in $envVariableNames) { [Environment]::SetEnvironmentVariable($name, 'leftover') }
    $recordsB = @(Restore-PgEnvironmentSnapshot -Snapshot $snapB)
    $allGone = $true
    foreach ($name in $envVariableNames) {
        if ((Test-WrapperChildEnvState -Name $name) -ne 'absent') { $allGone = $false }
    }
    Assert-True $allGone 'S6 restore removes variables that were unset before the run (case B, fresh-child verified)'
    Assert-True (@($envVariableNames | Where-Object { $null -ne [Environment]::GetEnvironmentVariable($_) }).Count -eq 0) 'S6 parent readback is null after provider-based unset restore (both runtimes)'
    Assert-True (@($recordsB | Where-Object { -not $_.restored }).Count -eq 0) 'S6 all records report restored=true (case B)'
    Assert-True (@($recordsB | Where-Object { $_.restoredSha256 -ne '(unset)' }).Count -eq 0) 'S6 unset restoration recorded as (unset), never a value'
    Assert-True (@($recordsB | Where-Object { [string]$_.outcome -ne 'restored-absent' }).Count -eq 0) 'S6 unset records carry outcome restored-absent'
    Assert-True (@($recordsB | Where-Object { [string]$_.childStateAfter -ne 'absent' }).Count -eq 0) 'S6 unset records carry childStateAfter absent'

    # case C: mixed preset/unset (unset side through the provider-based removal,
    # for the same NAME=-residue reason as case B)
    [Environment]::SetEnvironmentVariable('MAHJONG_LOBBY_TEST_POSTGRES', $sentinels['MAHJONG_LOBBY_TEST_POSTGRES'])
    [void](Remove-WrapperProcessEnvVariable -Name 'MAHJONG_LOBBY_PG_ROOT')
    [Environment]::SetEnvironmentVariable('MAHJONG_LOBBY_PG_DATA', $sentinels['MAHJONG_LOBBY_PG_DATA'])
    [void](Remove-WrapperProcessEnvVariable -Name 'MAHJONG_LOBBY_PG_EVIDENCE')
    $snapC = Save-PgEnvironmentSnapshot
    foreach ($name in $envVariableNames) { [Environment]::SetEnvironmentVariable($name, 'dirty') }
    $recordsC = @(Restore-PgEnvironmentSnapshot -Snapshot $snapC)
    $mixedOk = (([Environment]::GetEnvironmentVariable('MAHJONG_LOBBY_TEST_POSTGRES') -ceq $sentinels['MAHJONG_LOBBY_TEST_POSTGRES']) -and
        ((Test-WrapperChildEnvState -Name 'MAHJONG_LOBBY_PG_ROOT') -eq 'absent') -and
        ([Environment]::GetEnvironmentVariable('MAHJONG_LOBBY_PG_DATA') -ceq $sentinels['MAHJONG_LOBBY_PG_DATA']) -and
        ((Test-WrapperChildEnvState -Name 'MAHJONG_LOBBY_PG_EVIDENCE') -eq 'absent'))
    Assert-True $mixedOk 'S6 mixed preset/unset restore is exact per variable (case C, fresh-child verified for unset side)'
    Assert-True (@($recordsC | Where-Object { -not $_.restored }).Count -eq 0) 'S6 all records report restored=true (case C)'

    # restore the caller's pre-selftest state (all four are unset in this shell;
    # provider-based so no NAME= residue is inherited by later scenario children)
    foreach ($name in $envVariableNames) { [void](Remove-WrapperProcessEnvVariable -Name $name) }

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

    # --- pre-existence detection: every on-disk form must be caught --------------
    $s6pathBase = Join-Path $selfTemp 's6-paths'
    New-Item -ItemType Directory -Force -Path $s6pathBase | Out-Null
    $s6existing = Join-Path $s6pathBase 'existing-dir'
    New-Item -ItemType Directory -Force -Path $s6existing | Out-Null
    Assert-True (Test-WrapperPathExists -Path $s6existing) 'S6 PathExists sees an existing directory'
    Assert-True (-not (Test-WrapperPathExists -Path (Join-Path $s6pathBase 'never-created'))) 'S6 PathExists reports false for a free path'
    Assert-True (Test-WrapperPathExists -Path ($s6existing.ToUpperInvariant())) 'S6 PathExists catches a case-variant alias'
    Assert-True (Test-WrapperPathExists -Path ($s6existing + [IO.Path]::DirectorySeparatorChar)) 'S6 PathExists catches a trailing-separator alias'
    # a dangling junction must be refused even though the file system entry is
    # the only thing left (the marker/sentinel logic must never follow it into
    # "the path looks free")
    $s6link = Join-Path $s6pathBase 'dangling-junction'
    $s6linkTarget = Join-Path $s6pathBase 'junction-target'
    New-Item -ItemType Directory -Force -Path $s6linkTarget | Out-Null
    New-Item -ItemType Junction -Path $s6link -Value $s6linkTarget | Out-Null
    Remove-Item -LiteralPath $s6linkTarget -Recurse -Force
    Assert-True (Test-WrapperPathExists -Path $s6link) 'S6 PathExists catches a dangling junction/reparse point'
    Remove-Item -LiteralPath $s6link -Force

    # --- ownership decision: a legal-format marker alone owns NOTHING ------------
    $s6ownRoot = Join-Path $selfTemp 's6-own-root'
    New-Item -ItemType Directory -Force -Path $s6ownRoot | Out-Null
    $s6marker = Join-Path $s6ownRoot 'pg-owner.json'
    $legalMarker = @{ port = 55460; root = $s6ownRoot; database = 'mahjong_lobby_test'; owner = 'mahjong-p53-pg'; createdUtc = '2026-10-23T00:00:00Z' } | ConvertTo-Json
    Write-Utf8File -Path $s6marker -Content $legalMarker
    Assert-True ((Test-WrapperClusterOwnership -Root $s6ownRoot -Port 55460 -Database 'mahjong_lobby_test' -MarkerPath $s6marker -RecordedMarkerSha256 '') -ceq 'owned') 'S6 ownership: matching fields are owned'
    Assert-True ((Test-WrapperClusterOwnership -Root $s6ownRoot -Port 55461 -Database 'mahjong_lobby_test' -MarkerPath $s6marker -RecordedMarkerSha256 '') -ceq 'foreign') 'S6 ownership: wrong port is foreign'
    Assert-True ((Test-WrapperClusterOwnership -Root (Join-Path $selfTemp 's6-other-root') -Port 55460 -Database 'mahjong_lobby_test' -MarkerPath $s6marker -RecordedMarkerSha256 '') -ceq 'foreign') 'S6 ownership: wrong root field is foreign'
    Assert-True ((Test-WrapperClusterOwnership -Root $s6ownRoot -Port 55460 -Database 'mahjong_lobby_test_other' -MarkerPath $s6marker -RecordedMarkerSha256 '') -ceq 'foreign') 'S6 ownership: wrong database is foreign'
    Assert-True ((Test-WrapperClusterOwnership -Root $s6ownRoot -Port 55460 -Database 'mahjong_lobby_test' -MarkerPath (Join-Path $selfTemp 's6-missing-marker.json') -RecordedMarkerSha256 '') -ceq 'owned-unmarked') 'S6 ownership: missing marker is owned-unmarked (fresh half-initialized root)'
    Write-Utf8File -Path (Join-Path $selfTemp 's6-corrupt-marker.json') -Content '{ this is not json'
    Assert-True ((Test-WrapperClusterOwnership -Root $s6ownRoot -Port 55460 -Database 'x' -MarkerPath (Join-Path $selfTemp 's6-corrupt-marker.json') -RecordedMarkerSha256 '') -ceq 'foreign') 'S6 ownership: corrupt marker JSON is foreign, never a delete permit'
    $s6realSha = Get-WrapperFileSha256 -Path $s6marker
    Assert-True ((Test-WrapperClusterOwnership -Root $s6ownRoot -Port 55460 -Database 'mahjong_lobby_test' -MarkerPath $s6marker -RecordedMarkerSha256 $s6realSha) -ceq 'owned') 'S6 ownership: recorded marker hash still matching is owned'
    Assert-True ((Test-WrapperClusterOwnership -Root $s6ownRoot -Port 55460 -Database 'mahjong_lobby_test' -MarkerPath $s6marker -RecordedMarkerSha256 ('0' * 64)) -ceq 'foreign') 'S6 ownership: mid-run marker replacement is foreign'
    Assert-True ((Get-WrapperFileSha256 -Path $s6marker) -ceq (Get-FileHash -LiteralPath $s6marker -Algorithm SHA256).Hash) 'S6 file SHA-256 helper matches Get-FileHash'

    # clean the S6 disk probes (they are this self-test's own creations)
    Remove-Item -LiteralPath $s6pathBase -Recurse -Force
    Remove-Item -LiteralPath $s6ownRoot -Recurse -Force
    Remove-Item -LiteralPath (Join-Path $selfTemp 's6-corrupt-marker.json') -Force -ErrorAction SilentlyContinue
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
        Assert-Equal 'owned' ([string]$s2Summary.cluster.ownership) 'S2 half-initialized root resolved as OWNED (created by this invocation)'
        $s2CleanupDetail = ''
        foreach ($stage in @($s2Summary.stages)) { if ($stage.stage -eq 'cleanup') { $s2CleanupDetail = [string]$stage.detail } }
        Assert-True ($s2CleanupDetail -like 'owned cluster deleted*') 'S2 cleanup detail proves the ownership-gated deletion'
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
    $s4Summary = Get-WrapperSummary $s4LogDir
    Assert-True ($null -ne $s4Summary) 'S4 wrapper-summary.json written'
    if ($null -ne $s4Summary) {
        Assert-Equal 'absent' ([string]$s4Summary.cluster.ownership) 'S4 precheck failure resolved ownership as absent'
        Assert-Equal 'PASS' (Get-StageStatus $s4Summary 'cleanup') 'S4 cleanup skipped-PASS: nothing owned to clean, nothing foreign touched'
    }

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
# S7: PRE-EXISTING root refusal - a legal-format marker proves nothing about
# WHICH invocation created a root, so any pre-existing path must be refused
# before a single side effect (the P1 defect this scenario pins).
# =====================================================================================
Write-Host ''
Write-Host '=== S7: pre-existing root (legal marker + sentinel) refused before any side effect ==='
$s7Foreign = Join-Path $selfTemp 's7-preexisting-root'
New-Item -ItemType Directory -Force -Path $s7Foreign | Out-Null
$s7MarkerPath = Join-Path $s7Foreign 'pg-owner.json'
$s7SentinelPath = Join-Path $s7Foreign 'preexisting-sentinel.txt'
Write-Utf8File -Path $s7MarkerPath -Content (@{ port = 55460; root = $s7Foreign; database = 'mahjong_lobby_test'; owner = 'mahjong-p53-pg'; createdUtc = '2026-10-23T00:00:00Z' } | ConvertTo-Json)
Write-Utf8File -Path $s7SentinelPath -Content 'pre-existing data: the wrapper must never delete this'
$s7MarkerShaBefore = (Get-FileHash -LiteralPath $s7MarkerPath -Algorithm SHA256).Hash
$s7SentinelShaBefore = (Get-FileHash -LiteralPath $s7SentinelPath -Algorithm SHA256).Hash
# sentinel env values prove the guard path never touches the environment
[Environment]::SetEnvironmentVariable('MAHJONG_LOBBY_TEST_POSTGRES', 'sentinel-conn')
[Environment]::SetEnvironmentVariable('MAHJONG_LOBBY_PG_ROOT', 'sentinel-root')
[Environment]::SetEnvironmentVariable('MAHJONG_LOBBY_PG_DATA', 'sentinel-data')
[Environment]::SetEnvironmentVariable('MAHJONG_LOBBY_PG_EVIDENCE', 'sentinel-evidence')
$s7LogDir = Join-Path $selfTemp 'logs-s7'
try {
    $s7Exit = Invoke-WrapperRun -Arguments @{
        Root       = $s7Foreign
        Port       = 55460
        DbName     = 'mahjong_lobby_test_ci_s7'
        PgZipPath  = (Join-Path $selfTemp 'definitely-not-there.zip')
        LogDir     = $s7LogDir
        DotnetPath = $DotnetPath
    } -ConsoleOutPath (Join-Path $selfTemp 's7-console.log')
    Assert-Equal '2' ([string]$s7Exit) 'S7 wrapper exits 2 (BLOCKED) for a pre-existing root with a legal marker'
    $s7Console = Get-Content -LiteralPath (Join-Path $selfTemp 's7-console.log') -Raw
    Assert-True ($s7Console -match 'BLOCKED: refusing a pre-existing cluster root') 'S7 refusal message names the pre-existing root'
    Assert-True ($s7Console -like '*s7-preexisting-root*') 'S7 refusal names the exact foreign path'
    Assert-True (-not (Test-Path -LiteralPath $s7LogDir)) 'S7 nothing created: LogDir never made'
    Assert-True (Test-Path -LiteralPath $s7SentinelPath) 'S7 sentinel file survives'
    Assert-True (Test-Path -LiteralPath $s7MarkerPath) 'S7 foreign marker survives'
    Assert-True ((Get-FileHash -LiteralPath $s7SentinelPath -Algorithm SHA256).Hash -ceq $s7SentinelShaBefore) 'S7 sentinel hash unchanged'
    Assert-True ((Get-FileHash -LiteralPath $s7MarkerPath -Algorithm SHA256).Hash -ceq $s7MarkerShaBefore) 'S7 foreign marker hash unchanged'
    Assert-True ([Environment]::GetEnvironmentVariable('MAHJONG_LOBBY_TEST_POSTGRES') -ceq 'sentinel-conn') 'S7 env untouched: TEST_POSTGRES keeps its pre-call value'
    Assert-True ([Environment]::GetEnvironmentVariable('MAHJONG_LOBBY_PG_ROOT') -ceq 'sentinel-root') 'S7 env untouched: PG_ROOT'
    Assert-True ([Environment]::GetEnvironmentVariable('MAHJONG_LOBBY_PG_DATA') -ceq 'sentinel-data') 'S7 env untouched: PG_DATA'
    Assert-True ([Environment]::GetEnvironmentVariable('MAHJONG_LOBBY_PG_EVIDENCE') -ceq 'sentinel-evidence') 'S7 env untouched: PG_EVIDENCE'

    # normalized aliases of the SAME existing directory must refuse identically
    $s7AliasExit = Invoke-WrapperRun -Arguments @{
        Root       = ($s7Foreign + [IO.Path]::DirectorySeparatorChar)
        Port       = 55460
        DbName     = 'mahjong_lobby_test_ci_s7'
        PgZipPath  = (Join-Path $selfTemp 'definitely-not-there.zip')
        LogDir     = (Join-Path $selfTemp 'logs-s7-alias')
        DotnetPath = $DotnetPath
    } -ConsoleOutPath (Join-Path $selfTemp 's7-alias-console.log')
    Assert-Equal '2' ([string]$s7AliasExit) 'S7 trailing-separator alias of the same directory also refused'
    $s7CaseExit = Invoke-WrapperRun -Arguments @{
        Root       = $s7Foreign.ToUpperInvariant()
        Port       = 55460
        DbName     = 'mahjong_lobby_test_ci_s7'
        PgZipPath  = (Join-Path $selfTemp 'definitely-not-there.zip')
        LogDir     = (Join-Path $selfTemp 'logs-s7-case')
        DotnetPath = $DotnetPath
    } -ConsoleOutPath (Join-Path $selfTemp 's7-case-console.log')
    Assert-Equal '2' ([string]$s7CaseExit) 'S7 case-variant alias of the same directory also refused'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $selfTemp 'logs-s7-alias'))) 'S7 alias refusals created no log dir (trailing separator)'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $selfTemp 'logs-s7-case'))) 'S7 alias refusals created no log dir (case variant)'
    Assert-True (Test-Path -LiteralPath $s7SentinelPath) 'S7 sentinel still intact after the alias refusals'
}
catch {
    Write-Host "  ERROR S7: $($_.Exception.Message)"
    $script:failures.Add("S7 threw: $($_.Exception.Message)")
}
finally {
    foreach ($n in @('MAHJONG_LOBBY_TEST_POSTGRES', 'MAHJONG_LOBBY_PG_ROOT', 'MAHJONG_LOBBY_PG_DATA', 'MAHJONG_LOBBY_PG_EVIDENCE')) {
        [Environment]::SetEnvironmentVariable($n, $null)
    }
}

# =====================================================================================
# S8: existing listener + pre-existing root - the static pre-existence guard
# fires BEFORE the port precheck; the foreign listener and foreign data are
# untouched either way.
# =====================================================================================
Write-Host ''
Write-Host '=== S8: existing listener + pre-existing root (refusal precedes precheck) ==='
$s8LogDir = Join-Path $selfTemp 'logs-s8'
$s8Listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 55460)
$s8Listener.Start()
try {
    $s8Exit = Invoke-WrapperRun -Arguments @{
        Root       = $s7Foreign
        Port       = 55460
        DbName     = 'mahjong_lobby_test_ci_s8'
        PgZipPath  = $ZipPath
        LogDir     = $s8LogDir
        DotnetPath = $DotnetPath
    } -ConsoleOutPath (Join-Path $selfTemp 's8-console.log')
    Assert-Equal '2' ([string]$s8Exit) 'S8 exits 2 for a pre-existing root even with the port busy'
    $s8Console = Get-Content -LiteralPath (Join-Path $selfTemp 's8-console.log') -Raw
    Assert-True ($s8Console -match 'BLOCKED: refusing a pre-existing cluster root') 'S8 refused by the pre-existence guard'
    Assert-True (-not ($s8Console -match 'already in use')) 'S8 never reached the port precheck (refusal precedes it)'
    $probe = New-Object System.Net.Sockets.TcpClient
    $probe.Connect('127.0.0.1', 55460)
    Assert-True $probe.Connected 'S8 foreign listener survives the refusal'
    $probe.Dispose()
    Assert-True (-not (Test-Path -LiteralPath $s8LogDir)) 'S8 nothing created: LogDir never made'
    Assert-True ((Get-FileHash -LiteralPath $s7MarkerPath -Algorithm SHA256).Hash -ceq $s7MarkerShaBefore) 'S8 foreign marker hash unchanged'
    Assert-True ((Get-FileHash -LiteralPath $s7SentinelPath -Algorithm SHA256).Hash -ceq $s7SentinelShaBefore) 'S8 sentinel hash unchanged'
}
catch {
    Write-Host "  ERROR S8: $($_.Exception.Message)"
    $script:failures.Add("S8 threw: $($_.Exception.Message)")
}
finally {
    $s8Listener.Stop()
    # test hygiene: remove the synthetic foreign root this self-test created
    Remove-Item -LiteralPath $s7Foreign -Recurse -Force -ErrorAction SilentlyContinue
}

# =====================================================================================
# S9: three-state env restoration (absent / present-empty / present-nonempty),
# proven against a FRESH child process's own readback. Measured background
# (V5-CI-ENV-RESTORE-FOLLOWUP.md): on PowerShell 7.6.6/.NET 10 (windows-2025 CI
# image) the .NET method SetEnvironmentVariable(name, $null) leaves a `NAME=`
# empty block entry and the parent Get then reads '', so the old
# "$null -eq readback" criterion produced a false env-restore FAIL (CI runs
# 40021452487 / 40021457715); Windows PowerShell 5.1 reads null and passes.
# The child view is the runtime-independent ground truth. No PostgreSQL is
# involved here - this is a pure wrapper-function scenario and never claims to
# be a database verification.
# =====================================================================================
Write-Host ''
Write-Host '=== S9: three-state env restore + fresh-child block proof (current runtime) ==='
$s9Tag = [guid]::NewGuid().ToString('N').Substring(0, 8)
$s9DualSource = @'
# Auto-generated by VerifyV5PostgresCi.Tests.ps1 (S9 dual-host evidence leg).
# Runs the wrapper's extracted env-restore functions INSIDE this host process
# and proves the three-state restoration on THIS runtime. Never touches
# Machine/User targets; dummy variable names only; no environment value is
# ever printed - only structural states and exit codes.
$ErrorActionPreference = 'Stop'
$repoRoot9 = '__REPO__'
$wrapperScript9 = Join-Path $repoRoot9 'tools\ci\verify-v5-postgres.ps1'
$script:HostExecutable = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
$script:FailedStages = New-Object System.Collections.Generic.List[string]
$wrapperAst9 = [System.Management.Automation.Language.Parser]::ParseFile($wrapperScript9, [ref]$null, [ref]$null)
foreach ($fnName in @('Get-WrapperEnvValueState', 'Test-WrapperChildEnvState', 'Remove-WrapperProcessEnvVariable', 'Save-PgEnvironmentSnapshot', 'Restore-PgEnvironmentSnapshot', 'Get-Sha256Hex')) {
    $fnAst9 = $wrapperAst9.Find({ param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq $fnName }, $true)
    if ($null -eq $fnAst9) { Write-Host "FAIL wrapper defines $fnName"; exit 1 }
    Invoke-Expression $fnAst9.Extent.Text
}
$envVariableNames = @('MJV5S9DUAL_ABSENT__TAG__', 'MJV5S9DUAL_VALUE__TAG__', 'MJV5S9DUAL_EMPTY__TAG__')
$n1 = 'MJV5S9DUAL_ABSENT__TAG__'
$n2 = 'MJV5S9DUAL_VALUE__TAG__'
$n3 = 'MJV5S9DUAL_EMPTY__TAG__'
$capProbe = 'MJV5S9DUAL_CAPP__TAG__'
Write-Host ("S9-DUAL runtime: PowerShell {0} ({1}) .NET {2} host={3}" -f $PSVersionTable.PSVersion, $PSVersionTable.PSEdition, [System.Environment]::Version, $script:HostExecutable)

# measured capability of THIS runtime: does an empty-string set retain a value?
[Environment]::SetEnvironmentVariable($capProbe, 'x')
[Environment]::SetEnvironmentVariable($capProbe, '')
$keepsEmpty = ($null -ne [Environment]::GetEnvironmentVariable($capProbe))

# stage the three pre-call snapshots, then dirty all three (wrapper sequence)
$snapshots = @{}
$snapshots[$n1] = @{ WasSet = $false; Value = $null; State = 'absent'; ChildStateBefore = 'not-probed' }
$sentinel2 = 'dual-sentinel-__TAG__'
[Environment]::SetEnvironmentVariable($n2, $sentinel2)
$snapshots[$n2] = @{ WasSet = $true; Value = $sentinel2; State = 'present-nonempty'; ChildStateBefore = 'not-probed' }
$snapshots[$n3] = @{ WasSet = $true; Value = ''; State = 'present-empty'; ChildStateBefore = 'not-probed' }
[Environment]::SetEnvironmentVariable($n1, 'dual-dirty-__TAG__')
[Environment]::SetEnvironmentVariable($n2, 'dual-dirty-__TAG__')
[Environment]::SetEnvironmentVariable($n3, 'dual-dirty-__TAG__')
$before1 = Test-WrapperChildEnvState -Name $n1

$records = @(Restore-PgEnvironmentSnapshot -Snapshot $snapshots)
$rec1 = $records | Where-Object { $_.variable -eq $n1 }
$rec2 = $records | Where-Object { $_.variable -eq $n2 }
$rec3 = $records | Where-Object { $_.variable -eq $n3 }
$after1 = Test-WrapperChildEnvState -Name $n1
$fails = 0

if ($before1 -eq 'defined' -and [bool]$rec1.restored -and $rec1.outcome -eq 'restored-absent' -and $rec1.childStateAfter -eq 'absent' -and $after1 -eq 'absent' -and $rec1.restoredSha256 -eq '(unset)' -and $rec1.preCallState -eq 'absent') {
    Write-Host 'PASS dual absent-restore child-verified'
} else { Write-Host "FAIL dual absent-restore before=$before1 after=$after1 outcome=$($rec1.outcome) childAfter=$($rec1.childStateAfter)"; $fails++ }

$rec1Json = [string]($rec1 | ConvertTo-Json -Compress)
if ($rec1Json -notmatch 'dual-dirty-__TAG__') { Write-Host 'PASS dual no-value-leak (absent record)' } else { Write-Host 'FAIL dual no-value-leak (absent record)'; $fails++ }

$back2 = [Environment]::GetEnvironmentVariable($n2)
if ([bool]$rec2.restored -and $rec2.preCallState -eq 'present-nonempty' -and $rec2.outcome -eq 'restored-exact-value' -and ("$back2" -ceq $sentinel2) -and ($rec2.restoredSha256 -ceq (Get-Sha256Hex $sentinel2)) -and $rec2.childStateAfter -eq 'defined') {
    Write-Host 'PASS dual nonempty exact restore'
} else { Write-Host "FAIL dual nonempty exact restore outcome=$($rec2.outcome) back2=$([bool]($back2 -ceq $sentinel2))"; $fails++ }

$rec2Json = [string]($rec2 | ConvertTo-Json -Compress)
if ($rec2Json -notmatch 'dual-sentinel-__TAG__' -and $rec2Json -notmatch 'dual-dirty-__TAG__') { Write-Host 'PASS dual no-value-leak (nonempty record)' } else { Write-Host 'FAIL dual no-value-leak (nonempty record)'; $fails++ }

$consistent3 = (($keepsEmpty -and [bool]$rec3.restored -and ($rec3.outcome -eq 'restored-empty-value') -and ($rec3.childStateAfter -eq 'empty')) -or
    ((-not $keepsEmpty) -and (-not [bool]$rec3.restored) -and ($rec3.outcome -eq 'failed-runtime-cannot-represent-empty') -and $rec3.note))
if ($consistent3) { Write-Host "PASS dual empty consistency (keepsEmpty=$keepsEmpty outcome=$($rec3.outcome))" } else { Write-Host "FAIL dual empty consistency keepsEmpty=$keepsEmpty outcome=$($rec3.outcome)"; $fails++ }

foreach ($n in @($n1, $n2, $n3, $capProbe)) {
    try { Set-Item -LiteralPath ('Env:\' + $n) -Value $null } catch { }
}
if ($fails -eq 0) { Write-Host 'S9-DUAL-PASS'; exit 0 }
Write-Host "S9-DUAL-FAIL $fails"
exit 1
'@
try {
    $wrapperAst9 = [System.Management.Automation.Language.Parser]::ParseFile($wrapperScript, [ref]$null, [ref]$null)
    foreach ($fnName in @('Get-WrapperEnvValueState', 'Test-WrapperChildEnvState', 'Remove-WrapperProcessEnvVariable', 'Save-PgEnvironmentSnapshot', 'Restore-PgEnvironmentSnapshot', 'Get-Sha256Hex')) {
        $fnAst9 = $wrapperAst9.Find({ param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq $fnName }, $true)
        Assert-True ($null -ne $fnAst9) "S9 wrapper defines $fnName"
        if ($null -ne $fnAst9) { Invoke-Expression $fnAst9.Extent.Text }
    }
    if (-not $script:HostExecutable) {
        $script:HostExecutable = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    }
    $script:FailedStages = New-Object System.Collections.Generic.List[string]

    # classification: the three states are distinct by construction
    Assert-Equal 'absent' (Get-WrapperEnvValueState $null) 'S9 classify null as absent'
    Assert-Equal 'present-empty' (Get-WrapperEnvValueState '') 'S9 classify empty string as present-empty'
    Assert-Equal 'present-nonempty' (Get-WrapperEnvValueState 'x') 'S9 classify value as present-nonempty'

    # fresh-child probe mapping on unambiguous inputs
    $s9ProbeNever = "MJV5S9_${s9Tag}_PROBENEVER"
    Assert-Equal 'absent' (Test-WrapperChildEnvState -Name $s9ProbeNever) 'S9 fresh-child probe reads a never-set name as absent'
    $s9ProbeSet = "MJV5S9_${s9Tag}_PROBESET"
    [Environment]::SetEnvironmentVariable($s9ProbeSet, 'probe-value')
    Assert-Equal 'defined' (Test-WrapperChildEnvState -Name $s9ProbeSet) 'S9 fresh-child probe reads a set name as defined'

    # case 1: absent before the run -> deleted again, proven by the child view
    $s9n1 = "MJV5S9_${s9Tag}_ABSENT"
    [Environment]::SetEnvironmentVariable($s9n1, "s9-dirty-$s9Tag")
    $s9snap1 = @{ $s9n1 = @{ WasSet = $false; Value = $null; State = 'absent'; ChildStateBefore = 'not-probed' } }
    $s9before1 = Test-WrapperChildEnvState -Name $s9n1
    $envVariableNames = @($s9n1)   # surgical scope: restore sees ONLY this variable
    $s9rec1 = @(@(Restore-PgEnvironmentSnapshot -Snapshot $s9snap1) | Where-Object { $_.variable -eq $s9n1 })[0]
    $s9after1 = Test-WrapperChildEnvState -Name $s9n1
    Assert-Equal 'defined' $s9before1 'S9 absent case: fresh child saw the dirty value before restore'
    Assert-True ([bool]$s9rec1.restored) 'S9 absent case: restored=true'
    Assert-Equal 'restored-absent' ([string]$s9rec1.outcome) 'S9 absent case: outcome restored-absent'
    Assert-Equal 'absent' ([string]$s9rec1.childStateAfter) 'S9 absent case: fresh-child view after restore is absent'
    Assert-Equal 'absent' $s9after1 'S9 absent case: direct child probe after restore is absent'
    Assert-Equal '(unset)' ([string]$s9rec1.restoredSha256) 'S9 absent case: recorded as (unset), never a value'
    Assert-Equal 'absent' ([string]$s9rec1.preCallState) 'S9 absent case: preCallState recorded'
    $s9rec1Json = [string]($s9rec1 | ConvertTo-Json -Compress)
    Assert-True ($s9rec1Json -notmatch "s9-dirty-$s9Tag") 'S9 absent case: record carries no restored value (hash/state only)'

    # case 2: present-nonempty -> exact case-sensitive restoration
    $s9n2 = "MJV5S9_${s9Tag}_VALUE"
    $s9sentinel = "s9-sentinel-$s9Tag-secret"
    [Environment]::SetEnvironmentVariable($s9n2, $s9sentinel)
    $s9snap2 = @{ $s9n2 = @{ WasSet = $true; Value = $s9sentinel; State = 'present-nonempty'; ChildStateBefore = 'not-probed' } }
    [Environment]::SetEnvironmentVariable($s9n2, "s9-dirty-$s9Tag")
    $envVariableNames = @($s9n2)   # surgical scope
    $s9rec2 = @(@(Restore-PgEnvironmentSnapshot -Snapshot $s9snap2) | Where-Object { $_.variable -eq $s9n2 })[0]
    Assert-True ([bool]$s9rec2.restored) 'S9 nonempty case: restored=true'
    Assert-Equal 'restored-exact-value' ([string]$s9rec2.outcome) 'S9 nonempty case: outcome restored-exact-value'
    Assert-Equal 'present-nonempty' ([string]$s9rec2.preCallState) 'S9 nonempty case: preCallState recorded'
    Assert-True ([Environment]::GetEnvironmentVariable($s9n2) -ceq $s9sentinel) 'S9 nonempty case: exact value back in the process env'
    Assert-Equal (Get-Sha256Hex $s9sentinel) ([string]$s9rec2.restoredSha256) 'S9 nonempty case: sha256 proves the value without recording it'
    Assert-Equal 'defined' ([string]$s9rec2.childStateAfter) 'S9 nonempty case: fresh-child view sees the restored variable'
    $s9rec2Json = [string]($s9rec2 | ConvertTo-Json -Compress)
    Assert-True ($s9rec2Json -notmatch [regex]::Escape($s9sentinel)) 'S9 nonempty case: record carries no secret value (hash only)'

    # case 3: present-empty -> consistency with the runtime's MEASURED
    # capability, never a fixed expectation. The snapshot is synthesized
    # because no Windows PowerShell can give its OWN process a parent-visible
    # present-empty variable (an empty-string set deletes or, on .NET 10,
    # rewrites the block entry - both measured).
    $s9n3 = "MJV5S9_${s9Tag}_EMPTY"
    $s9capProbe = "MJV5S9_${s9Tag}_CAPP"
    [Environment]::SetEnvironmentVariable($s9capProbe, 'x')
    [Environment]::SetEnvironmentVariable($s9capProbe, '')
    $s9keepsEmpty = ($null -ne [Environment]::GetEnvironmentVariable($s9capProbe))
    [Environment]::SetEnvironmentVariable($s9capProbe, $null)
    $s9snap3 = @{ $s9n3 = @{ WasSet = $true; Value = ''; State = 'present-empty'; ChildStateBefore = 'not-probed' } }
    [Environment]::SetEnvironmentVariable($s9n3, "s9-dirty-$s9Tag")
    $envVariableNames = @($s9n3)   # surgical scope
    $s9rec3 = @(@(Restore-PgEnvironmentSnapshot -Snapshot $s9snap3) | Where-Object { $_.variable -eq $s9n3 })[0]
    if ($s9keepsEmpty) {
        Assert-True ([bool]$s9rec3.restored) 'S9 empty case: runtime keeps empty -> restored=true'
        Assert-Equal 'restored-empty-value' ([string]$s9rec3.outcome) 'S9 empty case: outcome restored-empty-value'
        Assert-Equal (Get-Sha256Hex '') ([string]$s9rec3.restoredSha256) 'S9 empty case: sha256 of the empty value recorded'
    }
    else {
        Assert-True (-not [bool]$s9rec3.restored) 'S9 empty case: runtime deletes on empty -> NOT reported restored (no silent merge with absent)'
        Assert-Equal 'failed-runtime-cannot-represent-empty' ([string]$s9rec3.outcome) 'S9 empty case: outcome names the runtime limitation'
        Assert-True ($null -ne $s9rec3.note -and "$($s9rec3.note)" -ne '') 'S9 empty case: explicit explanation recorded'
    }

    # dual-host leg: the same cases inside a second real host process
    if ($PwshPath -and (Test-Path -LiteralPath $PwshPath)) {
        $s9DualScript = Join-Path $selfTemp 's9-dual-host.ps1'
        Write-Utf8File -Path $s9DualScript -Content ($s9DualSource.Replace('__TAG__', $s9Tag).Replace('__REPO__', $repoRoot))
        $s9DualLog = Join-Path $selfTemp 's9-dual-host-console.log'
        $s9PreviousEap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $s9DualOutput = & $PwshPath -NoProfile -ExecutionPolicy Bypass -File $s9DualScript *>&1
            $s9DualExit = $LASTEXITCODE
        }
        finally { $ErrorActionPreference = $s9PreviousEap }
        ($s9DualOutput | Out-String) | Set-Content -Path $s9DualLog -Encoding UTF8
        foreach ($line in @($s9DualOutput)) { Write-Host "  [dual] $line" }
        Assert-Equal '0' ([string]$s9DualExit) 'S9 dual-host leg exits 0'
        $s9DualText = ($s9DualOutput | Out-String)
        Assert-True ($s9DualText -match 'S9-DUAL-PASS') 'S9 dual-host leg reports S9-DUAL-PASS'
        Assert-True ($s9DualText -notmatch 'FAIL dual') 'S9 dual-host leg has no failing case'
    }
    else {
        Write-Host '  NOT_RUN S9 dual-host leg: -PwshPath not provided or missing (single-runtime evidence only)'
    }

    # hygiene: this scenario's dummy variables leave the process env
    foreach ($s9n in @($s9ProbeNever, $s9ProbeSet, $s9n1, $s9n2, $s9n3, $s9capProbe)) {
        try { Set-Item -LiteralPath ('Env:\' + $s9n) -Value $null } catch { }
    }
}
catch {
    Write-Host "  ERROR S9: $($_.Exception.Message)"
    $script:failures.Add("S9 threw: $($_.Exception.Message)")
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
