<#
.SYNOPSIS
    Optional P2.2 IL2CPP reproduction path for Windows or Android.

.DESCRIPTION
    This script is intentionally fail-fast: it refuses to start a Unity build
    unless the host has the Unity IL2CPP module, a usable Unity seat license,
    and the target C++ toolchain. It never edits the repository's Unity project;
    all settings and generated assemblies are applied to a temporary copy.

    Windows: builds an IL2CPP player, runs it with -batchmode, and requires
             "[WireAotSmoke] PASS" in the player log.
    Android: builds an ARM64 IL2CPP APK. If a device is attached it installs,
             launches, captures logcat and uninstalls; otherwise the build is
             reported and the device round trip remains BLOCKED.

    On the current host the prerequisite check exits 2 before Unity starts.
    Do not report that as an IL2CPP pass.

.PARAMETER Target
    Windows or Android. Defaults to Windows.

.PARAMETER BuildOnly
    Build the player/APK but do not run it.

.PARAMETER UnityPath
    Unity.exe path. Auto-detected when omitted.

.PARAMETER DotnetPath
    dotnet.exe path. Auto-detected when omitted.

.PARAMETER KeepTemp
    Keep the temporary project and build output for inspection.
#>
[CmdletBinding()]
param(
    [ValidateSet('Windows', 'Android')]
    [string]$Target = 'Windows',

    [switch]$BuildOnly,

    [string]$UnityPath,

    [string]$DotnetPath,

    [switch]$KeepTemp
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runDir = Join-Path $repoRoot "build\p22-aot\il2cpp-$Target-$stamp"
New-Item -ItemType Directory -Force -Path $runDir | Out-Null

function Write-Status {
    param([string]$Status, [string]$Message)
    $color = switch ($Status) {
        'PASS' { 'Green' }
        'FAIL' { 'Red' }
        'BLOCKED' { 'Yellow' }
        default { 'Gray' }
    }
    Write-Host ("{0,-8} {1}" -f $Status, $Message) -ForegroundColor $color
}

function Find-Unity {
    if ($UnityPath) { return $UnityPath }
    $roots = @('D:\Unity\Hub\Editor', 'C:\Program Files\Unity\Hub\Editor')
    $editors = @()
    foreach ($root in $roots) {
        if (Test-Path $root) {
            $editors += Get-ChildItem $root -Directory -ErrorAction SilentlyContinue |
                Where-Object { Test-Path (Join-Path $_.FullName 'Editor\Unity.exe') }
        }
    }
    $editor = $editors | Sort-Object Name -Descending | Select-Object -First 1
    if ($editor) { return (Join-Path $editor.FullName 'Editor\Unity.exe') }
    return $null
}

function Find-MsvcCl {
    $cl = Get-Command cl.exe -ErrorAction SilentlyContinue
    if ($cl) { return $cl.Source }
    $vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $vsPath = (& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2>$null | Select-Object -First 1)
        if ($vsPath) {
            return (Get-ChildItem (Join-Path $vsPath 'VC\Tools\MSVC') -Directory -ErrorAction SilentlyContinue |
                Sort-Object Name -Descending |
                ForEach-Object { Join-Path $_.FullName 'bin\Hostx64\x64\cl.exe' } |
                Where-Object { Test-Path $_ } |
                Select-Object -First 1)
        }
    }
    return $null
}

function Find-WindowsSdk {
    foreach ($root in @('C:\Program Files (x86)\Windows Kits\10\Include', 'C:\Program Files\Windows Kits\10\Include')) {
        if (Test-Path $root) {
            $sdk = Get-ChildItem $root -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
            if ($sdk) { return $sdk.FullName }
        }
    }
    return $null
}

function Find-AndroidNdk {
    $roots = @()
    if ($env:ANDROID_NDK_HOME) { $roots += $env:ANDROID_NDK_HOME }
    if ($env:ANDROID_NDK_ROOT) { $roots += $env:ANDROID_NDK_ROOT }
    $sdk = @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT, "$env:LOCALAPPDATA\Android\Sdk") | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if ($sdk) { $roots += (Join-Path $sdk 'ndk') }
    foreach ($root in $roots) {
        if (Test-Path $root) {
            $candidate = Get-ChildItem $root -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
            if ($candidate) { return $candidate.FullName }
            return $root
        }
    }
    return $null
}

if (-not $DotnetPath) {
    if ($env:DOTNET_ROOT -and (Test-Path (Join-Path $env:DOTNET_ROOT 'dotnet.exe'))) { $DotnetPath = Join-Path $env:DOTNET_ROOT 'dotnet.exe' }
    elseif (Test-Path 'D:\Unity\dotnet\dotnet.exe') { $DotnetPath = 'D:\Unity\dotnet\dotnet.exe' }
    else { $DotnetPath = (Get-Command dotnet -ErrorAction Stop).Source }
}

$unity = Find-Unity
if (-not $unity) {
    Write-Status 'BLOCKED' 'Unity.exe not found.'
    exit 2
}

