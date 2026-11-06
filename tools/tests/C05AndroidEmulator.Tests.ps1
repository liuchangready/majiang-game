#Requires -Version 5.1
[CmdletBinding()]
param([string]$OutputRoot)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $OutputRoot) { $OutputRoot = Join-Path $root ('build\c05-emulator-tests\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8)) }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $OutputRoot) { throw "Refusing to reuse test output root: $OutputRoot" }
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$script:assertions = 0
$script:failures = New-Object System.Collections.Generic.List[string]
$script:fixtures = New-Object System.Collections.Generic.List[object]

function Check([string]$Name, [bool]$Condition, [string]$Detail = '') {
  $script:assertions++
  if (-not $Condition) { $script:failures.Add("$Name $Detail".Trim()); Write-Host "FAIL $Name $Detail" -ForegroundColor Red }
  else { Write-Host "ok   $Name" }
}
function Run-Fixture([string]$Name, [string]$Script, [string]$Switch, [int]$ExpectedCount, [switch]$ScriptHasAction) {
  $log = Join-Path $OutputRoot ($Name + '.log')
  $arguments = @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $root $Script))
  if ($ScriptHasAction) { $arguments += @('-Action','Probe') }
  $arguments += $Switch
  $previous = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'
  try { $output = @(& powershell.exe @arguments 2>&1) } finally { $ErrorActionPreference = $previous }
  $code = $LASTEXITCODE
  [IO.File]::WriteAllText($log, ($output -join "`r`n"), (New-Object Text.UTF8Encoding($false)))
  $text = ($output -join ' ')
  $count = 0
  if ($text -match 'count=(\d+)') { $count = [int]$Matches[1] }
  Check "fixture:$Name exit0" ($code -eq 0) "nativeExit=$code log=$log"
  Check "fixture:$Name marker" ($text -match 'PASS') $text.Trim()
  Check "fixture:$Name count" ($count -eq $ExpectedCount) "actual=$count expected=$ExpectedCount"
  $script:fixtures.Add([pscustomobject]@{ name=$Name; script=$Script; switch=$Switch; nativeExit=$code; count=$count; log=(Split-Path $log -Leaf) })
}

