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
    [Parameter(Mandatory = $true)][string]$FullCaseRoot,
    [Parameter(Mandatory = $true)][string]$RunnerPath,
    [Parameter(Mandatory = $true)][string]$PlayerPath,
    [Parameter(Mandatory = $true)][string]$BuildRecordPath,
    [Parameter(Mandatory = $true)][string]$PackageManifestPath,
    [Parameter(Mandatory = $true)][string]$SourceSnapshotPath,
    [Parameter(Mandatory = $true)][string]$ExpectedSummarySha256,
    [Parameter(Mandatory = $true)][string]$ExpectedEvidenceSha256,
    [Parameter(Mandatory = $true)][string]$ExpectedSourceHead,
    [Parameter(Mandatory = $true)][string]$ExpectedBuildRecordSha256,
    [Parameter(Mandatory = $true)][string]$ExpectedPackageManifestSha256,
    [Parameter(Mandatory = $true)][string]$ExpectedSourceSnapshotSha256,
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
    param([string]$Name, [string]$Source, [bool]$ExpectPass, [scriptblock]$Mutate, [string]$Reason = '')
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
            if (-not [string]::IsNullOrWhiteSpace($Reason)) {
                Assert-True ($run.Output.Contains($Reason)) ('negative reason missing [' + $Reason + ']: ' + $run.Output)
            }
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

function Assert-Test {
    param([string]$Name, [scriptblock]$Body)
    $script:Total++
    try {
        & $Body
        $script:Passed++
        Write-Host ('PASS ' + $Name)
    }
    catch {
        $script:Failed++
        Write-Host ('FAIL ' + $Name + ': ' + $_.Exception.Message)
    }
}

function Invoke-RunnerVerify {
    param([string]$Root, [string]$Name, [string]$SummaryPin = $ExpectedSummarySha256,
        [string]$EvidencePin = $ExpectedEvidenceSha256, [switch]$NoSummaryPin)
    $out = Join-Path $OutputDir ($Name + '-out')
    $args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $RunnerPath,
        '-Mode', 'Verify', '-EvidenceRoot', $Root, '-OutputDir', $out,
        '-ExpectedEvidenceSha256', $EvidencePin, '-HelperPath', $HelperPath, '-DotnetPath', $DotnetPath)
    if (-not $NoSummaryPin) { $args += @('-ExpectedSummarySha256', $SummaryPin) }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = 'powershell.exe'
    $psi.Arguments = ($args | ForEach-Object { '"' + ([string]$_).Replace('"','\"') + '"' }) -join ' '
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $proc = [System.Diagnostics.Process]::Start($psi)
    $stdout = $proc.StandardOutput.ReadToEnd()
    $stderr = $proc.StandardError.ReadToEnd()
    $proc.WaitForExit()
    return [pscustomobject]@{ ExitCode = $proc.ExitCode; Output = ($stdout + ' ' + $stderr) }
}

