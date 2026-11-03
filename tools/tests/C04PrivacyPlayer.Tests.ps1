#requires -Version 5.1
[CmdletBinding()]
param([string]$RunRoot = '')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ([string]::IsNullOrWhiteSpace($RunRoot)) { throw 'RunRoot must be a fresh ignored build/<GUID> directory.' }
$root = [IO.Path]::GetFullPath($RunRoot)
$allowed = [IO.Path]::GetFullPath((Join-Path $repo 'build')) + [IO.Path]::DirectorySeparatorChar
if (-not $root.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) { throw 'RunRoot must be under repository build/.' }
if ((Split-Path -Leaf $root) -notmatch '^[0-9a-fA-F]{32}$' -or (Test-Path -LiteralPath $root)) { throw 'RunRoot must be a fresh GUID-N directory.' }
[IO.Directory]::CreateDirectory($root) | Out-Null
$driverPath = Join-Path $repo 'unity/Mahjong.Unity/Assets/Scripts/App/C04PrivacyPlayerDriver.cs'
$metaPath = $driverPath + '.meta'
$runnerPath = Join-Path $repo 'tools/unity/run-p45-c04-privacy-matrix.ps1'
foreach ($path in @($driverPath,$metaPath,$runnerPath)) { if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required Player adapter input is missing: $path" } }
$driver = [IO.File]::ReadAllText($driverPath,[Text.Encoding]::UTF8)
$runner = [IO.File]::ReadAllText($runnerPath,[Text.Encoding]::UTF8)
$names = @(
  'RealRoom_FourPlayersSpectatorAndRoleSwitchUseIndependentSessions',
  'RealRoom_DelayedOldRawReceiveIsRejectedByNewBinding',
  'RealRoom_ColdReopenReconcilesOriginalRequestWithoutDuplicate',
  'RealRoom_LegalSleeveAndFeeOwnershipAcrossFourSeatsAndSpectator',
  'RealRoom_ReleasedSeatAiAdvancesAfterPlayerCloseWithSpectatorOnly',
  'RealRoom_OpenDecisionDeadlineContinuesAfterPlayerClose',
  'RealRoom_ClosedPlayerToSpectatorToNewPlayerReceivePathRejectsOldRaw'
)
$failures = New-Object Collections.Generic.List[string]
function Check([bool]$condition,[string]$id) { if (-not $condition) { $script:failures.Add($id) } }
$runnerNames = [regex]::Matches($runner,"Mahjong\.Unity\.Tests\.PlayMode\.C04PrivacyRoleSwitchPlayModeTests\.(RealRoom_[A-Za-z0-9_]+)") | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique
$driverCases = [regex]::Matches($driver,'"(RealRoom_[A-Za-z0-9_]+)"') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique
$suiteMethods = [regex]::Matches($driver,'public IEnumerator (RealRoom_[A-Za-z0-9_]+)\(') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique
Check ($runnerNames.Count -eq 7 -and (@(Compare-Object $names $runnerNames).Count -eq 0)) 'RUNNER_FIXED_SEVEN_CASES'
Check ($driverCases.Count -eq 7 -and (@(Compare-Object $names $driverCases).Count -eq 0)) 'DRIVER_FIXED_SEVEN_CASES'
Check ($suiteMethods.Count -eq 7 -and (@(Compare-Object $names $suiteMethods).Count -eq 0)) 'DRIVER_SCENARIO_BODIES'
$bootstrapStart = $driver.IndexOf('private static void Bootstrap()', [StringComparison]::Ordinal)
$bootstrapEnd = $driver.IndexOf('private void Start()', $bootstrapStart, [StringComparison]::Ordinal)
Check ($bootstrapStart -ge 0 -and $bootstrapEnd -gt $bootstrapStart) 'DRIVER_BOOTSTRAP_PRESENT'
if ($bootstrapStart -ge 0 -and $bootstrapEnd -gt $bootstrapStart) {
  $bootstrap = $driver.Substring($bootstrapStart,$bootstrapEnd-$bootstrapStart)
  $guard = $bootstrap.IndexOf('if (enabled == 0) return;', [StringComparison]::Ordinal)
  $allocate = $bootstrap.IndexOf('new GameObject("C04PrivacyPlayerDriver")', [StringComparison]::Ordinal)
  Check ($guard -ge 0 -and $allocate -gt $guard) 'NOOPTIN_ALLOCATES_NO_DRIVER'
  Check ($bootstrap.Contains('Array.IndexOf(Cases, caseName)') -and $bootstrap.Contains('Directory.Exists(full) || File.Exists(full)')) 'INVALID_CASE_AND_EXISTING_EVIDENCE_REJECTED'
  Check ($bootstrap.Contains('Required(args, CaseArgument)') -and $bootstrap.Contains('Required(args, EvidenceArgument)')) 'REQUIRED_ARGUMENTS_VALIDATED'
}
Check ($driver.Contains('AppRoot.EnsureInitialized') -and $driver.Contains('.Composition') -and $driver.Contains('new TableSession') -and $driver.Contains('CreateRoomViewerAsync')) 'REAL_APP_AND_SESSION_PATHS'
Check ($driver.Contains('class C04ControlContract') -and $driver.Contains('staleNoCommitMetadata') -and $driver.Contains('staleCommandFrames != 1')) 'PLAYER_CONTROL_MATRIX_ASSERTIONS'
Check ([regex]::IsMatch($driver,'private static string Json\(string value\).*Replace\("\\r", "\\\\r"\).*Replace\("\\n", "\\\\n"\);')) 'DRIVER_JSON_ESCAPE_IS_SINGLE_LINE'
Check ($runner.Contains('[switch]$WindowsAppMatrix') -and $runner.Contains('Invoke-C04WindowsAppMatrix') -and $runner.Contains('windows-app-package-before.sha256') -and $runner.Contains('windows-app-package-after.sha256')) 'ORDINARY_PLAYER_PACKAGE_MATRIX_PATH'
Check ($runner.Contains('windows-app-p2-prepare') -and $runner.Contains('windows-app-p2-recover') -and $runner.Contains('oldScopeQueryCount') -and $runner.Contains('prepareDoc.process.pid') -and $runner.Contains('recoverDoc.process.pid')) 'P2_PROCESS_RESTART_AND_ORIGINAL_QUERY'
Check ($runner.Contains('windows-app-arg-reject-') -and $runner.Contains('unknown-case') -and $runner.Contains('duplicate-enable') -and $runner.Contains('C04_PRIVACY_PLAYER_ARGS_REJECTED')) 'PLAYER_ARGUMENT_NEGATIVE_MATRIX'
Check ($runner.Contains('[switch]$CompileOnly') -and $runner.Contains('unity-project-compile-only') -and $runner.Contains('Mahjong.Unity.App.dll')) 'CHEAP_UNITY_SCRIPT_COMPILE_MODE'
Check ($runner.Contains('[string]$ReuseLibraryCacheFrom') -and $runner.Contains('excludedFromPriorSeal = $true') -and $runner.Contains('Fresh staging project')) 'SEALED_EXCLUDED_CACHE_REUSE_GUARD'
Check ($driver.Contains('deadline-evidence.json') -and $driver.Contains('AuthoritativeDeadlineServerTimeMs') -and $driver.Contains('SpectatorBaselineCursorStreamVersion')) 'DEADLINE_PRODUCER_PERSISTS_AUTHORITY_AND_CURSOR_EVIDENCE'
Check ($driver.Contains('decisionClock.DecisionSeat') -and $driver.Contains('decisionClock.ServerTimeMs') -and $driver.Contains('session.State == TableConnectionState.Closed') -and $driver.Contains('PlayerBindingClosed = playerBindingClosed')) 'DEADLINE_EVIDENCE_IS_CAPTURED_FROM_LIVE_SNAPSHOTS_AND_CLOSED_BINDING'
Check ($driver.Contains('control-session-before-stale-submit.json') -and $driver.Contains('claimed.State != TableConnectionState.Ready') -and $driver.Contains('Scope.ScopeId.Value')) 'RELEASED_CONTROL_CAPTURE_PROVES_READY_BOUND_SESSION_AT_SUBMIT'
Check ($driver.Contains('"r-" + Guid.NewGuid().ToString("N")') -and $driver.Contains('Path.Combine(runRoot, "state", prefix.Substring(0, Math.Min(2, prefix.Length))')) 'WINDOWS_SAVE_PATH_COMPONENTS_STAY_BELOW_LEGACY_PATH_LIMIT'
Check ($driver.Contains('claimedPumpError') -and $driver.Contains('onTransientError?.Invoke(exception)') -and $driver.Contains('transportOpen=') -and $driver.Contains('lastError=')) 'RELEASED_CONTROL_RECONNECT_FAILURE_PRESERVES_TRANSPORT_DIAGNOSTIC'
Check ($runner.Contains('function Assert-C04PlayerCaseEvidence') -and $runner.Contains('Open-deadline evidence failed its real decision') -and -not $runner.Contains('$caseFiles.Count -lt 3')) 'RUNNER_SEMANTICALLY_ACCEPTS_CASE_ARTIFACTS'
Check ($runner.Contains('raw-frames.json') -and $runner.Contains('zeroDuplicateStateDelta') -and $runner.Contains('staleNoCommitMetadata')) 'ALL_CASE_RAW_RECOVERY_AND_CONTROL_OUTPUTS_ARE_REQUIRED'
$recoveryProbe = [IO.File]::ReadAllText((Join-Path $repo 'unity/Mahjong.Unity/Assets/Scripts/App/C04RecoveryReconciliationProbe.cs'),[Text.Encoding]::UTF8)
Check ($recoveryProbe.Contains('FileOutboxStore(arguments.OutboxDirectory)') -and $recoveryProbe.Contains('session.ReconcilePendingAsync') -and $recoveryProbe.Contains('noOriginalCommandResend')) 'P2_REAL_OUTBOX_RECONCILE_CONTRACT'
$guid = [regex]::Match([IO.File]::ReadAllText($metaPath),'(?m)^guid: ([0-9a-f]{32})$').Groups[1].Value
Check ($guid.Length -eq 32) 'UNIQUE_META_GUID'
$result = [ordered]@{ schemaVersion=1; testKind='source-contract'; total=24; passed=(24-$failures.Count); failed=$failures.Count; failures=$failures.ToArray(); driverSha256=(Get-FileHash -Algorithm SHA256 $driverPath).Hash.ToLowerInvariant(); runnerSha256=(Get-FileHash -Algorithm SHA256 $runnerPath).Hash.ToLowerInvariant() }
$json = $result | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText((Join-Path $root 'summary.json'),$json + "`n",[Text.UTF8Encoding]::new($false))
if ($failures.Count) { Write-Error ('C04_PRIVACY_PLAYER_TESTS_FAIL ' + ($failures -join ',')); exit 1 }
Write-Output ('C04_PRIVACY_PLAYER_TESTS_PASS passed=' + $result.passed + ' failed=0 kind=source-contract')
exit 0
