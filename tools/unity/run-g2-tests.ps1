#requires -Version 5.1
<#
.SYNOPSIS
  Lists the fixed G2 cases or runs one existing Windows/Android evidence suite.
.DESCRIPTION
  Plan does not start Unity or ADB. Run preserves the underlying runner's guards,
  mutex, timeouts and verdicts. RUNNER_COMPLETED is not a case PASS or G2 approval.
#>
[CmdletBinding()]
param(
    [ValidateSet('Plan','Run')][string]$Mode = 'Plan',
    [ValidateSet('WindowsFullMatch','WindowsActions','WindowsCheats','WindowsRecovery',
        'WindowsPrivacy','WindowsAppMatrix','AndroidProbe','AndroidBuild','AndroidBoot',
        'AndroidInstall','AndroidStart','AndroidCapture','AndroidInput','AndroidLifecycle','AndroidShutdown')]
    [string]$Suite,
    [string]$RunnerParametersPath,
    [string]$OutputRoot,
    [string]$ExpectedSourceHead,
    [string]$UnityPath = 'D:/game_dev_tools/unity/Editor/Unity.exe',
    [string]$DotnetPath = 'D:/game_dev_tools/dotnet/dotnet.exe'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))

function Write-G2Json($Path, $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 30), (New-Object Text.UTF8Encoding($false)))
}

# Managed arguments cannot be overridden by optional JSON parameters.
$definitions = [ordered]@{
    WindowsFullMatch = @{ script='run-p45-full-match.ps1'; args=@{Mode='All';UnityPath=$UnityPath;DotnetPath=$DotnetPath}; output='RunDir'; options=@('TimeoutSeconds') }
    WindowsActions = @{ script='run-p45-c04-player-actions.ps1'; args=@{UnityPath=$UnityPath;DotnetPath=$DotnetPath}; output='RunRoot'; options=@('TimeoutSeconds','NugetSeedRoot','NugetSeedAuditPath') }
    WindowsCheats = @{ script='run-p45-c04-player-cheats.ps1'; args=@{Case='all';UnityExe=$UnityPath;DotnetPath=$DotnetPath}; output='EvidenceRoot'; options=@('VerifierPath') }
    WindowsRecovery = @{ script='run-p45-c04-recovery-reconciliation.ps1'; args=@{Mode='All';UnityPath=$UnityPath;DotnetPath=$DotnetPath}; output='EvidenceRoot'; options=@('TimeoutSeconds') }
    WindowsPrivacy = @{ script='run-p45-c04-privacy-matrix.ps1'; args=@{UnityPath=$UnityPath;DotnetPath=$DotnetPath}; output='RunRoot'; options=@('TimeoutSeconds','ReuseLibraryCacheFrom') }
    WindowsAppMatrix = @{ script='run-p45-c04-privacy-matrix.ps1'; args=@{UnityPath=$UnityPath;DotnetPath=$DotnetPath;WindowsAppMatrix=$true}; output='RunRoot'; options=@('TimeoutSeconds','ReuseLibraryCacheFrom') }
    AndroidBuild = @{ script='build-c05-android-emulator.ps1'; args=@{UnityPath=$UnityPath}; output='OutputRoot'; options=@('StagingRoot','PrivateToolchainRoot','RegistryCacheSource','UnityTimeoutSeconds') }
}
$deviceOptions = @('Serial','AvdName','AndroidSdkRoot','AvdHome','StateFile','Apk','AdbTimeoutSeconds')
foreach ($pair in @(
    @('AndroidProbe','Probe'), @('AndroidBoot','StartEmulator'), @('AndroidInstall','Install'),
    @('AndroidStart','Start'), @('AndroidCapture','Capture'), @('AndroidInput','Input'),
    @('AndroidLifecycle','BackgroundResume'), @('AndroidShutdown','ShutdownEmulator')
)) {
    $options = $deviceOptions
    if ($pair[0] -eq 'AndroidBoot') { $options += @('EmulatorPort','AccelMode','AvdWaitSeconds') }
    if ($pair[0] -eq 'AndroidInput') { $options += @('InputAction','X','Y','X2','Y2','DurationMs') }
    $definitions[$pair[0]] = @{ script='run-c05-android-emulator.ps1'; args=@{Action=$pair[1]}; output='EvidenceDir'; options=$options }
}

