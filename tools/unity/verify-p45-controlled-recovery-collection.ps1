#Requires -Version 5.1
<#
.SYNOPSIS
  Offline verifier for one sealed P4.5 controlled recovery collection case.

.DESCRIPTION
  This verifier is intentionally independent of p45-e2e-common.ps1 and of the
  collector's own summary. It:
    * binds the exact case manifest digest supplied by the reviewer;
    * strictly checks every sealed file and the linear record chain;
    * re-runs the fixed P45RecoveryVerifier on the sealed authority/cheat bytes;
    * cross-checks export/launch/exit/process/package/build/save-fault identity;
    * compares recovery facts and reports factsEqual, collectionChain and
      callerAcceptance as three separate items.

  callerAcceptance is always PENDING_INDEPENDENT_REVIEW. There is no parameter
  or code path that turns this verifier's output into an accepted G2 result.

.EXAMPLE
  powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File tools/unity/verify-p45-controlled-recovery-collection.ps1 `
    -CaseDir build/p45-controlled-collection/<run>/case `
    -ExpectedManifestSha256 <sha printed by the collector> `
    -VerifierPath dotnet/tools/P45RecoveryVerifier/bin/Release/net8.0/P45RecoveryVerifier.dll `
    -DotnetPath D:\Unity\dotnet\dotnet.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CaseDir,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedManifestSha256,

    [Parameter(Mandatory = $true)]
    [string]$VerifierPath,

    [string]$DotnetPath = 'D:\Unity\dotnet\dotnet.exe',
    [string]$OutputDir = '',
    [string]$ExpectedInputManifestSha256 = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$script:ToolVersion = 'P45-CONTROLLED-VERIFY/1'
$script:Utf8 = New-Object System.Text.UTF8Encoding($false)
$script:Checks = New-Object System.Collections.Generic.List[object]
$script:Failures = New-Object System.Collections.Generic.List[string]
$script:ExitCode = 0
$script:Outcome = 'BLOCKED'

function Throw-Ver {
    param([int]$Code, [string]$Status, [string]$Message)
    $safe = (($Message -replace '[\r\n|]+', ' ').Trim())
    throw ('P45VER|' + $Code.ToString([Globalization.CultureInfo]::InvariantCulture) + '|' + $Status + '|' + $safe)
}

function Get-ErrText {
    param([object]$Record)
    if ($null -eq $Record) { return 'unknown error' }
    if ($Record -is [System.Management.Automation.ErrorRecord]) {
        return ([string]$Record.Exception.Message -replace '[\r\n]+', ' ').Trim()
    }
    return ([string]$Record -replace '[\r\n]+', ' ').Trim()
}

function Get-Prop {
    param([object]$Object, [string]$Name, [object]$Default = $null)
    if ($null -eq $Object) { return $Default }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $Default }
    return $property.Value
}

function Get-FullPath {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return '' }
    return [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
}

function Get-Sha256Bytes {
    param([byte[]]$Bytes)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-', '').ToUpperInvariant() }
    finally { $sha.Dispose() }
}

function Get-Sha256Text {
    param([string]$Text)
    return Get-Sha256Bytes -Bytes ($script:Utf8.GetBytes($Text))
}

function Get-FileSig {
    param([string]$Path)
    if (-not [IO.File]::Exists($Path)) { return $null }
    $bytes = [IO.File]::ReadAllBytes($Path)
    [pscustomobject]@{
        path = $Path
        bytes = [long]$bytes.Length
        sha256 = Get-Sha256Bytes -Bytes $bytes
    }
}

function Get-Sha256File {
    param([string]$Path)
    $sig = Get-FileSig -Path $Path
    if ($null -eq $sig) { return $null }
    return [string]$sig.sha256
}

function Read-JsonFile {
    param([string]$Path)
    if (-not [IO.File]::Exists($Path)) { Throw-Ver 17 'EVIDENCE_MISSING' ('JSON evidence is missing: ' + $Path) }
    $text = [IO.File]::ReadAllText($Path, $script:Utf8)
    try { return ($text | ConvertFrom-Json) }
    catch { Throw-Ver 18 'EVIDENCE_INVALID' ('JSON evidence is not parseable: ' + $Path) }
}

function Write-TextAtomic {
    param([string]$Path, [string]$Text)
    [IO.Directory]::CreateDirectory((Split-Path -Parent $Path)) | Out-Null
    $tmp = $Path + '.tmp.' + [Guid]::NewGuid().ToString('N')
    [IO.File]::WriteAllText($tmp, $Text, $script:Utf8)
    if ([IO.File]::Exists($Path)) { [IO.File]::Delete($Path) }
    [IO.File]::Move($tmp, $Path)
}

function Write-JsonAtomic {
    param([string]$Path, [object]$Object)
    Write-TextAtomic -Path $Path -Text (($Object | ConvertTo-Json -Depth 40) + [Environment]::NewLine)
}

function Add-Check {
    param([string]$Name, [bool]$Pass, [string]$Detail = '')
    $script:Checks.Add([pscustomobject]@{ name = $Name; pass = $Pass; detail = $Detail }) | Out-Null
    if (-not $Pass) { $script:Failures.Add($Name + ($(if ([string]::IsNullOrWhiteSpace($Detail)) { '' } else { ': ' + $Detail }))) | Out-Null }
}

function Test-Sha256TextValue {
    param([string]$Text)
    return ($Text -match '^[A-Fa-f0-9]{64}$')
}

