<#
.SYNOPSIS
    Reproducible P2.2 probe: load the generated Wire codec in .NET and, when a
    licensed Unity Editor is available, in the Unity Editor itself.

.DESCRIPTION
    The .NET leg runs the netstandard2.1 AOT probe through a net8.0 runner. The
    Unity leg copies the Unity project to a temporary directory, adds the generated
    assemblies there, and invokes:

        Unity.exe -batchmode -quit -nographics -projectPath <temp> ^
            -executeMethod Mahjong.Unity.Platform.WireAotSmoke.Run

    The real project's Assets/Plugins and ProjectSettings are never modified.
    If the Unity Editor is unlicensed or missing, the Unity leg is reported as
    BLOCKED (exit 2), not as a pass.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.PARAMETER SkipUnity
    Run only the .NET leg. Useful on machines without Unity.

.PARAMETER KeepTemp
    Keep the temporary Unity project for inspection.

.PARAMETER DotnetPath
    dotnet.exe path. Defaults to DOTNET_ROOT, D:\Unity\dotnet\dotnet.exe, then PATH.

.PARAMETER UnityPath
    Unity.exe path. Defaults to the newest editor under D:\Unity\Hub\Editor and
    C:\Program Files\Unity\Hub\Editor.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$SkipUnity,

    [switch]$KeepTemp,

    [string]$DotnetPath,

    [string]$UnityPath
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$artifactRoot = Join-Path $repoRoot 'build\p22-aot'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runDir = Join-Path $artifactRoot $stamp
New-Item -ItemType Directory -Force -Path $runDir | Out-Null

$script:results = New-Object System.Collections.Generic.List[object]
$script:failed = $false
$script:blocked = $false

function Add-Result {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Status,
        [string]$Detail
    )

    $script:results.Add([pscustomobject]@{ Name = $Name; Status = $Status; Detail = $Detail })
    $color = switch ($Status) {
        'PASS' { 'Green' }
        'FAIL' { 'Red' }
        'BLOCKED' { 'Yellow' }
        default { 'Gray' }
    }
    Write-Host ("{0,-8} {1,-20} {2}" -f $Status, $Name, $Detail) -ForegroundColor $color
    if ($Status -eq 'FAIL') { $script:failed = $true }
    if ($Status -eq 'BLOCKED') { $script:blocked = $true }
}

if (-not $DotnetPath) {
    if ($env:DOTNET_ROOT -and (Test-Path (Join-Path $env:DOTNET_ROOT 'dotnet.exe'))) {
        $DotnetPath = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
    }
    elseif (Test-Path 'D:\Unity\dotnet\dotnet.exe') {
        $DotnetPath = 'D:\Unity\dotnet\dotnet.exe'
    }
    else {
        $DotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
    }
}

function Find-Unity {
    if ($UnityPath) {
        return $UnityPath
    }

    $roots = @('D:\Unity\Hub\Editor', 'C:\Program Files\Unity\Hub\Editor')
    $editors = @()
    foreach ($root in $roots) {
        if (Test-Path $root) {
            $editors += Get-ChildItem $root -Directory -ErrorAction SilentlyContinue |
                Where-Object { Test-Path (Join-Path $_.FullName 'Editor\Unity.exe') }
        }
    }

    $editor = $editors | Sort-Object Name -Descending | Select-Object -First 1
    if ($editor) {
        return (Join-Path $editor.FullName 'Editor\Unity.exe')
    }

    return $null
}

function Find-GoogleProtobuf {
    $globalPackages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
    $candidate = Join-Path $globalPackages 'google.protobuf\3.28.3\lib\netstandard2.0\Google.Protobuf.dll'
    if (Test-Path $candidate) {
        return $candidate
    }

    return $null
}

Write-Host "repo     : $repoRoot"
Write-Host "dotnet   : $DotnetPath"
Write-Host "artifacts: $runDir"
Write-Host ""

