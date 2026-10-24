using System;
using System.Collections.Generic;
using System.IO;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;

namespace Mahjong.TableRuntime
{
    /// <summary>
    /// Pure checkpoint integrity validation shared by the store's atomic save/commit paths and
    /// cold recovery. It proves that committed history is grouped by command and bound to the
    /// independent command-binding evidence before a checkpoint can replace a recovery point.
    /// It never reads the store and never derives bindings from the mutable history payload.
    /// </summary>
    public static class TableCheckpointValidator
    {
        public static bool TryValidate(
            TableCheckpoint checkpoint,
            out string reasonCode,
            out string detail)
        {
            if (checkpoint == null)
            {
                throw new ArgumentNullException(nameof(checkpoint));
            }

            try
            {
                if (checkpoint.SchemaVersion != TableCheckpointFactory.SchemaVersion)
                {
                    throw new InvalidDataException(
                        "Checkpoint schema " + checkpoint.SchemaVersion + " is not supported.");
                }

                Validate(checkpoint);
                reasonCode = string.Empty;
                detail = string.Empty;
                return true;
            }
            catch (Exception exception) when (
                exception is InvalidDataException ||
                exception is IOException ||
                exception is ArgumentException ||
                exception is InvalidOperationException ||
                exception is TableProjectionException)
            {
                reasonCode = TableRuntimeReasonCode.CheckpointInvalid;
                detail = "Checkpoint history/binding evidence is invalid: " + exception.Message;
                return false;
            }
        }

        private static void Validate(TableCheckpoint checkpoint)
        {
            IReadOnlyList<TableEventRecord> history = checkpoint.CommittedHistoryPayload == null
                ? Array.Empty<TableEventRecord>()
                : TableCommittedHistoryCodec.Decode(checkpoint.CommittedHistoryPayload);
            IReadOnlyList<TableCommandBinding> bindings = checkpoint.CommittedCommandBindingsPayload == null
                ? Array.Empty<TableCommandBinding>()
                : TableCommandBindingsCodec.Decode(checkpoint.CommittedCommandBindingsPayload);

            if (checkpoint.StateVersion.Value == 0)
            {
                if (history.Count != 0 || bindings.Count != 0 || checkpoint.LastEventSeq.Value != 0)
                {
                    throw new InvalidDataException(
                        "A zero-version checkpoint cannot carry committed history/bindings.");
                }

                ValidateSidePayloads(history, checkpoint);
                return;
            }

            if (history.Count == 0)
            {
                throw new InvalidDataException("A nonzero checkpoint has no committed history.");
            }

            if (bindings.Count != (int)checkpoint.StateVersion.Value)
            {
                throw new InvalidDataException(
                    "Command binding evidence does not cover every checkpoint command version.");
            }

            ValidateHistory(history, checkpoint);
            ValidateBindings(history, bindings);
            ValidateCapturedPrefix(history, checkpoint);
            ValidateSidePayloads(history, checkpoint);
        }

