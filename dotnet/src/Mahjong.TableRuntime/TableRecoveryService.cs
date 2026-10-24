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
            bool requireCheatLedger = true)
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
        }

        public string OwnerInstance { get; }

        public string BootId { get; }

        public int RulesetVersion { get; }

        public TimeSpan LeaseTtl { get; }

        public long GraceMillis { get; }

        public string? MatchId { get; }

        public bool RequireCheatLedger { get; }
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
            TableRecoveryAuditRecord audit)
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
                if (checkpoint.CheatPayload != null)
                {
                    cheat = TableCheatLedgerCheckpointCodec.Decode(checkpoint.CheatPayload);
                }
                else if (options.RequireCheatLedger)
                {
                    return TableRecoveryResult.Failed(
                        TableRuntimeReasonCode.CheckpointInvalid,
                        "Cheat ledger checkpoint is required but missing.");
                }
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

                outboxPresent = true;
            }

            if (round.IsFinished)
            {
                consumedSeats.UnionWith(new[] { 0, 1, 2, 3 });
            }

            ulong nowUtc = (ulong)_clock.UtcNowMs;
            long downtime = options.BootId == checkpoint.BootId
                ? 0L
                : Math.Max(0L, (long)nowUtc - (long)checkpoint.CreatedAtUtcMs);
            long adjustedGrace = options.BootId == checkpoint.BootId ? 0L : options.GraceMillis;
            TableTimingCheckpoint adjustedTiming;
            try
            {
                var surviving = new List<TableRecoveredDecision>();
                for (int i = 0; i < timing.Decisions.Count; i++)
                {
                    if (!consumedSeats.Contains(timing.Decisions[i].Key.Seat))
                    {
                        surviving.Add(timing.Decisions[i]);
                    }
                }

                adjustedTiming = AdjustTiming(
                    new TableTimingCheckpoint(timing.RoundId, timing.SeatReservesMillis, surviving),
                    downtime,
                    adjustedGrace);
            }
            catch (Exception exception) when (
                exception is ArgumentException ||
                exception is InvalidOperationException)
            {
                return TableRecoveryResult.Failed(
                    TableRuntimeReasonCode.RecoveryTimingInvalid,
                    exception.Message);
            }

            if (adjustedGrace > 0 && HasUntrustedGrace(control.Snapshot))
            {
                return TableRecoveryResult.Failed(
                    TableRuntimeReasonCode.RecoveryGraceTimingRequired,
                    "A cross-boot recovery cannot trust persisted monotonic grace deadlines.");
            }

            var audit = new TableRecoveryAuditRecord(
                roomId,
                TableRecoveryAuditKind.RecoveryAdjusted,
                nowUtc,
                checkpoint.BootId,
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
                adjustedTiming.ToRecoveryTiming());

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
                checkpoint.BootId,
                options.BootId,
                previousLease?.OwnerEpoch ?? new OwnerEpoch(0),
                lease.OwnerEpoch,
                (int)control.ControlVersion,
                adjustedTiming.Decisions.Count,
                cheat?.Entries.Count,
                round.IsFinished,
                outboxPresent,
                audit);

            return TableRecoveryResult.Success(baseline, report);
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
            long downtime,
            long grace)
        {
            var decisions = new List<TableRecoveredDecision>(timing.Decisions.Count);
            for (int i = 0; i < timing.Decisions.Count; i++)
            {
                TableRecoveredDecision decision = timing.Decisions[i];
                long baseRemaining = checked(decision.RemainingBaseMillis + downtime + grace);
                decisions.Add(new TableRecoveredDecision(
                    decision.Key,
                    decision.ControlEpoch,
                    decision.Candidates,
                    decision.DrawnTile,
                    baseRemaining,
                    decision.RemainingReserveMillis,
                    decision.OverdueMillis));
            }

            return new TableTimingCheckpoint(timing.RoundId, timing.SeatReservesMillis, decisions);
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
