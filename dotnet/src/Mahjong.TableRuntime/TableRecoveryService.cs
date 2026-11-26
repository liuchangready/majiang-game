using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;

namespace Mahjong.TableRuntime
{
    public sealed class TableRecoveryOptions
    {
        public TableRecoveryOptions(
            string ownerInstance,
            string bootId,
            int rulesetVersion,
            TimeSpan leaseTtl,
            long graceMillis = 5000L,
            string? matchId = null,
            bool requireCheatLedger = true,
            bool requireCommittedFacts = true,
            CheatEconomyConfig? cheatEconomy = null,
            SleeveBloomConfig? sleeveBloomConfig = null)
        {
            if (string.IsNullOrWhiteSpace(ownerInstance))
            {
                throw new ArgumentException("Owner instance cannot be blank.", nameof(ownerInstance));
            }

            if (string.IsNullOrWhiteSpace(bootId))
            {
                throw new ArgumentException("BootId cannot be blank.", nameof(bootId));
            }

            if (rulesetVersion < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(rulesetVersion), rulesetVersion, "Ruleset version must be positive.");
            }

            if (leaseTtl <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(leaseTtl), "Lease TTL must be positive.");
            }

            if (graceMillis < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(graceMillis), graceMillis, "Recovery grace must be non-negative.");
            }

            OwnerInstance = ownerInstance;
            BootId = bootId;
            RulesetVersion = rulesetVersion;
            LeaseTtl = leaseTtl;
            GraceMillis = graceMillis;
            MatchId = string.IsNullOrWhiteSpace(matchId) ? null : matchId;
            RequireCheatLedger = requireCheatLedger;
            RequireCommittedFacts = requireCommittedFacts;
            CheatEconomy = cheatEconomy;
            SleeveBloomConfig = sleeveBloomConfig;
        }

        public string OwnerInstance { get; }

        public string BootId { get; }

        public int RulesetVersion { get; }

        public TimeSpan LeaseTtl { get; }

        public long GraceMillis { get; }

        public string? MatchId { get; }

        public bool RequireCheatLedger { get; }

        public bool RequireCommittedFacts { get; }

        /// <summary>Required to replay N-1b cheat commands during cold recovery.</summary>
        public CheatEconomyConfig? CheatEconomy { get; }

        public SleeveBloomConfig? SleeveBloomConfig { get; }
    }

    public enum TableRecoveryStatus : byte
    {
        Recovered = 0,
        Failed = 1,
        Blocked = 2
    }

    public sealed class TableRecoveryReport
    {
        public TableRecoveryReport(
            string roomId,
            StateVersion checkpointVersion,
            EventSeq checkpointSeq,
            int replayedCommands,
            int replayedEvents,
            StateVersion finalStateVersion,
            EventSeq finalEventSeq,
            TablePayloadSha256 finalStateHash,
            ulong finalFingerprint,
            long downtimeMillis,
            long graceMillis,
            string previousBootId,
            string currentBootId,
            OwnerEpoch previousOwnerEpoch,
            OwnerEpoch currentOwnerEpoch,
            int controlVersion,
            int decisionsRestored,
            int? cheatLedgerVersion,
            bool terminal,
            bool outboxPresent,
            TableRecoveryAuditRecord audit,
            long timingVersion = 0L,
            bool cheatStateRestored = false,
            int committedHistoryCount = 0,
            bool outboxPayloadMatches = false)
        {
            RoomId = roomId;
            CheckpointVersion = checkpointVersion;
            CheckpointSeq = checkpointSeq;
            ReplayedCommands = replayedCommands;
            ReplayedEvents = replayedEvents;
            FinalStateVersion = finalStateVersion;
            FinalEventSeq = finalEventSeq;
            FinalStateHash = finalStateHash;
            FinalFingerprint = finalFingerprint;
            DowntimeMillis = downtimeMillis;
            GraceMillis = graceMillis;
            PreviousBootId = previousBootId;
            CurrentBootId = currentBootId;
            PreviousOwnerEpoch = previousOwnerEpoch;
            CurrentOwnerEpoch = currentOwnerEpoch;
            ControlVersion = controlVersion;
            DecisionsRestored = decisionsRestored;
            CheatLedgerVersion = cheatLedgerVersion;
            Terminal = terminal;
            OutboxPresent = outboxPresent;
            Audit = audit;
            TimingVersion = timingVersion;
            CheatStateRestored = cheatStateRestored;
            CommittedHistoryCount = committedHistoryCount;
            OutboxPayloadMatches = outboxPayloadMatches;
        }

        public string RoomId { get; }

        public StateVersion CheckpointVersion { get; }

        public EventSeq CheckpointSeq { get; }

        public int ReplayedCommands { get; }

        public int ReplayedEvents { get; }

        public StateVersion FinalStateVersion { get; }

        public EventSeq FinalEventSeq { get; }

        public TablePayloadSha256 FinalStateHash { get; }

        public ulong FinalFingerprint { get; }

        public long DowntimeMillis { get; }

        public long GraceMillis { get; }

        public string PreviousBootId { get; }

        public string CurrentBootId { get; }

        public OwnerEpoch PreviousOwnerEpoch { get; }

        public OwnerEpoch CurrentOwnerEpoch { get; }

        public int ControlVersion { get; }

        public int DecisionsRestored { get; }

        public int? CheatLedgerVersion { get; }

        public bool Terminal { get; }

        public bool OutboxPresent { get; }

        public TableRecoveryAuditRecord Audit { get; }

        public long TimingVersion { get; }

        public bool CheatStateRestored { get; }

        public int CommittedHistoryCount { get; }

        public bool OutboxPayloadMatches { get; }
    }

    public sealed class TableRecoveryResult
    {
        private TableRecoveryResult(
            TableRecoveryStatus status,
            string reasonCode,
            string detail,
            TableRecoveryBaseline? baseline,
            TableRecoveryReport? report)
        {
            if (status != TableRecoveryStatus.Recovered && string.IsNullOrWhiteSpace(reasonCode))
            {
                throw new ArgumentException("A failed/blocked recovery requires a reason code.", nameof(reasonCode));
            }

            Status = status;
            ReasonCode = reasonCode ?? string.Empty;
            Detail = detail ?? string.Empty;
            Baseline = baseline;
            Report = report;
        }

        public TableRecoveryStatus Status { get; }

        public string ReasonCode { get; }

        public string Detail { get; }

        public TableRecoveryBaseline? Baseline { get; }

        public TableRecoveryReport? Report { get; }

        public bool Recovered => Status == TableRecoveryStatus.Recovered;

        public static TableRecoveryResult Success(TableRecoveryBaseline baseline, TableRecoveryReport report) =>
            new TableRecoveryResult(TableRecoveryStatus.Recovered, string.Empty, string.Empty, baseline, report);

        public static TableRecoveryResult Failed(string reasonCode, string detail) =>
            new TableRecoveryResult(TableRecoveryStatus.Failed, reasonCode, detail, null, null);

        public static TableRecoveryResult Blocked(string reasonCode, string detail) =>
            new TableRecoveryResult(TableRecoveryStatus.Blocked, reasonCode, detail, null, null);
    }

    /// <summary>
    /// Cold-recovery coordinator. It acquires a new owner epoch, replays checkpoint + command
    /// journal through the real Domain and validates 136 tiles, hashes, sequence/ordinal
    /// continuity, control epochs, decision timing and the cheat ledger before returning a
    /// baseline. Any failure leaves the caller non-Serving; it never opens a fresh game.
    /// </summary>
    public sealed class TableRecoveryService
    {
        private readonly ITableStore _store;
        private readonly ITableLease _lease;
        private readonly ITableClock _clock;
        private readonly ISettlementOutbox _outbox;

        public TableRecoveryService(
            ITableStore store,
            ITableLease lease,
            ISettlementOutbox outbox,
            ITableClock clock)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _lease = lease ?? throw new ArgumentNullException(nameof(lease));
            _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public async ValueTask<TableRecoveryResult> RecoverAsync(
            string roomId,
            TableRecoveryOptions options,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(roomId))
            {
                throw new ArgumentException("RoomId cannot be blank.", nameof(roomId));
            }

            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            TableLeaseSnapshot? previousLease = await _lease.GetAsync(roomId, cancellationToken).ConfigureAwait(false);
            TableLeaseAcquireResult acquired = await _lease.AcquireAsync(
                roomId,
                options.OwnerInstance,
                options.LeaseTtl,
                cancellationToken).ConfigureAwait(false);
            if (!acquired.Acquired || acquired.Lease == null)
            {
                return acquired.Status == TableLeaseAcquireStatus.Held
                    ? TableRecoveryResult.Blocked(
                        TableRuntimeReasonCode.RecoveryLeaseHeld,
                        acquired.Detail)
                    : TableRecoveryResult.Failed(
                        TableRuntimeReasonCode.LeaseRenewFailed,
                        acquired.Detail);
            }

            TableLeaseSnapshot lease = acquired.Lease;

            try
            {
                return await RecoverWithLeaseAsync(roomId, options, lease, previousLease, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return TableRecoveryResult.Failed(
                    TableRuntimeReasonCode.CommitCancelled,
                    "Recovery was cancelled.");
            }
            catch (Exception exception)
            {
                return TableRecoveryResult.Failed(
                    TableRuntimeReasonCode.RecoveryFailed,
                    exception.GetType().Name + ": " + exception.Message);
            }
        }

        private async ValueTask<TableRecoveryResult> RecoverWithLeaseAsync(
            string roomId,
            TableRecoveryOptions options,
            TableLeaseSnapshot lease,
            TableLeaseSnapshot? previousLease,
            CancellationToken cancellationToken)
        {
            TableStateRow? state = await _store.LoadStateAsync(roomId, cancellationToken).ConfigureAwait(false);
            TableCheckpoint? checkpoint = await _store.LoadCheckpointAsync(roomId, cancellationToken).ConfigureAwait(false);
            TableTimingJournalRecord? timingRow = await _store.LoadTimingAsync(roomId, cancellationToken).ConfigureAwait(false);
            TableCommittedFactsSnapshot? latestFacts = await _store
                .LoadCommittedFactsAsync(roomId, cancellationToken)
                .ConfigureAwait(false);
            if (state == null || checkpoint == null)
            {
                return TableRecoveryResult.Failed(
                    TableRuntimeReasonCode.CheckpointMissing,
                    "No initial checkpoint exists; recovery never opens a fresh game.");
            }

            if (!string.Equals(checkpoint.RoomId, roomId, StringComparison.Ordinal))
            {
                return TableRecoveryResult.Failed(
                    TableRuntimeReasonCode.CheckpointInvalid,
                    "Checkpoint room id does not match the fenced recovery room.");
            }

            if (checkpoint.SchemaVersion != TableCheckpointFactory.SchemaVersion)
            {
                return TableRecoveryResult.Failed(
                    TableRuntimeReasonCode.CheckpointSchemaUnsupported,
                    "Checkpoint schema " + checkpoint.SchemaVersion + " is not supported.");
            }

            if (checkpoint.RulesetVersion != options.RulesetVersion || state.RulesetVersion != options.RulesetVersion)
            {
                return TableRecoveryResult.Failed(
                    TableRuntimeReasonCode.CheckpointInvalid,
                    "Checkpoint/ruleset version does not match this service.");
            }

            if (checkpoint.StateVersion.CompareTo(state.StateVersion) > 0 ||
                checkpoint.LastEventSeq.CompareTo(state.LastEventSeq) > 0)
            {
                return TableRecoveryResult.Failed(
                    TableRuntimeReasonCode.CheckpointInvalid,
                    "Checkpoint is newer than table_state.");
            }

            RoundStateMachine round;
            TableControlCheckpoint control;
            TableTimingCheckpoint timing;
            TableCheatLedgerCheckpoint? cheatCheckpoint = null;
            CheatState? cheat = null;
            IReadOnlyList<TableEventRecord> checkpointHistory = Array.Empty<TableEventRecord>();
            IReadOnlyList<TableCommandBinding> checkpointBindings = Array.Empty<TableCommandBinding>();
            try
            {
                if (TablePayloadSha256.Compute(checkpoint.ControlPayload) != checkpoint.ControlPayloadSha256 ||
                    TablePayloadSha256.Compute(checkpoint.TimingPayload) != checkpoint.TimingPayloadSha256 ||
                    (checkpoint.CheatPayload != null &&
                     TablePayloadSha256.Compute(checkpoint.CheatPayload) != checkpoint.CheatPayloadSha256) ||
                    (checkpoint.CommittedFactsPayload != null &&
                     TablePayloadSha256.Compute(checkpoint.CommittedFactsPayload) !=
                     checkpoint.CommittedFactsPayloadSha256) ||
                    (checkpoint.CommittedCommandBindingsPayload != null &&
                     TablePayloadSha256.Compute(checkpoint.CommittedCommandBindingsPayload) !=
                     checkpoint.CommittedCommandBindingsPayloadSha256))
                {
                    return TableRecoveryResult.Failed(
                        TableRuntimeReasonCode.RecoveryStateHashMismatch,
                        "A checkpoint side payload does not match its stored hash.");
                }

                RoundRecoveryState captured = RoundRecoveryCodec.Decode(checkpoint.StatePayload);
                round = RoundRecovery.Restore(captured);
                if (TablePayloadSha256.Compute(RoundRecoveryCodec.Encode(RoundRecovery.Capture(round))) !=
                    checkpoint.StatePayloadSha256)
                {
                    return TableRecoveryResult.Failed(
                        TableRuntimeReasonCode.RecoveryStateHashMismatch,
                        "Checkpoint state hash does not match the re-encoded restored round.");
                }

                control = TableControlCheckpointCodec.Decode(checkpoint.ControlPayload);
                if (control.ControlVersion != checkpoint.ControlVersion)
                {
                    return TableRecoveryResult.Failed(
                        TableRuntimeReasonCode.CheckpointInvalid,
                        "Control payload version does not match checkpoint metadata.");
                }

                timing = TableTimingCheckpointCodec.Decode(checkpoint.TimingPayload);
                if (checkpoint.CommittedHistoryPayload != null)
                {
                    if (TablePayloadSha256.Compute(checkpoint.CommittedHistoryPayload) !=
                        checkpoint.CommittedHistoryPayloadSha256)
                    {
                        return TableRecoveryResult.Failed(
                            TableRuntimeReasonCode.RecoveryStateHashMismatch,
                            "Committed history payload does not match its stored hash.");
                    }

                    checkpointHistory = TableCommittedHistoryCodec.Decode(checkpoint.CommittedHistoryPayload);
                }
                else if (checkpoint.LastEventSeq.Value > 0)
                {
                    return TableRecoveryResult.Failed(
                        TableRuntimeReasonCode.CheckpointInvalid,
                        "Checkpoint has committed events but no committed history payload.");
                }

                if (!TableCheckpointValidator.TryValidate(
                        checkpoint,
                        out string checkpointReason,
                        out string checkpointDetail))
                {
                    return TableRecoveryResult.Failed(checkpointReason, checkpointDetail);
                }

                if (checkpoint.StateVersion.Value > 0)
                {
                    checkpointBindings = TableCommandBindingsCodec.Decode(
                        checkpoint.CommittedCommandBindingsPayload!);
                }

                if (checkpoint.CheatPayload != null)
                {
                    cheatCheckpoint = TableCheatLedgerCheckpointCodec.Decode(checkpoint.CheatPayload);
                    cheat = cheatCheckpoint.RestoreCheatState();
                }
                else if (options.RequireCheatLedger)
                {
                    return TableRecoveryResult.Failed(
                        TableRuntimeReasonCode.CheckpointInvalid,
                        "Cheat ledger checkpoint is required but missing.");
                }
            }
            catch (RecoveryValidationException exception)
            {
                return TableRecoveryResult.Failed(exception.ReasonCode, exception.Message);
            }
            catch (Exception exception) when (
                exception is InvalidDataException ||
                exception is ArgumentException ||
                exception is InvalidOperationException)
            {
                return TableRecoveryResult.Failed(
                    TableRuntimeReasonCode.CheckpointInvalid,
                    exception.GetType().Name + ": " + exception.Message);
            }

            if (checkpoint.StateVersion == state.StateVersion)
            {
                if (checkpoint.StateFingerprint != state.StateFingerprint ||
                    checkpoint.StatePayloadSha256 != state.StateHash)
                {
                    return TableRecoveryResult.Failed(
                        TableRuntimeReasonCode.RecoveryStateHashMismatch,
                        "Current checkpoint does not match table_state.");
                }
            }

            IReadOnlyList<TableEventRecord> events = await _store
                .LoadEventsAfterAsync(roomId, checkpoint.LastEventSeq, cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<TableCommandJournalRecord> commands = await _store
                .LoadCommandsAfterAsync(roomId, checkpoint.StateVersion, cancellationToken)
                .ConfigureAwait(false);

            var fullHistory = new List<TableEventRecord>(checkpointHistory.Count + events.Count);
            fullHistory.AddRange(checkpointHistory);
            fullHistory.AddRange(events);

            Dictionary<ulong, List<TableEventRecord>> eventsByVersion;
            try
            {
                eventsByVersion = ValidateSequence(
                    checkpoint,
                    state,
                    events,
                    commands);
            }
            catch (RecoveryValidationException exception)
            {
                return TableRecoveryResult.Failed(exception.ReasonCode, exception.Message);
            }

            int replayedEvents = 0;
            var consumedSeats = new HashSet<int>();
            var effectiveBindings = new List<TableCommandBinding>(checkpointBindings);
            RoundStateMachine checkpointRound = round;
            CheatState? checkpointCheat = cheat;
            if (commands.Count > 0)
            {
                round = RoundRecovery.Restore(RoundRecoveryCodec.Decode(checkpoint.StatePayload));
                cheat = cheatCheckpoint?.RestoreCheatState();
            }
            try
            {
                bool sawPendingOffTurnThird = cheat != null && HasOffTurnThirdCandidate(cheat);
                ReplayJournalSequenceOutcome attempt1 = ExecuteJournalSequence(
                    round,
                    cheat,
                    checkpoint,
                    state,
                    options,
                    commands,
                    eventsByVersion,
                    checkpointBindings,
                    protectOffTurnThird: true,
                    ref sawPendingOffTurnThird);

                bool canLegacyRetry =
                    attempt1.Failure != null &&
                    attempt1.Failure.ReasonCode == TableRuntimeReasonCode.RecoveryReplayMismatch &&
                    cheatCheckpoint != null &&
                    cheatCheckpoint.FormatSchemaVersion <= TableCheatLedgerCheckpointCodec.ThirdTileAndCharacterSchemaVersion &&
                    sawPendingOffTurnThird;

                if (canLegacyRetry)
                {
                    bool retrySawThird = sawPendingOffTurnThird;
                    ReplayJournalSequenceOutcome attempt2 = ExecuteJournalSequence(
                        round,
                        cheat,
                        checkpoint,
                        state,
                        options,
                        commands,
                        eventsByVersion,
                        checkpointBindings,
                        protectOffTurnThird: false,
                        ref retrySawThird);
                    if (attempt2.Failure == null && attempt2.ConsumedPendingOffTurnThird)
                    {
                        attempt1 = attempt2;
                    }
                }

                if (attempt1.Failure != null)
                {
                    return attempt1.Failure;
                }

                for (int n = 0; n < attempt1.Normalizations.Count; n++)
                {
                    NormalizeHistoryCheatResultTile(
                        fullHistory,
                        attempt1.Normalizations[n].Key,
                        attempt1.Normalizations[n].Value);
                }

                round = attempt1.Round!;
                cheat = attempt1.Cheat;
                consumedSeats = attempt1.ConsumedSeats!;
                effectiveBindings = attempt1.EffectiveBindings!;
                replayedEvents = attempt1.ReplayedEvents;
            }
            catch (RecoveryValidationException exception)
            {
                return TableRecoveryResult.Failed(exception.ReasonCode, exception.Message);
            }
            catch (Exception exception) when (
                exception is InvalidDataException ||
                exception is ArgumentException ||
                exception is InvalidOperationException)
            {
                return TableRecoveryResult.Failed(
                    TableRuntimeReasonCode.RecoveryReplayMismatch,
                    exception.GetType().Name + ": " + exception.Message);
            }

            byte[] finalPayload = RoundRecoveryCodec.Encode(RoundRecovery.Capture(round));
            TablePayloadSha256 finalHash = TablePayloadSha256.Compute(finalPayload);
            if (fullHistory.Count > 0 &&
                fullHistory[fullHistory.Count - 1].Seq != state.LastEventSeq)
            {
                return TableRecoveryResult.Failed(
                    TableRuntimeReasonCode.RecoveryEventGap,
                    "Reconstructed committed history does not end at table_state.last_event_seq.");
            }
            if (round.SourceFingerprint != state.StateFingerprint || finalHash != state.StateHash)
            {
                return TableRecoveryResult.Failed(
                    TableRuntimeReasonCode.RecoveryStateHashMismatch,
                    "Recovered state fingerprint/hash does not match table_state.");
            }

            if (state.CheatBound)
            {
                if (state.CheatVersion == 0L || state.CheatFingerprint == 0UL ||
                    cheat == null ||
                    cheat.Version != state.CheatVersion ||
                    cheat.Fingerprint != state.CheatFingerprint)
                {
                    return TableRecoveryResult.Failed(
                        TableRuntimeReasonCode.RecoveryStateHashMismatch,
                        "Recovered complete cheat aggregate does not match the committed table_state binding.");
                }
            }
            else
            {
                if (state.CheatVersion != 0L || state.CheatFingerprint != 0UL)
                {
                    return TableRecoveryResult.Failed(
                        TableRuntimeReasonCode.RecoveryStateHashMismatch,
                        "table_state carries a cheat binding without the committed binding marker.");
                }

                bool hasCommittedCheatJournal = false;
                for (int i = 0; i < commands.Count; i++)
                {
                    if (commands[i].SchemaVersion == 2 || commands[i].SchemaVersion == 3)
                    {
                        hasCommittedCheatJournal = true;
                        break;
                    }
                }

                if (hasCommittedCheatJournal)
                {
                    return TableRecoveryResult.Failed(
                        TableRuntimeReasonCode.RecoveryStateHashMismatch,
                        "Committed cheat journal exists but table_state carries no complete cheat binding.");
                }
            }

            if (checkpoint.ControlVersion != state.ControlVersion)
            {
                return TableRecoveryResult.Failed(
                    TableRuntimeReasonCode.CheckpointInvalid,
                    "Checkpoint control version does not match table_state.");
            }

            bool outboxPresent = false;
            if (round.IsFinished)
            {
                if (round.Result == null)
                {
                    return TableRecoveryResult.Failed(
                        TableRuntimeReasonCode.RecoveryReplayMismatch,
                        "A finished round has no result.");
                }

                string matchId = options.MatchId ?? roomId;
                TableSettlementOutboxRecord? outbox = await _outbox
                    .TryGetAsync(matchId, (int)state.StateVersion.Value, cancellationToken)
                    .ConfigureAwait(false);
                if (outbox == null)
                {
                    return TableRecoveryResult.Failed(
                        TableRuntimeReasonCode.RecoveryOutboxMissing,
                        "Terminal state has no settlement outbox record.");
                }

                byte[] expectedOutbox = TableSettlementPayloadCodec.Encode(roomId, round.Result);
                if (TablePayloadSha256.Compute(expectedOutbox) != outbox.PayloadSha256 ||
                    !BytesEqual(expectedOutbox, outbox.Payload))
                {
                    return TableRecoveryResult.Failed(
                        TableRuntimeReasonCode.RecoveryOutboxMissing,
                        "Settlement outbox payload does not match the recovered result.");
                }

                outboxPresent = true;
            }

            TableTimingCheckpoint effectiveTiming;
            long effectiveTimingVersion;
            string timingBootId;
            ulong timingCapturedUtc;
            long timingCapturedMonotonic;
            byte[]? factsEnvelope = latestFacts?.Payload ?? checkpoint.CommittedFactsPayload;
            if (options.RequireCommittedFacts && factsEnvelope == null)
            {
                return TableRecoveryResult.Failed(
                    TableRuntimeReasonCode.RecoveryStateSinkRequired,
                    "Committed facts are required but no trusted facts payload exists.");
            }

            TableProjectionContextData? effectiveFacts = null;
            if (factsEnvelope != null)
            {
                try
                {
                    effectiveFacts = TableProjectionFactsCodec.Decode(
                        factsEnvelope,
                        round.RoundId,
                        state.StateVersion);
                }
                catch (Exception exception) when (
                    exception is InvalidDataException ||
                    exception is ArgumentException)
                {
                    return TableRecoveryResult.Failed(
                        TableRuntimeReasonCode.CheckpointInvalid,
                        "Projection facts payload is invalid: " + exception.Message);
                }
            }

            try
            {
                NormalizeLegacyCheckpointHistoryAndFacts(
                    fullHistory,
                    checkpointHistory.Count,
                    checkpointRound,
                    checkpointCheat,
                    cheatCheckpoint,
                    ref effectiveFacts);
            }
            catch (RecoveryValidationException exception)
            {
                return TableRecoveryResult.Failed(exception.ReasonCode, exception.Message);
            }

            if (effectiveFacts != null)
            {
                try
                {
                    TableVisibilityProjection.ValidateRecoveredFacts(round, effectiveFacts, cheat);
                    TableVisibilityProjection.ValidateRecoveredEventFacts(
                        fullHistory,
                        effectiveFacts.EventFacts,
                        effectiveFacts.Policy,
                        round);
                }
                catch (Exception exception) when (
                    exception is ArgumentException ||
                    exception is InvalidOperationException ||
                    exception is TableProjectionException)
                {
                    return TableRecoveryResult.Failed(
                        TableRuntimeReasonCode.CheckpointInvalid,
                        "Projection facts do not match the recovered round/history: " + exception.Message);
                }
            }
            if (timingRow != null)
            {
                if (timingRow.StateVersion != state.StateVersion ||
                    timingRow.ControlVersion != state.ControlVersion)
                {
                    return TableRecoveryResult.Failed(
                        TableRuntimeReasonCode.RecoveryTimingInvalid,
                        "The latest timing journal does not belong to the current state/control version.");
                }

                if (timingRow.TimingVersion < checkpoint.TimingVersion)
                {
                    return TableRecoveryResult.Failed(
                        TableRuntimeReasonCode.RecoveryTimingInvalid,
                        "The latest timing journal is older than the checkpoint.");
                }

                effectiveTiming = timingRow.Timing;
                effectiveTimingVersion = timingRow.TimingVersion;
                timingBootId = timingRow.BootId;
                timingCapturedUtc = timingRow.CapturedAtUtcMs;
                timingCapturedMonotonic = timingRow.CapturedMonotonicMillis;
            }
            else
            {
                if (checkpoint.StateVersion != state.StateVersion ||
                    checkpoint.TimingVersion != state.TimingVersion)
                {
                    return TableRecoveryResult.Failed(
                        TableRuntimeReasonCode.RecoveryTimingInvalid,
                        "No trusted timing journal exists after the checkpoint.");
                }

                effectiveTiming = timing;
                effectiveTimingVersion = checkpoint.TimingVersion;
                timingBootId = checkpoint.BootId;
                timingCapturedUtc = checkpoint.CreatedAtUtcMs;
                timingCapturedMonotonic = checkpoint.CapturedMonotonicMillis;
            }

            ulong nowUtc = (ulong)_clock.UtcNowMs;
            long nowMonotonic = _clock.MonotonicMillis;
            bool sameBoot = string.Equals(options.BootId, timingBootId, StringComparison.Ordinal);
            long activeDelta = 0L;
            long adjustedGrace = 0L;
            long downtime = 0L;
            if (sameBoot)
            {
                if (nowMonotonic < timingCapturedMonotonic)
                {
                    return TableRecoveryResult.Failed(
                        TableRuntimeReasonCode.ClockRegression,
                        "Monotonic time moved backwards for the same boot.");
                }

                activeDelta = nowMonotonic - timingCapturedMonotonic;
            }
            else
            {
                if (timingCapturedUtc > 0 && nowUtc < timingCapturedUtc)
                {
                    return TableRecoveryResult.Failed(
                        TableRuntimeReasonCode.ClockRegression,
                        "UTC time moved backwards across boot.");
                }

                downtime = timingCapturedUtc == 0 || nowUtc <= timingCapturedUtc
                    ? 0L
                    : (long)(nowUtc - timingCapturedUtc);
                adjustedGrace = options.GraceMillis;
                if (HasUntrustedGrace(control.Snapshot))
                {
                    return TableRecoveryResult.Failed(
                        TableRuntimeReasonCode.RecoveryGraceTimingRequired,
                        "A cross-boot recovery cannot trust persisted monotonic grace deadlines.");
                }
            }

            TableGraceRecoveryTiming? graceTiming = null;
            if (string.Equals(checkpoint.BootId, options.BootId, StringComparison.Ordinal))
            {
                if (nowMonotonic < checkpoint.CapturedMonotonicMillis)
                {
                    return TableRecoveryResult.Failed(
                        TableRuntimeReasonCode.ClockRegression,
                        "Monotonic time moved backwards for the checkpoint's own boot.");
                }

                graceTiming = BuildSameBootGraceTiming(control.Snapshot, nowMonotonic);
            }

            TableTimingCheckpoint adjustedTiming;
            try
            {
                adjustedTiming = AdjustTiming(effectiveTiming, activeDelta, adjustedGrace);
            }
            catch (Exception exception) when (
                exception is ArgumentException ||
                exception is InvalidOperationException)
            {
                return TableRecoveryResult.Failed(
                    TableRuntimeReasonCode.RecoveryTimingInvalid,
                    exception.Message);
            }

            TableLeaseSnapshot? finalLease = await _lease.GetAsync(roomId, cancellationToken).ConfigureAwait(false);
            ulong revalidateUtc = (ulong)_clock.UtcNowMs;
            if (finalLease == null ||
                !string.Equals(finalLease.OwnerInstance, lease.OwnerInstance, StringComparison.Ordinal) ||
                finalLease.OwnerEpoch != lease.OwnerEpoch ||
                finalLease.IsExpiredAt(revalidateUtc))
            {
                return TableRecoveryResult.Failed(
                    TableRuntimeReasonCode.StaleOwnerEpoch,
                    "The lease expired or moved during recovery.");
            }

            var recoveredTiming = new TableTimingCheckpoint(
                adjustedTiming.RoundId,
                adjustedTiming.SeatReservesMillis,
                adjustedTiming.Decisions,
                nowUtc,
                nowMonotonic,
                options.BootId);
            TableDurableCommitResult timingAppend = await _store.AppendTimingAsync(
                new TableTimingAppendRequest(
                    roomId,
                    lease.OwnerInstance,
                    lease.OwnerEpoch,
                    state.StateVersion,
                    state.ControlVersion,
                    state.TimingVersion,
                    recoveredTiming),
                cancellationToken).ConfigureAwait(false);
            if (!timingAppend.IsCommitted)
            {
                return TableRecoveryResult.Failed(
                    timingAppend.ReasonCode,
                    "Recovery could not persist its adjusted timing baseline: " + timingAppend.Detail);
            }

            long expectedTimingVersion = state.TimingVersion + 1L;
            effectiveTimingVersion = expectedTimingVersion;

            TableStateRow? finalState = await _store.LoadStateAsync(roomId, cancellationToken).ConfigureAwait(false);
            TableCheckpoint? finalCheckpoint = await _store.LoadCheckpointAsync(roomId, cancellationToken).ConfigureAwait(false);
            TableTimingJournalRecord? finalTimingRow = await _store.LoadTimingAsync(roomId, cancellationToken).ConfigureAwait(false);
            if (finalState == null ||
                finalState.StateVersion != state.StateVersion ||
                finalState.LastEventSeq != state.LastEventSeq ||
                finalState.StateFingerprint != state.StateFingerprint ||
                finalState.StateHash != state.StateHash ||
                finalState.ControlVersion != state.ControlVersion ||
                finalState.TimingVersion != expectedTimingVersion ||
                finalState.CheatBound != state.CheatBound ||
                finalState.CheatVersion != state.CheatVersion ||
                finalState.CheatFingerprint != state.CheatFingerprint)
            {
                return TableRecoveryResult.Failed(
                    TableRuntimeReasonCode.RecoveryFailed,
                    "table_state changed during recovery.");
            }

            if (finalCheckpoint == null ||
                finalCheckpoint.StateVersion != checkpoint.StateVersion ||
                finalCheckpoint.LastEventSeq != checkpoint.LastEventSeq ||
                finalCheckpoint.ControlVersion != checkpoint.ControlVersion ||
                finalCheckpoint.TimingVersion != checkpoint.TimingVersion)
            {
                return TableRecoveryResult.Failed(
                    TableRuntimeReasonCode.RecoveryFailed,
                    "checkpoint changed during recovery.");
            }

            if (finalTimingRow == null || finalTimingRow.TimingVersion != expectedTimingVersion)
            {
                return TableRecoveryResult.Failed(
                    TableRuntimeReasonCode.RecoveryTimingInvalid,
                    "timing journal changed during recovery.");
            }

            adjustedTiming = recoveredTiming;

            var audit = new TableRecoveryAuditRecord(
                roomId,
                TableRecoveryAuditKind.RecoveryAdjusted,
                nowUtc,
                timingBootId,
                options.BootId,
                downtime,
                adjustedGrace,
                previousLease?.OwnerInstance ?? string.Empty,
                previousLease?.OwnerEpoch ?? new OwnerEpoch(0),
                lease.OwnerEpoch,
                "Recovery replayed " + replayedEvents + " events after checkpoint " + checkpoint.StateVersion + ".");

            await _store.AppendRecoveryAuditAsync(audit, cancellationToken).ConfigureAwait(false);

            var baseline = new TableRecoveryBaseline(
                round,
                state.StateVersion,
                state.LastEventSeq,
                lease.OwnerEpoch,
                new TableControlRecoveryBaseline(control.Snapshot, control.ControlVersion, graceTiming),
                adjustedTiming.ToRecoveryTiming(),
                effectiveTimingVersion,
                cheat,
                effectiveFacts,
                fullHistory,
                strictHistory: true,
                commandBindings: effectiveBindings,
                previousBootId: checkpoint.BootId,
                currentBootId: options.BootId,
                cheatBound: state.CheatBound,
                cheatVersion: state.CheatVersion,
                cheatFingerprint: state.CheatFingerprint);

            var report = new TableRecoveryReport(
                roomId,
                checkpoint.StateVersion,
                checkpoint.LastEventSeq,
                commands.Count,
                replayedEvents,
                state.StateVersion,
                state.LastEventSeq,
                finalHash,
                round.SourceFingerprint,
                downtime,
                adjustedGrace,
                timingBootId,
                options.BootId,
                previousLease?.OwnerEpoch ?? new OwnerEpoch(0),
                lease.OwnerEpoch,
                (int)control.ControlVersion,
                adjustedTiming.Decisions.Count,
                cheatCheckpoint?.Entries.Count,
                round.IsFinished,
                outboxPresent,
                audit,
                effectiveTimingVersion,
                cheat != null,
                fullHistory.Count,
                outboxPresent);

            return TableRecoveryResult.Success(baseline, report);
        }

        private static bool IsLegacyThirdFirstResultTile(
            TableEventRecord stored,
            RoundCheatEvent expected,
            int journalSchemaVersion,
            RoundCheatCommand? command,
            CheatState? preCommandCheat)
        {
            if (stored.EventSchema != 1 ||
                stored.Cheat == null ||
                journalSchemaVersion != 2 ||
                command == null ||
                preCommandCheat == null)
            {
                return false;
            }

            if (expected.Value.Kind != CheatEventKind.SleeveBloomResolved ||
                expected.Keep != SleeveBloomKeep.Third ||
                string.IsNullOrEmpty(expected.OfferId) ||
                !expected.ResultTile.HasValue)
            {
                return false;
            }

            SleeveBloomOffer? offer = null;
            if (command.Action == RoundCheatAction.SubmitSleeveBloomChoice)
            {
                SleeveBloomOffer? immediate = preCommandCheat.SleeveBloom.ImmediateOffer;
                if (immediate != null &&
                    immediate.Seat == expected.Value.Seat &&
                    string.Equals(immediate.OfferId, expected.OfferId, StringComparison.Ordinal))
                {
                    offer = immediate;
                }
            }
            else if (command.Action == RoundCheatAction.SubmitSleeveBloomOffTurnChoice)
            {
                SleeveBloomOffer? offTurn = preCommandCheat.SleeveBloom.OffTurnOfferFor(expected.Value.Seat);
                if (offTurn != null &&
                    string.Equals(offTurn.OfferId, expected.OfferId, StringComparison.Ordinal))
                {
                    offer = offTurn;
                }
            }

            if (offer == null ||
                !offer.ThirdTile.HasValue ||
                !Nullable.Equals(expected.ResultTile, offer.ThirdTile))
            {
                return false;
            }

            return Nullable.Equals(stored.Cheat.ResultTile, offer.FirstTile);
        }

        private static void NormalizeHistoryCheatResultTile(
            List<TableEventRecord> history,
            EventSeq seq,
            TileId? resultTile)
        {
            for (int i = history.Count - 1; i >= 0; i--)
            {
                TableEventRecord record = history[i];
                if (!record.Seq.Equals(seq) || record.Cheat == null)
                {
                    continue;
                }

                TableCheatEventRecord cheat = record.Cheat;
                history[i] = new TableEventRecord(
                    record.Seq,
                    record.CommandVersion,
                    record.Ordinal,
                    record.Type,
                    record.Seat,
                    record.Tile,
                    record.Detail,
                    record.EventSchema,
                    record.Payload,
                    new TableCheatEventRecord(
                        cheat.Event,
                        cheat.OfferId,
                        cheat.Keep,
                        cheat.Action,
                        resultTile,
                        cheat.HandTile,
                        cheat.TargetSlot,
                        cheat.TurnNumber));
                return;
            }
        }

        private sealed class ReplayJournalSequenceOutcome
        {
            public ReplayJournalSequenceOutcome(
                TableRecoveryResult? failure,
                RoundStateMachine? round,
                CheatState? cheat,
                HashSet<int>? consumedSeats,
                List<TableCommandBinding>? effectiveBindings,
                List<KeyValuePair<EventSeq, TileId?>> normalizations,
                int replayedEvents,
                bool consumedPendingOffTurnThird)
            {
                Failure = failure;
                Round = round;
                Cheat = cheat;
                ConsumedSeats = consumedSeats;
                EffectiveBindings = effectiveBindings;
                Normalizations = normalizations;
                ReplayedEvents = replayedEvents;
                ConsumedPendingOffTurnThird = consumedPendingOffTurnThird;
            }

            public TableRecoveryResult? Failure { get; }
            public RoundStateMachine? Round { get; }
            public CheatState? Cheat { get; }
            public HashSet<int>? ConsumedSeats { get; }
            public List<TableCommandBinding>? EffectiveBindings { get; }
            public List<KeyValuePair<EventSeq, TileId?>> Normalizations { get; }
            public int ReplayedEvents { get; }
            public bool ConsumedPendingOffTurnThird { get; }
        }

        private static ReplayJournalSequenceOutcome ExecuteJournalSequence(
            RoundStateMachine round,
            CheatState? cheat,
            TableCheckpoint checkpoint,
            TableStateRow state,
            TableRecoveryOptions options,
            IReadOnlyList<TableCommandJournalRecord> commands,
            Dictionary<ulong, List<TableEventRecord>> eventsByVersion,
            IReadOnlyList<TableCommandBinding> checkpointBindings,
            bool protectOffTurnThird,
            ref bool sawPendingOffTurnThird)
        {
            Func<CheatState, Func<TileId, bool>> selectProtection = protectOffTurnThird
                ? currentCheat => tile => currentCheat.SleeveBloom.IsProtectedTile(tile)
                : currentCheat => tile => currentCheat.SleeveBloom.IsProtectedTileWithoutOffTurnThird(tile);

            var consumedSeats = new HashSet<int>();
            var effectiveBindings = new List<TableCommandBinding>(checkpointBindings);
            var normalizations = new List<KeyValuePair<EventSeq, TileId?>>();
            int replayedEvents = 0;
            bool consumedPendingOffTurnThird = false;

            for (ulong version = checkpoint.StateVersion.Value + 1; version <= state.StateVersion.Value; version++)
            {
                if (cheat != null && HasOffTurnThirdCandidate(cheat))
                {
                    sawPendingOffTurnThird = true;
                }

                TableCommandJournalRecord journal = commands[(int)(version - checkpoint.StateVersion.Value - 1)];
                CheatState? preCommandCheat = cheat;
                RoundCheatCommand? replayedCheatCommand = null;
                RoundStateMachine next;
                IReadOnlyList<RoundEvent> expectedRound;
                IReadOnlyList<RoundCheatEvent> expectedCheat = Array.Empty<RoundCheatEvent>();

                if (journal.SchemaVersion == RoundCommandCodec.FormatVersion)
                {
                    RoundCommand command = RoundCommandCodec.Decode(journal.CommandPayload);
                    if (command.Action != RoundActionKind.DrawTile)
                    {
                        consumedSeats.Add(command.Seat);
                    }

                    bool reservationDraw = command.Action == RoundActionKind.DrawTile &&
                                           cheat != null &&
                                           options.CheatEconomy != null &&
                                           options.SleeveBloomConfig != null &&
                                           (cheat.SleeveBloom.ReservationFor(command.Seat) != null ||
                                            cheat.SleeveBloom.OffTurnOfferFor(command.Seat) != null);
                    if (reservationDraw)
                    {
                        CheatTurnContext context = BuildRecoveryCheatContext(
                            round,
                            cheat!,
                            CountDrawTurns(round),
                            0L,
                            SleeveBloomDrawKind.Normal,
                            bootId: checkpoint.BootId);
                        RoundCheatPreparation preparation = RoundCheatTransition.PrepareReservationDraw(
                            round,
                            cheat,
                            context,
                            command.Seat);
                        if (!preparation.IsReady)
                        {
                            return new ReplayJournalSequenceOutcome(
                                TableRecoveryResult.Failed(
                                    TableRuntimeReasonCode.RecoveryReplayMismatch,
                                    "Reservation draw no longer replays at command " + version + ": " +
                                    preparation.Message),
                                null, null, null, null, normalizations, 0, false);
                        }

                        RoundTransition installed = round.Install(preparation.Change!.RoundChange);
                        if (!installed.IsAccepted || installed.State == null)
                        {
                            return new ReplayJournalSequenceOutcome(
                                TableRecoveryResult.Failed(
                                    TableRuntimeReasonCode.RecoveryReplayMismatch,
                                    "Reservation draw candidate did not install at command " + version + "."),
                                null, null, null, null, normalizations, 0, false);
                        }

                        next = installed.State;
                        cheat = preparation.Change.CheatState;
                        expectedRound = installed.Events;
                        expectedCheat = preparation.Change.Events;
                    }
                    else
                    {
                        int nextSeat = (command.Seat + 1) % 4;
                        bool discardDelivery = command.Action == RoundActionKind.DiscardTile &&
                                               cheat != null &&
                                               options.CheatEconomy != null &&
                                               options.SleeveBloomConfig != null &&
                                               (command.EligibleSeats == null || command.EligibleSeats.Count == 0) &&
                                               (cheat.SleeveBloom.ReservationFor(nextSeat) != null ||
                                                cheat.SleeveBloom.OffTurnOfferFor(nextSeat) != null);
                        if (discardDelivery)
                        {
                            RoundCheatPreparation preparation = RoundCheatTransition.PrepareDiscardDelivery(
                                round,
                                cheat,
                                command,
                                0L);
                            if (!preparation.IsReady)
                            {
                                return new ReplayJournalSequenceOutcome(
                                    TableRecoveryResult.Failed(
                                        TableRuntimeReasonCode.RecoveryReplayMismatch,
                                        "Discard reservation delivery no longer replays at command " + version + ": " +
                                        preparation.Message),
                                    null, null, null, null, normalizations, 0, false);
                            }

                            RoundTransition installed = round.Install(preparation.Change!.RoundChange);
                            if (!installed.IsAccepted || installed.State == null)
                            {
                                return new ReplayJournalSequenceOutcome(
                                    TableRecoveryResult.Failed(
                                        TableRuntimeReasonCode.RecoveryReplayMismatch,
                                        "Discard reservation candidate did not install at command " + version + "."),
                                    null, null, null, null, normalizations, 0, false);
                            }

                            next = installed.State;
                            cheat = preparation.Change.CheatState;
                            expectedRound = installed.Events;
                            expectedCheat = preparation.Change.Events;
                        }
                        else if (cheat != null &&
                                 (cheat.SleeveBloom.ImmediateOffer?.Seat == command.Seat ||
                                  cheat.BeamSwap.Offer?.Seat == command.Seat))
                        {
                            CheatState settlementCheat = cheat;
                            CheatTurnContext settlementContext = BuildRecoveryCheatContext(
                                round,
                                settlementCheat,
                                CountDrawTurns(round),
                                0L,
                                SleeveBloomDrawKind.Normal,
                                bootId: checkpoint.BootId);
                            RoundCheatPreparation settlement = RoundCheatTransition.PrepareCommandWithOfferSettlement(
                                round,
                                settlementCheat,
                                command,
                                settlementContext,
                                selectProtection(settlementCheat),
                                command.Action == RoundActionKind.DiscardTile ||
                                command.Action == RoundActionKind.DeclareRiichi,
                                (command.Action == RoundActionKind.Pass ||
                                 command.Action == RoundActionKind.DeclareRiichi)
                                    ? seat => settlementCheat.SleeveBloom.ReservationFor(seat) != null ||
                                              settlementCheat.SleeveBloom.OffTurnOfferFor(seat) != null
                                    : (Func<int, bool>?)null);
                            if (!settlement.IsReady)
                            {
                                return new ReplayJournalSequenceOutcome(
                                    TableRecoveryResult.Failed(
                                        TableRuntimeReasonCode.RecoveryReplayMismatch,
                                        "Offer settlement no longer replays at command " + version + ": " +
                                        settlement.Message),
                                    null, null, null, null, normalizations, 0, false);
                            }

                            RoundTransition installed = round.Install(settlement.Change!.RoundChange);
                            if (!installed.IsAccepted || installed.State == null)
                            {
                                return new ReplayJournalSequenceOutcome(
                                    TableRecoveryResult.Failed(
                                        TableRuntimeReasonCode.RecoveryReplayMismatch,
                                        "Offer settlement candidate did not install at command " + version + "."),
                                    null, null, null, null, normalizations, 0, false);
                            }

                            next = installed.State;
                            cheat = settlement.Change.CheatState;
                            expectedRound = installed.Events;
                            expectedCheat = settlement.Change.Events;
                        }
                        else if ((command.Action == RoundActionKind.Pass ||
                                  command.Action == RoundActionKind.DeclareRiichi) &&
                                 cheat != null &&
                                 HasProtectedWallTiles(cheat, selectProtection))
                        {
                            CheatState deferredCheat = cheat;
                            Func<int, bool> defer = seat =>
                                deferredCheat.SleeveBloom.ReservationFor(seat) != null ||
                                deferredCheat.SleeveBloom.OffTurnOfferFor(seat) != null;
                            RoundPreparation deferred = round.Prepare(
                                command,
                                selectProtection(deferredCheat),
                                defer);
                            if (deferred.IsReady &&
                                deferred.Change!.NextState.Phase == RoundPhase.Draw)
                            {
                                RoundCheatPreparation delivery = RoundCheatTransition.PrepareDeferredDrawDelivery(
                                    round,
                                    deferred.Change,
                                    deferredCheat,
                                    0L);
                                if (!delivery.IsReady)
                                {
                                    return new ReplayJournalSequenceOutcome(
                                        TableRecoveryResult.Failed(
                                            TableRuntimeReasonCode.RecoveryReplayMismatch,
                                            "Deferred draw delivery no longer replays at command " + version + ": " +
                                            delivery.Message),
                                        null, null, null, null, normalizations, 0, false);
                                }

                                RoundTransition installed = round.Install(delivery.Change!.RoundChange);
                                if (!installed.IsAccepted || installed.State == null)
                                {
                                    return new ReplayJournalSequenceOutcome(
                                        TableRecoveryResult.Failed(
                                            TableRuntimeReasonCode.RecoveryReplayMismatch,
                                            "Deferred draw delivery candidate did not install at command " + version + "."),
                                        null, null, null, null, normalizations, 0, false);
                                }

                                next = installed.State;
                                cheat = delivery.Change.CheatState;
                                expectedRound = installed.Events;
                                expectedCheat = delivery.Change.Events;
                            }
                            else
                            {
                                RoundTransition transition = round.Execute(
                                    command,
                                    selectProtection(deferredCheat));
                                if (!transition.IsAccepted || transition.State == null)
                                {
                                    return new ReplayJournalSequenceOutcome(
                                        TableRecoveryResult.Failed(
                                            TableRuntimeReasonCode.RecoveryReplayMismatch,
                                            "Command version " + version + " no longer replays: " +
                                            (transition.Error?.Code.ToString() ?? "unknown")),
                                        null, null, null, null, normalizations, 0, false);
                                }

                                next = transition.State;
                                expectedRound = transition.Events;
                            }
                        }
                        else if (command.Action == RoundActionKind.DrawTile &&
                                 cheat != null &&
                                 HasProtectedWallTiles(cheat, selectProtection))
                        {
                            CheatState protectedCheat = cheat;
                            RoundPreparation prepared = round.PrepareDrawProtected(
                                command,
                                selectProtection(protectedCheat));
                            if (!prepared.IsReady)
                            {
                                return new ReplayJournalSequenceOutcome(
                                    TableRecoveryResult.Failed(
                                        TableRuntimeReasonCode.RecoveryReplayMismatch,
                                        "Protected draw no longer replays at command " + version + ": " +
                                        prepared.Error!.Message),
                                    null, null, null, null, normalizations, 0, false);
                            }

                            RoundTransition installed = round.Install(prepared.Change!);
                            if (!installed.IsAccepted || installed.State == null)
                            {
                                return new ReplayJournalSequenceOutcome(
                                    TableRecoveryResult.Failed(
                                        TableRuntimeReasonCode.RecoveryReplayMismatch,
                                        "Protected draw candidate did not install at command " + version + "."),
                                    null, null, null, null, normalizations, 0, false);
                            }

                            next = installed.State;
                            expectedRound = installed.Events;
                        }
                        else if (command.Action == RoundActionKind.DiscardTile &&
                                 cheat != null &&
                                 HasProtectedWallTiles(cheat, selectProtection) &&
                                 (command.EligibleSeats == null || command.EligibleSeats.Count == 0))
                        {
                            CheatState protectedCheat = cheat;
                            RoundPreparation prepared = round.PrepareDiscardProtected(
                                command,
                                selectProtection(protectedCheat));
                            if (!prepared.IsReady)
                            {
                                return new ReplayJournalSequenceOutcome(
                                    TableRecoveryResult.Failed(
                                        TableRuntimeReasonCode.RecoveryReplayMismatch,
                                        "Protected discard no longer replays at command " + version + ": " +
                                        prepared.Error!.Message),
                                    null, null, null, null, normalizations, 0, false);
                            }

                            RoundTransition installed = round.Install(prepared.Change!);
                            if (!installed.IsAccepted || installed.State == null)
                            {
                                return new ReplayJournalSequenceOutcome(
                                    TableRecoveryResult.Failed(
                                        TableRuntimeReasonCode.RecoveryReplayMismatch,
                                        "Protected discard candidate did not install at command " + version + "."),
                                    null, null, null, null, normalizations, 0, false);
                            }

                            next = installed.State;
                            expectedRound = installed.Events;
                            cheat = cheat.WithSleeveBloom(cheat.SleeveBloom.WithOpenedWindow(
                                command.Seat,
                                CountDrawTurns(round),
                                out _));
                        }
                        else
                        {
                            CheatState? protectionState = cheat;
                            Func<TileId, bool>? protection = protectionState == null
                                ? null
                                : selectProtection(protectionState);
                            RoundTransition transition = round.Execute(command, protection);
                            if (!transition.IsAccepted || transition.State == null)
                            {
                                return new ReplayJournalSequenceOutcome(
                                    TableRecoveryResult.Failed(
                                        TableRuntimeReasonCode.RecoveryReplayMismatch,
                                        "Command version " + version + " no longer replays: " +
                                        (transition.Error?.Code.ToString() ?? "unknown")),
                                    null, null, null, null, normalizations, 0, false);
                            }

                            next = transition.State;
                            expectedRound = transition.Events;
                            if (cheat != null && command.Action == RoundActionKind.DiscardTile)
                            {
                                cheat = cheat.WithSleeveBloom(cheat.SleeveBloom.WithOpenedWindow(
                                    command.Seat,
                                    CountDrawTurns(round),
                                    out _));
                            }
                        }
                    }
                }
                else if (journal.SchemaVersion == 2)
                {
                    if (cheat == null || options.CheatEconomy == null || options.SleeveBloomConfig == null)
                    {
                        return new ReplayJournalSequenceOutcome(
                            TableRecoveryResult.Failed(
                                TableRuntimeReasonCode.RecoveryPrivateStateUnsupported,
                                "Command version " + version + " is a cheat command but no cheat aggregate/config is available."),
                            null, null, null, null, normalizations, 0, false);
                    }

                    RoundCheatJournal cheatJournal = TableCheatCommandCodec.Decode(journal.CommandPayload);
                    replayedCheatCommand = cheatJournal.Command;
                    CheatTurnContext context = BuildRecoveryCheatContext(
                        round,
                        cheat,
                        cheatJournal.TurnNumber,
                        cheatJournal.NowMillis,
                        cheatJournal.LastDrawKind,
                        cheatJournal.RoundOrdinal,
                        cheatJournal.BootId);
                    RoundCheatPreparation preparation = RoundCheatTransition.Prepare(
                        round,
                        cheat,
                        cheatJournal.Command,
                        context,
                        options.CheatEconomy,
                        options.SleeveBloomConfig,
                        protectOffTurnThird);
                    if (!preparation.IsReady)
                    {
                        return new ReplayJournalSequenceOutcome(
                            TableRecoveryResult.Failed(
                                TableRuntimeReasonCode.RecoveryReplayMismatch,
                                "Cheat command version " + version + " no longer replays: " +
                                preparation.Message),
                            null, null, null, null, normalizations, 0, false);
                    }

                    RoundTransition installed = round.Install(preparation.Change!.RoundChange);
                    if (!installed.IsAccepted || installed.State == null)
                    {
                        return new ReplayJournalSequenceOutcome(
                            TableRecoveryResult.Failed(
                                TableRuntimeReasonCode.RecoveryReplayMismatch,
                                "Cheat candidate did not install at command " + version + "."),
                            null, null, null, null, normalizations, 0, false);
                    }

                    next = installed.State;
                    cheat = preparation.Change.CheatState;
                    expectedRound = installed.Events;
                    expectedCheat = preparation.Change.Events;
                }
                else if (journal.SchemaVersion == 3)
                {
                    if (cheat == null || options.CheatEconomy == null || options.SleeveBloomConfig == null)
                    {
                        return new ReplayJournalSequenceOutcome(
                            TableRecoveryResult.Failed(
                                TableRuntimeReasonCode.RecoveryPrivateStateUnsupported,
                                "Command version " + version + " is a cheat sweep but no cheat aggregate/config is available."),
                            null, null, null, null, normalizations, 0, false);
                    }

                    TableCheatSystemJournal system = TableCheatSystemCodec.Decode(journal.CommandPayload);
                    CheatTurnContext context = BuildRecoveryCheatContext(
                        round,
                        cheat,
                        system.TurnNumber,
                        system.NowMillis,
                        system.LastDrawKind,
                        system.RoundOrdinal,
                        system.BootId);
                    RoundCheatPreparation preparation;
                    switch (system.Action)
                    {
                        case RoundCheatSystemAction.OffTurnExpiry:
                            preparation = RoundCheatTransition.ExpireOffTurn(
                                round,
                                cheat,
                                context,
                                offer => !string.Equals(
                                    offer.BootId,
                                    system.BootId,
                                    StringComparison.Ordinal));
                            break;
                        case RoundCheatSystemAction.ImmediateExpiry:
                            preparation = RoundCheatTransition.ExpireImmediate(round, cheat, context, system.Seat);
                            break;
                        case RoundCheatSystemAction.BeamExpiry:
                            preparation = RoundCheatTransition.ExpireBeamSwap(round, cheat, context, system.Seat);
                            break;
                        default:
                            return new ReplayJournalSequenceOutcome(
                                TableRecoveryResult.Failed(
                                    TableRuntimeReasonCode.RecoveryReplayMismatch,
                                    "Unknown cheat sweep action at command " + version + "."),
                                null, null, null, null, normalizations, 0, false);
                    }

                    if (!preparation.IsReady)
                    {
                        return new ReplayJournalSequenceOutcome(
                            TableRecoveryResult.Failed(
                                TableRuntimeReasonCode.RecoveryReplayMismatch,
                                "Cheat sweep version " + version + " no longer replays: " +
                                preparation.Message),
                            null, null, null, null, normalizations, 0, false);
                    }

                    RoundTransition installed = round.Install(preparation.Change!.RoundChange);
                    if (!installed.IsAccepted || installed.State == null)
                    {
                        return new ReplayJournalSequenceOutcome(
                            TableRecoveryResult.Failed(
                                TableRuntimeReasonCode.RecoveryReplayMismatch,
                                "Cheat sweep candidate did not install at command " + version + "."),
                            null, null, null, null, normalizations, 0, false);
                    }

                    next = installed.State;
                    cheat = preparation.Change.CheatState;
                    expectedRound = installed.Events;
                    expectedCheat = preparation.Change.Events;
                }
                else
                {
                    return new ReplayJournalSequenceOutcome(
                        TableRecoveryResult.Failed(
                            TableRuntimeReasonCode.RecoveryCommandMissing,
                            "Command journal version " + version + " has unsupported schema " + journal.SchemaVersion + "."),
                        null, null, null, null, normalizations, 0, false);
                }

                if (next.Version != round.Version + 1)
                {
                    return new ReplayJournalSequenceOutcome(
                        TableRecoveryResult.Failed(
                            TableRuntimeReasonCode.DomainVersionMismatch,
                            "Replayed Domain version did not advance by one at command " + version + "."),
                        null, null, null, null, normalizations, 0, false);
                }

                List<TableEventRecord> committed = eventsByVersion[version];
                var roundStored = new List<TableEventRecord>();
                var cheatStored = new List<TableEventRecord>();
                for (int i = 0; i < committed.Count; i++)
                {
                    if (committed[i].Cheat == null)
                    {
                        roundStored.Add(committed[i]);
                    }
                    else
                    {
                        cheatStored.Add(committed[i]);
                    }
                }

                if (expectedRound.Count != roundStored.Count)
                {
                    return new ReplayJournalSequenceOutcome(
                        TableRecoveryResult.Failed(
                            TableRuntimeReasonCode.RecoveryReplayMismatch,
                            "Replayed round event count differs at command " + version + "."),
                        null, null, null, null, normalizations, 0, false);
                }

                for (int i = 0; i < expectedRound.Count; i++)
                {
                    RoundEvent domain = expectedRound[i];
                    TableEventRecord stored = roundStored[i];
                    if (stored.EventSchema != 1 ||
                        domain.Type != stored.Type ||
                        domain.Ordinal != round.Events.Count + i ||
                        domain.Seat != stored.Seat ||
                        !Nullable.Equals(domain.Tile, stored.Tile) ||
                        !string.Equals(domain.Detail, stored.Detail, StringComparison.Ordinal))
                    {
                        return new ReplayJournalSequenceOutcome(
                            TableRecoveryResult.Failed(
                                TableRuntimeReasonCode.RecoveryReplayMismatch,
                                "Replayed event identity differs at version " + version + " ordinal " + i +
                                ": expected " + stored.Type + "/" + stored.Seat + "/" + stored.Tile +
                                "/" + stored.Detail + ", actual " + domain.Type + "/" + domain.Seat + "/" +
                                domain.Tile + "/" + domain.Detail + "."),
                            null, null, null, null, normalizations, 0, false);
                    }

                    try
                    {
                        VerifyRecoveryFact(stored, next, version, i);
                    }
                    catch (RecoveryValidationException exception)
                    {
                        return new ReplayJournalSequenceOutcome(
                            TableRecoveryResult.Failed(exception.ReasonCode, exception.Message),
                            null, null, null, null, normalizations, 0, false);
                    }
                }

                if (expectedCheat.Count != cheatStored.Count)
                {
                    return new ReplayJournalSequenceOutcome(
                        TableRecoveryResult.Failed(
                            TableRuntimeReasonCode.RecoveryReplayMismatch,
                            "Replayed private cheat event count differs at command " + version + "."),
                        null, null, null, null, normalizations, 0, false);
                }

                for (int i = 0; i < expectedCheat.Count; i++)
                {
                    RoundCheatEvent expected = expectedCheat[i];
                    TableEventRecord stored = cheatStored[i];
                    bool exactResultTile = Nullable.Equals(stored.Cheat?.ResultTile, expected.ResultTile);
                    bool legacyThirdFirstTile = !exactResultTile &&
                        IsLegacyThirdFirstResultTile(
                            stored,
                            expected,
                            journal.SchemaVersion,
                            replayedCheatCommand,
                            preCommandCheat);
                    if (stored.EventSchema != 1 ||
                        stored.Cheat == null ||
                        stored.Type != RoundEventType.CheatApplied ||
                        stored.Ordinal != expectedRound.Count + i ||
                        stored.Seat != expected.Value.Seat ||
                        !stored.Cheat.Event.Equals(expected.Value) ||
                        !string.Equals(stored.Cheat.OfferId, expected.OfferId, StringComparison.Ordinal) ||
                        stored.Cheat.Keep != expected.Keep ||
                        stored.Cheat.Action != expected.Action ||
                        (!exactResultTile && !legacyThirdFirstTile) ||
                        !Nullable.Equals(stored.Cheat.HandTile, expected.HandTile) ||
                        stored.Cheat.TargetSlot != expected.TargetSlot ||
                        stored.Cheat.TurnNumber != expected.TurnNumber)
                    {
                        return new ReplayJournalSequenceOutcome(
                            TableRecoveryResult.Failed(
                                TableRuntimeReasonCode.RecoveryReplayMismatch,
                                "Replayed private cheat event differs at version " + version + " ordinal " + i + "."),
                            null, null, null, null, normalizations, 0, false);
                    }

                    try
                    {
                        VerifyRecoveryFact(stored, next, version, expectedRound.Count + i);
                    }
                    catch (RecoveryValidationException exception)
                    {
                        return new ReplayJournalSequenceOutcome(
                            TableRecoveryResult.Failed(exception.ReasonCode, exception.Message),
                            null, null, null, null, normalizations, 0, false);
                    }
                    if (legacyThirdFirstTile)
                    {
                        normalizations.Add(new KeyValuePair<EventSeq, TileId?>(stored.Seq, expected.ResultTile));
                    }
                }

                if (preCommandCheat != null &&
                    ConsumedPendingOffTurnThirdTile(preCommandCheat, round, next))
                {
                    consumedPendingOffTurnThird = true;
                }

                round = next;
                effectiveBindings.Add(new TableCommandBinding(
                    version,
                    committed.Count,
                    next.SourceFingerprint));
                replayedEvents += committed.Count;
            }

            return new ReplayJournalSequenceOutcome(
                null,
                round,
                cheat,
                consumedSeats,
                effectiveBindings,
                normalizations,
                replayedEvents,
                consumedPendingOffTurnThird);
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            int diff = 0;
            for (int i = 0; i < left.Length; i++)
            {
                diff |= left[i] ^ right[i];
            }

            return diff == 0;
        }

        private static CheatTurnContext BuildRecoveryCheatContext(
            RoundStateMachine round,
            CheatState cheat,
            int turnNumber,
            long nowMillis,
            SleeveBloomDrawKind lastDrawKind,
            int roundOrdinal = -1,
            string? bootId = null)
        {
            return new CheatTurnContext(
                round.CurrentSeat,
                round.Phase,
                round.Window != null,
                round.RiichiDeclared,
                turnNumber,
                round.RoundId,
                roundOrdinal >= 0 ? roundOrdinal : cheat.TileMarks.DealtRoundOrdinal,
                lastDrawKind,
                nowMillis,
                bootId);
        }

        private static void NormalizeLegacyCheckpointHistoryAndFacts(
            List<TableEventRecord> fullHistory,
            int checkpointHistoryCount,
            RoundStateMachine round,
            CheatState? cheat,
            TableCheatLedgerCheckpoint? cheatCheckpoint,
            ref TableProjectionContextData? effectiveFacts)
        {
            if (checkpointHistoryCount <= 0)
            {
                return;
            }

            bool factsModified = false;
            List<TableProjectionEventFact>? mutableFacts = effectiveFacts != null
                ? new List<TableProjectionEventFact>(effectiveFacts.EventFacts)
                : null;
            bool isLegacyCheatSchema =
                cheatCheckpoint != null &&
                cheatCheckpoint.FormatSchemaVersion <= TableCheatLedgerCheckpointCodec.ThirdTileAndCharacterSchemaVersion;

            for (int i = 0; i < checkpointHistoryCount; i++)
            {
                TableEventRecord record = fullHistory[i];
                if (record.Cheat == null ||
                    record.Cheat.Event.Kind != CheatEventKind.SleeveBloomResolved ||
                    record.Cheat.Keep != SleeveBloomKeep.Third)
                {
                    continue;
                }

                int factIndex = -1;
                TableProjectionEventFact? matchingFact = null;
                if (mutableFacts != null)
                {
                    for (int f = 0; f < mutableFacts.Count; f++)
                    {
                        if (mutableFacts[f].EventSeq.Equals(record.Seq))
                        {
                            factIndex = f;
                            matchingFact = mutableFacts[f];
                            break;
                        }
                    }
                }

                uint? historyResultId = record.Cheat.ResultTile.HasValue
                    ? unchecked((uint)record.Cheat.ResultTile.Value.Value)
                    : (uint?)null;
                if (TryResolveCheckpointThirdEvidence(
                        fullHistory,
                        checkpointHistoryCount,
                        i,
                        round,
                        cheat,
                        out TileId verifiableThird,
                        out TileId? expectedLegacyFirst,
                        out TileId? forbiddenSecond))
                {
                    uint verifiableThirdId = unchecked((uint)verifiableThird.Value);
                    if (!isLegacyCheatSchema)
                    {
                        if (matchingFact != null && matchingFact.Facts.ResultTileId != historyResultId)
                        {
                            throw new RecoveryValidationException(
                                TableRuntimeReasonCode.CheckpointInvalid,
                                "Committed projection fact resultTileId disagrees with checkpoint history at seq " +
                                record.Seq.Value + ".");
                        }

                        if (!Nullable.Equals(record.Cheat.ResultTile, verifiableThird) ||
                            (matchingFact != null && matchingFact.Facts.ResultTileId != verifiableThirdId))
                        {
                            throw new RecoveryValidationException(
                                TableRuntimeReasonCode.CheckpointInvalid,
                                "Current-schema checkpoint history disagrees with verified third tile at seq " +
                                record.Seq.Value + ".");
                        }

                        continue;
                    }

                    if (matchingFact != null &&
                        matchingFact.Facts.ResultTileId != historyResultId &&
                        matchingFact.Facts.ResultTileId != verifiableThirdId)
                    {
                        throw new RecoveryValidationException(
                            TableRuntimeReasonCode.CheckpointInvalid,
                            "Committed projection fact resultTileId disagrees with checkpoint history at seq " +
                            record.Seq.Value + ".");
                    }

                    if (Nullable.Equals(record.Cheat.ResultTile, verifiableThird))
                    {
                        continue;
                    }

                    if (!record.Cheat.ResultTile.HasValue)
                    {
                        throw new RecoveryValidationException(
                            TableRuntimeReasonCode.CheckpointInvalid,
                            "Checkpoint history is missing a result tile for verified third choice at seq " +
                            record.Seq.Value + ".");
                    }

                    if (forbiddenSecond.HasValue &&
                        Nullable.Equals(record.Cheat.ResultTile, forbiddenSecond))
                    {
                        throw new RecoveryValidationException(
                            TableRuntimeReasonCode.CheckpointInvalid,
                            "Checkpoint history records the second candidate instead of a legacy first or verified third tile at seq " +
                            record.Seq.Value + ".");
                    }

                    if (!expectedLegacyFirst.HasValue ||
                        !Nullable.Equals(record.Cheat.ResultTile, expectedLegacyFirst))
                    {
                        throw new RecoveryValidationException(
                            TableRuntimeReasonCode.CheckpointInvalid,
                            "Checkpoint history records an unverified non-third result tile at seq " +
                            record.Seq.Value + ".");
                    }

                    NormalizeHistoryCheatResultTile(fullHistory, record.Seq, verifiableThird);
                    if (mutableFacts != null && factIndex >= 0 && matchingFact != null)
                    {
                        mutableFacts[factIndex] = WithEventFactResultTileId(
                            matchingFact,
                            verifiableThirdId);
                        factsModified = true;
                    }
                }
                else
                {
                    if (matchingFact != null && matchingFact.Facts.ResultTileId != historyResultId)
                    {
                        throw new RecoveryValidationException(
                            TableRuntimeReasonCode.CheckpointInvalid,
                            "Committed projection fact resultTileId disagrees with checkpoint history at seq " +
                            record.Seq.Value + ".");
                    }

                    if (!record.Cheat.ResultTile.HasValue)
                    {
                        // A normalized unknown result (null) saved from an earlier v5 checkpoint remains
                        // valid across subsequent v6 resaves/reloads when no verifiable physical Third exists.
                        continue;
                    }

                    AnalyzeNonVerifiableResultTile(
                        fullHistory,
                        checkpointHistoryCount,
                        i,
                        round,
                        cheat,
                        record.Cheat.ResultTile.Value,
                        out bool disprovenAsThird,
                        out bool matchesLegacyImmediateFirst);
                    if (!disprovenAsThird)
                    {
                        continue;
                    }

                    if (isLegacyCheatSchema && matchesLegacyImmediateFirst)
                    {
                        NormalizeHistoryCheatResultTile(fullHistory, record.Seq, null);
                        if (mutableFacts != null && factIndex >= 0 && matchingFact != null)
                        {
                            mutableFacts[factIndex] = WithEventFactResultTileId(matchingFact, null);
                            factsModified = true;
                        }
                        continue;
                    }

                    throw new RecoveryValidationException(
                        TableRuntimeReasonCode.CheckpointInvalid,
                        "Checkpoint history records an invalid result tile for third choice at seq " +
                        record.Seq.Value + ".");
                }
            }

            if (factsModified && effectiveFacts != null && mutableFacts != null)
            {
                effectiveFacts = new TableProjectionContextData(
                    effectiveFacts.RoundId,
                    effectiveFacts.StateVersion,
                    effectiveFacts.SessionVersion,
                    effectiveFacts.Cheat,
                    effectiveFacts.PublicFacts,
                    effectiveFacts.PrivateFacts,
                    effectiveFacts.Session,
                    effectiveFacts.Policy,
                    mutableFacts,
                    effectiveFacts.ConcealedMeldFacts);
            }
        }

        private static bool TryResolveCheckpointThirdEvidence(
            IReadOnlyList<TableEventRecord> history,
            int checkpointHistoryCount,
            int resolvedIndex,
            RoundStateMachine round,
            CheatState? cheat,
            out TileId verifiableThird,
            out TileId? expectedLegacyFirst,
            out TileId? forbiddenSecond)
        {
            verifiableThird = default;
            expectedLegacyFirst = null;
            forbiddenSecond = null;

            TableEventRecord resolved = history[resolvedIndex];
            TableCheatEventRecord cheatRecord = resolved.Cheat!;
            if (string.IsNullOrEmpty(cheatRecord.OfferId))
            {
                return false;
            }

            SleeveBloomReservation? reservation = cheat?.SleeveBloom.ReservationFor(resolved.Seat);
            if (reservation != null &&
                string.Equals(reservation.OfferId, cheatRecord.OfferId, StringComparison.Ordinal))
            {
                verifiableThird = reservation.Tile;
                TilePosition resPos = round.Tiles.PositionOf(reservation.Tile);
                if (cheatRecord.TurnNumber == CountDrawTurns(round) &&
                    resPos.Zone == TileZone.Wall &&
                    resPos.Index > round.Tiles.WallPointer)
                {
                    TileId? secondCandidate = FindPrecedingUnprotectedWallTile(
                        round.Tiles,
                        cheat!.SleeveBloom,
                        resPos.Index - 1,
                        reservation.Tile);
                    forbiddenSecond = secondCandidate;
                    if (secondCandidate.HasValue)
                    {
                        TilePosition secondPos = round.Tiles.PositionOf(secondCandidate.Value);
                        if (secondPos.Zone == TileZone.Wall && secondPos.Index > round.Tiles.WallPointer)
                        {
                            expectedLegacyFirst = FindPrecedingUnprotectedWallTile(
                                round.Tiles,
                                cheat.SleeveBloom,
                                secondPos.Index - 1,
                                reservation.Tile);
                        }
                    }
                }
                else
                {
                    TryInferOffTurnLegacyFirstAndSecond(
                        history,
                        checkpointHistoryCount,
                        resolvedIndex,
                        deliveredIndex: -1,
                        deliveredCommandVersion: 0UL,
                        round,
                        cheat,
                        out expectedLegacyFirst,
                        out forbiddenSecond);
                }

                return true;
            }

            for (int j = resolvedIndex + 1; j < checkpointHistoryCount; j++)
            {
                TableEventRecord delivered = history[j];
                if (delivered.Cheat == null ||
                    delivered.Seat != resolved.Seat ||
                    delivered.Cheat.Event.Kind != CheatEventKind.SleeveBloomReservationDelivered ||
                    !string.Equals(delivered.Cheat.OfferId, cheatRecord.OfferId, StringComparison.Ordinal))
                {
                    continue;
                }

                for (int k = 0; k < checkpointHistoryCount; k++)
                {
                    TableEventRecord drawn = history[k];
                    if (drawn.Cheat == null &&
                        drawn.CommandVersion == delivered.CommandVersion &&
                        drawn.Seat == resolved.Seat &&
                        drawn.Type == RoundEventType.TileDrawn &&
                        drawn.Tile.HasValue)
                    {
                        verifiableThird = drawn.Tile.Value;
                        TryInferOffTurnLegacyFirstAndSecond(
                            history,
                            checkpointHistoryCount,
                            resolvedIndex,
                            deliveredIndex: j,
                            delivered.CommandVersion.Value,
                            round,
                            cheat,
                            out expectedLegacyFirst,
                            out forbiddenSecond);
                        return true;
                    }
                }
            }

            if (!cheatRecord.OfferId.StartsWith("bloom-offturn-", StringComparison.Ordinal) &&
                cheatRecord.TurnNumber == CountDrawTurns(round) &&
                round.CurrentSeat == resolved.Seat &&
                (round.Phase == RoundPhase.CheatWindow || round.Phase == RoundPhase.Discard) &&
                round.Tiles.DrawnTile.HasValue &&
                !HasLaterCheatResolutionInCheckpoint(history, checkpointHistoryCount, resolvedIndex, resolved.Seat))
            {
                verifiableThird = round.Tiles.DrawnTile.Value.Tile;
                IReadOnlyList<int> available = CheatSubmission.AvailableWallIndices(
                    round.Tiles,
                    cheat?.SleeveBloom ?? new SleeveBloomState());
                if (available.Count >= 2 &&
                    round.Tiles.TryGetTileAt(TilePosition.Wall(available[0]), out TileId secondWallTile) &&
                    round.Tiles.TryGetTileAt(TilePosition.Wall(available[1]), out TileId firstWallTile))
                {
                    forbiddenSecond = secondWallTile;
                    expectedLegacyFirst = firstWallTile;
                }

                return true;
            }

            return false;
        }

        private static bool IsInterferingWallOrderEvent(TableEventRecord row)
        {
            if (row.Cheat != null)
            {
                return row.Cheat.Event.Kind == CheatEventKind.SleeveBloomReservationDelivered ||
                       row.Cheat.Event.Kind == CheatEventKind.BeamSwapResolved ||
                       row.Cheat.Event.Kind == CheatEventKind.SleeveBloomResolved ||
                       (row.Cheat.Event.Kind == CheatEventKind.CheatPointsChanged &&
                        (string.Equals(row.Cheat.Event.Reason, "SLEEVE_BLOOM", StringComparison.Ordinal) ||
                         string.Equals(row.Cheat.Event.Reason, "BEAM_SWAP", StringComparison.Ordinal)));
            }

            return row.Type == RoundEventType.RinshanDrawn ||
                   row.Type == RoundEventType.ResponseClaimed ||
                   row.Type == RoundEventType.KanDeclared ||
                   row.Type == RoundEventType.ChiDeclared ||
                   row.Type == RoundEventType.PonDeclared;
        }

        private static void TryInferOffTurnLegacyFirstAndSecond(
            IReadOnlyList<TableEventRecord> history,
            int checkpointHistoryCount,
            int resolvedIndex,
            int deliveredIndex,
            ulong deliveredCommandVersion,
            RoundStateMachine round,
            CheatState? cheat,
            out TileId? expectedLegacyFirst,
            out TileId? forbiddenSecond)
        {
            expectedLegacyFirst = null;
            forbiddenSecond = null;

            int upperExclusive = deliveredIndex >= 0 ? deliveredIndex : checkpointHistoryCount;
            var normalDraws = new List<TileId>(2);
            bool wallOrderClean = true;
            for (int j = resolvedIndex + 1; j < upperExclusive; j++)
            {
                TableEventRecord row = history[j];
                if (IsInterferingWallOrderEvent(row))
                {
                    wallOrderClean = false;
                    break;
                }

                if (row.Cheat == null &&
                    row.Type == RoundEventType.TileDrawn &&
                    row.Tile.HasValue &&
                    (deliveredCommandVersion == 0UL || row.CommandVersion.Value != deliveredCommandVersion))
                {
                    normalDraws.Add(row.Tile.Value);
                    if (normalDraws.Count >= 2)
                    {
                        break;
                    }
                }
            }

            if (normalDraws.Count >= 1)
            {
                expectedLegacyFirst = normalDraws[0];
                if (normalDraws.Count >= 2)
                {
                    forbiddenSecond = normalDraws[1];
                }
                return;
            }

            if (wallOrderClean && !HasAnyDrawAfter(history, checkpointHistoryCount, upperExclusive - 1, deliveredCommandVersion))
            {
                IReadOnlyList<int> available = CheatSubmission.AvailableWallIndices(
                    round.Tiles,
                    cheat?.SleeveBloom ?? new SleeveBloomState());
                if (available.Count >= 1 && round.Tiles.TryGetTileAt(TilePosition.Wall(available[0]), out TileId firstWallTile))
                {
                    expectedLegacyFirst = firstWallTile;
                }
                if (available.Count >= 2 && round.Tiles.TryGetTileAt(TilePosition.Wall(available[1]), out TileId secondWallTile))
                {
                    forbiddenSecond = secondWallTile;
                }
            }
        }

        private static bool HasAnyDrawAfter(
            IReadOnlyList<TableEventRecord> history,
            int checkpointHistoryCount,
            int afterIndex,
            ulong ignoredCommandVersion)
        {
            for (int j = afterIndex + 1; j < checkpointHistoryCount; j++)
            {
                TableEventRecord row = history[j];
                if (row.Cheat == null &&
                    (row.Type == RoundEventType.TileDrawn || row.Type == RoundEventType.RinshanDrawn) &&
                    (ignoredCommandVersion == 0UL || row.CommandVersion.Value != ignoredCommandVersion))
                {
                    return true;
                }
            }

            return false;
        }

        private enum TrackedResultTileZone
        {
            UnknownInitial = 0,
            InWall = 1,
            InHand = 2,
            LeftWallForever = 3
        }

        private static bool TryApplyTrackedTileEvent(
            TableEventRecord row,
            TileId recorded,
            Dictionary<int, (TileId Tile, int DrawTurn)> latestDrawBySeat,
            ref TrackedResultTileZone zone,
            ref int? holderSeat)
        {
            if (row.Cheat == null)
            {
                if (row.Type == RoundEventType.TileDrawn || row.Type == RoundEventType.RinshanDrawn)
                {
                    if (row.Tile.HasValue && row.Tile.Value.Equals(recorded))
                    {
                        if (zone != TrackedResultTileZone.UnknownInitial && zone != TrackedResultTileZone.InWall)
                        {
                            return false;
                        }

                        zone = TrackedResultTileZone.InHand;
                        holderSeat = row.Seat;
                    }
                }
                else if (row.Type == RoundEventType.TileDiscarded &&
                         row.Tile.HasValue &&
                         row.Tile.Value.Equals(recorded))
                {
                    bool validHolder =
                        zone == TrackedResultTileZone.UnknownInitial ||
                        (zone == TrackedResultTileZone.InHand && holderSeat.HasValue && holderSeat.Value == row.Seat);
                    if (!validHolder)
                    {
                        return false;
                    }

                    zone = TrackedResultTileZone.LeftWallForever;
                    holderSeat = null;
                }
                else if (row.Type == RoundEventType.KanDeclared &&
                         row.Tile.HasValue &&
                         row.Tile.Value.Equals(recorded))
                {
                    if (string.Equals(row.Detail, "OPEN_KAN", StringComparison.Ordinal))
                    {
                        if (zone != TrackedResultTileZone.UnknownInitial && zone != TrackedResultTileZone.LeftWallForever)
                        {
                            return false;
                        }
                    }
                    else
                    {
                        bool validHolder =
                            zone == TrackedResultTileZone.UnknownInitial ||
                            (zone == TrackedResultTileZone.InHand && holderSeat.HasValue && holderSeat.Value == row.Seat);
                        if (!validHolder)
                        {
                            return false;
                        }
                    }

                    zone = TrackedResultTileZone.LeftWallForever;
                    holderSeat = null;
                }
                else if ((row.Type == RoundEventType.ChiDeclared || row.Type == RoundEventType.PonDeclared) &&
                         row.Tile.HasValue &&
                         row.Tile.Value.Equals(recorded))
                {
                    if (zone != TrackedResultTileZone.UnknownInitial && zone != TrackedResultTileZone.LeftWallForever)
                    {
                        return false;
                    }

                    zone = TrackedResultTileZone.LeftWallForever;
                    holderSeat = null;
                }

                return true;
            }

            TableCheatEventRecord cheatEv = row.Cheat;
            if (cheatEv.Event.Kind == CheatEventKind.BeamSwapResolved &&
                cheatEv.Action == BeamSwapAction.Swap)
            {
                if (cheatEv.HandTile.HasValue && cheatEv.HandTile.Value.Equals(recorded))
                {
                    bool validHolder =
                        zone == TrackedResultTileZone.UnknownInitial ||
                        (zone == TrackedResultTileZone.InHand && holderSeat.HasValue && holderSeat.Value == row.Seat);
                    if (!validHolder)
                    {
                        return false;
                    }

                    zone = TrackedResultTileZone.InWall;
                    holderSeat = null;
                }
                else if (cheatEv.ResultTile.HasValue && cheatEv.ResultTile.Value.Equals(recorded))
                {
                    if (zone != TrackedResultTileZone.UnknownInitial && zone != TrackedResultTileZone.InWall)
                    {
                        return false;
                    }

                    zone = TrackedResultTileZone.InHand;
                    holderSeat = row.Seat;
                }
            }
            else if (cheatEv.Event.Kind == CheatEventKind.SleeveBloomResolved &&
                     !(cheatEv.OfferId?.StartsWith("bloom-offturn-", StringComparison.Ordinal) ?? false) &&
                     (cheatEv.Keep == SleeveBloomKeep.Second || cheatEv.Keep == SleeveBloomKeep.Third))
            {
                if (cheatEv.ResultTile.HasValue && cheatEv.ResultTile.Value.Equals(recorded))
                {
                    if (zone != TrackedResultTileZone.UnknownInitial && zone != TrackedResultTileZone.InWall)
                    {
                        return false;
                    }

                    zone = TrackedResultTileZone.InHand;
                    holderSeat = row.Seat;
                }
                else if (latestDrawBySeat.TryGetValue(row.Seat, out var seatDraw) &&
                         seatDraw.DrawTurn == cheatEv.TurnNumber &&
                         seatDraw.Tile.Equals(recorded))
                {
                    bool validHolder =
                        zone == TrackedResultTileZone.UnknownInitial ||
                        (zone == TrackedResultTileZone.InHand && holderSeat.HasValue && holderSeat.Value == row.Seat);
                    if (!validHolder)
                    {
                        return false;
                    }

                    zone = TrackedResultTileZone.InWall;
                    holderSeat = null;
                }
            }

            return true;
        }

        private static void AnalyzeNonVerifiableResultTile(
            IReadOnlyList<TableEventRecord> history,
            int checkpointHistoryCount,
            int resolvedIndex,
            RoundStateMachine round,
            CheatState? cheat,
            TileId recorded,
            out bool disprovenAsThird,
            out bool matchesLegacyImmediateFirst)
        {
            disprovenAsThird = false;
            matchesLegacyImmediateFirst = false;

            TableEventRecord resolved = history[resolvedIndex];
            string offerId = resolved.Cheat?.OfferId ?? string.Empty;
            bool isOffTurn = offerId.StartsWith("bloom-offturn-", StringComparison.Ordinal);
            TileId? mostRecentSeatDrawTile = null;
            var latestDrawBySeat = new Dictionary<int, (TileId Tile, int DrawTurn)>();
            int cumulativeDraws = 0;

            TrackedResultTileZone trackedZone = TrackedResultTileZone.UnknownInitial;
            int? holderSeat = null;

            for (int j = 0; j < resolvedIndex; j++)
            {
                TableEventRecord row = history[j];
                if (row.Cheat == null &&
                    (row.Type == RoundEventType.TileDrawn || row.Type == RoundEventType.RinshanDrawn))
                {
                    cumulativeDraws++;
                    if (row.Tile.HasValue)
                    {
                        latestDrawBySeat[row.Seat] = (row.Tile.Value, cumulativeDraws);
                        if (row.Seat == resolved.Seat)
                        {
                            mostRecentSeatDrawTile = row.Tile.Value;
                        }
                    }
                }

                if (!disprovenAsThird &&
                    !TryApplyTrackedTileEvent(row, recorded, latestDrawBySeat, ref trackedZone, ref holderSeat))
                {
                    disprovenAsThird = true;
                }
            }

            if (!disprovenAsThird &&
                trackedZone != TrackedResultTileZone.UnknownInitial &&
                trackedZone != TrackedResultTileZone.InWall)
            {
                disprovenAsThird = true;
            }

            if (!disprovenAsThird)
            {
                if (isOffTurn)
                {
                    trackedZone = TrackedResultTileZone.InWall;
                    holderSeat = null;
                }
                else
                {
                    trackedZone = TrackedResultTileZone.InHand;
                    holderSeat = resolved.Seat;
                }
            }

            bool wallOrderClean = true;
            var sequentialNormalDraws = new List<TileId>(2);
            for (int j = resolvedIndex + 1; j < checkpointHistoryCount; j++)
            {
                TableEventRecord row = history[j];
                if (IsInterferingWallOrderEvent(row))
                {
                    wallOrderClean = false;
                }

                if (row.Cheat == null &&
                    (row.Type == RoundEventType.TileDrawn || row.Type == RoundEventType.RinshanDrawn))
                {
                    cumulativeDraws++;
                    if (row.Tile.HasValue)
                    {
                        latestDrawBySeat[row.Seat] = (row.Tile.Value, cumulativeDraws);
                        if (wallOrderClean &&
                            row.Type == RoundEventType.TileDrawn &&
                            sequentialNormalDraws.Count < 2)
                        {
                            sequentialNormalDraws.Add(row.Tile.Value);
                        }
                    }
                }

                if (!disprovenAsThird &&
                    !TryApplyTrackedTileEvent(row, recorded, latestDrawBySeat, ref trackedZone, ref holderSeat))
                {
                    disprovenAsThird = true;
                }
            }

            TilePosition currentPos = round.Tiles.PositionOf(recorded);
            bool actuallyInWall =
                currentPos.Zone == TileZone.Wall ||
                currentPos.Zone == TileZone.DeadWall ||
                currentPos.Zone == TileZone.DeadWallReplacement;

            if (!disprovenAsThird)
            {
                if (trackedZone == TrackedResultTileZone.InWall)
                {
                    if (!actuallyInWall)
                    {
                        disprovenAsThird = true;
                    }
                }
                else if (trackedZone == TrackedResultTileZone.LeftWallForever)
                {
                    if (actuallyInWall ||
                        (currentPos.Zone != TileZone.Discard && currentPos.Zone != TileZone.Meld))
                    {
                        disprovenAsThird = true;
                    }
                }
                else
                {
                    bool validFinalHolder =
                        !actuallyInWall &&
                        holderSeat.HasValue &&
                        (currentPos.Zone == TileZone.Hand || currentPos.Zone == TileZone.Meld) &&
                        currentPos.Seat == holderSeat.Value;
                    if (!validFinalHolder)
                    {
                        disprovenAsThird = true;
                    }
                }
            }

            if (!disprovenAsThird)
            {
                return;
            }

            if (isOffTurn ||
                !wallOrderClean ||
                !mostRecentSeatDrawTile.HasValue ||
                !mostRecentSeatDrawTile.Value.Equals(recorded))
            {
                return;
            }

            // After an immediate Keep=Third swap (First swapped into wall[available[1]]),
            // 1 normal draw leaves First at wall[available[0]]; >=2 draws draw First as draw #2.
            if (sequentialNormalDraws.Count >= 2)
            {
                matchesLegacyImmediateFirst = sequentialNormalDraws[1].Equals(recorded);
            }
            else if (sequentialNormalDraws.Count == 1)
            {
                IReadOnlyList<int> available = CheatSubmission.AvailableWallIndices(
                    round.Tiles,
                    cheat?.SleeveBloom ?? new SleeveBloomState());
                if (available.Count >= 1 &&
                    round.Tiles.TryGetTileAt(TilePosition.Wall(available[0]), out TileId expectedWallFirst) &&
                    expectedWallFirst.Equals(recorded))
                {
                    matchesLegacyImmediateFirst = true;
                }
            }
        }

        private static TileId? FindPrecedingUnprotectedWallTile(
            TilePositionMap map,
            SleeveBloomState sleeve,
            int startIndex,
            TileId excludedProtectedTile)
        {
            for (int idx = startIndex; idx >= map.WallPointer; idx--)
            {
                if (!map.TryGetTileAt(TilePosition.Wall(idx), out TileId candidate))
                {
                    continue;
                }

                if (map.PositionOf(candidate).Zone != TileZone.Wall)
                {
                    continue;
                }

                if (!candidate.Equals(excludedProtectedTile) && sleeve.IsProtectedTile(candidate))
                {
                    continue;
                }

                return candidate;
            }

            return null;
        }

        private static bool HasLaterCheatResolutionInCheckpoint(
            IReadOnlyList<TableEventRecord> history,
            int checkpointHistoryCount,
            int afterIndex,
            int seat)
        {
            for (int i = afterIndex + 1; i < checkpointHistoryCount; i++)
            {
                TableEventRecord row = history[i];
                if (row.Cheat != null &&
                    row.Seat == seat &&
                    (row.Cheat.Event.Kind == CheatEventKind.SleeveBloomResolved ||
                     row.Cheat.Event.Kind == CheatEventKind.BeamSwapResolved))
                {
                    return true;
                }
            }

            return false;
        }

        private static TableProjectionEventFact WithEventFactResultTileId(
            TableProjectionEventFact source,
            uint? resultTileId)
        {
            TableProjectionEventFacts f = source.Facts;
            return new TableProjectionEventFact(
                source.Type,
                source.EventSeq,
                source.CommandVersion,
                source.EventOrdinal,
                new TableProjectionEventFacts(
                    turnNumber: f.TurnNumber,
                    wallSize: f.WallSize,
                    wallRemaining: f.WallRemaining,
                    meld: f.Meld,
                    fromSeat: f.FromSeat,
                    riichiDiscard: f.RiichiDiscard,
                    tsumogiri: f.Tsumogiri,
                    discardTileId: f.DiscardTileId,
                    doubleRiichi: f.DoubleRiichi,
                    loserSeat: f.LoserSeat,
                    yaku: f.Yaku,
                    han: f.Han,
                    fu: f.Fu,
                    score: f.Score,
                    breakdown: f.Breakdown,
                    tenpaiPlayers: f.TenpaiPlayers,
                    payments: f.Payments,
                    newScores: f.NewScores,
                    finalScores: f.FinalScores,
                    winnerSeat: f.WinnerSeat,
                    roundWind: f.RoundWind,
                    roundNumber: f.RoundNumber,
                    offerId: f.OfferId,
                    keptFirst: f.KeptFirst,
                    resultTileId: resultTileId,
                    beamSwapResolution: f.BeamSwapResolution,
                    handTileId: f.HandTileId,
                    targetSlot: f.TargetSlot));
        }

        private static bool HasOffTurnThirdCandidate(CheatState cheat)
        {
            for (int seat = 0; seat < 4; seat++)
            {
                SleeveBloomOffer? offer = cheat.SleeveBloom.OffTurnOfferFor(seat);
                if (offer != null && offer.ThirdTile.HasValue)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ConsumedPendingOffTurnThirdTile(
            CheatState preCommandCheat,
            RoundStateMachine beforeRound,
            RoundStateMachine afterRound)
        {
            for (int seat = 0; seat < 4; seat++)
            {
                SleeveBloomOffer? offer = preCommandCheat.SleeveBloom.OffTurnOfferFor(seat);
                if (offer == null || !offer.ThirdTile.HasValue)
                {
                    continue;
                }

                TileId third = offer.ThirdTile.Value;
                TilePosition beforePos = beforeRound.Tiles.PositionOf(third);
                if (beforePos.Zone == TileZone.Wall &&
                    beforePos.Index >= beforeRound.Tiles.WallPointer &&
                    beforePos.Index < beforeRound.Tiles.WallEndExclusive &&
                    afterRound.Tiles.PositionOf(third).Zone != TileZone.Wall)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasProtectedWallTiles(
            CheatState cheat,
            Func<CheatState, Func<TileId, bool>>? selectProtection = null)
        {
            Func<TileId, bool> predicate = selectProtection != null
                ? selectProtection(cheat)
                : (tile => cheat.SleeveBloom.IsProtectedTile(tile));
            for (int value = 0; value < TileId.Count; value++)
            {
                if (predicate(new TileId(value)))
                {
                    return true;
                }
            }

            return false;
        }

        private static int CountDrawTurns(RoundStateMachine round)
        {
            int turns = 0;
            for (int i = 0; i < round.Events.Count; i++)
            {
                RoundEventType type = round.Events[i].Type;
                if (type == RoundEventType.TileDrawn || type == RoundEventType.RinshanDrawn)
                {
                    turns++;
                }
            }

            return turns;
        }

        private static void VerifyRecoveryFact(
            TableEventRecord stored,
            RoundStateMachine next,
            ulong version,
            int ordinal)
        {
            if (stored.Payload == null)
            {
                throw new RecoveryValidationException(
                    TableRuntimeReasonCode.RecoveryReplayMismatch,
                    "Committed event " + stored.Seq + " has no fact binding at version " + version + ".");
            }

            TableEventFact fact = TableEventFactCodec.Decode(stored.Payload);
            if (fact.CommandVersion != stored.CommandVersion ||
                fact.Ordinal != stored.Ordinal ||
                fact.SourceFingerprint != next.SourceFingerprint)
            {
                throw new RecoveryValidationException(
                    TableRuntimeReasonCode.RecoveryReplayMismatch,
                    "Event fact binding differs at version " + version + " ordinal " + ordinal + ".");
            }
        }

        private static void ValidateHistory(
            IReadOnlyList<TableEventRecord> history,
            TableCheckpoint checkpoint)
        {
            ulong expectedSeq = 1;
            ulong expectedVersion = 1;
            int expectedOrdinal = 0;
            for (int i = 0; i < history.Count; i++)
            {
                TableEventRecord record = history[i];
                if (record.Seq.Value != expectedSeq)
                {
                    throw new RecoveryValidationException(
                        TableRuntimeReasonCode.RecoveryEventGap,
                        "Checkpoint history sequence is not contiguous.");
                }

                if (record.CommandVersion.Value != expectedVersion)
                {
                    throw new RecoveryValidationException(
                        TableRuntimeReasonCode.CheckpointInvalid,
                        "Checkpoint history command versions must cover 1..checkpoint.StateVersion contiguously.");
                }

                if (record.Ordinal != expectedOrdinal)
                {
                    throw new RecoveryValidationException(
                        TableRuntimeReasonCode.RecoveryOrdinalInvalid,
                        "Checkpoint history ordinal is not contiguous.");
                }

                if (record.EventSchema != 1)
                {
                    throw new RecoveryValidationException(
                        TableRuntimeReasonCode.CheckpointInvalid,
                        "Checkpoint history uses an unsupported event schema.");
                }

                expectedOrdinal++;
                expectedSeq++;
                if (i == history.Count - 1 ||
                    history[i + 1].CommandVersion.Value != expectedVersion)
                {
                    expectedVersion++;
                    expectedOrdinal = 0;
                }
            }

            if (expectedVersion != checkpoint.StateVersion.Value + 1)
            {
                throw new RecoveryValidationException(
                    TableRuntimeReasonCode.CheckpointInvalid,
                    "Checkpoint history does not cover every command version through the checkpoint.");
            }

            ulong lastSeq = expectedSeq - 1;
            if (lastSeq != checkpoint.LastEventSeq.Value)
            {
                throw new RecoveryValidationException(
                    TableRuntimeReasonCode.RecoveryEventGap,
                    "Checkpoint history does not end at checkpoint.lastEventSeq.");
            }
        }

        private static void ValidateCommandBindings(
            IReadOnlyList<TableEventRecord> history,
            IReadOnlyList<TableCommandBinding> bindings,
            TableCheckpoint checkpoint)
        {
            if (bindings.Count != (int)checkpoint.StateVersion.Value)
            {
                throw new RecoveryValidationException(
                    TableRuntimeReasonCode.CheckpointInvalid,
                    "Command binding evidence does not cover every checkpoint command version.");
            }

            int index = 0;
            for (int i = 0; i < bindings.Count; i++)
            {
                TableCommandBinding binding = bindings[i];
                ulong expectedVersion = (ulong)(i + 1);
                if (binding.CommandVersion != expectedVersion || binding.EventCount <= 0)
                {
                    throw new RecoveryValidationException(
                        TableRuntimeReasonCode.CheckpointInvalid,
                        "Command binding versions/event counts are invalid.");
                }

                for (int ordinal = 0; ordinal < binding.EventCount; ordinal++)
                {
                    if (index >= history.Count)
                    {
                        throw new RecoveryValidationException(
                            TableRuntimeReasonCode.RecoveryEventGap,
                            "Command binding evidence is longer than checkpoint history.");
                    }

                    TableEventRecord record = history[index++];
                    if (record.CommandVersion.Value != expectedVersion ||
                        record.Ordinal != ordinal ||
                        record.Payload == null)
                    {
                        throw new RecoveryValidationException(
                            TableRuntimeReasonCode.RecoveryReplayMismatch,
                            "Checkpoint history command grouping does not match its binding evidence.");
                    }

                    TableEventFact fact = TableEventFactCodec.Decode(record.Payload);
                    if (fact.CommandVersion != record.CommandVersion ||
                        fact.Ordinal != record.Ordinal ||
                        fact.SourceFingerprint != binding.SourceFingerprint)
                    {
                        throw new RecoveryValidationException(
                            TableRuntimeReasonCode.RecoveryReplayMismatch,
                            "Checkpoint history SourceFingerprint/version binding differs from evidence at event " +
                            record.Seq + ".");
                    }
                }
            }

            if (index != history.Count)
            {
                throw new RecoveryValidationException(
                    TableRuntimeReasonCode.RecoveryEventGap,
                    "Command binding evidence is shorter than checkpoint history.");
            }
        }

        private static void ValidateCheckpointHistoryAgainstCaptured(
            IReadOnlyList<TableEventRecord> history,
            RoundRecoveryState captured,
            TableCheckpoint checkpoint)
        {
            var roundHistory = new List<TableEventRecord>(history.Count);
            for (int i = 0; i < history.Count; i++)
            {
                if (history[i].Cheat == null)
                {
                    roundHistory.Add(history[i]);
                }
                else if (history[i].Payload == null)
                {
                    throw new RecoveryValidationException(
                        TableRuntimeReasonCode.CheckpointInvalid,
                        "Committed cheat history event has no fact binding.");
                }
            }

            int offset = captured.Events.Count - roundHistory.Count;
            if (offset < 0)
            {
                throw new RecoveryValidationException(
                    TableRuntimeReasonCode.CheckpointInvalid,
                    "Committed checkpoint history is longer than the captured Domain event prefix.");
            }

            for (int i = 0; i < offset; i++)
            {
                RoundEvent prefix = captured.Events[i];
                if (prefix.Type != RoundEventType.TilesDealt &&
                    prefix.Type != RoundEventType.DoraRevealed)
                {
                    throw new RecoveryValidationException(
                        TableRuntimeReasonCode.CheckpointInvalid,
                        "Committed checkpoint history omits a non-initialization Domain event.");
                }
            }

            for (int i = 0; i < roundHistory.Count; i++)
            {
                TableEventRecord record = roundHistory[i];
                RoundEvent domain = captured.Events[offset + i];
                if (domain.Ordinal != offset + i ||
                    domain.Type != record.Type ||
                    domain.Seat != record.Seat ||
                    !Nullable.Equals(domain.Tile, record.Tile) ||
                    !string.Equals(domain.Detail, record.Detail, StringComparison.Ordinal))
                {
                    throw new RecoveryValidationException(
                        TableRuntimeReasonCode.RecoveryReplayMismatch,
                        "Checkpoint history diverges from the captured Domain event prefix at ordinal " +
                        (offset + i) + ".");
                }

                if (record.Payload != null)
                {
                    TableEventFact fact = TableEventFactCodec.Decode(record.Payload);
                    if (fact.CommandVersion != record.CommandVersion ||
                        fact.Ordinal != record.Ordinal)
                    {
                        throw new RecoveryValidationException(
                            TableRuntimeReasonCode.RecoveryReplayMismatch,
                            "Checkpoint history payload binding differs at event " + record.Seq + ".");
                    }
                }
            }
        }

        private static Dictionary<ulong, List<TableEventRecord>> ValidateSequence(
            TableCheckpoint checkpoint,
            TableStateRow state,
            IReadOnlyList<TableEventRecord> events,
            IReadOnlyList<TableCommandJournalRecord> commands)
        {
            var byVersion = new Dictionary<ulong, List<TableEventRecord>>();
            ulong expectedSeq = checkpoint.LastEventSeq.Value + 1;
            ulong previousVersion = checkpoint.StateVersion.Value;
            int eventIndex = 0;
            int commandIndex = 0;
            for (ulong version = checkpoint.StateVersion.Value + 1; version <= state.StateVersion.Value; version++)
            {
                if (commandIndex >= commands.Count ||
                    commands[commandIndex].CommandVersion.Value != version)
                {
                    throw new RecoveryValidationException(
                        TableRuntimeReasonCode.RecoveryCommandMissing,
                        "Command journal is missing version " + version + ".");
                }

                if (commands[commandIndex].EvaluatedStateVersion.Value != version - 1)
                {
                    throw new RecoveryValidationException(
                        TableRuntimeReasonCode.RecoveryCommandMissing,
                        "Command journal evaluated version is wrong at " + version + ".");
                }

                int ordinal = 0;
                var list = new List<TableEventRecord>();
                while (eventIndex < events.Count && events[eventIndex].CommandVersion.Value == version)
                {
                    TableEventRecord record = events[eventIndex];
                    if (record.Ordinal != ordinal)
                    {
                        throw new RecoveryValidationException(
                            TableRuntimeReasonCode.RecoveryOrdinalInvalid,
                            "Event ordinal is not contiguous at version " + version + ".");
                    }

                    if (record.Seq.Value != expectedSeq)
                    {
                        throw new RecoveryValidationException(
                            TableRuntimeReasonCode.RecoveryEventGap,
                            "Event sequence gap at version " + version + ".");
                    }

                    list.Add(record);
                    ordinal++;
                    eventIndex++;
                    expectedSeq++;
                }

                if (list.Count == 0)
                {
                    throw new RecoveryValidationException(
                        TableRuntimeReasonCode.RecoveryEventGap,
                        "Accepted command version " + version + " has no event.");
                }

                byVersion.Add(version, list);
                previousVersion = version;
                commandIndex++;
            }

            if (eventIndex != events.Count)
            {
                throw new RecoveryValidationException(
                    TableRuntimeReasonCode.RecoveryEventGap,
                    "Event log contains records beyond table_state.");
            }

            if (commandIndex != commands.Count)
            {
                throw new RecoveryValidationException(
                    TableRuntimeReasonCode.RecoveryCommandMissing,
                    "Command journal contains records beyond table_state.");
            }

            if (previousVersion != state.StateVersion.Value)
            {
                throw new RecoveryValidationException(
                    TableRuntimeReasonCode.RecoveryEventGap,
                    "Replay did not reach table_state version.");
            }

            if (expectedSeq - 1 != state.LastEventSeq.Value)
            {
                throw new RecoveryValidationException(
                    TableRuntimeReasonCode.RecoveryEventGap,
                    "Replay did not reach table_state event sequence.");
            }

            return byVersion;
        }

        private static TableTimingCheckpoint AdjustTiming(
            TableTimingCheckpoint timing,
            long activeDelta,
            long grace)
        {
            if (activeDelta < 0 || grace < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(activeDelta), "Timing adjustment cannot be negative.");
            }

            var decisions = new List<TableRecoveredDecision>(timing.Decisions.Count);
            for (int i = 0; i < timing.Decisions.Count; i++)
            {
                TableRecoveredDecision decision = timing.Decisions[i];
                long graceRemaining = decision.RecoveryGraceMillis;
                long baseRemaining = Math.Max(0L, decision.RemainingBaseMillis - graceRemaining);
                long reserveRemaining = decision.RemainingReserveMillis;
                long overdue = decision.OverdueMillis;
                long remainingDelta = activeDelta;
                if (remainingDelta > 0)
                {
                    long baseCut = Math.Min(baseRemaining, remainingDelta);
                    baseRemaining -= baseCut;
                    remainingDelta -= baseCut;
                    long graceCut = Math.Min(graceRemaining, remainingDelta);
                    graceRemaining -= graceCut;
                    remainingDelta -= graceCut;
                    long reserveCut = Math.Min(reserveRemaining, remainingDelta);
                    reserveRemaining -= reserveCut;
                    remainingDelta -= reserveCut;
                    overdue = checked(overdue + remainingDelta);
                }

                bool alreadyApplied = decision.RecoveryGraceApplied;
                if (!alreadyApplied && grace > 0)
                {
                    graceRemaining = checked(graceRemaining + grace);
                    alreadyApplied = true;
                }

                decisions.Add(new TableRecoveredDecision(
                    decision.Key,
                    decision.ControlEpoch,
                    decision.Candidates,
                    decision.DrawnTile,
                    checked(baseRemaining + graceRemaining),
                    reserveRemaining,
                    overdue,
                    graceRemaining,
                    alreadyApplied));
            }

            return new TableTimingCheckpoint(
                timing.RoundId,
                timing.SeatReservesMillis,
                decisions,
                timing.CapturedAtUtcMs,
                timing.CapturedMonotonicMillis,
                timing.BootId);
        }

        private static TableGraceRecoveryTiming BuildSameBootGraceTiming(
            ControlRegistrySnapshot control,
            long nowMonotonic)
        {
            var seats = new List<TableRecoveredGrace>();
            for (int seat = 0; seat < 4; seat++)
            {
                SeatControlState state = control.Seats[seat];
                if (state.Status != ConnectionStatus.Disconnected ||
                    !state.GraceDeadlineMillis.HasValue)
                {
                    continue;
                }

                long deadline = state.GraceDeadlineMillis.Value;
                long remaining = deadline > nowMonotonic ? deadline - nowMonotonic : 0L;
                long overdue = deadline <= nowMonotonic ? nowMonotonic - deadline : 0L;
                seats.Add(new TableRecoveredGrace(seat, remaining, overdue));
            }

            return new TableGraceRecoveryTiming(seats);
        }

        private static bool HasUntrustedGrace(ControlRegistrySnapshot control)
        {
            for (int seat = 0; seat < 4; seat++)
            {
                SeatControlState state = control.Seats[seat];
                if (state.Status == ConnectionStatus.Disconnected && state.GraceDeadlineMillis.HasValue)
                {
                    return true;
                }
            }

            return false;
        }

        private sealed class RecoveryValidationException : Exception
        {
            public RecoveryValidationException(string reasonCode, string message)
                : base(message)
            {
                ReasonCode = reasonCode;
            }

            public string ReasonCode { get; }
        }
    }
}
