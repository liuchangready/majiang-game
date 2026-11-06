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
$PlayerPath=$Player;$BuildRecordPath=$Record;$PackageManifestPath=$Package;$HelperPath=$Helper;$SourceSnapshotPath=$Snapshot
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
    Assert-True ((([string]$r.error).Contains('EXTERNAL_PIN'))) 'missing pin reason missing'
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

# Caller closeout regression coverage: all mutations below operate on owned copies.
# These are TOOL_FIXTURE trust anchors, never replacements for the recorded GAME pins.
$nativeCases = @(
    @{ Name='recover-native-missing'; File='launch.json'; Field='nativeExitCode'; Remove=$true; Reason='strict JSON int32 and boolean' },
    @{ Name='recover-native-null'; File='launch.json'; Field='nativeExitCode'; Value=$null; Reason='strict JSON int32 and boolean' },
    @{ Name='recover-native-string'; File='launch.json'; Field='nativeExitCode'; Value='0'; Reason='strict JSON int32 and boolean' },
    @{ Name='recover-native-true'; File='launch.json'; Field='nativeExitCode'; Value=$true; Reason='strict JSON int32 and boolean' },
    @{ Name='recover-timeout-missing'; File='launch.json'; Field='timedOut'; Remove=$true; Reason='strict JSON int32 and boolean' },
    @{ Name='recover-timeout-true'; File='launch.json'; Field='timedOut'; Value=$true; Reason='did not exit successfully' },
    @{ Name='recover-native-nonzero'; File='launch.json'; Field='nativeExitCode'; Value=17; Reason='did not exit successfully' },
    @{ Name='prepare-native-missing'; File='prepare-launch.json'; Field='nativeExitCode'; Remove=$true; Reason='nativeExitCode' },
    @{ Name='prepare-native-null'; File='prepare-launch.json'; Field='nativeExitCode'; Value=$null; Reason='JSON integer in int32 range' },
    @{ Name='prepare-native-false'; File='prepare-launch.json'; Field='nativeExitCode'; Value=$false; Reason='JSON integer in int32 range' },
    @{ Name='prepare-native-string'; File='prepare-launch.json'; Field='nativeExitCode'; Value='0'; Reason='JSON integer in int32 range' },
    @{ Name='prepare-timeout-missing'; File='prepare-launch.json'; Field='timedOut'; Remove=$true; Reason='timedOut' },
    @{ Name='prepare-timeout-true'; File='prepare-launch.json'; Field='timedOut'; Value=$true; Reason='normal prepare did not exit 0' },
    @{ Name='prepare-native-nonzero'; File='prepare-launch.json'; Field='nativeExitCode'; Value=17; Reason='normal prepare did not exit 0' },
    @{ Name='force-native-zero'; File='prepare-launch.json'; Field='nativeExitCode'; Value=0; Force=$true; Reason='Force prepare did not record' },
    @{ Name='force-timeout-true'; File='prepare-launch.json'; Field='timedOut'; Value=$true; Force=$true; Reason='Force prepare did not record' }
)
foreach ($nativeCase in $nativeCases) {
    $fixtureSource = if ($nativeCase.Force) { Join-Path $FullCaseRoot 'cases/C04-G6-02' } else { $RecoveredCaseRoot }
    Invoke-Case -Name $nativeCase.Name -Source $fixtureSource -ExpectPass $false -Reason $nativeCase.Reason -Mutate {
        param($d)
        $path = Join-Path $d $nativeCase.File
        $launch = [IO.File]::ReadAllText($path) | ConvertFrom-Json
        if ($nativeCase.ContainsKey('Remove') -and $nativeCase['Remove']) {
            $launch.PSObject.Properties.Remove($nativeCase['Field'])
        }
        else { $launch.($nativeCase['Field']) = $nativeCase['Value'] }
        Write-Json $path $launch
    }
}

