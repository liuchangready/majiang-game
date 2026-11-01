#requires -Version 5.1
<#
.SYNOPSIS
    Independent verifier for one C04 recovery-request Player result.

.DESCRIPTION
    This script is a thin, fail-closed wrapper around the read-only .NET semantic
    verifier. The helper decodes the retained raw wire envelopes with the frozen
    Contracts/Wire codec, verifies the inbound Applied ReceiptLookup, re-derives
    state/event/ledger/physical facts from the retained LocalHost save and cheat
    aggregates, checks the exported outbox, and enforces the fixed eight-row
    case/fault/outcome policy. No result.json boolean is trusted on its own.

    The wrapper writes only its requested OutputPath and a helper JSON beside it.
    It never writes into the sealed case directory.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ResultPath,
    [Parameter(Mandatory = $true)][string]$ExpectedPath,
    [Parameter(Mandatory = $true)][string]$InputPath,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [string]$HelperPath = '',
    [string]$DotnetPath = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$errors = New-Object System.Collections.Generic.List[string]

if ([string]::IsNullOrWhiteSpace($HelperPath)) {
    $repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $HelperPath = Join-Path $repo 'dotnet\tools\C04RecoveryReconciliationVerifier\bin\Release\net8.0\C04RecoveryReconciliationVerifier.dll'
}

$helperOutput = $OutputPath + '.helper.json'
$status = 'FAIL'
$helperExit = 99
if (-not (Test-Path -LiteralPath $HelperPath -PathType Leaf)) {
    $errors.Add('semantic verifier helper is missing: ' + $HelperPath) | Out-Null
}
else {
    $output = & $DotnetPath $HelperPath --result $ResultPath --expected $ExpectedPath --input $InputPath --output $helperOutput 2>&1
    $helperExit = $LASTEXITCODE
    foreach ($line in @($output)) {
        Write-Output ([string]$line)
    }

    if ($helperExit -ne 0) {
        if (Test-Path -LiteralPath $helperOutput) {
            try {
                $parsed = [IO.File]::ReadAllText($helperOutput) | ConvertFrom-Json
                foreach ($item in @($parsed.errors)) { $errors.Add([string]$item) | Out-Null }
                if (@($parsed.errors).Count -eq 0) {
                    $errors.Add('semantic verifier exited nonzero without a listed error') | Out-Null
                }
            }
            catch {
                $errors.Add('semantic verifier output is unreadable: ' + $_.Exception.Message) | Out-Null
            }
        }
        else {
            $errors.Add('semantic verifier wrote no output (exit ' + $helperExit + ')') | Out-Null
        }
    }
    else {
        $status = 'PASS'
    }
}

$verification = [ordered]@{
    schemaVersion = 1
    kind = 'c04-recovery-verification'
    status = $status
    resultPath = [IO.Path]::GetFullPath($ResultPath)
    expectedPath = [IO.Path]::GetFullPath($ExpectedPath)
    inputPath = [IO.Path]::GetFullPath($InputPath)
    helperPath = [IO.Path]::GetFullPath($HelperPath)
    helperExitCode = $helperExit
    helperOutputPath = [IO.Path]::GetFullPath($helperOutput)
    errors = @($errors)
    verifiedAt = [DateTimeOffset]::UtcNow.ToString('o')
}
$json = $verification | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText($OutputPath, $json, (New-Object System.Text.UTF8Encoding($false)))
Write-Output ('C04_VERIFY ' + $status + ' errors=' + $errors.Count + ' output=' + $OutputPath)
if ($errors.Count -ne 0) {
    foreach ($errorText in $errors) { Write-Output ('C04_VERIFY_ERROR ' + $errorText) }
    exit 1
}

exit 0
