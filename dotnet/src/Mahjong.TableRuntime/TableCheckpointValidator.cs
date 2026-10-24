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
            ValidateRoundDecisionState(captured);
            if (timing.CapturedAtUtcMs != checkpoint.CreatedAtUtcMs ||
                timing.CapturedMonotonicMillis != checkpoint.CapturedMonotonicMillis ||
                !string.Equals(timing.BootId, checkpoint.BootId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Timing payload boot/UTC/monotonic origin does not match checkpoint metadata.");
            }

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

        /// <summary>
        /// Verifies the N-1a authoritative decision fields carried inside the recovery payload:
        /// current scores/riichi sticks conserve the round-start total, flags have matching
        /// declared events, meld records point at their own physical meld zone, and no physical
        /// tile is claimed by two melds. A schema-1 payload has already been refused by the codec.
        /// </summary>
        private static void ValidateRoundDecisionState(RoundRecoveryState captured)
        {
            if (captured.Scores.Count != 4 || captured.RiichiDeclared.Count != 4)
            {
                throw new InvalidDataException(
                    "Checkpoint decision state does not contain four scores and four riichi flags.");
            }

            long startingTotal = 0;
            long currentTotal = 0;
            for (int i = 0; i < 4; i++)
            {
                startingTotal += captured.Spec.Scores[i];
                currentTotal += captured.Scores[i];
            }

            startingTotal += (long)captured.Spec.RiichiSticks * 1000L;
            currentTotal += (long)captured.RiichiSticks * 1000L;
            if (startingTotal != currentTotal)
            {
                throw new InvalidDataException(
                    "Checkpoint scores/riichi sticks do not conserve the round-start total.");
            }

            if (captured.RiichiSticks < 0 ||
                captured.PendingKanDoraCount < 0 ||
                captured.PendingKanDoraCount > 5)
            {
                throw new InvalidDataException("Checkpoint riichi-stick/pending-dora value is invalid.");
            }

            var seenTiles = new HashSet<int>();
            var perSeat = new int[4];
            for (int i = 0; i < captured.Melds.Count; i++)
            {
                RoundMeld meld = captured.Melds[i];
                if (meld.Seat < 0 || meld.Seat > 3 || ++perSeat[meld.Seat] > 4)
                {
                    throw new InvalidDataException("Checkpoint meld seat/count is invalid.");
                }

                bool hasEvent = false;
                for (int e = 0; e < captured.Events.Count; e++)
                {
                    RoundEvent value = captured.Events[e];
                    if (value.Seat == meld.Seat &&
                        (value.Type == RoundEventType.ChiDeclared ||
                         value.Type == RoundEventType.PonDeclared ||
                         value.Type == RoundEventType.KanDeclared))
                    {
                        hasEvent = true;
                        break;
                    }
                }

                if (!hasEvent)
                {
                    throw new InvalidDataException("Checkpoint meld has no matching declared event.");
                }

                for (int t = 0; t < meld.Tiles.Count; t++)
                {
                    TileId tile = meld.Tiles[t];
                    if (!seenTiles.Add(tile.Value))
                    {
                        throw new InvalidDataException("Checkpoint physical tile belongs to two melds.");
                    }

                    TilePosition position = captured.Tiles[tile];
                    if (position.Zone != TileZone.Meld || position.Seat != meld.Seat)
                    {
                        throw new InvalidDataException(
                            "Checkpoint meld tile is not in its owner's meld zone.");
                    }
                }
            }

            for (int seat = 0; seat < 4; seat++)
            {
                if (!captured.RiichiDeclared[seat])
                {
                    continue;
                }

                bool declared = false;
                for (int e = 0; e < captured.Events.Count; e++)
                {
                    if (captured.Events[e].Type == RoundEventType.RiichiDeclared &&
                        captured.Events[e].Seat == seat)
                    {
                        declared = true;
                        break;
                    }
                }

                if (!declared)
                {
                    throw new InvalidDataException("Checkpoint riichi flag has no RiichiDeclared event.");
                }
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
