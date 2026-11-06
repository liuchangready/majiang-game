#Requires -Version 5.1
<#
  Dependency-free verification for the P4.5 Windows Player E2E tooling.

  It compiles a tiny auxiliary .NET Framework console process in the ignored
  build tree. That process is deliberately not a Mahjong Player and every
  artifact it creates is labelled TOOL_SELF_TEST. It is the only process this
  script stops or kills. No Pester or external module is required.
#>
[CmdletBinding()]
param(
    [string]$OutputDir,
    [switch]$KeepArtifacts
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$script:TestsRoot = $PSScriptRoot
$script:RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$script:Cli = Join-Path $script:RepoRoot 'tools\unity\run-p45-e2e.ps1'
$script:Common = Join-Path $script:RepoRoot 'tools\unity\p45-e2e-common.ps1'
$script:SchemaPath = Join-Path $script:RepoRoot 'tools\unity\p45-e2e-evidence.schema.json'
. $script:Common

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $script:RepoRoot ('build\p45-e2e-tooling\tests\' + (Get-P45TimeContext).localDate)
}
$OutputDir = Get-P45FullPath -Path $OutputDir
if ([IO.Directory]::Exists($OutputDir)) {
    $OutputDir = $OutputDir + '-retry-' + [Guid]::NewGuid().ToString('N').Substring(0, 6)
}
[IO.Directory]::CreateDirectory($OutputDir) | Out-Null

$script:CommandDir = Join-Path $OutputDir 'commands'
$script:HelperDir = Join-Path $OutputDir 'helper'
[IO.Directory]::CreateDirectory($script:CommandDir) | Out-Null
[IO.Directory]::CreateDirectory($script:HelperDir) | Out-Null

$script:StartedAt = Get-P45TimeContext
$script:Total = 0
$script:Passed = 0
$script:Failed = 0
$script:FirstFailure = $null
$script:Cases = New-Object System.Collections.Generic.List[object]
$script:CommandIndex = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Equal {
    param([object]$Expected, [object]$Actual, [string]$Message)
    if ([string]$Expected -ne [string]$Actual) {
        throw ($Message + ' (expected=' + [string]$Expected + ' actual=' + [string]$Actual + ')')
    }
}

function Assert-Contains {
    param([string]$Text, [string]$Needle, [string]$Message)
    if (-not $Text.Contains($Needle)) { throw ($Message + ' (missing ' + $Needle + ')') }
}

function Invoke-P45Cli {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [int]$TimeoutMilliseconds = 120000
    )

    $script:CommandIndex++
    $prefix = 'cmd-' + $script:CommandIndex.ToString('0000', [Globalization.CultureInfo]::InvariantCulture)
    $stdout = Join-Path $script:CommandDir ($prefix + '.stdout.log')
    $stderr = Join-Path $script:CommandDir ($prefix + '.stderr.log')
    $quoted = @()
    foreach ($arg in $Arguments) { $quoted += (Quote-P45Argument -Argument $arg) }
    $argText = '-NoProfile -ExecutionPolicy Bypass -File ' + (Quote-P45Argument -Argument $script:Cli) + ' ' + ($quoted -join ' ')

    # Capture the CLI's own stdout/stderr and exit code without Start-Process
    # -Wait, which would also wait for a no-wait child Player and hide the very
    # RUNNING state this test needs to observe.
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = 'powershell.exe'
    $psi.Arguments = $argText
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($psi)
    $outTask = $process.StandardOutput.ReadToEndAsync()
    $errTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($TimeoutMilliseconds)) {
        try { $process.Kill() } catch { }
        throw ('CLI command timed out: ' + ($Arguments -join ' '))
    }
    $process.Refresh()
    $exitCode = [int]$process.ExitCode
    $outText = $outTask.Result
    $errText = $errTask.Result
    $null = [IO.File]::WriteAllText($stdout, $outText, $script:P45Utf8)
    $null = [IO.File]::WriteAllText($stderr, $errText, $script:P45Utf8)

    $result = $null
    $resultMatches = [Text.RegularExpressions.Regex]::Matches($outText, '(?m)^P45_RESULT:\s*(\{.*\})\s*$')
    if ($resultMatches.Count -gt 0) {
        try { $result = $resultMatches[$resultMatches.Count - 1].Groups[1].Value | ConvertFrom-Json } catch { $result = $null }
    }
    [pscustomobject]@{
        ExitCode = $exitCode
        Stdout = $outText
        Stderr = $errText
        Result = $result
        StdoutPath = $stdout
        StderrPath = $stderr
        Command = ($Arguments -join ' ')
    }
}

function Start-P45CliAsync {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $script:CommandIndex++
    $prefix = 'async-' + $script:CommandIndex.ToString('0000', [Globalization.CultureInfo]::InvariantCulture)
    $stdout = Join-Path $script:CommandDir ($prefix + '.stdout.log')
    $stderr = Join-Path $script:CommandDir ($prefix + '.stderr.log')
    $quoted = @()
    foreach ($arg in $Arguments) { $quoted += (Quote-P45Argument -Argument $arg) }
    $argText = '-NoProfile -ExecutionPolicy Bypass -File ' + (Quote-P45Argument -Argument $script:Cli) + ' ' + ($quoted -join ' ')
    $process = Start-Process -FilePath 'powershell.exe' -ArgumentList $argText `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr `
        -WindowStyle Hidden -PassThru -ErrorAction Stop
    [pscustomobject]@{
        Process = $process
        StdoutPath = $stdout
        StderrPath = $stderr
    }
}

function Wait-P45CliAsync {
    param($Async, [int]$TimeoutMilliseconds = 30000)

    if (-not $Async.Process.WaitForExit($TimeoutMilliseconds)) {
        try { $Async.Process.Kill() } catch { }
        throw 'async CLI command did not exit after its child stopped'
    }
    $Async.Process.Refresh()
    return [int]$Async.Process.ExitCode
}

function Invoke-Case {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Body
    )

    $script:Total++
    try {
        & $Body
        $script:Passed++
        $script:Cases.Add([pscustomobject]@{ name = $Name; status = 'PASS'; error = '' }) | Out-Null
        Write-Host ('PASS ' + $Name)
    }
    catch {
        $script:Failed++
        $message = Get-P45ErrorMessage $_
        if ($null -eq $script:FirstFailure) { $script:FirstFailure = [pscustomobject]@{ name = $Name; error = $message } }
        $script:Cases.Add([pscustomobject]@{ name = $Name; status = 'FAIL'; error = $message }) | Out-Null
        Write-Host ('FAIL ' + $Name + ': ' + $message)
    }
}

function New-ObservationFixture {
    param(
        [Parameter(Mandatory = $true)]$RunRecord,
        [Parameter(Mandatory = $true)][string]$ObservationId,
        [Parameter(Mandatory = $true)][ValidateSet('BEFORE_RECOVERY', 'AFTER_RECOVERY', 'UNKNOWN')][string]$Phase,
        [string]$Source = 'TOOL_SELF_TEST',
        [string]$BuildCommit = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
        [string]$BuildProvenance = 'USER_DECLARED',
        [string]$RunIdOverride = '',
        [string]$SessionId = 'session-before',
        [int]$Cursor = 10,
        [string]$PhysicalHash = '3333333333333333333333333333333333333333333333333333333333333333',
        [bool]$LedgerAvailable = $true,
        [string]$LedgerHash = '2222222222222222222222222222222222222222222222222222222222222222'
    )

    $bindingRunId = if ([string]::IsNullOrWhiteSpace($RunIdOverride)) { [string]$RunRecord.runId } else { $RunIdOverride }
    $observation = [pscustomobject]@{
        schemaVersion = 1
        kind = 'p45-e2e-observation'
        observationId = $ObservationId
        source = $Source
        phase = $Phase
        capturedAt = (Get-P45TimeContext).local
        runBinding = [pscustomobject]@{
            runId = $bindingRunId
            playerSha256 = [string]$RunRecord.player.sha256
            sourceHead = [string]$RunRecord.source.head
            buildCommit = $BuildCommit
            buildProvenance = $BuildProvenance
            rulesetVersion = 1
        }
        availability = [pscustomobject]@{
            roundId = $true
            stateVersion = $true
            eventSeq = $true
            controlEpoch = $true
            requests = $true
            ledger = $LedgerAvailable
            physical = $true
            structuredPlayerExport = $true
        }
        roundId = 'local-main-r1'
        stateVersion = '12'
        eventSeq = '34'
        controlEpoch = '2'
        requests = @([pscustomobject]@{
            requestId = 'req-1'
            receiptId = 'rcpt-1'
            confirmed = $true
            payloadSha256 = '1111111111111111111111111111111111111111111111111111111111111111'
        })
        ledger = if ($LedgerAvailable) {
            [pscustomobject]@{
                points = '500'
                entryCount = 3
                entriesSha256 = $LedgerHash
            }
        }
        else { $null }
        physical = [pscustomobject]@{
            tileCount = 136
            tilesSha256 = $PhysicalHash
        }
        reconnect = [pscustomobject]@{
            sessionId = $SessionId
            cursor = $Cursor
        }
        integrity = [pscustomobject]@{
            algorithm = 'SHA-256'
            canonicalSha256 = ''
        }
    }
    $observation.integrity.canonicalSha256 = Get-P45ObservationDigest -Observation $observation
    return $observation
}

