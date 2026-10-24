using System;
using System.Collections.Generic;
using System.Linq;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.TableRuntime;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    /// <summary>
    /// N-1a checkpoint contract: the existing TableCheckpointFactory/TableCheckpointValidator
    /// must persist, validate and cold-restore the new authoritative decision fields instead of
    /// silently defaulting them.
    /// </summary>
    public class RoundDecisionCheckpointTests
    {
        private const string Room = "n1a-room";
        private const string Boot = "n1a-boot";

        private static TileId Man(int rank, int copy = 0) => TileId.FromTypeIndex(rank - 1, copy);
        private static TileId Pin(int rank, int copy = 0) => TileId.FromTypeIndex(17 + rank, copy);
        private static TileId Sou(int rank, int copy = 0) => TileId.FromTypeIndex(8 + rank, copy);

        private static RoundStateMachine BuildDealt(string roundId, IReadOnlyDictionary<int, TileId> fixedSlots)
        {
            var order = new TileId[TileId.Count];
            var assigned = new bool[TileId.Count];
            var used = new bool[TileId.Count];
            foreach (KeyValuePair<int, TileId> pair in fixedSlots)
            {
                if (assigned[pair.Key])
                {
                    throw new InvalidOperationException("slot " + pair.Key + " is fixed twice");
                }

                if (used[pair.Value.Value])
                {
                    throw new InvalidOperationException("tile " + pair.Value + " is fixed twice");
                }

                order[pair.Key] = pair.Value;
                assigned[pair.Key] = true;
                used[pair.Value.Value] = true;
            }

            int cursor = 0;
            for (int slot = 0; slot < order.Length; slot++)
            {
                if (assigned[slot])
                {
                    continue;
                }

                while (cursor < TileId.Count && used[cursor])
                {
                    cursor++;
                }

                if (cursor >= TileId.Count)
                {
                    throw new InvalidOperationException("wall order ran out of physical tiles");
                }

                order[slot] = new TileId(cursor);
                used[cursor] = true;
            }

            TilePositionMap dealt = TilePositionMap.CreateDealt(
                RecordedRandomDraw.Create("n1a-" + roundId, order),
                0);
            var spec = new RoundSpec(
                roundId,
                0,
                1,
                0,
                0,
                new long[] { 25000, 25000, 25000, 25000 },
                0);
            return RoundStateMachine.CreateDealt(spec, dealt);
        }

        private static RoundStateMachine BuildChiRound()
        {
            var fixedSlots = new Dictionary<int, TileId>
            {
                [52] = Man(3, 3),
                [4] = Man(1, 1),
                [5] = Man(2, 1)
            };
            RoundStateMachine round = BuildDealt("n1a-checkpoint-chi", fixedSlots);
            RoundTransition window = round.Execute(
                RoundCommand.Discard("n1a-checkpoint-chi", 0, Man(3, 3), new[] { 1 }));
            Assert.True(window.IsAccepted, window.Error?.ToString());
            RoundTransition chi = window.State!.Execute(
                RoundCommand.Chi("n1a-checkpoint-chi", 1, Man(3, 3), Man(1, 1), Man(2, 1)));
            Assert.True(chi.IsAccepted, chi.Error?.ToString());
            return chi.State!;
        }

        private static RoundStateMachine BuildRiichiRound()
        {
            var fixedSlots = new Dictionary<int, TileId>();
            TileId[] closed =
            {
                Man(1), Man(2), Man(3), Man(4), Man(5), Man(6), Man(7), Man(8), Man(9),
                Pin(1), Pin(1, 1), Sou(5), Sou(6)
            };
            int[] slots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48 };
            for (int i = 0; i < slots.Length; i++)
            {
                fixedSlots[slots[i]] = closed[i];
            }

            fixedSlots[52] = Pin(9);
            RoundStateMachine round = BuildDealt("n1a-checkpoint-riichi", fixedSlots);
            RoundTransition riichi = round.Execute(
                RoundCommand.Riichi("n1a-checkpoint-riichi", 0, Pin(9), new[] { 1, 2, 3 }));
            Assert.True(riichi.IsAccepted, riichi.Error?.ToString());
            return riichi.State!;
        }

        private static TableCheckpoint CreateCheckpoint(RoundStateMachine round, int schemaVersion = TableCheckpointFactory.SchemaVersion)
        {
            var control = new TableControlCheckpoint(
                new ControlRegistry(
                    new DisconnectPolicy(),
                    P31Fixtures.Controllers(
                        SeatControllerKind.RemoteHuman,
                        SeatControllerKind.RemoteHuman,
                        SeatControllerKind.RemoteHuman,
                        SeatControllerKind.RemoteHuman)).Capture(),
                0L);
            var timing = new TableTimingCheckpoint(
                round.RoundId,
                new long[] { 0L, 0L, 0L, 0L },
                Array.Empty<TableRecoveredDecision>(),
                1UL,
                0L,
                Boot);
            TableCheckpoint checkpoint = TableCheckpointFactory.Create(
                Room,
                round,
                new StateVersion(0UL),
                new EventSeq(0UL),
                control,
                timing,
                1,
                Boot,
                1UL);
            if (schemaVersion == checkpoint.SchemaVersion)
            {
                return checkpoint;
            }

            return new TableCheckpoint(
                checkpoint.RoomId,
                checkpoint.StateVersion,
                checkpoint.LastEventSeq,
                checkpoint.StateFingerprint,
                checkpoint.RulesetVersion,
                checkpoint.StatePayload,
                checkpoint.StatePayloadSha256,
                checkpoint.ControlVersion,
                checkpoint.ControlPayload,
                checkpoint.ControlPayloadSha256,
                checkpoint.TimingPayload,
                checkpoint.TimingPayloadSha256,
                checkpoint.CheatPayload,
                checkpoint.CheatPayloadSha256,
                checkpoint.CommittedFactsPayload,
                checkpoint.CommittedFactsPayloadSha256,
                checkpoint.CreatedAtUtcMs,
                checkpoint.BootId,
                schemaVersion,
                checkpoint.TimingVersion,
                checkpoint.CapturedMonotonicMillis,
                checkpoint.CommittedHistoryPayload,
                checkpoint.CommittedHistoryPayloadSha256,
                checkpoint.CommittedCommandBindingsPayload,
                checkpoint.CommittedCommandBindingsPayloadSha256);
        }

        private static TableCheckpoint WithStatePayload(TableCheckpoint good, byte[] payload)
        {
            return new TableCheckpoint(
                good.RoomId,
                good.StateVersion,
                good.LastEventSeq,
                good.StateFingerprint,
                good.RulesetVersion,
                payload,
                TablePayloadSha256.Compute(payload),
                good.ControlVersion,
                good.ControlPayload,
                good.ControlPayloadSha256,
                good.TimingPayload,
                good.TimingPayloadSha256,
                good.CheatPayload,
                good.CheatPayloadSha256,
                good.CommittedFactsPayload,
                good.CommittedFactsPayloadSha256,
                good.CreatedAtUtcMs,
                good.BootId,
                good.SchemaVersion,
                good.TimingVersion,
                good.CapturedMonotonicMillis,
                good.CommittedHistoryPayload,
                good.CommittedHistoryPayloadSha256,
                good.CommittedCommandBindingsPayload,
                good.CommittedCommandBindingsPayloadSha256);
        }

        [Fact]
        public void ChiStateRoundTripsThroughCheckpointAndColdRecovery()
        {
            RoundStateMachine round = BuildChiRound();
            TableCheckpoint checkpoint = CreateCheckpoint(round);

            Assert.True(
                TableCheckpointValidator.TryValidate(checkpoint, out string reason, out string detail),
                reason + ": " + detail);
            RoundRecoveryState captured = RoundRecoveryCodec.Decode(checkpoint.StatePayload);
            RoundStateMachine restored = RoundRecovery.Restore(captured);

            Assert.Equal(round.Version, restored.Version);
            Assert.Equal(round.SourceFingerprint, restored.SourceFingerprint);
            Assert.Equal(round.Tiles.StateHash, restored.Tiles.StateHash);
            Assert.Single(restored.Melds);
            Assert.Equal(RoundMeldKind.Chi, restored.Melds[0].Kind);
            Assert.Equal(round.Scores.ToArray(), restored.Scores.ToArray());
            Assert.Equal(0, restored.RiichiSticks);
            Assert.Equal(0, restored.PendingKanDoraCount);
            Assert.Equal(
                checkpoint.StatePayloadSha256,
                TablePayloadSha256.Compute(RoundRecoveryCodec.Encode(RoundRecovery.Capture(restored))));
        }

        [Fact]
        public void RiichiStateRoundTripsScoresSticksFlagsAndEvents()
        {
            RoundStateMachine round = BuildRiichiRound();
            TableCheckpoint checkpoint = CreateCheckpoint(round);

            Assert.True(
                TableCheckpointValidator.TryValidate(checkpoint, out string reason, out string detail),
                reason + ": " + detail);
            RoundRecoveryState captured = RoundRecoveryCodec.Decode(checkpoint.StatePayload);
            RoundStateMachine restored = RoundRecovery.Restore(captured);

            Assert.Equal(24000L, restored.Scores[0]);
            Assert.Equal(1, restored.RiichiSticks);
            Assert.True(restored.RiichiDeclared[0]);
            Assert.Equal(round.SourceFingerprint, restored.SourceFingerprint);
            Assert.Contains(restored.Events, e => e.Type == RoundEventType.RiichiDeclared && e.Seat == 0);
        }

        [Fact]
        public void CorruptedDecisionFieldAndSchemaOneAreRefused()
        {
            RoundStateMachine round = BuildChiRound();
            TableCheckpoint good = CreateCheckpoint(round);
            RoundRecoveryState captured = RoundRecoveryCodec.Decode(good.StatePayload);
            var corruptedState = new RoundRecoveryState(
                captured.Spec,
                captured.Version,
                captured.Phase,
                captured.CurrentSeat,
                captured.Tiles,
                captured.Window,
                captured.Result,
                captured.Events,
                captured.SourceFingerprint,
                captured.Scores,
                captured.RiichiSticks + 1,
                captured.RiichiDeclared,
                captured.Melds,
                captured.PendingKanDoraCount);
            TableCheckpoint corrupted = WithStatePayload(good, RoundRecoveryCodec.Encode(corruptedState));

            Assert.False(TableCheckpointValidator.TryValidate(corrupted, out string reason, out _));
            Assert.Equal(TableRuntimeReasonCode.CheckpointInvalid, reason);

            TableCheckpoint schemaOne = CreateCheckpoint(round, schemaVersion: 1);
            Assert.False(TableCheckpointValidator.TryValidate(schemaOne, out string schemaReason, out _));
            Assert.Equal(TableRuntimeReasonCode.CheckpointInvalid, schemaReason);
        }
    }
}
