using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Domain.Cheat;
using Xunit;
using PlayerSnapshot = Mahjong.Contracts.Wire.PlayerSnapshot;
using WireGamePhase = Mahjong.Contracts.Wire.GamePhase;

namespace Mahjong.LocalHost.Tests
{
    [Collection("LocalHostSerial")]
    public sealed class C04PlayerCheatChannelTests
    {
        [Fact]
        public void ScenarioDealsAll136UniqueEntitiesAndPinsTheAcceptedEconomy()
        {
            LocalPlayerCheatScenarioFacts facts = LocalPlayerCheatScenario.GetFacts(
                LocalPlayerCheatScenarioKind.AuthorizedThreeCheats);

            Assert.Equal(136, facts.EntityIds.Count);
            Assert.Equal(Enumerable.Range(0, 136).Select(v => (uint)v), facts.EntityIds);
            Assert.Equal(14, facts.DealerHandEntityIds.Count);
            Assert.Equal(facts.DealerHandEntityIds[facts.DealerHandEntityIds.Count - 1], facts.DrawnTileId);
            Assert.Equal(120, facts.InitialCheatPoints);
            Assert.Equal(1, facts.TileMarkCost);
            Assert.Equal(10, facts.SleeveBloomCost);
            Assert.Equal(10, facts.BeamSwapCost);
            Assert.Equal(8, facts.LiveWallHeadEntityIds.Count);
            Assert.Equal(4, facts.ProposedTileMarkTileIds.Count);

            LocalTableHostOptions options = LocalPlayerCheatScenario.CreateOptions(
                System.IO.Path.Combine(
                    P41Harness.FindRepositoryRoot(),
                    "build",
                    "c04-player-cheat-tests",
                    Guid.NewGuid().ToString("N"),
                    "local"),
                LocalPlayerCheatScenario.DefaultRoomId,
                LocalPlayerCheatScenarioKind.AuthorizedThreeCheats);
            Assert.NotNull(options.InitialRound);
            Assert.True(options.AutoPlay);
            Assert.Equal(136, options.InitialRound!.Tiles.CountAt(Mahjong.Domain.TileZone.Hand) +
                              options.InitialRound.Tiles.CountAt(Mahjong.Domain.TileZone.Wall) +
                              options.InitialRound.Tiles.CountAt(Mahjong.Domain.TileZone.DeadWall) +
                              options.InitialRound.Tiles.CountAt(Mahjong.Domain.TileZone.Discard) +
                              options.InitialRound.Tiles.CountAt(Mahjong.Domain.TileZone.Meld) +
                              options.InitialRound.Tiles.CountAt(Mahjong.Domain.TileZone.Sleeve));
        }

