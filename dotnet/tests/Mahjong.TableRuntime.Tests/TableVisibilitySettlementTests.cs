using System;
using System.Collections.Generic;
using System.Linq;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class TableVisibilitySettlementTests
    {
        [Fact]
        public void settlement_projection_gates_spectator_and_keeps_ledger_owner_only()
        {
            RoundStateMachine dealt = P31Fixtures.Dealt("round-1");
            long[] finalScores = { 28000, 24000, 24000, 24000 };
            RoundStateMachine finished = V32Fixtures.ApplyTsumo(dealt, 0, finalScores);
            Assert.True(finished.IsFinished);
            Assert.NotNull(finished.Result);

            TileId winTile = finished.Tiles.DrawnTile!.Value.Tile;
            var tsumoRecord = new TableEventRecord(
                new EventSeq(1),
                new StateVersion(1),
                0,
                RoundEventType.TsumoWin,
                0,
                winTile,
                null);
            var endedRecord = new TableEventRecord(
                new EventSeq(2),
                new StateVersion(1),
                1,
                RoundEventType.RoundEnded,
                ProtocolConstants.NoSeat,
                null,
                "Tsumo");
            var events = new[]
            {
                TableEventProjection.FromRoundEvent(tsumoRecord)!,
                TableEventProjection.FromRoundEvent(endedRecord)!
            };

            var session = new TableSessionProjection(
                true,
                "TSUMO",
                finalScores,
                new[] { 0, 1, 2, 3 },
                finished.RoundId);
            CheatState cheat = V32Fixtures.WithLedger(
                V32Fixtures.Cheat(),
                new CheatLedgerEntry("TSUMO-0", 0, -5, 995, "TILE_MARK"),
                new CheatLedgerEntry("TSUMO-1", 1, -10, 990, "BEAM_SWAP"));

            TableProjectionContext publicContext = V32Fixtures.Context(
                finished,
                settlementBatch: true,
                events: events,
                session: session);
            PublicSnapshotView spectator = TableVisibilityProjection.ProjectPublicSnapshot(finished, publicContext);
            Assert.True(spectator.View.Session.Finished);
            Assert.Equal("TSUMO", spectator.View.Session.FinishReason);
            Assert.Equal(4, spectator.View.Session.FinalScores.Count);
            Assert.Equal(finalScores[0], spectator.View.Session.FinalScores[0].Score);
            Assert.Equal(4, spectator.View.Session.Rankings.Count);
            Assert.Equal(finished.RoundId, spectator.View.SettlementRoundId);
            Assert.Equal(2, spectator.View.SettlementEvents.Count);
            Assert.Contains(spectator.View.SettlementEvents, item => item.Type == ProtocolEventType.TsumoWin);
            Assert.Contains(spectator.View.SettlementEvents, item => item.Type == ProtocolEventType.RoundEnded);
            Assert.Equal(finalScores[0], spectator.View.Board!.Scores[0].Score);

            TableProjectionContext notSettled = V32Fixtures.Context(
                finished,
                settlementBatch: false,
                events: events,
                session: session);
            PublicSnapshotView unsent = TableVisibilityProjection.ProjectPublicSnapshot(finished, notSettled);
            Assert.Single(unsent.View.SettlementEvents);
            Assert.DoesNotContain(unsent.View.SettlementEvents, item => item.Type == ProtocolEventType.TsumoWin);

            PlayerSnapshotView seat0 = TableVisibilityProjection.ProjectPlayerSnapshot(
                finished,
                cheat,
                0,
                notSettled);
            PlayerSnapshotView seat1 = TableVisibilityProjection.ProjectPlayerSnapshot(
                finished,
                cheat,
                1,
                notSettled);
            Assert.Contains(seat0.Public.SettlementEvents, item => item.Type == ProtocolEventType.TsumoWin);
            Assert.Contains(seat1.Public.SettlementEvents, item => item.Type == ProtocolEventType.TsumoWin);

            Assert.Equal(995L, seat0.Private.Cheat!.CheatPoints);
            Assert.Single(seat0.Private.Cheat.Ledger);
            Assert.Equal("TSUMO-0", seat0.Private.Cheat.Ledger[0].EntryId);
            Assert.Equal(990L, seat1.Private.Cheat!.CheatPoints);
            Assert.Equal("TSUMO-1", seat1.Private.Cheat.Ledger[0].EntryId);

            string publicDump = VisibilitySurface.Dump(spectator);
            Assert.False(VisibilitySurface.HasField(publicDump, "CheatPoints"));
            Assert.False(VisibilitySurface.HasField(publicDump, "Ledger"));
            Assert.DoesNotContain("TSUMO-0", publicDump);
            Assert.DoesNotContain("TSUMO-1", publicDump);
            Assert.DoesNotContain("BEAM_SWAP", publicDump);
        }

        [Fact]
        public void ura_dora_requires_explicit_authorized_settlement_batch()
        {
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            var ura = V32Fixtures.Event(
                ProtocolEventType.DoraRevealed,
                0,
                new[] { 25u },
                seq: 1,
                ura: true);

            PublicSnapshotView unbatched = TableVisibilityProjection.ProjectPublicSnapshot(
                round,
                V32Fixtures.Context(round, settlementBatch: false, revealUra: true, events: new[] { ura }));
            Assert.Empty(unbatched.View.SettlementEvents);

            PublicSnapshotView unauthorised = TableVisibilityProjection.ProjectPublicSnapshot(
                round,
                V32Fixtures.Context(round, settlementBatch: true, revealUra: false, events: new[] { ura }));
            Assert.Empty(unauthorised.View.SettlementEvents);

            PublicSnapshotView authorised = TableVisibilityProjection.ProjectPublicSnapshot(
                round,
                V32Fixtures.Context(round, settlementBatch: true, revealUra: true, events: new[] { ura }));
            ProtocolEventRecord revealed = Assert.Single(authorised.View.SettlementEvents);
            Assert.Equal(ProtocolEventType.DoraRevealed, revealed.Type);
            Assert.Equal(ProtocolEventVisibility.SettlementPublic, revealed.Visibility);
            Assert.Empty(revealed.CopyWirePayload());

            PlayerSnapshotView player = TableVisibilityProjection.ProjectPlayerSnapshot(
                round,
                V32Fixtures.Cheat(),
                0,
                V32Fixtures.Context(round, settlementBatch: false, revealUra: true, events: new[] { ura }));
            Assert.Empty(player.Public.SettlementEvents);
        }

        [Fact]
        public void draw_game_reason_is_sanitized_not_copied_from_domain_detail()
        {
            var exhaustive = TableEventProjection.FromRoundEvent(new TableEventRecord(
                new EventSeq(1),
                new StateVersion(1),
                0,
                RoundEventType.ExhaustiveDraw,
                ProtocolConstants.NoSeat,
                null,
                "tenpai:0,1"));
            Assert.NotNull(exhaustive);
            Assert.Equal("EXHAUSTIVE_DRAW", exhaustive!.PublicReasonCode);
            TableProjectedEvent projected = TableEventProjection.Project(
                exhaustive,
                TableProjectionAudience.Spectator,
                new TableProjectionPolicy())!;
            Assert.Equal("EXHAUSTIVE_DRAW", projected.PublicReasonCode);
            Assert.DoesNotContain("tenpai", VisibilitySurface.Dump(projected));

            var abortive = TableEventProjection.FromRoundEvent(new TableEventRecord(
                new EventSeq(1),
                new StateVersion(1),
                0,
                RoundEventType.AbortiveDraw,
                ProtocolConstants.NoSeat,
                null,
                "arbitrary private reason"));
            Assert.NotNull(abortive);
            Assert.Equal("ABORTIVE_DRAW", abortive!.PublicReasonCode);
            Assert.DoesNotContain("arbitrary", VisibilitySurface.Dump(abortive));
        }

        [Fact]
        public void chankan_ron_is_public_marked_and_never_carries_a_hand()
        {
            var record = new TableEventRecord(
                new EventSeq(8),
                new StateVersion(3),
                1,
                RoundEventType.RonWin,
                2,
                new TileId(33),
                "CHANKAN");
            TableEventProjectionInput input = TableEventProjection.FromRoundEvent(record)!;
            Assert.True(input.IsChankan);
            Assert.Equal(new uint[] { 33 }, input.TileIds.ToArray());

            TableProjectedEvent settlement = TableEventProjection.Project(
                input,
                TableProjectionAudience.Spectator,
                new TableProjectionPolicy(settlementBatch: true))!;
            Assert.True(settlement.IsChankan);
            Assert.Equal(2, settlement.Seat);
            Assert.Equal(new uint[] { 33 }, settlement.TileIds.ToArray());
            Assert.Equal(ProtocolEventVisibility.SettlementPublic, settlement.Visibility);

            Assert.Null(TableEventProjection.Project(
                input,
                TableProjectionAudience.Spectator,
                new TableProjectionPolicy(settlementBatch: false)));
        }
    }
}