function Write-ObservationFixture {
    param($Observation, [string]$Path)
    $json = ($Observation | ConvertTo-Json -Depth 30) + [Environment]::NewLine
    [IO.File]::WriteAllText($Path, $json, $script:P45Utf8)
}

function New-FakeGameContext {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$BuildCommit,
        [string]$BuildProvenance = 'BUILD_LOG_ATTACHED',
        [switch]$NoBuildLog
    )

    $head = if ($BuildCommit -eq 'UNKNOWN') { ('f' * 40) } else { $BuildCommit }
    [IO.Directory]::CreateDirectory((Join-Path $Root 'evidence')) | Out-Null
    $run = [pscustomobject]@{
        runId = 'fake-game-run-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
        evidenceClass = 'GAME'
        player = [pscustomobject]@{
            sha256 = ('F' * 64)
            buildCommit = $BuildCommit
            buildProvenance = $BuildProvenance
            buildLog = if ($NoBuildLog) { $null } else { [pscustomobject]@{ path = 'fake-build.log'; sha256 = ('A' * 64) } }
        }
        source = [pscustomobject]@{
            head = $head
            dirty = $false
        }
    }
    [pscustomobject]@{
        Root = $Root
        Run = $run
        RunPath = (Join-Path $Root 'run.json')
    }
}

function New-FakeSaveHeaderFile {
    param([string]$Path)

    $payloadStream = New-Object IO.MemoryStream
    $writer = New-Object IO.BinaryWriter($payloadStream, $script:P45Utf8, $true)
    try {
        $writer.Write(1)
        $roomBytes = $script:P45Utf8.GetBytes('local-main')
        $writer.Write($roomBytes.Length)
        $writer.Write($roomBytes)
        $writer.Write([byte]0)
        $writer.Write(1)
        $writer.Write([uint64]0)
        $writer.Write([uint64]0)
        $writer.Write([uint64]12345)
        $writer.Write(32)
        $writer.Write((New-Object byte[] 32))
        $writer.Write([int64]0)
        $writer.Write([uint64]1)
        $writer.Write([int64]0)
        $writer.Write([int64]0)
        $writer.Write([uint64]0)
        $writer.Write($false)
        $writer.Flush()
        $payload = $payloadStream.ToArray()
    }
    finally {
        $writer.Dispose()
        $payloadStream.Dispose()
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $payloadHash = $sha.ComputeHash($payload) } finally { $sha.Dispose() }
    $envelope = New-Object IO.MemoryStream
    $envelopeWriter = New-Object IO.BinaryWriter($envelope, $script:P45Utf8, $true)
    try {
        $envelopeWriter.Write($script:P45Utf8.GetBytes('P4HL'))
        $envelopeWriter.Write(1)
        $envelopeWriter.Write($payload.Length)
        $envelopeWriter.Write($payloadHash)
        $envelopeWriter.Write($payload)
        $envelopeWriter.Flush()
    }
    finally {
        $envelopeWriter.Dispose()
        $envelope.Dispose()
    }
    [IO.File]::WriteAllBytes($Path, $envelope.ToArray())
}

function Assert-SchemaRequired {
    param([object]$Object, [string]$DefinitionName)

    $schema = Read-P45Json -Path $script:SchemaPath
    $definition = Get-P45Property (Get-P45Property $schema '$defs') $DefinitionName
    Assert-True ($null -ne $definition) ('schema definition missing: ' + $DefinitionName)
    $requiredProperty = $definition.PSObject.Properties['required']
    Assert-True ($null -ne $requiredProperty) ('schema ' + $DefinitionName + ' has no required list')
    foreach ($required in @($requiredProperty.Value)) {
        $valueProperty = $Object.PSObject.Properties[$required]
        Assert-True ($null -ne $valueProperty) ('schema ' + $DefinitionName + ' missing required field ' + $required)
    }
    $schemaVersionProperty = $Object.PSObject.Properties['schemaVersion']
    Assert-True ($null -ne $schemaVersionProperty) ('schema ' + $DefinitionName + ' missing schemaVersion')
    Assert-Equal 1 $schemaVersionProperty.Value ('schema ' + $DefinitionName + ' version')
}

Write-Host ('P45 E2E TOOL SELF TEST: ' + $OutputDir)
Write-Host ('CLI: ' + $script:Cli)

$helperSource = @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

public static class Program
{
    private static string Get(Dictionary<string, string> map, string key, string fallback)
    {
        string value;
        return map.TryGetValue(key, out value) ? value : fallback;
    }

    public static int Main(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            string key = args[i].Substring(2);
            string value = (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                ? args[++i]
                : "1";
            map[key] = value;
        }

        string mode = Get(map, "mode", "hold");
        string dataDir = Get(map, "data-dir", "");
        string logFile = Get(map, "log-file", "");
        string argsOut = Get(map, "args-out", "");
        string stopFile = Get(map, "stop-file", "");
        string runId = Get(map, "run-id", "");
        int holdSeconds = 60;
        int.TryParse(Get(map, "hold-seconds", "60"), NumberStyles.Integer, CultureInfo.InvariantCulture, out holdSeconds);

        if (dataDir.Length > 0) Directory.CreateDirectory(dataDir);
        if (logFile.Length > 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logFile));
            File.AppendAllText(logFile, "HELPER_START mode=" + mode + " run=" + runId + Environment.NewLine, new UTF8Encoding(false));
        }
        if (argsOut.Length > 0) File.WriteAllLines(argsOut, args, new UTF8Encoding(false));
        if (dataDir.Length > 0)
        {
            string state = Path.Combine(dataDir, "helper-state.txt");
            int count = 0;
            if (File.Exists(state))
            {
                string first = File.ReadAllText(state, new UTF8Encoding(false)).Split('\n')[0].Trim();
                int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out count);
            }
            count++;
            File.WriteAllText(
                state,
                count.ToString(CultureInfo.InvariantCulture) + Environment.NewLine +
                "pid=" + System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + Environment.NewLine +
                "run=" + runId + Environment.NewLine +
                "env=" + (Environment.GetEnvironmentVariable("P45_E2E_RUN_ID") ?? "") + Environment.NewLine,
                new UTF8Encoding(false));
        }

        if (mode == "exit0") return 0;
        if (mode == "exit7") return 7;
        if (mode == "autoexit") { Thread.Sleep(900); return 0; }

        DateTime deadline = DateTime.UtcNow.AddSeconds(holdSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (stopFile.Length > 0 && File.Exists(stopFile)) return 0;
            Thread.Sleep(100);
        }
        return 0;
    }
}
'@

$helperSourcePath = Join-Path $script:HelperDir 'P45SelfTestChild.cs'
$helperExe = Join-Path $script:HelperDir 'P45SelfTestChild.exe'
[IO.File]::WriteAllText($helperSourcePath, $helperSource, $script:P45Utf8)
if ([IO.File]::Exists($helperExe)) { Remove-Item -LiteralPath $helperExe -Force }
Add-Type -Path $helperSourcePath -OutputAssembly $helperExe -OutputType ConsoleApplication -ErrorAction Stop
Assert-True ([IO.File]::Exists($helperExe)) 'helper compilation did not produce an exe'
$helperHash = Get-P45Sha256 -Path $helperExe

$testRoot = Join-Path $OutputDir '工具 路径 测试'
$runA = Join-Path $testRoot 'run A'
$runB = Join-Path $testRoot 'run B'
$runC = Join-Path $testRoot 'run C empty'
[IO.Directory]::CreateDirectory($testRoot) | Out-Null

$script:RunAContext = $null
$script:RunBContext = $null

