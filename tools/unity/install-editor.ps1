<#
.SYNOPSIS
    Downloads and silently installs a Unity editor plus build-support modules.

.DESCRIPTION
    Reproduces the V5 toolchain install without Unity Hub's GUI. Everything is written
    under -InstallRoot (D:\Unity by default) and the downloaded installers stay in
    D:\Unity\downloads so a later re-run skips the download.

    Unity's installer manifest uses "highestAvailable". On an administrator account with
    a filtered token that means a UAC prompt, which is undesirable for an unattended
    setup, so the child process is started with __COMPAT_LAYER=RunAsInvoker. That keeps
    the current token and lets the installer write to the user-writable D: target.

    Every installer is Authenticode-checked before it runs.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\unity\install-editor.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\unity\install-editor.ps1 `
        -EditorVersion 6000.3.27f1 -Changeset 672d562a42a7 -Modules windows-il2cpp,android
#>
[CmdletBinding()]
param(
    [string]$EditorVersion = '6000.3.27f1',
    [string]$Changeset = '672d562a42a7',
    [string]$InstallRoot = 'D:\Unity\Hub\Editor',
    [string]$DownloadRoot = 'D:\Unity\downloads',
    [string[]]$Modules = @('windows-il2cpp', 'android')
)

$ErrorActionPreference = 'Stop'
$editorDir = Join-Path $InstallRoot $EditorVersion
$logPath = Join-Path (Split-Path -Parent (Split-Path -Parent $InstallRoot)) 'install-log.txt'
New-Item -ItemType Directory -Force -Path $DownloadRoot, $InstallRoot | Out-Null

$moduleFiles = @{
    'windows-il2cpp' = 'UnitySetup-Windows-IL2CPP-Support-for-Editor'
    'android'        = 'UnitySetup-Android-Support-for-Editor'
    'webgl'          = 'UnitySetup-WebGL-Support-for-Editor'
    'linux-il2cpp'   = 'UnitySetup-Linux-IL2CPP-Support-for-Editor'
}

function Write-Log {
    param([string]$Message)
    $line = '{0}  {1}' -f (Get-Date -Format 'HH:mm:ss'), $Message
    Write-Host $line
    Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
}

function Get-Installer {
    param([string]$Url, [string]$FileName, [long]$MinimumBytes)

    $target = Join-Path $DownloadRoot $FileName
    if ((Test-Path $target) -and (Get-Item $target).Length -ge $MinimumBytes) {
        Write-Log "cached: $FileName"
    }
    else {
        Write-Log "download: $FileName"
        & curl.exe -L --fail --retry 5 --retry-delay 5 -C - -o $target $Url
        if ($LASTEXITCODE -ne 0) { throw "Download failed ($LASTEXITCODE): $Url" }
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $target
    if ($signature.Status -ne 'Valid') {
        throw "Installer signature is not valid ($($signature.Status)): $target"
    }
    Write-Log "signature valid: $($signature.SignerCertificate.Subject)"
    return $target
}

function Install-Package {
    param([string]$Installer, [string]$Target)

    Write-Log "install: $(Split-Path $Installer -Leaf) -> $Target"
    $env:__COMPAT_LAYER = 'RunAsInvoker'
    $process = Start-Process -FilePath $Installer -ArgumentList '/S', "/D=$Target" -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0) {
        throw "Installer failed with exit code $($process.ExitCode): $Installer"
    }
    Write-Log 'install ok'
}

Write-Log "=== Unity $EditorVersion ($Changeset) ==="

$base = "https://download.unity3d.com/download_unity/$Changeset"
$editorUrl = "$base/Windows64EditorInstaller/UnitySetup64-$EditorVersion.exe"
$editorInstaller = Get-Installer -Url $editorUrl -FileName "UnitySetup64-$EditorVersion.exe" -MinimumBytes 3900000000

if (Test-Path (Join-Path $editorDir 'Editor\Unity.exe')) {
    Write-Log "editor already present: $editorDir"
}
else {
    Install-Package -Installer $editorInstaller -Target $editorDir
}

foreach ($module in $Modules) {
    if (-not $moduleFiles.ContainsKey($module)) { throw "Unknown module id: $module" }
    $fileName = "$($moduleFiles[$module])-$EditorVersion.exe"
    $url = "$base/TargetSupportInstaller/$fileName"
    $installer = Get-Installer -Url $url -FileName $fileName -MinimumBytes 100000000
    Install-Package -Installer $installer -Target $editorDir
}

Write-Log "=== done: $editorDir ==="
Write-Log "Unity.exe: $(Test-Path (Join-Path $editorDir 'Editor\Unity.exe'))"
