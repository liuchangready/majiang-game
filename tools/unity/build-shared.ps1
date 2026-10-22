<#
.SYNOPSIS
    Builds the shared C# libraries and copies them into the Unity project.

.DESCRIPTION
    The shared assemblies target .NET Standard 2.1 / C# 9 so the exact same binaries
    load in Unity 6 and in the .NET server processes. Run this after changing any
    project under dotnet/src, then let Unity recompile the presentation assemblies.

.PARAMETER Configuration
    Debug or Release. Defaults to Release.

.PARAMETER DotnetPath
    Path to dotnet.exe. Defaults to DOTNET_ROOT, then D:\Unity\dotnet\dotnet.exe,
    then whatever dotnet is on PATH.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$DotnetPath
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$solution = Join-Path $root 'dotnet\Mahjong.sln'
$pluginDir = Join-Path $root 'unity\Mahjong.Unity\Assets\Plugins\Mahjong'
$projects = @(
    'Mahjong.Domain',
    'Mahjong.Contracts',
    'Mahjong.TableRuntime',
    'Mahjong.Client',
    'Mahjong.LocalHost'
)

if (-not $DotnetPath) {
    if ($env:DOTNET_ROOT) {
        $DotnetPath = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
    }
    elseif (Test-Path 'D:\Unity\dotnet\dotnet.exe') {
        $DotnetPath = 'D:\Unity\dotnet\dotnet.exe'
    }
    else {
        $DotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
    }
}

if (-not (Test-Path $solution)) {
    throw "Solution not found: $solution"
}

Write-Host "dotnet   : $DotnetPath"
Write-Host "solution : $solution"
Write-Host "config   : $Configuration"

& $DotnetPath build $solution -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE"
}

New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null

foreach ($project in $projects) {
    $binDir = Join-Path $root "dotnet\src\$project\bin\$Configuration\netstandard2.1"
    $dll = Join-Path $binDir "$project.dll"
    if (-not (Test-Path $dll)) {
        throw "Missing build output: $dll"
    }

    Copy-Item -LiteralPath $dll -Destination $pluginDir -Force
    $pdb = Join-Path $binDir "$project.pdb"
    if (Test-Path $pdb) {
        Copy-Item -LiteralPath $pdb -Destination $pluginDir -Force
    }
    Write-Host "copied   : $project.dll$(if (Test-Path $pdb) { ' (+pdb)' })"
}

Write-Host "Unity plugins ready: $pluginDir"
