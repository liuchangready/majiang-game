#requires -Version 5.1
# Tool fixtures only: no Unity, SDK, ADB or product tests.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$testRoot=Join-Path $repo ('build/g2-runner-test-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$script:checks=0
function Assert-True($Condition,$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
    $script:checks++
}
function Invoke-Tool($ScriptPath,$Parameters) {
    $childPath=Join-Path $testRoot ([guid]::NewGuid().ToString('N') + '.ps1')
    $request=$childPath + '.clixml'
    @{script=$ScriptPath;parameters=$Parameters} | Export-Clixml -LiteralPath $request
    $code='$ErrorActionPreference="Stop"; try { $r=Import-Clixml -LiteralPath ($PSCommandPath + ".clixml"); $p=$r.parameters; & $r.script @p; exit $LASTEXITCODE } catch { [Console]::Out.WriteLine($_); exit 1 }'
    [IO.File]::WriteAllText($childPath,$code)
    $log=Join-Path $testRoot ([guid]::NewGuid().ToString('N') + '.log')
    $nativePreference=$ErrorActionPreference
    try {
        $ErrorActionPreference='Continue'
        & (Join-Path $PSHOME 'powershell.exe') -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $childPath > $log 2>&1
        return $LASTEXITCODE
    } finally { $ErrorActionPreference=$nativePreference }
}
$runner=Join-Path $repo 'tools/unity/run-g2-tests.ps1'
$planRoot=Join-Path $testRoot 'plan'
Assert-True ((Invoke-Tool $runner @{OutputRoot=$planRoot}) -eq 0) 'real repository Plan succeeds'
$plan=Get-Content -Raw -Encoding UTF8 (Join-Path $planRoot 'plan.json') | ConvertFrom-Json
Assert-True ($plan.cases.Count -eq 41) 'all 41 rows are retained'
Assert-True (@($plan.cases.id | Select-Object -Unique).Count -eq 41) 'all IDs are unique'
Assert-True ($plan.suites.Count -eq 15) 'all execution interfaces parse without running tools'
Assert-True (@($plan.cases | Where-Object verdict -ne NOT_ASSESSED).Count -eq 0) 'Plan never accepts cases'
Assert-True ($plan.stageVerdict -eq 'G2_NOT_ASSESSED') 'Plan never approves G2'
Assert-True (@(Import-Csv (Join-Path $planRoot 'cases.csv')).Count -eq 41) 'CSV preserves denominator'
$hash=(Get-FileHash (Join-Path $planRoot 'plan.json')).Hash
Assert-True ((Invoke-Tool $runner @{OutputRoot=$planRoot}) -ne 0) 'existing evidence cannot be overwritten'
Assert-True ((Get-FileHash (Join-Path $planRoot 'plan.json')).Hash -eq $hash) 'old evidence unchanged'
Assert-True ((Invoke-Tool $runner @{ExpectedSourceHead=('0'*40);OutputRoot=(Join-Path $testRoot 'bad-head')}) -ne 0) 'wrong source HEAD rejected'
Assert-True (-not (Test-Path (Join-Path $testRoot 'bad-head'))) 'HEAD rejection precedes output creation'
Assert-True ((Invoke-Tool $runner @{Mode='Run';OutputRoot=(Join-Path $testRoot 'no-suite')}) -ne 0) 'Run requires an explicit suite'
$badOptions=Join-Path $testRoot 'bad-options.json'
[IO.File]::WriteAllText($badOptions,'{"FixtureOnly":true}')
Assert-True ((Invoke-Tool $runner @{Mode='Run';Suite='WindowsActions';RunnerParametersPath=$badOptions}) -ne 0) 'fixture mode cannot masquerade as a product run'
[IO.File]::WriteAllText($badOptions,'{"Case":"build"}')
Assert-True ((Invoke-Tool $runner @{Mode='Run';Suite='WindowsCheats';RunnerParametersPath=$badOptions}) -ne 0) 'cannot replace all cheat cases with build-only'
[IO.File]::WriteAllText($badOptions,'{"RunRoot":"elsewhere"}')
Assert-True ((Invoke-Tool $runner @{Mode='Run';Suite='WindowsActions';RunnerParametersPath=$badOptions}) -ne 0) 'cannot override managed output root'