$censusPath = Join-Path $repo 'doc/v5-重构/V5-G2-C04-PLAYER-SCENARIO-CENSUS-20261101.json'
$census = Get-Content -Raw -Encoding UTF8 -LiteralPath $censusPath | ConvertFrom-Json
$cases = @($census.cases)
if ($cases.Count -ne 33 -or @($cases.id | Select-Object -Unique).Count -ne 33) { throw 'Expected the 33 unique fixed C04 IDs.' }
$expectedCounts = @(4,4,6,3,3,8,2,3)
for ($i=1; $i -le 8; $i++) {
    $expectedIds = @(1..$expectedCounts[$i-1] | ForEach-Object { 'C04-G{0}-{1:00}' -f $i,$_ })
    if (@(Compare-Object $expectedIds @($cases | Where-Object parent -eq "C04-G$i" | ForEach-Object id)).Count) {
        throw "Fixed case IDs differ in C04-G$i."
    }
}
$casePlan = @($cases | ForEach-Object {
    $suites = switch ($_.parent) {
        'C04-G1' { @('WindowsFullMatch') }
        'C04-G2' { @('WindowsActions') }
        'C04-G3' { @('WindowsCheats') }
        'C04-G4' { @('WindowsPrivacy','WindowsAppMatrix') }
        'C04-G5' { @('WindowsPrivacy','WindowsAppMatrix') }
        'C04-G6' { @('WindowsRecovery') }
        default { @() }
    }
    # Related suites produce evidence; this is not a claim of full row coverage.
    [pscustomobject]@{ id=$_.id; platform=$(if ($_.id -eq 'C04-G8-03') {'Android'} else {'Windows'});
        title=$_.title; trigger=$_.trigger; initialFixture=$_.initialFixture; assertion=$_.assert;
        bindings=$_.bindings; relatedSuites=@($suites); execution='NOT_RUN'; verdict='NOT_ASSESSED';
        coverage=$(if (@($suites).Count) {'Review runner evidence against the full row contract.'} else {'No complete automatic runner mapped; retain manual or missing coverage.'}) }
})
$androidRows = @(
    @('E1','Environment and image','AndroidProbe'), @('B1','Full game APK','AndroidBuild'),
    @('D1','Owned emulator boot','AndroidBoot'), @('R1','Install and usable application start','AndroidInstall','AndroidStart'),
    @('V1','Effective visuals','AndroidCapture'), @('I1','Native input and legal gameplay effect','AndroidInput'),
    @('L1','Lifecycle and display','AndroidLifecycle'), @('F1','Stable sealing and handoff','AndroidShutdown')
)
$casePlan += @($androidRows | ForEach-Object {
    [pscustomobject]@{ id="C05-$($_[0])"; platform='Android'; title=$_[1]; trigger='Follow the fixed C05 emulator task contract.';
        initialFixture='Exact APK/build evidence, owned device state and installation receipt where required.';
        assertion='doc/v5-重构/V5-G2-C05-ANDROID-EMULATOR-TASK-20261103.md: fixed delivery matrix';
        bindings='Source, APK SHA256, build report, device serial, owned process and state file.';
        relatedSuites=@($_ | Select-Object -Skip 2); execution='NOT_RUN'; verdict='NOT_ASSESSED';
        coverage='A completed device operation is partial evidence. Visuals, gameplay effects, display matrix and sealing require contract review.' }
})