$editorData = Join-Path (Split-Path $unity) 'Data'
$il2cppExe = Join-Path $editorData 'il2cpp\build\deploy\il2cpp.exe'
if (-not (Test-Path $il2cppExe)) {
    Write-Status 'BLOCKED' "Unity IL2CPP compiler missing: $il2cppExe"
    exit 2
}

$missing = @()
if ($Target -eq 'Windows') {
    $cl = Find-MsvcCl
    $sdk = Find-WindowsSdk
    if (-not $cl) { $missing += 'MSVC cl.exe (Visual Studio C++ workload / Build Tools)' }
    if (-not $sdk) { $missing += 'Windows 10/11 SDK' }
}
else {
    $ndk = Find-AndroidNdk
    $androidPlayer = Join-Path $editorData 'PlaybackEngines\AndroidPlayer'
    if (-not (Test-Path $androidPlayer)) { $missing += 'Unity Android Player module' }
    if (-not $ndk) { $missing += 'Android NDK (ANDROID_NDK_HOME / SDK\ndk)' }
}

if ($missing.Count -gt 0) {
    Write-Status 'BLOCKED' ("$Target IL2CPP prerequisites missing: " + ($missing -join '; '))
    Write-Host "artifacts: $runDir"
    exit 2
}

# Build the shared assemblies first. The probe is a direct netstandard2.1
# consumer, so it must be rebuilt with the current Wire schema.
& $DotnetPath build (Join-Path $repoRoot 'dotnet\Mahjong.sln') -c Release --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Status 'FAIL' "dotnet build failed with exit $LASTEXITCODE"
    exit 1
}

$artifacts = @{
    'Mahjong.Contracts.Wire.dll' = Join-Path $repoRoot 'dotnet\src\Mahjong.Contracts.Wire\bin\Release\netstandard2.1\Mahjong.Contracts.Wire.dll'
    'Mahjong.Contracts.dll' = Join-Path $repoRoot 'dotnet\src\Mahjong.Contracts\bin\Release\netstandard2.1\Mahjong.Contracts.dll'
    'Mahjong.Contracts.Wire.AotSmoke.dll' = Join-Path $repoRoot 'dotnet\tests\Mahjong.Contracts.Wire.AotSmoke\bin\Release\netstandard2.1\Mahjong.Contracts.Wire.AotSmoke.dll'
}
$globalPackages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$artifacts['Google.Protobuf.dll'] = Join-Path $globalPackages 'google.protobuf\3.28.3\lib\netstandard2.0\Google.Protobuf.dll'

foreach ($name in $artifacts.Keys) {
    if (-not (Test-Path $artifacts[$name])) {
        Write-Status 'FAIL' "Missing build artifact: $($artifacts[$name])"
        exit 1
    }
}

$tempProject = Join-Path $env:TEMP ('mahjong-il2cpp-aot-' + [guid]::NewGuid().ToString('N'))
$logPath = Join-Path $runDir 'unity-il2cpp-build.log'
$outputPath = if ($Target -eq 'Windows') { Join-Path $runDir 'MahjongAotSmoke.exe' } else { Join-Path $runDir 'MahjongAotSmoke.apk' }
$playerLog = Join-Path $runDir 'player.log'

