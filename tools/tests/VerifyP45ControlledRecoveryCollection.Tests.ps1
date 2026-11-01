#Requires -Version 5.1
<#
.SYNOPSIS
  Negative/tamper matrix for the independent P4.5 controlled recovery collector
  and offline verifier.

.DESCRIPTION
  Every case goes through the real script entry points in a child PowerShell
  process. Fixture processes are labelled FIXTURE and are never reported as a
  GAME positive. The tests copy sealed cases into scratch space and mutate the
  copies; the original sealed evidence is read-only.

  The optional -RealCases argument accepts "name=caseDir" entries produced by
  real Windows Player runs; each is re-verified with its manifest digest.
#>
[CmdletBinding()]
param(
    [string]$OutputDir = '',
    [string]$TemplateDir = 'D:/majiang-wt/p45-recovery-export-ds-20261031/build/p45-recovery-export/official-r3-672b6c3/normal-20261031-062349/evidence/collections/0002/observations/export-attempt-0002-after',
    [string]$VerifierPath = '',
    [string]$DotnetPath = 'D:\Unity\dotnet\dotnet.exe',
    [string]$FixturePlayer = '',
    [string[]]$RealCases = @()
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$script:RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$script:Collector = Join-Path $script:RepoRoot 'tools/unity/run-p45-controlled-recovery-collection.ps1'
$script:Verifier = Join-Path $script:RepoRoot 'tools/unity/verify-p45-controlled-recovery-collection.ps1'
$script:Utf8 = New-Object System.Text.UTF8Encoding($false)
$script:Total = 0
$script:Passed = 0
$script:Failed = 0
$script:Skipped = 0
$script:Cases = New-Object System.Collections.Generic.List[object]

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $script:RepoRoot ('build/p45-controlled-collection/tests/' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
}
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
if ([IO.Directory]::Exists($OutputDir)) { $OutputDir = $OutputDir + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6) }
[IO.Directory]::CreateDirectory($OutputDir) | Out-Null

if ([string]::IsNullOrWhiteSpace($VerifierPath)) {
    $VerifierPath = Join-Path $script:RepoRoot 'dotnet/tools/P45RecoveryVerifier/bin/Release/net8.0/P45RecoveryVerifier.dll'
}
$VerifierPath = [IO.Path]::GetFullPath($VerifierPath)

$env:DOTNET_ROOT = Split-Path -Parent $DotnetPath
$env:P45_FIXTURE_TEMPLATE = $TemplateDir
$env:P45_FIXTURE_START_DELAY_MS = '500'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Invoke-Case {
    param([string]$Name, [scriptblock]$Body, [switch]$Skip)
    $script:Total++
    if ($Skip) {
        $script:Skipped++
        $script:Cases.Add([pscustomobject]@{ name = $Name; status = 'NOT_RUN'; error = '' }) | Out-Null
        Write-Host ('NOT_RUN ' + $Name)
        return
    }
    try {
        . $Body
        $script:Passed++
        $script:Cases.Add([pscustomobject]@{ name = $Name; status = 'PASS'; error = '' }) | Out-Null
        Write-Host ('PASS ' + $Name)
    }
    catch {
        $script:Failed++
        $message = ([string]$_.Exception.Message -replace '[\r\n]+', ' ').Trim()
        $script:Cases.Add([pscustomobject]@{ name = $Name; status = 'FAIL'; error = $message }) | Out-Null
        Write-Host ('FAIL ' + $Name + ': ' + $message)
    }
}

