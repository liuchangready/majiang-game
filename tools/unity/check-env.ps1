<#
.SYNOPSIS
    Reports the state of the Unity toolchain on this machine.

.DESCRIPTION
    Read-only environment check for the V5 migration: Unity Hub, Unity editor and its
    modules, .NET SDK, Git/LFS, license state and the repo-side Unity project.
    Required items decide the exit code; optional items are reported for follow-up.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Continue'
$script:requiredFailures = 0
$script:warnings = 0

function Write-Result {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Status,
        [string]$Detail
    )

    $color = switch ($Status) {
        'PASS' { 'Green' }
        'FAIL' { 'Red' }
        default { 'Yellow' }
    }
    Write-Host ("{0,-5} {1,-26} {2}" -f $Status, $Name, $Detail) -ForegroundColor $color
}

function Test-Required {
    param([string]$Name, [bool]$Ok, [string]$Detail)
    if ($Ok) { Write-Result -Name $Name -Status 'PASS' -Detail $Detail }
    else { $script:requiredFailures++; Write-Result -Name $Name -Status 'FAIL' -Detail $Detail }
}

function Test-Optional {
    param([string]$Name, [bool]$Ok, [string]$Detail)
    if ($Ok) { Write-Result -Name $Name -Status 'PASS' -Detail $Detail }
    else { $script:warnings++; Write-Result -Name $Name -Status 'WARN' -Detail $Detail }
}

Write-Host "=== Unity toolchain ==="

$hub = Get-AppxPackage -Name '*UnityHub*' -ErrorAction SilentlyContinue
Test-Required -Name 'Unity Hub' -Ok ([bool]$hub) -Detail $(if ($hub) { "MSIX $($hub.Version)" } else { 'not installed' })

$editorRoots = @('D:\Unity\Hub\Editor', "$env:ProgramFiles\Unity\Hub\Editor")
$editors = @()
foreach ($root in $editorRoots) {
    if (Test-Path $root) {
        $editors += Get-ChildItem $root -Directory -ErrorAction SilentlyContinue |
            Where-Object { Test-Path (Join-Path $_.FullName 'Editor\Unity.exe') }
    }
}
Test-Required -Name 'Unity editor' -Ok ($editors.Count -gt 0) -Detail $(if ($editors) { (($editors | ForEach-Object { $_.Name }) -join ', ') } else { 'no editor install found' })

$editor = $editors | Select-Object -First 1
if ($editor) {
    $il2cpp = Test-Path (Join-Path $editor.FullName 'Editor\Data\PlaybackEngines\windowsstandalonesupport\Variations\win64_player_development_il2cpp')
    Test-Optional -Name 'Windows IL2CPP' -Ok $il2cpp -Detail $(if ($il2cpp) { 'installed' } else { 'module missing' })

    $androidPlayer = Join-Path $editor.FullName 'Editor\Data\PlaybackEngines\AndroidPlayer'
    Test-Optional -Name 'Android Build Support' -Ok (Test-Path $androidPlayer) -Detail $(if (Test-Path $androidPlayer) { 'installed' } else { 'module missing' })

    $androidSdk = Join-Path $androidPlayer 'SDK'
    Test-Optional -Name 'Android SDK/NDK (Unity)' -Ok (Test-Path $androidSdk) -Detail $(if (Test-Path $androidSdk) { $androidSdk } else { 'not bundled; point External Tools at an SDK or add the module' })
}

$dotnet = $null
$candidates = @('D:\Unity\dotnet', $env:DOTNET_ROOT)
$onPath = Get-Command dotnet -ErrorAction SilentlyContinue
if ($onPath -and $onPath.Source) { $candidates += (Split-Path -Parent $onPath.Source) }
foreach ($candidate in $candidates) {
    if ($candidate -and (Test-Path (Join-Path $candidate 'dotnet.exe'))) { $dotnet = Join-Path $candidate 'dotnet.exe'; break }
}
$sdk = if ($dotnet) { (& $dotnet --list-sdks 2>$null) -join '; ' } else { '' }
Test-Required -Name '.NET SDK' -Ok ([bool]$sdk) -Detail $(if ($sdk) { "$sdk @ $dotnet" } else { 'not found (expected D:\Unity\dotnet\dotnet.exe)' })

$git = (Get-Command git -ErrorAction SilentlyContinue).Source
Test-Required -Name 'Git' -Ok ([bool]$git) -Detail $(if ($git) { "& git --version = $(& git --version)" } else { 'not found' })

$lfs = if ($git) { & git lfs version 2>$null } else { $null }
Test-Optional -Name 'Git LFS' -Ok ([bool]$lfs) -Detail $(if ($lfs) { $lfs } else { 'not found' })

$licenseFiles = @()
$licenseDirs = @("$env:ProgramData\Unity\licenses", "$env:LOCALAPPDATA\Unity\licenses")
# Unity Hub is installed as MSIX, so the entitlement it downloads is redirected into the package local cache.
$licenseDirs += Get-ChildItem "$env:LOCALAPPDATA\Packages" -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like 'UnityTechnologies.UnityHub_*' } |
    ForEach-Object { Join-Path $_.FullName 'LocalCache\Local\Unity\licenses' }
foreach ($dir in $licenseDirs) {
    if (Test-Path $dir) {
        $licenseFiles += Get-ChildItem $dir -File -Filter 'Unity*License*' -ErrorAction SilentlyContinue
    }
}
Test-Optional -Name 'Unity license' -Ok ($licenseFiles.Count -gt 0) -Detail $(if ($licenseFiles) { ($licenseFiles | ForEach-Object { "$($_.Name) ($($_.LastWriteTime.ToString('yyyy-MM-dd HH:mm')))" }) -join ', ' } else { 'no entitlement yet - sign in once via Unity Hub, then re-run' })

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $repoRoot 'unity\Mahjong.Unity'
Test-Required -Name 'Unity project' -Ok (Test-Path (Join-Path $project 'ProjectSettings\ProjectVersion.txt')) -Detail $project

$plugins = Join-Path $project 'Assets\Plugins\Mahjong'
$pluginCount = if (Test-Path $plugins) { (Get-ChildItem $plugins -Filter '*.dll' | Measure-Object).Count } else { 0 }
Test-Optional -Name 'Shared assemblies' -Ok ($pluginCount -ge 5) -Detail "$pluginCount DLL(s) in Assets\Plugins\Mahjong"

Write-Host ""
if ($script:requiredFailures -eq 0) {
    Write-Host "Required checks passed ($($script:warnings) warning(s))." -ForegroundColor Green
    exit 0
}

Write-Host "$($script:requiredFailures) required check(s) failed, $($script:warnings) warning(s)." -ForegroundColor Red
exit 1
