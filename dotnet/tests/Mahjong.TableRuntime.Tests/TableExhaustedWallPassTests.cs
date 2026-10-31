using System.Linq;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Mahjong.TableRuntime;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class TableExhaustedWallPassTests
    {
        [Fact]
        public void ExhaustiveWallPassDoesNotRequireReservationDelivery()
        {
            var spec = new RoundSpec(
                "exhausted-pass",
                0,
                1,
                0,
                0,
                new long[] { 25000, 25000, 25000, 25000 },
                0);
            RoundStateMachine round = RoundStateMachine.CreateDealt(
                spec,
                TilePositionMap.CreateDealt(RecordedRandomDraw.Standard("exhausted-pass"), 0));
            int discarded = 0;
            while (round.Tiles.WallRemaining > 0)
            {
                RoundTransition transition = round.Execute(
                    RoundCommand.Discard(
                        round.RoundId,
                        round.CurrentSeat,
                        round.Tiles.DrawnTile!.Value.Tile));
                Assert.True(transition.IsAccepted);
                round = transition.State!;
                discarded++;
            }

            int responseSeat = (round.CurrentSeat + 1) % 4;
            RoundTransition opening = round.Execute(
                RoundCommand.Discard(
                    round.RoundId,
                    round.CurrentSeat,
                    round.Tiles.DrawnTile!.Value.Tile,
                    new[] { responseSeat }));
            Assert.True(opening.IsAccepted);
            round = opening.State!;
            Assert.True(round.Window != null);
            Assert.Equal(0, round.Tiles.WallRemaining);

            RoundPreparation prepared = round.Prepare(
                RoundCommand.Pass(round.RoundId, responseSeat),
                null,
                _ => false);
            Assert.True(prepared.IsReady);
            Assert.Equal(RoundPhase.Draw, prepared.Change!.NextState.Phase);
            Assert.Equal(0, prepared.Change.NextState.Tiles.WallRemaining);
            Assert.False(TableActor.ShouldDeliverDeferredDraw(prepared.Change));

            var economy = new CheatEconomyConfig(
                true,
                1000,
                new System.Collections.Generic.Dictionary<string, int>
                {
                    [CheatEconomyConfig.TileMarkCostKey] = 5,
                    [CheatEconomyConfig.SleeveBloomCostKey] = 10,
                    [CheatEconomyConfig.BeamSwapCostKey] = 10
                });
            RoundCheatPreparation delivery = RoundCheatTransition.PrepareDeferredDrawDelivery(
                round,
                prepared.Change,
                CheatState.Initialize(economy),
                1000);
            Assert.False(delivery.IsReady);
            Assert.Equal(CheatErrorCode.ReservationMissing, delivery.CheatError);
            Assert.Equal(69, discarded);
        }
    }
}