Invoke-Case 'Prepare creates isolated run, ownership, source/player identity, NOT_RUN scenarios' {
    $result = Invoke-P45Cli @(
        'Prepare', '-RunDir', $runA, '-PlayerPath', $helperExe,
        '-EvidenceClass', 'TOOL_SELF_TEST', '-Mode', 'ToolSelfTest',
        '-BuildProvenance', 'USER_DECLARED', '-BuildCommit', ('a' * 40),
        '-BuildSourceNote', 'P45 self-test helper, not a Mahjong Player',
        '-ExpectedPlayerSha256', $helperHash
    )
    Assert-Equal 0 $result.ExitCode 'Prepare exit'
    Assert-True ($null -ne $result.Result) 'Prepare did not emit P45_RESULT'
    $run = Read-P45Json -Path (Join-Path $runA 'run.json')
    $marker = Read-P45Json -Path (Join-Path $runA 'ownership.json')
    Assert-SchemaRequired -Object $run -DefinitionName 'run'
    Assert-SchemaRequired -Object $marker -DefinitionName 'ownership'
    Assert-Equal 'TOOL_SELF_TEST' $run.evidenceClass 'evidence class'
    Assert-Equal 'ToolSelfTest' $run.launch.mode 'launch mode'
    Assert-Equal $helperHash $run.player.sha256 'player hash'
    Assert-Equal $false $run.player.buildCommitMatchesSourceHead '40-a build must not match source head'
    Assert-Equal 17 @($run.scenarios).Count 'scenario count'
    foreach ($scenario in @($run.scenarios)) { Assert-Equal 'NOT_RUN' $scenario.status ('scenario ' + $scenario.name) }
    Assert-Equal 'Asia/Shanghai' $run.time.label 'time zone label'
    Assert-Equal '+08:00' $run.time.utcOffset 'explicit UTC offset'
    Assert-True ($run.source.statusSha256 -match '^[A-F0-9]{64}$') 'source status digest'
    Assert-True ([IO.Directory]::Exists($run.paths.localSave)) 'local save directory'
    Assert-True ([IO.Directory]::Exists($run.paths.clientOutbox)) 'client outbox directory'
    Assert-True ([IO.Directory]::Exists($run.paths.log)) 'log directory'
    Assert-True ([IO.Directory]::Exists($run.paths.evidence)) 'evidence directory'
    Assert-Equal $marker.ownerToken $marker.ownerToken 'marker token must exist'
    $script:RunAContext = Get-P45Run -RunDir $runA

    $beforeMarkerHash = Get-P45Sha256 -Path (Join-Path $runA 'ownership.json')
    $duplicate = Invoke-P45Cli @(
        'Prepare', '-RunDir', $runA, '-PlayerPath', $helperExe,
        '-EvidenceClass', 'TOOL_SELF_TEST', '-Mode', 'ToolSelfTest',
        '-BuildProvenance', 'USER_DECLARED', '-BuildCommit', ('a' * 40)
    )
    Assert-Equal 3 $duplicate.ExitCode 'duplicate Prepare must be refused'
    Assert-Equal $beforeMarkerHash (Get-P45Sha256 -Path (Join-Path $runA 'ownership.json')) 'old ownership must be untouched'
}

Invoke-Case 'Exit 0 is EXITED_OK and exact Chinese/space arguments reach the child' {
    $result = Invoke-P45Cli @('Launch', '-RunDir', $runA, '-Mode', 'ToolSelfTest', '-Wait', '-TimeoutSeconds', '20', '-SelfTestMode', 'exit0')
    Assert-Equal 0 $result.ExitCode 'exit0 tool exit'
    $launch = Read-P45Json -Path (Join-Path $runA 'attempts\0001\launch.json')
    $exit = Read-P45Json -Path (Join-Path $runA 'attempts\0001\exit.json')
    Assert-SchemaRequired -Object $launch -DefinitionName 'attempt'
    Assert-SchemaRequired -Object $exit -DefinitionName 'exit'
    Assert-Equal 'EXITED' $launch.status 'launch status'
    Assert-Equal 'EXITED_OK' $exit.disposition 'exit disposition'
    Assert-Equal 0 $exit.nativeExitCode 'native exit'
    Assert-Equal $true $launch.hidden 'default hidden'
    Assert-Equal $false $launch.visible 'default not visible'
    Assert-Equal $script:RunAContext.Run.runId $launch.runId 'attempt run id'

    $argsPath = Join-Path $runA 'attempts\0001\helper-args.txt'
    Assert-True ([IO.File]::Exists($argsPath)) 'helper args file'
    $lines = @([IO.File]::ReadAllLines($argsPath, $script:P45Utf8))
    $dataIndex = [Array]::IndexOf($lines, '--data-dir')
    Assert-True ($dataIndex -ge 0) '--data-dir present'
    Assert-Equal (Join-Path $runA 'local') $lines[$dataIndex + 1] 'absolute save root survived spaces/Chinese'
    $outboxIndex = [Array]::IndexOf($lines, '--outbox-dir')
    Assert-Equal (Join-Path $runA 'client\outbox') $lines[$outboxIndex + 1] 'absolute outbox survived spaces/Chinese'
    $runIndex = [Array]::IndexOf($lines, '--run-id')
    Assert-Equal $script:RunAContext.Run.runId $lines[$runIndex + 1] 'run id argument'
    Assert-True ($launch.command.argumentsText.Contains('"')) 'arguments with spaces are quoted, not shell-evaluated'
}

Invoke-Case 'Nonzero native exit is failure (10), never exit 0/PASS' {
    $result = Invoke-P45Cli @('Launch', '-RunDir', $runA, '-Mode', 'ToolSelfTest', '-Wait', '-TimeoutSeconds', '20', '-SelfTestMode', 'exit7')
    Assert-Equal 10 $result.ExitCode 'nonzero tool exit'
    $exit = Read-P45Json -Path (Join-Path $runA 'attempts\0002\exit.json')
    Assert-Equal 7 $exit.nativeExitCode 'native exit'
    Assert-Equal 'EXITED_NONZERO' $exit.disposition 'disposition'
    Assert-Equal 10 $exit.toolExitCode 'tool exit recorded separately'
    Assert-True ($result.Result.status -ne 'PASS') 'nonzero must not be PASS'
}

Invoke-Case 'Timeout fails 11, records TOOL_TIMEOUT and terminates only the owned child' {
    $result = Invoke-P45Cli @('Launch', '-RunDir', $runA, '-Mode', 'ToolSelfTest', '-Wait', '-TimeoutSeconds', '2', '-SelfTestMode', 'hold', '-SelfTestHoldSeconds', '60')
    Assert-Equal 11 $result.ExitCode 'timeout tool exit'
    $launch = Read-P45Json -Path (Join-Path $runA 'attempts\0003\launch.json')
    $exit = Read-P45Json -Path (Join-Path $runA 'attempts\0003\exit.json')
    Assert-Equal 'TIMED_OUT' $launch.disposition 'launch timeout'
    Assert-Equal 'TIMED_OUT' $exit.disposition 'exit timeout'
    Assert-Equal 'TOOL_TIMEOUT' $exit.killReason 'kill reason'
    Assert-Equal $true $exit.requested 'tool-requested'
    Assert-True ($null -eq (Get-Process -Id $launch.pid -ErrorAction SilentlyContinue)) 'timed-out child must be gone'
}

Invoke-Case 'Force Stop kills only the run-owned process and records FORCE_KILLED' {
    $async = Start-P45CliAsync @('Launch', '-RunDir', $runA, '-Mode', 'ToolSelfTest', '-SelfTestMode', 'hold', '-SelfTestHoldSeconds', '60')
    $childPid = $null
    for ($i = 0; $i -lt 150; $i++) {
        Start-Sleep -Milliseconds 100
        try {
            $state = Read-P45Json -Path (Join-Path $runA 'state.json')
            if ($null -ne $state.activeProcess) { $childPid = [int]$state.activeProcess.pid; break }
        }
        catch { }
    }
    Assert-True ($null -ne $childPid) 'no-wait launch did not become active'
    $launchExit = Wait-P45CliAsync -Async $async -TimeoutMilliseconds 20000
    Assert-Equal 0 $launchExit 'no-wait launch exits 0 RUNNING'
    Assert-True ($null -ne (Get-Process -Id $childPid -ErrorAction SilentlyContinue)) 'child running before stop'
    $stop = Invoke-P45Cli @('Stop', '-RunDir', $runA, '-GraceSeconds', '1', '-Force')
    Assert-Equal 0 $stop.ExitCode 'force stop exit'
    $exit = Read-P45Json -Path (Join-Path $runA 'attempts\0004\exit.json')
    Assert-Equal 'FORCE_KILLED' $exit.disposition 'force disposition'
    Assert-Equal 'TOOL_FORCE' $exit.killReason 'force reason'
    Assert-True ($null -eq (Get-Process -Id $childPid -ErrorAction SilentlyContinue)) 'forced child gone'
}