function Invoke-PinnedRootFixture {
    param([string]$Name, [scriptblock]$Mutate, [string]$Reason, [switch]$RemoveCsv, [switch]$CorruptCsv)
    $clone = Join-Path $OutputDir $Name
    Copy-Item -LiteralPath $FullCaseRoot -Destination $clone -Recurse
    $summaryPath = Join-Path $clone 'summary.json'
    $value = [IO.File]::ReadAllText($summaryPath) | ConvertFrom-Json
    if ($null -ne $Mutate) { & $Mutate $value; Write-Json $summaryPath $value }
    $csvPath = Join-Path $clone 'evidence-sha256.csv'
    $csv = foreach ($file in Get-ChildItem -LiteralPath $clone -Recurse -File | Sort-Object FullName) {
        $relative = $file.FullName.Substring($clone.Length).TrimStart('\','/').Replace('\','/')
        if ($relative -eq 'evidence-sha256.csv' -or $relative.StartsWith('temp/') -or $relative.StartsWith('staging-project/')) { continue }
        '"{0}","{1}","{2}"' -f (Get-Sha $file.FullName), $file.Length, $relative.Replace('"','""')
    }
    [IO.File]::WriteAllLines($csvPath, [string[]]$csv, (New-Object Text.UTF8Encoding($false)))
    if ($CorruptCsv) {
        $csv[0] = [regex]::Replace($csv[0], '^"[0-9A-Fa-f]{64}"', ('"' + ('0' * 64) + '"'))
        [IO.File]::WriteAllLines($csvPath, [string[]]$csv, (New-Object Text.UTF8Encoding($false)))
    }
    $summaryPin = Get-Sha $summaryPath
    $csvPin = Get-Sha $csvPath
    if ($RemoveCsv) { Remove-Item -LiteralPath $csvPath }
    $run = Invoke-RunnerVerify -Root $clone -Name $Name -SummaryPin $summaryPin -EvidencePin $csvPin
    Assert-True ($run.ExitCode -ne 0) ($Name + ' unexpectedly PASSed')
    Assert-True ($run.Output.Contains($Reason)) ($Name + ' wrong rejection: ' + $run.Output)
}
Assert-Test 'missing-evidence-manifest' {
    Invoke-PinnedRootFixture -Name 'missing-evidence-manifest' -Reason 'EVIDENCE_CSV_MISSING' -RemoveCsv
}
Assert-Test 'manifest-entry-hash-mismatch' {
    Invoke-PinnedRootFixture -Name 'manifest-entry-hash-mismatch' -Reason 'VERIFY_EVIDENCE_MISMATCH' -CorruptCsv
}
Assert-Test 'extra-summary-row' {
    Invoke-PinnedRootFixture -Name 'extra-summary-row' -Reason 'VERIFY_INCOMPLETE_CASE_SET' -Mutate {
        param($s) $s.cases = @($s.cases) + @($s.cases[0]); $s.rowCount = 9
    }
}
Assert-Test 'duplicate-summary-identity' {
    Invoke-PinnedRootFixture -Name 'duplicate-summary-identity' -Reason 'VERIFY_INCOMPLETE_CASE_SET' -Mutate {
        param($s) $s.cases[7] = $s.cases[0]
    }
}
Assert-Test 'unknown-summary-identity' {
    Invoke-PinnedRootFixture -Name 'unknown-summary-identity' -Reason 'VERIFY_FIXED_CASE_MISSING' -Mutate {
        param($s) $s.cases[7].caseId = 'C04-G6-09'
    }
}
Assert-Test 'bind-input-mismatch' {
    $snap = [IO.File]::ReadAllText($SourceSnapshotPath) | ConvertFrom-Json
    $first = @($snap.inputs.PSObject.Properties)[0].Name
    $snap.inputs.$first = '0' * 64
    $path = Join-Path $OutputDir 'input-mismatch.json'
    Write-Json $path $snap
    $r = Invoke-BindProbe -Name 'input-mismatch' -Record $BuildRecordPath -Snapshot $path -Package $PackageManifestPath -SnapshotPin (Get-Sha $path)
    Assert-True (-not [bool]$r.accepted) 'source input mismatch PASSed'
    Assert-True (([string]$r.error).Contains('SOURCE_INPUT_MISMATCH')) ('input guard not reached: ' + $r.error)
}
Assert-Test 'bind-stale-source-records' {
    $record = [IO.File]::ReadAllText($BuildRecordPath) | ConvertFrom-Json
    $snap = [IO.File]::ReadAllText($SourceSnapshotPath) | ConvertFrom-Json
    $record.sourceHead = '993d172a2ee953a74134a95bb219a0560e22a18b'
    $record.buildCommit = $record.sourceHead
    $snap.sourceHead = $record.sourceHead
    $recordPath = Join-Path $OutputDir 'stale-build-marker.json'
    $snapshotPath = Join-Path $OutputDir 'stale-source-snapshot.json'
    Write-Json $recordPath $record
    Write-Json $snapshotPath $snap
    $r = Invoke-BindProbe -Name 'stale-source' -Record $recordPath -Snapshot $snapshotPath -Package $PackageManifestPath
    Assert-True (-not [bool]$r.accepted) 'stale source records PASSed'
    Assert-True (([string]$r.error).Contains('EXTERNAL_PIN_MISMATCH')) 'stale record pin was not checked'
}
Assert-Test 'bind-modified-record' {
    $record = [IO.File]::ReadAllText($BuildRecordPath) | ConvertFrom-Json
    $record | Add-Member -NotePropertyName callerFixture -NotePropertyValue 'modified build record'
    $path = Join-Path $OutputDir 'modified-record.json'
    Write-Json $path $record
    $r = Invoke-BindProbe -Name 'modified-record' -Record $path -Snapshot $SourceSnapshotPath -Package $PackageManifestPath
    Assert-True (-not [bool]$r.accepted) 'modified build record PASSed'
    Assert-True (([string]$r.error).Contains('EXTERNAL_PIN_MISMATCH')) 'record pin was not checked'
}
Assert-Test 'bind-package-entry-mismatch' {
    $package = [IO.File]::ReadAllText($PackageManifestPath) | ConvertFrom-Json
    $package.files[0].sha256 = '0' * 64
    $path = Join-Path $OutputDir 'package-entry-mismatch.json'
    Write-Json $path $package
    $r = Invoke-BindProbe -Name 'package-entry-mismatch' -Record $BuildRecordPath -Snapshot $SourceSnapshotPath -Package $path -PackagePin (Get-Sha $path)
    Assert-True (-not [bool]$r.accepted) 'wrong package entry PASSed'
    Assert-True (([string]$r.error).Contains('PACKAGE_MANIFEST_MISMATCH')) ('package bytes were not checked: ' + $r.error)
}
Invoke-Case -Name 'checksum-bytes-labelled-incompatible' -Source (Join-Path $FullCaseRoot 'cases/C04-G6-08') -ExpectPass $false -Reason 'IncompatiblePrimary did not change only version bytes' -Mutate {
    param($d)
    $bytes = [IO.File]::ReadAllBytes((Join-Path $d 'boundary/primary-before.save'))
    $bytes[44] = $bytes[44] -bxor 0x5A
    [IO.File]::WriteAllBytes((Join-Path $d 'boundary/primary-after.save'), $bytes)
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