        private static void ValidateSidePayloads(
            IReadOnlyList<TableEventRecord> history,
            TableCheckpoint checkpoint)
        {
            TableControlCheckpoint control = TableControlCheckpointCodec.Decode(checkpoint.ControlPayload);
            if (control.ControlVersion != checkpoint.ControlVersion)
            {
                throw new InvalidDataException(
                    "Control payload version does not match checkpoint metadata.");
            }

            CheatState? cheat = null;
            if (checkpoint.CheatPayload != null)
            {
                TableCheatLedgerCheckpoint cheatCheckpoint =
                    TableCheatLedgerCheckpointCodec.Decode(checkpoint.CheatPayload);
                cheat = cheatCheckpoint.RestoreCheatState();
            }

            TableTimingCheckpoint timing = TableTimingCheckpointCodec.Decode(checkpoint.TimingPayload);
            RoundRecoveryState captured = RoundRecoveryCodec.Decode(checkpoint.StatePayload);
            if (timing.RoundId.Length > 0 &&
                !string.Equals(timing.RoundId, captured.Spec.RoundId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Timing checkpoint round id does not match the recovered round.");
            }

            if (checkpoint.CommittedFactsPayload == null)
            {
                return;
            }

            TableProjectionContextData facts = TableProjectionFactsCodec.Decode(
                checkpoint.CommittedFactsPayload,
                captured.Spec.RoundId,
                checkpoint.StateVersion);
            RoundStateMachine round = RoundRecovery.Restore(captured);
            TableVisibilityProjection.ValidateRecoveredFacts(round, facts, cheat);
            TableVisibilityProjection.ValidateRecoveredEventFacts(
                history,
                facts.EventFacts,
                facts.Policy,
                round);
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
                if (record.Seq.Value != expectedSeq ||
                    record.CommandVersion.Value != expectedVersion ||
                    record.Ordinal != expectedOrdinal ||
                    record.EventSchema != 1)
                {
                    throw new InvalidDataException(
                        "Committed history sequence/version/ordinal/schema is not contiguous.");
                }

                expectedSeq++;
                expectedOrdinal++;
                if (i == history.Count - 1 ||
                    history[i + 1].CommandVersion.Value != expectedVersion)
                {
                    expectedVersion++;
                    expectedOrdinal = 0;
                }
            }

            if (expectedVersion != checkpoint.StateVersion.Value + 1 ||
                expectedSeq - 1 != checkpoint.LastEventSeq.Value)
            {
                throw new InvalidDataException(
                    "Committed history does not cover the checkpoint versions/lastEventSeq.");
            }
        }

        private static void ValidateBindings(
            IReadOnlyList<TableEventRecord> history,
            IReadOnlyList<TableCommandBinding> bindings)
        {
            int index = 0;
            for (int i = 0; i < bindings.Count; i++)
            {
                TableCommandBinding binding = bindings[i];
                ulong version = (ulong)(i + 1);
                if (binding.CommandVersion != version || binding.EventCount <= 0)
                {
                    throw new InvalidDataException(
                        "Command binding versions/event counts are invalid.");
                }

                for (int ordinal = 0; ordinal < binding.EventCount; ordinal++)
                {
                    if (index >= history.Count)
                    {
                        throw new InvalidDataException(
                            "Command binding evidence is longer than committed history.");
                    }

                    TableEventRecord record = history[index++];
                    if (record.CommandVersion.Value != version ||
                        record.Ordinal != ordinal ||
                        record.Payload == null)
                    {
                        throw new InvalidDataException(
                            "Committed history command grouping does not match its binding evidence.");
                    }

                    TableEventFact fact = TableEventFactCodec.Decode(record.Payload);
                    if (fact.CommandVersion != record.CommandVersion ||
                        fact.Ordinal != record.Ordinal ||
                        fact.SourceFingerprint != binding.SourceFingerprint)
                    {
                        throw new InvalidDataException(
                            "Committed history SourceFingerprint/version binding differs from evidence.");
                    }
                }
            }

            if (index != history.Count)
            {
                throw new InvalidDataException(
                    "Command binding evidence is shorter than committed history.");
            }
        }

        private static void ValidateCapturedPrefix(
            IReadOnlyList<TableEventRecord> history,
            TableCheckpoint checkpoint)
        {
            RoundRecoveryState captured = RoundRecoveryCodec.Decode(checkpoint.StatePayload);
            int offset = captured.Events.Count - history.Count;
            if (offset < 0)
            {
                throw new InvalidDataException(
                    "Committed history is longer than the captured Domain event prefix.");
            }

            for (int i = 0; i < offset; i++)
            {
                RoundEvent prefix = captured.Events[i];
                if (prefix.Type != RoundEventType.TilesDealt &&
                    prefix.Type != RoundEventType.DoraRevealed)
                {
                    throw new InvalidDataException(
                        "Committed history omits a non-initialization Domain event.");
                }
            }

            for (int i = 0; i < history.Count; i++)
            {
                TableEventRecord record = history[i];
                RoundEvent domain = captured.Events[offset + i];
                if (domain.Type != record.Type ||
                    domain.Seat != record.Seat ||
                    !Nullable.Equals(domain.Tile, record.Tile) ||
                    !string.Equals(domain.Detail, record.Detail, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Committed history diverges from the captured Domain event prefix.");
                }
            }
        }
    }
}