function Invoke-BindProbe {
    param([string]$Name, [string]$Record, [string]$Snapshot, [string]$Package,
        [string]$SourceHead = $ExpectedSourceHead, [string]$RecordPin = $ExpectedBuildRecordSha256,
        [string]$PackagePin = $ExpectedPackageManifestSha256, [string]$SnapshotPin = $ExpectedSourceSnapshotSha256)
    $probe = Join-Path $OutputDir 'bind-probe.ps1'
    if (-not (Test-Path $probe)) {
        $text = @'
param($Runner,$Repo,$Out,$Player,$Record,$Snapshot,$Package,$SourceHead,$RecordPin,$PackagePin,$SnapshotPin,$Helper)
$ErrorActionPreference='Stop'
$ast=[Management.Automation.Language.Parser]::ParseFile($Runner,[ref]$null,[ref]$null)
$script:RepoRoot=$Repo;$script:Root=$Out
foreach($name in 'Get-Sha256','Get-StringSha256','Get-GitBlobSha256','Write-JsonFile','Write-ToolLog','Bind-ExistingPlayer'){
 $fn=$ast.Find({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]-and$n.Name-eq$name},$true)
 . ([scriptblock]::Create($fn.Extent.Text))
}
$PlayerPath=$Player;$BuildRecordPath=$Record;$PackageManifestPath=$Package;$HelperPath=$Helper
$ExpectedSourceHead=$SourceHead;$ExpectedBuildRecordSha256=$RecordPin;$ExpectedPackageManifestSha256=$PackagePin;$ExpectedSourceSnapshotSha256=$SnapshotPin
$result=[ordered]@{accepted=$true;error='';sourceHead=''}
try{Bind-ExistingPlayer;$result.sourceHead=$script:SourceHead}catch{$result.accepted=$false;$result.error=$_.Exception.Message}
[IO.File]::WriteAllText((Join-Path $Out 'bind-result.json'),($result|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
exit 0
'@
        [IO.File]::WriteAllText($probe, $text, (New-Object Text.UTF8Encoding($false)))
    }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $probe $RunnerPath $repo $OutputDir $PlayerPath $Record $Snapshot $Package $SourceHead $RecordPin $PackagePin $SnapshotPin $HelperPath 2>&1 | Out-Null
    return ([IO.File]::ReadAllText((Join-Path $OutputDir 'bind-result.json')) | ConvertFrom-Json)
}

# Full 8 with both external pins and the trusted helper.
Assert-Test 'full8-summary-evidence-pinned' {
    $run = Invoke-RunnerVerify -Root $FullCaseRoot -Name 'full8'
    Assert-True ($run.ExitCode -eq 0) ('full8 exit ' + $run.ExitCode + ': ' + $run.Output)
    Assert-True ($run.Output.Contains('C04_VERIFY_PASS')) 'full8 did not emit C04_VERIFY_PASS'
}

# Strict Recover native/timeout types.
Invoke-Case -Name 'recover-native-false' -Source $RecoveredCaseRoot -ExpectPass $false -Reason 'strict JSON int32 and boolean' -Mutate {
    param($d) $l=Get-Content -Raw (Join-Path $d 'launch.json')|ConvertFrom-Json;$l.nativeExitCode=$false;Write-Json (Join-Path $d 'launch.json') $l }
Invoke-Case -Name 'recover-native-fraction' -Source $RecoveredCaseRoot -ExpectPass $false -Reason 'strict JSON int32 and boolean' -Mutate {
    param($d) $l=Get-Content -Raw (Join-Path $d 'launch.json')|ConvertFrom-Json;$l.nativeExitCode=0.4;Write-Json (Join-Path $d 'launch.json') $l }
Invoke-Case -Name 'recover-native-overflow' -Source $RecoveredCaseRoot -ExpectPass $false -Reason 'strict JSON int32 and boolean' -Mutate {
    param($d) $l=Get-Content -Raw (Join-Path $d 'launch.json')|ConvertFrom-Json;$l.nativeExitCode=2147483648;Write-Json (Join-Path $d 'launch.json') $l }
Invoke-Case -Name 'recover-timeout-null' -Source $RecoveredCaseRoot -ExpectPass $false -Reason 'strict JSON int32 and boolean' -Mutate {
    param($d) $l=Get-Content -Raw (Join-Path $d 'launch.json')|ConvertFrom-Json;$l.timedOut=$null;Write-Json (Join-Path $d 'launch.json') $l }
Invoke-Case -Name 'recover-timeout-string' -Source $RecoveredCaseRoot -ExpectPass $false -Reason 'strict JSON int32 and boolean' -Mutate {
    param($d) $l=Get-Content -Raw (Join-Path $d 'launch.json')|ConvertFrom-Json;$l.timedOut='true';Write-Json (Join-Path $d 'launch.json') $l }
Invoke-Case -Name 'recover-timeout-number' -Source $RecoveredCaseRoot -ExpectPass $false -Reason 'strict JSON int32 and boolean' -Mutate {
    param($d) $l=Get-Content -Raw (Join-Path $d 'launch.json')|ConvertFrom-Json;$l.timedOut=1;Write-Json (Join-Path $d 'launch.json') $l }
Invoke-Case -Name 'prepare-native-fraction' -Source $RecoveredCaseRoot -ExpectPass $false -Reason 'JSON integer in int32 range' -Mutate {
    param($d) $l=Get-Content -Raw (Join-Path $d 'prepare-launch.json')|ConvertFrom-Json;$l.nativeExitCode=0.4;Write-Json (Join-Path $d 'prepare-launch.json') $l }
Invoke-Case -Name 'prepare-native-overflow' -Source $RecoveredCaseRoot -ExpectPass $false -Reason 'JSON integer in int32 range' -Mutate {
    param($d) $l=Get-Content -Raw (Join-Path $d 'prepare-launch.json')|ConvertFrom-Json;$l.nativeExitCode=2147483648;Write-Json (Join-Path $d 'prepare-launch.json') $l }
Invoke-Case -Name 'prepare-timeout-null' -Source $RecoveredCaseRoot -ExpectPass $false -Reason 'JSON boolean' -Mutate {
    param($d) $l=Get-Content -Raw (Join-Path $d 'prepare-launch.json')|ConvertFrom-Json;$l.timedOut=$null;Write-Json (Join-Path $d 'prepare-launch.json') $l }
Invoke-Case -Name 'prepare-timeout-string' -Source $RecoveredCaseRoot -ExpectPass $false -Reason 'JSON boolean' -Mutate {
    param($d) $l=Get-Content -Raw (Join-Path $d 'prepare-launch.json')|ConvertFrom-Json;$l.timedOut='true';Write-Json (Join-Path $d 'prepare-launch.json') $l }
Invoke-Case -Name 'prepare-timeout-number' -Source $RecoveredCaseRoot -ExpectPass $false -Reason 'JSON boolean' -Mutate {
    param($d) $l=Get-Content -Raw (Join-Path $d 'prepare-launch.json')|ConvertFrom-Json;$l.timedOut=1;Write-Json (Join-Path $d 'prepare-launch.json') $l }
Invoke-Case -Name 'force-prepare-valid' -Source (Join-Path $FullCaseRoot 'cases/C04-G6-02') -ExpectPass $true -Mutate $null

# Actual case identity and fault mechanism mismatches.
Invoke-Case -Name 'truncate-as-checksum' -Source (Join-Path $FullCaseRoot 'cases/C04-G6-03') -ExpectPass $false -Reason 'BadChecksumPrimary' -Mutate {
    param($d) foreach($f in @('result.json','input.json','launch.json')){$p=Join-Path $d $f;$o=Get-Content -Raw $p|ConvertFrom-Json;$o.caseId='C04-G6-04';$o.fault='BadChecksumPrimary';if($o.PSObject.Properties['actualOutcome']){$o.actualOutcome='RECOVERED_FROM_BACKUP'};Write-Json $p $o} }
Invoke-Case -Name 'checksum-as-truncate' -Source (Join-Path $FullCaseRoot 'cases/C04-G6-04') -ExpectPass $false -Reason 'TruncatePrimary' -Mutate {
    param($d) foreach($f in @('result.json','input.json','launch.json')){$p=Join-Path $d $f;$o=Get-Content -Raw $p|ConvertFrom-Json;$o.caseId='C04-G6-03';$o.fault='TruncatePrimary';if($o.PSObject.Properties['actualOutcome']){$o.actualOutcome='RECOVERED_FROM_BACKUP'};Write-Json $p $o} }

Assert-Test 'actual-case-replacement' {
    $clone = Join-Path $OutputDir 'actual-case-replacement'
    Copy-Item -LiteralPath $FullCaseRoot -Destination $clone -Recurse
    Remove-Item -LiteralPath (Join-Path $clone 'cases/C04-G6-01') -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $FullCaseRoot 'cases/C04-G6-03') -Destination (Join-Path $clone 'cases/C04-G6-01') -Recurse
    $run = Invoke-RunnerVerify -Root $clone -Name 'actual-case-replacement'
    Assert-True ($run.ExitCode -ne 0) 'replacement case unexpectedly PASSed'
    Assert-True ($run.Output.Contains('VERIFY_')) ('replacement did not hit a verify guard: ' + $run.Output)
}
Assert-Test 'missing-summary-pin' {
    $run = Invoke-RunnerVerify -Root $FullCaseRoot -Name 'missing-summary-pin' -NoSummaryPin
    Assert-True ($run.ExitCode -ne 0) 'missing summary pin unexpectedly PASSed'
    Assert-True ($run.Output.Contains('VERIFY_EXTERNAL_PIN_REQUIRED')) 'missing pin reason missing'
}
Assert-Test 'wrong-evidence-pin' {
    $run = Invoke-RunnerVerify -Root $FullCaseRoot -Name 'wrong-evidence-pin' -EvidencePin ('0' * 64)
    Assert-True ($run.ExitCode -ne 0) 'wrong CSV pin unexpectedly PASSed'
    Assert-True ($run.Output.Contains('VERIFY_EVIDENCE_PIN_MISMATCH')) 'wrong CSV pin reason missing'
}
Assert-Test 'missing-case' {
    $clone = Join-Path $OutputDir 'missing-case'
    Copy-Item -LiteralPath $FullCaseRoot -Destination $clone -Recurse
    Remove-Item -LiteralPath (Join-Path $clone 'cases/C04-G6-08') -Recurse -Force
    $run = Invoke-RunnerVerify -Root $clone -Name 'missing-case'
    Assert-True ($run.ExitCode -ne 0) 'missing case unexpectedly PASSed'
}