Invoke-Case 'Graceful Stop uses the stop request and records native 0' {
    $prefix = 'async-graceful-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $asyncOut = Join-Path $script:CommandDir ($prefix + '.stdout.log')
    $asyncErr = Join-Path $script:CommandDir ($prefix + '.stderr.log')
    $launchArgs = @('Launch', '-RunDir', $runA, '-Mode', 'ToolSelfTest', '-Wait', '-TimeoutSeconds', '60', '-SelfTestMode', 'hold', '-SelfTestHoldSeconds', '60')
    $quoted = @()
    foreach ($arg in $launchArgs) { $quoted += (Quote-P45Argument -Argument $arg) }
    $argText = '-NoProfile -ExecutionPolicy Bypass -File ' + (Quote-P45Argument -Argument $script:Cli) + ' ' + ($quoted -join ' ')
    $launchProcess = Start-Process -FilePath 'powershell.exe' -ArgumentList $argText `
        -RedirectStandardOutput $asyncOut -RedirectStandardError $asyncErr -WindowStyle Hidden -PassThru -ErrorAction Stop

    $activePid = $null
    for ($i = 0; $i -lt 150; $i++) {
        Start-Sleep -Milliseconds 100
        try {
            $state = Read-P45Json -Path (Join-Path $runA 'state.json')
            if ($null -ne $state.activeProcess) { $activePid = [int]$state.activeProcess.pid; break }
        }
        catch { }
    }
    Assert-True ($null -ne $activePid) 'async wait launch did not become active'
    Assert-True ($null -ne (Get-Process -Id $activePid -ErrorAction SilentlyContinue)) 'async child running'
    $stop = Invoke-P45Cli @('Stop', '-RunDir', $runA, '-GraceSeconds', '8')
    Assert-Equal 0 $stop.ExitCode 'graceful stop exit'
    $null = $launchProcess.WaitForExit(20000)
    $exit = Read-P45Json -Path (Join-Path $runA 'attempts\0005\exit.json')
    Assert-Equal 'GRACEFUL_STOPPED' $exit.disposition 'graceful disposition'
    Assert-Equal 0 $exit.nativeExitCode 'graceful native exit'
    Assert-True ($null -eq (Get-Process -Id $activePid -ErrorAction SilentlyContinue)) 'graceful child gone'
}

Invoke-Case 'Restart preserves the data domain, adds a new attempt and does not clear state' {
    $stateBefore = [IO.File]::ReadAllText((Join-Path $runA 'local\helper-state.txt'), $script:P45Utf8)
    $countBefore = [int]($stateBefore.Split("`n")[0])
    $result = Invoke-P45Cli @('Restart', '-RunDir', $runA, '-Mode', 'ToolSelfTest', '-Wait', '-TimeoutSeconds', '20', '-SelfTestMode', 'autoexit')
    Assert-Equal 0 $result.ExitCode 'restart exit'
    $stateAfter = [IO.File]::ReadAllText((Join-Path $runA 'local\helper-state.txt'), $script:P45Utf8)
    $countAfter = [int]($stateAfter.Split("`n")[0])
    Assert-Equal ($countBefore + 1) $countAfter 'helper data domain retained one state file and incremented'
    Assert-True (Test-Path (Join-Path $runA 'attempts\0006\launch.json')) 'new launch evidence'
    Assert-True (Test-Path (Join-Path $runA 'attempts\0005\launch.json')) 'old launch evidence retained'
    Assert-True ($stateAfter.Contains('run=' + $script:RunAContext.Run.runId)) 'same run id persisted'
}

Invoke-Case 'Empty run Collect is refused and leaves no empty PASS manifest' {
    $prep = Invoke-P45Cli @(
        'Prepare', '-RunDir', $runC, '-PlayerPath', $helperExe,
        '-EvidenceClass', 'TOOL_SELF_TEST', '-Mode', 'ToolSelfTest',
        '-BuildProvenance', 'USER_DECLARED', '-BuildCommit', ('a' * 40)
    )
    Assert-Equal 0 $prep.ExitCode 'empty run prepare'
    $collect = Invoke-P45Cli @('Collect', '-RunDir', $runC)
    Assert-Equal 14 $collect.ExitCode 'empty collect must fail 14'
    Assert-True (-not (Test-Path (Join-Path $runC 'evidence\collections'))) 'no collection directory'
}

Invoke-Case 'Ownership/binding refusal never kills another run child' {
    $prepB = Invoke-P45Cli @(
        'Prepare', '-RunDir', $runB, '-PlayerPath', $helperExe,
        '-EvidenceClass', 'TOOL_SELF_TEST', '-Mode', 'ToolSelfTest',
        '-BuildProvenance', 'USER_DECLARED', '-BuildCommit', ('a' * 40)
    )
    Assert-Equal 0 $prepB.ExitCode 'run B prepare'
    $launchAsync = Start-P45CliAsync @('Launch', '-RunDir', $runA, '-Mode', 'ToolSelfTest', '-SelfTestMode', 'hold', '-SelfTestHoldSeconds', '60')
    $pidA = $null
    for ($i = 0; $i -lt 150; $i++) {
        Start-Sleep -Milliseconds 100
        try {
            $stateA = Read-P45Json -Path (Join-Path $runA 'state.json')
            if ($null -ne $stateA.activeProcess) { $pidA = [int]$stateA.activeProcess.pid; break }
        }
        catch { }
    }
    Assert-True ($null -ne $pidA) 'run A launch for ownership test did not become active'
    $launchExitA = Wait-P45CliAsync -Async $launchAsync -TimeoutMilliseconds 20000
    Assert-Equal 0 $launchExitA 'run A launch for ownership test exits 0'
    Assert-True ($null -ne (Get-Process -Id $pidA -ErrorAction SilentlyContinue)) 'run A child alive'
    $stateA = Read-P45Json -Path (Join-Path $runA 'state.json')

    $stateB = Read-P45Json -Path (Join-Path $runB 'state.json')
    $forged = $stateA.activeProcess
    $stateB.activeProcess = $forged
    Write-P45JsonAtomic -Path (Join-Path $runB 'state.json') -Object $stateB
    $stopB = Invoke-P45Cli @('Stop', '-RunDir', $runB, '-GraceSeconds', '1', '-Force')
    Assert-Equal 4 $stopB.ExitCode 'foreign ownerRunId must be refused'
    Assert-True ($null -ne (Get-Process -Id $pidA -ErrorAction SilentlyContinue)) 'run A child survived run B owner refusal'

    $runBRecord = Read-P45Json -Path (Join-Path $runB 'run.json')
    $stateB = Read-P45Json -Path (Join-Path $runB 'state.json')
    $forged.ownerRunId = [string]$runBRecord.runId
    $stateB.activeProcess = $forged
    # The owner id now passes B's first check, but A's real command line
    # contains A's save root, not B's. The binding check must refuse.
    Write-P45JsonAtomic -Path (Join-Path $runB 'state.json') -Object $stateB
    $stopB2 = Invoke-P45Cli @('Stop', '-RunDir', $runB, '-GraceSeconds', '1', '-Force')
    Assert-Equal 4 $stopB2.ExitCode 'run binding mismatch must be refused'
    Assert-True ($null -ne (Get-Process -Id $pidA -ErrorAction SilentlyContinue)) 'run A child survived binding refusal'

    $cleanup = Invoke-P45Cli @('Stop', '-RunDir', $runA, '-GraceSeconds', '1', '-Force')
    Assert-Equal 0 $cleanup.ExitCode 'legitimate owner can stop A'
    $null = Wait-P45CliAsync -Async $launchAsync -TimeoutMilliseconds 30000
    $script:RunBContext = Get-P45Run -RunDir $runB
}

