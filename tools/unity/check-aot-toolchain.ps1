<#
.SYNOPSIS
    Read-only P2.2 AOT/IL2CPP toolchain inventory.

.DESCRIPTION
    Reports why a Windows/Android IL2CPP build can or cannot run on this host:
    Unity editor/modules/license, MSVC C++ tools, Windows SDK, CMake/Ninja,
    Android SDK/NDK/JDK/adb, and macOS/Xcode availability. It never installs or
    changes anything. Missing prerequisites are reported as BLOCKED.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Continue'
$script:blocked = 0

function Write-Check {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][bool]$Ok,
        [Parameter(Mandatory)][string]$Detail,
        [switch]$Required
    )

    $status = if ($Ok) { 'PASS' } elseif ($Required) { 'BLOCKED' } else { 'INFO' }
    if ($status -eq 'BLOCKED') { $script:blocked++ }
    $color = switch ($status) {
        'PASS' { 'Green' }
        'BLOCKED' { 'Yellow' }
        default { 'Gray' }
    }
    Write-Host ("{0,-8} {1,-26} {2}" -f $status, $Name, $Detail) -ForegroundColor $color
}

Write-Host "=== P2.2 AOT / IL2CPP toolchain inventory ==="
Write-Host ("host     : " + [System.Environment]::MachineName + " / " + [System.Environment]::OSVersion.VersionString)
Write-Host ("date     : " + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz'))
Write-Host ""

Write-Host "--- Unity ---"
$editorRoots = @('D:\Unity\Hub\Editor', 'C:\Program Files\Unity\Hub\Editor')
$editors = @()
foreach ($root in $editorRoots) {
    if (Test-Path $root) {
        $editors += Get-ChildItem $root -Directory -ErrorAction SilentlyContinue |
            Where-Object { Test-Path (Join-Path $_.FullName 'Editor\Unity.exe') }
    }
}
$editor = $editors | Sort-Object Name -Descending | Select-Object -First 1
Write-Check -Name 'Unity editor' -Ok ([bool]$editor) -Detail $(if ($editor) { "$($editor.Name) @ $($editor.FullName)" } else { 'not found' }) -Required

if ($editor) {
    $il2cpp = Test-Path (Join-Path $editor.FullName 'Editor\Data\PlaybackEngines\windowsstandalonesupport\Variations\win64_player_development_il2cpp')
    Write-Check -Name 'Windows IL2CPP module' -Ok $il2cpp -Detail $(if ($il2cpp) { 'win64_player_development_il2cpp present' } else { 'module missing' }) -Required

    $androidPlayer = Join-Path $editor.FullName 'Editor\Data\PlaybackEngines\AndroidPlayer'
    Write-Check -Name 'Android Player module' -Ok (Test-Path $androidPlayer) -Detail $(if (Test-Path $androidPlayer) { $androidPlayer } else { 'module missing' }) -Required

    $unitySdk = Join-Path $androidPlayer 'SDK'
    $unityNdk = Join-Path $androidPlayer 'NDK'
    Write-Check -Name 'Unity Android SDK' -Ok (Test-Path $unitySdk) -Detail $(if (Test-Path $unitySdk) { $unitySdk } else { 'not bundled' }) -Required
    Write-Check -Name 'Unity Android NDK' -Ok (Test-Path $unityNdk) -Detail $(if (Test-Path $unityNdk) { $unityNdk } else { 'not bundled' }) -Required

    $il2cppExe = Join-Path $editor.FullName 'Editor\Data\il2cpp\build\deploy\il2cpp.exe'
    Write-Check -Name 'Unity IL2CPP compiler' -Ok (Test-Path $il2cppExe) -Detail $(if (Test-Path $il2cppExe) { $il2cppExe } else { 'il2cpp.exe missing' }) -Required
}

$licenseRoots = @("$env:ProgramData\Unity\licenses", "$env:LOCALAPPDATA\Unity\licenses")
$licenseFiles = @()
foreach ($root in $licenseRoots) {
    if (Test-Path $root) {
        $licenseFiles += Get-ChildItem $root -Recurse -File -Filter 'Unity*License*' -ErrorAction SilentlyContinue
    }
}
$hubRoot = "$env:LOCALAPPDATA\Packages"
if (Test-Path $hubRoot) {
    Get-ChildItem $hubRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'UnityTechnologies.UnityHub_*' } |
        ForEach-Object {
            $dir = Join-Path $_.FullName 'LocalCache\Local\Unity\licenses'
            if (Test-Path $dir) {
                $licenseFiles += Get-ChildItem $dir -Recurse -File -Filter 'Unity*License*' -ErrorAction SilentlyContinue
            }
        }
}
$ulfRoots = @("$env:ProgramData\Unity\Unity_lic.ulf", "$env:LOCALAPPDATA\Unity\Unity_lic.ulf", "$env:APPDATA\Unity\Unity_lic.ulf")
$ulf = $ulfRoots | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($ulf) {
    Write-Check -Name 'Unity license file' -Ok $true -Detail $ulf
}
elseif ($licenseFiles.Count -gt 0) {
    # An entitlement is not a seat license. A batchmode run is the only proof.
    Write-Check -Name 'Unity license file' -Ok $false -Detail "$($licenseFiles.Count) entitlement file(s) only; batchmode seat validation still required"
}
else {
    Write-Check -Name 'Unity license file' -Ok $false -Detail 'no license/entitlement found; activate once via Unity Hub' -Required
}

