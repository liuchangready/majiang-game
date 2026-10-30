<#
.SYNOPSIS
    Reproducible component/Unity/Player validation for the P4.5 owner seat
    control UI (release -> explicit reconnect -> claim -> stale epoch reject).

.DESCRIPTION
    Windows PowerShell 5.1 compatible. Unity-heavy stages acquire the shared
    named mutex Local\MahjongV5P45UnityValidation and log every wait. All output
    is written under -EvidenceRoot; no tracked Unity asset is intentionally
    rewritten except through the repository's build-shared.ps1 (whose generated
    plugin diff must be preserved/reverted by the caller as documented).

    Stages:
      build-shared + shared-plugin provenance
      fresh Release dotnet tests (TableModel, Contracts, TableRuntime)
      targeted Unity EditMode (Table scene builder/bindings)
      targeted Unity PlayMode (real LocalHost + Client control chain)
      optional fresh Windows Player and opt-in control probe

.PARAMETER SkipPlayer
    Skip the Windows Player build and probe (Unity Editor only).

.PARAMETER SkipUnity
    Skip both Unity Editor stages.

.PARAMETER ProbeRoot
    Isolated persistent-data root passed to the Player through -smoke-data.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$EvidenceRoot,
    [string]$PlayerBuildDirectory,
    [string]$ProbeRoot,
    [switch]$SkipUnity,
    [switch]$SkipPlayer
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $EvidenceRoot) {
    $EvidenceRoot = Join-Path $root ('build\p45-control-ui\evidence-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
if (-not $PlayerBuildDirectory) {
    $PlayerBuildDirectory = Join-Path $root 'build\p45-control-ui\player'
}
if (-not $ProbeRoot) {
    $ProbeRoot = Join-Path $root 'build\p45-control-ui\probe-data'
}
New-Item -ItemType Directory -Force -Path $EvidenceRoot | Out-Null
New-Item -ItemType Directory -Force -Path $PlayerBuildDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $ProbeRoot | Out-Null

# Unity resolves -testResults/-logFile relative to its project directory, not
# the caller's working directory. Use absolute paths for every native tool.
$EvidenceRoot = (Resolve-Path -LiteralPath $EvidenceRoot).Path
$PlayerBuildDirectory = (Resolve-Path -LiteralPath $PlayerBuildDirectory).Path
$ProbeRoot = (Resolve-Path -LiteralPath $ProbeRoot).Path

$dotnet = if ($env:DOTNET_ROOT) { Join-Path $env:DOTNET_ROOT 'dotnet.exe' } elseif (Test-Path 'D:\Unity\dotnet\dotnet.exe') { 'D:\Unity\dotnet\dotnet.exe' } else { 'dotnet' }
$unity = 'D:\Unity\Hub\Editor\6000.3.27f1\Editor\Unity.exe'
$project = Join-Path $root 'unity\Mahjong.Unity'
$unityMutexName = 'Local\MahjongV5P45UnityValidation'
$results = New-Object System.Collections.Generic.List[string]

function Write-Result([string]$name, [int]$exitCode, [string]$detail) {
    $line = '{0} exit={1} {2}' -f $name, $exitCode, $detail
    Write-Host $line
    $results.Add($line) | Out-Null
}

function Read-LogText([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) {
        return ''
    }

    $bytes = [IO.File]::ReadAllBytes($path)
    if ($bytes.Length -eq 0) {
        return ''
    }

    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        return [Text.Encoding]::UTF8.GetString($bytes, 3, $bytes.Length - 3)
    }

    if ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) {
        return [Text.Encoding]::Unicode.GetString($bytes, 2, $bytes.Length - 2)
    }

    if ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFE -and $bytes[1] -eq 0xFF) {
        return [Text.Encoding]::BigEndianUnicode.GetString($bytes, 2, $bytes.Length - 2)
    }

    # Native redirection under Windows PowerShell 5.1 is byte-for-byte; dotnet
    # commonly emits UTF-8 while other tools emit the active ANSI code page.
    try {
        $strictUtf8 = New-Object System.Text.UTF8Encoding -ArgumentList @($false, $true)
        return $strictUtf8.GetString($bytes)
    }
    catch {
        return [Text.Encoding]::Default.GetString($bytes)
    }
}

