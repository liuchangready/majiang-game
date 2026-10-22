<#
.SYNOPSIS
    Opens the V5 Unity project with the installed Unity 6.3 LTS editor.

.DESCRIPTION
    Requires an activated Unity license. The first open imports packages and art and can
    take a few minutes; later opens reuse Library/.
#>
[CmdletBinding()]
param(
    [string]$EditorVersion = '6000.3.27f1',
    [string]$ProjectPath
)

$ErrorActionPreference = 'Stop'

if (-not $ProjectPath) {
    $ProjectPath = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'unity\Mahjong.Unity'
}

$unity = Join-Path "D:\Unity\Hub\Editor\$EditorVersion" 'Editor\Unity.exe'
if (-not (Test-Path $unity)) {
    throw "Unity not found: $unity"
}
if (-not (Test-Path (Join-Path $ProjectPath 'ProjectSettings\ProjectVersion.txt'))) {
    throw "Not a Unity project: $ProjectPath"
}

Start-Process -FilePath $unity -ArgumentList '-projectPath', $ProjectPath
Write-Host "Opening $ProjectPath with $EditorVersion"
Write-Host "If Unity reports a licensing error, sign in once through Unity Hub."