Invoke-Case 'Collect writes a non-self-referential manifest with verifiable hashes, no overwrite' {
    $saveDir = Join-Path $runA 'local'
    [IO.Directory]::CreateDirectory($saveDir) | Out-Null
    New-FakeSaveHeaderFile -Path (Join-Path $saveDir 'local-main.save')

    $first = Invoke-P45Cli @('Collect', '-RunDir', $runA)
    Assert-Equal 0 $first.ExitCode 'first collect'
    $manifestPath = Join-Path $runA 'evidence\collections\0001\manifest.json'
    Assert-True (Test-Path $manifestPath) 'manifest exists'
    $manifest = Read-P45Json -Path $manifestPath
    Assert-SchemaRequired -Object $manifest -DefinitionName 'manifest'
    Assert-True (@($manifest.entries).Count -gt 0) 'manifest has entries'
    foreach ($entry in @($manifest.entries)) {
        Assert-True ($entry.artifactPath -ne 'manifest.json') 'manifest excludes itself'
        Assert-True ($entry.artifactPath -ne 'manifest.sha256.txt') 'manifest excludes sidecar'
        $artifact = Join-Path (Split-Path -Parent $manifestPath) $entry.artifactPath
        Assert-True (Test-Path $artifact) ('artifact exists: ' + $entry.artifactPath)
        Assert-Equal (Get-Item -LiteralPath $artifact).Length $entry.bytes ('artifact size: ' + $entry.artifactPath)
        Assert-Equal (Get-P45Sha256 -Path $artifact) $entry.sha256 ('artifact hash: ' + $entry.artifactPath)
    }
    $sidecar = ([IO.File]::ReadAllText((Join-Path $runA 'evidence\collections\0001\manifest.sha256.txt'), $script:P45Utf8)).Trim()
    Assert-Equal (Get-P45Sha256 -Path $manifestPath) ($sidecar.Split(' ')[0]) 'sidecar hash'
    $verify = Invoke-P45Cli @('Verify', '-ManifestPath', $manifestPath)
    Assert-Equal 0 $verify.ExitCode 'manifest verify'
    $firstHash = Get-P45Sha256 -Path $manifestPath

    $second = Invoke-P45Cli @('Collect', '-RunDir', $runA)
    Assert-Equal 0 $second.ExitCode 'second collect'
    Assert-True (Test-Path (Join-Path $runA 'evidence\collections\0002\manifest.json')) 'new collection number'
    Assert-Equal $firstHash (Get-P45Sha256 -Path $manifestPath) 'old manifest not overwritten'
    $verify2 = Invoke-P45Cli @('Verify', '-ManifestPath', (Join-Path $runA 'evidence\collections\0002\manifest.json'))
    Assert-Equal 0 $verify2.ExitCode 'second manifest verify'
    Assert-True (Test-Path (Join-Path $runA 'evidence\collections\0002\normalized\local-main.save.observation.json')) 'save header observation archived'
    $saveObs = Read-P45Json -Path (Join-Path $runA 'evidence\collections\0002\normalized\local-main.save.observation.json')
    Assert-SchemaRequired -Object $saveObs -DefinitionName 'observation'
    Assert-Equal 'SAVE_HEADER_EXTRACT' $saveObs.source 'save source'
    Assert-Equal '0' $saveObs.stateVersion 'save header state version'
    Assert-Equal $false $saveObs.availability.ledger 'save header cannot prove ledger'

    $tamperDir = Join-Path $runA 'evidence\collections\0002-tampered'
    Copy-Item -LiteralPath (Join-Path $runA 'evidence\collections\0002') -Destination $tamperDir -Recurse
    $tamperLog = Get-ChildItem -LiteralPath (Join-Path $tamperDir 'logs') -File | Select-Object -First 1
    [IO.File]::AppendAllText($tamperLog.FullName, 'tamper', $script:P45Utf8)
    $tamperVerify = Invoke-P45Cli @('Verify', '-ManifestPath', (Join-Path $tamperDir 'manifest.json'))
    Assert-Equal 15 $tamperVerify.ExitCode 'tampered manifest must fail 15'
}

