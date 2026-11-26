using System;
using System.Linq;
using Mahjong.Domain.Cheat;
using Xunit;

namespace Mahjong.Domain.Tests
{
    public sealed class ConfirmedSleeveDrawTests
    {
        [Fact]
        public void PaidAndOrdinaryDrawEachTakeOneTileAndOnlyPaidDrawCharges()
        {
            RoundStateMachine pending = BeforeOrdinaryDraw(Opening());
            CheatState cheat = CheatTestBench.InitialState();
            RoundCheatPreparation paid = Bloom(pending, cheat, "paid");
            Assert.True(paid.IsReady, paid.Message);
            RoundCheatChange change = paid.Change!;
            RoundStateMachine drawn = pending.Install(change.RoundChange).State!;
            SleeveBloomOffer offer = change.CheatState.SleeveBloom.ImmediateOffer!;

            RoundStateMachine ordinary = Execute(pending, RoundCommand.Draw(pending.RoundId, 0));
            Assert.Equal(ordinary.Tiles.StateHash, drawn.Tiles.StateHash);
            Assert.Equal(ordinary.Tiles.DrawnTile!.Value.Tile, offer.FirstTile);
            Assert.Equal(40, cheat.Ledger.BalanceOf(0));
            Assert.Equal(30, change.CheatState.Ledger.BalanceOf(0));
            Assert.Single(change.CheatState.Ledger.Entries);
            Assert.False(Bloom(drawn, change.CheatState, "late").IsReady);

            RoundCheatPreparation selected = Choose(drawn, change.CheatState, offer, SleeveBloomKeep.Second);
            Assert.True(selected.IsReady, selected.Message);
            RoundStateMachine kept = drawn.Install(selected.Change!.RoundChange).State!;
            Assert.Equal(offer.SecondTile, kept.Tiles.DrawnTile!.Value.Tile);
            Assert.Equal(TileZone.Wall, kept.Tiles.PositionOf(offer.FirstTile).Zone);
            Assert.Equal(30, selected.Change.CheatState.Ledger.BalanceOf(0));
            CheatTestBench.AssertConserved(kept.Tiles);
        }

        [Fact]
        public void OrdinarySleeveDoesNotConsumeTheNextRinshanOpportunity()
        {
            RoundStateMachine pending = BeforeOrdinaryDraw(Opening());
            RoundCheatChange first = Bloom(pending, CheatTestBench.InitialState(), "first").Change!;
            RoundStateMachine drawn = pending.Install(first.RoundChange).State!;
            RoundCheatChange retained = Choose(drawn, first.CheatState, first.CheatState.SleeveBloom.ImmediateOffer!,
                SleeveBloomKeep.First).Change!;
            RoundStateMachine kan = ClosedKan(drawn.Install(retained.RoundChange).State!);
            RoundCheatPreparation second = Bloom(kan, retained.CheatState, "second");
            Assert.True(second.IsReady, second.Message);
            Assert.Equal(20, second.Change!.CheatState.Ledger.BalanceOf(0));
            Assert.Equal(2, second.Change.CheatState.Ledger.Entries.Count);
            Assert.Equal(SleeveBloomDrawKind.Rinshan, second.Change.CheatState.SleeveBloom.ImmediateOffer!.DrawKind);
        }

        [Fact]
        public void OctopusConfirmedDrawUsesAuthorityEvidenceAndCanPickAThirdRinshanTile()
        {
            CheatState octopus = CheatTestBench.InitialState().WithPlayerCharacter(PlayerCharacterIds.Octopus);
            for (int seed = 0; seed < 100; seed++)
            {
                int[] order = Enumerable.Range(0, TileId.Count).ToArray();
                var random = new Random(seed);
                for (int i = order.Length - 1; i > 4; i--)
                {
                    int j = random.Next(4, i + 1);
                    (order[i], order[j]) = (order[j], order[i]);
                }

                RoundStateMachine pending = ClosedKan(Opening(RecordedRandomDraw.Create("octopus-wall",
                    order.Select(id => new TileId(id)).ToArray())));
                RoundCheatChange first = Bloom(pending, octopus, "confirmed-first-request").Change!;
                SleeveBloomOffer offer = first.CheatState.SleeveBloom.ImmediateOffer!;
                RoundCheatChange replay = Bloom(pending, octopus, "different-request").Change!;
                Assert.Equal(offer.ThirdTile, replay.CheatState.SleeveBloom.ImmediateOffer!.ThirdTile);
                if (!offer.ThirdTile.HasValue) continue;

                RoundStateMachine drawn = pending.Install(first.RoundChange).State!;
                Assert.Equal(drawn.Tiles.TileAt(TilePosition.DeadWall(2)), offer.ThirdTile.Value);
                RoundCheatPreparation selection = Choose(drawn, first.CheatState, offer, SleeveBloomKeep.Third);
                Assert.True(selection.IsReady, selection.Message);
                RoundStateMachine kept = drawn.Install(selection.Change!.RoundChange).State!;
                Assert.Equal(offer.ThirdTile.Value, kept.Tiles.DrawnTile!.Value.Tile);
                Assert.Equal(offer.FirstTile, kept.Tiles.TileAt(TilePosition.DeadWall(2)));
                CheatTestBench.AssertConserved(kept.Tiles);
                return;
            }

            Assert.Fail("The fixed test seeds must contain at least one Octopus hit.");
        }