Write-Host ""
Write-Host "--- Windows C++ / IL2CPP ---"
$cl = Get-Command cl.exe -ErrorAction SilentlyContinue
if (-not $cl) {
    $vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $vsPath = (& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2>$null | Select-Object -First 1)
        if ($vsPath) {
            $cl = Get-ChildItem (Join-Path $vsPath 'VC\Tools\MSVC') -Directory -ErrorAction SilentlyContinue |
                Sort-Object Name -Descending |
                ForEach-Object { Join-Path $_.FullName 'bin\Hostx64\x64\cl.exe' } |
                Where-Object { Test-Path $_ } |
                Select-Object -First 1
        }
    }
}
Write-Check -Name 'MSVC cl.exe' -Ok ([bool]$cl) -Detail $(if ($cl) { if ($cl.Source) { $cl.Source } else { $cl } } else { 'not found (Visual Studio C++ workload / Build Tools)' }) -Required

$sdkRoots = @('C:\Program Files (x86)\Windows Kits\10\Include', 'C:\Program Files\Windows Kits\10\Include')
$sdkInclude = $null
foreach ($root in $sdkRoots) {
    if (Test-Path $root) {
        $sdkInclude = Get-ChildItem $root -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
        if ($sdkInclude) { break }
    }
}
Write-Check -Name 'Windows SDK' -Ok ([bool]$sdkInclude) -Detail $(if ($sdkInclude) { $sdkInclude.FullName } else { 'not found (Windows 10/11 SDK)' }) -Required

$cmake = Get-Command cmake.exe -ErrorAction SilentlyContinue
Write-Check -Name 'CMake' -Ok ([bool]$cmake) -Detail $(if ($cmake) { $cmake.Source } else { 'not found' }) -Required
$ninja = Get-Command ninja.exe -ErrorAction SilentlyContinue
Write-Check -Name 'Ninja' -Ok ([bool]$ninja) -Detail $(if ($ninja) { $ninja.Source } else { 'not found' }) -Required

Write-Host ""
Write-Host "--- Android ---"
$androidHomes = @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT, "$env:LOCALAPPDATA\Android\Sdk", 'C:\Android\Sdk', 'D:\Android\Sdk') | Where-Object { $_ }
$androidSdk = $androidHomes | Where-Object { Test-Path $_ } | Select-Object -First 1
Write-Check -Name 'Android SDK' -Ok ([bool]$androidSdk) -Detail $(if ($androidSdk) { $androidSdk } else { 'not found' }) -Required

$ndkRoots = @()
if ($androidSdk) { $ndkRoots += (Join-Path $androidSdk 'ndk') }
if ($env:ANDROID_NDK_HOME) { $ndkRoots += $env:ANDROID_NDK_HOME }
if ($env:ANDROID_NDK_ROOT) { $ndkRoots += $env:ANDROID_NDK_ROOT }
$ndk = $null
foreach ($root in $ndkRoots) {
    if (Test-Path $root) {
        $ndk = $root
        break
    }
}
Write-Check -Name 'Android NDK' -Ok ([bool]$ndk) -Detail $(if ($ndk) { $ndk } else { 'not found (install NDK matching Unity 6.3; V4 SDK has no NDK)' }) -Required

$adb = Get-Command adb.exe -ErrorAction SilentlyContinue
$adbPath = if ($adb) { if ($adb.Source) { $adb.Source } else { $adb.FullName } } else { $null }
if (-not $adbPath -and $androidSdk) {
    $candidate = Join-Path $androidSdk 'platform-tools\adb.exe'
    if (Test-Path $candidate) { $adbPath = $candidate }
}
Write-Check -Name 'Android adb' -Ok ([bool]$adbPath) -Detail $(if ($adbPath) { $adbPath } else { 'not found' }) -Required

$javaCandidates = @()
$javaOnPath = Get-Command java.exe -ErrorAction SilentlyContinue
if ($javaOnPath) { $javaCandidates += $javaOnPath.Source }
if ($env:JAVA_HOME) { $javaCandidates += (Join-Path $env:JAVA_HOME 'bin\java.exe') }
$javaCandidates += 'C:\Program Files\Android\Android Studio\jbr\bin\java.exe'
$java = $javaCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
Write-Check -Name 'JDK java.exe' -Ok ([bool]$java) -Detail $(if ($java) { $java } else { 'not found (JAVA_HOME or Android Studio JBR)' }) -Required

Write-Host ""
Write-Host "--- iOS / macOS ---"
$uname = Get-Command uname -ErrorAction SilentlyContinue
$isMac = $uname -and ((& $uname -s 2>$null) -eq 'Darwin')
Write-Check -Name 'macOS/Xcode host' -Ok ([bool]$isMac) -Detail $(if ($isMac) { 'Darwin detected' } else { 'Windows host; iOS ARM64 IL2CPP requires macOS + Xcode + device' }) -Required

Write-Host ""
Write-Host "Summary: $script:blocked required prerequisite group(s) blocked on this host."
Write-Host "This script is read-only; it does not prove any build or serialization round trip."
exit 0