Invoke-Case 'Collect redacts JSON/header secrets and withholds unsafe public logs' {
    $logRoot = Join-Path $runA 'log'
    [IO.Directory]::CreateDirectory($logRoot) | Out-Null
    $jsonSecret = 'example-json-secret-value-7f3a'
    $bearerSecret = 'example-bearer-value-9c2d'
    $opaqueSecret = 'example-opaque-value-4e8b'
    $nestedSecret = 'example-nested-secret-value-1a6c'
    $escapedSecret = 'example-escaped-secret-value-3d5e'
    $encodedSecret = 'example-nested-encoded-secret-value-6c7d'
    $incompleteSecret = 'example-incomplete-secret-value-8b1f'
    $safeLog = @(
        'INFO harmless line before JSON',
        '[Physics::Module] harmless non-JSON bracket prefix',
        ('{"ticket":"' + $jsonSecret + '","token":"' + $jsonSecret + '","authorization":"Bearer ' + $bearerSecret + '","opaque":"' + $opaqueSecret + '"}'),
        ('Authorization: Bearer ' + $bearerSecret),
        ('opaque=' + $opaqueSecret),
        'INFO harmless line after JSON'
    ) -join "`n"
    $unsafeLog = '{"ticket":{"nested":"' + $nestedSecret + '"}}'
    $escapedLog = 'INFO {"\u0074icket":"' + $escapedSecret + '"}'
    $encodedLog = 'INFO {"message":"{\"\u0074icket\":\"' + $encodedSecret + '\"}"}'
    $incompleteLog = 'INFO {"\u0074icket":"' + $incompleteSecret + '"'
    $safePath = Join-Path $logRoot 'redaction-sample.log'
    $unsafePath = Join-Path $logRoot 'redaction-unsafe.log'
    $escapedPath = Join-Path $logRoot 'redaction-escaped.log'
    $encodedPath = Join-Path $logRoot 'redaction-encoded.log'
    $incompletePath = Join-Path $logRoot 'redaction-incomplete.log'
    [IO.File]::WriteAllText($safePath, $safeLog, $script:P45Utf8)
    [IO.File]::WriteAllText($unsafePath, $unsafeLog, $script:P45Utf8)
    [IO.File]::WriteAllText($escapedPath, $escapedLog, $script:P45Utf8)
    [IO.File]::WriteAllText($encodedPath, $encodedLog, $script:P45Utf8)
    [IO.File]::WriteAllText($incompletePath, $incompleteLog, $script:P45Utf8)

    $direct = Get-P45RedactedLog -Text ('{"ticket":"' + $jsonSecret + '","token":"' + $jsonSecret + '"}')
    Assert-True (-not $direct.Contains($jsonSecret)) 'direct JSON redaction removed ticket/token values'
    Assert-True ($direct.Contains('<REDACTED>')) 'direct JSON redaction used placeholder'
    $safeAssessment = Get-P45LogRedactionAssessment -Text $safeLog
    Assert-Equal $true $safeAssessment.Safe 'mixed JSON/header sample must be safely redactable'
    $unsafeAssessment = Get-P45LogRedactionAssessment -Text $unsafeLog
    Assert-Equal $false $unsafeAssessment.Safe 'nested JSON under sensitive key must be unsafe'
    $escapedAssessment = Get-P45LogRedactionAssessment -Text $escapedLog
    Assert-Equal $true $escapedAssessment.Safe 'prefixed escaped-key JSON must be handled'
    Assert-True (-not $escapedAssessment.Text.Contains($escapedSecret)) 'escaped-key JSON secret removed'
    Assert-True ($escapedAssessment.Text.Contains('<REDACTED-JSON>')) 'escaped-key JSON whole value redacted'
    $encodedAssessment = Get-P45LogRedactionAssessment -Text $encodedLog
    Assert-Equal $true $encodedAssessment.Safe 'nested encoded JSON must be handled'
    Assert-True (-not $encodedAssessment.Text.Contains($encodedSecret)) 'nested encoded JSON secret removed'
    Assert-True ($encodedAssessment.Text.Contains('<REDACTED-JSON>')) 'nested encoded JSON whole outer value redacted'
    $incompleteAssessment = Get-P45LogRedactionAssessment -Text $incompleteLog
    Assert-Equal $false $incompleteAssessment.Safe 'incomplete JSON-like input must be unsafe'

    $collect = Invoke-P45Cli @('Collect', '-RunDir', $runA)
    Assert-Equal 0 $collect.ExitCode 'redaction collect exit'
    $collection = Join-Path $runA 'evidence\collections\0003'
    $publicSafe = Join-Path $collection 'logs\redaction-sample.log'
    $privateSafe = Join-Path $collection 'private\logs\redaction-sample.log'
    $publicUnsafe = Join-Path $collection 'logs\redaction-unsafe.log'
    $privateUnsafe = Join-Path $collection 'private\logs\redaction-unsafe.log'
    $publicEscaped = Join-Path $collection 'logs\redaction-escaped.log'
    $privateEscaped = Join-Path $collection 'private\logs\redaction-escaped.log'
    $publicEncoded = Join-Path $collection 'logs\redaction-encoded.log'
    $privateEncoded = Join-Path $collection 'private\logs\redaction-encoded.log'
    $publicIncomplete = Join-Path $collection 'logs\redaction-incomplete.log'
    $privateIncomplete = Join-Path $collection 'private\logs\redaction-incomplete.log'
    Assert-True (Test-Path $publicSafe) 'safe redacted log published'
    Assert-True (Test-Path $privateSafe) 'safe raw log retained private'
    Assert-True (-not (Test-Path $publicUnsafe)) 'unsafe log must not be published'
    Assert-True (Test-Path $privateUnsafe) 'unsafe raw log retained private'
    Assert-True (Test-Path $publicEscaped) 'escaped-key JSON log redacted and published'
    Assert-True (Test-Path $privateEscaped) 'escaped-key raw log retained private'
    Assert-True (Test-Path $publicEncoded) 'nested encoded JSON log redacted and published'
    Assert-True (Test-Path $privateEncoded) 'nested encoded raw log retained private'
    Assert-True (-not (Test-Path $publicIncomplete)) 'incomplete JSON log must not be published'
    Assert-True (Test-Path $privateIncomplete) 'incomplete raw log retained private'
    $publicText = [IO.File]::ReadAllText($publicSafe, $script:P45Utf8)
    Assert-True (-not $publicText.Contains($jsonSecret)) 'public log has no JSON ticket/token secret'
    Assert-True (-not $publicText.Contains($bearerSecret)) 'public log has no Bearer secret'
    Assert-True (-not $publicText.Contains($opaqueSecret)) 'public log has no opaque secret'
    Assert-True ($publicText.Contains('<REDACTED>')) 'public log shows redaction placeholder'
    $privateText = [IO.File]::ReadAllText($privateSafe, $script:P45Utf8)
    Assert-True ($privateText.Contains($jsonSecret)) 'private raw keeps original bytes'
    Assert-True ([IO.File]::ReadAllText($privateUnsafe, $script:P45Utf8).Contains($nestedSecret)) 'private unsafe keeps original bytes'
    $publicEscapedText = [IO.File]::ReadAllText($publicEscaped, $script:P45Utf8)
    Assert-True (-not $publicEscapedText.Contains($escapedSecret)) 'public escaped-key log has no secret'
    Assert-True ($publicEscapedText.Contains('<REDACTED-JSON>')) 'public escaped-key log uses whole-JSON placeholder'
    Assert-True ([IO.File]::ReadAllText($privateEscaped, $script:P45Utf8).Contains($escapedSecret)) 'private escaped-key raw keeps original'
    $publicEncodedText = [IO.File]::ReadAllText($publicEncoded, $script:P45Utf8)
    Assert-True (-not $publicEncodedText.Contains($encodedSecret)) 'public nested encoded log has no secret'
    Assert-True ($publicEncodedText.Contains('<REDACTED-JSON>')) 'public nested encoded log uses whole-JSON placeholder'
    Assert-True ([IO.File]::ReadAllText($privateEncoded, $script:P45Utf8).Contains($encodedSecret)) 'private nested encoded raw keeps original'
    Assert-True ([IO.File]::ReadAllText($privateIncomplete, $script:P45Utf8).Contains($incompleteSecret)) 'private incomplete raw keeps original'

    $manifestPath = Join-Path $collection 'manifest.json'
    $manifest = Read-P45Json -Path $manifestPath
    Assert-Equal 'P45-LOG-REDACTION/2-FAIL-CLOSED' $manifest.redactionPolicy 'redaction policy'
    $manifestText = [IO.File]::ReadAllText($manifestPath, $script:P45Utf8)
    foreach ($secret in @($jsonSecret, $bearerSecret, $opaqueSecret, $nestedSecret, $escapedSecret, $encodedSecret, $incompleteSecret)) {
        Assert-True (-not $manifestText.Contains($secret)) ('manifest must not contain secret ' + $secret)
    }
    $publicEntries = @($manifest.entries | Where-Object { $_.sensitivity -eq 'public' -and $_.kind -eq 'log-redacted' })
    Assert-True (-not ($publicEntries.artifactPath -contains 'logs\redaction-unsafe.log')) 'unsafe path absent from public entries'
    Assert-True (-not ($publicEntries.artifactPath -contains 'logs\redaction-incomplete.log')) 'incomplete path absent from public entries'
    Assert-True (@($manifest.entries | Where-Object { $_.artifactPath -eq 'private\logs\redaction-unsafe.log' }).Count -eq 1) 'unsafe raw private entry exists'
    Assert-True (@($manifest.entries | Where-Object { $_.artifactPath -eq 'private\logs\redaction-incomplete.log' }).Count -eq 1) 'incomplete raw private entry exists'
    Assert-True (($manifest.notes -join ' ') -match 'redaction-unsafe.log') 'withheld unsafe log is recorded in notes'
    Assert-True (($manifest.notes -join ' ') -match 'redaction-incomplete.log') 'withheld incomplete log is recorded in notes'
    foreach ($entry in $publicEntries) {
        $artifact = Join-Path $collection $entry.artifactPath
        $artifactText = [IO.File]::ReadAllText($artifact, $script:P45Utf8)
        foreach ($secret in @($jsonSecret, $bearerSecret, $opaqueSecret, $nestedSecret, $escapedSecret, $encodedSecret, $incompleteSecret)) {
            Assert-True (-not $artifactText.Contains($secret)) ('public artifact leaked ' + $secret)
        }
    }
    $verify = Invoke-P45Cli @('Verify', '-ManifestPath', $manifestPath)
    Assert-Equal 0 $verify.ExitCode 'redaction manifest verify'
}

