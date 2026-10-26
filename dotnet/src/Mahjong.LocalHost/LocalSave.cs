using System;
using System.Collections.Generic;
using Mahjong.Contracts;
using Mahjong.TableRuntime;

namespace Mahjong.LocalHost
{
    /// <summary>Terminal position of one persisted local table.</summary>
    public enum LocalSaveTerminalState : byte
    {
        InProgress = 0,
        RoundFinished = 1,
        MatchFinished = 2,
        Abandoned = 3
    }

    /// <summary>Origin of every fact in this save. Local play never promotes REMOTE facts.</summary>
    public enum LocalSaveOrigin : byte
    {
        Local = 0,
        Remote = 1
    }

    /// <summary>
    /// Canonical state row. It is the only owner of state/control/timing/cheat versions in the
    /// envelope; the outer container never repeats those fields.
    /// </summary>
    public sealed class LocalSaveState
    {
        public StateVersion StateVersion { get; set; }

        public EventSeq LastEventSeq { get; set; }

        public ulong StateFingerprint { get; set; }

        public TablePayloadSha256 StateHash { get; set; }

        public long ControlVersion { get; set; }

        public ulong ControlEpoch { get; set; }

        public long TimingVersion { get; set; }

        public long CheatVersion { get; set; }

        public ulong CheatFingerprint { get; set; }

        public bool CheatBound { get; set; }

        public static LocalSaveState FromStateRow(TableStateRow row)
        {
            if (row == null)
            {
                throw new ArgumentNullException(nameof(row));
            }

            return new LocalSaveState
            {
                StateVersion = row.StateVersion,
                LastEventSeq = row.LastEventSeq,
                StateFingerprint = row.StateFingerprint,
                StateHash = row.StateHash,
                ControlVersion = row.ControlVersion,
                ControlEpoch = 0UL,
                TimingVersion = row.TimingVersion,
                CheatVersion = row.CheatVersion,
                CheatFingerprint = row.CheatFingerprint,
                CheatBound = row.CheatBound
            };
        }

        public TableStateRow ToStateRow(string roomId, int rulesetVersion, ulong updatedAtUtcMs)
        {
            return new TableStateRow(
                roomId,
                StateVersion,
                LastEventSeq,
                StateFingerprint,
                StateHash,
                rulesetVersion,
                ControlVersion,
                updatedAtUtcMs,
                TimingVersion,
                CheatVersion,
                CheatFingerprint,
                CheatBound);
        }
    }

    /// <summary>
    /// Complete local save envelope. It is serialized as one file and replaced atomically; the
    /// checkpoint is the base and the journals/operations are the replayable tail.
    /// </summary>
    public sealed class LocalSave
    {
        public int FileFormatVersion { get; set; } = 1;
        public string RoomId { get; set; } = string.Empty;
        public LocalSaveOrigin Origin { get; set; } = LocalSaveOrigin.Local;
        public int RulesetVersion { get; set; } = 1;

        public LocalSaveState CurrentState { get; set; } = new LocalSaveState();

        public byte[] BaseCheckpointBlob { get; set; } = Array.Empty<byte>();
        public TablePayloadSha256 CheckpointSha256 { get; set; }

        public byte[] LatestTimingPayload { get; set; } = Array.Empty<byte>();
        public TablePayloadSha256 LatestTimingPayloadSha256 { get; set; }

        public byte[]? LatestCommittedFactsPayload { get; set; }
        public TablePayloadSha256? LatestCommittedFactsPayloadSha256 { get; set; }

        public byte[]? CurrentControlSnapshotPayload { get; set; }
        public TablePayloadSha256? CurrentControlSnapshotSha256 { get; set; }

        public List<TableCommandJournalRecord> CommandJournals { get; set; } = new List<TableCommandJournalRecord>();
        public List<TableEventRecord> EventJournals { get; set; } = new List<TableEventRecord>();
        public Dictionary<string, TableDurableReceipt> Receipts { get; set; } =
            new Dictionary<string, TableDurableReceipt>(StringComparer.Ordinal);
        public Dictionary<string, TableSettlementOutboxRecord> Outbox { get; set; } =
            new Dictionary<string, TableSettlementOutboxRecord>(StringComparer.Ordinal);

        public LocalSaveTerminalState TerminalState { get; set; }
        public string? TerminalResultId { get; set; }
        public int? TerminalResultVersion { get; set; }

        /// <summary>
        /// Ordered durable operations after <see cref="BaseCheckpointBlob"/>. They let a cold
        /// start rebuild the accepted in-memory store with the same validated calls instead of
        /// re-inventing transaction rules. Every journal above has a matching operation.
        /// </summary>
        public List<LocalDurableOperation> Operations { get; set; } = new List<LocalDurableOperation>();