function Get-TestSummary([string]$text) {
    if ([string]::IsNullOrEmpty($text)) {
        return $null
    }

    $passedZh = ([string][char]0x5DF2) + [char]0x901A + [char]0x8FC7 + '!'
    $failedZh = ([string][char]0x5931) + [char]0x8D25 + '!'
    $patterns = @(
        '(?m)^\s*Passed!\s*.*$',
        '(?m)^\s*Failed!\s*.*$',
        ('(?m)^\s*' + [regex]::Escape($passedZh) + '.*$'),
        ('(?m)^\s*' + [regex]::Escape($failedZh) + '.*$')
    )
    foreach ($pattern in $patterns) {
        $match = [regex]::Match($text, $pattern)
        if ($match.Success) {
            return $match.Value.Trim()
        }
    }

    return $null
}

function Get-TestCounts([string]$summary) {
    if ([string]::IsNullOrEmpty($summary)) {
        return $null
    }

    $failedZh = ([string][char]0x5931) + [char]0x8D25 + ':'
    $passedZh = ([string][char]0x901A) + [char]0x8FC7 + ':'
    $skippedZh = ([string][char]0x5DF2) + [char]0x8DF3 + [char]0x8FC7 + ':'
    $totalZh = ([string][char]0x603B) + [char]0x8BA1 + ':'
    $patterns = [ordered]@{
        failed = @([regex]::Escape($failedZh), 'Failed:')
        passed = @([regex]::Escape($passedZh), 'Passed:')
        skipped = @([regex]::Escape($skippedZh), 'Skipped:')
        total = @([regex]::Escape($totalZh), 'Total:')
    }
    $counts = [ordered]@{ failed = $null; passed = $null; skipped = $null; total = $null }
    foreach ($key in $patterns.Keys) {
        foreach ($prefix in $patterns[$key]) {
            $match = [regex]::Match($summary, $prefix + '\s*(\d+)')
            if ($match.Success) {
                $counts[$key] = [int]$match.Groups[1].Value
                break
            }
        }
    }

    if ($null -eq $counts.total -and $null -ne $counts.passed -and $null -ne $counts.failed) {
        $skippedValue = if ($null -eq $counts.skipped) { 0 } else { [int]$counts.skipped }
        $counts.total = [int]$counts.passed + [int]$counts.failed + $skippedValue
    }

    return $counts
}

function Format-TestCounts($counts) {
    if ($null -eq $counts) {
        return 'counts=unavailable'
    }

    return ('failed={0} passed={1} skipped={2} total={3}' -f $counts.failed, $counts.passed, $counts.skipped, $counts.total)
}

function Invoke-DotnetTest([string]$name, [string]$projectPath, [string]$file) {
    $log = Join-Path $EvidenceRoot $file

    # -NoLogo is accepted by dotnet test. Keep stdout/stderr separate so a
    # failed run retains the native exit code and both streams verbatim.
    $p = Start-Process -FilePath $dotnet -ArgumentList @('test', $projectPath, '-c', $Configuration, '--nologo', '-v:minimal') -PassThru -Wait -WindowStyle Hidden -RedirectStandardOutput $log -RedirectStandardError ($log + '.err')
    $text = Read-LogText $log
    $stderr = Read-LogText ($log + '.err')
    if (-not [string]::IsNullOrEmpty($stderr)) {
        $text = $text + [Environment]::NewLine + $stderr
    }

    $summary = Get-TestSummary $text
    $counts = Get-TestCounts $summary
    $detail = $log + ' ' + (Format-TestCounts $counts)
    if ([string]::IsNullOrEmpty($summary)) {
        $detail = $detail + ' SUMMARY_UNAVAILABLE'
    }
    else {
        $detail = $detail + ' "' + $summary + '"'
    }

    $effectiveExit = $p.ExitCode
    if ($p.ExitCode -eq 0 -and [string]::IsNullOrEmpty($summary)) {
        # Exit 0 without a parseable summary is not enough to claim a pass.
        $effectiveExit = 1
    }

    Write-Result $name $effectiveExit $detail
    return $effectiveExit
}