# ---------------------------------------------------------------------------
# .NET leg
# ---------------------------------------------------------------------------
try {
    $dotnetLog = Join-Path $runDir 'dotnet-probe.log'
    $dotnetOutput = & $DotnetPath run --project (Join-Path $repoRoot 'dotnet\tests\Mahjong.Wire.AotSmoke.Runner\Mahjong.Wire.AotSmoke.Runner.csproj') -c $Configuration 2>&1
    $dotnetExit = $LASTEXITCODE
    $dotnetOutput | Tee-Object -FilePath $dotnetLog
    $dotnetText = ($dotnetOutput | Out-String)

    if ($dotnetExit -eq 0 -and $dotnetText -match 'PASS codec=netstandard2\.1') {
        Add-Result -Name '.NET AOT probe' -Status 'PASS' -Detail ("exit=0; " + (($dotnetText -split "`r?`n" | Where-Object { $_ -match '^PASS ' } | Select-Object -First 1)))
    }
    else {
        Add-Result -Name '.NET AOT probe' -Status 'FAIL' -Detail "exit=$dotnetExit; log=$dotnetLog"
    }
}
catch {
    Add-Result -Name '.NET AOT probe' -Status 'FAIL' -Detail $_.Exception.Message
}

# ---------------------------------------------------------------------------
# Unity Editor leg
# ---------------------------------------------------------------------------
if ($SkipUnity) {
    Add-Result -Name 'Unity Editor load' -Status 'BLOCKED' -Detail 'skipped by -SkipUnity'
}
else {
    $unity = Find-Unity
    if (-not $unity) {
        Add-Result -Name 'Unity Editor load' -Status 'BLOCKED' -Detail 'Unity.exe not found'
    }
    else {
        # Supplemental check: compile the smoke script against the exact Unity
        # assemblies with Unity's bundled Roslyn. This is not an Editor import,
        # but it catches syntax/API errors when the Editor license is blocked.
        $editorData = Join-Path (Split-Path $unity) 'Data'
        $csc = Join-Path $editorData 'DotNetSdkRoslyn\csc.dll'
        $unityEngine = Join-Path $editorData 'Managed\UnityEngine.dll'
        $unityEditor = Join-Path $editorData 'Managed\UnityEditor.dll'
        $netstandard = Join-Path $editorData 'NetStandard\ref\2.1.0\netstandard.dll'
        $smokeSource = Join-Path $repoRoot 'unity\Mahjong.Unity\Assets\Scripts\Platform\WireAotSmoke.cs'
        if ((Test-Path $csc) -and (Test-Path $unityEngine) -and (Test-Path $unityEditor) -and (Test-Path $netstandard) -and (Test-Path $smokeSource)) {
            $compileLog = Join-Path $runDir 'unity-smoke-compile.log'
            $compileOut = Join-Path $runDir 'WireAotSmoke.dll'
            $compileArgs = @(
                $csc,
                '-nologo',
                '-nostdlib+',
                '-target:library',
                '-langversion:9.0',
                '-define:UNITY_EDITOR',
                "-out:$compileOut",
                "-r:$unityEngine",
                "-r:$unityEditor",
                "-r:$netstandard",
                $smokeSource
            )
            $compileOutput = & $DotnetPath @compileArgs 2>&1
            $compileExit = $LASTEXITCODE
            $compileOutput | Tee-Object -FilePath $compileLog
            if ($compileExit -eq 0) {
                Add-Result -Name 'Unity script compile' -Status 'PASS' -Detail "Unity Roslyn exit=0; log=$compileLog"
            }
            else {
                Add-Result -Name 'Unity script compile' -Status 'FAIL' -Detail "Unity Roslyn exit=$compileExit; log=$compileLog"
            }
        }
        else {
            Add-Result -Name 'Unity script compile' -Status 'BLOCKED' -Detail 'Unity Roslyn or reference assemblies not found'
        }

        $wireDll = Join-Path $repoRoot "dotnet\src\Mahjong.Contracts.Wire\bin\$Configuration\netstandard2.1\Mahjong.Contracts.Wire.dll"
        $contractsDll = Join-Path $repoRoot "dotnet\src\Mahjong.Contracts\bin\$Configuration\netstandard2.1\Mahjong.Contracts.dll"
        $probeDll = Join-Path $repoRoot "dotnet\tests\Mahjong.Contracts.Wire.AotSmoke\bin\$Configuration\netstandard2.1\Mahjong.Contracts.Wire.AotSmoke.dll"
        $protobufDll = Find-GoogleProtobuf

        $missing = @()
        foreach ($pair in @(
            @{ Path = $wireDll; Name = 'Mahjong.Contracts.Wire.dll' },
            @{ Path = $contractsDll; Name = 'Mahjong.Contracts.dll' },
            @{ Path = $probeDll; Name = 'Mahjong.Contracts.Wire.AotSmoke.dll' },
            @{ Path = $protobufDll; Name = 'Google.Protobuf.dll' }
        )) {
            if (-not $pair.Path -or -not (Test-Path $pair.Path)) {
                $missing += $pair.Name
            }
        }

        if ($missing.Count -gt 0) {
            Add-Result -Name 'Unity Editor load' -Status 'BLOCKED' -Detail ('missing assemblies: ' + ($missing -join ', '))
        }
        else {
            $tempProject = Join-Path $env:TEMP ('mahjong-wire-aot-' + [guid]::NewGuid().ToString('N'))
            $logPath = Join-Path $runDir 'unity-wire-aot.log'
            try {
                New-Item -ItemType Directory -Force -Path $tempProject | Out-Null
                foreach ($part in @('Assets', 'Packages', 'ProjectSettings')) {
                    Copy-Item -LiteralPath (Join-Path $repoRoot "unity\Mahjong.Unity\$part") -Destination $tempProject -Recurse
                }

                # Keep the smoke project minimal: the repository's older
                # precompiled Domain/Client/... DLLs are not part of this probe
                # and could mask a stale Contracts dependency.
                $pluginDir = Join-Path $tempProject 'Assets\Plugins\Mahjong'
                if (Test-Path $pluginDir) {
                    Remove-Item -LiteralPath $pluginDir -Recurse -Force
                }
                New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
                Copy-Item -LiteralPath $wireDll -Destination $pluginDir -Force
                Copy-Item -LiteralPath $contractsDll -Destination $pluginDir -Force
                Copy-Item -LiteralPath $probeDll -Destination $pluginDir -Force
                Copy-Item -LiteralPath $protobufDll -Destination $pluginDir -Force

                Write-Host "Unity    : $unity"
                Write-Host "temp     : $tempProject"
                Write-Host "log      : $logPath"

                $process = Start-Process -FilePath $unity -Wait -PassThru -ArgumentList @(
                    '-batchmode',
                    '-quit',
                    '-nographics',
                    '-projectPath', $tempProject,
                    '-executeMethod', 'Mahjong.Unity.Platform.WireAotSmoke.Run',
                    '-logFile', $logPath
                )

                $unityExit = $process.ExitCode
                $unityLog = if (Test-Path $logPath) { Get-Content $logPath -Raw } else { '' }
                $passLine = ($unityLog -split "`r?`n" | Where-Object { $_ -match '\[WireAotSmoke\] PASS' } | Select-Object -First 1)

                if ($unityExit -eq 0 -and $passLine) {
                    Add-Result -Name 'Unity Editor load' -Status 'PASS' -Detail ("exit=0; " + $passLine.Trim())
                }
                elseif ($unityLog -match 'No valid Unity Editor license found' -or $unityLog -match 'return code 198') {
                    Add-Result -Name 'Unity Editor load' -Status 'BLOCKED' -Detail "Unity license unavailable; exit=$unityExit; log=$logPath"
                }
                elseif ($unityLog -match 'error CS[0-9]+' -or $unityLog -match 'Scripts have compiler errors') {
                    Add-Result -Name 'Unity Editor load' -Status 'FAIL' -Detail "Unity compile error; exit=$unityExit; log=$logPath"
                }
                else {
                    Add-Result -Name 'Unity Editor load' -Status 'FAIL' -Detail "exit=$unityExit; log=$logPath"
                }
            }
            catch {
                Add-Result -Name 'Unity Editor load' -Status 'FAIL' -Detail $_.Exception.Message
            }
            finally {
                if (-not $KeepTemp -and (Test-Path $tempProject)) {
                    Remove-Item -LiteralPath $tempProject -Recurse -Force -ErrorAction SilentlyContinue
                }
            }
        }
    }
}

Add-Result -Name 'IL2CPP Windows' -Status 'BLOCKED' -Detail 'not run by this probe; use check-aot-toolchain.ps1 and a licensed IL2CPP build host'
Add-Result -Name 'IL2CPP Android' -Status 'BLOCKED' -Detail 'not run by this probe; NDK and Unity Android SDK are not detected'
Add-Result -Name 'IL2CPP iOS' -Status 'BLOCKED' -Detail 'requires macOS/Xcode and a real device; unavailable on Windows'

Write-Host ""
$resultsPath = Join-Path $runDir 'results.txt'
$script:results | Format-Table -AutoSize | Out-String | Tee-Object -FilePath $resultsPath
Write-Host "results  : $resultsPath"

if ($script:failed) {
    exit 1
}

if ($script:blocked) {
    exit 2
}

exit 0
