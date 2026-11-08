using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
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
        public async Task unavailable_fixed_action_option_is_rejected_without_advancing_authority(
            string scenario,
            ProtocolActionType actionType)
        {
            await using var harness = new P41Harness();
            LocalPlayerActionScenario.ConfigureHostOptions(harness.Options, scenario);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));

            ITableChannel channel = await LocalTableHost.CreateChannelAsync(harness.Options, timeout.Token);
            string ticket = LocalTableHost.IssueTicket(harness.Options);
            await channel.OpenAsync(new TableChannelConnectRequest(ticket, "c04-player-action-negative", null), timeout.Token);
            var session = new TableSession(channel, new InMemoryOutboxStore());
            Task pump = Task.CompletedTask;
            try
            {
                await session.OpenAsync(new TableSessionOpenRequest(ticket, "c04-player-action-negative"), timeout.Token);
                pump = PumpUntilCancelledAsync(session, timeout.Token);
                PlayerSnapshot before = await WaitForDecisionAsync(session, timeout.Token);
                LegalActionView candidate = Assert.Single(before.PrivateView.Decision!.Actions,
                    value => value.ActionType == (uint)actionType);
                var localChannel = Assert.IsType<LocalTableChannel>(channel);
                LocalTableRecoveryCapture beforeCapture = await localChannel.RuntimeForMatch!.CaptureRecoveryAsync(timeout.Token);
                string requestId = "c04-" + scenario + "-unavailable-option";
                var receiptTask = new TaskCompletionSource<TableReceiptResolvedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                        optionId: "c04-unavailable-option",
                        tileIds: actionType == ProtocolActionType.DeclareRiichi
                            ? new uint[] { 135 }
                            : actionType == ProtocolActionType.DeclareKan
                                ? new uint[] { 130, 131, 132, 133 }
                                : new uint[] { 130, 131 },
                        meldType: actionType == ProtocolActionType.DeclareKan ? 4u : (uint?)null);
                    var command = new ProtocolCommand(
                        CommandKind.SubmitAction, payload, new RequestId(requestId), session.Binding!.Scope.ScopeId,
                        new StateVersion(before.StateVersion), new RoundId(before.RoundId),
                        decisionId: new DecisionId(before.PrivateView.Decision.DecisionId),
                        controlEpoch: new ControlEpoch(before.PrivateView.Decision.ControlEpoch));
                    CommandSubmission sent = await session.SubmitAsync(command, timeout.Token);
                    Assert.True(sent.Sent);
                    TableReceiptResolvedEventArgs receipt = await receiptTask.Task.WaitAsync(timeout.Token);
                    Assert.Equal(ReceiptStatus.Rejected, receipt.Resolution.ReceiptStatus);
                    Assert.False(receipt.Resolution.CommandVersion.HasValue);
                    Assert.False(receipt.Resolution.EventSeq.HasValue);
                    Assert.False(string.IsNullOrWhiteSpace(receipt.Resolution.ReasonCode));

                    LocalTableRecoveryCapture afterCapture = await localChannel.RuntimeForMatch!.CaptureRecoveryAsync(timeout.Token);
                    Assert.Equal(beforeCapture.Facts.RoundId, afterCapture.Facts.RoundId);
                    Assert.Equal(beforeCapture.Facts.StateVersion, afterCapture.Facts.StateVersion);
                    Assert.Equal(beforeCapture.Facts.EventSeq, afterCapture.Facts.EventSeq);
                    Assert.Equal(beforeCapture.Facts.Physical.TilesSha256, afterCapture.Facts.Physical.TilesSha256);
                    Assert.Equal(136, afterCapture.Facts.Physical.TileCount);
                    Assert.Equal(136, afterCapture.Facts.Physical.UniqueCount);
                    PlayerSnapshot after = session.PlayerSnapshot!;
                    Assert.Equal(before.RoundId, after.RoundId);
                    Assert.Equal(before.StateVersion, after.StateVersion);
                    Assert.Equal(before.EventSeq, after.EventSeq);
                }
                finally { session.ReceiptResolved -= handler; }
            }
            finally
            {
                timeout.Cancel();
                try { await pump; } catch (OperationCanceledException) { } catch (InvalidOperationException) { }
                await session.CloseAsync("C04_NEGATIVE_DONE", CancellationToken.None);
                await channel.CloseAsync(new TableChannelCloseReason("C04_NEGATIVE_DONE", true), CancellationToken.None);
            }
        }

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
            var visiblePublications = new System.Collections.Concurrent.ConcurrentQueue<TableSnapshotChangedEventArgs>();
            EventHandler<TableSnapshotChangedEventArgs> snapshotHandler = (_, args) =>
            {
                if (args.VisibleEventBatch.Kind == TableVisibleEventBatchKind.Incremental)
                {
                    visiblePublications.Enqueue(args);
                }
            };
            try
            {
                await session.OpenAsync(new TableSessionOpenRequest(ticket, "c04-player-action"), timeout.Token);
                session.SnapshotChanged += snapshotHandler;
                pump = PumpUntilCancelledAsync(session, timeout.Token);
                PlayerSnapshot before = await WaitForDecisionAsync(session, timeout.Token);
                Assert.Equal(0, session.Binding!.SeatId);
                var localChannel = Assert.IsType<LocalTableChannel>(channel);
                LocalTableRecoveryCapture baselineCapture = await localChannel.RuntimeForMatch!.CaptureRecoveryAsync(timeout.Token);
                Assert.Equal(before.StateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    baselineCapture.Facts.StateVersion);
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
                    LocalTableRecoveryCapture acceptedCapture = await localChannel.RuntimeForMatch!.CaptureRecoveryAsync(timeout.Token);
                    LocalTableRecoveryCapture rawAfterCapture = acceptedCapture;

                    CanonicalPayloadDocument typedPayload = CanonicalPayloadParser.Parse(payload);
                    Assert.Equal((ushort)CommandKind.SubmitAction, typedPayload.CommandKind);
                    Assert.Equal(before.RoundId, ReadCanonicalString(typedPayload, CommandPayloadFields.RoundIdField, "roundId"));
                    Assert.Equal(before.PrivateView.Decision.DecisionId,
                        ReadCanonicalString(typedPayload, CommandPayloadFields.DecisionIdField, "decisionId"));
                    Assert.Equal((ulong)before.PrivateView.Decision.ControlEpoch,
                        typedPayload.RequireUInt64(CommandPayloadFields.ControlEpochField, "controlEpoch"));
                    Assert.Equal((uint)actionType,
                        typedPayload.RequireUInt32(CommandPayloadFields.ActionType, "actionType"));

                    ulong targetVersion = receipt.Resolution.CommandVersion!.Value.Value;
                    TableSnapshotChangedEventArgs? laterRiichiPublication = null;
                    if (actionType == ProtocolActionType.DeclareRiichi)
                    {
                        // Reproduce the CI condition deterministically: the driver's later
                        // legal command is already published before this test reads the
                        // target. session.PlayerSnapshot is therefore beyond the action;
                        // the target must still be selected by its exact receipt version
                        // and event, never by the latest snapshot.
                        laterRiichiPublication = await WaitForLaterPublicationAsync(
                            visiblePublications, targetVersion, timeout.Token);
                        Assert.True(laterRiichiPublication.PlayerSnapshot!.StateVersion > targetVersion);
                        Assert.True((session.PlayerSnapshot?.StateVersion ?? 0UL) > targetVersion,
                            "The fixed Riichi runtime must already have published a later legal command before target selection.");
                    }

                    TableSnapshotChangedEventArgs acceptedPublication = await WaitForAppliedPublicationAsync(
                        visiblePublications, targetVersion, before.RoundId, actionType, timeout.Token);
                    PlayerSnapshot after = acceptedPublication.PlayerSnapshot
                        ?? throw new InvalidOperationException("The matching incremental publication omitted its PlayerSnapshot.");
                    TableVisibleEventBatch actionBatch = acceptedPublication.VisibleEventBatch;
                    Assert.Equal(before.RoundId, after.RoundId);
                    Assert.Equal(targetVersion, after.StateVersion);
                    Assert.True(before.StateVersion + 1UL == after.StateVersion,
                        "The first real submitted command must advance the pre-seeded checkpoint exactly once.");
                    RoundStateMachine authoritativeRound = localChannel.RuntimeForMatch!.ActorForTest.GetRoundStateForHost();
                    ConservationReport physicalAudit = TilePositionMap.Validate(authoritativeRound.Tiles);
                    Assert.True(physicalAudit.IsValid, physicalAudit.ToString());
                    Assert.Equal(136, physicalAudit.AssignedCount);
                    Assert.Equal(0, physicalAudit.UnassignedCount);
                    Assert.Equal(actionBatch.Cursor.ProjectionSeq.Value, after.Cursor.ProjectionSeq);
                    Assert.Single(visiblePublications, value =>
                        value.PlayerSnapshot != null &&
                        value.PlayerSnapshot.StateVersion == targetVersion &&
                        value.VisibleEventBatch.StateVersion == targetVersion &&
                        value.VisibleEventBatch.Events.Any(actionEventValue =>
                            actionEventValue.CommandVersion == targetVersion &&
                            actionEventValue.Seat == 0 &&
                            actionEventValue.Type == EventFor(actionType)));
                    TableVisibleEvent actionEvent = Assert.Single(actionBatch.Events, value =>
                        value.CommandVersion == receipt.Resolution.CommandVersion.Value.Value &&
                        value.Seat == 0 && value.Type == EventFor(actionType));
                    Assert.True(actionEvent.EventSeq <= receipt.Resolution.EventSeq!.Value.Value,
                        "The matching action event must be at or before the Applied receipt's event high-water mark.");
                    string exactActionPhysicalAudit = LocalPlayerActionScenario.ReadCommittedActionPhysicalAudit(
                        acceptedCapture.AuthoritySaveBytes,
                        requestId,
                        receipt.Command.Key.ScopeId.Value,
                        before.RoundId,
                        before.StateVersion,
                        actionBatch.StateVersion,
                        actionEvent.EventSeq,
                        EventFor(actionType).ToString(),
                        0);
                    Assert.Equal(136, exactActionPhysicalAudit.Split('\n').Length);
                    Assert.Throws<InvalidOperationException>(() => LocalPlayerActionScenario.ReadCommittedActionPhysicalAudit(
                        acceptedCapture.AuthoritySaveBytes, "foreign-request", receipt.Command.Key.ScopeId.Value,
                        before.RoundId, before.StateVersion, actionBatch.StateVersion, actionEvent.EventSeq,
                        EventFor(actionType).ToString(), 0));
                    Assert.Throws<InvalidOperationException>(() => LocalPlayerActionScenario.ReadCommittedActionPhysicalAudit(
                        acceptedCapture.AuthoritySaveBytes, requestId, "foreign-scope",
                        before.RoundId, before.StateVersion, actionBatch.StateVersion, actionEvent.EventSeq,
                        EventFor(actionType).ToString(), 0));
                    Assert.Throws<InvalidOperationException>(() => LocalPlayerActionScenario.ReadCommittedActionPhysicalAudit(
                        acceptedCapture.AuthoritySaveBytes, requestId, receipt.Command.Key.ScopeId.Value,
                        before.RoundId, before.StateVersion, actionBatch.StateVersion + 1, actionEvent.EventSeq,
                        EventFor(actionType).ToString(), 0));
                    Assert.Throws<InvalidOperationException>(() => LocalPlayerActionScenario.ReadCommittedActionPhysicalAudit(
                        acceptedCapture.AuthoritySaveBytes, requestId, receipt.Command.Key.ScopeId.Value,
                        before.RoundId, before.StateVersion, actionBatch.StateVersion, actionEvent.EventSeq + 1,
                        EventFor(actionType).ToString(), 0));
                    Assert.Throws<InvalidOperationException>(() => LocalPlayerActionScenario.ReadCommittedActionPhysicalAudit(
                        acceptedCapture.AuthoritySaveBytes, requestId, receipt.Command.Key.ScopeId.Value,
                        before.RoundId + "-foreign", before.StateVersion, actionBatch.StateVersion, actionEvent.EventSeq,
                        EventFor(actionType).ToString(), 0));
                    Assert.Throws<InvalidOperationException>(() => LocalPlayerActionScenario.ReadCommittedActionPhysicalAudit(
                        acceptedCapture.AuthoritySaveBytes, requestId, receipt.Command.Key.ScopeId.Value,
                        before.RoundId, before.StateVersion, actionBatch.StateVersion, actionEvent.EventSeq,
                        EventFor(actionType).ToString(), 1));
                    Assert.Throws<InvalidOperationException>(() => LocalPlayerActionScenario.ReadCommittedActionPhysicalAudit(
                        acceptedCapture.AuthoritySaveBytes, requestId, receipt.Command.Key.ScopeId.Value,
                        before.RoundId, before.StateVersion, actionBatch.StateVersion, actionEvent.EventSeq,
                        ProtocolEventType.TileDiscarded.ToString(), 0));
                    LocalSave tamperedSave = LocalSaveCodec.DecodeEnvelope(acceptedCapture.AuthoritySaveBytes);
                    LocalDurableOperation acceptedOperation = Assert.Single(tamperedSave.Operations, value =>
                        value.Journal?.CommandVersion.Value == actionBatch.StateVersion);
                    acceptedOperation.NextStateFingerprint ^= 1UL;
                    byte[] fingerprintMismatch = LocalSaveCodec.EncodeEnvelope(tamperedSave);
                    Assert.Throws<InvalidOperationException>(() => LocalPlayerActionScenario.ReadCommittedActionPhysicalAudit(
                        fingerprintMismatch, requestId, receipt.Command.Key.ScopeId.Value,
                        before.RoundId, before.StateVersion, actionBatch.StateVersion, actionEvent.EventSeq,
                        EventFor(actionType).ToString(), 0));

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
                        Assert.True(actionEvent.Payload.DoubleRiichi,
                            "The fixed first uninterrupted discard must publish the authoritative double-riichi fact.");
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
                    if (actionType == ProtocolActionType.DeclareRiichi)
                    {
                        TableSnapshotChangedEventArgs later = laterRiichiPublication
                            ?? throw new InvalidOperationException("The Riichi later-publication regression was not established.");
                        PlayerSnapshot latest = session.PlayerSnapshot
                            ?? throw new InvalidOperationException("The Riichi later publication has no current PlayerSnapshot.");
                        Assert.True(later.PlayerSnapshot!.StateVersion > actionBatch.StateVersion);
                        Assert.True(latest.StateVersion > actionBatch.StateVersion);
                        Assert.True(latest.StateVersion >= later.PlayerSnapshot.StateVersion);
                        Assert.Equal(actionBatch.StateVersion, acceptedPublication.PlayerSnapshot!.StateVersion);
                        TableSnapshotChangedEventArgs retainedTarget = Assert.Single(visiblePublications, value =>
                            value.PlayerSnapshot != null &&
                            value.PlayerSnapshot.StateVersion == actionBatch.StateVersion &&
                            value.VisibleEventBatch.Events.Any(valueEvent =>
                                valueEvent.CommandVersion == actionBatch.StateVersion &&
                                valueEvent.Seat == 0 &&
                                valueEvent.Type == EventFor(actionType)));
                        Assert.Same(acceptedPublication, retainedTarget);
                        LocalTableRecoveryCapture laterCapture = await localChannel.RuntimeForMatch!.CaptureRecoveryAsync(timeout.Token);
                        Assert.True(ulong.Parse(laterCapture.Facts.StateVersion, System.Globalization.CultureInfo.InvariantCulture) > actionBatch.StateVersion);
                        Assert.True(ulong.Parse(laterCapture.Facts.StateVersion, System.Globalization.CultureInfo.InvariantCulture) >= latest.StateVersion);
                        string retainedActionAudit = LocalPlayerActionScenario.ReadCommittedActionPhysicalAudit(
                            laterCapture.AuthoritySaveBytes,
                            requestId,
                            receipt.Command.Key.ScopeId.Value,
                            before.RoundId,
                            before.StateVersion,
                            actionBatch.StateVersion,
                            actionEvent.EventSeq,
                            EventFor(actionType).ToString(),
                            0);
                        Assert.Equal(136, retainedActionAudit.Split('\n').Length);
                        rawAfterCapture = laterCapture;
                    }
                    WriteVerifierToolFixture(scenario, payload, session, before, after, baselineCapture, rawAfterCapture,
                        receipt, actionBatch, actionEvent);
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

        [Fact]
        public async Task chi_without_a_candidate_is_rejected_without_advancing_authority()
        {
            await using var harness = new P41Harness();
            LocalPlayerActionScenario.ConfigureHostOptions(harness.Options, LocalPlayerActionScenario.Pon);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            ITableChannel channel = await LocalTableHost.CreateChannelAsync(harness.Options, timeout.Token);
            string ticket = LocalTableHost.IssueTicket(harness.Options);
            await channel.OpenAsync(new TableChannelConnectRequest(ticket, "c04-no-chi", null), timeout.Token);
            var session = new TableSession(channel, new InMemoryOutboxStore());
            Task pump = Task.CompletedTask;
            try
            {
                await session.OpenAsync(new TableSessionOpenRequest(ticket, "c04-no-chi"), timeout.Token);
                pump = PumpUntilCancelledAsync(session, timeout.Token);
                PlayerSnapshot before = await WaitForDecisionAsync(session, timeout.Token);
                Assert.DoesNotContain(before.PrivateView.Decision!.Actions,
                    value => value.ActionType == (uint)ProtocolActionType.DeclareChi);
                var local = Assert.IsType<LocalTableChannel>(channel);
                LocalTableRecoveryCapture beforeCapture = await local.RuntimeForMatch!.CaptureRecoveryAsync(timeout.Token);
                const string requestId = "c04-no-chi-request";
                var receiptTask = new TaskCompletionSource<TableReceiptResolvedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<TableReceiptResolvedEventArgs> handler = (_, args) =>
                {
                    if (args.Command.Key.RequestId.Value == requestId) receiptTask.TrySetResult(args);
                };
                session.ReceiptResolved += handler;
                try
                {
                    BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                        before.RoundId, before.PrivateView.Decision.DecisionId,
                        before.PrivateView.Decision.ControlEpoch,
                        ProtocolActionType.DeclareChi,
                        optionId: "not-advertised",
                        tileIds: new uint[] { 130, 131 });
                    var command = new ProtocolCommand(
                        CommandKind.SubmitAction, payload, new RequestId(requestId), session.Binding!.Scope.ScopeId,
                        new StateVersion(before.StateVersion), new RoundId(before.RoundId),
                        decisionId: new DecisionId(before.PrivateView.Decision.DecisionId),
                        controlEpoch: new ControlEpoch(before.PrivateView.Decision.ControlEpoch));
                    Assert.True((await session.SubmitAsync(command, timeout.Token)).Sent);
                    TableReceiptResolvedEventArgs receipt = await receiptTask.Task.WaitAsync(timeout.Token);
                    Assert.Equal(ReceiptStatus.Rejected, receipt.Resolution.ReceiptStatus);
                    LocalTableRecoveryCapture afterCapture = await local.RuntimeForMatch!.CaptureRecoveryAsync(timeout.Token);
                    Assert.Equal(beforeCapture.Facts.StateVersion, afterCapture.Facts.StateVersion);
                    Assert.Equal(beforeCapture.Facts.EventSeq, afterCapture.Facts.EventSeq);
                    Assert.Equal(beforeCapture.Facts.Physical.TilesSha256, afterCapture.Facts.Physical.TilesSha256);
                    Assert.Equal(before.StateVersion, session.PlayerSnapshot!.StateVersion);
                    Assert.Equal(before.RoundId, session.PlayerSnapshot.RoundId);
                }
                finally { session.ReceiptResolved -= handler; }
            }
            finally
            {
                timeout.Cancel();
                try { await pump; } catch (OperationCanceledException) { } catch (InvalidOperationException) { }
                await session.CloseAsync("C04_NO_CHI_DONE", CancellationToken.None);
                await channel.CloseAsync(new TableChannelCloseReason("C04_NO_CHI_DONE", true), CancellationToken.None);
            }
        }

        private static void WriteVerifierToolFixture(
            string scenario,
            BusinessPayload payload,
            TableSession session,
            PlayerSnapshot before,
            PlayerSnapshot after,
            LocalTableRecoveryCapture baseline,
            LocalTableRecoveryCapture accepted,
            TableReceiptResolvedEventArgs receipt,
            TableVisibleEventBatch batch,
            TableVisibleEvent actionEvent)
        {
            string? outputRoot = Environment.GetEnvironmentVariable("C04_ACTION_VERIFIER_FIXTURE_DIR");
            if (string.IsNullOrWhiteSpace(outputRoot)) return;
            string root = Path.GetFullPath(outputRoot);
            Directory.CreateDirectory(root);
            LocalTableRecoveryFacts beforeFacts = baseline.Facts;
            LocalTableRecoveryFacts afterFacts = accepted.Facts;
            var ownMeld = after.PrivateView.OwnerMelds.FirstOrDefault();
            var value = new Dictionary<string, object?>
            {
                ["EvidenceSource"] = "TOOL_FIXTURE_LOCALHOST_CHANNEL",
                ["CaseId"] = CaseForScenario(scenario),
                ["AccountId"] = session.Binding!.Scope.AccountId,
                ["BaselineAccountId"] = session.Binding.Scope.AccountId,
                ["AfterAccountId"] = session.Binding.Scope.AccountId,
                ["RequestId"] = receipt.Command.Key.RequestId.Value,
                ["ScopeId"] = receipt.Command.Key.ScopeId.Value,
                ["BaselineScopeId"] = receipt.Command.Key.ScopeId.Value,
                ["AfterScopeId"] = receipt.Command.Key.ScopeId.Value,
                ["PayloadBase64"] = Convert.ToBase64String(payload.CopyBytes()),
                ["PayloadSha256"] = payload.Digest.ToHex(),
                ["OptionId"] = ReadOptionalCanonicalString(CanonicalPayloadParser.Parse(payload), CommandPayloadFields.OptionId),
                ["BaselineRoomId"] = beforeFacts.RoomId,
                ["BaselineSessionId"] = before.SessionId,
                ["BaselineTableId"] = batch.TableId,
                ["BaselineRoundId"] = before.RoundId,
                ["BaselineSeat"] = session.Binding.SeatId,
                ["BaselineBindingEpoch"] = session.Binding.BindingEpoch.Value,
                ["BaselineConnectionEpoch"] = session.Binding.ConnectionEpoch.Value,
                ["BaselineOwnerEpoch"] = session.Binding.OwnerEpoch.Value,
                ["BaselineControlEpoch"] = before.PrivateView.Decision!.ControlEpoch,
                ["BaselineDecisionId"] = before.PrivateView.Decision.DecisionId,
                ["BaselineStateVersion"] = before.StateVersion,
                ["BaselineEventSeq"] = before.EventSeq,
                ["BaselineCursorStream"] = before.Cursor.StreamVersion,
                ["BaselineCursor"] = before.Cursor.ProjectionSeq,
                ["BaselinePlayerSnapshotBase64"] = Convert.ToBase64String(before.ToByteArray()),
                ["BaselineBindingJsonBase64"] = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
                {
                    ["SessionId"] = session.Binding.SessionId.Value, ["RoomId"] = beforeFacts.RoomId,
                    ["TableId"] = batch.TableId, ["AccountId"] = session.Binding.Scope.AccountId,
                    ["ScopeId"] = session.Binding.Scope.ScopeId.Value, ["Role"] = session.Binding.Role.ToString(),
                    ["SeatId"] = session.Binding.SeatId, ["BindingEpoch"] = session.Binding.BindingEpoch.Value,
                    ["ConnectionEpoch"] = session.Binding.ConnectionEpoch.Value, ["OwnerEpoch"] = session.Binding.OwnerEpoch.Value
                })),
                ["BaselineAuthoritySaveBase64"] = Convert.ToBase64String(baseline.AuthoritySaveBytes),
                ["BaselineAuthorityCheatStateBase64"] = Convert.ToBase64String(baseline.CheatStatePayload),
                ["BaselinePhysicalTileCount"] = beforeFacts.Physical.TileCount,
                ["BaselinePhysicalUniqueCount"] = beforeFacts.Physical.UniqueCount,
                ["BaselineDeadWallCount"] = beforeFacts.Physical.DeadWallCount,
                ["BaselinePhysicalTilesSha256"] = beforeFacts.Physical.TilesSha256,
                ["ReceiptDisposition"] = receipt.Resolution.Disposition.ToString(),
                ["ReceiptStatus"] = receipt.Resolution.ReceiptStatus.ToString(),
                ["ReceiptRequestId"] = receipt.Command.Key.RequestId.Value,
                ["ReceiptScopeId"] = receipt.Command.Key.ScopeId.Value,
                ["ReceiptDigest"] = receipt.Resolution.CanonicalPayloadSha256,
                ["ReceiptCommandVersion"] = receipt.Resolution.CommandVersion!.Value.Value,
                ["ReceiptEventSeq"] = receipt.Resolution.EventSeq!.Value.Value,
                ["RawReceiptJsonBase64"] = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
                {
                    ["Disposition"] = receipt.Resolution.Disposition.ToString(),
                    ["Status"] = receipt.Resolution.ReceiptStatus.ToString(),
                    ["RequestId"] = receipt.Command.Key.RequestId.Value,
                    ["ScopeId"] = receipt.Command.Key.ScopeId.Value,
                    ["CanonicalPayloadSha256"] = receipt.Resolution.CanonicalPayloadSha256,
                    ["CommandVersion"] = receipt.Resolution.CommandVersion!.Value.Value,
                    ["EventSeq"] = receipt.Resolution.EventSeq!.Value.Value
                })),
                ["EventType"] = actionEvent.Type.ToString(),
                ["EventRoundId"] = batch.RoundId,
                ["EventScopeId"] = batch.ScopeId,
                ["EventSessionId"] = batch.SessionId,
                ["EventRoomId"] = batch.RoomId,
                ["EventTableId"] = batch.TableId,
                ["EventSeat"] = actionEvent.Seat,
                ["EventBindingEpoch"] = batch.BindingEpoch,
                ["EventConnectionEpoch"] = batch.ConnectionEpoch,
                ["EventOwnerEpoch"] = batch.OwnerEpoch,
                ["EventRole"] = batch.Role.ToString(),
                ["EventBatchKind"] = batch.Kind.ToString(),
                ["EventFullBaseline"] = batch.IsFullBaseline,
                ["RawEventBatchJsonBase64"] = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
                {
                    ["Kind"] = batch.Kind,
                    ["IsFullBaseline"] = batch.IsFullBaseline,
                    ["SessionId"] = batch.SessionId, ["RoomId"] = batch.RoomId, ["TableId"] = batch.TableId,
                    ["RoundId"] = batch.RoundId, ["ScopeId"] = batch.ScopeId, ["Role"] = batch.Role,
                    ["Seat"] = batch.Seat, ["BindingEpoch"] = batch.BindingEpoch,
                    ["ConnectionEpoch"] = batch.ConnectionEpoch, ["OwnerEpoch"] = batch.OwnerEpoch,
                    ["StateVersion"] = batch.StateVersion, ["EventSeq"] = batch.EventSeq,
                    ["ProjectionSeq"] = batch.ProjectionSeq, ["StreamVersion"] = batch.StreamVersion,
                    // TOOL_FIXTURE uses a deliberately separate captured publication counter; it does not claim Coordinator provenance.
                    ["AcceptanceSequence"] = after.Cursor.ProjectionSeq,
                    ["Events"] = batch.Events.Select(value => new Dictionary<string, object?>
                    {
                        ["Type"] = value.Type, ["EventSeq"] = value.EventSeq, ["CommandVersion"] = value.CommandVersion,
                        ["Seat"] = value.Seat, ["TileId"] = value.TileId ?? 0U, ["TileIds"] = value.TileIds,
                        ["Payload"] = new Dictionary<string, object?> { ["DiscardTileId"] = value.Payload.DiscardTileId ?? 0U }
                    }).ToArray()
                })),
                ["EventCommandVersion"] = actionEvent.CommandVersion,
                ["EventSeq"] = actionEvent.EventSeq,
                ["EventTileIds"] = string.Join(",", actionEvent.TileIds),
                ["AcceptanceSequence"] = after.Cursor.ProjectionSeq,
                ["AfterRoomId"] = afterFacts.RoomId,
                ["AfterSessionId"] = after.SessionId,
                ["AfterTableId"] = batch.TableId,
                ["AfterRoundId"] = after.RoundId,
                ["AfterSeat"] = session.Binding.SeatId,
                ["AfterBindingEpoch"] = session.Binding.BindingEpoch.Value,
                ["AfterConnectionEpoch"] = session.Binding.ConnectionEpoch.Value,
                ["AfterOwnerEpoch"] = session.Binding.OwnerEpoch.Value,
                ["AfterControlEpoch"] = afterFacts.ControlEpochs[session.Binding.SeatId],
                ["AfterStateVersion"] = after.StateVersion,
                ["AfterEventSeq"] = after.EventSeq,
                ["AfterAcceptanceSequence"] = after.Cursor.ProjectionSeq,
                ["AfterCursorStream"] = after.Cursor.StreamVersion,
                ["AfterCursor"] = after.Cursor.ProjectionSeq,
                ["AfterPlayerSnapshotBase64"] = Convert.ToBase64String(after.ToByteArray()),
                ["AfterBindingJsonBase64"] = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
                {
                    ["SessionId"] = session.Binding.SessionId.Value, ["RoomId"] = afterFacts.RoomId,
                    ["TableId"] = batch.TableId, ["AccountId"] = session.Binding.Scope.AccountId,
                    ["ScopeId"] = session.Binding.Scope.ScopeId.Value, ["Role"] = session.Binding.Role.ToString(),
                    ["SeatId"] = session.Binding.SeatId, ["BindingEpoch"] = session.Binding.BindingEpoch.Value,
                    ["ConnectionEpoch"] = session.Binding.ConnectionEpoch.Value, ["OwnerEpoch"] = session.Binding.OwnerEpoch.Value
                })),
                ["AfterAuthoritySaveBase64"] = Convert.ToBase64String(accepted.AuthoritySaveBytes),
                ["AfterAuthorityCheatStateBase64"] = Convert.ToBase64String(accepted.CheatStatePayload),
                ["AfterPhysicalTileCount"] = afterFacts.Physical.TileCount,
                ["AfterPhysicalUniqueCount"] = afterFacts.Physical.UniqueCount,
                ["AfterDeadWallCount"] = afterFacts.Physical.DeadWallCount,
                ["AfterPhysicalTilesSha256"] = afterFacts.Physical.TilesSha256,
                ["HandTileIds"] = string.Join(",", after.PrivateView.ClosedTiles),
                ["DrawnTileId"] = after.PrivateView.HasDrawnTileId ? after.PrivateView.DrawnTileId.ToString() : string.Empty,
                ["MeldKind"] = ownMeld?.Type.ToString() ?? string.Empty,
                ["MeldTileIds"] = ownMeld == null ? string.Empty : string.Join(",", ownMeld.TileIds),
                ["SeatScoreBefore"] = before.PublicView.Board.Scores.Single(value => value.Seat == 0).Score,
                ["SeatScoreAfter"] = after.PublicView.Board.Scores.Single(value => value.Seat == 0).Score,
                ["RiichiSticksBefore"] = before.PublicView.Board.RiichiSticks,
                ["RiichiSticksAfter"] = after.PublicView.Board.RiichiSticks
            };
            string path = Path.Combine(root, CaseForScenario(scenario) + ".json");
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(stream, value);
            stream.WriteByte((byte)'\n');
            stream.Flush(true);
        }

        private static string CaseForScenario(string scenario) => scenario switch
        {
            LocalPlayerActionScenario.Pon => "C04-G2-01",
            LocalPlayerActionScenario.Chi => "C04-G2-02",
            LocalPlayerActionScenario.Riichi => "C04-G2-03",
            LocalPlayerActionScenario.ClosedKan => "C04-G2-04",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

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

        private static async Task<TableSnapshotChangedEventArgs> WaitForAppliedPublicationAsync(
            System.Collections.Concurrent.ConcurrentQueue<TableSnapshotChangedEventArgs> publications,
            ulong commandVersion,
            string roundId,
            ProtocolActionType actionType,
            CancellationToken cancellationToken)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (TableSnapshotChangedEventArgs publication in publications)
                {
                    PlayerSnapshot? snapshot = publication.PlayerSnapshot;
                    TableVisibleEventBatch batch = publication.VisibleEventBatch;
                    if (snapshot != null &&
                        snapshot.StateVersion == commandVersion &&
                        string.Equals(snapshot.RoundId, roundId, StringComparison.Ordinal) &&
                        batch.Kind == TableVisibleEventBatchKind.Incremental &&
                        batch.StateVersion == commandVersion &&
                        string.Equals(batch.RoundId, roundId, StringComparison.Ordinal) &&
                        batch.Events.Any(value =>
                            value.CommandVersion == commandVersion &&
                            value.Seat == 0 &&
                            value.Type == EventFor(actionType)))
                    {
                        return publication;
                    }
                }

                if (DateTime.UtcNow >= deadline)
                {
                    string observed = string.Join(",", publications
                        .Where(value => value.PlayerSnapshot != null)
                        .Select(value => value.PlayerSnapshot!.StateVersion + "@" + value.VisibleEventBatch.StateVersion)
                        .ToArray());
                    throw new InvalidOperationException(
                        "The exact Applied action publication was not observed within 10s: version=" +
                        commandVersion + " round=" + roundId + " event=" + EventFor(actionType) +
                        " observed=" + observed);
                }

                await Task.Delay(10, cancellationToken);
            }
        }

        private static async Task<TableSnapshotChangedEventArgs> WaitForLaterPublicationAsync(
            System.Collections.Concurrent.ConcurrentQueue<TableSnapshotChangedEventArgs> publications,
            ulong afterVersion,
            CancellationToken cancellationToken)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (TableSnapshotChangedEventArgs publication in publications)
                {
                    PlayerSnapshot? snapshot = publication.PlayerSnapshot;
                    if (snapshot != null &&
                        snapshot.StateVersion > afterVersion &&
                        publication.VisibleEventBatch.Kind == TableVisibleEventBatchKind.Incremental)
                    {
                        return publication;
                    }
                }

                if (DateTime.UtcNow >= deadline)
                {
                    ulong latest = 0;
                    foreach (TableSnapshotChangedEventArgs publication in publications)
                    {
                        if (publication.PlayerSnapshot != null &&
                            publication.PlayerSnapshot.StateVersion > latest)
                        {
                            latest = publication.PlayerSnapshot.StateVersion;
                        }
                    }

                    throw new InvalidOperationException(
                        "The fixed Riichi runtime did not publish a legal command after the target within 10s: target=" +
                        afterVersion + " latestPublication=" + latest);
                }

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

        private static string ReadOptionalCanonicalString(CanonicalPayloadDocument document, ushort fieldNumber)
        {
            return document.HasField(fieldNumber)
                ? ReadCanonicalString(document, fieldNumber, "optionId")
                : string.Empty;
        }
    }
}
