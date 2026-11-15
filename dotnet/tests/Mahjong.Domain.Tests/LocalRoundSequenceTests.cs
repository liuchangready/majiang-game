using System;
using System.Collections.Generic;
using System.Linq;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.Domain.Tests
{
    public class LocalRoundSequenceTests
    {
        [Theory]
        [InlineData(17)]
        [InlineData(29)]
        [InlineData(43)]
        [InlineData(71)]
        public void FixedSeedLegalTurnsConserveTilesAndRestoreToEquivalentReplay(int seed)
        {
            string roundId = "local-t1-seed-" + seed;
            TileId[] wallOrder = ShuffledWall(seed);
            RecordedRandomDraw draw = RecordedRandomDraw.Create("local-t1-seed-" + seed, wallOrder);
            TilePositionMap dealt = TilePositionMap.CreateDealt(draw, 0);
            RoundStateMachine state = RoundStateMachine.CreateDealt(P13.Spec(roundId), dealt);
            int step = 0;
            int responseSteps = 0;
            AssertStep(state, seed, step);

            // Six fixed-length legal turns. Every discard opens all three other seats' real response window.
            for (int turn = 0; turn < 6; turn++)
            {
                int seat = state.CurrentSeat;
                AssertRejectedWithoutMutation(state, RoundCommand.Discard(
                    roundId, seat, P13.FirstHandTile(state, (seat + 1) % 4)), seed, step);

                RoundCommand discard = RoundCommand.Discard(
                    roundId,
                    seat,
                    P13.FirstHandTile(state, seat),
                    Enumerable.Range(0, 4).Where(candidate => candidate != seat).ToArray());
                state = ExecuteAccepted(state, discard, seed, ++step);
                Assert.Equal(RoundPhase.ResponseWindow, state.Phase);
                AssertRejectedWithoutMutation(state, RoundCommand.Pass("foreign-round", 0), seed, step);

                while (state.Phase == RoundPhase.ResponseWindow)
                {
                    int responder = state.Window!.EligibleSeats
                        .First(candidate => !state.Window.Claims.Any(claim => claim.Seat == candidate));
                    RoundCommand pass = RoundCommand.Pass(roundId, responder);
                    state = ExecuteAccepted(state, pass, seed, ++step);
                    responseSteps++;
                }
            }
            Assert.True(responseSteps > 0, "seed=" + seed + " never entered a response transition");

            RoundStateMachine restored = RoundRecoveryCodec.Decode(
                RoundRecoveryCodec.Encode(RoundRecovery.Capture(state))).ToStateMachine();
            AssertEquivalent(state, restored, seed, "checkpoint");

            // Select each continuation from the left branch once, then apply those exact command values on the right.
            for (int turn = 0; turn < 3; turn++)
            {
                var replay = new List<RoundCommand>();
                var expectedStates = new List<RoundStateMachine>();
                RoundStateMachine nextLeft = PlayAndRecordTurn(state, roundId, replay, expectedStates, seed, ref step);
                RoundStateMachine nextRight = Replay(
                    restored, replay, expectedStates, seed, "restore-replay-" + turn, ref step);
                state = nextLeft;
                restored = nextRight;
                AssertEquivalent(state, restored, seed, "turn=" + turn);
                AssertStep(state, seed, step);
            }

            // A rejected foreign-round command after progression leaves the captured state byte-for-byte unchanged.
            AssertRejectedWithoutMutation(state, RoundCommand.Discard(
                "foreign-round", state.CurrentSeat, P13.FirstHandTile(state, state.CurrentSeat)), seed, step);
        }

        private static TileId[] ShuffledWall(int seed)
        {
            TileId[] tiles = Enumerable.Range(0, TileId.Count).Select(value => new TileId(value)).ToArray();
            var random = new Random(seed);
            for (int i = tiles.Length - 1; i > 0; i--)
            {
                int swap = random.Next(i + 1);
                (tiles[i], tiles[swap]) = (tiles[swap], tiles[i]);
            }

            return tiles;
        }

        private static RoundStateMachine PlayAndRecordTurn(
            RoundStateMachine state,
            string roundId,
            List<RoundCommand> commands,
            List<RoundStateMachine> expectedStates,
            int seed,
            ref int step)
        {
            int seat = state.CurrentSeat;
            RoundCommand discard = RoundCommand.Discard(
                roundId,
                seat,
                P13.FirstHandTile(state, seat),
                Enumerable.Range(0, 4).Where(candidate => candidate != seat).ToArray());
            commands.Add(discard);
            RoundStateMachine next = ExecuteAccepted(state, discard, seed, ++step);
            expectedStates.Add(next);
            while (next.Phase == RoundPhase.ResponseWindow)
            {
                int responder = next.Window!.EligibleSeats
                    .First(candidate => !next.Window.Claims.Any(claim => claim.Seat == candidate));
                RoundCommand pass = RoundCommand.Pass(roundId, responder);
                commands.Add(pass);
                next = ExecuteAccepted(next, pass, seed, ++step);
                expectedStates.Add(next);
            }

            return next;
        }

        private static RoundStateMachine Replay(
            RoundStateMachine state,
            IReadOnlyList<RoundCommand> commands,
            IReadOnlyList<RoundStateMachine> expectedStates,
            int seed,
            string label,
            ref int step)
        {
            for (int i = 0; i < commands.Count; i++)
            {
                state = ExecuteAccepted(state, commands[i], seed, ++step, label + "/command=" + i);
                AssertEquivalent(expectedStates[i], state, seed, label + "/command=" + i);
            }

            return state;
        }

        private static RoundStateMachine ExecuteAccepted(
            RoundStateMachine source,
            RoundCommand command,
            int seed,
            int step,
            string label = "sequence")
        {
            string diagnostic = "seed=" + seed + ",step=" + step + "," + label;
            RoundTransition transition = source.Execute(command);
            Assert.True(transition.IsAccepted, diagnostic + ": " + transition.Error?.ToString());
            Assert.Equal(source.Version + 1L, transition.CommandVersion);
            Assert.Equal(source.Version + 1L, transition.State!.Version);
            Assert.Equal(source.Events.Count + transition.Events.Count, transition.State.Events.Count);
            for (int i = 0; i < source.Events.Count; i++)
            {
                AssertEvent(source.Events[i], transition.State.Events[i], diagnostic + ",prefix=" + i);
            }
            for (int i = 0; i < transition.Events.Count; i++)
            {
                AssertEvent(transition.Events[i], transition.State.Events[source.Events.Count + i], diagnostic + ",suffix=" + i);
            }

            AssertStep(transition.State, seed, step);
            return transition.State;
        }

        private static void AssertStep(RoundStateMachine state, int seed, int step)
        {
            string diagnostic = "seed=" + seed + ",step=" + step;
            TilePlacement[] placements = state.Tiles.AsPlacements().OrderBy(p => p.Tile.Value).ToArray();
            Assert.True(placements.Length == 136, diagnostic + ": placement count");
            Assert.Equal(Enumerable.Range(0, 136), placements.Select(p => p.Tile.Value));
            Assert.True(placements.Select(p => p.Position).Distinct().Count() == 136, diagnostic + ": duplicate position");
            Assert.All(placements, placement => Assert.True(placement.Position.IsAssigned, diagnostic + ": unassigned " + placement.Tile));
            Assert.True(TilePositionMap.Validate(state.Tiles).IsValid, diagnostic + ": domain conservation report");
            Assert.Equal(Enumerable.Range(0, state.Events.Count), state.Events.Select(e => e.Ordinal));
        }

        private static void AssertEquivalent(RoundStateMachine left, RoundStateMachine right, int seed, string label)
        {
            string diagnostic = "seed=" + seed + "," + label;
            Assert.True(left.Version == right.Version, diagnostic + ": version");
            Assert.True(left.Phase == right.Phase, diagnostic + ": phase");
            Assert.True(left.CurrentSeat == right.CurrentSeat, diagnostic + ": seat");
            Assert.True(left.SourceFingerprint == right.SourceFingerprint, diagnostic + ": fingerprint");
            Assert.True(left.Scores.SequenceEqual(right.Scores), diagnostic + ": scores");
            Assert.True(left.RiichiSticks == right.RiichiSticks, diagnostic + ": sticks");

            TilePlacement[] leftPlacements = left.Tiles.AsPlacements().OrderBy(p => p.Tile.Value).ToArray();
            TilePlacement[] rightPlacements = right.Tiles.AsPlacements().OrderBy(p => p.Tile.Value).ToArray();
            Assert.Equal(leftPlacements.Length, rightPlacements.Length);
            for (int i = 0; i < leftPlacements.Length; i++)
            {
                Assert.True(leftPlacements[i].Tile == rightPlacements[i].Tile, diagnostic + ": tile=" + i);
                Assert.True(leftPlacements[i].Position == rightPlacements[i].Position, diagnostic + ": position tile=" + i);
            }

            Assert.Equal(left.Events.Count, right.Events.Count);
            for (int i = 0; i < left.Events.Count; i++)
            {
                AssertEvent(left.Events[i], right.Events[i], diagnostic + ": event=" + i);
            }
        }

        private static void AssertEvent(RoundEvent expected, RoundEvent actual, string diagnostic)
        {
            Assert.True(expected.Ordinal == actual.Ordinal, diagnostic + ": ordinal");
            Assert.True(expected.Type == actual.Type, diagnostic + ": type");
            Assert.True(expected.Seat == actual.Seat, diagnostic + ": seat");
            Assert.True(expected.Tile == actual.Tile, diagnostic + ": tile");
            Assert.True(expected.Detail == actual.Detail, diagnostic + ": detail");
        }

        private static void AssertRejectedWithoutMutation(
            RoundStateMachine state,
            RoundCommand command,
            int seed,
            int step)
        {
            byte[] before = RoundRecoveryCodec.Encode(RoundRecovery.Capture(state));
            RoundPreparation rejected = state.Prepare(command);
            Assert.False(rejected.IsReady, "seed=" + seed + ",step=" + step + ": invalid command accepted");
            Assert.Equal(before, RoundRecoveryCodec.Encode(RoundRecovery.Capture(state)));
        }
    }
}