        [Fact]
        public void InsufficientPointsStillDrawsWithoutAnOfferOrCharge()
        {
            RoundStateMachine pending = BeforeOrdinaryDraw(Opening());
            CheatState empty = CheatTestBench.InitialState(initialPoints: 0);
            RoundCheatPreparation result = Bloom(pending, empty, "low-balance");
            Assert.True(result.IsReady, result.Message);
            Assert.Null(result.Change!.CheatState.SleeveBloom.ImmediateOffer);
            Assert.Empty(result.Change.CheatState.Ledger.Entries);
            Assert.True(pending.Install(result.Change.RoundChange).State!.Tiles.DrawnTile.HasValue);
        }

        [Fact]
        public void LegacyOffTurnActivationIsRejectedInConfirmedDrawMode()
        {
            RoundStateMachine round = Opening();
            round = Execute(round, RoundCommand.Discard(round.RoundId, 0, round.Tiles.DrawnTile!.Value.Tile));
            CheatState cheat = CheatTestBench.InitialState();
            cheat = cheat.WithSleeveBloom(cheat.SleeveBloom.WithOpenedWindow(0, 0, out string window));
            RoundCheatPreparation result = RoundCheatTransition.Prepare(round, cheat,
                new RoundCheatCommand(round.RoundId, 0, RoundCheatAction.SubmitSleeveBloomOffTurn, "off-turn",
                    RoundCheatPayload.OffTurn(window)), Context(round), CheatTestBench.Economy(), CheatTestBench.Sleeve());
            Assert.False(result.IsReady);
            Assert.Equal(CheatErrorCode.Disabled, result.CheatError);
            Assert.Empty(cheat.Ledger.Entries);
        }

        [Theory]
        [InlineData(RoundMeldKind.ClosedKan)]
        [InlineData(RoundMeldKind.OpenKan)]
        [InlineData(RoundMeldKind.AddedKan)]
        public void CompletedKanWaitsForConfirmationAndCanFundAnIndependentSleeveDraw(RoundMeldKind kind)
        {
            RoundStateMachine pending = kind == RoundMeldKind.ClosedKan
                ? ClosedKan(Opening())
                : CallFromOtherSeat(kind);
            Assert.Equal(RoundPhase.KanDraw, pending.Phase);
            Assert.False(pending.Tiles.DrawnTile.HasValue);
            Assert.Equal(0, pending.Tiles.RinshanPointer);
            RoundStateMachine restored = RoundRecovery.Restore(RoundRecoveryCodec.Decode(
                RoundRecoveryCodec.Encode(RoundRecovery.Capture(pending))));
            Assert.Equal(pending.SourceFingerprint, restored.SourceFingerprint);
            Assert.True(restored.Spec.ConfirmsDraw(0));

            CheatState cheat = CheatTestBench.InitialState();
            RoundCheatPreparation paid = Bloom(restored, cheat, "paid-rinshan");
            Assert.True(paid.IsReady, paid.Message);
            RoundStateMachine drawn = restored.Install(paid.Change!.RoundChange).State!;
            Assert.Equal(1, drawn.Tiles.RinshanPointer);
            SleeveBloomOffer offer = paid.Change.CheatState.SleeveBloom.ImmediateOffer!;
            Assert.Equal(SleeveBloomDrawKind.Rinshan, offer.DrawKind);
            Assert.Equal(30, paid.Change.CheatState.Ledger.BalanceOf(0));
            RoundCheatPreparation selected = Choose(drawn, paid.Change.CheatState, offer, SleeveBloomKeep.Second);
            Assert.True(selected.IsReady, selected.Message);
            RoundStateMachine kept = drawn.Install(selected.Change!.RoundChange).State!;
            Assert.Equal(offer.SecondTile, kept.Tiles.DrawnTile!.Value.Tile);
            CheatTestBench.AssertConserved(kept.Tiles);
        }