function Invoke-FixedVerifier {
    param([string]$AuthorityPath, [string]$CheatStatePath, [string]$OutputDirectory, [string]$Prefix)
    [IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
    $stdoutPath = Join-Path $OutputDirectory ($Prefix + '.stdout.log')
    $stderrPath = Join-Path $OutputDirectory ($Prefix + '.stderr.log')
    $exitPath = Join-Path $OutputDirectory ($Prefix + '.exit.txt')
    $exe = $script:DotnetFull
    $args = @($script:VerifierFull, $AuthorityPath)
    if (-not [string]::IsNullOrWhiteSpace($CheatStatePath)) { $args += $CheatStatePath }
    if ($script:VerifierFull -match '\.exe$') {
        $exe = $script:VerifierFull
        $args = @($AuthorityPath)
        if (-not [string]::IsNullOrWhiteSpace($CheatStatePath)) { $args += $CheatStatePath }
    }
    $raw = & $exe @args 2> $stderrPath
    $exit = $LASTEXITCODE
    $text = (@($raw) | ForEach-Object { [string]$_ }) -join "`n"
    [IO.File]::WriteAllText($stdoutPath, $text, $script:Utf8)
    [IO.File]::WriteAllText($exitPath, ([string]$exit + [Environment]::NewLine), $script:Utf8)
    if (-not [IO.File]::Exists($stderrPath)) { [IO.File]::WriteAllText($stderrPath, '', $script:Utf8) }
    $parsed = $null
    try { $parsed = $text | ConvertFrom-Json } catch { $parsed = $null }
    [pscustomobject]@{
        exitCode = [int]$exit
        parsed = $parsed
        ok = ($exit -eq 0 -and $null -ne $parsed -and [bool](Get-Prop $parsed 'ok' $false))
        stdoutSha256 = Get-Sha256File -Path $stdoutPath
        stderrSha256 = Get-Sha256File -Path $stderrPath
        stdoutPath = $stdoutPath
        stderrPath = $stderrPath
        exitPath = $exitPath
    }
}

function Get-FactsSummary {
    param([object]$Facts)
    if ($null -eq $Facts) { return $null }
    [pscustomobject]@{
        roomId = [string](Get-Prop $Facts 'roomId')
        roundId = [string](Get-Prop $Facts 'roundId')
        rulesetVersion = Get-Prop $Facts 'rulesetVersion'
        stateVersion = [string](Get-Prop $Facts 'stateVersion')
        eventSeq = [string](Get-Prop $Facts 'eventSeq')
        stateFingerprint = [string](Get-Prop $Facts 'stateFingerprint')
        stateHash = [string](Get-Prop $Facts 'stateHash')
        controlVersion = [string](Get-Prop $Facts 'controlVersion')
        cheatVersion = [string](Get-Prop $Facts 'cheatVersion')
        cheatFingerprint = [string](Get-Prop $Facts 'cheatFingerprint')
        cheatStateSha256 = [string](Get-Prop $Facts 'cheatStateSha256')
        authoritySaveSha256 = [string](Get-Prop $Facts 'authoritySaveSha256')
        authoritySaveBytes = Get-Prop $Facts 'authoritySaveBytes'
        controlEpochs = @((Get-Prop $Facts 'controlEpochs' @()) | ForEach-Object { [string]$_ })
        requestCount = @((Get-Prop $Facts 'requests' @())).Count
        controlRequestCount = @((Get-Prop $Facts 'controlRequests' @())).Count
        ledgerPoints = [string](Get-Prop (Get-Prop $Facts 'ledger') 'points')
        ledgerEntryCount = [string](Get-Prop (Get-Prop $Facts 'ledger') 'entryCount')
        ledgerEntriesSha256 = [string](Get-Prop (Get-Prop $Facts 'ledger') 'entriesSha256')
        physicalTileCount = [string](Get-Prop (Get-Prop $Facts 'physical') 'tileCount')
        physicalUniqueCount = [string](Get-Prop (Get-Prop $Facts 'physical') 'uniqueCount')
        physicalTilesSha256 = [string](Get-Prop (Get-Prop $Facts 'physical') 'tilesSha256')
    }
}

function Compare-Facts {
    param([object]$Left, [object]$Right)
    $mismatches = New-Object System.Collections.Generic.List[string]
    if ($null -eq $Left -or $null -eq $Right) {
        return [pscustomobject]@{ equal = $false; mismatches = @('one or both facts are missing') }
    }
    foreach ($field in @('roomId', 'roundId', 'rulesetVersion', 'stateVersion', 'eventSeq',
            'stateFingerprint', 'stateHash', 'controlVersion', 'cheatVersion', 'cheatFingerprint',
            'cheatStateSha256')) {
        $a = [string](Get-Prop $Left $field)
        $b = [string](Get-Prop $Right $field)
        if (-not [string]::Equals($a, $b, [StringComparison]::OrdinalIgnoreCase)) {
            $mismatches.Add($field + ': before=' + $a + ' after=' + $b) | Out-Null
        }
    }
    $aEpochs = (@((Get-Prop $Left 'controlEpochs' @())) | ForEach-Object { [string]$_ }) -join ','
    $bEpochs = (@((Get-Prop $Right 'controlEpochs' @())) | ForEach-Object { [string]$_ }) -join ','
    if (-not [string]::Equals($aEpochs, $bEpochs, [StringComparison]::Ordinal)) {
        $mismatches.Add('controlEpochs: before=' + $aEpochs + ' after=' + $bEpochs) | Out-Null
    }
    $leftLedger = Get-Prop $Left 'ledger'
    $rightLedger = Get-Prop $Right 'ledger'
    foreach ($field in @('points', 'initialPoints', 'entryCount', 'entriesSha256', 'balancesSha256', 'totalVersion', 'checkpointVersion', 'source')) {
        $a = [string](Get-Prop $leftLedger $field)
        $b = [string](Get-Prop $rightLedger $field)
        if (-not [string]::Equals($a, $b, [StringComparison]::OrdinalIgnoreCase)) {
            $mismatches.Add('ledger.' + $field + ': before=' + $a + ' after=' + $b) | Out-Null
        }
    }
    $aBalances = (@((Get-Prop $leftLedger 'balances' @())) | ForEach-Object { [string]$_ }) -join ','
    $bBalances = (@((Get-Prop $rightLedger 'balances' @())) | ForEach-Object { [string]$_ }) -join ','
    if (-not [string]::Equals($aBalances, $bBalances, [StringComparison]::Ordinal)) {
        $mismatches.Add('ledger.balances: before=' + $aBalances + ' after=' + $bBalances) | Out-Null
    }
    $leftPhysical = Get-Prop $Left 'physical'
    $rightPhysical = Get-Prop $Right 'physical'
    foreach ($field in @('tileCount', 'uniqueCount', 'wallCount', 'deadWallCount', 'tilesSha256')) {
        $a = [string](Get-Prop $leftPhysical $field)
        $b = [string](Get-Prop $rightPhysical $field)
        if (-not [string]::Equals($a, $b, [StringComparison]::OrdinalIgnoreCase)) {
            $mismatches.Add('physical.' + $field + ': before=' + $a + ' after=' + $b) | Out-Null
        }
    }
    $requestLinesLeft = @((Get-Prop $Left 'requests' @()) | ForEach-Object {
            ([string](Get-Prop $_ 'accountId') + '|' + [string](Get-Prop $_ 'scopeId') + '|' + [string](Get-Prop $_ 'requestId') + '|' +
                [string](Get-Prop $_ 'receiptId') + '|' + [string](Get-Prop $_ 'confirmed') + '|' + [string](Get-Prop $_ 'status') + '|' +
                [string](Get-Prop $_ 'payloadSha256') + '|' + [string](Get-Prop $_ 'evaluatedStateVersion') + '|' +
                [string](Get-Prop $_ 'commandVersion') + '|' + [string](Get-Prop $_ 'eventSeq'))
        }) | Sort-Object -CaseSensitive
    $requestLinesRight = @((Get-Prop $Right 'requests' @()) | ForEach-Object {
            ([string](Get-Prop $_ 'accountId') + '|' + [string](Get-Prop $_ 'scopeId') + '|' + [string](Get-Prop $_ 'requestId') + '|' +
                [string](Get-Prop $_ 'receiptId') + '|' + [string](Get-Prop $_ 'confirmed') + '|' + [string](Get-Prop $_ 'status') + '|' +
                [string](Get-Prop $_ 'payloadSha256') + '|' + [string](Get-Prop $_ 'evaluatedStateVersion') + '|' +
                [string](Get-Prop $_ 'commandVersion') + '|' + [string](Get-Prop $_ 'eventSeq'))
        }) | Sort-Object -CaseSensitive
    if (-not [string]::Equals(($requestLinesLeft -join "`n"), ($requestLinesRight -join "`n"), [StringComparison]::Ordinal)) {
        $mismatches.Add('requests: before and after differ') | Out-Null
    }
    $controlLinesLeft = @((Get-Prop $Left 'controlRequests' @()) | ForEach-Object {
            ([string](Get-Prop $_ 'accountId') + '|' + [string](Get-Prop $_ 'scopeId') + '|' + [string](Get-Prop $_ 'requestId') + '|' +
                [string](Get-Prop $_ 'digest') + '|' + [string](Get-Prop $_ 'kind') + '|' + [string](Get-Prop $_ 'evaluatedStateVersion'))
        }) | Sort-Object -CaseSensitive
    $controlLinesRight = @((Get-Prop $Right 'controlRequests' @()) | ForEach-Object {
            ([string](Get-Prop $_ 'accountId') + '|' + [string](Get-Prop $_ 'scopeId') + '|' + [string](Get-Prop $_ 'requestId') + '|' +
                [string](Get-Prop $_ 'digest') + '|' + [string](Get-Prop $_ 'kind') + '|' + [string](Get-Prop $_ 'evaluatedStateVersion'))
        }) | Sort-Object -CaseSensitive
    if (-not [string]::Equals(($controlLinesLeft -join "`n"), ($controlLinesRight -join "`n"), [StringComparison]::Ordinal)) {
        $mismatches.Add('controlRequests: before and after differ') | Out-Null
    }
    [pscustomobject]@{ equal = ($mismatches.Count -eq 0); mismatches = @($mismatches.ToArray()) }
}

function Test-SummaryMatchesFacts {
    param([object]$Summary, [object]$Facts)
    if ($null -eq $Summary -or $null -eq $Facts) { return $false }
    $actual = Get-FactsSummary -Facts $Facts
    foreach ($property in @($Summary.PSObject.Properties)) {
        $expectedValue = [string]$property.Value
        $actualValue = [string](Get-Prop $actual $property.Name)
        if ($property.Name -eq 'controlEpochs') {
            $expectedValue = (@($property.Value) | ForEach-Object { [string]$_ }) -join ','
            $actualValue = (@((Get-Prop $actual 'controlEpochs' @())) | ForEach-Object { [string]$_ }) -join ','
        }
        if (-not [string]::Equals($expectedValue, $actualValue, [StringComparison]::OrdinalIgnoreCase)) { return $false }
    }
    return $true
}

function Read-RecordChain {
    param([string]$Path)
    $records = New-Object System.Collections.Generic.List[object]
    if (-not [IO.File]::Exists($Path)) { Throw-Ver 17 'CHAIN_MISSING' ('records.jsonl is missing: ' + $Path) }
    $lines = [IO.File]::ReadAllLines($Path, $script:Utf8)
    $previous = ''
    $index = 0
    foreach ($line in $lines) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $record = $null
        try { $record = $line | ConvertFrom-Json } catch { Throw-Ver 18 'CHAIN_INVALID' ('record ' + $index + ' is not JSON') }
        if ([int](Get-Prop $record 'sequence' 0) -ne $index) { Throw-Ver 18 'CHAIN_INVALID' ('record ' + $index + ' has a non-linear sequence') }
        if (-not [string]::Equals([string](Get-Prop $record 'previousRecordSha256' ''), $previous, [StringComparison]::OrdinalIgnoreCase)) {
            Throw-Ver 18 'CHAIN_INVALID' ('record ' + $index + ' previous hash does not match the prior record')
        }
        $payloadText = [string](Get-Prop $record 'payloadText' '')
        $recordSha = Get-Sha256Text -Text $payloadText
        if (-not [string]::Equals($recordSha, [string](Get-Prop $record 'recordSha256' ''), [StringComparison]::OrdinalIgnoreCase)) {
            Throw-Ver 18 'CHAIN_INVALID' ('record ' + $index + ' payload hash is invalid')
        }
        $payload = $null
        try { $payload = $payloadText | ConvertFrom-Json } catch { Throw-Ver 18 'CHAIN_INVALID' ('record ' + $index + ' payload is not JSON') }
        $records.Add([pscustomobject]@{ index = $index; kind = [string](Get-Prop $record 'kind'); payload = $payload; recordSha256 = [string](Get-Prop $record 'recordSha256'); recordedAt = [string](Get-Prop $record 'recordedAt') }) | Out-Null
        $previous = [string](Get-Prop $record 'recordSha256' '')
        $index++
    }
    if ($records.Count -eq 0) { Throw-Ver 17 'CHAIN_EMPTY' 'records.jsonl has no records.' }
    [pscustomobject]@{ records = @($records.ToArray()); head = $previous; count = $records.Count }
}