        [Fact]
        public async Task AuthorizedThreeCheatsUseTheRealChannelWithExactFeesAnd136Entities()
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            await using var harness = new C04ScenarioHarness(
                LocalPlayerCheatScenarioKind.AuthorizedThreeCheats);
            (ITableChannel channel, TableSession session) = await harness.OpenAsync(ct);
            using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pumpCts.CancelAfter(TimeSpan.FromSeconds(90));
            Task pump = PumpAsync(session, pumpCts.Token);
            try
            {
                PlayerSnapshot snapshot = await WaitForSeatZeroCheatWindowAsync(session, pumpCts.Token);
                Assert.Equal(LocalPlayerCheatScenario.InitialRoundId, snapshot.RoundId);
                Assert.Equal(120, snapshot.PrivateView.Cheat.CheatPoints);
                Assert.Empty(snapshot.PrivateView.Cheat.LedgerEntries);

                LocalTableRecoveryFacts before = await CaptureFactsAsync(harness, pumpCts.Token);
                AssertPhysical(before);
                Assert.Equal(0, before.Ledger.EntryCount);
                Assert.Equal(120, before.Ledger.Balances[0]);

                // 闂佽В鍋撻柤鎭掑劤濞? exact configured cost 1, one private ledger entry, no tile movement.
                uint[] markTiles = LocalPlayerCheatScenario.ProposedTileMarkTileIds.ToArray();
                CheatReceipt mark = await harness.SubmitCheatAsync(
                    session,
                    snapshot,
                    CommandKind.SubmitTileMark,
                    CommandPayloadFactory.SubmitTileMark(
                        snapshot.RoundId,
                        snapshot.PrivateView.Decision.DecisionId,
                        snapshot.PrivateView.Decision.ControlEpoch,
                        markTiles),
                    "c04-g3-tilemark",
                    pumpCts.Token);
                AssertApplied(mark);
                PlayerSnapshot afterMark = await WaitForCheatPointsAsync(session, 119, pumpCts.Token);
                Assert.Single(afterMark.PrivateView.Cheat.LedgerEntries);
                Assert.Equal(-1, afterMark.PrivateView.Cheat.LedgerEntries[0].Amount);
                Assert.Equal(119, afterMark.PrivateView.Cheat.LedgerEntries[0].BalanceAfter);
                Assert.Equal("TILE_MARK", afterMark.PrivateView.Cheat.LedgerEntries[0].ReasonCode);
                Assert.Contains(afterMark.PrivateView.Cheat.TileMark!.Groups, g => g.Tiles.Select(t => t.TileId).SequenceEqual(markTiles));

                LocalTableRecoveryFacts afterMarkFacts = await CaptureFactsAsync(harness, pumpCts.Token);
                AssertPhysical(afterMarkFacts);
                Assert.Equal(1, afterMarkFacts.Ledger.EntryCount);
                Assert.Equal(119, afterMarkFacts.Ledger.Balances[0]);
                Assert.Equal(before.Physical.TilesSha256, afterMarkFacts.Physical.TilesSha256);
                Assert.True(ParseULong(afterMarkFacts.StateVersion) > ParseULong(before.StateVersion));
                Assert.True(ParseULong(afterMarkFacts.EventSeq) > ParseULong(before.EventSeq));
                AssertRequestApplied(afterMarkFacts, mark.RequestId, mark.RawPayloadSha256);

                // The initial dealer window rides on the deal-extra tile, which may not host
                // SleeveBloom. Discard the safe north honour and let the accepted local driver
                // advance to the dealer's next real draw.
                await DiscardAsync(session, afterMark, 120u, pumpCts.Token);
                snapshot = await AdvanceToNextSeatZeroTurnAsync(
                    session,
                    afterMark.StateVersion,
                    pumpCts.Token);
                Assert.True(snapshot.PrivateView.HasDrawnTileId);

                // SleeveBloom: activation costs 10; choosing SECOND moves the drawn tile to the
                // wall and the frozen wall candidate into the hand. The choice never charges again.
                uint drawnBefore = snapshot.PrivateView.DrawnTileId;
                CheatReceipt bloom = await harness.SubmitCheatAsync(
                    session,
                    snapshot,
                    CommandKind.SubmitSleeveBloom,
                    CommandPayloadFactory.SubmitSleeveBloom(
                        snapshot.RoundId,
                        snapshot.PrivateView.Decision.DecisionId,
                        snapshot.PrivateView.Decision.ControlEpoch),
                    "c04-g3-sleeve",
                    pumpCts.Token);
                AssertApplied(bloom);
                PlayerSnapshot afterBloomActivation = await WaitForCheatPointsAsync(session, 109, pumpCts.Token);
                Assert.Equal(2, afterBloomActivation.PrivateView.Cheat.LedgerEntries.Count);
                Assert.Equal(-10, afterBloomActivation.PrivateView.Cheat.LedgerEntries[1].Amount);
                Assert.Equal(109, afterBloomActivation.PrivateView.Cheat.LedgerEntries[1].BalanceAfter);
                Assert.Equal("SLEEVE_BLOOM", afterBloomActivation.PrivateView.Cheat.LedgerEntries[1].ReasonCode);
                var offer = afterBloomActivation.PrivateView.Cheat.SleeveBloom!;
                Assert.Equal(drawnBefore, offer.FirstTileId);
                Assert.True(offer.HasSecondTileId);
                uint second = offer.SecondTileId;

                CheatReceipt bloomChoice = await harness.SubmitCheatAsync(
                    session,
                    afterBloomActivation,
                    CommandKind.SubmitSleeveBloomChoice,
                    CommandPayloadFactory.SubmitSleeveBloomChoice(offer.OfferId, SleeveBloomChoice.Second),
                    "c04-g3-sleeve-choice",
                    pumpCts.Token);
                AssertApplied(bloomChoice);
                PlayerSnapshot afterBloomChoice = await WaitForNoSleeveOfferAsync(session, pumpCts.Token);
                Assert.Equal(109, afterBloomChoice.PrivateView.Cheat.CheatPoints);
                Assert.Equal(2, afterBloomChoice.PrivateView.Cheat.LedgerEntries.Count);
                Assert.Equal(second, afterBloomChoice.PrivateView.DrawnTileId);
                Assert.NotEqual(drawnBefore, afterBloomChoice.PrivateView.DrawnTileId);

                LocalTableRecoveryFacts afterBloomFacts = await CaptureFactsAsync(harness, pumpCts.Token);
                AssertPhysical(afterBloomFacts);
                Assert.Equal(2, afterBloomFacts.Ledger.EntryCount);
                Assert.Equal(109, afterBloomFacts.Ledger.Balances[0]);
                Assert.NotEqual(afterMarkFacts.Physical.TilesSha256, afterBloomFacts.Physical.TilesSha256);
                AssertRequestApplied(afterBloomFacts, bloom.RequestId, bloom.RawPayloadSha256);
                AssertRequestApplied(afterBloomFacts, bloomChoice.RequestId, bloomChoice.RawPayloadSha256);

                // Discard the swapped draw and advance one more full turn to the dealer's
                // third real draw, where BeamSwap is legal (one trick per turn).
                uint discardTile = afterBloomChoice.PrivateView.HasDrawnTileId
                    ? afterBloomChoice.PrivateView.DrawnTileId
                    : afterBloomChoice.PrivateView.ClosedTiles[0];
                await DiscardAsync(session, afterBloomChoice, discardTile, pumpCts.Token);
                PlayerSnapshot secondTurn = await AdvanceToNextSeatZeroTurnAsync(
                    session,
                    afterBloomChoice.StateVersion,
                    pumpCts.Token);
                Assert.True(secondTurn.PrivateView.HasDrawnTileId);

                CheatReceipt beam = await harness.SubmitCheatAsync(
                    session,
                    secondTurn,
                    CommandKind.SubmitBeamSwap,
                    CommandPayloadFactory.SubmitBeamSwap(
                        secondTurn.RoundId,
                        secondTurn.PrivateView.Decision.DecisionId,
                        secondTurn.PrivateView.Decision.ControlEpoch),
                    "c04-g3-beam",
                    pumpCts.Token);
                AssertApplied(beam);
                PlayerSnapshot afterBeamActivation = await WaitForCheatPointsAsync(session, 99, pumpCts.Token);
                Assert.Equal(3, afterBeamActivation.PrivateView.Cheat.LedgerEntries.Count);
                Assert.Equal(-10, afterBeamActivation.PrivateView.Cheat.LedgerEntries[2].Amount);
                Assert.Equal(99, afterBeamActivation.PrivateView.Cheat.LedgerEntries[2].BalanceAfter);
                Assert.Equal("BEAM_SWAP", afterBeamActivation.PrivateView.Cheat.LedgerEntries[2].ReasonCode);
                var beamOffer = afterBeamActivation.PrivateView.Cheat.BeamSwap!;
                Assert.NotEmpty(beamOffer.Candidates);
                Assert.NotEmpty(beamOffer.SelectableHandTileIds);
                uint handTile = beamOffer.SelectableHandTileIds[0];
                uint targetSlot = beamOffer.Candidates[0].Slot;

                CheatReceipt beamChoice = await harness.SubmitCheatAsync(
                    session,
                    afterBeamActivation,
                    CommandKind.SubmitBeamSwapChoice,
                    CommandPayloadFactory.SubmitBeamSwapChoice(
                        beamOffer.OfferId,
                        BeamSwapChoice.Swap,
                        handTile,
                        targetSlot),
                    "c04-g3-beam-choice",
                    pumpCts.Token);
                AssertApplied(beamChoice);
                PlayerSnapshot afterBeamChoice = await WaitForNoBeamOfferAsync(session, pumpCts.Token);
                Assert.Equal(99, afterBeamChoice.PrivateView.Cheat.CheatPoints);
                Assert.Equal(3, afterBeamChoice.PrivateView.Cheat.LedgerEntries.Count);

                LocalTableRecoveryFacts final = await CaptureFactsAsync(harness, pumpCts.Token);
                AssertPhysical(final);
                Assert.Equal(3, final.Ledger.EntryCount);
                Assert.Equal(459, final.Ledger.Points);
                Assert.Equal(new[] { 99, 120, 120, 120 }, final.Ledger.Balances.ToArray());
                Assert.Equal(120, final.Ledger.InitialPoints);
                AssertRequestApplied(final, beam.RequestId, beam.RawPayloadSha256);
                AssertRequestApplied(final, beamChoice.RequestId, beamChoice.RawPayloadSha256);
                Assert.True(ParseULong(final.StateVersion) > ParseULong(afterBloomFacts.StateVersion));

                pumpCts.Cancel();
                await pump;
            }
            finally
            {
                await harness.CloseAsync(session, channel);
            }
        }

