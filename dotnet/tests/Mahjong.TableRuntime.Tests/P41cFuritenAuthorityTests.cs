using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Ai;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Mahjong.Domain.Rules;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    /// <summary>
    /// D-R1-03: the furiten fact must be derived from the concrete command inside the actor
    /// mailbox and persisted with the same state/timing/projection/receipt transaction. These
    /// probes use the real actor, real domain transitions and the real durable commit contract;
    /// they never stage a host pre-enqueue furiten slot.
    /// </summary>
    public sealed class P41cFuritenAuthorityTests
    {
        [Fact]
        public async Task PassOnAuthoritativeRonCandidateSetsTemporaryFuritenInTheCommit()
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            RoundStateMachine round = SettlementFixture.DoubleRiichiRonWindow();
            TableFuritenSnapshot? callbackSnapshot = null;
            await using N2DurableHarness harness = await N2DurableHarness.CreateAsync(
                round,
                configure: options => options.FuritenCommitted = snapshot => callbackSnapshot = snapshot);

            TableOperationResult opened = await harness.Actor.OpenDecisionAsync(
                new PendingDecision(
                    new DecisionKey(round.RoundId, 1, "furiten-pass-decision"),
                    0L,
                    new[]
                    {
                        new LegalActionOption(LegalActionKind.Ron, 1, Tile(22, 3), "ron"),
                        new LegalActionOption(LegalActionKind.Pass, 1, null, "pass")
                    },
                    null),
                ct);
            Assert.Equal(TableOperationStatus.Configured, opened.Status);

            TableOperationResult pass = await harness.Actor.SubmitAsync(
                Envelope(
                    harness,
                    RoundCommand.Pass(round.RoundId, 1),
                    "furiten-pass",
                    0UL,
                    new DecisionId("furiten-pass-decision")),
                ct);

            Assert.Equal(TableOperationStatus.Accepted, pass.Status);
            Assert.NotNull(callbackSnapshot);
            TableFuritenSnapshot installed = harness.Actor.FuritenForHost;
            Assert.True(installed.IsTemporary(1));
            Assert.True(installed.IsRiichi(1));
            Assert.True(callbackSnapshot!.IsTemporary(1));
            Assert.False(installed.IsTemporary(0));
            Assert.False(installed.IsTemporary(2));
            Assert.False(installed.IsTemporary(3));

            TableCommittedFactsSnapshot facts = Assert.IsType<TableCommittedFactsSnapshot>(
                await harness.Store.LoadCommittedFactsAsync(N2DurableHarness.Room, ct));
            TableProjectionContextData decoded = TableProjectionFactsCodec.Decode(
                facts.Payload,
                round.RoundId,
                new StateVersion(1UL));
            TableSeatPrivateFacts seat1 = decoded.PrivateFacts[1];
            Assert.True(seat1.TemporaryFuriten);
            Assert.True(seat1.RiichiFuriten);
            Assert.True(seat1.Furiten);
            Assert.False(decoded.PrivateFacts[2].Furiten);
        }

        [Fact]
        public async Task DrawClearsTemporaryFuritenButRiichiFuritenSurvives()
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            RoundStateMachine round = SettlementFixture.DoubleRiichiRonWindow();
            await using N2DurableHarness harness = await N2DurableHarness.CreateAsync(round);

            // The fixture ends on seat 2's discard; seat 1 is riichi and can Ron it.
            Assert.Equal(RoundPhase.ResponseWindow, round.Phase);
            Assert.True(round.IsRiichi(1));

            TableOperationResult opened = await harness.Actor.OpenDecisionAsync(
                new PendingDecision(
                    new DecisionKey(round.RoundId, 1, "furiten-riichi-pass"),
                    0L,
                    new[]
                    {
                        new LegalActionOption(LegalActionKind.Ron, 1, Tile(22, 3), "ron"),
                        new LegalActionOption(LegalActionKind.Pass, 1, null, "pass")
                    },
                    null),
                ct);
            Assert.Equal(TableOperationStatus.Configured, opened.Status);

            TableOperationResult pass = await harness.Actor.SubmitAsync(
                Envelope(
                    harness,
                    RoundCommand.Pass(round.RoundId, 1),
                    "furiten-riichi-pass",
                    0UL,
                    new DecisionId("furiten-riichi-pass")),
                ct);
            Assert.Equal(TableOperationStatus.Accepted, pass.Status);
            Assert.True(harness.Actor.FuritenForHost.IsTemporary(1));
            Assert.True(harness.Actor.FuritenForHost.IsRiichi(1));

            // Play the deterministic turns until seat 1 draws: its temporary bit clears while
            // the riichi bit remains for the rest of the round.
            await DrawForSeatAsync(harness, 1, ct);

            TableFuritenSnapshot afterDraw = harness.Actor.FuritenForHost;
            Assert.False(afterDraw.IsTemporary(1));
            Assert.True(afterDraw.IsRiichi(1));
            Assert.True(afterDraw.IsFuriten(1));
        }

        [Fact]
        public async Task RejectedAndReplayedPassesDoNotCrossWriteFuriten()
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            RoundStateMachine round = SettlementFixture.DoubleRiichiRonWindow();
            await using N2DurableHarness harness = await N2DurableHarness.CreateAsync(round);

            TableOperationResult opened = await harness.Actor.OpenDecisionAsync(
                new PendingDecision(
                    new DecisionKey(round.RoundId, 1, "furiten-iso-1"),
                    0L,
                    new[]
                    {
                        new LegalActionOption(LegalActionKind.Ron, 1, Tile(22, 3), "ron"),
                        new LegalActionOption(LegalActionKind.Pass, 1, null, "pass")
                    },
                    null),
                ct);
            Assert.Equal(TableOperationStatus.Configured, opened.Status);

            TableCommandEnvelope seat1Pass = Envelope(
                harness,
                RoundCommand.Pass(round.RoundId, 1),
                "furiten-iso-pass-1",
                0UL,
                new DecisionId("furiten-iso-1"));
            TableOperationResult first = await harness.Actor.SubmitAsync(seat1Pass, ct);
            Assert.Equal(TableOperationStatus.Accepted, first.Status);

            // Same request/payload while the decision is already gone: a replay must not create
            // a second side effect, and a stale/other-request pass must not write another seat.
            TableOperationResult replay = await harness.Actor.SubmitAsync(seat1Pass, ct);
            Assert.Equal(TableOperationStatus.Replayed, replay.Status);

            TableOperationResult stale = await harness.Actor.SubmitAsync(
                Envelope(
                    harness,
                    RoundCommand.Pass(round.RoundId, 2),
                    "furiten-iso-stale",
                    0UL,
                    new DecisionId("furiten-iso-2")),
                ct);
            Assert.NotEqual(TableOperationStatus.Accepted, stale.Status);
            Assert.False(harness.Actor.FuritenForHost.IsTemporary(2));

            TableFuritenSnapshot furiten = harness.Actor.FuritenForHost;
            Assert.True(furiten.IsTemporary(1));
            Assert.False(furiten.IsFuriten(0));
            Assert.False(furiten.IsFuriten(2));
            Assert.False(furiten.IsFuriten(3));
        }

        [Fact]
        public async Task ColdRecoveryRestoresOnlyTheCommittedFuritenBits()
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            CheatLedger ledger = DurableHarness.NewLedger();
            var source = DurableHarness.TerminalFactsSource();
            source.FuritenOverride = new TableFuritenSnapshot(round.RoundId, 1 << 1, 1 << 2);
            TableCheckpoint checkpoint = DurableHarness.InitialCheckpoint(
                round,
                ledger,
                projectionSource: source);

            await using DurableHarness harness = await DurableHarness.CreateAsync(
                initialCheckpoint: checkpoint,
                projectionSource: source);
            TableRecoveryResult result = await harness.Actor.RecoverFromStoreAsync(ct);

            Assert.True(result.Recovered, result.ReasonCode + " " + result.Detail);
            TableFuritenSnapshot recovered = harness.Actor.FuritenForHost;
            Assert.True(recovered.IsTemporary(1));
            Assert.False(recovered.IsRiichi(1));
            Assert.True(recovered.IsRiichi(2));
            Assert.False(recovered.IsTemporary(2));
            Assert.False(recovered.IsFuriten(0));
            Assert.False(recovered.IsFuriten(3));
        }

        [Fact]
        public async Task AuthoritativeTimeoutPassDerivesTheSameFuritenFact()
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            RoundStateMachine round = SettlementFixture.DoubleRiichiRonWindow();
            await using N2DurableHarness harness = await N2DurableHarness.CreateAsync(round);

            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.Actor.OpenDecisionAsync(
                    new PendingDecision(
                        new DecisionKey(round.RoundId, 1, "furiten-timeout"),
                        0L,
                        new[]
                        {
                            new LegalActionOption(LegalActionKind.Ron, 1, Tile(22, 3), "ron"),
                            new LegalActionOption(LegalActionKind.Pass, 1, null, "pass")
                        },
                        null),
                    ct)).Status);

            harness.Clock.Now = 5_000L;
            TableTickResult tick = await harness.Actor.TickAsync(TableTickRequest.Empty, ct);
            Assert.Contains(
                tick.Entries,
                entry => entry.Seat == 1 && entry.Kind == TickEntryKind.Timeout);

            TableFuritenSnapshot furiten = harness.Actor.FuritenForHost;
            Assert.True(furiten.IsTemporary(1));
            Assert.True(furiten.IsRiichi(1));
        }

        [Fact]
        public async Task AuthoritativeAiPassUsesTheSameFuritenDerivation()
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            RoundStateMachine round = SettlementFixture.DoubleRiichiRonWindow();
            await using N2DurableHarness harness = await N2DurableHarness.CreateAsync(
                round,
                configure: options => options.InitialControllers = new Dictionary<int, SeatControllerKind>
                {
                    { 0, SeatControllerKind.RemoteHuman },
                    { 1, SeatControllerKind.Computer },
                    { 2, SeatControllerKind.Computer },
                    { 3, SeatControllerKind.Computer }
                });

            TableOperationResult opened = await harness.Actor.OpenDecisionAsync(
                new PendingDecision(
                    new DecisionKey(round.RoundId, 1, "furiten-ai-pass"),
                    0L,
                    new[]
                    {
                        new LegalActionOption(LegalActionKind.Ron, 1, Tile(22, 3), "ron"),
                        new LegalActionOption(LegalActionKind.Pass, 1, null, "pass")
                    },
                    null),
                ct);
            Assert.Equal(TableOperationStatus.Configured, opened.Status);

            TableAiObservationResult captured = await harness.Actor.CaptureAiObservationAsync(1, ct);
            Assert.True(captured.IsCaptured);
            LegalActionOption pass = captured.Observation!.LegalActions.First(
                action => action.Kind == LegalActionKind.Pass);
            var intent = new CommandIntent(captured.Observation, pass, "furiten-ai", 1);
            TableTickResult tick = await harness.Actor.CompleteAiAsync(
                new AiDecision(captured.Observation, intent, 0.5d),
                ct);

            Assert.Contains(
                tick.Entries,
                entry => entry.Seat == 1 &&
                         entry.Transition != null &&
                         entry.Transition.Status == TableOperationStatus.Accepted);
            Assert.True(harness.Actor.FuritenForHost.IsTemporary(1));
            Assert.True(harness.Actor.FuritenForHost.IsRiichi(1));
        }

        [Fact]
        public async Task FailedOrCancelledCommitDoesNotInstallFuriten()
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            foreach (bool cancel in new[] { false, true })
            {
                RoundStateMachine round = SettlementFixture.DoubleRiichiRonWindow();
                await using N2Harness harness = N2Harness.Create(round);
                Assert.Equal(
                    TableOperationStatus.Configured,
                    (await harness.Actor.OpenDecisionAsync(
                        new PendingDecision(
                            new DecisionKey(round.RoundId, 1, "furiten-failed-" + cancel),
                            0L,
                            new[]
                            {
                                new LegalActionOption(LegalActionKind.Ron, 1, Tile(22, 3), "ron"),
                                new LegalActionOption(LegalActionKind.Pass, 1, null, "pass")
                            },
                            null),
                        ct)).Status);

                if (cancel)
                {
                    harness.CommitPort.CancelNext();
                }
                else
                {
                    harness.CommitPort.FailNext();
                }

                TableOperationResult result = await harness.Actor.SubmitAsync(
                    new TableCommandEnvelope(
                        RoundCommand.Pass(round.RoundId, 1),
                        new RequestId("furiten-failed-pass-" + cancel),
                        new StateVersion(0UL),
                        new OwnerEpoch(1),
                        new ControlEpoch(0),
                        new DecisionId("furiten-failed-" + cancel)),
                    ct);

                Assert.NotEqual(TableOperationStatus.Accepted, result.Status);
                Assert.False(harness.Actor.FuritenForHost.IsTemporary(1));
                Assert.False(harness.Actor.FuritenForHost.IsRiichi(1));
                TableSnapshot snapshot = await harness.Actor.GetSnapshotAsync(ct);
                Assert.Equal(0UL, snapshot.StateVersion.Value);
            }
        }

        [Fact]
        public async Task ForeignScopeOrWrongSeatDoesNotWriteFuriten()
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            RoundStateMachine round = SettlementFixture.DoubleRiichiRonWindow();
            await using N2DurableHarness harness = await N2DurableHarness.CreateAsync(round);

            TableScopeRecord foreignScope = harness.Scopes.Issue(
                "foreign-account",
                TableScopeRole.Player,
                "foreign-room",
                0UL,
                ulong.MaxValue,
                "foreign-scope");
            byte[] payload = Encoding.UTF8.GetBytes("foreign-scope-pass");
            var foreign = new TableCommandEnvelope(
                RoundCommand.Pass(round.RoundId, 1),
                new RequestId("furiten-foreign-scope"),
                new StateVersion(0UL),
                new OwnerEpoch(1),
                new ControlEpoch(0),
                null,
                0L,
                foreignScope.ScopeId,
                "TEST_PASS",
                TablePayloadSha256.Compute(payload));
            TableOperationResult foreignResult = await harness.Actor.SubmitAsync(foreign, ct);
            Assert.Equal(TableRuntimeReasonCode.ScopeRoomMismatch, foreignResult.ReasonCode);
            Assert.False(harness.Actor.FuritenForHost.IsFuriten(1));

            TableOperationResult wrongSeat = await harness.Actor.SubmitAsync(
                Envelope(
                    harness,
                    RoundCommand.Pass(round.RoundId, 2),
                    "furiten-wrong-seat",
                    0UL),
                ct);
            Assert.NotEqual(TableOperationStatus.Accepted, wrongSeat.Status);
            Assert.False(harness.Actor.FuritenForHost.IsFuriten(2));
            Assert.False(harness.Actor.FuritenForHost.IsFuriten(1));
        }

        [Fact]
        public async Task NewRoundOrNewRoomStartsWithNoFuriten()
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            RoundStateMachine round = SettlementFixture.RonOpening();
            await using N2DurableHarness harness = await N2DurableHarness.CreateAsync(round);
            Assert.False(harness.Actor.FuritenForHost.IsFuriten(0));
            Assert.False(harness.Actor.FuritenForHost.IsFuriten(1));

            // A new round is opened as a new local table/actor (LocalMatchCoordinator issues a
            // new room id), so the round identity change starts from the empty snapshot.
            RoundStateMachine nextRound = P31Fixtures.Dealt("next-round");
            await using N2DurableHarness next = await N2DurableHarness.CreateAsync(nextRound);
            TableFuritenSnapshot snapshot = next.Actor.FuritenForHost;
            Assert.Equal("next-round", snapshot.RoundId);
            Assert.False(snapshot.IsFuriten(0));
            Assert.False(snapshot.IsFuriten(1));
            Assert.False(snapshot.IsFuriten(2));
            Assert.False(snapshot.IsFuriten(3));
        }

        private static TableCommandEnvelope Envelope(
            N2DurableHarness harness,
            RoundCommand command,
            string requestId,
            ulong expectedVersion,
            DecisionId? decisionId = null)
        {
            byte[] payload = Encoding.UTF8.GetBytes(
                requestId + "|" + command.Action + "|" + expectedVersion);
            return new TableCommandEnvelope(
                command,
                new RequestId(requestId),
                new StateVersion(expectedVersion),
                new OwnerEpoch(1),
                new ControlEpoch(0),
                decisionId,
                0L,
                harness.Scope.ScopeId,
                "TEST_" + command.Action.ToString().ToUpperInvariant(),
                TablePayloadSha256.Compute(payload));
        }

        private static async Task DrawForSeatAsync(
            N2DurableHarness harness,
            int targetSeat,
            CancellationToken ct)
        {
            var trail = new StringBuilder();
            for (int guard = 0; guard < 24; guard++)
            {
                TableSnapshot snapshot = await harness.Actor.GetSnapshotAsync(ct);
                RoundStateMachine round = harness.Actor.GetRoundStateForHost();
                trail.Append(guard).Append(':').Append(round.Phase).Append('/')
                    .Append(round.CurrentSeat).Append(" v").Append(snapshot.StateVersion.Value).Append("; ");
                if (round.Phase == RoundPhase.CheatWindow &&
                    round.CurrentSeat == targetSeat &&
                    round.Tiles.DrawnTile.HasValue &&
                    round.Tiles.DrawnTile.Value.Seat == targetSeat)
                {
                    // The previous discard's automatic draw already reached the target seat.
                    return;
                }

                if ((round.Phase == RoundPhase.Draw || round.Phase == RoundPhase.KanDraw) &&
                    round.CurrentSeat == targetSeat)
                {
                    TableOperationResult draw = await harness.Actor.SubmitAsync(
                        Envelope(
                            harness,
                            RoundCommand.Draw(round.RoundId, targetSeat),
                            "furiten-draw-" + guard,
                            snapshot.StateVersion.Value),
                        ct);
                    Assert.Equal(TableOperationStatus.Accepted, draw.Status);
                    return;
                }

                if (round.Phase == RoundPhase.Draw || round.Phase == RoundPhase.KanDraw)
                {
                    TableOperationResult draw = await harness.Actor.SubmitAsync(
                        Envelope(
                            harness,
                            RoundCommand.Draw(round.RoundId, round.CurrentSeat),
                            "furiten-draw-" + guard,
                            snapshot.StateVersion.Value),
                        ct);
                    Assert.Equal(TableOperationStatus.Accepted, draw.Status);
                    continue;
                }

                if (round.Phase == RoundPhase.CheatWindow || round.Phase == RoundPhase.Discard)
                {
                    IReadOnlyList<TileId> hand = round.Tiles.TilesInZone(TileZone.Hand, round.CurrentSeat);
                    Assert.NotEmpty(hand);
                    DrawnTileEvidence? drawn = round.Tiles.DrawnTile;
                    TileId tile = drawn.HasValue && drawn.Value.Seat == round.CurrentSeat
                        ? drawn.Value.Tile
                        : hand[0];
                    TableOperationResult discard = await harness.Actor.SubmitAsync(
                        Envelope(
                            harness,
                            RoundCommand.Discard(round.RoundId, round.CurrentSeat, tile, Array.Empty<int>()),
                            "furiten-advance-discard-" + guard,
                            snapshot.StateVersion.Value),
                        ct);
                    Assert.Equal(TableOperationStatus.Accepted, discard.Status);
                    continue;
                }

                Assert.Fail("Unexpected phase while advancing to seat " + targetSeat + ": " + round.Phase + " trail=" + trail);
            }

            Assert.Fail("Seat " + targetSeat + " did not draw within the bounded turn sequence. trail=" + trail);
        }

        private static TileId Tile(int typeIndex, int copyIndex)
        {
            return TileId.FromTypeIndex(typeIndex, copyIndex);
        }
    }
}