function Get-DirtyList {
    $paths = @(& git -C $script:RepoRoot status --porcelain=v1 2>$null | ForEach-Object {
            if ($_.Length -gt 3) { $_.Substring(3).Trim().Trim('"').Replace('\', '/') } else { '' }
        } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    return ($paths -join ',')
}

function Get-CollectorFunctionText {
    param([string[]]$Names)
    $tokens = $null
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($script:Collector, [ref]$tokens, [ref]$errors)
    $selected = @()
    foreach ($fn in $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
        if ($Names -contains $fn.Name) { $selected += $fn.Extent.Text }
    }
    return ($selected -join [Environment]::NewLine)
}

function Set-TestEnv {
    param([hashtable]$Values)
    $saved = @{}
    foreach ($key in $Values.Keys) {
        $saved[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        [Environment]::SetEnvironmentVariable($key, [string]$Values[$key], 'Process')
    }
    return $saved
}

function Restore-TestEnv {
    param([hashtable]$Saved)
    foreach ($key in $Saved.Keys) {
        [Environment]::SetEnvironmentVariable($key, $Saved[$key], 'Process')
    }
}

function New-FixturePlayer {
    if (-not [string]::IsNullOrWhiteSpace($FixturePlayer) -and [IO.File]::Exists($FixturePlayer)) {
        return [IO.Path]::GetFullPath($FixturePlayer)
    }
    $dir = Join-Path $OutputDir 'fixture'
    [IO.Directory]::CreateDirectory($dir) | Out-Null
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>p45-fixture-player</AssemblyName><InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
</Project>
'@ | Set-Content -Encoding UTF8 (Join-Path $dir 'fixture.csproj')
    @'
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

internal static class Program
{
    static string? V(string[] a, string n) { for (int i = 0; i + 1 < a.Length; i++) if (a[i] == n) return a[i + 1]; return null; }
    static string E(string n, string d = "") => Environment.GetEnvironmentVariable(n) ?? d;
    static string H(byte[] b) { using var s = SHA256.Create(); return Convert.ToHexString(s.ComputeHash(b)); }
    static string HF(string p) { using var s = SHA256.Create(); using var f = File.OpenRead(p); return Convert.ToHexString(s.ComputeHash(f)); }
    public static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--bystander") { Thread.Sleep(90000); return 0; }
        string dir = V(args, "-p45-recovery-export") ?? "";
        string phase = V(args, "-p45-recovery-phase") ?? "BEFORE_RECOVERY";
        string challenge = V(args, "-p45-recovery-challenge") ?? "";
        string run = V(args, "-p45-recovery-run") ?? "";
        string attempt = V(args, "-p45-recovery-attempt") ?? "";
        string package = V(args, "-p45-recovery-package-sha") ?? "";
        string source = V(args, "-p45-recovery-source-head") ?? "";
        string build = V(args, "-p45-recovery-build-commit") ?? "";
        string dataDir = V(args, "-p45-recovery-data-dir") ?? "";
        int delay = 0; int.TryParse(E("P45_FIXTURE_START_DELAY_MS", "300"), out delay); Thread.Sleep(delay);
        string alias = E("P45_FIXTURE_ALIAS");
        if (alias == "junction")
        {
            string target = E("P45_FIXTURE_ALIAS_TARGET");
            if (Directory.Exists(dataDir)) Directory.Delete(dataDir, true);
            var cp = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + dataDir + "\" \"" + target + "\"")
            { UseShellExecute = false, CreateNoWindow = true });
            cp!.WaitForExit();
            if (cp.ExitCode != 0) return 91;
        }
        else if (alias == "hardlink")
        {
            string link = Path.Combine(dataDir, "local-main.save");
            string target = E("P45_FIXTURE_ALIAS_TARGET");
            if (File.Exists(link)) File.Delete(link);
            var cp = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /H \"" + link + "\" \"" + target + "\"")
            { UseShellExecute = false, CreateNoWindow = true });
            cp!.WaitForExit();
            if (cp.ExitCode != 0) return 92;
        }
        else if (alias == "sealed-junction")
        {
            string smoke = V(args, "-smoke-data") ?? "";
            string side = phase == "BEFORE_RECOVERY" ? "before" : "after";
            string sealedPath = Path.Combine(smoke, "case", "attempts", attempt + "-" + side, "sealed");
            string target = E("P45_FIXTURE_ALIAS_TARGET");
            if (Directory.Exists(sealedPath)) Directory.Delete(sealedPath, true);
            var cp = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + sealedPath + "\" \"" + target + "\"")
            { UseShellExecute = false, CreateNoWindow = true });
            cp!.WaitForExit();
            if (cp.ExitCode != 0) return 93;
        }
        else if (alias == "journal-hardlink" || alias == "verifier-stdout-hardlink")
        {
            string smoke = V(args, "-smoke-data") ?? "";
            string side = phase == "BEFORE_RECOVERY" ? "before" : "after";
            string leaf = alias == "journal-hardlink"
                ? Path.Combine(smoke, "case", "records.jsonl")
                : Path.Combine(smoke, "case", "attempts", attempt + "-" + side, "verifier", "recovery.stdout.log");
            string target = E("P45_FIXTURE_ALIAS_TARGET");
            if (File.Exists(leaf)) File.Delete(leaf);
            var cp = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /H \"" + leaf + "\" \"" + target + "\"")
            { UseShellExecute = false, CreateNoWindow = true });
            cp!.WaitForExit();
            if (cp.ExitCode != 0) return 94;
        }
        bool err = E("P45_FIXTURE_ERROR") == "1" && (E("P45_FIXTURE_ERROR_PHASE") == "" || E("P45_FIXTURE_ERROR_PHASE") == phase);
        if (err) { Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, "export.error.json"), "{\"kind\":\"p45-player-structured-export-error\",\"code\":\"FIXTURE\"}"); return 1; }
        Directory.CreateDirectory(dir);
        string t = E("P45_FIXTURE_TEMPLATE");
        File.Copy(Path.Combine(t, "authority.save"), Path.Combine(dir, "authority.save"), true);
        File.Copy(Path.Combine(t, "cheat-state.bin"), Path.Combine(dir, "cheat-state.bin"), true);
        byte[] ab = File.ReadAllBytes(Path.Combine(dir, "authority.save"));
        byte[] cb = File.ReadAllBytes(Path.Combine(dir, "cheat-state.bin"));
        JsonNode n = JsonNode.Parse(File.ReadAllText(Path.Combine(t, "export.json")))!;
        var p = Process.GetCurrentProcess();
        string pid = E("P45_FIXTURE_PID", p.Id.ToString(CultureInfo.InvariantCulture));
        n["phase"] = E("P45_FIXTURE_PHASE", phase); n["challenge"] = E("P45_FIXTURE_CHALLENGE", challenge);
        n["runId"] = E("P45_FIXTURE_RUN", run); n["attempt"] = E("P45_FIXTURE_ATTEMPT", attempt);
        n["pid"] = int.Parse(pid, CultureInfo.InvariantCulture);
        n["processStartUtc"] = E("P45_FIXTURE_START", p.StartTime.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        n["processPath"] = E("P45_FIXTURE_PATH", p.MainModule!.FileName!);
        n["playerSha256"] = E("P45_FIXTURE_PLAYER_SHA", HF(p.MainModule!.FileName!));
        n["packageManifestSha256"] = E("P45_FIXTURE_PACKAGE", package);
        n["packageFileCount"] = int.Parse(E("P45_FIXTURE_PACKAGE_COUNT", "1"), CultureInfo.InvariantCulture);
        n["sourceHead"] = E("P45_FIXTURE_SOURCE", source); n["buildCommit"] = E("P45_FIXTURE_BUILD", build);
        n["authorityBytes"] = ab.Length; n["authoritySha256"] = H(ab);
        n["cheatStateBytes"] = cb.Length; n["cheatStateSha256"] = H(cb);
        if (n["facts"] is JsonObject f)
        {
            f["authoritySaveSha256"] = H(ab); f["authoritySaveBytes"] = ab.Length; f["cheatStateSha256"] = H(cb);
            if (E("P45_FIXTURE_FACTS_MISMATCH") == "1") f["stateVersion"] = "999";
        }
        string text = n.ToJsonString();
        File.WriteAllText(Path.Combine(dir, "export.json"), text, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, "export.done"), H(Encoding.UTF8.GetBytes(text)) + "  export.json" + Environment.NewLine, new UTF8Encoding(false));
        if (E("P45_FIXTURE_MUTATE_AUTHORITY") == "1") { File.AppendAllText(Path.Combine(dir, "authority.save"), "X"); }
        string deleteMode = E("P45_FIXTURE_DELETE_EXPORT");
        string deletePhase = E("P45_FIXTURE_DELETE_EXPORT_PHASE");
        if (deleteMode != "" && (deletePhase == "" || deletePhase == phase))
        {
            if (deleteMode.Contains("json") && File.Exists(Path.Combine(dir, "export.json"))) File.Delete(Path.Combine(dir, "export.json"));
            if (deleteMode.Contains("done") && File.Exists(Path.Combine(dir, "export.done"))) File.Delete(Path.Combine(dir, "export.done"));
            if (deleteMode.Contains("authority") && File.Exists(Path.Combine(dir, "authority.save"))) File.Delete(Path.Combine(dir, "authority.save"));
            if (deleteMode.Contains("cheat") && File.Exists(Path.Combine(dir, "cheat-state.bin"))) File.Delete(Path.Combine(dir, "cheat-state.bin"));
        }
        if (args.Length >= 0 && Array.IndexOf(args, "-p45-recovery-exit-after-export") >= 0) return 0;
        Thread.Sleep(120000); return 0;
    }
}
'@ | Set-Content -Encoding UTF8 (Join-Path $dir 'Program.cs')
    $log = Join-Path $dir 'build.log'
    & $DotnetPath build (Join-Path $dir 'fixture.csproj') -c Release --nologo *> $log
    if ($LASTEXITCODE -ne 0) { throw ('fixture build failed: ' + (Get-Content $log -Tail 5 | Out-String)) }
    return (Join-Path $dir 'bin/Release/net8.0/p45-fixture-player.exe')
}

