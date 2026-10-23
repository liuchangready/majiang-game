<#
.SYNOPSIS
    Builds the shared C# libraries and copies the complete Unity-consumed assembly
    closure into the Unity project.

.DESCRIPTION
    The shared assemblies target .NET Standard 2.1 / C# 9 so the exact same binaries
    load in Unity 6 and in the .NET server processes. Run this after changing any
    project under dotnet/src, then let Unity recompile the presentation assemblies.

    The copy set is the full runtime closure the Unity client consumes, not just the
    five historical modules:
      - every plugin project's own assembly (Domain, Contracts, Contracts.Wire,
        TableRuntime, Client, LocalHost, Ai)
      - third-party runtime dependencies resolved from each plugin project's
        project.assets.json against the NuGet global packages cache (today that is
        Google.Protobuf.dll for the Wire codec)
    Test projects, the outbox crash-worker helper, test frameworks (xunit,
    Microsoft.Testing.*), build tools (Grpc.Tools) and SDK payloads are never copied.

    Guards:
      - every plugin project must produce bin/<config>/netstandard2.1/<name>.dll
        (a shared assembly silently bumped above the Unity floor fails the build)
      - the effective LangVersion must stay <= 9.0 (csproj value, else
        dotnet/Directory.Build.props)
      - duplicate third-party assembly names are only accepted when every source
        copy has the identical SHA-256; conflicting versions abort the copy

.PARAMETER Configuration
    Debug or Release. Defaults to Release.

.PARAMETER DotnetPath
    Path to dotnet.exe. Defaults to DOTNET_ROOT, then D:\Unity\dotnet\dotnet.exe,
    then whatever dotnet is on PATH.

.PARAMETER PluginOutputDir
    Optional override for the copy destination. Defaults to
    unity\Mahjong.Unity\Assets\Plugins\Mahjong. Pass a scratch directory to inspect
    the copy result without touching the tracked plugin DLLs (used by CI and by
    tools\verify-v5.ps1; verification must never overwrite tracked Unity assets).
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$DotnetPath,

    [string]$PluginOutputDir
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$solution = Join-Path $root 'dotnet\Mahjong.sln'
$defaultPluginDir = Join-Path $root 'unity\Mahjong.Unity\Assets\Plugins\Mahjong'
$pluginDir = if ($PluginOutputDir) { $PluginOutputDir } else { $defaultPluginDir }

# Assemblies the Unity client consumes (own plugin projects, dependency order).
$pluginProjects = @(
    'Mahjong.Domain',
    'Mahjong.Contracts',
    'Mahjong.Contracts.Wire',
    'Mahjong.TableRuntime',
    'Mahjong.Client',
    'Mahjong.LocalHost',
    'Mahjong.Ai'
)

# Never allow test / helper / tooling payloads into the Unity plugin folder.
$forbiddenPatterns = @(
    'Mahjong.*.Tests.*',
    'Mahjong.Outbox.CrashWorker.*',
    'xunit.*',
    'Microsoft.Testing.*',
    'Microsoft.ApplicationInsights.*',
    'Grpc.*',
    'Google.Protobuf.Tools.*'
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

function Test-AllowedFileName([string]$name) {
    foreach ($pattern in $forbiddenPatterns) {
        if ($name -like $pattern) { return $false }
    }
    return $true
}

# Effective LangVersion: csproj override wins, otherwise dotnet/Directory.Build.props.
function Get-EffectiveLangVersion([string]$csprojPath, [string]$propsPath) {
    $csprojValue = (Select-Xml -Path $csprojPath -XPath '//*[local-name()="LangVersion"]').Node.'#text'
    if ($csprojValue) { return $csprojValue.Trim() }
    $propsValue = (Select-Xml -Path $propsPath -XPath '//*[local-name()="LangVersion"]').Node.'#text'
    if ($propsValue) { return $propsValue.Trim() }
    return $null
}

function Assert-LangVersionFloor([string]$value, [string]$csprojPath) {
    # Shared assemblies must stay at or below the Unity 6 / netstandard2.1 floor (C# 9).
    if (-not $value) {
        throw "LangVersion not set for '$csprojPath' (neither in the csproj nor in dotnet/Directory.Build.props); the Unity floor would be left to toolchain defaults"
    }
    $numeric = $value -replace '^([0-9]+(\.[0-9]+)?).*$', '$1'
    if ($numeric -notmatch '^[0-9]+(\.[0-9]+)?$' -or [double]$numeric -gt 9.0) {
        throw "LangVersion '$value' in '$csprojPath' exceeds the shared-library C# 9 floor"
    }
}

Write-Host "dotnet   : $DotnetPath"
Write-Host "solution : $solution"
Write-Host "config   : $Configuration"
Write-Host "plugins  : $pluginDir"

$propsPath = Join-Path $root 'dotnet\Directory.Build.props'

foreach ($project in $pluginProjects) {
    $csproj = Join-Path $root "dotnet\src\$project\$project.csproj"
    if (-not (Test-Path $csproj)) {
        throw "Plugin project not found: $csproj"
    }
    $tfmInCsproj = (Select-Xml -Path $csproj -XPath '//*[local-name()="TargetFramework"]').Node.'#text'
    if ($tfmInCsproj -and $tfmInCsproj.Trim() -ne 'netstandard2.1') {
        throw "$project targets '$tfmInCsproj'; shared Unity assemblies must stay on netstandard2.1"
    }
    Assert-LangVersionFloor (Get-EffectiveLangVersion $csproj $propsPath) $csproj
}

& $DotnetPath build $solution -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE"
}

