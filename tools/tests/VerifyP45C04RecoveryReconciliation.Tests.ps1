#Requires -Version 5.1
<#
.SYNOPSIS
    TOOL_FIXTURE negative/positive matrix for the C04 semantic verifier.

.DESCRIPTION
    Copies two already-recorded Player cases (one recovered, one rejected) into an
    ignored scratch root. The copies are fixtures, not new GAME evidence. The
    original case roots are only read. Each mutation must fail; both untouched
    copies must pass. The test never replaces the verifier with a claim check.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$VerifierPath,
    [Parameter(Mandatory = $true)][string]$RecoveredCaseRoot,
    [Parameter(Mandatory = $true)][string]$RejectedCaseRoot,
    [Parameter(Mandatory = $true)][string]$HelperPath,
    [string]$DotnetPath = 'dotnet',
    [string]$OutputDir = ''
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repo ('build/c04-recovery-verifier-tests/' + [Guid]::NewGuid().ToString('N'))
}
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
if (Test-Path -LiteralPath $OutputDir) { throw ('OUTPUT_REUSE ' + $OutputDir) }
[IO.Directory]::CreateDirectory($OutputDir) | Out-Null

$script:Total = 0
$script:Passed = 0
$script:Failed = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Get-Sha {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Write-Json {
    param([string]$Path, $Object)
    $json = $Object | ConvertTo-Json -Depth 30
    [IO.File]::WriteAllText($Path, $json, (New-Object Text.UTF8Encoding($false)))
}

function Copy-Case {
    param([string]$Source, [string]$Name)
    $dest = Join-Path $OutputDir $Name
    Copy-Item -LiteralPath $Source -Destination $dest -Recurse -Force
    return $dest
}

function Invoke-Verifier {
    param([string]$CaseDir, [string]$Label)
    $verification = Join-Path $CaseDir ('verification-' + $Label + '.json')
    $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $VerifierPath `
        -ResultPath (Join-Path $CaseDir 'result.json') `
        -ExpectedPath (Join-Path $CaseDir 'expected.txt') `
        -InputPath (Join-Path $CaseDir 'input.json') `
        -OutputPath $verification `
        -HelperPath $HelperPath -DotnetPath $DotnetPath 2>&1
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = ($output -join ' ')
        Verification = $verification
    }
}

function Invoke-Case {
    param([string]$Name, [string]$Source, [bool]$ExpectPass, [scriptblock]$Mutate)
    $script:Total++
    try {
        $dir = Copy-Case -Source $Source -Name $Name
        if ($null -ne $Mutate) { & $Mutate $dir }
        $run = Invoke-Verifier -CaseDir $dir -Label $Name
        if ($ExpectPass) {
            Assert-True ($run.ExitCode -eq 0) ('expected PASS, got ' + $run.ExitCode + ': ' + $run.Output)
            $parsed = [IO.File]::ReadAllText($run.Verification) | ConvertFrom-Json
            Assert-True ([string]$parsed.status -eq 'PASS') ('verification status ' + $parsed.status)
        }
        else {
            Assert-True ($run.ExitCode -ne 0) ('expected FAIL, got ' + $run.ExitCode + ': ' + $run.Output)
            $parsed = [IO.File]::ReadAllText($run.Verification) | ConvertFrom-Json
            Assert-True ([string]$parsed.status -eq 'FAIL') ('negative verification status ' + $parsed.status)
        }
        $script:Passed++
        Write-Host ('PASS ' + $Name)
    }
    catch {
        $script:Failed++
        Write-Host ('FAIL ' + $Name + ': ' + $_.Exception.Message)
    }
}

function Read-Result {
    param([string]$Dir)
    return [IO.File]::ReadAllText((Join-Path $Dir 'result.json')) | ConvertFrom-Json
}

function Save-Result {
    param([string]$Dir, $Result)
    Write-Json -Path (Join-Path $Dir 'result.json') -Object $Result
}

function Mutate-FrameBytes {
    param([string]$Dir)
    $result = Read-Result $Dir
    $frame = @($result.frames | Where-Object { $_.direction -eq 'OUT' -and $_.type -eq 'Command' })
    if ($frame.Count -eq 0) { $frame = @($result.frames | Where-Object { $_.direction -eq 'OUT' } | Select-Object -First 1) }
    $relative = [string]$frame[0].file
    $path = Join-Path $Dir $relative
    [IO.File]::WriteAllBytes($path, [Text.Encoding]::UTF8.GetBytes('not-a-protobuf-envelope'))
    $frame[0].sha256 = Get-Sha $path
    Save-Result $Dir $result
}

function Remove-LookupFrames {
    param([string]$Dir)
    $result = Read-Result $Dir
    $result.frames = @($result.frames | Where-Object { -not ($_.direction -eq 'IN' -and $_.type -eq 'ReceiptLookup') })
    Save-Result $Dir $result
}

function Mutate-Identity {
    param([string]$Dir)
    $result = Read-Result $Dir
    $result.binding.roomId = 'foreign-room'
    $result.binding.seat = 3
    $result.binding.role = 'Spectator'
    $result.binding.scopeId = 'foreign-scope'
    $result.process.sha256 = ('00' * 32)
    $result.process.pid = 0
    $result.process.startUtc = ''
    Save-Result $Dir $result
}

function Mutate-Ledger {
    param([string]$Dir)
    $result = Read-Result $Dir
    $result.state.ledgerPoints = [long]$result.state.ledgerPoints + 99
    $result.state.ledgerEntries = [int]$result.state.ledgerEntries + 1
    Save-Result $Dir $result
}

function Mutate-RejectedReady {
    param([string]$Dir)
    $result = Read-Result $Dir
    $result.state | Add-Member -NotePropertyName ready -NotePropertyValue $true -Force
    Save-Result $Dir $result
}

function Remove-Authority {
    param([string]$Dir)
    $result = Read-Result $Dir
    $file = @($result.authority.files | Where-Object { $_.path -like '*prepare-post.save' })[0]
    Remove-Item -LiteralPath (Join-Path $Dir $file.path) -Force
}

function Mutate-Digest {
    param([string]$Dir)
    $result = Read-Result $Dir
    $result.request.payloadSha256 = ('AB' * 32) -join ''
    Save-Result $Dir $result
}

function Mutate-Policy {
    param([string]$Dir)
    $result = Read-Result $Dir
    $result.caseId = 'C04-G6-06'
    $result.fault = 'BothMissing'
    $result.expectedOutcome = 'FRESH_START'
    Save-Result $Dir $result
}

Invoke-Case -Name 'valid-recovered' -Source $RecoveredCaseRoot -ExpectPass $true -Mutate $null
Invoke-Case -Name 'valid-rejected' -Source $RejectedCaseRoot -ExpectPass $true -Mutate $null
Invoke-Case -Name 'garbage-reframed' -Source $RecoveredCaseRoot -ExpectPass $false -Mutate { param($d) Mutate-FrameBytes $d }
Invoke-Case -Name 'missing-inbound-lookup' -Source $RecoveredCaseRoot -ExpectPass $false -Mutate { param($d) Remove-LookupFrames $d }
Invoke-Case -Name 'foreign-identity' -Source $RecoveredCaseRoot -ExpectPass $false -Mutate { param($d) Mutate-Identity $d }
Invoke-Case -Name 'invented-ledger' -Source $RecoveredCaseRoot -ExpectPass $false -Mutate { param($d) Mutate-Ledger $d }
Invoke-Case -Name 'rejected-ready' -Source $RejectedCaseRoot -ExpectPass $false -Mutate { param($d) Mutate-RejectedReady $d }
Invoke-Case -Name 'missing-raw-authority' -Source $RecoveredCaseRoot -ExpectPass $false -Mutate { param($d) Remove-Authority $d }
Invoke-Case -Name 'wrong-digest' -Source $RecoveredCaseRoot -ExpectPass $false -Mutate { param($d) Mutate-Digest $d }
Invoke-Case -Name 'wrong-case-policy' -Source $RecoveredCaseRoot -ExpectPass $false -Mutate { param($d) Mutate-Policy $d }

$summary = [ordered]@{
    schemaVersion = 1
    kind = 'c04-recovery-verifier-tool-tests'
    evidenceClass = 'TOOL_FIXTURE'
    total = $script:Total
    passed = $script:Passed
    failed = $script:Failed
    status = if ($script:Failed -eq 0) { 'PASS' } else { 'FAIL' }
    recoveredFixtureRoot = [IO.Path]::GetFullPath($RecoveredCaseRoot)
    rejectedFixtureRoot = [IO.Path]::GetFullPath($RejectedCaseRoot)
    outputDir = $OutputDir
    generatedAt = [DateTimeOffset]::UtcNow.ToString('o')
}
$summaryPath = Join-Path $OutputDir 'summary.json'
Write-Json -Path $summaryPath -Object $summary
Write-Host ('C04_VERIFIER_TOOL_TESTS total=' + $script:Total + ' passed=' + $script:Passed +
    ' failed=' + $script:Failed + ' summary=' + $summaryPath + ' sha=' + (Get-Sha $summaryPath))
if ($script:Failed -ne 0) { exit 1 }
exit 0
