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

function Invoke-Logged([string]$name, [string]$file, [string[]]$arguments) {
    $log = Join-Path $EvidenceRoot $file
    $process = Start-Process -FilePath $arguments[0] -ArgumentList @($arguments[1..($arguments.Length - 1)]) -PassThru -Wait -WindowStyle Hidden -RedirectStandardOutput $log -RedirectStandardError ($log + '.err')
    Write-Result $name $process.ExitCode $log
    return $process.ExitCode
}

function Invoke-DotnetTest([string]$name, [string]$projectPath, [string]$file) {
    $log = Join-Path $EvidenceRoot $file
    $p = Start-Process -FilePath $dotnet -ArgumentList @('test', $projectPath, '-c', $Configuration, '--nologo', '-v:minimal') -PassThru -Wait -WindowStyle Hidden -RedirectStandardOutput $log -RedirectStandardError ($log + '.err')
    $count = Select-String -Path $log -Pattern '已通过!|Passed!' | Select-Object -Last 1
    Write-Result $name $p.ExitCode ($log + ' ' + ($count.Line.Trim()))
    return $p.ExitCode
}

function Invoke-UnityWithMutex([string]$name, [string]$file, [string[]]$unityArgs) {
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
        Write-Result $name $process.ExitCode $log
        return $process.ExitCode
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
        '-testResults', $editXml, '-logFile', (Join-Path $EvidenceRoot 'unity-editmode.log'))
    $exit = [Math]::Max($exit, $editExit)

    $playXml = Join-Path $EvidenceRoot 'unity-playmode-control.xml'
    $playExit = Invoke-UnityWithMutex 'unity-playmode-control' 'unity-playmode-control.log' @(
        '-batchmode', '-projectPath', $project,
        '-runTests', '-testPlatform', 'PlayMode',
        '-testFilter', 'Mahjong.Unity.Tests.PlayMode.TableControlPlayModeTests',
        '-testResults', $playXml, '-logFile', (Join-Path $EvidenceRoot 'unity-playmode-control.log'))
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