# Source/build/package pin matrix (real Bind-ExistingPlayer).
Assert-Test 'bind-valid-317' {
    $r = Invoke-BindProbe -Name 'valid' -Record $BuildRecordPath -Snapshot $SourceSnapshotPath -Package $PackageManifestPath
    Assert-True ([bool]$r.accepted) ('317 binding rejected: ' + $r.error)
    Assert-True ([string]$r.sourceHead -eq $ExpectedSourceHead) 'bound sourceHead mismatch'
}
Assert-Test 'bind-missing-pin' {
    $r = Invoke-BindProbe -Name 'missing-pin' -Record $BuildRecordPath -Snapshot $SourceSnapshotPath -Package $PackageManifestPath -SourceHead ''
    Assert-True (-not [bool]$r.accepted) 'binding without external source pin PASSed'
    Assert-True ((([string]$r.error).Contains('EXTERNAL_PIN_REQUIRED'))) 'missing pin reason missing'
}
Assert-Test 'bind-empty-package' {
    $empty = Get-Content -Raw $PackageManifestPath | ConvertFrom-Json
    $empty.files = @()
    $path = Join-Path $OutputDir 'empty-package.json'
    Write-Json $path $empty
    $r = Invoke-BindProbe -Name 'empty-package' -Record $BuildRecordPath -Snapshot $SourceSnapshotPath -Package $path -PackagePin (Get-Sha $path)
    Assert-True (-not [bool]$r.accepted) 'empty package PASSed'
    Assert-True ((([string]$r.error).Contains('PACKAGE_MANIFEST_COUNT_MISMATCH'))) 'empty package reason missing'
}
Assert-Test 'bind-tree-mismatch' {
    $snap = Get-Content -Raw $SourceSnapshotPath | ConvertFrom-Json
    $snap.treeSha256 = ('0' * 64)
    $path = Join-Path $OutputDir 'tree-mismatch.json'
    Write-Json $path $snap
    $r = Invoke-BindProbe -Name 'tree-mismatch' -Record $BuildRecordPath -Snapshot $path -Package $PackageManifestPath -SnapshotPin (Get-Sha $path)
    Assert-True (-not [bool]$r.accepted) 'tree mismatch PASSed'
    Assert-True ((([string]$r.error).Contains('SOURCE_TREE_MISMATCH'))) 'tree mismatch reason missing'
}

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