try {
    New-Item -ItemType Directory -Force -Path $tempProject | Out-Null
    foreach ($part in @('Assets', 'Packages', 'ProjectSettings')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot "unity\Mahjong.Unity\$part") -Destination $tempProject -Recurse
    }

    $pluginDir = Join-Path $tempProject 'Assets\Plugins\Mahjong'
    $editorDir = Join-Path $tempProject 'Assets\Editor'
    if (Test-Path $pluginDir) {
        Remove-Item -LiteralPath $pluginDir -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $pluginDir, $editorDir | Out-Null
    foreach ($name in $artifacts.Keys) {
        Copy-Item -LiteralPath $artifacts[$name] -Destination $pluginDir -Force
    }

    $buildScript = @'
using System;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class MahjongIl2cppSmokeBuild
{
    public static void Build()
    {
        string target = Environment.GetEnvironmentVariable("MAHJONG_AOT_TARGET") ?? "Windows";
        string output = Environment.GetEnvironmentVariable("MAHJONG_AOT_OUTPUT");
        if (string.IsNullOrEmpty(output))
        {
            throw new Exception("MAHJONG_AOT_OUTPUT is required.");
        }

        BuildTarget buildTarget;
        BuildTargetGroup group;
        if (target == "Android")
        {
            buildTarget = BuildTarget.Android;
            group = BuildTargetGroup.Android;
            PlayerSettings.SetApplicationIdentifier(group, "com.mahjong.aotsmoke");
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            EditorUserBuildSettings.buildAppBundle = false;
        }
        else
        {
            buildTarget = BuildTarget.StandaloneWindows64;
            group = BuildTargetGroup.Standalone;
        }

        PlayerSettings.SetScriptingBackend(group, ScriptingImplementation.IL2CPP);
        string previous = PlayerSettings.GetScriptingDefineSymbolsForGroup(group);
        string defines = string.IsNullOrEmpty(previous) ? "MAHJONG_WIRE_AOT_SMOKE" : previous + ";MAHJONG_WIRE_AOT_SMOKE";
        PlayerSettings.SetScriptingDefineSymbolsForGroup(group, defines);
        try
        {
            BuildReport report = BuildPipeline.BuildPlayer(
                new[] { "Assets/Scenes/SampleScene.unity" },
                output,
                buildTarget,
                BuildOptions.None);
            if (report.summary.result != BuildResult.Succeeded)
            {
                throw new Exception("Build result " + report.summary.result + ", errors=" + report.summary.totalErrors);
            }
        }
        finally
        {
            PlayerSettings.SetScriptingDefineSymbolsForGroup(group, previous);
        }
    }
}
'@
    Set-Content -LiteralPath (Join-Path $editorDir 'MahjongIl2cppSmokeBuild.cs') -Value $buildScript -Encoding UTF8

    $oldTarget = $env:MAHJONG_AOT_TARGET
    $oldOutput = $env:MAHJONG_AOT_OUTPUT
    $env:MAHJONG_AOT_TARGET = $Target
    $env:MAHJONG_AOT_OUTPUT = $outputPath
    try {
        $process = Start-Process -FilePath $unity -Wait -PassThru -ArgumentList @(
            '-batchmode',
            '-quit',
            '-nographics',
            '-projectPath', $tempProject,
            '-executeMethod', 'MahjongIl2cppSmokeBuild.Build',
            '-logFile', $logPath
        )
    }
    finally {
        $env:MAHJONG_AOT_TARGET = $oldTarget
        $env:MAHJONG_AOT_OUTPUT = $oldOutput
    }

    $buildLog = if (Test-Path $logPath) { Get-Content $logPath -Raw } else { '' }
    if ($buildLog -match 'No valid Unity Editor license found' -or $buildLog -match 'return code 198') {
        Write-Status 'BLOCKED' "Unity license unavailable; exit=$($process.ExitCode); log=$logPath"
        exit 2
    }
    if ($process.ExitCode -ne 0 -or -not (Test-Path $outputPath)) {
        Write-Status 'FAIL' "IL2CPP build failed; exit=$($process.ExitCode); log=$logPath"
        exit 1
    }
    Write-Status 'PASS' "$Target IL2CPP build completed: $outputPath"

    if ($BuildOnly) {
        exit 0
    }

    if ($Target -eq 'Windows') {
        $player = Start-Process -FilePath $outputPath -Wait -PassThru -ArgumentList @(
            '-batchmode',
            '-nographics',
            '-logFile', $playerLog
        )
        $text = if (Test-Path $playerLog) { Get-Content $playerLog -Raw } else { '' }
        if ($player.ExitCode -eq 0 -and $text -match '\[WireAotSmoke\] PASS') {
            Write-Status 'PASS' 'Windows IL2CPP serialization round trip'
            exit 0
        }
        Write-Status 'FAIL' "Windows IL2CPP runtime probe failed; exit=$($player.ExitCode); log=$playerLog"
        exit 1
    }

    $adb = Get-Command adb.exe -ErrorAction SilentlyContinue
    $adbPath = if ($adb) { if ($adb.Source) { $adb.Source } else { $adb.FullName } } else { $null }
    if (-not $adbPath) {
        $sdk = @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT, "$env:LOCALAPPDATA\Android\Sdk") | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
        if ($sdk) {
            $candidate = Join-Path $sdk 'platform-tools\adb.exe'
            if (Test-Path $candidate) { $adbPath = $candidate }
        }
    }
    if (-not $adbPath) {
        Write-Status 'BLOCKED' 'Android APK built, but adb is unavailable; device round trip NOT_RUN.'
        exit 2
    }

    $devices = (& $adbPath devices) -split "`r?`n" | Where-Object { $_ -match '\tdevice$' }
    if ($devices.Count -eq 0) {
        Write-Status 'BLOCKED' 'Android APK built, but no adb device is attached; round trip NOT_RUN.'
        exit 2
    }

    & $adbPath install -r $outputPath
    if ($LASTEXITCODE -ne 0) {
        Write-Status 'FAIL' 'adb install failed.'
        exit 1
    }
    & $adbPath logcat -c
    & $adbPath shell am force-stop com.mahjong.aotsmoke
    & $adbPath shell am start -n com.mahjong.aotsmoke/com.unity3d.player.UnityPlayerActivity
    Start-Sleep -Seconds 12
    $logcat = & $adbPath logcat -d -s Unity
    $logcat | Set-Content -LiteralPath (Join-Path $runDir 'android-logcat.txt')
    & $adbPath uninstall com.mahjong.aotsmoke | Out-Null
    if (($logcat | Out-String) -match '\[WireAotSmoke\] PASS') {
        Write-Status 'PASS' 'Android IL2CPP serialization round trip'
        exit 0
    }
    Write-Status 'FAIL' 'Android IL2CPP runtime probe did not report PASS; see android-logcat.txt'
    exit 1
}
catch {
    Write-Status 'FAIL' $_.Exception.Message
    exit 1
}
finally {
    if (-not $KeepTemp -and (Test-Path $tempProject)) {
        Remove-Item -LiteralPath $tempProject -Recurse -Force -ErrorAction SilentlyContinue
    }
}