Invoke-Case 'Compare validates structure and never turns self-test fixtures into PASS' {
    $beforePath = Join-Path $OutputDir 'before.json'
    $afterPath = Join-Path $OutputDir 'after.json'
    $before = New-ObservationFixture -RunRecord $script:RunAContext.Run -ObservationId 'before-self-test' -Phase 'BEFORE_RECOVERY' -SessionId 'session-before' -Cursor 10
    $after = New-ObservationFixture -RunRecord $script:RunAContext.Run -ObservationId 'after-self-test' -Phase 'AFTER_RECOVERY' -SessionId 'session-after' -Cursor 99
    Write-ObservationFixture -Observation $before -Path $beforePath
    Write-ObservationFixture -Observation $after -Path $afterPath

    $importBefore = Invoke-P45Cli @('Import', '-RunDir', $runA, '-ObservationPath', $beforePath, '-Phase', 'Before')
    Assert-Equal 0 $importBefore.ExitCode 'import before'
    $importAfter = Invoke-P45Cli @('Import', '-RunDir', $runA, '-ObservationPath', $afterPath, '-Phase', 'After')
    Assert-Equal 0 $importAfter.ExitCode 'import after'
    $compare = Invoke-P45Cli @('Compare', '-RunDir', $runA)
    Assert-Equal 30 $compare.ExitCode 'self-test compare exit 30'
    Assert-Equal 'TOOL_SELF_TEST_ONLY' $compare.Result.status 'self-test outcome'
    $compareFile = Get-ChildItem -LiteralPath (Join-Path $runA 'evidence') -Filter 'compare-*.json' | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    $compareRecord = Read-P45Json -Path $compareFile.FullName
    Assert-SchemaRequired -Object $compareRecord -DefinitionName 'compare'
    Assert-Equal $true $compareRecord.requiredFieldsEqual 'required fields equal'
    Assert-Equal $false $compareRecord.pass 'self-test never PASS'
    Assert-Equal $false $compareRecord.mutableFields.reconnectSessionAndCursor.compared 'reconnect cursor is not compared'

    $mismatch = New-ObservationFixture -RunRecord $script:RunAContext.Run -ObservationId 'after-physical-mismatch' -Phase 'AFTER_RECOVERY' -SessionId 'session-after' -Cursor 99 -PhysicalHash ('4' * 64)
    $mismatchPath = Join-Path $OutputDir 'after-physical-mismatch.json'
    Write-ObservationFixture -Observation $mismatch -Path $mismatchPath
    $compareMismatch = Invoke-P45Cli @('Compare', '-RunDir', $runA, '-BeforeObservationPath', $beforePath, '-AfterObservationPath', $mismatchPath)
    Assert-Equal 16 $compareMismatch.ExitCode 'physical mismatch exit 16'
    Assert-Equal 'FAIL' $compareMismatch.Result.status 'physical mismatch status'
    Assert-Equal $false $compareMismatch.Result.data.requiredFieldsEqual 'physical mismatch required fields'

    $missingLedger = New-ObservationFixture -RunRecord $script:RunAContext.Run -ObservationId 'after-missing-ledger' -Phase 'AFTER_RECOVERY' -LedgerAvailable $false
    $missingLedgerPath = Join-Path $OutputDir 'after-missing-ledger.json'
    Write-ObservationFixture -Observation $missingLedger -Path $missingLedgerPath
    $compareMissing = Invoke-P45Cli @('Compare', '-RunDir', $runA, '-BeforeObservationPath', $beforePath, '-AfterObservationPath', $missingLedgerPath)
    Assert-Equal 17 $compareMissing.ExitCode 'missing ledger exit 17'
    Assert-Equal 'BLOCKED' $compareMissing.Result.status 'missing ledger status'

    $badChecksum = New-ObservationFixture -RunRecord $script:RunAContext.Run -ObservationId 'after-bad-checksum' -Phase 'AFTER_RECOVERY'
    $badChecksum.integrity.canonicalSha256 = ('0' * 64)
    $badChecksumPath = Join-Path $OutputDir 'after-bad-checksum.json'
    Write-ObservationFixture -Observation $badChecksum -Path $badChecksumPath
    $bad = Invoke-P45Cli @('Compare', '-RunDir', $runA, '-BeforeObservationPath', $beforePath, '-AfterObservationPath', $badChecksumPath)
    Assert-Equal 18 $bad.ExitCode 'bad checksum exit 18'

    $wrongRun = New-ObservationFixture -RunRecord $script:RunAContext.Run -ObservationId 'after-wrong-run' -Phase 'AFTER_RECOVERY' -RunIdOverride 'p45-not-this-run'
    $wrongRunPath = Join-Path $OutputDir 'after-wrong-run.json'
    Write-ObservationFixture -Observation $wrongRun -Path $wrongRunPath
    $wrong = Invoke-P45Cli @('Compare', '-RunDir', $runA, '-BeforeObservationPath', $beforePath, '-AfterObservationPath', $wrongRunPath)
    Assert-Equal 19 $wrong.ExitCode 'wrong run identity exit 19'
}

Invoke-Case 'Caller probe: TOOL_SELF_TEST run cannot become real PASS via source/build/phase relabeling' {
    # Reproduce the independent review's construction: a TOOL_SELF_TEST run
    # whose observation falsely claims PLAYER_STRUCTURED_EXPORT, uses the
    # run's source head as its "build commit", changes provenance, and leaves
    # phase UNKNOWN. Recomputing the ordinary checksum must not make it real.
    $probeBefore = New-ObservationFixture -RunRecord $script:RunAContext.Run `
        -ObservationId 'probe-before' -Phase 'UNKNOWN' -Source 'PLAYER_STRUCTURED_EXPORT' `
        -BuildCommit ([string]$script:RunAContext.Run.source.head) -BuildProvenance 'USER_DECLARED'
    $probeAfter = New-ObservationFixture -RunRecord $script:RunAContext.Run `
        -ObservationId 'probe-after' -Phase 'UNKNOWN' -Source 'PLAYER_STRUCTURED_EXPORT' `
        -BuildCommit ([string]$script:RunAContext.Run.source.head) -BuildProvenance 'USER_DECLARED'
    $probeBeforePath = Join-Path $OutputDir 'probe-before.json'
    $probeAfterPath = Join-Path $OutputDir 'probe-after.json'
    Write-ObservationFixture -Observation $probeBefore -Path $probeBeforePath
    Write-ObservationFixture -Observation $probeAfter -Path $probeAfterPath
    $probe = Invoke-P45Cli @('Compare', '-RunDir', $runA, '-BeforeObservationPath', $probeBeforePath, '-AfterObservationPath', $probeAfterPath)
    Assert-Equal 19 $probe.ExitCode 'probe build identity mismatch exit 19'
    Assert-True ($probe.Result.status -ne 'PASS') 'probe must not PASS'

    # Even if the attacker copies the actual Player build commit and provenance,
    # a TOOL_SELF_TEST run is still not a real Player evidence run.
    $forgedBefore = New-ObservationFixture -RunRecord $script:RunAContext.Run `
        -ObservationId 'forged-before' -Phase 'BEFORE_RECOVERY' -Source 'PLAYER_STRUCTURED_EXPORT' `
        -BuildCommit ([string]$script:RunAContext.Run.player.buildCommit) -BuildProvenance 'USER_DECLARED'
    $forgedAfter = New-ObservationFixture -RunRecord $script:RunAContext.Run `
        -ObservationId 'forged-after' -Phase 'AFTER_RECOVERY' -Source 'PLAYER_STRUCTURED_EXPORT' `
        -BuildCommit ([string]$script:RunAContext.Run.player.buildCommit) -BuildProvenance 'USER_DECLARED'
    $forgedBeforePath = Join-Path $OutputDir 'forged-before.json'
    $forgedAfterPath = Join-Path $OutputDir 'forged-after.json'
    Write-ObservationFixture -Observation $forgedBefore -Path $forgedBeforePath
    Write-ObservationFixture -Observation $forgedAfter -Path $forgedAfterPath
    $forged = Invoke-P45Cli @('Compare', '-RunDir', $runA, '-BeforeObservationPath', $forgedBeforePath, '-AfterObservationPath', $forgedAfterPath)
    Assert-Equal 18 $forged.ExitCode 'TOOL_SELF_TEST source relabel must exit 18'
    Assert-True ($forged.Result.status -ne 'PASS') 'TOOL_SELF_TEST run must never real PASS'

    # Provenance is bound to Prepare as tightly as buildCommit.
    $provMismatch = New-ObservationFixture -RunRecord $script:RunAContext.Run `
        -ObservationId 'provenance-mismatch' -Phase 'BEFORE_RECOVERY' -Source 'TOOL_SELF_TEST' `
        -BuildCommit ([string]$script:RunAContext.Run.player.buildCommit) -BuildProvenance 'BUILD_LOG_ATTACHED'
    $provMismatchPath = Join-Path $OutputDir 'provenance-mismatch.json'
    Write-ObservationFixture -Observation $provMismatch -Path $provMismatchPath
    $prov = Invoke-P45Cli @('Compare', '-RunDir', $runA, '-BeforeObservationPath', $provMismatchPath, '-AfterObservationPath', $forgedAfterPath)
    Assert-Equal 19 $prov.ExitCode 'provenance mismatch must exit 19'

    # An observation with UNKNOWN phase is not a usable recovery side.
    $unknownPhase = New-ObservationFixture -RunRecord $script:RunAContext.Run `
        -ObservationId 'unknown-phase' -Phase 'UNKNOWN' -Source 'TOOL_SELF_TEST'
    $unknownPhasePath = Join-Path $OutputDir 'unknown-phase.json'
    Write-ObservationFixture -Observation $unknownPhase -Path $unknownPhasePath
    $unknownImport = Invoke-P45Cli @('Import', '-RunDir', $runA, '-ObservationPath', $unknownPhasePath, '-Phase', 'Before')
    Assert-Equal 18 $unknownImport.ExitCode 'UNKNOWN phase import must be rejected'
    $unknownCompare = Invoke-P45Cli @('Compare', '-RunDir', $runA, '-BeforeObservationPath', $unknownPhasePath, '-AfterObservationPath', $forgedAfterPath)
    Assert-Equal 18 $unknownCompare.ExitCode 'UNKNOWN phase compare must be rejected'
}

Invoke-Case 'Digest covers availability; availability tamper is rejected or BLOCKED' {
    $before = New-ObservationFixture -RunRecord $script:RunAContext.Run `
        -ObservationId 'availability-before' -Phase 'BEFORE_RECOVERY' -Source 'TOOL_SELF_TEST'
    $after = New-ObservationFixture -RunRecord $script:RunAContext.Run `
        -ObservationId 'availability-after' -Phase 'AFTER_RECOVERY' -Source 'TOOL_SELF_TEST'

    # Change availability without recomputing: the canonical checksum must fail.
    $before.availability.ledger = $false
    $before.integrity.canonicalSha256 = '0' * 64
    $tamperedPath = Join-Path $OutputDir 'availability-tampered.json'
    $afterPath = Join-Path $OutputDir 'availability-after.json'
    Write-ObservationFixture -Observation $before -Path $tamperedPath
    Write-ObservationFixture -Observation $after -Path $afterPath
    $tampered = Invoke-P45Cli @('Compare', '-RunDir', $runA, '-BeforeObservationPath', $tamperedPath, '-AfterObservationPath', $afterPath)
    Assert-Equal 18 $tampered.ExitCode 'availability change without digest must fail 18'

    # Recompute the checksum after hiding ledger availability: still BLOCKED.
    $before2 = New-ObservationFixture -RunRecord $script:RunAContext.Run `
        -ObservationId 'availability-before-2' -Phase 'BEFORE_RECOVERY' -Source 'TOOL_SELF_TEST' -LedgerAvailable $false
    $before2Path = Join-Path $OutputDir 'availability-before-2.json'
    Write-ObservationFixture -Observation $before2 -Path $before2Path
    $blocked = Invoke-P45Cli @('Compare', '-RunDir', $runA, '-BeforeObservationPath', $before2Path, '-AfterObservationPath', $afterPath)
    Assert-Equal 17 $blocked.ExitCode 'honest availability=false must be blocked 17'
    Assert-Equal 'BLOCKED' $blocked.Result.status 'availability false status'
}

