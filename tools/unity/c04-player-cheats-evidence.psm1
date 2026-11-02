Set-StrictMode -Version Latest

function Invoke-C04PlayerCheatEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$DotNetPath,
        [Parameter(Mandatory = $true)][string]$VerifierDll,
        [Parameter(Mandatory = $true)][string]$Evidence,
        [Parameter(Mandatory = $true)][ValidateSet('authorized', 'negative')][string]$Case,
        [Parameter(Mandatory = $true)][string]$SourceHead,
        [Parameter(Mandatory = $true)][string]$PackageManifest
    )

    foreach ($path in @($DotNetPath, $VerifierDll, $Evidence, $PackageManifest)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "C04_VERIFIER_INPUT_MISSING: $path"
        }
    }

    $output = @(& $DotNetPath $VerifierDll verify `
        --evidence $Evidence `
        --case $Case `
        --source-head $SourceHead `
        --package-manifest $PackageManifest 2>&1)
    $nativeExit = $LASTEXITCODE
    $text = ($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
    $global:LASTEXITCODE = $nativeExit
    [pscustomobject]@{
        NativeExit = $nativeExit
        Output = $text
        Passed = ($nativeExit -eq 0)
    }
}

Export-ModuleMember -Function Invoke-C04PlayerCheatEvidence