function Invoke-Collector {
    param(
        [string]$Name,
        [ValidateSet('Normal', 'Force')][string]$Case = 'Normal',
        [string]$SaveFault = 'None',
        [hashtable]$Environment = @{},
        [string]$EvidenceRoot = '',
        [string]$PlayerPath = $script:FixturePlayer,
        [string]$BuildExitCode = '0',
        [string]$ExpectedPlayerSha256 = '',
        [string]$AllowedDirtyPath = '',
        [switch]$OmitAllowDirty
    )
    if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) { $EvidenceRoot = Join-Path $OutputDir ('cases/' + $Name) }
    $log = Join-Path $OutputDir ($Name + '.collector.log')
    $saved = Set-TestEnv -Values $Environment
    try {
        $args = @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script:Collector,
            '-Case', $Case, '-SaveFault', $SaveFault,
            '-PlayerPath', $PlayerPath,
            '-BuildCommit', 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
            '-BuildLog', (Join-Path $OutputDir 'fixture-build.log'),
            '-BuildCommand', 'fixture-only',
            '-BuildExitCode', $BuildExitCode,
            '-Baseline', 'D:/majiang-wt/p45-windows-validation-20261030/build/p45-windows-validation/series-r2/private-saves/baseline.save',
            '-VerifierPath', $VerifierPath,
            '-DotnetPath', $DotnetPath,
            '-EvidenceRoot', $EvidenceRoot,
            '-RunId', ('test-' + ($Name -replace '[^A-Za-z0-9-]', '-')),
            '-EvidenceClass', 'FIXTURE',
            '-TimeoutSeconds', '45'
        )
        if (-not [string]::IsNullOrWhiteSpace($ExpectedPlayerSha256)) {
            $args += @('-ExpectedPlayerSha256', $ExpectedPlayerSha256)
        }
        if (-not $OmitAllowDirty -and -not [string]::IsNullOrWhiteSpace($AllowedDirtyPath)) {
            $args += @('-AllowDirtySource', '-AllowedDirtyPath', $AllowedDirtyPath)
        }
        & powershell.exe @args *> $log
        $exit = $LASTEXITCODE
        $resultPath = Join-Path $EvidenceRoot 'collector-result.json'
        $result = if ([IO.File]::Exists($resultPath)) { Get-Content $resultPath -Raw | ConvertFrom-Json } else { $null }
        [pscustomobject]@{ exit = $exit; root = $EvidenceRoot; log = $log; result = $result }
    }
    finally { Restore-TestEnv -Saved $saved }
}

function Invoke-OfflineVerifier {
    param([string]$CaseDir, [string]$ExpectedManifest, [string]$OutDir)
    if ([string]::IsNullOrWhiteSpace($OutDir)) { $OutDir = Join-Path $OutputDir ('verify-' + [Guid]::NewGuid().ToString('N').Substring(0, 6)) }
    [IO.Directory]::CreateDirectory($OutDir) | Out-Null
    $args = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script:Verifier,
        '-CaseDir', $CaseDir, '-ExpectedManifestSha256', $ExpectedManifest,
        '-VerifierPath', $VerifierPath, '-DotnetPath', $DotnetPath, '-OutputDir', $OutDir
    )
    & powershell.exe @args *> (Join-Path $OutDir '.console.log')
    $exit = $LASTEXITCODE
    $resultPath = Join-Path $OutDir 'verification-result.json'
    $result = if ([IO.File]::Exists($resultPath)) { Get-Content $resultPath -Raw | ConvertFrom-Json } else { $null }
    [pscustomobject]@{ exit = $exit; out = $OutDir; result = $result }
}

function Get-ManifestHash {
    param([string]$CaseDir)
    return (Get-FileHash (Join-Path $CaseDir 'manifest.json') -Algorithm SHA256).Hash
}