# --- collect the plugin assembly closure ---------------------------------------
# candidate map: file name -> list of @{ Source; Path; Sha256 }
$candidates = @{}

function Add-Candidate([string]$source, [System.IO.FileInfo]$file) {
    if (-not (Test-AllowedFileName $file.Name)) { return }
    $entry = @{
        Source = $source
        Path   = $file.FullName
        Sha256 = (Get-FileHash -Algorithm SHA256 $file.FullName).Hash
    }
    if (-not $candidates.ContainsKey($file.Name)) {
        $candidates[$file.Name] = New-Object System.Collections.Generic.List[object]
    }
    $candidates[$file.Name].Add($entry) | Out-Null
}

foreach ($project in $pluginProjects) {
    $binDir = Join-Path $root "dotnet\src\$project\bin\$Configuration\netstandard2.1"
    $dll = Join-Path $binDir "$project.dll"
    if (-not (Test-Path $dll)) {
        throw "Missing build output: $dll (shared assemblies must target netstandard2.1)"
    }
    Add-Candidate $project (Get-Item $dll)
    $pdb = Join-Path $binDir "$project.pdb"
    if (Test-Path $pdb) {
        Add-Candidate $project (Get-Item $pdb)
    }
}

# Third-party runtime closure from the resolved asset graph (e.g. Google.Protobuf
# for Contracts.Wire). Class libraries do not copy package assemblies to bin, so
# the DLLs are resolved against the NuGet global packages cache.
if (-not $env:NUGET_PACKAGES) {
    $localsOutput = & $DotnetPath nuget locals global-packages --list 2>$null
    if ($LASTEXITCODE -eq 0 -and $localsOutput -match 'global-packages:\s*(.+)$') {
        $env:NUGET_PACKAGES = $Matches[1].Trim()
    }
}
$nugetCache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
Write-Host "nuget    : $nugetCache"

foreach ($project in $pluginProjects) {
    $assetsPath = Join-Path $root "dotnet\src\$project\obj\project.assets.json"
    if (-not (Test-Path $assetsPath)) {
        throw "Missing $assetsPath; run a restore before copying plugins"
    }
    $assets = Get-Content $assetsPath -Raw | ConvertFrom-Json
    $targetProp = $assets.targets.PSObject.Properties |
        Where-Object { $_.Name -ieq '.NETStandard,Version=v2.1' }
    if (-not $targetProp) {
        throw "$project was not restored for netstandard2.1 (no '.NETStandard,Version=v2.1' target in project.assets.json)"
    }
    foreach ($packageProp in $targetProp.Value.PSObject.Properties) {
        if ($packageProp.Name -notmatch '^(.+)/([^/]+)$') { continue }
        # Project references show up in the same graph with placeholder paths;
        # only 'package' library entries have a real NuGet payload.
        $library = $assets.libraries.PSObject.Properties[$packageProp.Name]
        if (-not $library -or $library.Value.type -ne 'package') { continue }
        $packageId = $Matches[1]
        $packageVersion = $Matches[2]
        $runtimeProp = $packageProp.Value.runtime
        if (-not $runtimeProp) { continue }
        foreach ($runtimeEntry in $runtimeProp.PSObject.Properties) {
            $relativePath = $runtimeEntry.Name
            $packageDll = Join-Path $nugetCache "$($packageId.ToLower())\$packageVersion\$($relativePath -replace '/', '\')"
            if (-not (Test-Path $packageDll)) {
                throw "Resolved package assembly not found in the NuGet cache: $packageDll"
            }
            Add-Candidate "$packageId $packageVersion" (Get-Item $packageDll)
        }
    }
}

# Duplicate check: same file name from different sources must be byte-identical.
foreach ($name in $candidates.Keys | Sort-Object) {
    $entries = $candidates[$name]
    $distinct = $entries | ForEach-Object Sha256 | Select-Object -Unique
    if (@($distinct).Count -gt 1) {
        $details = ($entries | ForEach-Object { "$($_.Source)[$($_.Sha256.Substring(0,8))]" }) -join ', '
        throw "Conflicting copies of assembly '$name' in the plugin closure: $details"
    }
}

# --- copy ----------------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null

$copied = @()
foreach ($name in $candidates.Keys | Sort-Object) {
    $entry = $candidates[$name][0]
    Copy-Item -LiteralPath $entry.Path -Destination $pluginDir -Force
    $copied += $name
}

Write-Host "copied $($copied.Count) files:"
foreach ($name in $copied) {
    $file = Get-Item (Join-Path $pluginDir $name)
    Write-Host ("  {0,-28} {1,10} bytes  <- {2}" -f $name, $file.Length, $candidates[$name][0].Source)
}

# The Unity client needs at least the plugin assemblies and the Wire runtime.
foreach ($required in @($pluginProjects | ForEach-Object { "$($_).dll" }) + @('Google.Protobuf.dll')) {
    if (-not (Test-Path (Join-Path $pluginDir $required))) {
        throw "Plugin copy incomplete: missing $required in $pluginDir"
    }
}

Write-Host "Unity plugins ready: $pluginDir"
