using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.LocalHost;
using Mahjong.TableRuntime;
using Xunit;
using PlayerSnapshot = Mahjong.Contracts.Wire.PlayerSnapshot;
using MeldType = Mahjong.Contracts.Wire.MeldType;
using LegalActionView = Mahjong.Contracts.Wire.LegalActionView;
using ProtocolEventType = Mahjong.Contracts.ProtocolEventType;

namespace Mahjong.LocalHost.Tests
{
    public sealed class C04PlayerActionChannelTests
    {
        [Theory]
        [InlineData(LocalPlayerActionScenario.Pon, ProtocolActionType.DeclarePon)]
        [InlineData(LocalPlayerActionScenario.Chi, ProtocolActionType.DeclareChi)]
        [InlineData(LocalPlayerActionScenario.Riichi, ProtocolActionType.DeclareRiichi)]
        [InlineData(LocalPlayerActionScenario.ClosedKan, ProtocolActionType.DeclareKan)]
        public async Task fixed_player_action_scenario_is_legal_and_applies_over_the_real_local_channel(
            string scenario,
            ProtocolActionType actionType)
        {
            await using var harness = new P41Harness();
            LocalPlayerActionScenario.ConfigureHostOptions(harness.Options, scenario);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));

            ITableChannel channel = await LocalTableHost.CreateChannelAsync(harness.Options, timeout.Token);
            string ticket = LocalTableHost.IssueTicket(harness.Options);
            await channel.OpenAsync(new TableChannelConnectRequest(ticket, "c04-player-action", null), timeout.Token);
            var session = new TableSession(channel, new InMemoryOutboxStore());
            Task pump = Task.CompletedTask;
            var visibleEvents = new System.Collections.Concurrent.ConcurrentQueue<TableVisibleEventBatch>();
            EventHandler<TableSnapshotChangedEventArgs> snapshotHandler = (_, args) =>
            {
                if (args.VisibleEventBatch.Kind == TableVisibleEventBatchKind.Incremental)
                    visibleEvents.Enqueue(args.VisibleEventBatch);
            };
            try
            {
                await session.OpenAsync(new TableSessionOpenRequest(ticket, "c04-player-action"), timeout.Token);
                session.SnapshotChanged += snapshotHandler;
                pump = PumpUntilCancelledAsync(session, timeout.Token);
                PlayerSnapshot before = await WaitForDecisionAsync(session, timeout.Token);
                Assert.Equal(0, session.Binding!.SeatId);
                ulong expectedInitialStateVersion = checked((ulong)LocalPlayerActionScenario.CreateInitialRound(scenario).Version);
                Assert.Equal(expectedInitialStateVersion, before.StateVersion);
                Assert.NotNull(before.PrivateView.Decision);
                LegalActionView candidate = Assert.Single(before.PrivateView.Decision!.Actions,
                    value => value.ActionType == (uint)actionType);

                string requestId = "c04-" + scenario + "-seat0-request-1";
                var receiptTask = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<TableReceiptResolvedEventArgs> handler = (_, args) =>
                {
                    if (string.Equals(args.Command.Key.RequestId.Value, requestId, StringComparison.Ordinal))
                        receiptTask.TrySetResult(args);
                };
                session.ReceiptResolved += handler;
                try
                {
                    BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                        before.RoundId,
                        before.PrivateView.Decision.DecisionId,
                        before.PrivateView.Decision.ControlEpoch,
                        actionType,
                        optionId: string.IsNullOrEmpty(candidate.OptionId) ? null : candidate.OptionId,
                        tileIds: candidate.TileIds.Count == 0 ? null : candidate.TileIds.ToArray(),
                        meldType: actionType == ProtocolActionType.DeclareKan ? 4u : (uint?)null);
                    var command = new ProtocolCommand(
                        CommandKind.SubmitAction,
                        payload,
                        new RequestId(requestId),
                        session.Binding.Scope.ScopeId,
                        new StateVersion(before.StateVersion),
                        new RoundId(before.RoundId),
                        decisionId: new DecisionId(before.PrivateView.Decision.DecisionId),
                        controlEpoch: new ControlEpoch(before.PrivateView.Decision.ControlEpoch));

                    CommandSubmission sent = await session.SubmitAsync(command, timeout.Token);
                    Assert.True(sent.Sent);
                    TableReceiptResolvedEventArgs receipt = await receiptTask.Task.WaitAsync(timeout.Token);
                    Assert.True(receipt.Resolution.ReceiptStatus == ReceiptStatus.Applied,
                        "The fixed player action was rejected: " + receipt.Resolution.ReasonCode);
                    Assert.Equal(requestId, receipt.Command.Key.RequestId.Value);
                    Assert.True(receipt.Resolution.CommandVersion.HasValue);
                    Assert.True(receipt.Resolution.EventSeq.HasValue);
                    Assert.True(receipt.Resolution.CommandVersion.Value.Value > before.StateVersion,
                        "Applied command version must advance beyond the subscribed baseline.");

                    CanonicalPayloadDocument typedPayload = CanonicalPayloadParser.Parse(payload);
                    Assert.Equal((ushort)CommandKind.SubmitAction, typedPayload.CommandKind);
                    Assert.Equal(before.RoundId, ReadCanonicalString(typedPayload, CommandPayloadFields.RoundIdField, "roundId"));
                    Assert.Equal(before.PrivateView.Decision.DecisionId,
                        ReadCanonicalString(typedPayload, CommandPayloadFields.DecisionIdField, "decisionId"));
                    Assert.Equal((ulong)before.PrivateView.Decision.ControlEpoch,
                        typedPayload.RequireUInt64(CommandPayloadFields.ControlEpochField, "controlEpoch"));
                    Assert.Equal((uint)actionType,
                        typedPayload.RequireUInt32(CommandPayloadFields.ActionType, "actionType"));

                    PlayerSnapshot after;
                    try
                    {
                        after = await WaitForVersionAsync(session, before.StateVersion, timeout.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        var localRuntime = Assert.IsType<LocalTableChannel>(channel).RuntimeForMatch!;
                        RoundStateMachine authorityRound = localRuntime.ActorForTest.GetRoundStateForHost();
                        TableStreamFrame? lastStreamFrame = localRuntime.LastStreamFrameForTest;
                        TableStreamTermination? termination = DebugTerminationSink.LastTermination;
                        throw new InvalidOperationException(
                            "Applied C04 action had no visible post-state: baseline=" + before.StateVersion +
                            " receiptVersion=" + receipt.Resolution.CommandVersion.Value.Value +
                            " receiptEventSeq=" + receipt.Resolution.EventSeq.Value.Value +
                            " current=" + (session.PlayerSnapshot?.StateVersion.ToString() ?? "<none>") +
                            " state=" + session.State + " lastError=" + (session.LastError ?? "<none>") +
                            " visibleBatches=" + visibleEvents.Count +
                            " authorityVersion=" + (await localRuntime.ActorForTest.GetSnapshotAsync(CancellationToken.None)).StateVersion.Value +
                            " roundVersion=" + authorityRound.Version + " phase=" + authorityRound.Phase +
                            " window=" + (authorityRound.Window == null ? "<none>" : authorityRound.Window.FromSeat.ToString()) +
                            " driver=" + localRuntime.DriverForTest.DescribeDriverForTest() +
                            " lastFrame=" + (lastStreamFrame == null ? "<none>" :
                                lastStreamFrame.StateVersion.Value + "/" + lastStreamFrame.EventSeq.Value +
                                "/" + lastStreamFrame.Cursor.StreamVersion.Value + ":" + lastStreamFrame.Cursor.ProjectionSeq.Value) +
                            " termination=" + (termination?.ReasonCode ?? "<none>") + ":" +
                                (termination?.Detail ?? string.Empty));
                    }
                    Assert.Equal(before.RoundId, after.RoundId);
                    Assert.True(before.StateVersion + 1UL == after.StateVersion,
                        "The first real submitted command must advance the pre-seeded checkpoint exactly once.");
                    var localChannel = Assert.IsType<LocalTableChannel>(channel);
                    RoundStateMachine authoritativeRound = localChannel.RuntimeForMatch!.ActorForTest.GetRoundStateForHost();
                    ConservationReport physicalAudit = TilePositionMap.Validate(authoritativeRound.Tiles);
                    Assert.True(physicalAudit.IsValid, physicalAudit.ToString());
                    Assert.Equal(136, physicalAudit.AssignedCount);
                    Assert.Equal(0, physicalAudit.UnassignedCount);
                    TableVisibleEventBatch actionBatch = await WaitForActionEventAsync(
                        visibleEvents, receipt.Resolution.CommandVersion!.Value.Value, before.RoundId, actionType, timeout.Token);
                    TableVisibleEvent actionEvent = Assert.Single(actionBatch.Events, value =>
                        value.CommandVersion == receipt.Resolution.CommandVersion.Value.Value &&
                        value.Seat == 0 && value.Type == EventFor(actionType));
                    Assert.True(actionEvent.EventSeq <= receipt.Resolution.EventSeq!.Value.Value,
                        "The matching action event must be at or before the Applied receipt's event high-water mark.");

                    uint[] expectedActionTiles = actionType == ProtocolActionType.DeclarePon
                        ? new uint[] { 0, 1, 2 }
                        : actionType == ProtocolActionType.DeclareChi
                            ? new uint[] { 0, 4, 8 }
                            : actionType == ProtocolActionType.DeclareKan
                                ? new uint[] { 0, 1, 2, 3 }
                                : Array.Empty<uint>();
                    if (expectedActionTiles.Length != 0)
                    {
                        Assert.Equal(expectedActionTiles.OrderBy(value => value), actionEvent.TileIds.OrderBy(value => value));
                        uint[] authoritativeMeldTiles = authoritativeRound.Tiles.TilesInZone(TileZone.Meld, 0)
                            .Select(tile => (uint)tile.Value).OrderBy(value => value).ToArray();
                        Assert.All(expectedActionTiles, tile => Assert.Contains(tile, authoritativeMeldTiles));
                        Assert.All(expectedActionTiles, tile => Assert.DoesNotContain(tile,
                            authoritativeRound.Tiles.TilesInZone(TileZone.Hand, 0).Select(value => (uint)value.Value)));
                    }

                    if (actionType == ProtocolActionType.DeclareRiichi)
                    {
                        Assert.True(after.PublicView.Board.Players[0].Riichi);
                        Assert.Equal(1u, after.PublicView.Board.RiichiSticks);
                        Assert.Equal(25_000L, before.PublicView.Board.Scores.Single(score => score.Seat == 0).Score);
                        Assert.Equal(24_000L, after.PublicView.Board.Scores.Single(score => score.Seat == 0).Score);
                    }
                    else if (actionType == ProtocolActionType.DeclareKan)
                    {
                        Assert.Contains(after.PrivateView.OwnerMelds, meld => meld.Type == MeldType.ClosedKan);
                    }
                    else
                    {
                        Assert.Contains(after.PublicView.Board.Players[0].Melds,
                            meld => actionType == ProtocolActionType.DeclarePon
                                    ? meld.Type == MeldType.Pon
                                    : meld.Type == MeldType.Chi);
                    }

                    if (actionType == ProtocolActionType.DeclareKan)
                    {
                        PlayerSnapshot replacement = after.PrivateView.HasDrawnTileId
                            ? after
                            : await WaitForReplacementDrawAsync(session, after.StateVersion, timeout.Token);
                        Assert.Equal(before.RoundId, replacement.RoundId);
                        Assert.True(replacement.PrivateView.HasDrawnTileId);
                        Assert.Equal(73u, replacement.PrivateView.DrawnTileId);
                        Assert.Single(replacement.PrivateView.OwnerMelds);
                        Assert.Equal(new uint[] { 0, 1, 2, 3 },
                            replacement.PrivateView.OwnerMelds[0].TileIds.OrderBy(value => value));
                        Assert.Contains(authoritativeRound.Tiles.TilesInZone(TileZone.Hand, 0), tile => tile.Value == 73);
                    }
                    else if (actionType == ProtocolActionType.DeclarePon || actionType == ProtocolActionType.DeclareChi)
                    {
                        MeldType expectedMeld = actionType == ProtocolActionType.DeclarePon ? MeldType.Pon : MeldType.Chi;
                        var meld = Assert.Single(after.PublicView.Board.Players[0].Melds, value => value.Type == expectedMeld);
                        Assert.Equal(expectedActionTiles.OrderBy(value => value), meld.TileIds.OrderBy(value => value));
                        Assert.Equal(expectedActionTiles.Length, meld.TileIds.Distinct().Count());
                        Assert.DoesNotContain(2u, after.PrivateView.ClosedTiles);
                        Assert.DoesNotContain(actionType == ProtocolActionType.DeclarePon ? 0u : 4u, after.PrivateView.ClosedTiles);
                    }
                }
                finally
                {
                    session.ReceiptResolved -= handler;
                }
            }
            finally
            {
                timeout.Cancel();
                try { await pump; } catch (OperationCanceledException) { } catch (InvalidOperationException) { }
                session.SnapshotChanged -= snapshotHandler;
                await session.CloseAsync("C04_DONE", CancellationToken.None);
                await channel.CloseAsync(new TableChannelCloseReason("C04_DONE", true), CancellationToken.None);
            }
        }

        private static async Task PumpUntilCancelledAsync(TableSession session, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (session.State == TableConnectionState.Faulted || session.State == TableConnectionState.Closed) return;
                await session.PumpOnceAsync(cancellationToken);
            }
        }

        private static async Task<PlayerSnapshot> WaitForDecisionAsync(TableSession session, CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (session.State == TableConnectionState.Faulted) throw new InvalidOperationException("Local session faulted: " + session.LastError);
                PlayerSnapshot? snapshot = session.PlayerSnapshot;
                if (snapshot?.PrivateView.Decision != null && snapshot.RoundId.Length != 0) return snapshot;
                await Task.Delay(10, cancellationToken);
            }
        }

        private static async Task<PlayerSnapshot> WaitForVersionAsync(
            TableSession session,
            ulong afterVersion,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (session.State == TableConnectionState.Faulted) throw new InvalidOperationException("Local session faulted: " + session.LastError);
                PlayerSnapshot? snapshot = session.PlayerSnapshot;
                if (snapshot != null && snapshot.StateVersion > afterVersion) return snapshot;
                await Task.Delay(10, cancellationToken);
            }
        }

        private static async Task<PlayerSnapshot> WaitForReplacementDrawAsync(
            TableSession session,
            ulong afterVersion,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (session.State == TableConnectionState.Faulted) throw new InvalidOperationException("Local session faulted: " + session.LastError);
                PlayerSnapshot? snapshot = session.PlayerSnapshot;
                if (snapshot != null && snapshot.StateVersion > afterVersion && snapshot.PrivateView.HasDrawnTileId)
                    return snapshot;
                await Task.Delay(10, cancellationToken);
            }
        }

        private static async Task<TableVisibleEventBatch> WaitForActionEventAsync(
            System.Collections.Concurrent.ConcurrentQueue<TableVisibleEventBatch> batches,
            ulong commandVersion,
            string roundId,
            ProtocolActionType actionType,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (TableVisibleEventBatch batch in batches)
                {
                    if (batch.StateVersion == commandVersion && batch.RoundId == roundId &&
                        batch.Events.Any(value => value.CommandVersion == commandVersion && value.Seat == 0 && value.Type == EventFor(actionType)))
                        return batch;
                }
                await Task.Delay(10, cancellationToken);
            }
        }

        private static ProtocolEventType EventFor(ProtocolActionType actionType)
        {
            switch (actionType)
            {
                case ProtocolActionType.DeclarePon: return ProtocolEventType.PonDeclared;
                case ProtocolActionType.DeclareChi: return ProtocolEventType.ChiDeclared;
                case ProtocolActionType.DeclareRiichi: return ProtocolEventType.RiichiDeclared;
                case ProtocolActionType.DeclareKan: return ProtocolEventType.KanDeclared;
                default: throw new ArgumentOutOfRangeException(nameof(actionType));
            }
        }

        private static string ReadCanonicalString(CanonicalPayloadDocument document, ushort fieldNumber, string fieldName)
        {
            byte[] encoded = Encoding.UTF8.GetBytes(document.RequireString(fieldNumber, fieldName));
            Assert.True(encoded.Length >= 4, "Canonical string field is missing its inner length prefix.");
            uint byteCount = BitConverter.ToUInt32(encoded, 0);
            Assert.Equal((uint)(encoded.Length - 4), byteCount);
            return new UTF8Encoding(false, true).GetString(encoded, 4, encoded.Length - 4);
        }
    }
}
