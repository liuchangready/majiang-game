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
$playModePath = Join-Path $repo 'unity/Mahjong.Unity/Assets/Tests/PlayMode/C04PrivacyRoleSwitchPlayModeTests.cs'
$metaPath = $driverPath + '.meta'
$runnerPath = Join-Path $repo 'tools/unity/run-p45-c04-privacy-matrix.ps1'
foreach ($path in @($driverPath,$playModePath,$metaPath,$runnerPath)) { if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required Player adapter input is missing: $path" } }
$driver = [IO.File]::ReadAllText($driverPath,[Text.Encoding]::UTF8)
$playMode = [IO.File]::ReadAllText($playModePath,[Text.Encoding]::UTF8)
$runner = [IO.File]::ReadAllText($runnerPath,[Text.Encoding]::UTF8)
$wireValidatorPath = Join-Path $repo 'dotnet/tests/Mahjong.LocalHost.Tests/C04PrivacyStreamMatrixTests.cs'
$wireValidator = [IO.File]::ReadAllText($wireValidatorPath,[Text.Encoding]::UTF8)
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
Check ($driver.Contains('Assert.AreEqual(evidence.ReleaseVersion, evidence.SpectatorBaselineVersion') -and $runner.Contains('$deadlineFieldsComplete') -and $runner.Contains('$e.afterCursorProjectionSeq -le [UInt64]$e.spectatorBaselineCursorProjectionSeq')) 'ZERO_INITIAL_STATE_FLOOR_IS_EXPLICIT_AND_REQUIRES_REAL_STATE_AND_CURSOR_PROGRESS'
Check ($driver.Contains('control-session-before-stale-submit.json') -and $driver.Contains('claimed.State != TableConnectionState.Ready') -and $driver.Contains('Scope.ScopeId.Value')) 'RELEASED_CONTROL_CAPTURE_PROVES_READY_BOUND_SESSION_AT_SUBMIT'
Check ($driver.Contains('"r-" + Guid.NewGuid().ToString("N")') -and $driver.Contains('Path.Combine(runRoot, "state", prefix.Substring(0, Math.Min(2, prefix.Length))')) 'WINDOWS_SAVE_PATH_COMPONENTS_STAY_BELOW_LEGACY_PATH_LIMIT'
Check ($driver.Contains('claimedPumpError') -and $driver.Contains('onTransientError?.Invoke(exception)') -and $driver.Contains('transportOpen=') -and $driver.Contains('lastError=')) 'RELEASED_CONTROL_RECONNECT_FAILURE_PRESERVES_TRANSPORT_DIAGNOSTIC'
Check ($driver.Contains('claimedPumpCts.Cancel()') -and $driver.Contains('await claimedPump.ConfigureAwait(false)') -and $driver.Contains('claimed.OpenAsync(new TableSessionOpenRequest(claimedTicket.SessionTicket') -and $driver.Contains('recoveryBranchTaken')) 'CONTROL_CURSOR_FLOOR_RECOVERY_IS_SERIALIZED_WITH_ORIGINAL_TICKET'
Check ($driver.Contains('ClaimReceiptStatus = claimReceipt.Resolution.ReceiptStatus.ToString()') -and $driver.Contains('StaleReceiptStatus = staleReceipt.Resolution.ReceiptStatus.ToString()') -and $driver.Contains('ClaimReceiptReason = claimReceipt.Resolution.ReasonCode')) 'CLAIM_AND_STALE_RECEIPTS_HAVE_SEPARATE_PRODUCER_FIELDS'
Check ($driver.Contains('StaleRequestId = staleCommand.RequestId.Value') -and $driver.Contains('StaleDecisionId = staleDecision.DecisionId') -and $driver.Contains('StaleBindingEpoch = claimed.Binding.BindingEpoch.Value')) 'STALE_RECEIPT_IS_BOUND_TO_ORIGINAL_DECISION_AND_RESUMED_BINDING'
Check ($playMode.Contains('claimedPumpCts.Cancel()') -and $playMode.Contains('claimed.OpenAsync(new TableSessionOpenRequest(claimedTicket.SessionTicket') -and $playMode.Contains('control-session-reconnect.json')) 'PLAYMODE_USES_SERIALIZED_ORIGINAL_IDENTITY_RECOVERY'
Check ($playMode.Contains('claimReceipt.Resolution.ReasonCode') -and $playMode.Contains('staleReceipt.Resolution.ReceiptStatus.ToString()') -and $playMode.Contains('StaleBindingEpoch = claimed.Binding.BindingEpoch.Value')) 'PLAYMODE_PRODUCES_SEPARATE_RECEIPTS_AND_RESUMED_BINDING_IDENTITY'
Check ($runner.Contains('$e.claimReceiptReason -ceq ''LOCAL_CONTROL''') -and $runner.Contains('$e.staleReason -ceq ''DECISION_MISMATCH''') -and $runner.Contains('[UInt64]$e.capturedDecisionEpoch -eq [UInt64]$e.staleEpoch')) 'RUNNER_REQUIRES_EXACT_SEPARATE_CONTROL_RECEIPTS_AND_EPOCHS'
Check ($runner.Contains("'control-session-before-stale-submit.json'") -and $runner.Contains("'control-session-reconnect.json'")) 'CONTROL_EVIDENCE_PATHS_FLATTENED_FOR_WINDOWS_BUDGET'
Check ($runner.Contains('$session.capturedStaleDecisionId -ceq $e.staleDecisionId') -and -not $runner.Contains('$e.staleDecisionId -cne $session.snapshotDecisionId')) 'ORIGINAL_STALE_DECISION_IS_DISTINCT_FROM_CURRENT_SNAPSHOT_DECISION'
Check ($runner.Contains('$reconnect.oldScopeId -cne $reconnect.newScopeId') -and $runner.Contains('$reconnect.newWelcomeScopeId -ceq $reconnect.newScopeId') -and -not $runner.Contains('sameScopeIdentity')) 'ROTATED_SCOPE_IS_BOUND_TO_THE_NEW_WELCOME'
Check ($runner.Contains('resumeTicketSha256Before') -and $runner.Contains('resumeTicketSha256After') -and $runner.Contains('$reconnect.newScopeId -ceq $readyPositive.oldScopeId') -and $runner.Contains('[UInt64]$reconnect.newBindingEpoch -eq [UInt64]$readyPositive.oldBindingEpoch') -and -not $runner.Contains('$reconnect.newScopeId -ceq $session.scopeId')) 'FIRST_RECOVERY_BINDING_IS_LINKED_TO_THE_NEXT_READY_POSITIVE'
Check ($driver.Contains('claimedCapture.CapturedWelcome') -and $driver.Contains('Path.Combine(EvidenceRoot(), "control-session-reconnect.json")') -and $playMode.Contains('Path.Combine(EvidenceRoot(), "control-session-reconnect.json")')) 'BOTH_PRODUCERS_CAPTURE_WELCOME_AND_USE_CONSUMER_PATH'
Check ($driver.Contains('capturedStaleDecisionId') -and $driver.Contains('capturedStaleDecisionEpoch') -and $playMode.Contains('capturedStaleDecisionId') -and $playMode.Contains('capturedStaleDecisionEpoch')) 'BOTH_PRODUCERS_SEPARATE_ORIGINAL_AND_CURRENT_DECISIONS'
Check ($driver.Contains('control-session-ready-positive.json') -and $playMode.Contains('control-session-ready-positive.json') -and $runner.Contains('post-recovery-no-change-claim')) 'BOTH_PRODUCERS_KEEP_NORMAL_READY_POSITIVE_CONTROL'
Check ($driver.Contains('WaitForSnapshotFloor(recoveredSession') -and $driver.Contains('ownerProjectionEventFloor') -and $playMode.Contains('WaitForSnapshotFloor(recoveredSession') -and $playMode.Contains('ownerProjectionEventFloor')) 'RECOVERY_RAW_CAPTURE_WAITS_FOR_ALL_OWNER_STATE_AND_EVENT_FLOORS'
Check ($driver.Contains('ProjectionSeq ?? 0UL) < 2UL') -and $playMode.Contains('ProjectionSeq ?? 0UL) < 2UL')) 'RECOVERY_CASE_WAITS_FOR_REAL_PROJECTION_CURSOR_FLOOR'
Check ($runner.Contains('function Assert-C04PlayerCaseEvidence') -and $runner.Contains('Open-deadline evidence failed its real decision') -and -not $runner.Contains('$caseFiles.Count -lt 3')) 'RUNNER_SEMANTICALLY_ACCEPTS_CASE_ARTIFACTS'
Check ($runner.Contains('raw-frames.json') -and $runner.Contains('zeroDuplicateStateDelta') -and $runner.Contains('staleNoCommitMetadata')) 'ALL_CASE_RAW_RECOVERY_AND_CONTROL_OUTPUTS_ARE_REQUIRED'
Check ($runner.Contains('function Get-C04WindowsCanonicalRawVerifierFiles') -and $runner.Contains('released-control-raw/raw-frames.json') -and $runner.Contains('function Invoke-C04ReleasedControlWireValidator') -and $runner.Contains('control-wire-validator-build') -and $wireValidator.Contains('WireCodec.Decode') -and $wireValidator.Contains('WireCodec.EnsureStrict') -and $wireValidator.Contains('bad-welcome-rehashed') -and $wireValidator.Contains('real-foreign-owner-wire')) 'WINDOWS_CANONICAL_RAW_SET_AND_CONTROL_RAW_USE_TYPED_WIRECODEC_VALIDATION'
Check ($driver.Contains('reconciled.Resolution.CommandVersion?.Value') -and $driver.Contains('reconciled.Resolution.EventSeq?.Value')) 'RECOVERY_MATCHES_ORIGINAL_COMMIT_METADATA'
Check ($driver.Contains('Assert.GreaterOrEqual(recoveredSession.PlayerSnapshot!.StateVersion, afterSubmitVersion') -and $driver.Contains('Assert.GreaterOrEqual(recoveredSession.PlayerSnapshot.EventSeq, afterSubmitEventSeq') -and -not $driver.Contains('Assert.AreEqual(afterSubmitEventSeq, recoveredSession.PlayerSnapshot.EventSeq)')) 'RECOVERY_ALLOWS_VALID_PROGRESS_WITHOUT_DUPLICATE_COMMAND'
$driverNoChange = $driver.IndexOf('PlayerSnapshot noChangeBase', [StringComparison]::Ordinal)
$driverStale = $driver.IndexOf('var staleCommand = new ProtocolCommand', $driverNoChange, [StringComparison]::Ordinal)
$playModeNoChange = $playMode.IndexOf('PlayerSnapshot noChangeBase', [StringComparison]::Ordinal)
$playModeStale = $playMode.IndexOf('var staleCommand = new ProtocolCommand', $playModeNoChange, [StringComparison]::Ordinal)
Check ($driverNoChange -ge 0 -and $driverStale -gt $driverNoChange -and $playModeNoChange -ge 0 -and $playModeStale -gt $playModeNoChange) 'NO_CHANGE_READY_POSITIVE_CONTROL_PRECEDES_STALE_ACTION'
Check ($driver.Contains('noChangeRecoveryBranchTaken') -and $driver.Contains('noChangeResumedWelcome') -and $playMode.Contains('noChangeRecoveryBranchTaken') -and $playMode.Contains('noChangeResumedWelcome')) 'NO_CHANGE_CONTROL_USES_FORMAL_ORIGINAL_TICKET_RECOVERY'
Check ($driver.Contains('C04PrivacyProbe.BeginPlayerSeatZero(Path.Combine(EvidenceRoot(), "released-control-raw"))') -and $driver.Contains('scene=released-control') -and $playMode.Contains('C04PrivacyProbe.BeginPlayerSeatZero(Path.Combine(EvidenceRoot(), "released-control-raw"))')) 'RELEASED_CONTROL_SCENARIO_SEALS_REAL_RAW_FRAMES'
$probeText = [IO.File]::ReadAllText((Join-Path $repo 'unity/Mahjong.Unity/Assets/Scripts/App/C04PrivacyProbe.cs'),[Text.Encoding]::UTF8)
Check ($probeText.Contains('PlayerSeatZeroSessions.Add(envelope.Welcome.SessionId)') -and $probeText.Contains('ConnectionKey(welcome.SessionId, envelope.BindingEpoch)') -and $probeText.Contains('ConnectionKey(envelope.SessionId, envelope.BindingEpoch)')) 'CONTROL_RAW_CAPTURE_FILTERS_SPECTATOR_AND_BINDS_ROTATED_WELCOME_BY_EPOCH'
$ticketHashJsonEnd = [string]::Concat('claimedTicket.SessionTicket)) + ',[char]34,[char]92,[char]34,[char]34,' +')
Check ($driver.Contains($ticketHashJsonEnd) -and $playMode.Contains($ticketHashJsonEnd)) 'NO_CHANGE_EVIDENCE_CLOSES_RESUME_TICKET_SHA_JSON_STRING'
$recoveryProbe = [IO.File]::ReadAllText((Join-Path $repo 'unity/Mahjong.Unity/Assets/Scripts/App/C04RecoveryReconciliationProbe.cs'),[Text.Encoding]::UTF8)
Check ($recoveryProbe.Contains('FileOutboxStore(arguments.OutboxDirectory)') -and $recoveryProbe.Contains('session.ReconcilePendingAsync') -and $recoveryProbe.Contains('noOriginalCommandResend')) 'P2_REAL_OUTBOX_RECONCILE_CONTRACT'
$guid = [regex]::Match([IO.File]::ReadAllText($metaPath),'(?m)^guid: ([0-9a-f]{32})$').Groups[1].Value
Check ($guid.Length -eq 32) 'UNIQUE_META_GUID'
$result = [ordered]@{ schemaVersion=1; testKind='source-contract'; total=47; passed=(47-$failures.Count); failed=$failures.Count; failures=$failures.ToArray(); driverSha256=(Get-FileHash -Algorithm SHA256 $driverPath).Hash.ToLowerInvariant(); playModeSha256=(Get-FileHash -Algorithm SHA256 $playModePath).Hash.ToLowerInvariant(); runnerSha256=(Get-FileHash -Algorithm SHA256 $runnerPath).Hash.ToLowerInvariant() }
$json = $result | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText((Join-Path $root 'summary.json'),$json + "`n",[Text.UTF8Encoding]::new($false))
if ($failures.Count) { Write-Error ('C04_PRIVACY_PLAYER_TESTS_FAIL ' + ($failures -join ',')); exit 1 }
Write-Output ('C04_PRIVACY_PLAYER_TESTS_PASS passed=' + $result.passed + ' failed=0 kind=source-contract')
exit 0