        private static RoundStateMachine Opening(RecordedRandomDraw? wall = null)
        {
            string id = CheatTestBench.RoundId;
            var spec = new RoundSpec(id, 0, 1, 0, 0, new long[] { 25000, 25000, 25000, 25000 }, 0, confirmSeatZeroDraw: true);
            return RoundStateMachine.CreateDealt(spec, TilePositionMap.CreateDealt(wall ?? RecordedRandomDraw.Standard(id), 0));
        }

        private static RoundStateMachine BeforeOrdinaryDraw(RoundStateMachine round)
        {
            while (round.Phase != RoundPhase.Draw)
                round = Execute(round, RoundCommand.Discard(round.RoundId, round.CurrentSeat,
                    round.Tiles.DrawnTile?.Tile ?? round.Tiles.TilesInZone(TileZone.Hand, round.CurrentSeat).Last()));
            Assert.Equal(0, round.CurrentSeat);
            return round;
        }

        private static RoundStateMachine ClosedKan(RoundStateMachine round) =>
            Execute(round, RoundCommand.ClosedKan(round.RoundId, 0, new TileId(0), new TileId(1), new TileId(2), new TileId(3)));

        private static RoundStateMachine CallFromOtherSeat(RoundMeldKind kind)
        {
            RoundStateMachine round = Opening(TestTables.Crafted((53, new TileId(3))));
            string id = round.RoundId;
            round = Execute(round, RoundCommand.Discard(id, 0, round.Tiles.DrawnTile!.Value.Tile));
            round = Execute(round, RoundCommand.Discard(id, 1, new TileId(3), new[] { 0 }));
            if (kind == RoundMeldKind.OpenKan)
                return Execute(round, RoundCommand.OpenKan(id, 0, new TileId(3), new TileId(0), new TileId(1), new TileId(2)));

            round = Execute(round, RoundCommand.Pon(id, 0, new TileId(3), new TileId(0), new TileId(1)));
            round = BeforeOrdinaryDraw(round);
            round = Execute(round, RoundCommand.Draw(id, 0));
            return Execute(round, RoundCommand.AddedKan(id, 0, new TileId(2)));
        }

        private static RoundStateMachine Execute(RoundStateMachine round, RoundCommand command)
        {
            RoundTransition next = round.Execute(command);
            Assert.True(next.IsAccepted, next.Error?.ToString());
            return next.State!;
        }

        private static RoundCheatPreparation Bloom(RoundStateMachine round, CheatState cheat, string id) =>
            RoundCheatTransition.Prepare(round, cheat,
                new RoundCheatCommand(round.RoundId, 0, RoundCheatAction.SubmitSleeveBloom, id, RoundCheatPayload.SleeveBloom()),
                Context(round), CheatTestBench.Economy(), CheatTestBench.Sleeve());

        private static RoundCheatPreparation Choose(RoundStateMachine round, CheatState cheat, SleeveBloomOffer offer, SleeveBloomKeep keep) =>
            RoundCheatTransition.Prepare(round, cheat,
                new RoundCheatCommand(round.RoundId, 0, RoundCheatAction.SubmitSleeveBloomChoice, "choose",
                    RoundCheatPayload.SleeveBloomChoice(offer.OfferId, keep)),
                Context(round), CheatTestBench.Economy(), CheatTestBench.Sleeve());

        private static CheatTurnContext Context(RoundStateMachine round) =>
            new CheatTurnContext(round.CurrentSeat, round.Phase, round.Window != null, round.RiichiDeclared,
                round.Events.Count(e => e.Type == RoundEventType.TileDrawn || e.Type == RoundEventType.RinshanDrawn),
                round.RoundId, 1, round.Tiles.DrawnTile?.Source == DrawSource.Rinshan ? SleeveBloomDrawKind.Rinshan : SleeveBloomDrawKind.Normal, 1_000);
    }
}