$head = (& git -C $repo rev-parse HEAD | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $head -notmatch '^[a-f0-9]{40}$') { throw 'Cannot determine source HEAD.' }
if ($ExpectedSourceHead -and $ExpectedSourceHead -cne $head) { throw 'ExpectedSourceHead differs from the current HEAD.' }
$treeStatus = @(& git -C $repo status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0) { throw 'Cannot read working tree status.' }
if ($Mode -eq 'Run' -and -not $Suite) { throw 'Run requires one explicit -Suite.' }
if ($Mode -eq 'Plan' -and ($Suite -or $RunnerParametersPath)) { throw 'Suite and RunnerParametersPath are for Run only.' }

$suitePlan = @($definitions.Keys | ForEach-Object {
    $d = $definitions[$_]
    $scriptPath = Join-Path $PSScriptRoot $d.script
    # Inspect the interface without executing the script or loading its functions.
    $tokens=$null; $parseErrors=$null
    $ast = [Management.Automation.Language.Parser]::ParseFile($scriptPath,[ref]$tokens,[ref]$parseErrors)
    if ($parseErrors.Count) { throw "Cannot parse runner: $scriptPath" }
    $names = @($ast.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath })
    foreach ($name in @($d.args.Keys) + @($d.output) + @($d.options)) {
        if ($names -notcontains $name) { throw "Runner interface drift: $_ / $name" }
    }
    [pscustomobject]@{ suite=$_; runner=$scriptPath; runnerSha256=(Get-FileHash -LiteralPath $scriptPath -Algorithm SHA256).Hash;
        managedArguments=$d.args; outputParameter=$d.output; optionalParameters=$d.options }
})
$parameters = @{}
if ($Mode -eq 'Run') {
    $d = $definitions[$Suite]
    foreach ($key in $d.args.Keys) { $parameters[$key]=$d.args[$key] }
    switch ($Suite) {
        'WindowsActions' { $parameters.ExpectedCandidate=$head }
        'WindowsCheats' { $parameters.SourceHead=$head; $parameters.BuildCommit=$head }
        'WindowsRecovery' { $parameters.ExpectedSourceHead=$head }
        { $_ -in @('WindowsPrivacy','WindowsAppMatrix') } { $parameters.ExpectedSourceHead=$head }
    }
    if ($RunnerParametersPath) {
        $extra = Get-Content -LiteralPath $RunnerParametersPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($null -eq $extra -or $extra -isnot [pscustomobject]) { throw 'Runner parameters must be a JSON object.' }
        foreach ($property in $extra.PSObject.Properties) {
            if ($d.options -notcontains $property.Name) { throw "Unsupported or managed runner parameter: $($property.Name)" }
            if ($property.Value -isnot [string] -and $property.Value -isnot [int] -and $property.Value -isnot [long]) {
                throw "Runner parameter must be a string or integer: $($property.Name)"
            }
            $parameters[$property.Name]=$property.Value
        }
    }
}