function Get-UnityTestSummary([string]$xmlPath) {
    if (-not (Test-Path -LiteralPath $xmlPath)) {
        return $null
    }

    try {
        [xml]$xml = Read-LogText $xmlPath
        $run = $xml.'test-run'
        if ($null -eq $run) {
            return $null
        }

        return [pscustomobject]@{
            total = [int]$run.total
            passed = [int]$run.passed
            failed = [int]$run.failed
            skipped = [int]$run.skipped
            result = [string]$run.result
        }
    }
    catch {
        return $null
    }
}

function Invoke-UnityWithMutex(
    [string]$name,
    [string]$file,
    [string[]]$unityArgs,
    [string]$resultsXml,
    [int]$expectedTotal = -1) {
    $mutex = New-Object System.Threading.Mutex($false, $unityMutexName)
    $acquired = $false
    try {
        Write-Host ('WAIT ' + $name + ' mutex=' + $unityMutexName)
        $acquired = $mutex.WaitOne([TimeSpan]::FromMinutes(60))
        if (-not $acquired) {
            Write-Result $name 124 'MUTEX_TIMEOUT'
            return 124
        }

        $log = Join-Path $EvidenceRoot $file
        $process = Start-Process -FilePath $unity -ArgumentList $unityArgs -PassThru -Wait -WindowStyle Hidden -RedirectStandardOutput ($log + '.stdout') -RedirectStandardError ($log + '.stderr')
        $summary = if ([string]::IsNullOrEmpty($resultsXml)) { $null } else { Get-UnityTestSummary $resultsXml }
        $detail = $log
        if ($null -eq $summary) {
            $detail = $detail + ' UNITY_RESULT_UNAVAILABLE'
        }
        else {
            $detail = $detail + (' total={0} passed={1} failed={2} skipped={3} result={4}' -f $summary.total, $summary.passed, $summary.failed, $summary.skipped, $summary.result)
            if ($expectedTotal -ge 0 -and $summary.total -ne $expectedTotal) {
                $detail = $detail + (' EXPECTED_TOTAL={0}' -f $expectedTotal)
            }
        }

        $effectiveExit = $process.ExitCode
        if ($process.ExitCode -eq 0 -and $null -eq $summary) {
            $effectiveExit = 1
        }
        elseif ($process.ExitCode -eq 0 -and $summary.failed -gt 0) {
            $effectiveExit = 1
        }
        elseif ($process.ExitCode -eq 0 -and $expectedTotal -ge 0 -and $summary.total -ne $expectedTotal) {
            $effectiveExit = 1
        }

        Write-Result $name $effectiveExit $detail
        return $effectiveExit
    }
    finally {
        if ($acquired) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}

$exit = 0

$buildLog = Join-Path $EvidenceRoot 'build-shared.log'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\unity\build-shared.ps1') -Configuration $Configuration 2>&1 | Tee-Object -FilePath $buildLog
$exit = $LASTEXITCODE
Write-Result 'build-shared' $exit $buildLog

$checkLog = Join-Path $EvidenceRoot 'check-shared-plugins.log'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\unity\check-shared-plugins.ps1') 2>&1 | Tee-Object -FilePath $checkLog
$checkExit = $LASTEXITCODE
Write-Result 'check-shared-plugins' $checkExit $checkLog
$exit = [Math]::Max($exit, $checkExit)

$exit = [Math]::Max($exit, (Invoke-DotnetTest 'tablemodel-tests' (Join-Path $root 'dotnet\tests\Mahjong.Unity.TableModel.Tests\Mahjong.Unity.TableModel.Tests.csproj') 'tablemodel-tests.log'))
$exit = [Math]::Max($exit, (Invoke-DotnetTest 'contracts-tests' (Join-Path $root 'dotnet\tests\Mahjong.Contracts.Tests\Mahjong.Contracts.Tests.csproj') 'contracts-tests.log'))
$exit = [Math]::Max($exit, (Invoke-DotnetTest 'tableruntime-tests' (Join-Path $root 'dotnet\tests\Mahjong.TableRuntime.Tests\Mahjong.TableRuntime.Tests.csproj') 'tableruntime-tests.log'))

if (-not $SkipUnity) {
    $editXml = Join-Path $EvidenceRoot 'unity-editmode.xml'
    $editExit = Invoke-UnityWithMutex 'unity-editmode' 'unity-editmode.log' @(
        '-batchmode', '-nographics', '-projectPath', $project,
        '-runTests', '-testPlatform', 'EditMode',
        '-testFilter', 'Mahjong.Unity.Tests.EditMode.TableSceneEditModeTests',
        '-testResults', $editXml, '-logFile', (Join-Path $EvidenceRoot 'unity-editmode.log')) $editXml 6
    $exit = [Math]::Max($exit, $editExit)

    $playXml = Join-Path $EvidenceRoot 'unity-playmode-control.xml'
    $playExit = Invoke-UnityWithMutex 'unity-playmode-control' 'unity-playmode-control.log' @(
        '-batchmode', '-projectPath', $project,
        '-runTests', '-testPlatform', 'PlayMode',
        '-testFilter', 'Mahjong.Unity.Tests.PlayMode.TableControlPlayModeTests',
        '-testResults', $playXml, '-logFile', (Join-Path $EvidenceRoot 'unity-playmode-control.log')) $playXml 2
    $exit = [Math]::Max($exit, $playExit)
}

if (-not $SkipPlayer) {
    $playerExe = Join-Path $PlayerBuildDirectory 'Mahjong.exe'
    $buildPlayerExit = Invoke-UnityWithMutex 'unity-player-build' 'unity-player-build.log' @(
        '-batchmode', '-nographics', '-quit', '-projectPath', $project,
        '-buildWindows64Player', $playerExe,
        '-logFile', (Join-Path $EvidenceRoot 'unity-player-build.log'))
    $exit = [Math]::Max($exit, $buildPlayerExit)
    if ($buildPlayerExit -eq 0) {
        $probeRun = Join-Path $EvidenceRoot ('player-probe-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
        New-Item -ItemType Directory -Force -Path $probeRun | Out-Null
        $playerLog = Join-Path $probeRun 'player.log'
        $player = Start-Process -FilePath $playerExe -ArgumentList @('-local-control-probe', $probeRun, '-smoke-data', $ProbeRoot, '-screen-width', '1280', '-screen-height', '720', '-logFile', $playerLog) -PassThru -Wait -WindowStyle Hidden
        Write-Result 'player-control-probe' $player.ExitCode $probeRun
        $exit = [Math]::Max($exit, $player.ExitCode)
    }
}

$manifest = Join-Path $EvidenceRoot 'manifest.csv'
$rows = foreach ($file in Get-ChildItem -Path $EvidenceRoot -File -Recurse | Where-Object { $_.Name -notmatch 'manifest\.csv$' }) {
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash
    '"{0}","{1}","{2}"' -f $file.FullName.Replace('"', '""'), $file.Length, $hash
}
'"path","bytes","sha256"' | Set-Content -LiteralPath $manifest -Encoding UTF8
$rows | Add-Content -LiteralPath $manifest -Encoding UTF8
$results | Set-Content -LiteralPath (Join-Path $EvidenceRoot 'results.txt') -Encoding UTF8
Write-Host ('EVIDENCE ' + $EvidenceRoot)
Write-Host ('MANIFEST ' + $manifest)
exit $exit
