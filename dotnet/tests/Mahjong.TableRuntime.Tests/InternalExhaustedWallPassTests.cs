using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Mahjong.Ai;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Mahjong.TableRuntime;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    /// <summary>
    /// D-1-05(c) internal-authority wall exhaustion regressions. These drive the
    /// real <c>TableActor</c> timeout and AI paths (not <c>SubmitAsync</c>) into a
    /// genuine Domain wall exhaustion and keep the external-entry positive and the
    /// real reserved-delivery positives separate.
    /// </summary>
    public sealed class InternalExhaustedWallPassTests
    {
        private const int InitialPoints = 1000;

        [Fact]
        public async Task InternalTimeoutPassAtAnExhaustedWallCommitsWallExhaustedOnce()
        {
            RoundStateMachine round = ExhaustedWallWithResponseWindow(out int responseSeat);
            await using ActorHarness harness = CreateHarness(
                round,
                ExhaustedWallReservation(round, responseSeat));
            var pending = PassDecision(round, responseSeat, "timeout-pass", controlEpoch: 0);
            TableOperationResult opened = await harness.OpenDecisionAsync(pending);
            Assert.Equal(TableOperationStatus.Configured, opened.Status);

            harness.Clock.Now += 2_000L;
            TableTickResult tick = await harness.TickAsync();

            Assert.Equal(TableOperationStatus.Accepted, TableTickResultStatus(tick));
            TableTickEntry entry = Assert.Single(tick.Entries);
            Assert.Equal(TickEntryKind.Timeout, entry.Kind);
            Assert.Equal(responseSeat, entry.Seat);
            Assert.Equal("timeout-pass", entry.DecisionId);
            Assert.NotNull(entry.Transition);
            Assert.Equal(TableOperationStatus.Accepted, entry.Transition!.Status);
            Assert.Contains(entry.Transition.Events, e => e.Type == RoundEventType.WallExhausted);
            Assert.DoesNotContain(entry.Transition.Events, e => e.Type == RoundEventType.TileDrawn);
            Assert.DoesNotContain(
                entry.Transition.Events,
                e => e.Cheat?.Event.Kind == CheatEventKind.SleeveBloomReservationDelivered);
            Assert.NotNull(GetCheat(harness.Actor)!.SleeveBloom.ReservationFor(responseSeat));
            RoundStateMachine exhausted = GetRound(harness.Actor);
            Assert.Equal(0, exhausted.Tiles.WallRemaining);
            Assert.Null(exhausted.Tiles.DrawnTile);
            Assert.Equal(RoundPhase.Draw, exhausted.Phase);
            Assert.Equal(entry.Transition.CommandVersion!.Value, tick.FinalStateVersion);
            Assert.Equal(1UL, tick.FinalStateVersion.Value);

            StateVersion afterFirst = tick.FinalStateVersion;
            TableTickResult again = await harness.TickAsync();
            Assert.Empty(again.Entries);
            Assert.Equal(afterFirst, again.FinalStateVersion);
            Assert.Equal(136, PhysicalTileCount(GetRound(harness.Actor)));
        }

        [Fact]
        public async Task InternalAiPassAtAnExhaustedWallCommitsWallExhaustedOnce()
        {
            RoundStateMachine round = ExhaustedWallWithResponseWindow(out int responseSeat);
            await using ActorHarness harness = CreateHarness(
                round,
                ExhaustedWallReservation(round, responseSeat));
            SingleActionDecisionFixture fixture = P31Fixtures.BuildSingleActionDecision(
                harness,
                LegalActionKind.Pass,
                "ai-pass",
                seat: responseSeat,
                controlEpoch: 0);
            TableOperationResult opened = await harness.OpenDecisionAsync(fixture.Pending);
            Assert.Equal(TableOperationStatus.Configured, opened.Status);

            TableTickResult tick = await harness.CompleteAiAsync(fixture.Ai);

            Assert.Equal(TableOperationStatus.Accepted, TableTickResultStatus(tick));
            TableTickEntry entry = Assert.Single(tick.Entries);
            Assert.Equal(TickEntryKind.AiAccepted, entry.Kind);
            Assert.Equal(responseSeat, entry.Seat);
            Assert.Equal("ai-pass", entry.DecisionId);
            Assert.NotNull(entry.Transition);
            Assert.Equal(TableOperationStatus.Accepted, entry.Transition!.Status);
            Assert.Contains(entry.Transition.Events, e => e.Type == RoundEventType.WallExhausted);
            Assert.DoesNotContain(entry.Transition.Events, e => e.Type == RoundEventType.TileDrawn);
            Assert.DoesNotContain(
                entry.Transition.Events,
                e => e.Cheat?.Event.Kind == CheatEventKind.SleeveBloomReservationDelivered);
            Assert.NotNull(GetCheat(harness.Actor)!.SleeveBloom.ReservationFor(responseSeat));
            RoundStateMachine exhausted = GetRound(harness.Actor);
            Assert.Equal(0, exhausted.Tiles.WallRemaining);
            Assert.Null(exhausted.Tiles.DrawnTile);
            Assert.Equal(RoundPhase.Draw, exhausted.Phase);
            Assert.Equal(136, PhysicalTileCount(exhausted));

            StateVersion afterFirst = tick.FinalStateVersion;
            TableTickResult replay = await harness.CompleteAiAsync(fixture.Ai);
            TableTickEntry replayEntry = Assert.Single(replay.Entries);
            Assert.Equal(TickEntryKind.AiRejected, replayEntry.Kind);
            Assert.Null(replayEntry.Transition);
            Assert.Equal(afterFirst, replay.FinalStateVersion);
            Assert.Equal(0, GetRound(harness.Actor).Tiles.WallRemaining);
            Assert.Null(GetRound(harness.Actor).Tiles.DrawnTile);
            Assert.Equal(136, PhysicalTileCount(GetRound(harness.Actor)));
        }

        [Fact]
        public async Task InternalAiPassWithLiveWallDrawsNormallyInsteadOfExhausting()
        {
            RoundStateMachine round = LiveWallWithResponseWindow(out int responseSeat);
            Assert.True(round.Tiles.WallRemaining > 0);
            int wallBefore = round.Tiles.WallRemaining;
            await using ActorHarness harness = CreateHarness(round);
            SingleActionDecisionFixture fixture = P31Fixtures.BuildSingleActionDecision(
                harness,
                LegalActionKind.Pass,
                "ai-live-pass",
                seat: responseSeat,
                controlEpoch: 0);
            Assert.Equal(TableOperationStatus.Configured, (await harness.OpenDecisionAsync(fixture.Pending)).Status);

            TableTickResult tick = await harness.CompleteAiAsync(fixture.Ai);

            Assert.Equal(TableOperationStatus.Accepted, TableTickResultStatus(tick));
            TableTickEntry entry = Assert.Single(tick.Entries);
            Assert.Equal(TableOperationStatus.Accepted, entry.Transition!.Status);
            Assert.DoesNotContain(entry.Transition.Events, e => e.Type == RoundEventType.WallExhausted);
            Assert.Contains(entry.Transition.Events, e => e.Type == RoundEventType.TileDrawn);
            Assert.Equal(1UL, tick.FinalStateVersion.Value);
            Assert.Equal(wallBefore - 1, GetRound(harness.Actor).Tiles.WallRemaining);
            Assert.False(GetRound(harness.Actor).IsFinished);
            Assert.Equal(136, PhysicalTileCount(GetRound(harness.Actor)));
        }

        [Fact]
        public async Task InternalAiPassDeliversARealReservationToTheOwnerDrawExactlyOnce()
        {
            RoundStateMachine round = P31Fixtures.Dealt("internal-reservation");
            await using ActorHarness harness = CreateHarness(round);
            IReadOnlyList<TableCommandEnvelope> chain = P31Fixtures.BuildDiscardChain(round, 4);
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await harness.SubmitAsync(chain[0])).Status);
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await harness.Actor.SubmitCheatAsync(
                    Cheat(
                        round.RoundId,
                        0,
                        RoundCheatAction.SubmitSleeveBloomOffTurn,
                        "off",
                        RoundCheatPayload.OffTurn("bloom-window-0-0-0"),
                        1,
                        "off"),
                    TestContext.Current.CancellationToken)).Status);
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await harness.Actor.SubmitCheatAsync(
                    Cheat(
                        round.RoundId,
                        0,
                        RoundCheatAction.SubmitSleeveBloomOffTurnChoice,
                        "choose",
                        RoundCheatPayload.OffTurnChoice("bloom-offturn-0-off", SleeveBloomKeep.First),
                        2,
                        "choose"),
                    TestContext.Current.CancellationToken)).Status);
            TileId reserved = GetCheat(harness.Actor)!.SleeveBloom.ReservationFor(0)!.Tile;

            Assert.Equal(TableOperationStatus.Accepted, (await SubmitRoundAt(harness, chain[1].Command, 3, "d1")).Status);
            Assert.Equal(TableOperationStatus.Accepted, (await SubmitRoundAt(harness, chain[2].Command, 4, "d2")).Status);
            RoundStateMachine beforeSeat3 = GetRound(harness.Actor);
            TileId seat3Tile = beforeSeat3.Tiles.TilesInZone(TileZone.Hand, 3)[0];
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await SubmitRoundAt(
                    harness,
                    RoundCommand.Discard("internal-reservation", 3, seat3Tile, new[] { 1 }),
                    5,
                    "d3")).Status);

            var fixture = new SingleActionDecisionFixture(
                new PendingDecision(
                    new DecisionKey("internal-reservation", 1, "pass-internal"),
                    0,
                    new[] { new LegalActionOption(LegalActionKind.Pass, 1) }),
                BuildPassAiDecision(harness, "internal-reservation", 1, "pass-internal"),
                new LegalActionOption(LegalActionKind.Pass, 1));
            Assert.Equal(TableOperationStatus.Configured, (await harness.OpenDecisionAsync(fixture.Pending)).Status);

            TableTickResult tick = await harness.CompleteAiAsync(fixture.Ai);

            Assert.Equal(TableOperationStatus.Accepted, TableTickResultStatus(tick));
            TableTickEntry entry = Assert.Single(tick.Entries);
            Assert.Equal(TableOperationStatus.Accepted, entry.Transition!.Status);
            Assert.Equal(7UL, entry.Transition.CommandVersion!.Value.Value);
            TableEventRecord drawn = Assert.Single(
                entry.Transition.Events,
                e => e.Type == RoundEventType.TileDrawn && e.Seat == 0);
            Assert.Equal(reserved, drawn.Tile);
            TableEventRecord delivered = Assert.Single(
                entry.Transition.Events,
                e => e.Cheat?.Event.Kind == CheatEventKind.SleeveBloomReservationDelivered);
            Assert.Equal("bloom-offturn-0-off", delivered.Cheat!.OfferId);
            Assert.DoesNotContain(
                entry.Transition.Events,
                e => e.Cheat?.Event.Kind == CheatEventKind.CheatPointsChanged);
            Assert.Null(GetCheat(harness.Actor)!.SleeveBloom.ReservationFor(0));
            Assert.Equal(136, PhysicalTileCount(GetRound(harness.Actor)));
        }

        private static TableOperationStatus TableTickResultStatus(TableTickResult tick) => tick.Status;

        private static async Task<TableOperationResult> SubmitRoundAt(
            ActorHarness harness,
            RoundCommand command,
            int expectedVersion,
            string requestId)
        {
            return await harness.SubmitAsync(new TableCommandEnvelope(
                command,
                new RequestId(requestId),
                new StateVersion((ulong)expectedVersion),
                new OwnerEpoch(1),
                new ControlEpoch(0)));
        }

        private static RoundStateMachine ExhaustedWallWithResponseWindow(out int responseSeat)
        {
            var spec = new RoundSpec(
                "exhausted-internal",
                0,
                1,
                0,
                0,
                new long[] { 25000, 25000, 25000, 25000 },
                0);
            RoundStateMachine round = RoundStateMachine.CreateDealt(
                spec,
                TilePositionMap.CreateDealt(RecordedRandomDraw.Standard("exhausted-internal"), 0));
            while (round.Tiles.WallRemaining > 0)
            {
                RoundTransition discard = round.Execute(
                    RoundCommand.Discard(
                        round.RoundId,
                        round.CurrentSeat,
                        round.Tiles.DrawnTile!.Value.Tile));
                Assert.True(discard.IsAccepted);
                round = discard.State!;
            }

            responseSeat = (round.CurrentSeat + 1) % 4;
            RoundTransition opening = round.Execute(
                RoundCommand.Discard(
                    round.RoundId,
                    round.CurrentSeat,
                    round.Tiles.DrawnTile!.Value.Tile,
                    new[] { responseSeat }));
            Assert.True(opening.IsAccepted);
            round = opening.State!;
            Assert.NotNull(round.Window);
            return round;
        }

        private static RoundStateMachine LiveWallWithResponseWindow(out int responseSeat)
        {
            RoundStateMachine round = P31Fixtures.Dealt("live-internal");
            int current = round.CurrentSeat;
            responseSeat = (current + 1) % 4;
            RoundTransition opening = round.Execute(
                RoundCommand.Discard(
                    round.RoundId,
                    current,
                    round.Tiles.DrawnTile!.Value.Tile,
                    new[] { responseSeat }));
            Assert.True(opening.IsAccepted);
            round = opening.State!;
            Assert.NotNull(round.Window);
            return round;
        }

        /// <summary>
        /// Seeds the same guard shape as the accepted external-entry regression:
        /// a real registered round plus a reservation held by the passing seat.
        /// This is a non-delivery guard, not a positive live-wall delivery proof.
        /// </summary>
        private static CheatState ExhaustedWallReservation(RoundStateMachine round, int seat)
        {
            CheatState cheat = V32Fixtures.WithRegisteredRound(
                V32Fixtures.Cheat(InitialPoints),
                round.RoundId);
            TileId reserved = round.Tiles.TilesInZone(TileZone.Hand, seat)[0];
            return cheat.WithSleeveBloom(cheat.SleeveBloom.WithReservation(
                new SleeveBloomReservation(seat, "exhausted-reservation", reserved, "window", 0)));
        }

        private static PendingDecision PassDecision(
            RoundStateMachine round,
            int seat,
            string decisionId,
            long controlEpoch)
        {
            return new PendingDecision(
                new DecisionKey(round.RoundId, seat, decisionId),
                controlEpoch,
                new[] { new LegalActionOption(LegalActionKind.Pass, seat) });
        }

        private static AiDecision BuildPassAiDecision(
            ActorHarness harness,
            string roundId,
            int seat,
            string decisionId)
        {
            RoundStateMachine round = harness.InitialRound;
            var key = new DecisionKey(roundId, seat, decisionId);
            var action = new LegalActionOption(LegalActionKind.Pass, seat);
            var observation = new AiObservation(
                "session-1",
                key,
                0L,
                0L,
                harness.Clock.Now + 1000L,
                round.Phase,
                round.DealerSeat,
                round.RoundWind,
                round.RoundNumber,
                round.HonbaSticks,
                round.RiichiSticks,
                round.Tiles.WallRemaining,
                round.Tiles.TilesInZone(TileZone.Hand, seat),
                null,
                Array.Empty<AiVisibleMeld>(),
                Array.Empty<AiVisibleMeld>(),
                Array.Empty<AiVisibleDiscard>(),
                Array.Empty<TileId>(),
                round.Scores,
                new[] { action });
            var intent = new CommandIntent(observation, action, "test-ai", 1);
            return new AiDecision(observation, intent, 0.5d);
        }

        private static ActorHarness CreateHarness(RoundStateMachine round, CheatState? initialCheat = null)
        {
            var clock = new ManualTableClock { Now = 1000L };
            var commitPort = new RecordingCommitPort();
            var sink = new RecordingPublicationSink(commitPort);
            CheatState cheat = initialCheat ??
                V32Fixtures.WithRegisteredRound(V32Fixtures.Cheat(InitialPoints), round.RoundId);
            var options = new TableActorOptions
            {
                RoomId = "room-1",
                MailboxCapacity = 16,
                ControlReserve = 16,
                DecisionClockConfig = new DecisionClockConfig(1000L, 0L, ReserveScope.Round),
                InitialControllers = P31Fixtures.Controllers(
                    SeatControllerKind.LocalHuman,
                    SeatControllerKind.Computer,
                    SeatControllerKind.Computer,
                    SeatControllerKind.Computer),
                CheatEconomy = V32Fixtures.Economy(InitialPoints),
                SleeveBloomConfig = V32Fixtures.SleeveConfig(),
                CheatRequestBindingCapacity = 8192,
                InitialCheatState = cheat
            };
            var actor = new TableActor(options, round, commitPort, clock, sink);
            return new ActorHarness(actor, clock, commitPort, sink, round);
        }

        private static int PhysicalTileCount(RoundStateMachine round)
        {
            IReadOnlyList<TilePlacement> placements = round.Tiles.AsPlacements();
            int count = 0;
            for (int i = 0; i < placements.Count; i++)
            {
                if (placements[i].Position.Zone != TileZone.Unassigned)
                {
                    count++;
                }
            }

            return count;
        }

        private static RoundStateMachine GetRound(TableActor actor)
        {
            FieldInfo field = typeof(TableActor).GetField("_round", BindingFlags.Instance | BindingFlags.NonPublic)!;
            return (RoundStateMachine)field.GetValue(actor)!;
        }

        private static CheatState? GetCheat(TableActor actor)
        {
            FieldInfo field = typeof(TableActor).GetField("_cheat", BindingFlags.Instance | BindingFlags.NonPublic)!;
            return (CheatState?)field.GetValue(actor);
        }

        private static TableCheatCommandEnvelope Cheat(
            string roundId,
            int seat,
            RoundCheatAction action,
            string requestId,
            RoundCheatPayload payload,
            int expectedVersion,
            string? digestRequest = null)
        {
            TablePayloadSha256? sha = null;
            if (digestRequest != null)
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(
                    "payload|" + digestRequest + "|" + action + "|" + seat);
                sha = TablePayloadSha256.Compute(bytes);
            }

            return new TableCheatCommandEnvelope(
                new RoundCheatCommand(roundId, seat, action, requestId, payload),
                new RequestId(requestId),
                new StateVersion((ulong)expectedVersion),
                new OwnerEpoch(1),
                new ControlEpoch(0),
                null,
                0L,
                sha.HasValue ? new RequestScopeId("scope-1") : (RequestScopeId?)null,
                sha.HasValue ? "CHEAT" : null,
                sha);
        }
    }
}