if (-not $OutputRoot) { $OutputRoot=Join-Path $repo ('build/g' + [guid]::NewGuid().ToString('N').Substring(0,8)) }
$OutputRoot=[IO.Path]::GetFullPath($OutputRoot)
$buildPrefix=[IO.Path]::GetFullPath((Join-Path $repo 'build')) + [IO.Path]::DirectorySeparatorChar
if (-not $OutputRoot.StartsWith($buildPrefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'OutputRoot must be inside repository build/.' }
if (Test-Path -LiteralPath $OutputRoot) { throw 'OutputRoot exists; refusing to overwrite evidence.' }
[IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
$plan = [ordered]@{ schemaVersion=1; mode=$Mode; sourceHead=$head; sourceStatus=$treeStatus;
    entrySha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash;
    censusSha256=(Get-FileHash -LiteralPath $censusPath -Algorithm SHA256).Hash;
    createdUtc=[DateTimeOffset]::UtcNow.ToString('o'); stageVerdict='G2_NOT_ASSESSED';
    fixedCaseCount=41; cases=$casePlan; suites=$suitePlan }
Write-G2Json (Join-Path $OutputRoot 'plan.json') $plan
$casePlan | Select-Object id,platform,title,execution,verdict,coverage,@{n='relatedSuites';e={$_.relatedSuites -join ','}} |
    Export-Csv -LiteralPath (Join-Path $OutputRoot 'cases.csv') -Encoding UTF8 -NoTypeInformation
Write-Host "G2 $Mode : 33 C04 cases + 8 Android rows. Output: $OutputRoot"
if ($Mode -eq 'Plan') { exit 0 }

# Each existing runner owns its fresh GUID root and its lock; no outer Unity lock.
$runnerRoot=Join-Path $repo ('build/' + [guid]::NewGuid().ToString('N'))
$parameters[$d.output]=$runnerRoot
$runner=Join-Path $PSScriptRoot $d.script
$requestPath=Join-Path $OutputRoot 'runner-request.clixml'
@{script=$runner;parameters=$parameters} | Export-Clixml -LiteralPath $requestPath
Write-G2Json (Join-Path $OutputRoot 'command.json') @{runner=$runner;parameters=$parameters;sourceHead=$head}
$execution=[ordered]@{suite=$Suite;status='RUNNING';nativeExit=$null;runnerRoot=$runnerRoot;
    startedUtc=[DateTimeOffset]::UtcNow.ToString('o');endedUtc=$null;stageVerdict='G2_NOT_ASSESSED';error=$null}
Write-G2Json (Join-Path $OutputRoot 'execution.json') $execution
$exitCode=1
try {
    # CLIXML carries arguments as values, including spaces/quotes/$(), not shell code.
    $childCode = '$ErrorActionPreference="Stop"; try { $r=Import-Clixml -LiteralPath (Join-Path $PSScriptRoot "runner-request.clixml"); $p=$r.parameters; $global:LASTEXITCODE=0; & $r.script @p; exit $global:LASTEXITCODE } catch { [Console]::Out.WriteLine($_); exit 1 }'
    $childPath=Join-Path $OutputRoot 'invoke-runner.ps1'
    [IO.File]::WriteAllText($childPath,$childCode)
    # PS 5.1 represents native stderr as ErrorRecords. Preserve it and the exact
    # native exit rather than terminating the parent on the first stderr line.
    $nativePreference=$ErrorActionPreference
    try {
        $ErrorActionPreference='Continue'
        & (Join-Path $PSHOME 'powershell.exe') -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $childPath 2>&1 |
            Tee-Object -FilePath (Join-Path $OutputRoot 'runner.log') -ErrorAction Stop | Out-Host
        $exitCode=$LASTEXITCODE
    } finally { $ErrorActionPreference=$nativePreference }
    $execution.nativeExit=$exitCode
    $execution.status=$(if ($exitCode -eq 0) {'RUNNER_COMPLETED'} else {'RUNNER_FAILED'})
} catch {
    $execution.status='RUNNER_FAILED'; $execution.error=$_.Exception.Message
} finally {
    $execution.endedUtc=[DateTimeOffset]::UtcNow.ToString('o')
    Write-G2Json (Join-Path $OutputRoot 'execution.json') $execution
    # Hash only this wrapper's stopped writers. Child raw evidence keeps its
    # existing sealing rules; an emulator's active log must not be re-sealed here.
    $files=@(Get-ChildItem -LiteralPath $OutputRoot -File | ForEach-Object {
        @{path=$_.Name;bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
    })
    Write-G2Json (Join-Path $OutputRoot 'manifest.json') @{scope='wrapper artifacts only';files=$files}
}
Write-Host "$($execution.status): native=$($execution.nativeExit). Use the runner's raw verdicts; G2 remains NOT_ASSESSED."
if ($execution.status -ne 'RUNNER_COMPLETED') { exit 1 }
exit 0