function Update-CaseManifest {
    param([string]$CaseDir)
    $manifestPath = Join-Path $CaseDir 'manifest.json'
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    $files = New-Object System.Collections.Generic.List[object]
    foreach ($file in @(Get-ChildItem -LiteralPath $CaseDir -Recurse -File -Force)) {
        $rel = $file.FullName.Substring($CaseDir.Length).TrimStart('\', '/').Replace('\', '/')
        if ($rel -in @('manifest.json', 'manifest.sha256.txt')) { continue }
        $files.Add([pscustomobject]@{ path = $rel; bytes = $file.Length; sha256 = (Get-FileHash $file.FullName -Algorithm SHA256).Hash }) | Out-Null
    }
    $manifest.files = @($files.ToArray() | Sort-Object -Property path -CaseSensitive)
    $manifest.counts.files = $manifest.files.Count
    $json = $manifest | ConvertTo-Json -Depth 40
    [IO.File]::WriteAllText($manifestPath, $json + [Environment]::NewLine, $script:Utf8)
    $hash = Get-ManifestHash -CaseDir $CaseDir
    [IO.File]::WriteAllText((Join-Path $CaseDir 'manifest.sha256.txt'), $hash + '  manifest.json' + [Environment]::NewLine, $script:Utf8)
    return $hash
}

function Copy-CaseForTamper {
    param([string]$SourceCase, [string]$Name)
    $dest = Join-Path $OutputDir ('tamper/' + $Name)
    if ([IO.Directory]::Exists($dest)) { Remove-Item $dest -Recurse -Force }
    [IO.Directory]::CreateDirectory($dest) | Out-Null
    Copy-Item -Path (Join-Path $SourceCase '*') -Destination $dest -Recurse -Force
    return $dest
}

function Write-JsonMutable {
    param([string]$Path, [scriptblock]$Mutator)
    $doc = Get-Content $Path -Raw | ConvertFrom-Json
    & $Mutator $doc
    [IO.File]::WriteAllText($Path, (($doc | ConvertTo-Json -Depth 40) + [Environment]::NewLine), $script:Utf8)
}

if (-not [IO.File]::Exists($VerifierPath)) { throw ('Verifier not found: ' + $VerifierPath) }
if (-not [IO.Directory]::Exists($TemplateDir)) { throw ('Fixture template not found: ' + $TemplateDir) }
$script:FixturePlayer = New-FixturePlayer
$fixtureBuildLog = Join-Path $OutputDir 'fixture-build.log'
[IO.File]::WriteAllText($fixtureBuildLog, "Build Successful (fixture only)`n", $script:Utf8)
$dirty = Get-DirtyList

Write-Host ('FIXTURE_PLAYER=' + $script:FixturePlayer)
Write-Host ('VERIFIER=' + $VerifierPath)
Write-Host ('OUTPUT=' + $OutputDir)

$script:normal = $null
$script:force = $null

Invoke-Case 'collector rejects an existing evidence root (no reuse)' {
    $root = Join-Path $OutputDir 'cases/reuse'
    [IO.Directory]::CreateDirectory($root) | Out-Null
    $run = Invoke-Collector -Name 'reuse-second' -Environment @{} -EvidenceRoot $root -AllowedDirtyPath $dirty
    Assert-True ($run.exit -eq 3) ('expected exit 3, actual ' + $run.exit)
}
Invoke-Case 'collector rejects a wrong expected Player digest' {
    $run = Invoke-Collector -Name 'wrong-player' -ExpectedPlayerSha256 ('0' * 64) -AllowedDirtyPath $dirty
    Assert-True ($run.exit -eq 4) ('expected exit 4, actual ' + $run.exit)
}
Invoke-Case 'collector rejects a fresh build that did not exit 0' {
    $run = Invoke-Collector -Name 'bad-build' -BuildExitCode '7' -AllowedDirtyPath $dirty
    Assert-True ($run.exit -eq 17) ('expected exit 17, actual ' + $run.exit)
}
Invoke-Case 'collector rejects an unapproved dirty source path' {
    $probeName = '.p45-dirty-probe-' + [Guid]::NewGuid().ToString('N')
    $relative = 'tools/tests/' + $probeName
    $probe = Join-Path $script:RepoRoot $relative
    $created = $false
    try {
        $stream = [IO.File]::Open($probe, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $bytes = $script:Utf8.GetBytes('probe'); $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
        $created = $true
        $run = Invoke-Collector -Name 'dirty-source' -OmitAllowDirty
        Assert-True ($run.exit -eq 17) ('expected exit 17, actual ' + $run.exit)
    }
    finally { if ($created -and [IO.File]::Exists($probe)) { Remove-Item $probe -Force } }
}
Invoke-Case 'collector accepts an explicitly approved dirty path and preserves clean-source behavior' {
    $probeName = '.p45-approved-dirty-' + [Guid]::NewGuid().ToString('N')
    $relative = 'tools/tests/' + $probeName
    $probe = Join-Path $script:RepoRoot $relative
    $created = $false
    try {
        $stream = [IO.File]::Open($probe, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $bytes = $script:Utf8.GetBytes('approved'); $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
        $created = $true
        $allowed = @($dirty, $relative) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        $run = Invoke-Collector -Name 'approved-dirty' -AllowedDirtyPath ($allowed -join ',')
        Assert-True ($run.exit -eq 0) ('approved dirty path exit ' + $run.exit + ' log=' + $run.log)
        Assert-True ($run.result.status -eq 'COMPLETE') 'approved dirty path did not complete'
    }
    finally { if ($created -and [IO.File]::Exists($probe)) { Remove-Item $probe -Force } }
}
Invoke-Case 'a pre-existing dirty-probe sentinel is never overwritten or deleted' {
    $probe = Join-Path $script:RepoRoot 'tools/tests/.p45-dirty-probe'
    $created = $false
    $beforeHash = $null
    try {
        if (-not [IO.File]::Exists($probe)) {
            $stream = [IO.File]::Open($probe, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $bytes = $script:Utf8.GetBytes('caller-sentinel-original'); $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
            $created = $true
        }
        $beforeHash = (Get-FileHash $probe -Algorithm SHA256).Hash
        $run = Invoke-Collector -Name 'sentinel-dirty' -OmitAllowDirty
        Assert-True ($run.exit -eq 17) ('sentinel dirty run should fail cleanly, actual ' + $run.exit)
        Assert-True ([IO.File]::Exists($probe)) 'pre-existing sentinel was deleted'
        Assert-True ([string]::Equals((Get-FileHash $probe -Algorithm SHA256).Hash, $beforeHash, [StringComparison]::OrdinalIgnoreCase)) 'pre-existing sentinel bytes changed'
    }
    finally { if ($created -and [IO.File]::Exists($probe)) { Remove-Item $probe -Force } }
}
Invoke-Case 'real entry point records Normal/Force with direct Process objects' {
    $script:normal = Invoke-Collector -Name 'fixture-normal' -Case Normal -SaveFault None -AllowedDirtyPath $dirty
    Assert-True ($script:normal.exit -eq 0) ('Normal exit ' + $script:normal.exit + ' log=' + $script:normal.log)
    Assert-True ($script:normal.result.status -eq 'COMPLETE') ('Normal status ' + $script:normal.result.status)
    Assert-True ($script:normal.result.factsEqual -eq $true) 'Normal factsEqual was not true'
    Assert-True ($script:normal.result.collectionChain -eq 'PASS') 'Normal chain was not PASS'
    Assert-True ($script:normal.result.callerAcceptance -eq 'PENDING_INDEPENDENT_REVIEW') 'collector self-accepted'
    Assert-True ($script:normal.result.gamePositive -eq $false) 'fixture was reported as GAME positive'
    $launch1 = Get-Content (Join-Path $script:normal.root 'case/attempts/0001-before/launch-observation.json') -Raw | ConvertFrom-Json
    $launch2 = Get-Content (Join-Path $script:normal.root 'case/attempts/0002-after/launch-observation.json') -Raw | ConvertFrom-Json
    Assert-True ([int]$launch1.pid -ne [int]$launch2.pid) 'attempts reused a PID'
    Assert-True ([string]$launch1.challenge -ne [string]$launch2.challenge) 'attempts reused a challenge'
    Assert-True ($launch1.argvMatched -eq $true -and $launch2.argvMatched -eq $true) 'actual argv was not matched'

    $script:force = Invoke-Collector -Name 'fixture-force' -Case Force -SaveFault None -AllowedDirtyPath $dirty
    Assert-True ($script:force.exit -eq 0) ('Force exit ' + $script:force.exit + ' log=' + $script:force.log)
    $exit1 = Get-Content (Join-Path $script:force.root 'case/attempts/0001-before/exit-observation.json') -Raw | ConvertFrom-Json
    Assert-True ($exit1.disposition -eq 'FORCE_KILLED') ('Force BEFORE disposition ' + $exit1.disposition)
    Assert-True ($script:force.result.factsEqual -eq $true) 'Force factsEqual was not true'
}
Invoke-Case 'junction/hardlink aliases are rejected before any foreign write' {
    $baseline = 'D:/majiang-wt/p45-windows-validation-20261030/build/p45-windows-validation/series-r2/private-saves/baseline.save'
    foreach ($alias in @('junction', 'hardlink')) {
        $foreign = Join-Path $OutputDir ('foreign/' + $alias + '-' + [Guid]::NewGuid().ToString('N'))
        [IO.Directory]::CreateDirectory($foreign) | Out-Null
        $foreignPrimary = Join-Path $foreign 'local-main.save'
        $foreignBackup = Join-Path $foreign 'local-main.save.prev'
        Copy-Item $baseline $foreignPrimary -Force
        Copy-Item $baseline $foreignBackup -Force
        $beforePrimary = (Get-FileHash $foreignPrimary -Algorithm SHA256).Hash
        $beforeBackup = (Get-FileHash $foreignBackup -Algorithm SHA256).Hash
        $run = Invoke-Collector -Name ('alias-' + $alias) -Case Normal -SaveFault TruncatePrimary `
            -Environment @{ P45_FIXTURE_ALIAS = $alias; P45_FIXTURE_ALIAS_TARGET = $(if ($alias -eq 'junction') { $foreign } else { $foreignPrimary }) } `
            -AllowedDirtyPath $dirty
        Assert-True ($run.exit -ne 0) ($alias + ' alias run unexpectedly succeeded')
        Assert-True (-not (Test-Path (Join-Path $run.root 'case/attempts/0002-after'))) ($alias + ' started AFTER after alias rejection')
        $failure = if ([IO.File]::Exists((Join-Path $run.root 'case/failure.json'))) { Get-Content (Join-Path $run.root 'case/failure.json') -Raw | ConvertFrom-Json } else { $null }
        Assert-True ($null -ne $failure -and [string]$failure.message -match 'RESOURCE_ALIAS') ($alias + ' rejection did not name RESOURCE_ALIAS')
        Assert-True ((Get-FileHash $foreignPrimary -Algorithm SHA256).Hash -eq $beforePrimary) ($alias + ' changed the foreign primary')
        Assert-True ((Get-FileHash $foreignBackup -Algorithm SHA256).Hash -eq $beforeBackup) ($alias + ' changed the foreign backup')
    }
}
Invoke-Case 'a junctioned sealed destination is rejected before any foreign seal write' {
    $foreign = Join-Path $OutputDir ('foreign/sealed-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($foreign) | Out-Null
    $run = Invoke-Collector -Name 'alias-sealed-junction' -Case Normal -SaveFault None `
        -Environment @{ P45_FIXTURE_ALIAS = 'sealed-junction'; P45_FIXTURE_ALIAS_TARGET = $foreign } `
        -AllowedDirtyPath $dirty
    Assert-True ($run.exit -ne 0) 'sealed-destination junction run unexpectedly succeeded'
    Assert-True (@(Get-ChildItem $foreign -File -ErrorAction SilentlyContinue).Count -eq 0) 'collector wrote through the sealed-destination junction'
    Assert-True (-not (Test-Path (Join-Path $run.root 'case/attempts/0002-after'))) 'sealed-destination junction started AFTER'
    $failure = if ([IO.File]::Exists((Join-Path $run.root 'case/failure.json'))) { Get-Content (Join-Path $run.root 'case/failure.json') -Raw | ConvertFrom-Json } else { $null }
    Assert-True ($null -ne $failure -and [string]$failure.message -match 'RESOURCE_ALIAS') 'sealed-destination alias did not fail with RESOURCE_ALIAS'
}
Invoke-Case 'journal and decoder-output leaf hardlinks are rejected before external write' {
    foreach ($alias in @('journal-hardlink', 'verifier-stdout-hardlink')) {
        $foreign = Join-Path $OutputDir ('foreign/' + $alias + '-' + [Guid]::NewGuid().ToString('N'))
        [IO.Directory]::CreateDirectory($foreign) | Out-Null
        $sentinel = Join-Path $foreign 'sentinel.bin'
        $sentinelBytes = $script:Utf8.GetBytes('caller-owned-sentinel-28bytes!')
        [IO.File]::WriteAllBytes($sentinel, $sentinelBytes)
        $beforeHash = (Get-FileHash $sentinel -Algorithm SHA256).Hash
        $beforeLength = (Get-Item $sentinel).Length
        $run = Invoke-Collector -Name ('alias-' + $alias) -Case Normal -SaveFault None `
            -Environment @{ P45_FIXTURE_ALIAS = $alias; P45_FIXTURE_ALIAS_TARGET = $sentinel } `
            -AllowedDirtyPath $dirty
        Assert-True ($run.exit -ne 0) ($alias + ' leaf alias run unexpectedly succeeded')
        Assert-True (-not (Test-Path (Join-Path $run.root 'case/attempts/0002-after'))) ($alias + ' started AFTER after leaf rejection')
        $failure = if ([IO.File]::Exists((Join-Path $run.root 'case/failure.json'))) { Get-Content (Join-Path $run.root 'case/failure.json') -Raw | ConvertFrom-Json } else { $null }
        Assert-True ($null -ne $failure -and [string]$failure.message -match 'RESOURCE_ALIAS') ($alias + ' rejection did not name RESOURCE_ALIAS')
        Assert-True ((Get-FileHash $sentinel -Algorithm SHA256).Hash -eq $beforeHash) ($alias + ' changed the foreign sentinel bytes')
        Assert-True ((Get-Item $sentinel).Length -eq $beforeLength) ($alias + ' changed the foreign sentinel length')
    }
}
Invoke-Case 'deterministic handle-lifetime delete keeps a foreign sentinel intact' {
    $base = Join-Path $OutputDir ('delete-race/' + [Guid]::NewGuid().ToString('N'))
    $owned = Join-Path $base 'owned'
    $dir = Join-Path $owned 'dir'
    $foreign = Join-Path $base 'foreign'
    [IO.Directory]::CreateDirectory($dir) | Out-Null
    [IO.Directory]::CreateDirectory($foreign) | Out-Null
    $foreignFile = Join-Path $foreign 'local-main.save'
    [IO.File]::WriteAllText($foreignFile, 'FOREIGN_SENTINEL_KEEP', $script:Utf8)
    $expectedHash = (Get-FileHash $foreignFile -Algorithm SHA256).Hash
    $funcs = Get-CollectorFunctionText -Names @('Throw-Ctl', 'Get-FullPath', 'Initialize-OwnershipNative', 'Get-PathIdentity', 'Test-OwnedNativeHandle', 'Remove-OwnedFile')
    $driver = @'
param([string]$Root,[string]$Foreign,[string]$ExpectedHash)
$ErrorActionPreference='Stop'
$script:Utf8=New-Object System.Text.UTF8Encoding($false)
$script:OwnershipRoot=$Root
__FUNCTIONS__
$dir=Join-Path $Root 'dir'
$leaf=Join-Path $dir 'local-main.save'
[IO.File]::WriteAllText($leaf,'owned-delete-target',$script:Utf8)
$park=Join-Path $Root 'park'
$job=Start-Job -ScriptBlock {
    param($Dir,$Park,$Foreign)
    Start-Sleep -Milliseconds 500
    $moved=$false
    $attempted=$false
    try {
        $attempted=$true
        [IO.Directory]::Move($Dir,$Park)
        & cmd.exe /c mklink /J $Dir $Foreign | Out-Null
        $moved=$true
    } catch { }
    [pscustomobject]@{ moved=$moved; attempted=$attempted }
} -ArgumentList $dir,$park,$foreign
Remove-OwnedFile -Path $leaf -Context 'delete-race-probe' -HoldMilliseconds 2500
$completed=$null -ne (Wait-Job $job -Timeout 15)
$jr=@(Receive-Job $job)
Remove-Job $job -Force -ErrorAction SilentlyContinue
$moved=$false;$attempted=$false
if($jr.Count -gt 0){$moved=[bool]$jr[0].moved;$attempted=[bool]$jr[0].attempted}
$foreignHash=if(Test-Path (Join-Path $Foreign 'local-main.save')){(Get-FileHash (Join-Path $Foreign 'local-main.save') -Algorithm SHA256).Hash}else{''}
$originalPath=if($moved){Join-Path $Park 'local-main.save'}else{Join-Path $Dir 'local-main.save'}
[pscustomobject]@{
    moved=$moved
    attempted=$attempted
    completed=$completed
    foreignUnchanged=($foreignHash -eq $ExpectedHash)
    originalDeleted=(-not (Test-Path $originalPath))
} | ConvertTo-Json -Compress
'@.Replace('__FUNCTIONS__',$funcs)
    $driverPath = Join-Path $base 'driver.ps1'
    [IO.File]::WriteAllText($driverPath,$driver,$script:Utf8)
    $raw = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $driverPath -Root $owned -Foreign $foreign -ExpectedHash $expectedHash 2>&1
    $exit = $LASTEXITCODE
    Assert-True ($exit -eq 0) ('delete-race driver exit ' + $exit + ' output=' + ($raw -join ' '))
    $parsed = ($raw | Select-Object -Last 1) | ConvertFrom-Json
    Assert-True ([bool]$parsed.completed) 'delete-race worker did not complete'
    Assert-True ([bool]$parsed.attempted) 'delete-race worker never attempted the parent swap'
    Assert-True ([bool]$parsed.foreignUnchanged) 'handle-lifetime delete changed the foreign sentinel'
    Assert-True ([bool]$parsed.originalDeleted) 'the validated original leaf was not deleted through its handle'
    # moved=false is the stronger case: the validated open handle prevented the parent
    # from being swapped to a junction. moved=true is also accepted only when the
    # original object (not the foreign target) was deleted and the sentinel survived.
}
Invoke-Case 'actual unstable copy cleanup deletes the invalid sealed target' {
    $base = Join-Path $OutputDir ('copy-cleanup/' + [Guid]::NewGuid().ToString('N'))
    $owned = Join-Path $base 'owned'
    $foreign = Join-Path $base 'foreign'
    [IO.Directory]::CreateDirectory($owned) | Out-Null
    [IO.Directory]::CreateDirectory($foreign) | Out-Null
    $foreignFile = Join-Path $foreign 'sentinel.bin'
    [IO.File]::WriteAllText($foreignFile,'FOREIGN_SENTINEL_KEEP',$script:Utf8)
    $foreignHash = (Get-FileHash $foreignFile -Algorithm SHA256).Hash
    $funcs = Get-CollectorFunctionText -Names @('Throw-Ctl','Get-ErrText','Get-FullPath','Initialize-OwnershipNative','Get-PathIdentity','Assert-NoReparseAncestors','Assert-OwnedDirectory','Assert-OwnedRegularFile','Assert-OwnedWriteParent','Get-FileSig','Test-OwnedNativeHandle','Test-OwnedHandle','Copy-StableEvidence')
    $driver = @'
param([string]$Root,[string]$Foreign,[string]$ExpectedForeignHash)
$ErrorActionPreference='Stop'
$script:Utf8=New-Object System.Text.UTF8Encoding($false)
$script:OwnershipRoot=$Root
$script:DataRoot=$Root
$script:CaseRoot=$Root
__FUNCTIONS__
$source=Join-Path $Root 'source.bin'
$dest=Join-Path $Root 'dest.bin'
$bytes=New-Object byte[] (1048576)
[IO.File]::WriteAllBytes($source,$bytes)
$job=Start-Job -ScriptBlock {
    param($Source,$Dest)
    for($i=0;$i -lt 200;$i++){
        if(Test-Path $Dest){
            try{
                $fs=[IO.File]::Open($Source,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::ReadWrite)
                try{$fs.Position=0;$fs.WriteByte(0x5A);$fs.Flush()}finally{$fs.Dispose()}
                return [pscustomobject]@{mutated=$true}
            }catch{return [pscustomobject]@{mutated=$false;error=$_.Exception.Message}}
        }
        Start-Sleep -Milliseconds 20
    }
    [pscustomobject]@{mutated=$false;error='destination never appeared'}
} -ArgumentList $source,$dest
$errorText=''
try { $null=Copy-StableEvidence -Source $source -Destination $dest -ShareMode ReadWrite -HoldMilliseconds 1500 }
catch { $errorText=[string]$_.Exception.Message }
$null=Wait-Job $job -Timeout 15
$jr=@(Receive-Job $job)
Remove-Job $job -Force -ErrorAction SilentlyContinue
$mutated=$false; if($jr.Count -gt 0){$mutated=[bool]$jr[0].mutated}
$foreignHash=if(Test-Path (Join-Path $Foreign 'sentinel.bin')){(Get-FileHash (Join-Path $Foreign 'sentinel.bin') -Algorithm SHA256).Hash}else{''}
[pscustomobject]@{
    error=$errorText
    sourceMutated=$mutated
    destExists=(Test-Path $dest)
    foreignUnchanged=($foreignHash -eq $ExpectedForeignHash)
} | ConvertTo-Json -Compress
'@.Replace('__FUNCTIONS__',$funcs)
    $driverPath = Join-Path $base 'copy-driver.ps1'
    [IO.File]::WriteAllText($driverPath,$driver,$script:Utf8)
    $raw = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $driverPath -Root $owned -Foreign $foreign -ExpectedForeignHash $foreignHash 2>&1
    $exit = $LASTEXITCODE
    Assert-True ($exit -eq 0) ('copy cleanup driver exit ' + $exit + ' output=' + ($raw -join ' '))
    $parsed = ($raw | Select-Object -Last 1) | ConvertFrom-Json
    Assert-True ([bool]$parsed.sourceMutated) 'source mutation worker did not run in the copy window'
    Assert-True ([string]$parsed.error -match 'COPY_UNSTABLE') 'copy did not report COPY_UNSTABLE'
    Assert-True (-not [bool]$parsed.destExists) 'invalid sealed destination survived the unstable-copy cleanup'
    Assert-True ([bool]$parsed.foreignUnchanged) 'copy cleanup changed a foreign sentinel'
}
Invoke-Case 'missing/incomplete BEFORE structured export cannot progress to AFTER' {
    foreach ($mode in @('json', 'done', 'authority', 'cheat', 'json,done')) {
        $run = Invoke-Collector -Name ('missing-before-' + ($mode -replace '[,]', '-')) -Case Normal `
            -Environment @{ P45_FIXTURE_DELETE_EXPORT = $mode; P45_FIXTURE_DELETE_EXPORT_PHASE = 'BEFORE_RECOVERY' } `
            -AllowedDirtyPath $dirty
        Assert-True ($run.exit -ne 0) ('missing BEFORE ' + $mode + ' unexpectedly succeeded')
        Assert-True (-not (Test-Path (Join-Path $run.root 'case/attempts/0002-after'))) ('missing BEFORE ' + $mode + ' started AFTER')
        $failure = if ([IO.File]::Exists((Join-Path $run.root 'case/failure.json'))) { Get-Content (Join-Path $run.root 'case/failure.json') -Raw | ConvertFrom-Json } else { $null }
        Assert-True ($null -ne $failure -and [string]$failure.message -match 'BEFORE_EXPORT_INCOMPLETE|EXPORT_IDENTITY') ('missing BEFORE ' + $mode + ' did not fail with a BEFORE export/identity error')
        Assert-True ($null -eq $run.result -or -not [bool]$run.result.factsEqual) ('missing BEFORE ' + $mode + ' reported factsEqual')
    }
}
Invoke-Case 'a changed original after export is refused instead of re-sealed' {
    $run = Invoke-Collector -Name 'fixture-mutated-original' -Case Normal -Environment @{ P45_FIXTURE_MUTATE_AUTHORITY = '1' } -AllowedDirtyPath $dirty
    Assert-True ($run.exit -eq 19) ('expected changed-original identity rejection 19, actual ' + $run.exit)
    $failure = if ([IO.File]::Exists((Join-Path $run.root 'case/failure.json'))) { Get-Content (Join-Path $run.root 'case/failure.json') -Raw | ConvertFrom-Json } else { $null }
    Assert-True ($null -ne $failure -and [string]$failure.message -match 'EXPORT_IDENTITY') 'changed original did not produce EXPORT_IDENTITY'
}
Invoke-Case 'offline verifier accepts the sealed fixture case with a caller digest' {
    Assert-True ($null -ne $script:normal) 'Normal case was not produced'
    $case = Join-Path $script:normal.root 'case'
    $verify = Invoke-OfflineVerifier -CaseDir $case -ExpectedManifest (Get-ManifestHash -CaseDir $case)
    Assert-True ($verify.exit -eq 0) ('verifier exit ' + $verify.exit + ' failures=' + (($verify.result.failures) -join '; '))
    Assert-True ($verify.result.collectionChain -eq 'PASS') 'verifier chain was not PASS'
    Assert-True ($verify.result.factsEqual -eq $true) 'verifier factsEqual was not true'
    Assert-True ($verify.result.callerAcceptance -eq 'PENDING_INDEPENDENT_REVIEW') 'verifier self-accepted'
    Assert-True ($verify.result.gamePositive -eq $false) 'verifier reported a GAME positive'
}
Invoke-Case 'verifier accepts a clean source with an explicit empty dirtyPaths array' {
    Assert-True ($null -ne $script:normal) 'Normal case was not produced'
    $case = Join-Path $script:normal.root 'case'
    $input = Get-Content (Join-Path $case 'input-manifest.json') -Raw | ConvertFrom-Json
    Assert-True ($input.source.dirty -eq $false) 'the clean-source regression requires a clean worktree'
    Assert-True (@($input.source.dirtyPaths).Count -eq 0) 'clean source did not carry an explicit empty dirtyPaths array'
    $verify = Invoke-OfflineVerifier -CaseDir $case -ExpectedManifest (Get-ManifestHash -CaseDir $case)
    Assert-True ($verify.exit -eq 0) ('clean empty dirtyPaths exit ' + $verify.exit + ' failures=' + (($verify.result.failures) -join '; '))
    Assert-True ($verify.result.collectionChain -eq 'PASS') 'clean empty dirtyPaths chain was not PASS'
}
Invoke-Case 'verifier rejects a missing or malformed source.dirtyPaths' {
    Assert-True ($null -ne $script:normal) 'Normal case was not produced'
    foreach ($variant in @('missing', 'string', 'null')) {
        $copy = Copy-CaseForTamper -SourceCase (Join-Path $script:normal.root 'case') -Name ('dirty-shape-' + $variant)
        $input = Join-Path $copy 'input-manifest.json'
        Write-JsonMutable -Path $input -Mutator {
            param($d)
            if ($variant -eq 'missing') { $d.source.PSObject.Properties.Remove('dirtyPaths') }
            elseif ($variant -eq 'string') { $d.source.dirtyPaths = 'not-an-array' }
            else { $d.source.dirtyPaths = $null }
        }
        $hash = Update-CaseManifest -CaseDir $copy
        $verify = Invoke-OfflineVerifier -CaseDir $copy -ExpectedManifest $hash
        Assert-True ($verify.exit -ne 0) ('malformed dirtyPaths ' + $variant + ' became PASS')
        $joined = ($verify.result.failures -join '; ')
        Assert-True ($joined -match 'source HEAD and dirty state are frozen') ('wrong rejection reason for ' + $variant + ': ' + $joined)
    }
}
Invoke-Case 'wrong PID/start/path identity probe is rejected and the live process is untouched' {
    $me = Get-Process -Id $PID
    $realStart = $me.StartTime.ToUniversalTime().ToString('o', [Globalization.CultureInfo]::InvariantCulture)
    $realPath = $me.Path
    $args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script:Collector, '-ProbeIdentity',
        '-ProbePid', [string]$PID, '-ProbeStartTimeUtc', '2000-01-01T00:00:00.0000000Z',
        '-ProbePath', $realPath)
    & powershell.exe @args *> (Join-Path $OutputDir 'probe-wrong-start.log')
    Assert-True ($LASTEXITCODE -eq 19) ('wrong start probe should exit 19, actual ' + $LASTEXITCODE)
    $args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script:Collector, '-ProbeIdentity',
        '-ProbePid', [string]$PID, '-ProbeStartTimeUtc', $realStart,
        '-ProbePath', 'C:\Windows\System32\notepad.exe')
    & powershell.exe @args *> (Join-Path $OutputDir 'probe-wrong-path.log')
    Assert-True ($LASTEXITCODE -eq 19) ('wrong path probe should exit 19, actual ' + $LASTEXITCODE)
    Assert-True ([bool](Get-Process -Id $PID -ErrorAction SilentlyContinue)) 'the probe killed the live test process'
}
Invoke-Case 'foreign live PID impersonation is refused and the foreign process survives' {
    $bystander = Start-Process -FilePath $script:FixturePlayer -ArgumentList '--bystander' -PassThru -WindowStyle Hidden
    try {
        Start-Sleep -Milliseconds 500
        Assert-True (-not $bystander.HasExited) 'bystander did not start'
        $run = Invoke-Collector -Name 'fixture-foreign-pid' -Case Force -Environment @{ P45_FIXTURE_PID = [string]$bystander.Id } -AllowedDirtyPath $dirty
        Assert-True ($run.exit -eq 19) ('expected identity rejection 19, actual ' + $run.exit)
        Assert-True (-not $bystander.HasExited) 'collector killed a foreign live process'
        $failure = if ([IO.File]::Exists((Join-Path $run.root 'case/failure.json'))) { Get-Content (Join-Path $run.root 'case/failure.json') -Raw | ConvertFrom-Json } else { $null }
        Assert-True ($null -ne $failure -and [string]$failure.message -match 'EXPORT_IDENTITY') 'expected EXPORT_IDENTITY failure record'
    }
    finally { try { if (-not $bystander.HasExited) { $bystander.Kill() } } catch { } }
}
Invoke-Case 'a recomputed manifest cannot hide a wrong PID/start/path' {
    Assert-True ($null -ne $script:normal) 'Normal case was not produced'
    $copy = Copy-CaseForTamper -SourceCase (Join-Path $script:normal.root 'case') -Name 'wrong-pid'
    $export = Join-Path $copy 'attempts/0001-before/sealed/export.json'
    Write-JsonMutable -Path $export -Mutator { param($d) $d.pid = 999999; $d.processStartUtc = '2000-01-01T00:00:00.0000000Z'; $d.processPath = 'C:\Windows\System32\notepad.exe' }
    $done = Join-Path $copy 'attempts/0001-before/sealed/export.done'
    [IO.File]::WriteAllText($done, ((Get-FileHash $export -Algorithm SHA256).Hash + '  export.json' + [Environment]::NewLine), $script:Utf8)
    $hash = Update-CaseManifest -CaseDir $copy
    $verify = Invoke-OfflineVerifier -CaseDir $copy -ExpectedManifest $hash
    Assert-True ($verify.exit -ne 0) 'recomputed manifest with wrong PID/start/path became PASS'
    Assert-True ($verify.result.collectionChain -eq 'FAIL') 'wrong identity chain was not FAIL'
}
Invoke-Case 'a wrong case manifest digest is rejected before any trust decision' {
    Assert-True ($null -ne $script:normal) 'Normal case was not produced'
    $copy = Copy-CaseForTamper -SourceCase (Join-Path $script:normal.root 'case') -Name 'digest-binding'
    $original = Get-ManifestHash -CaseDir $copy
    [IO.File]::AppendAllText((Join-Path $copy 'manifest.json'), ' ', $script:Utf8)
    $verify = Invoke-OfflineVerifier -CaseDir $copy -ExpectedManifest $original
    Assert-True ($verify.exit -eq 4) ('expected digest mismatch 4, actual ' + $verify.exit)
}
Invoke-Case 'post-seal artifact truncation is detected after manifest recompute' {
    Assert-True ($null -ne $script:normal) 'Normal case was not produced'
    $copy = Copy-CaseForTamper -SourceCase (Join-Path $script:normal.root 'case') -Name 'truncate'
    $authority = Join-Path $copy 'attempts/0002-after/sealed/authority.save'
    $bytes = [IO.File]::ReadAllBytes($authority)
    [IO.File]::WriteAllBytes($authority, $bytes[0..([Math]::Max(1, [int]($bytes.Length / 2)))])
    $hash = Update-CaseManifest -CaseDir $copy
    $verify = Invoke-OfflineVerifier -CaseDir $copy -ExpectedManifest $hash
    Assert-True ($verify.exit -ne 0) 'truncated authority became PASS'
}
Invoke-Case 'missing sealed artifact is detected after manifest recompute' {
    Assert-True ($null -ne $script:normal) 'Normal case was not produced'
    $copy = Copy-CaseForTamper -SourceCase (Join-Path $script:normal.root 'case') -Name 'missing-artifact'
    Remove-Item (Join-Path $copy 'attempts/0002-after/sealed/export.done') -Force
    $hash = Update-CaseManifest -CaseDir $copy
    $verify = Invoke-OfflineVerifier -CaseDir $copy -ExpectedManifest $hash
    Assert-True ($verify.exit -ne 0) 'missing export.done became PASS'
}
Invoke-Case 'rewritten export facts/summary cannot override the independently decoded originals' {
    Assert-True ($null -ne $script:normal) 'Normal case was not produced'
    $copy = Copy-CaseForTamper -SourceCase (Join-Path $script:normal.root 'case') -Name 'rewrite-facts'
    $export = Join-Path $copy 'attempts/0002-after/sealed/export.json'
    Write-JsonMutable -Path $export -Mutator { param($d) $d.facts.stateVersion = '999'; $d.facts.physical.tileCount = 1 }
    [IO.File]::WriteAllText((Join-Path $copy 'attempts/0002-after/sealed/export.done'), ((Get-FileHash $export -Algorithm SHA256).Hash + '  export.json' + [Environment]::NewLine), $script:Utf8)
    $hash = Update-CaseManifest -CaseDir $copy
    $verify = Invoke-OfflineVerifier -CaseDir $copy -ExpectedManifest $hash
    Assert-True ($verify.exit -ne 0) 'rewritten export facts became PASS'
}
Invoke-Case 'old challenge / wrong phase / wrong attempt are rejected after manifest recompute' {
    Assert-True ($null -ne $script:normal) 'Normal case was not produced'
    $copy = Copy-CaseForTamper -SourceCase (Join-Path $script:normal.root 'case') -Name 'wrong-phase'
    $export = Join-Path $copy 'attempts/0002-after/sealed/export.json'
    Write-JsonMutable -Path $export -Mutator { param($d) $d.phase = 'BEFORE_RECOVERY'; $d.attempt = '0001'; $d.challenge = ('0' * 64) }
    [IO.File]::WriteAllText((Join-Path $copy 'attempts/0002-after/sealed/export.done'), ((Get-FileHash $export -Algorithm SHA256).Hash + '  export.json' + [Environment]::NewLine), $script:Utf8)
    $hash = Update-CaseManifest -CaseDir $copy
    $verify = Invoke-OfflineVerifier -CaseDir $copy -ExpectedManifest $hash
    Assert-True ($verify.exit -ne 0) 'wrong phase/attempt/challenge became PASS'
}
Invoke-Case 'copying another case manifest digest is rejected' {
    Assert-True ($null -ne $script:force -and $null -ne $script:normal) 'cases were not produced'
    $case = Join-Path $script:normal.root 'case'
    $other = Get-ManifestHash -CaseDir (Join-Path $script:force.root 'case')
    $verify = Invoke-OfflineVerifier -CaseDir $case -ExpectedManifest $other
    Assert-True ($verify.exit -eq 4) ('cross-case digest should exit 4, actual ' + $verify.exit)
}
Invoke-Case 'old public collector-style files cannot be re-sealed as a new trusted case' {
    $fake = Join-Path $OutputDir 'tamper/old-public'
    [IO.Directory]::CreateDirectory($fake) | Out-Null
    Copy-Item (Join-Path $TemplateDir 'authority.save') $fake -Force
    Copy-Item (Join-Path $TemplateDir 'cheat-state.bin') $fake -Force
    Copy-Item (Join-Path $TemplateDir 'export.json') $fake -Force
    $verify = Invoke-OfflineVerifier -CaseDir $fake -ExpectedManifest ('0' * 64)
    Assert-True ($verify.exit -ne 0) 'old public files became a trusted case'
}

$realSummary = New-Object System.Collections.Generic.List[object]
foreach ($entry in @($RealCases)) {
    if ([string]::IsNullOrWhiteSpace($entry)) { continue }
    $parts = $entry -split '=', 2
    $name = if ($parts.Count -eq 2) { $parts[0] } else { 'real-' + $realSummary.Count }
    $caseDir = if ($parts.Count -eq 2) { $parts[1] } else { $entry }
    Invoke-Case ('real sealed case: ' + $name) {
        Assert-True ([IO.Directory]::Exists($caseDir)) ('case not found: ' + $caseDir)
        $hash = Get-ManifestHash -CaseDir $caseDir
        $verify = Invoke-OfflineVerifier -CaseDir $caseDir -ExpectedManifest $hash
        Assert-True ($verify.exit -eq 0) ('real case verifier exit ' + $verify.exit + ' failures=' + (($verify.result.failures) -join '; '))
        Assert-True ($verify.result.collectionChain -eq 'PASS') 'real case chain was not PASS'
        Assert-True ($verify.result.gamePositive -eq $false) 'real case auto-accepted GAME'
        $realSummary.Add([pscustomobject]@{ name = $name; caseDir = $caseDir; manifest = $hash; verdict = $verify.result.recoveryResult; factsEqual = $verify.result.factsEqual; observed = $verify.result.observedOutcome }) | Out-Null
    }
}

$summary = [pscustomobject]@{
    schemaVersion = 1
    kind = 'p45-controlled-collection-test-summary'
    generatedAt = [DateTime]::UtcNow.ToString('o', [Globalization.CultureInfo]::InvariantCulture)
    total = $script:Total
    passed = $script:Passed
    failed = $script:Failed
    skipped = $script:Skipped
    cases = @($script:Cases.ToArray())
    realCases = @($realSummary.ToArray())
    fixtureOnly = $true
    gamePositive = $false
    callerAcceptance = 'PENDING_INDEPENDENT_REVIEW'
}
$summaryPath = Join-Path $OutputDir 'test-summary.json'
[IO.File]::WriteAllText($summaryPath, (($summary | ConvertTo-Json -Depth 20) + [Environment]::NewLine), $script:Utf8)
Write-Host ('SUMMARY ' + $summaryPath + ' passed=' + $script:Passed + ' failed=' + $script:Failed + ' skipped=' + $script:Skipped)
if ($script:Failed -gt 0) { exit 1 }
exit 0