        [Fact]
        public async Task InsufficientBalanceIsRejectedByTheRealAuthorityWithZeroDelta()
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            await using var harness = new C04ScenarioHarness(
                LocalPlayerCheatScenarioKind.InsufficientBalance);
            (ITableChannel channel, TableSession session) = await harness.OpenAsync(ct);
            using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pumpCts.CancelAfter(TimeSpan.FromSeconds(180));
            Task pump = PumpAsync(session, pumpCts.Token);
            try
            {
                PlayerSnapshot snapshot = await WaitForSeatZeroCheatWindowAsync(session, pumpCts.Token);

                // Turn 1 is the deal-extra window. Spend one point on TileMark, then discard.
                uint[] markTiles = LocalPlayerCheatScenario.ProposedTileMarkTileIds.ToArray();
                CheatReceipt mark = await harness.SubmitCheatAsync(
                    session,
                    snapshot,
                    CommandKind.SubmitTileMark,
                    CommandPayloadFactory.SubmitTileMark(
                        snapshot.RoundId,
                        snapshot.PrivateView.Decision.DecisionId,
                        snapshot.PrivateView.Decision.ControlEpoch,
                        markTiles),
                    "c04-g3-insufficient-mark",
                    pumpCts.Token);
                AssertApplied(mark);
                snapshot = await WaitForCheatPointsAsync(session, 119, pumpCts.Token);
                await DiscardAsync(session, snapshot, 120u, pumpCts.Token);
                snapshot = await AdvanceToNextSeatZeroTurnAsync(session, snapshot.StateVersion, pumpCts.Token);

                // Eleven real SleeveBloom activations at the configured 10 points each.
                for (int turn = 0; turn < 11; turn++)
                {
                    Assert.True(snapshot.PrivateView.HasDrawnTileId);
                    CheatReceipt bloom = await harness.SubmitCheatAsync(
                        session,
                        snapshot,
                        CommandKind.SubmitSleeveBloom,
                        CommandPayloadFactory.SubmitSleeveBloom(
                            snapshot.RoundId,
                            snapshot.PrivateView.Decision.DecisionId,
                            snapshot.PrivateView.Decision.ControlEpoch),
                        "c04-g3-insufficient-bloom-" + turn,
                        pumpCts.Token);
                    AssertApplied(bloom);
                    long expectedPoints = 119 - (10 * (turn + 1));
                    snapshot = await WaitForCheatPointsAsync(session, expectedPoints, pumpCts.Token);
                    var offer = snapshot.PrivateView.Cheat.SleeveBloom!;
                    CheatReceipt choice = await harness.SubmitCheatAsync(
                        session,
                        snapshot,
                        CommandKind.SubmitSleeveBloomChoice,
                        CommandPayloadFactory.SubmitSleeveBloomChoice(offer.OfferId, SleeveBloomChoice.First),
                        "c04-g3-insufficient-choice-" + turn,
                        pumpCts.Token);
                    AssertApplied(choice);
                    snapshot = await WaitForNoSleeveOfferAsync(session, pumpCts.Token);
                    uint tile = snapshot.PrivateView.HasDrawnTileId
                        ? snapshot.PrivateView.DrawnTileId
                        : snapshot.PrivateView.ClosedTiles[0];
                    await DiscardAsync(session, snapshot, tile, pumpCts.Token);
                    snapshot = await AdvanceToNextSeatZeroTurnAsync(session, snapshot.StateVersion, pumpCts.Token);
                }

                Assert.Equal(9, snapshot.PrivateView.Cheat.CheatPoints);
                LocalTableRecoveryFacts before = await CaptureFactsAsync(harness, pumpCts.Token);
                AssertPhysical(before);
                Assert.Equal(12, before.Ledger.EntryCount);
                Assert.Equal(9, before.Ledger.Balances[0]);

                CheatReceipt rejected = await harness.SubmitCheatAsync(
                    session,
                    snapshot,
                    CommandKind.SubmitSleeveBloom,
                    CommandPayloadFactory.SubmitSleeveBloom(
                        snapshot.RoundId,
                        snapshot.PrivateView.Decision.DecisionId,
                        snapshot.PrivateView.Decision.ControlEpoch),
                    "c04-g3-insufficient",
                    pumpCts.Token);
                AssertRejected(rejected);
                Assert.Contains("INSUFFICIENT", rejected.ReasonCode, StringComparison.OrdinalIgnoreCase);

                await Task.Delay(150, pumpCts.Token);
                LocalTableRecoveryFacts after = await CaptureFactsAsync(harness, pumpCts.Token);
                AssertPhysical(after);
                Assert.Equal(ParseULong(before.StateVersion), ParseULong(after.StateVersion));
                Assert.Equal(ParseULong(before.EventSeq), ParseULong(after.EventSeq));
                Assert.Equal(before.CheatVersion, after.CheatVersion);
                Assert.Equal(before.CheatFingerprint, after.CheatFingerprint);
                Assert.Equal(before.Physical.TilesSha256, after.Physical.TilesSha256);
                Assert.Equal(12, after.Ledger.EntryCount);
                Assert.Equal(9, after.Ledger.Balances[0]);
                Assert.Equal(before.Ledger.EntriesSha256, after.Ledger.EntriesSha256);
                AssertRequestRejected(after, rejected.RequestId, rejected.RawPayloadSha256);

                pumpCts.Cancel();
                await pump;
            }
            finally
            {
                await harness.CloseAsync(session, channel);
            }
        }

        [Fact]
        public async Task MissingDecisionAuthorizationIsRejectedByTheRealAuthorityWithZeroDelta()
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            await using var harness = new C04ScenarioHarness(
                LocalPlayerCheatScenarioKind.AuthorizedThreeCheats,
                autoPlay: false);
            (ITableChannel channel, TableSession session) = await harness.OpenAsync(ct);
            using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pumpCts.CancelAfter(TimeSpan.FromSeconds(30));
            Task pump = PumpAsync(session, pumpCts.Token);
            try
            {
                PlayerSnapshot snapshot = await WaitForSeatZeroCheatWindowAsync(session, pumpCts.Token);
                LocalTableRecoveryFacts before = await CaptureFactsAsync(harness, pumpCts.Token);
                AssertPhysical(before);
                Assert.Equal(0, before.Ledger.EntryCount);

                // A non-empty but unrelated decision id is not a valid authorization for the
                // pending human decision. The authority, not the client, must reject it.
                BusinessPayload payload = CommandPayloadFactory.SubmitTileMark(
                    snapshot.RoundId,
                    "c04-no-such-decision",
                    snapshot.PrivateView.Decision.ControlEpoch,
                    LocalPlayerCheatScenario.ProposedTileMarkTileIds.ToArray());
                CheatReceipt receipt = await harness.SubmitRawAsync(
                    session,
                    snapshot,
                    CommandKind.SubmitTileMark,
                    payload,
                    "c04-g3-unauthorized",
                    "c04-no-such-decision",
                    snapshot.PrivateView.Decision.ControlEpoch,
                    pumpCts.Token);
                AssertRejected(receipt);

                await Task.Delay(150, pumpCts.Token);
                LocalTableRecoveryFacts after = await CaptureFactsAsync(harness, pumpCts.Token);
                AssertPhysical(after);
                Assert.Equal(ParseULong(before.StateVersion), ParseULong(after.StateVersion));
                Assert.Equal(ParseULong(before.EventSeq), ParseULong(after.EventSeq));
                Assert.Equal(before.CheatVersion, after.CheatVersion);
                Assert.Equal(before.CheatFingerprint, after.CheatFingerprint);
                Assert.Equal(before.Physical.TilesSha256, after.Physical.TilesSha256);
                Assert.Equal(0, after.Ledger.EntryCount);
                Assert.Equal(120, after.Ledger.Balances[0]);
                Assert.Equal(before.Ledger.EntriesSha256, after.Ledger.EntriesSha256);
                AssertRequestRejected(after, receipt.RequestId, receipt.RawPayloadSha256);
                Assert.DoesNotContain(
                    after.Requests,
                    r => string.Equals(r.RequestId, receipt.RequestId, StringComparison.Ordinal) &&
                         string.Equals(r.Status, "APPLIED", StringComparison.OrdinalIgnoreCase));

                pumpCts.Cancel();
                await pump;
            }
            finally
            {
                await harness.CloseAsync(session, channel);
            }
        }

        // ---------------------------------------------------------------- helpers

        private sealed class C04ScenarioHarness : IAsyncDisposable
        {
            private readonly LocalTableHostOptions _options;

            public C04ScenarioHarness(
                LocalPlayerCheatScenarioKind kind,
                bool autoPlay = true)
            {
                string root = System.IO.Path.Combine(
                    P41Harness.FindRepositoryRoot(),
                    "build",
                    "c04-player-cheat-tests",
                    Guid.NewGuid().ToString("N"));
                System.IO.Directory.CreateDirectory(root);
                Root = root;
                _options = LocalPlayerCheatScenario.CreateOptions(
                    System.IO.Path.Combine(root, "local"),
                    LocalPlayerCheatScenario.DefaultRoomId,
                    kind,
                    "c04-owner");
                _options.AutoPlay = autoPlay;
            }

            public string Root { get; }

            public LocalTableHostOptions Options => _options;

            public async Task<(ITableChannel, TableSession)> OpenAsync(CancellationToken cancellationToken)
            {
                ITableChannel channel = await LocalTableHost.CreateChannelAsync(_options, cancellationToken);
                string channelTicket = LocalTableHost.IssueTicket(_options);
                await channel.OpenAsync(
                    new TableChannelConnectRequest(channelTicket, "c04-test", null),
                    cancellationToken);
                var session = new TableSession(channel, new InMemoryOutboxStore());
                string sessionTicket = LocalTableHost.IssueTicket(_options);
                await session.OpenAsync(new TableSessionOpenRequest(sessionTicket, "c04-test"), cancellationToken);
                for (int i = 0; i < 50 && session.State != TableConnectionState.Ready; i++)
                {
                    await session.PumpOnceAsync(cancellationToken);
                }

                Assert.True(
                    session.State == TableConnectionState.Ready,
                    "state=" + session.State + " last=" + session.LastError);
                return (channel, session);
            }

            public async Task<CheatReceipt> SubmitCheatAsync(
                TableSession session,
                PlayerSnapshot snapshot,
                CommandKind kind,
                BusinessPayload payload,
                string requestPrefix,
                CancellationToken cancellationToken)
            {
                return await SubmitRawAsync(
                    session,
                    snapshot,
                    kind,
                    payload,
                    requestPrefix,
                    snapshot.PrivateView.Decision.DecisionId,
                    snapshot.PrivateView.Decision.ControlEpoch,
                    cancellationToken);
            }

            public async Task<CheatReceipt> SubmitRawAsync(
                TableSession session,
                PlayerSnapshot snapshot,
                CommandKind kind,
                BusinessPayload payload,
                string requestPrefix,
                string decisionId,
                ulong controlEpoch,
                CancellationToken cancellationToken)
            {
                string requestId = requestPrefix + "-" + Guid.NewGuid().ToString("N");
                var command = new ProtocolCommand(
                    kind,
                    payload,
                    new RequestId(requestId),
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(snapshot.StateVersion),
                    new RoundId(snapshot.RoundId),
                    decisionId: string.IsNullOrEmpty(decisionId) ? null : new DecisionId(decisionId),
                    controlEpoch: new ControlEpoch(controlEpoch));
                var resolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<TableReceiptResolvedEventArgs> handler = (_, args) =>
                {
                    if (string.Equals(args.Command.RequestId, requestId, StringComparison.Ordinal))
                    {
                        resolved.TrySetResult(args);
                    }
                };
                session.ReceiptResolved += handler;
                try
                {
                    CommandSubmission submission = await session.SubmitAsync(command, cancellationToken);
                    Assert.True(submission.Sent, requestPrefix + " was not sent");
                    TableReceiptResolvedEventArgs receipt = await LocalHostCommandTests.WithTimeoutAsync(
                        resolved.Task,
                        cancellationToken);
                    return new CheatReceipt(
                        requestId,
                        submission.Command.DigestSha256,
                        RawSha256(payload),
                        receipt.Resolution.ReceiptStatus,
                        receipt.Resolution.ReasonCode);
                }
                finally
                {
                    session.ReceiptResolved -= handler;
                }
            }

            public async Task CloseAsync(TableSession session, ITableChannel channel)
            {
                try
                {
                    await session.CloseAsync("TEST_DONE", CancellationToken.None);
                }
                catch
                {
                }

                try
                {
                    await channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
                }
                catch
                {
                }
            }

            public ValueTask DisposeAsync()
            {
                try
                {
                    if (System.IO.Directory.Exists(Root))
                    {
                        System.IO.Directory.Delete(Root, true);
                    }
                }
                catch
                {
                }

                return ValueTask.CompletedTask;
            }
        }

        private readonly struct CheatReceipt
        {
            public CheatReceipt(
                string requestId,
                string payloadSha256,
                string rawPayloadSha256,
                ReceiptStatus status,
                string reasonCode)
            {
                RequestId = requestId;
                PayloadSha256 = payloadSha256;
                RawPayloadSha256 = rawPayloadSha256;
                Status = status;
                ReasonCode = reasonCode ?? string.Empty;
            }

            public string RequestId { get; }
            public string PayloadSha256 { get; }
            public string RawPayloadSha256 { get; }
            public ReceiptStatus Status { get; }
            public string ReasonCode { get; }
        }

        private static async Task<T> WithTimeoutAsync<T>(Task<T> task, CancellationToken cancellationToken)
        {
            Task completed = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, cancellationToken));
            if (completed != task)
            {
                throw new TimeoutException("C04 cheat wait timed out.");
            }

            return await task;
        }

        private static async Task PumpAsync(TableSession session, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await session.PumpOnceAsync(cancellationToken);
                }
            }
            catch (Exception exception) when (
                exception is OperationCanceledException ||
                exception is TableChannelClosedException ||
                exception is InvalidOperationException)
            {
            }
        }

        private static async Task<PlayerSnapshot> WaitForAsync(
            TableSession session,
            Func<PlayerSnapshot, bool> predicate,
            CancellationToken cancellationToken,
            string label)
        {
            for (int i = 0; i < 600; i++)
            {
                PlayerSnapshot? snapshot = session.PlayerSnapshot;
                if (snapshot != null && predicate(snapshot))
                {
                    return snapshot;
                }

                await Task.Delay(20, cancellationToken);
            }

            throw new TimeoutException("C04 wait failed: " + label);
        }

        private static Task<PlayerSnapshot> WaitForSeatZeroCheatWindowAsync(
            TableSession session,
            CancellationToken cancellationToken)
        {
            return WaitForAsync(
                session,
                snapshot => snapshot.PublicView.Board.Phase == WireGamePhase.CheatWindow &&
                            snapshot.PublicView.Board.CurrentSeat == 0 &&
                            snapshot.PrivateView?.Decision != null &&
                            snapshot.PrivateView.Cheat != null,
                cancellationToken,
                "seat0 cheat window");
        }

        /// <summary>
        /// Advances exactly one dealer turn using the same real client command route as the
        /// UI: discard when the dealer holds the turn and pass when the dealer is eligible in
        /// a response window. The three Computer seats are advanced by the accepted local
        /// driver. Returns only after the dealer's next real CheatWindow is confirmed.
        /// </summary>
        private static async Task<PlayerSnapshot> AdvanceToNextSeatZeroTurnAsync(
            TableSession session,
            ulong afterVersion,
            CancellationToken cancellationToken)
        {
            for (int i = 0; i < 1200; i++)
            {
                PlayerSnapshot? snapshot = session.PlayerSnapshot;
                if (snapshot != null && snapshot.StateVersion > afterVersion)
                {
                    if (snapshot.PublicView.Board.Phase == WireGamePhase.CheatWindow &&
                        snapshot.PublicView.Board.CurrentSeat == 0 &&
                        snapshot.PrivateView?.Decision != null)
                    {
                        return snapshot;
                    }

                    var decision = snapshot.PrivateView?.Decision;
                    if (decision != null)
                    {
                        WireGamePhase phase = snapshot.PublicView.Board.Phase;
                        if (snapshot.PublicView.Board.CurrentSeat == 0 &&
                            (phase == WireGamePhase.CheatWindow || phase == WireGamePhase.Discard))
                        {
                            uint tile = snapshot.PrivateView!.HasDrawnTileId
                                ? snapshot.PrivateView.DrawnTileId
                                : snapshot.PrivateView.ClosedTiles[0];
                            await SendActionAsync(
                                session,
                                snapshot,
                                ProtocolActionType.DiscardTile,
                                new[] { tile },
                                cancellationToken);
                        }
                        else if (phase == WireGamePhase.ResponseWindow ||
                                 phase == WireGamePhase.ChankanWindow)
                        {
                            bool hasPass = decision.Actions.Any(
                                action => action.ActionType == (uint)ProtocolActionType.Pass);
                            if (hasPass)
                            {
                                await SendActionAsync(
                                    session,
                                    snapshot,
                                    ProtocolActionType.Pass,
                                    null,
                                    cancellationToken);
                            }
                        }
                    }
                }

                await Task.Delay(20, cancellationToken);
            }

            throw new TimeoutException("Seat 0 did not reach the next CheatWindow.");
        }

        private static Task<PlayerSnapshot> WaitForCheatPointsAsync(
            TableSession session,
            long points,
            CancellationToken cancellationToken)
        {
            return WaitForAsync(
                session,
                snapshot => snapshot.PrivateView?.Cheat?.CheatPoints == points,
                cancellationToken,
                "cheat points " + points);
        }

        private static Task<PlayerSnapshot> WaitForNoSleeveOfferAsync(
            TableSession session,
            CancellationToken cancellationToken)
        {
            return WaitForAsync(
                session,
                snapshot => snapshot.PrivateView?.Cheat != null &&
                            snapshot.PrivateView.Cheat.SleeveBloom == null &&
                            snapshot.PrivateView.Cheat.SleeveBloomReservation == null,
                cancellationToken,
                "sleeve offer resolved");
        }

        private static Task<PlayerSnapshot> WaitForNoBeamOfferAsync(
            TableSession session,
            CancellationToken cancellationToken)
        {
            return WaitForAsync(
                session,
                snapshot => snapshot.PrivateView?.Cheat != null &&
                            snapshot.PrivateView.Cheat.BeamSwap == null,
                cancellationToken,
                "beam offer resolved");
        }

        private static async Task DiscardAsync(
            TableSession session,
            PlayerSnapshot snapshot,
            uint tileId,
            CancellationToken cancellationToken)
        {
            await SendActionAsync(session, snapshot, ProtocolActionType.DiscardTile, new[] { tileId }, cancellationToken);
        }

        private static async Task SendActionAsync(
            TableSession session,
            PlayerSnapshot snapshot,
            ProtocolActionType action,
            uint[]? tiles,
            CancellationToken cancellationToken)
        {
            string requestId = "c04-action-" + Guid.NewGuid().ToString("N");
            BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                snapshot.RoundId,
                snapshot.PrivateView!.Decision!.DecisionId,
                snapshot.PrivateView.Decision.ControlEpoch,
                action,
                tileIds: tiles);
            var command = new ProtocolCommand(
                CommandKind.SubmitAction,
                payload,
                new RequestId(requestId),
                session.Binding!.Scope.ScopeId,
                new StateVersion(snapshot.StateVersion),
                new RoundId(snapshot.RoundId),
                decisionId: new DecisionId(snapshot.PrivateView.Decision.DecisionId),
                controlEpoch: new ControlEpoch(snapshot.PrivateView.Decision.ControlEpoch));
            var resolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<TableReceiptResolvedEventArgs> handler = (_, args) =>
            {
                if (string.Equals(args.Command.RequestId, requestId, StringComparison.Ordinal))
                {
                    resolved.TrySetResult(args);
                }
            };
            session.ReceiptResolved += handler;
            try
            {
                CommandSubmission submission = await session.SubmitAsync(command, cancellationToken);
                Assert.True(submission.Sent, "action was not sent");
                TableReceiptResolvedEventArgs receipt = await LocalHostCommandTests.WithTimeoutAsync(
                    resolved.Task,
                    cancellationToken);
                Assert.True(
                    receipt.Resolution.ReceiptStatus == ReceiptStatus.Applied,
                    "action rejected: " + receipt.Resolution.ReasonCode);
            }
            finally
            {
                session.ReceiptResolved -= handler;
            }
        }

        private static async Task<LocalTableRecoveryFacts> CaptureFactsAsync(
            C04ScenarioHarness harness,
            CancellationToken cancellationToken)
        {
            LocalTableRecoveryCapture capture = await LocalTableHost.CaptureRecoveryAsync(
                harness.Options.SaveDirectory,
                cancellationToken);
            return capture.Facts;
        }

        private static string RawSha256(BusinessPayload payload)
        {
            return Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(payload.AsSpan()));
        }

        private static ulong ParseULong(string value)
        {
            return ulong.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void AssertPhysical(LocalTableRecoveryFacts facts)
        {
            Assert.Equal(136, facts.Physical.TileCount);
            Assert.Equal(136, facts.Physical.UniqueCount);
            Assert.False(string.IsNullOrWhiteSpace(facts.Physical.TilesSha256));
        }

        private static void AssertApplied(CheatReceipt receipt)
        {
            Assert.True(
                receipt.Status == ReceiptStatus.Applied,
                receipt.RequestId + " rejected: " + receipt.ReasonCode);
            Assert.False(string.IsNullOrWhiteSpace(receipt.RawPayloadSha256));
        }

        private static void AssertRejected(CheatReceipt receipt)
        {
            Assert.True(
                receipt.Status == ReceiptStatus.Rejected,
                receipt.RequestId + " was not rejected: " + receipt.Status);
            Assert.False(string.IsNullOrWhiteSpace(receipt.ReasonCode));
            Assert.False(string.IsNullOrWhiteSpace(receipt.RawPayloadSha256));
        }

        private static void AssertRequestApplied(
            LocalTableRecoveryFacts facts,
            string requestId,
            string payloadSha256)
        {
            LocalTableRecoveryRequestFact fact = Assert.Single(facts.Requests, r => string.Equals(r.RequestId, requestId, StringComparison.Ordinal));
            Assert.Equal("APPLIED", fact.Status, StringComparer.OrdinalIgnoreCase);
            Assert.True(fact.Confirmed);
            Assert.Equal(payloadSha256.ToUpperInvariant(), fact.PayloadSha256.ToUpperInvariant());
        }

        private static void AssertRequestRejected(
            LocalTableRecoveryFacts facts,
            string requestId,
            string payloadSha256)
        {
            LocalTableRecoveryRequestFact fact = Assert.Single(facts.Requests, r => string.Equals(r.RequestId, requestId, StringComparison.Ordinal));
            Assert.NotEqual("APPLIED", fact.Status, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(payloadSha256.ToUpperInvariant(), fact.PayloadSha256.ToUpperInvariant());
        }
    }
}
