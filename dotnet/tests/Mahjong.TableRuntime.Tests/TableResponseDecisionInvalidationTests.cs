using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    /// <summary>
    /// D-TR-RESP-01: a committed response must not leave decisions/timings from a window that
    /// the Domain has already closed. The legal open-window multi-seat submit/priority case is
    /// preserved; only decisions that the exact current window no longer requires are removed,
    /// atomically with the commit that closed the window.
    /// </summary>
    public sealed class TableResponseDecisionInvalidationTests
    {
        private static TileId Man(int rank, int copy) => TileId.FromTypeIndex(rank - 1, copy);

        [Fact]
        public async Task ClaimKeepsPeersWhileWindowIsOpenAndConsumesTheirCommit()
        {
            await using ActorHarness harness = CreateWindowHarness();
            RoundStateMachine window = harness.InitialRound;
            TileId target = Man(3, 3);
            Assert.Equal(RoundPhase.ResponseWindow, window.Phase);
            Assert.Equal(new[] { 1, 2 }, window.Window!.EligibleSeats.OrderBy(value => value).ToArray());

            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(ChiDecision(window, seat: 1, target))).Status);
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(PonDecision(window, seat: 2, target))).Status);

            TableOperationResult chi = await harness.SubmitAsync(P31Fixtures.Command(
                RoundCommand.Chi(window.RoundId, 1, target, Man(1, 0), Man(2, 0)),
                0,
                "chi-claim",
                new DecisionId("chi-1")));

            Assert.Equal(TableOperationStatus.Accepted, chi.Status);
            Assert.Equal(RoundPhase.ResponseWindow, (await harness.GetSnapshotAsync()).Phase);

            // The exact same window still requires seat 2, so its decision and timing survive
            // the claim. This is the intentional multi-seat submit/priority semantics.
            TableTimingSnapshot afterClaim = await harness.GetTimingSnapshotAsync();
            Assert.NotNull(afterClaim.Seats[2].PendingKey);
            Assert.Equal("pon-2", afterClaim.Seats[2].PendingKey!.Value.DecisionId);
            Assert.NotNull(afterClaim.Seats[2].Timing);
            Assert.Null(afterClaim.Seats[1].PendingKey);

            TableOperationResult pass = await harness.SubmitAsync(P31Fixtures.Command(
                RoundCommand.Pass(window.RoundId, 2),
                1,
                "pass-2",
                new DecisionId("pon-2")));

            Assert.Equal(TableOperationStatus.Accepted, pass.Status);
            TableSnapshot finished = await harness.GetSnapshotAsync();
            Assert.Equal(RoundPhase.Discard, finished.Phase);
            Assert.Equal(1, finished.CurrentSeat);
            TableTimingSnapshot afterWindow = await harness.GetTimingSnapshotAsync();
            Assert.All(afterWindow.Seats, seat => Assert.Null(seat.PendingKey));
            Assert.All(afterWindow.Seats, seat => Assert.Null(seat.Timing));
        }

        [Fact]
        public async Task ClosedWindowDropsStaleDecisionAndTimingInTheSameCommit()
        {
            await using ActorHarness harness = CreateWindowHarness();
            RoundStateMachine window = harness.InitialRound;
            TileId target = Man(3, 3);

            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(ChiDecision(window, seat: 1, target))).Status);
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(PonDecision(window, seat: 2, target))).Status);

            TableOperationResult chi = await harness.SubmitAsync(P31Fixtures.Command(
                RoundCommand.Chi(window.RoundId, 1, target, Man(1, 0), Man(2, 0)),
                0,
                "chi-claim",
                new DecisionId("chi-1")));
            Assert.Equal(TableOperationStatus.Accepted, chi.Status);

            // A stale capture re-opens a decision for the seat that has ALREADY responded in
            // this window. The actor accepts the open (the host race is real), but the next
            // window-closing commit must invalidate it atomically.
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(ChiDecision(window, seat: 1, target, "stale-1"))).Status);

            TableOperationResult pass = await harness.SubmitAsync(P31Fixtures.Command(
                RoundCommand.Pass(window.RoundId, 2),
                1,
                "pass-2",
                new DecisionId("pon-2")));
            Assert.Equal(TableOperationStatus.Accepted, pass.Status);

            TableSnapshot snapshot = await harness.GetSnapshotAsync();
            Assert.Equal(RoundPhase.Discard, snapshot.Phase);
            TableTimingSnapshot timing = await harness.GetTimingSnapshotAsync();
            Assert.All(timing.Seats, seat => Assert.Null(seat.PendingKey));
            Assert.All(timing.Seats, seat => Assert.Null(seat.Timing));

            // The stale decision can no longer be submitted, and the committed state still
            // describes exactly one Chi by seat 1 with no extra version.
            TableOperationResult stale = await harness.SubmitAsync(P31Fixtures.Command(
                RoundCommand.Chi(window.RoundId, 1, target, Man(1, 0), Man(2, 0)),
                2,
                "stale-claim",
                new DecisionId("stale-1")));
            Assert.Equal(TableOperationStatus.Rejected, stale.Status);
            Assert.Empty(harness.CommitPort.Requests.Skip(2));
        }

        [Fact]
        public async Task TimeoutOnRemainingResponseSeatClosesWindowAndConsumesItsDecision()
        {
            await using ActorHarness harness = CreateWindowHarness();
            RoundStateMachine window = harness.InitialRound;
            TileId target = Man(3, 3);
            await harness.OpenDecisionAsync(ChiDecision(window, seat: 1, target));
            await harness.OpenDecisionAsync(PonDecision(window, seat: 2, target));

            TableOperationResult chi = await harness.SubmitAsync(P31Fixtures.Command(
                RoundCommand.Chi(window.RoundId, 1, target, Man(1, 0), Man(2, 0)),
                0,
                "chi-claim",
                new DecisionId("chi-1")));
            Assert.Equal(TableOperationStatus.Accepted, chi.Status);

            harness.Clock.Now = 2000L;
            TableTickResult tick = await harness.TickAsync();
            Assert.Single(tick.Entries);
            Assert.Equal(TickEntryKind.Timeout, tick.Entries[0].Kind);
            Assert.Equal(TableOperationStatus.Accepted, tick.Entries[0].Transition!.Status);
            TableSnapshot snapshot = await harness.GetSnapshotAsync();
            Assert.Equal(RoundPhase.Discard, snapshot.Phase);
            TableTimingSnapshot timing = await harness.GetTimingSnapshotAsync();
            Assert.All(timing.Seats, seat => Assert.Null(seat.PendingKey));
            Assert.All(timing.Seats, seat => Assert.Null(seat.Timing));
        }

        [Fact]
        public async Task AiPassOnRemainingResponseSeatClosesWindow()
        {
            await using ActorHarness harness = CreateWindowHarness();
            RoundStateMachine window = harness.InitialRound;
            TileId target = Man(3, 3);
            await harness.OpenDecisionAsync(ChiDecision(window, seat: 1, target));
            await harness.OpenDecisionAsync(PonDecision(window, seat: 2, target));

            TableOperationResult chi = await harness.SubmitAsync(P31Fixtures.Command(
                RoundCommand.Chi(window.RoundId, 1, target, Man(1, 0), Man(2, 0)),
                0,
                "chi-claim",
                new DecisionId("chi-1")));
            Assert.Equal(TableOperationStatus.Accepted, chi.Status);

            harness.Clock.Now = 1500L;
            SingleActionDecisionFixture ai = P31Fixtures.BuildSingleActionDecision(
                harness,
                LegalActionKind.Pass,
                "pon-2",
                2);
            TableTickResult tick = await harness.CompleteAiAsync(ai.Ai);
            Assert.Single(tick.Entries);
            Assert.Equal(TickEntryKind.AiAccepted, tick.Entries[0].Kind);
            Assert.Equal(TableOperationStatus.Accepted, tick.Entries[0].Transition!.Status);
            Assert.Equal(RoundPhase.Discard, (await harness.GetSnapshotAsync()).Phase);
            TableTimingSnapshot timing = await harness.GetTimingSnapshotAsync();
            Assert.All(timing.Seats, seat => Assert.Null(seat.PendingKey));
        }

        [Fact]
        public async Task RecoveredStaleOpenKanResponseIsDropped()
        {
            RoundStateMachine round = BuildClosedOpenKanRound();
            await using ActorHarness harness = CreateActor(round);
            var stale = new TableRecoveredDecision(
                new DecisionKey(round.RoundId, 2, "stale-openkan"),
                0L,
                new[]
                {
                    new LegalActionOption(LegalActionKind.Kan, 2, Man(3, 3)),
                    new LegalActionOption(LegalActionKind.Pass, 2)
                },
                null,
                1000L,
                0L,
                0L);
            await harness.BeginRecoveryAsync("probe-openkan");
            TableOperationResult result = await harness.CompleteRecoveryAsync(
                RecoveryBaseline(round, stale));
            Assert.True(
                result.Status == TableOperationStatus.Configured,
                result.ReasonCode + ": " + result.Detail);
            TableTimingSnapshot timing = await harness.GetTimingSnapshotAsync();
            Assert.Null(timing.Seats[2].PendingKey);
            Assert.Null(timing.Seats[2].Timing);
        }

        [Fact]
        public async Task RecoveredInvalidControlEpochStillRejectsBeforeFiltering()
        {
            RoundStateMachine round = BuildClosedOpenKanRound();
            await using ActorHarness harness = CreateActor(round);
            var invalid = new TableRecoveredDecision(
                new DecisionKey(round.RoundId, 2, "stale-bad-epoch"),
                99L,
                new[]
                {
                    new LegalActionOption(LegalActionKind.Pon, 2, Man(3, 3)),
                    new LegalActionOption(LegalActionKind.Pass, 2)
                },
                null,
                1000L,
                0L,
                0L);
            await harness.BeginRecoveryAsync("probe-bad-epoch");
            TableOperationResult result = await harness.CompleteRecoveryAsync(
                RecoveryBaseline(round, invalid));
            Assert.Equal(TableOperationStatus.Rejected, result.Status);
            Assert.Equal(TableRuntimeReasonCode.RecoveryTimingInvalid, result.ReasonCode);
            Assert.Equal(
                TableActorLifecycle.Recovering,
                (await harness.GetSnapshotAsync()).Lifecycle);
        }

        [Fact]
        public async Task NonDurableDeferredSleeveDeliveryClearsClosedWindowPeers()
        {
            RoundStateMachine round = BuildWindowRound();
            CheatState cheat = V32Fixtures.WithRegisteredRound(V32Fixtures.Cheat(1000), round.RoundId);
            TileId tile = round.Tiles.TileAt(TilePosition.Wall(round.Tiles.WallPointer));
            cheat = cheat.WithSleeveBloom(
                cheat.SleeveBloom.WithReservation(
                    new SleeveBloomReservation(1, "reserved", tile, "window", 0)));
            var clock = new ManualTableClock { Now = 1000L };
            var port = new RecordingCommitPort();
            var sink = new RecordingPublicationSink(port);
            var options = new TableActorOptions
            {
                RoomId = "room-1",
                MailboxCapacity = 16,
                ControlReserve = 16,
                DecisionClockConfig = new DecisionClockConfig(1000L, 0L, ReserveScope.Round),
                InitialControllers = P31Fixtures.Controllers(
                    SeatControllerKind.Computer,
                    SeatControllerKind.Computer,
                    SeatControllerKind.Computer,
                    SeatControllerKind.Computer),
                CheatEconomy = V32Fixtures.Economy(1000),
                SleeveBloomConfig = V32Fixtures.SleeveConfig(),
                InitialCheatState = cheat
            };
            await using var harness = new ActorHarness(
                new TableActor(options, round, port, clock, sink),
                clock,
                port,
                sink,
                round);

            PendingDecision Pass(int seat, string id) => new PendingDecision(
                new DecisionKey(round.RoundId, seat, id),
                0L,
                new[] { new LegalActionOption(LegalActionKind.Pass, seat) },
                null);

            await harness.OpenDecisionAsync(Pass(1, "p1"));
            await harness.OpenDecisionAsync(Pass(2, "p2"));
            TableOperationResult first = await harness.SubmitAsync(P31Fixtures.Command(
                RoundCommand.Pass(round.RoundId, 1),
                0,
                "pass1",
                new DecisionId("p1")));
            Assert.Equal(TableOperationStatus.Accepted, first.Status);

            clock.Now = 1500L;
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(Pass(1, "stale1"))).Status);
            clock.Now = 2000L;
            TableTickResult tick = await harness.TickAsync();
            Assert.Single(tick.Entries);
            Assert.Equal(TableOperationStatus.Accepted, tick.Entries[0].Transition!.Status);
            Assert.Contains(
                tick.Entries[0].Transition!.Events,
                value => value.Cheat?.Event.Kind == CheatEventKind.SleeveBloomReservationDelivered);
            TableTimingSnapshot timing = await harness.GetTimingSnapshotAsync();
            Assert.Null(timing.Seats[1].PendingKey);
            Assert.Null(timing.Seats[1].Timing);
        }

        [Fact]
        public async Task FailedCommitDoesNotConsumeOrInvalidateAnyResponseDecision()
        {
            await using ActorHarness harness = CreateWindowHarness();
            RoundStateMachine window = harness.InitialRound;
            TileId target = Man(3, 3);

            await harness.OpenDecisionAsync(ChiDecision(window, seat: 1, target));
            await harness.OpenDecisionAsync(PonDecision(window, seat: 2, target));

            TableTimingSnapshot before = await harness.GetTimingSnapshotAsync();
            harness.CommitPort.FailNext();
            TableOperationResult failed = await harness.SubmitAsync(P31Fixtures.Command(
                RoundCommand.Chi(window.RoundId, 1, target, Man(1, 0), Man(2, 0)),
                0,
                "failed-claim",
                new DecisionId("chi-1")));

            Assert.Equal(TableOperationStatus.Failed, failed.Status);
            TableSnapshot afterFailure = await harness.GetSnapshotAsync();
            Assert.Equal(0UL, afterFailure.StateVersion.Value);
            Assert.Equal(RoundPhase.ResponseWindow, afterFailure.Phase);
            TableTimingSnapshot unchanged = await harness.GetTimingSnapshotAsync();
            Assert.Equal(before.Seats[1].Timing!.DeadlineMillis, unchanged.Seats[1].Timing!.DeadlineMillis);
            Assert.Equal(before.Seats[2].Timing!.DeadlineMillis, unchanged.Seats[2].Timing!.DeadlineMillis);
            Assert.Equal(before.Seats[1].RemainingReserveMillis, unchanged.Seats[1].RemainingReserveMillis);
            Assert.Equal(before.Seats[2].RemainingReserveMillis, unchanged.Seats[2].RemainingReserveMillis);
            Assert.NotNull(unchanged.Seats[1].PendingKey);
            Assert.NotNull(unchanged.Seats[2].PendingKey);

            TableOperationResult retry = await harness.SubmitAsync(P31Fixtures.Command(
                RoundCommand.Chi(window.RoundId, 1, target, Man(1, 0), Man(2, 0)),
                0,
                "retry-claim",
                new DecisionId("chi-1")));
            Assert.Equal(TableOperationStatus.Accepted, retry.Status);
            TableTimingSnapshot afterRetry = await harness.GetTimingSnapshotAsync();
            Assert.Null(afterRetry.Seats[1].PendingKey);
            Assert.NotNull(afterRetry.Seats[2].PendingKey);
        }

        [Fact]
        public async Task DurableCommitFaultLeavesPendingAndReserveUntouchedThenRetries()
        {
            await using DurableHarness harness = await DurableHarness.CreateAsync();
            RoundStateMachine round = harness.InitialRound;
            TileId first = round.Tiles.TilesInZone(TileZone.Hand, 0)[0];
            var pending = new PendingDecision(
                new DecisionKey(round.RoundId, 0, "d1"),
                0L,
                new[] { new LegalActionOption(LegalActionKind.Discard, 0, first) },
                first);
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.Actor.OpenDecisionAsync(
                    pending,
                    TestContext.Current.CancellationToken)).Status);

            TableTimingSnapshot before = await harness.Actor.GetTimingSnapshotAsync(
                TestContext.Current.CancellationToken);
            harness.Store.ArmedFaultPoint = TableStoreFaultPoint.BeforeApply;
            TableOperationResult failed = await harness.SubmitAsync(harness.Envelope(
                RoundCommand.Discard(round.RoundId, 0, first),
                0,
                "durable-fault",
                decisionId: "d1"));
            Assert.Equal(TableOperationStatus.Failed, failed.Status);
            Assert.Equal(0UL, (await harness.Actor.GetSnapshotAsync(
                TestContext.Current.CancellationToken)).StateVersion.Value);
            TableTimingSnapshot after = await harness.Actor.GetTimingSnapshotAsync(
                TestContext.Current.CancellationToken);
            Assert.Equal(before.Seats[0].Timing!.DeadlineMillis, after.Seats[0].Timing!.DeadlineMillis);
            Assert.Equal(before.Seats[0].RemainingReserveMillis, after.Seats[0].RemainingReserveMillis);
            Assert.NotNull(after.Seats[0].PendingKey);

            harness.Store.ArmedFaultPoint = null;
            TableOperationResult retry = await harness.SubmitAsync(harness.Envelope(
                RoundCommand.Discard(round.RoundId, 0, first),
                0,
                "durable-retry",
                decisionId: "d1"));
            Assert.Equal(TableOperationStatus.Accepted, retry.Status);
            Assert.Equal(1UL, (await harness.Actor.GetSnapshotAsync(
                TestContext.Current.CancellationToken)).StateVersion.Value);
        }

        [Fact]
        public async Task RecoveryDropsStaleClosedWindowDecisionsAndAllowsLegalPlay()
        {
            RoundStateMachine window = BuildWindowRound();
            TileId target = Man(3, 3);
            RoundTransition chi = window.Execute(
                RoundCommand.Chi(window.RoundId, 1, target, Man(1, 0), Man(2, 0)));
            Assert.True(chi.IsAccepted, chi.Error?.ToString());
            RoundTransition closed = chi.State!.Execute(RoundCommand.Pass(window.RoundId, 2));
            Assert.True(closed.IsAccepted, closed.Error?.ToString());
            RoundStateMachine finalRound = closed.State!;
            Assert.Null(finalRound.Window);
            Assert.Equal(1, finalRound.CurrentSeat);

            ActorHarness harness = CreateActor(finalRound);
            await using (harness)
            {
                long before = (await harness.GetTimingSnapshotAsync()).Seats[0].RemainingReserveMillis;
                var stale = new[]
                {
                    new TableRecoveredDecision(
                        new DecisionKey(finalRound.RoundId, 1, "stale-chi"),
                        0L,
                        new[] { new LegalActionOption(LegalActionKind.Chi, 1, target) },
                        null,
                        1000L,
                        0L,
                        0L),
                    new TableRecoveredDecision(
                        new DecisionKey(finalRound.RoundId, 2, "stale-pon"),
                        0L,
                        new[] { new LegalActionOption(LegalActionKind.Pon, 2, target) },
                        null,
                        1000L,
                        0L,
                        0L)
                };
                ControlRegistrySnapshot control = P31Fixtures.ControlSnapshot(
                    Enumerable.Range(0, 4)
                        .Select(seat => P31Fixtures.Connected(SeatControllerKind.Computer, 0L, seat))
                        .ToArray());
                TableOperationResult begun = await harness.BeginRecoveryAsync("closed-window");
                Assert.Equal(TableOperationStatus.Configured, begun.Status);
                TableOperationResult recovered = await harness.CompleteRecoveryAsync(
                    new TableRecoveryBaseline(
                        finalRound,
                        new StateVersion(2),
                        new EventSeq(3),
                        new OwnerEpoch(1),
                        new TableControlRecoveryBaseline(control, 0L),
                        new TableRecoveryTiming(stale, new long[] { before, 0L, 0L, 0L })));
                Assert.True(
                    recovered.Status == TableOperationStatus.Configured,
                    "recovery status=" + recovered.Status + " reason=" + recovered.ReasonCode +
                    " detail=" + recovered.Detail);

                TableTimingSnapshot timing = await harness.GetTimingSnapshotAsync();
                Assert.All(timing.Seats, seat => Assert.Null(seat.PendingKey));
                Assert.All(timing.Seats, seat => Assert.Null(seat.Timing));
                harness.CommitPort.SetDurableBaseline(new StateVersion(2), new EventSeq(3));

                // A fresh legal turn decision can be opened and committed after the recovery;
                // the stale window entries did not poison the tick loop.
                TileId discard = finalRound.Tiles.TilesInZone(TileZone.Hand, 1)[0];
                var turn = new PendingDecision(
                    new DecisionKey(finalRound.RoundId, 1, "turn-1"),
                    0L,
                    new[] { new LegalActionOption(LegalActionKind.Discard, 1, discard) },
                    discard);
                Assert.Equal(
                    TableOperationStatus.Configured,
                    (await harness.OpenDecisionAsync(turn)).Status);
                TableOperationResult applied = await harness.SubmitAsync(P31Fixtures.Command(
                    RoundCommand.Discard(finalRound.RoundId, 1, discard),
                    2,
                    "recovered-discard",
                    new DecisionId("turn-1"),
                    recoveryGeneration: 1L));
                Assert.True(
                    applied.Status == TableOperationStatus.Accepted,
                    "discard status=" + applied.Status + " reason=" + applied.ReasonCode +
                    " detail=" + applied.Detail);
                Assert.Equal(3UL, (await harness.GetSnapshotAsync()).StateVersion.Value);
            }
        }

        private static TableRecoveryBaseline RecoveryBaseline(
            RoundStateMachine round,
            TableRecoveredDecision decision)
        {
            ControlRegistrySnapshot control = P31Fixtures.ControlSnapshot(
                Enumerable.Range(0, 4)
                    .Select(seat => P31Fixtures.Connected(SeatControllerKind.Computer, 0L, seat))
                    .ToArray());
            return new TableRecoveryBaseline(
                round,
                new StateVersion(2),
                new EventSeq(3),
                new OwnerEpoch(1),
                new TableControlRecoveryBaseline(control, 0L),
                new TableRecoveryTiming(new[] { decision }, new long[] { 0L, 0L, 0L, 0L }));
        }

        private static RoundStateMachine BuildClosedOpenKanRound()
        {
            RoundStateMachine window = BuildWindowRound();
            TileId target = Man(3, 3);
            RoundTransition openKan = window.Execute(
                RoundCommand.OpenKan(
                    window.RoundId,
                    2,
                    target,
                    Man(3, 0),
                    Man(3, 1),
                    Man(3, 2)));
            Assert.True(openKan.IsAccepted, openKan.Error?.ToString());

            RoundTransition chi = window.Execute(
                RoundCommand.Chi(window.RoundId, 1, target, Man(1, 0), Man(2, 0)));
            Assert.True(chi.IsAccepted, chi.Error?.ToString());
            RoundTransition closed = chi.State!.Execute(RoundCommand.Pass(window.RoundId, 2));
            Assert.True(closed.IsAccepted, closed.Error?.ToString());
            Assert.Null(closed.State!.Window);
            return closed.State!;
        }

        private static ActorHarness CreateWindowHarness()
        {
            return CreateActor(BuildWindowRound());
        }

        private static ActorHarness CreateActor(RoundStateMachine round)
        {
            var clock = new ManualTableClock { Now = 1000L };
            var commit = new RecordingCommitPort();
            var sink = new RecordingPublicationSink(commit);
            var options = new TableActorOptions
            {
                RoomId = "room-1",
                MailboxCapacity = 16,
                ControlReserve = 16,
                DecisionClockConfig = new DecisionClockConfig(1000L, 0L, ReserveScope.Round),
                InitialControllers = P31Fixtures.Controllers(
                    SeatControllerKind.Computer,
                    SeatControllerKind.Computer,
                    SeatControllerKind.Computer,
                    SeatControllerKind.Computer)
            };
            var actor = new TableActor(options, round, commit, clock, sink, null);
            return new ActorHarness(actor, clock, commit, sink, round);
        }

        private static PendingDecision ChiDecision(
            RoundStateMachine round,
            int seat,
            TileId target,
            string decisionId = "chi-1")
        {
            return new PendingDecision(
                new DecisionKey(round.RoundId, seat, decisionId),
                0L,
                new[]
                {
                    new LegalActionOption(LegalActionKind.Chi, seat, target),
                    new LegalActionOption(LegalActionKind.Pass, seat)
                },
                null);
        }

        private static PendingDecision PonDecision(
            RoundStateMachine round,
            int seat,
            TileId target,
            string decisionId = "pon-2")
        {
            return new PendingDecision(
                new DecisionKey(round.RoundId, seat, decisionId),
                0L,
                new[]
                {
                    new LegalActionOption(LegalActionKind.Pon, seat, target),
                    new LegalActionOption(LegalActionKind.Pass, seat)
                },
                null);
        }

        /// <summary>
        /// Fully explicit 136-tile legal state. Seat 0 has just discarded Man3 copy 3; seat 1
        /// (shimocha) holds Man1+Man2 for a real Chi and seat 2 holds two Man3 copies for a real
        /// Pon, so the response window has exactly two independently eligible seats.
        /// </summary>
        private static RoundStateMachine BuildWindowRound()
        {
            var placements = new TilePlacement[TileId.Count];
            var used = new bool[TileId.Count];
            var placed = new bool[TileId.Count];

            void Place(TileId tile, TilePosition position)
            {
                if (used[tile.Value])
                {
                    throw new InvalidOperationException("tile " + tile + " specified twice");
                }

                used[tile.Value] = true;
                placed[tile.Value] = true;
                placements[tile.Value] = new TilePlacement(tile, position);
            }

            TileId target = Man(3, 3);
            Place(Man(1, 0), TilePosition.Hand(1, 11));
            Place(Man(2, 0), TilePosition.Hand(1, 12));
            Place(Man(3, 0), TilePosition.Hand(2, 10));
            Place(Man(3, 1), TilePosition.Hand(2, 11));
            Place(Man(3, 2), TilePosition.Hand(2, 12));
            Place(target, TilePosition.Hand(0, 13));

            int cursor = 0;
            TileId NextUnused()
            {
                while (cursor < TileId.Count && used[cursor])
                {
                    cursor++;
                }

                if (cursor >= TileId.Count)
                {
                    throw new InvalidOperationException("fixture ran out of physical tiles");
                }

                used[cursor] = true;
                return new TileId(cursor);
            }

            for (int seat = 0; seat < 4; seat++)
            {
                for (int slot = 0; slot < 13; slot++)
                {
                    if (seat == 0 && slot == 13)
                    {
                        continue;
                    }

                    if (seat == 1 && slot >= 11)
                    {
                        continue;
                    }

                    if (seat == 2 && slot >= 10)
                    {
                        continue;
                    }

                    TileId tile = NextUnused();
                    placements[tile.Value] = new TilePlacement(tile, TilePosition.Hand(seat, slot));
                    placed[tile.Value] = true;
                }
            }

            for (int slot = 0; slot < 14; slot++)
            {
                TileId tile = NextUnused();
                placements[tile.Value] = new TilePlacement(tile, TilePosition.DeadWall(slot));
                placed[tile.Value] = true;
            }

            int wallCount = 0;
            for (int i = 0; i < TileId.Count; i++)
            {
                if (used[i])
                {
                    continue;
                }

                placements[i] = new TilePlacement(new TileId(i), TilePosition.Wall(wallCount));
                placed[i] = true;
                wallCount++;
            }

            for (int i = 0; i < TileId.Count; i++)
            {
                if (!placed[i])
                {
                    throw new InvalidOperationException("fixture left tile " + i + " unplaced");
                }
            }

            TileId dora = new TileId(Array.FindIndex(
                placements,
                placement => placement.Position == TilePosition.DeadWall(4)));
            TilePositionMap map = TilePositionMap.FromPlacements(
                placements,
                wallPointer: 0,
                wallEndExclusive: wallCount,
                rinshanPointer: 0,
                lastDiscard: null,
                revision: 0,
                rinshanDraw: null,
                doraIndicators: new[] { dora },
                kanCount: 0,
                drawnTile: new DrawnTileEvidence(target, 0, DrawSource.Wall));
            var spec = new RoundSpec(
                "round-resp",
                0,
                1,
                0,
                0,
                new long[] { 25000, 25000, 25000, 25000 },
                0);
            RoundStateMachine dealt = RoundStateMachine.CreateDealt(spec, map, RoundPhase.CheatWindow);
            RoundTransition window = dealt.Execute(
                RoundCommand.Discard(dealt.RoundId, 0, target, new[] { 1, 2 }));
            Assert.True(window.IsAccepted, window.Error?.ToString());
            ConservationReport report = TilePositionMap.Validate(window.State!.Tiles);
            Assert.True(report.IsValid, report.ToString());
            Assert.Equal(136, report.AssignedCount);
            Assert.Equal(0, report.UnassignedCount);
            return window.State!;
        }
    }
}
