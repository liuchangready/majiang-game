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
            bool requireCommittedFacts = true)
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
        }

        public string OwnerInstance { get; }

        public string BootId { get; }

        public int RulesetVersion { get; }

        public TimeSpan LeaseTtl { get; }

        public long GraceMillis { get; }

        public string? MatchId { get; }

        public bool RequireCheatLedger { get; }

        public bool RequireCommittedFacts { get; }
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
            TableCheatLedgerCheckpoint? cheat = null;
            CheatState? recoveredCheat = null;
            IReadOnlyList<TableEventRecord> checkpointHistory = Array.Empty<TableEventRecord>();
            try
            {
                if (TablePayloadSha256.Compute(checkpoint.ControlPayload) != checkpoint.ControlPayloadSha256 ||
                    TablePayloadSha256.Compute(checkpoint.TimingPayload) != checkpoint.TimingPayloadSha256 ||
                    (checkpoint.CheatPayload != null &&
                     TablePayloadSha256.Compute(checkpoint.CheatPayload) != checkpoint.CheatPayloadSha256) ||
                    (checkpoint.CommittedFactsPayload != null &&
                     TablePayloadSha256.Compute(checkpoint.CommittedFactsPayload) !=
                     checkpoint.CommittedFactsPayloadSha256))
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

                ValidateHistory(checkpointHistory, checkpoint);
                if (checkpoint.CheatPayload != null)
                {
                    cheat = TableCheatLedgerCheckpointCodec.Decode(checkpoint.CheatPayload);
                    recoveredCheat = cheat.RestoreCheatState();
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
            try
            {
                for (ulong version = checkpoint.StateVersion.Value + 1; version <= state.StateVersion.Value; version++)
                {
                    TableCommandJournalRecord journal = commands[(int)(version - checkpoint.StateVersion.Value - 1)];
                    if (journal.SchemaVersion != RoundCommandCodec.FormatVersion)
                    {
                        return TableRecoveryResult.Failed(
                            TableRuntimeReasonCode.RecoveryCommandMissing,
                            "Command journal version " + version + " has unsupported schema " + journal.SchemaVersion + ".");
                    }

                    RoundCommand command = RoundCommandCodec.Decode(journal.CommandPayload);
                    if (command.Action != RoundActionKind.DrawTile)
                    {
                        consumedSeats.Add(command.Seat);
                    }

                    RoundTransition transition = round.Execute(command);
                    if (!transition.IsAccepted || transition.State == null)
                    {
                        return TableRecoveryResult.Failed(
                            TableRuntimeReasonCode.RecoveryReplayMismatch,
                            "Command version " + version + " no longer replays: " +
                            (transition.Error?.Code.ToString() ?? "unknown"));
                    }

                    RoundStateMachine next = transition.State;
                    if (next.Version != round.Version + 1)
                    {
                        return TableRecoveryResult.Failed(
                            TableRuntimeReasonCode.DomainVersionMismatch,
                            "Replayed Domain version did not advance by one at command " + version + ".");
                    }

                    List<TableEventRecord> committed = eventsByVersion[version];
                    if (transition.Events.Count != committed.Count)
                    {
                        return TableRecoveryResult.Failed(
                            TableRuntimeReasonCode.RecoveryReplayMismatch,
                            "Replayed event count differs at command " + version + ".");
                    }

                    for (int i = 0; i < committed.Count; i++)
                    {
                        RoundEvent domain = transition.Events[i];
                        TableEventRecord stored = committed[i];
                        if (stored.EventSchema != 1)
                        {
                            return TableRecoveryResult.Failed(
                                TableRuntimeReasonCode.RecoveryReplayMismatch,
                                "Event schema " + stored.EventSchema + " is unsupported at version " + version + ".");
                        }
                        if (domain.Type != stored.Type ||
                            domain.Ordinal != round.Events.Count + i ||
                            domain.Seat != stored.Seat ||
                            !Nullable.Equals(domain.Tile, stored.Tile) ||
                            !string.Equals(domain.Detail, stored.Detail, StringComparison.Ordinal))
                        {
                            return TableRecoveryResult.Failed(
                                TableRuntimeReasonCode.RecoveryReplayMismatch,
                                "Replayed event identity differs at version " + version + " ordinal " + i +
                                ": expected " + stored.Type + "/" + stored.Seat + "/" + stored.Tile +
                                "/" + stored.Detail + ", actual " + domain.Type + "/" + domain.Seat + "/" +
                                domain.Tile + "/" + domain.Detail + ".");
                        }

                        if (stored.Payload != null)
                        {
                            TableEventFact fact = TableEventFactCodec.Decode(stored.Payload);
                            if (fact.CommandVersion != stored.CommandVersion ||
                                fact.Ordinal != stored.Ordinal ||
                                fact.SourceFingerprint != next.SourceFingerprint)
                            {
                                return TableRecoveryResult.Failed(
                                    TableRuntimeReasonCode.RecoveryReplayMismatch,
                                    "Event fact binding differs at version " + version + " ordinal " + i + ".");
                            }
                        }
                    }

                    round = next;
                    replayedEvents += committed.Count;
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

            if (effectiveFacts != null)
            {
                try
                {
                    TableVisibilityProjection.ValidateRecoveredFacts(round, effectiveFacts, recoveredCheat);
                    TableVisibilityProjection.ValidateRecoveredEventFacts(
                        fullHistory,
                        effectiveFacts.EventFacts,
                        effectiveFacts.Policy);
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
                finalState.TimingVersion != expectedTimingVersion)
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
                new TableControlRecoveryBaseline(control.Snapshot, control.ControlVersion),
                adjustedTiming.ToRecoveryTiming(),
                effectiveTimingVersion,
                recoveredCheat,
                effectiveFacts,
                fullHistory,
                strictHistory: true);

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
                cheat?.Entries.Count,
                round.IsFinished,
                outboxPresent,
                audit,
                effectiveTimingVersion,
                recoveredCheat != null,
                fullHistory.Count,
                outboxPresent);

            return TableRecoveryResult.Success(baseline, report);
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

        private static void ValidateHistory(
            IReadOnlyList<TableEventRecord> history,
            TableCheckpoint checkpoint)
        {
            ulong expectedSeq = 1;
            ulong currentVersion = 0;
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

                if (record.CommandVersion.Value > checkpoint.StateVersion.Value ||
                    record.CommandVersion.Value == 0)
                {
                    throw new RecoveryValidationException(
                        TableRuntimeReasonCode.CheckpointInvalid,
                        "Checkpoint history references a version beyond the checkpoint.");
                }

                if (record.CommandVersion.Value != currentVersion)
                {
                    currentVersion = record.CommandVersion.Value;
                    expectedOrdinal = 0;
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
            }

            ulong lastSeq = expectedSeq - 1;
            if (lastSeq != checkpoint.LastEventSeq.Value)
            {
                throw new RecoveryValidationException(
                    TableRuntimeReasonCode.RecoveryEventGap,
                    "Checkpoint history does not end at checkpoint.lastEventSeq.");
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