Invoke-Case 'Self-asserted real source and unknown build remain BLOCKED, never PASS' {
    # A clean-looking GAME context with a known build, attached-log claim and
    # complete fields. The ordinary SHA-256 is still not a provenance trust
    # root, so the tool must refuse real PASS until a verified exporter exists.
    $fakeRoot = Join-Path $OutputDir 'fake-game-known'
    $fake = New-FakeGameContext -Root $fakeRoot -BuildCommit ('f' * 40) -BuildProvenance 'BUILD_LOG_ATTACHED'
    $before = New-ObservationFixture -RunRecord $fake.Run -ObservationId 'fake-before' -Phase 'BEFORE_RECOVERY' `
        -Source 'PLAYER_STRUCTURED_EXPORT' -BuildCommit ('f' * 40) -BuildProvenance 'BUILD_LOG_ATTACHED'
    $after = New-ObservationFixture -RunRecord $fake.Run -ObservationId 'fake-after' -Phase 'AFTER_RECOVERY' `
        -Source 'PLAYER_STRUCTURED_EXPORT' -BuildCommit ('f' * 40) -BuildProvenance 'BUILD_LOG_ATTACHED'
    $beforePath = Join-Path $fakeRoot 'before.json'
    $afterPath = Join-Path $fakeRoot 'after.json'
    Write-ObservationFixture -Observation $before -Path $beforePath
    Write-ObservationFixture -Observation $after -Path $afterPath
    $known = Compare-P45Observations -Context $fake -BeforePath $beforePath -AfterPath $afterPath
    Assert-Equal 17 $known.ExitCode 'self-asserted real source must be BLOCKED'
    Assert-Equal 'BLOCKED' $known.Result.outcome 'self-asserted real source outcome'
    Assert-Equal $false $known.Result.pass 'self-asserted real source must not PASS'
    Assert-Equal 'NO_VERIFIED_PLAYER_STRUCTURED_EXPORTER_IN_THIS_TOOL_VERSION' $known.Result.realPassGate 'real pass gate'

    # A GAME context with unknown build identity and no build log is also
    # explicitly blocked by identity/eligibility rules.
    $unknownRoot = Join-Path $OutputDir 'fake-game-unknown'
    $unknown = New-FakeGameContext -Root $unknownRoot -BuildCommit 'UNKNOWN' -BuildProvenance 'UNKNOWN' -NoBuildLog
    $uBefore = New-ObservationFixture -RunRecord $unknown.Run -ObservationId 'unknown-before' -Phase 'BEFORE_RECOVERY' `
        -Source 'PLAYER_STRUCTURED_EXPORT' -BuildCommit 'UNKNOWN' -BuildProvenance 'UNKNOWN'
    $uAfter = New-ObservationFixture -RunRecord $unknown.Run -ObservationId 'unknown-after' -Phase 'AFTER_RECOVERY' `
        -Source 'PLAYER_STRUCTURED_EXPORT' -BuildCommit 'UNKNOWN' -BuildProvenance 'UNKNOWN'
    $uBeforePath = Join-Path $unknownRoot 'before.json'
    $uAfterPath = Join-Path $unknownRoot 'after.json'
    Write-ObservationFixture -Observation $uBefore -Path $uBeforePath
    Write-ObservationFixture -Observation $uAfter -Path $uAfterPath
    $unknownResult = Compare-P45Observations -Context $unknown -BeforePath $uBeforePath -AfterPath $uAfterPath
    Assert-Equal 17 $unknownResult.ExitCode 'unknown build must be BLOCKED'
    Assert-Equal 'BLOCKED' $unknownResult.Result.outcome 'unknown build outcome'
    Assert-Equal $false $unknownResult.Result.pass 'unknown build must not PASS'
}

Invoke-Case 'Schema definitions and emitted records stay aligned' {
    $schema = Read-P45Json -Path $script:SchemaPath
    foreach ($name in @('run', 'attempt', 'exit', 'observation', 'manifest', 'compare', 'ownership')) {
        $definition = Get-P45Property (Get-P45Property $schema '$defs') $name
        Assert-True ($null -ne $definition) ('schema def ' + $name)
        Assert-True (@(Get-P45Property $definition 'required' @()).Count -gt 0) ('schema required ' + $name)
    }
    $observationSchema = Get-P45Property (Get-P45Property $schema '$defs') 'observation'
    $source = Get-P45Property (Get-P45Property (Get-P45Property $observationSchema 'properties') 'source') 'enum'
    Assert-True ($source -contains 'TOOL_SELF_TEST' -and $source -contains 'PLAYER_STRUCTURED_EXPORT') 'observation source enum'
    $run = Read-P45Json -Path (Join-Path $runA 'run.json')
    Assert-SchemaRequired -Object $run -DefinitionName 'run'
    $manifest = Read-P45Json -Path (Join-Path $runA 'evidence\collections\0001\manifest.json')
    Assert-SchemaRequired -Object $manifest -DefinitionName 'manifest'
    $compareFile = Get-ChildItem -LiteralPath (Join-Path $runA 'evidence') -Filter 'compare-*.json' | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    $compareRecord = Read-P45Json -Path $compareFile.FullName
    Assert-SchemaRequired -Object $compareRecord -DefinitionName 'compare'
}

$completedAt = Get-P45TimeContext
$summary = [pscustomobject]@{
    schemaVersion = 1
    kind = 'p45-e2e-tool-self-test-summary'
    startedAt = $script:StartedAt.local
    completedAt = $completedAt.local
    evidenceRoot = $OutputDir
    helper = [pscustomobject]@{ path = $helperExe; sha256 = $helperHash; role = 'TOOL_SELF_TEST auxiliary process, not Mahjong Player' }
    cliSha256 = Get-P45Sha256 -Path $script:Cli
    commonSha256 = Get-P45Sha256 -Path $script:Common
    schemaSha256 = Get-P45Sha256 -Path $script:SchemaPath
    total = $script:Total
    passed = $script:Passed
    failed = $script:Failed
    skipped = 0
    firstFailure = $script:FirstFailure
    cases = @($script:Cases.ToArray())
    nativeExitCode = if ($script:Failed -eq 0) { 0 } else { 1 }
}
$summaryPath = Join-Path $OutputDir 'test-summary.json'
Write-P45JsonAtomic -Path $summaryPath -Object $summary
$summaryHash = Get-P45Sha256 -Path $summaryPath

Write-Host ''
Write-Host ('TOTAL=' + $script:Total + ' PASSED=' + $script:Passed + ' FAILED=' + $script:Failed + ' SKIPPED=0')
Write-Host ('SUMMARY: ' + $summaryPath)
Write-Host ('SUMMARY_SHA256: ' + $summaryHash)
if ($script:FirstFailure -ne $null) {
    Write-Host ('FIRST_FAILURE: ' + $script:FirstFailure.name + ': ' + $script:FirstFailure.error)
}
if ($script:Failed -gt 0) { exit 1 }
exit 0
