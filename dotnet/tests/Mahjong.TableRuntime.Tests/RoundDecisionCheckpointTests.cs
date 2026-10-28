using System.IO;
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
                1L,
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
        [Fact]
        public void KanAndRinshanRecoveryValidatesDomainEventFactsAndRejectsTamperedWall()
        {
            var fixedSlots = new Dictionary<int, TileId>
            {
                [0] = Man(1, 0),
                [1] = Man(1, 1),
                [2] = Man(1, 2),
                [3] = Man(1, 3),
                [52] = Man(9, 0)
            };
            RoundStateMachine round = BuildDealt("checkpoint-kan-rinshan", fixedSlots);
            RoundTransition kan = round.Execute(
                RoundCommand.ClosedKan("checkpoint-kan-rinshan", 0, Man(1, 0), Man(1, 1), Man(1, 2), Man(1, 3)));
            Assert.True(kan.IsAccepted, kan.Error?.ToString());
            round = kan.State!;

            RoundTransition discard = round.Execute(
                RoundCommand.Discard("checkpoint-kan-rinshan", 0, Man(9, 0), Array.Empty<int>()));
            Assert.True(discard.IsAccepted, discard.Error?.ToString());
            round = discard.State!;

            Assert.Contains(round.Events, e => e.Type == RoundEventType.KanDeclared);
            Assert.Contains(round.Events, e => e.Type == RoundEventType.RinshanDrawn);
            Assert.Contains(round.Events, e => e.Type == RoundEventType.TileDrawn);

            var history = new List<TableEventRecord>();
            for (int i = 0; i < round.Events.Count; i++)
            {
                RoundEvent e = round.Events[i];
                history.Add(new TableEventRecord(
                    new EventSeq((ulong)(i + 1)),
                    new StateVersion(1),
                    i,
                    e.Type,
                    e.Seat,
                    e.Tile,
                    e.Detail));
            }

            var request = new TableProjectionContextRequest(
                Room,
                round,
                new StateVersion(1),
                new EventSeq((ulong)history.Count),
                new OwnerEpoch(1),
                0L,
                0L,
                0L,
                1L,
                history);
            var kanMeld = new MeldView(
                ProtocolMeldType.ClosedKan,
                new uint[] { 0, 1, 2, 3 },
                0u,
                0);
            IReadOnlyList<TableProjectionEventFact> eventFacts = WireEventFacts.Build(
                request,
                new WireEventFactOptions { KanMeld = kanMeld });
            var policy = new TableProjectionPolicy(false, settlementBatch: true, revealSettlementUra: true);

            // 1. Positive validation: Domain reverse reconstruction with Kan and Rinshan events succeeds
            TableVisibilityProjection.ValidateRecoveredEventFacts(history, eventFacts, policy, round);

            // 2. Negative validation: tamper WallRemaining on a TileDrawn fact, ensuring tampered wall is rejected
            TableProjectionEventFact[] tamperedFacts = eventFacts.ToArray();
            int drawIdx = Array.FindIndex(tamperedFacts, f => f.Type == ProtocolEventType.TileDrawn);
            Assert.True(drawIdx >= 0);
            TableProjectionEventFact origDraw = tamperedFacts[drawIdx];
            tamperedFacts[drawIdx] = new TableProjectionEventFact(
                origDraw.Type,
                origDraw.EventSeq,
                origDraw.CommandVersion,
                origDraw.EventOrdinal,
                new TableProjectionEventFacts(turnNumber: origDraw.Facts.TurnNumber, wallRemaining: 999U));

            var ex = Assert.Throws<TableProjectionException>(() =>
                TableVisibilityProjection.ValidateRecoveredEventFacts(history, tamperedFacts, policy, round));
            Assert.Contains("wallRemaining", ex.Message);

            // 3. Checkpoint validation level: verify TableCheckpointValidator rejects tampered checkpoint
            TableCheckpoint goodCheckpoint = CreateCheckpoint(round);
            Assert.True(TableCheckpointValidator.TryValidate(goodCheckpoint, out string goodReason, out _), goodReason);

            RoundRecoveryState captured = RoundRecoveryCodec.Decode(goodCheckpoint.StatePayload);
            var tamperedState = new RoundRecoveryState(
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
            TableCheckpoint corrupted = WithStatePayload(goodCheckpoint, RoundRecoveryCodec.Encode(tamperedState));
            Assert.False(TableCheckpointValidator.TryValidate(corrupted, out string reason, out _));
            Assert.Equal(TableRuntimeReasonCode.CheckpointInvalid, reason);
        }

        [Fact]
        public void TableTimingCheckpointCodec_AllCandidateKindsRoundtrip()
        {
            var candidates = new List<LegalActionOption>
            {
                new LegalActionOption(LegalActionKind.Discard, 0, new TileId(1), "discard-1", new[] { new TileId(1) }),
                new LegalActionOption(LegalActionKind.Tsumo, 0, new TileId(2), "tsumo", new[] { new TileId(2) }),
                new LegalActionOption(LegalActionKind.Chi, 0, new TileId(8), "chi-0-4", new[] { new TileId(8), new TileId(0), new TileId(4) }),
                new LegalActionOption(LegalActionKind.Pon, 0, new TileId(12), "pon-13-14", new[] { new TileId(12), new TileId(13), new TileId(14) }),
                new LegalActionOption(LegalActionKind.Kan, 0, new TileId(20), "open-kan-20", new[] { new TileId(20), new TileId(21), new TileId(22), new TileId(23) }),
                new LegalActionOption(LegalActionKind.Kan, 0, null, "closed-kan-1m", new[] { new TileId(0), new TileId(1), new TileId(2), new TileId(3) }),
                new LegalActionOption(LegalActionKind.Kan, 0, new TileId(0), "added-kan-1m", new[] { new TileId(3), new TileId(0), new TileId(1), new TileId(2) }),
                new LegalActionOption(LegalActionKind.Riichi, 0, new TileId(132), "riichi-132", new[] { new TileId(132) }),
                new LegalActionOption(LegalActionKind.Pass, 0, null, "pass", Array.Empty<TileId>()),
                new LegalActionOption(LegalActionKind.Ron, 0, new TileId(5), "ron-5", new[] { new TileId(5) })
            };

            var decision = new TableRecoveredDecision(
                new DecisionKey("round-all-kinds", 0, "dec-1"),
                42L,
                candidates,
                new TileId(1),
                3000L,
                20000L,
                0L,
                500L,
                false);

            var checkpoint = new TableTimingCheckpoint(
                "round-all-kinds",
                new long[] { 25000L, 25000L, 25000L, 25000L },
                new[] { decision },
                123456789UL,
                987654321L,
                "boot-all-kinds");

            byte[] bytes = TableTimingCheckpointCodec.Encode(checkpoint);
            TableTimingCheckpoint recovered = TableTimingCheckpointCodec.Decode(bytes);

            Assert.Equal(checkpoint.RoundId, recovered.RoundId);
            Assert.Equal(checkpoint.BootId, recovered.BootId);
            Assert.Equal(checkpoint.CapturedAtUtcMs, recovered.CapturedAtUtcMs);
            Assert.Equal(checkpoint.CapturedMonotonicMillis, recovered.CapturedMonotonicMillis);
            Assert.Single(recovered.Decisions);

            TableRecoveredDecision recDec = recovered.Decisions[0];
            Assert.Equal(decision.Key, recDec.Key);
            Assert.Equal(decision.ControlEpoch, recDec.ControlEpoch);
            Assert.Equal(decision.DrawnTile, recDec.DrawnTile);
            Assert.Equal(decision.RemainingBaseMillis, recDec.RemainingBaseMillis);
            Assert.Equal(decision.RemainingReserveMillis, recDec.RemainingReserveMillis);
            Assert.Equal(candidates.Count, recDec.Candidates.Count);

            for (int i = 0; i < candidates.Count; i++)
            {
                LegalActionOption expected = candidates[i];
                LegalActionOption actual = recDec.Candidates[i];

                Assert.Equal(expected.Kind, actual.Kind);
                Assert.Equal(expected.Seat, actual.Seat);
                Assert.Equal(expected.Tile, actual.Tile);
                Assert.Equal(expected.OptionId, actual.OptionId);
                Assert.Equal(expected.Tiles.Select(t => t.Value), actual.Tiles.Select(t => t.Value));
            }
        }

        [Fact]
        public void TableTimingCheckpointCodec_NegativeValidationsRejectCorruptedPayloads()
        {
            var option = new LegalActionOption(LegalActionKind.Chi, 0, new TileId(8), "chi-1", new[] { new TileId(8), new TileId(0), new TileId(4) });
            var decision = new TableRecoveredDecision(new DecisionKey("r", 0, "d"), 1, new[] { option }, null, 3000, 20000, 0);
            var checkpoint = new TableTimingCheckpoint("r", new long[] { 20000, 20000, 20000, 20000 }, new[] { decision });
            byte[] valid = TableTimingCheckpointCodec.Encode(checkpoint);

            // 1. Truncated
            Assert.Throws<InvalidDataException>(() => TableTimingCheckpointCodec.Decode(new byte[4]));

            // 2. Corrupt Magic
            byte[] badMagic = (byte[])valid.Clone();
            badMagic[0] = 0;
            Assert.Throws<InvalidDataException>(() => TableTimingCheckpointCodec.Decode(badMagic));

            // 3. Unsupported schema (schema 1 or 99)
            byte[] badSchema = (byte[])valid.Clone();
            badSchema[4] = 99;
            Assert.Throws<InvalidDataException>(() => TableTimingCheckpointCodec.Decode(badSchema));

            // 4. Trailing bytes
            byte[] trailing = valid.Concat(new byte[] { 0xFF }).ToArray();
            Assert.Throws<InvalidDataException>(() => TableTimingCheckpointCodec.Decode(trailing));
        }
    }
}