function Get-ArtifactRecord {
    param([object]$Attempt, [string]$Name)
    return @($Attempt.artifacts | Where-Object { [string](Get-Prop $_ 'name') -eq $Name }) | Select-Object -First 1
}

$caseFull = Get-FullPath -Path $CaseDir
$manifestPath = Join-Path $caseFull 'manifest.json'
$sidecarPath = Join-Path $caseFull 'manifest.sha256.txt'
$script:VerifierFull = Get-FullPath -Path $VerifierPath
$script:DotnetFull = Get-FullPath -Path $DotnetPath
$out = if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    Join-Path (Split-Path -Parent $caseFull) ('verify-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss', [Globalization.CultureInfo]::InvariantCulture))
}
else { Get-FullPath -Path $OutputDir }
if ($out.StartsWith($caseFull + '\', [StringComparison]::OrdinalIgnoreCase) -or $out -eq $caseFull) {
    Throw-Ver 2 'USAGE' 'OutputDir must be outside the sealed case directory.'
}
[IO.Directory]::CreateDirectory($out) | Out-Null

$result = [ordered]@{
    schemaVersion = 1
    kind = 'p45-controlled-collection-verification'
    toolVersion = $script:ToolVersion
    generatedAt = [DateTime]::UtcNow.ToString('o', [Globalization.CultureInfo]::InvariantCulture)
    caseDirectory = $caseFull
    expectedManifestSha256 = $ExpectedManifestSha256.ToUpperInvariant()
    caseManifestSha256 = $null
    inputManifestSha256 = $null
    factsEqual = $false
    collectionChain = 'FAIL'
    callerAcceptance = 'PENDING_INDEPENDENT_REVIEW'
    callerAcceptanceEligible = $false
    gamePositive = $false
    expectedOutcome = $null
    observedOutcome = 'NOT_RUN'
    checks = @()
    failures = @()
    verifierRuns = @()
    recoveryResult = 'BLOCKED'
}

try {
    if (-not (Test-Sha256TextValue -Text $ExpectedManifestSha256)) {
        Throw-Ver 2 'USAGE' 'ExpectedManifestSha256 must be 64 hexadecimal characters.'
    }
    if (-not [IO.File]::Exists($manifestPath)) { Throw-Ver 17 'MANIFEST_MISSING' ('Case manifest is missing: ' + $manifestPath) }
    $manifestHash = Get-Sha256File -Path $manifestPath
    $result.caseManifestSha256 = $manifestHash
    Add-Check -Name 'case manifest matches the reviewer-supplied digest' -Pass ([string]::Equals($manifestHash, $ExpectedManifestSha256, [StringComparison]::OrdinalIgnoreCase)) -Detail $manifestHash
    if (-not [string]::Equals($manifestHash, $ExpectedManifestSha256, [StringComparison]::OrdinalIgnoreCase)) {
        $result.collectionChain = 'FAIL'
        $result.failures = @($script:Failures.ToArray())
        $result.checks = @($script:Checks.ToArray())
        $result.recoveryResult = 'MANIFEST_DIGEST_MISMATCH'
        $resultJson = $result | ConvertTo-Json -Depth 40
        Write-JsonAtomic -Path (Join-Path $out 'verification-result.json') -Object $result
        Write-Host ($resultJson)
        exit 4
    }
    if ([IO.File]::Exists($sidecarPath)) {
        $sidecar = ([IO.File]::ReadAllText($sidecarPath, $script:Utf8)).Trim()
        $sidecarHash = ($sidecar -split '\s+')[0]
        Add-Check -Name 'manifest sidecar matches the manifest bytes' -Pass ([string]::Equals($sidecarHash, $manifestHash, [StringComparison]::OrdinalIgnoreCase)) -Detail $sidecar
    }
    else {
        Add-Check -Name 'manifest sidecar exists' -Pass $false -Detail $sidecarPath
    }

    $manifest = Read-JsonFile -Path $manifestPath
    if ([string](Get-Prop $manifest 'kind') -ne 'p45-controlled-collection-case-manifest' -or
        [int](Get-Prop $manifest 'schemaVersion' 0) -ne 1) {
        Throw-Ver 15 'MANIFEST_INVALID' 'Not a controlled collection case manifest.'
    }
    $caseId = [string](Get-Prop $manifest 'caseId')
    $runId = [string](Get-Prop $manifest 'runId')
    $evidenceClass = [string](Get-Prop $manifest 'evidenceClass')
    $inputManifestSha = [string](Get-Prop $manifest 'inputManifestSha256')
    $result.inputManifestSha256 = $inputManifestSha
    if (-not [string]::IsNullOrWhiteSpace($ExpectedInputManifestSha256)) {
        Add-Check -Name 'input manifest matches the reviewer-supplied digest' -Pass ([string]::Equals($inputManifestSha, $ExpectedInputManifestSha256, [StringComparison]::OrdinalIgnoreCase)) -Detail $inputManifestSha
        if (-not [string]::Equals($inputManifestSha, $ExpectedInputManifestSha256, [StringComparison]::OrdinalIgnoreCase)) { Throw-Ver 4 'INPUT_DIGEST_MISMATCH' 'The sealed input manifest does not match the reviewer-supplied digest.' }
    }

    # Strict file inventory: no missing, extra or changed bytes.
    $actualFiles = New-Object System.Collections.Generic.List[object]
    foreach ($file in @(Get-ChildItem -LiteralPath $caseFull -Recurse -File -Force -ErrorAction Stop)) {
        $relative = $file.FullName.Substring($caseFull.Length).TrimStart('\', '/').Replace('\', '/')
        if ($relative -in @('manifest.json', 'manifest.sha256.txt')) { continue }
        $sig = Get-FileSig -Path $file.FullName
        $actualFiles.Add([pscustomobject]@{ path = $relative; bytes = [long]$sig.bytes; sha256 = [string]$sig.sha256 }) | Out-Null
    }
    $manifestEntries = @((Get-Prop $manifest 'files' @()) | ForEach-Object {
            [pscustomobject]@{ path = [string](Get-Prop $_ 'path'); bytes = [long](Get-Prop $_ 'bytes' 0); sha256 = [string](Get-Prop $_ 'sha256') }
        })
    $actualMap = @{}
    foreach ($entry in $actualFiles) { $actualMap[[string]$entry.path] = $entry }
    $manifestMap = @{}
    foreach ($entry in $manifestEntries) { $manifestMap[[string]$entry.path] = $entry }
    $inventoryOk = ($actualMap.Count -eq $manifestMap.Count)
    $inventoryDetail = 'actual=' + $actualMap.Count + ' manifest=' + $manifestMap.Count
    foreach ($path in $manifestMap.Keys) {
        if (-not $actualMap.ContainsKey($path)) { $inventoryOk = $false; $inventoryDetail += '; missing=' + $path; continue }
        $expectedEntry = $manifestMap[$path]
        $actualEntry = $actualMap[$path]
        if ([long]$expectedEntry.bytes -ne [long]$actualEntry.bytes -or
            -not [string]::Equals([string]$expectedEntry.sha256, [string]$actualEntry.sha256, [StringComparison]::OrdinalIgnoreCase)) {
            $inventoryOk = $false
            $inventoryDetail += '; changed=' + $path
        }
    }
    foreach ($path in $actualMap.Keys) {
        if (-not $manifestMap.ContainsKey($path)) { $inventoryOk = $false; $inventoryDetail += '; extra=' + $path }
    }
    Add-Check -Name 'complete case inventory is unchanged' -Pass $inventoryOk -Detail $inventoryDetail

    $chainPath = Join-Path $caseFull 'records.jsonl'
    $chainFile = Read-RecordChain -Path $chainPath
    $chainMeta = Read-JsonFile -Path (Join-Path $caseFull 'chain.json')
    Add-Check -Name 'record chain count/head/file digest match chain.json' -Pass (
        [int](Get-Prop $chainMeta 'recordCount' -1) -eq $chainFile.count -and
        [string]::Equals([string](Get-Prop $chainMeta 'headRecordSha256' ''), [string]$chainFile.head, [StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals([string](Get-Prop $chainMeta 'recordsFileSha256' ''), (Get-Sha256File -Path $chainPath), [StringComparison]::OrdinalIgnoreCase)
    ) -Detail ('count=' + $chainFile.count + ' head=' + $chainFile.head)

    $byKind = @{}
    foreach ($record in $chainFile.records) {
        if (-not $byKind.ContainsKey([string]$record.kind)) { $byKind[[string]$record.kind] = New-Object System.Collections.Generic.List[object] }
        $byKind[[string]$record.kind].Add($record) | Out-Null
    }
    $caseOpen = if ($byKind.ContainsKey('case-open')) { $byKind['case-open'][0].payload } else { $null }
    $caseResult = if ($byKind.ContainsKey('case-result')) { $byKind['case-result'][0].payload } else { $null }
    $caseFailure = if ($byKind.ContainsKey('case-failure')) { $byKind['case-failure'][0].payload } else { $null }
    $saveFault = if ($byKind.ContainsKey('save-fault-applied')) { $byKind['save-fault-applied'][0].payload } else { $null }
    Add-Check -Name 'case-open record is present' -Pass ($null -ne $caseOpen)
    Add-Check -Name 'case terminal record is present' -Pass ($null -ne $caseResult -or $null -ne $caseFailure)
    if ($null -eq $caseOpen) { Throw-Ver 17 'CHAIN_INCOMPLETE' 'No case-open record exists.' }

    $inputManifestPath = Join-Path $caseFull 'input-manifest.json'
    $inputManifest = Read-JsonFile -Path $inputManifestPath
    Add-Check -Name 'sealed input manifest bytes match the case record' -Pass ([string]::Equals((Get-Sha256File -Path $inputManifestPath), $inputManifestSha, [StringComparison]::OrdinalIgnoreCase)) -Detail $inputManifestSha
    $result.expectedOutcome = [string](Get-Prop $caseOpen 'expectedOutcome')
    Add-Check -Name 'case identity matches input manifest' -Pass (
        [string]::Equals([string](Get-Prop $inputManifest 'caseId'), $caseId, [StringComparison]::Ordinal) -and
        [string]::Equals([string](Get-Prop $inputManifest 'runId'), $runId, [StringComparison]::Ordinal) -and
        [string]::Equals([string](Get-Prop $caseOpen 'caseId'), $caseId, [StringComparison]::Ordinal) -and
        [string]::Equals([string](Get-Prop $caseOpen 'runId'), $runId, [StringComparison]::Ordinal)
    ) -Detail $caseId

    $source = Get-Prop $inputManifest 'source'
    $build = Get-Prop $inputManifest 'build'
    $player = Get-Prop $inputManifest 'player'
    $scripts = Get-Prop $inputManifest 'scripts'
    $verifierInput = Get-Prop $inputManifest 'verifier'
    $plugin = Get-Prop $inputManifest 'pluginClosure'
    $sourceHeadValid = ($null -ne $source) -and ([string](Get-Prop $source 'head') -match '^[0-9a-f]{40}$')
    $dirtyProperty = if ($null -ne $source) { $source.PSObject.Properties['dirty'] } else { $null }
    $dirtyValueValid = ($null -ne $dirtyProperty) -and ($dirtyProperty.Value -is [bool])
    $dirtyPathsProperty = if ($null -ne $source) { $source.PSObject.Properties['dirtyPaths'] } else { $null }
    $dirtyPathsValue = $null
    if ($null -ne $dirtyPathsProperty) { $dirtyPathsValue = $dirtyPathsProperty.Value }
    $dirtyPathsValid = ($null -ne $dirtyPathsProperty) -and ($dirtyPathsValue -is [System.Array])
    $dirtyPathsItemsValid = $dirtyPathsValid
    if ($dirtyPathsValid) {
        foreach ($dirtyItem in @($dirtyPathsValue)) {
            if ($dirtyItem -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$dirtyItem)) { $dirtyPathsItemsValid = $false }
        }
    }
    $dirtyFlagConsistent = $dirtyValueValid -and $dirtyPathsValid -and
        ([bool]$dirtyProperty.Value -eq ([int]@($dirtyPathsValue).Count -gt 0))
    Add-Check -Name 'source HEAD and dirty state are frozen' -Pass (
        $sourceHeadValid -and $dirtyFlagConsistent -and $dirtyPathsItemsValid
    ) -Detail ('head=' + [string]$sourceHeadValid + ' dirty=' + [string]$dirtyValueValid +
        ' dirtyPathsArray=' + [string]$dirtyPathsValid + ' items=' + [string]$dirtyPathsItemsValid +
        ' consistent=' + [string]$dirtyFlagConsistent)
    Add-Check -Name 'build command/exit/log are frozen' -Pass (
        $null -ne $build -and -not [string]::IsNullOrWhiteSpace([string](Get-Prop $build 'command')) -and
        [int](Get-Prop $build 'exitCode' -1) -eq 0 -and
        [string]::Equals([string](Get-Prop $build 'logSha256'), (Get-Sha256File -Path (Join-Path (Split-Path -Parent $caseFull) 'build-player.log')), [StringComparison]::OrdinalIgnoreCase)
    )
    Add-Check -Name 'complete Player package is frozen' -Pass (
        $null -ne $player -and [string](Get-Prop $player 'packageFilesSha256') -match '^[0-9A-Fa-f]{64}$' -and
        [int](Get-Prop $player 'packageFileCount' 0) -gt 0
    )
    Add-Check -Name 'shared plugin closure is frozen' -Pass (
        $null -ne $plugin -and [string](Get-Prop $plugin 'filesSha256') -match '^[0-9A-Fa-f]{64}$' -and
        [int](Get-Prop $plugin 'fileCount' 0) -gt 0
    )

    $sealedCollector = Join-Path $caseFull 'tools\run-p45-controlled-recovery-collection.ps1'
    $sealedVerifierScript = Join-Path $caseFull 'tools\verify-p45-controlled-recovery-collection.ps1'
    $toolsOk = $true
    if (-not [IO.File]::Exists($sealedCollector) -or -not [IO.File]::Exists($sealedVerifierScript)) { $toolsOk = $false }
    else {
        $toolsOk = [string]::Equals((Get-Sha256File -Path $sealedCollector), [string](Get-Prop $scripts 'collectorSha256'), [StringComparison]::OrdinalIgnoreCase) -and
            [string]::Equals((Get-Sha256File -Path $sealedVerifierScript), [string](Get-Prop $scripts 'verifierScriptSha256'), [StringComparison]::OrdinalIgnoreCase)
    }
    Add-Check -Name 'sealed collector/verifier scripts match the input manifest' -Pass $toolsOk
    $runningVerifierSha = Get-Sha256File -Path $PSCommandPath
    Add-Check -Name 'the running verifier is the sealed fixed verifier' -Pass (
        [string]::Equals($runningVerifierSha, (Get-Sha256File -Path $sealedVerifierScript), [StringComparison]::OrdinalIgnoreCase)
    ) -Detail ([string]$runningVerifierSha)
    Add-Check -Name 'the fixed verifier binary matches the input manifest' -Pass (
        [string]::Equals((Get-Sha256File -Path $script:VerifierFull), [string](Get-Prop $verifierInput 'sha256'), [StringComparison]::OrdinalIgnoreCase)
    ) -Detail (Get-Sha256File -Path $script:VerifierFull)

    $launches = @(if ($byKind.ContainsKey('attempt-launch')) { $byKind['attempt-launch'] } else { @() })
    $exits = @(if ($byKind.ContainsKey('attempt-exit')) { $byKind['attempt-exit'] } else { @() })
    $factsRecords = @(if ($byKind.ContainsKey('attempt-facts')) { $byKind['attempt-facts'] } else { @() })
    $launchByAttempt = @{}
    foreach ($record in $launches) { $launchByAttempt[[int](Get-Prop $record.payload 'attempt' 0)] = $record.payload }
    $exitByAttempt = @{}
    foreach ($record in $exits) { $exitByAttempt[[int](Get-Prop $record.payload 'attempt' 0)] = $record.payload }
    $factsRecordByAttempt = @{}
    foreach ($record in $factsRecords) { $factsRecordByAttempt[[int](Get-Prop $record.payload 'attempt' 0)] = $record.payload }
    Add-Check -Name 'two launch/exit/facts attempts exist' -Pass (
        $launchByAttempt.ContainsKey(1) -and $launchByAttempt.ContainsKey(2) -and
        $exitByAttempt.ContainsKey(1) -and $exitByAttempt.ContainsKey(2) -and
        $factsRecordByAttempt.ContainsKey(1) -and $factsRecordByAttempt.ContainsKey(2)
    )
    if (-not $launchByAttempt.ContainsKey(1) -or -not $launchByAttempt.ContainsKey(2)) {
        Throw-Ver 17 'CHAIN_INCOMPLETE' 'Both BEFORE and AFTER launch records are required.'
    }

    $beforeLaunch = $launchByAttempt[1]
    $afterLaunch = $launchByAttempt[2]
    $beforeExit = $exitByAttempt[1]
    $afterExit = $exitByAttempt[2]
    Add-Check -Name 'BEFORE/AFTER phases are exact and ordered' -Pass (
        [string](Get-Prop $beforeLaunch 'phase') -eq 'BEFORE_RECOVERY' -and
        [string](Get-Prop $afterLaunch 'phase') -eq 'AFTER_RECOVERY'
    )
    Add-Check -Name 'two distinct process identities and challenges are bound' -Pass (
        [int](Get-Prop $beforeLaunch 'pid' 0) -ne [int](Get-Prop $afterLaunch 'pid' 0) -and
        [string](Get-Prop $beforeLaunch 'startTimeUtc') -ne [string](Get-Prop $afterLaunch 'startTimeUtc') -and
        [string](Get-Prop $beforeLaunch 'challenge') -ne [string](Get-Prop $afterLaunch 'challenge') -and
        [string](Get-Prop $beforeLaunch 'exportDirectory') -ne [string](Get-Prop $afterLaunch 'exportDirectory') -and
        [string](Get-Prop $beforeLaunch 'dataDirectory') -eq [string](Get-Prop $afterLaunch 'dataDirectory')
    )
    $expectedPlayerPath = [string](Get-Prop $player 'path')
    $expectedPlayerSha = [string](Get-Prop $player 'sha256')
    $packageSha = [string](Get-Prop $player 'packageFilesSha256')
    $sourceHead = [string](Get-Prop $source 'head')
    $buildCommit = [string](Get-Prop $build 'commit')
    $launchIdentityOk = $true
    foreach ($launch in @($beforeLaunch, $afterLaunch)) {
        if ([string]$launch.status -ne 'RUNNING') { $launchIdentityOk = $false }
        if (-not [string]::Equals([string](Get-Prop $launch 'processPath'), $expectedPlayerPath, [StringComparison]::OrdinalIgnoreCase)) { $launchIdentityOk = $false }
        if (-not [string]::Equals([string](Get-Prop $launch 'processSha256'), $expectedPlayerSha, [StringComparison]::OrdinalIgnoreCase)) { $launchIdentityOk = $false }
        if (-not [string]::Equals([string](Get-Prop $launch 'packageFilesSha256'), $packageSha, [StringComparison]::OrdinalIgnoreCase)) { $launchIdentityOk = $false }
        if (-not [string]::Equals([string](Get-Prop $launch 'sourceHead'), $sourceHead, [StringComparison]::OrdinalIgnoreCase)) { $launchIdentityOk = $false }
        if (-not [string]::Equals([string](Get-Prop $launch 'buildCommit'), $buildCommit, [StringComparison]::OrdinalIgnoreCase)) { $launchIdentityOk = $false }
        if ([string]$launch.actualCommandLine -notmatch [Regex]::Escape([string]$launch.challenge)) { $launchIdentityOk = $false }
        if ($evidenceClass -eq 'GAME' -and -not [bool](Get-Prop $launch 'argvMatched' $false)) { $launchIdentityOk = $false }
    }
    Add-Check -Name 'PID/start/path/hash/actual argv/package/build bind both attempts' -Pass $launchIdentityOk

    Add-Check -Name 'BEFORE real exit or owned force-kill is recorded' -Pass (
        ([string](Get-Prop $beforeExit 'disposition') -eq 'EXITED_OK' -and [int](Get-Prop $beforeExit 'nativeExitCode' -1) -eq 0) -or
        ([string](Get-Prop $beforeExit 'disposition') -eq 'FORCE_KILLED')
    )
    Add-Check -Name 'BEFORE/AFTER handles were closed' -Pass (
        [bool](Get-Prop $beforeExit 'handleClosed' $false) -and [bool](Get-Prop $afterExit 'handleClosed' $false)
    )

    $attemptDirs = @{
        1 = Join-Path $caseFull 'attempts\0001-before'
        2 = Join-Path $caseFull 'attempts\0002-after'
    }
    $beforeSealed = Join-Path $attemptDirs[1] 'sealed'
    $afterSealed = Join-Path $attemptDirs[2] 'sealed'
    $beforeFactsRecord = $factsRecordByAttempt[1]
    $afterFactsRecord = $factsRecordByAttempt[2]
    $artifactOk = $true
    $artifactDetail = ''
    foreach ($attemptNumber in @(1, 2)) {
        $attempt = if ($attemptNumber -eq 1) { $beforeLaunch } else { $afterLaunch }
        $attemptDir = $attemptDirs[$attemptNumber]
        $sealedDir = Join-Path $attemptDir 'sealed'
        $hasError = [IO.File]::Exists((Join-Path $sealedDir 'export.error.json'))
        $isExpectedRejection = ($attemptNumber -eq 2 -and [string]$result.expectedOutcome -eq 'REJECTED')
        if ($isExpectedRejection) {
            if (-not $hasError) { $artifactOk = $false; $artifactDetail += '; missingRejection=' + $attemptNumber }
        }
        else {
            foreach ($name in @('authority.save', 'cheat-state.bin', 'export.json', 'export.done')) {
                if (-not [IO.File]::Exists((Join-Path $sealedDir $name))) { $artifactOk = $false; $artifactDetail += '; missing=' + $attemptNumber + '/' + $name }
            }
        }
        foreach ($entry in @((Get-Prop $attempt 'artifacts' @()))) {
            $path = Join-Path $sealedDir ([string](Get-Prop $entry 'name'))
            if (-not [IO.File]::Exists($path)) { $artifactOk = $false; $artifactDetail += '; artifactMissing=' + $attemptNumber + '/' + [string](Get-Prop $entry 'name'); continue }
            if ([long](Get-Prop $entry 'bytes' 0) -ne (Get-Item -LiteralPath $path).Length -or
                -not [string]::Equals([string](Get-Prop $entry 'sha256'), (Get-Sha256File -Path $path), [StringComparison]::OrdinalIgnoreCase)) {
                $artifactOk = $false; $artifactDetail += '; artifactChanged=' + $attemptNumber + '/' + [string](Get-Prop $entry 'name')
            }
            if (-not [bool](Get-Prop $entry 'stable' $false)) { $artifactOk = $false; $artifactDetail += '; unstable=' + $attemptNumber + '/' + [string](Get-Prop $entry 'name') }
        }
    }
    Add-Check -Name 'sealed raw artifacts are complete and unchanged' -Pass $artifactOk -Detail $artifactDetail

    $verifierRuns = New-Object System.Collections.Generic.List[object]
    $decoded = @{}
    foreach ($attemptNumber in @(1, 2)) {
        $sealedDir = if ($attemptNumber -eq 1) { $beforeSealed } else { $afterSealed }
        $authority = Join-Path $sealedDir 'authority.save'
        $cheat = Join-Path $sealedDir 'cheat-state.bin'
        if (-not [IO.File]::Exists($authority) -or -not [IO.File]::Exists($cheat)) {
            $decoded[$attemptNumber] = $null
            $verifierRuns.Add([pscustomobject]@{ attempt = $attemptNumber; ran = $false; reason = 'AUTHORITY_OR_CHEAT_MISSING'; exitCode = $null; ok = $false }) | Out-Null
            continue
        }
        $run = Invoke-FixedVerifier -AuthorityPath $authority -CheatStatePath $cheat -OutputDirectory (Join-Path $out ('verifier-' + $attemptNumber)) -Prefix 'recovery'
        $facts = if ($run.ok) { Get-Prop $run.parsed 'facts' } else { $null }
        $decoded[$attemptNumber] = $facts
        $verifierRuns.Add([pscustomobject]@{
                attempt = $attemptNumber
                ran = $true
                exitCode = [int]$run.exitCode
                ok = [bool]$run.ok
                stdoutSha256 = [string]$run.stdoutSha256
                stderrSha256 = [string]$run.stderrSha256
                factsSummary = Get-FactsSummary -Facts $facts
            }) | Out-Null
        $record = if ($attemptNumber -eq 1) { $beforeFactsRecord } else { $afterFactsRecord }
        if ($null -ne $record) {
            Add-Check -Name ('attempt ' + $attemptNumber + ' re-decoded facts match the sealed facts summary') -Pass (Test-SummaryMatchesFacts -Summary (Get-Prop $record 'facts') -Facts $facts)
            Add-Check -Name ('attempt ' + $attemptNumber + ' verifier exit/stdout/stderr were sealed') -Pass (
                [int](Get-Prop $record 'verifierExitCode' -1) -eq [int]$run.exitCode -and
                [string]::Equals([string](Get-Prop $record 'verifierStdoutSha256'), [string]$run.stdoutSha256, [StringComparison]::OrdinalIgnoreCase) -and
                [string]::Equals([string](Get-Prop $record 'verifierStderrSha256'), [string]$run.stderrSha256, [StringComparison]::OrdinalIgnoreCase)
            )
        }
    }
    $result.verifierRuns = @($verifierRuns.ToArray())

    $exportIdentityOk = $true
    $exportDetail = ''
    foreach ($attemptNumber in @(1, 2)) {
        $sealedDir = if ($attemptNumber -eq 1) { $beforeSealed } else { $afterSealed }
        $exportPath = Join-Path $sealedDir 'export.json'
        if (-not [IO.File]::Exists($exportPath)) {
            $isExpectedRejection = ($attemptNumber -eq 2 -and [string]$result.expectedOutcome -eq 'REJECTED')
            if ($isExpectedRejection -and
                [IO.File]::Exists((Join-Path $sealedDir 'export.error.json')) -and
                ([string](Get-Prop $afterExit 'exportWaitStatus') -eq 'ERROR' -or
                    [string](Get-Prop $afterExit 'exportWaitStatus') -eq 'EXITED_NO_EXPORT') -and
                [int](Get-Prop $afterExit 'nativeExitCode' 0) -ne 0) {
                continue
            }
            $exportIdentityOk = $false; $exportDetail += '; noExport=' + $attemptNumber; continue
        }
        $export = Read-JsonFile -Path $exportPath
        $launch = if ($attemptNumber -eq 1) { $beforeLaunch } else { $afterLaunch }
        $authoritySig = Get-FileSig -Path (Join-Path $sealedDir 'authority.save')
        $cheatSig = Get-FileSig -Path (Join-Path $sealedDir 'cheat-state.bin')
        if ($export.kind -ne 'p45-player-structured-export' -or $export.exporterVersion -ne 'p45-player-structured-export/1') { $exportIdentityOk = $false; $exportDetail += '; version=' + $attemptNumber }
        if (-not [string]::Equals([string]$export.phase, [string]$launch.phase, [StringComparison]::Ordinal)) { $exportIdentityOk = $false; $exportDetail += '; phase=' + $attemptNumber }
        if (-not [string]::Equals([string]$export.runId, $runId, [StringComparison]::Ordinal)) { $exportIdentityOk = $false; $exportDetail += '; run=' + $attemptNumber }
        if (-not [string]::Equals([string]$export.attempt, ([int]$attemptNumber).ToString('0000', [Globalization.CultureInfo]::InvariantCulture), [StringComparison]::Ordinal)) { $exportIdentityOk = $false; $exportDetail += '; attempt=' + $attemptNumber }
        if (-not [string]::Equals([string]$export.challenge, [string]$launch.challenge, [StringComparison]::Ordinal)) { $exportIdentityOk = $false; $exportDetail += '; challenge=' + $attemptNumber }
        if ([int]$export.pid -ne [int]$launch.pid) { $exportIdentityOk = $false; $exportDetail += '; pid=' + $attemptNumber }
        if (-not [string]::Equals([string]$export.processStartUtc, [string]$launch.startTimeUtc, [StringComparison]::Ordinal)) { $exportIdentityOk = $false; $exportDetail += '; start=' + $attemptNumber }
        if (-not [string]::Equals([string]$export.processPath, [string]$launch.processPath, [StringComparison]::OrdinalIgnoreCase)) { $exportIdentityOk = $false; $exportDetail += '; path=' + $attemptNumber }
        if (-not [string]::Equals([string]$export.playerSha256, $expectedPlayerSha, [StringComparison]::OrdinalIgnoreCase)) { $exportIdentityOk = $false; $exportDetail += '; player=' + $attemptNumber }
        if (-not [string]::Equals([string]$export.packageManifestSha256, $packageSha, [StringComparison]::OrdinalIgnoreCase)) { $exportIdentityOk = $false; $exportDetail += '; package=' + $attemptNumber }
        if (-not [string]::Equals([string]$export.sourceHead, $sourceHead, [StringComparison]::OrdinalIgnoreCase)) { $exportIdentityOk = $false; $exportDetail += '; source=' + $attemptNumber }
        if (-not [string]::Equals([string]$export.buildCommit, $buildCommit, [StringComparison]::OrdinalIgnoreCase)) { $exportIdentityOk = $false; $exportDetail += '; build=' + $attemptNumber }
        if ($null -eq $authoritySig -or [int]$export.authorityBytes -ne $authoritySig.bytes -or
            -not [string]::Equals([string]$export.authoritySha256, [string]$authoritySig.sha256, [StringComparison]::OrdinalIgnoreCase)) { $exportIdentityOk = $false; $exportDetail += '; authority=' + $attemptNumber }
        if ($null -eq $cheatSig -or [int]$export.cheatStateBytes -ne $cheatSig.bytes -or
            -not [string]::Equals([string]$export.cheatStateSha256, [string]$cheatSig.sha256, [StringComparison]::OrdinalIgnoreCase)) { $exportIdentityOk = $false; $exportDetail += '; cheat=' + $attemptNumber }
        $donePath = Join-Path $sealedDir 'export.done'
        if (-not [IO.File]::Exists($donePath)) { $exportIdentityOk = $false; $exportDetail += '; done=' + $attemptNumber }
        else {
            $doneText = ([IO.File]::ReadAllText($donePath, $script:Utf8)).Trim()
            if (-not [string]::Equals($doneText, ((Get-Sha256File -Path $exportPath) + '  export.json'), [StringComparison]::OrdinalIgnoreCase)) { $exportIdentityOk = $false; $exportDetail += '; doneHash=' + $attemptNumber }
        }
        $decodedFacts = $decoded[$attemptNumber]
        if ($null -ne $decodedFacts) {
            $exportFactsComparison = Compare-Facts -Left (Get-Prop $export 'facts') -Right $decodedFacts
            if (-not [bool]$exportFactsComparison.equal) { $exportIdentityOk = $false; $exportDetail += '; exportFacts=' + $attemptNumber }
        }
        else { $exportIdentityOk = $false; $exportDetail += '; noDecodedFacts=' + $attemptNumber }
    }
    Add-Check -Name 'export/launch/PID/start/path/challenge/package/build/raw digests cross-bind both attempts' -Pass $exportIdentityOk -Detail $exportDetail

    $faultOk = $true
    $faultDetail = ''
    if ($null -eq $saveFault) { $faultOk = $false; $faultDetail = 'no save-fault record' }
    else {
        $faultName = [string](Get-Prop $saveFault 'fault')
        $beforeFault = Get-Prop $saveFault 'before'
        $afterFault = Get-Prop $saveFault 'after'
        $beforePrimary = Get-Prop $beforeFault 'primary'
        $beforeBackup = Get-Prop $beforeFault 'backup'
        $afterPrimary = Get-Prop $afterFault 'primary'
        $afterBackup = Get-Prop $afterFault 'backup'
        if (-not [bool](Get-Prop $beforePrimary 'valid' $false) -or -not [bool](Get-Prop $beforeBackup 'valid' $false)) { $faultOk = $false; $faultDetail += '; beforeNotValid' }
        switch ($faultName) {
            'None' {
                if (-not [bool](Get-Prop $afterPrimary 'valid' $false) -or -not [bool](Get-Prop $afterBackup 'valid' $false)) { $faultOk = $false; $faultDetail += '; noneChanged' }
            }
            'TruncatePrimary' {
                if ([bool](Get-Prop $afterPrimary 'valid' $false) -or -not [bool](Get-Prop $afterBackup 'valid' $false)) { $faultOk = $false; $faultDetail += '; truncate' }
            }
            'BadChecksumPrimary' {
                if ([bool](Get-Prop $afterPrimary 'valid' $false) -or -not [bool](Get-Prop $afterBackup 'valid' $false)) { $faultOk = $false; $faultDetail += '; checksum' }
            }
            'IncompatiblePrimary' {
                if ([bool](Get-Prop $afterPrimary 'valid' $false) -or -not [bool](Get-Prop $afterBackup 'valid' $false)) { $faultOk = $false; $faultDetail += '; incompatible' }
            }
            'MissingPrimaryWithBackup' {
                if ([bool](Get-Prop $afterPrimary 'exists' $true) -or -not [bool](Get-Prop $afterBackup 'valid' $false)) { $faultOk = $false; $faultDetail += '; missingPrimary' }
            }
            'BothMissing' {
                if ([bool](Get-Prop $afterPrimary 'exists' $true) -or [bool](Get-Prop $afterBackup 'exists' $true)) { $faultOk = $false; $faultDetail += '; bothMissing' }
            }
            'BothBad' {
                if ([bool](Get-Prop $afterPrimary 'valid' $false) -or [bool](Get-Prop $afterBackup 'valid' $false)) { $faultOk = $false; $faultDetail += '; bothBad' }
            }
            default { $faultOk = $false; $faultDetail += '; unknownFault=' + $faultName }
        }
    }
    Add-Check -Name 'declared save fault bytes and applicable primary/backup semantics are recorded' -Pass $faultOk -Detail $faultDetail

    $beforeDecoded = $decoded[1]
    $afterDecoded = $decoded[2]
    $factsComparison = Compare-Facts -Left $beforeDecoded -Right $afterDecoded
    $factsEqual = [bool]$factsComparison.equal
    $result.factsEqual = $factsEqual

    $expectedOutcome = [string](Get-Prop $caseOpen 'expectedOutcome')
    $afterWait = [string](Get-Prop $afterExit 'exportWaitStatus')
    $afterNative = Get-Prop $afterExit 'nativeExitCode'
    $observedOutcome = 'UNKNOWN'
    if ($null -eq $beforeDecoded) { $observedOutcome = 'BEFORE_NOT_RECOVERED' }
    elseif ($afterWait -eq 'DONE') {
        if ($factsEqual) { $observedOutcome = 'RECOVERED' }
        else {
            $afterStateVersion = [string](Get-Prop (Get-FactsSummary -Facts $afterDecoded) 'stateVersion')
            if ($afterStateVersion -eq '0' -or $afterStateVersion -eq '1') { $observedOutcome = 'FRESH_START' }
            else { $observedOutcome = 'RECOVERED_DIFFERENT' }
        }
    }
    elseif ($afterWait -eq 'ERROR' -or $afterWait -eq 'EXITED_NO_EXPORT') { $observedOutcome = 'REJECTED' }
    $result.observedOutcome = $observedOutcome

    $semanticsOk = $false
    if ($expectedOutcome -eq 'RECOVERED' -or $expectedOutcome -eq 'RECOVERED_FROM_BACKUP') {
        $semanticsOk = ($observedOutcome -eq 'RECOVERED' -and $factsEqual -and [int]$afterNative -eq 0)
    }
    elseif ($expectedOutcome -eq 'FRESH_START') {
        $semanticsOk = ($observedOutcome -eq 'FRESH_START' -and -not $factsEqual)
    }
    elseif ($expectedOutcome -eq 'REJECTED') {
        $semanticsOk = ($observedOutcome -eq 'REJECTED' -and -not $factsEqual -and [int]$afterNative -ne 0)
    }
    Add-Check -Name 'expected recovery semantics match the observed outcome' -Pass $semanticsOk -Detail ('expected=' + $expectedOutcome + ' observed=' + $observedOutcome + ' native=' + [string]$afterNative)

    $result.checks = @($script:Checks.ToArray())
    $result.failures = @($script:Failures.ToArray())
    $result.collectionChain = if ($script:Failures.Count -eq 0) { 'PASS' } else { 'FAIL' }
    $result.recoveryResult = if (-not $semanticsOk) {
        if (($expectedOutcome -eq 'RECOVERED' -or $expectedOutcome -eq 'RECOVERED_FROM_BACKUP') -and -not $factsEqual) { 'FACTS_DIFFER' }
        elseif ($expectedOutcome -eq 'FRESH_START') { 'FRESH_START_NOT_OLD_STATE' }
        elseif ($expectedOutcome -eq 'REJECTED') { 'FAIL_CLOSED_REJECTED' }
        else { 'EXPECTED_SEMANTICS_NOT_OBSERVED' }
    }
    elseif ($expectedOutcome -eq 'FRESH_START') { 'FRESH_START_NOT_OLD_STATE' }
    elseif ($expectedOutcome -eq 'REJECTED') { 'FAIL_CLOSED_REJECTED' }
    elseif ($factsEqual) { 'FACTS_EQUAL' }
    else { 'FACTS_DIFFER' }

    if ($evidenceClass -eq 'FIXTURE') {
        $result.gamePositive = $false
        $result.recoveryResult = 'FIXTURE_ONLY_NOT_GAME'
    }
    elseif ($evidenceClass -eq 'GAME') {
        $result.gamePositive = ($script:Failures.Count -eq 0)
    }

    if ($script:Failures.Count -gt 0) {
        if (-not $semanticsOk) { $script:ExitCode = 16 } else { $script:ExitCode = 19 }
        $result.collectionChain = 'FAIL'
    }
    else {
        $script:ExitCode = 0
        $result.collectionChain = 'PASS'
    }
    $result.callerAcceptance = 'PENDING_INDEPENDENT_REVIEW'
    $result.callerAcceptanceEligible = $false
}
catch {
    $message = Get-ErrText $_
    $result.collectionChain = 'FAIL'
    $result.failures = @($script:Failures.ToArray() + $message)
    $result.checks = @($script:Checks.ToArray())
    $result.recoveryResult = 'BLOCKED'
    $resultJson = $result | ConvertTo-Json -Depth 40
    Write-JsonAtomic -Path (Join-Path $out 'verification-result.json') -Object $result
    Write-Host ('P45_CONTROLLED_VERIFY_FAILURE: ' + $message)
    exit 17
}

$resultJson = $result | ConvertTo-Json -Depth 40
Write-JsonAtomic -Path (Join-Path $out 'verification-result.json') -Object $result
Write-Host ($resultJson)
exit $script:ExitCode