# An isolated miniature Git repo exercises real child-process exit propagation.
# It never changes production runners or uses the user's devices.
$fixture=Join-Path $testRoot 'fixture'
$fixtureTools=Join-Path $fixture 'tools/unity'
[IO.Directory]::CreateDirectory($fixtureTools) | Out-Null
Copy-Item -LiteralPath $runner -Destination $fixtureTools
$census=Get-ChildItem -LiteralPath (Join-Path $repo 'doc') -Recurse -Filter 'V5-G2-C04-PLAYER-SCENARIO-CENSUS-20261101.json' | Select-Object -First 1
$relative=$census.FullName.Substring($repo.Length+1)
[IO.Directory]::CreateDirectory((Split-Path -Parent (Join-Path $fixture $relative))) | Out-Null
Copy-Item -LiteralPath $census.FullName -Destination (Join-Path $fixture $relative)
foreach ($group in ($plan.suites | Group-Object runner)) {
    $names=@($group.Group | ForEach-Object { @($_.managedArguments.PSObject.Properties.Name) + @($_.outputParameter) + @($_.optionalParameters) })
    $names+=@('ExpectedCandidate','SourceHead','BuildCommit','ExpectedSourceHead')
    $parameters=@($names | Select-Object -Unique | ForEach-Object {
        if ($_ -eq 'WindowsAppMatrix') { '[switch]$WindowsAppMatrix' } else { '[object]$' + $_ }
    }) -join ','
    $body='param(' + $parameters + ")`n" + @'
$ErrorActionPreference='Stop'
if ($Action -eq 'Probe') { [Console]::Error.WriteLine('expected fixture stderr'); exit 7 }
[IO.Directory]::CreateDirectory($EvidenceDir) | Out-Null
@{avd=$AvdName; action=$Action} | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $EvidenceDir 'fixture.json')
exit 0
'@
    [IO.File]::WriteAllText((Join-Path $fixtureTools (Split-Path -Leaf $group.Name)),$body)
}
& git -C $fixture init --quiet
if ($LASTEXITCODE -ne 0) { throw 'fixture git init failed' }
& git -C $fixture add .
if ($LASTEXITCODE -ne 0) { throw 'fixture git add failed' }
& git -C $fixture -c user.name=G2Fixture -c user.email=g2-fixture@example.invalid -c commit.gpgsign=false commit --quiet -m fixture
if ($LASTEXITCODE -ne 0) { throw 'fixture git commit failed' }
$fixtureRunner=Join-Path $fixtureTools 'run-g2-tests.ps1'
$failedRoot=Join-Path $fixture 'build/failed'
Assert-True ((Invoke-Tool $fixtureRunner @{Mode='Run';Suite='AndroidProbe';OutputRoot=$failedRoot}) -ne 0) 'child failure propagates'
$failed=Get-Content -Raw (Join-Path $failedRoot 'execution.json') | ConvertFrom-Json
Assert-True ($failed.nativeExit -eq 7 -and $failed.status -eq 'RUNNER_FAILED') 'exact child exit retained'
Assert-True ((Get-Content -Raw (Join-Path $failedRoot 'runner.log')) -match 'expected fixture stderr') 'native stderr is preserved'

$literal='space " quote '' apostrophe $([IO.File]::WriteAllText("SHOULD_NOT_EXIST","x"))'
$options=Join-Path $testRoot 'literal-options.json'
@{AvdName=$literal;Apk='fixture.apk';StateFile='fixture-state.json';X=12;Y=34} | ConvertTo-Json | Set-Content -Encoding UTF8 $options
$completedRoot=Join-Path $fixture 'build/completed'
Assert-True ((Invoke-Tool $fixtureRunner @{Mode='Run';Suite='AndroidInput';OutputRoot=$completedRoot;RunnerParametersPath=$options}) -eq 0) 'child suite can complete'
$completed=Get-Content -Raw (Join-Path $completedRoot 'execution.json') | ConvertFrom-Json
Assert-True ($completed.status -eq 'RUNNER_COMPLETED' -and $completed.nativeExit -eq 0) 'completed operation is recorded'
Assert-True ($completed.stageVerdict -eq 'G2_NOT_ASSESSED') 'successful child cannot approve G2'
$manifest=Get-Content -Raw (Join-Path $completedRoot 'manifest.json') | ConvertFrom-Json
Assert-True (@($manifest.files | Where-Object path -eq 'execution.json').Count -eq 1) 'final execution is sealed'
foreach ($file in $manifest.files) {
    Assert-True ((Get-FileHash -LiteralPath (Join-Path $completedRoot $file.path) -Algorithm SHA256).Hash -eq $file.sha256) "wrapper hash matches $($file.path)"
}
$record=Get-Content -Raw (Join-Path $completed.runnerRoot 'fixture.json') | ConvertFrom-Json
Assert-True ($record.avd -ceq $literal) 'spaces, quotes and shell metacharacters stay literal'
Assert-True (-not (Test-Path (Join-Path $repo 'SHOULD_NOT_EXIST'))) 'no shell interpolation occurred'
@{evidenceClass='TOOL_FIXTURE';checks=$script:checks;failed=0;root=$testRoot} | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $testRoot 'test-summary.json')
Write-Host "PASS: $script:checks tool assertions. Evidence: $testRoot"