        /// <summary>Durable recovery audit trail (not used to reconstruct authority).</summary>
        public List<TableRecoveryAuditRecord> RecoveryAudits { get; set; } = new List<TableRecoveryAuditRecord>();

        public LocalSaveState CloneState()
        {
            return new LocalSaveState
            {
                StateVersion = CurrentState.StateVersion,
                LastEventSeq = CurrentState.LastEventSeq,
                StateFingerprint = CurrentState.StateFingerprint,
                StateHash = CurrentState.StateHash,
                ControlVersion = CurrentState.ControlVersion,
                ControlEpoch = CurrentState.ControlEpoch,
                TimingVersion = CurrentState.TimingVersion,
                CheatVersion = CurrentState.CheatVersion,
                CheatFingerprint = CurrentState.CheatFingerprint,
                CheatBound = CurrentState.CheatBound
            };
        }

        public LocalSave DeepCloneForCandidate()
        {
            var copy = new LocalSave
            {
                FileFormatVersion = FileFormatVersion,
                RoomId = RoomId,
                Origin = Origin,
                RulesetVersion = RulesetVersion,
                CurrentState = CloneState(),
                BaseCheckpointBlob = (byte[])BaseCheckpointBlob.Clone(),
                CheckpointSha256 = CheckpointSha256,
                LatestTimingPayload = (byte[])LatestTimingPayload.Clone(),
                LatestTimingPayloadSha256 = LatestTimingPayloadSha256,
                LatestCommittedFactsPayload = LatestCommittedFactsPayload == null
                    ? null
                    : (byte[])LatestCommittedFactsPayload.Clone(),
                LatestCommittedFactsPayloadSha256 = LatestCommittedFactsPayloadSha256,
                CurrentControlSnapshotPayload = CurrentControlSnapshotPayload == null
                    ? null
                    : (byte[])CurrentControlSnapshotPayload.Clone(),
                CurrentControlSnapshotSha256 = CurrentControlSnapshotSha256,
                TerminalState = TerminalState,
                TerminalResultId = TerminalResultId,
                TerminalResultVersion = TerminalResultVersion
            };

            copy.CommandJournals.AddRange(CommandJournals);
            copy.EventJournals.AddRange(EventJournals);
            foreach (KeyValuePair<string, TableDurableReceipt> pair in Receipts)
            {
                copy.Receipts.Add(pair.Key, pair.Value);
            }

            foreach (KeyValuePair<string, TableSettlementOutboxRecord> pair in Outbox)
            {
                copy.Outbox.Add(pair.Key, pair.Value);
            }

            copy.Operations.AddRange(Operations);
            copy.RecoveryAudits.AddRange(RecoveryAudits);
            return copy;
        }
    }

    /// <summary>Kinds of durable calls that must be replayed into the accepted store.</summary>
    public enum LocalOperationKind : byte
    {
        Commit = 0,
        SaveCheckpoint = 1,
        CommitControl = 2,
        AppendTiming = 3,
        AppendAudit = 4
    }

    /// <summary>
    /// One exact durable call, stored after commit so cold start can re-apply the same accepted
    /// primitives. It is evidence, never a second source of authority.
    /// </summary>
    public sealed class LocalDurableOperation
    {
        public LocalOperationKind Kind { get; set; }
        public string OwnerInstance { get; set; } = string.Empty;
        public ulong ExpectedOwnerEpoch { get; set; }
        public ulong ExpectedStateVersion { get; set; }
        public long ExpectedControlVersion { get; set; }
        public long ExpectedTimingVersion { get; set; }

        public TableDurableReceipt? Receipt { get; set; }
        public bool InternalCommand { get; set; }
        public List<TableEventRecord> Events { get; set; } = new List<TableEventRecord>();
        public TableCommandJournalRecord? Journal { get; set; }
        public List<TableSettlementOutboxRecord> Outbox { get; set; } = new List<TableSettlementOutboxRecord>();
        public byte[]? CheckpointBlob { get; set; }
        public byte[]? NextStatePayload { get; set; }
        public ulong NextStateFingerprint { get; set; }
        public bool Terminal { get; set; }
        public byte[]? TimingPayload { get; set; }
        public long TimingVersion { get; set; }
        public byte[]? CommittedFactsPayload { get; set; }
        public TablePayloadSha256? CommittedFactsPayloadSha256 { get; set; }
        public long NextCheatVersion { get; set; }
        public ulong NextCheatFingerprint { get; set; }

        public string? RoundId { get; set; }
        public TableControlEvent? ControlChange { get; set; }
        public TableCheckpointFence? Fence { get; set; }

        public TableTimingJournalRecord? TimingRecord { get; set; }
        public TableRecoveryAuditRecord? Audit { get; set; }
    }
}