# 1. Syntax: all three scripts must parse under Windows PowerShell 5.1.
foreach ($script in @('tools/unity/build-c05-android-emulator.ps1','tools/unity/run-c05-android-emulator.ps1','tools/tests/C05AndroidEmulator.Tests.ps1')) {
  $path = Join-Path $root $script
  $tokens = $null; $errors = $null
  [void][System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
  Check "parse:$script" ($errors.Count -eq 0) (($errors | ForEach-Object { $_.Message }) -join '; ')
}

# 2. Exact authored scope: the seven expected paths exist and no foreign tracked write is present.
$expected = @(
  'tools/unity/build-c05-android-emulator.ps1',
  'tools/unity/run-c05-android-emulator.ps1',
  'tools/tests/C05AndroidEmulator.Tests.ps1',
  'unity/Mahjong.Unity/Assets/Editor/C05EmulatorAndroidBuild.cs',
  'unity/Mahjong.Unity/Assets/Editor/C05EmulatorAndroidBuild.cs.meta'
)
$docPattern = '^doc/[^/]+/V5-G2-C05-ANDROID-GEMINI38-(DELIVERY|BINDING)-20261104\.(md|json)$'
$requiredNow = $expected
foreach ($p in $requiredNow) { Check "exists:$p" (Test-Path -LiteralPath (Join-Path $root $p) -PathType Leaf) }
$docDelivery = @(Get-ChildItem (Join-Path $root 'doc') -Recurse -File -Filter 'V5-G2-C05-ANDROID-GEMINI38-DELIVERY-20261104.md' -ErrorAction SilentlyContinue)
$docBinding = @(Get-ChildItem (Join-Path $root 'doc') -Recurse -File -Filter 'V5-G2-C05-ANDROID-GEMINI38-BINDING-20261104.json' -ErrorAction SilentlyContinue)
Check 'exists:gemini38-delivery-doc' ($docDelivery.Count -eq 1) ($docDelivery | ForEach-Object { $_.FullName })
Check 'exists:gemini38-binding-json' ($docBinding.Count -eq 1) ($docBinding | ForEach-Object { $_.FullName })
$histDiff = @(& git -C $root diff --name-only HEAD -- "doc/*20261103*" 2>&1)
Check 'scope:historical-20261103-untouched' ($histDiff.Count -eq 0) ($histDiff -join '; ')
$status = @(& git -C $root -c core.quotepath=false status --porcelain=v1 --untracked-files=all 2>&1)
Check 'git-status-native0' ($LASTEXITCODE -eq 0)
$dirty = @($status | Where-Object { $_ -match '^\s*(?:\?\?|[MADRCU ][MADRCU])\s+(.+)$' } | ForEach-Object { ($_ -replace '^\s*(?:\?\?|[MADRCU ][MADRCU])\s+','').Trim('"').Replace('\','/') })
$foreign = @($dirty | Where-Object { $expected -notcontains $_ -and $_ -notmatch $docPattern })
Check 'scope:no-foreign-paths' ($foreign.Count -eq 0) ($foreign -join '; ')
Check 'scope:seven-or-fewer' ($dirty.Count -le 7) ($dirty -join '; ')

# 3. The seven paths must not include the accepted old C05 files.
$old = @('tools/unity/build-p45-android-game.ps1','tools/unity/run-p45-android-game.ps1','tools/tests/VerifyP45AndroidGame.Tests.ps1',
  'unity/Mahjong.Unity/Assets/Editor/C05FullGameAndroidBuild.cs','unity/Mahjong.Unity/Assets/Editor/C05FullGameAndroidBuild.cs.meta')
$oldDiff = @(& git -C $root diff --name-only HEAD -- $old 2>&1)
Check 'scope:old-c05-untouched' ($oldDiff.Count -eq 0) ($oldDiff -join '; ')
$oldStatus = @($status | Where-Object { $_ -match 'C05FullGameAndroidBuild|build-p45-android-game|run-p45-android-game|VerifyP45AndroidGame' })
Check 'scope:old-c05-not-dirty' ($oldStatus.Count -eq 0) ($oldStatus -join '; ')

# 4. Meta GUID must be unique across the Unity project.
$metaPath = Join-Path $root 'unity/Mahjong.Unity/Assets/Editor/C05EmulatorAndroidBuild.cs.meta'
$metaText = Get-Content -LiteralPath $metaPath -Raw
$guidMatch = [regex]::Match($metaText, '(?m)^guid:\s*([0-9a-f]{32})\s*$')
Check 'meta:guid-format' $guidMatch.Success
$guid = $guidMatch.Groups[1].Value
$allMetas = Get-ChildItem (Join-Path $root 'unity\Mahjong.Unity\Assets') -Recurse -File -Filter '*.meta'
$dups = @($allMetas | Where-Object { $_.FullName -ne $metaPath } | Select-String -SimpleMatch "guid: $guid" -List)
Check 'meta:guid-unique' ($dups.Count -eq 0) (($dups | ForEach-Object { $_.Path }) -join '; ')

# 5. Static contract probes: no silent Mono fallback, explicit x86_64 emulator variant and bounded patterns.
$cs = Get-Content -LiteralPath (Join-Path $root 'unity/Mahjong.Unity/Assets/Editor/C05EmulatorAndroidBuild.cs') -Raw
Check 'cs:variant' ($cs -match 'ANDROID_EMULATOR')
Check 'cs:x86_64' ($cs -match 'X86_64')
Check 'cs:il2cpp-gate' ($cs -match 'ScriptingImplementation\.IL2CPP')
Check 'cs:clean-cache' ($cs -match 'BuildOptions\.CleanBuildCache')
Check 'cs:source-rewrite-gate' ($cs -match 'production-input-rewritten-by-build')
Check 'cs:no-mono-fallback' ($cs -notmatch 'ScriptingImplementation\.Mono2x')
$build = Get-Content -LiteralPath (Join-Path $root 'tools/unity/build-c05-android-emulator.ps1') -Raw
Check 'build:mutex-name' ($build -match 'Local\\MahjongV5P45UnityValidation')
Check 'build:resbus-status' ($build -match 'BLOCKED_RESOURCE_BUSY')
Check 'build:storage-status' ($build -match 'BLOCKED_STORAGE')
Check 'build:x86-only-gate' ($build -match "abis\[0\] -cne 'x86_64'")
Check 'build:no-second-download' (($build -notmatch 'Invoke-WebRequest|Start-BitsTransfer') -and ($build -notmatch 'sdkmanager[^\r\n]*--(install|licenses|update)') -and ($build -notmatch 'avdmanager[^\r\n]*create'))
$run = Get-Content -LiteralPath (Join-Path $root 'tools/unity/run-c05-android-emulator.ps1') -Raw
Check 'run:boot-property' ($run -match 'sys\.boot_completed')
Check 'run:read-only-avd' ($run -match '-read-only')
Check 'run:no-snapshot-save' ($run -match '-no-snapshot-save')
Check 'run:native-tap' ($run -match "shell','input','tap'")
Check 'run:native-swipe' ($run -match "shell','input','swipe'")
Check 'run:native-back' ($run -match 'KEYCODE_BACK')
Check 'run:screencap' ($run -match "exec-out','screencap','-p'")
Check 'run:reject-arm64' ($run -match "abis\[0\] -cne 'x86_64'")
Check 'run:owned-state' ($run -match 'startUtcTicks')
Check 'run:no-snapshot-load' ($run -match '-no-snapshot-load')
Check 'run:descendant-tree-stop' ($run -match 'Get-C05RDescendantProcessIds')
Check 'run:child-env' (($run -match 'Push-C05RChildEnvironment') -and ($run -match 'ANDROID_AVD_HOME'))
Check 'run:exact-foreground' ($run -match 'Get-C05RForegroundState')
Check 'run:installed-digest' (($run -match 'Get-C05RInstalledIdentity') -and ($run -match 'baseDigest'))
Check 'run:owned-records' (($run -match 'ownedProcesses') -and ($run -match 'Stop-C05RVerifiedRecordSet'))
Check 'run:artifact-preflight-action' (($run -match "'ValidateArtifact'") -and ($run -match 'if \(\$Action -eq ''ValidateArtifact''\)') -and ($run -match 'Get-C05RVerifiedArtifact \$Apk \$PackageId'))
Check 'run:artifact-preflight-before-adb' ($run.IndexOf("if (`$Action -eq 'ValidateArtifact')") -lt $run.IndexOf("if (-not (Test-Path -LiteralPath `$adb -PathType Leaf))"))
Check 'run:capture-requires-logcat' (($run -match '\$capture\.logcat\.status -ne ''PASS''') -and ($run -match '\$capture\.logcat\.exit -ne 0') -and ($run -match 'IsNullOrWhiteSpace\(\[string\]\$capture\.logcat\.stdout\)'))
Check 'run:raw-logcat-evidence' (($run -match 'logcat\.stdout\.txt') -and ($run -match 'logcat\.stderr\.txt') -and ($run -match 'logcat-receipt\.json'))
Check 'run:drain-descendant-cleanup' (($run -match 'Stop-C05RObservedDescendants') -and ($run -match 'residualCount'))
Check 'run:descendants-bound-to-live-root' (($run -match 'Get-C05RObservedDescendantRecords') -and ($run -match 'observedWhileParentLive') -and ($run -match 'trackingComplete=\$false'))
Check 'run:cleanup-result-gates-native-status' (($run -match '\$cleanup\.status -ne ''PASS''') -and ($run -match '\$cleanup\.residualCount -ne 0') -and ($run -match '\$cleanup\.trackingComplete -ne \$true') -and ($run -match '\$cleanup\.unknown -eq \$true') -and ($run -match 'knownResidualCount -ne 0'))
Check 'run:timeout-uses-recorded-tree' (-not ($run -match 'Stop-C05ROwnedProcess \$pidOwn'))
$observedStopStart = $run.IndexOf('function Stop-C05RObservedDescendants')
$observedStopEnd = $run.IndexOf('function Stop-C05ROwnedProcess')
$observedStopBody = if ($observedStopStart -ge 0 -and $observedStopEnd -gt $observedStopStart) { $run.Substring($observedStopStart,$observedStopEnd-$observedStopStart) } else { '' }
Check 'run:dead-root-never-scans-or-kills' ($observedStopBody -match 'if \(-not \$rootNow -or \$rootNow\.StartTime\.ToUniversalTime\(\)\.Ticks -ne \$RootTicks\)' -and $observedStopBody.IndexOf('if (-not $rootNow') -lt $observedStopBody.IndexOf('foreach ($record in @($Records'))
Check 'run:bridge-copies-raw-native-streams' (($run -match 'BaseStream\.CopyToAsync\(\[Console\]::OpenStandardOutput\(\)\)') -and ($run -match 'BaseStream\.CopyToAsync\(\[Console\]::OpenStandardError\(\)\)') -and ($run -notmatch '& \$request\.file @argv'))
Check 'run:bridge-suppresses-powershell-progress' ($run.Contains('$ProgressPreference=' + [char]39 + [char]39 + 'SilentlyContinue' + [char]39 + [char]39))
Check 'run:native-exit-recorded-before-drain' (($run -match 'WriteAllText\(\$request\.statusPath') -and ($run -match 'C05RStatusPath') -and ($run -match 'native = if \(Test-Path -LiteralPath \$proc\.C05RStatusPath'))
$ownedStopStart = $run.IndexOf('function Stop-C05ROwnedProcess')
$ownedStopEnd = $run.IndexOf('function Push-C05RChildEnvironment')
$ownedStopBody = if ($ownedStopStart -ge 0 -and $ownedStopEnd -gt $ownedStopStart) { $run.Substring($ownedStopStart,$ownedStopEnd-$ownedStopStart) } else { '' }
Check 'run:owned-stop-wait-bounded' ($ownedStopBody.Length -gt 0 -and $ownedStopBody -notmatch '\.WaitForExit\(\)')
Check 'run:accel-validate-set' ($run -match '\[ValidateSet\(''auto'',''off'',''on''\)\]\[string\]\$AccelMode')
Check 'run:accel-no-whpx' ($run -notmatch '\[ValidateSet\([^)]*''whpx''[^)]*\)\]\[string\]\$AccelMode')
Check 'run:swiftshader-comment-corrected' ($run -match 'While GLES-on-SwiftShader was intended')
Check 'run:display-orientation-authoritative' ($run -match 'SurfaceOrientation.*authoritative')
Check 'run:no-aspect-ratio-guessing' ($run -notmatch '\$curW\s+-gt\s+\$curH')
Check 'run:capture-reject-stale' ($run -match 'Test-Path -LiteralPath \$png' -and $run -match 'throw.*Destination capture file already exists')
Check 'run:capture-native-exit' ($run -match '\$Capture\.screencap\.exit -eq 0' -and $run -match '\$Capture\.pidof\.exit -eq 0')
Check 'build:step-errors' (($build -match 'stepErrors') -and ($build -match 'stepExceptions'))
Check 'build:staging' (($build -match 'Copy-C05EStagingProject') -and ($build -match 'sourceInputsUnchanged'))
Check 'build:no-source-restore' (-not ($build -match 'git -C \$RepoRoot checkout'))

# 6. Real deterministic fixtures through the actual entry points.
Run-Fixture 'build-verdict' 'tools/unity/build-c05-android-emulator.ps1' '-VerdictFixtureTest' 27
Run-Fixture 'build-process' 'tools/unity/build-c05-android-emulator.ps1' '-ProcessFixtureTest' 0
Run-Fixture 'build-output' 'tools/unity/build-c05-android-emulator.ps1' '-OutputFixtureTest' 0
Run-Fixture 'build-source' 'tools/unity/build-c05-android-emulator.ps1' '-SourceFixtureTest' 0
Run-Fixture 'build-staging-guard' 'tools/unity/build-c05-android-emulator.ps1' '-StagingGuardFixtureTest' 6
Run-Fixture 'build-argv' 'tools/unity/build-c05-android-emulator.ps1' '-ArgumentFixtureTest' 0
Run-Fixture 'run-artifact' 'tools/unity/run-c05-android-emulator.ps1' '-ArtifactFixtureTest' 24 -ScriptHasAction
Run-Fixture 'run-state' 'tools/unity/run-c05-android-emulator.ps1' '-StateFixtureTest' 5 -ScriptHasAction
Run-Fixture 'run-input' 'tools/unity/run-c05-android-emulator.ps1' '-InputFixtureTest' 7 -ScriptHasAction
Run-Fixture 'run-tree' 'tools/unity/run-c05-android-emulator.ps1' '-TreeFixtureTest' 0 -ScriptHasAction
Run-Fixture 'run-env' 'tools/unity/run-c05-android-emulator.ps1' '-EnvFixtureTest' 0 -ScriptHasAction
Run-Fixture 'run-device-contract' 'tools/unity/run-c05-android-emulator.ps1' '-DeviceContractFixtureTest' 4 -ScriptHasAction
Run-Fixture 'run-receipt' 'tools/unity/run-c05-android-emulator.ps1' '-ReceiptFixtureTest' 4 -ScriptHasAction
Run-Fixture 'run-target-state' 'tools/unity/run-c05-android-emulator.ps1' '-TargetStateFixtureTest' 9 -ScriptHasAction
Run-Fixture 'run-root-eligibility' 'tools/unity/run-c05-android-emulator.ps1' '-RootEligibilityFixtureTest' 1 -ScriptHasAction

# 7. git diff --check and final summary.
$diffCheck = @(& git -C $root diff --check 2>&1); $diffCode = $LASTEXITCODE
Check 'git-diff-check' ($diffCode -eq 0) ($diffCheck -join '; ')
$summary = [pscustomobject]@{
  schema=1; status=$(if ($script:failures.Count -eq 0) { 'PASS' } else { 'FAIL' }); assertions=$script:assertions;
  failures=$script:failures.ToArray(); fixtures=$script:fixtures.ToArray(); dirtyPaths=$dirty;
  metaGuid=$guid; gitHead=(& git -C $root rev-parse HEAD 2>$null); outputRoot=$OutputRoot;
  generatedUtc=(Get-Date).ToUniversalTime().ToString('o')
}
[IO.File]::WriteAllText((Join-Path $OutputRoot 'test-summary.json'), ($summary | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding($false)))
if ($script:failures.Count -gt 0) {
  Write-Host "TESTS FAIL assertions=$script:assertions failures=$($script:failures.Count)"
  $script:failures | ForEach-Object { Write-Host " - $_" }
  exit 1
}
Write-Host "TESTS PASS assertions=$script:assertions fixtures=$($script:fixtures.Count) output=$OutputRoot"
exit 0
